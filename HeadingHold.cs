// Heading Hold - A Cities: Skylines mod to maintain runway heading during takeoff up to 100m AGL
// October 2026 - Idea and testing by MisterMcD, coding by Gemini

using ColossalFramework;
using HarmonyLib;
using ICities;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HeadingHold
{
    public class ModEntry : IUserMod
    {
        public string Name => "Heading Hold";
        public string Description => "Planes will maintain runway heading during initial climb.";
    }

    public class HeadingHoldLoadingExtension : LoadingExtensionBase
    {
        private const string HarmonyId = "com.mistermcd.cities.headinghold";
        private static Harmony _harmony;
        private static bool _isPatched = false;

        public override void OnLevelLoaded(LoadMode mode)
        {
            if (mode == LoadMode.LoadGame || mode == LoadMode.NewGame)
            {
                ApplyPatches();
            }
        }

        public override void OnLevelUnloading()
        {
            if (!ReferenceEquals(_harmony, null) && _isPatched)
            {
                _harmony.UnpatchAll(HarmonyId);
                _harmony = null;
                _isPatched = false;
                HeadingHoldPatch.Cleanup();
            }
        }

        private static void ApplyPatches()
        {
            if (_isPatched) return;

            _harmony = new Harmony(HarmonyId);
            try
            {
                Type vehicleAiType = AccessTools.TypeByName("VehicleAI");
                if (ReferenceEquals(vehicleAiType, null)) return;

                MethodInfo originalMethod = null;
                foreach (var m in vehicleAiType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name == "SimulationStep")
                    {
                        var p = m.GetParameters();
                        if (p.Length >= 3 &&
                            !ReferenceEquals(p[0].ParameterType, null) && p[0].ParameterType.Name == "UInt16" &&
                            !ReferenceEquals(p[1].ParameterType, null) && p[1].ParameterType.Name.Contains("Vehicle") &&
                            !ReferenceEquals(p[2].ParameterType, null) && p[2].ParameterType.Name.Contains("Frame"))
                        {
                            originalMethod = m;
                            break;
                        }
                    }
                }

                if (!ReferenceEquals(originalMethod, null))
                {
                    var prefixMethod = AccessTools.Method(typeof(HeadingHoldPatch), nameof(HeadingHoldPatch.SimulationStepPrefix));
                    _harmony.Patch(originalMethod, prefix: new HarmonyMethod(prefixMethod));
                    _isPatched = true;
                }

                var releaseVehicleMethod = AccessTools.Method(typeof(VehicleManager), nameof(VehicleManager.ReleaseVehicle));
                var releaseVehiclePostfix = AccessTools.Method(typeof(HeadingHoldPatch), nameof(HeadingHoldPatch.ReleaseVehiclePostfix));
                if (!ReferenceEquals(releaseVehicleMethod, null) && !ReferenceEquals(releaseVehiclePostfix, null))
                {
                    _harmony.Patch(releaseVehicleMethod, postfix: new HarmonyMethod(releaseVehiclePostfix));
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[Heading Hold] Patch exception: {ex}");
            }
        }
    }

    public static class HeadingHoldPatch
    {
        private struct TakeoffData
        {
            public Vector3 RunwayHeadingVector;
            public Vector3 TakeoffStartPosition;
            public float TakeoffStartY;
        }

        private static readonly Dictionary<ushort, TakeoffData> _takeoffs = new Dictionary<ushort, TakeoffData>(64);

        public static void Cleanup()
        {
            _takeoffs.Clear();
        }

        public static void ReleaseVehiclePostfix(ushort vehicle)
        {
            if (_takeoffs.ContainsKey(vehicle))
            {
                _takeoffs.Remove(vehicle);
            }
        }

        /// <summary>
        /// Global Prefix matching VehicleAI.SimulationStep signature. Filters for CargoPlaneAI and PassengerPlaneAI, 
        /// locking heading until 50m AGL.
        /// </summary>
        public static bool SimulationStepPrefix(ushort vehicleID, ref Vehicle vehicleData, ref Vehicle.Frame frameData)
        {
            try
            {
                if (ReferenceEquals(vehicleData.Info, null) || ReferenceEquals(vehicleData.Info.m_vehicleAI, null))
                {
                    return true;
                }

                Type aiType = vehicleData.Info.m_vehicleAI.GetType();
                if (ReferenceEquals(aiType, null))
                {
                    return true;
                }

                string aiTypeName = aiType.Name;
                if (ReferenceEquals(aiTypeName, null) ||
                    (!aiTypeName.Contains("CargoPlaneAI") && !aiTypeName.Contains("PassengerPlaneAI")))
                {
                    return true;
                }

                const Vehicle.Flags safeIgnoreFlags = Vehicle.Flags.Landing | Vehicle.Flags.Deleted;
                if ((vehicleData.m_flags & safeIgnoreFlags) != 0 || (vehicleData.m_flags & Vehicle.Flags.Created) == 0)
                {
                    if (_takeoffs.ContainsKey(vehicleID))
                    {
                        _takeoffs.Remove(vehicleID);
                    }
                    return true;
                }

                bool isTakingOff = (vehicleData.m_flags & Vehicle.Flags.TakingOff) != 0;

                if (!isTakingOff)
                {
                    if (_takeoffs.ContainsKey(vehicleID))
                    {
                        _takeoffs.Remove(vehicleID);
                    }
                    return true;
                }

                Vector3 currentPos = frameData.m_position;
                Quaternion currentRot = frameData.m_rotation;

                if (!_takeoffs.TryGetValue(vehicleID, out TakeoffData tData))
                {
                    Vector3 forward = currentRot * Vector3.forward;
                    forward.y = 0f;
                    if (forward.sqrMagnitude < 0.001f)
                    {
                        forward = Vector3.forward;
                    }
                    else
                    {
                        forward.Normalize();
                    }

                    tData = new TakeoffData
                    {
                        RunwayHeadingVector = forward,
                        TakeoffStartPosition = currentPos,
                        TakeoffStartY = currentPos.y
                    };
                    _takeoffs[vehicleID] = tData;
                }

                float heightGained = currentPos.y - tData.TakeoffStartY;

                // ALTITUDE GATE REACHED (100m AGL): Release tracking and let native game routing take over cleanly
                if (heightGained >= 100f)
                {
                    vehicleData.m_flags &= ~Vehicle.Flags.TakingOff;
                    vehicleData.m_flags |= Vehicle.Flags.Flying;
                    _takeoffs.Remove(vehicleID);
                    return true;
                }

                // --- 1. RUNWAY CENTERLINE & POSITION LOCK ---
                Vector3 displacement = currentPos - tData.TakeoffStartPosition;
                float forwardDist = Vector3.Dot(displacement, tData.RunwayHeadingVector);
                if (forwardDist < 0f) forwardDist = 0f;

                Vector3 idealXZ = tData.TakeoffStartPosition + (tData.RunwayHeadingVector * forwardDist);
                frameData.m_position = new Vector3(idealXZ.x, currentPos.y, idealXZ.z);

                // --- 2. CONSTRAINED PATH TARGET PROJECTOR ---
                Vector3 nativeTarget = new Vector3(vehicleData.m_targetPos0.x, vehicleData.m_targetPos0.y, vehicleData.m_targetPos0.z);
                Vector3 targetDisplacement = nativeTarget - tData.TakeoffStartPosition;
                float targetForwardDist = Vector3.Dot(targetDisplacement, tData.RunwayHeadingVector);
                if (targetForwardDist < forwardDist + 10f) targetForwardDist = forwardDist + 50f;

                Vector3 constrainedTarget = tData.TakeoffStartPosition + (tData.RunwayHeadingVector * targetForwardDist);
                constrainedTarget.y = nativeTarget.y;

                Vector4 constrainedTargetV4 = new Vector4(constrainedTarget.x, constrainedTarget.y, constrainedTarget.z, vehicleData.m_targetPos0.w);
                vehicleData.m_targetPos0 = constrainedTargetV4;
                vehicleData.m_targetPos1 = constrainedTargetV4;

                // --- 3. VELOCITY & ROTATION LOCK (WINGS LEVEL, NO YAW, VANILLA PITCH) ---
                Vector3 currentForward = currentRot * Vector3.forward;
                float currentPitch = Mathf.Asin(Mathf.Clamp(currentForward.y, -1f, 1f));

                Vector3 lockedHeadingWithPitch = new Vector3(
                    tData.RunwayHeadingVector.x * Mathf.Cos(currentPitch),
                    Mathf.Sin(currentPitch),
                    tData.RunwayHeadingVector.z * Mathf.Cos(currentPitch)
                ).normalized;

                float currentSpeed = Mathf.Max(frameData.m_velocity.magnitude, 35f);
                frameData.m_velocity = lockedHeadingWithPitch * currentSpeed;
                frameData.m_rotation = Quaternion.LookRotation(lockedHeadingWithPitch, Vector3.up);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[Heading Hold] Error in Prefix for vehicle {vehicleID}: {ex.Message}");
            }

            return true;
        }
    }
}
