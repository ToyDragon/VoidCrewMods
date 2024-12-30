using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Timers;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using RuntimeUnityEditor.Bepin5;
using RuntimeUnityEditor.Core;
using UnityEngine;

namespace FrogtownVoidMods
{
    [BepInDependency("RuntimeUnityEditor")]
    [BepInPlugin(RuntimeUnityEditorVoidCrewPatches.PLUGIN_GUID, RuntimeUnityEditorVoidCrewPatches.PLUGIN_NAME, RuntimeUnityEditorVoidCrewPatches.PLUGIN_VERSION)]
    public class RuntimeUnityEditorVoidCrewPatches : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "runtimeunityeditorvoidcrewpatches.frogtown.me";
        public const string PLUGIN_NAME = "Runtime Unity Editor Void Crew Patches";
        public const string PLUGIN_VERSION = "0.1";

        private static bool _started = false;
        private static ManualLogSource L;
        private static DateTime _lastRegenAttempt;

        // For some reason void crew keeps destroying the primary mod object, so this class gets regenerated and forwards the update event to the RUE.
        [Obsolete]
        public static void RegenInstance(object sender, ElapsedEventArgs e)
        {
            if (RuntimeUnityEditor5.Instance != null) { return; }
            if (_lastRegenAttempt.AddSeconds(5) > DateTime.Now) { return; }
            _lastRegenAttempt = DateTime.Now;
            var rueGo = new GameObject("RuntimeUnityEditor5");
            rueGo.AddComponent<RuntimeUnityEditor5>();
            L.LogInfo("Regenerated RuntimeUnityEditor5");
        }
        public static void StartRegenThread()
        {
            var timer = new Timer(100);
            timer.Elapsed += RegenInstance;
            timer.AutoReset = true;
            timer.Enabled = true;
        }
        class PatchUERMouseInspect {
            private static Camera masterCamera;
            [HarmonyPatch(typeof(RuntimeUnityEditor.Core.MouseInspect), "DoRaycast")]
            [HarmonyPrefix()]
            private static bool DoRaycast(Camera camera, ref Transform __result) {
                __result = null;
                if (camera == Camera.main) {
                    if (!masterCamera) {
                        var go = GameObject.Find("MasterCamera");
                        if (go) {
                            masterCamera = go.GetComponent<Camera>();
                        }
                    }
                    camera = masterCamera;
                }
                if (!camera) { return false; }
                RaycastHit hitInfo;
                if (Physics.Raycast(camera.ScreenPointToRay(UnityInput.Current.mousePosition), out hitInfo, 1000f)) {
                    __result = hitInfo.transform;
                }
                return false;
            }
        }
        private void Awake()
        {
            if (_started) { return; }
            _started = true;
            L = Logger;
            Harmony.CreateAndPatchAll(typeof(PatchUERMouseInspect));
            RegenInstance(null, null);
            StartRegenThread();
        }
    }
}
