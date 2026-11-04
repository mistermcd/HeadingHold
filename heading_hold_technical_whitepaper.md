# Technical Whitepaper: Heading Hold Engine Interception & Takeoff Stabilization Architecture

**Mod Version:** 1.0.0  
**Target Platform:** Cities: Skylines (Unity / Mono runtime)  
**Author:** MisterMcD (Concept & Testing), Skylines (Architecture & Implementation)  
**Repository:** [github.com/mistermcd/headinghold](https://github.com/mistermcd/headinghold)

---

## 1. Executive Summary

In vanilla *Cities: Skylines*, aircraft departure physics are governed by dynamic target position assignment within `VehicleAI.SimulationStep`. Immediately upon setting the `Vehicle.Flags.TakingOff` flag, the game engine calculates path nodes toward the flight's next out-of-city connection. This frequently generates steep lateral vector changes while aircraft are still at low speeds and low altitudes, resulting in unrealistic sharp turns, extreme bank angles, centerline deviation, and potential ground/structure clips.

**Heading Hold** is a Harmony-based runtime patch designed for mod developers and systems engineers seeking to understand vehicle simulation overrides in *Cities: Skylines*. The mod intercepts `VehicleAI.SimulationStep` globally via a high-performance prefix, isolates commercial aviation types via reflection, and projects original flight plan target vectors straight ahead along the initial liftoff vector. By locking the vehicle frame’s position, velocity, and orientation to a 1D runway heading axis until reaching a **100-meter Above Ground Level (AGL)** threshold, it stabilizes initial climbs before seamlessly handing control back to the native pathfinding state machine.

---

## 2. Architecture & Runtime Constraints

### 2.1 Mono .NET 3.5 Compatibility Layer
*Cities: Skylines* operates on an older Mono runtime (.NET Framework 3.5 equivalence layer). Modern C# compiler optimizations can inadvertently introduce calls to operators not present in this runtime, leading to severe system crashes.

* **Type Comparison Constraint:** Direct type equality (`Type == Type`) or null comparisons on `System.Type` references emit calls to `System.Type.op_Equality` or `System.Type.op_Inequality`, which throw `MissingMethodException` at runtime under Mono.
* **Mitigation Strategy:** All object references are strictly validated using `ReferenceEquals(obj, null)`. Type matching uses runtime reflection string evaluation (`Type.Name.Contains(...)`), completely bypassing compiler-generated operator overloads.

### 2.2 Global Harmony Interception Strategy
Rather than attempting dynamic assembly reflection across dozens of custom workshop assemblies—which often silent-fails when custom aircraft override AI classes—Heading Hold hooks directly into the root abstract method: `VehicleAI.SimulationStep`.

Because every vehicle entity (road, rail, marine, and aerial) inherits from `VehicleAI`, patching `VehicleAI.SimulationStep(ushort vehicleID, ref Vehicle vehicleData, ref Vehicle.Frame frameData)` guarantees 100% execution coverage across vanilla, DLC, and workshop custom assets.

---

## 3. High-Level System Flow

```
                     [ VehicleAI.SimulationStep ]
                                  │
                                  ▼
                    Is vehicle Info & AI valid?
                                  │
                        ├── No ──► [ Return True (Native Simulation) ]
                        │
                        ▼
            Does AI Type match Cargo/Passenger Plane?
                        │
                        ├── No ──► [ Return True (Native Simulation) ]
                        │
                        ▼
            Is Vehicle.Flags.TakingOff active?
                        │
                        ├── No ──► [ Clear Tracking / Return True ]
                        │
                        ▼
                 Get/Create TakeoffData Snapshot
            (Locks Vector3 RunwayHeading & Altitude)
                                  │
                                  ▼
                  Height Gained >= 100m AGL?
                        │
                        ├── Yes ─► [ Clear TakingOff Flag, Set Flying Flag, Release ]
                        │
                        ▼
           [ Apply Centerline Projection & Heading Lock ]
                                  │
                                  ▼
                      [ Return True (Prefix Hand-Off) ]
```

---

## 4. Algorithmic Breakdown

The mod operates in three distinct phases during every simulation tick of an active aircraft departure:

### 4.1 Phase 1: Filtering & State Initialization
To minimize CPU overhead across high vehicle counts, the prefix executes a cascading filter:

1. **AI Type Identification:** Extracted via `vehicleData.Info.m_vehicleAI.GetType().Name`. Returns immediately unless the class name contains `"CargoPlaneAI"` or `"PassengerPlaneAI"`. (This explicitly ignores `PrivatePlaneAI`, helicopters, and non-aviation assets).
2. **Flag Verification:** Confirms `(vehicleData.m_flags & Vehicle.Flags.TakingOff) != 0` and ensures safety flags (`Landing`, `Deleted`) are clear.
3. **Liftoff Vector Capture:** On the first frame of takeoff, a `TakeoffData` struct is constructed and stored in a static `Dictionary<ushort, TakeoffData>`:
   $$ \vec{v}_{\text{heading}} = \text{Normalize}\left( \mathbf{R}_{\text{frame0}} \cdot \begin{bmatrix} 0 \\ 0 \\ 1 \end{bmatrix} \right)_{XZ} $$
   The starting XZ coordinate ($\vec{P}_{\text{start}}$) and baseline ground elevation ($Y_{\text{start}}$) are locked.

---

### 4.2 Phase 2: Runway Centerline Projection & Trajectory Lock
While the aircraft's current altitude $Y_{\text{current}} - Y_{\text{start}} < 100.0\,\text{meters}$:

#### 1. Centerline Constraint
To eliminate lateral drift caused by crosswinds or premature pathing, the aircraft's horizontal position is projected onto a ray defined by the takeoff origin and heading vector:
$$ d_{\text{forward}} = \max\left(0, (\vec{P}_{\text{current}} - \vec{P}_{\text{start}}) \cdot \vec{v}_{\text{heading}}\right) $$
$$ \vec{P}_{\text{ideal}} = \vec{P}_{\text{start}} + (d_{\text{forward}} \cdot \vec{v}_{\text{heading}}) $$
$$ \mathbf{Frame}_{\text{position}} = \begin{bmatrix} P_{\text{ideal}, x} \\ Y_{\text{current}} \\ P_{\text{ideal}, z} \end{bmatrix} $$

#### 2. Constrained Path Target Projection
To prevent the native pathfinding engine from stalling or looping (which occurs when virtual target waypoints are placed out of path bounds), the native target (`vehicleData.m_targetPos0`) is read every frame and re-projected straight ahead along the takeoff ray:
$$ d_{\text{target}} = \max\left(d_{\text{forward}} + 50.0, (\vec{P}_{\text{nativeTarget}} - \vec{P}_{\text{start}}) \cdot \vec{v}_{\text{heading}}\right) $$
$$ \vec{P}_{\text{constrainedTarget}} = \vec{P}_{\text{start}} + (d_{\text{target}} \cdot \vec{v}_{\text{heading}}) $$
Both `m_targetPos0` and `m_targetPos1` are updated with this forward-projected target, ensuring the game’s internal path node progression index advances naturally without triggering yaw inputs.

#### 3. Pitch Preservation & Rotation Lock
To allow the game's physics to determine pitch (preventing tail-strikes on rotate) while locking yaw and roll to zero:
$$ \theta_{\text{pitch}} = \arcsin\left( \text{Clamp}(\mathbf{R}_{\text{forward}, y}, -1.0, 1.0) \right) $$
$$ \vec{v}_{\text{pitchHeading}} = \text{Normalize}\left( \begin{bmatrix} v_{\text{heading}, x} \cdot \cos(\theta_{\text{pitch}}) \\ \sin(\theta_{\text{pitch}}) \\ v_{\text{heading}, z} \cdot \cos(\theta_{\text{pitch}}) \end{bmatrix} \right) $$
$$ \mathbf{Frame}_{\text{rotation}} = \text{Quaternion.LookRotation}(\vec{v}_{\text{pitchHeading}}, \vec{u}_{\text{up}}) $$
$$ \mathbf{Frame}_{\text{velocity}} = \vec{v}_{\text{pitchHeading}} \cdot \max(\|\mathbf{Frame}_{\text{velocity}}\|, 35.0) $$

---

### 4.3 Phase 3: The 100m AGL Release & Hand-Off
Once $Y_{\text{current}} - Y_{\text{start}} \ge 100.0\,\text{meters}$:

1. **Flag State Transition:** The `TakingOff` flag is cleared and the `Flying` flag is asserted:
   ```csharp
   vehicleData.m_flags &= ~Vehicle.Flags.TakingOff;
   vehicleData.m_flags |= Vehicle.Flags.Flying;
   ```
2. **Dictionary Cleanup:** The vehicle ID is removed from `_takeoffs`.
3. **Execution Release:** On subsequent frames, `SimulationStepPrefix` returns `true` early without modifying target vectors or frame data. The native `AirplaneAI.SimulationStep` takes over routing smoothly using the active path nodes.

---

## 5. Performance Considerations

* **Memory Allocation:** Zero per-frame heap allocations during active climb. `TakeoffData` structs reside in a pre-allocated static dictionary (`Capacity = 64`).
* **Time Complexity:** 
  * Early exit for non-airplane vehicles: $\mathcal{O}(1)$ via cached type name string matching.
  * Active plane simulation step: $\mathcal{O}(1)$ scalar math and vector operations.
* **Garbage Collection (GC) Impact:** Minimal. Dictionary additions/removals only occur at liftoff and handoff boundaries.

---

## 6. Code Architecture Reference

```csharp
namespace HeadingHold
{
    public class HeadingHoldLoadingExtension : LoadingExtensionBase { /* Patch orchestration */ }
    public static class HeadingHoldPatch
    {
        private struct TakeoffData { /* Liftoff vector & altitude state */ }
        private static readonly Dictionary<ushort, TakeoffData> _takeoffs;

        public static bool SimulationStepPrefix(ushort vehicleID, ref Vehicle vehicleData, ref Vehicle.Frame frameData)
        {
            // 1. Guard Clauses & Mono Safe Null-Checks
            // 2. Flight Filter (CargoPlaneAI & PassengerPlaneAI)
            // 3. Takeoff Altitude Delta Evaluation
            // 4. Centerline & Target Projection Vector Math
            // 5. Flag Clearing & Native Handoff
        }
    }
}
```