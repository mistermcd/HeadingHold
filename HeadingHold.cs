// Heading Hold - A Cities: Skylines mod to maintain runway heading during takeoff
// October 2026 - Idea and testing by MisterMcD, coding by Gemini
// Version 1.1 - Added Options panel and optimized code.

using ColossalFramework;
using ColossalFramework.UI;
using HarmonyLib;
using ICities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using UnityEngine;

namespace HeadingHold
{
    public static class Configuration
    {
        private static readonly string ConfigFilePath = "HeadingHoldConfig.xml";
        public static bool Enabled { get; set; } = true;
        public static float ReleaseAltitude { get; set; } = 100f;

        public static void Load()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    using (var reader = new StreamReader(ConfigFilePath))
                    {
                        var xml = new XmlSerializer(typeof(ConfigData));
                        var data = (ConfigData)xml.Deserialize(reader);
                        Enabled = data.Enabled;
                        ReleaseAltitude = Mathf.Clamp(data.ReleaseAltitude, 50f, 200f);
                    }
                }
            }
            catch
            {
                Enabled = true;
                ReleaseAltitude = 100f;
            }
        }

        public static void Save()
        {
            try
            {
                var data = new ConfigData { Enabled = Enabled, ReleaseAltitude = ReleaseAltitude };
                using (var writer = new StreamWriter(ConfigFilePath))
                {
                    var xml = new XmlSerializer(typeof(ConfigData));
                    xml.Serialize(writer, data);
                }
                Debug.Log("[Heading Hold] Configuration saved successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Heading Hold] Failed to save configuration: {ex.Message}");
            }
        }

        public class ConfigData
        {
            public bool Enabled { get; set; } = true;
            public float ReleaseAltitude { get; set; } = 100f;
        }
    }

    public class ModEntry : IUserMod
    {
        public string Name => "Heading Hold";
        public string Description => "Planes will maintain runway heading during initial climb.";

        public void OnEnabled()
        {
            Configuration.Load();
        }

        public void OnSettingsUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup("Heading Hold Settings");

            group.AddCheckbox("Enabled", Configuration.Enabled, (value) =>
            {
                Configuration.Enabled = value;
            });

            UISlider targetSlider = null;
            UILabel titleLabel = null;

            var sliderComponent = group.AddSlider(
                "Release Altitude",
                50f,
                200f,
                10f,
                Configuration.ReleaseAltitude,
                (value) =>
                {
                    Configuration.ReleaseAltitude = value;
                    if (titleLabel != null)
                    {
                        titleLabel.text = "Release Altitude: " + Mathf.RoundToInt(value) + "m";
                    }
                }
            ) as UIComponent;

            if (sliderComponent != null)
            {
                sliderComponent.tooltip = "Minimum altitude aircraft must achieve in straight level flight.";

                targetSlider = sliderComponent.GetComponentInChildren<UISlider>();
                if (targetSlider == null && sliderComponent is UISlider)
                {
                    targetSlider = (UISlider)sliderComponent;
                }

                var labels = sliderComponent.GetComponentsInChildren<UILabel>();
                if (labels.Length == 0 && sliderComponent.parent != null)
                {
                    labels = sliderComponent.parent.GetComponentsInChildren<UILabel>();
                }

                foreach (var lbl in labels)
                {
                    if (lbl != null && lbl.text.Contains("Release Altitude"))
                    {
                        titleLabel = lbl;
                        break;
                    }
                }

                if (titleLabel != null)
                {
                    titleLabel.text = "Release Altitude: " + Mathf.RoundToInt(Configuration.ReleaseAltitude) + "m";
                }
            }

            group.AddButton("Save Settings", () =>
            {
                Configuration.Save();
            });

            group.AddButton("Reset to Default (100m)", () =>
            {
                Configuration.Enabled = true;
                Configuration.ReleaseAltitude = 100f;
                if (targetSlider != null)
                {
                    targetSlider.value = 100f;
                }
                if (titleLabel != null)
                {
                    titleLabel.text = "Release Altitude: 100m";
                }
                Debug.Log("[Heading Hold] Settings reset to default. Hit 'Save Settings' to write to disk.");
            });
        }
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

            Configuration.Load();
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
        /// Global Prefix matching VehicleAI.SimulationStep signature. Filters for CargoPlaneAI and PassengerPlaneAI 
        /// using direct object references to ensure zero GC allocation overhead.
        /// </summary>
        public static bool SimulationStepPrefix(ushort vehicleID, ref Vehicle vehicleData, ref Vehicle.Frame frameData)
        {
            try
            {
                if (!Configuration.Enabled)
                {
                    return true;
                }

                if (ReferenceEquals(vehicleData.Info, null) || ReferenceEquals(vehicleData.Info.m_vehicleAI, null))
                {
                    return true;
                }

                // Zero-allocation type check replacing string reflection
                VehicleAI ai = vehicleData.Info.m_vehicleAI;
                if (!(ai is CargoPlaneAI) && !(ai is PassengerPlaneAI))
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
                bool isTracked = _takeoffs.ContainsKey(vehicleID);

                if (!isTakingOff && !isTracked)
                {
                    return true;
                }

                Vector3 currentPos = frameData.m_position;
                Quaternion currentRot = frameData.m_rotation;

                if (!isTracked)
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

                    TakeoffData tData = new TakeoffData
                    {
                        RunwayHeadingVector = forward,
                        TakeoffStartPosition = currentPos,
                        TakeoffStartY = currentPos.y
                    };
                    _takeoffs[vehicleID] = tData;
                }

                TakeoffData activeData = _takeoffs[vehicleID];
                float heightGained = currentPos.y - activeData.TakeoffStartY;

                // ALTITUDE GATE REACHED: Release tracking and hand off cleanly
                if (heightGained >= Configuration.ReleaseAltitude)
                {
                    vehicleData.m_flags &= ~Vehicle.Flags.TakingOff;
                    vehicleData.m_flags |= Vehicle.Flags.Flying;
                    _takeoffs.Remove(vehicleID);
                    return true;
                }

                // --- 1. RUNWAY CENTERLINE & POSITION LOCK ---
                Vector3 displacement = currentPos - activeData.TakeoffStartPosition;
                float forwardDist = Vector3.Dot(displacement, activeData.RunwayHeadingVector);
                if (forwardDist < 0f) forwardDist = 0f;

                Vector3 idealXZ = activeData.TakeoffStartPosition + (activeData.RunwayHeadingVector * forwardDist);
                frameData.m_position = new Vector3(idealXZ.x, currentPos.y, idealXZ.z);

                // --- 2. CONSTRAINED PATH TARGET PROJECTOR ---
                Vector3 nativeTarget = new Vector3(vehicleData.m_targetPos0.x, vehicleData.m_targetPos0.y, vehicleData.m_targetPos0.z);
                Vector3 targetDisplacement = nativeTarget - activeData.TakeoffStartPosition;
                float targetForwardDist = Vector3.Dot(targetDisplacement, activeData.RunwayHeadingVector);
                if (targetForwardDist < forwardDist + 10f) targetForwardDist = forwardDist + 50f;

                Vector3 constrainedTarget = activeData.TakeoffStartPosition + (activeData.RunwayHeadingVector * targetForwardDist);
                constrainedTarget.y = nativeTarget.y;

                Vector4 constrainedTargetV4 = new Vector4(constrainedTarget.x, constrainedTarget.y, constrainedTarget.z, vehicleData.m_targetPos0.w);
                vehicleData.m_targetPos0 = constrainedTargetV4;
                vehicleData.m_targetPos1 = constrainedTargetV4;

                // --- 3. VELOCITY & ROTATION LOCK (WINGS LEVEL, NO YAW, VANILLA PITCH) ---
                Vector3 currentForward = currentRot * Vector3.forward;
                float currentPitch = Mathf.Asin(Mathf.Clamp(currentForward.y, -1f, 1f));

                Vector3 lockedHeadingWithPitch = new Vector3(
                    activeData.RunwayHeadingVector.x * Mathf.Cos(currentPitch),
                    Mathf.Sin(currentPitch),
                    activeData.RunwayHeadingVector.z * Mathf.Cos(currentPitch)
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