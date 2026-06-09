using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace TA30Plus
{
    [BepInPlugin("com.ta30plus.northstar", "T/A-30+ Northstar", "1.0.0")]
    public class TA30PlusPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        // Engine
        internal const float ThrustPerEngine         = 60000f;   // stock 28,000 N
        internal const float EngineMaxSpeed          = 2000;     // stock 522 m/s
        // Scramjet
        internal const float ScramjetMinMach         = 0.9f;
        internal const float ScramjetThrustPerEngine = 120000f;
        internal const float FlameoutAltM            = 22000f;
        // EOTS
        internal const float EOTSVisualRange         = 25000f;
        internal const float EOTSMagnification       = 4f;
        internal const float EOTSMaxSpeed            = 3000f;
        // Power
        internal const float PowerChargeMultiplier   = 5f;
        internal const float PowerMaxMultiplier      = 5f;
        // Laser
        internal const float LaserRange              = 25000f;
        internal const int   LaserMaxTargets         = 50;
        // HUD
        internal const float OverspeedThreshold      = 800f;
        // FBW
        internal const float FBWMachThreshold        = 1.0f;
        // Airframe
        internal const float AirframeHPMultiplier    = 3f;

        internal static bool scramjetActive = false;
        internal static bool flameout       = false;

        private static bool _detectedKeyLogged = false;

        private static readonly AnimationCurve flatAltitudeCurve;
        static TA30PlusPlugin()
        {
            flatAltitudeCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(25000f, 1f));
            flatAltitudeCurve.preWrapMode  = WrapMode.ClampForever;
            flatAltitudeCurve.postWrapMode = WrapMode.ClampForever;
        }

        private static bool IsCompass(Aircraft aircraft)
        {
            if (aircraft == null) return false;
            try
            {
                var def = aircraft.definition;
                if (def == null) return false;

                if (!_detectedKeyLogged)
                {
                    Log.LogInfo("[IsCompass] jsonKey='" + def.jsonKey + "' unitName='" + aircraft.unitName + "'");
                    _detectedKeyLogged = true;
                }

                if (def.jsonKey == "Trainer") return true;
                if (string.Equals(def.jsonKey, "Trainer", StringComparison.OrdinalIgnoreCase)) return true;
                if (aircraft.unitName != null &&
                    (aircraft.unitName.IndexOf("Compass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     aircraft.unitName.IndexOf("TA-30",   StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;

                return false;
            }
            catch (Exception ex)
            {
                Log.LogWarning("[IsCompass] exception: " + ex.Message);
                return false;
            }
        }

        private void Awake()
        {
            Log = Logger;
            new Harmony("com.ta30plus.northstar").PatchAll();
            Log.LogInfo("T/A-30+ Northstar loaded.");
        }

        //  Engine + Scramjet 

        [HarmonyPatch(typeof(Turbojet), "FixedUpdate")]
        public static class TurbojetPatch
        {
            private static readonly FieldInfo aircraftField   = AccessTools.Field(typeof(Turbojet), "aircraft");
            private static readonly FieldInfo maxSpeedField   = AccessTools.Field(typeof(Turbojet), "maxSpeed");
            private static readonly FieldInfo minDensityField = AccessTools.Field(typeof(Turbojet), "minDensity");
            private static readonly FieldInfo altThrustField  = AccessTools.Field(typeof(Turbojet), "altitudeThrust");
            private static readonly FieldInfo thrustField     = AccessTools.Field(typeof(Turbojet), "thrust");
            private static readonly HashSet<int> logged      = new HashSet<int>();
            private static bool wasScramjet = false;
            private static bool wasFlameout = false;
            private static float _machLogTimer = 0f;

            static TurbojetPatch()
            {
                if (aircraftField   == null) TA30PlusPlugin.Log.LogWarning("[Engine] MISSING: aircraftField on Turbojet");
                if (maxSpeedField   == null) TA30PlusPlugin.Log.LogWarning("[Engine] MISSING: maxSpeedField on Turbojet");
                if (minDensityField == null) TA30PlusPlugin.Log.LogWarning("[Engine] MISSING: minDensityField on Turbojet");
                if (altThrustField  == null) TA30PlusPlugin.Log.LogWarning("[Engine] MISSING: altThrustField on Turbojet");
            }

            public static void Prefix(Turbojet __instance)
            {
                var aircraft = aircraftField?.GetValue(__instance) as Aircraft;
                if (!IsCompass(aircraft)) return;

                int id    = __instance.GetInstanceID();
                bool first = !logged.Contains(id);

                float alt = __instance.transform.position.y - Datum.originPosition.y;
                flameout = alt >= FlameoutAltM;

                if (flameout)
                {
                    __instance.maxThrust = 0f;
                    minDensityField?.SetValue(__instance, 999f);
                    if (flameout != wasFlameout)
                    {
                        Log.LogInfo("[Flameout] Engines out at " + alt.ToString("F0") + " m");
                        wasFlameout = flameout;
                    }
                    if (first) logged.Add(id);
                    return;
                }
                if (flameout != wasFlameout)
                {
                    Log.LogInfo("[Flameout] Engines relit at " + alt.ToString("F0") + " m");
                    wasFlameout = flameout;
                }

                float target = scramjetActive ? ScramjetThrustPerEngine : ThrustPerEngine;
                if (Math.Abs(__instance.maxThrust - target) > 1f)
                    __instance.maxThrust = target;

                if (maxSpeedField != null)
                {
                    float v = (float)maxSpeedField.GetValue(__instance);
                    if (Math.Abs(v - EngineMaxSpeed) > 1f)
                        maxSpeedField.SetValue(__instance, EngineMaxSpeed);
                }
                if (minDensityField != null)
                {
                    float v = (float)minDensityField.GetValue(__instance);
                    if (v > -0.5f) minDensityField.SetValue(__instance, -1f);
                }
                if (altThrustField != null)
                {
                    var curve = altThrustField.GetValue(__instance) as AnimationCurve;
                    if (curve != flatAltitudeCurve) altThrustField.SetValue(__instance, flatAltitudeCurve);
                }

                if (first)
                {
                    Log.LogInfo("[Engine] " + __instance.gameObject.name + " maxThrust -> " + target);
                    logged.Add(id);
                }
            }

            public static void Postfix(Turbojet __instance)
            {
                var aircraft = aircraftField?.GetValue(__instance) as Aircraft;
                if (!IsCompass(aircraft)) return;

                if (flameout)
                {
                    scramjetActive = false;
                    thrustField?.SetValue(__instance, 0f);
                    return;
                }

                float alt  = __instance.transform.position.y - Datum.originPosition.y;
                float sos  = Mathf.Max(-0.005f * alt + 340f, 290f);
                float mach = aircraft.speed / sos;

                bool shouldBeActive = mach >= ScramjetMinMach;
                if (scramjetActive && !shouldBeActive)
                    scramjetActive = mach >= ScramjetMinMach * 0.9f;
                else
                    scramjetActive = shouldBeActive;

                if (scramjetActive != wasScramjet)
                {
                    Log.LogInfo("[Scramjet] " + (scramjetActive ? "ON" : "OFF") +
                                " — Mach " + mach.ToString("F2") + " alt " + alt.ToString("F0") + " m");
                    wasScramjet = scramjetActive;
                }

                _machLogTimer += Time.fixedDeltaTime;
                if (_machLogTimer >= 3f)
                {
                    Log.LogInfo("[Scramjet] Mach=" + mach.ToString("F2") + " active=" + scramjetActive);
                    _machLogTimer = 0f;
                }
            }
        }

        //  EOTS 

        [HarmonyPatch(typeof(TargetDetector), "Awake")]
        public static class EOTSPatch
        {
            private static readonly FieldInfo attachedUnitField  = AccessTools.Field(typeof(TargetDetector), "attachedUnit");
            private static readonly FieldInfo visualRangeField   = AccessTools.Field(typeof(TargetDetector), "visualRange");
            private static readonly FieldInfo magnificationField = AccessTools.Field(typeof(TargetDetector), "magnification");
            private static readonly FieldInfo maxSpeedField      = AccessTools.Field(typeof(TargetDetector), "maxSpeed");

            public static void Postfix(TargetDetector __instance)
            {
                var aircraft = attachedUnitField?.GetValue(__instance) as Aircraft;
                if (!IsCompass(aircraft)) return;

                visualRangeField?.SetValue(__instance, EOTSVisualRange);
                magnificationField?.SetValue(__instance, EOTSMagnification);
                maxSpeedField?.SetValue(__instance, EOTSMaxSpeed);
                Log.LogInfo("[EOTS] visualRange=" + EOTSVisualRange +
                            " magnification=" + EOTSMagnification +
                            " maxSpeed=" + EOTSMaxSpeed);
            }
        }

        //  Power Supply 

        [HarmonyPatch(typeof(PowerSupply), "Awake")]
        public static class PowerSupplyPatch
        {
            private static readonly FieldInfo aircraftField     = AccessTools.Field(typeof(PowerSupply), "aircraft");
            private static readonly FieldInfo maxChargeField    = AccessTools.Field(typeof(PowerSupply), "maxCharge");
            private static readonly FieldInfo chargePerRPMField = AccessTools.Field(typeof(PowerSupply), "chargePerRPM");
            private static readonly FieldInfo maxPowerField     = AccessTools.Field(typeof(PowerSupply), "maxPower");

            public static void Postfix(PowerSupply __instance)
            {
                var aircraft = aircraftField?.GetValue(__instance) as Aircraft;
                if (!IsCompass(aircraft)) return;

                if (maxChargeField != null)
                {
                    float v = (float)maxChargeField.GetValue(__instance);
                    maxChargeField.SetValue(__instance, v * PowerChargeMultiplier);
                }
                if (chargePerRPMField != null)
                {
                    float v = (float)chargePerRPMField.GetValue(__instance);
                    chargePerRPMField.SetValue(__instance, v * PowerChargeMultiplier);
                }
                if (maxPowerField != null)
                {
                    float v = (float)maxPowerField.GetValue(__instance);
                    maxPowerField.SetValue(__instance, v * PowerMaxMultiplier);
                }
                Log.LogInfo("[Power] charge ×" + PowerChargeMultiplier + " maxPower ×" + PowerMaxMultiplier);
            }
        }

        //  Airbrake Joint Reinforcement 

        [HarmonyPatch(typeof(Airbrake), "FixedUpdate")]
        public static class AirbrakePatch
        {
            private static readonly FieldInfo abAircraftField = AccessTools.Field(typeof(Airbrake), "aircraft");
            private static readonly FieldInfo abAttachedField = AccessTools.Field(typeof(Airbrake), "attachedAircraft");
            private static readonly FieldInfo abPartField     = AccessTools.Field(typeof(Airbrake), "part");
            private static readonly HashSet<int> reinforced   = new HashSet<int>();

            public static void Prefix(Airbrake __instance)
            {
                var aircraft = abAttachedField?.GetValue(__instance) as Aircraft
                            ?? abAircraftField?.GetValue(__instance) as Aircraft;
                if (!IsCompass(aircraft)) return;

                int id = __instance.GetInstanceID();
                if (reinforced.Contains(id)) return;

                var part = abPartField?.GetValue(__instance) as UnitPart;
                if (part != null)
                {
                    foreach (var j in part.GetComponents<FixedJoint>())
                    {
                        j.breakForce  = float.PositiveInfinity;
                        j.breakTorque = float.PositiveInfinity;
                    }
                    Log.LogInfo("[Airbrake] Reinforced joints on " + __instance.gameObject.name);
                }
                reinforced.Add(id);
            }
        }

        //  Laser Designator 

        [HarmonyPatch(typeof(LaserDesignator), "Awake")]
        public static class LaserDesignatorPatch
        {
            private static readonly FieldInfo rangeField      = AccessTools.Field(typeof(LaserDesignator), "range");
            private static readonly FieldInfo maxTargetsField = AccessTools.Field(typeof(LaserDesignator), "maxTargets");

            public static void Postfix(LaserDesignator __instance)
            {
                var aircraft = __instance.GetComponentInParent<Aircraft>();
                if (!IsCompass(aircraft)) return;

                rangeField?.SetValue(__instance, LaserRange);
                maxTargetsField?.SetValue(__instance, LaserMaxTargets);
                Log.LogInfo("[Laser] range=" + LaserRange + " maxTargets=" + LaserMaxTargets);
            }
        }

        //  Speed Gauge 

        [HarmonyPatch(typeof(SpeedGauge), "Refresh")]
        public static class SpeedGaugeRefreshPatch
        {
            private static readonly FieldInfo thresholdField      = AccessTools.Field(typeof(SpeedGauge), "overspeedThreshold");
            private static readonly FieldInfo sgAircraftField     = AccessTools.Field(typeof(SpeedGauge), "aircraft");
            private static readonly FieldInfo overspeedDispField  = AccessTools.Field(typeof(SpeedGauge), "overspeedDisplay");
            private static readonly FieldInfo lastOverspeedField  = AccessTools.Field(typeof(SpeedGauge), "lastOverspeed");
            private static readonly FieldInfo overspeedVoiceField = AccessTools.Field(typeof(SpeedGauge), "overspeedVoice");
            private static readonly FieldInfo airspeedDispField   = AccessTools.Field(typeof(SpeedGauge), "airspeedDisplay");
            private static bool voiceNulled = false;

            public static void Prefix(SpeedGauge __instance)
            {
                var aircraft = sgAircraftField?.GetValue(__instance) as Aircraft;
                if (!IsCompass(aircraft)) return;
                thresholdField?.SetValue(__instance, OverspeedThreshold);
                if (!voiceNulled && overspeedVoiceField != null)
                {
                    overspeedVoiceField.SetValue(__instance, null);
                    voiceNulled = true;
                }
                lastOverspeedField?.SetValue(__instance, Time.timeSinceLevelLoad);
            }

            public static void Postfix(SpeedGauge __instance)
            {
                var aircraft = sgAircraftField?.GetValue(__instance) as Aircraft;
                if (!IsCompass(aircraft)) return;
                var disp = overspeedDispField?.GetValue(__instance) as Text;
                if (disp != null && disp.enabled) disp.enabled = false;
                var spd = airspeedDispField?.GetValue(__instance) as Text;
                if (spd != null && spd.color == Color.red) spd.color = Color.white;
            }
        }

        [HarmonyPatch(typeof(SpeedGauge), "Initialize")]
        public static class SpeedGaugeInitPatch
        {
            private static readonly FieldInfo thresholdField = AccessTools.Field(typeof(SpeedGauge), "overspeedThreshold");
            public static void Postfix(SpeedGauge __instance, Aircraft aircraft)
            {
                if (!IsCompass(aircraft)) return;
                thresholdField?.SetValue(__instance, OverspeedThreshold);
            }
        }

        //  Scramjet HUD Indicator 

        [HarmonyPatch(typeof(FlightHud), "Update")]
        public static class ScramjetHudPatch
        {
            private static GameObject hudObject;
            private static Text hudText;
            private static bool wasActive = false;
            private static float pulseTimer = 0f;

            public static void Postfix(FlightHud __instance)
            {
                if (hudObject == null)
                {
                    try
                    {
                        var canvasField = AccessTools.Field(typeof(FlightHud), "canvas");
                        var canvas = canvasField?.GetValue(__instance) as Canvas;
                        if (canvas == null) return;

                        hudObject = new GameObject("TA30ScramjetIndicator");
                        hudObject.transform.SetParent(canvas.transform, false);

                        hudText = hudObject.AddComponent<Text>();
                        hudText.text      = "SCRAMJET ACTIVE";
                        hudText.font      = Resources.GetBuiltinResource<Font>("Arial.ttf");
                        hudText.fontSize  = 22;
                        hudText.fontStyle = FontStyle.Bold;
                        hudText.alignment = TextAnchor.MiddleCenter;
                        hudText.color     = new Color(1f, 0.6f, 0f, 1f);

                        var outline = hudObject.AddComponent<Outline>();
                        outline.effectColor    = new Color(0f, 0f, 0f, 0.8f);
                        outline.effectDistance = new Vector2(1.5f, -1.5f);

                        var rect = hudObject.GetComponent<RectTransform>();
                        rect.anchorMin        = new Vector2(0.5f, 0.75f);
                        rect.anchorMax        = new Vector2(0.5f, 0.75f);
                        rect.pivot            = new Vector2(0.5f, 0.5f);
                        rect.anchoredPosition = Vector2.zero;
                        rect.sizeDelta        = new Vector2(300f, 40f);

                        hudObject.SetActive(false);
                    }
                    catch { return; }
                }

                if (scramjetActive != wasActive)
                {
                    hudObject.SetActive(scramjetActive);
                    wasActive = scramjetActive;
                    if (scramjetActive) pulseTimer = 0f;
                }

                if (scramjetActive && hudText != null)
                {
                    pulseTimer += Time.deltaTime;
                    float alpha = 0.7f + 0.3f * Mathf.Sin(pulseTimer * 3f);
                    hudText.color = new Color(1f, 0.6f, 0f, alpha);
                }
            }
        }

        //  FBW Damper 

        [HarmonyPatch(typeof(ControlsFilter), "Filter",
            new[] { typeof(ControlInputs), typeof(Vector3), typeof(Rigidbody), typeof(float), typeof(bool) })]
        public static class FBWHypersonicDamperPatch
        {
            public static void Postfix(ControlInputs inputs, Vector3 rawInputs, Rigidbody rb, float gForce, bool flightAssist)
            {
                var aircraft = rb.GetComponent<Aircraft>();
                if (!IsCompass(aircraft)) return;

                float alt  = rb.transform.position.y - Datum.originPosition.y;
                float sos  = Mathf.Max(-0.005f * alt + 340f, 290f);
                float mach = aircraft.speed / sos;

                if (mach > FBWMachThreshold)
                {
                    float damper = FBWMachThreshold / mach;
                    inputs.pitch *= damper;
                    inputs.roll  *= damper;
                }
            }
        }

        //  Airframe Strength 

        [HarmonyPatch(typeof(UnitPart), "Awake")]
        public static class AirframeStrengthPatch
        {
            private static readonly FieldInfo maxHPField = AccessTools.Field(typeof(UnitPart), "maxHP");
            private static readonly HashSet<int> reinforced = new HashSet<int>();

            public static void Postfix(UnitPart __instance)
            {
                var aircraft = __instance.GetComponentInParent<Aircraft>();
                if (!IsCompass(aircraft)) return;

                int id = __instance.GetInstanceID();
                if (reinforced.Contains(id)) return;

                if (maxHPField != null)
                {
                    float v = (float)maxHPField.GetValue(__instance);
                    maxHPField.SetValue(__instance, v * AirframeHPMultiplier);
                    Log.LogInfo("[Airframe] " + __instance.gameObject.name + " maxHP " + v + " -> " + (v * AirframeHPMultiplier));
                }
                reinforced.Add(id);
            }
        }
    }
}
