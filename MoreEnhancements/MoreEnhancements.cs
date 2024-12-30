using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Timers;
using BepInEx;
using BepInEx.Logging;
using CG;
using CG.Network;
using CG.Utils;
using Client.Player.Interactions;
using Gameplay.CompositeWeapons;
using Gameplay.Enhancements;
using Gameplay.Utilities;
using HarmonyLib;
using Photon.Pun;
using ResourceAssets;
using UI.Ping;
using UnityEngine;

namespace FrogtownVoidMods
{
    [BepInPlugin(MoreEnhancements.PLUGIN_GUID, MoreEnhancements.PLUGIN_NAME, MoreEnhancements.PLUGIN_VERSION)]
    public class MoreEnhancements : BaseUnityPlugin
    {
        public static MoreEnhancements instance;

        public const string PLUGIN_GUID = "moreenhancements.frogtown.me";
        public const string PLUGIN_NAME = "More Enhancements";
        public const string PLUGIN_VERSION = "0.1";

        public static string PATH_Charge_Station = "Prefabs/Space Objects/Modules/Buildable/Module_Buildable_ChargeStation_01";
        public static GUIDUnion GUID_ME_Enhancement_Panel = new GUIDUnion(40, 40, 40, 0);
        private static ManualLogSource L;

        public class ME_Enhancement : Enhancement
        {
            public static HashSet<int> AttachedViews = new HashSet<int>();
            public int cellModuleViewId;
            public override void OnPhotonInstantiate(PhotonMessageInfo info)
            {
                base.OnPhotonInstantiate(info);
                Dictionary<byte, object> instantiationData = InstantiationDataParser.ParseInstantiationData(info.photonView.InstantiationData);
                if (!instantiationData.TryGetValue(40, out var targetIdObject) || !(targetIdObject is int targetId)) {
                    L.LogError($"No targetId to attach panel to :(");
                    return;
                }
                AttachedViews.Add(targetId);
                var weaponModule = ObjectRegistry.FindByPhotonView<CompositeWeaponModule>(targetId);
                if (weaponModule == null) {
                    L.LogError($"No weapon with id {targetId}");
                    return;
                }

                cellModuleViewId = targetId;
                transform.SetParent(weaponModule.transform);
                transform.position = weaponModule.transform.position + -1.75f*weaponModule.transform.forward + .85f*weaponModule.transform.right + .1f*weaponModule.transform.up;
                transform.localEulerAngles = new Vector3(-30, 180, 0);
                contextInfo = (ContextInfo)weaponModule.ContextInfo.source;
                Traverse.Create(this).Field("targets").SetValue(new List<Component>() { weaponModule });
                L.LogInfo($"Instantiated enhancement panel for {targetId}: AmOwner={photonView.AmOwner}");

                var cip = GetComponent<ContextInfoProvider>();
                var t = Traverse.Create(cip);
                t.Field("contextItem").SetValue(this);
                t.Field("contextInfoSource").SetValue(weaponModule);
                photonView.ObservedComponents.Clear();
                photonView.ObservedComponents.Add(this);
                var timeDisplays = GetComponentsInChildren<DurationTimeDisplayer>(true);
                foreach (var timeDisplay in timeDisplays)
                {
                    Traverse.Create(timeDisplay).Field("enhancement").SetValue(this);
                }
                AppliedEnhancementEffect = new EnhancementEffectAsset();
                AppliedEnhancementEffect.ActiveDuration = 30;
                List<StatMod> modifiersA = new List<StatMod>(), modifiersB = new List<StatMod>(), modifiersC = new List<StatMod>();
                foreach (var kvp in weaponModule.Stats.Stats)
                {
                    if (kvp.Key > 10000) { // excludes UI/power/status stats.
                        continue;
                    }
                    if (kvp.Key == StatType.HeatPerShot.Id) {
                        continue;
                    }
                    bool invert = kvp.Key == StatType.ReloadTime.Id;
                    modifiersA.Add(new StatMod(FloatModifier.ScalarModifier(invert ? 1/1.1f : 1.1f), kvp.Key));
                    modifiersB.Add(new StatMod(FloatModifier.ScalarModifier(invert ? 1/1.25f : 1.25f), kvp.Key));
                    modifiersC.Add(new StatMod(FloatModifier.ScalarModifier(invert ? 1/1.50f : 1.50f), kvp.Key));
                }
                AppliedEnhancementEffect.SuccessfulEffects = new EnhancementEffectAsset.Effect[]
                {
                    new EnhancementEffectAsset.Effect()
                    {
                        grade = new EnhancementGrade() { RequiredScore = 1 },
                        Modifiers = modifiersA
                    },
                    new EnhancementEffectAsset.Effect()
                    {
                        grade = new EnhancementGrade() { RequiredScore = 2 },
                        Modifiers = modifiersB
                    },
                    new EnhancementEffectAsset.Effect()
                    {
                        grade = new EnhancementGrade() { RequiredScore = 3 },
                        Modifiers = modifiersC
                    }
                };
                var pingableCollection = GetComponentInParent<PingableItemCollection>();
                var pingableItems = GetComponentsInChildren<PingableItem>();
                foreach (var item in pingableItems)
                {
                    pingableCollection.AddPingableItem(item);
                }

                enabled = true;
            }

            public static void CreateIfMissing(CompositeWeaponModule weapon)
            {
                if (!weapon.AmOwner) { return; }
                if (AttachedViews.Contains(weapon.photonView.ViewID)) { return; }
                AttachedViews.Add(weapon.photonView.ViewID);

                L.LogInfo($"Creating enhancement panel for {weapon.photonView.ViewID}");
                var obj = ObjectFactory.InstantiateResourceAssetByGUID(GUID_ME_Enhancement_Panel, Vector3.zero, Quaternion.identity, new Dictionary<byte, object>()
                {
                    {40, weapon.photonView.ViewID }
                });
            }

            [PunRPC]
            private void StateChangePun(
              EnhancementState newState,
              float activationGrade,
              float durationMultiplier)
            {
                // PunRPCs don't check base types, this just fails if not redefined and forwarded.
                typeof(Enhancement).GetMethod("StateChangePun", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(this, new object[] { newState, activationGrade, durationMultiplier });
            }
        }
        
        // This singleton contains the actual mod logic.
        public class MoreEnhancementsCore
        {
            public static MoreEnhancementsCore instance;

            private static List<CompositeWeaponModule> weaponModules = new List<CompositeWeaponModule>();
            private static PlayerShip ship;
            private DateTime lastCreateAttempt = DateTime.Now;
            private bool initialized = false;
            public void TryCreatePrefab() {
                if (DateTime.Now < lastCreateAttempt.AddSeconds(5)) { return; }
                lastCreateAttempt = DateTime.Now;

                var rac = ResourceAssetContainer<CloneStarObjectContainer, AbstractCloneStarObject, CloneStarObjectDef>.Instance;
                if (rac == null) {
                    L.LogInfo("RAC not yet initialized");
                    return;
                }
                L.LogInfo($"Trying to create ME_Enhancement prefab");
                if (PhotonNetwork.PrefabPool == null || !(PhotonNetwork.PrefabPool is DefaultPool defaultPool)) {
                    L.LogError("Prefab pool is not a DefaultPool, probably caused by a mod conflict?");
                    return;
                }
                CloneStarObjectDef chargeStationDef = null;
                try {
                    chargeStationDef = rac.GetAssetByPath(PATH_Charge_Station);
                } catch (Exception e) {}
                if (chargeStationDef == null) {
                    L.LogError($"RAC doesn't contain charge station definition?");
                    return;
                }

                L.LogInfo($"Checking for charge station at {chargeStationDef.Path}");

                var vanillaPrefab = Resources.Load<GameObject>(chargeStationDef.Path);
                if (vanillaPrefab == null) {
                    L.LogError("Unable to find prefab for charge station.");
                    return;
                }

                var chargeStationPrefab = GameObject.Instantiate(vanillaPrefab);
                var enhancementPrefab = GameObject.Instantiate(chargeStationPrefab.GetComponentInChildren<Enhancement>().gameObject);
                var oldEnhancement = enhancementPrefab.GetComponent<Enhancement>();
                var newEnhancement = enhancementPrefab.AddComponent<ME_Enhancement>();
                newEnhancement.AppliedEnhancementEffect = oldEnhancement.AppliedEnhancementEffect;
                Traverse.Create(enhancementPrefab.GetComponent<EnhancementActivator>()).Field("requiresPower").SetValue(false);
                var modifiers = (List<ContextInfoModifier>)Traverse.Create(enhancementPrefab.GetComponent<ContextInfoProvider>()).Field("modifiers").GetValue();
                foreach (var modifier in modifiers) {
                    if (modifier is EnhancementContextModifier enhancementModifier)
                    {
                        Traverse.Create(enhancementModifier).Field("enhancement").SetValue(newEnhancement);
                    }
                }
                var animator = enhancementPrefab.GetComponentInChildren<EnhancementCrypticAnimator>();
                Traverse.Create(animator).Field("enhancement").SetValue(newEnhancement);
                var contextInfoProvider = enhancementPrefab.GetComponent<ContextInfoProvider>();
                Traverse.Create(contextInfoProvider).Field("contextItem").SetValue(newEnhancement);

                Destroy(chargeStationPrefab);
                Destroy(oldEnhancement);

                var path = "MoreEnhancements/ME_EnhancementPanel";
                L.LogInfo($"Created ME_Enhancement prefab called {enhancementPrefab.name}__{path}__{Path.GetFileName(path)}");
                ResourcePaths.Instance.SetPath(GUID_ME_Enhancement_Panel, path);
                defaultPool.ResourceCache.Add(path, enhancementPrefab);
                L.LogInfo("Registered ME_Enhancement prefab");
                initialized = true;
            }

            public void Update(GameObject go)
            {
                TryCreatePrefab();
                if (!initialized) {
                    TryCreatePrefab();
                    return;
                }
                if (ship == null) {
                    ship = GameObject.FindAnyObjectByType<PlayerShip>();
                }
                if (ship == null) { return; }
                weaponModules.Clear();
                ship.GetComponentsInChildren(weaponModules);
                foreach (var weapon in weaponModules)
                {
                    ME_Enhancement.CreateIfMissing(weapon);
                }
            }
        }

        // For some reason void crew keeps destroying the primary mod object, so this class gets regenerated and forwards the update event to the RUE.
        public class UnityUpdateShim : MonoBehaviour
        {
            private static UnityUpdateShim _instance;
            private static DateTime _lastRegenAttempt;
            private static Timer _timer;
            void Update()
            {
                MoreEnhancementsCore.instance.Update(gameObject);
            }
            [Obsolete]
            public static void RegenInstance(object sender, ElapsedEventArgs e)
            {
                if (_instance) { return; }
                if (_lastRegenAttempt.AddSeconds(5) > DateTime.Now) { return; }
                _lastRegenAttempt = DateTime.Now;
                var go = new GameObject($"{PLUGIN_NAME}");
                _instance = go.AddComponent<UnityUpdateShim>();
                L.LogInfo($"Recreated shim.");
            }

            [Obsolete]
            public static void StartRegenThread()
            {
                _timer = new Timer(100);
                _timer.Elapsed += RegenInstance;
                _timer.AutoReset = true;
                _timer.Enabled = true;
                L.LogInfo($"Started regen thread.");
            }
        }

        [Obsolete]
        private void Awake()
        {
            if (MoreEnhancementsCore.instance != null) { return; }
            L = Logger;
            L.LogInfo("Creating core.");
            MoreEnhancementsCore.instance = new MoreEnhancementsCore();
            UnityUpdateShim.RegenInstance(null, null);
            UnityUpdateShim.StartRegenThread();
        }
    }
}
