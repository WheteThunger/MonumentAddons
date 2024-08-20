using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Plugins.MonumentAddonsExtensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Text;
using UnityEngine;
using static IOEntity;
using static WireTool;
using Component = UnityEngine.Component;
using HumanNPCGlobal = global::HumanNPC;
using SkullTrophyGlobal = global::SkullTrophy;

using CustomInitializeCallback = System.Func<BasePlayer, string[], object>;
using CustomInitializeCallbackV2 = System.Func<BasePlayer, string[], System.ValueTuple<bool, object>>;
using CustomEditCallback = System.Func<BasePlayer, string[], UnityEngine.Component, Newtonsoft.Json.Linq.JObject, System.ValueTuple<bool, object>>;
using CustomSpawnCallback = System.Func<UnityEngine.Vector3, UnityEngine.Quaternion, Newtonsoft.Json.Linq.JObject, UnityEngine.Component>;
using CustomSpawnCallbackV2 = System.Func<System.Guid, UnityEngine.Component, UnityEngine.Vector3, UnityEngine.Quaternion, Newtonsoft.Json.Linq.JObject, UnityEngine.Component>;
using CustomCheckSpaceCallback = System.Func<UnityEngine.Vector3, UnityEngine.Quaternion, Newtonsoft.Json.Linq.JObject, bool>;
using CustomKillCallback = System.Action<UnityEngine.Component>;
using CustomUnloadCallback = System.Action<UnityEngine.Component>;
using CustomUpdateCallback = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject>;
using CustomUpdateCallbackV2 = System.Func<UnityEngine.Component, Newtonsoft.Json.Linq.JObject, UnityEngine.Component>;
using CustomDisplayCallback = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject, System.Text.StringBuilder>;
using CustomDisplayCallbackV2 = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject, BasePlayer, System.Text.StringBuilder, float>;
using CustomSetDataCallback = System.Action<UnityEngine.Component, object>;

using Tuple1 = System.ValueTuple<object>;
using Tuple2 = System.ValueTuple<object, object>;
using Tuple3 = System.ValueTuple<object, object, object>;
using Tuple4 = System.ValueTuple<object, object, object, object>;

namespace Oxide.Plugins
{
    [Info("Monument Addons", "WhiteThunder", "0.17.3")]
    [Description("Allows adding entities, spawn points and more to monuments.")]
    internal class MonumentAddons : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin CopyPaste, CustomVendingSetup, EntityScaleManager, MonumentFinder, SignArtist;

        private MonumentAddons _plugin;
        private Configuration _config;
        private StoredData _data;
        private ProfileStateData _profileStateData;

        private const float MaxRaycastDistance = 100;
        private const float TerrainProximityTolerance = 0.001f;
        private const float MaxFindDistanceSquared = 4;
        private const float ShowVanillaDuration = 60;

        private const string PermissionAdmin = "monumentaddons.admin";

        private const string WireToolPlugEffect = "assets/prefabs/tools/wire/effects/plugeffect.prefab";

        private const string CargoShipPrefab = "assets/content/vehicles/boats/cargoship/cargoshiptest.prefab";
        private const string CargoShipShortName = "cargoshiptest";
        private const string DefaultProfileName = "Default";
        private const string DefaultUrlPattern = "https://github.com/WheteThunger/MonumentAddons/blob/master/Profiles/{0}.json?raw=true";

        private static readonly int HitLayers = Rust.Layers.Solid
            | Rust.Layers.Mask.Water;

        private static readonly Dictionary<string, string> JsonRequestHeaders = new Dictionary<string, string>
        {
            { "Content-Type", "application/json" }
        };

        private readonly HookCollection _dynamicMonumentHooks;
        private readonly ProfileStore _profileStore = new ProfileStore();
        private readonly OriginalProfileStore _originalProfileStore = new OriginalProfileStore();
        private readonly ProfileManager _profileManager;
        private readonly CoroutineManager _coroutineManager = new CoroutineManager();
        private readonly AddonComponentTracker _componentTracker = new AddonComponentTracker();
        private readonly AdapterListenerManager _adapterListenerManager;
        private readonly ControllerFactory _controllerFactory;
        private readonly CustomAddonManager _customAddonManager;
        private readonly CustomMonumentManager _customMonumentManager;
        private readonly UniqueNameRegistry _uniqueNameRegistry = new UniqueNameRegistry();
        private readonly AdapterDisplayManager _adapterDisplayManager;
        private readonly MonumentHelper _monumentHelper;
        private readonly WireToolManager _wireToolManager;
        private readonly IOManager _ioManager = new IOManager();
        private readonly UndoManager _undoManager = new UndoManager();

        private readonly ValueRotator<Color> _colorRotator = new(
            Color.HSVToRGB(0, 1, 1),
            Color.HSVToRGB(0.1f, 1, 1),
            Color.HSVToRGB(0.2f, 1, 1),
            Color.HSVToRGB(0.35f, 1, 1),
            Color.HSVToRGB(0.55f, 1, 1),
            Color.HSVToRGB(0.8f, 1, 1),
            new Color(1, 1, 1)
        );

        private readonly object True = true;
        private readonly object False = false;

        private ItemDefinition _waterDefinition;
        private ProtectionProperties _immortalProtection;
        private ActionDebounced _saveProfileStateDebounced;
        private StringBuilder _sb = new StringBuilder();
        private HashSet<string> _deployablePrefabs = new();

        private Coroutine _startupCoroutine;
        private bool _serverInitialized;
        private bool _isLoaded = true;

        public MonumentAddons()
        {
            _profileManager = new ProfileManager(this, _originalProfileStore, _profileStore);
            _adapterDisplayManager = new AdapterDisplayManager(this, _uniqueNameRegistry);
            _adapterListenerManager = new AdapterListenerManager(this);
            _customAddonManager = new CustomAddonManager(this);
            _customMonumentManager = new CustomMonumentManager(this);
            _controllerFactory = new ControllerFactory(this);
            _monumentHelper = new MonumentHelper(this);
            _wireToolManager = new WireToolManager(this, _profileStore);

            _saveProfileStateDebounced = new ActionDebounced(timer, 1, () =>
            {
                if (!_isLoaded)
                    return;

                _profileStateData.Save();
            });

            _dynamicMonumentHooks = new HookCollection(
                this,
                new[] { nameof(OnEntitySpawned) },
                () => _profileManager.HasAnyEnabledDynamicMonuments
            );
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _data = StoredData.Load(_profileStore);
            _profileStateData = ProfileStateData.Load(_data);

            _config.Init();

            // Ensure the profile folder is created to avoid errors.
            _profileStore.EnsureDefaultProfile();

            permission.RegisterPermission(PermissionAdmin, this);

            _dynamicMonumentHooks.Unsubscribe();
            _adapterListenerManager.Init();
        }

        private void OnServerInitialized()
        {
            _waterDefinition = ItemManager.FindItemDefinition("water");

            _immortalProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
            _immortalProtection.name = "MonumentAddonsProtection";
            _immortalProtection.Add(1);

            _uniqueNameRegistry.OnServerInitialized();
            _adapterListenerManager.OnServerInitialized();
            _monumentHelper.OnServerInitialized();
            _ioManager.OnServerInitialized();

            var entitiesToKill = _profileStateData.CleanDisabledProfileState();
            if (entitiesToKill.Count > 0)
            {
                CoroutineManager.StartGlobalCoroutine(KillEntitiesRoutine(entitiesToKill));
            }

            if (CheckDependencies())
            {
                StartupRoutine();
            }

            _deployablePrefabs = DetermineAllDeployablePrefabs();

            _profileManager.ProfileStatusChanged += (_, _, _) => _dynamicMonumentHooks.Refresh();
            _serverInitialized = true;
        }

        private void Unload()
        {
            _coroutineManager.Destroy();
            _profileManager.UnloadAllProfiles();
            _saveProfileStateDebounced.Flush();
            _wireToolManager.Unload();

            UnityEngine.Object.Destroy(_immortalProtection);

            _isLoaded = false;
        }

        private void OnNewSave()
        {
            _profileStateData.Reset();
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            // Check whether initialized to detect only late (re)loads.
            // Note: We are not dynamically subscribing to OnPluginLoaded since that interferes with [PluginReference] for some reason.
            if (_serverInitialized && plugin == MonumentFinder)
            {
                StartupRoutine();
            }
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin.Name == Name)
                return;

            _customAddonManager.UnregisterAllForPlugin(plugin);
            _customMonumentManager.UnregisterAllForPlugin(plugin);
        }

        private void OnEntitySpawned(BaseEntity entity)
        {
            if (!IsDynamicMonument(entity))
                return;

            var entityForClosure = entity;
            NextTick(() =>
            {
                if (ExposedHooks.OnDynamicMonument(entityForClosure) is false)
                    return;

                var dynamicMonument = new DynamicMonument(entityForClosure);
                _coroutineManager.StartCoroutine(_profileManager.PartialLoadForLateMonumentRoutine(dynamicMonument));
            });
        }

        // This hook is exposed by plugin: Remover Tool (RemoverTool).
        private object canRemove(BasePlayer player, BaseEntity entity)
        {
            if (_componentTracker.IsAddonComponent(entity))
                return False;

            return null;
        }

        private object CanChangeGrade(BasePlayer player, BuildingBlock block, BuildingGrade.Enum grade)
        {
            if (_componentTracker.IsAddonComponent(block) && !HasAdminPermission(player))
                return False;

            return null;
        }

        private object CanUpdateSign(BasePlayer player, ISignage signage)
        {
            if (_componentTracker.IsAddonComponent(signage as BaseEntity) && !HasAdminPermission(player))
            {
                ChatMessage(player, LangEntry.ErrorNoPermission);
                return False;
            }

            return null;
        }

        private void OnSignUpdated(ISignage signage, BasePlayer player)
        {
            if (!_componentTracker.IsAddonComponent(signage as BaseEntity, out SignController controller))
                return;

            controller.UpdateSign(signage.GetTextureCRCs());
        }

        // This hook is exposed by plugin: Sign Arist (SignArtist).
        private void OnImagePost(BasePlayer player, string url, bool raw, ISignage signage, uint textureIndex = 0)
        {
            if (!_componentTracker.IsAddonComponent(signage as BaseEntity, out SignController controller))
                return;

            if (controller.EntityData.SignArtistImages == null)
            {
                controller.EntityData.SignArtistImages = new SignArtistImage[signage.TextureCount];
            }
            else if (controller.EntityData.SignArtistImages.Length < signage.TextureCount)
            {
                Array.Resize(ref controller.EntityData.SignArtistImages, signage.TextureCount);
            }

            controller.EntityData.SignArtistImages[textureIndex] = new SignArtistImage
            {
                Url = url,
                Raw = raw,
            };
            _profileStore.Save(controller.Profile);
        }

        private object OnSprayRemove(SprayCanSpray spray, BasePlayer player)
        {
            if (_componentTracker.IsAddonComponent(spray))
                return False;

            return null;
        }

        private void OnEntityScaled(BaseEntity entity, float scale)
        {
            if (!_componentTracker.IsAddonComponent(entity, out EntityController controller)
                || controller.EntityData.Scale == scale)
                return;

            controller.EntityData.Scale = scale;
            controller.StartUpdateRoutine();
            _profileStore.Save(controller.Profile);
        }

        // This hook is exposed by plugin: Telekinesis.
        private Component OnTelekinesisFindFailed(BasePlayer player)
        {
            if (!HasAdminPermission(player))
                return null;

            return FindAdapter<TransformAdapter>(player).Adapter?.Component;
        }

        // This hook is exposed by plugin: Telekinesis.
        private Tuple<Component, Component> OnTelekinesisStart(BasePlayer player, BaseEntity entity)
        {
            if (!_componentTracker.IsAddonComponent(entity, out PasteAdapter adapter, out PasteController _))
                return null;

            return new Tuple<Component, Component>(adapter.Component, adapter.Component);
        }

        // This hook is exposed by plugin: Telekinesis.
        private object CanStartTelekinesis(BasePlayer player, Component moveComponent, Component rotateComponent)
        {
            if (IsTransformAddon(moveComponent, out _) && !HasAdminPermission(player))
                return False;

            return null;
        }

        // This hook is exposed by plugin: Telekinesis.
        private void OnTelekinesisStarted(BasePlayer player, Component moveComponent, Component rotateComponent)
        {
            if (!IsTransformAddon(moveComponent, out var adapter, out var controller)
                || controller is not IUpdateableController)
                return;

            if (adapter.Component == moveComponent || adapter.Component == rotateComponent)
            {
                _adapterDisplayManager.SetPlayerMovingAdapter(player, adapter);
            }

            _adapterDisplayManager.ShowAllRepeatedly(player);

            if (moveComponent is BaseEntity moveEntity && IsSpawnPointEntity(moveEntity))
            {
                _adapterDisplayManager.ShowAllRepeatedly(player);

                var spawnedVehicleComponent = moveComponent.GetComponent<SpawnedVehicleComponent>();
                if (spawnedVehicleComponent != null)
                {
                    spawnedVehicleComponent.CancelInvoke(spawnedVehicleComponent.CheckPositionTracked);
                }
            }
        }

        // This hook is exposed by plugin: Telekinesis.
        private void OnTelekinesisStopped(BasePlayer player, Component moveComponent, Component rotateComponent)
        {
            if (!IsTransformAddon(moveComponent, out TransformAdapter transformAdapter, out BaseController controller)
                || controller is not IUpdateableController updateableController)
                return;

            if (player != null)
            {
                _adapterDisplayManager.SetPlayerMovingAdapter(player, null);
            }

            if (!transformAdapter.TryRecordUpdates(moveComponent.transform, rotateComponent.transform))
                return;

            if (moveComponent is CustomSpawnPoint spawnPoint)
            {
                spawnPoint.MoveSpawnedInstances();
            }

            updateableController.StartUpdateRoutine();
            _profileStore.Save(controller.Profile);

            if (player != null)
            {
                _adapterDisplayManager.ShowAllRepeatedly(player);
                ChatMessage(player, LangEntry.SaveSuccess, controller.Adapters.Count, controller.Profile.Name);
            }
        }

        // This hook is exposed by plugin: Custom Vending Setup (CustomVendingSetup).
        private Dictionary<string, object> OnCustomVendingSetupDataProvider(NPCVendingMachine vendingMachine)
        {
            if (!_componentTracker.IsAddonComponent(vendingMachine, out EntityController controller))
                return null;

            var vendingProfile = controller.EntityData.VendingProfile;
            if (vendingProfile == null)
            {
                // Check if there's one to be migrated.
                var migratedVendingProfile = MigrateVendingProfile(vendingMachine);
                if (migratedVendingProfile != null)
                {
                    controller.EntityData.VendingProfile = migratedVendingProfile;
                    _profileStore.Save(controller.Profile);
                    LogWarning($"Successfully migrated vending machine settings from CustomVendingSetup to MonumentAddons profile '{controller.Profile.Name}'.");
                }
            }

            return controller.GetVendingDataProvider();
        }

        #endregion

        #region Dependencies

        private bool CheckDependencies()
        {
            if (MonumentFinder == null)
            {
                LogError("MonumentFinder is not loaded, get it at https://umod.org.");
                return false;
            }

            return true;
        }

        private float GetEntityScale(BaseEntity entity)
        {
            if (EntityScaleManager == null)
                return 1;

            return Convert.ToSingle(EntityScaleManager?.Call("API_GetScale", entity));
        }

        private bool TryScaleEntity(BaseEntity entity, float scale)
        {
            return EntityScaleManager?.Call("API_ScaleEntity", entity, scale) is true;
        }

        private void SkinSign(ISignage signage, SignArtistImage[] signArtistImages)
        {
            if (SignArtist == null)
                return;

            var apiName = signage is Signage
                ? "API_SkinSign"
                : signage is PhotoFrame
                ? "API_SkinPhotoFrame"
                : signage is CarvablePumpkin
                ? "API_SkinPumpkin"
                : null;

            if (apiName == null)
            {
                LogError($"Unrecognized sign type: {signage.GetType()}");
                return;
            }

            for (uint textureIndex = 0; textureIndex < signArtistImages.Length; textureIndex++)
            {
                var imageInfo = signArtistImages[textureIndex];
                if (imageInfo == null)
                    continue;

                SignArtist.Call(apiName, null, signage, imageInfo.Url, imageInfo.Raw, textureIndex);
            }
        }

        private JObject MigrateVendingProfile(NPCVendingMachine vendingMachine)
        {
            return CustomVendingSetup?.Call("API_MigrateVendingProfile", vendingMachine) as JObject;
        }

        private void RefreshVendingProfile(NPCVendingMachine vendingMachine)
        {
            CustomVendingSetup?.Call("API_RefreshDataProvider", vendingMachine);
        }

        private static class PasteUtils
        {
            private static readonly string[] CopyPasteArgs =
            {
                "stability", "false",
                "checkplaced", "false",
            };

            private static VersionNumber _requiredVersion = new VersionNumber(4, 1, 32);

            public static bool IsCopyPasteCompatible(Plugin copyPaste)
            {
                return copyPaste != null && copyPaste.Version >= _requiredVersion;
            }

            public static bool DoesPasteExist(string filename)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile("copypaste/" + filename);
            }

            public static Action PasteWithCancelCallback(Plugin copyPaste, PasteData pasteData, Vector3 position, float yRotation, Action<BaseEntity> onEntityPasted, Action onPasteCompleted)
            {
                if (copyPaste == null)
                    return null;

                var args = CopyPasteArgs;
                if (pasteData.Args is { Length: > 0 })
                {
                    args = args.Concat(pasteData.Args).ToArray();
                }

                var result = copyPaste.Call("TryPasteFromVector3Cancellable", position, yRotation, pasteData.Filename, args, onPasteCompleted, onEntityPasted);
                if (!(result is ValueTuple<object, Action>))
                {
                    LogError($"CopyPaste returned an unexpected response for paste \"{pasteData.Filename}\": {result}. Is CopyPaste up-to-date?");
                    return null;
                }

                var pasteResult = (ValueTuple<object, Action>)result;
                if (!true.Equals(pasteResult.Item1))
                {
                    LogError($"CopyPaste returned an unexpected response for paste \"{pasteData.Filename}\": {pasteResult.Item1}.");
                    return null;
                }

                return pasteResult.Item2;
            }
        }

        #endregion

        #region API

        [HookMethod(nameof(API_IsMonumentEntity))]
        public object API_IsMonumentEntity(BaseEntity entity)
        {
            return ObjectCache.Get(_componentTracker.IsAddonComponent(entity));
        }

        [HookMethod(nameof(API_GetMonumentEntityGuid))]
        public object API_GetMonumentEntityGuid(BaseEntity entity)
        {
            if (!_componentTracker.IsAddonComponent(entity))
                return null;

            var adapter = AddonComponent.GetForComponent(entity).Adapter;
            if (adapter == null)
                return null;

            return ObjectCache.Get(adapter.Data.Id);
        }

        #endregion

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static void OnMonumentAddonsInitialized()
            {
                Interface.CallHook("OnMonumentAddonsInitialized");
            }

            public static void OnMonumentEntitySpawned(BaseEntity entity, Component monument, Guid guid)
            {
                Interface.CallHook("OnMonumentEntitySpawned", entity, monument, ObjectCache.Get(guid));
            }

            public static object OnDynamicMonument(BaseEntity entity)
            {
                return Interface.CallHook("OnDynamicMonument", entity);
            }
        }

        #endregion

        #region Commands

        private enum PuzzleOption
        {
            PlayersBlockReset,
            PlayerDetectionRadius,
            SecondsBetweenResets,
        }

        private enum SpawnGroupOption
        {
            Name,
            MaxPopulation,
            RespawnDelayMin,
            RespawnDelayMax,
            SpawnPerTickMin,
            SpawnPerTickMax,
            InitialSpawn,
            PreventDuplicates,
            PauseScheduleWhileFull,
            RespawnWhenNearestPuzzleResets,
        }

        private enum SpawnPointOption
        {
            Exclusive,
            SnapToGround,
            CheckSpace,
            RandomRotation,
            RandomRadius,
            PlayerDetectionRadius,
        }

        [Command("maspawn")]
        private void CommandSpawn(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyMonumentFinderLoaded(player)
                || !VerifyProfileSelected(player, out var profileController)
                || !VerifyValidEntityPrefabOrDeployable(player, args, out var prefabName, out var addonDefinition, out var skinId)
                || !VerifyLookingAtMonumentPosition(player, out var position, out var monument))
                return;

            DetermineLocalTransformData(position, basePlayer, monument, out var localPosition, out var localRotationAngles, out var isOnTerrain);

            BaseData addonData;

            if (prefabName != null)
            {
                // Found a valid prefab name.
                var shortPrefabName = GetShortName(prefabName);

                if (shortPrefabName == "big_wheel")
                {
                    localRotationAngles.y -= 90;
                    localRotationAngles.z = 270;
                }
                else if (shortPrefabName == "boatspawner")
                {
                    if (position.y == TerrainMeta.WaterMap.GetHeight(position))
                    {
                        // Set the boatspawner to -1.5 like the vanilla ones.
                        localPosition.y -= 1.5f;
                    }
                }
                else if (shortPrefabName == "spray.decal")
                {
                    localRotationAngles.x += 270;
                    localRotationAngles.y -= 90;
                }

                var entityData = new EntityData
                {
                    Id = Guid.NewGuid(),
                    Skin = skinId,
                    PrefabName = prefabName,
                    Position = localPosition,
                    RotationAngles = localRotationAngles,
                    SnapToTerrain = isOnTerrain,
                };

                if (shortPrefabName.StartsWith("generator.static"))
                {
                    entityData.Puzzle = _config.AddonDefaults.Puzzles.ApplyTo(new PuzzleData());
                }

                addonData = entityData;
            }
            else
            {
                // Found a custom addon definition.
                if (!addonDefinition.TryInitialize(basePlayer, args.Skip(1).ToArray(), out var pluginData))
                    return;

                addonData = new CustomAddonData
                {
                    Id = Guid.NewGuid(),
                    AddonName = addonDefinition.AddonName,
                    Position = localPosition,
                    RotationAngles = localRotationAngles,
                    SnapToTerrain = isOnTerrain,
                }.SetData(pluginData);
            }

            var matchingMonuments = GetMonumentsByIdentifier(monument.UniqueName);

            profileController.Profile.AddData(monument.UniqueName, addonData);
            _profileStore.Save(profileController.Profile);
            profileController.SpawnNewData(addonData, matchingMonuments);

            ReplyToPlayer(player, LangEntry.SpawnSuccess, matchingMonuments.Count, profileController.Profile.Name, monument.UniqueDisplayName);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer);

            if (addonData is not CustomAddonData && ShouldRecommendSpawnPoints(prefabName))
            {
                ReplyToPlayer(player, LangEntry.WarningRecommendSpawnPoint);
            }
        }

        [Command("maedit")]
        private void CommandEdit(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyLookingAtAdapter(player, out CustomAddonAdapter adapter, out CustomAddonController controller, LangEntry.ErrorNoCustomAddonFound))
                return;

            if (args.Length == 0)
            {
                ReplyToPlayer(player, LangEntry.EditSynax);
                return;
            }

            var addonDefinition = adapter.AddonDefinition;
            var addonName = addonDefinition.AddonName;
            if (addonName != args[0])
            {
                ReplyToPlayer(player, LangEntry.EditErrorNoMatch, args[0]);
                return;
            }

            if (!addonDefinition.SupportsEditing)
            {
                ReplyToPlayer(player, LangEntry.EditErrorNotEditable, addonName);
                return;
            }

            if (!addonDefinition.TryEdit(basePlayer, args.Skip(1).ToArray(), adapter.Component, adapter.CustomAddonData.GetSerializedData(), out var data))
                return;

            addonDefinition.SetData(_profileStore, controller, data);

            ReplyToPlayer(player, LangEntry.EditSuccess, addonName);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
        }

        [Command("maprefab")]
        private void CommandPrefab(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyMonumentFinderLoaded(player)
                || !VerifyProfileSelected(player, out var profileController)
                || !VerifyValidModderPrefab(player, args, out var prefabName)
                || !VerifyLookingAtMonumentPosition(player, out var position, out var monument))
                return;

            if (FindPrefabBaseEntity(prefabName) != null)
            {
                ReplyToPlayer(player, LangEntry.PrefabErrorIsEntity, prefabName);
                return;
            }

            DetermineLocalTransformData(position, basePlayer, monument, out var localPosition, out var localRotationAngles, out var isOnTerrain);

            var prefabData = new PrefabData
            {
                Id = Guid.NewGuid(),
                PrefabName = prefabName,
                Position = localPosition,
                RotationAngles = localRotationAngles,
                SnapToTerrain = isOnTerrain,
            };

            var matchingMonuments = GetMonumentsByIdentifier(monument.UniqueName);

            profileController.Profile.AddData(monument.UniqueName, prefabData);
            _profileStore.Save(profileController.Profile);
            profileController.SpawnNewData(prefabData, matchingMonuments);

            ReplyToPlayer(player, LangEntry.PrefabSuccess, matchingMonuments.Count, profileController.Profile.Name, monument.UniqueDisplayName);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
        }

        [Command("masave")]
        private void CommandSave(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyLookingAtAdapter(player, out EntityAdapter adapter, out EntityController controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            if (!adapter.TryRecordUpdates())
            {
                ReplyToPlayer(player, LangEntry.SaveNothingToDo);
                return;
            }

            ReplyToPlayer(player, LangEntry.SaveSuccess, controller.Adapters.Count, controller.Profile.Name);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
        }

        [Command("makill")]
        private void CommandKill(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyLookingAtAdapter(player, out TransformAdapter adapter, out BaseController controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            // Capture adapter count before killing the controller.
            var numAdapters = controller.Adapters.Count;

            controller.Profile.RemoveData(adapter.Data, out var monumentIdentifier);
            _dynamicMonumentHooks.Refresh();
            var profile = controller.Profile;
            _profileStore.Save(profile);

            var profileController = controller.ProfileController;
            var killRoutine = controller.Kill(adapter.Data);
            if (killRoutine != null)
            {
                profileController.StartCallbackRoutine(killRoutine, profileController.SetupIO);
            }

            if (controller.Data is SpawnGroupData spawnGroupData && adapter.Data is SpawnPointData spawnPointData)
            {
                _undoManager.AddUndo(basePlayer, new UndoKillSpawnPoint(this, profileController, monumentIdentifier, spawnGroupData, spawnPointData));
            }
            else
            {
                _undoManager.AddUndo(basePlayer, new UndoKill(this, profileController, monumentIdentifier, controller.Data));
            }

            ReplyToPlayer(player, LangEntry.KillSuccess, GetAddonName(player, adapter.Data), numAdapters, profile.Name);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
        }

        [Command("maundo")]
        private void CommandUndo(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer) || !VerifyHasPermission(player))
                return;

            if (!_undoManager.TryUndo(basePlayer))
            {
                ReplyToPlayer(player, LangEntry.UndoNotFound);
                _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
            }
        }

        [Command("masetid")]
        private void CommandSetId(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer) || !VerifyHasPermission(player))
                return;

            if (args.Length < 1 || !ComputerStation.IsValidIdentifier(args[0]))
            {
                ReplyToPlayer(player, LangEntry.CCTVSetIdSyntax, cmd);
                return;
            }

            if (!VerifyLookingAtAdapter(player, out CCTVController controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            controller.EntityData.CCTV ??= new CCTVInfo();

            var hadIdentifier = !string.IsNullOrEmpty(controller.EntityData.CCTV.RCIdentifier);

            controller.EntityData.CCTV.RCIdentifier = args[0];
            _profileStore.Save(controller.Profile);
            controller.StartUpdateRoutine();

            ReplyToPlayer(player, LangEntry.CCTVSetIdSuccess, args[0], controller.Adapters.Count, controller.Profile.Name);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer, immediate: hadIdentifier);
        }

        [Command("masetdir")]
        private void CommandSetDirection(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyLookingAtAdapter(player, out CCTVEntityAdapter adapter, out CCTVController controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            var cctv = adapter.Entity as CCTV_RC;

            var direction = Vector3Ex.Direction(basePlayer.eyes.position, cctv.transform.position);
            direction = cctv.transform.InverseTransformDirection(direction);
            var lookAngles = BaseMountable.ConvertVector(Quaternion.LookRotation(direction).eulerAngles);

            controller.EntityData.CCTV ??= new CCTVInfo();
            controller.EntityData.CCTV.Pitch = lookAngles.x;
            controller.EntityData.CCTV.Yaw = lookAngles.y;
            _profileStore.Save(controller.Profile);
            controller.StartUpdateRoutine();

            ReplyToPlayer(player, LangEntry.CCTVSetDirectionSuccess, controller.Adapters.Count, controller.Profile.Name);

            _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
        }

        [Command("maskull")]
        private void CommandSkull(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyLookingAtAdapter(player, out EntityAdapter adapter, out EntityController controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            var skullTrophy = adapter.Entity as SkullTrophyGlobal;
            if (skullTrophy == null)
            {
                ReplyToPlayer(player, LangEntry.ErrorNoSuitableAddonFound);
                return;
            }

            if (args.Length == 0)
            {
                ReplyToPlayer(player, LangEntry.SkullNameSyntax, cmd);
                return;
            }

            var skullName = args[0];
            var updatedSkullName = controller.EntityData.SkullName != skullName;

            controller.EntityData.SkullName = skullName;
            _profileStore.Save(controller.Profile);
            controller.StartUpdateRoutine();

            ReplyToPlayer(player, LangEntry.SkullNameSetSuccess, skullName, controller.Adapters.Count, controller.Profile.Name);

            _adapterDisplayManager.ShowAllRepeatedly(basePlayer, immediate: !updatedSkullName);
        }

        [Command("matrophy")]
        private void CommandTrophy(IPlayer player, string cmd, string[] args)
        {
            T GetSubEntity<T>(Item item) where T : BaseEntity
            {
                var entityId = item.instanceData?.subEntity ?? new NetworkableId(0);
                if (entityId.Value == 0)
                    return null;

                return BaseNetworkable.serverEntities.Find(entityId) as T;
            }

            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyLookingAtAdapter(player, out EntityAdapter adapter, out EntityController controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            var huntingTrophy = adapter.Entity as HuntingTrophy;
            if (huntingTrophy == null)
            {
                ReplyToPlayer(player, LangEntry.ErrorNoSuitableAddonFound);
                return;
            }

            var heldItem = basePlayer.GetActiveItem();
            var headEntity = heldItem != null ? GetSubEntity<HeadEntity>(heldItem) : null;
            if (headEntity == null)
            {
                ReplyToPlayer(player, LangEntry.SetHeadNoHeadItem);
                return;
            }

            if (!huntingTrophy.CanSubmitHead(headEntity))
            {
                ReplyToPlayer(player, LangEntry.SetHeadMismatch);
                return;
            }

            controller.EntityData.HeadData = HeadData.FromHeadEntity(headEntity);
            _profileStore.Save(controller.Profile);
            controller.StartUpdateRoutine();

            ReplyToPlayer(player, LangEntry.SetHeadSuccess, controller.Adapters.Count, controller.Profile.Name);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
        }

        [Command("maskin")]
        private void CommandSkin(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyLookingAtAdapter(player, out EntityAdapter adapter, out EntityController controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            if (args.Length == 0)
            {
                ReplyToPlayer(player, LangEntry.SkinGet, adapter.Entity.skinID, cmd);
                return;
            }

            if (!ulong.TryParse(args[0], out var skinId))
            {
                ReplyToPlayer(player, LangEntry.SkinSetSyntax, cmd);
                return;
            }

            if (IsRedirectSkin(skinId, out var alternativeShortName))
            {
                ReplyToPlayer(player, LangEntry.SkinErrorRedirect, skinId, alternativeShortName);
                return;
            }

            var updatedExistingSkin = (controller.EntityData.Skin == 0) != (skinId == 0);

            controller.EntityData.Skin = skinId;
            _profileStore.Save(controller.Profile);
            controller.StartUpdateRoutine();

            ReplyToPlayer(player, LangEntry.SkinSetSuccess, skinId, controller.Adapters.Count, controller.Profile.Name);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer, immediate: !updatedExistingSkin);
        }

        [Command("maflag")]
        private void CommandFlag(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out _)
                || !VerifyHasPermission(player)
                || !VerifyLookingAtAdapter(player, out EntityAdapter adapter, out EntityController controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            if (args.Length == 0)
            {
                var notAplicableMessage = GetMessage(player.Id, LangEntry.NotApplicable);
                var currentFlags = adapter.Entity.flags == 0 ? notAplicableMessage : adapter.Entity.flags.ToString();
                var enabledFlags = adapter.EntityData.EnabledFlags == 0 ? notAplicableMessage : adapter.EntityData.EnabledFlags.ToString();
                var disabledFlags = adapter.EntityData.DisabledFlags == 0 ? notAplicableMessage : adapter.EntityData.DisabledFlags.ToString();

                ReplyToPlayer(player, LangEntry.FlagsGet, currentFlags, enabledFlags, disabledFlags);
                return;
            }

            if (!Enum.TryParse<BaseEntity.Flags>(args[0], ignoreCase: true, result: out var flag))
            {
                ReplyToPlayer(player, LangEntry.FlagsSetSyntax, cmd);
                return;
            }

            var hasFlag = adapter.Entity.HasFlag(flag) ? true : adapter.EntityData.HasFlag(flag);
            hasFlag = hasFlag switch
            {
                true => false,
                false => null,
                null => true
            };

            adapter.EntityData.SetFlag(flag, hasFlag);
            _profileStore.Save(controller.Profile);
            controller.StartUpdateRoutine();

            ReplyToPlayer(player, hasFlag switch
            {
                true => LangEntry.FlagsEnableSuccess,
                false => LangEntry.FlagsDisableSuccess,
                null => LangEntry.FlagsUnsetSuccess
            }, flag);
        }

        private void AddProfileDescription(StringBuilder sb, IPlayer player, ProfileController profileController)
        {
            foreach (var summaryEntry in GetProfileSummary(player, profileController.Profile))
            {
                sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileDescribeItem, summaryEntry.AddonType, summaryEntry.AddonName, summaryEntry.Count, summaryEntry.MonumentName));
            }
        }

        [Command("macardlevel")]
        private void CommandLevel(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyLookingAtAdapter(player, out EntityAdapter adapter, out EntityController controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            var cardReader = adapter.Entity as CardReader;
            if ((object)cardReader == null)
            {
                ReplyToPlayer(player, LangEntry.ErrorNoSuitableAddonFound);
                return;
            }

            if (args.Length < 1 || !int.TryParse(args[0], result: out var accessLevel) || accessLevel < 1 || accessLevel > 3)
            {
                ReplyToPlayer(player, LangEntry.CardReaderSetLevelSyntax, cmd);
                return;
            }

            if (cardReader.accessLevel != accessLevel)
            {
                adapter.EntityData.CardReaderLevel = (ushort)accessLevel;
                _profileStore.Save(controller.Profile);
                controller.StartUpdateRoutine();
            }

            ReplyToPlayer(player, LangEntry.CardReaderSetLevelSuccess, adapter.EntityData.CardReaderLevel);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
        }

        [Command("mapuzzle")]
        private void CommandPuzzle(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer) || !VerifyHasPermission(player))
                return;

            if (args.Length == 0)
            {
                SubCommandPuzzleHelp(player, cmd);
                return;
            }

            switch (args[0].ToLower())
            {
                case "reset":
                {
                    if (!VerifyLookingAtAdapter(player, out EntityAdapter adapter, out EntityController _, LangEntry.ErrorNoSuitableAddonFound))
                        return;

                    var ioEntity = adapter.Entity as IOEntity;
                    var puzzleReset = ioEntity != null ? FindConnectedPuzzleReset(ioEntity) : null;
                    if (puzzleReset == null)
                    {
                        ReplyToPlayer(player, LangEntry.PuzzleNotConnected, _uniqueNameRegistry.GetUniqueShortName(adapter.Entity.PrefabName));
                        return;
                    }

                    puzzleReset.DoReset();
                    puzzleReset.ResetTimer();
                    ReplyToPlayer(player, LangEntry.PuzzleResetSuccess);
                    break;
                }

                case "add":
                case "remove":
                {
                    var isAdd = args[0] == "add";
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, isAdd ? LangEntry.PuzzleAddSpawnGroupSyntax : LangEntry.PuzzleRemoveSpawnGroupSyntax, cmd);
                        return;
                    }

                    if (!VerifyLookingAtAdapter(player, out EntityAdapter adapter, out EntityController controller, LangEntry.ErrorNoSuitableAddonFound)
                        || !VerifyEntityComponent(player, adapter.Entity, out PuzzleReset puzzleReset, LangEntry.PuzzleNotPresent))
                        return;

                    if (!VerifySpawnGroupFound(player, args[1], adapter.Monument, out var spawnGroupController))
                        return;

                    var spawnGroupData = spawnGroupController.SpawnGroupData;
                    var spawnGroupId = spawnGroupData.Id;
                    var puzzleData = controller.EntityData.EnsurePuzzleData(puzzleReset);

                    if (!isAdd)
                    {
                        puzzleData.RemoveSpawnGroupId(spawnGroupId);
                    }
                    else if (!puzzleData.HasSpawnGroupId(spawnGroupId))
                    {
                        puzzleData.AddSpawnGroupId(spawnGroupId);
                    }

                    controller.StartUpdateRoutine();
                    _profileStore.Save(controller.Profile);

                    ReplyToPlayer(player, isAdd ? LangEntry.PuzzleAddSpawnGroupSuccess : LangEntry.PuzzleRemoveSpawnGroupSuccess, spawnGroupData.Name);

                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer, immediate: false);
                    break;
                }

                case "set":
                {
                    if (args.Length < 3)
                    {
                        _sb.Clear();
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.ErrorSetSyntaxGeneric, cmd));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleSetHelpMaxPlayersBlockReset));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleSetHelpPlayerDetectionRadius));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleSetHelpSecondsBetweenResets));
                        player.Reply(_sb.ToString());
                        return;
                    }

                    if (!VerifyValidEnumValue(player, args[1], out PuzzleOption puzzleOption))
                        return;

                    if (!VerifyLookingAtAdapter(player, out EntityAdapter adapter, out EntityController controller, LangEntry.ErrorNoSuitableAddonFound)
                        || !VerifyEntityComponent(player, adapter.Entity, out PuzzleReset puzzleReset, LangEntry.PuzzleNotPresent))
                        return;

                    var puzzleData = controller.EntityData.EnsurePuzzleData(puzzleReset);

                    object setValue = args[2];
                    var showImmediate = true;

                    switch (puzzleOption)
                    {
                        case PuzzleOption.PlayersBlockReset:
                        {
                            if (!VerifyValidBool(player, args[2], out var playerBlockReset, LangEntry.ErrorSetSyntax.Bind(cmd, PuzzleOption.PlayersBlockReset)))
                                return;

                            puzzleData.PlayersBlockReset = playerBlockReset;
                            setValue = playerBlockReset;
                            showImmediate = false;
                            break;
                        }

                        case PuzzleOption.PlayerDetectionRadius:
                        {
                            if (!VerifyValidFloat(player, args[2], out var playerDetectionRadius, LangEntry.ErrorSetSyntax.Bind(cmd, PuzzleOption.PlayerDetectionRadius)))
                                return;

                            puzzleData.PlayersBlockReset = true;
                            puzzleData.PlayerDetectionRadius = playerDetectionRadius;
                            setValue = playerDetectionRadius;
                            break;
                        }

                        case PuzzleOption.SecondsBetweenResets:
                        {
                            if (!VerifyValidFloat(player, args[2], out var secondsBetweenResets, LangEntry.ErrorSetSyntax.Bind(cmd, PuzzleOption.SecondsBetweenResets)))
                                return;

                            puzzleData.SecondsBetweenResets = secondsBetweenResets;
                            setValue = secondsBetweenResets;
                            break;
                        }
                    }

                    controller.StartUpdateRoutine();
                    _profileStore.Save(controller.Profile);

                    ReplyToPlayer(player, LangEntry.PuzzleSetSuccess, puzzleOption, setValue);

                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer, immediate: showImmediate);
                    break;
                }

                default:
                {
                    SubCommandPuzzleHelp(player, cmd);
                    break;
                }
            }
        }

        private void SubCommandPuzzleHelp(IPlayer player, string cmd)
        {
            _sb.Clear();
            _sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleHelpHeader));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleHelpReset, cmd));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleHelpSet, cmd));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleHelpAdd, cmd));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleHelpRemove, cmd));
            player.Reply(_sb.ToString());
        }

        [Command("maprofile")]
        private void CommandProfile(IPlayer player, string cmd, string[] args)
        {
            if (!_serverInitialized
                || !VerifyHasPermission(player)
                || !VerifyMonumentFinderLoaded(player))
                return;

            if (args.Length == 0)
            {
                SubCommandProfileHelp(player);
                return;
            }

            var basePlayer = player.Object as BasePlayer;

            switch (args[0].ToLower())
            {
                case "list":
                {
                    var profileList = ProfileInfo.GetList(_data, _profileManager);
                    if (profileList.Count == 0)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileListEmpty);
                        return;
                    }

                    var playerProfileName = player.IsServer ? null : _data.GetSelectedProfileName(player.Id);

                    profileList = profileList
                        .OrderByDescending(profile => profile.Enabled && profile.Name == playerProfileName)
                        .ThenByDescending(profile => profile.Enabled)
                        .ThenBy(profile => profile.Name)
                        .ToList();

                    _sb.Clear();
                    _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileListHeader));
                    foreach (var profile in profileList)
                    {
                        var messageName = profile.Enabled && profile.Name == playerProfileName
                            ? LangEntry.ProfileListItemSelected
                            : profile.Enabled
                            ? LangEntry.ProfileListItemEnabled
                            : LangEntry.ProfileListItemDisabled;

                        _sb.AppendLine(GetMessage(player.Id, messageName, profile.Name, GetAuthorSuffix(player, profile.Profile?.Author)));
                    }
                    player.Reply(_sb.ToString());
                    break;
                }

                case "describe":
                {
                    if (!VerifyProfile(player, args, out var controller, LangEntry.ProfileDescribeSyntax))
                        return;

                    if (controller.Profile.IsEmpty())
                    {
                        ReplyToPlayer(player, LangEntry.ProfileEmpty, controller.Profile.Name);
                        return;
                    }

                    _sb.Clear();
                    _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileDescribeHeader, controller.Profile.Name));
                    AddProfileDescription(_sb, player, controller);

                    player.Reply(_sb.ToString());

                    if (!player.IsServer)
                    {
                        _adapterDisplayManager.SetPlayerProfile(basePlayer, controller);
                        _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    }

                    break;
                }

                case "sel":
                case "select":
                {
                    if (player.IsServer)
                        return;

                    ProfileController controller;

                    if (args.Length <= 1)
                    {
                        // Find the adapter where the player is aiming, if they did not specify a profile name.
                        controller = FindAdapter(basePlayer).Controller?.ProfileController;
                        if (controller == null)
                        {
                            ReplyToPlayer(player, LangEntry.ProfileSelectSyntax);
                            return;
                        }
                    }
                    else if (!VerifyProfile(player, args, out controller, LangEntry.ProfileSelectSyntax))
                        return;

                    var profile = controller.Profile;
                    var profileName = profile.Name;

                    _data.SetProfileSelected(player.Id, profileName);
                    var wasEnabled = controller.IsEnabled;
                    if (wasEnabled)
                    {
                        // Only save if the profile is not enabled, since enabling it will already save the main data file.
                        _data.Save();
                    }
                    else
                    {
                        if (!VerifyCanLoadProfile(player, profileName, out var newProfileData))
                            return;

                        controller.Enable(newProfileData);
                    }

                    ReplyToPlayer(player, wasEnabled ? LangEntry.ProfileSelectSuccess : LangEntry.ProfileSelectEnableSuccess, profileName);
                    _adapterDisplayManager.SetPlayerProfile(basePlayer, controller);
                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    break;
                }

                case "create":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileCreateSyntax);
                        return;
                    }

                    var newName = DynamicConfigFile.SanitizeName(args[1]);
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        ReplyToPlayer(player, LangEntry.ProfileCreateSyntax);
                        return;
                    }

                    if (!VerifyProfileNameAvailable(player, newName))
                        return;

                    var controller = _profileManager.CreateProfile(newName, basePlayer?.displayName);

                    if (!player.IsServer)
                    {
                        _data.SetProfileSelected(player.Id, newName);
                    }

                    _data.SetProfileEnabled(newName);

                    ReplyToPlayer(player, LangEntry.ProfileCreateSuccess, controller.Profile.Name);
                    _adapterDisplayManager.SetPlayerProfile(basePlayer, controller);
                    break;
                }

                case "rename":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileRenameSyntax);
                        return;
                    }

                    ProfileController controller;
                    if (args.Length == 2)
                    {
                        controller = player.IsServer ? null : _profileManager.GetPlayerProfileController(player.Id);
                        if (controller == null)
                        {
                            ReplyToPlayer(player, LangEntry.ProfileRenameSyntax);
                            return;
                        }
                    }
                    else if (!VerifyProfileExists(player, args[1], out controller))
                        return;

                    string newName = DynamicConfigFile.SanitizeName(args.Length == 2 ? args[1] : args[2]);
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        ReplyToPlayer(player, LangEntry.ProfileRenameSyntax);
                        return;
                    }

                    if (!VerifyProfileNameAvailable(player, newName))
                        return;

                    // Cache the actual old name in case it was case-insensitive matched.
                    var actualOldName = controller.Profile.Name;

                    controller.Rename(newName);
                    ReplyToPlayer(player, LangEntry.ProfileRenameSuccess, actualOldName, controller.Profile.Name);
                    if (!player.IsServer)
                    {
                        _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    }

                    break;
                }

                case "reload":
                {
                    if (!VerifyProfile(player, args, out var controller, LangEntry.ProfileReloadSyntax))
                        return;

                    if (!controller.IsEnabled)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileNotEnabled, controller.Profile.Name);
                        return;
                    }

                    if (!VerifyCanLoadProfile(player, controller.Profile.Name, out var newProfileData))
                        return;

                    controller.Reload(newProfileData);
                    ReplyToPlayer(player, LangEntry.ProfileReloadSuccess, controller.Profile.Name);
                    if (!player.IsServer)
                    {
                        _adapterDisplayManager.SetPlayerProfile(basePlayer, controller);
                        _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    }

                    break;
                }

                case "enable":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileEnableSyntax);
                        return;
                    }

                    if (!VerifyProfileExists(player, args[1], out var controller))
                        return;

                    var profileName = controller.Profile.Name;
                    if (controller.IsEnabled)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileAlreadyEnabled, profileName);
                        return;
                    }

                    if (!VerifyCanLoadProfile(player, controller.Profile.Name, out var newProfileData))
                        return;

                    controller.Enable(newProfileData);
                    ReplyToPlayer(player, LangEntry.ProfileEnableSuccess, profileName);
                    if (!player.IsServer)
                    {
                        _adapterDisplayManager.SetPlayerProfile(basePlayer, controller);
                        _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    }

                    break;
                }

                case "disable":
                {
                    if (!VerifyProfile(player, args, out var controller, LangEntry.ProfileDisableSyntax))
                        return;

                    var profileName = controller.Profile.Name;
                    if (!controller.IsEnabled)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileAlreadyDisabled, profileName);
                        return;
                    }

                    _profileManager.DisableProfile(controller);
                    ReplyToPlayer(player, LangEntry.ProfileDisableSuccess, profileName);
                    break;
                }

                case "clear":
                {
                    if (args.Length <= 1)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileClearSyntax);
                        return;
                    }

                    if (!VerifyProfile(player, args, out var controller, LangEntry.ProfileClearSyntax))
                        return;

                    if (!controller.Profile.IsEmpty())
                    {
                        controller.Clear();
                    }

                    ReplyToPlayer(player, LangEntry.ProfileClearSuccess, controller.Profile.Name);
                    break;
                }

                case "delete":
                {
                    if (args.Length <= 1)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileDeleteSyntax);
                        return;
                    }

                    if (!VerifyProfile(player, args, out var controller, LangEntry.ProfileDeleteSyntax))
                        return;

                    var profileName = controller.Profile.Name;
                    if (controller.IsEnabled && !controller.Profile.IsEmpty())
                    {
                        ReplyToPlayer(player, LangEntry.ProfileDeleteBlocked, profileName);
                        return;
                    }

                    _profileManager.DeleteProfile(controller);
                    ReplyToPlayer(player, LangEntry.ProfileDeleteSuccess, profileName);
                    break;
                }

                case "moveto":
                {
                    if (!VerifyLookingAtAdapter(player, out BaseController addonController, LangEntry.ErrorNoSuitableAddonFound))
                        return;

                    if (!VerifyProfile(player, args, out var newProfileController, LangEntry.ProfileMoveToSyntax))
                        return;

                    var oldProfileController = addonController.ProfileController;
                    var newProfile = newProfileController.Profile;
                    var oldProfile = addonController.Profile;

                    var data = addonController.Data;
                    var addonName = GetAddonName(player, data);

                    if (newProfileController == oldProfileController)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileMoveToAlreadyPresent, addonName, oldProfile.Name);
                        return;
                    }

                    if (!oldProfile.RemoveData(data, out var monumentIdentifier))
                    {
                        LogError($"Unexpected error: {data.GetType()} {data.Id} was not found in profile {oldProfile.Name}");
                        return;
                    }

                    _profileStore.Save(oldProfile);
                    var killRoutine = addonController.Kill();
                    if (killRoutine != null)
                    {
                        oldProfileController.StartCallbackRoutine(killRoutine, oldProfileController.SetupIO);
                    }

                    newProfile.AddData(monumentIdentifier, data);
                    _profileStore.Save(newProfile);
                    newProfileController.SpawnNewData(data, GetMonumentsByIdentifier(monumentIdentifier));

                    ReplyToPlayer(player, LangEntry.ProfileMoveToSuccess, addonName, oldProfile.Name, newProfile.Name);
                    if (!player.IsServer)
                    {
                        _adapterDisplayManager.SetPlayerProfile(basePlayer, newProfileController);
                        _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    }

                    break;
                }

                case "install":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileInstallSyntax);
                        return;
                    }

                    SharedCommandInstallProfile(player, args.Skip(1).ToArray());
                    break;
                }

                default:
                {
                    SubCommandProfileHelp(player);
                    break;
                }
            }
        }

        private void SubCommandProfileHelp(IPlayer player)
        {
            _sb.Clear();
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpHeader));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpList));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpDescribe));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpEnable));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpDisable));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpReload));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpSelect));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpCreate));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpRename));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpClear));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpDelete));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpMoveTo));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpInstall));
            player.Reply(_sb.ToString());
        }

        [Command("mainstall")]
        private void CommandInstallProfile(IPlayer player, string cmd, string[] args)
        {
            if (!_serverInitialized
                || !VerifyHasPermission(player)
                || !VerifyMonumentFinderLoaded(player))
                return;

            if (args.Length < 1)
            {
                ReplyToPlayer(player, LangEntry.ProfileInstallShorthandSyntax);
                return;
            }

            SharedCommandInstallProfile(player, args);
        }

        private void SharedCommandInstallProfile(IPlayer player, string[] args)
        {
            var url = args[0];

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
            {
                var fallbackUrl = string.Format(DefaultUrlPattern, url);
                if (Uri.TryCreate(fallbackUrl, UriKind.Absolute, out parsedUri))
                {
                    url = fallbackUrl;
                }
                else
                {
                    ReplyToPlayer(player, LangEntry.ProfileUrlInvalid, url);
                    return;
                }
            }

            DownloadProfile(
                player,
                url,
                successCallback: profile =>
                {
                    profile.Name = DynamicConfigFile.SanitizeName(profile.Name);

                    if (string.IsNullOrWhiteSpace(profile.Name))
                    {
                        var urlDerivedProfileName = DynamicConfigFile.SanitizeName(parsedUri.Segments.LastOrDefault().Replace(".json", ""));

                        if (string.IsNullOrEmpty(urlDerivedProfileName))
                        {
                            LogError($"Unable to determine profile name from url: \"{url}\". Please ask the URL owner to supply a \"Name\" in the file.");
                            ReplyToPlayer(player, LangEntry.ProfileInstallError, url);
                            return;
                        }

                        profile.Name = urlDerivedProfileName;
                    }

                    if (OriginalProfileStore.IsOriginalProfile(profile.Name))
                    {
                        LogError($"Profile \"{profile.Name}\" should not end with \"{OriginalProfileStore.OriginalSuffix}\".");
                        ReplyToPlayer(player, LangEntry.ProfileInstallError, url);
                        return;
                    }

                    var profileController = _profileManager.GetProfileController(profile.Name);
                    if (profileController != null && !profileController.Profile.IsEmpty())
                    {
                        ReplyToPlayer(player, LangEntry.ProfileAlreadyExistsNotEmpty, profile.Name);
                        return;
                    }

                    _profileStore.Save(profile);
                    _originalProfileStore.Save(profile);

                    profileController ??= _profileManager.GetProfileController(profile.Name);

                    if (profileController == null)
                    {
                        LogError($"Profile \"{profile.Name}\" could not be found on disk after download from url: \"{url}\"");
                        ReplyToPlayer(player, LangEntry.ProfileInstallError, url);
                        return;
                    }

                    if (profileController.IsEnabled)
                    {
                        profileController.Reload(profile);
                    }
                    else
                    {
                        profileController.Enable(profile);
                    }

                    _sb.Clear();
                    _sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileInstallSuccess, profile.Name, GetAuthorSuffix(player, profile.Author)));
                    AddProfileDescription(_sb, player, profileController);
                    player.Reply(_sb.ToString());

                    if (!player.IsServer)
                    {
                        var basePlayer = player.Object as BasePlayer;
                        _adapterDisplayManager.SetPlayerProfile(basePlayer, profileController);
                        _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    }
                },
                errorCallback: player.Reply
            );
        }

        [Command("mashow")]
        private void CommandShow(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer) || !VerifyHasPermission(player))
                return;

            int duration = AdapterDisplayManager.DefaultDisplayDuration;
            string profileName = null;

            foreach (var arg in args)
            {
                if (int.TryParse(arg, out var argIntValue))
                {
                    duration = argIntValue;
                    continue;
                }

                profileName ??= arg;
            }

            ProfileController profileController = null;
            if (profileName != null)
            {
                profileController = _profileManager.GetProfileController(profileName);
                if (profileController == null)
                {
                    ReplyToPlayer(player, LangEntry.ProfileNotFound, profileName);
                    return;
                }
            }

            _adapterDisplayManager.SetPlayerProfile(basePlayer, profileController);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer, duration);

            ReplyToPlayer(player, LangEntry.ShowSuccess, FormatTime(duration));
        }

        [Command("maspawngroup", "masg")]
        private void CommandSpawnGroup(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer) || !VerifyHasPermission(player))
                return;

            if (args.Length < 1)
            {
                SubCommandSpawnGroupHelp(player, cmd);
                return;
            }

            switch (args[0].ToLower())
            {
                case "create":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, LangEntry.SpawnGroupCreateSyntax, cmd);
                        return;
                    }

                    var spawnGroupName = args[1];

                    if (!VerifyMonumentFinderLoaded(player)
                        || !VerifyProfileSelected(player, out var profileController)
                        || !VerifyLookingAtMonumentPosition(player, out var position, out var monument)
                        || !VerifySpawnGroupNameAvailable(player, profileController.Profile, monument, spawnGroupName))
                        return;

                    DetermineLocalTransformData(position, basePlayer, monument, out var localPosition, out var localRotationAngles, out var isOnTerrain);

                    var spawnGroupData = _config.AddonDefaults.SpawnGroups.ApplyTo(new SpawnGroupData
                    {
                        Id = Guid.NewGuid(),
                        Name = spawnGroupName,
                        SpawnPoints = new List<SpawnPointData>
                        {
                            _config.AddonDefaults.SpawnPoints.ApplyTo(new SpawnPointData
                            {
                                Id = Guid.NewGuid(),
                                Position = localPosition,
                                RotationAngles = localRotationAngles,
                                SnapToTerrain = isOnTerrain,
                            }),
                        },
                    });

                    var matchingMonuments = GetMonumentsByIdentifier(monument.UniqueName);

                    profileController.Profile.AddData(monument.UniqueName, spawnGroupData);
                    _profileStore.Save(profileController.Profile);
                    profileController.SpawnNewData(spawnGroupData, matchingMonuments);

                    ReplyToPlayer(player, LangEntry.SpawnGroupCreateSucces, spawnGroupName);

                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    break;
                }

                case "set":
                {
                    if (args.Length < 3)
                    {
                        _sb.Clear();
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.ErrorSetSyntaxGeneric, cmd));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpName));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpMaxPopulation));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpRespawnDelayMin));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpRespawnDelayMax));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpSpawnPerTickMin));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpSpawnPerTickMax));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpInitialSpawn));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpPreventDuplicates));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpPauseScheduleWhileFull));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpRespawnWhenNearestPuzzleResets));
                        player.Reply(_sb.ToString());
                        return;
                    }

                    if (!VerifyValidEnumValue(player, args[1], out SpawnGroupOption spawnGroupOption))
                        return;

                    if (!VerifyLookingAtAdapter(player, out SpawnPointAdapter spawnPointAdapter, out SpawnGroupController spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
                        return;

                    var spawnGroupData = spawnGroupController.SpawnGroupData;
                    object setValue = args[2];

                    var showImmediate = true;

                    switch (spawnGroupOption)
                    {
                        case SpawnGroupOption.Name:
                        {
                            if (!VerifySpawnGroupNameAvailable(player, spawnGroupController.Profile, spawnPointAdapter.Monument, args[2], spawnGroupController))
                                return;

                            spawnGroupData.Name = args[2];
                            break;
                        }

                        case SpawnGroupOption.MaxPopulation:
                        {
                            if (!VerifyValidInt(player, args[2], out var maxPopulation, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnGroupOption.MaxPopulation)))
                                return;

                            spawnGroupData.MaxPopulation = maxPopulation;
                            break;
                        }

                        case SpawnGroupOption.RespawnDelayMin:
                        {
                            if (!VerifyValidFloat(player, args[2], out var respawnDelayMin, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnGroupOption.RespawnDelayMin)))
                                return;

                            showImmediate = respawnDelayMin == 0 || spawnGroupData.RespawnDelayMax != 0;
                            spawnGroupData.RespawnDelayMin = respawnDelayMin;
                            spawnGroupData.RespawnDelayMax = Math.Max(respawnDelayMin, spawnGroupData.RespawnDelayMax);
                            setValue = respawnDelayMin;
                            break;
                        }

                        case SpawnGroupOption.RespawnDelayMax:
                        {
                            if (!VerifyValidFloat(player, args[2], out var respawnDelayMax, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnGroupOption.RespawnDelayMax)))
                                return;

                            showImmediate = (respawnDelayMax == 0) == (spawnGroupData.RespawnDelayMax == 0);
                            spawnGroupData.RespawnDelayMax = respawnDelayMax;
                            spawnGroupData.RespawnDelayMin = Math.Min(spawnGroupData.RespawnDelayMin, respawnDelayMax);
                            setValue = respawnDelayMax;
                            break;
                        }

                        case SpawnGroupOption.SpawnPerTickMin:
                        {
                            if (!VerifyValidInt(player, args[2], out var spawnPerTickMin, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnGroupOption.SpawnPerTickMin)))
                                return;

                            spawnGroupData.SpawnPerTickMin = spawnPerTickMin;
                            spawnGroupData.SpawnPerTickMax = Math.Max(spawnPerTickMin, spawnGroupData.SpawnPerTickMax);
                            setValue = spawnPerTickMin;
                            break;
                        }

                        case SpawnGroupOption.SpawnPerTickMax:
                        {
                            if (!VerifyValidInt(player, args[2], out var spawnPerTickMax, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnGroupOption.SpawnPerTickMax)))
                                return;

                            spawnGroupData.SpawnPerTickMax = spawnPerTickMax;
                            spawnGroupData.SpawnPerTickMin = Math.Min(spawnGroupData.SpawnPerTickMin, spawnPerTickMax);
                            setValue = spawnPerTickMax;
                            break;
                        }

                        case SpawnGroupOption.InitialSpawn:
                        {
                            if (!VerifyValidBool(player, args[2], out var initialSpawn, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnGroupOption.PreventDuplicates)))
                                return;

                            spawnGroupData.InitialSpawn = initialSpawn;
                            setValue = initialSpawn;
                            showImmediate = false;
                            break;
                        }

                        case SpawnGroupOption.PreventDuplicates:
                        {
                            if (!VerifyValidBool(player, args[2], out var preventDuplicates, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnGroupOption.PreventDuplicates)))
                                return;

                            spawnGroupData.PreventDuplicates = preventDuplicates;
                            setValue = preventDuplicates;
                            showImmediate = false;
                            break;
                        }

                        case SpawnGroupOption.PauseScheduleWhileFull:
                        {
                            if (!VerifyValidBool(player, args[2], out var pauseScheduleWhileFull, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnGroupOption.PauseScheduleWhileFull)))
                                return;

                            spawnGroupData.PauseScheduleWhileFull = pauseScheduleWhileFull;
                            setValue = pauseScheduleWhileFull;
                            showImmediate = false;
                            break;
                        }

                        case SpawnGroupOption.RespawnWhenNearestPuzzleResets:
                        {
                            if (!VerifyValidBool(player, args[2], out var respawnWhenNearestPuzzleResets, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnGroupOption.RespawnWhenNearestPuzzleResets)))
                                return;

                            spawnGroupData.RespawnWhenNearestPuzzleResets = respawnWhenNearestPuzzleResets;
                            setValue = respawnWhenNearestPuzzleResets;
                            showImmediate = false;
                            break;
                        }
                    }

                    spawnGroupController.UpdateSpawnGroups();
                    _profileStore.Save(spawnGroupController.Profile);

                    ReplyToPlayer(player, LangEntry.SpawnGroupSetSuccess, spawnGroupData.Name, spawnGroupOption, setValue);

                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer, immediate: showImmediate);
                    break;
                }

                case "add":
                {
                    var weight = 100;
                    if (args.Length < 2 || args.Length >= 3 && !int.TryParse(args[2], out weight))
                    {
                        ReplyToPlayer(player, LangEntry.SpawnGroupAddSyntax, cmd);
                        return;
                    }

                    if (!VerifyValidEntityPrefabOrCustomAddon(player, args[1], out var prefabPath, out var customAddonDefinition))
                        return;

                    if (!VerifyLookingAtAdapter(player, out SpawnGroupController spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
                        return;

                    var updatedExistingEntry = false;

                    var spawnGroupData = spawnGroupController.SpawnGroupData;
                    var prefabData = spawnGroupData.Prefabs.FirstOrDefault(entry => entry.PrefabName == prefabPath
                        || entry.CustomAddonName == customAddonDefinition?.AddonName);

                    if (prefabData != null)
                    {
                        prefabData.Weight = weight;
                        updatedExistingEntry = true;
                    }
                    else
                    {
                        prefabData = new WeightedPrefabData
                        {
                            PrefabName = prefabPath,
                            CustomAddonName = customAddonDefinition?.AddonName,
                            Weight = weight,
                        };
                        spawnGroupData.Prefabs.Add(prefabData);
                    }

                    spawnGroupController.UpdateSpawnGroups();
                    _profileStore.Save(spawnGroupController.Profile);

                    var displayName = prefabData.CustomAddonName ?? _uniqueNameRegistry.GetUniqueShortName(prefabData.PrefabName);
                    ReplyToPlayer(player, LangEntry.SpawnGroupAddSuccess, displayName, weight, spawnGroupData.Name);

                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer, immediate: updatedExistingEntry);
                    break;
                }

                case "remove":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, LangEntry.SpawnGroupRemoveSyntax, cmd);
                        return;
                    }

                    if (!VerifyLookingAtAdapter(player, out SpawnGroupController spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
                        return;

                    string desiredPrefab = args[1];

                    var spawnGroupData = spawnGroupController.SpawnGroupData;
                    if (!VerifySpawnGroupPrefabOrCustomAddon(player, spawnGroupData, desiredPrefab, out var prefabData))
                    {
                        _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                        return;
                    }

                    spawnGroupData.Prefabs.Remove(prefabData);
                    spawnGroupController.StartKillSpawnedInstancesRoutine(prefabData);
                    spawnGroupController.UpdateSpawnGroups();
                    _profileStore.Save(spawnGroupController.Profile);

                    var displayName = prefabData.CustomAddonName ?? _uniqueNameRegistry.GetUniqueShortName(prefabData.PrefabName);
                    ReplyToPlayer(player, LangEntry.SpawnGroupRemoveSuccess, displayName, spawnGroupData.Name);

                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer, immediate: false);
                    break;
                }

                case "spawn":
                case "tick":
                {
                    if (!VerifyLookingAtAdapter(player, out SpawnGroupController spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
                        return;

                    spawnGroupController.StartSpawnRoutine();
                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    break;
                }

                case "respawn":
                {
                    if (!VerifyLookingAtAdapter(player, out SpawnGroupController spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
                        return;

                    spawnGroupController.StartRespawnRoutine();
                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    break;
                }

                default:
                {
                    SubCommandSpawnGroupHelp(player, cmd);
                    break;
                }
            }
        }

        private void SubCommandSpawnGroupHelp(IPlayer player, string cmd)
        {
            _sb.Clear();
            _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpHeader));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpCreate, cmd));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpSet, cmd));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpAdd, cmd));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpRemove, cmd));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpSpawn, cmd));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpRespawn, cmd));
            player.Reply(_sb.ToString());
        }

        [Command("maspawnpoint", "masp")]
        private void CommandSpawnPoint(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer) || !VerifyHasPermission(player))
                return;

            if (args.Length < 1)
            {
                SubCommandSpawnPointHelp(player, cmd);
                return;
            }

            switch (args[0].ToLower())
            {
                case "create":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, LangEntry.SpawnPointCreateSyntax, cmd);
                        return;
                    }

                    if (!VerifyMonumentFinderLoaded(player)
                        || !VerifyLookingAtMonumentPosition(player, out var position, out var monument))
                        return;

                    if (!VerifySpawnGroupFound(player, args[1], monument, out var spawnGroupController))
                        return;

                    DetermineLocalTransformData(position, basePlayer, monument, out var localPosition, out var localRotationAngles, out var isOnTerrain);

                    var spawnPointData = _config.AddonDefaults.SpawnPoints.ApplyTo(new SpawnPointData
                    {
                        Id = Guid.NewGuid(),
                        Position = localPosition,
                        RotationAngles = localRotationAngles,
                        SnapToTerrain = isOnTerrain,
                    });

                    spawnGroupController.SpawnGroupData.SpawnPoints.Add(spawnPointData);
                    _profileStore.Save(spawnGroupController.Profile);
                    spawnGroupController.CreateSpawnPoint(spawnPointData);

                    ReplyToPlayer(player, LangEntry.SpawnPointCreateSuccess, spawnGroupController.SpawnGroupData.Name);

                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    break;
                }

                case "set":
                {
                    if (args.Length < 3)
                    {
                        _sb.Clear();
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.ErrorSetSyntaxGeneric, cmd));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpExclusive));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpSnapToGround));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpCheckSpace));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpRandomRotation));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpRandomRadius));
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpPlayerDetectionRadius));
                        player.Reply(_sb.ToString());
                        return;
                    }

                    if (!VerifyValidEnumValue(player, args[1], out SpawnPointOption spawnPointOption))
                        return;

                    if (!VerifyLookingAtAdapter(player, out SpawnPointAdapter spawnPointAdapter, out SpawnGroupController spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
                        return;

                    var spawnPointData = spawnPointAdapter.SpawnPointData;
                    object setValue = args[2];

                    switch (spawnPointOption)
                    {
                        case SpawnPointOption.Exclusive:
                        {
                            if (!VerifyValidBool(player, args[2], out var exclusive, LangEntry.SpawnGroupSetSuccess.Bind(LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.Exclusive)))
                                return;

                            spawnPointData.Exclusive = exclusive;
                            setValue = exclusive;
                            break;
                        }

                        case SpawnPointOption.SnapToGround:
                        {
                            if (!VerifyValidBool(player, args[2], out var snapToGround, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnPointOption.SnapToGround)))
                                return;

                            spawnPointData.SnapToGround = snapToGround;
                            setValue = snapToGround;
                            break;
                        }

                        case SpawnPointOption.CheckSpace:
                        {
                            if (!VerifyValidBool(player, args[2], out var checkSpace, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnPointOption.CheckSpace)))
                                return;

                            spawnPointData.CheckSpace = checkSpace;
                            setValue = checkSpace;
                            break;
                        }

                        case SpawnPointOption.RandomRotation:
                        {
                            if (!VerifyValidBool(player, args[2], out var randomRotation, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnPointOption.RandomRotation)))
                                return;

                            spawnPointData.RandomRotation = randomRotation;
                            setValue = randomRotation;
                            break;
                        }

                        case SpawnPointOption.RandomRadius:
                        {
                            if (!VerifyValidFloat(player, args[2], out var radius, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnPointOption.RandomRadius)))
                                return;

                            spawnPointData.RandomRadius = radius;
                            setValue = radius;
                            break;
                        }

                        case SpawnPointOption.PlayerDetectionRadius:
                        {
                            if (!VerifyValidFloat(player, args[2], out var radius, LangEntry.ErrorSetSyntax.Bind(cmd, SpawnPointOption.PlayerDetectionRadius)))
                                return;

                            spawnPointData.PlayerDetectionRadius = radius;
                            setValue = radius;
                            break;
                        }
                    }

                    _profileStore.Save(spawnGroupController.Profile);

                    ReplyToPlayer(player, LangEntry.SpawnPointSetSuccess, spawnPointOption, setValue);

                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    break;
                }

                default:
                {
                    SubCommandSpawnPointHelp(player, cmd);
                    break;
                }
            }
        }

        private void SubCommandSpawnPointHelp(IPlayer player, string cmd)
        {
            _sb.Clear();
            _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointHelpHeader));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointHelpCreate, cmd));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointHelpSet, cmd));
            player.Reply(_sb.ToString());
        }

        [Command("mapaste")]
        private void CommandPaste(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer) || !VerifyHasPermission(player))
                return;

            if (args.Length < 1)
            {
                ReplyToPlayer(player, LangEntry.PasteSyntax);
                return;
            }

            if (!PasteUtils.IsCopyPasteCompatible(CopyPaste))
            {
                ReplyToPlayer(player, LangEntry.PasteNotCompatible);
                return;
            }

            if (!VerifyMonumentFinderLoaded(player)
                || !VerifyProfileSelected(player, out var profileController)
                || !VerifyLookingAtMonumentPosition(player, out var position, out var monument))
                return;

            var pasteName = args[0];

            if (!PasteUtils.DoesPasteExist(pasteName))
            {
                ReplyToPlayer(player, LangEntry.PasteNotFound, pasteName);
                return;
            }

            DetermineLocalTransformData(position, basePlayer, monument, out var localPosition, out var localRotationAngles, out var isOnTerrain, flipRotation: false);

            var pasteData = new PasteData
            {
                Id = Guid.NewGuid(),
                Position = localPosition,
                RotationAngles = localRotationAngles,
                SnapToTerrain = isOnTerrain,
                Filename = pasteName,
                Args = args.Skip(1).ToArray(),
            };

            var matchingMonuments = GetMonumentsByIdentifier(monument.UniqueName);

            profileController.Profile.AddData(monument.UniqueName, pasteData);
            _profileStore.Save(profileController.Profile);
            profileController.SpawnNewData(pasteData, matchingMonuments);

            ReplyToPlayer(player, LangEntry.PasteSuccess, pasteName, monument.UniqueDisplayName, matchingMonuments.Count, profileController.Profile.Name);

            _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
        }

        private void AddSpawnGroupInfo(IPlayer player, StringBuilder sb, SpawnGroup spawnGroup, int spawnPointCount)
        {
            sb.AppendLine($"<size={AdapterDisplayManager.HeaderSize}>{GetMessage(player.Id, LangEntry.ShowHeaderVanillaSpawnGroup, spawnGroup.name)}</size>");
            sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelSpawnPoints, spawnPointCount));

            if ((int)spawnGroup.Tier != -1)
            {
                sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelTiers, string.Join(", ", GetTierList(spawnGroup.Tier))));
            }

            if (spawnGroup.wantsInitialSpawn)
            {
                if (spawnGroup.temporary)
                {
                    sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelSpawnWhenParentSpawns));
                }
                else if (spawnGroup.forceInitialSpawn)
                {
                    sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelSpawnOnServerStart));
                }
                else
                {
                    sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelSpawnOnMapWipe));
                }
            }

            if (spawnGroup.preventDuplicates && spawnGroup.prefabs.Count > 1)
            {
                sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelPreventDuplicates));
            }

            sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelPopulation, spawnGroup.currentPopulation, spawnGroup.maxPopulation));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelRespawnPerTick, spawnGroup.numToSpawnPerTickMin, spawnGroup.numToSpawnPerTickMax));

            if (!spawnGroup.temporary)
            {
                if (!float.IsPositiveInfinity(spawnGroup.respawnDelayMin))
                {
                    sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelRespawnDelay, FormatTime(spawnGroup.respawnDelayMin), FormatTime(spawnGroup.respawnDelayMax)));
                }

                var nextSpawnTime = GetTimeToNextSpawn(spawnGroup);
                if (!float.IsPositiveInfinity(nextSpawnTime) && SingletonComponent<SpawnHandler>.Instance.SpawnGroups.Contains(spawnGroup))
                {
                    sb.AppendLine(GetMessage(
                        player.Id,
                        LangEntry.ShowLabelNextSpawn,
                        nextSpawnTime <= 0
                            ? GetMessage(player.Id, LangEntry.ShowLabelNextSpawnQueued)
                            : FormatTime(Mathf.CeilToInt(nextSpawnTime))
                    ));
                }
            }

            if (spawnGroup.prefabs.Count > 0)
            {
                var totalWeight = 0;
                foreach (var prefab in spawnGroup.prefabs)
                {
                    totalWeight += prefab.weight;
                }

                sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelEntities));
                foreach (var prefabEntry in spawnGroup.prefabs)
                {
                    var relativeChance = (float)prefabEntry.weight / totalWeight;
                    sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelEntityDetail, _uniqueNameRegistry.GetUniqueShortName(prefabEntry.prefab.resourcePath), prefabEntry.weight, relativeChance));
                }
            }
            else
            {
                sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelNoEntities));
            }
        }

        [Command("mashowvanilla")]
        private void CommandShowVanillaSpawns(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer) || !VerifyHasPermission(player))
                return;

            Transform parentObject = null;

            if (TryRaycast(basePlayer, out var hit))
            {
                parentObject = hit.GetEntity()?.transform;
            }

            if (parentObject == null)
            {
                if (!VerifyMonumentFinderLoaded(player)
                    || !VerifyHitPosition(player, out var position)
                    || !VerifyAtMonument(player, position, out var monument))
                    return;

                parentObject = monument.Object.transform;
            }

            var spawnerList = parentObject.GetComponentsInChildren<ISpawnGroup>();
            if (spawnerList.Length == 0)
            {
                var grandParent = parentObject.transform.parent;
                if (grandParent != null && grandParent != parentObject.transform.root)
                {
                    parentObject = grandParent;
                    spawnerList = parentObject.GetComponentsInChildren<ISpawnGroup>();
                }
            }

            if (spawnerList.Length == 0)
            {
                ReplyToPlayer(player, LangEntry.ShowVanillaNoSpawnPoints, parentObject.name);
                return;
            }

            _colorRotator.Reset();

            var playerPosition = basePlayer.transform.position;

            foreach (var spawner in spawnerList)
            {
                var spawnGroup = spawner as SpawnGroup;
                if (spawnGroup != null)
                {
                    var spawnPointList = spawnGroup.spawnPoints;
                    if (spawnPointList == null || spawnPointList.Length == 0)
                    {
                        spawnPointList = spawnGroup.GetComponentsInChildren<BaseSpawnPoint>();
                    }

                    var drawer = new Ddraw(basePlayer, ShowVanillaDuration, _colorRotator.GetNext());
                    var tierMask = (int)spawnGroup.Tier;

                    if (spawnPointList.Length == 0)
                    {
                        _sb.Clear();
                        AddSpawnGroupInfo(player, _sb, spawnGroup, spawnPointList.Length);
                        var spawnGroupPosition = spawnGroup.transform.position;

                        drawer.Sphere(spawnGroupPosition, 0.5f);
                        drawer.Text(spawnGroupPosition + new Vector3(0, tierMask > 0 ? Mathf.Log(tierMask, 2) : 0, 0), _sb.ToString());
                        continue;
                    }

                    BaseSpawnPoint closestSpawnPoint = null;
                    var closestDistanceSquared = float.MaxValue;

                    foreach (var spawnPoint in spawnPointList)
                    {
                        var distanceSquared = (playerPosition - spawnPoint.transform.position).sqrMagnitude;
                        if (distanceSquared < closestDistanceSquared)
                        {
                            closestSpawnPoint = spawnPoint;
                            closestDistanceSquared = distanceSquared;
                        }
                    }

                    var closestSpawnPointPosition = closestSpawnPoint.transform.position;

                    foreach (var spawnPoint in spawnPointList)
                    {
                        _sb.Clear();
                        _sb.AppendLine($"<size={AdapterDisplayManager.HeaderSize}>{GetMessage(player.Id, LangEntry.ShowHeaderVanillaSpawnPoint, spawnGroup.name)}</size>");

                        var booleanProperties = new List<string>();

                        var genericSpawnPoint = spawnPoint as GenericSpawnPoint;
                        if (genericSpawnPoint != null)
                        {
                            booleanProperties.Add(GetMessage(player.Id, LangEntry.ShowLabelSpawnPointExclusive));

                            if (genericSpawnPoint.randomRot)
                            {
                                booleanProperties.Add(GetMessage(player.Id, LangEntry.ShowLabelSpawnPointRandomRotation));
                            }

                            if (genericSpawnPoint.dropToGround)
                            {
                                booleanProperties.Add(GetMessage(player.Id, LangEntry.ShowLabelSpawnPointSnapToGround));
                            }
                        }

                        var spaceCheckingSpawnPoint = spawnPoint as SpaceCheckingSpawnPoint;
                        if (spaceCheckingSpawnPoint != null)
                        {
                            booleanProperties.Add(GetMessage(player.Id, LangEntry.ShowLabelSpawnPointCheckSpace));
                        }

                        if (booleanProperties.Count > 0)
                        {
                            _sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelFlags, string.Join(" | ", booleanProperties)));
                        }

                        var radialSpawnPoint = spawnPoint as RadialSpawnPoint;
                        if (radialSpawnPoint != null)
                        {
                            _sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelSpawnPointRandomRadius, radialSpawnPoint.radius));
                        }

                        if (spawnPoint == closestSpawnPoint)
                        {
                            _sb.AppendLine(AdapterDisplayManager.Divider);
                            AddSpawnGroupInfo(player, _sb, spawnGroup, spawnPointList.Length);
                        }

                        var spawnPointTransform = spawnPoint.transform;
                        var spawnPointPosition = spawnPointTransform.position;
                        drawer.Arrow(spawnPointPosition + AdapterDisplayManager.ArrowVerticalOffeset, spawnPointTransform.rotation, 1, 0.15f);
                        drawer.Sphere(spawnPointPosition, 0.5f);
                        drawer.Text(spawnPointPosition + new Vector3(0, tierMask > 0 ? Mathf.Log(tierMask, 2) : 0, 0), _sb.ToString());

                        if (spawnPoint != closestSpawnPoint)
                        {
                            drawer.Arrow(closestSpawnPointPosition + AdapterDisplayManager.ArrowVerticalOffeset, spawnPointPosition + AdapterDisplayManager.ArrowVerticalOffeset, 0.25f);
                        }
                    }

                    continue;
                }

                var individualSpawner = spawner as IndividualSpawner;
                if (individualSpawner != null)
                {
                    var drawer = new Ddraw(basePlayer, ShowVanillaDuration, _colorRotator.GetNext());

                    _sb.Clear();
                    _sb.AppendLine($"<size={AdapterDisplayManager.HeaderSize}>{GetMessage(player.Id, LangEntry.ShowHeaderVanillaIndividualSpawnPoint, individualSpawner.name)}</size>");
                    _sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelFlags, $"{GetMessage(player.Id, LangEntry.ShowLabelSpawnPointExclusive)} | {GetMessage(player.Id, LangEntry.ShowLabelSpawnPointCheckSpace)}"));

                    if (individualSpawner.oneTimeSpawner)
                    {
                        _sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelSpawnOnMapWipe));
                    }
                    else
                    {
                        if (!float.IsPositiveInfinity(individualSpawner.respawnDelayMin))
                        {
                            _sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelRespawnDelay, FormatTime(individualSpawner.respawnDelayMin), FormatTime(individualSpawner.respawnDelayMax)));
                        }

                        var nextSpawnTime = GetTimeToNextSpawn(individualSpawner);
                        if (!individualSpawner.IsSpawned && !float.IsPositiveInfinity(nextSpawnTime))
                        {
                            _sb.AppendLine(GetMessage(
                                player.Id,
                                LangEntry.ShowLabelNextSpawn,
                                nextSpawnTime <= 0
                                    ? GetMessage(player.Id, LangEntry.ShowLabelNextSpawnQueued)
                                    : FormatTime(Mathf.CeilToInt(nextSpawnTime))
                            ));
                        }
                    }

                    _sb.AppendLine(GetMessage(player.Id, LangEntry.ShowHeaderEntity, _uniqueNameRegistry.GetUniqueShortName(individualSpawner.entityPrefab.resourcePath)));

                    var spawnerTransform = individualSpawner.transform;
                    var spawnPointPosition = spawnerTransform.position;
                    drawer.Arrow(spawnPointPosition + AdapterDisplayManager.ArrowVerticalOffeset, spawnerTransform.rotation, 1f, 0.15f);
                    drawer.Sphere(spawnPointPosition, 0.5f);
                    drawer.Text(spawnPointPosition, _sb.ToString());
                    continue;
                }
            }
        }

        [Command("magenerate")]
        private void CommandGenerateSpawnPointProfile(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyMonumentFinderLoaded(player)
                || !VerifyHitPosition(player, out var position)
                || !VerifyAtMonument(player, position, out var monument))
                return;

            var parentObject = monument.Object.transform;

            var spawnerList = parentObject.GetComponentsInChildren<ISpawnGroup>();
            if (spawnerList.Length == 0)
            {
                var grandParent = parentObject.transform.parent;
                if (grandParent != null && grandParent != parentObject.transform.root)
                {
                    parentObject = grandParent;
                    spawnerList = parentObject.GetComponentsInChildren<ISpawnGroup>();
                }
            }

            if (spawnerList.Length == 0)
            {
                ReplyToPlayer(player, LangEntry.ShowVanillaNoSpawnPoints, parentObject.name);
                return;
            }

            var monumentTierMask = GetMonumentTierMask(monument.Position);
            var monumentTierList = GetTierList(monumentTierMask);
            var spawnGroupsSpecifyTier = false;

            var spawnGroupDataList = new List<SpawnGroupData>();

            foreach (var spawner in spawnerList)
            {
                var spawnGroup = spawner as SpawnGroup;
                if (spawnGroup != null)
                {
                    if (spawnGroup.spawnPoints.Length == 0)
                        continue;

                    if ((int)spawnGroup.Tier != -1)
                    {
                        spawnGroupsSpecifyTier = true;

                        if ((spawnGroup.Tier & monumentTierMask) == 0)
                        {
                            // Don't add spawn groups with different tiers. This can be improved later.
                            continue;
                        }
                    }

                    var spawnGroupData = new SpawnGroupData
                    {
                        Id = Guid.NewGuid(),
                        Name = spawnGroup.name,
                        MaxPopulation = spawnGroup.maxPopulation,
                        RespawnDelayMin = spawnGroup.respawnDelayMin,
                        RespawnDelayMax = spawnGroup.respawnDelayMax,
                        SpawnPerTickMin = spawnGroup.numToSpawnPerTickMin,
                        SpawnPerTickMax = spawnGroup.numToSpawnPerTickMax,
                        PreventDuplicates = spawnGroup.preventDuplicates,
                    };

                    spawnGroupDataList.Add(spawnGroupData);

                    foreach (var prefabEntry in spawnGroup.prefabs)
                    {
                        spawnGroupData.Prefabs.Add(new WeightedPrefabData
                        {
                            PrefabName = prefabEntry.prefab.resourcePath,
                            Weight = prefabEntry.weight,
                        });
                    }

                    foreach (var spawnPoint in spawnGroup.spawnPoints)
                    {
                        var spawnPointData = new SpawnPointData
                        {
                            Id = Guid.NewGuid(),
                            Position = monument.InverseTransformPoint(spawnPoint.transform.position),
                            RotationAngles = (Quaternion.Inverse(monument.Rotation) * spawnPoint.transform.rotation).eulerAngles,
                        };

                        var genericSpawnPoint = spawnPoint as GenericSpawnPoint;
                        if (genericSpawnPoint != null)
                        {
                            spawnPointData.Exclusive = true;
                            spawnPointData.RandomRotation = genericSpawnPoint.randomRot;
                            spawnPointData.SnapToGround = genericSpawnPoint.dropToGround;
                        }

                        var radialSpawnPoint = spawnPoint as RadialSpawnPoint;
                        if (radialSpawnPoint != null)
                        {
                            spawnPointData.RandomRotation = true;
                            spawnPointData.RandomRadius = radialSpawnPoint.radius;
                        }

                        if (spawnPoint is SpaceCheckingSpawnPoint)
                        {
                            spawnPointData.CheckSpace = true;
                        }

                        spawnGroupData.SpawnPoints.Add(spawnPointData);
                    }

                    continue;
                }
            }

            var tierSuffix = spawnGroupsSpecifyTier && monumentTierList.Count > 0
                ? $"_{string.Join("_", monumentTierList)}"
                : string.Empty;

            var profileName = $"{monument.UniqueDisplayName}{tierSuffix}_vanilla_generated";
            var profile = _profileStore.Create(profileName, basePlayer.displayName);

            foreach (var data in spawnGroupDataList)
            {
                profile.AddData(monument.UniqueName, data);
            }

            _profileStore.Save(profile);

            ReplyToPlayer(player, LangEntry.GenerateSuccess, profileName);
        }

        [Command("mawire")]
        private void CommandWire(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer) || !VerifyHasPermission(player))
                return;

            if (_wireToolManager.HasPlayer(basePlayer) && args.Length == 0)
            {
                _wireToolManager.StopSession(basePlayer);
                return;
            }

            WireColour? wireColor;
            if (args.Length > 0)
            {
                if (StringUtils.EqualsCaseInsensitive(args[0], "invisible"))
                {
                    wireColor = null;
                }
                else if (Enum.TryParse(args[0], ignoreCase: true, result: out WireColour parsedWireColor))
                {
                    wireColor = parsedWireColor;
                }
                else
                {
                    ReplyToPlayer(player, LangEntry.WireToolInvalidColor, args[0]);
                    return;
                }
            }
            else
            {
                wireColor = WireColour.Gray;
            }

            var activeItemShortName = basePlayer.GetActiveItem()?.info.shortname;
            if (activeItemShortName != "wiretool" && activeItemShortName != "hosetool")
            {
                ReplyToPlayer(player, LangEntry.WireToolNotEquipped);
                return;
            }

            _wireToolManager.StartOrUpdateSession(basePlayer, wireColor);

            ChatMessage(basePlayer, LangEntry.WireToolActivated, wireColor?.ToString() ?? GetMessage(player.Id, LangEntry.WireToolInvisible));
        }

        [Command("mahelp")]
        private void CommandHelp(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyHasPermission(player))
                return;

            _sb.Clear();
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpHeader));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSpawn));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpPrefab));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpKill));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpUndo));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSave));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpFlag));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSkin));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSetId));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSetDir));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSkull));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpTrophy));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpCardReaderLevel));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpPuzzle));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSpawnGroup));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSpawnPoint));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpPaste));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpEdit));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpShow));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpShowVanilla));
            _sb.AppendLine(GetMessage(player.Id, LangEntry.HelpProfile));
            player.Reply(_sb.ToString());
        }

        #endregion

        #region API

        [HookMethod(nameof(API_RegisterCustomAddon))]
        public Dictionary<string, object> API_RegisterCustomAddon(Plugin plugin, string addonName, Dictionary<string, object> addonSpec)
        {
            var addonDefinition = CustomAddonDefinition.FromDictionary(addonName, plugin, addonSpec);
            if (!addonDefinition.Validate())
                return null;

            if (_customAddonManager.IsRegistered(addonName, out var otherPlugin))
            {
                if (otherPlugin.Name != plugin.Name)
                {
                    LogError($"Unable to register custom addon \"{addonName}\" for plugin {plugin.Name} because it's already been registered by plugin {otherPlugin.Name}.");
                    return null;
                }
            }
            else
            {
                _customAddonManager.RegisterAddon(addonDefinition);
            }

            return addonDefinition.ToApiResult(_profileStore);
        }

        [HookMethod(nameof(API_RegisterCustomMonument))]
        public object API_RegisterCustomMonument(Plugin plugin, string monumentName, Component component, Bounds bounds)
        {
            var objectType = component is BaseEntity ? "entity" : "object";

            if (plugin == null)
            {
                LogError($"A plugin has attempted to register an {objectType} as a custom monument, but the plugin did not identify itself.");
                return False;
            }

            if (String.IsNullOrWhiteSpace(monumentName))
            {
                LogError($"Plugin {plugin.Name} tried to register an {objectType} as a custom monument, but did not provide a valid monument name.");
                return False;
            }

            if (component == null || (component is BaseEntity { IsDestroyed: true }))
            {
                LogError($"Plugin {plugin.Name} tried to register a null or destroyed {objectType} as a custom monument.");
                return False;
            }

            if (bounds == default)
            {
                LogWarning($"Plugin {plugin.Name} tried to register an {objectType} as a custom monument, but did not provide bounds. This was most likely a mistake by the developer of {plugin.Name} ({plugin.Author}).");
            }

            var existingMonument = _customMonumentManager.FindByComponent(component);
            if (existingMonument != null)
            {
                if (existingMonument.OwnerPlugin.Name != plugin.Name)
                {
                    LogError($"Plugin {plugin.Name} tried to register an {objectType} at {component.transform.position} as a custom monument with name '{monumentName}', but that {objectType} was already registered by plugin {existingMonument.OwnerPlugin.Name} with name '{existingMonument.UniqueName}'.");
                    return False;
                }
                else if (existingMonument.UniqueName != monumentName)
                {
                    LogError($"Plugin {plugin.Name} tried to register an {objectType} at {component.transform.position} as a custom monument with name '{monumentName}', but that {objectType} was already registered with name '{existingMonument.UniqueName}'.");
                    return False;
                }
                else if (existingMonument.Bounds != bounds)
                {
                    // Changing the bounds is permitted.
                    existingMonument.Bounds = bounds;
                    return True;
                }
                else
                {
                    LogWarning($"Plugin {plugin.Name} tried to double register a monument '{monumentName}'. This is OK but may be a mistake by the developer of {plugin.Name} ({plugin.Author}).");
                    return True;
                }
            }

            var monument = component is BaseEntity entity
                ? new CustomEntityMonument(plugin, entity, monumentName, bounds)
                : new CustomMonument(plugin, component, monumentName, bounds);

            _customMonumentManager.Register(monument);
            CustomMonumentComponent.AddToMonument(_customMonumentManager, monument);
            _coroutineManager.StartCoroutine(_profileManager.PartialLoadForLateMonumentRoutine(monument));

            LogInfo($"Plugin {plugin.Name} successfully registered an {objectType} at {component.transform.position} as a custom monument with name '{monumentName}'.");
            return True;
        }

        [HookMethod(nameof(API_UnregisterCustomMonument))]
        public object API_UnregisterCustomMonument(Plugin plugin, Component component)
        {
            var objectType = component is BaseEntity ? "entity" : "object";

            if (component == null || (component is BaseEntity { IsDestroyed: true }))
            {
                LogWarning($"Plugin {plugin.Name} tried to unregister a null or destroyed {objectType} as a custom monument. This is not necessary because {Name} automatically detects when custom monuments are destroyed and despawns associated addons. This is OK but may be a mistake by the developer of {plugin.Name} ({plugin.Author}).");
                return True;
            }

            var existingMonument = _customMonumentManager.FindByComponent(component);
            if (existingMonument == null)
            {
                LogError($"Plugin {plugin.Name} tried to unregister an {objectType} at {component.transform.position} as a custom monument, but that {objectType} was not currently registered as a custom monument. Either the {objectType} was unregistered earlier, or the wrong {objectType} was provided. This was most likely a mistake by the developer of {plugin.Name} ({plugin.Author}).");
                return True;
            }

            if (existingMonument.OwnerPlugin.Name != plugin.Name)
            {
                LogError($"Plugin {plugin.Name} tried to unregister an {objectType} at {component.transform.position} as a custom monument, but that {objectType} was registered as a custom monument by plugin {existingMonument.OwnerPlugin.Name}, so this was not allowed.");
                return False;
            }

            _customMonumentManager.Unregister(existingMonument);
            LogInfo($"Plugin {plugin.Name} successfully unregistered an {objectType} at {component.transform.position} as a custom monument with name '{existingMonument.UniqueName}'.");
            return True;
        }

        #endregion

        #region Utilities

        private static class ObjectCache
        {
            private static readonly object True = true;
            private static readonly object False = false;

            private static class StaticObjectCache<T>
            {
                private static readonly Dictionary<T, object> _cacheByValue = new();

                public static object Get(T value)
                {
                    if (!_cacheByValue.TryGetValue(value, out var cachedObject))
                    {
                        cachedObject = value;
                        _cacheByValue[value] = cachedObject;
                    }
                    return cachedObject;
                }
            }

            public static object Get<T>(T value)
            {
                return StaticObjectCache<T>.Get(value);
            }

            public static object Get(bool value)
            {
                return value ? True : False;
            }
        }

        #region Helper Methods - Command Checks

        private bool VerifyPlayer(IPlayer player, out BasePlayer basePlayer)
        {
            if (player.IsServer)
            {
                basePlayer = null;
                return false;
            }

            basePlayer = player.Object as BasePlayer;
            return true;
        }

        private bool VerifyHasPermission(IPlayer player, string perm = PermissionAdmin)
        {
            if (player.HasPermission(perm))
                return true;

            ReplyToPlayer(player, LangEntry.ErrorNoPermission);
            return false;
        }

        private bool VerifyValidInt<T>(IPlayer player, string arg, out int value, T errorFormatter) where T : IMessageFormatter
        {
            if (int.TryParse(arg, out value))
                return true;

            ReplyToPlayer(player, errorFormatter);
            return false;
        }

        private bool VerifyValidFloat<T>(IPlayer player, string arg, out float value, T errorFormatter) where T : IMessageFormatter
        {
            if (float.TryParse(arg, out value))
                return true;

            ReplyToPlayer(player, errorFormatter);
            return false;
        }

        private bool VerifyValidBool<T>(IPlayer player, string arg, out bool value, T errorFormatter) where T : IMessageFormatter
        {
            if (BooleanParser.TryParse(arg, out value))
                return true;

            ReplyToPlayer(player, errorFormatter);
            return false;
        }

        private bool VerifyValidEnumValue<T>(IPlayer player, string arg, out T enumValue) where T : struct
        {
            if (TryParseEnum(arg, out enumValue))
                return true;

            ReplyToPlayer(player, LangEntry.ErrorSetUnknownOption, arg);
            return false;
        }

        private bool VerifyMonumentFinderLoaded(IPlayer player)
        {
            if (MonumentFinder != null)
                return true;

            ReplyToPlayer(player, LangEntry.ErrorMonumentFinderNotLoaded);
            return false;
        }

        private bool VerifyProfileSelected(IPlayer player, out ProfileController profileController)
        {
            profileController = _profileManager.GetPlayerProfileControllerOrDefault(player.Id);
            if (profileController != null)
                return true;

            ReplyToPlayer(player, LangEntry.SpawnErrorNoProfileSelected);
            return false;
        }

        private bool VerifyHitPosition(IPlayer player, out Vector3 position)
        {
            if (TryGetHitPosition(player.Object as BasePlayer, out position))
                return true;

            ReplyToPlayer(player, LangEntry.ErrorNoSurface);
            return false;
        }

        private bool VerifyAtMonument(IPlayer player, Vector3 position, out BaseMonument closestMonument)
        {
            closestMonument = GetClosestMonument(player.Object as BasePlayer, position);
            if (closestMonument == null)
            {
                ReplyToPlayer(player, LangEntry.ErrorNoMonuments);
                return false;
            }

            if (!closestMonument.IsInBounds(position))
            {
                var closestPoint = closestMonument.ClosestPointOnBounds(position);
                var distance = (position - closestPoint).magnitude;
                ReplyToPlayer(player, LangEntry.ErrorNotAtMonument, closestMonument.UniqueDisplayName, distance.ToString("f1"));
                return false;
            }

            return true;
        }

        private bool VerifyLookingAtMonumentPosition(IPlayer player, out Vector3 position, out BaseMonument closestMonument)
        {
            if (!TryRaycast(player.Object as BasePlayer, out var hit))
            {
                ReplyToPlayer(player, LangEntry.ErrorNoSurface);
                position = Vector3.zero;
                closestMonument = null;
                return false;
            }

            position = hit.point;

            var entity = hit.GetEntity();
            if (entity != null && IsDynamicMonument(entity))
            {
                closestMonument = new DynamicMonument(entity);
                return true;
            }

            return VerifyAtMonument(player, position, out closestMonument);
        }

        private bool VerifyValidModderPrefab(IPlayer player, string[] args, out string prefabPath)
        {
            prefabPath = null;

            var prefabArg = args.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(prefabArg) || IsKeyBindArg(prefabArg))
            {
                ReplyToPlayer(player, LangEntry.PrefabErrorSyntax);
                return false;
            }

            var prefabMatches = SearchUtils.FindModderPrefabMatches(prefabArg);
            if (prefabMatches.Count == 1)
            {
                prefabPath = prefabMatches.First().ToLower();
                return true;
            }

            if (prefabMatches.Count == 0)
            {
                ReplyToPlayer(player, LangEntry.PrefabErrorNotFound, prefabArg);
                return false;
            }

            // Multiple matches were found, so print them all to the player.
            var replyMessage = GetMessage(player.Id, LangEntry.SpawnErrorMultipleMatches);
            foreach (var matchingPrefabPath in prefabMatches)
            {
                replyMessage += $"\n{matchingPrefabPath}";
            }

            player.Reply(replyMessage);
            return false;
        }

        private bool VerifyValidEntityPrefab(IPlayer player, string prefabArg, out string prefabPath)
        {
            prefabPath = null;

            var prefabMatches = SearchUtils.FindEntityPrefabMatches(prefabArg, _uniqueNameRegistry);
            if (prefabMatches.Count == 1)
            {
                prefabPath = prefabMatches.First().ToLower();
                return true;
            }

            if (prefabMatches.Count == 0)
            {
                ReplyToPlayer(player, LangEntry.SpawnErrorEntityNotFound, prefabArg);
                return false;
            }

            // Multiple matches were found, so print them all to the player.
            var replyMessage = GetMessage(player.Id, LangEntry.SpawnErrorMultipleMatches);
            foreach (var matchingPrefabPath in prefabMatches)
            {
                replyMessage += $"\n{_uniqueNameRegistry.GetUniqueShortName(matchingPrefabPath)}";
            }

            player.Reply(replyMessage);
            return false;
        }

        private bool VerifyValidEntityPrefabOrCustomAddon(IPlayer player, string prefabArg, out string prefabPath, out CustomAddonDefinition addonDefinition)
        {
            prefabPath = null;
            addonDefinition = null;

            var prefabMatches = SearchUtils.FindEntityPrefabMatches(prefabArg, _uniqueNameRegistry);
            var customAddonMatches = SearchUtils.FindCustomAddonMatches(prefabArg, _customAddonManager.GetAllAddons());

            var matchCount = prefabMatches.Count + customAddonMatches.Count;
            if (matchCount == 0)
            {
                ReplyToPlayer(player, LangEntry.SpawnErrorEntityOrAddonNotFound, prefabArg);
                return false;
            }

            if (matchCount == 1)
            {
                if (prefabMatches.Count == 1)
                {
                    prefabPath = prefabMatches.First().ToLower();
                }
                else
                {
                    addonDefinition = customAddonMatches.First();
                }

                return true;
            }

            // Multiple matches were found, so print them all to the player.
            var replyMessage = GetMessage(player.Id, LangEntry.SpawnErrorMultipleMatches);
            foreach (var matchingPrefabPath in prefabMatches)
            {
                replyMessage += $"\n{_uniqueNameRegistry.GetUniqueShortName(matchingPrefabPath)}";
            }
            foreach (var matchingAddonDefinition in customAddonMatches)
            {
                replyMessage += $"\n{matchingAddonDefinition.AddonName}";
            }

            player.Reply(replyMessage);
            return false;
        }

        private bool VerifyValidEntityPrefabOrDeployable(IPlayer player, string[] args, out string prefabPath, out CustomAddonDefinition addonDefinition, out ulong skinId)
        {
            var prefabArg = args.FirstOrDefault();
            skinId = 0;

            if (!string.IsNullOrWhiteSpace(prefabArg) && !IsKeyBindArg(prefabArg))
                return VerifyValidEntityPrefabOrCustomAddon(player, prefabArg, out prefabPath, out addonDefinition);

            addonDefinition = null;

            var basePlayer = player.Object as BasePlayer;
            var deployablePrefab = DeterminePrefabFromPlayerActiveDeployable(basePlayer, out skinId);
            if (!string.IsNullOrEmpty(deployablePrefab))
            {
                prefabPath = deployablePrefab;
                return true;
            }

            prefabPath = null;
            ReplyToPlayer(player, LangEntry.SpawnErrorSyntax);
            return false;
        }

        private bool VerifySpawnGroupPrefabOrCustomAddon(IPlayer player, SpawnGroupData spawnGroupData, string prefabArg, out WeightedPrefabData weightedPrefabData)
        {
            var customAddonMatches = spawnGroupData.FindCustomAddonMatches(prefabArg);
            if (customAddonMatches.Count == 1)
            {
                weightedPrefabData = customAddonMatches.First();
                return true;
            }

            var prefabMatches = spawnGroupData.FindPrefabMatches(prefabArg, _uniqueNameRegistry);
            if (prefabMatches.Count == 1)
            {
                weightedPrefabData = prefabMatches.First();
                return true;
            }

            var matchCount = prefabMatches.Count + customAddonMatches.Count;
            if (matchCount == 0)
            {
                ReplyToPlayer(player, LangEntry.SpawnGroupRemoveNoMatch, spawnGroupData.Name, prefabArg);
                weightedPrefabData = null;
                return false;
            }

            ReplyToPlayer(player, LangEntry.SpawnGroupRemoveMultipleMatches, spawnGroupData.Name, prefabArg);
            weightedPrefabData = null;
            return false;
        }

        private bool VerifyLookingAtAdapter<TAdapter, TController, TFormatter>(IPlayer player, out AdapterFindResult<TAdapter, TController> findResult, TFormatter errorFormatter)
            where TAdapter : TransformAdapter
            where TController : BaseController
            where TFormatter : IMessageFormatter
        {
            var basePlayer = player.Object as BasePlayer;

            var hitResult = FindHitAdapter<TAdapter, TController>(basePlayer, out var hit);
            if (hitResult.Controller != null)
            {
                // Found a suitable entity via direct hit.
                findResult = hitResult;
                return true;
            }

            var nearbyResult = FindClosestNearbyAdapter<TAdapter, TController>(hit.point);
            if (nearbyResult.Controller != null)
            {
                // Found a suitable nearby entity.
                findResult = nearbyResult;
                return true;
            }

            if (hitResult.Entity != null && hitResult.Component == null)
            {
                // Found an entity via direct hit, but it does not belong to Monument Addons.
                ReplyToPlayer(player, LangEntry.ErrorEntityNotEligible);
            }
            else
            {
                // Maybe found an entity, but it did not match the adapter/controller type.
                ReplyToPlayer(player, errorFormatter);
            }

            findResult = default(AdapterFindResult<TAdapter, TController>);
            return false;
        }

        private bool VerifyLookingAtAdapter<TAdapter, TController, TFormatter>(IPlayer player, out TAdapter adapter, out TController controller, TFormatter errorFormatter)
            where TAdapter : TransformAdapter
            where TController : BaseController
            where TFormatter : IMessageFormatter
        {
            var result = VerifyLookingAtAdapter(player, out AdapterFindResult<TAdapter, TController> findResult, errorFormatter);
            adapter = findResult.Adapter;
            controller = findResult.Controller;
            return result;
        }

        // Convenient method that does not require an adapter type.
        private bool VerifyLookingAtAdapter<TController, TFormatter>(IPlayer player, out TController controller, TFormatter errorFormatter)
            where TController : BaseController
            where TFormatter : IMessageFormatter
        {
            var result = VerifyLookingAtAdapter(player, out AdapterFindResult<TransformAdapter, TController> findResult, errorFormatter);
            controller = findResult.Controller;
            return result;
        }

        private bool VerifySpawnGroupFound(IPlayer player, string partialGroupName, BaseMonument monument, out SpawnGroupController spawnGroupController)
        {
            var matches = FindSpawnGroups(partialGroupName, monument.UniqueName, partialMatch: true).ToList();

            spawnGroupController = matches.FirstOrDefault();

            if (matches.Count == 1)
                return true;

            if (matches.Count == 0)
            {
                ReplyToPlayer(player, LangEntry.SpawnGroupNotFound, partialGroupName);
                return false;
            }

            var playerProfileController = _profileManager.GetPlayerProfileControllerOrDefault(player.Id);

            // Multiple matches found, try to narrow it down.
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                var match = matches[i];
                if (match.ProfileController != playerProfileController)
                {
                    // Remove any controllers that don't match the player's selected profile.
                    matches.Remove(match);
                }
            }

            if (matches.Count == 1)
            {
                spawnGroupController = matches[0];
                return true;
            }

            ReplyToPlayer(player, LangEntry.SpawnGroupMultipeMatches, partialGroupName);
            return false;
        }

        private bool VerifyProfileNameAvailable(IPlayer player, string profileName)
        {
            if (!_profileManager.ProfileExists(profileName))
                return true;

            ReplyToPlayer(player, LangEntry.ProfileAlreadyExists, profileName);
            return false;
        }

        private bool VerifyProfileExists(IPlayer player, string profileName, out ProfileController controller)
        {
            try
            {
                controller = _profileManager.GetProfileController(profileName);
                if (controller != null)
                    return true;
            }
            catch (JsonReaderException ex)
            {
                controller = null;
                player.Reply("{0}", string.Empty, ex.Message);
                return false;
            }

            ReplyToPlayer(player, LangEntry.ProfileNotFound, profileName);
            return false;
        }

        private bool VerifyProfile(IPlayer player, string[] args, out ProfileController controller, LangEntry0 syntaxLangEntry)
        {
            if (args.Length <= 1)
            {
                controller = player.IsServer ? null : _profileManager.GetPlayerProfileControllerOrDefault(player.Id);
                if (controller != null)
                    return true;

                ReplyToPlayer(player, syntaxLangEntry);
                return false;
            }

            return VerifyProfileExists(player, args[1], out controller);
        }

        private bool VerifySpawnGroupNameAvailable(IPlayer player, Profile profile, BaseMonument monument, string spawnGroupName, SpawnGroupController spawnGroupController = null)
        {
            var matches = FindSpawnGroups(spawnGroupName, monument.UniqueName, profile).ToList();
            if (matches.Count == 0)
                return true;

            // Allow renaming a spawn group with different case.
            if (spawnGroupController != null && matches.Count == 1 && matches[0] == spawnGroupController)
                return true;

            ReplyToPlayer(player, LangEntry.SpawnGroupCreateNameInUse, spawnGroupName, monument.UniqueName, profile.Name);
            return false;
        }

        private bool VerifyEntityComponent<T>(IPlayer player, BaseEntity entity, out T component, LangEntry0 errorLangEntry) where T : Component
        {
            if (entity.gameObject.TryGetComponent(out component))
                return true;

            ReplyToPlayer(player, errorLangEntry);
            return false;
        }

        private bool VerifyCanLoadProfile(IPlayer player, string profileName, out Profile profile)
        {
            if (!_profileStore.TryLoad(profileName, out profile, out var errorMessage))
            {
                player.Reply("{0}", string.Empty, errorMessage);
            }

            return true;
        }

        #endregion

        #region Helper Methods - Finding Adapters

        private struct AdapterFindResult<TAdapter, TController>
            where TAdapter : TransformAdapter
            where TController : BaseController
        {
            public BaseEntity Entity;
            public IAddonComponent Component;
            public TAdapter Adapter;
            public TController Controller;

            public AdapterFindResult(BaseEntity entity)
            {
                Entity = entity;
                Component = entity.GetComponent<IAddonComponent>();
                Adapter = Component?.Adapter as TAdapter;
                Controller = Adapter?.Controller as TController;
            }

            public AdapterFindResult(TAdapter adapter, TController controller)
            {
                Entity = null;
                Component = null;
                Adapter = adapter;
                Controller = controller;
            }
        }

        private AdapterFindResult<TAdapter, TController> FindHitAdapter<TAdapter, TController>(BasePlayer basePlayer, out RaycastHit hit)
            where TAdapter : TransformAdapter
            where TController : BaseController
        {
            if (!TryRaycast(basePlayer, out hit))
                return default(AdapterFindResult<TAdapter, TController>);

            var entity = hit.GetEntity();
            if (entity == null)
                return default(AdapterFindResult<TAdapter, TController>);

            if (IsSpawnPointEntity(entity, out var spawnPointAdapter) && spawnPointAdapter is TAdapter spawnAdapter)
                return new AdapterFindResult<TAdapter, TController>(spawnAdapter, spawnAdapter.Controller as TController);

            return new AdapterFindResult<TAdapter, TController>(entity);
        }

        private AdapterFindResult<TAdapter, TController> FindClosestNearbyAdapter<TAdapter, TController>(Vector3 position)
            where TAdapter : TransformAdapter
            where TController : BaseController
        {
            TAdapter closestNearbyAdapter = null;
            TController associatedController = null;
            var closestDistanceSquared = float.MaxValue;

            foreach (var adapter in _profileManager.GetEnabledAdapters<TAdapter>())
            {
                if (!adapter.IsValid || adapter.Controller is not TController controllerOfType)
                    continue;

                var adapterDistanceSquared = (adapter.Position - position).sqrMagnitude;
                if (adapterDistanceSquared <= MaxFindDistanceSquared && adapterDistanceSquared < closestDistanceSquared)
                {
                    closestNearbyAdapter = adapter;
                    associatedController = controllerOfType;
                    closestDistanceSquared = adapterDistanceSquared;
                }
            }

            return closestNearbyAdapter != null
                ? new AdapterFindResult<TAdapter, TController>(closestNearbyAdapter, associatedController)
                : default;
        }

        private AdapterFindResult<TAdapter, TController> FindAdapter<TAdapter, TController>(BasePlayer basePlayer)
            where TAdapter : TransformAdapter
            where TController : BaseController
        {
            var hitResult = FindHitAdapter<TAdapter, TController>(basePlayer, out var hit);
            if (hitResult.Controller != null)
                return hitResult;

            return FindClosestNearbyAdapter<TAdapter, TController>(hit.point);
        }

        // Convenient method that does not require a controller type.
        private AdapterFindResult<TAdapter, BaseController> FindAdapter<TAdapter>(BasePlayer basePlayer)
            where TAdapter : TransformAdapter
        {
            return FindAdapter<TAdapter, BaseController>(basePlayer);
        }

        // Convenient method that does not require an adapter or controller type.
        private AdapterFindResult<TransformAdapter, BaseController> FindAdapter(BasePlayer basePlayer)
        {
            return FindAdapter<TransformAdapter, BaseController>(basePlayer);
        }

        private IEnumerable<SpawnGroupController> FindSpawnGroups(string partialGroupName, string monumentIdentifier, Profile profile = null, bool partialMatch = false)
        {
            foreach (var spawnGroupController in _profileManager.GetEnabledControllers<SpawnGroupController>())
            {
                if (profile != null && spawnGroupController.Profile != profile)
                    continue;

                var spawnGroupName = spawnGroupController.SpawnGroupData.Name;

                if (partialMatch)
                {
                    if (spawnGroupName.IndexOf(partialGroupName, StringComparison.InvariantCultureIgnoreCase) == -1)
                        continue;
                }
                else if (!spawnGroupName.Equals(partialGroupName, StringComparison.InvariantCultureIgnoreCase))
                    continue;

                // Can only select a spawn group for the same monument.
                // This a slightly hacky way to check this, since data and controllers aren't directly aware of monuments.
                if (spawnGroupController.Adapters.FirstOrDefault()?.Monument.UniqueName != monumentIdentifier)
                    continue;

                yield return spawnGroupController;
            }
        }

        private PuzzleReset FindConnectedPuzzleReset(IOSlot[] slotList, HashSet<IOEntity> visited)
        {
            foreach (var slot in slotList)
            {
                var otherEntity = slot.connectedTo.Get();
                if (otherEntity == null)
                    continue;

                var puzzleReset = FindConnectedPuzzleReset(otherEntity, visited);
                if (puzzleReset != null)
                    return puzzleReset;
            }

            return null;
        }

        private PuzzleReset FindConnectedPuzzleReset(IOEntity ioEntity, HashSet<IOEntity> visited = null)
        {
            var puzzleReset = ioEntity.GetComponent<PuzzleReset>();
            if (puzzleReset != null)
                return puzzleReset;

            visited ??= new HashSet<IOEntity>();

            if (!visited.Add(ioEntity))
                return null;

            return FindConnectedPuzzleReset(ioEntity.inputs, visited)
                ?? FindConnectedPuzzleReset(ioEntity.outputs, visited);
        }

        #endregion

        #region Helper Methods

        public static void LogInfo(string message) => Interface.Oxide.LogInfo($"[Monument Addons] {message}");
        public static void LogError(string message) => Interface.Oxide.LogError($"[Monument Addons] {message}");
        public static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Monument Addons] {message}");

        private static bool RenameDictKey<TValue>(Dictionary<string, TValue> dict, string oldName, string newName)
        {
            if (dict.TryGetValue(oldName, out var monumentState))
            {
                dict[newName] = monumentState;
                dict.Remove(oldName);
                return true;
            }

            return false;
        }

        private static HashSet<string> DetermineAllDeployablePrefabs()
        {
            var prefabList = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            foreach (var itemDefinition in ItemManager.itemList)
            {
                var deployablePrefab = itemDefinition.GetComponent<ItemModDeployable>()?.entityPrefab?.resourcePath;
                if (deployablePrefab == null)
                    continue;

                prefabList.Add(deployablePrefab);
            }

            return prefabList;
        }

        private static bool IsKeyBindArg(string arg)
        {
            return arg == "True";
        }

        private static bool TryRaycast(BasePlayer player, out RaycastHit hit, float maxDistance = MaxRaycastDistance)
        {
            return Physics.Raycast(player.eyes.HeadRay(), out hit, maxDistance, HitLayers, QueryTriggerInteraction.Ignore);
        }

        private static bool TrySphereCast(BasePlayer player, out RaycastHit hit, int layerMask, float radius, float maxDistance = MaxRaycastDistance)
        {
            return Physics.SphereCast(player.eyes.HeadRay(), radius, out hit, maxDistance, layerMask, QueryTriggerInteraction.Ignore);
        }

        private static bool TryGetHitPosition(BasePlayer player, out Vector3 position)
        {
            if (TryRaycast(player, out var hit))
            {
                position = hit.point;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        public static T GetLookEntitySphereCast<T>(BasePlayer player, int layerMask, float radius, float maxDistance = MaxRaycastDistance) where T : BaseEntity
        {
            return TrySphereCast(player, out var hit, layerMask, radius, maxDistance)
                ? hit.GetEntity() as T
                : null;
        }

        private static void SendEffect(BasePlayer player, string effectPrefab)
        {
            var effect = new Effect(effectPrefab, player, 0, Vector3.zero, Vector3.forward);
            EffectNetwork.Send(effect, player.net.connection);
        }

        private static bool IsOnTerrain(Vector3 position)
        {
            return Math.Abs(position.y - TerrainMeta.HeightMap.GetHeight(position)) <= TerrainProximityTolerance;
        }

        private static string GetShortName(string prefabName)
        {
            var slashIndex = prefabName.LastIndexOf("/", StringComparison.Ordinal);
            var baseName = (slashIndex == -1) ? prefabName : prefabName.Substring(slashIndex + 1);
            return baseName.Replace(".prefab", string.Empty);
        }

        private static void DetermineLocalTransformData(Vector3 position, BasePlayer basePlayer, BaseMonument monument, out Vector3 localPosition, out Vector3 localRotationAngles, out bool isOnTerrain, bool flipRotation = true)
        {
            localPosition = monument.InverseTransformPoint(position);

            var localRotationAngle = basePlayer.HasParent()
                ? basePlayer.viewAngles.y
                : basePlayer.viewAngles.y - monument.Rotation.eulerAngles.y;

            if (flipRotation)
            {
                localRotationAngle += 180;
            }

            localRotationAngles = new Vector3(0, (localRotationAngle + 360) % 360, 0);
            isOnTerrain = IsOnTerrain(position);
        }

        private static void DestroyProblemComponents(BaseEntity entity)
        {
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<DestroyOnGroundMissing>());
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<GroundWatch>());
        }

        private static bool HasRigidBody(BaseEntity entity)
        {
            return entity.GetComponentInParent<Rigidbody>() != null;
        }

        private static bool IsRedirectSkin(ulong skinId, out string alternativeShortName)
        {
            alternativeShortName = null;

            if (skinId > int.MaxValue)
                return false;

            var skinIdInt = Convert.ToInt32(skinId);

            foreach (var skin in ItemSkinDirectory.Instance.skins)
            {
                var itemSkin = skin.invItem as ItemSkin;
                if (itemSkin == null || itemSkin.id != skinIdInt)
                    continue;

                var redirect = itemSkin.Redirect;
                if (redirect == null)
                    return false;

                var modDeployable = redirect.GetComponent<ItemModDeployable>();
                if (modDeployable != null)
                {
                    alternativeShortName = GetShortName(modDeployable.entityPrefab.resourcePath);
                }

                return true;
            }

            return false;
        }

        private static T FindPrefabComponent<T>(string prefabName) where T : Component
        {
            return GameManager.server.FindPrefab(prefabName)?.GetComponent<T>();
        }

        private static BaseEntity FindPrefabBaseEntity(string prefabName)
        {
            return FindPrefabComponent<BaseEntity>(prefabName);
        }

        private static string FormatTime(double seconds)
        {
            return TimeSpan.FromSeconds(seconds).ToString("g");
        }

        private static void BroadcastEntityTransformChange(BaseEntity entity)
        {
            if (entity is StabilityEntity)
            {
                entity.TerminateOnClient(BaseNetworkable.DestroyMode.None);
                entity.SendNetworkUpdateImmediate();
                return;
            }

            var wasSyncPosition = entity.syncPosition;
            entity.syncPosition = true;
            entity.TransformChanged();
            entity.syncPosition = wasSyncPosition;

            entity.transform.hasChanged = false;
        }

        private static void EnableSavingRecursive(BaseEntity entity, bool enableSaving)
        {
            entity.EnableSaving(enableSaving);

            foreach (var child in entity.children)
            {
                EnableSavingRecursive(child, enableSaving);
            }
        }

        private static IEnumerator WaitWhileWithTimeout(Func<bool> predicate, float timeoutSeconds)
        {
            var timeoutAt = UnityEngine.Time.time + timeoutSeconds;

            while (predicate() && UnityEngine.Time.time < timeoutAt)
            {
                yield return null;
            }
        }

        private static bool TryParseEnum<T>(string arg, out T enumValue) where T : struct
        {
            foreach (var value in Enum.GetValues(typeof(T)))
            {
                if (value.ToString().IndexOf(arg, StringComparison.InvariantCultureIgnoreCase) >= 0)
                {
                    enumValue = (T)value;
                    return true;
                }
            }

            enumValue = default(T);
            return false;
        }

        private static float GetTimeToNextSpawn(SpawnGroup spawnGroup)
        {
            var events = spawnGroup.spawnClock.events;

            if (events.Count == 0 || float.IsNaN(events.First().time))
                return float.PositiveInfinity;

            return events.First().time - UnityEngine.Time.time;
        }

        private static float GetTimeToNextSpawn(IndividualSpawner spawner)
        {
            if (spawner.nextSpawnTime == -1)
                return float.PositiveInfinity;

            return spawner.nextSpawnTime - UnityEngine.Time.time;
        }

        private static List<MonumentTier> GetTierList(MonumentTier tier)
        {
            var tierList = new List<MonumentTier>();

            if ((tier & MonumentTier.Tier0) != 0)
            {
                tierList.Add(MonumentTier.Tier0);
            }

            if ((tier & MonumentTier.Tier1) != 0)
            {
                tierList.Add(MonumentTier.Tier1);
            }

            if ((tier & MonumentTier.Tier2) != 0)
            {
                tierList.Add(MonumentTier.Tier2);
            }

            return tierList;
        }

        private static MonumentTier GetMonumentTierMask(Vector3 position)
        {
            var topologyMask = TerrainMeta.TopologyMap.GetTopology(position);

            var mask = (MonumentTier)0;

            if ((TerrainTopology.TIER0 & topologyMask) != 0)
            {
                mask |= MonumentTier.Tier0;
            }

            if ((TerrainTopology.TIER1 & topologyMask) != 0)
            {
                mask |= MonumentTier.Tier1;
            }

            if ((TerrainTopology.TIER2 & topologyMask) != 0)
            {
                mask |= MonumentTier.Tier2;
            }

            return mask;
        }

        private static BaseEntity FindValidEntity(ulong entityId)
        {
            var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(entityId)) as BaseEntity;
            return entity != null && !entity.IsDestroyed
                ? entity
                : null;
        }

        private static IEnumerator KillEntitiesRoutine(ICollection<BaseEntity> entityList)
        {
            foreach (var entity in entityList)
            {
                entity.Kill();
                yield return null;
            }
        }

        private static CustomSpawnPoint GetSpawnPoint(BaseEntity entity)
        {
            var spawnPointInstance = entity.GetComponent<SpawnPointInstance>();
            if (spawnPointInstance == null)
                return null;

            return spawnPointInstance.parentSpawnPoint as CustomSpawnPoint;
        }

        private static bool IsSpawnPointEntity(BaseEntity entity, out SpawnPointAdapter adapter)
        {
            adapter = null;

            var spawnPoint = GetSpawnPoint(entity);
            if (spawnPoint == null)
                return false;

            adapter = spawnPoint.Adapter as SpawnPointAdapter;
            return adapter != null;
        }

        private static bool IsSpawnPointEntity(BaseEntity entity)
        {
            return IsSpawnPointEntity(entity, out _);
        }

        private bool IsTransformAddon(Component component, out TransformAdapter adapter, out BaseController controller)
        {
            if (_componentTracker.IsAddonComponent(component, out adapter, out controller))
                return true;

            adapter = null;
            controller = null;

            if (component is IAddonComponent addonComponent)
            {
                adapter = addonComponent.Adapter;
            }
            else
            {
                if (component is not BaseEntity entity)
                    return false;

                if (!IsSpawnPointEntity(entity, out var spawnPointAdapter))
                    return false;

                adapter = spawnPointAdapter;
            }

            controller = adapter?.Controller;
            return controller != null;
        }

        private bool IsTransformAddon(Component component, out BaseController controller)
        {
            return IsTransformAddon(component, out _, out controller);
        }

        private bool HasAdminPermission(string userId)
        {
            return permission.UserHasPermission(userId, PermissionAdmin);
        }

        private bool HasAdminPermission(BasePlayer player)
        {
            return HasAdminPermission(player.UserIDString);
        }

        private bool IsDynamicMonument(BaseEntity entity)
        {
            return _config.DynamicMonuments.IsConfiguredAsDynamicMonument(entity)
                || _profileManager.HasDynamicMonument(entity);
        }

        private bool IsPlayerParentedToDynamicMonument(BasePlayer player, Vector3 position, out BaseMonument monument)
        {
            monument = null;

            var parentEntity = player.GetParentEntity();
            if (parentEntity == null || !IsDynamicMonument(parentEntity))
                return false;

            monument = new DynamicMonument(parentEntity, isMobile: true);
            return monument.IsInBounds(position);
        }

        private BaseMonument GetClosestMonument(BasePlayer player, Vector3 position)
        {
            if (IsPlayerParentedToDynamicMonument(player, position, out var dynamicMonument))
                return dynamicMonument;

            var monument = _customMonumentManager.FindByPosition(position);
            if (monument != null)
                return monument;

            return _monumentHelper.GetClosestMonumentAdapter(position);
        }

        private List<BaseMonument> GetDynamicMonumentInstances(uint prefabId)
        {
            var entityList = (List<BaseMonument>)null;

            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                if (networkable is BaseEntity entity
                    && entity.prefabID == prefabId
                    && ExposedHooks.OnDynamicMonument(entity) is not false)
                {
                    entityList ??= new List<BaseMonument>();
                    entityList.Add(new DynamicMonument(entity));
                }
            }

            return entityList;
        }

        private List<BaseMonument> GetMonumentsByIdentifier(string monumentIdentifier)
        {
            if (monumentIdentifier.StartsWith("assets/"))
            {
                var baseEntity = FindPrefabBaseEntity(monumentIdentifier);
                if (baseEntity != null && IsDynamicMonument(baseEntity))
                    return GetDynamicMonumentInstances(baseEntity.prefabID);
            }

            var customMonuments = _customMonumentManager.FindMonumentsByName(monumentIdentifier);
            if (customMonuments?.Count > 0)
                return customMonuments;

            var monuments = _monumentHelper.FindMonumentsByAlias(monumentIdentifier);
            if (monuments.Count > 0)
                return monuments;

            return _monumentHelper.FindMonumentsByShortName(monumentIdentifier);
        }

        private IEnumerator SpawnAllProfilesRoutine()
        {
            // Delay one frame to allow Monument Finder to finish loading.
            yield return null;
            yield return _profileManager.LoadAllProfilesRoutine();

            ExposedHooks.OnMonumentAddonsInitialized();
        }

        private void StartupRoutine()
        {
            // Don't spawn entities if that's already been done.
            if (_startupCoroutine != null)
                return;

            _startupCoroutine = _coroutineManager.StartCoroutine(SpawnAllProfilesRoutine());
        }

        private void DownloadProfile(IPlayer player, string url, Action<Profile> successCallback, Action<string> errorCallback)
        {
            webrequest.Enqueue(
                url: url,
                body: null,
                callback: (statusCode, responseBody) =>
                {
                    if (!IsLoaded)
                    {
                        // Ignore the response because the plugin was unloaded.
                        return;
                    }

                    if (statusCode != 200)
                    {
                        errorCallback(GetMessage(player.Id, LangEntry.ProfileDownloadError, url, statusCode));
                        return;
                    }

                    Profile profile;
                    try
                    {
                        profile = JsonConvert.DeserializeObject<Profile>(responseBody);
                    }
                    catch (Exception ex)
                    {
                        errorCallback(GetMessage(player.Id, LangEntry.ProfileParseError, url, ex.Message));
                        return;
                    }

                    ProfileDataMigration<Profile>.MigrateToLatest(profile);

                    profile.Url = url;
                    successCallback(profile);
                },
                owner: this,
                method: RequestMethod.GET,
                headers: JsonRequestHeaders,
                timeout: 5000
            );
        }

        private string DeterminePrefabFromPlayerActiveDeployable(BasePlayer basePlayer, out ulong skinId)
        {
            skinId = 0;

            var activeItem = basePlayer.GetActiveItem();
            if (activeItem == null)
                return null;

            skinId = activeItem.skin;

            if (_config.DeployableOverrides.TryGetValue(activeItem.info.shortname, out var overridePrefabPath))
                return overridePrefabPath;

            var itemModDeployable = activeItem.info.GetComponent<ItemModDeployable>();
            if (itemModDeployable == null)
                return null;

            return itemModDeployable.entityPrefab.resourcePath;
        }

        // This is a best-effort attempt to flag as many possible entities as possible without false positives.
        // Hopefully this will help users learn they are using the wrong command before they open a support thread.
        private bool ShouldRecommendSpawnPoints(string prefabName)
        {
            var entity = FindPrefabBaseEntity(prefabName);

            if (entity is BaseNpc or BradleyAPC or PatrolHelicopter or SimpleShark)
                return true;

            if (entity is LootContainer && !_deployablePrefabs.Contains(prefabName))
                return true;

            if (entity is NPCPlayer and not NPCShopKeeper and not BanditGuard)
                return true;

            if (entity is BaseBoat or BaseHelicopter or BaseRidableAnimal or BaseSubmarine or BasicCar or GroundVehicle or HotAirBalloon or Sled or TrainCar)
                return true;

            return false;
        }

        #endregion

        #region Helper Classes

        private static class StringUtils
        {
            public static bool EqualsCaseInsensitive(string a, string b)
            {
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) == 0;
            }

            public static bool Contains(string haystack, string needle)
            {
                return haystack?.Contains(needle, CompareOptions.IgnoreCase) ?? false;
            }
        }

        private static class SearchUtils
        {
            public static List<string> FindModderPrefabMatches(string partialName)
            {
                return FindMatches(
                    GameManifest.Current.pooledStrings.Select(pooledString => pooledString.str)
                        .Where(str => str.StartsWith("assets/bundled/prefabs/modding", StringComparison.OrdinalIgnoreCase)),
                    prefabPath => StringUtils.Contains(prefabPath, partialName),
                    prefabPath => StringUtils.EqualsCaseInsensitive(prefabPath, partialName)
                );
            }

            public static List<T> FindPrefabMatches<T>(IEnumerable<T> sourceList, Func<T, string> selector, string partialName, UniqueNameRegistry uniqueNameRegistry)
            {
                return SearchUtils.FindMatches(
                    sourceList,
                    prefabPath => StringUtils.Contains(selector(prefabPath), partialName),
                    prefabPath => StringUtils.EqualsCaseInsensitive(selector(prefabPath), partialName),
                    prefabPath => StringUtils.Contains(uniqueNameRegistry.GetUniqueShortName(selector(prefabPath)), partialName),
                    prefabPath => StringUtils.EqualsCaseInsensitive(uniqueNameRegistry.GetUniqueShortName(selector(prefabPath)), partialName)
                );
            }

            public static List<string> FindEntityPrefabMatches(string partialName, UniqueNameRegistry uniqueNameRegistry)
            {
                return FindMatches(
                    GameManifest.Current.entities,
                    prefabPath => StringUtils.Contains(prefabPath, partialName) && FindPrefabBaseEntity(prefabPath) != null,
                    prefabPath => StringUtils.EqualsCaseInsensitive(prefabPath, partialName),
                    prefabPath => StringUtils.Contains(uniqueNameRegistry.GetUniqueShortName(prefabPath), partialName),
                    prefabPath => StringUtils.EqualsCaseInsensitive(uniqueNameRegistry.GetUniqueShortName(prefabPath), partialName)
                );
            }

            public static List<T> FindCustomAddonMatches<T>(IEnumerable<T> sourceList, Func<T, string> selector, string partialName)
            {
                return FindMatches(
                    sourceList,
                    addonDefinition => StringUtils.Contains(selector(addonDefinition), partialName),
                    addonDefinition => StringUtils.EqualsCaseInsensitive(selector(addonDefinition), partialName)
                );
            }

            public static List<CustomAddonDefinition> FindCustomAddonMatches(string partialName, IEnumerable<CustomAddonDefinition> customAddons)
            {
                return FindCustomAddonMatches(customAddons, customAddonDefinition => customAddonDefinition.AddonName, partialName);
            }

            public static List<T> FindMatches<T>(IEnumerable<T> sourceList, params Func<T, bool>[] predicateList)
            {
                List<T> results = null;

                foreach (var predicate in predicateList)
                {
                    if (results == null)
                    {
                        results = sourceList.Where(predicate).ToList();
                        continue;
                    }

                    var newResults = results.Where(predicate).ToList();
                    if (newResults.Count == 0)
                    {
                        // No matches found after filtering, so ignore the results and then try a different filter.
                        continue;
                    }

                    if (newResults.Count == 1)
                    {
                        // Only a single match after filtering, so return new results.
                        return newResults;
                    }

                    // Multiple matches found, so proceed with further filtering.
                    results = newResults;
                }

                return results;
            }
        }

        private struct Ddraw
        {
            private const float DefaultBoxSphereRadius = 0.5f;

            public static void Sphere(BasePlayer player, float duration, Color color, Vector3 origin, float radius)
            {
                player.SendConsoleCommand("ddraw.sphere", duration, color, origin, radius);
            }

            public static void Line(BasePlayer player, float duration, Color color, Vector3 origin, Vector3 target)
            {
                player.SendConsoleCommand("ddraw.line", duration, color, origin, target);
            }

            public static void Arrow(BasePlayer player, float duration, Color color, Vector3 origin, Vector3 target, float headSize)
            {
                player.SendConsoleCommand("ddraw.arrow", duration, color, origin, target, headSize);
            }

            public static void Arrow(BasePlayer player, float duration, Color color, Vector3 center, Quaternion rotation, float length, float headSize)
            {
                var origin = center - rotation * Vector3.forward * length;
                var target = center + rotation * Vector3.forward * length;
                Arrow(player, duration, color, origin, target, headSize);
            }

            public static void Text(BasePlayer player, float duration, Color color, Vector3 origin, string text)
            {
                player.SendConsoleCommand("ddraw.text", duration, color, origin, text);
            }

            public static void Box(BasePlayer player, float duration, Color color, Vector3 center, Quaternion rotation, Vector3 extents, float sphereRadius = 0.5f)
            {
                var forwardUpperLeft = center + rotation * extents.WithX(-extents.x);
                var forwardUpperRight = center + rotation * extents;
                var forwardLowerLeft = center + rotation * extents.WithX(-extents.x).WithY(-extents.y);
                var forwardLowerRight = center + rotation * extents.WithY(-extents.y);

                var backLowerRight = center + rotation * -extents.WithX(-extents.x);
                var backLowerLeft = center + rotation * -extents;
                var backUpperRight = center + rotation * -extents.WithX(-extents.x).WithY(-extents.y);
                var backUpperLeft = center + rotation * -extents.WithY(-extents.y);

                Sphere(player, duration, color, forwardUpperLeft, sphereRadius);
                Sphere(player, duration, color, forwardUpperRight, sphereRadius);
                Sphere(player, duration, color, forwardLowerLeft, sphereRadius);
                Sphere(player, duration, color, forwardLowerRight, sphereRadius);

                Sphere(player, duration, color, backLowerRight, sphereRadius);
                Sphere(player, duration, color, backLowerLeft, sphereRadius);
                Sphere(player, duration, color, backUpperRight, sphereRadius);
                Sphere(player, duration, color, backUpperLeft, sphereRadius);

                Line(player, duration, color, forwardUpperLeft, forwardUpperRight);
                Line(player, duration, color, forwardLowerLeft, forwardLowerRight);
                Line(player, duration, color, forwardUpperLeft, forwardLowerLeft);
                Line(player, duration, color, forwardUpperRight, forwardLowerRight);

                Line(player, duration, color, backUpperLeft, backUpperRight);
                Line(player, duration, color, backLowerLeft, backLowerRight);
                Line(player, duration, color, backUpperLeft, backLowerLeft);
                Line(player, duration, color, backUpperRight, backLowerRight);

                Line(player, duration, color, forwardUpperLeft, backUpperLeft);
                Line(player, duration, color, forwardLowerLeft, backLowerLeft);
                Line(player, duration, color, forwardUpperRight, backUpperRight);
                Line(player, duration, color, forwardLowerRight, backLowerRight);
            }

            public static void Box(BasePlayer player, float duration, Color color, OBB obb, float sphereRadius)
            {
                Box(player, duration, color, obb.position, obb.rotation, obb.extents, sphereRadius);
            }

            private BasePlayer _player;
            private Color _color;
            public float Duration { get; }

            public Ddraw(BasePlayer player, float duration, Color? color = null)
            {
                _player = player;
                _color = color ?? Color.white;
                Duration = duration;
            }

            public void Sphere(Vector3 position, float radius, float? duration = null, Color? color = null)
            {
                Sphere(_player, duration ?? Duration, color ?? _color, position, radius);
            }

            public void Line(Vector3 origin, Vector3 target, float? duration = null, Color? color = null)
            {
                Line(_player, duration ?? Duration, color ?? _color, origin, target);
            }

            public void Arrow(Vector3 origin, Vector3 target, float headSize, float? duration = null, Color? color = null)
            {
                Arrow(_player, duration ?? Duration, color ?? _color, origin, target, headSize);
            }

            public void Arrow(Vector3 center, Quaternion rotation, float length, float headSize, float? duration = null, Color? color = null)
            {
                Arrow(_player, duration ?? Duration, color ?? _color, center, rotation, length, headSize);
            }

            public void Text(Vector3 position, string text, float? duration = null, Color? color = null)
            {
                Text(_player, duration ?? Duration, color ?? _color, position, text);
            }

            public void Box(Vector3 center, Quaternion rotation, Vector3 extents, float sphereRadius = DefaultBoxSphereRadius, float? duration = null, Color? color = null)
            {
                Box(_player, duration ?? Duration, color ?? _color, center, rotation, extents, sphereRadius);
            }

            public void Box(OBB obb, float sphereRadius = DefaultBoxSphereRadius, float? duration = null, Color? color = null)
            {
                Box(_player, duration ?? Duration, color ?? _color, obb, sphereRadius);
            }
        }

        private static class EntityUtils
        {
            public static T GetNearbyEntity<T>(BaseEntity originEntity, float maxDistance, int layerMask = -1, string filterShortPrefabName = null) where T : BaseEntity
            {
                var entityList = new List<T>();
                Vis.Entities(originEntity.transform.position, maxDistance, entityList, layerMask, QueryTriggerInteraction.Ignore);
                foreach (var entity in entityList)
                {
                    if (filterShortPrefabName == null || entity.ShortPrefabName == filterShortPrefabName)
                        return entity;
                }

                return null;
            }

            public static T GetClosestNearbyEntity<T>(Vector3 position, float maxDistance, int layerMask = -1, Func<T, bool> predicate = null) where T : BaseEntity
            {
                var entityList = Pool.GetList<T>();
                Vis.Entities(position, maxDistance, entityList, layerMask, QueryTriggerInteraction.Ignore);
                try
                {
                    return GetClosestComponent(position, entityList, predicate);
                }
                finally
                {
                    Pool.FreeList(ref entityList);
                }
            }

            public static T GetClosestNearbyComponent<T>(Vector3 position, float maxDistance, int layerMask = -1, Func<T, bool> predicate = null) where T : Component
            {
                var componentList = Pool.GetList<T>();
                Vis.Components(position, maxDistance, componentList, layerMask, QueryTriggerInteraction.Ignore);
                try
                {
                    return GetClosestComponent(position, componentList, predicate);
                }
                finally
                {
                    Pool.FreeList(ref componentList);
                }
            }

            public static void ConnectNearbyVehicleSpawner(VehicleVendor vehicleVendor)
            {
                if (vehicleVendor.GetVehicleSpawner() != null)
                    return;

                var vehicleSpawner = vehicleVendor.ShortPrefabName == "bandit_conversationalist"
                    ? GetNearbyEntity<VehicleSpawner>(vehicleVendor, 40, Rust.Layers.Mask.Deployed, "airwolfspawner")
                    : vehicleVendor.ShortPrefabName == "boat_shopkeeper"
                    ? GetNearbyEntity<VehicleSpawner>(vehicleVendor, 20, Rust.Layers.Mask.Deployed, "boatspawner")
                    : null;

                if (vehicleSpawner == null)
                    return;

                vehicleVendor.spawnerRef.Set(vehicleSpawner);
            }

            public static void ConnectNearbyVehicleVendor(VehicleSpawner vehicleSpawner)
            {
                var vehicleVendor = vehicleSpawner.ShortPrefabName == "airwolfspawner"
                    ? GetNearbyEntity<VehicleVendor>(vehicleSpawner, 40, Rust.Layers.Mask.Player_Server, "bandit_conversationalist")
                    : vehicleSpawner.ShortPrefabName == "boatspawner"
                    ? GetNearbyEntity<VehicleVendor>(vehicleSpawner, 20, Rust.Layers.Mask.Player_Server, "boat_shopkeeper")
                    : null;

                if (vehicleVendor == null)
                    return;

                vehicleVendor.spawnerRef.Set(vehicleSpawner);
            }

            public static void ConnectNearbyDoor(DoorManipulator doorManipulator)
            {
                // The door manipulator normally checks on layer 21, but use layerMask -1 to allow finding arctic garage doors.
                var door = GetClosestNearbyEntity<Door>(doorManipulator.transform.position, 3);
                if (door == null || door.IsDestroyed)
                    return;

                doorManipulator.SetTargetDoor(door);
                door.SetFlag(BaseEntity.Flags.Locked, true);
            }

            private static T GetClosestComponent<T>(Vector3 position, List<T> componentList, Func<T, bool> predicate = null) where T : Component
            {
                T closestComponent = null;
                float closestDistanceSquared = float.MaxValue;

                foreach (var component in componentList)
                {
                    if (predicate?.Invoke(component) == false)
                        continue;

                    var distance = (component.transform.position - position).sqrMagnitude;
                    if (distance < closestDistanceSquared)
                    {
                        closestDistanceSquared = distance;
                        closestComponent = component;
                    }
                }

                return closestComponent;
            }
        }

        private static class EntitySetupUtils
        {
            public static bool ShouldBeImmortal(BaseEntity entity)
            {
                var samSite = entity as SamSite;
                if (samSite != null && samSite.staticRespawn)
                    return false;

                return true;
            }

            public static void PreSpawnShared(BaseEntity entity)
            {
                var combatEntity = entity as BaseCombatEntity;
                if (combatEntity != null)
                {
                    combatEntity.pickup.enabled = false;
                }

                var stabilityEntity = entity as StabilityEntity;
                if (stabilityEntity != null)
                {
                    stabilityEntity.grounded = true;
                    stabilityEntity.canBeDemolished = false;
                }

                DestroyProblemComponents(entity);
            }

            public static void PostSpawnShared(MonumentAddons plugin, BaseEntity entity, bool enableSaving)
            {
                // Enable/Disable saving after spawn to account for children that spawn late (e.g., Lift).
                EnableSavingRecursive(entity, enableSaving);

                var combatEntity = entity as BaseCombatEntity;
                if (combatEntity != null)
                {
                    if (ShouldBeImmortal(entity))
                    {
                        var basePlayer = entity as BasePlayer;
                        if (basePlayer != null)
                        {
                            // Don't share common protection properties with BasePlayer instances since they get destroyed on kill.
                            combatEntity.baseProtection.Clear();
                            combatEntity.baseProtection.Add(1);
                        }
                        else
                        {
                            // Must set after spawn for building blocks.
                            combatEntity.baseProtection = plugin._immortalProtection;
                        }
                    }
                }

                var decayEntity = entity as DecayEntity;
                if (decayEntity != null)
                {
                    decayEntity.decay = null;

                    var buildingBlock = entity as BuildingBlock;
                    if (buildingBlock != null)
                    {
                        // Must be done after spawn for some reason.
                        if (buildingBlock.HasFlag(BuildingBlock.BlockFlags.CanRotate)
                            || buildingBlock.HasFlag(StabilityEntity.DemolishFlag))
                        {
                            buildingBlock.SetFlag(BuildingBlock.BlockFlags.CanRotate, false, recursive: false, networkupdate: false);
                            buildingBlock.SetFlag(StabilityEntity.DemolishFlag, false, recursive: false, networkupdate: false);
                            buildingBlock.CancelInvoke(buildingBlock.StopBeingRotatable);
                            buildingBlock.CancelInvoke(buildingBlock.StopBeingDemolishable);
                            buildingBlock.SendNetworkUpdate_Flags();
                        }
                    }
                }
            }
        }

        private static class BooleanParser
        {
            private static string[] _booleanYesValues = new string[] { "true", "yes", "on", "1" };
            private static string[] _booleanNoValues = new string[] { "false", "no", "off", "0" };

            public static bool TryParse(string arg, out bool value)
            {
                if (_booleanYesValues.Contains(arg, StringComparer.InvariantCultureIgnoreCase))
                {
                    value = true;
                    return true;
                }

                if (_booleanNoValues.Contains(arg, StringComparer.InvariantCultureIgnoreCase))
                {
                    value = false;
                    return true;
                }

                value = false;
                return false;
            }
        }

        private class ValueRotator<T>
        {
            private T[] _values;
            private int _index;

            public ValueRotator(params T[] values)
            {
                _values = values;
            }

            public T GetNext()
            {
                var color = _values[_index++];
                if (_index >= _values.Length)
                {
                    _index = 0;
                }

                return color;
            }

            public void Reset()
            {
                _index = 0;
            }
        }

        private class HookCollection
        {
            private MonumentAddons _plugin;
            private string[] _hookNames;
            private Func<bool> _shouldSubscribe;
            private bool _isSubscribed;

            public HookCollection(MonumentAddons plugin, string[] hookNames, Func<bool> shouldSubscribe = null)
            {
                _plugin = plugin;
                _hookNames = hookNames;
                _shouldSubscribe = shouldSubscribe ?? (() => true);
            }

            public void Refresh()
            {
                if (_shouldSubscribe())
                {
                    if (!_isSubscribed)
                    {
                        Subscribe();
                    }
                }
                else if (_isSubscribed)
                {
                    Unsubscribe();
                }
            }

            public void Subscribe()
            {
                foreach (var hookName in _hookNames)
                {
                    _plugin.Subscribe(hookName);
                }

                _isSubscribed = true;
            }

            public void Unsubscribe()
            {
                foreach (var hookName in _hookNames)
                {
                    _plugin.Unsubscribe(hookName);
                }

                _isSubscribed = false;
            }
        }

        private class UniqueNameRegistry
        {
            private Dictionary<string, string> _uniqueNameByPrefabPath = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            public void OnServerInitialized()
            {
                BuildIndex();
            }

            public string GetUniqueShortName(string prefabPath)
            {
                if (!_uniqueNameByPrefabPath.TryGetValue(prefabPath, out var uniqueName))
                {
                    // Unique names are only stored initially if different from short name.
                    // To avoid frequent heap allocations, also cache other short names that are accessed.
                    uniqueName = GetShortName(prefabPath);
                    _uniqueNameByPrefabPath[prefabPath] = uniqueName.ToLower();
                }

                return uniqueName;
            }

            private string[] GetSegments(string prefabPath)
            {
                return prefabPath.Split('/');
            }

            private string GetPartialPath(string[] segments, int numSegments)
            {
                numSegments = Math.Min(numSegments, segments.Length);
                var arraySegment = new ArraySegment<string>(segments, segments.Length - numSegments, numSegments);
                return string.Join("/", arraySegment);
            }

            private void BuildIndex()
            {
                var remainingPrefabPaths = GameManifest.Current.entities.ToList();
                var numSegmentsFromEnd = 1;

                var iterations = 0;
                var maxIterations = 1;

                while (remainingPrefabPaths.Count > 0 && iterations++ < maxIterations)
                {
                    var countByPartialPath = new Dictionary<string, int>();

                    foreach (var prefabPath in remainingPrefabPaths)
                    {
                        var segments = GetSegments(prefabPath);
                        maxIterations = Math.Max(maxIterations, segments.Length);

                        var partialPath = GetPartialPath(segments, numSegmentsFromEnd);

                        if (!countByPartialPath.TryGetValue(partialPath, out var segmentCount))
                        {
                            segmentCount = 0;
                        }

                        countByPartialPath[partialPath] = segmentCount + 1;
                    }

                    for (var i = remainingPrefabPaths.Count - 1; i >= 0; i--)
                    {
                        var prefabPath = remainingPrefabPaths[i];
                        var partialPath = GetPartialPath(GetSegments(prefabPath), numSegmentsFromEnd);

                        if (countByPartialPath[partialPath] == 1)
                        {
                            // Only cache the unique name if different than short name.
                            if (numSegmentsFromEnd > 1)
                            {
                                _uniqueNameByPrefabPath[prefabPath] = partialPath.ToLower().Replace(".prefab", string.Empty);
                            }

                            remainingPrefabPaths.RemoveAt(i);
                        }
                    }

                    numSegmentsFromEnd++;
                }
            }
        }

        private class EmptyMonoBehavior : MonoBehaviour {}

        private class CoroutineManager
        {
            public static Coroutine StartGlobalCoroutine(IEnumerator enumerator)
            {
                return ServerMgr.Instance?.StartCoroutine(enumerator);
            }

            private static IEnumerator CallbackRoutine(Coroutine dependency, Action action)
            {
                yield return dependency;
                action();
            }

            // Object for tracking all coroutines for spawning or updating entities.
            // This allows easily stopping all those coroutines by simply destroying the game object.
            private MonoBehaviour _coroutineComponent;

            public Coroutine StartCoroutine(IEnumerator enumerator)
            {
                if (_coroutineComponent == null)
                {
                    _coroutineComponent = new GameObject().AddComponent<EmptyMonoBehavior>();
                }

                return _coroutineComponent.StartCoroutine(enumerator);
            }

            public Coroutine StartCallbackRoutine(Coroutine coroutine, Action callback)
            {
                return StartCoroutine(CallbackRoutine(coroutine, callback));
            }

            public void StopAll()
            {
                if (_coroutineComponent == null)
                    return;

                _coroutineComponent.StopAllCoroutines();
            }

            public void Destroy()
            {
                if (_coroutineComponent == null)
                    return;

                UnityEngine.Object.DestroyImmediate(_coroutineComponent?.gameObject);
            }
        }

        private class WireToolManager
        {
            private const float DrawDuration = 3;
            private const float DrawSlotRadius = 0.04f;
            private const float PreviewDotRadius = 0.01f;
            private const float InputCooldown = 0.25f;
            private const float HoldToClearDuration = 0.5f;

            private const float DrawIntervalDuration = 0.01f;
            private const float DrawIntervalWithBuffer = DrawIntervalDuration + 0.05f;
            private const float MinAngleDot = 0.999f;

            private class WireSession
            {
                public BasePlayer Player { get; }
                public WireColour? WireColor;
                public IOType WireType;
                public EntityAdapter Adapter;
                public IOSlot StartSlot;
                public int StartSlotIndex;
                public bool IsSource;
                public float LastInput;
                public float SecondaryPressedTime = float.MaxValue;
                public float LastDrawTime;

                public List<Vector3> Points = new List<Vector3>();

                public WireSession(BasePlayer player)
                {
                    Player = player;
                }

                public void Reset(bool refreshPoints = false)
                {
                    Points.Clear();
                    if (refreshPoints)
                    {
                        RefreshPoints();
                    }

                    Adapter = null;
                    SecondaryPressedTime = float.MaxValue;
                }

                public void StartConnection(EntityAdapter adapter, IOSlot startSlot, int slotIndex, bool isSource)
                {
                    WireType = startSlot.type;
                    Adapter = adapter;
                    StartSlot = startSlot;
                    StartSlotIndex = slotIndex;
                    IsSource = isSource;
                    startSlot.wireColour = WireColor ?? WireColour.Gray;
                }

                public void AddPoint(Vector3 position)
                {
                    Points.Add(position);
                    RefreshPoints();
                }

                public void RemoveLast()
                {
                    if (Points.Count > 0)
                    {
                        Points.RemoveAt(Points.Count - 1);
                        RefreshPoints();
                    }
                    else
                    {
                        Reset(refreshPoints: true);
                    }
                }

                public bool IsOnInputCooldown()
                {
                    return LastInput + InputCooldown > UnityEngine.Time.time;
                }

                public bool IsOnDrawCooldown()
                {
                    return LastDrawTime + DrawIntervalDuration > UnityEngine.Time.time;
                }

                public void RefreshPoints()
                {
                    var ioEntity = Adapter?.Entity as IOEntity;
                    if (ioEntity == null)
                        return;

                    var slot = IsSource
                        ? ioEntity.outputs[StartSlotIndex]
                        : ioEntity.inputs[StartSlotIndex];

                    if (slot.type == IOType.Kinetic)
                    {
                        // Don't attempt to render wires for kinetic slots since that will kick the player.
                        return;
                    }

                    slot.linePoints = new Vector3[Points.Count + 1];

                    if (IsSource)
                    {
                        slot.linePoints[0] = slot.handlePosition;
                        for (var i = 0; i < Points.Count; i++)
                        {
                            slot.linePoints[i + 1] = ioEntity.transform.InverseTransformPoint(Points[i]);
                        }
                    }
                    else
                    {
                        // TODO: Implement a temporary entity to show a line from destination input
                    }

                    ioEntity.SendNetworkUpdate();
                }
            }

            private MonumentAddons _plugin;
            private ProfileStore _profileStore;
            private AddonComponentTracker _componentTracker => _plugin._componentTracker;
            private List<WireSession> _playerSessions = new List<WireSession>();
            private Timer _timer;

            public WireToolManager(MonumentAddons plugin, ProfileStore profileStore)
            {
                _plugin = plugin;
                _profileStore = profileStore;
            }

            public bool HasPlayer(BasePlayer player)
            {
                return GetPlayerSession(player) != null;
            }

            public void StartOrUpdateSession(BasePlayer player, WireColour? wireColor)
            {
                var session = GetPlayerSession(player, out _);
                if (session == null)
                {
                    session = new WireSession(player);
                    _playerSessions.Add(session);
                }

                session.WireColor = wireColor;

                if (_timer == null || _timer.Destroyed)
                {
                    _timer = _plugin.timer.Every(0, ProcessPlayers);
                }
            }

            public void StopSession(BasePlayer player)
            {
                var session = GetPlayerSession(player, out var sessionIndex);
                if (session == null)
                    return;

                DestroySession(session, sessionIndex);
            }

            public void Unload()
            {
                for (var i = _playerSessions.Count - 1; i >= 0; i--)
                {
                    DestroySession(_playerSessions[i], i);
                }
            }

            private WireSession GetPlayerSession(BasePlayer player, out int index)
            {
                for (var i = 0; i < _playerSessions.Count; i++)
                {
                    var session = _playerSessions[i];
                    if (session.Player == player)
                    {
                        index = i;
                        return session;
                    }
                }

                index = 0;
                return null;
            }

            private WireSession GetPlayerSession(BasePlayer player)
            {
                return GetPlayerSession(player, out _);
            }

            private void DestroySession(WireSession session, int index)
            {
                session.Reset(refreshPoints: true);
                _playerSessions.RemoveAt(index);

                if (_playerSessions.Count == 0 && _timer != null)
                {
                    _timer.Destroy();
                    _timer = null;
                }

                _plugin.ChatMessage(session.Player, LangEntry.WireToolDeactivated);
            }

            private EntityAdapter GetLookIOAdapter(BasePlayer player, out IOEntity ioEntity)
            {
                ioEntity = GetLookEntitySphereCast<IOEntity>(player, Rust.Layers.Solid, 0.1f, 6);
                if (ioEntity == null)
                    return null;

                if (!_componentTracker.IsAddonComponent(ioEntity, out EntityAdapter adapter, out EntityController _))
                {
                    _plugin.ChatMessage(player, LangEntry.ErrorEntityNotEligible);
                    return null;
                }

                if (adapter == null)
                {
                    _plugin.ChatMessage(player, LangEntry.ErrorNoSuitableAddonFound);
                    return null;
                }

                return adapter;
            }

            private IOSlot GetClosestIOSlot(IOEntity ioEntity, Ray ray, float minDot, out int index, out float highestDot, bool wantsSourceSlot, bool? wantsOccupiedSlots = false)
            {
                IOSlot closestSlot = null;
                index = 0;
                highestDot = -1f;

                var transform = ioEntity.transform;
                var slotList = wantsSourceSlot ? ioEntity.outputs : ioEntity.inputs;

                for (var slotIndex = 0; slotIndex < slotList.Length; slotIndex++)
                {
                    var slot = slotList[slotIndex];
                    if (wantsOccupiedSlots.HasValue && wantsOccupiedSlots.Value == (slot.connectedTo.Get() == null))
                        continue;

                    var slotPosition = transform.TransformPoint(slot.handlePosition);

                    var dot = Vector3.Dot(ray.direction, (slotPosition - ray.origin).normalized);
                    if (dot > minDot && dot > highestDot)
                    {
                        closestSlot = slot;
                        index = slotIndex;
                        highestDot = dot;
                    }
                }

                return closestSlot;
            }

            private IOSlot GetClosestIOSlot(IOEntity ioEntity, Ray ray, float minDot, out int index, out bool isSourceSlot, bool? wantsOccupiedSlots = null)
            {
                index = 0;
                isSourceSlot = false;

                var sourceSlot = GetClosestIOSlot(
                    ioEntity,
                    ray,
                    minDot,
                    out var sourceSlotIndex,
                    out var sourceDot,
                    wantsSourceSlot: true,
                    wantsOccupiedSlots: wantsOccupiedSlots
                );

                var destinationSlot = GetClosestIOSlot(
                    ioEntity,
                    ray, minDot, out var destinationSlotIndex,
                    out var destinationDot,
                    wantsSourceSlot: false,
                    wantsOccupiedSlots: wantsOccupiedSlots
                );

                if (sourceSlot == null && destinationSlot == null)
                    return null;

                if (sourceSlot != null && destinationSlot != null)
                {
                    if (sourceDot >= destinationDot)
                    {
                        isSourceSlot = true;
                        index = sourceSlotIndex;
                        return sourceSlot;
                    }

                    index = destinationSlotIndex;
                    return destinationSlot;
                }

                if (sourceSlot != null)
                {
                    isSourceSlot = true;
                    index = sourceSlotIndex;
                    return sourceSlot;
                }

                index = destinationSlotIndex;
                return destinationSlot;
            }

            private bool CanPlayerUseTool(BasePlayer player)
            {
                if (player == null || player.IsDestroyed || !player.IsConnected || player.IsDead())
                    return false;

                var activeItemShortName = player.GetActiveItem()?.info.shortname;
                if (activeItemShortName == null)
                    return false;

                return activeItemShortName == "wiretool" || activeItemShortName == "hosetool";
            }

            private Color DetermineSlotColor(IOSlot slot)
            {
                if (slot.connectedTo.Get() != null)
                    return Color.red;

                if (slot.type == IOType.Fluidic)
                    return Color.cyan;

                if (slot.type == IOType.Kinetic)
                    return new Color(1, 0.5f, 0);

                return Color.yellow;
            }

            private void ShowSlots(BasePlayer player, IOEntity ioEntity, bool showSourceSlots)
            {
                var transform = ioEntity.transform;
                var slotList = showSourceSlots ? ioEntity.outputs : ioEntity.inputs;

                foreach (var slot in slotList)
                {
                    var color = DetermineSlotColor(slot);
                    var drawer = new Ddraw(player, DrawDuration, color);
                    var position = transform.TransformPoint(slot.handlePosition);

                    drawer.Sphere(position, DrawSlotRadius);
                    drawer.Text(position, showSourceSlots ? "OUT" : "IN");
                }
            }

            private void DrawSessionState(WireSession session)
            {
                if (session.Adapter == null || session.IsOnDrawCooldown())
                    return;

                var player = session.Player;
                session.LastDrawTime = UnityEngine.Time.time;

                var ioEntity = session.Adapter.Entity as IOEntity;
                var startPosition = ioEntity.transform.TransformPoint(session.StartSlot.handlePosition);
                var drawer = new Ddraw(player, DrawDuration, Color.green);
                drawer.Sphere(startPosition, DrawSlotRadius);

                if (TryGetHitPosition(player, out var hitPosition))
                {
                    var lastPoint = session.Points.Count == 0
                        ? session.StartSlot.handlePosition
                        : session.StartSlot.linePoints.LastOrDefault();

                    drawer.Sphere(hitPosition, PreviewDotRadius);
                    drawer.Line(ioEntity.transform.TransformPoint(lastPoint), hitPosition);
                }
            }

            private void MaybeStartWire(WireSession session, EntityAdapter adapter)
            {
                var player = session.Player;
                var ioEntity = adapter.Entity as IOEntity;
                var drawer = new Ddraw(player, DrawDuration);

                var slot = GetClosestIOSlot(ioEntity, player.eyes.HeadRay(), MinAngleDot, out var slotIndex, out var isSourceSlot, wantsOccupiedSlots: false);
                if (slot == null)
                {
                    ShowSlots(player, ioEntity, showSourceSlots: true);
                    ShowSlots(player, ioEntity, showSourceSlots: false);
                    return;
                }

                if (slot.connectedTo.Get() != null)
                {
                    drawer.Sphere(adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius, color: Color.red);
                    return;
                }

                session.StartConnection(adapter, slot, slotIndex, isSource: isSourceSlot);
                drawer.Sphere(adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius, color: Color.green);
                SendEffect(player, WireToolPlugEffect);
            }

            private void MaybeEndWire(WireSession session, EntityAdapter adapter)
            {
                var player = session.Player;
                var ioEntity = adapter.Entity as IOEntity;
                var drawer = new Ddraw(player, DrawDuration, Color.red);

                var headRay = player.eyes.HeadRay();
                var slot = GetClosestIOSlot(ioEntity, headRay, MinAngleDot, out var slotIndex, out var distanceSquared, wantsSourceSlot: !session.IsSource);
                if (slot == null)
                {
                    slot = GetClosestIOSlot(ioEntity, headRay, MinAngleDot, out slotIndex, out distanceSquared, wantsSourceSlot: session.IsSource);
                    if (slot != null)
                    {
                        drawer.Sphere(adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius);
                    }

                    ShowSlots(player, ioEntity, showSourceSlots: !session.IsSource);
                    return;
                }

                if (slot.connectedTo.Get() != null)
                {
                    drawer.Sphere(adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius);
                    return;
                }

                var adapterProfile = adapter.Controller.Profile;
                var sessionProfile = session.Adapter.Controller.Profile;
                if (adapterProfile != sessionProfile)
                {
                    drawer.Sphere(adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius);
                    _plugin.ChatMessage(player, LangEntry.WireToolProfileMismatch, sessionProfile, adapterProfile.Name);
                    return;
                }

                if (!adapter.Monument.IsEquivalentTo(session.Adapter.Monument))
                {
                    drawer.Sphere(adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius);
                    _plugin.ChatMessage(player, LangEntry.WireToolMonumentMismatch);
                    return;
                }

                if (slot.type != session.WireType)
                {
                    drawer.Sphere(adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius);
                    _plugin.ChatMessage(player, LangEntry.WireToolTypeMismatch, session.WireType, slot.type);
                    return;
                }

                var sourceAdapter = session.IsSource ? session.Adapter : adapter;
                var destinationAdapter = session.IsSource ? adapter : session.Adapter;

                var points = session.Points.Select(adapter.Monument.InverseTransformPoint);
                if (!session.IsSource)
                {
                    points = points.Reverse();
                }

                var connectionData = new IOConnectionData
                {
                    ConnectedToId = destinationAdapter.EntityData.Id,
                    Slot = session.IsSource ? session.StartSlotIndex : slotIndex,
                    ConnectedToSlot = session.IsSource ? slotIndex : session.StartSlotIndex,
                    Points = points.ToArray(),
                    ShowWire = session.WireColor.HasValue,
                    Color = session.WireColor ?? WireColour.Gray,
                };

                sourceAdapter.EntityData.AddIOConnection(connectionData);
                _profileStore.Save(sourceAdapter.Controller.Profile);

                (sourceAdapter.Controller as EntityController).StartUpdateRoutine();
                session.Reset();

                drawer.Sphere(adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius, color: Color.green);
                SendEffect(player, WireToolPlugEffect);
            }

            private void MaybeClearWire(WireSession session)
            {
                var player = session.Player;
                session.SecondaryPressedTime = float.MaxValue;

                var adapter = GetLookIOAdapter(player, out var ioEntity);
                if (adapter == null)
                    return;

                var slot = GetClosestIOSlot(ioEntity, player.eyes.HeadRay(), MinAngleDot, out var slotIndex, out var isSourceSlot, wantsOccupiedSlots: true);
                if (slot == null)
                    return;

                EntityAdapter sourceAdapter;
                EntityAdapter destinationAdapter;
                int sourceSlotIndex;

                if (isSourceSlot)
                {
                    sourceAdapter = adapter;
                    sourceSlotIndex = slotIndex;

                    var destinationEntity = slot.connectedTo.Get();

                    if (!_componentTracker.IsAddonComponent(destinationEntity, out destinationAdapter, out EntityController _))
                        return;
                }
                else
                {
                    destinationAdapter = adapter;

                    var sourceEntity = slot.connectedTo.Get();

                    if (!_componentTracker.IsAddonComponent(sourceEntity, out sourceAdapter, out EntityController _))
                        return;

                    sourceSlotIndex = slot.connectedToSlot;
                }

                sourceAdapter.EntityData.RemoveIOConnection(sourceSlotIndex);
                _profileStore.Save(sourceAdapter.Controller.Profile);
                var handleChangesRoutine = (sourceAdapter.Controller as EntityController).StartUpdateRoutine();
                destinationAdapter.ProfileController.StartCallbackRoutine(handleChangesRoutine,
                    () => (destinationAdapter.Controller as EntityController).StartUpdateRoutine());
            }

            private void ProcessPlayers()
            {
                for (var i = _playerSessions.Count - 1; i >= 0; i--)
                {
                    var session = _playerSessions[i];
                    var player = session.Player;

                    if (!CanPlayerUseTool(player))
                    {
                        DestroySession(session, i);
                        continue;
                    }

                    if (session.Adapter != null && (session.Adapter == null || session.Adapter.IsDestroyed))
                    {
                        session.Reset();
                    }

                    var secondaryJustPressed = player.serverInput.WasJustPressed(BUTTON.FIRE_SECONDARY);
                    var secondaryJustReleased = player.serverInput.WasJustReleased(BUTTON.FIRE_SECONDARY);
                    var now = UnityEngine.Time.time;
                    var secondaryPressedDuration = 0f;

                    if (secondaryJustPressed)
                    {
                        session.SecondaryPressedTime = now;
                    }
                    else if (secondaryJustReleased)
                    {
                        session.SecondaryPressedTime = float.MaxValue;
                    }
                    else if (session.SecondaryPressedTime != float.MaxValue)
                    {
                        secondaryPressedDuration = now - session.SecondaryPressedTime;
                    }

                    DrawSessionState(session);

                    if (session.IsOnInputCooldown())
                        continue;

                    if (player.serverInput.WasJustPressed(BUTTON.FIRE_PRIMARY))
                    {
                        session.LastInput = now;

                        var adapter = GetLookIOAdapter(player, out _);
                        if (adapter != null)
                        {
                            if (session.Adapter == null)
                            {
                                MaybeStartWire(session, adapter);
                                continue;
                            }

                            MaybeEndWire(session, adapter);
                            continue;
                        }

                        if (session.Adapter != null)
                        {
                            if (session.WireType == IOType.Kinetic)
                            {
                                // Placing wires with kinetic slots will kick the player.
                                continue;
                            }

                            if (session.WireColor.HasValue && TryGetHitPosition(player, out var position))
                            {
                                session.AddPoint(position);
                                SendEffect(player, WireToolPlugEffect);
                            }
                        }

                        continue;
                    }

                    if (session.Adapter != null && secondaryJustPressed)
                    {
                        // Remove the most recently placed wire.
                        session.LastInput = now;
                        session.RemoveLast();
                    }

                    if (session.Adapter == null
                        && session.SecondaryPressedTime != float.MaxValue
                        && secondaryPressedDuration >= HoldToClearDuration)
                    {
                        MaybeClearWire(session);
                    }
                }

                if (_playerSessions.Count == 0 && _timer is { Destroyed: true })
                {
                    _timer.Destroy();
                    _timer = null;
                }
            }
        }

        private abstract class DictionaryKeyConverter<TKey, TValue> : JsonConverter
        {
            public virtual string KeyToString(TKey key)
            {
                return key.ToString();
            }

            public abstract TKey KeyFromString(string key);

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Dictionary<TKey, TValue>);
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                var obj = serializer.Deserialize(reader) as JObject;
                if (existingValue is not Dictionary<TKey, TValue> dict)
                    return null;

                foreach (var entry in obj)
                {
                    dict[KeyFromString(entry.Key)] = entry.Value.ToObject<TValue>();
                }

                return dict;
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (value is not Dictionary<TKey, TValue> dict)
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                    return;
                }

                writer.WriteStartObject();

                foreach (var entry in dict)
                {
                    writer.WritePropertyName(KeyToString(entry.Key));
                    serializer.Serialize(writer, entry.Value);
                }

                writer.WriteEndObject();
            }
        }

        private class HtmlColorConverter : JsonConverter
        {
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (value == null)
                {
                    writer.WriteNull();
                    return;
                }

                Color color = (Color)value;
                writer.WriteValue(Mathf.Approximately(color.a, 1f)
                    ? $"#{ColorUtility.ToHtmlStringRGB(color)}"
                    : $"#{ColorUtility.ToHtmlStringRGBA(color)}");
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Color);
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                    return default(Color);

                if (reader.Value is not string colorString || !ColorUtility.TryParseHtmlString(colorString, out var color))
                    throw new JsonException($"Invalid RGB color string: {reader.Value}");

                return color;
            }
        }

        private class ActionDebounced
        {
            private PluginTimers _pluginTimers;
            private float _seconds;
            private Action _action;
            private Timer _timer;

            public ActionDebounced(PluginTimers pluginTimers, float seconds, Action action)
            {
                _pluginTimers = pluginTimers;
                _seconds = seconds;
                _action = action;
            }

            public void Schedule()
            {
                if (_timer == null || _timer.Destroyed)
                {
                    _timer = _pluginTimers.Once(_seconds, _action);
                    return;
                }

                // Restart the existing timer.
                _timer.Reset();
            }

            public void Flush()
            {
                if (_timer == null || _timer.Destroyed)
                    return;

                _timer.Destroy();
                _action();
            }
        }

        private interface IDeepCollection
        {
            bool HasItems();
        }

        private static bool HasDeepItems<TKey, TValue>(Dictionary<TKey, TValue> dict) where TValue : IDeepCollection
        {
            if (dict.Count == 0)
                return false;

            foreach (var value in dict.Values)
            {
                if (value.HasItems())
                    return true;
            }

            return false;
        }

        private class MonumentHelper
        {
            private MonumentAddons _plugin;
            private Plugin _monumentFinder => _plugin.MonumentFinder;
            private Dictionary<DungeonGridInfo, MonumentInfo> _entranceToMonument = new Dictionary<DungeonGridInfo, MonumentInfo>();

            public MonumentHelper(MonumentAddons plugin)
            {
                _plugin = plugin;
            }

            public void OnServerInitialized()
            {
                foreach (var monumentInfo in TerrainMeta.Path.Monuments)
                {
                    if (monumentInfo.DungeonEntrance != null)
                    {
                        _entranceToMonument[monumentInfo.DungeonEntrance] = monumentInfo;
                    }
                }
            }

            public List<BaseMonument> FindMonumentsByAlias(string alias)
            {
                return WrapFindMonumentResults(_monumentFinder.Call("API_FindByAlias", alias) as List<Dictionary<string, object>>);
            }

            public List<BaseMonument> FindMonumentsByShortName(string shortName)
            {
                return WrapFindMonumentResults(_monumentFinder.Call("API_FindByShortName", shortName) as List<Dictionary<string, object>>);
            }

            public MonumentAdapter GetClosestMonumentAdapter(Vector3 position)
            {
                if (_monumentFinder.Call("API_GetClosest", position) is not Dictionary<string, object> dictResult)
                    return null;

                return new MonumentAdapter(dictResult);
            }

            public bool IsMonumentUnique(string shortName)
            {
                var monuments = FindMonumentsByShortName(shortName);
                return monuments == null || monuments.Count <= 1;
            }

            private List<BaseMonument> WrapFindMonumentResults(List<Dictionary<string, object>> dictList)
            {
                if (dictList == null)
                    return null;

                var monumentList = new List<BaseMonument>();
                foreach (var dict in dictList)
                {
                    monumentList.Add(new MonumentAdapter(dict));
                }

                return monumentList;
            }

            public MonumentInfo GetMonumentFromTunnel(DungeonGridCell dungeonGridCell)
            {
                var entrance = TerrainMeta.Path.FindClosest(TerrainMeta.Path.DungeonGridEntrances, dungeonGridCell.transform.position);
                if (entrance == null)
                    return null;

                return GetMonumentFromEntrance(entrance);
            }

            private MonumentInfo GetMonumentFromEntrance(DungeonGridInfo dungeonGridInfo)
            {
                return _entranceToMonument.TryGetValue(dungeonGridInfo, out var monumentInfo)
                    ? monumentInfo
                    : null;
            }
        }

        private static class PhoneUtils
        {
            public const string Tunnel = "FTL";
            public const string UnderwaterLab = "Underwater Lab";
            public const string CargoShip = "Cargo Ship";

            private static readonly Regex SplitCamelCaseRegex = new Regex("([a-z])([A-Z])", RegexOptions.Compiled);

            private static readonly string[] PolymorphicMonumentVariants =
            {
                "fishing_village_b",
                "fishing_village_c",
                "harbor_1",
                "harbor_2",
                "power_sub_big_1",
                "power_sub_big_2",
                "power_sub_small_1",
                "power_sub_small_2",
                "water_well_a",
                "water_well_b",
                "water_well_c",
                "water_well_d",
                "water_well_e",
                "entrance_bunker_a",
                "entrance_bunker_b",
                "entrance_bunker_c",
                "entrance_bunker_d",
            };

            public static void NameTelephone(Telephone telephone, BaseMonument monument, Vector3 position, MonumentHelper monumentHelper)
            {
                string phoneName = null;

                var monumentInfo = monument.Object as MonumentInfo;
                if (monumentInfo != null && !string.IsNullOrEmpty(monumentInfo.displayPhrase.translated))
                {
                    phoneName = monumentInfo.displayPhrase.translated;

                    if (ShouldAppendCoordinate(monument.ShortName, monumentHelper))
                    {
                        phoneName += $" {PhoneController.PositionToGridCoord(position)}";
                    }
                }

                var dungeonGridCell = monument.Object as DungeonGridCell;
                if (dungeonGridCell != null && !string.IsNullOrEmpty(monument.Alias))
                {
                    phoneName = GetFTLPhoneName(monument.Alias, dungeonGridCell, monument, position, monumentHelper);
                }

                var dungeonBaseLink = monument.Object as DungeonBaseLink;
                if (dungeonBaseLink != null)
                {
                    phoneName = GetUnderwaterLabPhoneName(dungeonBaseLink, position);
                }

                if (monument is DynamicMonument dynamicMonument)
                {
                    phoneName = GetDynamicMonumentPhoneName(dynamicMonument, telephone);
                }

                telephone.Controller.PhoneName = !string.IsNullOrEmpty(phoneName)
                    ? phoneName
                    : $"{telephone.GetDisplayName()} {PhoneController.PositionToGridCoord(position)}";

                TelephoneManager.RegisterTelephone(telephone.Controller);
            }

            private static string GetFTLCorridorPhoneName(string tunnelName, Vector3 position)
            {
                return $"{Tunnel} {tunnelName} {PhoneController.PositionToGridCoord(position)}";
            }

            private static string GetFTLPhoneName(string tunnelAlias, DungeonGridCell dungeonGridCell, BaseMonument monument, Vector3 position, MonumentHelper monumentHelper)
            {
                var tunnelName = SplitCamelCase(tunnelAlias);
                var phoneName = GetFTLCorridorPhoneName(tunnelName, position);

                if (monument.UniqueName == "TrainStation")
                {
                    var attachedMonument = monumentHelper.GetMonumentFromTunnel(dungeonGridCell);
                    if (attachedMonument != null && !attachedMonument.name.Contains("tunnel-entrance/entrance_bunker"))
                    {
                        phoneName = GetFTLTrainStationPhoneName(attachedMonument, tunnelName, position, monumentHelper);
                    }
                }

                return phoneName;
            }

            private static string GetFTLTrainStationPhoneName(MonumentInfo attachedMonument, string tunnelName, Vector3 position, MonumentHelper monumentHelper)
            {
                var phoneName = string.IsNullOrEmpty(attachedMonument.displayPhrase.translated)
                    ? $"{Tunnel} {tunnelName}"
                    : $"{Tunnel} {attachedMonument.displayPhrase.translated} {tunnelName}";

                var shortname = GetShortName(attachedMonument.name);

                if (ShouldAppendCoordinate(shortname, monumentHelper))
                {
                    phoneName += $" {PhoneController.PositionToGridCoord(position)}";
                }

                return phoneName;
            }

            private static string GetUnderwaterLabPhoneName(DungeonBaseLink link, Vector3 position)
            {
                var floors = link.Dungeon.Floors;
                var gridCoordinate = PhoneController.PositionToGridCoord(position);

                for (int i = 0; i < floors.Count; i++)
                {
                    if (floors[i].Links.Contains(link))
                    {
                        var roomLevel = $"L{1 + i}";
                        var roomType = link.Type.ToString();
                        var roomNumber = 1 + floors[i].Links.IndexOf(link);
                        var roomName = $"{roomLevel} {roomType} {roomNumber}";

                        return $"{UnderwaterLab} {gridCoordinate} {roomName}";
                    }
                }

                return $"{UnderwaterLab} {gridCoordinate}";
            }

            private static string GetDynamicMonumentPhoneName(DynamicMonument monument, Telephone phone)
            {
                if (monument.RootEntity is CargoShip)
                    return $"{CargoShip} {monument.EntityId}";

                return $"{phone.GetDisplayName()} {monument.EntityId}";
            }

            private static bool ShouldAppendCoordinate(string monumentShortName, MonumentHelper monumentHelper)
            {
                if (PolymorphicMonumentVariants.Contains(monumentShortName))
                    return true;

                return !monumentHelper.IsMonumentUnique(monumentShortName);
            }

            private static string SplitCamelCase(string camelCase)
            {
                return SplitCamelCaseRegex.Replace(camelCase, "$1 $2");
            }
        }

        #endregion

        #endregion

        #region Monuments

        private abstract class BaseMonument
        {
            public Component Object { get; }
            public virtual string PrefabName => Object.name;
            public virtual string ShortName => GetShortName(PrefabName);
            public virtual string Alias => null;
            public virtual string UniqueDisplayName => Alias ?? ShortName;
            public virtual string UniqueName => UniqueDisplayName;
            public virtual Vector3 Position => Object.transform.position;
            public virtual Quaternion Rotation => Object.transform.rotation;
            public virtual bool IsValid => Object != null;

            protected BaseMonument(Component component)
            {
                Object = component;
            }

            public virtual Vector3 TransformPoint(Vector3 localPosition)
            {
                return Object.transform.TransformPoint(localPosition);
            }

            public virtual Vector3 InverseTransformPoint(Vector3 worldPosition)
            {
                return Object.transform.InverseTransformPoint(worldPosition);
            }

            public abstract Vector3 ClosestPointOnBounds(Vector3 position);
            public abstract bool IsInBounds(Vector3 position);

            public virtual bool IsEquivalentTo(BaseMonument other)
            {
                return PrefabName == other.PrefabName
                    && Position == other.Position;
            }
        }

        private class MonumentAdapter : BaseMonument
        {
            public override string PrefabName => (string)_monumentInfo["PrefabName"];
            public override string ShortName => (string)_monumentInfo["ShortName"];
            public override string Alias => (string)_monumentInfo["Alias"];
            public override string UniqueDisplayName => Alias ?? ShortName;
            public override Vector3 Position => (Vector3)_monumentInfo["Position"];
            public override Quaternion Rotation => (Quaternion)_monumentInfo["Rotation"];

            private Dictionary<string, object> _monumentInfo;

            public MonumentAdapter(Dictionary<string, object> monumentInfo) : base((MonoBehaviour)monumentInfo["Object"])
            {
                _monumentInfo = monumentInfo;
            }

            public override Vector3 TransformPoint(Vector3 localPosition)
            {
                return ((Func<Vector3, Vector3>)_monumentInfo["TransformPoint"]).Invoke(localPosition);
            }

            public override Vector3 InverseTransformPoint(Vector3 worldPosition)
            {
                return ((Func<Vector3, Vector3>)_monumentInfo["InverseTransformPoint"]).Invoke(worldPosition);
            }

            public override Vector3 ClosestPointOnBounds(Vector3 position)
            {
                return ((Func<Vector3, Vector3>)_monumentInfo["ClosestPointOnBounds"]).Invoke(position);
            }

            public override bool IsInBounds(Vector3 position)
            {
                return ((Func<Vector3, bool>)_monumentInfo["IsInBounds"]).Invoke(position);
            }
        }

        private interface IDynamicMonument {}

        private interface IEntityMonument
        {
            BaseEntity RootEntity { get; }
            NetworkableId EntityId { get; }
            bool IsMobile { get; }
            bool IsValid { get; }
        }

        private class DynamicMonument : BaseMonument, IDynamicMonument, IEntityMonument
        {
            public BaseEntity RootEntity { get; }
            public bool IsMobile { get; }
            public override string PrefabName => RootEntity.PrefabName;
            public override string ShortName => RootEntity.ShortPrefabName;
            public override string UniqueName => PrefabName;
            public override string UniqueDisplayName => ShortName;
            public override bool IsValid => base.IsValid && !RootEntity.IsDestroyed;
            public NetworkableId EntityId { get; }

            protected OBB BoundingBox => RootEntity.WorldSpaceBounds();

            public DynamicMonument(BaseEntity entity, bool isMobile) : base(entity)
            {
                RootEntity = entity;
                // Cache the entity ID in case the entity gets killed, since the ID used in the state file.
                EntityId = entity.net?.ID ?? new NetworkableId();
                IsMobile = isMobile;
            }

            public DynamicMonument(BaseEntity entity) : this(entity, HasRigidBody(entity)) { }

            public override Vector3 ClosestPointOnBounds(Vector3 position)
            {
                return BoundingBox.ClosestPoint(position);
            }

            public override bool IsInBounds(Vector3 position)
            {
                return BoundingBox.Contains(position);
            }

            public override bool IsEquivalentTo(BaseMonument other)
            {
                return other is DynamicMonument otherDynamocMonument
                    && otherDynamocMonument.RootEntity == RootEntity;
            }
        }

        private class CustomMonument : BaseMonument
        {
            public readonly Plugin OwnerPlugin;
            public readonly Component Component;
            public Bounds Bounds;

            private readonly string _monumentName;
            private Vector3 _position;

            public override string PrefabName => _monumentName;
            public override string ShortName => _monumentName;
            public override string UniqueName => _monumentName;
            public override string UniqueDisplayName => _monumentName;
            public override Vector3 Position => _position;

            public OBB BoundingBox => new OBB(Component.transform, Bounds);

            public CustomMonument(Plugin ownerPlugin, Component component, string monumentName, Bounds bounds) : base(component)
            {
                OwnerPlugin = ownerPlugin;
                Component = component;
                _monumentName = monumentName;
                // Cache the position in case the monument gets killed, since the position is used in the state file.
                _position = component.transform.position;
                Bounds = bounds;
            }

            public override Vector3 ClosestPointOnBounds(Vector3 position)
            {
                return BoundingBox.ClosestPoint(position);
            }

            public override bool IsInBounds(Vector3 position)
            {
                return BoundingBox.Contains(position);
            }

            public override bool IsEquivalentTo(BaseMonument other)
            {
                return other is CustomMonument otherCustomMonument
                    && otherCustomMonument.Component == Component;
            }
        }

        private class CustomEntityMonument : CustomMonument, IEntityMonument
        {
            public BaseEntity RootEntity { get; }
            public NetworkableId EntityId { get; }
            public bool IsMobile { get; }

            public CustomEntityMonument(Plugin ownerPlugin, BaseEntity entity, string monumentName, Bounds bounds)
                : base(ownerPlugin, entity, monumentName, bounds)
            {
                RootEntity = entity;
                EntityId = entity.net?.ID ?? new NetworkableId();
                IsMobile = HasRigidBody(entity);
            }
        }

        private class CustomMonumentComponent : FacepunchBehaviour
        {
            public static CustomMonumentComponent AddToMonument(CustomMonumentManager manager, CustomMonument monument)
            {
                var component = monument.Component.gameObject.AddComponent<CustomMonumentComponent>();
                component.Monument = monument;
                component._manager = manager;
                return component;
            }

            public CustomMonument Monument { get; private set; }
            private CustomMonumentManager _manager;

            private void OnDestroy()
            {
                _manager.Unregister(Monument);
            }
        }

        private class CustomMonumentManager
        {
            private readonly MonumentAddons _plugin;
            public readonly List<CustomMonument> MonumentList = new();

            public CustomMonumentManager(MonumentAddons plugin)
            {
                _plugin = plugin;
            }

            public void Register(CustomMonument monument)
            {
                if (FindByComponent(monument.Component) != null)
                    return;

                MonumentList.Add(monument);
            }

            public void Unregister(CustomMonument monument)
            {
                if (!MonumentList.Remove(monument))
                    return;

                _plugin._coroutineManager.StartCoroutine(KillRoutine(
                    _plugin._profileManager.GetEnabledAdaptersForMonument<BaseAdapter>(monument).ToList()));
            }

            public void UnregisterAllForPlugin(Plugin plugin)
            {
                if (MonumentList.Count == 0)
                    return;

                foreach (var monument in MonumentList.ToArray())
                {
                    if (monument.OwnerPlugin.Name != plugin.Name)
                        continue;

                    Unregister(monument);
                }
            }

            public CustomMonument FindByComponent(Component component)
            {
                foreach (var monument in MonumentList)
                {
                    if (monument.Component == component)
                        return monument;
                }

                return null;
            }

            public CustomMonument FindByPosition(Vector3 position)
            {
                foreach (var monument in MonumentList)
                {
                    if (monument.IsInBounds(position))
                        return monument;
                }

                return null;
            }

            public int CountMonumentByName(string name)
            {
                var count = 0;

                foreach (var monument in MonumentList)
                {
                    if (monument.UniqueName != name)
                        continue;

                    count++;
                }

                return count;
            }

            public List<BaseMonument> FindMonumentsByName(string name)
            {
                List<BaseMonument> matchingMonuments = null;

                foreach (var monument in MonumentList)
                {
                    if (monument.UniqueName != name)
                        continue;

                    matchingMonuments ??= new List<BaseMonument>();
                    matchingMonuments.Add(monument);
                }

                return matchingMonuments;
            }

            private IEnumerator KillRoutine(List<BaseAdapter> adapterList)
            {
                foreach (var adapter in adapterList)
                {
                    _plugin.TrackStart();
                    adapter.Kill();
                    _plugin.TrackEnd();
                    yield return adapter.WaitInstruction;
                }
            }
        }

        #endregion

        #region Undo Manager

        private abstract class BaseUndo
        {
            private const float ExpireAfterSeconds = 300;

            public virtual bool IsValid => !IsExpired && ProfileExists;

            protected readonly MonumentAddons _plugin;
            protected readonly ProfileController _profileController;
            protected readonly string _monumentIdentifier;
            private readonly float _undoTime;

            protected Profile Profile => _profileController.Profile;
            private bool IsExpired => _undoTime + ExpireAfterSeconds < UnityEngine.Time.realtimeSinceStartup;
            private bool ProfileExists => _plugin._profileStore.Exists(Profile.Name);

            protected BaseUndo(MonumentAddons plugin, ProfileController profileController, string monumentIdentifier)
            {
                _plugin = plugin;
                _undoTime = UnityEngine.Time.realtimeSinceStartup;
                _profileController = profileController;
                _monumentIdentifier = monumentIdentifier;
            }

            public abstract void Undo(BasePlayer player);
        }

        private class UndoKill : BaseUndo
        {
            protected readonly BaseData _data;

            public UndoKill(MonumentAddons plugin, ProfileController profileController, string monumentIdentifier, BaseData data)
                : base(plugin, profileController, monumentIdentifier)
            {
                _data = data;
            }

            public override void Undo(BasePlayer player)
            {
                Profile.AddData(_monumentIdentifier, _data);
                _plugin._profileStore.Save(Profile);

                if (_profileController.IsEnabled)
                {
                    var matchingMonuments = _plugin.GetMonumentsByIdentifier(_monumentIdentifier);
                    if (matchingMonuments?.Count > 0)
                    {
                        _profileController.SpawnNewData(_data, matchingMonuments);
                    }
                }

                var iPlayer = player.IPlayer;
                _plugin.ReplyToPlayer(iPlayer, LangEntry.UndoKillSuccess, _plugin.GetAddonName(iPlayer, _data), _monumentIdentifier, Profile.Name);
            }
        }

        private class UndoKillSpawnPoint : UndoKill
        {
            private readonly SpawnGroupData _spawnGroupData;
            private readonly SpawnPointData _spawnPointData;

            public UndoKillSpawnPoint(MonumentAddons plugin, ProfileController profileController, string monumentIdentifier, SpawnGroupData spawnGroupData, SpawnPointData spawnPointData)
                : base(plugin, profileController, monumentIdentifier, spawnGroupData)
            {
                _spawnGroupData = spawnGroupData;
                _spawnPointData = spawnPointData;
            }

            public override void Undo(BasePlayer player)
            {
                if (!Profile.HasSpawnGroup(_monumentIdentifier, _spawnGroupData.Id))
                {
                    base.Undo(player);
                    return;
                }

                _spawnGroupData.SpawnPoints.Add(_spawnPointData);
                _plugin._profileStore.Save(Profile);

                if (_profileController.IsEnabled)
                {
                    (_profileController.FindControllerById(_spawnGroupData.Id) as SpawnGroupController)?.CreateSpawnPoint(_spawnPointData);
                }

                var iPlayer = player.IPlayer;
                _plugin.ReplyToPlayer(iPlayer, LangEntry.UndoKillSuccess, _plugin.GetAddonName(iPlayer, _spawnPointData), _monumentIdentifier, Profile.Name);
            }
        }

        private class UndoManager
        {
            private static void CleanStack(Stack<BaseUndo> stack)
            {
                while (stack.TryPeek(out var undoAction) && !undoAction.IsValid)
                {
                    stack.Pop();
                }
            }

            private readonly Dictionary<ulong, Stack<BaseUndo>> _undoStackByPlayer = new Dictionary<ulong, Stack<BaseUndo>>();

            public bool TryUndo(BasePlayer player)
            {
                var undoStack = GetUndoStack(player);
                if (undoStack == null)
                    return false;

                if (!undoStack.TryPop(out var undoAction))
                    return false;

                undoAction.Undo(player);
                return true;
            }

            public void AddUndo(BasePlayer player, BaseUndo undoAction)
            {
                EnsureUndoStack(player).Push(undoAction);
            }

            private Stack<BaseUndo> EnsureUndoStack(BasePlayer player)
            {
                var undoStack = GetUndoStack(player);
                if (undoStack == null)
                {
                    undoStack = new Stack<BaseUndo>();
                    _undoStackByPlayer[player.userID] = undoStack;
                }

                return undoStack;
            }

            private Stack<BaseUndo> GetUndoStack(BasePlayer player)
            {
                if (!_undoStackByPlayer.TryGetValue(player.userID, out var stack))
                    return null;

                CleanStack(stack);
                return stack;
            }
        }

        #endregion

        #region Adapters/Controllers

        #region Addon Component

        private interface IAddonComponent
        {
            TransformAdapter Adapter { get; }
        }

        private class AddonComponent : FacepunchBehaviour, IAddonComponent
        {
            public static AddonComponent AddToComponent(AddonComponentTracker componentTracker, Component hostComponent, TransformAdapter adapter)
            {
                var component = hostComponent.gameObject.AddComponent<AddonComponent>();
                component.Adapter = adapter;
                component._componentTracker = componentTracker;
                component._hostComponent = hostComponent;

                componentTracker.RegisterComponent(hostComponent);

                if (hostComponent is BaseEntity entity && entity.GetParentEntity() is SphereEntity parentSphere)
                {
                    AddonComponent.AddToComponent(componentTracker, parentSphere, adapter);
                }

                return component;
            }

            public static void RemoveFromComponent(Component component)
            {
                DestroyImmediate(component.GetComponent<AddonComponent>());

                if (component is BaseEntity entity && entity.GetParentEntity() is SphereEntity parentSphere)
                {
                    RemoveFromComponent(parentSphere);
                }
            }

            public static AddonComponent GetForComponent(BaseEntity entity)
            {
                return entity.GetComponent<AddonComponent>();
            }

            public TransformAdapter Adapter { get; private set; }
            private Component _hostComponent;
            private AddonComponentTracker _componentTracker;

            private void OnDestroy()
            {
                _componentTracker.UnregisterComponent(_hostComponent);
                Adapter.OnComponentDestroyed(_hostComponent);
            }
        }

        private class AddonComponentTracker
        {
            private static bool IsComponentValid(Component component)
            {
                return component != null
                    && component is not BaseEntity { IsDestroyed: true };
            }

            private HashSet<Component> _trackedComponents = new();

            public void RegisterComponent(Component component)
            {
                _trackedComponents.Add(component);
            }

            public void UnregisterComponent(Component component)
            {
                _trackedComponents.Remove(component);
            }

            public bool IsAddonComponent(Component component)
            {
                return IsComponentValid(component) && _trackedComponents.Contains(component);
            }

            public bool IsAddonComponent<TAdapter, TController>(Component component, out TAdapter adapter, out TController controller)
                where TAdapter : BaseAdapter
                where TController : BaseController
            {
                adapter = null;
                controller = null;

                if (!IsAddonComponent(component))
                    return false;

                var addonComponent = component.GetComponent<IAddonComponent>();
                if (addonComponent == null)
                    return false;

                adapter = addonComponent.Adapter as TAdapter;
                controller = adapter?.Controller as TController;
                return controller != null;
            }

            public bool IsAddonComponent<TController>(Component component, out TController controller)
                where TController : BaseController
            {
                return IsAddonComponent<BaseAdapter, TController>(component, out _, out controller);
            }
        }

        #endregion

        #region Adapter/Controller - Base

        private interface IUpdateableController
        {
            Coroutine StartUpdateRoutine();
        }

        // Represents a single entity, spawn group, or spawn point at a single monument.
        private abstract class BaseAdapter
        {
            public abstract bool IsValid { get; }

            public BaseData Data { get; }
            public BaseController Controller { get; }
            public BaseMonument Monument { get; }

            public MonumentAddons Plugin => Controller.Plugin;
            public ProfileController ProfileController => Controller.ProfileController;
            public Profile Profile => Controller.Profile;

            protected Configuration _config => Plugin._config;
            protected ProfileStateData _profileStateData => Plugin._profileStateData;
            protected IOManager _ioManager => Plugin._ioManager;

            // Subclasses can override this to wait more than one frame for spawn/kill operations.
            public IEnumerator WaitInstruction { get; protected set; }

            protected BaseAdapter(BaseData data, BaseController controller, BaseMonument monument)
            {
                Data = data;
                Controller = controller;
                Monument = monument;
            }

            // Creates all GameObjects/Component that make up the addon.
            public abstract void Spawn();

            // Destroys all GameObjects/Components that make up the addon.
            public abstract void Kill();

            // Called when a component associated with the adapter is destroyed.
            public abstract void OnComponentDestroyed(Component component);

            // Detaches entities that should be saved/persisted across restarts/reloads.
            public virtual void DetachSavedEntities() {}

            // Called when the addon is scheduled to be killed or unregistered.
            public virtual void PreUnload() {}
        }

        // Represents a single entity or spawn point at a single monument.
        private abstract class TransformAdapter : BaseAdapter
        {
            public BaseTransformData TransformData { get; }

            public abstract Component Component { get; }
            public abstract Transform Transform { get; }
            public abstract Vector3 Position { get; }
            public abstract Quaternion Rotation { get; }

            public Vector3 LocalPosition => Monument.InverseTransformPoint(Position);
            public Quaternion LocalRotation => Quaternion.Inverse(Monument.Rotation) * Rotation;

            public Vector3 IntendedPosition
            {
                get
                {
                    var intendedPosition = Monument.TransformPoint(TransformData.Position);

                    if (TransformData.SnapToTerrain)
                        intendedPosition.y = TerrainMeta.HeightMap.GetHeight(intendedPosition);

                    return intendedPosition;
                }
            }

            public Quaternion IntendedRotation => Monument.Rotation * Quaternion.Euler(TransformData.RotationAngles);

            public virtual bool IsAtIntendedPosition => Position == IntendedPosition && Rotation == IntendedRotation;

            protected TransformAdapter(BaseTransformData transformData, BaseController controller, BaseMonument monument) : base(transformData, controller, monument)
            {
                TransformData = transformData;
            }

            public virtual bool TryRecordUpdates(Transform moveTransform = null, Transform rotateTransform = null)
            {
                if (IsAtIntendedPosition)
                    return false;

                TransformData.Position = LocalPosition;
                TransformData.RotationAngles = LocalRotation.eulerAngles;
                TransformData.SnapToTerrain = IsOnTerrain(Position);
                return true;
            }
        }

        // Represents an entity or spawn point across one or more identical monuments.
        private abstract class BaseController
        {
            public ProfileController ProfileController { get; }
            public BaseData Data { get; }
            public List<BaseAdapter> Adapters { get; } = new List<BaseAdapter>();

            public MonumentAddons Plugin => ProfileController.Plugin;
            public Profile Profile => ProfileController.Profile;

            private bool _enabled = true;

            protected BaseController(ProfileController profileController, BaseData data)
            {
                ProfileController = profileController;
                Data = data;
            }

            public abstract BaseAdapter CreateAdapter(BaseMonument monument);

            public virtual void OnAdapterSpawned(BaseAdapter adapter) {}

            public virtual void OnAdapterKilled(BaseAdapter adapter)
            {
                Adapters.Remove(adapter);

                if (Adapters.Count == 0)
                {
                    ProfileController.OnControllerKilled(this);
                }
            }

            public virtual BaseAdapter SpawnAtMonument(BaseMonument monument)
            {
                var adapter = CreateAdapter(monument);
                Adapters.Add(adapter);
                adapter.Spawn();
                OnAdapterSpawned(adapter);
                return adapter;
            }

            public virtual IEnumerator SpawnAtMonumentsRoutine(IEnumerable<BaseMonument> monumentList)
            {
                foreach (var monument in monumentList)
                {
                    if (!_enabled)
                        yield break;

                    if (!monument.IsValid)
                        continue;

                    BaseAdapter adapter = null;
                    Plugin.TrackStart();
                    try
                    {
                        adapter = SpawnAtMonument(monument);
                    }
                    catch (Exception ex)
                    {
                        LogError($"Caught exception when spawning addon {Data.Id}.\n{ex}");
                    }

                    Plugin.TrackEnd();
                    yield return adapter?.WaitInstruction;
                }
            }

            // Subclasses can override this if they need to kill child data (e.g., spawn point data).
            public virtual Coroutine Kill(BaseData data)
            {
                PreUnload();

                Coroutine coroutine = null;

                if (Adapters.Count > 0)
                {
                    coroutine = CoroutineManager.StartGlobalCoroutine(KillRoutine());
                }

                ProfileController.OnControllerKilled(this);
                return coroutine;
            }

            public bool HasAdapterForMonument(BaseMonument monument)
            {
                foreach (var adapter in Adapters)
                {
                    if (adapter.Monument.IsEquivalentTo(monument))
                        return true;
                }

                return false;
            }

            public Coroutine Kill()
            {
                return Kill(Data);
            }

            public void PreUnload()
            {
                // Stop the controller from spawning more adapters.
                _enabled = false;

                for (var i = Adapters.Count - 1; i >= 0; i--)
                {
                    Adapters[i].PreUnload();
                }
            }

            public IEnumerator KillRoutine()
            {
                for (var i = Adapters.Count - 1; i >= 0; i--)
                {
                    Plugin.TrackStart();
                    var adapter = Adapters[i];
                    adapter.Kill();
                    Plugin.TrackEnd();
                    yield return adapter.WaitInstruction;
                }
            }

            public void DetachSavedEntities()
            {
                for (var i = Adapters.Count - 1; i >= 0; i--)
                {
                    Plugin.TrackStart();
                    Adapters[i].DetachSavedEntities();
                    Plugin.TrackEnd();
                }
            }

            public BaseAdapter FindAdapterForMonument(BaseMonument monument)
            {
                foreach (var adapter in Adapters)
                {
                    if (adapter.Monument.IsEquivalentTo(monument))
                        return adapter;
                }

                return null;
            }
        }

        #endregion

        #region Adapter/Controller - Prefab

        private class PrefabAdapter : TransformAdapter
        {
            public GameObject GameObject { get; private set; }
            public PrefabData PrefabData { get; }
            public override Component Component => _transform;
            public override Transform Transform => _transform;
            public override Vector3 Position => Transform.position;
            public override Quaternion Rotation => Transform.rotation;
            public override bool IsValid => GameObject != null;
            private Transform _transform;

            public PrefabAdapter(BaseController controller, PrefabData prefabData, BaseMonument monument)
                : base(prefabData, controller, monument)
            {
                PrefabData = prefabData;
            }

            public override void Spawn()
            {
                GameObject = GameManager.server.CreatePrefab(PrefabData.PrefabName, IntendedPosition, IntendedRotation);
                _transform = GameObject.transform;
                AddonComponent.AddToComponent(Plugin._componentTracker, _transform, this);
            }

            public override void Kill()
            {
                UnityEngine.Object.Destroy(GameObject);
            }

            public void HandleChanges()
            {
                if (IsAtIntendedPosition)
                    return;

                Transform.SetPositionAndRotation(IntendedPosition, IntendedRotation);
            }

            public override void OnComponentDestroyed(Component component)
            {
                Controller.OnAdapterKilled(this);
            }
        }

        private class PrefabController : BaseController, IUpdateableController
        {
            public PrefabData PrefabData { get; }

            public PrefabController(ProfileController profileController, PrefabData prefabData)
                : base(profileController, prefabData)
            {
                PrefabData = prefabData;
            }

            public override BaseAdapter CreateAdapter(BaseMonument monument)
            {
                return new PrefabAdapter(this, PrefabData, monument);
            }

            public Coroutine StartUpdateRoutine()
            {
                return ProfileController.StartCoroutine(UpdateRoutine());
            }

            private IEnumerator UpdateRoutine()
            {
                foreach (var adapter in Adapters.ToList())
                {
                    var prefabAdapter = adapter as PrefabAdapter;
                    if (prefabAdapter is not { IsValid: true })
                        continue;

                    prefabAdapter.HandleChanges();
                    yield return null;
                }
            }
        }

        #endregion

        #region Entity Adapter/Controller

        private class EntityAdapter : TransformAdapter
        {
            private class IOEntityOverrideInfo
            {
                public Dictionary<int, Vector3> Inputs = new Dictionary<int, Vector3>();
                public Dictionary<int, Vector3> Outputs = new Dictionary<int, Vector3>();
            }

            private static readonly Dictionary<string, IOEntityOverrideInfo> IOOverridesByEntity = new Dictionary<string, IOEntityOverrideInfo>
            {
                ["assets/prefabs/io/electric/switches/doormanipulator.prefab"] = new IOEntityOverrideInfo
                {
                    Inputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0, 0.9f, 0),
                        [1] = new Vector3(0, 0.85f, 0),
                    },
                },
                ["assets/prefabs/io/electric/switches/simpleswitch/simpleswitch.prefab"] = new IOEntityOverrideInfo
                {
                    Inputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0, 0.78f, 0.03f),
                    },
                    Outputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0, 1.22f, 0.03f),
                    },
                },
                ["assets/prefabs/io/electric/switches/timerswitch.prefab"] = new IOEntityOverrideInfo
                {
                    Inputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0, 0.795f, 0.03f),
                        [1] = new Vector3(-0.15f, 1.04f, 0.03f),
                    },
                    Outputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0, 1.295f, 0.025f),
                    },
                },
                ["assets/prefabs/io/electric/switches/orswitch.prefab"] = new IOEntityOverrideInfo
                {
                    Inputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(-0.035f, 0.82f, -0.125f),
                        [1] = new Vector3(-0.035f, 0.82f, -0.175f),
                    },
                    Outputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [1] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [2] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [3] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [4] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [5] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [6] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [7] = new Vector3(-0.035f, 1.3f, -0.15f),
                    },
                },
                ["assets/prefabs/io/electric/switches/andswitch.prefab"] = new IOEntityOverrideInfo
                {
                    Inputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(-0.035f, 0.82f, -0.125f),
                        [1] = new Vector3(-0.035f, 0.82f, -0.175f),
                    },
                    Outputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [1] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [2] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [3] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [4] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [5] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [6] = new Vector3(-0.035f, 1.3f, -0.15f),
                        [7] = new Vector3(-0.035f, 1.3f, -0.15f),
                    },
                },
                ["assets/prefabs/io/electric/switches/cardreader.prefab"] = new IOEntityOverrideInfo
                {
                    Inputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(-0.014f, 1.18f, 0.03f),
                    },
                    Outputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(-0.014f, 1.55f, 0.03f),
                    },
                },
                ["assets/prefabs/io/electric/switches/pressbutton/pressbutton.prefab"] = new IOEntityOverrideInfo
                {
                    Inputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0, 1.1f, 0.025f),
                    },
                    Outputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0, 1.38f, 0.025f),
                    },
                },
                ["assets/prefabs/io/kinetic/wheelswitch.prefab"] = new IOEntityOverrideInfo
                {
                    Inputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(-0.1f, 0, 0),
                    },
                    Outputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0.1f, 0, 0),
                    },
                },
                ["assets/content/structures/interactive_garage_door/sliding_blast_door.prefab"] = new IOEntityOverrideInfo
                {
                    Inputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0.1f, 0, 0),
                    },
                    Outputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(-0.1f, 0, 0),
                    },
                },
                ["assets/prefabs/io/electric/switches/fusebox/fusebox.prefab"] = new IOEntityOverrideInfo
                {
                    Inputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0, 1.06f, 0.06f),
                    },
                    Outputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0, 1.565f, 0.06f),
                        [1] = new Vector3(0, 1.565f, 0.06f),
                        [2] = new Vector3(0, 1.565f, 0.06f),
                        [3] = new Vector3(0, 1.565f, 0.06f),
                        [4] = new Vector3(0, 1.565f, 0.06f),
                        [5] = new Vector3(0, 1.565f, 0.06f),
                        [6] = new Vector3(0, 1.565f, 0.06f),
                        [7] = new Vector3(0, 1.565f, 0.06f),
                    },
                },
                ["assets/prefabs/io/electric/generators/generator.static.prefab"] = new IOEntityOverrideInfo
                {
                    Outputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(0, 0.75f, -0.5f),
                        [1] = new Vector3(0, 0.75f, -0.3f),
                        [2] = new Vector3(0, 0.75f, -0.1f),
                        [3] = new Vector3(0, 0.75f, 0.1f),
                        [4] = new Vector3(0, 0.75f, 0.3f),
                        [5] = new Vector3(0, 0.75f, 0.5f),
                    },
                },
                ["assets/prefabs/io/electric/switches/splitter.prefab"] = new IOEntityOverrideInfo
                {
                    Inputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(-0.03f, 1.44f, 0f),
                    },
                    Outputs = new Dictionary<int, Vector3>
                    {
                        [0] = new Vector3(-0.03f, 0.8f,0.108f),
                        [1] = new Vector3(-0.03f, 0.8f, 0),
                        [2] = new Vector3(-0.03f, 0.8f, -0.112f),
                    },
                },
            };

            public BaseEntity Entity { get; private set; }
            public EntityData EntityData { get; }
            public virtual bool IsDestroyed => Entity == null || Entity.IsDestroyed;
            public override Component Component => Entity;
            public override Transform Transform => _transform;
            public override Vector3 Position => Transform.position;
            public override Quaternion Rotation => Transform.rotation;
            public override bool IsValid => !IsDestroyed;

            private Transform _transform;
            private PuzzleResetHandler _puzzleResetHandler;

            private BuildingGrade.Enum IntendedBuildingGrade
            {
                get
                {
                    var buildingBlock = Entity as BuildingBlock;
                    var desiredGrade = EntityData.BuildingBlock?.Grade;

                    return desiredGrade != null && desiredGrade != BuildingGrade.Enum.None
                        ? desiredGrade.Value
                        : buildingBlock.blockDefinition.defaultGrade.gradeBase.type;
                }
            }

            public EntityAdapter(BaseController controller, EntityData entityData, BaseMonument monument)
                : base(entityData, controller, monument)
            {
                EntityData = entityData;
            }

            public override void Spawn()
            {
                var existingEntity = _profileStateData.FindEntity(Profile.Name, Monument, Data.Id);
                if (existingEntity != null)
                {
                    if (existingEntity.PrefabName != EntityData.PrefabName)
                    {
                        existingEntity.Kill();
                    }
                    else
                    {
                        Entity = existingEntity;
                        _transform = Entity.transform;

                        PreEntitySpawn();
                        PostEntitySpawn();
                        HandleChanges();
                        AddonComponent.AddToComponent(Plugin._componentTracker, Entity, this);

                        var vendingMachine = Entity as NPCVendingMachine;
                        if (vendingMachine != null)
                        {
                            Plugin.RefreshVendingProfile(vendingMachine);
                        }

                        var mountable = Entity as BaseMountable;
                        if (mountable != null)
                        {
                            if (Monument is IEntityMonument { IsMobile: true })
                            {
                                mountable.isMobile = true;
                                if (!BaseMountable.FixedUpdateMountables.Contains(mountable))
                                {
                                    BaseMountable.FixedUpdateMountables.Add(mountable);
                                }
                            }
                        }

                        ExposedHooks.OnMonumentEntitySpawned(Entity, Monument.Object, Data.Id);

                        if (!ShouldEnableSaving(Entity))
                        {
                            // If saving is no longer enabled, remove the entity from the data file.
                            // This prevents a bug where a subsequent reload would discover the entity before it is destroyed.
                            _profileStateData.RemoveEntity(Profile.Name, Monument, Data.Id);
                            Plugin._saveProfileStateDebounced.Schedule();
                        }

                        return;
                    }
                }

                Entity = CreateEntity(IntendedPosition, IntendedRotation);
                _transform = Entity.transform;

                PreEntitySpawn();
                Entity.Spawn();
                PostEntitySpawn();
                ExposedHooks.OnMonumentEntitySpawned(Entity, Monument.Object, Data.Id);

                if (ShouldEnableSaving(Entity) && Entity != existingEntity)
                {
                    _profileStateData.AddEntity(Profile.Name, Monument, Data.Id, Entity.net.ID);
                    Plugin._saveProfileStateDebounced.Schedule();
                }
            }

            public override void Kill()
            {
                if (IsDestroyed)
                    return;

                PreEntityKill();
                Entity.Kill();
            }

            public override void OnComponentDestroyed(Component component)
            {
                Plugin.TrackStart();

                // Only consider the adapter destroyed if the main entity was destroyed.
                // For example, the scaled sphere parent may be killed if resized to default scale.
                if (component == Entity)
                {
                    if (_profileStateData.RemoveEntity(Profile.Name, Monument, Data.Id))
                    {
                        Plugin._saveProfileStateDebounced.Schedule();
                    }
                }

                Controller.OnAdapterKilled(this);

                Plugin.TrackEnd();
            }

            public void UpdateScale()
            {
                if (Plugin.TryScaleEntity(Entity, EntityData.Scale))
                {
                    var parentSphere = Entity.GetParentEntity() as SphereEntity;
                    if (parentSphere == null)
                        return;

                    if (Plugin._componentTracker.IsAddonComponent(parentSphere))
                        return;

                    AddonComponent.AddToComponent(Plugin._componentTracker, parentSphere, this);
                }
            }

            public void UpdateSkin()
            {
                if (Entity.skinID == EntityData.Skin
                    || EntityData.Skin == 0 && Entity is NPCVendingMachine)
                    return;

                Entity.skinID = EntityData.Skin;

                if (Entity.IsFullySpawned())
                {
                    Entity.SendNetworkUpdate();
                }
            }

            public override bool TryRecordUpdates(Transform moveTransform = null, Transform rotateTransform = null)
            {
                var hasChanged = base.TryRecordUpdates(moveTransform, rotateTransform);

                var buildingBlock = Entity as BuildingBlock;
                if (buildingBlock != null && buildingBlock.grade != IntendedBuildingGrade)
                {
                    EntityData.BuildingBlock ??= new BuildingBlockInfo();
                    EntityData.BuildingBlock.Grade = buildingBlock.grade;
                    hasChanged = true;
                }

                if (hasChanged)
                {
                    var singleEntityController = Controller as EntityController;
                    singleEntityController.StartUpdateRoutine();
                    Plugin._profileStore.Save(singleEntityController.Profile);
                }

                return hasChanged;
            }

            public virtual void HandleChanges()
            {
                DisableFlags();
                UpdatePosition();
                UpdateSkin();
                UpdateScale();
                UpdateBuildingGrade();
                UpdateSkullName();
                UpdateHuntingTrophy();
                UpdatePuzzle();
                UpdateCardReaderLevel();
                UpdateIOConnections();
                MaybeProvidePower();
                EnableFlags();
            }

            public void UpdateSkullName()
            {
                var skullName = EntityData.SkullName;
                if (skullName == null)
                    return;

                var skullTrophy = Entity as SkullTrophyGlobal;
                if (skullTrophy == null)
                    return;

                if (skullTrophy.inventory == null)
                    return;

                if (skullTrophy.inventory.itemList.Count == 1)
                {
                    var item = skullTrophy.inventory.itemList[0];
                    item.RemoveFromContainer();
                    item.Remove();
                }

                var skullItem = ItemManager.CreateByPartialName("skull.human");
                skullItem.name = HumanBodyResourceDispenser.CreateSkullName(skullName);
                if (!skullItem.MoveToContainer(skullTrophy.inventory))
                {
                    skullItem.Remove();
                }

                // Setting flag here so vanilla functionality is preserved for trophies without name set
                skullTrophy.SetFlag(BaseEntity.Flags.Busy, true);
            }

            public void UpdateHuntingTrophy()
            {
                var headData = EntityData.HeadData;
                if (headData == null)
                    return;

                var huntingTrophy = Entity as HuntingTrophy;
                if (huntingTrophy == null)
                    return;

                headData.ApplyToHuntingTrophy(huntingTrophy);
                huntingTrophy.SendNetworkUpdate();

                // Setting flag here so vanilla functionality is preserved for trophies without head set
                huntingTrophy.SetFlag(BaseEntity.Flags.Busy, true);
            }

            public void UpdateIOConnections()
            {
                var ioEntityData = EntityData.IOEntityData;
                if (ioEntityData == null)
                    return;

                var ioEntity = Entity as IOEntity;
                if (ioEntity == null)
                    return;

                var hasChanged = false;

                for (var outputSlotIndex = 0; outputSlotIndex < ioEntity.outputs.Length; outputSlotIndex++)
                {
                    var sourceSlot = ioEntity.outputs[outputSlotIndex];
                    var destinationEntity = sourceSlot.connectedTo.Get();
                    var destinationSlot = destinationEntity?.inputs.ElementAtOrDefault(sourceSlot.connectedToSlot);

                    var connectionData = ioEntityData.FindConnection(outputSlotIndex);
                    if (connectionData != null)
                    {
                        var intendedDestinationEntity = Controller.ProfileController.FindEntity<IOEntity>(connectionData.ConnectedToId, Monument);
                        var intendedDestinationSlot = intendedDestinationEntity?.inputs.ElementAtOrDefault(connectionData.ConnectedToSlot);

                        if (destinationSlot != intendedDestinationSlot)
                        {
                            // Existing destination entity or slot is incorrect.
                            if (destinationSlot != null)
                            {
                                ClearIOSlot(destinationEntity, destinationSlot);
                            }

                            destinationEntity = intendedDestinationEntity;
                            destinationSlot = intendedDestinationSlot;

                            sourceSlot.Clear();
                            hasChanged = true;
                        }

                        if (destinationEntity == null)
                        {
                            // The destination entity cannot be found. Maybe it was destroyed.
                            continue;
                        }

                        if (destinationSlot == null)
                        {
                            // The destination slot cannot be found. Maybe the index was out of range (bad data).
                            continue;
                        }

                        SetupIOSlot(sourceSlot, destinationEntity, connectionData.ConnectedToSlot, connectionData.Color);

                        if (connectionData.ShowWire)
                        {
                            if (sourceSlot.type != IOType.Kinetic && destinationSlot.type != IOType.Kinetic)
                            {
                                var numPoints = connectionData.Points?.Length ?? 0;

                                sourceSlot.linePoints = new Vector3[numPoints + 2];
                                sourceSlot.linePoints[0] = sourceSlot.handlePosition;
                                sourceSlot.linePoints[numPoints + 1] = Transform.InverseTransformPoint(destinationEntity.transform.TransformPoint(destinationSlot.handlePosition));

                                for (var pointIndex = 0; pointIndex < numPoints; pointIndex++)
                                {
                                    sourceSlot.linePoints[pointIndex + 1] = Transform.InverseTransformPoint(Monument.TransformPoint(connectionData.Points[pointIndex]));
                                }
                            }
                        }
                        else
                        {
                            sourceSlot.linePoints = Array.Empty<Vector3>();
                        }

                        SetupIOSlot(destinationSlot, ioEntity, connectionData.Slot, connectionData.Color);
                        destinationEntity.SendNetworkUpdate();

                        continue;
                    }

                    if (destinationSlot != null)
                    {
                        // No connection data saved, so clear existing connection.
                        ClearIOSlot(destinationEntity, destinationSlot);

                        sourceSlot.Clear();
                        hasChanged = true;
                    }
                }

                if (hasChanged)
                {
                    ioEntity.MarkDirtyForceUpdateOutputs();
                    ioEntity.SendNetworkUpdate();
                    ioEntity.SendChangedToRoot(forceUpdate: true);
                }
            }

            public void MaybeProvidePower()
            {
                var ioEntity = Entity as IOEntity;
                if ((object)ioEntity == null)
                    return;

                _ioManager.MaybeProvidePower(ioEntity);
            }

            public override void PreUnload()
            {
                if (_puzzleResetHandler != null)
                {
                    _puzzleResetHandler.Destroy();
                }

                var targetDoor = (Entity as DoorManipulator)?.targetDoor;
                if (targetDoor != null)
                {
                    targetDoor.SetFlag(BaseEntity.Flags.Locked, false);
                }
            }

            public void HandlePuzzleReset()
            {
                var spawnGroupIdList = EntityData.Puzzle?.SpawnGroupIds;
                if (spawnGroupIdList == null)
                    return;

                foreach (var spawnGroupId in spawnGroupIdList)
                {
                    (ProfileController.FindAdapter(spawnGroupId, Monument) as SpawnGroupAdapter)?.SpawnGroup.OnPuzzleReset();
                }
            }

            protected virtual void PreEntitySpawn()
            {
                UpdateSkin();
                UpdateCardReaderLevel();

                EntitySetupUtils.PreSpawnShared(Entity);

                var buildingBlock = Entity as BuildingBlock;
                if (buildingBlock != null)
                {
                    buildingBlock.blockDefinition = PrefabAttribute.server.Find<Construction>(buildingBlock.prefabID);
                    if (buildingBlock.blockDefinition != null)
                    {
                        buildingBlock.SetGrade(IntendedBuildingGrade);

                        var maxHealth = buildingBlock.currentGrade.maxHealth;
                        buildingBlock.InitializeHealth(maxHealth, maxHealth);
                        buildingBlock.ResetLifeStateOnSpawn = false;
                    }
                }

                var ioEntity = Entity as IOEntity;
                if (ioEntity != null)
                {
                    UpdateIOEntitySlotPositions(ioEntity);
                }
            }

            protected virtual void PostEntitySpawn()
            {
                EntitySetupUtils.PostSpawnShared(Plugin, Entity, ShouldEnableSaving(Entity));

                UpdatePuzzle();
                DisableFlags();

                // NPCVendingMachine needs its skin updated after spawn because vanilla sets it to 861142659.
                UpdateSkin();

                var computerStation = Entity as ComputerStation;
                if (computerStation != null && computerStation.isStatic)
                {
                    var computerStation2 = computerStation;
                    computerStation.CancelInvoke(computerStation.GatherStaticCameras);
                    computerStation.Invoke(() =>
                    {
                        Plugin.TrackStart();
                        GatherStaticCameras(computerStation2);
                        Plugin.TrackEnd();
                    }, 1);
                }

                var paddlingPool = Entity as PaddlingPool;
                if (paddlingPool != null)
                {
                    paddlingPool.inventory.AddItem(Plugin._waterDefinition, paddlingPool.inventory.maxStackSize);

                    // Disallow adding or removing water.
                    paddlingPool.SetFlag(BaseEntity.Flags.Busy, true);
                }

                var vehicleSpawner = Entity as VehicleSpawner;
                if (vehicleSpawner != null)
                {
                    var vehicleSpawner2 = vehicleSpawner;
                    vehicleSpawner.Invoke(() =>
                    {
                        Plugin.TrackStart();
                        EntityUtils.ConnectNearbyVehicleVendor(vehicleSpawner2);
                        Plugin.TrackEnd();
                    }, 1);
                }

                var vehicleVendor = Entity as VehicleVendor;
                if (vehicleVendor != null)
                {
                    // Use a slightly longer delay than the vendor check since this can short-circuit as an optimization.
                    var vehicleVendor2 = vehicleVendor;
                    vehicleVendor.Invoke(() =>
                    {
                        Plugin.TrackStart();
                        EntityUtils.ConnectNearbyVehicleSpawner(vehicleVendor2);
                        Plugin.TrackEnd();
                    }, 2);
                }

                var candle = Entity as Candle;
                if (candle != null)
                {
                    candle.SetFlag(BaseEntity.Flags.On, true);
                    candle.CancelInvoke(candle.Burn);

                    // Disallow extinguishing.
                    candle.SetFlag(BaseEntity.Flags.Busy, true);
                }

                var fogMachine = Entity as FogMachine;
                if (fogMachine != null)
                {
                    var fogMachine2 = fogMachine;
                    fogMachine.SetFlag(BaseEntity.Flags.On, true);
                    fogMachine.InvokeRepeating(() =>
                    {
                        fogMachine2.SetFlag(FogMachine.Emitting, true);
                        fogMachine2.Invoke(fogMachine2.EnableFogField, 1f);
                        fogMachine2.Invoke(fogMachine2.DisableNozzle, fogMachine2.nozzleBlastDuration);
                        fogMachine2.Invoke(fogMachine2.FinishFogging, fogMachine2.fogLength);
                    },
                    UnityEngine.Random.Range(0f, 5f),
                    fogMachine.fogLength - 1);

                    // Disallow interaction.
                    fogMachine.SetFlag(BaseEntity.Flags.Busy, true);
                }

                var oven = Entity as BaseOven;
                if (oven != null)
                {
                    // Lanterns
                    if (oven is BaseFuelLightSource)
                    {
                        oven.SetFlag(BaseEntity.Flags.On, true);
                        oven.SetFlag(BaseEntity.Flags.Busy, true);
                    }

                    // jackolantern.angry or jackolantern.happy
                    else if (oven.prefabID == 1889323056 || oven.prefabID == 630866573)
                    {
                        oven.SetFlag(BaseEntity.Flags.On, true);
                        oven.SetFlag(BaseEntity.Flags.Busy, true);
                    }
                }

                var spooker = Entity as SpookySpeaker;
                if (spooker != null)
                {
                    spooker.SetFlag(BaseEntity.Flags.On, true);
                    spooker.InvokeRandomized(
                        spooker.SendPlaySound,
                        spooker.soundSpacing,
                        spooker.soundSpacing,
                        spooker.soundSpacingRand);

                    spooker.SetFlag(BaseEntity.Flags.Busy, true);
                }

                var doorManipulator = Entity as DoorManipulator;
                if (doorManipulator != null)
                {
                    if (doorManipulator.targetDoor != null)
                    {
                        doorManipulator.targetDoor.SetFlag(BaseEntity.Flags.Locked, true);
                    }
                    else
                    {
                        var doorManipulator2 = doorManipulator;
                        doorManipulator.Invoke(() =>
                        {
                            Plugin.TrackStart();
                            EntityUtils.ConnectNearbyDoor(doorManipulator2);
                            Plugin.TrackEnd();
                        }, 1);
                    }
                }

                var spray = Entity as SprayCanSpray;
                if (spray != null)
                {
                    spray.CancelInvoke(spray.RainCheck);
                    #if !CARBON
                    spray.splashThreshold = int.MaxValue;
                    #endif
                }

                var telephone = Entity as Telephone;
                if (telephone != null && telephone.prefabID == 1009655496)
                {
                    PhoneUtils.NameTelephone(telephone, Monument, Position, Plugin._monumentHelper);
                }

                var microphoneStand = Entity as MicrophoneStand;
                if ((object)microphoneStand != null)
                {
                    var microphoneStand2 = microphoneStand;
                    microphoneStand.Invoke(() =>
                    {
                        Plugin.TrackStart();
                        microphoneStand2.PostMapEntitySpawn();
                        Plugin.TrackEnd();
                    }, 1);
                }

                var storageContainer = Entity as StorageContainer;
                if ((object)storageContainer != null)
                {
                    storageContainer.isLockable = false;
                    storageContainer.isMonitorable = false;
                }

                var christmasTree = Entity as ChristmasTree;
                if ((object)christmasTree != null)
                {
                    foreach (var itemShortName in _config.XmasTreeDecorations)
                    {
                        var item = ItemManager.CreateByName(itemShortName);
                        if (item == null)
                            continue;

                        if (!item.MoveToContainer(christmasTree.inventory))
                        {
                            item.Remove();
                        }
                    }

                    christmasTree.inventory.SetLocked(true);
                }

                if (EntityData.Scale != 1 || Entity.GetParentEntity() is SphereEntity)
                {
                    UpdateScale();
                }

                var skullTrophy = Entity as SkullTrophyGlobal;
                if (skullTrophy != null)
                {
                    UpdateSkullName();
                }

                var huntingTrophy = Entity as HuntingTrophy;
                if (huntingTrophy != null)
                {
                    UpdateHuntingTrophy();
                }

                EnableFlags();
            }

            protected virtual void PreEntityKill() {}

            public override void DetachSavedEntities()
            {
                if (IsDestroyed)
                    return;

                // Only unregister the adapter if it has saving enabled.
                if (!ShouldEnableSaving(Entity))
                    return;

                // Don't unregister the entity if the profile no longer declares it.
                // Entity should be killed along with the adapter.
                if (!Profile.HasEntity(Monument.UniqueName, EntityData))
                    return;

                // Don't unregister the entity if it's not tracked in the profile state.
                // Entity should be killed along with the adapter.
                if (!_profileStateData.HasEntity(Profile.Name, Monument, Data.Id, Entity.net.ID))
                    return;

                // Unregister the adapter to prevent the entity from being killed when the adapter is killed.
                // The primary use case is to persist the entity while the plugin is unloaded.
                AddonComponent.RemoveFromComponent(Entity);
            }

            protected BaseEntity CreateEntity(Vector3 position, Quaternion rotation)
            {
                var entity = GameManager.server.CreateEntity(EntityData.PrefabName, position, rotation);
                if (entity == null)
                    return null;

                EnableSavingRecursive(entity, enableSaving: ShouldEnableSaving(entity));

                if (Monument is IEntityMonument entityMonument)
                {
                    entity.SetParent(entityMonument.RootEntity, worldPositionStays: true);

                    if (entityMonument.IsMobile)
                    {
                        var mountable = entity as BaseMountable;
                        if (mountable != null)
                        {
                            // Setting isMobile prior to spawn will automatically update the position of mounted players.
                            mountable.isMobile = true;
                        }
                    }
                }

                AddonComponent.AddToComponent(Plugin._componentTracker, entity, this);

                return entity;
            }

            private bool ShouldEnableSaving(BaseEntity entity)
            {
                return _config.EntitySaveSettings.ShouldEnableSaving(entity);
            }

            private void UpdatePosition()
            {
                if (IsAtIntendedPosition)
                    return;

                var entityToMove = GetEntityToMove();
                var entityToRotate = Entity;

                entityToMove.transform.position = IntendedPosition;

                var intendedRotation = IntendedRotation;
                entityToRotate.transform.rotation = intendedRotation;
                if (entityToRotate is BasePlayer playerToRotate)
                {
                    playerToRotate.viewAngles = intendedRotation.eulerAngles;

                    if (playerToRotate is NPCShopKeeper shopKeeper)
                    {
                        shopKeeper.initialFacingDir = intendedRotation * Vector3.forward;
                    }
                }

                BroadcastEntityTransformChange(entityToMove);

                if (entityToRotate != entityToMove)
                {
                    BroadcastEntityTransformChange(entityToRotate);
                }
            }

            private List<CCTV_RC> GetNearbyStaticCameras()
            {
                if (Monument is IEntityMonument { IsMobile: true } entityMonument
                    && entityMonument.RootEntity == Entity.GetParentEntity())
                {
                    var cargoCameraList = new List<CCTV_RC>();
                    foreach (var child in entityMonument.RootEntity.children)
                    {
                        var cctv = child as CCTV_RC;
                        if (cctv != null && cctv.isStatic)
                        {
                            cargoCameraList.Add(cctv);
                        }
                    }

                    return cargoCameraList;
                }

                var entityList = new List<BaseEntity>();
                Vis.Entities(Entity.transform.position, 100, entityList, Rust.Layers.Mask.Deployed, QueryTriggerInteraction.Ignore);
                if (entityList.Count == 0)
                    return null;

                var cameraList = new List<CCTV_RC>();
                foreach (var entity in entityList)
                {
                    var cctv = entity as CCTV_RC;
                    if (cctv != null && !cctv.IsDestroyed && cctv.isStatic)
                    {
                        cameraList.Add(cctv);
                    }
                }

                return cameraList;
            }

            private void GatherStaticCameras(ComputerStation computerStation)
            {
                var cameraList = GetNearbyStaticCameras();
                if (cameraList == null)
                    return;

                foreach (var cctv in cameraList)
                {
                    computerStation.ForceAddBookmark(cctv.rcIdentifier);
                }
            }

            private BaseEntity GetEntityToMove()
            {
                if (EntityData.Scale != 1 && Plugin.GetEntityScale(Entity) != 1)
                {
                    var parentSphere = Entity.GetParentEntity() as SphereEntity;
                    if (parentSphere != null)
                        return parentSphere;
                }

                return Entity;
            }

            private void UpdateBuildingGrade()
            {
                var buildingBlock = Entity as BuildingBlock;
                if (buildingBlock == null)
                    return;

                var intendedBuildingGrade = IntendedBuildingGrade;
                if (buildingBlock.grade == intendedBuildingGrade)
                    return;

                buildingBlock.SetGrade(intendedBuildingGrade);
                buildingBlock.SetHealthToMax();
                buildingBlock.SendNetworkUpdate();
                buildingBlock.baseProtection = Plugin._immortalProtection;
            }

            private void UpdatePuzzle()
            {
                if (EntityData.Puzzle == null)
                    return;

                var puzzleReset = (Entity as IOEntity)?.GetComponent<PuzzleReset>();
                if (puzzleReset == null)
                    return;

                puzzleReset.playersBlockReset = EntityData.Puzzle.PlayersBlockReset;
                puzzleReset.playerDetectionRadius = EntityData.Puzzle.PlayerDetectionRadius;
                puzzleReset.timeBetweenResets = EntityData.Puzzle.SecondsBetweenResets;

                if (EntityData.Puzzle.SpawnGroupIds?.Count > 0)
                {
                    if (_puzzleResetHandler == null)
                    {
                        _puzzleResetHandler = PuzzleResetHandler.Create(this, puzzleReset);
                    }
                }
            }

            private void UpdateCardReaderLevel()
            {
                var cardReader = Entity as CardReader;
                if ((object)cardReader == null)
                    return;

                var accessLevel = EntityData.CardReaderLevel;
                if (EntityData.CardReaderLevel == 0 || accessLevel == cardReader.accessLevel)
                    return;

                cardReader.accessLevel = accessLevel;
                cardReader.SetFlag(cardReader.AccessLevel1, accessLevel == 1);
                cardReader.SetFlag(cardReader.AccessLevel2, accessLevel == 2);
                cardReader.SetFlag(cardReader.AccessLevel3, accessLevel == 3);
            }

            private void DisableFlags()
            {
                Entity.SetFlag(EntityData.DisabledFlags, false);
            }

            private void EnableFlags()
            {
                Entity.SetFlag(EntityData.EnabledFlags, true);
            }

            private void UpdateIOEntitySlotPositions(IOEntity ioEntity)
            {
                if (!IOOverridesByEntity.TryGetValue(ioEntity.PrefabName, out var overrideInfo))
                    return;

                for (var i = 0; i < ioEntity.inputs.Length; i++)
                {
                    if (overrideInfo.Inputs.TryGetValue(i, out var overridePosition))
                    {
                        ioEntity.inputs[i].handlePosition = overridePosition;
                    }
                }

                for (var i = 0; i < ioEntity.outputs.Length; i++)
                {
                    if (overrideInfo.Outputs.TryGetValue(i, out var overridePosition))
                    {
                        ioEntity.outputs[i].handlePosition = overridePosition;
                    }
                }
            }

            private void SetupIOSlot(IOSlot slot, IOEntity otherEntity, int otherSlotIndex, WireColour color)
            {
                slot.connectedTo.Set(otherEntity);
                slot.connectedToSlot = otherSlotIndex;
                slot.wireColour = color;
                slot.connectedTo.Init();
            }

            private void ClearIOSlot(IOEntity ioEntity, IOSlot slot)
            {
                slot.Clear();

                ioEntity.MarkDirtyForceUpdateOutputs();
                ioEntity.SendNetworkUpdate();
            }
        }

        private class EntityController : BaseController, IUpdateableController
        {
            public EntityData EntityData { get; }

            private Dictionary<string, object> _vendingDataProvider;

            public EntityController(ProfileController profileController, EntityData entityData)
                : base(profileController, entityData)
            {
                EntityData = entityData;
            }

            public override BaseAdapter CreateAdapter(BaseMonument monument)
            {
                return new EntityAdapter(this, EntityData, monument);
            }

            public override void OnAdapterSpawned(BaseAdapter adapter)
            {
                base.OnAdapterSpawned(adapter);
                Plugin._adapterListenerManager.OnAdapterSpawned(adapter);
            }

            public override void OnAdapterKilled(BaseAdapter adapter)
            {
                base.OnAdapterKilled(adapter);
                Plugin._adapterListenerManager.OnAdapterKilled(adapter);
            }

            public Coroutine StartUpdateRoutine()
            {
                return ProfileController.StartCoroutine(HandleChangesRoutine());
            }

            public Dictionary<string, object> GetVendingDataProvider()
            {
                return _vendingDataProvider ??= new Dictionary<string, object>
                {
                    ["Plugin"] = Plugin,
                    ["GetData"] = new Func<JObject>(() => EntityData.VendingProfile as JObject),
                    ["SaveData"] = new Action<JObject>(vendingProfile =>
                    {
                        if (!Plugin._isLoaded)
                            return;

                        EntityData.VendingProfile = vendingProfile;
                        Plugin._profileStore.Save(Profile);
                    }),
                    ["GetSkin"] = new Func<ulong>(() => EntityData.Skin),
                    ["SetSkin"] = new Action<ulong>(skinId => EntityData.Skin = skinId),
                };
            }

            private IEnumerator HandleChangesRoutine()
            {
                foreach (var adapter in Adapters.ToList())
                {
                    var singleAdapter = adapter as EntityAdapter;
                    if (singleAdapter.IsDestroyed)
                        continue;

                    singleAdapter.HandleChanges();
                    yield return null;
                }
            }
        }

        #endregion

        #region Entity Adapter/Controller - Signs

        private class SignAdapter : EntityAdapter
        {
            public SignAdapter(BaseController controller, EntityData entityData, BaseMonument monument)
                : base(controller, entityData, monument) {}

            public override void PreUnload()
            {
                if (!Entity.IsDestroyed && !Entity.enableSaving)
                {
                    // Delete sign files immediately, since the entities may not be explicitly killed on server shutdown.
                    DeleteSignFiles();
                }

                base.PreUnload();
            }

            public uint[] GetTextureIds()
            {
                return (Entity as ISignage)?.GetTextureCRCs();
            }

            public void SetTextureIds(uint[] textureIds)
            {
                var sign = Entity as ISignage;
                if (textureIds == null || textureIds.Equals(sign.GetTextureCRCs()))
                    return;

                sign.SetTextureCRCs(textureIds);
            }

            public void SkinSign()
            {
                if (EntityData.SignArtistImages == null)
                    return;

                Plugin.SkinSign(Entity as ISignage, EntityData.SignArtistImages);
            }

            protected override void PreEntitySpawn()
            {
                base.PreEntitySpawn();

                (Entity as Signage)?.EnsureInitialized();

                var carvablePumpkin = Entity as CarvablePumpkin;
                if (carvablePumpkin != null)
                {
                    carvablePumpkin.EnsureInitialized();
                    carvablePumpkin.SetFlag(BaseEntity.Flags.On, true);
                }

                Entity.SetFlag(BaseEntity.Flags.Locked, true);
            }

            protected override void PreEntityKill()
            {
                // Sign files are removed by vanilla only during Die(), so we have to explicitly delete them.
                DeleteSignFiles();
            }

            private void DeleteSignFiles()
            {
                FileStorage.server.RemoveAllByEntity(Entity.net.ID);

                var signage = Entity as Signage;
                if (signage != null)
                {
                    if (signage.textureIDs != null)
                    {
                        Array.Clear(signage.textureIDs, 0, signage.textureIDs.Length);
                    }

                    return;
                }

                var photoFrame = Entity as PhotoFrame;
                if (photoFrame != null)
                {
                    photoFrame._overlayTextureCrc = 0u;
                    return;
                }

                var carvablePumpkin = Entity as CarvablePumpkin;
                if (carvablePumpkin != null)
                {
                    if (carvablePumpkin.textureIDs != null)
                    {
                        Array.Clear(carvablePumpkin.textureIDs, 0, carvablePumpkin.textureIDs.Length);
                    }

                    return;
                }
            }
        }

        private class SignController : EntityController
        {
            public SignController(ProfileController profileController, EntityData data)
                : base(profileController, data) {}

            // Sign artist will only be called for the primary adapter.
            // Texture ids are copied to the others.
            protected SignAdapter _primaryAdapter;

            public override BaseAdapter CreateAdapter(BaseMonument monument)
            {
                return new SignAdapter(this, EntityData, monument);
            }

            public override void OnAdapterSpawned(BaseAdapter adapter)
            {
                base.OnAdapterSpawned(adapter);

                var signEntityAdapter = adapter as SignAdapter;

                if (_primaryAdapter != null)
                {
                    var textureIds = _primaryAdapter.GetTextureIds();
                    if (textureIds != null)
                    {
                        signEntityAdapter.SetTextureIds(textureIds);
                    }
                }
                else
                {
                    _primaryAdapter = signEntityAdapter;
                    _primaryAdapter.SkinSign();
                }
            }

            public override void OnAdapterKilled(BaseAdapter adapter)
            {
                base.OnAdapterKilled(adapter);

                if (adapter == _primaryAdapter)
                {
                    _primaryAdapter = Adapters.FirstOrDefault() as SignAdapter;
                }
            }

            public void UpdateSign(uint[] textureIds)
            {
                foreach (var adapter in Adapters)
                {
                    (adapter as SignAdapter).SetTextureIds(textureIds);
                }
            }
        }

        #endregion

        #region Entity Adapter/Controller - CCTV

        private class CCTVEntityAdapter : EntityAdapter
        {
            private int _idSuffix;
            private string _cachedIdentifier;
            private string _savedIdentifier => EntityData.CCTV?.RCIdentifier;

            public CCTVEntityAdapter(BaseController controller, EntityData entityData, BaseMonument monument, int idSuffix) : base(controller, entityData, monument)
            {
                _idSuffix = idSuffix;
            }

            // Ensure the RC identifiers are freed up as soon as possible to avoid conflicts when reloading.
            public override void PreUnload()
            {
                SetIdentifier(string.Empty);

                base.PreUnload();
            }

            protected override void PreEntitySpawn()
            {
                base.PreEntitySpawn();

                UpdateIdentifier();
                UpdateDirection();
            }

            protected override void PostEntitySpawn()
            {
                base.PostEntitySpawn();

                if (_cachedIdentifier != null)
                {
                    var computerStationList = GetNearbyStaticComputerStations();
                    if (computerStationList != null)
                    {
                        foreach (var computerStation in computerStationList)
                        {
                            computerStation.ForceAddBookmark(_cachedIdentifier);
                        }
                    }
                }
            }

            public override void OnComponentDestroyed(Component component)
            {
                base.OnComponentDestroyed(component);

                if (component != Entity)
                    return;

                Plugin.TrackStart();

                if (_cachedIdentifier != null)
                {
                    var computerStationList = GetNearbyStaticComputerStations();
                    if (computerStationList != null)
                    {
                        foreach (var computerStation in computerStationList)
                        {
                            computerStation.controlBookmarks.Remove(_cachedIdentifier);
                        }
                    }
                }

                Plugin.TrackEnd();
            }

            public override void HandleChanges()
            {
                base.HandleChanges();

                UpdateIdentifier();
                UpdateDirection();
            }

            public void UpdateIdentifier()
            {
                if (_savedIdentifier == null)
                {
                    SetIdentifier(string.Empty);
                    return;
                }

                var newIdentifier = $"{_savedIdentifier}{_idSuffix}";
                if (newIdentifier == _cachedIdentifier)
                    return;

                if (RemoteControlEntity.IDInUse(newIdentifier))
                {
                    LogWarning($"CCTV ID in use: {newIdentifier}");
                    return;
                }

                SetIdentifier(newIdentifier);

                if (Entity.IsFullySpawned())
                {
                    Entity.SendNetworkUpdate();

                    var computerStationList = GetNearbyStaticComputerStations();
                    if (computerStationList != null)
                    {
                        foreach (var computerStation in computerStationList)
                        {
                            if (_cachedIdentifier != null)
                                computerStation.controlBookmarks.Remove(_cachedIdentifier);

                            computerStation.ForceAddBookmark(newIdentifier);
                        }
                    }
                }

                _cachedIdentifier = newIdentifier;
            }

            public void UpdateDirection()
            {
                var cctvInfo = EntityData.CCTV;
                if (cctvInfo == null)
                    return;

                var cctv = Entity as CCTV_RC;
                cctv.pitchAmount = cctvInfo.Pitch;
                cctv.yawAmount = cctvInfo.Yaw;

                cctv.pitchAmount = Mathf.Clamp(cctv.pitchAmount, cctv.pitchClamp.x, cctv.pitchClamp.y);
                cctv.yawAmount = Mathf.Clamp(cctv.yawAmount, cctv.yawClamp.x, cctv.yawClamp.y);

                cctv.pitch.transform.localRotation = Quaternion.Euler(cctv.pitchAmount, 0f, 0f);
                cctv.yaw.transform.localRotation = Quaternion.Euler(0f, cctv.yawAmount, 0f);

                if (Entity.IsFullySpawned())
                {
                    Entity.SendNetworkUpdate();
                }
            }

            public string GetIdentifier()
            {
                return (Entity as CCTV_RC).rcIdentifier;
            }

            private void SetIdentifier(string id)
            {
                (Entity as CCTV_RC).rcIdentifier = id;
            }

            private List<ComputerStation> GetNearbyStaticComputerStations()
            {
                if (Monument is IEntityMonument { IsMobile: true } entityMonument
                    && entityMonument.RootEntity == Entity.GetParentEntity())
                {
                    var cargoComputerStationList = new List<ComputerStation>();
                    foreach (var child in entityMonument.RootEntity.children)
                    {
                        var computerStation = child as ComputerStation;
                        if (computerStation != null && computerStation.isStatic)
                        {
                            cargoComputerStationList.Add(computerStation);
                        }
                    }

                    return cargoComputerStationList;
                }

                var entityList = new List<BaseEntity>();
                Vis.Entities(Entity.transform.position, 100, entityList, Rust.Layers.Mask.Deployed, QueryTriggerInteraction.Ignore);
                if (entityList.Count == 0)
                    return null;

                var computerStationList = new List<ComputerStation>();
                foreach (var entity in entityList)
                {
                    var computerStation = entity as ComputerStation;
                    if (computerStation != null && !computerStation.IsDestroyed && computerStation.isStatic)
                    {
                        computerStationList.Add(computerStation);
                    }
                }

                return computerStationList;
            }
        }

        private class CCTVController : EntityController
        {
            private int _nextId = 1;

            public CCTVController(ProfileController profileController, EntityData data)
                : base(profileController, data) {}

            public override BaseAdapter CreateAdapter(BaseMonument monument)
            {
                return new CCTVEntityAdapter(this, EntityData, monument, _nextId++);
            }
        }

        #endregion

        #region Entity Listeners

        private abstract class AdapterListenerBase
        {
            public virtual void Init() {}
            public virtual void OnServerInitialized() {}
            public abstract bool InterestedInAdapter(BaseAdapter adapter);
            public abstract void OnAdapterSpawned(BaseAdapter adapter);
            public abstract void OnAdapterKilled(BaseAdapter adapter);

            protected static bool IsEntityAdapter<T>(BaseAdapter adapter)
            {
                if (adapter.Data is not EntityData entityData)
                    return false;

                return FindPrefabBaseEntity(entityData.PrefabName) is T;
            }
        }

        private abstract class DynamicHookListener : AdapterListenerBase
        {
            private MonumentAddons _plugin;
            protected string[] _dynamicHookNames;

            private HashSet<BaseAdapter> _adapters = new HashSet<BaseAdapter>();

            protected DynamicHookListener(MonumentAddons plugin)
            {
                _plugin = plugin;
            }

            public override void Init()
            {
                UnsubscribeHooks();
            }

            public override void OnAdapterSpawned(BaseAdapter adapter)
            {
                _adapters.Add(adapter);

                if (_adapters.Count == 1)
                {
                    SubscribeHooks();
                }
            }

            public override void OnAdapterKilled(BaseAdapter adapter)
            {
                _adapters.Remove(adapter);

                if (_adapters.Count == 0)
                {
                    UnsubscribeHooks();
                }
            }

            private void SubscribeHooks()
            {
                if (_dynamicHookNames == null || !_plugin._isLoaded)
                    return;

                foreach (var hookName in _dynamicHookNames)
                {
                    _plugin.Subscribe(hookName);
                }
            }

            private void UnsubscribeHooks()
            {
                if (_dynamicHookNames == null || !_plugin._isLoaded)
                    return;

                foreach (var hookName in _dynamicHookNames)
                {
                    _plugin.Unsubscribe(hookName);
                }
            }
        }

        private class SignEntityListener : DynamicHookListener
        {
            public SignEntityListener(MonumentAddons plugin) : base(plugin)
            {
                _dynamicHookNames = new[]
                {
                    nameof(CanUpdateSign),
                    nameof(OnSignUpdated),
                    nameof(OnImagePost),
                };
            }

            public override bool InterestedInAdapter(BaseAdapter adapter)
            {
                return IsEntityAdapter<ISignage>(adapter);
            }
        }

        private class BuildingBlockEntityListener : DynamicHookListener
        {
            public BuildingBlockEntityListener(MonumentAddons plugin) : base(plugin)
            {
                _dynamicHookNames = new[]
                {
                    nameof(CanChangeGrade),
                };
            }

            public override bool InterestedInAdapter(BaseAdapter adapter)
            {
                return IsEntityAdapter<BuildingBlock>(adapter);
            }
        }

        private class SprayDecalListener : DynamicHookListener
        {
            public SprayDecalListener(MonumentAddons plugin) : base(plugin)
            {
                _dynamicHookNames = new[]
                {
                    nameof(OnSprayRemove),
                };
            }

            public override bool InterestedInAdapter(BaseAdapter adapter)
            {
                return IsEntityAdapter<SprayCanSpray>(adapter);
            }
        }

        private class AdapterListenerManager
        {
            private AdapterListenerBase[] _listeners;

            public AdapterListenerManager(MonumentAddons plugin)
            {
                _listeners = new AdapterListenerBase[]
                {
                    new SignEntityListener(plugin),
                    new BuildingBlockEntityListener(plugin),
                    new SprayDecalListener(plugin),
                };
            }

            public void Init()
            {
                foreach (var listener in _listeners)
                {
                    listener.Init();
                }
            }

            public void OnServerInitialized()
            {
                foreach (var listener in _listeners)
                {
                    listener.OnServerInitialized();
                }
            }

            public void OnAdapterSpawned(BaseAdapter entityAdapter)
            {
                foreach (var listener in _listeners)
                {
                    if (listener.InterestedInAdapter(entityAdapter))
                    {
                        listener.OnAdapterSpawned(entityAdapter);
                    }
                }
            }

            public void OnAdapterKilled(BaseAdapter entityAdapter)
            {
                foreach (var listener in _listeners)
                {
                    if (listener.InterestedInAdapter(entityAdapter))
                    {
                        listener.OnAdapterKilled(entityAdapter);
                    }
                }
            }
        }

        #endregion

        #region SpawnGroup Adapter/Controller

        private class PuzzleResetHandler : FacepunchBehaviour
        {
            public static PuzzleResetHandler Create(EntityAdapter adapter, PuzzleReset puzzleReset)
            {
                var gameObject = new GameObject();
                gameObject.transform.SetParent(puzzleReset.transform);
                var component = gameObject.AddComponent<PuzzleResetHandler>();
                component._adapter = adapter;
                puzzleReset.resetObjects = new[] { gameObject };
                return component;
            }

            private EntityAdapter _adapter;

            // Called by Rust via Unity SendMessage.
            private void OnPuzzleReset()
            {
                _adapter.HandlePuzzleReset();
            }

            public void Destroy()
            {
                Destroy(gameObject);
            }
        }

        private class SpawnedVehicleComponent : FacepunchBehaviour
        {
            public static void AddToVehicle(MonumentAddons plugin, GameObject gameObject)
            {
                var newComponent = gameObject.AddComponent<SpawnedVehicleComponent>();
                newComponent._plugin = plugin;
            }

            private const float MaxDistanceSquared = 1;

            private MonumentAddons _plugin;
            private Vector3 _originalPosition;
            private Transform _transform;

            public void Awake()
            {
                _transform = transform;
                _originalPosition = _transform.position;

                InvokeRandomized(CheckPositionTracked, 10, 10, 1);
            }

            public void CheckPositionTracked()
            {
                _plugin.TrackStart();
                CheckPosition();
                _plugin.TrackEnd();
            }

            private void CheckPosition()
            {
                if ((_transform.position - _originalPosition).sqrMagnitude < MaxDistanceSquared)
                    return;

                // Vehicle has moved from its spawn point, so unregister it and re-enable saving.
                var vehicle = GetComponent<BaseEntity>();
                if (vehicle != null && !vehicle.IsDestroyed)
                {
                    EnableSavingRecursive(vehicle, true);

                    var workcart = vehicle as TrainEngine;
                    if (workcart != null && !workcart.IsInvoking(workcart.DecayTick))
                    {
                        workcart.InvokeRandomized(workcart.DecayTick, UnityEngine.Random.Range(20f, 40f), workcart.decayTickSpacing, workcart.decayTickSpacing * 0.1f);
                    }
                }

                Destroy(GetComponent<SpawnPointInstance>());
                Destroy(this);
            }
        }

        private class CustomAddonSpawnPointInstance : SpawnPointInstance
        {
            public static CustomAddonSpawnPointInstance AddToComponent(AddonComponentTracker componentTracker, Component hostComponent, CustomAddonDefinition customAddonDefinition)
            {
                var spawnPointInstance = hostComponent.gameObject.AddComponent<CustomAddonSpawnPointInstance>();
                spawnPointInstance.CustomAddonDefinition = customAddonDefinition;
                spawnPointInstance._hostComponent = hostComponent;
                spawnPointInstance._componentTracker = componentTracker;
                componentTracker.RegisterComponent(hostComponent);
                customAddonDefinition.SpawnPointInstances.Add(spawnPointInstance);
                return spawnPointInstance;
            }

            public CustomAddonDefinition CustomAddonDefinition { get; private set; }
            private Component _hostComponent;
            private AddonComponentTracker _componentTracker;

            public void Kill()
            {
                CustomAddonDefinition.Kill(_hostComponent);
            }

            public new void OnDestroy()
            {
                _componentTracker.UnregisterComponent(_hostComponent);
                CustomAddonDefinition.SpawnPointInstances.Remove(this);

                if (!Rust.Application.isQuitting)
                {
                    Retire();
                }
            }
        }

        private class CustomSpawnPoint : BaseSpawnPoint, IAddonComponent
        {
            private const int TrainCarLayerMask = Rust.Layers.Mask.AI
                | Rust.Layers.Mask.Vehicle_World
                | Rust.Layers.Mask.Player_Server
                | Rust.Layers.Mask.Construction;

            // When CheckSpace is enabled, override the layer mask for certain entity prefabs.
            private static readonly Dictionary<string, int> CustomBoundsCheckMask = new Dictionary<string, int>
            {
                ["assets/content/vehicles/trains/locomotive/locomotive.entity.prefab"] = TrainCarLayerMask,
                ["assets/content/vehicles/sedan_a/sedanrail.entity.prefab"] = TrainCarLayerMask,
                ["assets/content/vehicles/trains/workcart/workcart.entity.prefab"] = TrainCarLayerMask,
                ["assets/content/vehicles/trains/workcart/workcart_aboveground.entity.prefab"] = TrainCarLayerMask,
                ["assets/content/vehicles/trains/workcart/workcart_aboveground2.entity.prefab"] = TrainCarLayerMask,
                ["assets/content/vehicles/trains/wagons/trainwagona.entity.prefab"] = TrainCarLayerMask,
                ["assets/content/vehicles/trains/wagons/trainwagonb.entity.prefab"] = TrainCarLayerMask,
                ["assets/content/vehicles/trains/wagons/trainwagonc.entity.prefab"] = TrainCarLayerMask,
                ["assets/content/vehicles/trains/wagons/trainwagonunloadablefuel.entity.prefab"] = TrainCarLayerMask,
                ["assets/content/vehicles/trains/wagons/trainwagonunloadableloot.entity.prefab"] = TrainCarLayerMask,
                ["assets/content/vehicles/trains/wagons/trainwagonunloadable.entity.prefab"] = TrainCarLayerMask,
                ["assets/content/vehicles/trains/caboose/traincaboose.entity.prefab"] = TrainCarLayerMask,
            };

            public static CustomSpawnPoint AddToGameObject(AddonComponentTracker componentTracker, GameObject gameObject, SpawnPointAdapter adapter, SpawnPointData spawnPointData)
            {
                var component = gameObject.AddComponent<CustomSpawnPoint>();
                component._spawnPointAdapter = adapter;
                component._spawnPointData = spawnPointData;
                component._componentTracker = componentTracker;
                componentTracker.RegisterComponent(component);
                return component;
            }

            public TransformAdapter Adapter => _spawnPointAdapter;
            private AddonComponentTracker _componentTracker;
            private SpawnPointAdapter _spawnPointAdapter;
            private SpawnPointData _spawnPointData;
            private Transform _transform;
            private BaseEntity _parentEntity;
            private List<SpawnPointInstance> _instances = new List<SpawnPointInstance>();

            public void PreUnload()
            {
                KillSpawnedInstances();
                gameObject.SetActive(false);
            }

            private void Awake()
            {
                _transform = transform;
                _parentEntity = _transform.parent?.ToBaseEntity();
            }

            public override void GetLocation(out Vector3 position, out Quaternion rotation)
            {
                position = _transform.position;
                rotation = _transform.rotation;

                if (_spawnPointData.RandomRadius > 0)
                {
                    Vector2 vector = UnityEngine.Random.insideUnitCircle * _spawnPointData.RandomRadius;
                    position += new Vector3(vector.x, 0f, vector.y);
                }

                if (_spawnPointData.RandomRotation)
                {
                    rotation *= Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                }

                if (_spawnPointData.SnapToGround)
                {
                    DropToGround(ref position, ref rotation);
                }
            }

            public override void ObjectSpawned(SpawnPointInstance instance)
            {
                _instances.Add(instance);

                var entity = instance.GetComponent<BaseEntity>();

                // Parent the entity to the monument only if the monument is mobile.
                // This might not be the best behavior for all situations, may need to be revisited.
                // In particular, vehicles should not be parented to entities that don't have a parent trigger,
                // since that would cause the vehicle to be destroyed when the parent is, even if the vehicle has left.
                if (Adapter.Monument is IEntityMonument { IsValid: true, IsMobile: true } entityMonument
                    && _parentEntity == entityMonument.RootEntity
                    && !entity.HasParent())
                {
                    entity.SetParent(_parentEntity, worldPositionStays: true);
                }

                if (IsVehicle(entity))
                {
                    SpawnedVehicleComponent.AddToVehicle(Adapter.Plugin, instance.gameObject);
                    entity.Invoke(() => DisableVehicleDecay(entity), 5);
                }

                var hackableCrate = entity as HackableLockedCrate;
                if ((object)hackableCrate != null && hackableCrate.shouldDecay)
                {
                    hackableCrate.shouldDecay = false;
                    hackableCrate.CancelInvoke(hackableCrate.DelayedDestroy);
                }
            }

            public override void ObjectRetired(SpawnPointInstance instance)
            {
                _instances.Remove(instance);

                _spawnPointAdapter.SpawnGroupAdapter.SpawnGroup.HandleObjectRetired();
            }

            public bool IsAvailableTo(CustomSpawnGroup.SpawnEntry spawnEntry)
            {
                if (_spawnPointData.Exclusive && _instances.Count > 0)
                    return false;

                if (spawnEntry.CustomAddonDefinition != null)
                {
                    if (_spawnPointData.CheckSpace)
                        // Pass null data for now since data isn't supported for custom addons with spawn points.
                        return spawnEntry.CustomAddonDefinition.CheckSpace?.Invoke(_transform.position, _transform.rotation, null) ?? true;

                    return true;
                }

                return IsAvailableTo(spawnEntry.Prefab.Get());
            }

            public override bool IsAvailableTo(GameObject prefab)
            {
                if (!base.IsAvailableTo(prefab))
                    return false;

                if (_spawnPointData.CheckSpace)
                {
                    if (CustomBoundsCheckMask.TryGetValue(prefab.name, out var customBoundsCheckMask))
                        return SpawnHandler.CheckBounds(prefab, _transform.position, _transform.rotation, Vector3.one, customBoundsCheckMask);

                    return SingletonComponent<SpawnHandler>.Instance.CheckBounds(prefab, _transform.position, _transform.rotation, Vector3.one);
                }

                return true;
            }

            public override bool HasPlayersIntersecting()
            {
                var detectionRadius = _spawnPointData.PlayerDetectionRadius > 0
                    ? _spawnPointData.PlayerDetectionRadius
                    : _spawnPointData.RandomRadius > 0
                        ? _spawnPointData.RandomRadius + 1
                        : 0;

                return detectionRadius > 0
                    ? BaseNetworkable.HasCloseConnections(transform.position, detectionRadius)
                    : base.HasPlayersIntersecting();
            }

            public void KillSpawnedInstances(WeightedPrefabData weightedPrefabData = null)
            {
                for (var i = _instances.Count - 1; i >= 0; i--)
                {
                    var spawnPointInstance = _instances[i];
                    if (spawnPointInstance is CustomAddonSpawnPointInstance customAddonSpawnPointInstance)
                    {
                        if (weightedPrefabData == null || weightedPrefabData.CustomAddonName == customAddonSpawnPointInstance.CustomAddonDefinition.AddonName)
                        {
                            customAddonSpawnPointInstance.Kill();
                        }

                        continue;
                    }

                    var entity = spawnPointInstance.GetComponent<BaseEntity>();
                    if ((weightedPrefabData == null || entity.PrefabName == weightedPrefabData.PrefabName)
                        && entity != null && !entity.IsDestroyed)
                    {
                        entity.Kill();
                    }
                }
            }

            private bool IsVehicle(BaseEntity entity)
            {
                return entity is HotAirBalloon || entity is BaseVehicle;
            }

            private void DisableVehicleDecay(BaseEntity vehicle)
            {
                switch (vehicle)
                {
                    case BaseSubmarine sub:
                        sub.timeSinceLastUsed = float.MinValue;
                        break;
                    case Bike bike:
                        bike.timeSinceLastUsed = float.MinValue;
                        break;
                    case HotAirBalloon hab:
                        hab.sinceLastBlast = float.MaxValue;
                        break;
                    case Kayak kayak:
                        kayak.timeSinceLastUsed = float.MinValue;
                        break;
                    case ModularCar car:
                        car.lastEngineOnTime = float.MaxValue;
                        break;
                    case MotorRowboat boat:
                        boat.timeSinceLastUsedFuel = float.MinValue;
                        break;
                    case PlayerHelicopter heli:
                        heli.lastEngineOnTime = float.MaxValue;
                        break;
                    case RidableHorse horse:
                        horse.lastInputTime = float.MaxValue;
                        break;
                    case Snowmobile snowmobile:
                        snowmobile.timeSinceLastUsed = float.MinValue;
                        break;
                    case TrainCar trainCar:
                        trainCar.CancelInvoke(trainCar.DecayTick);
                        break;
                }
            }

            public void MoveSpawnedInstances()
            {
                for (var i = _instances.Count - 1; i >= 0; i--)
                {
                    var entity = _instances[i].GetComponent<BaseEntity>();
                    if (entity == null || entity.IsDestroyed)
                        continue;

                    GetLocation(out var position, out var rotation);

                    if (position != entity.transform.position || rotation != entity.transform.rotation)
                    {
                        if (IsVehicle(entity))
                        {
                            var spawnedVehicleComponent = entity.GetComponent<SpawnedVehicleComponent>();
                            if (spawnedVehicleComponent != null)
                            {
                                spawnedVehicleComponent.CancelInvoke(spawnedVehicleComponent.CheckPositionTracked);
                            }

                            Adapter.Plugin.NextTick(() => spawnedVehicleComponent.Awake());
                        }

                        entity.transform.SetPositionAndRotation(position, rotation);
                        BroadcastEntityTransformChange(entity);
                    }
                }
            }

            private void OnDestroy()
            {
                KillSpawnedInstances();
                _spawnPointAdapter.OnComponentDestroyed(this);
                _componentTracker.UnregisterComponent(this);
            }
        }

        private class CustomSpawnGroup : SpawnGroup
        {
            public new class SpawnEntry
            {
                public readonly CustomAddonDefinition CustomAddonDefinition;
                public readonly GameObjectRef Prefab;
                public int Weight;

                public bool IsValid => CustomAddonDefinition?.IsValid ?? !string.IsNullOrEmpty(Prefab.guid);

                public SpawnEntry(CustomAddonDefinition customAddonDefinition, int weight)
                {
                    CustomAddonDefinition = customAddonDefinition;
                    Weight = weight;
                }

                public SpawnEntry(GameObjectRef prefab, int weight)
                {
                    Prefab = prefab;
                    Weight = weight;
                }
            }

            private static AIInformationZone FindVirtualInfoZone(Vector3 position)
            {
                foreach (var zone in AIInformationZone.zones)
                {
                    if (zone.Virtual && zone.PointInside(position))
                        return zone;
                }

                return null;
            }

            public SpawnGroupAdapter SpawnGroupAdapter { get; private set; }
            public List<SpawnEntry> SpawnEntries { get; } = new();
            private AIInformationZone _cachedInfoZone;
            private bool _didLookForInfoZone;

            private SpawnGroupData SpawnGroupData => SpawnGroupAdapter.SpawnGroupData;

            public void Init(SpawnGroupAdapter spawnGroupAdapter)
            {
                SpawnGroupAdapter = spawnGroupAdapter;
            }

            public void UpdateSpawnClock()
            {
                if (!WantsTimedSpawn())
                {
                    spawnClock.events.Clear();
                    return;
                }

                if (spawnClock.events.Count == 0)
                {
                    spawnClock.Add(GetSpawnDelta(), GetSpawnVariance(), Spawn);
                }
                else
                {
                    var clockEvent = spawnClock.events[0];
                    var timeUntilSpawn = clockEvent.time - UnityEngine.Time.time;

                    if (timeUntilSpawn > SpawnGroupData.RespawnDelayMax)
                    {
                        clockEvent.time = UnityEngine.Time.time + SpawnGroupData.RespawnDelayMax;
                        spawnClock.events[0] = clockEvent;
                    }
                }
            }

            public void HandleObjectRetired()
            {
                // Add one to current population because it was just decremented.
                if (SpawnGroupData.PauseScheduleWhileFull && currentPopulation + 1 >= maxPopulation)
                {
                    ResetSpawnClock();
                }
            }

            // Called by Rust via Unity SendMessage, when associating with a vanilla puzzle reset.
            public void OnPuzzleReset()
            {
                Clear();
                DelayedSpawn();
            }

            protected override void Spawn(int numToSpawn)
            {
                numToSpawn = Mathf.Min(numToSpawn, maxPopulation - currentPopulation);

                for (int i = 0; i < numToSpawn; i++)
                {
                    var spawnEntry = GetRandomSpawnEntry();
                    if (spawnEntry is not { IsValid: true })
                        continue;

                    var spawnPoint = GetRandomSpawnPoint(spawnEntry, out var position, out var rotation);
                    if (spawnPoint == null)
                        continue;

                    SpawnPointInstance spawnPointInstance = null;

                    if (spawnEntry.CustomAddonDefinition != null)
                    {
                        // TODO: Figure out how to associate data with addons spawned this via spawn points.
                        var component = spawnEntry.CustomAddonDefinition.DoSpawn(SpawnGroupData.Id, SpawnGroupAdapter.Monument.Object, position, rotation, null);
                        if (component != null)
                        {
                            spawnPointInstance = CustomAddonSpawnPointInstance.AddToComponent(SpawnGroupAdapter.Plugin._componentTracker, component, spawnEntry.CustomAddonDefinition);
                        }
                    }
                    else
                    {
                        var entity = GameManager.server.CreateEntity(spawnEntry.Prefab.resourcePath, position, rotation, startActive: false);
                        if (entity != null)
                        {
                            entity.gameObject.AwakeFromInstantiate();
                            entity.Spawn();
                            PostSpawnProcess(entity, spawnPoint);
                            spawnPointInstance = entity.gameObject.AddComponent<SpawnPointInstance>();
                        }
                    }

                    if (spawnPointInstance is not null)
                    {
                        spawnPointInstance.parentSpawnPointUser = this;
                        spawnPointInstance.parentSpawnPoint = spawnPoint;
                        spawnPointInstance.Notify();
                    }
                }
            }

            protected override void PostSpawnProcess(BaseEntity entity, BaseSpawnPoint spawnPoint)
            {
                base.PostSpawnProcess(entity, spawnPoint);

                EnableSavingRecursive(entity, false);

                var npcPlayer = entity as NPCPlayer;
                if (npcPlayer != null)
                {
                    var virtualInfoZone = GetVirtualInfoZone();
                    if (virtualInfoZone != null)
                    {
                        npcPlayer.VirtualInfoZone = virtualInfoZone;
                    }

                    var humanNpc = npcPlayer as HumanNPCGlobal;
                    if (humanNpc != null)
                    {
                        virtualInfoZone?.RegisterSleepableEntity(humanNpc.Brain);

                        var agent = npcPlayer.NavAgent;
                        agent.agentTypeID = -1372625422;
                        agent.areaMask = 1;
                        agent.autoTraverseOffMeshLink = true;
                        agent.autoRepath = true;

                        var brain = humanNpc.Brain;
                        humanNpc.Invoke(() =>
                        {
                            var navigator = brain.Navigator;
                            if (navigator == null)
                                return;

                            navigator.DefaultArea = "Walkable";
                            navigator.Init(humanNpc, agent);
                            navigator.PlaceOnNavMesh(0);
                        }, 0);
                    }
                }
            }

            private bool HasSpawned(SpawnEntry spawnEntry)
            {
                foreach (var spawnInstance in spawnInstances)
                {
                    if (spawnInstance is CustomAddonSpawnPointInstance customAddonSpawnPointInstance)
                    {
                        if (spawnEntry.CustomAddonDefinition == customAddonSpawnPointInstance.CustomAddonDefinition)
                            return true;

                        continue;
                    }

                    var entity = spawnInstance.gameObject.ToBaseEntity();
                    if (entity != null && entity.prefabID == spawnEntry.Prefab.resourceID)
                        return true;
                }

                return false;
            }

            private BaseSpawnPoint GetRandomSpawnPoint(SpawnEntry spawnEntry, out Vector3 position, out Quaternion rotation)
            {
                BaseSpawnPoint baseSpawnPoint = null;
                position = Vector3.zero;
                rotation = Quaternion.identity;

                var randomIndex = UnityEngine.Random.Range(0, spawnPoints.Length);

                for (int i = 0; i < spawnPoints.Length; i++)
                {
                    var spawnPoint = spawnPoints[(randomIndex + i) % spawnPoints.Length] as CustomSpawnPoint;
                    if (spawnPoint != null
                        && spawnPoint.IsAvailableTo(spawnEntry)
                        && !spawnPoint.HasPlayersIntersecting())
                    {
                        baseSpawnPoint = spawnPoint;
                        break;
                    }
                }

                if (baseSpawnPoint != null)
                {
                    baseSpawnPoint.GetLocation(out position, out rotation);
                }

                return baseSpawnPoint;
            }

            private float DetermineSpawnEntryWeight(SpawnEntry spawnEntry)
            {
                if (spawnEntry.CustomAddonDefinition is { IsValid: false })
                    return 0;

                return preventDuplicates && HasSpawned(spawnEntry) ? 0 : spawnEntry.Weight;
            }

            private SpawnEntry GetRandomSpawnEntry()
            {
                var totalWeight = SpawnEntries.Sum(DetermineSpawnEntryWeight);
                if (totalWeight == 0)
                    return null;

                var randomWeight = UnityEngine.Random.Range(0f, totalWeight);

                foreach (var spawnEntry in SpawnEntries)
                {
                    var weight = DetermineSpawnEntryWeight(spawnEntry);
                    if ((randomWeight -= weight) <= 0f)
                        return spawnEntry;
                }

                return SpawnEntries[^1];
            }

            private AIInformationZone GetVirtualInfoZone()
            {
                if (!_didLookForInfoZone)
                {
                    _cachedInfoZone = FindVirtualInfoZone(transform.position);
                    _didLookForInfoZone = true;
                }

                return _cachedInfoZone;
            }

            private void ResetSpawnClock()
            {
                if (spawnClock.events.Count == 0)
                    return;

                var clockEvent = spawnClock.events[0];
                clockEvent.delta = GetSpawnDelta();
                var variance = GetSpawnVariance();
                clockEvent.variance = variance;
                clockEvent.time = UnityEngine.Time.time + clockEvent.delta + UnityEngine.Random.Range(-variance, variance);
                spawnClock.events[0] = clockEvent;
            }

            private new void OnDestroy()
            {
                SingletonComponent<SpawnHandler>.Instance.SpawnGroups.Remove(this);
                SpawnGroupAdapter.OnSpawnGroupKilled();
            }
        }

        private class SpawnPointAdapter : TransformAdapter
        {
            public SpawnPointData SpawnPointData { get; }
            public SpawnGroupAdapter SpawnGroupAdapter { get; }
            public CustomSpawnPoint SpawnPoint { get; private set; }
            public override Component Component => SpawnPoint;
            public override Transform Transform => _transform;
            public override Vector3 Position => _transform.position;
            public override Quaternion Rotation => _transform.rotation;
            public override bool IsValid => SpawnPoint != null;

            private Transform _transform;

            public SpawnPointAdapter(SpawnPointData spawnPointData, SpawnGroupAdapter spawnGroupAdapter, BaseController controller, BaseMonument monument) : base(spawnPointData, controller, monument)
            {
                SpawnPointData = spawnPointData;
                SpawnGroupAdapter = spawnGroupAdapter;
            }

            public override void Spawn()
            {
                var gameObject = new GameObject();
                _transform = gameObject.transform;
                _transform.SetPositionAndRotation(IntendedPosition, IntendedRotation);

                if (Monument is IEntityMonument entityMonument)
                {
                    _transform.SetParent(entityMonument.RootEntity.transform, worldPositionStays: true);
                }

                SpawnPoint = CustomSpawnPoint.AddToGameObject(SpawnGroupAdapter.Plugin._componentTracker, gameObject, this, SpawnPointData);
            }

            public override void Kill()
            {
                UnityEngine.Object.Destroy(SpawnPoint?.gameObject);
            }

            public override void OnComponentDestroyed(Component component)
            {
                SpawnGroupAdapter.OnSpawnPointAdapterKilled(this);
            }

            public override void PreUnload()
            {
                SpawnPoint.PreUnload();
            }

            public void KillSpawnedInstances(WeightedPrefabData weightedPrefabData)
            {
                SpawnPoint.KillSpawnedInstances(weightedPrefabData);
            }

            public void UpdatePosition()
            {
                if (!IsAtIntendedPosition)
                {
                    _transform.SetPositionAndRotation(IntendedPosition, IntendedRotation);
                    SpawnPoint.MoveSpawnedInstances();
                }
            }

            public override bool TryRecordUpdates(Transform moveTransform = null, Transform rotateTransform = null)
            {
                // Only check if at intended position if the moved/rotated transform is the spawn point itself.
                if (moveTransform == _transform && rotateTransform == _transform && IsAtIntendedPosition)
                    return false;

                moveTransform ??= _transform;
                rotateTransform ??= _transform;

                var moveEntityPosition = moveTransform.position;
                SpawnPointData.Position = Monument.InverseTransformPoint(moveEntityPosition);
                SpawnPointData.RotationAngles = (Quaternion.Inverse(Monument.Rotation) * rotateTransform.rotation).eulerAngles;
                SpawnPointData.SnapToTerrain = IsOnTerrain(moveEntityPosition);
                return true;
            }
        }

        private class SpawnGroupAdapter : BaseAdapter
        {
            public SpawnGroupData SpawnGroupData { get; }
            public List<SpawnPointAdapter> SpawnPointAdapters { get; } = new List<SpawnPointAdapter>();
            public CustomSpawnGroup SpawnGroup { get; private set; }
            public PuzzleReset AssociatedPuzzleReset { get; private set; }
            public override bool IsValid => SpawnGroup != null;

            public SpawnGroupAdapter(SpawnGroupData spawnGroupData, BaseController controller, BaseMonument monument) : base(spawnGroupData, controller, monument)
            {
                SpawnGroupData = spawnGroupData;
            }

            public override void Spawn()
            {
                var spawnGroupGameObject = new GameObject();
                spawnGroupGameObject.transform.SetPositionAndRotation(Monument.Position, Monument.Rotation);
                spawnGroupGameObject.SetActive(false);

                // Configure the spawn group and create spawn points before enabling the group.
                // This allows the vanilla Awake() method to perform initial spawn and schedule spawns.
                SpawnGroup = spawnGroupGameObject.AddComponent<CustomSpawnGroup>();
                SpawnGroup.Init(this);

                SpawnGroup.prefabs ??= new List<SpawnGroup.SpawnEntry>();

                UpdateProperties();
                UpdatePrefabEntries();

                // This will call Awake() on the CustomSpawnGroup component.
                spawnGroupGameObject.SetActive(true);

                foreach (var spawnPointData in SpawnGroupData.SpawnPoints)
                {
                    CreateSpawnPoint(spawnPointData);
                }

                UpdateSpawnPointReferences();
                UpdatePuzzleResetAssociation();

                if (SpawnGroupData.InitialSpawn)
                {
                    SpawnGroup.Spawn();
                }
            }

            public override void Kill()
            {
                UnityEngine.Object.Destroy(SpawnGroup?.gameObject);
            }

            public override void OnComponentDestroyed(Component component)
            {
                Kill();
            }

            public override void PreUnload()
            {
                foreach (var adapter in SpawnPointAdapters)
                {
                    adapter.PreUnload();
                }
            }

            public void OnSpawnPointAdapterKilled(SpawnPointAdapter spawnPointAdapter)
            {
                SpawnPointAdapters.Remove(spawnPointAdapter);

                if (SpawnGroup != null)
                {
                    UpdateSpawnPointReferences();
                }

                if (SpawnPointAdapters.Count == 0)
                {
                    Controller.OnAdapterKilled(this);
                }
            }

            public void OnSpawnGroupKilled()
            {
                if (AssociatedPuzzleReset != null)
                {
                    UnregisterWithPuzzleReset(AssociatedPuzzleReset);
                }

                foreach (var spawnPointAdapter in SpawnPointAdapters.ToList())
                {
                    spawnPointAdapter.Kill();
                }
            }

            public void UpdateCustomAddons(CustomAddonDefinition customAddonDefinition)
            {
                foreach (var prefabData in SpawnGroupData.Prefabs)
                {
                    if (prefabData.CustomAddonName != null
                        && prefabData.CustomAddonName == customAddonDefinition.AddonName)
                    {
                        // Clear and recreate the prefab entries since an entry may have been skipped for an invalid custom addon.
                        SpawnGroup.SpawnEntries.Clear();
                        UpdatePrefabEntries();
                        break;
                    }
                }
            }

            private Vector3 GetMidpoint()
            {
                var min = Vector3.positiveInfinity;
                var max = Vector3.negativeInfinity;

                foreach (var spawnPointAdapter in SpawnPointAdapters)
                {
                    var position = spawnPointAdapter.Position;
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                }

                return (min + max) / 2f;
            }

            private PuzzleReset FindClosestVanillaPuzzleReset(float maxDistance = 60)
            {
                return EntityUtils.GetClosestNearbyComponent<PuzzleReset>(GetMidpoint(), maxDistance, Rust.Layers.Mask.World, puzzleReset =>
                {
                    var entity = puzzleReset.GetComponent<BaseEntity>();
                    return entity != null && !Plugin._componentTracker.IsAddonComponent(entity);
                });
            }

            private void RegisterWithPuzzleReset(PuzzleReset puzzleReset)
            {
                if (IsRegisteredWithPuzzleReset(puzzleReset))
                    return;

                if (puzzleReset.resetObjects == null)
                {
                    puzzleReset.resetObjects = new[] { SpawnGroup.gameObject };
                    return;
                }

                var originalLength = puzzleReset.resetObjects.Length;
                Array.Resize(ref puzzleReset.resetObjects, originalLength + 1);
                puzzleReset.resetObjects[originalLength] = SpawnGroup.gameObject;
            }

            private void UnregisterWithPuzzleReset(PuzzleReset puzzleReset)
            {
                if (!IsRegisteredWithPuzzleReset(puzzleReset))
                    return;

                puzzleReset.resetObjects = puzzleReset.resetObjects.Where(obj => obj != SpawnGroup.gameObject).ToArray();
            }

            private bool IsRegisteredWithPuzzleReset(PuzzleReset puzzleReset)
            {
                return puzzleReset.resetObjects?.Contains(SpawnGroup.gameObject) == true;
            }

            private void UpdateProperties()
            {
                SpawnGroup.preventDuplicates = SpawnGroupData.PreventDuplicates;
                SpawnGroup.maxPopulation = SpawnGroupData.MaxPopulation;
                SpawnGroup.numToSpawnPerTickMin = SpawnGroupData.SpawnPerTickMin;
                SpawnGroup.numToSpawnPerTickMax = SpawnGroupData.SpawnPerTickMax;

                var respawnDelayMin = SpawnGroupData.RespawnDelayMin;
                var respawnDelayMax = Mathf.Max(respawnDelayMin, SpawnGroupData.RespawnDelayMax > 0 ? SpawnGroupData.RespawnDelayMax : float.PositiveInfinity);

                var respawnDelayMinChanged = !Mathf.Approximately(SpawnGroup.respawnDelayMin, respawnDelayMin);
                var respawnDelayMaxChanged = !Mathf.Approximately(SpawnGroup.respawnDelayMax, respawnDelayMax);

                SpawnGroup.respawnDelayMin = respawnDelayMin;
                SpawnGroup.respawnDelayMax = respawnDelayMax;

                if (SpawnGroup.gameObject.activeSelf && (respawnDelayMinChanged || respawnDelayMaxChanged))
                {
                    SpawnGroup.UpdateSpawnClock();
                }
            }

            private void UpdatePrefabEntries()
            {
                if (SpawnGroup.SpawnEntries.Count == SpawnGroupData.Prefabs.Count)
                {
                    for (var i = 0; i < SpawnGroup.SpawnEntries.Count; i++)
                    {
                        SpawnGroup.SpawnEntries[i].Weight = SpawnGroupData.Prefabs[i].Weight;
                    }

                    return;
                }

                SpawnGroup.SpawnEntries.Clear();

                foreach (var prefabEntry in SpawnGroupData.Prefabs)
                {
                    if (prefabEntry.CustomAddonName != null)
                    {
                        var customAddonDefinition = Plugin._customAddonManager.GetAddon(prefabEntry.CustomAddonName);
                        if (customAddonDefinition != null)
                        {
                            SpawnGroup.SpawnEntries.Add(new CustomSpawnGroup.SpawnEntry(customAddonDefinition, prefabEntry.Weight));
                        }

                        continue;
                    }

                    if (prefabEntry.PrefabName != null && GameManifest.pathToGuid.TryGetValue(prefabEntry.PrefabName, out var guid))
                    {
                        SpawnGroup.SpawnEntries.Add(new CustomSpawnGroup.SpawnEntry(new GameObjectRef { guid = guid }, prefabEntry.Weight));
                    }
                }
            }

            private void UpdateSpawnPointReferences()
            {
                if (!SpawnGroup.gameObject.activeSelf || SpawnGroup.spawnPoints.Length == SpawnPointAdapters.Count)
                    return;

                SpawnGroup.spawnPoints = new BaseSpawnPoint[SpawnPointAdapters.Count];

                for (var i = 0; i < SpawnPointAdapters.Count; i++)
                {
                    SpawnGroup.spawnPoints[i] = SpawnPointAdapters[i].SpawnPoint;
                }
            }

            private void UpdateSpawnPointPositions()
            {
                foreach (var adapter in SpawnPointAdapters)
                {
                    if (!adapter.IsAtIntendedPosition)
                    {
                        adapter.UpdatePosition();
                    }
                }
            }

            private void UpdatePuzzleResetAssociation()
            {
                if (!SpawnGroup.gameObject.activeSelf)
                    return;

                if (SpawnGroupData.RespawnWhenNearestPuzzleResets)
                {
                    var closestPuzzleReset = FindClosestVanillaPuzzleReset();

                    // Re-evaluate current association since the midpoint and other circumstances can change.
                    if (AssociatedPuzzleReset != null && AssociatedPuzzleReset != closestPuzzleReset)
                    {
                        UnregisterWithPuzzleReset(AssociatedPuzzleReset);
                    }

                    AssociatedPuzzleReset = closestPuzzleReset;
                    if (closestPuzzleReset != null)
                    {
                        RegisterWithPuzzleReset(closestPuzzleReset);
                    }
                }
                else if (AssociatedPuzzleReset != null)
                {
                    UnregisterWithPuzzleReset(AssociatedPuzzleReset);
                }
            }

            public void UpdateSpawnGroup()
            {
                UpdateProperties();
                UpdatePrefabEntries();
                UpdateSpawnPointReferences();
                UpdateSpawnPointPositions();
                UpdatePuzzleResetAssociation();
            }

            public void SpawnTick()
            {
                SpawnGroup.Spawn();
            }

            public void KillSpawnedInstances(WeightedPrefabData weightedPrefabData)
            {
                foreach (var spawnPointAdapter in SpawnPointAdapters)
                {
                    spawnPointAdapter.KillSpawnedInstances(weightedPrefabData);
                }
            }

            public void CreateSpawnPoint(SpawnPointData spawnPointData)
            {
                var spawnPointAdapter = new SpawnPointAdapter(spawnPointData, this, Controller, Monument);
                SpawnPointAdapters.Add(spawnPointAdapter);
                spawnPointAdapter.Spawn();

                if (SpawnGroup.gameObject.activeSelf)
                {
                    UpdateSpawnPointReferences();
                }
            }

            public void KillSpawnPoint(SpawnPointData spawnPointData)
            {
                FindSpawnPoint(spawnPointData)?.Kill();
            }

            private SpawnPointAdapter FindSpawnPoint(SpawnPointData spawnPointData)
            {
                foreach (var spawnPointAdapter in SpawnPointAdapters)
                {
                    if (spawnPointAdapter.SpawnPointData == spawnPointData)
                        return spawnPointAdapter;
                }

                return null;
            }
        }

        private class SpawnGroupController : BaseController, IUpdateableController
        {
            public SpawnGroupData SpawnGroupData { get; }
            public IEnumerable<SpawnGroupAdapter> SpawnGroupAdapters { get; }

            public SpawnGroupController(ProfileController profileController, SpawnGroupData spawnGroupData) : base(profileController, spawnGroupData)
            {
                SpawnGroupData = spawnGroupData;
                SpawnGroupAdapters = Adapters.Cast<SpawnGroupAdapter>();
            }

            public override BaseAdapter CreateAdapter(BaseMonument monument)
            {
                return new SpawnGroupAdapter(SpawnGroupData, this, monument);
            }

            public override Coroutine Kill(BaseData data)
            {
                if (data == Data)
                    return base.Kill(data);

                if (data is SpawnPointData spawnPointData)
                {
                    KillSpawnPoint(spawnPointData);
                    return null;
                }

                LogError($"{nameof(SpawnGroupController)}.{nameof(Kill)} not implemented for type {data.GetType()}. Killing {nameof(SpawnGroupController)}.");
                return null;
            }

            public void CreateSpawnPoint(SpawnPointData spawnPointData)
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters)
                {
                    spawnGroupAdapter.CreateSpawnPoint(spawnPointData);
                }
            }

            public void KillSpawnPoint(SpawnPointData spawnPointData)
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters)
                {
                    spawnGroupAdapter.KillSpawnPoint(spawnPointData);
                }
            }

            public void UpdateSpawnGroups()
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters)
                {
                    spawnGroupAdapter.UpdateSpawnGroup();
                }
            }

            public Coroutine StartUpdateRoutine()
            {
                return ProfileController.StartCoroutine(UpdateRoutine());
            }

            public void StartSpawnRoutine()
            {
                ProfileController.StartCoroutine(SpawnTickRoutine());
            }

            public void StartKillSpawnedInstancesRoutine(WeightedPrefabData weightedPrefabData)
            {
                ProfileController.StartCoroutine(KillSpawnedInstancesRoutine(weightedPrefabData));
            }

            public void StartRespawnRoutine()
            {
                ProfileController.StartCoroutine(RespawnRoutine());
            }

            private IEnumerator UpdateRoutine()
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters)
                {
                    spawnGroupAdapter.UpdateSpawnGroup();
                    yield return null;
                }
            }

            private IEnumerator SpawnTickRoutine()
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters.ToList())
                {
                    spawnGroupAdapter.SpawnTick();
                    yield return null;
                }
            }

            private IEnumerator KillSpawnedInstancesRoutine(WeightedPrefabData weightedPrefabData = null)
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters.ToList())
                {
                    spawnGroupAdapter.KillSpawnedInstances(weightedPrefabData);
                    yield return null;
                }
            }

            private IEnumerator RespawnRoutine()
            {
                yield return KillSpawnedInstancesRoutine();
                yield return SpawnTickRoutine();
            }
        }

        #endregion

        #region Paste Adapter/Controller

        private class PasteAdapter : TransformAdapter
        {
            private const float CopyPasteMagicRotationNumber = 57.2958f;
            private const float MaxWaitSeconds = 5;
            private const float KillBatchSize = 5;

            public PasteData PasteData { get; }
            public OBB Bounds => new OBB(Position + Rotation * _bounds.center, _bounds.size, Rotation);
            public override Component Component => _transform;
            public override Transform Transform => _transform;
            public override Vector3 Position => _transform.position;
            public override Quaternion Rotation => _transform.rotation;
            public override bool IsValid => _transform != null;
            public override bool IsAtIntendedPosition => _spawnedPosition == Position && _spawnedRotation == Rotation;

            private Transform _transform;
            private Vector3 _spawnedPosition;
            private Quaternion _spawnedRotation;
            private Bounds _bounds;
            private bool _isWorking;
            private Action _cancelPaste;
            private List<BaseEntity> _pastedEntities = new List<BaseEntity>();

            public PasteAdapter(PasteData pasteData, BaseController controller, BaseMonument monument) : base(pasteData, controller, monument)
            {
                PasteData = pasteData;
            }

            public override void Spawn()
            {
                var position = IntendedPosition;
                var rotation = IntendedRotation;

                _transform = new GameObject().transform;
                _transform.SetPositionAndRotation(position, rotation);
                AddonComponent.AddToComponent(Plugin._componentTracker, _transform, this);

                if (Monument is IEntityMonument entityMonument)
                {
                    _transform.SetParent(entityMonument.RootEntity.transform);
                }

                SpawnPaste(position, rotation);
            }

            public override void Kill()
            {
                if (_transform != null)
                {
                    UnityEngine.Object.Destroy(_transform.gameObject);
                }

                KillPaste();
                Controller.OnAdapterKilled(this);
            }

            public override void OnComponentDestroyed(Component component)
            {
                if (component is not BaseEntity entity)
                {
                    Kill();
                    return;
                }

                _pastedEntities.Remove(entity);
            }

            public void HandleChanges()
            {
                if (IsAtIntendedPosition)
                    return;

                var position = IntendedPosition;
                var rotation = IntendedRotation;
                Transform.SetPositionAndRotation(position, rotation);
                ProfileController.StartCoroutine(RespawnPasteRoutine(position, rotation));
            }

            private void SpawnPaste(Vector3 position, Quaternion rotation)
            {
                _spawnedPosition = position;
                _spawnedRotation = rotation;
                _cancelPaste = PasteUtils.PasteWithCancelCallback(Plugin.CopyPaste, PasteData, position, rotation.eulerAngles.y / CopyPasteMagicRotationNumber, OnEntityPasted, OnPasteComplete);

                if (_cancelPaste != null)
                {
                    _isWorking = true;
                    WaitInstruction = WaitWhileWithTimeout(() => _isWorking, MaxWaitSeconds);
                }
                else
                {
                    _isWorking = false;
                    WaitInstruction = null;
                }
            }

            private IEnumerator SpawnPasteRoutine(Vector3 position, Quaternion rotation)
            {
                SpawnPaste(position, rotation);
                yield return WaitInstruction;
            }

            private IEnumerator RespawnPasteRoutine(Vector3 position, Quaternion rotation)
            {
                yield return KillPaste();
                yield return SpawnPasteRoutine(position, rotation);
            }

            private Coroutine KillPaste()
            {
                _cancelPaste?.Invoke();
                _isWorking = true;
                WaitInstruction = WaitWhileWithTimeout(() => _isWorking, MaxWaitSeconds);
                return CoroutineManager.StartGlobalCoroutine(KillRoutine());
            }

            private IEnumerator KillRoutine()
            {
                var pastedEntities = _pastedEntities.ToList();
                var killedInCurrentBatch = 0;

                // Remove the entities in reverse order. Hopefully this makes the top of the building get removed first.
                for (var i = pastedEntities.Count - 1; i >= 0; i--)
                {
                    var entity = pastedEntities[i];
                    if (entity != null && !entity.IsDestroyed)
                    {
                        Plugin.TrackStart();
                        entity.Kill();
                        Plugin.TrackEnd();

                        if (killedInCurrentBatch++ >= KillBatchSize)
                        {
                            killedInCurrentBatch = 0;
                            yield return null;
                        }
                    }
                }

                _isWorking = false;
            }

            private void OnEntityPasted(BaseEntity entity)
            {
                EntitySetupUtils.PreSpawnShared(entity);
                EntitySetupUtils.PostSpawnShared(Plugin, entity, enableSaving: false);

                AddonComponent.AddToComponent(Plugin._componentTracker, entity, this);
                _pastedEntities.Add(entity);
            }

            private void OnPasteComplete()
            {
                _isWorking = false;
                _bounds = GetBounds();
            }

            private Bounds GetBounds()
            {
                var bounds = new Bounds();
                var pastePosition = Position;
                var pasteInverseRotation = Quaternion.Inverse(Rotation);

                foreach (var entity in _pastedEntities)
                {
                    if (entity == null || entity.IsDestroyed)
                        continue;

                    var transform = entity.transform;
                    var relativePosition = pasteInverseRotation * (transform.position - pastePosition);
                    var relativeRotation = pasteInverseRotation * transform.rotation;
                    var obb = new OBB(relativePosition, transform.lossyScale, relativeRotation, entity.bounds);
                    bounds.Encapsulate(obb.ToBounds());
                }

                return bounds;
            }
        }

        private class PasteController : BaseController, IUpdateableController
        {
            public PasteData PasteData { get; }

            public PasteController(ProfileController profileController, PasteData pasteData) : base(profileController, pasteData)
            {
                PasteData = pasteData;
            }

            public override BaseAdapter CreateAdapter(BaseMonument monument)
            {
                return new PasteAdapter(PasteData, this, monument);
            }

            public override IEnumerator SpawnAtMonumentsRoutine(IEnumerable<BaseMonument> monumentList)
            {
                if (!PasteUtils.IsCopyPasteCompatible(Plugin.CopyPaste))
                {
                    LogError($"Unable to paste \"{PasteData.Filename}\" for profile \"{Profile.Name}\" because CopyPaste is not loaded or its version is incompatible.");
                    yield break;
                }

                if (!PasteUtils.DoesPasteExist(PasteData.Filename))
                {
                    LogError($"Unable to paste \"{PasteData.Filename}\" for profile \"{Profile.Name}\" because the file does not exist.");
                    yield break;
                }

                yield return base.SpawnAtMonumentsRoutine(monumentList);
            }

            public Coroutine StartUpdateRoutine()
            {
                return ProfileController.StartCoroutine(UpdateRoutine());
            }

            private IEnumerator UpdateRoutine()
            {
                foreach (var adapter in Adapters.ToList())
                {
                    var pasteAdapter = adapter as PasteAdapter;
                    if (pasteAdapter is not { IsValid: true })
                        continue;

                    pasteAdapter.HandleChanges();
                    yield return pasteAdapter.WaitInstruction;
                }
            }
        }

        #endregion

        #region Custom Addon Adapter/Controller

        private class CustomAddonDefinition
        {
            public static CustomAddonDefinition FromDictionary(string addonName, Plugin plugin, Dictionary<string, object> addonSpec)
            {
                var addonDefinition = new CustomAddonDefinition
                {
                    AddonName = addonName,
                    OwnerPlugin = plugin,
                };

                if (addonSpec.TryGetValue("Initialize", out var initializeCallback))
                {
                    addonDefinition.Initialize = initializeCallback as CustomInitializeCallback;
                    addonDefinition.InitializeV2 = initializeCallback as CustomInitializeCallbackV2;
                }

                if (addonSpec.TryGetValue("Edit", out var editCallback))
                {
                    addonDefinition.Edit = editCallback as CustomEditCallback;
                }

                if (addonSpec.TryGetValue("Spawn", out var spawnCallback))
                {
                    addonDefinition.Spawn = spawnCallback as CustomSpawnCallback;
                    addonDefinition.SpawnV2 = spawnCallback as CustomSpawnCallbackV2;
                }

                if (addonSpec.TryGetValue("CheckSpace", out var checkSpaceCallback))
                {
                    addonDefinition.CheckSpace = checkSpaceCallback as CustomCheckSpaceCallback;
                }

                if (addonSpec.TryGetValue("Kill", out var killCallback))
                {
                    addonDefinition.Kill = killCallback as CustomKillCallback;
                }

                if (addonSpec.TryGetValue("Unload", out var unloadCallback))
                {
                    addonDefinition.Unload = unloadCallback as CustomUnloadCallback;
                }

                if (addonSpec.TryGetValue("Update", out var updateCallback))
                {
                    addonDefinition.Update = updateCallback as CustomUpdateCallback;
                    addonDefinition.UpdateV2 = updateCallback as CustomUpdateCallbackV2;
                }

                if (addonSpec.TryGetValue("AddDisplayInfo", out var addDisplayInfoCallback))
                {
                    addonDefinition.Display = addDisplayInfoCallback as CustomDisplayCallback;
                }

                if (addonSpec.TryGetValue("Display", out var displayCallback))
                {
                    addonDefinition.DisplayV2 = displayCallback as CustomDisplayCallbackV2;
                }

                return addonDefinition;
            }

            public string AddonName;
            public Plugin OwnerPlugin;
            private CustomInitializeCallback Initialize;
            private CustomInitializeCallbackV2 InitializeV2;
            private CustomEditCallback Edit;
            private CustomSpawnCallback Spawn;
            private CustomSpawnCallbackV2 SpawnV2;
            public CustomCheckSpaceCallback CheckSpace;
            public CustomKillCallback Kill;
            public CustomUnloadCallback Unload;
            private CustomUpdateCallback Update;
            private CustomUpdateCallbackV2 UpdateV2;
            private CustomDisplayCallback Display;
            private CustomDisplayCallbackV2 DisplayV2;
            public bool IsValid = true;

            public List<CustomAddonAdapter> AdapterUsers = new();
            public List<CustomAddonSpawnPointInstance> SpawnPointInstances = new();

            public bool SupportsEditing => Edit != null && (Update != null || UpdateV2 != null);

            public Dictionary<string, object> ToApiResult(ProfileStore profileStore)
            {
                return new Dictionary<string, object>
                {
                    ["SetData"] = new CustomSetDataCallback((component, data) => SetData(profileStore, component, data)),
                };
            }

            public bool Validate()
            {
                if (Spawn == null && SpawnV2 == null)
                {
                    LogError($"Unable to register custom addon \"{AddonName}\" for plugin {OwnerPlugin.Name} due to missing Spawn method.");
                    return false;
                }

                if (Kill == null)
                {
                    LogError($"Unable to register custom addon \"{AddonName}\" for plugin {OwnerPlugin.Name} due to missing Kill method.");
                    return false;
                }

                return true;
            }

            public bool TryInitialize(BasePlayer player, string[] args, out object data)
            {
                try
                {
                    if (Initialize != null)
                    {
                        try
                        {
                            data = Initialize.Invoke(player, args);
                            return true;
                        }
                        catch (ArgumentException)
                        {
                            // Don't log argument exception, assume that the addon plugin threw this intentionally.
                            data = null;
                            return false;
                        }
                    }

                    if (InitializeV2 != null)
                    {
                        (var success, data) = InitializeV2.Invoke(player, args);
                        return success;
                    }
                }
                catch (Exception ex)
                {
                    data = null;
                    LogError($"Caught exception when calling plugin '{OwnerPlugin}' to initialize custom addon '{AddonName}': {ex}");
                    return false;
                }

                data = null;
                return true;
            }

            public bool TryEdit(BasePlayer player, string[] args, Component component, JObject data, out object newData)
            {
                try
                {
                    (var success, newData) = Edit(player, args, component, data);
                    return success;
                }
                catch (Exception ex)
                {
                    LogError($"Caught exception when calling plugin '{OwnerPlugin}' to initialize custom addon '{AddonName}': {ex}");
                    newData = null;
                    return false;
                }
            }

            public Component DoSpawn(Guid guid, Component monument, Vector3 position, Quaternion rotation, JObject jObject)
            {
                return Spawn?.Invoke(position, rotation, jObject)
                    ?? SpawnV2?.Invoke(guid, monument, position, rotation, jObject);
            }

            public Component DoUpdate(Component component, JObject data)
            {
                if (Update != null)
                {
                    Update(component, data);
                    return component;
                }

                if (UpdateV2 != null)
                    return UpdateV2(component, data);

                // This should not happen.
                return component;
            }

            public void SetData(ProfileStore profileStore, CustomAddonController controller, object data)
            {
                controller.CustomAddonData.SetData(data);
                profileStore.Save(controller.Profile);

                controller.StartUpdateRoutine();
            }

            public void SetData(ProfileStore profileStore, Component component, object data)
            {
                if (Update == null && UpdateV2 == null)
                {
                    LogError($"Unable to set data for custom addon \"{AddonName}\" due to missing Update method.");
                    return;
                }

                var matchingAdapter = AdapterUsers.FirstOrDefault(adapter => adapter.Component == component);
                if (matchingAdapter == null)
                {
                    LogError($"Unable to set data for custom addon \"{AddonName}\" because it has no spawned instances.");
                    return;
                }

                if (matchingAdapter.Controller is not CustomAddonController controller)
                    return;

                SetData(profileStore, controller, data);
            }

            public void DoDisplay(Component component, JObject data, BasePlayer player, StringBuilder sb, float duration)
            {
                Display?.Invoke(component, data, sb);
                DisplayV2?.Invoke(component, data, player, sb, duration);
            }
        }

        private class CustomAddonManager
        {
            private MonumentAddons _plugin;
            private Dictionary<string, CustomAddonDefinition> _customAddonsByName = new Dictionary<string, CustomAddonDefinition>();
            private Dictionary<string, List<CustomAddonDefinition>> _customAddonsByPlugin = new Dictionary<string, List<CustomAddonDefinition>>();

            public IEnumerable<CustomAddonDefinition> GetAllAddons()
            {
                return _customAddonsByName.Values;
            }

            public CustomAddonManager(MonumentAddons plugin)
            {
                _plugin = plugin;
            }

            public bool IsRegistered(string addonName, out Plugin otherPlugin)
            {
                otherPlugin = null;
                if (_customAddonsByName.TryGetValue(addonName, out var existingAddon))
                {
                    otherPlugin = existingAddon.OwnerPlugin;
                    return true;
                }

                return false;
            }

            public void RegisterAddon(CustomAddonDefinition addonDefinition)
            {
                _customAddonsByName[addonDefinition.AddonName] = addonDefinition;

                var addonsForPlugin = GetAddonsForPlugin(addonDefinition.OwnerPlugin);
                if (addonsForPlugin == null)
                {
                    addonsForPlugin = new List<CustomAddonDefinition>();
                    _customAddonsByPlugin[addonDefinition.OwnerPlugin.Name] = addonsForPlugin;
                }

                addonsForPlugin.Add(addonDefinition);

                if (_plugin._serverInitialized)
                {
                    foreach (var profileController in _plugin._profileManager.GetEnabledProfileControllers())
                    {
                        foreach (var spawnGroupAdapter in profileController.GetAdapters<SpawnGroupAdapter>())
                        {
                            spawnGroupAdapter.UpdateCustomAddons(addonDefinition);
                        }

                        foreach (var monumentEntry in profileController.Profile.MonumentDataMap)
                        {
                            var monumentName = monumentEntry.Key;
                            var monumentData = monumentEntry.Value;

                            foreach (var customAddonData in monumentData.CustomAddons)
                            {
                                if (customAddonData.AddonName == addonDefinition.AddonName)
                                {
                                    profileController.SpawnNewData(customAddonData, _plugin.GetMonumentsByIdentifier(monumentName));
                                }
                            }
                        }
                    }
                }
            }

            public void UnregisterAllForPlugin(Plugin plugin)
            {
                if (_customAddonsByName.Count == 0)
                    return;

                var addonsForPlugin = GetAddonsForPlugin(plugin);
                if (addonsForPlugin == null)
                    return;

                var controllerList = new HashSet<CustomAddonController>();

                foreach (var addonDefinition in addonsForPlugin)
                {
                    addonDefinition.IsValid = false;

                    foreach (var adapter in addonDefinition.AdapterUsers)
                    {
                        controllerList.Add(adapter.Controller as CustomAddonController);

                        // Remove the controller from the profile,
                        // since we may need to respawn it immediately after as part of the other plugin reloading.
                        adapter.Controller.ProfileController.OnControllerKilled(adapter.Controller);
                    }

                    foreach (var spawnPointInstance in addonDefinition.SpawnPointInstances)
                    {
                        spawnPointInstance.Kill();
                    }

                    _customAddonsByName.Remove(addonDefinition.AddonName);
                }

                foreach (var controller in controllerList)
                {
                    controller.PreUnload();
                }

                CoroutineManager.StartGlobalCoroutine(DestroyControllersRoutine(controllerList));

                _customAddonsByPlugin.Remove(plugin.Name);
            }

            public CustomAddonDefinition GetAddon(string addonName)
            {
                return _customAddonsByName.GetValueOrDefault(addonName);
            }

            private List<CustomAddonDefinition> GetAddonsForPlugin(Plugin plugin)
            {
                return _customAddonsByPlugin.GetValueOrDefault(plugin.Name);
            }

            private IEnumerator DestroyControllersRoutine(ICollection<CustomAddonController> controllerList)
            {
                foreach (var controller in controllerList)
                {
                    yield return controller.KillRoutine();
                }
            }
        }

        private class CustomAddonAdapter : TransformAdapter
        {
            public CustomAddonData CustomAddonData { get; }
            public CustomAddonDefinition AddonDefinition { get; }

            public override Component Component => _component;
            public override Transform Transform => _transform;
            public override Vector3 Position => _transform.position;
            public override Quaternion Rotation => _transform.rotation;
            public override bool IsValid => Component != null && (Component is not BaseEntity { IsDestroyed: true });

            private Component _component;
            private Transform _transform;
            private bool _wasKilled;

            public CustomAddonAdapter(CustomAddonData customAddonData, BaseController controller, BaseMonument monument, CustomAddonDefinition addonDefinition) : base(customAddonData, controller, monument)
            {
                CustomAddonData = customAddonData;
                AddonDefinition = addonDefinition;
            }

            public override void Spawn()
            {
                var component = AddonDefinition.DoSpawn(CustomAddonData.Id, Monument.Object, IntendedPosition, IntendedRotation, CustomAddonData.GetSerializedData());
                AddonDefinition.AdapterUsers.Add(this);
                SetupComponent(component);
            }

            public override void PreUnload()
            {
                AddonDefinition.Unload?.Invoke(Component);
            }

            public override void Kill()
            {
                if (_wasKilled)
                    return;

                _wasKilled = true;
                AddonDefinition.Kill(_component);
            }

            public override void OnComponentDestroyed(Component component)
            {
                // Don't kill the addon if the component was replaced.
                if (component != _component)
                    return;

                // In case it's a multi-part addon, call Kill() to ensure the whole addon is removed.
                Kill();

                AddonDefinition.AdapterUsers.Remove(this);
                Controller.OnAdapterKilled(this);
            }

            public void SetupComponent(Component component)
            {
                _component = component;
                _transform = component.transform;
                AddonComponent.AddToComponent(Plugin._componentTracker, component, this);
            }

            public void HandleChanges()
            {
                UpdatePosition();
                UpdateViaOwnerPlugin();
            }

            private void UpdateViaOwnerPlugin()
            {
                if (!AddonDefinition.SupportsEditing)
                    return;

                var newComponent = AddonDefinition.DoUpdate(_component, CustomAddonData.GetSerializedData());
                if (newComponent != _component)
                {
                    SetupComponent(newComponent);
                }
            }

            private void UpdatePosition()
            {
                if (IsAtIntendedPosition)
                    return;

                _component.transform.SetPositionAndRotation(IntendedPosition, IntendedRotation);

                if (_component is BaseEntity entity)
                {
                    BroadcastEntityTransformChange(entity);
                }
            }
        }

        private class CustomAddonController : BaseController, IUpdateableController
        {
            public CustomAddonData CustomAddonData { get; }

            private CustomAddonDefinition _addonDefinition;

            public CustomAddonController(ProfileController profileController, CustomAddonData customAddonData, CustomAddonDefinition addonDefinition) : base(profileController, customAddonData)
            {
                CustomAddonData = customAddonData;
                _addonDefinition = addonDefinition;
            }

            public override BaseAdapter CreateAdapter(BaseMonument monument)
            {
                return new CustomAddonAdapter(CustomAddonData, this, monument, _addonDefinition);
            }

            public Coroutine StartUpdateRoutine()
            {
                return ProfileController.StartCoroutine(UpdateRoutine());
            }

            private IEnumerator UpdateRoutine()
            {
                foreach (var adapter in Adapters.ToList())
                {
                    if (!_addonDefinition.IsValid)
                        yield break;

                    var customAddonAdapter = adapter as CustomAddonAdapter;
                    if (customAddonAdapter is not { IsValid: true })
                        continue;

                    customAddonAdapter.HandleChanges();
                    yield return null;
                }
            }
        }

        #endregion

        #region Controller Factories

        private class EntityControllerFactory
        {
            public virtual bool AppliesToEntity(BaseEntity entity)
            {
                return true;
            }

            public virtual EntityController CreateController(ProfileController controller, EntityData entityData)
            {
                return new EntityController(controller, entityData);
            }
        }

        private class SignControllerFactory : EntityControllerFactory
        {
            public override bool AppliesToEntity(BaseEntity entity)
            {
                return entity is ISignage;
            }

            public override EntityController CreateController(ProfileController controller, EntityData entityData)
            {
                return new SignController(controller, entityData);
            }
        }

        private class CCTVControllerFactory : EntityControllerFactory
        {
            public override bool AppliesToEntity(BaseEntity entity)
            {
                return entity is CCTV_RC;
            }

            public override EntityController CreateController(ProfileController controller, EntityData entityData)
            {
                return new CCTVController(controller, entityData);
            }
        }

        private class ControllerFactory
        {
            private MonumentAddons _plugin;

            public ControllerFactory(MonumentAddons plugin)
            {
                _plugin = plugin;
            }

            private EntityControllerFactory[] _entityFactories =
            {
                // The first that matches will be used.
                new CCTVControllerFactory(),
                new SignControllerFactory(),
                new EntityControllerFactory(),
            };

            public BaseController CreateController(ProfileController profileController, BaseData data)
            {
                if (data is SpawnGroupData spawnGroupData)
                    return new SpawnGroupController(profileController, spawnGroupData);

                if (data is PasteData pasteData)
                    return new PasteController(profileController, pasteData);

                if (data is CustomAddonData customAddonData)
                {
                    var addonDefinition = _plugin._customAddonManager.GetAddon(customAddonData.AddonName);
                    return addonDefinition != null
                        ? new CustomAddonController(profileController, customAddonData, addonDefinition)
                        : null;
                }

                if (data is EntityData entityData)
                    return ResolveEntityFactory(entityData)?.CreateController(profileController, entityData);

                if (data is PrefabData prefabData)
                    return new PrefabController(profileController, prefabData);

                return null;
            }

            private EntityControllerFactory ResolveEntityFactory(EntityData entityData)
            {
                var baseEntity = FindPrefabBaseEntity(entityData.PrefabName);
                if (baseEntity == null)
                    return null;

                foreach (var controllerFactory in _entityFactories)
                {
                    if (controllerFactory.AppliesToEntity(baseEntity))
                        return controllerFactory;
                }

                return null;
            }
        }

        #endregion

        #endregion

        #region IO Manager

        private class IOManager
        {
            private const int FreePowerAmount = 1000;

            private static bool HasInput(IOEntity ioEntity, IOType ioType)
            {
                foreach (var input in ioEntity.inputs)
                {
                    if (input.type == ioType)
                        return true;
                }

                return false;
            }

            private static bool HasConnectedInput(IOEntity ioEntity, int inputSlot)
            {
                return inputSlot < ioEntity.inputs.Length
                    && ioEntity.inputs[inputSlot].connectedTo.Get() != null;
            }

            private readonly int[] _defaultInputSlots = { 0 };

            private readonly Type[] _dontPowerPrefabsOfType =
            {
                typeof(ANDSwitch),
                typeof(CustomDoorManipulator),
                typeof(DoorManipulator),
                typeof(ORSwitch),
                typeof(RFBroadcaster),
                typeof(TeslaCoil),
                typeof(XORSwitch),

                // Has inputs to move the lift but does not consume power.
                typeof(Elevator),

                // Has inputs to toggle on/off but does not consume power.
                typeof(FuelGenerator),

                // Has audio input only.
                typeof(AudioVisualisationEntityLight),
                typeof(ConnectedSpeaker),

                // Has no power input.
                typeof(FogMachine),
                typeof(SnowMachine),
                typeof(StrobeLight),
            };

            private readonly Dictionary<string, int[]> _inputSlotsByPrefabName = new Dictionary<string, int[]>
            {
                ["assets/prefabs/deployable/playerioents/gates/combiner/electrical.combiner.deployed.prefab"] = new[] { 0, 1 },
                ["assets/prefabs/deployable/playerioents/fluidswitch/fluidswitch.prefab"] = new[] { 2 },
                ["assets/prefabs/deployable/playerioents/industrialconveyor/industrialconveyor.deployed.prefab"] = new[] { 1 },
                ["assets/prefabs/deployable/playerioents/industrialcrafter/industrialcrafter.deployed.prefab"] = new[] { 1 },
                ["assets/prefabs/deployable/playerioents/poweredwaterpurifier/poweredwaterpurifier.deployed.prefab"] = new[] { 1 },
            };

            private readonly Dictionary<uint, int[]> _inputSlotsByPrefabId = new Dictionary<uint, int[]>();

            private List<uint> _dontPowerPrefabIds = new List<uint>();

            public void OnServerInitialized()
            {
                foreach (var prefabPath in GameManifest.Current.entities)
                {
                    var ioEntity = FindPrefabComponent<IOEntity>(prefabPath);
                    if (ioEntity == null || !HasInput(ioEntity, IOType.Electric))
                        continue;

                    if (_dontPowerPrefabsOfType.Contains(ioEntity.GetType()))
                    {
                        _dontPowerPrefabIds.Add(ioEntity.prefabID);
                    }
                }

                foreach (var entry in _inputSlotsByPrefabName)
                {
                    var ioEntity = FindPrefabComponent<IOEntity>(entry.Key);
                    if (ioEntity == null)
                        continue;

                    _inputSlotsByPrefabId[ioEntity.prefabID] = entry.Value;
                }
            }

            public bool MaybeProvidePower(IOEntity ioEntity)
            {
                if (_dontPowerPrefabIds.Contains(ioEntity.prefabID))
                    return false;

                var providedPower = false;

                var inputSlotList = DeterminePowerInputSlots(ioEntity);

                foreach (var inputSlot in inputSlotList)
                {
                    if (inputSlot >= ioEntity.inputs.Length
                        || HasConnectedInput(ioEntity, inputSlot))
                        continue;

                    if (ioEntity.inputs[inputSlot].type != IOType.Electric)
                        continue;

                    ioEntity.UpdateFromInput(FreePowerAmount, inputSlot);
                    providedPower = true;
                }

                return providedPower;
            }

            private int[] DeterminePowerInputSlots(IOEntity ioEntity)
            {
                return _inputSlotsByPrefabId.TryGetValue(ioEntity.prefabID, out var inputSlots)
                    ? inputSlots
                    : _defaultInputSlots;
            }
        }

        #endregion

        #region Adapter Display Manager

        private class AdapterDisplayManager
        {
            private MonumentAddons _plugin;
            private UniqueNameRegistry _uniqueNameRegistry;
            private Configuration _config => _plugin._config;

            public const int DefaultDisplayDuration = 60;
            public const int HeaderSize = 25;
            public static readonly string Divider = $"<size={HeaderSize}>------------------------------</size>";
            public static readonly Vector3 ArrowVerticalOffeset = new Vector3(0, 0.5f, 0);

            private const float DisplayIntervalDurationFast = 0.01f;
            private const float DisplayIntervalDuration = 2;

            private class PlayerInfo
            {
                public Timer Timer;
                public ProfileController ProfileController;
                public TransformAdapter MovingAdapter;
                public CustomMonument MovingCustomMonument;
                public RealTimeSince RealTimeSinceShown;

                public float DisplayDurationSlow => DisplayIntervalDuration * 1.1f;
                public float DisplayDurationFast => Performance.report.frameTime / 1000f + 0.01f;

                public float GetDisplayDuration(BaseAdapter adapter)
                {
                    return MovingAdapter == adapter ? DisplayDurationFast : DisplayDurationSlow;
                }

                public float GetDisplayDuration(CustomMonument monument)
                {
                    return monument == MovingCustomMonument ? DisplayDurationFast : DisplayDurationSlow;
                }
            }

            private float DisplayDistanceSquared => Mathf.Pow(_config.DebugDisplaySettings.DisplayDistance, 2);
            private float DisplayDistanceAbbreviatedSquared => Mathf.Pow(_config.DebugDisplaySettings.DisplayDistanceAbbreviated, 2);

            private StringBuilder _sb = new StringBuilder(200);
            private Dictionary<ulong, PlayerInfo> _playerInfo = new Dictionary<ulong, PlayerInfo>();

            public AdapterDisplayManager(MonumentAddons plugin, UniqueNameRegistry uniqueNameRegistry)
            {
                _plugin = plugin;
                _uniqueNameRegistry = uniqueNameRegistry;
            }

            public void SetPlayerProfile(BasePlayer player, ProfileController profileController)
            {
                GetOrCreatePlayerInfo(player).ProfileController = profileController;
            }

            public void SetPlayerMovingAdapter(BasePlayer player, TransformAdapter adapter)
            {
                var playerInfo = GetOrCreatePlayerInfo(player);
                playerInfo.MovingAdapter = adapter;
                playerInfo.MovingCustomMonument = adapter is CustomAddonAdapter { IsValid: true } customAddonAdapter
                    ? customAddonAdapter.Component.GetComponent<CustomMonumentComponent>()?.Monument
                    : null;
            }

            public void ShowAllRepeatedly(BasePlayer player, int duration = -1, bool immediate = true)
            {
                var playerInfo = GetOrCreatePlayerInfo(player);

                if (immediate || playerInfo.Timer == null || playerInfo.Timer.Destroyed)
                {
                    playerInfo.RealTimeSinceShown = float.MaxValue;
                    ShowNearbyAdapters(player, player.transform.position, playerInfo);
                }

                if (playerInfo.Timer is { Destroyed: false })
                {
                    if (duration == 0)
                    {
                        playerInfo.Timer.Destroy();
                    }
                    else
                    {
                        var remainingTime = playerInfo.Timer.Repetitions * DisplayIntervalDurationFast;
                        var newDuration = duration > 0 ? duration : Math.Max(remainingTime, DefaultDisplayDuration);
                        var newRepetitions = Math.Max(Mathf.CeilToInt(newDuration / DisplayIntervalDurationFast), 1);
                        playerInfo.Timer.Reset(delay: -1, repetitions: newRepetitions);
                    }
                    return;
                }

                if (duration == -1)
                {
                    duration = DefaultDisplayDuration;
                }

                // Ensure repetitions is not 0 since that would result in infintire repetitions.
                var repetitions = Math.Max(Mathf.CeilToInt(duration / DisplayIntervalDurationFast), 1);

                playerInfo.Timer = _plugin.timer.Repeat(DisplayIntervalDurationFast, repetitions, () =>
                {
                    if (player == null || player.IsDestroyed || !player.IsConnected)
                    {
                        playerInfo.Timer.Destroy();
                        _playerInfo.Remove(player.userID);
                        return;
                    }

                    ShowNearbyAdapters(player, player.transform.position, playerInfo);
                });
            }

            private Color DetermineColor(BaseAdapter adapter, PlayerInfo playerInfo, ProfileController profileController)
            {
                if (playerInfo.ProfileController != null && playerInfo.ProfileController != profileController)
                    return _config.DebugDisplaySettings.InactiveProfileColor;

                if (adapter is SpawnPointAdapter or SpawnGroupAdapter)
                    return _config.DebugDisplaySettings.SpawnPointColor;

                if (adapter is PasteAdapter)
                    return _config.DebugDisplaySettings.PasteColor;

                if (adapter is CustomAddonAdapter)
                    return _config.DebugDisplaySettings.CustomAddonColor;

                return _config.DebugDisplaySettings.EntityColor;
            }

            private void AddCommonInfo(BasePlayer player, ProfileController profileController, BaseController controller, BaseAdapter adapter)
            {
                _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelProfile, profileController.Profile.Name));

                var monumentTierList = adapter.Monument.IsValid ? GetTierList(GetMonumentTierMask(adapter.Monument.Position)) : null;
                _sb.AppendLine(adapter.Monument is not IDynamicMonument && monumentTierList?.Count > 0
                    ? _plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelMonumentWithTier, adapter.Monument.UniqueDisplayName, controller.Adapters.Count, string.Join(", ", monumentTierList))
                    : _plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelMonument, adapter.Monument.UniqueDisplayName, controller.Adapters.Count));
            }

            private void ShowPuzzleInfo(BasePlayer player, EntityAdapter entityAdapter, PuzzleReset puzzleReset, Vector3 playerPosition, PlayerInfo playerInfo)
            {
                _sb.AppendLine($"<size=25>{_plugin.GetMessage(player.UserIDString, LangEntry.ShowHeaderPuzzle)}</size>");
                _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPuzzlePlayersBlockReset, puzzleReset.playersBlockReset));

                if (puzzleReset.playersBlockReset)
                {
                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPlayerDetectionRadius, puzzleReset.playerDetectionRadius));
                    if (PuzzleReset.AnyPlayersWithinDistance(puzzleReset.playerDetectionOrigin, puzzleReset.playerDetectionRadius))
                    {
                        _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPlayerDetectedInRadius));
                    }
                }

                _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPuzzleTimeBetweenResets, FormatTime(puzzleReset.timeBetweenResets)));

                var resetTimeElapsedField = typeof(PuzzleReset).GetField("resetTimeElapsed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (resetTimeElapsedField != null)
                {
                    var resetTimeElapsed = (float)resetTimeElapsedField.GetValue(puzzleReset);
                    var timeRemaining = puzzleReset.GetResetSpacing() - resetTimeElapsed;
                    var nextResetMessage = timeRemaining > 0
                        ? FormatTime(timeRemaining)
                        : _plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPuzzleNextResetOverdue);

                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPuzzleNextReset, nextResetMessage));
                }

                if (entityAdapter != null)
                {
                    var profileController = entityAdapter.ProfileController;

                    var spawnGroupIdList = entityAdapter.EntityData.Puzzle?.SpawnGroupIds;
                    if (spawnGroupIdList != null)
                    {
                        List<string> spawnGroupNameList = null;

                        foreach (var spawnGroupId in spawnGroupIdList)
                        {
                            if (profileController.FindAdapter(spawnGroupId, entityAdapter.Monument) is not SpawnGroupAdapter spawnGroupAdapter)
                                continue;

                            spawnGroupNameList ??= Pool.GetList<string>();
                            spawnGroupNameList.Add(spawnGroupAdapter.SpawnGroupData.Name);

                            var spawnPointAdapter = FindClosestSpawnPointAdapter(spawnGroupAdapter, playerPosition);
                            if (spawnPointAdapter != null)
                            {
                                new Ddraw(player, playerInfo.GetDisplayDuration(spawnPointAdapter), DetermineColor(spawnPointAdapter, playerInfo, profileController))
                                    .Arrow(entityAdapter.Position + ArrowVerticalOffeset, spawnPointAdapter.Position + ArrowVerticalOffeset, 0.25f);
                            }
                        }

                        if (spawnGroupNameList != null)
                        {
                            _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPuzzleSpawnGroups, string.Join(", ", spawnGroupNameList)));
                            Pool.FreeList(ref spawnGroupNameList);
                        }
                    }
                }
            }

            private Ddraw CreateDrawer(BasePlayer player, BaseAdapter adapter, PlayerInfo playerInfo)
            {
                return new Ddraw(player, playerInfo.GetDisplayDuration(adapter), DetermineColor(adapter, playerInfo, adapter.ProfileController));
            }

            private void ShowEntityInfo(ref Ddraw drawer, BasePlayer player, EntityAdapter adapter, Vector3 playerPosition, PlayerInfo playerInfo)
            {
                var entityData = adapter.EntityData;
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;

                var uniqueEntityName = _uniqueNameRegistry.GetUniqueShortName(entityData.PrefabName);

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_plugin.GetMessage(player.UserIDString, LangEntry.ShowHeaderEntity, uniqueEntityName)}</size>");
                AddCommonInfo(player, profileController, controller, adapter);

                if (entityData.Skin != 0)
                {
                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelSkin, entityData.Skin));
                }

                if (entityData.Scale != 1)
                {
                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelScale, entityData.Scale));
                }

                var vehicleVendor = adapter.Entity as VehicleVendor;
                if (vehicleVendor != null)
                {
                    var vehicleSpawner = vehicleVendor.GetVehicleSpawner();
                    if (vehicleSpawner != null)
                    {
                        drawer.Arrow(adapter.Position + new Vector3(0, 1.5f, 0), vehicleSpawner.transform.position, 0.25f);
                    }
                }

                var doorManipulator = adapter.Entity as DoorManipulator;
                if (doorManipulator != null && doorManipulator.targetDoor != null)
                {
                    drawer.Arrow(adapter.Position, doorManipulator.targetDoor.transform.position, 0.2f);
                }

                var cctvIdentifier = entityData.CCTV?.RCIdentifier;
                if (cctvIdentifier != null)
                {
                    var identifier = (adapter as CCTVEntityAdapter)?.GetIdentifier();
                    if (identifier != null)
                    {
                        _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelRCIdentifier, identifier));
                    }
                }

                var puzzleReset = (adapter.Entity as IOEntity)?.GetComponent<PuzzleReset>();
                if (puzzleReset != null)
                {
                    _sb.AppendLine(Divider);
                    ShowPuzzleInfo(player, adapter, puzzleReset, playerPosition, playerInfo);
                }

                drawer.Text(adapter.Position, _sb.ToString());
            }

            private void ShowPrefabInfo(ref Ddraw drawer, BasePlayer player, PrefabAdapter adapter, PlayerInfo playerInfo)
            {
                var prefabData = adapter.PrefabData;
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;

                var uniqueEntityName = _uniqueNameRegistry.GetUniqueShortName(prefabData.PrefabName);

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_plugin.GetMessage(player.UserIDString, LangEntry.ShowHeaderPrefab, uniqueEntityName)}</size>");
                AddCommonInfo(player, profileController, controller, adapter);

                var position = adapter.Position;
                drawer.Sphere(position, 0.25f);
                drawer.Text(position, _sb.ToString());
            }

            private void ShowSpawnPointInfo(BasePlayer player, SpawnPointAdapter adapter, SpawnGroupAdapter spawnGroupAdapter, PlayerInfo playerInfo, bool showGroupInfo)
            {
                var spawnPointData = adapter.SpawnPointData;
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);
                var drawer = new Ddraw(player, playerInfo.GetDisplayDuration(adapter), color);

                var spawnGroupData = spawnGroupAdapter.SpawnGroupData;

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_plugin.GetMessage(player.UserIDString, LangEntry.ShowHeaderSpawnPoint, spawnGroupData.Name)}</size>");
                AddCommonInfo(player, profileController, controller, adapter);

                var booleanProperties = new List<string>();

                if (spawnPointData.Exclusive)
                {
                    booleanProperties.Add(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPointExclusive));
                }

                if (spawnPointData.RandomRotation)
                {
                    booleanProperties.Add(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPointRandomRotation));
                }

                if (spawnPointData.SnapToGround)
                {
                    booleanProperties.Add(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPointSnapToGround));
                }

                if (spawnPointData.CheckSpace)
                {
                    booleanProperties.Add(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPointCheckSpace));
                }

                if (booleanProperties.Count > 0)
                {
                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelFlags, string.Join(" | ", booleanProperties)));
                }

                if (spawnPointData.RandomRadius > 0)
                {
                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPointRandomRadius, spawnPointData.RandomRadius));
                }

                if (spawnPointData.PlayerDetectionRadius > 0)
                {
                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPlayerDetectionRadius, spawnPointData.PlayerDetectionRadius));
                }

                if (adapter.SpawnPoint.HasPlayersIntersecting())
                {
                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPlayerDetectedInRadius));
                }

                if (showGroupInfo)
                {
                    _sb.AppendLine(Divider);
                    _sb.AppendLine($"<size=25>{_plugin.GetMessage(player.UserIDString, LangEntry.ShowHeaderSpawnGroup, spawnGroupData.Name)}</size>");

                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPoints, spawnGroupData.SpawnPoints.Count));

                    var groupBooleanProperties = new List<string>();

                    if (spawnGroupData.InitialSpawn)
                    {
                        groupBooleanProperties.Add(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelInitialSpawn));
                    }

                    if (spawnGroupData.PreventDuplicates)
                    {
                        groupBooleanProperties.Add(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPreventDuplicates));
                    }

                    if (spawnGroupData.PauseScheduleWhileFull)
                    {
                        groupBooleanProperties.Add(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPauseScheduleWhileFull));
                    }

                    if (spawnGroupData.RespawnWhenNearestPuzzleResets)
                    {
                        groupBooleanProperties.Add(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelRespawnWhenNearestPuzzleResets) + (spawnGroupAdapter.AssociatedPuzzleReset == null ? " (!)" : ""));
                    }

                    if (groupBooleanProperties.Count > 0)
                    {
                        _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelFlags, string.Join(" | ", groupBooleanProperties)));
                    }

                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPopulation, spawnGroupAdapter.SpawnGroup.currentPopulation, spawnGroupData.MaxPopulation));
                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelRespawnPerTick, spawnGroupData.SpawnPerTickMin, spawnGroupData.SpawnPerTickMax));

                    var spawnGroup = spawnGroupAdapter.SpawnGroup;
                    if (spawnGroup.WantsTimedSpawn())
                    {
                        _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelRespawnDelay, FormatTime(spawnGroup.respawnDelayMin), FormatTime(spawnGroup.respawnDelayMax)));

                        var nextSpawnTime = GetTimeToNextSpawn(spawnGroup);
                        if (!float.IsPositiveInfinity(nextSpawnTime))
                        {
                            var nextSpawnMessage = spawnGroupData.PauseScheduleWhileFull && spawnGroup.currentPopulation >= spawnGroup.maxPopulation
                                ? _plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelNextSpawnPaused)
                                : nextSpawnTime <= 0
                                    ? _plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelNextSpawnQueued)
                                    : FormatTime(Mathf.CeilToInt(nextSpawnTime));

                            _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelNextSpawn, nextSpawnMessage));
                        }
                    }

                    if (spawnGroupData.Prefabs.Count > 0)
                    {
                        var totalWeight = spawnGroupData.TotalWeight;

                        _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelEntities));
                        foreach (var prefabEntry in spawnGroupData.Prefabs)
                        {
                            var relativeChance = (float)prefabEntry.Weight / totalWeight;
                            var displayName = prefabEntry.CustomAddonName ?? _uniqueNameRegistry.GetUniqueShortName(prefabEntry.PrefabName);
                            if (prefabEntry.CustomAddonName != null && _plugin._customAddonManager.GetAddon(prefabEntry.CustomAddonName) == null)
                            {
                                displayName += " (!)";
                            }
                            _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelEntityDetail, displayName, prefabEntry.Weight, relativeChance));
                        }
                    }
                    else
                    {
                        _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelNoEntities));
                    }

                    foreach (var otherAdapter in spawnGroupAdapter.SpawnPointAdapters)
                    {
                        drawer.Arrow(otherAdapter.Position + ArrowVerticalOffeset, adapter.Position + ArrowVerticalOffeset, 0.25f);
                    }
                }

                drawer.Arrow(adapter.Position + ArrowVerticalOffeset, adapter.Rotation, 1f, 0.15f);
                drawer.Sphere(adapter.Position, 0.5f);
                drawer.Text(adapter.Position, _sb.ToString());

                if (spawnGroupData.RespawnWhenNearestPuzzleResets)
                {
                    _sb.Clear();

                    var puzzleReset = spawnGroupAdapter.AssociatedPuzzleReset;
                    if (puzzleReset != null)
                    {
                        ShowPuzzleInfo(player, null, spawnGroupAdapter.AssociatedPuzzleReset, player.transform.position, playerInfo);
                        var position = puzzleReset.transform.position;
                        drawer.Arrow(position + ArrowVerticalOffeset, adapter.Position + ArrowVerticalOffeset, 0.25f, color: DetermineColor(adapter, playerInfo, profileController));
                        drawer.Text(position, _sb.ToString());
                    }
                }
            }

            private void ShowPasteInfo(ref Ddraw drawer, BasePlayer player, PasteAdapter adapter)
            {
                var pasteData = adapter.PasteData;
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_plugin.GetMessage(player.UserIDString, LangEntry.ShowHeaderPaste, pasteData.Filename)}</size>");
                AddCommonInfo(player, profileController, controller, adapter);
                if (pasteData.Args is { Length: > 0 })
                {
                    _sb.AppendLine(string.Join(" ", pasteData.Args));
                }

                drawer.Box(adapter.Bounds, 0.25f);
                drawer.Text(adapter.Position, _sb.ToString());
            }

            private void ShowCustomAddonInfo(ref Ddraw drawer, BasePlayer player, CustomAddonAdapter adapter)
            {
                var customAddonData = adapter.CustomAddonData;
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;

                var addonDefinition = adapter.AddonDefinition;

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_plugin.GetMessage(player.UserIDString, LangEntry.ShowHeaderCustom, customAddonData.AddonName)}</size>");
                _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPlugin, addonDefinition.OwnerPlugin.Name));
                AddCommonInfo(player, profileController, controller, adapter);

                addonDefinition.DoDisplay(adapter.Component, customAddonData.GetSerializedData(), player, _sb, drawer.Duration);

                drawer.Text(adapter.Position, _sb.ToString());
            }

            private SpawnPointAdapter FindClosestSpawnPointAdapter(SpawnGroupAdapter spawnGroupAdapter, Vector3 origin)
            {
                SpawnPointAdapter closestSpawnPointAdapter = null;
                var closestDistanceSquared = float.MaxValue;

                foreach (var spawnPointAdapter in spawnGroupAdapter.SpawnPointAdapters)
                {
                    var adapterDistanceSquared = (spawnPointAdapter.Position - origin).sqrMagnitude;
                    if (adapterDistanceSquared < closestDistanceSquared)
                    {
                        closestSpawnPointAdapter = spawnPointAdapter;
                        closestDistanceSquared = adapterDistanceSquared;
                    }
                }

                return closestSpawnPointAdapter;
            }

            private SpawnPointAdapter FindClosestSpawnPointAdapterToRay(SpawnGroupAdapter spawnGroupAdapter, Ray ray)
            {
                SpawnPointAdapter closestSpawnPointAdapter = null;
                var highestDot = float.MinValue;

                foreach (var spawnPointAdapter in spawnGroupAdapter.SpawnPointAdapters)
                {
                    var dot = Vector3.Dot(ray.direction, (spawnPointAdapter.Position - ray.origin).normalized);
                    if (dot > highestDot)
                    {
                        closestSpawnPointAdapter = spawnPointAdapter;
                        highestDot = dot;
                    }
                }

                return closestSpawnPointAdapter;
            }

            private Vector3 GetClosestAdapterPosition(BaseAdapter adapter, Ray ray, out BaseAdapter closestAdapter)
            {
                if (adapter is TransformAdapter transformAdapter)
                {
                    closestAdapter = adapter;
                    return transformAdapter.Position;
                }

                if (adapter is SpawnGroupAdapter spawnGroupAdapter)
                {
                    var spawnPointAdapter = FindClosestSpawnPointAdapterToRay(spawnGroupAdapter, ray);
                    closestAdapter = spawnPointAdapter;
                    return spawnPointAdapter.Position;
                }

                closestAdapter = null;
                return Vector3.positiveInfinity;
            }

            private static bool IsWithinDistanceSquared(Vector3 position1, Vector3 position2, float distanceSquared)
            {
                return (position1 - position2).sqrMagnitude <= distanceSquared;
            }

            private static bool IsWithinDistanceSquared(TransformAdapter adapter, Vector3 position, float distanceSquared)
            {
                return IsWithinDistanceSquared(position, adapter.Position, distanceSquared);
            }

            private static void DrawAbbreviation(ref Ddraw drawer, TransformAdapter adapter)
            {
                drawer.Text(adapter.Position, "<size=25>*</size>");
            }

            private void DisplayMonument(BasePlayer player, PlayerInfo playerInfo, CustomMonument monument)
            {
                var drawer = new Ddraw(player, playerInfo.GetDisplayDuration(monument), _config.DebugDisplaySettings.CustomMonumentColor);

                // If an object is both a custom monument and an addon (perhaps even a custom addon),
                // don't show debug test since it will overlap.
                if (!_plugin._componentTracker.IsAddonComponent(monument.Object))
                {
                    _sb.Clear();
                    var monumentCount = _plugin._customMonumentManager.CountMonumentByName(monument.UniqueName);
                    _sb.AppendLine($"<size={HeaderSize}>{_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelCustomMonument, monument.UniqueDisplayName, monumentCount)}</size>");
                    _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPlugin, monument.OwnerPlugin.Name));
                    drawer.Text(monument.Position, _sb.ToString());
                }

                drawer.Box(monument.BoundingBox);
            }

            private void ShowNearbyCustomMonuments(BasePlayer player, Vector3 playerPosition, PlayerInfo playerInfo)
            {
                foreach (var monument in _plugin._customMonumentManager.MonumentList)
                {
                    if (monument == playerInfo.MovingCustomMonument)
                        continue;

                    if (!monument.IsInBounds(playerPosition)
                        && (playerPosition - monument.ClosestPointOnBounds(playerPosition)).sqrMagnitude > DisplayDistanceSquared)
                        continue;

                    DisplayMonument(player, playerInfo, monument);
                }
            }

            private void DisplayAdapter(BasePlayer player, Vector3 playerPosition, PlayerInfo playerInfo,
                BaseAdapter adapter, BaseAdapter closestAdapter, float distanceSquared, ref int remainingToShow)
            {
                var drawer = CreateDrawer(player, adapter, playerInfo);

                switch (adapter)
                {
                    case EntityAdapter entityAdapter:
                    {
                        if (remainingToShow-- > 0 && distanceSquared <= DisplayDistanceSquared)
                        {
                            ShowEntityInfo(ref drawer, player, entityAdapter, playerPosition, playerInfo);
                        }
                        else
                        {
                            DrawAbbreviation(ref drawer, entityAdapter);
                        }

                        return;
                    }
                    case PrefabAdapter prefabAdapter:
                    {
                        if (remainingToShow-- > 0 && distanceSquared <= DisplayDistanceSquared)
                        {
                            ShowPrefabInfo(ref drawer, player, prefabAdapter, playerInfo);
                        }
                        else
                        {
                            DrawAbbreviation(ref drawer, prefabAdapter);
                        }

                        return;
                    }
                    case SpawnPointAdapter spawnPointAdapter:
                    {
                        // This case only occurs when calling for the adapter being moved.
                        ShowSpawnPointInfo(player, spawnPointAdapter, spawnPointAdapter.SpawnGroupAdapter, playerInfo, showGroupInfo: true);
                        return;
                    }
                    case SpawnGroupAdapter spawnGroupAdapter:
                    {
                        var closestSpawnPointAdapter = closestAdapter as SpawnPointAdapter;
                        if (closestAdapter == null)
                            return;

                        if (remainingToShow-- > 0 && distanceSquared <= DisplayDistanceSquared)
                        {
                            ShowSpawnPointInfo(player, closestSpawnPointAdapter, spawnGroupAdapter, playerInfo, showGroupInfo: true);
                        }
                        else
                        {
                            DrawAbbreviation(ref drawer, closestSpawnPointAdapter);
                        }

                        foreach (var spawnPointAdapter in spawnGroupAdapter.SpawnPointAdapters)
                        {
                            if (IsWithinDistanceSquared(spawnPointAdapter, playerPosition, DisplayDistanceAbbreviatedSquared))
                            {
                                if (spawnPointAdapter == closestSpawnPointAdapter)
                                    continue;

                                DrawAbbreviation(ref drawer, spawnPointAdapter);
                            }
                        }

                        return;
                    }
                    case PasteAdapter pasteAdapter:
                    {
                        if (remainingToShow-- > 0 && distanceSquared <= DisplayDistanceSquared)
                        {
                            ShowPasteInfo(ref drawer, player, pasteAdapter);
                        }
                        else
                        {
                            DrawAbbreviation(ref drawer, pasteAdapter);
                        }

                        return;
                    }
                    case CustomAddonAdapter customAddonAdapter:
                    {
                        if (remainingToShow-- > 0 && distanceSquared <= DisplayDistanceSquared)
                        {
                            ShowCustomAddonInfo(ref drawer, player, customAddonAdapter);
                        }
                        else
                        {
                            DrawAbbreviation(ref drawer, customAddonAdapter);
                        }

                        return;
                    }
                }
            }

            private void ShowNearbyAdapters(BasePlayer player, Vector3 playerPosition, PlayerInfo playerInfo)
            {
                var remainingToShow = _config.DebugDisplaySettings.MaxAddonsToShowUnabbreviated;

                var movingAdapter = playerInfo.MovingAdapter;
                if (movingAdapter != null)
                {
                    if (movingAdapter.IsValid)
                    {
                        DisplayAdapter(player, playerPosition, playerInfo, movingAdapter, movingAdapter, 0, ref remainingToShow);

                        var movingMonument = playerInfo.MovingCustomMonument;
                        if (movingMonument != null)
                        {
                            DisplayMonument(player, playerInfo, movingMonument);
                        }
                    }
                    else
                    {
                        playerInfo.MovingAdapter = null;
                        playerInfo.MovingCustomMonument = null;
                    }
                }

                if (playerInfo.RealTimeSinceShown < DisplayIntervalDuration)
                    return;

                playerInfo.RealTimeSinceShown = 0;

                var isAdmin = player.IsAdmin;
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                ShowNearbyCustomMonuments(player, playerPosition, playerInfo);

                var headRay = player.eyes.HeadRay();

                foreach (var (adapter, closestAdapter, distanceSquared, _) in _plugin._profileManager.GetEnabledAdapters<BaseAdapter>()
                             .Where(adapter => adapter.IsValid)
                             .Select(adapter =>
                             {
                                 var position = GetClosestAdapterPosition(adapter, headRay, out var closestAdapter);
                                 var distanceSquared = (position - playerPosition).sqrMagnitude;
                                 return (adapter, closestAdapter, distanceSquared, position);
                             })
                             .Where(tuple => tuple.Item3 <= DisplayDistanceAbbreviatedSquared)
                             .OrderByDescending(tuple =>
                             {
                                 var dot = Vector3.Dot(headRay.direction, (tuple.Item4 - headRay.origin).normalized);
                                 if (tuple.Item3 > DisplayDistanceSquared)
                                 {
                                     dot -= 2;
                                 }
                                 return dot;
                             })
                         )
                {
                    if (adapter == playerInfo.MovingAdapter)
                        continue;

                    DisplayAdapter(player, playerPosition, playerInfo, adapter, closestAdapter, distanceSquared, ref remainingToShow);
                }

                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                    player.SendNetworkUpdateImmediate();
                }
            }

            private PlayerInfo GetOrCreatePlayerInfo(BasePlayer player)
            {
                if (!_playerInfo.TryGetValue(player.userID, out var playerInfo))
                {
                    playerInfo = new PlayerInfo();
                    _playerInfo[player.userID] = playerInfo;
                }

                return playerInfo;
            }
        }

        #endregion

        #region Profile Management

        private enum ProfileStatus { Loading, Loaded, Unloading, Unloaded }

        private struct SpawnQueueItem
        {
            public BaseData Data;
            public BaseMonument Monument;
            public ICollection<BaseMonument> MonumentList;

            public SpawnQueueItem(BaseData data, ICollection<BaseMonument> monumentList)
            {
                Data = data;
                Monument = null;
                MonumentList = monumentList;
            }

            public SpawnQueueItem(BaseData data, BaseMonument monument)
            {
                Data = data;
                Monument = monument;
                MonumentList = null;
            }
        }

        private class ProfileController
        {
            public MonumentAddons Plugin { get; }
            public Profile Profile { get; private set; }
            public ProfileStatus ProfileStatus { get; private set; } = ProfileStatus.Unloaded;
            public WaitUntil WaitUntilLoaded;
            public WaitUntil WaitUntilUnloaded;

            private Configuration _config => Plugin._config;
            private StoredData _pluginData => Plugin._data;
            private ProfileManager _profileManager => Plugin._profileManager;
            private ProfileStateData _profileStateData => Plugin._profileStateData;

            private CoroutineManager _coroutineManager = new CoroutineManager();
            private Dictionary<BaseData, BaseController> _controllersByData = new Dictionary<BaseData, BaseController>();
            private Queue<SpawnQueueItem> _spawnQueue = new Queue<SpawnQueueItem>();

            public bool IsEnabled => _pluginData.IsProfileEnabled(Profile.Name);

            public ProfileController(MonumentAddons plugin, Profile profile, bool startLoaded = false)
            {
                Plugin = plugin;
                Profile = profile;
                WaitUntilLoaded = new WaitUntil(() => ProfileStatus == ProfileStatus.Loaded);
                WaitUntilUnloaded = new WaitUntil(() => ProfileStatus == ProfileStatus.Unloaded);

                if (startLoaded)
                {
                    SetProfileStatus(ProfileStatus.Loaded);
                }
            }

            public void OnControllerKilled(BaseController controller)
            {
                _controllersByData.Remove(controller.Data);
            }

            public Coroutine StartCoroutine(IEnumerator enumerator)
            {
                return _coroutineManager.StartCoroutine(enumerator);
            }

            public Coroutine StartCallbackRoutine(Coroutine coroutine, Action callback)
            {
                return _coroutineManager.StartCallbackRoutine(coroutine, callback);
            }

            public IEnumerable<T> GetControllers<T>() where T : BaseController
            {
                return _controllersByData.Values.OfType<T>();
            }

            public IEnumerable<T> GetAdapters<T>() where T : BaseAdapter
            {
                foreach (var controller in _controllersByData.Values)
                {
                    foreach (var adapter in controller.Adapters)
                    {
                        if (adapter is T adapterOfType)
                        {
                            yield return adapterOfType;
                            continue;
                        }

                        if (adapter is SpawnGroupAdapter spawnGroupAdapter)
                        {
                            foreach (var childAdapter in spawnGroupAdapter.SpawnPointAdapters.OfType<T>())
                            {
                                yield return childAdapter;
                            }
                        }
                    }
                }
            }

            public void Load(ProfileCounts profileCounts = null)
            {
                if (ProfileStatus == ProfileStatus.Loading || ProfileStatus == ProfileStatus.Loaded)
                    return;

                CleanOrphanedEntities();
                EnqueueAll(profileCounts);

                if (_spawnQueue.Count == 0)
                {
                    SetProfileStatus(ProfileStatus.Loaded);
                }
            }

            public void PreUnload()
            {
                _coroutineManager.Destroy();

                foreach (var controller in _controllersByData.Values.ToList())
                {
                    controller.PreUnload();
                }
            }

            public void DetachSavedEntities()
            {
                if (_controllersByData.Count == 0)
                    return;

                foreach (var controller in _controllersByData.Values.ToList())
                {
                    controller.DetachSavedEntities();
                }
            }

            public void Unload(IEnumerator cleanupRoutine = null)
            {
                if (ProfileStatus == ProfileStatus.Unloading || ProfileStatus == ProfileStatus.Unloaded)
                    return;

                SetProfileStatus(ProfileStatus.Unloading);
                CoroutineManager.StartGlobalCoroutine(UnloadRoutine(cleanupRoutine));
            }

            public void Reload(Profile newProfileData)
            {
                Interrupt();
                PreUnload();
                DetachSavedEntities();

                StartCoroutine(ReloadRoutine(newProfileData));
            }

            public IEnumerator PartialLoadForLateMonument(ICollection<BaseData> dataList, BaseMonument monument)
            {
                foreach (var data in dataList)
                {
                    Enqueue(new SpawnQueueItem(data, monument));
                }

                yield return WaitUntilLoaded;
            }

            public void SpawnNewData(BaseData data, ICollection<BaseMonument> monumentList)
            {
                if (ProfileStatus == ProfileStatus.Unloading || ProfileStatus == ProfileStatus.Unloaded)
                    return;

                Enqueue(new SpawnQueueItem(data, monumentList));
            }

            public void Rename(string newName)
            {
                _pluginData.RenameProfileReferences(Profile.Name, newName);
                Plugin._originalProfileStore.MoveTo(Profile, newName);
                Plugin._profileStore.MoveTo(Profile, newName);
            }

            public void Enable(Profile newProfileData)
            {
                if (IsEnabled)
                    return;

                Profile = newProfileData;
                _pluginData.SetProfileEnabled(Profile.Name);
                Load();
            }

            public void Disable()
            {
                Interrupt();
                PreUnload();

                IEnumerator cleanupRoutine = null;

                DetachSavedEntities();

                var entitiesToKill = _profileStateData.FindAndRemoveValidEntities(Profile.Name);
                if (entitiesToKill is { Count: > 0 })
                {
                    Plugin._saveProfileStateDebounced.Schedule();
                    cleanupRoutine = KillEntitiesRoutine(entitiesToKill);
                }

                Unload(cleanupRoutine);
            }

            public void Clear()
            {
                if (!IsEnabled)
                {
                    Profile.MonumentDataMap.Clear();
                    Plugin._profileStore.Save(Profile);
                    return;
                }

                Interrupt();
                StartCoroutine(ClearRoutine());
            }

            public BaseController FindControllerById(Guid guid)
            {
                foreach (var entry in _controllersByData)
                {
                    if (entry.Key.Id == guid)
                        return entry.Value;
                }

                return null;
            }

            public BaseAdapter FindAdapter(Guid guid, BaseMonument monument)
            {
                return FindControllerById(guid)?.FindAdapterForMonument(monument);
            }

            public T FindEntity<T>(Guid guid, BaseMonument monument) where T : BaseEntity
            {
                return _profileStateData.FindEntity(Profile.Name, monument, guid) as T
                    ?? (FindAdapter(guid, monument) as EntityAdapter)?.Entity as T;
            }

            public void SetupIO()
            {
                // Setup connections first.
                foreach (var entry in _controllersByData)
                {
                    var data = entry.Key;
                    var controller = entry.Value;

                    var entityData = data as EntityData;
                    if (entityData?.IOEntityData == null)
                        continue;

                    if (controller is not EntityController entityController)
                        continue;

                    foreach (var adapter in entityController.Adapters)
                    {
                        (adapter as EntityAdapter)?.UpdateIOConnections();
                    }
                }

                // Provide free power to unconnected entities.
                foreach (var entry in _controllersByData)
                {
                    if (entry.Value is not EntityController singleEntityController)
                        continue;

                    foreach (var adapter in singleEntityController.Adapters)
                    {
                        (adapter as EntityAdapter)?.MaybeProvidePower();
                    }
                }
            }

            private void Interrupt()
            {
                _coroutineManager.StopAll();
                _spawnQueue.Clear();
            }

            private BaseController GetController(BaseData data)
            {
                return _controllersByData.TryGetValue(data, out var controller)
                    ? controller
                    : null;
            }

            private BaseController EnsureController(BaseData data)
            {
                var controller = GetController(data);
                if (controller == null)
                {
                    controller = Plugin._controllerFactory.CreateController(this, data);
                    if (controller != null)
                    {
                        _controllersByData[data] = controller;
                    }
                }

                return controller;
            }

            private void Enqueue(SpawnQueueItem queueItem)
            {
                _spawnQueue.Enqueue(queueItem);

                // If there are more items in the queue, we can assume there's already a coroutine processing them.
                if (_spawnQueue.Count == 1)
                {
                    SetProfileStatus(ProfileStatus.Loading);
                    StartCoroutine(ProcessSpawnQueue());
                }
            }

            private void EnqueueAll(ProfileCounts profileCounts)
            {
                foreach (var entry in Profile.MonumentDataMap)
                {
                    var monumentData = entry.Value;
                    if (monumentData.NumSpawnables == 0)
                        continue;

                    var monumentIdentifier = entry.Key;
                    var matchingMonuments = Plugin.GetMonumentsByIdentifier(monumentIdentifier);
                    if (matchingMonuments == null)
                        continue;

                    if (profileCounts != null)
                    {
                        profileCounts.EntityCount += matchingMonuments.Count * monumentData.Entities.Count;
                        profileCounts.SpawnPointCount += matchingMonuments.Count * monumentData.NumSpawnPoints;
                        profileCounts.PasteCount += matchingMonuments.Count * monumentData.Pastes.Count;
                        profileCounts.PrefabCount += matchingMonuments.Count * monumentData.Prefabs.Count;
                    }

                    foreach (var data in monumentData.GetSpawnablesLazy())
                    {
                        Enqueue(new SpawnQueueItem(data, matchingMonuments));
                    }
                }
            }

            private void SetProfileStatus(ProfileStatus newStatus, bool broadcast = true)
            {
                var previousStatus = ProfileStatus;
                ProfileStatus = newStatus;
                if (broadcast)
                {
                    _profileManager.BroadcastProfileStateChanged(this, ProfileStatus, previousStatus);
                }
            }

            private IEnumerator ProcessSpawnQueue()
            {
                // Wait one frame to ensure the queue has time to be populated.
                yield return null;

                while (_spawnQueue.TryDequeue(out var queueItem))
                {
                    Plugin.TrackStart();
                    var controller = EnsureController(queueItem.Data);
                    Plugin.TrackEnd();

                    if (controller == null)
                    {
                        // The controller factory may not have been implemented for this data type,
                        // or the custom addon owner plugin may not be loaded.
                        continue;
                    }

                    if (queueItem.Monument != null)
                    {
                        // Check for null in case the monument is dynamic and was destroyed (e.g., cargo ship).
                        if (queueItem.Monument.IsValid)
                        {
                            // Prevent double spawning addons (e.g., if a dynamic monument spawns while a profile is loading).
                            if (controller.HasAdapterForMonument(queueItem.Monument))
                            {
                                LogWarning("Prevented double spawn");
                                continue;
                            }

                            try
                            {
                                controller.SpawnAtMonument(queueItem.Monument);
                            }
                            catch (Exception ex)
                            {
                                LogError($"Caught exception when spawning addon {queueItem.Data.Id}.\n{ex}");
                            }

                            yield return null;
                        }
                    }
                    else
                    {
                        yield return controller.SpawnAtMonumentsRoutine(queueItem.MonumentList);
                    }
                }

                SetProfileStatus(ProfileStatus.Loaded);
                SetupIO();
            }

            private IEnumerator UnloadRoutine(IEnumerator cleanupRoutine)
            {
                foreach (var controller in _controllersByData.Values.ToList())
                {
                    yield return controller.KillRoutine();
                }

                if (cleanupRoutine != null)
                    yield return cleanupRoutine;

                SetProfileStatus(ProfileStatus.Unloaded);
            }

            private IEnumerator ReloadRoutine(Profile newProfileData)
            {
                Unload();
                yield return WaitUntilUnloaded;

                Profile = newProfileData;

                Load();
                yield return WaitUntilLoaded;
            }

            private IEnumerator ClearRoutine()
            {
                Unload();
                yield return WaitUntilUnloaded;

                Profile.MonumentDataMap.Clear();
                Plugin._profileStore.Save(Profile);
                SetProfileStatus(ProfileStatus.Loaded);
            }

            private IEnumerator CleanEntitiesRoutine(ProfileState profileState, List<BaseEntity> entitiesToKill)
            {
                yield return KillEntitiesRoutine(entitiesToKill);

                if (profileState.CleanStaleEntityRecords() > 0)
                {
                    Plugin._saveProfileStateDebounced.Schedule();
                }
            }

            private void CleanOrphanedEntities()
            {
                var profileState = _profileStateData.GetProfileState(Profile.Name);
                if (profileState == null)
                    return;

                List<BaseEntity> entitiesToKill = null;

                foreach (var entityEntry in profileState.FindValidEntities())
                {
                    if (!Profile.HasEntity(entityEntry.MonumentUniqueName, entityEntry.Guid))
                    {
                        entitiesToKill ??= new List<BaseEntity>();
                        entitiesToKill.Add(entityEntry.Entity);
                    }
                }

                if (entitiesToKill is { Count: > 0 })
                {
                    CoroutineManager.StartGlobalCoroutine(CleanEntitiesRoutine(profileState, entitiesToKill));
                }
                else if (profileState.CleanStaleEntityRecords() > 0)
                {
                    Plugin._saveProfileStateDebounced.Schedule();
                }
            }
        }

        private class ProfileCounts
        {
            public int EntityCount;
            public int PrefabCount;
            public int SpawnPointCount;
            public int PasteCount;
        }

        private class ProfileInfo
        {
            public static List<ProfileInfo> GetList(StoredData pluginData, ProfileManager profileManager)
            {
                var profileNameList = ProfileStore.GetProfileNames();
                var profileInfoList = new List<ProfileInfo>(profileNameList.Length);

                foreach (var profileName in profileNameList)
                {
                    if (OriginalProfileStore.IsOriginalProfile(profileName))
                        continue;

                    profileInfoList.Add(new ProfileInfo
                    {
                        Name = profileName,
                        Enabled = pluginData.EnabledProfiles.Contains(profileName),
                        Profile = profileManager.GetCachedProfileController(profileName)?.Profile
                    });
                }

                return profileInfoList;
            }

            public string Name;
            public bool Enabled;
            public Profile Profile;
        }

        private class ProfileManager
        {
            private readonly MonumentAddons _plugin;
            private OriginalProfileStore _originalProfileStore;
            private readonly ProfileStore _profileStore;
            private List<ProfileController> _profileControllers = new List<ProfileController>();

            private Configuration _config => _plugin._config;
            private StoredData _pluginData => _plugin._data;

            public event Action<ProfileController, ProfileStatus, ProfileStatus> ProfileStatusChanged;

            public bool HasAnyEnabledDynamicMonuments
            {
                get
                {
                    foreach (var profileController in _profileControllers)
                    {
                        if (profileController is { IsEnabled: true, Profile.HasAnyDynamicMonuments: true })
                            return true;
                    }

                    return false;
                }
            }

            public ProfileManager(MonumentAddons plugin, OriginalProfileStore originalProfileStore, ProfileStore profileStore)
            {
                _plugin = plugin;
                _originalProfileStore = originalProfileStore;
                _profileStore = profileStore;
            }

            public void BroadcastProfileStateChanged(ProfileController profileController, ProfileStatus status, ProfileStatus previousStatus)
            {
                ProfileStatusChanged?.Invoke(profileController, status, previousStatus);
            }

            public bool HasDynamicMonument(BaseEntity entity)
            {
                foreach (var profileController in _profileControllers)
                {
                    if (profileController.Profile.HasDynamicMonument(entity))
                        return true;
                }

                return false;
            }

            public IEnumerator LoadAllProfilesRoutine()
            {
                foreach (var profileName in _pluginData.EnabledProfiles.ToList())
                {
                    ProfileController controller;
                    try
                    {
                        controller = GetProfileController(profileName);
                    }
                    catch (Exception ex)
                    {
                        _pluginData.SetProfileDisabled(profileName);
                        LogError($"Disabled profile {profileName} due to error: {ex.Message}");
                        continue;
                    }

                    if (controller == null)
                    {
                        _pluginData.SetProfileDisabled(profileName);
                        LogWarning($"Disabled profile {profileName} because its data file was not found.");
                        continue;
                    }

                    var profileCounts = new ProfileCounts();

                    controller.Load(profileCounts);
                    yield return controller.WaitUntilLoaded;

                    var profile = controller.Profile;
                    var byAuthor = !string.IsNullOrWhiteSpace(profile.Author) ? $" by {profile.Author}" : string.Empty;

                    var spawnablesSummaryList = new List<string>();
                    if (profileCounts.EntityCount > 0)
                    {
                        spawnablesSummaryList.Add($"{profileCounts.EntityCount} entities");
                    }

                    if (profileCounts.PrefabCount > 0)
                    {
                        spawnablesSummaryList.Add($"{profileCounts.PrefabCount} prefabs");
                    }

                    if (profileCounts.SpawnPointCount > 0)
                    {
                        spawnablesSummaryList.Add($"{profileCounts.SpawnPointCount} spawn points");
                    }

                    if (profileCounts.PasteCount > 0)
                    {
                        spawnablesSummaryList.Add($"{profileCounts.PasteCount} pastes");
                    }

                    var spawnablesSummary = spawnablesSummaryList.Count > 0
                        ? string.Join(", ", spawnablesSummaryList)
                        : "No addons spawned";

                    LogInfo($"Loaded profile {profile.Name}{byAuthor} ({spawnablesSummary}).");
                }
            }

            public void UnloadAllProfiles()
            {
                foreach (var profileController in _profileControllers)
                {
                    profileController.PreUnload();
                    profileController.DetachSavedEntities();
                }

                CoroutineManager.StartGlobalCoroutine(UnloadAllProfilesRoutine());
            }

            public IEnumerator PartialLoadForLateMonumentRoutine(BaseMonument monument)
            {
                foreach (var controller in _profileControllers.ToArray())
                {
                    if (!controller.IsEnabled)
                        continue;

                    if (!controller.Profile.MonumentDataMap.TryGetValue(monument.UniqueName, out var monumentData))
                        continue;

                    if (monumentData.NumSpawnables == 0)
                        continue;

                    yield return controller.PartialLoadForLateMonument(monumentData.GetSpawnables(), monument);
                }
            }

            public ProfileController GetCachedProfileController(string profileName)
            {
                var profileNameLower = profileName.ToLower();

                foreach (var cachedController in _profileControllers)
                {
                    if (cachedController.Profile.Name.ToLower() == profileNameLower)
                        return cachedController;
                }

                return null;
            }

            public ProfileController GetProfileController(string profileName)
            {
                var profileController = GetCachedProfileController(profileName);
                if (profileController != null)
                    return profileController;

                var profile = _profileStore.LoadIfExists(profileName);
                if (profile != null)
                {
                    var controller = new ProfileController(_plugin, profile);
                    _profileControllers.Add(controller);
                    return controller;
                }

                return null;
            }

            public ProfileController GetPlayerProfileController(string userId)
            {
                return _pluginData.SelectedProfiles.TryGetValue(userId, out var profileName)
                    ? GetProfileController(profileName)
                    : null;
            }

            public ProfileController GetPlayerProfileControllerOrDefault(string userId)
            {
                var controller = GetPlayerProfileController(userId);
                if (controller != null)
                    return controller;

                controller = GetProfileController(DefaultProfileName);
                return controller is { IsEnabled: true }
                    ? controller
                    : null;
            }

            public bool ProfileExists(string profileName)
            {
                var profileNameLower = profileName.ToLower();

                foreach (var cachedController in _profileControllers)
                {
                    if (cachedController.Profile.Name.ToLower() == profileNameLower)
                        return true;
                }

                return _profileStore.Exists(profileName);
            }

            public ProfileController CreateProfile(string profileName, string authorName)
            {
                var profile = _profileStore.Create(profileName, authorName);
                var controller = new ProfileController(_plugin, profile, startLoaded: true);
                _profileControllers.Add(controller);
                return controller;
            }

            public void DisableProfile(ProfileController profileController)
            {
                _pluginData.SetProfileDisabled(profileController.Profile.Name);
                profileController.Disable();
            }

            public void DeleteProfile(ProfileController profileController)
            {
                if (profileController.IsEnabled)
                {
                    DisableProfile(profileController);
                }

                _profileControllers.Remove(profileController);
                _originalProfileStore.Delete(profileController.Profile.Name);
                _profileStore.Delete(profileController.Profile.Name);
            }

            public IEnumerable<ProfileController> GetEnabledProfileControllers()
            {
                foreach (var profileControler in _profileControllers)
                {
                    if (profileControler.IsEnabled)
                        yield return profileControler;
                }
            }

            public IEnumerable<T> GetEnabledControllers<T>() where T : BaseController
            {
                foreach (var profileController in GetEnabledProfileControllers())
                {
                    foreach (var controller in profileController.GetControllers<T>())
                    {
                        yield return controller;
                    }
                }
            }

            public IEnumerable<T> GetEnabledAdapters<T>() where T : BaseAdapter
            {
                foreach (var profileControler in GetEnabledProfileControllers())
                {
                    foreach (var adapter in profileControler.GetAdapters<T>())
                    {
                        yield return adapter;
                    }
                }
            }

            public IEnumerable<T> GetEnabledAdaptersForMonument<T>(BaseMonument monument) where T : BaseAdapter
            {
                return GetEnabledAdapters<T>().Where(adapter => adapter.Monument.IsEquivalentTo(monument));
            }

            private IEnumerator UnloadAllProfilesRoutine()
            {
                foreach (var controller in _profileControllers)
                {
                    controller.Unload();
                    yield return controller.WaitUntilUnloaded;
                }
            }
        }

        #endregion

        #region Data

        #region Base Data

        private abstract class BaseData
        {
            [JsonProperty("Id", Order = -10)]
            public Guid Id;
        }

        private abstract class BaseTransformData : BaseData
        {
            [JsonProperty("SnapToTerrain", Order = -4, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool SnapToTerrain;

            [JsonProperty("Position", Order = -3)]
            public Vector3 Position;

            // Kept for backwards compatibility.
            [JsonProperty("RotationAngle", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float DeprecatedRotationAngle { set => RotationAngles = new Vector3(0, value, 0); }

            [JsonProperty("RotationAngles", Order = -2, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Vector3 RotationAngles;

            [JsonProperty("OnTerrain")]
            public bool DepredcatedOnTerrain { set => SnapToTerrain = value; }
        }

        #endregion

        #region Prefab Data

        private class PrefabData : BaseTransformData
        {
            [JsonProperty("PrefabName", Order = -5)]
            public string PrefabName;
        }

        #endregion

        #region Entity Data

        private class BuildingBlockInfo
        {
            [JsonProperty("Grade")]
            [JsonConverter(typeof(StringEnumConverter))]
            [DefaultValue(BuildingGrade.Enum.None)]
            public BuildingGrade.Enum Grade = BuildingGrade.Enum.None;
        }

        private class CCTVInfo
        {
            [JsonProperty("RCIdentifier", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string RCIdentifier;

            [JsonProperty("Pitch", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Pitch;

            [JsonProperty("Yaw", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Yaw;
        }

        private class SignArtistImage
        {
            [JsonProperty("Url")]
            public string Url;

            [JsonProperty("Raw", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Raw;
        }

        private class IOConnectionData
        {
            [JsonProperty("ConnectedToId")]
            public Guid ConnectedToId;

            [JsonProperty("Slot")]
            public int Slot;

            [JsonProperty("ConnectedToSlot")]
            public int ConnectedToSlot;

            [JsonProperty("ShowWire")]
            public bool ShowWire = true;

            [JsonProperty("Color", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [JsonConverter(typeof(StringEnumConverter))]
            public WireColour Color;

            [JsonProperty("Points")]
            public Vector3[] Points;
        }

        private class IOEntityData
        {
            [JsonProperty("Outputs")]
            public List<IOConnectionData> Outputs = new List<IOConnectionData>();

            public IOConnectionData FindConnection(int slot)
            {
                foreach (var connectionData in Outputs)
                {
                    if (connectionData.Slot == slot)
                        return connectionData;
                }

                return null;
            }
        }

        private class PuzzleData
        {
            [JsonProperty("PlayersBlockReset")]
            public bool PlayersBlockReset;

            [JsonProperty("PlayerDetectionRadius")]
            public float PlayerDetectionRadius;

            [JsonProperty("SecondsBetweenResets")]
            public float SecondsBetweenResets;

            [JsonProperty("SpawnGroupIds", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public List<Guid> SpawnGroupIds;

            public bool ShouldSerializeSpawnGroupIds() => SpawnGroupIds?.Count > 0;

            public bool HasSpawnGroupId(Guid spawnGroupId)
            {
                return SpawnGroupIds?.Contains(spawnGroupId) ?? false;
            }

            public void AddSpawnGroupId(Guid spawnGroupId)
            {
                SpawnGroupIds ??= new List<Guid>();
                SpawnGroupIds.Add(spawnGroupId);
            }

            public void RemoveSpawnGroupId(Guid spawnGroupId)
            {
                SpawnGroupIds?.Remove(spawnGroupId);
            }
        }

        private class HeadData
        {
            private FieldInfo CurrentTrophyDataField = typeof(HuntingTrophy).GetField("CurrentTrophyData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            public static HeadData FromHeadEntity(HeadEntity headEntity)
            {
                var trophyData = headEntity.CurrentTrophyData;

                var headData = new HeadData
                {
                    EntitySource = trophyData.entitySource,
                    HorseBreed = trophyData.horseBreed,
                    PlayerId = trophyData.playerId,
                    PlayerName = !string.IsNullOrEmpty(trophyData.playerName) ? trophyData.playerName : null,
                };

                if (trophyData.clothing?.Count > 0)
                {
                    headData.Clothing = trophyData.clothing.Select(itemId => new BasicItemData(itemId)).ToArray();
                }

                return headData;
            }

            public class BasicItemData
            {
                public readonly int ItemId;

                public BasicItemData(int itemId)
                {
                    ItemId = itemId;
                }
            }

            [JsonProperty("EntitySource")]
            public uint EntitySource;

            [JsonProperty("HorseBreed", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int HorseBreed;

            [JsonProperty("PlayerId", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ulong PlayerId;

            [JsonProperty("PlayerName", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string PlayerName;

            [JsonProperty("Clothing", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public BasicItemData[] Clothing;

            public void ApplyToHuntingTrophy(HuntingTrophy huntingTrophy)
            {
                if (CurrentTrophyDataField == null)
                    return;

                if (CurrentTrophyDataField.GetValue(huntingTrophy) is not ProtoBuf.HeadData headData)
                {
                    headData = Pool.Get<ProtoBuf.HeadData>();
                    CurrentTrophyDataField.SetValue(huntingTrophy, headData);
                }

                headData.entitySource = EntitySource;
                headData.horseBreed = HorseBreed;
                headData.playerId = PlayerId;
                headData.playerName = PlayerName;
                headData.count = 1;

                if (Clothing?.Length > 0)
                {
                    headData.clothing = Pool.GetList<int>();
                    foreach (var itemData in Clothing)
                    {
                        headData.clothing.Add(itemData.ItemId);
                    }
                }
                else if (headData.clothing != null)
                {
                    Pool.FreeList(ref headData.clothing);
                }
            }
        }

        private class EntityData : BaseTransformData
        {
            [JsonProperty("PrefabName", Order = -5)]
            public string PrefabName;

            [JsonProperty("Skin", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ulong Skin;

            [JsonProperty("EnabledFlags", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public BaseEntity.Flags EnabledFlags;

            [JsonProperty("DisabledFlags", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public BaseEntity.Flags DisabledFlags;

            [JsonProperty("Puzzle", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public PuzzleData Puzzle;

            [JsonProperty("Scale", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(1f)]
            public float Scale = 1;

            [JsonProperty("CardReaderLevel", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ushort CardReaderLevel;

            [JsonProperty("BuildingBlock", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public BuildingBlockInfo BuildingBlock;

            [JsonProperty("CCTV", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public CCTVInfo CCTV;

            [JsonProperty("SignArtistImages", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public SignArtistImage[] SignArtistImages;

            [JsonProperty("VendingProfile", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public object VendingProfile;

            [JsonProperty("IOEntity", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public IOEntityData IOEntityData;

            [JsonProperty("SkullName", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string SkullName;

            [JsonProperty("HeadData", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public HeadData HeadData;

            public void SetFlag(BaseEntity.Flags flag, bool? value)
            {
                switch (value)
                {
                    case true:
                        EnabledFlags |= flag;
                        DisabledFlags &= ~flag;
                        break;

                    case false:
                        EnabledFlags &= ~flag;
                        DisabledFlags |= flag;
                        break;

                    case null:
                        EnabledFlags &= ~flag;
                        DisabledFlags &= ~flag;
                        break;
                }
            }

            public bool? HasFlag(BaseEntity.Flags flag)
            {
                if (EnabledFlags.HasFlag(flag))
                    return true;

                if (DisabledFlags.HasFlag(flag))
                    return false;

                return null;
            }

            public void RemoveIOConnection(int slot)
            {
                if (IOEntityData == null)
                    return;

                for (var i = IOEntityData.Outputs.Count - 1; i >= 0; i--)
                {
                    if (IOEntityData.Outputs[i].Slot == slot)
                    {
                        IOEntityData.Outputs.RemoveAt(i);
                        break;
                    }
                }
            }

            public void AddIOConnection(IOConnectionData connectionData)
            {
                if (IOEntityData == null)
                {
                    IOEntityData = new IOEntityData();
                }

                RemoveIOConnection(connectionData.Slot);
                IOEntityData.Outputs.Add(connectionData);
            }

            public PuzzleData EnsurePuzzleData(PuzzleReset puzzleReset)
            {
                return Puzzle ??= new PuzzleData
                {
                    PlayersBlockReset = puzzleReset.playersBlockReset,
                    PlayerDetectionRadius = puzzleReset.playerDetectionRadius,
                    SecondsBetweenResets = puzzleReset.timeBetweenResets,
                };
            }
        }

        #endregion

        #region Spawn Group Data

        private class SpawnPointData : BaseTransformData
        {
            [JsonProperty("Exclusive", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Exclusive;

            [JsonProperty("SnapToGround", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool SnapToGround;

            [JsonProperty("DropToGround")]
            public bool DeprecatedDropToGround { set => SnapToGround = value; }

            [JsonProperty("CheckSpace", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool CheckSpace;

            [JsonProperty("RandomRotation", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool RandomRotation;

            [JsonProperty("RandomRadius", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float RandomRadius;

            [JsonProperty("PlayerDetectionRadius", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float PlayerDetectionRadius;
        }

        private class WeightedPrefabData
        {
            [JsonProperty("PrefabName", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string PrefabName;

            [JsonProperty("CustomAddonName", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string CustomAddonName;

            [JsonProperty("Weight")]
            public int Weight = 1;
        }

        private class SpawnGroupData : BaseData
        {
            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("MaxPopulation")]
            public int MaxPopulation = 1;

            [JsonProperty("SpawnPerTickMin")]
            public int SpawnPerTickMin = 1;

            [JsonProperty("SpawnPerTickMax")]
            public int SpawnPerTickMax = 2;

            [JsonProperty("RespawnDelayMin")]
            public float RespawnDelayMin = 1500;

            [JsonProperty("RespawnDelayMax")]
            public float RespawnDelayMax = 2100;

            // Default to true for backwards compatibility.
            [JsonProperty("InitialSpawn")]
            public bool InitialSpawn = true;

            [JsonProperty("PreventDuplicates", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool PreventDuplicates;

            [JsonProperty("PauseScheduleWhileFull", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool PauseScheduleWhileFull;

            [JsonProperty("RespawnWhenNearestPuzzleResets", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool RespawnWhenNearestPuzzleResets;

            [JsonProperty("Prefabs")]
            public List<WeightedPrefabData> Prefabs = new List<WeightedPrefabData>();

            [JsonProperty("SpawnPoints")]
            public List<SpawnPointData> SpawnPoints = new List<SpawnPointData>();

            [JsonIgnore]
            public int TotalWeight
            {
                get
                {
                    var total = 0;
                    foreach (var prefabEntry in Prefabs)
                    {
                        total += prefabEntry.Weight;
                    }

                    return total;
                }
            }

            public List<WeightedPrefabData> FindCustomAddonMatches(string partialName)
            {
                return SearchUtils.FindCustomAddonMatches(Prefabs, prefabData => prefabData.CustomAddonName, partialName);
            }

            public List<WeightedPrefabData> FindPrefabMatches(string partialName, UniqueNameRegistry uniqueNameRegistry)
            {
                return SearchUtils.FindPrefabMatches(Prefabs, prefabData => prefabData.PrefabName, partialName, uniqueNameRegistry);
            }
        }

        #endregion

        #region Paste Data

        private class PasteData : BaseTransformData
        {
            [JsonProperty("Filename")]
            public string Filename;

            [JsonProperty("Args")]
            public string[] Args;
        }

        #endregion

        #region Custom Addon Data

        private class CustomAddonData : BaseTransformData
        {
            [JsonProperty("AddonName")]
            public string AddonName;

            [JsonProperty("PluginData", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private object PluginData;

            public JObject GetSerializedData()
            {
                return PluginData as JObject;
            }

            public CustomAddonData SetData(object data)
            {
                PluginData = data as JObject ?? (data != null ? JObject.FromObject(data) : null);
                return this;
            }
        }

        #endregion

        #region Profile Data

        private class ProfileSummaryEntry
        {
            public string MonumentName;
            public string AddonType;
            public string AddonName;
            public int Count;
        }

        private List<ProfileSummaryEntry> GetProfileSummary(IPlayer player, Profile profile)
        {
            var summary = new List<ProfileSummaryEntry>();

            var addonTypeEntity = GetMessage(player.Id, LangEntry.AddonTypeEntity);
            var addonTypePrefab = GetMessage(player.Id, LangEntry.AddonTypePrefab);
            var addonTypePaste = GetMessage(player.Id, LangEntry.AddonTypePaste);
            var addonTypeSpawnPoint = GetMessage(player.Id, LangEntry.AddonTypeSpawnPoint);
            var addonTypeCustom = GetMessage(player.Id, LangEntry.AddonTypeCustom);

            foreach (var monumentEntry in profile.MonumentDataMap)
            {
                var monumentName = monumentEntry.Key;
                var monumentData = monumentEntry.Value;

                var entryMap = new Dictionary<string, ProfileSummaryEntry>();

                foreach (var entityData in monumentData.Entities)
                {
                    var entityUniqueName = _uniqueNameRegistry.GetUniqueShortName(entityData.PrefabName);

                    if (!entryMap.TryGetValue(entityUniqueName, out var summaryEntry))
                    {
                        summaryEntry = new ProfileSummaryEntry
                        {
                            MonumentName = monumentName,
                            AddonType = addonTypeEntity,
                            AddonName = entityUniqueName,
                        };
                        entryMap[entityUniqueName] = summaryEntry;
                    }

                    summaryEntry.Count++;
                }

                foreach (var prefabData in monumentData.Prefabs)
                {
                    var uniqueName = _uniqueNameRegistry.GetUniqueShortName(prefabData.PrefabName);

                    if (!entryMap.TryGetValue(uniqueName, out var summaryEntry))
                    {
                        summaryEntry = new ProfileSummaryEntry
                        {
                            MonumentName = monumentName,
                            AddonType = addonTypePrefab,
                            AddonName = uniqueName,
                        };
                        entryMap[uniqueName] = summaryEntry;
                    }

                    summaryEntry.Count++;
                }

                foreach (var spawnGroupData in monumentData.SpawnGroups)
                {
                    if (spawnGroupData.SpawnPoints.Count == 0)
                        continue;

                    // Add directly to the summary since different spawn groups could have the same name.
                    summary.Add(new ProfileSummaryEntry
                    {
                        MonumentName = monumentName,
                        AddonType = addonTypeSpawnPoint,
                        AddonName = spawnGroupData.Name,
                        Count = spawnGroupData.SpawnPoints.Count,
                    });
                }

                foreach (var pasteData in monumentData.Pastes)
                {
                    if (!entryMap.TryGetValue(pasteData.Filename, out var summaryEntry))
                    {
                        summaryEntry = new ProfileSummaryEntry
                        {
                            MonumentName = monumentName,
                            AddonType = addonTypePaste,
                            AddonName = pasteData.Filename,
                        };
                        entryMap[pasteData.Filename] = summaryEntry;
                    }

                    summaryEntry.Count++;
                }

                foreach (var customAddonData in monumentData.CustomAddons)
                {
                    if (!entryMap.TryGetValue(customAddonData.AddonName, out var summaryEntry))
                    {
                        summaryEntry = new ProfileSummaryEntry
                        {
                            MonumentName = monumentName,
                            AddonType = addonTypeCustom,
                            AddonName = customAddonData.AddonName,
                        };
                        entryMap[customAddonData.AddonName] = summaryEntry;
                    }

                    summaryEntry.Count++;
                }

                summary.AddRange(entryMap.Values);
            }

            return summary;
        }

        private class MonumentData
        {
            [JsonProperty("Entities")]
            public List<EntityData> Entities = new List<EntityData>();

            public bool ShouldSerializeEntities() => Entities.Count > 0;

            [JsonProperty("Prefabs")]
            public List<PrefabData> Prefabs = new List<PrefabData>();

            public bool ShouldSerializePrefabs() => Prefabs.Count > 0;

            [JsonProperty("SpawnGroups")]
            public List<SpawnGroupData> SpawnGroups = new List<SpawnGroupData>();

            public bool ShouldSerializeSpawnGroups() => SpawnGroups.Count > 0;

            [JsonProperty("Pastes")]
            public List<PasteData> Pastes = new List<PasteData>();

            public bool ShouldSerializePastes() => Pastes.Count > 0;

            [JsonProperty("CustomAddons")]
            public List<CustomAddonData> CustomAddons = new List<CustomAddonData>();

            public bool ShouldSerializeCustomAddons() => CustomAddons.Count > 0;

            [JsonIgnore]
            public int NumSpawnables =>
                Entities.Count
                + Prefabs.Count
                + SpawnGroups.Count
                + Pastes.Count
                + CustomAddons.Count;

            [JsonIgnore]
            public int NumSpawnPoints
            {
                get
                {
                    var count = 0;
                    foreach (var spawnGroup in SpawnGroups)
                    {
                        count += spawnGroup.SpawnPoints.Count;
                    }

                    return count;
                }
            }

            public IEnumerable<BaseData> GetSpawnablesLazy()
            {
                foreach (var entityData in Entities)
                    yield return entityData;

                foreach (var prefabData in Prefabs)
                    yield return prefabData;

                foreach (var spawnGroupData in SpawnGroups)
                    yield return spawnGroupData;

                foreach (var pasteData in Pastes)
                    yield return pasteData;

                foreach (var customAddonData in CustomAddons)
                    yield return customAddonData;
            }

            public ICollection<BaseData> GetSpawnables()
            {
                var list = new List<BaseData>(NumSpawnables);
                foreach (var spawnable in GetSpawnablesLazy())
                {
                    list.Add(spawnable);
                }
                return list;
            }

            public bool HasEntity(Guid guid)
            {
                foreach (var entityData in Entities)
                {
                    if (entityData.Id == guid)
                        return true;
                }

                return false;
            }

            public bool HasSpawnGroup(Guid guid)
            {
                foreach (var spawnGroupData in SpawnGroups)
                {
                    if (spawnGroupData.Id == guid)
                        return true;
                }

                return false;
            }

            public void AddData(BaseData data)
            {
                if (data is EntityData entityData)
                {
                    Entities.Add(entityData);
                    return;
                }

                if (data is PrefabData prefabData)
                {
                    Prefabs.Add(prefabData);
                    return;
                }

                if (data is SpawnGroupData spawnGroupData)
                {
                    SpawnGroups.Add(spawnGroupData);
                    return;
                }

                if (data is PasteData pasteData)
                {
                    Pastes.Add(pasteData);
                    return;
                }

                if (data is CustomAddonData customAddonData)
                {
                    CustomAddons.Add(customAddonData);
                    return;
                }

                LogError($"AddData not implemented for type: {data.GetType()}");
            }

            public bool RemoveData(BaseData data)
            {
                if (data is EntityData entityData)
                    return Entities.Remove(entityData);

                if (data is PrefabData prefabData)
                    return Prefabs.Remove(prefabData);

                if (data is SpawnGroupData spawnGroupData)
                    return SpawnGroups.Remove(spawnGroupData);

                if (data is SpawnPointData spawnPointData)
                {
                    foreach (var parentSpawnGroupData in SpawnGroups)
                    {
                        var index = parentSpawnGroupData.SpawnPoints.IndexOf(spawnPointData);
                        if (index == -1)
                            continue;

                        // If removing the spawn group, don't remove the spawn point, so it's easier to undo.
                        if (parentSpawnGroupData.SpawnPoints.Count == 1)
                        {
                            SpawnGroups.Remove(parentSpawnGroupData);
                            CleanSpawnGroupReferences(parentSpawnGroupData.Id);
                        }
                        else
                        {
                            parentSpawnGroupData.SpawnPoints.RemoveAt(index);
                        }

                        return true;
                    }

                    return false;
                }

                if (data is PasteData pasteData)
                    return Pastes.Remove(pasteData);

                if (data is CustomAddonData customAddonData)
                    return CustomAddons.Remove(customAddonData);

                LogError($"RemoveData not implemented for type: {data.GetType()}");
                return false;
            }

            private void CleanSpawnGroupReferences(Guid id)
            {
                foreach (var entityData in Entities)
                {
                    entityData.Puzzle?.SpawnGroupIds?.Remove(id);
                }
            }
        }

        private static class ProfileDataMigration<T> where T : Profile
        {
            private static readonly Dictionary<string, string> _monumentNameCorrections = new Dictionary<string, string>
            {
                ["OilrigAI"] = "oilrig_2",
                ["OilrigAI2"] = "oilrig_1",
            };

            private static readonly Dictionary<string, string> _prefabCorrections = new Dictionary<string, string>
            {
                ["assets/content/vehicles/locomotive/locomotive.entity.prefab"] = "assets/content/vehicles/trains/locomotive/locomotive.entity.prefab",
                ["assets/content/vehicles/workcart/workcart.entity.prefab"] = "assets/content/vehicles/trains/workcart/workcart.entity.prefab",
                ["assets/content/vehicles/workcart/workcart_aboveground.entity.prefab"] = "assets/content/vehicles/trains/workcart/workcart_aboveground.entity.prefab",
                ["assets/content/vehicles/workcart/workcart_aboveground2.entity.prefab"] = "assets/content/vehicles/trains/workcart/workcart_aboveground2.entity.prefab",
                ["assets/content/vehicles/train/trainwagona.entity.prefab"] = "assets/content/vehicles/trains/wagons/trainwagona.entity.prefab",
                ["assets/content/vehicles/train/trainwagonb.entity.prefab"] = "assets/content/vehicles/trains/wagons/trainwagonb.entity.prefab",
                ["assets/content/vehicles/train/trainwagonc.entity.prefab"] = "assets/content/vehicles/trains/wagons/trainwagonc.entity.prefab",
                ["assets/content/vehicles/train/trainwagonunloadablefuel.entity.prefab"] = "assets/content/vehicles/trains/wagons/trainwagonunloadablefuel.entity.prefab",
                ["assets/content/vehicles/train/trainwagonunloadableloot.entity.prefab"] = "assets/content/vehicles/trains/wagons/trainwagonunloadableloot.entity.prefab",
                ["assets/content/vehicles/train/trainwagonunloadable.entity.prefab"] = "assets/content/vehicles/trains/wagons/trainwagonunloadable.entity.prefab",
            };

            private static string GetPrefabCorrectionIfExists(string prefabName)
            {
                return _prefabCorrections.TryGetValue(prefabName, out var correctedPrefabName)
                    ? correctedPrefabName
                    : prefabName;
            }

            public static bool MigrateToLatest(T data)
            {
                // Using single | to avoid short-circuiting.
                return MigrateV0ToV1(data)
                    | MigrateV1ToV2(data)
                    | MigrateIncorrectPrefabs(data)
                    | MigrateIncorrectMonuments(data);
            }

            public static bool MigrateV0ToV1(T data)
            {
                if (data.SchemaVersion != 0)
                    return false;

                data.SchemaVersion++;

                var contentChanged = false;

                if (data.DeprecatedMonumentMap != null)
                {
                    foreach (var entityDataList in data.DeprecatedMonumentMap.Values)
                    {
                        if (entityDataList == null)
                            continue;

                        foreach (var entityData in entityDataList)
                        {
                            if (GetShortName(entityData.PrefabName) == "big_wheel"
                                && entityData.RotationAngles.x != 90)
                            {
                                // The plugin used to coerce the x component to 90.
                                entityData.RotationAngles.x = 90;
                                contentChanged = true;
                            }
                        }
                    }
                }

                return contentChanged;
            }

            public static bool MigrateV1ToV2(T data)
            {
                if (data.SchemaVersion != 1)
                    return false;

                data.SchemaVersion++;

                var contentChanged = false;

                if (data.DeprecatedMonumentMap != null)
                {
                    foreach (var entry in data.DeprecatedMonumentMap)
                    {
                        var entityDataList = entry.Value;
                        if (entityDataList == null || entityDataList.Count == 0)
                            continue;

                        data.MonumentDataMap[entry.Key] = new MonumentData
                        {
                            Entities = entityDataList,
                        };
                        contentChanged = true;
                    }

                    data.DeprecatedMonumentMap = null;
                }

                return contentChanged;
            }

            public static bool MigrateIncorrectPrefabs(T data)
            {
                var contentChanged = false;

                foreach (var monumentData in data.MonumentDataMap.Values)
                {
                    foreach (var entityData in monumentData.Entities)
                    {
                        var correctedPrefabName = GetPrefabCorrectionIfExists(entityData.PrefabName);
                        if (correctedPrefabName != entityData.PrefabName)
                        {
                            entityData.PrefabName = correctedPrefabName;
                            contentChanged = true;
                        }
                    }

                    foreach (var spawnGroupData in monumentData.SpawnGroups)
                    {
                        foreach (var prefabData in spawnGroupData.Prefabs)
                        {
                            // Custom addons won't have prefab name.
                            if (prefabData.PrefabName == null)
                                continue;

                            var correctedPrefabName = GetPrefabCorrectionIfExists(prefabData.PrefabName);
                            if (correctedPrefabName != prefabData.PrefabName)
                            {
                                prefabData.PrefabName = correctedPrefabName;
                                contentChanged = true;
                            }
                        }
                    }
                }

                return contentChanged;
            }

            public static bool MigrateIncorrectMonuments(T data)
            {
                var contentChanged = false;

                foreach (var entry in _monumentNameCorrections)
                {
                    if (!data.MonumentDataMap.TryGetValue(entry.Key, out var monumentData))
                        continue;

                    data.MonumentDataMap[entry.Value] = monumentData;
                    data.MonumentDataMap.Remove(entry.Key);
                    contentChanged = true;
                }

                return contentChanged;
            }
        }

        private class FileStore<T> where T : class, new()
        {
            protected string _directoryPath;

            public FileStore(string directoryPath)
            {
                _directoryPath = directoryPath + "/";
            }

            public virtual bool Exists(string filename)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(GetFilepath(filename));
            }

            public virtual T Load(string filename)
            {
                return Interface.Oxide.DataFileSystem.ReadObject<T>(GetFilepath(filename)) ?? new T();
            }

            public T LoadIfExists(string filename)
            {
                return Exists(filename)
                    ? Load(filename)
                    : default(T);
            }

            public void Save(string filename, T data)
            {
                Interface.Oxide.DataFileSystem.WriteObject(GetFilepath(filename), data);
            }

            public void Delete(string filename)
            {
                Interface.Oxide.DataFileSystem.DeleteDataFile(GetFilepath(filename));
            }

            protected virtual string GetFilepath(string filename)
            {
                return $"{_directoryPath}{filename}";
            }
        }

        private class OriginalProfileStore : FileStore<Profile>
        {
            public const string OriginalSuffix = "_original";

            public static bool IsOriginalProfile(string profileName)
            {
                return profileName.EndsWith(OriginalSuffix);
            }

            public OriginalProfileStore() : base(nameof(MonumentAddons)) {}

            protected override string GetFilepath(string profileName)
            {
                return base.GetFilepath(profileName + OriginalSuffix);
            }

            public void Save(Profile profile)
            {
                base.Save(profile.Name, profile);
            }

            public void MoveTo(Profile profile, string newName)
            {
                var original = LoadIfExists(profile.Name);
                if (original == null)
                    return;

                var oldName = original.Name;
                original.Name = newName;
                Save(original);
                Delete(oldName);
            }
        }

        private class ProfileStore : FileStore<Profile>
        {
            public static string[] GetProfileNames()
            {
                var filenameList = Interface.Oxide.DataFileSystem.GetFiles(nameof(MonumentAddons));

                for (var i = 0; i < filenameList.Length; i++)
                {
                    var filename = filenameList[i];
                    var start = filename.LastIndexOf(System.IO.Path.DirectorySeparatorChar) + 1;
                    var end = filename.LastIndexOf(".", StringComparison.Ordinal);
                    filenameList[i] = filename.Substring(start, end - start);
                }

                return filenameList;
            }

            public ProfileStore() : base(nameof(MonumentAddons)) {}

            public override bool Exists(string profileName)
            {
                return !OriginalProfileStore.IsOriginalProfile(profileName) && base.Exists(profileName);
            }

            public override Profile Load(string profileName)
            {
                if (OriginalProfileStore.IsOriginalProfile(profileName))
                    return null;

                var profile = base.Load(profileName);
                profile.Name = GetCaseSensitiveFileName(profileName);

                var migrated = ProfileDataMigration<Profile>.MigrateToLatest(profile);
                if (migrated)
                {
                    LogWarning($"Profile {profile.Name} has been automatically migrated.");
                }

                // Backfill ids if missing.
                foreach (var monumentData in profile.MonumentDataMap.Values)
                {
                    foreach (var entityData in monumentData.Entities)
                    {
                        if (entityData.Id == Guid.Empty)
                        {
                            entityData.Id = Guid.NewGuid();
                        }
                    }
                }

                if (migrated)
                {
                    Save(profile.Name, profile);
                }

                return profile;
            }

            public bool TryLoad(string profileName, out Profile profile, out string errorMessage)
            {
                try
                {
                    profile = Load(profileName);
                    errorMessage = null;
                    return true;
                }
                catch (JsonReaderException ex)
                {
                    profile = null;
                    errorMessage = ex.Message;
                    return false;
                }
            }

            public void Save(Profile profile)
            {
                base.Save(profile.Name, profile);
            }

            public Profile Create(string profileName, string authorName)
            {
                var profile = new Profile
                {
                    Name = profileName,
                    Author = authorName,
                };
                ProfileDataMigration<Profile>.MigrateToLatest(profile);
                Save(profile);
                return profile;
            }

            public void MoveTo(Profile profile, string newName)
            {
                var oldName = profile.Name;
                profile.Name = newName;
                Save(profile);
                Delete(oldName);
            }

            public void EnsureDefaultProfile()
            {
                Load(DefaultProfileName);
            }

            private string GetCaseSensitiveFileName(string profileName)
            {
                foreach (var name in GetProfileNames())
                {
                    if (StringUtils.EqualsCaseInsensitive(name, profileName))
                        return name;
                }

                return profileName;
            }
        }

        private class Profile
        {
            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("Author", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Author;

            [JsonProperty("SchemaVersion", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float SchemaVersion;

            [JsonProperty("Url", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Url;

            [JsonProperty("Monuments", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Dictionary<string, List<EntityData>> DeprecatedMonumentMap;

            [JsonProperty("MonumentData")]
            public Dictionary<string, MonumentData> MonumentDataMap = new Dictionary<string, MonumentData>();

            private HashSet<uint> _dynamicMonumentPrefabIds = new();
            private bool _hasDeterminedDynamicMonuments;

            [JsonIgnore]
            public bool HasAnyDynamicMonuments
            {
                get
                {
                    if (!_hasDeterminedDynamicMonuments)
                    {
                        DetermineDynamicMonuments();
                    }

                    return _dynamicMonumentPrefabIds.Count > 0;
                }
            }

            [OnDeserialized]
            private void OnDeserialized(StreamingContext context)
            {
                RenameDictKey(MonumentDataMap, CargoShipShortName, CargoShipPrefab);
            }

            public bool IsEmpty()
            {
                if (MonumentDataMap == null || MonumentDataMap.IsEmpty())
                    return true;

                foreach (var monumentData in MonumentDataMap.Values)
                {
                    if (monumentData.NumSpawnables > 0)
                        return false;
                }

                return true;
            }

            public bool HasEntity(string monumentUniqueName, Guid guid)
            {
                return MonumentDataMap.GetValueOrDefault(monumentUniqueName)?.HasEntity(guid) ?? false;
            }

            public bool HasEntity(string monumentUniqueName, EntityData entityData)
            {
                return MonumentDataMap.GetValueOrDefault(monumentUniqueName)?.Entities.Contains(entityData) ?? false;
            }

            public bool HasSpawnGroup(string monumentUniqueName, Guid guid)
            {
                return MonumentDataMap.GetValueOrDefault(monumentUniqueName)?.HasSpawnGroup(guid) ?? false;
            }

            public void AddData(string monumentUniqueName, BaseData data)
            {
                EnsureMonumentData(monumentUniqueName).AddData(data);
                DetermineDynamicMonuments();
            }

            public bool RemoveData(BaseData data, out string monumentUniqueName)
            {
                foreach (var entry in MonumentDataMap)
                {
                    if (entry.Value.RemoveData(data))
                    {
                        monumentUniqueName = entry.Key;
                        DetermineDynamicMonuments();
                        return true;
                    }
                }

                monumentUniqueName = null;
                return false;
            }

            public bool HasDynamicMonument(BaseEntity entity)
            {
                if (!_hasDeterminedDynamicMonuments)
                {
                    DetermineDynamicMonuments();
                }

                return _dynamicMonumentPrefabIds.Contains(entity.prefabID);
            }

            private MonumentData EnsureMonumentData(string monumentUniqueName)
            {
                if (!MonumentDataMap.TryGetValue(monumentUniqueName, out var monumentData))
                {
                    monumentData = new MonumentData();
                    MonumentDataMap[monumentUniqueName] = monumentData;
                }

                return monumentData;
            }

            private void DetermineDynamicMonuments()
            {
                _dynamicMonumentPrefabIds.Clear();

                foreach (var (monumentUniqueName, monumentData) in MonumentDataMap)
                {
                    if (monumentData.NumSpawnables == 0)
                        continue;

                    if (!monumentUniqueName.StartsWith("assets/"))
                        continue;

                    var baseEntity = FindPrefabBaseEntity(monumentUniqueName);
                    if (baseEntity != null)
                    {
                        _dynamicMonumentPrefabIds.Add(baseEntity.prefabID);
                    }
                }

                _hasDeterminedDynamicMonuments = true;
            }
        }

        #endregion

        #region Data File Utils

        private static class DataFileUtils
        {
            public static bool Exists(string filepath)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(filepath);
            }

            public static T Load<T>(string filepath) where T : class, new()
            {
                return Interface.Oxide.DataFileSystem.ReadObject<T>(filepath) ?? new T();
            }

            public static T LoadIfExists<T>(string filepath) where T : class, new()
            {
                return Exists(filepath) ? Load<T>(filepath) : null;
            }

            public static T LoadOrNew<T>(string filepath) where T : class, new()
            {
                return LoadIfExists<T>(filepath) ?? new T();
            }

            public static void Save<T>(string filepath, T data)
            {
                Interface.Oxide.DataFileSystem.WriteObject<T>(filepath, data);
            }
        }

        private class BaseDataFile
        {
            private string _filepath;

            public BaseDataFile(string filepath)
            {
                _filepath = filepath;
            }

            public void Save()
            {
                DataFileUtils.Save(_filepath, this);
            }
        }

        #endregion

        #region Profile State

        private struct MonumentEntityEntry
        {
            public string MonumentUniqueName;
            public Guid Guid;
            public BaseEntity Entity;

            public MonumentEntityEntry(string monumentUniqueName, Guid guid, BaseEntity entity)
            {
                MonumentUniqueName = monumentUniqueName;
                Guid = guid;
                Entity = entity;
            }
        }

        private class MonumentState : IDeepCollection
        {
            [JsonProperty("Entities")]
            public Dictionary<Guid, ulong> Entities = new Dictionary<Guid, ulong>();

            public bool HasItems()
            {
                return Entities.Count > 0;
            }

            public bool HasEntity(Guid guid, NetworkableId entityId)
            {
                return Entities.GetValueOrDefault(guid) == entityId.Value;
            }

            public BaseEntity FindEntity(Guid guid)
            {
                if (!Entities.TryGetValue(guid, out var entityId))
                    return null;

                var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(entityId)) as BaseEntity;
                if (entity == null || entity.IsDestroyed)
                    return null;

                return entity;
            }

            public void AddEntity(Guid guid, NetworkableId entityId)
            {
                Entities[guid] = entityId.Value;
            }

            public bool RemoveEntity(Guid guid)
            {
                return Entities.Remove(guid);
            }

            public IEnumerable<ValueTuple<Guid, BaseEntity>> FindValidEntities()
            {
                if (Entities.Count == 0)
                    yield break;

                foreach (var entry in Entities)
                {
                    var entity = FindValidEntity(entry.Value);
                    if (entity == null)
                        continue;

                    yield return new ValueTuple<Guid, BaseEntity>(entry.Key, entity);
                }
            }

            public int CleanStaleEntityRecords()
            {
                if (Entities.Count == 0)
                    return 0;

                var cleanedCount = 0;

                foreach (var entry in Entities.ToList())
                {
                    if (FindValidEntity(entry.Value) == null)
                    {
                        Entities.Remove(entry.Key);
                        cleanedCount++;
                    }
                }

                return cleanedCount;
            }
        }

        private class MonumentStateMapConverter : DictionaryKeyConverter<Vector3, MonumentState>
        {
            public override string KeyToString(Vector3 v)
            {
                return $"{v.x:g9},{v.y:g9},{v.z:g9}";
            }

            public override Vector3 KeyFromString(string key)
            {
                var parts = key.Split(',');
                return new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]));
            }
        }

        private class Vector3EqualityComparer : IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a, Vector3 b)
            {
                return a == b;
            }

            public int GetHashCode(Vector3 vector)
            {
                return vector.GetHashCode();
            }
        }

        private class MonumentStateMap : IDeepCollection
        {
            [JsonProperty("ByLocation")]
            [JsonConverter(typeof(MonumentStateMapConverter))]
            private Dictionary<Vector3, MonumentState> ByLocation = new Dictionary<Vector3, MonumentState>(new Vector3EqualityComparer());

            public bool ShouldSerializeByLocation()
            {
                return HasDeepItems(ByLocation);
            }

            [JsonProperty("ByEntity")]
            private Dictionary<ulong, MonumentState> ByEntity = new Dictionary<ulong, MonumentState>();

            public bool ShouldSerializeByEntity()
            {
                return HasDeepItems(ByEntity);
            }

            public bool HasItems()
            {
                return HasDeepItems(ByLocation) || HasDeepItems(ByEntity);
            }

            public IEnumerable<ValueTuple<Guid, BaseEntity>> FindValidEntities()
            {
                foreach (var monumentState in ByLocation.Values)
                {
                    foreach (var entityEntry in monumentState.FindValidEntities())
                    {
                        yield return entityEntry;
                    }
                }

                foreach (var monumentEntry in ByEntity)
                {
                    var monumentEntityId = monumentEntry.Key;
                    if (FindValidEntity(monumentEntityId) == null)
                        continue;

                    foreach (var entityEntry in monumentEntry.Value.FindValidEntities())
                    {
                        yield return entityEntry;
                    }
                }
            }

            public int CleanStaleEntityRecords()
            {
                var cleanedCount = 0;

                if (ByLocation.Count > 0)
                {
                    foreach (var monumentState in ByLocation.Values)
                    {
                        cleanedCount += monumentState.CleanStaleEntityRecords();
                    }
                }

                if (ByEntity.Count > 0)
                {
                    foreach (var monumentEntry in ByEntity.ToList())
                    {
                        if (FindValidEntity(monumentEntry.Key) == null)
                        {
                            ByEntity.Remove(monumentEntry.Key);
                            continue;
                        }

                        cleanedCount += monumentEntry.Value.CleanStaleEntityRecords();
                    }
                }

                return cleanedCount;
            }

            public MonumentState GetMonumentState(BaseMonument monument)
            {
                if (monument is IEntityMonument entityMonument)
                    return ByEntity.GetValueOrDefault(entityMonument.EntityId.Value);

                return ByLocation.GetValueOrDefault(monument.Position);
            }

            public MonumentState GetOrCreateMonumentState(BaseMonument monument)
            {
                if (monument is IEntityMonument entityMonument)
                    return ByEntity.GetOrCreate(entityMonument.EntityId.Value);

                return ByLocation.GetOrCreate(monument.Position);
            }
        }

        private class ProfileState : Dictionary<string, MonumentStateMap>, IDeepCollection
        {
            [OnDeserialized]
            private void OnDeserialized(StreamingContext context)
            {
                RenameDictKey(this, CargoShipShortName, CargoShipPrefab);
            }

            public bool HasItems()
            {
                return HasDeepItems(this);
            }

            public int CleanStaleEntityRecords()
            {
                var cleanedCount = 0;

                foreach (var monumentStateMap in Values)
                {
                    cleanedCount += monumentStateMap.CleanStaleEntityRecords();
                }

                return cleanedCount;
            }

            public IEnumerable<MonumentEntityEntry> FindValidEntities()
            {
                if (Count == 0)
                    yield break;

                foreach (var (monumentIdentifier, monumentStateMap) in this)
                {
                    if (!monumentStateMap.HasItems())
                        continue;

                    foreach (var entityEntry in monumentStateMap.FindValidEntities())
                    {
                        yield return new MonumentEntityEntry(monumentIdentifier, entityEntry.Item1, entityEntry.Item2);
                    }
                }
            }
        }

        private class ProfileStateMap : Dictionary<string, ProfileState>, IDeepCollection
        {
            public ProfileStateMap() : base(StringComparer.InvariantCultureIgnoreCase) {}

            public bool HasItems()
            {
                return HasDeepItems(this);
            }
        }

        private class ProfileStateData : BaseDataFile
        {
            private static string Filepath => $"{nameof(MonumentAddons)}_State";

            public static ProfileStateData Load(StoredData pluginData)
            {
                var data = DataFileUtils.LoadOrNew<ProfileStateData>(Filepath);
                data._pluginData = pluginData;
                return data;
            }

            private StoredData _pluginData;

            public ProfileStateData() : base(Filepath) {}

            [JsonProperty("ProfileState", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private ProfileStateMap ProfileStateMap = new ProfileStateMap();

            public bool ShouldSerializeProfileStateMap()
            {
                return ProfileStateMap.HasItems();
            }

            public ProfileState GetProfileState(string profileName)
            {
                return ProfileStateMap.GetValueOrDefault(profileName);
            }

            public bool HasEntity(string profileName, BaseMonument monument, Guid guid, NetworkableId entityId)
            {
                return GetProfileState(profileName)
                    ?.GetValueOrDefault(monument.UniqueName)
                    ?.GetMonumentState(monument)
                    ?.HasEntity(guid, entityId) ?? false;
            }

            public BaseEntity FindEntity(string profileName, BaseMonument monument, Guid guid)
            {
                return GetProfileState(profileName)
                    ?.GetValueOrDefault(monument.UniqueName)
                    ?.GetMonumentState(monument)
                    ?.FindEntity(guid);
            }

            public void AddEntity(string profileName, BaseMonument monument, Guid guid, NetworkableId entityId)
            {
                ProfileStateMap.GetOrCreate(profileName)
                    .GetOrCreate(monument.UniqueName)
                    .GetOrCreateMonumentState(monument)
                    .AddEntity(guid, entityId);
            }

            public bool RemoveEntity(string profileName, BaseMonument monument, Guid guid)
            {
                return GetProfileState(profileName)
                    ?.GetValueOrDefault(monument.UniqueName)
                    ?.GetMonumentState(monument)
                    ?.RemoveEntity(guid) ?? false;
            }

            public List<BaseEntity> FindAndRemoveValidEntities(string profileName)
            {
                var profileState = ProfileStateMap.GetValueOrDefault(profileName);
                if (profileState == null)
                    return null;

                List<BaseEntity> entityList = null;

                foreach (var entityEntry in profileState.FindValidEntities())
                {
                    entityList ??= new List<BaseEntity>();
                    entityList.Add(entityEntry.Entity);
                }

                ProfileStateMap.Remove(profileName);

                return entityList;
            }

            public List<BaseEntity> CleanDisabledProfileState()
            {
                var entitiesToKill = new List<BaseEntity>();

                if (ProfileStateMap.Count == 0)
                    return entitiesToKill;

                var cleanedCount = 0;

                foreach (var entry in ProfileStateMap.ToList())
                {
                    var profileName = entry.Key;
                    var monumentStateMap = entry.Value;

                    if (_pluginData.IsProfileEnabled(profileName))
                        continue;

                    // Delete entities previously spawned for profiles which are now disabled.
                    // This addresses the use case where the plugin is unloaded with entity persistence enabled,
                    // then the data file is manually edited to disable a profile.
                    foreach (var entityEntry in monumentStateMap.FindValidEntities())
                    {
                        entitiesToKill.Add(entityEntry.Entity);
                    }

                    ProfileStateMap.Remove(profileName);
                    cleanedCount++;
                }

                if (cleanedCount > 0)
                {
                    Save();
                }

                return entitiesToKill;
            }

            public void Reset()
            {
                if (!ProfileStateMap.HasItems())
                    return;

                ProfileStateMap.Clear();
                Save();
            }
        }

        #endregion

        #region Plugin Data

        private static class StoredDataMigration
        {
            private static readonly Dictionary<string, string> MigrateMonumentNames = new Dictionary<string, string>
            {
                ["TRAIN_STATION"] = "TrainStation",
                ["BARRICADE_TUNNEL"] = "BarricadeTunnel",
                ["LOOT_TUNNEL"] = "LootTunnel",
                ["3_WAY_INTERSECTION"] = "Intersection",
                ["4_WAY_INTERSECTION"] = "LargeIntersection",
            };

            public static bool MigrateToLatest(ProfileStore profileStore, StoredData data)
            {
                // Using single | to avoid short-circuiting.
                return MigrateV0ToV1(data)
                    | MigrateV1ToV2(profileStore, data);
            }

            public static bool MigrateV0ToV1(StoredData data)
            {
                if (data.DataFileVersion != 0)
                    return false;

                data.DataFileVersion++;

                var contentChanged = false;

                if (data.DeprecatedMonumentMap != null)
                {
                    foreach (var monumentEntry in data.DeprecatedMonumentMap.ToList())
                    {
                        var alias = monumentEntry.Key;
                        var entityList = monumentEntry.Value;

                        if (MigrateMonumentNames.TryGetValue(alias, out var newAlias))
                        {
                            data.DeprecatedMonumentMap[newAlias] = entityList;
                            data.DeprecatedMonumentMap.Remove(alias);
                            alias = newAlias;
                        }

                        foreach (var entityData in entityList)
                        {
                            if (alias == "LootTunnel" || alias == "BarricadeTunnel")
                            {
                                // Migrate from the original rotations to the rotations used by MonumentFinder.
                                entityData.DeprecatedRotationAngle = (entityData.RotationAngles.y + 180) % 360;
                                entityData.Position = Quaternion.Euler(0, 180, 0) * entityData.Position;
                            }

                            // Migrate from the backwards rotations to the correct ones.
                            var newAngle = (720 - entityData.RotationAngles.y) % 360;
                            entityData.DeprecatedRotationAngle = newAngle;
                            contentChanged = true;
                        }
                    }
                }

                return contentChanged;
            }

            public static bool MigrateV1ToV2(ProfileStore profileStore, StoredData data)
            {
                if (data.DataFileVersion != 1)
                    return false;

                data.DataFileVersion++;

                var profile = new Profile
                {
                    Name = DefaultProfileName,
                };

                if (data.DeprecatedMonumentMap != null)
                {
                    profile.DeprecatedMonumentMap = data.DeprecatedMonumentMap;
                }

                profileStore.Save(profile);

                data.DeprecatedMonumentMap = null;
                data.EnabledProfiles.Add(DefaultProfileName);

                return true;
            }
        }

        private class StoredData
        {
            public static StoredData Load(ProfileStore profileStore)
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(nameof(MonumentAddons)) ?? new StoredData();

                var originalDataFileVersion = data.DataFileVersion;

                if (StoredDataMigration.MigrateToLatest(profileStore, data))
                {
                    LogWarning("Data file has been automatically migrated.");
                }

                if (data.DataFileVersion != originalDataFileVersion)
                {
                    data.Save();
                }

                return data;
            }

            [JsonProperty("DataFileVersion", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float DataFileVersion;

            [JsonProperty("EnabledProfiles")]
            public HashSet<string> EnabledProfiles = new HashSet<string>();

            [JsonProperty("SelectedProfiles")]
            public Dictionary<string, string> SelectedProfiles = new Dictionary<string, string>();

            [JsonProperty("Monuments", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Dictionary<string, List<EntityData>> DeprecatedMonumentMap;

            public void Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject(nameof(MonumentAddons), this);
            }

            public bool IsProfileEnabled(string profileName)
            {
                return EnabledProfiles.Contains(profileName);
            }

            public void SetProfileEnabled(string profileName)
            {
                EnabledProfiles.Add(profileName);
                Save();
            }

            public void SetProfileDisabled(string profileName)
            {
                if (!EnabledProfiles.Remove(profileName))
                    return;

                foreach (var entry in SelectedProfiles.ToList())
                {
                    if (entry.Value == profileName)
                    {
                        SelectedProfiles.Remove(entry.Key);
                    }
                }

                Save();
            }

            public void RenameProfileReferences(string oldName, string newName)
            {
                foreach (var entry in SelectedProfiles.ToList())
                {
                    if (entry.Value == oldName)
                    {
                        SelectedProfiles[entry.Key] = newName;
                    }
                }

                if (EnabledProfiles.Remove(oldName))
                {
                    EnabledProfiles.Add(newName);
                }

                Save();
            }

            public string GetSelectedProfileName(string userId)
            {
                if (SelectedProfiles.TryGetValue(userId, out var profileName))
                    return profileName;

                if (EnabledProfiles.Contains(DefaultProfileName))
                    return DefaultProfileName;

                return null;
            }

            public void SetProfileSelected(string userId, string profileName)
            {
                SelectedProfiles[userId] = profileName;
            }
        }

        #endregion

        #endregion

        #region Configuration

        [JsonObject(MemberSerialization.OptIn)]
        private class DebugDisplaySettings
        {
            [JsonProperty("Display distance")]
            public float DisplayDistance = 100;

            [JsonProperty("Display distance abbreviated")]
            public float DisplayDistanceAbbreviated = 200;

            [JsonProperty("Max addons to show unabbreviated")]
            public int MaxAddonsToShowUnabbreviated = 1;

            [JsonProperty("Entity color")]
            [JsonConverter(typeof(HtmlColorConverter))]
            public Color EntityColor = Color.magenta;

            [JsonProperty("Spawn point color")]
            [JsonConverter(typeof(HtmlColorConverter))]
            public Color SpawnPointColor = new Color(1, 0.5f, 0);

            [JsonProperty("Paste color")]
            [JsonConverter(typeof(HtmlColorConverter))]
            public Color PasteColor = Color.cyan;

            [JsonProperty("Custom addon color")]
            [JsonConverter(typeof(HtmlColorConverter))]
            public Color CustomAddonColor = Color.green;

            [JsonProperty("Custom monument color")]
            [JsonConverter(typeof(HtmlColorConverter))]
            public Color CustomMonumentColor = Color.green;

            [JsonProperty("Inactive profile color")]
            [JsonConverter(typeof(HtmlColorConverter))]
            public Color InactiveProfileColor = Color.grey;
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class EntitySaveSettings
        {
            [JsonProperty("Enable saving for storage entities")]
            public bool EnabledForStorageEntities;

            [JsonProperty("Enable saving for non-storage entities")]
            public bool EnabledForNonStorageEntities;

            [JsonProperty("Override saving enabled by prefab")]
            private Dictionary<string, bool> OverrideEnabledByPrefab = new();

            private Dictionary<uint, bool> _overrideEnabledByPrefabId = new();

            public void Init()
            {
                foreach (var (prefabPath, enabled) in OverrideEnabledByPrefab)
                {
                    var entity = FindPrefabBaseEntity(prefabPath);
                    if (entity == null)
                    {
                        LogError($"Invalid entity prefab in config: {prefabPath}");
                        continue;
                    }

                    _overrideEnabledByPrefabId[entity.prefabID] = enabled;
                }
            }

            public bool ShouldEnableSaving(BaseEntity entity)
            {
                if (_overrideEnabledByPrefabId.TryGetValue(entity.prefabID, out var enabled))
                    return enabled;

                if (entity is IItemContainerEntity or MiningQuarry)
                    return EnabledForStorageEntities;

                return EnabledForNonStorageEntities;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class DynamicMonumentSettings
        {
            [JsonProperty("Entity prefabs to consider as monuments")]
            public string[] DynamicMonumentPrefabs = { CargoShipPrefab };

            [JsonIgnore]
            private uint[] _dynamicMonumentPrefabIds;

            public void Init()
            {
                var prefabIds = new List<uint>();

                foreach (var prefabPath in DynamicMonumentPrefabs)
                {
                    var baseEntity = FindPrefabBaseEntity(prefabPath);
                    if (baseEntity == null)
                    {
                        LogError($"Invalid prefab path in configuration: {prefabPath}");
                        continue;
                    }

                    prefabIds.Add(baseEntity.prefabID);
                }

                _dynamicMonumentPrefabIds = prefabIds.ToArray();
            }

            public bool IsConfiguredAsDynamicMonument(BaseEntity entity)
            {
                return _dynamicMonumentPrefabIds.Contains(entity.prefabID);
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class SpawnGroupDefaults
        {
            [JsonProperty(nameof(SpawnGroupOption.MaxPopulation))]
            private int MaxPopulation = 1;

            [JsonProperty(nameof(SpawnGroupOption.SpawnPerTickMin))]
            private int SpawnPerTickMin = 1;

            [JsonProperty(nameof(SpawnGroupOption.SpawnPerTickMax))]
            private int SpawnPerTickMax = 2;

            [JsonProperty(nameof(SpawnGroupOption.RespawnDelayMin))]
            private float RespawnDelayMin = 1500;

            [JsonProperty(nameof(SpawnGroupOption.RespawnDelayMax))]
            private float RespawnDelayMax = 2100;

            [JsonProperty(nameof(SpawnGroupOption.InitialSpawn))]
            private bool InitialSpawn = true;

            [JsonProperty(nameof(SpawnGroupOption.PreventDuplicates))]
            private bool PreventDuplicates;

            [JsonProperty(nameof(SpawnGroupOption.PauseScheduleWhileFull))]
            private bool PauseScheduleWhileFull;

            [JsonProperty(nameof(SpawnGroupOption.RespawnWhenNearestPuzzleResets))]
            private bool RespawnWhenNearestPuzzleResets;

            public SpawnGroupData ApplyTo(SpawnGroupData spawnGroupData)
            {
                spawnGroupData.MaxPopulation = MaxPopulation;
                spawnGroupData.SpawnPerTickMin = SpawnPerTickMin;
                spawnGroupData.SpawnPerTickMax = SpawnPerTickMax;
                spawnGroupData.RespawnDelayMin = RespawnDelayMin;
                spawnGroupData.RespawnDelayMax = RespawnDelayMax;
                spawnGroupData.InitialSpawn = InitialSpawn;
                spawnGroupData.PreventDuplicates = PreventDuplicates;
                spawnGroupData.PauseScheduleWhileFull = PauseScheduleWhileFull;
                spawnGroupData.RespawnWhenNearestPuzzleResets = RespawnWhenNearestPuzzleResets;
                return spawnGroupData;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class SpawnPointDefaults
        {
            [JsonProperty(nameof(SpawnPointOption.Exclusive))]
            private bool Exclusive = true;

            [JsonProperty(nameof(SpawnPointOption.SnapToGround))]
            private bool SnapToGround = true;

            [JsonProperty(nameof(SpawnPointOption.CheckSpace))]
            private bool CheckSpace;

            [JsonProperty(nameof(SpawnPointOption.RandomRotation))]
            private bool RandomRotation;

            [JsonProperty(nameof(SpawnPointOption.RandomRadius))]
            private float RandomRadius;

            [JsonProperty(nameof(SpawnPointOption.PlayerDetectionRadius))]
            private float PlayerDetectionRadius;

            public SpawnPointData ApplyTo(SpawnPointData spawnPointData)
            {
                spawnPointData.Exclusive = Exclusive;
                spawnPointData.SnapToGround = SnapToGround;
                spawnPointData.CheckSpace = CheckSpace;
                spawnPointData.RandomRotation = RandomRotation;
                spawnPointData.RandomRadius = RandomRadius;
                spawnPointData.PlayerDetectionRadius = PlayerDetectionRadius;
                return spawnPointData;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class PuzzleDefaults
        {
            [JsonProperty(nameof(PuzzleOption.PlayersBlockReset))]
            public bool PlayersBlockReset = true;

            [JsonProperty(nameof(PuzzleOption.PlayerDetectionRadius))]
            public float PlayerDetectionRadius = 30f;

            [JsonProperty(nameof(PuzzleOption.SecondsBetweenResets))]
            public float SecondsBetweenResets = 1800f;

            public PuzzleData ApplyTo(PuzzleData puzzleData)
            {
                puzzleData.PlayersBlockReset = PlayersBlockReset;
                puzzleData.PlayerDetectionRadius = PlayerDetectionRadius;
                puzzleData.SecondsBetweenResets = SecondsBetweenResets;
                return puzzleData;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class AddonDefaults
        {
            [JsonProperty("Spawn group defaults")]
            public SpawnGroupDefaults SpawnGroups = new();

            [JsonProperty("Spawn point defaults")]
            public SpawnPointDefaults SpawnPoints = new();

            [JsonProperty("Puzzle defaults")]
            public PuzzleDefaults Puzzles = new();
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class Configuration : BaseConfiguration
        {
            [JsonProperty("Debug", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Debug = false;

            [JsonProperty("Debug display settings")]
            public DebugDisplaySettings DebugDisplaySettings = new();

            [JsonProperty("Debug display distance")]
            private float DeprecatedDebugDisplayDistance
            {
                set
                {
                    DebugDisplaySettings.DisplayDistance = value;
                    DebugDisplaySettings.DisplayDistanceAbbreviated = value * 2;
                }
            }

            [JsonProperty("Save entities between restarts/reloads to preserve their state throughout a wipe")]
            public EntitySaveSettings EntitySaveSettings = new();

            [JsonProperty("Persist entities while the plugin is unloaded")]
            private bool DeprecatedEnableEntitySaving
            {
                set
                {
                    EntitySaveSettings.EnabledForStorageEntities = value;
                    EntitySaveSettings.EnabledForNonStorageEntities = value;
                }
            }

            [JsonProperty("Dynamic monuments")]
            public DynamicMonumentSettings DynamicMonuments = new();

            [JsonProperty("Addon defaults")]
            public AddonDefaults AddonDefaults = new();

            [JsonProperty("Deployable overrides")]
            public Dictionary<string, string> DeployableOverrides = new Dictionary<string, string>
            {
                ["arcade.machine.chippy"] = "assets/bundled/prefabs/static/chippyarcademachine.static.prefab",
                ["autoturret"] = "assets/content/props/sentry_scientists/sentry.bandit.static.prefab",
                ["bbq"] = "assets/bundled/prefabs/static/bbq.static.prefab",
                ["boombox"] = "assets/prefabs/voiceaudio/boombox/boombox.static.prefab",
                ["box.repair.bench"] = "assets/bundled/prefabs/static/repairbench_static.prefab",
                ["cctv.camera"] = "assets/prefabs/deployable/cctvcamera/cctv.static.prefab",
                ["chair"] = "assets/bundled/prefabs/static/chair.static.prefab",
                ["computerstation"] = "assets/prefabs/deployable/computerstation/computerstation.static.prefab",
                ["connected.speaker"] = "assets/prefabs/voiceaudio/hornspeaker/connectedspeaker.deployed.static.prefab",
                ["hobobarrel"] = "assets/bundled/prefabs/static/hobobarrel_static.prefab",
                ["microphonestand"] = "assets/prefabs/voiceaudio/microphonestand/microphonestand.deployed.static.prefab",
                ["modularcarlift"] = "assets/bundled/prefabs/static/modularcarlift.static.prefab",
                ["research.table"] = "assets/bundled/prefabs/static/researchtable_static.prefab",
                ["samsite"] = "assets/prefabs/npc/sam_site_turret/sam_static.prefab",
                ["small.oil.refinery"] = "assets/bundled/prefabs/static/small_refinery_static.prefab",
                ["telephone"] = "assets/bundled/prefabs/autospawn/phonebooth/phonebooth.static.prefab",
                ["vending.machine"] = "assets/prefabs/deployable/vendingmachine/npcvendingmachine.prefab",
                ["wall.frame.shopfront.metal"] = "assets/bundled/prefabs/static/wall.frame.shopfront.metal.static.prefab",
                ["workbench1"] = "assets/bundled/prefabs/static/workbench1.static.prefab",
                ["workbench2"] = "assets/bundled/prefabs/static/workbench2.static.prefab",
            };

            [JsonProperty("Xmas tree decorations (item shortnames)")]
            public string[] XmasTreeDecorations =
            {
                "xmas.decoration.baubels",
                "xmas.decoration.candycanes",
                "xmas.decoration.gingerbreadmen",
                "xmas.decoration.lights",
                "xmas.decoration.pinecone",
                "xmas.decoration.star",
                "xmas.decoration.tinsel",
            };

            public void Init()
            {
                EntitySaveSettings.Init();
                DynamicMonuments.Init();

                if (XmasTreeDecorations != null)
                {
                    foreach (var itemShortName in XmasTreeDecorations)
                    {
                        var itemDefinition = ItemManager.FindItemDefinition(itemShortName);
                        if (itemDefinition == null)
                        {
                            LogError(($"Invalid item short name in config: {itemShortName}"));
                            continue;
                        }

                        if (itemDefinition.GetComponent<ItemModXMasTreeDecoration>() == null)
                        {
                            LogError(($"Item is not an Xmas tree decoration: {itemShortName}"));
                            continue;
                        }
                    }
                }
            }
        }

        #region Configuration Helpers

        private class BaseConfiguration
        {
            public string ToJson()
            {
                return JsonConvert.SerializeObject(this);
            }

            public Dictionary<string, object> ToDictionary()
            {
                return JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
            }
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json)
            {
                return ToObject(JToken.Parse(json));
            }

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(BaseConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                if (currentRaw.TryGetValue(key, out var currentRawValue))
                {
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (currentWithDefaults[key] is Dictionary<string, object> defaultDictValue)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                        {
                            changed = true;
                        }
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_config))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                LogError(e.Message);
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_config, true);
        }

        #endregion

        #endregion

        #region Localization

        private class LangEntry
        {
            public static List<LangEntry> AllLangEntries = new List<LangEntry>();

            public static readonly LangEntry0 NotApplicable = new("NotApplicable", "N/A");
            public static readonly LangEntry0 ErrorNoPermission = new("Error.NoPermission", "You don't have permission to do that.");
            public static readonly LangEntry0 ErrorMonumentFinderNotLoaded = new("Error.MonumentFinderNotLoaded", "Error: Monument Finder is not loaded.");
            public static readonly LangEntry0 ErrorNoMonuments = new("Error.NoMonuments", "Error: No monuments found.");
            public static readonly LangEntry2 ErrorNotAtMonument = new("Error.NotAtMonument", "Error: Not at a monument. Nearest is <color=#fd4>{0}</color> with distance <color=#fd4>{1}</color>");
            public static readonly LangEntry0 ErrorNoSuitableAddonFound = new("Error.NoSuitableAddonFound", "Error: No suitable addon found.");
            public static readonly LangEntry0 ErrorNoCustomAddonFound = new("Error.NoCustomAddonFound", "Error: No custom addon found.");
            public static readonly LangEntry0 ErrorEntityNotEligible = new("Error.EntityNotEligible", "Error: That entity is not managed by Monument Addons.");
            public static readonly LangEntry0 ErrorNoSpawnPointFound = new("Error.NoSpawnPointFound", "Error: No spawn point found.");
            public static readonly LangEntry1 ErrorSetSyntaxGeneric = new("Error.Set.Syntax.Generic", "Syntax: <color=#fd4>{0} set <option> <value></color>");
            public static readonly LangEntry2 ErrorSetSyntax = new("Error.Set.Syntax", "Syntax: <color=#fd4>{0} set {1} <value></color>");
            public static readonly LangEntry1 ErrorSetUnknownOption = new("Error.Set.UnknownOption", "Unrecognized option: <color=#fd4>{0}</color>");

            public static readonly LangEntry0 WarningRecommendSpawnPoint = new("Warning.RecommandSpawnPoints", "<color=#fd4>Warning: It is not recommended to use /maspawn to place temporary entities such as NPCs, loot containers, and vehicles. Consider creating a spawn point for that entity instead.</color>");

            public static readonly LangEntry0 SpawnErrorSyntax = new("Spawn.Error.Syntax", "Syntax: <color=#fd4>maspawn <entity></color>");
            public static readonly LangEntry0 SpawnErrorNoProfileSelected = new("Spawn.Error.NoProfileSelected", "Error: No profile selected. Run <color=#fd4>maprofile help</color> for help.");
            public static readonly LangEntry1 SpawnErrorEntityNotFound = new("Spawn.Error.EntityNotFound2", "Error: No entity found matching name <color=#fd4>{0}</color>.");
            public static readonly LangEntry1 SpawnErrorEntityOrAddonNotFound = new("Spawn.Error.EntityOrCustomNotFound", "Error: No entity or custom addon found matching name <color=#fd4>{0}</color>.");
            public static readonly LangEntry0 SpawnErrorMultipleMatches = new("Spawn.Error.MultipleMatches", "Multiple matches:\n");
            public static readonly LangEntry0 ErrorNoSurface = new("Error.NoSurface", "Error: No valid surface found.");
            public static readonly LangEntry3 SpawnSuccess = new("Spawn.Success2", "Spawned entity at <color=#fd4>{0}</color> matching monument(s) and saved to <color=#fd4>{1}</color> profile for monument <color=#fd4>{2}</color>.");
            public static readonly LangEntry3 KillSuccess = new("Kill.Success4", "Killed <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and removed from profile <color=#fd4>{2}</color>. Run <color=#fd4>maundo</color> to restore it.");
            public static readonly LangEntry0 SaveNothingToDo = new("Save.NothingToDo", "No changes detected for that entity.");
            public static readonly LangEntry2 SaveSuccess = new("Save.Success", "Updated entity at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.");

            public static readonly LangEntry0 PrefabErrorSyntax = new("Prefab.Error.Syntax", "Syntax: <color=#fd4>maprefab <prefab></color>");
            public static readonly LangEntry1 PrefabErrorIsEntity = new("Prefab.Error.IsEntity", "Error: <color=#fd4>{0}</color> is an entity prefab. Use <color=#fd4>maspawn</color> instead of <color=#fd4>maprefab</color>.");
            public static readonly LangEntry1 PrefabErrorNotFound = new("Prefab.Error.NotFound", "Error: No allowed prefab found matching name <color=#fd4>{0}</color>.");
            public static readonly LangEntry3 PrefabSuccess = new("Prefab.Success", "Created prefab instance at <color=#fd4>{0}</color> matching monument(s) and saved to <color=#fd4>{1}</color> profile for monument <color=#fd4>{2}</color>.");

            public static readonly LangEntry0 UndoNotFound = new("Undo.NotFound", "No recent action to undo.");
            public static readonly LangEntry3 UndoKillSuccess = new("Undo.Kill.Success", "Successfully restored <color=#fd4>{0}</color> at monument <color=#fd4>{1}</color> in profile <color=#fd4>{2}</color>.");

            public static readonly LangEntry0 EditSynax = new("Edit.Syntax", "Syntax: <color=#fd4>maedit <addon-name> <arg1> <arg2> ...</color>");
            public static readonly LangEntry1 EditErrorNoMatch = new("Edit.Error.NoMatch", "Error: That custom addon does not have name <color=#fd4>{0}</color>.");
            public static readonly LangEntry1 EditErrorNotEditable = new("Edit.Error.NotEditable", "Error: The custom addon <color=#fd4>{0}</color> does not support editing.");
            public static readonly LangEntry1 EditSuccess = new("Edit.Success", "Successfully edited custom addon <color=#fd4>{0}</color>.");

            public static readonly LangEntry0 PasteNotCompatible = new("Paste.NotCompatible", "CopyPaste is not loaded or its version is incompatible.");
            public static readonly LangEntry0 PasteSyntax = new("Paste.Syntax2", "Syntax: <color=#fd4>mapaste <file> <arg1> <arg2> ...</color>");
            public static readonly LangEntry1 PasteNotFound = new("Paste.NotFound", "File <color=#fd4>{0}</color> does not exist.");
            public static readonly LangEntry4 PasteSuccess = new("Paste.Success", "Pasted <color=#fd4>{0}</color> at <color=#fd4>{1}</color> (x<color=#fd4>{2}</color>) and saved to profile <color=#fd4>{3}</color>.");

            public static readonly LangEntry0 AddonTypeUnknown = new("AddonType.Unknown", "Addon");
            public static readonly LangEntry0 AddonTypeEntity = new("AddonType.Entity", "Entity");
            public static readonly LangEntry0 AddonTypePrefab = new("AddonType.Prefab", "Prefab");
            public static readonly LangEntry0 AddonTypeSpawnPoint = new("AddonType.SpawnPoint", "Spawn point");
            public static readonly LangEntry0 AddonTypePaste = new("AddonType.Paste", "Paste");
            public static readonly LangEntry0 AddonTypeCustom = new("AddonType.Custom", "Custom");

            public static readonly LangEntry1 SpawnGroupCreateSyntax = new("SpawnGroup.Create.Syntax", "Syntax: <color=#fd4>{0} create <name></color>");
            public static readonly LangEntry3 SpawnGroupCreateNameInUse = new("SpawnGroup.Create.NameInUse", "There is already a spawn group named <color=#fd4>{0}</color> at monument <color=#fd4>{1}</color> in profile <color=#fd4>{2}</color>. Please use a different name.");
            public static readonly LangEntry1 SpawnGroupCreateSucces = new("SpawnGroup.Create.Success", "Successfully created spawn group <color=#fd4>{0}</color>.");
            public static readonly LangEntry3 SpawnGroupSetSuccess = new("SpawnGroup.Set.Success", "Successfully updated spawn group <color=#fd4>{0}</color> with option <color=#fd4>{1}</color>: <color=#fd4>{2}</color>.");
            public static readonly LangEntry1 SpawnGroupAddSyntax = new("SpawnGroup.Add.Syntax", "Syntax: <color=#fd4>{0} add <entity> <weight></color>");
            public static readonly LangEntry3 SpawnGroupAddSuccess = new("SpawnGroup.Add.Success", "Successfully added entity <color=#fd4>{0}</color> with weight <color=#fd4>{1}</color> to spawn group <color=#fd4>{2}</color>.");
            public static readonly LangEntry1 SpawnGroupRemoveSyntax = new("SpawnGroup.Remove.Syntax2", "Syntax: <color=#fd4>{0} remove <entity></color>");
            public static readonly LangEntry2 SpawnGroupRemoveMultipleMatches = new("SpawnGroup.Remove.MultipleMatches", "Multiple entities in spawn group <color=#fd4>{0}</color> found matching: <color=#fd4>{1}</color>. Please be more specific.");
            public static readonly LangEntry2 SpawnGroupRemoveNoMatch = new("SpawnGroup.Remove.NoMatch", "No entity found in spawn group <color=#fd4>{0}</color> matching <color=#fd4>{1}</color>");
            public static readonly LangEntry2 SpawnGroupRemoveSuccess = new("SpawnGroup.Remove.Success", "Successfully removed entity <color=#fd4>{0}</color> from spawn group <color=#fd4>{1}</color>.");

            public static readonly LangEntry1 SpawnGroupNotFound = new("SpawnGroup.NotFound", "No spawn group found with name: <color=#fd4>{0}</color>");
            public static readonly LangEntry1 SpawnGroupMultipeMatches = new("SpawnGroup.MultipeMatches2", "Multiple spawn groups found matching name: <color=#fd4>{0}</color>");
            public static readonly LangEntry1 SpawnPointCreateSyntax = new("SpawnPoint.Create.Syntax", "Syntax: <color=#fd4>{0} create <group_name></color>");
            public static readonly LangEntry1 SpawnPointCreateSuccess = new("SpawnPoint.Create.Success", "Successfully added spawn point to spawn group <color=#fd4>{0}</color>.");
            public static readonly LangEntry2 SpawnPointSetSuccess = new("SpawnPoint.Set.Success", "Successfully updated spawn point with option <color=#fd4>{0}</color>: <color=#fd4>{1}</color>.");

            public static readonly LangEntry0 SpawnGroupHelpHeader = new("SpawnGroup.Help.Header", "<size=18>Monument Addons Spawn Group Commands</size>");
            public static readonly LangEntry1 SpawnGroupHelpCreate = new("SpawnGroup.Help.Create", "<color=#fd4>{0} create <name></color> - Create a spawn group with a spawn point");
            public static readonly LangEntry1 SpawnGroupHelpSet = new("SpawnGroup.Help.Set", "<color=#fd4>{0} set <option> <value></color> - Set a property of a spawn group");
            public static readonly LangEntry1 SpawnGroupHelpAdd = new("SpawnGroup.Help.Add", "<color=#fd4>{0} add <entity> <weight></color> - Add an entity prefab to a spawn group");
            public static readonly LangEntry1 SpawnGroupHelpRemove = new("SpawnGroup.Help.Remove", "<color=#fd4>{0} remove <entity> <weight></color> - Remove an entity prefab from a spawn group");
            public static readonly LangEntry1 SpawnGroupHelpSpawn = new("SpawnGroup.Help.Spawn", "<color=#fd4>{0} spawn</color> - Run one spawn tick for a spawn group");
            public static readonly LangEntry1 SpawnGroupHelpRespawn = new("SpawnGroup.Help.Respawn", "<color=#fd4>{0} respawn</color> - Despawn entities for a spawn group and run one spawn tick");

            public static readonly LangEntry0 SpawnPointHelpHeader = new("SpawnPoint.Help.Header", "<size=18>Monument Addons Spawn Point Commands</size>");
            public static readonly LangEntry1 SpawnPointHelpCreate = new("SpawnPoint.Help.Create", "<color=#fd4>{0} create <group_name></color> - Create a spawn point");
            public static readonly LangEntry1 SpawnPointHelpSet = new("SpawnPoint.Help.Set", "<color=#fd4>{0} set <option> <value></color> - Set a property of a spawn point");

            public static readonly LangEntry0 SpawnGroupSetHelpName = new("SpawnGroup.Set.Help.Name", "<color=#fd4>Name</color>: string");
            public static readonly LangEntry0 SpawnGroupSetHelpMaxPopulation = new("SpawnGroup.Set.Help.MaxPopulation", "<color=#fd4>MaxPopulation</color>: number");
            public static readonly LangEntry0 SpawnGroupSetHelpRespawnDelayMin = new("SpawnGroup.Set.Help.RespawnDelayMin", "<color=#fd4>RespawnDelayMin</color>: number");
            public static readonly LangEntry0 SpawnGroupSetHelpRespawnDelayMax = new("SpawnGroup.Set.Help.RespawnDelayMax", "<color=#fd4>RespawnDelayMax</color>: number");
            public static readonly LangEntry0 SpawnGroupSetHelpSpawnPerTickMin = new("SpawnGroup.Set.Help.SpawnPerTickMin", "<color=#fd4>SpawnPerTickMin</color>: number");
            public static readonly LangEntry0 SpawnGroupSetHelpSpawnPerTickMax = new("SpawnGroup.Set.Help.SpawnPerTickMax", "<color=#fd4>SpawnPerTickMax</color>: number");
            public static readonly LangEntry0 SpawnGroupSetHelpInitialSpawn = new("SpawnGroup.Set.Help.InitialSpawn", "<color=#fd4>InitialSpawn</color>: true | false");
            public static readonly LangEntry0 SpawnGroupSetHelpPreventDuplicates = new("SpawnGroup.Set.Help.PreventDuplicates", "<color=#fd4>PreventDuplicates</color>: true | false");
            public static readonly LangEntry0 SpawnGroupSetHelpPauseScheduleWhileFull = new("SpawnGroup.Set.Help.PauseScheduleWhileFull","<color=#fd4>PauseScheduleWhileFull</color>: true | false");
            public static readonly LangEntry0 SpawnGroupSetHelpRespawnWhenNearestPuzzleResets = new("SpawnGroup.Set.Help.RespawnWhenNearestPuzzleResets","<color=#fd4>RespawnWhenNearestPuzzleResets</color>: true | false");

            public static readonly LangEntry0 SpawnPointSetHelpExclusive = new("SpawnPoint.Set.Help.Exclusive", "<color=#fd4>Exclusive</color>: true | false");
            public static readonly LangEntry0 SpawnPointSetHelpSnapToGround = new("SpawnPoint.Set.Help.SnapToGround", "<color=#fd4>SnapToGround</color>: true | false");
            public static readonly LangEntry0 SpawnPointSetHelpCheckSpace = new("SpawnPoint.Set.Help.CheckSpace", "<color=#fd4>CheckSpace</color>: true | false");
            public static readonly LangEntry0 SpawnPointSetHelpRandomRotation = new("SpawnPoint.Set.Help.RandomRotation", "<color=#fd4>RandomRotation</color>: true | false");
            public static readonly LangEntry0 SpawnPointSetHelpRandomRadius = new("SpawnPoint.Set.Help.RandomRadius", "<color=#fd4>RandomRadius</color>: number");
            public static readonly LangEntry0 SpawnPointSetHelpPlayerDetectionRadius = new("SpawnPoint.Set.Help.PlayerDetectionRadius", "<color=#fd4>PlayerDetectionRadius</color>: number");

            public static readonly LangEntry1 PuzzleAddSpawnGroupSyntax = new("Puzzle.AddSpawnGroup.Syntax", "Syntax: <color=#fd4>{0} add <group_name></color>");
            public static readonly LangEntry1 PuzzleAddSpawnGroupSuccess = new("Puzzle.AddSpawnGroup.Success", "Successfully added spawn group <color=#fd4>{0}</color> to puzzle.");
            public static readonly LangEntry1 PuzzleRemoveSpawnGroupSyntax = new("Puzzle.RemoveSpawnGroup.Syntax", "Syntax: <color=#fd4>{0} remove <group_name></color>");
            public static readonly LangEntry1 PuzzleRemoveSpawnGroupSuccess = new("Puzzle.RemoveSpawnGroup.Success", "Successfully removed spawn group <color=#fd4>{0}</color> from puzzle.");
            public static readonly LangEntry0 PuzzleNotPresent = new("Puzzle.Error.NotPresent", "That is not a puzzle entity.");
            public static readonly LangEntry1 PuzzleNotConnected = new("Puzzle.Error.NotConnected", "Entity <color=#fd4>{0}</color> is not connected to a puzzle.");
            public static readonly LangEntry0 PuzzleResetSuccess = new("Puzzle.Reset.Success", "Puzzle successfully reset.");
            public static readonly LangEntry2 PuzzleSetSuccess = new("Puzzle.Set.Success", "Successfully updated puzzle with option <color=#fd4>{0}</color>: <color=#fd4>{1}</color>.");

            public static readonly LangEntry0 PuzzleHelpHeader = new("Puzzle.Help.Header", "<size=18>Monument Addons Puzzle Commands</size>");
            public static readonly LangEntry1 PuzzleHelpReset = new("Puzzle.Help.Reset", "<color=#fd4>{0} reset</color> - Reset the puzzle connected to the entity you are looking at");
            public static readonly LangEntry1 PuzzleHelpSet = new("Puzzle.Help.Set", "<color=#fd4>{0} set <option> <value></color> - Set a property of a puzzle");
            public static readonly LangEntry1 PuzzleHelpAdd = new("Puzzle.Help.Add", "<color=#fd4>{0} add <group_name></color> - Associate a spawn group with a puzzle");
            public static readonly LangEntry1 PuzzleHelpRemove = new("Puzzle.Help.Remove", "<color=#fd4>{0} remove <group_name></color> - Disassociate a spawn group with a puzzle");

            public static readonly LangEntry0 PuzzleSetHelpMaxPlayersBlockReset = new("Puzzle.Set.Help.MaxPlayersBlockReset", "<color=#fd4>PlayersBlockReset</color>: true | false");
            public static readonly LangEntry0 PuzzleSetHelpPlayerDetectionRadius = new("Puzzle.Set.Help.PlayerDetectionRadius", "<color=#fd4>PlayerDetectionRadius</color>: number");
            public static readonly LangEntry0 PuzzleSetHelpSecondsBetweenResets = new("Puzzle.Set.Help.SecondsBetweenResets", "<color=#fd4>SecondsBetweenResets</color>: number");

            public static readonly LangEntry1 ShowVanillaNoSpawnPoints = new("Show.Vanilla.NoSpawnPoints", "No spawn points found in <color=#fd4>{0}</color>.");
            public static readonly LangEntry1 GenerateSuccess = new("Generate.Success", "Successfully generated profile <color=#fd4>{0}</color>.");

            public static readonly LangEntry1 ShowSuccess = new("Show.Success", "Showing nearby Monument Addons for <color=#fd4>{0}</color>.");
            public static readonly LangEntry1 ShowLabelPlugin = new("Show.Label.Plugin", "Plugin: {0}");
            public static readonly LangEntry1 ShowLabelProfile = new("Show.Label.Profile", "Profile: {0}");
            public static readonly LangEntry2 ShowLabelCustomMonument = new("Show.Label.CustomMonument", "Custom Monument: {0} (x{1})");
            public static readonly LangEntry2 ShowLabelMonument = new("Show.Label.Monument", "Monument: {0} (x{1})");
            public static readonly LangEntry3 ShowLabelMonumentWithTier = new("Show.Label.MonumentWithTier", "Monument: {0} (x{1} | {2})");
            public static readonly LangEntry1 ShowLabelSkin = new("Show.Label.Skin", "Skin: {0}");
            public static readonly LangEntry1 ShowLabelScale = new("Show.Label.Scale", "Scale: {0}");
            public static readonly LangEntry1 ShowLabelRCIdentifier = new("Show.Label.RCIdentifier", "RC Identifier: {0}");

            public static readonly LangEntry1 ShowHeaderEntity = new("Show.Header.Entity", "Entity: {0}");
            public static readonly LangEntry1 ShowHeaderPrefab = new("Show.Header.Prefab", "Prefab: {0}");
            public static readonly LangEntry0 ShowHeaderPuzzle = new("Show.Header.Puzzle", "Puzzle");
            public static readonly LangEntry1 ShowHeaderSpawnGroup = new("Show.Header.SpawnGroup", "Spawn Group: {0}");
            public static readonly LangEntry1 ShowHeaderVanillaSpawnGroup = new("Show.Header.Vanilla.SpawnGroup", "Vanilla Spawn Group: {0}");
            public static readonly LangEntry1 ShowHeaderSpawnPoint = new("Show.Header.SpawnPoint", "Spawn Point ({0})");
            public static readonly LangEntry1 ShowHeaderVanillaSpawnPoint = new("Show.Header.Vanilla.SpawnPoint", "Vanilla Spawn Point ({0})");
            public static readonly LangEntry1 ShowHeaderVanillaIndividualSpawnPoint = new("Show.Header.Vanilla.IndividualSpawnPoint", "Vanilla Individual Spawn Point: {0}");
            public static readonly LangEntry1 ShowHeaderPaste = new("Show.Header.Paste", "Paste: {0}");
            public static readonly LangEntry1 ShowHeaderCustom = new("Show.Header.Custom", "Custom Addon: {0}");

            public static readonly LangEntry1 ShowLabelFlags = new("Show.Label.SpawnPoint.Flags", "Flags: {0}");
            public static readonly LangEntry0 ShowLabelSpawnPointExclusive = new("Show.Label.SpawnPoint.Exclusive", "Exclusive");
            public static readonly LangEntry0 ShowLabelSpawnPointRandomRotation = new("Show.Label.SpawnPoint.RandomRotation2", "RandomRotation");
            public static readonly LangEntry0 ShowLabelSpawnPointSnapToGround = new("Show.Label.SpawnPoint.SnapToGround", "SnapToGround");
            public static readonly LangEntry0 ShowLabelSpawnPointCheckSpace = new("Show.Label.SpawnPoint.CheckSpace", "CheckSpace");
            public static readonly LangEntry1 ShowLabelSpawnPointRandomRadius = new("Show.Label.SpawnPoint.RandomRadius", "Random spawn radius: {0:f1}");

            public static readonly LangEntry1 ShowLabelSpawnPoints = new("Show.Label.Points", "Spawn points: {0}");
            public static readonly LangEntry1 ShowLabelTiers = new("Show.Label.Tiers", "Tiers: {0}");
            public static readonly LangEntry0 ShowLabelSpawnWhenParentSpawns = new("Show.Label.SpawnWhenParentSpawns", "Spawn when parent spawns");
            public static readonly LangEntry0 ShowLabelSpawnOnServerStart = new("Show.Label.SpawnOnServerStart", "Spawn on server start");
            public static readonly LangEntry0 ShowLabelSpawnOnMapWipe = new("Show.Label.SpawnOnMapWipe", "Spawn on map wipe");
            public static readonly LangEntry0 ShowLabelInitialSpawn = new("Show.Label.InitialSpawn", "InitialSpawn");
            public static readonly LangEntry0 ShowLabelPreventDuplicates = new("Show.Label.PreventDuplicates2", "PreventDuplicates");
            public static readonly LangEntry0 ShowLabelPauseScheduleWhileFull = new("Show.Label.PauseScheduleWhileFull", "PauseScheduleWhileFull");
            public static readonly LangEntry0 ShowLabelRespawnWhenNearestPuzzleResets = new("Show.Label.RespawnWhenNearestPuzzleResets", "RespawnWhenNearestPuzzleResets");
            public static readonly LangEntry2 ShowLabelPopulation = new("Show.Label.Population", "Population: {0} / {1}");
            public static readonly LangEntry2 ShowLabelRespawnPerTick = new("Show.Label.RespawnPerTick", "Spawn per tick: {0} - {1}");
            public static readonly LangEntry2 ShowLabelRespawnDelay = new("Show.Label.RespawnDelay", "Respawn delay: {0} - {1}");
            public static readonly LangEntry1 ShowLabelNextSpawn = new("Show.Label.NextSpawn", "Next spawn: {0}");
            public static readonly LangEntry0 ShowLabelNextSpawnQueued = new("Show.Label.NextSpawn.Queued", "Queued");
            public static readonly LangEntry0 ShowLabelNextSpawnPaused = new("Show.Label.NextSpawn.Paused", "Paused");
            public static readonly LangEntry0 ShowLabelEntities = new("Show.Label.Entities", "Entities:");
            public static readonly LangEntry3 ShowLabelEntityDetail = new("Show.Label.Entities.Detail2", "{0} | weight: {1} ({2:P1})");
            public static readonly LangEntry0 ShowLabelNoEntities = new("Show.Label.NoEntities", "No entities configured. Run /maspawngroup add <entity> <weight>");
            public static readonly LangEntry1 ShowLabelPlayerDetectionRadius = new("Show.Label.PlayerDetectionRadius", "Player detection radius: {0:f1}");
            public static readonly LangEntry0 ShowLabelPlayerDetectedInRadius = new("Show.Label.PlayerDetectedInRadius", "(!) Player detected in radius (!)");

            public static readonly LangEntry1 ShowLabelPuzzlePlayersBlockReset = new("Show.Label.Puzzle.PlayersBlockReset", "Players block reset progress: {0}");
            public static readonly LangEntry1 ShowLabelPuzzleTimeBetweenResets = new("Show.Label.Puzzle.TimeBetweenResets", "Time between resets: {0}");
            public static readonly LangEntry1 ShowLabelPuzzleNextReset = new("Show.Label.Puzzle.NextReset", "Time until next reset: {0}");
            public static readonly LangEntry0 ShowLabelPuzzleNextResetOverdue = new("Show.Label.Puzzle.NextReset.Overdue", "Any moment now");
            public static readonly LangEntry1 ShowLabelPuzzleSpawnGroups = new("Show.Label.Puzzle.SpawnGroups", "Resets spawn groups: {0}");

            public static readonly LangEntry2 SkinGet = new("Skin.Get", "Skin ID: <color=#fd4>{0}</color>. Run <color=#fd4>{1} <skin id></color> to change it.");
            public static readonly LangEntry1 SkinSetSyntax = new("Skin.Set.Syntax", "Syntax: <color=#fd4>{0} <skin id></color>");
            public static readonly LangEntry3 SkinSetSuccess = new("Skin.Set.Success2", "Updated skin ID to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.");
            public static readonly LangEntry2 SkinErrorRedirect = new("Skin.Error.Redirect", "Error: Skin <color=#fd4>{0}</color> is a redirect skin and cannot be set directly. Instead, spawn the entity as <color=#fd4>{1}</color>.");

            public static readonly LangEntry3 FlagsGet = new("Flags.Get", "Current flags: <color=#fd4>{0}</color>\nEnabled flags: <color=#fd4>{1}</color>\nDisabled flags: <color=#fd4>{2}</color>");
            public static readonly LangEntry1 FlagsSetSyntax = new("Flags.Syntax", "Syntax: <color=#fd4>{0} <flag></color>");
            public static readonly LangEntry1 FlagsEnableSuccess = new("Flags.Enable.Success", "Overrode flag <color=#fd4>{0}</color> to enabled");
            public static readonly LangEntry1 FlagsDisableSuccess = new("Flags.Disable.Success", "Overrode flag <color=#fd4>{0}</color> to disabled");
            public static readonly LangEntry1 FlagsUnsetSuccess = new("Flags.Unset.Success", "Removed override for flag <color=#fd4>{0}</color>");

            public static readonly LangEntry1 CCTVSetIdSyntax = new("CCTV.SetId.Error.Syntax", "Syntax: <color=#fd4>{0} <id></color>");
            public static readonly LangEntry3 CCTVSetIdSuccess = new("CCTV.SetId.Success2", "Updated CCTV id to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.");
            public static readonly LangEntry2 CCTVSetDirectionSuccess = new("CCTV.SetDirection.Success2", "Updated CCTV direction at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.");

            public static readonly LangEntry1 SkullNameSyntax = new("SkullName.Syntax", "Syntax: <color=#fd4>{0} <name></color>");
            public static readonly LangEntry3 SkullNameSetSuccess = new("SkullName.Set.Success", "Updated skull name to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.");

            public static readonly LangEntry0 SetHeadNoHeadItem = new("Head.Set.NoHeadItem", "Error: You must be holding a head bag item to do that.");
            public static readonly LangEntry0 SetHeadMismatch = new("Head.Set.Mismatch", "Error: That is the wrong type of head for that trophy.");
            public static readonly LangEntry2 SetHeadSuccess = new("Head.Set.Success", "Updated head trophy according to your equipped item at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.");

            public static readonly LangEntry1 CardReaderSetLevelSyntax = new("CardReader.SetLevel.Error.Syntax", "Syntax: <color=#fd4>{0} <1-3></color>");
            public static readonly LangEntry1 CardReaderSetLevelSuccess = new("CardReader.SetLevel.Success", "Updated card reader access level to <color=#fd4>{0}</color>.");

            public static readonly LangEntry0 ProfileListEmpty = new("Profile.List.Empty", "You have no profiles. Create one with <color=#fd4>maprofile create <name></maprofile>");
            public static readonly LangEntry0 ProfileListHeader = new("Profile.List.Header", "<size=18>Monument Addons Profiles</size>");
            public static readonly LangEntry2 ProfileListItemEnabled = new("Profile.List.Item.Enabled2", "<color=#fd4>{0}</color>{1} - <color=#6e6>ENABLED</color>");
            public static readonly LangEntry2 ProfileListItemDisabled = new("Profile.List.Item.Disabled2", "<color=#fd4>{0}</color>{1} - <color=#ccc>DISABLED</color>");
            public static readonly LangEntry2 ProfileListItemSelected = new("Profile.List.Item.Selected2", "<color=#fd4>{0}</color>{1} - <color=#6cf>SELECTED</color>");
            public static readonly LangEntry1 ProfileByAuthor = new("Profile.ByAuthor", " by {0}");

            public static readonly LangEntry0 ProfileInstallSyntax = new("Profile.Install.Syntax", "Syntax: <color=#fd4>maprofile install <url></color>");
            public static readonly LangEntry0 ProfileInstallShorthandSyntax = new("Profile.Install.Shorthand.Syntax", "Syntax: <color=#fd4>mainstall <url></color>");
            public static readonly LangEntry1 ProfileUrlInvalid = new("Profile.Url.Invalid", "Invalid URL: {0}");
            public static readonly LangEntry1 ProfileAlreadyExistsNotEmpty = new("Profile.Error.AlreadyExists.NotEmpty", "Error: Profile <color=#fd4>{0}</color> already exists and is not empty.");
            public static readonly LangEntry2 ProfileInstallSuccess = new("Profile.Install.Success2", "Successfully installed and <color=#6e6>ENABLED</color> profile <color=#fd4>{0}</color>{1}.");
            public static readonly LangEntry1 ProfileInstallError = new("Profile.Install.Error", "Error installing profile from url {0}. See the error logs for more details.");
            public static readonly LangEntry2 ProfileDownloadError = new("Profile.Download.Error", "Error downloading profile from url {0}\nStatus code: {1}");
            public static readonly LangEntry2 ProfileParseError = new("Profile.Parse.Error", "Error parsing profile from url {0}\n{1}");

            public static readonly LangEntry0 ProfileDescribeSyntax = new("Profile.Describe.Syntax", "Syntax: <color=#fd4>maprofile describe <name></color>");
            public static readonly LangEntry1 ProfileNotFound = new("Profile.Error.NotFound", "Error: Profile <color=#fd4>{0}</color> not found.");
            public static readonly LangEntry1 ProfileEmpty = new("Profile.Empty", "Profile <color=#fd4>{0}</color> is empty.");
            public static readonly LangEntry1 ProfileDescribeHeader = new("Profile.Describe.Header", "Describing profile <color=#fd4>{0}</color>.");
            public static readonly LangEntry4 ProfileDescribeItem = new("Profile.Describe.Item2", "{0}: <color=#fd4>{1}</color> x{2} @ {3}");
            public static readonly LangEntry0 ProfileSelectSyntax = new("Profile.Select.Syntax", "Syntax: <color=#fd4>maprofile select <name></color>");
            public static readonly LangEntry1 ProfileSelectSuccess = new("Profile.Select.Success2", "Successfully <color=#6cf>SELECTED</color> profile <color=#fd4>{0}</color>.");
            public static readonly LangEntry1 ProfileSelectEnableSuccess = new("Profile.Select.Enable.Success", "Successfully <color=#6cf>SELECTED</color> and <color=#6e6>ENABLED</color> profile <color=#fd4>{0}</color>.");

            public static readonly LangEntry0 ProfileEnableSyntax = new("Profile.Enable.Syntax", "Syntax: <color=#fd4>maprofile enable <name></color>");
            public static readonly LangEntry1 ProfileAlreadyEnabled = new("Profile.AlreadyEnabled", "Profile <color=#fd4>{0}</color> is already <color=#6e6>ENABLED</color>.");
            public static readonly LangEntry1 ProfileEnableSuccess = new("Profile.Enable.Success", "Profile <color=#fd4>{0}</color> is now: <color=#6e6>ENABLED</color>.");
            public static readonly LangEntry0 ProfileDisableSyntax = new("Profile.Disable.Syntax", "Syntax: <color=#fd4>maprofile disable <name></color>");
            public static readonly LangEntry1 ProfileAlreadyDisabled = new("Profile.AlreadyDisabled2", "Profile <color=#fd4>{0}</color> is already <color=#ccc>DISABLED</color>.");
            public static readonly LangEntry1 ProfileDisableSuccess = new("Profile.Disable.Success2", "Profile <color=#fd4>{0}</color> is now: <color=#ccc>DISABLED</color>.");
            public static readonly LangEntry0 ProfileReloadSyntax = new("Profile.Reload.Syntax", "Syntax: <color=#fd4>maprofile reload <name></color>");
            public static readonly LangEntry1 ProfileNotEnabled = new("Profile.NotEnabled", "Error: Profile <color=#fd4>{0}</color> is not enabled.");
            public static readonly LangEntry1 ProfileReloadSuccess = new("Profile.Reload.Success", "Reloaded profile <color=#fd4>{0}</color>.");

            public static readonly LangEntry0 ProfileCreateSyntax = new("Profile.Create.Syntax", "Syntax: <color=#fd4>maprofile create <name></color>");
            public static readonly LangEntry1 ProfileAlreadyExists = new("Profile.Error.AlreadyExists", "Error: Profile <color=#fd4>{0}</color> already exists.");
            public static readonly LangEntry1 ProfileCreateSuccess = new("Profile.Create.Success", "Successfully created and <color=#6cf>SELECTED</color> profile <color=#fd4>{0}</color>.");
            public static readonly LangEntry0 ProfileRenameSyntax = new("Profile.Rename.Syntax", "Syntax: <color=#fd4>maprofile rename <old name> <new name></color>");
            public static readonly LangEntry2 ProfileRenameSuccess = new("Profile.Rename.Success2", "Successfully renamed profile <color=#fd4>{0}</color> to <color=#fd4>{1}</color>");
            public static readonly LangEntry0 ProfileClearSyntax = new("Profile.Clear.Syntax", "Syntax: <color=#fd4>maprofile clear <name></color>");
            public static readonly LangEntry1 ProfileClearSuccess = new("Profile.Clear.Success", "Successfully cleared profile <color=#fd4>{0}</color>.");
            public static readonly LangEntry0 ProfileDeleteSyntax = new("Profile.Delete.Syntax", "Syntax: <color=#fd4>maprofile delete <name></color>");
            public static readonly LangEntry1 ProfileDeleteBlocked = new("Profile.Delete.Blocked", "Profile <color=#fd4>{0}</color> must be empty or disabled before it can be deleted.");
            public static readonly LangEntry1 ProfileDeleteSuccess = new("Profile.Delete.Success", "Successfully deleted profile <color=#fd4>{0}</color>.");

            public static readonly LangEntry0 ProfileMoveToSyntax = new("Profile.MoveTo.Syntax", "Syntax: <color=#fd4>maprofile moveto <name></color>");
            public static readonly LangEntry2 ProfileMoveToAlreadyPresent = new("Profile.MoveTo.AlreadyPresent", "Error: <color=#fd4>{0}</color> is already part of profile <color=#fd4>{1}</color>.");
            public static readonly LangEntry3 ProfileMoveToSuccess = new("Profile.MoveTo.Success", "Successfully moved <color=#fd4>{0}</color> from profile <color=#fd4>{1}</color> to <color=#fd4>{2}</color>.");

            public static readonly LangEntry0 ProfileHelpHeader = new("Profile.Help.Header", "<size=18>Monument Addons Profile Commands</size>");
            public static readonly LangEntry0 ProfileHelpList = new("Profile.Help.List", "<color=#fd4>maprofile list</color> - List all profiles");
            public static readonly LangEntry0 ProfileHelpDescribe = new("Profile.Help.Describe", "<color=#fd4>maprofile describe <name></color> - Describe profile contents");
            public static readonly LangEntry0 ProfileHelpEnable = new("Profile.Help.Enable", "<color=#fd4>maprofile enable <name></color> - Enable a profile");
            public static readonly LangEntry0 ProfileHelpDisable = new("Profile.Help.Disable", "<color=#fd4>maprofile disable <name></color> - Disable a profile");
            public static readonly LangEntry0 ProfileHelpReload = new("Profile.Help.Reload", "<color=#fd4>maprofile reload <name></color> - Reload a profile from disk");
            public static readonly LangEntry0 ProfileHelpSelect = new("Profile.Help.Select", "<color=#fd4>maprofile select <name></color> - Select a profile");
            public static readonly LangEntry0 ProfileHelpCreate = new("Profile.Help.Create", "<color=#fd4>maprofile create <name></color> - Create a new profile");
            public static readonly LangEntry0 ProfileHelpRename = new("Profile.Help.Rename", "<color=#fd4>maprofile rename <name> <new name></color> - Rename a profile");
            public static readonly LangEntry0 ProfileHelpClear = new("Profile.Help.Clear2", "<color=#fd4>maprofile clear <name></color> - Clear a profile");
            public static readonly LangEntry0 ProfileHelpDelete = new("Profile.Help.Delete", "<color=#fd4>maprofile delete <name></color> - Delete a profile");
            public static readonly LangEntry0 ProfileHelpMoveTo = new("Profile.Help.MoveTo2", "<color=#fd4>maprofile moveto <name></color> - Move an entity to a profile");
            public static readonly LangEntry0 ProfileHelpInstall = new("Profile.Help.Install", "<color=#fd4>maprofile install <url></color> - Install a profile from a URL");

            public static readonly LangEntry0 WireToolInvisible = new("WireTool.Invisible", "Invisible");
            public static readonly LangEntry1 WireToolInvalidColor = new("WireTool.Error.InvalidColor", "Invalid wire color: <color=#fd4>{0}</color>.");
            public static readonly LangEntry0 WireToolNotEquipped = new("WireTool.Error.NotEquipped", "Error: No Wire Tool or Hose Tool equipped.");
            public static readonly LangEntry1 WireToolActivated = new("WireTool.Activated", "Monument Addons Wire Tool activated with color <color=#fd4>{0}</color>.");
            public static readonly LangEntry0 WireToolDeactivated = new("WireTool.Deactivated", "Monument Addons Wire Tool deactivated.");
            public static readonly LangEntry2 WireToolTypeMismatch = new("WireTool.TypeMismatch", "Error: You can only connect slots of the same type. Looking for <color=#fd4>{0}</color>, but found <color=#fd4>{1}</color>.");
            public static readonly LangEntry2 WireToolProfileMismatch = new("WireTool.ProfileMismatch", "Error: You can only connect entities in the same profile. Looking for <color=#fd4>{0}</color>, but found <color=#fd4>{1}</color>.");
            public static readonly LangEntry0 WireToolMonumentMismatch = new("WireTool.MonumentMismatch", "Error: You can only connect entities at the same monument.");

            public static readonly LangEntry0 HelpHeader = new("Help.Header", "<size=18>Monument Addons Help</size>");
            public static readonly LangEntry0 HelpSpawn = new("Help.Spawn", "<color=#fd4>maspawn <entity></color> - Spawn an entity");
            public static readonly LangEntry0 HelpPrefab = new("Help.Prefab", "<color=#fd4>maprefab <prefab></color> - Create a non-entity prefab instance");
            public static readonly LangEntry0 HelpKill = new("Help.Kill", "<color=#fd4>makill</color> - Delete an entity or other addon");
            public static readonly LangEntry0 HelpUndo = new("Help.Undo", "<color=#fd4>maundo</color> - Undo a recent <color=#fd4>makill</color> action");
            public static readonly LangEntry0 HelpSave = new("Help.Save", "<color=#fd4>masave</color> - Save an entity's updated position");
            public static readonly LangEntry0 HelpFlag = new("Help.Flag", "<color=#fd4>maflag <flag></color> - Toggle a flag of an entity");
            public static readonly LangEntry0 HelpSkin = new("Help.Skin", "<color=#fd4>maskin <skin id></color> - Change the skin of an entity");
            public static readonly LangEntry0 HelpSetId = new("Help.SetId", "<color=#fd4>masetid <id></color> - Set the id of a CCTV");
            public static readonly LangEntry0 HelpSetDir = new("Help.SetDir", "<color=#fd4>masetdir</color> - Set the direction of a CCTV");
            public static readonly LangEntry0 HelpSkull = new("Help.Skull", "<color=#fd4>maskull <name></color> - Set skull trophy display name");
            public static readonly LangEntry0 HelpTrophy = new("Help.Trophy", "<color=#fd4>matrophy <name></color> - Update a hunting trophy");
            public static readonly LangEntry0 HelpCardReaderLevel = new("Help.CardReaderLevel", "<color=#fd4>macardlevel <1-3></color> - Set a card reader's access level");
            public static readonly LangEntry0 HelpPuzzle = new("Help.Puzzle", "<color=#fd4>mapuzzle</color> - Print puzzle help");
            public static readonly LangEntry0 HelpSpawnGroup = new("Help.SpawnGroup", "<color=#fd4>maspawngroup</color> - Print spawn group help");
            public static readonly LangEntry0 HelpSpawnPoint = new("Help.SpawnPoint", "<color=#fd4>maspawnpoint</color> - Print spawn point help");
            public static readonly LangEntry0 HelpPaste = new("Help.Paste", "<color=#fd4>mapaste <file></color> - Paste a building");
            public static readonly LangEntry0 HelpEdit = new("Help.Edit", "<color=#fd4>maedit <addon-name> <arg1> <arg2> ...</color> - Edit a custom addon");
            public static readonly LangEntry0 HelpShow = new("Help.Show", "<color=#fd4>mashow</color> - Show nearby addons");
            public static readonly LangEntry0 HelpShowVanilla = new("Help.ShowVanilla", "<color=#fd4>mashowvanilla</color> - Show vanilla spawn points");
            public static readonly LangEntry0 HelpProfile = new("Help.Profile", "<color=#fd4>maprofile</color> - Print profile help");

            public string Name;
            public string English;

            protected LangEntry(string name, string english)
            {
                Name = name;
                English = english;

                AllLangEntries.Add(this);
            }
        }

        private struct TemplateProvider
        {
            private MonumentAddons _plugin;
            private string _playerId;

            public TemplateProvider(MonumentAddons plugin, string playerId)
            {
                _plugin = plugin;
                _playerId = playerId;
            }

            public string Get(string templateName)
            {
                return _plugin.lang.GetMessage(templateName, _plugin, _playerId);
            }
        }

        private interface IMessageFormatter
        {
            string Format(TemplateProvider templateProvider);
        }

        private class LangEntry0 : LangEntry, IMessageFormatter
        {
            public LangEntry0(string name, string english) : base(name, english) {}

            public string Format(TemplateProvider templateProvider)
            {
                return templateProvider.Get(Name);
            }
        }

        private class LangEntry1 : LangEntry
        {
            public struct Formatter : IMessageFormatter
            {
                private string _langKey;
                private Tuple1 _args;

                public Formatter(LangEntry1 langEntry, Tuple1 args)
                {
                    _langKey = langEntry.Name;
                    _args = args;
                }

                public string Format(TemplateProvider templateProvider)
                {
                    return string.Format(templateProvider.Get(_langKey), _args.Item1);
                }
            }

            public LangEntry1(string name, string english) : base(name, english) {}

            public Formatter Bind(Tuple1 args) => new(this, args);
            public Formatter Bind(object arg1) => Bind(new Tuple1(arg1));
        }

        private class LangEntry2 : LangEntry
        {
            public struct Formatter : IMessageFormatter
            {
                private string _langKey;
                private Tuple2 _args;

                public Formatter(LangEntry2 langEntry, Tuple2 args)
                {
                    _langKey = langEntry.Name;
                    _args = args;
                }

                public string Format(TemplateProvider templateProvider)
                {
                    return string.Format(templateProvider.Get(_langKey), _args.Item1, _args.Item2);
                }
            }

            public LangEntry2(string name, string english) : base(name, english) {}

            public Formatter Bind(Tuple2 args) => new(this, args);
            public Formatter Bind(object arg1, object arg2) => Bind(new Tuple2(arg1, arg2));
        }

        private class LangEntry3 : LangEntry
        {
            public struct Formatter : IMessageFormatter
            {
                private string _langKey;
                private Tuple3 _args;

                public Formatter(LangEntry3 langEntry, Tuple3 args)
                {
                    _langKey = langEntry.Name;
                    _args = args;
                }

                public string Format(TemplateProvider templateProvider)
                {
                    return string.Format(templateProvider.Get(_langKey), _args.Item1, _args.Item2, _args.Item3);
                }
            }

            public LangEntry3(string name, string english) : base(name, english) {}

            public Formatter Bind(Tuple3 args) => new(this, args);
            public Formatter Bind(object arg1, object arg2, object arg3) => Bind(new Tuple3(arg1, arg2, arg3));
        }

        private class LangEntry4 : LangEntry
        {
            public struct Formatter : IMessageFormatter
            {
                private string _langKey;
                private Tuple4 _args;

                public Formatter(LangEntry4 langEntry, Tuple4 args)
                {
                    _langKey = langEntry.Name;
                    _args = args;
                }

                public string Format(TemplateProvider templateProvider)
                {
                    return string.Format(templateProvider.Get(_langKey), _args.Item1, _args.Item2, _args.Item3, _args.Item4);
                }
            }

            public LangEntry4(string name, string english) : base(name, english) {}

            public Formatter Bind(Tuple4 args) => new(this, args);
            public Formatter Bind(object arg1, object arg2, object arg3, object arg4) => Bind(new Tuple4(arg1, arg2, arg3, arg4));
        }

        private string GetMessage<T>(string playerId, T formatter) where T : IMessageFormatter
        {
            return formatter.Format(new TemplateProvider(this, playerId));
        }

        private string GetMessage(string playerId, LangEntry1 langEntry, object arg1)
        {
            return GetMessage(playerId, langEntry.Bind(arg1));
        }

        private string GetMessage(string playerId, LangEntry2 langEntry, object arg1, object arg2)
        {
            return GetMessage(playerId, langEntry.Bind(arg1, arg2));
        }

        private string GetMessage(string playerId, LangEntry3 langEntry, object arg1, object arg2, object arg3)
        {
            return GetMessage(playerId, langEntry.Bind(arg1, arg2, arg3));
        }

        private string GetMessage(string playerId, LangEntry4 langEntry, object arg1, object arg2, object arg3, object arg4)
        {
            return GetMessage(playerId, langEntry.Bind(arg1, arg2, arg3, arg4));
        }


        private void ReplyToPlayer<T>(IPlayer player, T formatter) where T : IMessageFormatter
        {
            player.Reply(GetMessage(player.Id, formatter));
        }

        private void ReplyToPlayer(IPlayer player, LangEntry1 langEntry, object arg1)
        {
            ReplyToPlayer(player, langEntry.Bind(arg1));
        }

        private void ReplyToPlayer(IPlayer player, LangEntry2 langEntry, object arg1, object arg2)
        {
            ReplyToPlayer(player, langEntry.Bind(arg1, arg2));
        }

        private void ReplyToPlayer(IPlayer player, LangEntry3 langEntry, object arg1, object arg2, object arg3)
        {
            ReplyToPlayer(player, langEntry.Bind(arg1, arg2, arg3));
        }

        private void ReplyToPlayer(IPlayer player, LangEntry4 langEntry, object arg1, object arg2, object arg3, object arg4)
        {
            ReplyToPlayer(player, langEntry.Bind(arg1, arg2, arg3, arg4));
        }


        private void ChatMessage<T>(BasePlayer player, T formatter) where T : IMessageFormatter
        {
            player.ChatMessage(formatter.Format(new TemplateProvider(this, player.UserIDString)));
        }

        private void ChatMessage(BasePlayer player, LangEntry1 langEntry, object arg1)
        {
            ChatMessage(player, langEntry.Bind(arg1));
        }

        private void ChatMessage(BasePlayer player, LangEntry2 langEntry, object arg1, object arg2)
        {
            ChatMessage(player, langEntry.Bind(arg1, arg2));
        }

        private void ChatMessage(BasePlayer player, LangEntry3 langEntry, object arg1, object arg2, object arg3)
        {
            ChatMessage(player, langEntry.Bind(arg1, arg2, arg3));
        }

        private void ChatMessage(BasePlayer player, LangEntry4 langEntry, object arg1, object arg2, object arg3, object arg4)
        {
            ChatMessage(player, langEntry.Bind(arg1, arg2, arg3, arg4));
        }


        private string GetAuthorSuffix(IPlayer player, string author)
        {
            return !string.IsNullOrWhiteSpace(author)
                ? GetMessage(player.Id, LangEntry.ProfileByAuthor, author)
                : string.Empty;
        }

        private string GetAddonName(IPlayer player, BaseData data)
        {
            if (data is EntityData entityData)
                return _uniqueNameRegistry.GetUniqueShortName(entityData.PrefabName);

            if (data is PrefabData prefabData)
                return _uniqueNameRegistry.GetUniqueShortName(prefabData.PrefabName);

            if (data is SpawnPointData || data is SpawnGroupData)
                return GetMessage(player.Id, LangEntry.AddonTypeSpawnPoint);

            if (data is PasteData pasteData)
                return pasteData.Filename;

            return GetMessage(player.Id, LangEntry.AddonTypeUnknown);
        }

        protected override void LoadDefaultMessages()
        {
            var englishLangKeys = new Dictionary<string, string>();

            foreach (var langEntry in LangEntry.AllLangEntries)
            {
                englishLangKeys[langEntry.Name] = langEntry.English;
            }

            lang.RegisterMessages(englishLangKeys, this);
        }

        #endregion
    }
}

#region Extension Methods

namespace Oxide.Plugins.MonumentAddonsExtensions
{
    public static class DictionaryExtensions
    {
        public static TValue GetOrCreate<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key) where TValue : new()
        {
            var value = dict.GetValueOrDefault(key);
            if (value == null)
            {
                value = new TValue();
                dict[key] = value;
            }

            return value;
        }
    }
}

#endregion
