# Heading Hold for Cities: Skylines

Say goodbye to commercial pilots treating your international airport runways like a stunt track. **Heading Hold** is a lightweight, high-performance mod for *Cities: Skylines* that forces cargo and passenger planes to maintain a clean, professional runway heading and wings-level attitude during initial climb until they reach a safe altitude.

---

## 🚀 Features

* **Straight-Line Departures:** Intercepts takeoff routines to lock planes onto the exact runway centerline heading.
* **Tail-Strike Prevention:** Preserves natural pitch handling while locking out erratic lateral yaw and sudden banking.
* **Smart Filtering:** Strictly targets `CargoPlaneAI` and `PassengerPlaneAI`, leaving private planes and other vehicles to do their own thing.
* **Clean Handoff:** Once the aircraft climbs past **50m AGL**, the mod seamlessly releases control and restores native game routing so planes continue smoothly to their destination out of the city.
* **Performance First:** Built with low-overhead Harmony patching and fully optimized for the game's .NET 3.5 runtime.

---

## 📦 Installation

1. Download the latest release DLL or build from source.
2. Place `HeadingHold.dll` into your Cities: Skylines local mods folder:
   * **Windows:** `%localappdata%\Colossal Order\Cities_Skylines\Addons\Mods\HeadingHold`
3. Enable the mod in the **Content Manager** inside Cities: Skylines.

---

## 🛠️ Source Code & Contributions

Contributions, bug reports, and pull requests are welcome! Check out the repository on GitHub:
👉 [github.com/mistermcd/headinghold](https://github.com/mistermcd/headinghold)