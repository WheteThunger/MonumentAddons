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
using System.Text;
using UnityEngine;
using VLB;
using static IOEntity;
using static WireTool;
using HumanNPCGlobal = global::HumanNPC;
using SkullTrophyGlobal = global::SkullTrophy;

using CustomSpawnCallback = System.Func<UnityEngine.Vector3, UnityEngine.Quaternion, Newtonsoft.Json.Linq.JObject, UnityEngine.Component>;
using CustomKillCallback = System.Action<UnityEngine.Component>;
using CustomUpdateCallback = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject>;
using CustomAddDisplayInfoCallback = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject, System.Text.StringBuilder>;
using CustomSetDataCallback = System.Action<UnityEngine.Component, object>;
using System.Text.RegularExpressions;
using Facepunch;

namespace Oxide.Plugins
{
    [Info("Monument Addons", "WhiteThunder", "0.16.2")]
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

        private const string CargoShipShortName = "cargoshiptest";
        private const string DefaultProfileName = "Default";
        private const string DefaultUrlPattern = "https://github.com/WheteThunger/MonumentAddons/blob/master/Profiles/{0}.json?raw=true";

        private static readonly int HitLayers = Rust.Layers.Solid
            | Rust.Layers.Mask.Water;

        private static readonly Dictionary<string, string> JsonRequestHeaders = new Dictionary<string, string>
        {
            { "Content-Type", "application/json" }
        };

        private readonly ProfileStore _profileStore = new ProfileStore();
        private readonly OriginalProfileStore _originalProfileStore = new OriginalProfileStore();
        private readonly ProfileManager _profileManager;
        private readonly CoroutineManager _coroutineManager = new CoroutineManager();
        private readonly MonumentEntityTracker _entityTracker = new MonumentEntityTracker();
        private readonly AdapterListenerManager _adapterListenerManager;
        private readonly ControllerFactory _controllerFactory;
        private readonly CustomAddonManager _customAddonManager;
        private readonly UniqueNameRegistry _uniqueNameRegistry = new UniqueNameRegistry();
        private readonly AdapterDisplayManager _adapterDisplayManager;
        private readonly MonumentHelper _monumentHelper;
        private readonly WireToolManager _wireToolManager;
        private readonly IOManager _ioManager = new IOManager();
        private readonly UndoManager _undoManager = new UndoManager();

        private readonly Color[] _distinctColors =
        {
            Color.HSVToRGB(0, 1, 1),
            Color.HSVToRGB(0.1f, 1, 1),
            Color.HSVToRGB(0.2f, 1, 1),
            Color.HSVToRGB(0.35f, 1, 1),
            Color.HSVToRGB(0.55f, 1, 1),
            Color.HSVToRGB(0.8f, 1, 1),
            new Color(1, 1, 1),
        };

        private readonly object False = false;

        private ItemDefinition _waterDefinition;
        private ProtectionProperties _immortalProtection;
        private ActionDebounced _saveProfileStateDebounced;

        private Coroutine _startupCoroutine;
        private bool _serverInitialized;
        private bool _isLoaded = true;

        public MonumentAddons()
        {
            _profileManager = new ProfileManager(this, _originalProfileStore, _profileStore);
            _adapterDisplayManager = new AdapterDisplayManager(this, _uniqueNameRegistry);
            _adapterListenerManager = new AdapterListenerManager(this);
            _customAddonManager = new CustomAddonManager(this);
            _controllerFactory = new ControllerFactory(this);
            _monumentHelper = new MonumentHelper(this);
            _wireToolManager = new WireToolManager(this, _profileStore, _entityTracker);

            _saveProfileStateDebounced = new ActionDebounced(timer, 1, () =>
            {
                if (!_isLoaded)
                    return;

                _profileStateData.Save();
            });
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _plugin = this;
            _data = StoredData.Load(_profileStore);
            _profileStateData = ProfileStateData.Load(_data);

            _config.Init();

            // Ensure the profile folder is created to avoid errors.
            _profileStore.EnsureDefaultProfile();

            permission.RegisterPermission(PermissionAdmin, this);

            Unsubscribe(nameof(OnEntitySpawned));

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
        }

        private void OnEntitySpawned(CargoShip cargoShip)
        {
            var cargoShipMonument = new DynamicMonument(cargoShip, isMobile: true);
            _coroutineManager.StartCoroutine(_profileManager.PartialLoadForLateMonumentRoutine(cargoShipMonument));
        }

        // This hook is exposed by plugin: Remover Tool (RemoverTool).
        private object canRemove(BasePlayer player, BaseEntity entity)
        {
            if (_entityTracker.IsMonumentEntity(entity))
                return False;

            return null;
        }

        private object CanChangeGrade(BasePlayer player, BuildingBlock block, BuildingGrade.Enum grade)
        {
            if (_entityTracker.IsMonumentEntity(block) && !HasAdminPermission(player))
                return False;

            return null;
        }

        private object CanUpdateSign(BasePlayer player, ISignage signage)
        {
            if (_entityTracker.IsMonumentEntity(signage as BaseEntity) && !HasAdminPermission(player))
            {
                ChatMessage(player, LangEntry.ErrorNoPermission);
                return False;
            }

            return null;
        }

        private void OnSignUpdated(ISignage signage, BasePlayer player)
        {
            if (!_entityTracker.IsMonumentEntity(signage as BaseEntity))
                return;

            var component = MonumentEntityComponent.GetForEntity(signage.NetworkID);
            if (component == null)
                return;

            if (component.Adapter is not SignAdapter { Controller: SignController controller })
                return;

            controller.UpdateSign(signage.GetTextureCRCs());
        }

        // This hook is exposed by plugin: Sign Arist (SignArtist).
        private void OnImagePost(BasePlayer player, string url, bool raw, ISignage signage, uint textureIndex = 0)
        {
            if (!_entityTracker.IsMonumentEntity(signage as BaseEntity, out SignController controller))
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
            if (_entityTracker.IsMonumentEntity(spray))
                return False;

            return null;
        }

        private void OnEntityScaled(BaseEntity entity, float scale)
        {
            if (!_entityTracker.IsMonumentEntity(entity, out EntityController controller)
                || controller.EntityData.Scale == scale)
                return;

            controller.EntityData.Scale = scale;
            controller.StartHandleChangesRoutine();
            _profileStore.Save(controller.Profile);
        }

        // This hook is exposed by plugin: Telekinesis.
        private BaseEntity OnTelekinesisFindFailed(BasePlayer player)
        {
            if (!HasAdminPermission(player))
                return null;

            return FindAdapter<EntityAdapter>(player).Adapter?.Entity;
        }

        // This hook is exposed by plugin: Telekinesis.
        private object CanStartTelekinesis(BasePlayer player, BaseEntity moveEntity)
        {
            if (_entityTracker.IsMonumentEntity(moveEntity) && !HasAdminPermission(player))
                return False;

            return null;
        }

        // This hook is exposed by plugin: Telekinesis.
        private void OnTelekinesisStarted(BasePlayer player, BaseEntity moveEntity, BaseEntity rotateEntity)
        {
            if (_entityTracker.IsMonumentEntity(moveEntity))
            {
                _adapterDisplayManager.ShowAllRepeatedly(player);
            }

            if (GetSpawnPointAdapter(moveEntity) != null)
            {
                _adapterDisplayManager.ShowAllRepeatedly(player);

                var spawnedVehicleComponent = moveEntity.GetComponent<SpawnedVehicleComponent>();
                if (spawnedVehicleComponent != null)
                {
                    spawnedVehicleComponent.CancelInvoke(spawnedVehicleComponent.CheckPositionTracked);
                }
            }
        }

        // This hook is exposed by plugin: Telekinesis.
        private void OnTelekinesisStopped(BasePlayer player, BaseEntity moveEntity, BaseEntity rotateEntity)
        {
            int adapterCount;
            string profileName;

            var spawnPointAdapter = GetSpawnPointAdapter(moveEntity);

            if (spawnPointAdapter != null)
            {
                var moveEntityTransform = moveEntity.transform;
                var moveEntityPosition = moveEntityTransform.position;
                if (!spawnPointAdapter.Monument.IsInBounds(moveEntityPosition))
                    return;

                var localPosition = spawnPointAdapter.Monument.InverseTransformPoint(moveEntityPosition);

                spawnPointAdapter.SpawnPointData.Position = localPosition;
                spawnPointAdapter.SpawnPointData.RotationAngles = (Quaternion.Inverse(spawnPointAdapter.Monument.Rotation) * moveEntityTransform.rotation).eulerAngles;
                spawnPointAdapter.SpawnPointData.SnapToTerrain = IsOnTerrain(moveEntityPosition);
                _profileStore.Save(spawnPointAdapter.Profile);

                var spawnGroupController = spawnPointAdapter.Controller as SpawnGroupController;
                spawnGroupController.UpdateSpawnGroups();

                adapterCount = spawnGroupController.Adapters.Count;
                profileName = spawnPointAdapter.Profile.Name;
            }
            else if (_entityTracker.IsMonumentEntity(moveEntity, out EntityAdapter adapter, out EntityController controller))
            {
                if (!adapter.TrySaveAndApplyChanges())
                    return;

                adapterCount = controller.Adapters.Count;
                profileName = controller.Profile.Name;
            }
            else
            {
                return;
            }

            if (player != null)
            {
                _adapterDisplayManager.ShowAllRepeatedly(player);
                ChatMessage(player, LangEntry.SaveSuccess, adapterCount, profileName);
            }
        }

        // This hook is exposed by plugin: Custom Vending Setup (CustomVendingSetup).
        private Dictionary<string, object> OnCustomVendingSetupDataProvider(NPCVendingMachine vendingMachine)
        {
            if (!_entityTracker.IsMonumentEntity(vendingMachine, out EntityController controller))
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
            var result = EntityScaleManager?.Call("API_ScaleEntity", entity, scale);
            return result is bool && (bool)result;
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

                var result = copyPaste.Call("TryPasteFromVector3Cancellable", position, yRotation, pasteData.Filename, CopyPasteArgs, onPasteCompleted, onEntityPasted);
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
            return ObjectCache.Get(_entityTracker.IsMonumentEntity(entity));
        }

        [HookMethod(nameof(API_GetMonumentEntityGuid))]
        public object API_GetMonumentEntityGuid(BaseEntity entity)
        {
            if (!_entityTracker.IsMonumentEntity(entity))
                return null;

            if (MonumentEntityComponent.GetForEntity(entity).Adapter is not BaseAdapter adapter)
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

            public static void OnMonumentEntitySpawned(BaseEntity entity, MonoBehaviour monument, Guid guid)
            {
                Interface.CallHook("OnMonumentEntitySpawned", entity, monument, ObjectCache.Get(guid));
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
                || !VerifyHitPosition(player, out var position)
                || !VerifyAtMonument(player, position, out var monument))
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
                    entityData.Puzzle = new PuzzleData
                    {
                        PlayersBlockReset = true,
                        PlayerDetectionRadius = 30,
                        SecondsBetweenResets = 1800,
                    };
                }

                addonData = entityData;
            }
            else
            {
                // Found a custom addon definition.
                addonData = new CustomAddonData
                {
                    Id = Guid.NewGuid(),
                    AddonName = addonDefinition.AddonName,
                    Position = localPosition,
                    RotationAngles = localRotationAngles,
                    SnapToTerrain = isOnTerrain,
                };
            }

            var matchingMonuments = GetMonumentsByAliasOrShortName(monument.AliasOrShortName);

            profileController.Profile.AddData(monument.AliasOrShortName, addonData);
            _profileStore.Save(profileController.Profile);
            profileController.SpawnNewData(addonData, matchingMonuments);

            ReplyToPlayer(player, LangEntry.SpawnSuccess, matchingMonuments.Count, profileController.Profile.Name, monument.AliasOrShortName);
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
                || !VerifyHitPosition(player, out var position)
                || !VerifyAtMonument(player, position, out var monument))
                return;

            if (FindBaseEntityForPrefab(prefabName) != null)
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

            var matchingMonuments = GetMonumentsByAliasOrShortName(monument.AliasOrShortName);

            profileController.Profile.AddData(monument.AliasOrShortName, prefabData);
            _profileStore.Save(profileController.Profile);
            profileController.SpawnNewData(prefabData, matchingMonuments);

            ReplyToPlayer(player, LangEntry.PrefabSuccess, matchingMonuments.Count, profileController.Profile.Name, monument.AliasOrShortName);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
        }

        [Command("masave")]
        private void CommandSave(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyHasPermission(player)
                || !VerifyLookingAtAdapter(player, out EntityAdapter adapter, out EntityController controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            if (!adapter.TrySaveAndApplyChanges())
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

            controller.Profile.RemoveData(adapter.Data, out var monumentAliasOrShortName);
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
                _undoManager.AddUndo(basePlayer, new UndoKillSpawnPoint(this, profileController, monumentAliasOrShortName, spawnGroupData, spawnPointData));
            }
            else
            {
                _undoManager.AddUndo(basePlayer, new UndoKill(this, profileController, monumentAliasOrShortName, controller.Data));
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
            controller.StartHandleChangesRoutine();

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
            controller.StartHandleChangesRoutine();

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
            controller.StartHandleChangesRoutine();

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
            controller.StartHandleChangesRoutine();

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
            controller.StartHandleChangesRoutine();

            ReplyToPlayer(player, LangEntry.SkinSetSuccess, skinId, controller.Adapters.Count, controller.Profile.Name);
            _adapterDisplayManager.ShowAllRepeatedly(basePlayer, immediate: !updatedExistingSkin);
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
                controller.StartHandleChangesRoutine();
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

                    controller.StartHandleChangesRoutine();
                    _profileStore.Save(controller.Profile);

                    ReplyToPlayer(player, isAdd ? LangEntry.PuzzleAddSpawnGroupSuccess : LangEntry.PuzzleRemoveSpawnGroupSuccess, spawnGroupData.Name);

                    _adapterDisplayManager.ShowAllRepeatedly(basePlayer, immediate: false);
                    break;
                }

                case "set":
                {
                    if (args.Length < 3)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine(GetMessage(player.Id, LangEntry.ErrorSetSyntaxGeneric, cmd));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleSetHelpMaxPlayersBlockReset));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleSetHelpPlayerDetectionRadius));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleSetHelpSecondsBetweenResets));
                        player.Reply(sb.ToString());
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
                            if (!VerifyValidBool(player, args[2], out var playerBlockReset, LangEntry.ErrorSetSyntax, cmd, PuzzleOption.PlayersBlockReset))
                                return;

                            puzzleData.PlayersBlockReset = playerBlockReset;
                            setValue = playerBlockReset;
                            showImmediate = false;
                            break;
                        }

                        case PuzzleOption.PlayerDetectionRadius:
                        {
                            if (!VerifyValidFloat(player, args[2], out var playerDetectionRadius, LangEntry.ErrorSetSyntax, cmd, PuzzleOption.PlayerDetectionRadius))
                                return;

                            puzzleData.PlayersBlockReset = true;
                            puzzleData.PlayerDetectionRadius = playerDetectionRadius;
                            setValue = playerDetectionRadius;
                            break;
                        }

                        case PuzzleOption.SecondsBetweenResets:
                        {
                            if (!VerifyValidFloat(player, args[2], out var secondsBetweenResets, LangEntry.ErrorSetSyntax, cmd, PuzzleOption.SecondsBetweenResets))
                                return;

                            puzzleData.SecondsBetweenResets = secondsBetweenResets;
                            setValue = secondsBetweenResets;
                            break;
                        }
                    }

                    controller.StartHandleChangesRoutine();
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
            var sb = new StringBuilder();
            sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleHelpHeader, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleHelpReset, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleHelpSet, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleHelpAdd, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.PuzzleHelpRemove, cmd));
            player.Reply(sb.ToString());
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

                    var sb = new StringBuilder();
                    sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileListHeader));
                    foreach (var profile in profileList)
                    {
                        var messageName = profile.Enabled && profile.Name == playerProfileName
                            ? LangEntry.ProfileListItemSelected
                            : profile.Enabled
                            ? LangEntry.ProfileListItemEnabled
                            : LangEntry.ProfileListItemDisabled;

                        sb.AppendLine(GetMessage(player.Id, messageName, profile.Name, GetAuthorSuffix(player, profile.Profile?.Author)));
                    }
                    player.Reply(sb.ToString());
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

                    var sb = new StringBuilder();
                    sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileDescribeHeader, controller.Profile.Name));
                    AddProfileDescription(sb, player, controller);

                    player.Reply(sb.ToString());

                    if (!player.IsServer)
                    {
                        _adapterDisplayManager.SetPlayerProfile(basePlayer, controller);
                        _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                    }

                    break;
                }

                case "select":
                {
                    if (player.IsServer)
                        return;

                    if (args.Length <= 1)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileSelectSyntax);
                        return;
                    }

                    if (!VerifyProfile(player, args, out var controller, LangEntry.ProfileSelectSyntax))
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

                    if (!oldProfile.RemoveData(data, out var monumentAliasOrShortName))
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

                    newProfile.AddData(monumentAliasOrShortName, data);
                    _profileStore.Save(newProfile);
                    newProfileController.SpawnNewData(data, GetMonumentsByAliasOrShortName(monumentAliasOrShortName));

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
            var sb = new StringBuilder();
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpHeader));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpList));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpDescribe));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpEnable));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpDisable));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpReload));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpSelect));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpCreate));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpRename));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpClear));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpDelete));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpMoveTo));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpInstall));
            player.Reply(sb.ToString());
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

                    var sb = new StringBuilder();
                    sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileInstallSuccess, profile.Name, GetAuthorSuffix(player, profile.Author)));
                    AddProfileDescription(sb, player, profileController);
                    player.Reply(sb.ToString());

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
                        || !VerifyHitPosition(player, out var position)
                        || !VerifyAtMonument(player, position, out var monument)
                        || !VerifySpawnGroupNameAvailable(player, profileController.Profile, monument, spawnGroupName))
                        return;

                    DetermineLocalTransformData(position, basePlayer, monument, out var localPosition, out var localRotationAngles, out var isOnTerrain);

                    var spawnGroupData = new SpawnGroupData
                    {
                        Id = Guid.NewGuid(),
                        Name = spawnGroupName,
                        SpawnPoints = new List<SpawnPointData>
                        {
                            new SpawnPointData
                            {
                                Id = Guid.NewGuid(),
                                Position = localPosition,
                                RotationAngles = localRotationAngles,
                                SnapToTerrain = isOnTerrain,
                                Exclusive = true,
                                SnapToGround = true,
                            },
                        },
                    };

                    var matchingMonuments = GetMonumentsByAliasOrShortName(monument.AliasOrShortName);

                    profileController.Profile.AddData(monument.AliasOrShortName, spawnGroupData);
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
                        var sb = new StringBuilder();
                        sb.AppendLine(GetMessage(player.Id, LangEntry.ErrorSetSyntaxGeneric, cmd));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpName));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpMaxPopulation));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpRespawnDelayMin));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpRespawnDelayMax));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpSpawnPerTickMin));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpSpawnPerTickMax));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpInitialSpawn));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpPreventDuplicates));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpPauseScheduleWhileFull));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupSetHelpRespawnWhenNearestPuzzleResets));
                        player.Reply(sb.ToString());
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
                            if (!VerifyValidInt(player, args[2], out var maxPopulation, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.MaxPopulation))
                                return;

                            spawnGroupData.MaxPopulation = maxPopulation;
                            break;
                        }

                        case SpawnGroupOption.RespawnDelayMin:
                        {
                            if (!VerifyValidFloat(player, args[2], out var respawnDelayMin, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.RespawnDelayMin))
                                return;

                            showImmediate = respawnDelayMin == 0 || spawnGroupData.RespawnDelayMax != 0;
                            spawnGroupData.RespawnDelayMin = respawnDelayMin;
                            spawnGroupData.RespawnDelayMax = Math.Max(respawnDelayMin, spawnGroupData.RespawnDelayMax);
                            setValue = respawnDelayMin;
                            break;
                        }

                        case SpawnGroupOption.RespawnDelayMax:
                        {
                            if (!VerifyValidFloat(player, args[2], out var respawnDelayMax, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.RespawnDelayMax))
                                return;

                            showImmediate = (respawnDelayMax == 0) == (spawnGroupData.RespawnDelayMax == 0);
                            spawnGroupData.RespawnDelayMax = respawnDelayMax;
                            spawnGroupData.RespawnDelayMin = Math.Min(spawnGroupData.RespawnDelayMin, respawnDelayMax);
                            setValue = respawnDelayMax;
                            break;
                        }

                        case SpawnGroupOption.SpawnPerTickMin:
                        {
                            if (!VerifyValidInt(player, args[2], out var spawnPerTickMin, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.SpawnPerTickMin))
                                return;

                            spawnGroupData.SpawnPerTickMin = spawnPerTickMin;
                            spawnGroupData.SpawnPerTickMax = Math.Max(spawnPerTickMin, spawnGroupData.SpawnPerTickMax);
                            setValue = spawnPerTickMin;
                            break;
                        }

                        case SpawnGroupOption.SpawnPerTickMax:
                        {
                            if (!VerifyValidInt(player, args[2], out var spawnPerTickMax, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.SpawnPerTickMax))
                                return;

                            spawnGroupData.SpawnPerTickMax = spawnPerTickMax;
                            spawnGroupData.SpawnPerTickMin = Math.Min(spawnGroupData.SpawnPerTickMin, spawnPerTickMax);
                            setValue = spawnPerTickMax;
                            break;
                        }

                        case SpawnGroupOption.InitialSpawn:
                        {
                            if (!VerifyValidBool(player, args[2], out var initialSpawn, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.PreventDuplicates))
                                return;

                            spawnGroupData.InitialSpawn = initialSpawn;
                            setValue = initialSpawn;
                            showImmediate = false;
                            break;
                        }

                        case SpawnGroupOption.PreventDuplicates:
                        {
                            if (!VerifyValidBool(player, args[2], out var preventDuplicates, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.PreventDuplicates))
                                return;

                            spawnGroupData.PreventDuplicates = preventDuplicates;
                            setValue = preventDuplicates;
                            showImmediate = false;
                            break;
                        }

                        case SpawnGroupOption.PauseScheduleWhileFull:
                        {
                            if (!VerifyValidBool(player, args[2], out var pauseScheduleWhileFull, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.PauseScheduleWhileFull))
                                return;

                            spawnGroupData.PauseScheduleWhileFull = pauseScheduleWhileFull;
                            setValue = pauseScheduleWhileFull;
                            showImmediate = false;
                            break;
                        }

                        case SpawnGroupOption.RespawnWhenNearestPuzzleResets:
                        {
                            if (!VerifyValidBool(player, args[2], out var respawnWhenNearestPuzzleResets, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.RespawnWhenNearestPuzzleResets))
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

                    if (!VerifyValidEntityPrefab(player, args[1], out var prefabPath))
                        return;

                    if (!VerifyLookingAtAdapter(player, out SpawnGroupController spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
                        return;

                    var updatedExistingEntry = false;

                    var spawnGroupData = spawnGroupController.SpawnGroupData;
                    var prefabData = spawnGroupData.Prefabs.FirstOrDefault(entry => entry.PrefabName == prefabPath);
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
                            Weight = weight,
                        };
                        spawnGroupData.Prefabs.Add(prefabData);
                    }

                    spawnGroupController.UpdateSpawnGroups();
                    _profileStore.Save(spawnGroupController.Profile);

                    ReplyToPlayer(player, LangEntry.SpawnGroupAddSuccess, _uniqueNameRegistry.GetUniqueShortName(prefabData.PrefabName), weight, spawnGroupData.Name);

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

                    var matchingPrefabs = spawnGroupData.FindPrefabMatches(desiredPrefab, _uniqueNameRegistry);
                    if (matchingPrefabs.Count == 0)
                    {
                        ReplyToPlayer(player, LangEntry.SpawnGroupRemoveNoMatch, spawnGroupData.Name, desiredPrefab);
                        _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                        return;
                    }

                    if (matchingPrefabs.Count > 1)
                    {
                        ReplyToPlayer(player, LangEntry.SpawnGroupRemoveMultipleMatches, spawnGroupData.Name, desiredPrefab);
                        _adapterDisplayManager.ShowAllRepeatedly(basePlayer);
                        return;
                    }

                    var prefabMatch = matchingPrefabs[0];

                    spawnGroupData.Prefabs.Remove(prefabMatch);
                    spawnGroupController.StartKillSpawnedInstancesRoutine(prefabMatch.PrefabName);
                    spawnGroupController.UpdateSpawnGroups();
                    _profileStore.Save(spawnGroupController.Profile);

                    ReplyToPlayer(player, LangEntry.SpawnGroupRemoveSuccess, _uniqueNameRegistry.GetUniqueShortName(prefabMatch.PrefabName), spawnGroupData.Name);

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
            var sb = new StringBuilder();
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpHeader, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpCreate, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpSet, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpAdd, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpRemove, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpSpawn, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpRespawn, cmd));
            player.Reply(sb.ToString());
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
                        || !VerifyHitPosition(player, out var position)
                        || !VerifyAtMonument(player, position, out var monument))
                        return;

                    if (!VerifySpawnGroupFound(player, args[1], monument, out var spawnGroupController))
                        return;

                    DetermineLocalTransformData(position, basePlayer, monument, out var localPosition, out var localRotationAngles, out var isOnTerrain);

                    var spawnPointData = new SpawnPointData
                    {
                        Id = Guid.NewGuid(),
                        Position = localPosition,
                        RotationAngles = localRotationAngles,
                        SnapToTerrain = isOnTerrain,
                        Exclusive = true,
                        SnapToGround = true,
                    };

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
                        var sb = new StringBuilder();
                        sb.AppendLine(GetMessage(player.Id, LangEntry.ErrorSetSyntaxGeneric, cmd));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpExclusive));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpSnapToGround));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpCheckSpace));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpRandomRotation));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpRandomRadius));
                        sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointSetHelpPlayerDetectionRadius));
                        player.Reply(sb.ToString());
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
                            if (!VerifyValidBool(player, args[2], out var exclusive, LangEntry.SpawnGroupSetSuccess, LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.Exclusive))
                                return;

                            spawnPointData.Exclusive = exclusive;
                            setValue = exclusive;
                            break;
                        }

                        case SpawnPointOption.SnapToGround:
                        {
                            if (!VerifyValidBool(player, args[2], out var snapToGround, LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.SnapToGround))
                                return;

                            spawnPointData.SnapToGround = snapToGround;
                            setValue = snapToGround;
                            break;
                        }

                        case SpawnPointOption.CheckSpace:
                        {
                            if (!VerifyValidBool(player, args[2], out var checkSpace, LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.CheckSpace))
                                return;

                            spawnPointData.CheckSpace = checkSpace;
                            setValue = checkSpace;
                            break;
                        }

                        case SpawnPointOption.RandomRotation:
                        {
                            if (!VerifyValidBool(player, args[2], out var randomRotation, LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.RandomRotation))
                                return;

                            spawnPointData.RandomRotation = randomRotation;
                            setValue = randomRotation;
                            break;
                        }

                        case SpawnPointOption.RandomRadius:
                        {
                            if (!VerifyValidFloat(player, args[2], out var radius, LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.RandomRadius))
                                return;

                            spawnPointData.RandomRadius = radius;
                            setValue = radius;
                            break;
                        }

                        case SpawnPointOption.PlayerDetectionRadius:
                        {
                            if (!VerifyValidFloat(player, args[2], out var radius, LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.PlayerDetectionRadius))
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
            var sb = new StringBuilder();
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointHelpHeader, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointHelpCreate, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnPointHelpSet, cmd));
            player.Reply(sb.ToString());
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
                || !VerifyHitPosition(player, out var position)
                || !VerifyAtMonument(player, position, out var monument))
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
            };

            var matchingMonuments = GetMonumentsByAliasOrShortName(monument.AliasOrShortName);

            profileController.Profile.AddData(monument.AliasOrShortName, pasteData);
            _profileStore.Save(profileController.Profile);
            profileController.SpawnNewData(pasteData, matchingMonuments);

            ReplyToPlayer(player, LangEntry.PasteSuccess, pasteName, monument.AliasOrShortName, matchingMonuments.Count, profileController.Profile.Name);

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

                sb.AppendLine(_plugin.GetMessage(player.Id, LangEntry.ShowLabelEntities));
                foreach (var prefabEntry in spawnGroup.prefabs)
                {
                    var relativeChance = (float)prefabEntry.weight / totalWeight;
                    sb.AppendLine(_plugin.GetMessage(player.Id, LangEntry.ShowLabelEntityDetail, _uniqueNameRegistry.GetUniqueShortName(prefabEntry.prefab.resourcePath), prefabEntry.weight, relativeChance));
                }
            }
            else
            {
                sb.AppendLine(_plugin.GetMessage(player.Id, LangEntry.ShowLabelNoEntities));
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

            var _selectedColorIndex = 0;
            var sb = new StringBuilder();

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

                    var color = _distinctColors[_selectedColorIndex++];
                    if (_selectedColorIndex >= _distinctColors.Length)
                    {
                        _selectedColorIndex = 0;
                    }

                    var tierMask = (int)spawnGroup.Tier;

                    if (spawnPointList.Length == 0)
                    {
                        AddSpawnGroupInfo(player, sb, spawnGroup, spawnPointList.Length);
                        var spawnGroupPosition = spawnGroup.transform.position;

                        Ddraw.Sphere(basePlayer, spawnGroupPosition, 0.5f, color, ShowVanillaDuration);
                        Ddraw.Text(basePlayer, spawnGroupPosition + new Vector3(0, tierMask > 0 ? Mathf.Log(tierMask, 2) : 0, 0), sb.ToString(), color, ShowVanillaDuration);
                        sb.Clear();
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
                        sb.AppendLine($"<size={AdapterDisplayManager.HeaderSize}>{GetMessage(player.Id, LangEntry.ShowHeaderVanillaSpawnPoint, spawnGroup.name)}</size>");

                        var booleanProperties = new List<string>();

                        var genericSpawnPoint = spawnPoint as GenericSpawnPoint;
                        if (genericSpawnPoint != null)
                        {
                            booleanProperties.Add(_plugin.GetMessage(player.Id, LangEntry.ShowLabelSpawnPointExclusive));

                            if (genericSpawnPoint.randomRot)
                            {
                                booleanProperties.Add(_plugin.GetMessage(player.Id, LangEntry.ShowLabelSpawnPointRandomRotation));
                            }

                            if (genericSpawnPoint.dropToGround)
                            {
                                booleanProperties.Add(_plugin.GetMessage(player.Id, LangEntry.ShowLabelSpawnPointSnapToGround));
                            }
                        }

                        var spaceCheckingSpawnPoint = spawnPoint as SpaceCheckingSpawnPoint;
                        if (spaceCheckingSpawnPoint != null)
                        {
                            booleanProperties.Add(_plugin.GetMessage(player.Id, LangEntry.ShowLabelSpawnPointCheckSpace));
                        }

                        if (booleanProperties.Count > 0)
                        {
                            sb.AppendLine(_plugin.GetMessage(player.Id, LangEntry.ShowLabelFlags, string.Join(" | ", booleanProperties)));
                        }

                        var radialSpawnPoint = spawnPoint as RadialSpawnPoint;
                        if (radialSpawnPoint != null)
                        {
                            sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelSpawnPointRandomRadius, radialSpawnPoint.radius));
                        }

                        if (spawnPoint == closestSpawnPoint)
                        {
                            sb.AppendLine(AdapterDisplayManager.Divider);
                            AddSpawnGroupInfo(player, sb, spawnGroup, spawnPointList.Length);
                        }

                        var spawnPointTransform = spawnPoint.transform;
                        var spawnPointPosition = spawnPointTransform.position;
                        Ddraw.ArrowThrough(basePlayer, spawnPointPosition + AdapterDisplayManager.ArrowVerticalOffeset, spawnPointTransform.rotation, 1, 0.15f, color, ShowVanillaDuration);
                        Ddraw.Sphere(basePlayer, spawnPointPosition, 0.5f, color, ShowVanillaDuration);
                        Ddraw.Text(basePlayer, spawnPointPosition + new Vector3(0, tierMask > 0 ? Mathf.Log(tierMask, 2) : 0, 0), sb.ToString(), color, ShowVanillaDuration);

                        if (spawnPoint != closestSpawnPoint)
                        {
                            Ddraw.Arrow(basePlayer, closestSpawnPointPosition + AdapterDisplayManager.ArrowVerticalOffeset, spawnPointPosition + AdapterDisplayManager.ArrowVerticalOffeset, 0.25f, color, ShowVanillaDuration);
                        }

                        sb.Clear();
                    }

                    continue;
                }

                var individualSpawner = spawner as IndividualSpawner;
                if (individualSpawner != null)
                {
                    var color = _distinctColors[_selectedColorIndex++];
                    if (_selectedColorIndex >= _distinctColors.Length)
                    {
                        _selectedColorIndex = 0;
                    }

                    sb.AppendLine($"<size={AdapterDisplayManager.HeaderSize}>{GetMessage(player.Id, LangEntry.ShowHeaderVanillaIndividualSpawnPoint, individualSpawner.name)}</size>");
                    sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelFlags, $"{GetMessage(player.Id, LangEntry.ShowLabelSpawnPointExclusive)} | {GetMessage(player.Id, LangEntry.ShowLabelSpawnPointCheckSpace)}"));

                    if (individualSpawner.oneTimeSpawner)
                    {
                        sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelSpawnOnMapWipe));
                    }
                    else
                    {
                        if (!float.IsPositiveInfinity(individualSpawner.respawnDelayMin))
                        {
                            sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelRespawnDelay, FormatTime(individualSpawner.respawnDelayMin), FormatTime(individualSpawner.respawnDelayMax)));
                        }

                        var nextSpawnTime = GetTimeToNextSpawn(individualSpawner);
                        if (!individualSpawner.IsSpawned && !float.IsPositiveInfinity(nextSpawnTime))
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

                    sb.AppendLine(GetMessage(player.Id, LangEntry.ShowHeaderEntity, _uniqueNameRegistry.GetUniqueShortName(individualSpawner.entityPrefab.resourcePath)));

                    var spawnerTransform = individualSpawner.transform;
                    var spawnPointPosition = spawnerTransform.position;
                    Ddraw.ArrowThrough(basePlayer, spawnPointPosition + AdapterDisplayManager.ArrowVerticalOffeset, spawnerTransform.rotation, 1f, 0.15f, color, ShowVanillaDuration);
                    Ddraw.Sphere(basePlayer, spawnPointPosition, 0.5f, color, ShowVanillaDuration);
                    Ddraw.Text(basePlayer, spawnPointPosition, sb.ToString(), color, ShowVanillaDuration);

                    sb.Clear();
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

            var profileName = $"{monument.AliasOrShortName}{tierSuffix}_vanilla_generated";
            var profile = _profileStore.Create(profileName, basePlayer.displayName);

            foreach (var data in spawnGroupDataList)
            {
                profile.AddData(monument.AliasOrShortName, data);
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
                wireColor = WireColour.Default;
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

            var sb = new StringBuilder();
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpHeader));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSpawn));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpPrefab));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpKill));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpUndo));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSave));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSkin));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSetId));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSetDir));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSkull));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpTrophy));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpCardReaderLevel));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpPuzzle));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSpawnGroup));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpSpawnPoint));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpPaste));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpShow));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpShowVanilla));
            sb.AppendLine(GetMessage(player.Id, LangEntry.HelpProfile));
            player.Reply(sb.ToString());
        }

        #endregion

        #region API

        private Dictionary<string, object> API_RegisterCustomAddon(Plugin plugin, string addonName, Dictionary<string, object> addonSpec)
        {
            LogWarning($"API_RegisterCustomAddon is experimental and may be changed or removed in future updates.");

            var addonDefinition = CustomAddonDefinition.FromDictionary(addonName, plugin, addonSpec);

            if (addonDefinition.Spawn == null)
            {
                LogError($"Unable to register custom addon \"{addonName}\" for plugin {plugin.Name} due to missing Spawn method.");
                return null;
            }

            if (addonDefinition.Kill == null)
            {
                LogError($"Unable to register custom addon \"{addonName}\" for plugin {plugin.Name} due to missing Kill method.");
                return null;
            }

            if (_customAddonManager.IsRegistered(addonName, out var otherPlugin))
            {
                LogError($"Unable to register custom addon \"{addonName}\" for plugin {plugin.Name} because it's already been registered by plugin {otherPlugin.Name}.");
                return null;
            }

            _customAddonManager.RegisterAddon(addonDefinition);

            return addonDefinition.ToApiResult(_profileStore);
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

        private bool VerifyValidInt(IPlayer player, string arg, out int value, LangEntry errorLangEntry, params object[] args)
        {
            if (int.TryParse(arg, out value))
                return true;

            ReplyToPlayer(player, errorLangEntry, args);
            return false;
        }

        private bool VerifyValidFloat(IPlayer player, string arg, out float value, LangEntry errorLangEntry, params object[] args)
        {
            if (float.TryParse(arg, out value))
                return true;

            ReplyToPlayer(player, errorLangEntry, args);
            return false;
        }

        private bool VerifyValidBool(IPlayer player, string arg, out bool value, LangEntry errorLangEntry, params object[] args)
        {
            if (BooleanParser.TryParse(arg, out value))
                return true;

            ReplyToPlayer(player, errorLangEntry, args);
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
                ReplyToPlayer(player, LangEntry.ErrorNotAtMonument, closestMonument.AliasOrShortName, distance.ToString("f1"));
                return false;
            }

            return true;
        }

        private bool VerifyValidModderPrefab(IPlayer player, string[] args, out string prefabPath)
        {
            prefabPath = null;

            var prefabArg = args.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(prefabArg) || IsKeyBindArg(prefabArg))
            {
                ReplyToPlayer(player, LangEntry.PrefabErrorSyntax, prefabArg);
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

        private bool VerifyLookingAtAdapter<TAdapter, TController>(IPlayer player, out AdapterFindResult<TAdapter, TController> findResult, LangEntry errorLangEntry)
            where TAdapter : TransformAdapter
            where TController : BaseController
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
                ReplyToPlayer(player, errorLangEntry);
            }

            findResult = default(AdapterFindResult<TAdapter, TController>);
            return false;
        }

        private bool VerifyLookingAtAdapter<TAdapter, TController>(IPlayer player, out TAdapter adapter, out TController controller, LangEntry errorLangEntry)
            where TAdapter : TransformAdapter
            where TController : BaseController
        {
            var result = VerifyLookingAtAdapter(player, out AdapterFindResult<TAdapter, TController> findResult, errorLangEntry);
            adapter = findResult.Adapter;
            controller = findResult.Controller;
            return result;
        }

        // Convenient method that does not require an adapter type.
        private bool VerifyLookingAtAdapter<TController>(IPlayer player, out TController controller, LangEntry errorLangEntry)
            where TController : BaseController
        {
            var result = VerifyLookingAtAdapter(player, out AdapterFindResult<TransformAdapter, TController> findResult, errorLangEntry);
            controller = findResult.Controller;
            return result;
        }

        private bool VerifySpawnGroupFound(IPlayer player, string partialGroupName, BaseMonument monument, out SpawnGroupController spawnGroupController)
        {
            var matches = FindSpawnGroups(partialGroupName, monument.AliasOrShortName, partialMatch: true).ToList();

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

        private bool VerifyProfile(IPlayer player, string[] args, out ProfileController controller, LangEntry syntaxLangEntry)
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
            var matches = FindSpawnGroups(spawnGroupName, monument.AliasOrShortName, profile).ToList();
            if (matches.Count == 0)
                return true;

            // Allow renaming a spawn group with different case.
            if (spawnGroupController != null && matches.Count == 1 && matches[0] == spawnGroupController)
                return true;

            ReplyToPlayer(player, LangEntry.SpawnGroupCreateNameInUse, spawnGroupName, monument.AliasOrShortName, profile.Name);
            return false;
        }

        private bool VerifyEntityComponent<T>(IPlayer player, BaseEntity entity, out T component, LangEntry errorLangEntry) where T : UnityEngine.Component
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
            public MonumentEntityComponent Component;
            public TAdapter Adapter;
            public TController Controller;

            public AdapterFindResult(BaseEntity entity)
            {
                Entity = entity;
                Component = MonumentEntityComponent.GetForEntity(entity);
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

            var spawnPointAdapter = GetSpawnPointAdapter(entity);
            if (spawnPointAdapter != null)
            {
                // First check if the caller wants a SpawnPointAdapter, else try to give a SpawnGroupAdapter.
                var spawnAdapter = spawnPointAdapter as TAdapter
                    ?? spawnPointAdapter.SpawnGroupAdapter as TAdapter;

                if (spawnAdapter != null)
                    return new AdapterFindResult<TAdapter, TController>(spawnAdapter, spawnAdapter.Controller as TController);
            }

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
                if (adapter.Controller is not TController controllerOfType)
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
                : default(AdapterFindResult<TAdapter, TController>);
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

        private IEnumerable<SpawnGroupController> FindSpawnGroups(string partialGroupName, string monumentAliasOrShortName, Profile profile = null, bool partialMatch = false)
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
                if (spawnGroupController.Adapters.FirstOrDefault()?.Monument.AliasOrShortName != monumentAliasOrShortName)
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

        private SpawnPointAdapter GetSpawnPointAdapter(BaseEntity entity)
        {
            var spawnPointInstance = entity.GetComponent<SpawnPointInstance>();
            if (spawnPointInstance == null)
                return null;

            var spawnPoint = spawnPointInstance.parentSpawnPoint as CustomSpawnPoint;
            if (spawnPoint == null)
                return null;

            return spawnPoint.Adapter;
        }

        #endregion

        #region Helper Methods

        public static void LogInfo(string message) => Interface.Oxide.LogInfo($"[Monument Addons] {message}");
        public static void LogError(string message) => Interface.Oxide.LogError($"[Monument Addons] {message}");
        public static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Monument Addons] {message}");

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

        private static bool OnCargoShip(BasePlayer player, Vector3 position, out BaseMonument cargoShipMonument)
        {
            cargoShipMonument = null;

            var cargoShip = player.GetParentEntity() as CargoShip;
            if (cargoShip == null)
                return false;

            cargoShipMonument = new DynamicMonument(cargoShip, isMobile: true);

            if (!cargoShipMonument.IsInBounds(position))
                return false;

            return true;
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

        private static BaseEntity FindBaseEntityForPrefab(string prefabName)
        {
            var prefab = GameManager.server.FindPrefab(prefabName);
            if (prefab == null)
                return null;

            return prefab.GetComponent<BaseEntity>();
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

        private bool HasAdminPermission(string userId)
        {
            return permission.UserHasPermission(userId, PermissionAdmin);
        }

        private bool HasAdminPermission(BasePlayer player)
        {
            return HasAdminPermission(player.UserIDString);
        }

        private BaseMonument GetClosestMonument(BasePlayer player, Vector3 position)
        {
            if (OnCargoShip(player, position, out var cargoShipMonument))
                return cargoShipMonument;

            return _monumentHelper.GetClosestMonumentAdapter(position);
        }

        private List<BaseMonument> GetMonumentsByAliasOrShortName(string aliasOrShortName)
        {
            if (aliasOrShortName == CargoShipShortName)
            {
                var cargoShipList = new List<BaseMonument>();
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var cargoShip = entity as CargoShip;
                    if (cargoShip != null)
                        cargoShipList.Add(new DynamicMonument(cargoShip, isMobile: true));
                }

                return cargoShipList.Count > 0 ? cargoShipList : null;
            }

            var monuments = _monumentHelper.FindMonumentsByAlias(aliasOrShortName);
            if (monuments.Count > 0)
                return monuments;

            return _monumentHelper.FindMonumentsByShortName(aliasOrShortName);
        }

        private IEnumerator SpawnAllProfilesRoutine()
        {
            // Delay slightly to allow Monument Finder to finish loading.
            yield return null;
            yield return _profileManager.LoadAllProfilesRoutine();

            ExposedHooks.OnMonumentAddonsInitialized();

            if (_config.EnableDynamicMonuments)
            {
                // We don't want to be subscribed to OnEntitySpawned(CargoShip) until the coroutine is done.
                // Otherwise, a cargo ship could spawn while the coroutine is running and could get double entities.
                Subscribe(nameof(OnEntitySpawned));
            }
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
                    if (_plugin == null)
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
                return haystack.Contains(needle, CompareOptions.IgnoreCase);
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

            public static List<string> FindEntityPrefabMatches(string partialName, UniqueNameRegistry uniqueNameRegistry)
            {
                return FindMatches(
                    GameManifest.Current.entities,
                    prefabPath => StringUtils.Contains(prefabPath, partialName) && FindBaseEntityForPrefab(prefabPath) != null,
                    prefabPath => StringUtils.EqualsCaseInsensitive(prefabPath, partialName),
                    prefabPath => StringUtils.Contains(uniqueNameRegistry.GetUniqueShortName(prefabPath), partialName),
                    prefabPath => StringUtils.EqualsCaseInsensitive(uniqueNameRegistry.GetUniqueShortName(prefabPath), partialName)
                );
            }

            public static List<CustomAddonDefinition> FindCustomAddonMatches(string partialName, IEnumerable<CustomAddonDefinition> customAddons)
            {
                return FindMatches(
                    customAddons,
                    addonDefinition => StringUtils.Contains(addonDefinition.AddonName, partialName),
                    addonDefinition => StringUtils.EqualsCaseInsensitive(addonDefinition.AddonName, partialName)
                );
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

        private static class Ddraw
        {
            public static void Sphere(BasePlayer player, Vector3 origin, float radius, Color color, float duration)
            {
                player.SendConsoleCommand("ddraw.sphere", duration, color, origin, radius);
            }

            public static void Line(BasePlayer player, Vector3 origin, Vector3 target, Color color, float duration)
            {
                player.SendConsoleCommand("ddraw.line", duration, color, origin, target);
            }

            public static void Arrow(BasePlayer player, Vector3 origin, Vector3 target, float headSize, Color color, float duration)
            {
                player.SendConsoleCommand("ddraw.arrow", duration, color, origin, target, headSize);
            }

            public static void ArrowThrough(BasePlayer player, Vector3 center, Quaternion rotation, float length, float headSize, Color color, float duration)
            {
                var start = center - rotation * new Vector3(0, 0, length / 2);
                var end = center + rotation * new Vector3(0, 0, length / 2);
                Arrow(player, start, end, headSize, color, duration);
            }

            public static void Text(BasePlayer player, Vector3 origin, string text, Color color, float duration)
            {
                player.SendConsoleCommand("ddraw.text", duration, color, origin, text);
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

            public static T GetClosestNearbyComponent<T>(Vector3 position, float maxDistance, int layerMask = -1, Func<T, bool> predicate = null) where T : UnityEngine.Component
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

            private static T GetClosestComponent<T>(Vector3 position, List<T> componentList, Func<T, bool> predicate = null) where T : UnityEngine.Component
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
                    startSlot.wireColour = WireColor ?? WireColour.Default;
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
            private MonumentEntityTracker _entityTracker;
            private List<WireSession> _playerSessions = new List<WireSession>();
            private Timer _timer;

            public WireToolManager(MonumentAddons plugin, ProfileStore profileStore, MonumentEntityTracker entityTracker)
            {
                _plugin = plugin;
                _profileStore = profileStore;
                _entityTracker = entityTracker;
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

                if (!_entityTracker.IsMonumentEntity(ioEntity, out EntityAdapter adapter, out EntityController _))
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
                    var position = transform.TransformPoint(slot.handlePosition);

                    Ddraw.Sphere(player, position, DrawSlotRadius, color, DrawDuration);
                    Ddraw.Text(player, position, showSourceSlots ? "OUT" : "IN", color, DrawDuration);
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
                Ddraw.Sphere(player, startPosition, DrawSlotRadius, Color.green, DrawIntervalWithBuffer);

                if (TryGetHitPosition(player, out var hitPosition))
                {
                    var lastPoint = session.Points.Count == 0
                        ? session.StartSlot.handlePosition
                        : session.StartSlot.linePoints.LastOrDefault();

                    Ddraw.Sphere(player, hitPosition, PreviewDotRadius, Color.green, DrawIntervalWithBuffer);
                    Ddraw.Line(player, ioEntity.transform.TransformPoint(lastPoint), hitPosition, Color.green, DrawIntervalWithBuffer);
                }
            }

            private void MaybeStartWire(WireSession session, EntityAdapter adapter)
            {
                var player = session.Player;
                var ioEntity = adapter.Entity as IOEntity;

                var slot = GetClosestIOSlot(ioEntity, player.eyes.HeadRay(), MinAngleDot, out var slotIndex, out var isSourceSlot, wantsOccupiedSlots: false);
                if (slot == null)
                {
                    ShowSlots(player, ioEntity, showSourceSlots: true);
                    ShowSlots(player, ioEntity, showSourceSlots: false);
                    return;
                }

                if (slot.connectedTo.Get() != null)
                {
                    Ddraw.Sphere(player, adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius, Color.red, DrawDuration);
                    return;
                }

                session.StartConnection(adapter, slot, slotIndex, isSource: isSourceSlot);
                Ddraw.Sphere(player, adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius, Color.green, DrawDuration);
                SendEffect(player, WireToolPlugEffect);
            }

            private void MaybeEndWire(WireSession session, EntityAdapter adapter)
            {
                var player = session.Player;
                var ioEntity = adapter.Entity as IOEntity;

                var headRay = player.eyes.HeadRay();
                var slot = GetClosestIOSlot(ioEntity, headRay, MinAngleDot, out var slotIndex, out var distanceSquared, wantsSourceSlot: !session.IsSource);
                if (slot == null)
                {
                    slot = GetClosestIOSlot(ioEntity, headRay, MinAngleDot, out slotIndex, out distanceSquared, wantsSourceSlot: session.IsSource);
                    if (slot != null)
                    {
                        Ddraw.Sphere(player, adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius, Color.red, DrawDuration);
                    }

                    ShowSlots(player, ioEntity, showSourceSlots: !session.IsSource);
                    return;
                }

                if (slot.connectedTo.Get() != null)
                {
                    Ddraw.Sphere(player, adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius, Color.red, DrawDuration);
                    return;
                }

                var adapterProfile = adapter.Controller.Profile;
                var sessionProfile = session.Adapter.Controller.Profile;
                if (adapterProfile != sessionProfile)
                {
                    Ddraw.Sphere(player, adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius, Color.red, DrawDuration);
                    _plugin.ChatMessage(player, LangEntry.WireToolProfileMismatch, sessionProfile, adapterProfile.Name);
                    return;
                }

                if (!adapter.Monument.IsSameAs(session.Adapter.Monument))
                {
                    Ddraw.Sphere(player, adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius, Color.red, DrawDuration);
                    _plugin.ChatMessage(player, LangEntry.WireToolMonumentMismatch);
                    return;
                }

                if (slot.type != session.WireType)
                {
                    Ddraw.Sphere(player, adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius, Color.red, DrawDuration);
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
                    Color = session.WireColor ?? WireColour.Default,
                };

                sourceAdapter.EntityData.AddIOConnection(connectionData);
                _profileStore.Save(sourceAdapter.Controller.Profile);

                (sourceAdapter.Controller as EntityController).StartHandleChangesRoutine();
                session.Reset();

                Ddraw.Sphere(player, adapter.Transform.TransformPoint(slot.handlePosition), DrawSlotRadius, Color.green, DrawDuration);
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

                    if (!_entityTracker.IsMonumentEntity(destinationEntity, out destinationAdapter, out EntityController _))
                        return;
                }
                else
                {
                    destinationAdapter = adapter;

                    var sourceEntity = slot.connectedTo.Get();

                    if (!_entityTracker.IsMonumentEntity(sourceEntity, out sourceAdapter, out EntityController _))
                        return;

                    sourceSlotIndex = slot.connectedToSlot;
                }

                sourceAdapter.EntityData.RemoveIOConnection(sourceSlotIndex);
                _profileStore.Save(sourceAdapter.Controller.Profile);
                var handleChangesRoutine = (sourceAdapter.Controller as EntityController).StartHandleChangesRoutine();
                destinationAdapter.ProfileController.StartCallbackRoutine(handleChangesRoutine,
                    () => (destinationAdapter.Controller as EntityController).StartHandleChangesRoutine());
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

                if (monument.AliasOrShortName == "TrainStation")
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
            public MonoBehaviour Object { get; }
            public virtual string PrefabName => Object.name;
            public virtual string ShortName => GetShortName(PrefabName);
            public virtual string Alias => null;
            public virtual string AliasOrShortName => Alias ?? ShortName;
            public virtual Vector3 Position => Object.transform.position;
            public virtual Quaternion Rotation => Object.transform.rotation;
            public virtual bool IsValid => Object != null;

            protected BaseMonument(MonoBehaviour behavior)
            {
                Object = behavior;
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

            public virtual bool IsSameAs(BaseMonument other)
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

        private class DynamicMonument : BaseMonument
        {
            public BaseEntity RootEntity { get; }
            public bool IsMobile { get; }
            public override bool IsValid => base.IsValid && !RootEntity.IsDestroyed;
            public NetworkableId EntityId { get; }

            protected OBB BoundingBox => RootEntity.WorldSpaceBounds();

            public DynamicMonument(BaseEntity entity, bool isMobile) : base(entity)
            {
                RootEntity = entity;
                EntityId = entity.net?.ID ?? new NetworkableId();
                IsMobile = isMobile;
            }

            public override Vector3 ClosestPointOnBounds(Vector3 position)
            {
                return BoundingBox.ClosestPoint(position);
            }

            public override bool IsInBounds(Vector3 position)
            {
                return BoundingBox.Contains(position);
            }

            public override bool IsSameAs(BaseMonument other)
            {
                return RootEntity == (other as DynamicMonument)?.RootEntity;
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
            protected readonly string _monumentAliasOrShortName;
            private readonly float _undoTime;

            protected Profile Profile => _profileController.Profile;
            private bool IsExpired => _undoTime + ExpireAfterSeconds < UnityEngine.Time.realtimeSinceStartup;
            private bool ProfileExists => _plugin._profileStore.Exists(Profile.Name);

            protected BaseUndo(MonumentAddons plugin, ProfileController profileController, string monumentAliasOrShortName)
            {
                _plugin = plugin;
                _undoTime = UnityEngine.Time.realtimeSinceStartup;
                _profileController = profileController;
                _monumentAliasOrShortName = monumentAliasOrShortName;
            }

            public abstract void Undo(BasePlayer player);
        }

        private class UndoKill : BaseUndo
        {
            protected readonly BaseData _data;

            public UndoKill(MonumentAddons plugin, ProfileController profileController, string monumentAliasOrShortName, BaseData data)
                : base(plugin, profileController, monumentAliasOrShortName)
            {
                _data = data;
            }

            public override void Undo(BasePlayer player)
            {
                Profile.AddData(_monumentAliasOrShortName, _data);
                _plugin._profileStore.Save(Profile);

                if (_profileController.IsEnabled)
                {
                    var matchingMonuments = _plugin.GetMonumentsByAliasOrShortName(_monumentAliasOrShortName);
                    if (matchingMonuments?.Count > 0)
                    {
                        _profileController.SpawnNewData(_data, matchingMonuments);
                    }
                }

                var iPlayer = player.IPlayer;
                _plugin.ReplyToPlayer(iPlayer, LangEntry.UndoKillSuccess, _plugin.GetAddonName(iPlayer, _data), _monumentAliasOrShortName, Profile.Name);
            }
        }

        private class UndoKillSpawnPoint : UndoKill
        {
            private readonly SpawnGroupData _spawnGroupData;
            private readonly SpawnPointData _spawnPointData;

            public UndoKillSpawnPoint(MonumentAddons plugin, ProfileController profileController, string monumentAliasOrShortName, SpawnGroupData spawnGroupData, SpawnPointData spawnPointData)
                : base(plugin, profileController, monumentAliasOrShortName, spawnGroupData)
            {
                _spawnGroupData = spawnGroupData;
                _spawnPointData = spawnPointData;
            }

            public override void Undo(BasePlayer player)
            {
                if (!Profile.HasSpawnGroup(_monumentAliasOrShortName, _spawnGroupData.Id))
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
                _plugin.ReplyToPlayer(iPlayer, LangEntry.UndoKillSuccess, _plugin.GetAddonName(iPlayer, _spawnPointData), _monumentAliasOrShortName, Profile.Name);
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

        #region Entity Component

        private class MonumentEntityComponent : FacepunchBehaviour
        {
            public static void AddToEntity(MonumentEntityTracker entityTracker, BaseEntity entity, IEntityAdapter adapter, BaseMonument monument)
            {
                entity.GetOrAddComponent<MonumentEntityComponent>().Init(entityTracker, adapter, monument);

                var parentSphere = entity.GetParentEntity() as SphereEntity;
                if (parentSphere != null)
                {
                    AddToEntity(entityTracker, parentSphere, adapter, monument);
                }
            }

            public static void RemoveFromEntity(BaseEntity entity)
            {
                DestroyImmediate(entity.GetComponent<MonumentEntityComponent>());

                var parentSphere = entity.GetParentEntity() as SphereEntity;
                if (parentSphere != null)
                {
                    RemoveFromEntity(parentSphere);
                }
            }

            public static MonumentEntityComponent GetForEntity(BaseEntity entity)
            {
                return entity.GetComponent<MonumentEntityComponent>();
            }

            public static MonumentEntityComponent GetForEntity(NetworkableId id)
            {
                return BaseNetworkable.serverEntities.Find(id)?.GetComponent<MonumentEntityComponent>();
            }

            public IEntityAdapter Adapter;
            private MonumentEntityTracker _entityTracker;
            private BaseEntity _entity;

            private void Awake()
            {
                _entity = GetComponent<BaseEntity>();
            }

            public void Init(MonumentEntityTracker entityTracker, IEntityAdapter adapter, BaseMonument monument)
            {
                _entityTracker = entityTracker;
                _entityTracker.RegisterEntity(_entity);
                Adapter = adapter;
            }

            private void OnDestroy()
            {
                _entityTracker.UnregisterEntity(_entity);
                Adapter.OnEntityKilled(_entity);
            }
        }

        private class MonumentEntityTracker
        {
            private HashSet<BaseEntity> _trackedEntities = new HashSet<BaseEntity>();

            public bool IsMonumentEntity(BaseEntity entity)
            {
                return entity != null && !entity.IsDestroyed && _trackedEntities.Contains(entity);
            }

            public bool IsMonumentEntity<TAdapter, TController>(BaseEntity entity, out TAdapter adapter, out TController controller)
                where TAdapter : EntityAdapter
                where TController : EntityController
            {
                adapter = null;
                controller = null;

                if (!IsMonumentEntity(entity))
                    return false;

                var component = MonumentEntityComponent.GetForEntity(entity);
                if (component == null)
                    return false;

                adapter = component.Adapter as TAdapter;
                controller = adapter?.Controller as TController;

                return controller != null;
            }

            public bool IsMonumentEntity<TController>(BaseEntity entity, out TController controller)
                where TController : EntityController
            {
                return IsMonumentEntity(entity, out EntityAdapter _, out controller);
            }

            public void RegisterEntity(BaseEntity entity)
            {
                _trackedEntities.Add(entity);
            }

            public void UnregisterEntity(BaseEntity entity)
            {
                _trackedEntities.Remove(entity);
            }
        }

        #endregion

        #region Adapter/Controller - Base

        private interface IEntityAdapter
        {
            void OnEntityKilled(BaseEntity entity);
        }

        // Represents a single entity, spawn group, or spawn point at a single monument.
        private abstract class BaseAdapter
        {
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

            public virtual void Unregister() {}

            // Called when the addon is scheduled to be killed or unregistered.
            public virtual void PreUnload() {}
        }

        // Represents a single entity or spawn point at a single monument.
        private abstract class TransformAdapter : BaseAdapter
        {
            public BaseTransformData TransformData { get; }

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

                    Plugin.TrackStart();
                    var adapter = SpawnAtMonument(monument);
                    Plugin.TrackEnd();
                    yield return adapter.WaitInstruction;
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

            public void Unregister()
            {
                for (var i = Adapters.Count - 1; i >= 0; i--)
                {
                    Plugin.TrackStart();
                    Adapters[i].Unregister();
                    Plugin.TrackEnd();
                }
            }

            public BaseAdapter FindAdapterForMonument(BaseMonument monument)
            {
                foreach (var adapter in Adapters)
                {
                    if (adapter.Monument.IsSameAs(monument))
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
            public Transform Transform { get; private set; }
            public override Vector3 Position => Transform.position;
            public override Quaternion Rotation => Transform.rotation;

            public PrefabAdapter(BaseController controller, PrefabData prefabData, BaseMonument monument)
                : base(prefabData, controller, monument)
            {
                PrefabData = prefabData;
            }

            public override void Spawn()
            {
                GameObject = GameManager.server.CreatePrefab(PrefabData.PrefabName, IntendedPosition, IntendedRotation);
                Transform = GameObject.transform;
            }

            public override void Kill()
            {
                UnityEngine.Object.Destroy(GameObject);
            }
        }

        private class PrefabController : BaseController
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
        }

        #endregion

        #region Entity Adapter/Controller

        private class EntityAdapter : TransformAdapter, IEntityAdapter
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
            public override Vector3 Position => Transform.position;
            public override Quaternion Rotation => Transform.rotation;

            public Transform Transform { get; private set; }

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
                        Transform = Entity.transform;

                        PreEntitySpawn();
                        PostEntitySpawn();
                        HandleChanges();
                        MonumentEntityComponent.AddToEntity(Plugin._entityTracker, Entity, this, Monument);

                        var vendingMachine = Entity as NPCVendingMachine;
                        if (vendingMachine != null)
                        {
                            Plugin.RefreshVendingProfile(vendingMachine);
                        }

                        var mountable = Entity as BaseMountable;
                        if (mountable != null)
                        {
                            if (Monument is DynamicMonument { IsMobile: true })
                            {
                                mountable.isMobile = true;
                                if (!BaseMountable.FixedUpdateMountables.Contains(mountable))
                                {
                                    BaseMountable.FixedUpdateMountables.Add(mountable);
                                }
                            }
                        }

                        ExposedHooks.OnMonumentEntitySpawned(Entity, Monument.Object, Data.Id);

                        if (!_config.EnableEntitySaving)
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
                Transform = Entity.transform;

                PreEntitySpawn();
                Entity.Spawn();
                PostEntitySpawn();
                ExposedHooks.OnMonumentEntitySpawned(Entity, Monument.Object, Data.Id);

                if (_config.EnableEntitySaving && Entity != existingEntity)
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

            public virtual void OnEntityKilled(BaseEntity entity)
            {
                Plugin.TrackStart();

                // Only consider the adapter destroyed if the main entity was destroyed.
                // For example, the scaled sphere parent may be killed if resized to default scale.
                if (entity == Entity)
                {
                    if (entity == null || entity.IsDestroyed)
                    {
                        if (_profileStateData.RemoveEntity(Profile.Name, Monument, Data.Id))
                        {
                            Plugin._saveProfileStateDebounced.Schedule();
                        }
                    }
                    Controller.OnAdapterKilled(this);
                }

                Plugin.TrackEnd();
            }

            public void UpdateScale()
            {
                if (Plugin.TryScaleEntity(Entity, EntityData.Scale))
                {
                    var parentSphere = Entity.GetParentEntity() as SphereEntity;
                    if (parentSphere == null)
                        return;

                    if (Plugin._entityTracker.IsMonumentEntity(parentSphere))
                        return;

                    MonumentEntityComponent.AddToEntity(Plugin._entityTracker, parentSphere, this, Monument);
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

            public bool TrySaveAndApplyChanges()
            {
                var hasChanged = false;

                if (!IsAtIntendedPosition)
                {
                    EntityData.Position = LocalPosition;
                    EntityData.RotationAngles = LocalRotation.eulerAngles;
                    EntityData.SnapToTerrain = IsOnTerrain(Position);
                    hasChanged = true;
                }

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
                    singleEntityController.StartHandleChangesRoutine();
                    Plugin._profileStore.Save(singleEntityController.Profile);
                }

                return hasChanged;
            }

            public virtual void HandleChanges()
            {
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
                EntitySetupUtils.PostSpawnShared(Plugin, Entity, _config.EnableEntitySaving);

                UpdatePuzzle();

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
            }

            protected virtual void PreEntityKill() {}

            public override void Unregister()
            {
                if (IsDestroyed)
                    return;

                // Not safe to unregister the entity if the profile no longer declares it.
                if (!Profile.HasEntity(Monument.AliasOrShortName, EntityData))
                    return;

                // Not safe to unregister the entity if it's not tracked in the profile state.
                if (!_profileStateData.HasEntity(Profile.Name, Monument, Data.Id, Entity.net.ID))
                    return;

                MonumentEntityComponent.RemoveFromEntity(Entity);
            }

            protected BaseEntity CreateEntity(Vector3 position, Quaternion rotation)
            {
                var entity = GameManager.server.CreateEntity(EntityData.PrefabName, position, rotation);
                if (entity == null)
                    return null;

                EnableSavingRecursive(entity, enableSaving: _config.EnableEntitySaving);

                if (Monument is DynamicMonument dynamicMonument)
                {
                    entity.SetParent(dynamicMonument.RootEntity, worldPositionStays: true);

                    if (dynamicMonument.IsMobile)
                    {
                        var mountable = entity as BaseMountable;
                        if (mountable != null)
                        {
                            // Setting isMobile prior to spawn will automatically update the position of mounted players.
                            mountable.isMobile = true;
                        }
                    }
                }

                MonumentEntityComponent.AddToEntity(Plugin._entityTracker, entity, this, Monument);

                return entity;
            }

            private void UpdatePosition()
            {
                if (IsAtIntendedPosition)
                    return;

                var entityToMove = GetEntityToMove();
                var entityToRotate = Entity;

                entityToMove.transform.position = IntendedPosition;
                entityToRotate.transform.rotation = IntendedRotation;

                BroadcastEntityTransformChange(entityToMove);

                if (entityToRotate != entityToMove)
                {
                    BroadcastEntityTransformChange(entityToRotate);
                }
            }

            private List<CCTV_RC> GetNearbyStaticCameras()
            {
                if (Monument is DynamicMonument dynamicMonument && dynamicMonument.RootEntity == Entity.GetParentEntity())
                {
                    var cargoCameraList = new List<CCTV_RC>();
                    foreach (var child in dynamicMonument.RootEntity.children)
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

        private class EntityController : BaseController
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

            public Coroutine StartHandleChangesRoutine()
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

            public override void OnEntityKilled(BaseEntity entity)
            {
                base.OnEntityKilled(entity);

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
                if (Monument is DynamicMonument dynamicMonument && dynamicMonument.RootEntity == Entity.GetParentEntity())
                {
                    var cargoComputerStationList = new List<ComputerStation>();
                    foreach (var child in dynamicMonument.RootEntity.children)
                    {
                        var computerStation = child as ComputerStation;
                        if (computerStation != null && computerStation.isStatic)
                            cargoComputerStationList.Add(computerStation);
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

                return FindBaseEntityForPrefab(entityData.PrefabName) is T;
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

        private class CustomSpawnPoint : BaseSpawnPoint
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

            public SpawnPointAdapter Adapter { get; private set; }
            private SpawnPointData _spawnPointData;
            private Transform _transform;
            private BaseEntity _parentEntity;
            private List<SpawnPointInstance> _instances = new List<SpawnPointInstance>();

            public void Init(SpawnPointAdapter adapter, SpawnPointData spawnPointData)
            {
                Adapter = adapter;
                _spawnPointData = spawnPointData;
            }

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

                if (!entity.HasParent() && _parentEntity != null && !_parentEntity.IsDestroyed)
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

                Adapter.SpawnGroupAdapter.SpawnGroup.HandleObjectRetired();
            }

            public override bool IsAvailableTo(GameObjectRef prefabRef)
            {
                if (!base.IsAvailableTo(prefabRef))
                    return false;

                if (_spawnPointData.Exclusive && _instances.Count > 0)
                    return false;

                if (_spawnPointData.CheckSpace)
                {
                    if (CustomBoundsCheckMask.TryGetValue(prefabRef.resourcePath, out var customBoundsCheckMask))
                        return SpawnHandler.CheckBounds(prefabRef.Get(), _transform.position, _transform.rotation, Vector3.one, customBoundsCheckMask);

                    return SingletonComponent<SpawnHandler>.Instance.CheckBounds(prefabRef.Get(), _transform.position, _transform.rotation, Vector3.one);
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

            public void OnDestroy()
            {
                KillSpawnedInstances();
                Adapter.OnSpawnPointKilled();
            }

            public void KillSpawnedInstances(string prefabName = null)
            {
                for (var i = _instances.Count - 1; i >= 0; i--)
                {
                    var entity = _instances[i].GetComponent<BaseEntity>();
                    if ((prefabName == null || entity.PrefabName == prefabName) && entity != null && !entity.IsDestroyed)
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
                var kayak = vehicle as Kayak;
                if (kayak != null)
                {
                    kayak.timeSinceLastUsed = float.MinValue;
                    return;
                }

                var boat = vehicle as MotorRowboat;
                if (boat != null)
                {
                    boat.timeSinceLastUsedFuel = float.MinValue;
                    return;
                }

                var snowmobile = vehicle as Snowmobile;
                if (snowmobile != null)
                {
                    snowmobile.timeSinceLastUsed = float.MinValue;
                    return;
                }

                var sub = vehicle as BaseSubmarine;
                if (sub != null)
                {
                    sub.timeSinceLastUsed = float.MinValue;
                    return;
                }

                var hab = vehicle as HotAirBalloon;
                if (hab != null)
                {
                    hab.lastBlastTime = float.MaxValue;
                    return;
                }

                var heli = vehicle as PlayerHelicopter;
                if (heli != null)
                {
                    heli.lastEngineOnTime = float.MaxValue;
                    return;
                }

                var car = vehicle as ModularCar;
                if (car != null)
                {
                    car.lastEngineOnTime = float.MaxValue;
                    return;
                }

                var horse = vehicle as RidableHorse;
                if (horse != null)
                {
                    horse.lastInputTime = float.MaxValue;
                    return;
                }

                var workcart = vehicle as TrainEngine;
                if (workcart != null)
                {
                    workcart.CancelInvoke(workcart.DecayTick);
                    return;
                }
            }

            public void MoveSpawnedInstances()
            {
                for (var i = _instances.Count - 1; i >= 0; i--)
                {
                    var entity = _instances[i].GetComponent<BaseEntity>();
                    if (entity != null && !entity.IsDestroyed)
                    {
                        Adapter.SpawnPoint.GetLocation(out var position, out var rotation);

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
            }
        }

        private class CustomSpawnGroup : SpawnGroup
        {
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

            private void OnDestroy()
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
            public override Vector3 Position => _transform.position;
            public override Quaternion Rotation => _transform.rotation;

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

                if (Monument is DynamicMonument dynamicMonument)
                {
                    _transform.SetParent(dynamicMonument.RootEntity.transform, worldPositionStays: true);
                }

                SpawnPoint = gameObject.AddComponent<CustomSpawnPoint>();
                SpawnPoint.Init(this, SpawnPointData);
            }

            public override void Kill()
            {
                UnityEngine.Object.Destroy(SpawnPoint?.gameObject);
            }

            public override void PreUnload()
            {
                SpawnPoint.PreUnload();
            }

            public void OnSpawnPointKilled()
            {
                SpawnGroupAdapter.OnSpawnPointAdapterKilled(this);
            }

            public void KillSpawnedInstances(string prefabName)
            {
                SpawnPoint.KillSpawnedInstances(prefabName);
            }

            public void UpdatePosition()
            {
                if (!IsAtIntendedPosition)
                {
                    _transform.SetPositionAndRotation(IntendedPosition, IntendedRotation);
                    SpawnPoint.MoveSpawnedInstances();
                }
            }
        }

        private class SpawnGroupAdapter : BaseAdapter
        {
            public SpawnGroupData SpawnGroupData { get; }
            public List<SpawnPointAdapter> SpawnPointAdapters { get; } = new List<SpawnPointAdapter>();
            public CustomSpawnGroup SpawnGroup { get; private set; }
            public PuzzleReset AssociatedPuzzleReset { get; private set; }

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
                    return entity != null && !Plugin._entityTracker.IsMonumentEntity(entity);
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

                var respawnDelayMinChanged = SpawnGroup.respawnDelayMin != respawnDelayMin;
                var respawnDelayMaxChanged = SpawnGroup.respawnDelayMax != respawnDelayMax;

                SpawnGroup.respawnDelayMin = respawnDelayMin;
                SpawnGroup.respawnDelayMax = respawnDelayMax;

                if (SpawnGroup.gameObject.activeSelf && (respawnDelayMinChanged || respawnDelayMaxChanged))
                {
                    SpawnGroup.UpdateSpawnClock();
                }
            }

            private void UpdatePrefabEntries()
            {
                if (SpawnGroup.prefabs.Count == SpawnGroupData.Prefabs.Count)
                {
                    for (var i = 0; i < SpawnGroup.prefabs.Count; i++)
                    {
                        SpawnGroup.prefabs[i].weight = SpawnGroupData.Prefabs[i].Weight;
                    }

                    return;
                }

                SpawnGroup.prefabs.Clear();

                foreach (var prefabEntry in SpawnGroupData.Prefabs)
                {
                    if (!GameManifest.pathToGuid.TryGetValue(prefabEntry.PrefabName, out var guid))
                        continue;

                    SpawnGroup.prefabs.Add(new SpawnGroup.SpawnEntry
                    {
                        prefab = new GameObjectRef { guid = guid },
                        weight = prefabEntry.Weight,
                    });
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

            public void KillSpawnedInstances(string prefabName)
            {
                foreach (var spawnPointAdapter in SpawnPointAdapters)
                {
                    spawnPointAdapter.KillSpawnedInstances(prefabName);
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

        private class SpawnGroupController : BaseController
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

            public void StartSpawnRoutine()
            {
                ProfileController.StartCoroutine(SpawnTickRoutine());
            }

            public void StartKillSpawnedInstancesRoutine(string prefabName)
            {
                ProfileController.StartCoroutine(KillSpawnedInstancesRoutine(prefabName));
            }

            public void StartRespawnRoutine()
            {
                ProfileController.StartCoroutine(RespawnRoutine());
            }

            private IEnumerator SpawnTickRoutine()
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters.ToList())
                {
                    spawnGroupAdapter.SpawnTick();
                    yield return null;
                }
            }

            private IEnumerator KillSpawnedInstancesRoutine(string prefabName = null)
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters.ToList())
                {
                    spawnGroupAdapter.KillSpawnedInstances(prefabName);
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

        private class PasteAdapter : TransformAdapter, IEntityAdapter
        {
            private const float CopyPasteMagicRotationNumber = 57.2958f;

            public PasteData PasteData { get; }
            public override Vector3 Position => _position;
            public override Quaternion Rotation => _rotation;

            private Vector3 _position;
            private Quaternion _rotation;
            private bool _isWorking;
            private Action _cancelPaste;
            private List<BaseEntity> _pastedEntities = new List<BaseEntity>();

            public PasteAdapter(PasteData pasteData, BaseController controller, BaseMonument monument) : base(pasteData, controller, monument)
            {
                PasteData = pasteData;
            }

            public override void Spawn()
            {
                // Simply cache the position and rotation since they are not expected to change.
                // Parenting pastes to dynamic monuments is not currently supported.
                _position = IntendedPosition;
                _rotation = IntendedRotation;

                _cancelPaste = PasteUtils.PasteWithCancelCallback(Plugin.CopyPaste, PasteData, _position, _rotation.eulerAngles.y / CopyPasteMagicRotationNumber, OnEntityPasted, OnPasteComplete);

                if (_cancelPaste != null)
                {
                    _isWorking = true;
                    WaitInstruction = new WaitWhile(() => _isWorking);
                }
            }

            public override void Kill()
            {
                _cancelPaste?.Invoke();

                _isWorking = true;
                CoroutineManager.StartGlobalCoroutine(KillRoutine());
                WaitInstruction = WaitWhileWithTimeout(() => _isWorking, 5);
            }

            public void OnEntityKilled(BaseEntity entity)
            {
                _pastedEntities.Remove(entity);

                if (_pastedEntities.Count == 0)
                {
                    // Cancel the paste in case it is still in-progress, to avoid an orphaned adapter.
                    _cancelPaste?.Invoke();

                    Controller.OnAdapterKilled(this);
                }
            }

            private IEnumerator KillRoutine()
            {
                var pastedEntities = _pastedEntities.ToList();

                // Remove the entities in reverse order. Hopefully this makes the top of the building get removed first.
                for (var i = pastedEntities.Count - 1; i >= 0; i--)
                {
                    var entity = pastedEntities[i];
                    if (entity != null && !entity.IsDestroyed)
                    {
                        Plugin.TrackStart();
                        entity.Kill();
                        Plugin.TrackEnd();
                        yield return null;
                    }
                }

                _isWorking = false;
            }

            private void OnEntityPasted(BaseEntity entity)
            {
                EntitySetupUtils.PreSpawnShared(entity);
                EntitySetupUtils.PostSpawnShared(Plugin, entity, enableSaving: false);

                MonumentEntityComponent.AddToEntity(Plugin._entityTracker, entity, this, Monument);
                _pastedEntities.Add(entity);
            }

            private void OnPasteComplete()
            {
                _isWorking = false;
            }
        }

        private class PasteController : BaseController
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

                if (addonSpec.TryGetValue("Spawn", out var spawnCallback))
                {
                    addonDefinition.Spawn = spawnCallback as CustomSpawnCallback;
                }

                if (addonSpec.TryGetValue("Kill", out var killCallback))
                {
                    addonDefinition.Kill = killCallback as CustomKillCallback;
                }

                if (addonSpec.TryGetValue("Update", out var updateCallback))
                {
                    addonDefinition.Update = updateCallback as CustomUpdateCallback;
                }

                if (addonSpec.TryGetValue("AddDisplayInfo", out var addDataCallback))
                {
                    addonDefinition.AddDisplayInfo = addDataCallback as CustomAddDisplayInfoCallback;
                }

                return addonDefinition;
            }

            public string AddonName;
            public Plugin OwnerPlugin;
            public CustomSpawnCallback Spawn;
            public CustomKillCallback Kill;
            public CustomUpdateCallback Update;
            public CustomAddDisplayInfoCallback AddDisplayInfo;

            public List<CustomAddonAdapter> AdapterUsers = new List<CustomAddonAdapter>();

            public Dictionary<string, object> ToApiResult(ProfileStore profileStore)
            {
                return new Dictionary<string, object>
                {
                    ["SetData"] = new CustomSetDataCallback(
                        (component, data) =>
                        {
                            if (Update == null)
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

                            var controller = matchingAdapter.Controller as CustomAddonController;
                            controller.CustomAddonData.SetData(data);
                            profileStore.Save(controller.Profile);

                            foreach (var adapter in controller.Adapters)
                            {
                                Update((adapter as CustomAddonAdapter).Component, controller.CustomAddonData.GetSerializedData());
                            }
                        }
                    ),
                };
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
                        foreach (var monumentEntry in profileController.Profile.MonumentDataMap)
                        {
                            var monumentName = monumentEntry.Key;
                            var monumentData = monumentEntry.Value;

                            foreach (var customAddonData in monumentData.CustomAddons)
                            {
                                if (customAddonData.AddonName == addonDefinition.AddonName)
                                {
                                    profileController.SpawnNewData(customAddonData, _plugin.GetMonumentsByAliasOrShortName(monumentName));
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
                    foreach (var adapter in addonDefinition.AdapterUsers)
                    {
                        controllerList.Add(adapter.Controller as CustomAddonController);
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
                return _customAddonsByName.TryGetValue(addonName, out var addonDefinition)
                    ? addonDefinition
                    : null;
            }

            private List<CustomAddonDefinition> GetAddonsForPlugin(Plugin plugin)
            {
                return _customAddonsByPlugin.TryGetValue(plugin.Name, out var addonsForPlugin)
                    ? addonsForPlugin
                    : null;
            }

            private IEnumerator DestroyControllersRoutine(ICollection<CustomAddonController> controllerList)
            {
                foreach (var controller in controllerList)
                {
                    yield return controller.KillRoutine();
                }
            }
        }

        private class CustomAddonAdapter : TransformAdapter, IEntityAdapter
        {
            private class CustomAddonComponent : MonoBehaviour
            {
                public CustomAddonAdapter Adapter;

                private void OnDestroy()
                {
                    Adapter.OnAddonDestroyed();
                }
            }

            public CustomAddonData CustomAddonData { get; }
            public CustomAddonDefinition AddonDefinition { get; }
            public UnityEngine.Component Component { get; private set; }

            public override Vector3 Position => Component.transform.position;
            public override Quaternion Rotation => Component.transform.rotation;

            private bool _wasKilled;

            public CustomAddonAdapter(CustomAddonData customAddonData, BaseController controller, BaseMonument monument, CustomAddonDefinition addonDefinition) : base(customAddonData, controller, monument)
            {
                CustomAddonData = customAddonData;
                AddonDefinition = addonDefinition;
            }

            public override void Spawn()
            {
                Component = AddonDefinition.Spawn(IntendedPosition, IntendedRotation, CustomAddonData.GetSerializedData());
                AddonDefinition.AdapterUsers.Add(this);

                var entity = Component as BaseEntity;
                if (entity != null)
                {
                    MonumentEntityComponent.AddToEntity(Plugin._entityTracker, entity, this, Monument);
                }
                else
                {
                    Component.gameObject.AddComponent<CustomAddonComponent>().Adapter = this;
                }
            }

            public override void Kill()
            {
                if (_wasKilled)
                    return;

                _wasKilled = true;
                AddonDefinition.Kill(Component);
            }

            public void OnEntityKilled(BaseEntity entity)
            {
                // In case it's a multi-part addon, call Kill() to ensure the whole addon is removed.
                Kill();

                AddonDefinition.AdapterUsers.Remove(this);
                Controller.OnAdapterKilled(this);
            }

            public void OnAddonDestroyed()
            {
                // In case it's a multi-part addon, call Kill() to ensure the whole addon is removed.
                Kill();

                AddonDefinition.AdapterUsers.Remove(this);
                Controller.OnAdapterKilled(this);
            }
        }

        private class CustomAddonController : BaseController
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
                var baseEntity = FindBaseEntityForPrefab(entityData.PrefabName);
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
                    var ioEntity = GameManager.server.FindPrefab(prefabPath)?.GetComponent<IOEntity>();
                    if (ioEntity == null || !HasInput(ioEntity, IOType.Electric))
                        continue;

                    if (_dontPowerPrefabsOfType.Contains(ioEntity.GetType()))
                    {
                        _dontPowerPrefabIds.Add(ioEntity.prefabID);
                    }
                }

                foreach (var entry in _inputSlotsByPrefabName)
                {
                    var ioEntity = GameManager.server.FindPrefab(entry.Key)?.GetComponent<IOEntity>();
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

            private const int DisplayIntervalDuration = 2;

            private class PlayerInfo
            {
                public Timer Timer;
                public ProfileController ProfileController;
            }

            private float DisplayDistanceSquared => Mathf.Pow(_config.DebugDisplayDistance, 2);

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

            public void ShowAllRepeatedly(BasePlayer player, int duration = -1, bool immediate = true)
            {
                var playerInfo = GetOrCreatePlayerInfo(player);

                if (immediate || playerInfo.Timer == null || playerInfo.Timer.Destroyed)
                {
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
                        var remainingTime = playerInfo.Timer.Repetitions * DisplayIntervalDuration;
                        var newDuration = duration > 0 ? duration : Math.Max(remainingTime, DefaultDisplayDuration);
                        var newRepetitions = Math.Max(newDuration / DisplayIntervalDuration, 1);
                        playerInfo.Timer.Reset(delay: -1, repetitions: newRepetitions);
                    }
                    return;
                }

                if (duration == -1)
                {
                    duration = DefaultDisplayDuration;
                }

                // Ensure repetitions is not 0 since that would result in infintire repetitions.
                var repetitions = Math.Max(duration / DisplayIntervalDuration, 1);

                playerInfo.Timer = _plugin.timer.Repeat(DisplayIntervalDuration - 0.2f, repetitions, () =>
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
                    return Color.grey;

                if (adapter is SpawnPointAdapter)
                    return new Color(1, 0.5f, 0);

                if (adapter is PasteAdapter)
                    return Color.cyan;

                if (adapter is CustomAddonAdapter)
                    return Color.green;

                return Color.magenta;
            }

            private void AddCommonInfo(BasePlayer player, ProfileController profileController, BaseController controller, BaseAdapter adapter)
            {
                _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelProfile, profileController.Profile.Name));

                var monumentTierList = GetTierList(GetMonumentTierMask(adapter.Monument.Position));
                _sb.AppendLine(monumentTierList.Count > 0
                    ? _plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelMonumentWithTier, adapter.Monument.AliasOrShortName, controller.Adapters.Count, string.Join(", ", monumentTierList))
                    : _plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelMonument, adapter.Monument.AliasOrShortName, controller.Adapters.Count));
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

                            var spawnPointAdapter = FindClosestSpawnPointAdapter(spawnGroupAdapter, playerPosition, out _);
                            if (spawnPointAdapter != null)
                            {
                                Ddraw.Arrow(player, entityAdapter.Position + ArrowVerticalOffeset, spawnPointAdapter.Position + ArrowVerticalOffeset, 0.25f, DetermineColor(spawnPointAdapter, playerInfo, profileController), DisplayIntervalDuration);
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

            private void ShowEntityInfo(BasePlayer player, EntityAdapter adapter, Vector3 playerPosition, PlayerInfo playerInfo)
            {
                var entityData = adapter.EntityData;
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);

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
                        Ddraw.Arrow(player, adapter.Position + new Vector3(0, 1.5f, 0), vehicleSpawner.transform.position, 0.25f, color, DisplayIntervalDuration);
                    }
                }

                var doorManipulator = adapter.Entity as DoorManipulator;
                if (doorManipulator != null && doorManipulator.targetDoor != null)
                {
                    Ddraw.Arrow(player, adapter.Position, doorManipulator.targetDoor.transform.position, 0.2f, color, DisplayIntervalDuration);
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

                Ddraw.Text(player, adapter.Position, _sb.ToString(), color, DisplayIntervalDuration);
            }

            private void ShowPrefabInfo(BasePlayer player, PrefabAdapter adapter, Vector3 playerPosition, PlayerInfo playerInfo)
            {
                var prefabData = adapter.PrefabData;
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);

                var uniqueEntityName = _uniqueNameRegistry.GetUniqueShortName(prefabData.PrefabName);

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_plugin.GetMessage(player.UserIDString, LangEntry.ShowHeaderPrefab, uniqueEntityName)}</size>");
                AddCommonInfo(player, profileController, controller, adapter);

                Ddraw.Text(player, adapter.Position, _sb.ToString(), color, DisplayIntervalDuration);
            }
            private void ShowSpawnPointInfo(BasePlayer player, SpawnPointAdapter adapter, SpawnGroupAdapter spawnGroupAdapter, PlayerInfo playerInfo, bool showGroupInfo)
            {
                var spawnPointData = adapter.SpawnPointData;
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);

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
                            _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelEntityDetail, _uniqueNameRegistry.GetUniqueShortName(prefabEntry.PrefabName), prefabEntry.Weight, relativeChance));
                        }
                    }
                    else
                    {
                        _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelNoEntities));
                    }

                    foreach (var otherAdapter in spawnGroupAdapter.SpawnPointAdapters)
                    {
                        Ddraw.Arrow(player, otherAdapter.Position + ArrowVerticalOffeset, adapter.Position + ArrowVerticalOffeset, 0.25f, color, DisplayIntervalDuration);
                    }
                }

                Ddraw.ArrowThrough(player, adapter.Position + ArrowVerticalOffeset, adapter.Rotation, 1f, 0.15f, color, DisplayIntervalDuration);
                Ddraw.Sphere(player, adapter.Position, 0.5f, color, DisplayIntervalDuration);
                Ddraw.Text(player, adapter.Position, _sb.ToString(), color, DisplayIntervalDuration);

                if (spawnGroupData.RespawnWhenNearestPuzzleResets)
                {
                    _sb.Clear();

                    var puzzleReset = spawnGroupAdapter.AssociatedPuzzleReset;
                    if (puzzleReset != null)
                    {
                        ShowPuzzleInfo(player, null, spawnGroupAdapter.AssociatedPuzzleReset, player.transform.position, playerInfo);
                        var position = puzzleReset.transform.position;
                        Ddraw.Arrow(player, position + ArrowVerticalOffeset, adapter.Position + ArrowVerticalOffeset, 0.25f, DetermineColor(adapter, playerInfo, profileController), DisplayIntervalDuration);
                        Ddraw.Text(player, position, _sb.ToString(), color, DisplayIntervalDuration);
                    }
                }
            }

            private void ShowPasteInfo(BasePlayer player, PasteAdapter adapter, PlayerInfo playerInfo)
            {
                var pasteData = adapter.PasteData;
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_plugin.GetMessage(player.UserIDString, LangEntry.ShowHeaderPaste, pasteData.Filename)}</size>");
                AddCommonInfo(player, profileController, controller, adapter);

                Ddraw.Text(player, adapter.Position, _sb.ToString(), color, DisplayIntervalDuration);
            }

            private void ShowCustomAddonInfo(BasePlayer player, CustomAddonAdapter adapter, PlayerInfo playerInfo)
            {
                var customAddonData = adapter.CustomAddonData;
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);

                var addonDefinition = adapter.AddonDefinition;

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_plugin.GetMessage(player.UserIDString, LangEntry.ShowHeaderCustom, customAddonData.AddonName)}</size>");
                _sb.AppendLine(_plugin.GetMessage(player.UserIDString, LangEntry.ShowLabelPlugin, addonDefinition.OwnerPlugin.Name));
                AddCommonInfo(player, profileController, controller, adapter);

                addonDefinition.AddDisplayInfo?.Invoke(adapter.Component, customAddonData.GetSerializedData(), _sb);

                Ddraw.Text(player, adapter.Position, _sb.ToString(), color, DisplayIntervalDuration);
            }

            private SpawnPointAdapter FindClosestSpawnPointAdapter(SpawnGroupAdapter spawnGroupAdapter, Vector3 origin, out float closestDistanceSquared)
            {
                SpawnPointAdapter closestSpawnPointAdapter = null;
                closestDistanceSquared = float.MaxValue;

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

            private void ShowNearbyAdapters(BasePlayer player, Vector3 playerPosition, PlayerInfo playerInfo)
            {
                var isAdmin = player.IsAdmin;
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                foreach (var adapter in _plugin._profileManager.GetEnabledAdapters<BaseAdapter>())
                {
                    if (adapter is EntityAdapter entityAdapter)
                    {
                        if ((playerPosition - entityAdapter.Position).sqrMagnitude <= DisplayDistanceSquared)
                        {
                            ShowEntityInfo(player, entityAdapter, playerPosition, playerInfo);
                        }

                        continue;
                    }

                    if (adapter is PrefabAdapter prefabAdapter)
                    {
                        if ((playerPosition - prefabAdapter.Position).sqrMagnitude <= DisplayDistanceSquared)
                        {
                            ShowPrefabInfo(player, prefabAdapter, playerPosition, playerInfo);
                        }

                        continue;
                    }

                    if (adapter is SpawnGroupAdapter spawnGroupAdapter)
                    {
                        var closestSpawnPointAdapter = FindClosestSpawnPointAdapter(spawnGroupAdapter, playerPosition, out var closestDistanceSquared);

                        if (closestDistanceSquared <= DisplayDistanceSquared)
                        {
                            foreach (var spawnPointAdapter in spawnGroupAdapter.SpawnPointAdapters)
                            {
                                ShowSpawnPointInfo(player, spawnPointAdapter, spawnGroupAdapter, playerInfo, showGroupInfo: spawnPointAdapter == closestSpawnPointAdapter);
                            }
                        }

                        continue;
                    }

                    if (adapter is PasteAdapter pasteAdapter)
                    {
                        if ((playerPosition - pasteAdapter.Position).sqrMagnitude <= DisplayDistanceSquared)
                        {
                            ShowPasteInfo(player, pasteAdapter, playerInfo);
                        }

                        continue;
                    }

                    if (adapter is CustomAddonAdapter customAddonAdapter)
                    {
                        if ((playerPosition - customAddonAdapter.Position).sqrMagnitude <= DisplayDistanceSquared)
                        {
                            ShowCustomAddonInfo(player, customAddonAdapter, playerInfo);
                        }

                        continue;
                    }
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
                    ProfileStatus = ProfileStatus.Loaded;
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
                    ProfileStatus = ProfileStatus.Loaded;
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

            public void Unregister()
            {
                foreach (var controller in _controllersByData.Values.ToList())
                {
                    controller.Unregister();
                }
            }

            public void Unload(IEnumerator cleanupRoutine = null)
            {
                if (ProfileStatus == ProfileStatus.Unloading || ProfileStatus == ProfileStatus.Unloaded)
                    return;

                ProfileStatus = ProfileStatus.Unloading;
                CoroutineManager.StartGlobalCoroutine(UnloadRoutine(cleanupRoutine));
            }

            public void Reload(Profile newProfileData)
            {
                Interrupt();
                PreUnload();

                if (_config.EnableEntitySaving)
                {
                    Unregister();
                }

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

                if (_config.EnableEntitySaving)
                {
                    Unregister();

                    var entitiesToKill = _profileStateData.FindAndRemoveValidEntities(Profile.Name);
                    if (entitiesToKill is { Count: > 0 })
                    {
                        Plugin._saveProfileStateDebounced.Schedule();
                        cleanupRoutine = KillEntitiesRoutine(entitiesToKill);
                    }
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
                return _config.EnableEntitySaving
                    ? _profileStateData.FindEntity(Profile.Name, monument, guid) as T
                    : (FindAdapter(guid, monument) as EntityAdapter)?.Entity as T;
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
                    ProfileStatus = ProfileStatus.Loading;
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

                    var monumentAliasOrShortName = entry.Key;
                    var matchingMonuments = Plugin.GetMonumentsByAliasOrShortName(monumentAliasOrShortName);
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
                            controller.SpawnAtMonument(queueItem.Monument);
                            yield return null;
                        }
                    }
                    else
                    {
                        yield return controller.SpawnAtMonumentsRoutine(queueItem.MonumentList);
                    }
                }

                ProfileStatus = ProfileStatus.Loaded;

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

                ProfileStatus = ProfileStatus.Unloaded;
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
                ProfileStatus = ProfileStatus.Loaded;
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
                    if (!Profile.HasEntity(entityEntry.MonumentAliasOrShortName, entityEntry.Guid))
                    {
                        if (entitiesToKill == null)
                        {
                            entitiesToKill = new List<BaseEntity>();
                        }

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

            public ProfileManager(MonumentAddons plugin, OriginalProfileStore originalProfileStore, ProfileStore profileStore)
            {
                _plugin = plugin;
                _originalProfileStore = originalProfileStore;
                _profileStore = profileStore;
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

                    if (_config.EnableEntitySaving)
                    {
                        profileController.Unregister();
                    }
                }

                CoroutineManager.StartGlobalCoroutine(UnloadAllProfilesRoutine());
            }

            public IEnumerator PartialLoadForLateMonumentRoutine(BaseMonument monument)
            {
                foreach (var controller in _profileControllers)
                {
                    if (!controller.IsEnabled)
                        continue;

                    if (!controller.Profile.MonumentDataMap.TryGetValue(monument.AliasOrShortName, out var monumentData))
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
            [JsonProperty("PrefabName")]
            public string PrefabName;

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

            public List<WeightedPrefabData> FindPrefabMatches(string partialName, UniqueNameRegistry uniqueNameRegistry)
            {
                return SearchUtils.FindMatches(
                    Prefabs,
                    prefabData => StringUtils.Contains(prefabData.PrefabName, partialName),
                    prefabData => StringUtils.EqualsCaseInsensitive(prefabData.PrefabName, partialName),
                    prefabData => StringUtils.Contains(uniqueNameRegistry.GetUniqueShortName(prefabData.PrefabName), partialName),
                    prefabData => StringUtils.EqualsCaseInsensitive(uniqueNameRegistry.GetUniqueShortName(prefabData.PrefabName), partialName)
                );
            }
        }

        #endregion

        #region Paste Data

        private class PasteData : BaseTransformData
        {
            [JsonProperty("Filename")]
            public string Filename;
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

            public void SetData(object data)
            {
                PluginData = data as JObject ?? JObject.FromObject(data);
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
                    | MigrateIncorrectPrefabs(data);
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

            public bool HasEntity(string monumentAliasOrShortName, Guid guid)
            {
                return MonumentDataMap.GetOrDefault(monumentAliasOrShortName)?.HasEntity(guid) ?? false;
            }

            public bool HasEntity(string monumentAliasOrShortName, EntityData entityData)
            {
                return MonumentDataMap.GetOrDefault(monumentAliasOrShortName)?.Entities.Contains(entityData) ?? false;
            }

            public bool HasSpawnGroup(string monumentAliasOrShortName, Guid guid)
            {
                return MonumentDataMap.GetOrDefault(monumentAliasOrShortName)?.HasSpawnGroup(guid) ?? false;
            }

            public void AddData(string monumentAliasOrShortName, BaseData data)
            {
                EnsureMonumentData(monumentAliasOrShortName).AddData(data);
            }

            public bool RemoveData(BaseData data, out string monumentAliasOrShortName)
            {
                foreach (var entry in MonumentDataMap)
                {
                    if (entry.Value.RemoveData(data))
                    {
                        monumentAliasOrShortName = entry.Key;
                        return true;
                    }
                }

                monumentAliasOrShortName = null;
                return false;
            }

            private MonumentData EnsureMonumentData(string monumentAliasOrShortName)
            {
                if (!MonumentDataMap.TryGetValue(monumentAliasOrShortName, out var monumentData))
                {
                    monumentData = new MonumentData();
                    MonumentDataMap[monumentAliasOrShortName] = monumentData;
                }

                return monumentData;
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
            public string MonumentAliasOrShortName;
            public Guid Guid;
            public BaseEntity Entity;

            public MonumentEntityEntry(string monumentAliasOrShortName, Guid guid, BaseEntity entity)
            {
                MonumentAliasOrShortName = monumentAliasOrShortName;
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
                return Entities.GetOrDefault(guid) == entityId.Value;
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
                if (monument is DynamicMonument dynamicMonument)
                    return ByEntity.GetOrDefault(dynamicMonument.EntityId.Value);

                return ByLocation.GetOrDefault(monument.Position);
            }

            public MonumentState GetOrCreateMonumentState(BaseMonument monument)
            {
                if (monument is DynamicMonument dynamicMonument)
                    return ByEntity.GetOrCreate(dynamicMonument.EntityId.Value);

                return ByLocation.GetOrCreate(monument.Position);
            }
        }

        private class ProfileState : Dictionary<string, MonumentStateMap>, IDeepCollection
        {
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

                foreach (var entry in this)
                {
                    var monumentAliasOrShortName = entry.Key;
                    var monumentStateMap = entry.Value;

                    if (!monumentStateMap.HasItems())
                        continue;

                    foreach (var entityEntry in monumentStateMap.FindValidEntities())
                    {
                        yield return new MonumentEntityEntry(monumentAliasOrShortName, entityEntry.Item1, entityEntry.Item2);
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
                return ProfileStateMap.GetOrDefault(profileName);
            }

            public bool HasEntity(string profileName, BaseMonument monument, Guid guid, NetworkableId entityId)
            {
                return GetProfileState(profileName)
                    ?.GetOrDefault(monument.AliasOrShortName)
                    ?.GetMonumentState(monument)
                    ?.HasEntity(guid, entityId) ?? false;
            }

            public BaseEntity FindEntity(string profileName, BaseMonument monument, Guid guid)
            {
                return GetProfileState(profileName)
                    ?.GetOrDefault(monument.AliasOrShortName)
                    ?.GetMonumentState(monument)
                    ?.FindEntity(guid);
            }

            public void AddEntity(string profileName, BaseMonument monument, Guid guid, NetworkableId entityId)
            {
                ProfileStateMap.GetOrCreate(profileName)
                    .GetOrCreate(monument.AliasOrShortName)
                    .GetOrCreateMonumentState(monument)
                    .AddEntity(guid, entityId);
            }

            public bool RemoveEntity(string profileName, BaseMonument monument, Guid guid)
            {
                return GetProfileState(profileName)
                    ?.GetOrDefault(monument.AliasOrShortName)
                    ?.GetMonumentState(monument)
                    ?.RemoveEntity(guid) ?? false;
            }

            public List<BaseEntity> FindAndRemoveValidEntities(string profileName)
            {
                var profileState = ProfileStateMap.GetOrDefault(profileName);
                if (profileState == null)
                    return null;

                var entityList = new List<BaseEntity>();

                foreach (var entityEntry in profileState.FindValidEntities())
                {
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

        private class Configuration : BaseConfiguration
        {
            [JsonProperty("Debug", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Debug = false;

            [JsonProperty("EnableDynamicMonuments")]
            [DefaultValue(true)]
            private bool DeprecatedEnableDynamicMonuments { set => EnableDynamicMonuments = value; }

            [JsonProperty("Enable dynamic monuments", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(true)]
            public bool EnableDynamicMonuments = true;

            [JsonProperty("DebugDisplayDistance")]
            private float DeprecatedDebugDisplayDistance { set => DebugDisplayDistance = value; }

            [JsonProperty("Debug display distance")]
            public float DebugDisplayDistance = 150;

            [JsonProperty("PersistEntitiesAfterUnload")]
            private bool DeprecatedEnableEntitySaving { set => EnableEntitySaving = value; }

            [JsonProperty("Persist entities while the plugin is unloaded")]
            public bool EnableEntitySaving;

            [JsonProperty("DeployableOverrides")]
            public Dictionary<string, string> DeprecatedDeployableOverrides { set => DeployableOverrides = value; }

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

            public static readonly LangEntry ErrorNoPermission = new LangEntry("Error.NoPermission", "You don't have permission to do that.");
            public static readonly LangEntry ErrorMonumentFinderNotLoaded = new LangEntry("Error.MonumentFinderNotLoaded", "Error: Monument Finder is not loaded.");
            public static readonly LangEntry ErrorNoMonuments = new LangEntry("Error.NoMonuments", "Error: No monuments found.");
            public static readonly LangEntry ErrorNotAtMonument = new LangEntry("Error.NotAtMonument", "Error: Not at a monument. Nearest is <color=#fd4>{0}</color> with distance <color=#fd4>{1}</color>");
            public static readonly LangEntry ErrorNoSuitableAddonFound = new LangEntry("Error.NoSuitableAddonFound", "Error: No suitable addon found.");
            public static readonly LangEntry ErrorEntityNotEligible = new LangEntry("Error.EntityNotEligible", "Error: That entity is not managed by Monument Addons.");
            public static readonly LangEntry ErrorNoSpawnPointFound = new LangEntry("Error.NoSpawnPointFound", "Error: No spawn point found.");
            public static readonly LangEntry ErrorSetSyntaxGeneric = new LangEntry("Error.Set.Syntax.Generic", "Syntax: <color=#fd4>{0} set <option> <value></color>");
            public static readonly LangEntry ErrorSetSyntax = new LangEntry("Error.Set.Syntax", "Syntax: <color=#fd4>{0} set {1} <value></color>");
            public static readonly LangEntry ErrorSetUnknownOption = new LangEntry("Error.Set.UnknownOption", "Unrecognized option: <color=#fd4>{0}</color>");

            public static readonly LangEntry SpawnErrorSyntax = new LangEntry("Spawn.Error.Syntax", "Syntax: <color=#fd4>maspawn <entity></color>");
            public static readonly LangEntry SpawnErrorNoProfileSelected = new LangEntry("Spawn.Error.NoProfileSelected", "Error: No profile selected. Run <color=#fd4>maprofile help</color> for help.");
            public static readonly LangEntry SpawnErrorEntityNotFound = new LangEntry("Spawn.Error.EntityNotFound2", "Error: No entity found matching name <color=#fd4>{0}</color>.");
            public static readonly LangEntry SpawnErrorEntityOrAddonNotFound = new LangEntry("Spawn.Error.EntityOrCustomNotFound", "Error: No entity or custom addon found matching name <color=#fd4>{0}</color>.");
            public static readonly LangEntry SpawnErrorMultipleMatches = new LangEntry("Spawn.Error.MultipleMatches", "Multiple matches:\n");
            public static readonly LangEntry ErrorNoSurface = new LangEntry("Error.NoSurface", "Error: No valid surface found.");
            public static readonly LangEntry SpawnSuccess = new LangEntry("Spawn.Success2", "Spawned entity at <color=#fd4>{0}</color> matching monument(s) and saved to <color=#fd4>{1}</color> profile for monument <color=#fd4>{2}</color>.");
            public static readonly LangEntry KillSuccess = new LangEntry("Kill.Success4", "Killed <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and removed from profile <color=#fd4>{2}</color>. Run <color=#fd4>maundo</color> to restore it.");
            public static readonly LangEntry SaveNothingToDo = new LangEntry("Save.NothingToDo", "No changes detected for that entity.");
            public static readonly LangEntry SaveSuccess = new LangEntry("Save.Success", "Updated entity at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.");

            public static readonly LangEntry PrefabErrorSyntax = new LangEntry("Prefab.Error.Syntax", "Syntax: <color=#fd4>maprefab <prefab></color>");
            public static readonly LangEntry PrefabErrorIsEntity = new LangEntry("Prefab.Error.IsEntity", "Error: <color=#fd4>{0}</color> is an entity prefab. Use <color=#fd4>maspawn</color> instead of <color=#fd4>maprefab</color>.");
            public static readonly LangEntry PrefabErrorNotFound = new LangEntry("Prefab.Error.NotFound", "Error: No allowed prefab found matching name <color=#fd4>{0}</color>.");
            public static readonly LangEntry PrefabSuccess = new LangEntry("Prefab.Success", "Created prefab instance at <color=#fd4>{0}</color> matching monument(s) and saved to <color=#fd4>{1}</color> profile for monument <color=#fd4>{2}</color>.");

            public static readonly LangEntry UndoNotFound = new LangEntry("Undo.NotFound", "No recent action to undo.");
            public static readonly LangEntry UndoKillSuccess = new LangEntry("Undo.Kill.Success", "Successfully restored <color=#fd4>{0}</color> at monument <color=#fd4>{1}</color> in profile <color=#fd4>{2}</color>.");

            public static readonly LangEntry PasteNotCompatible = new LangEntry("Paste.NotCompatible", "CopyPaste is not loaded or its version is incompatible.");
            public static readonly LangEntry PasteSyntax = new LangEntry("Paste.Syntax", "Syntax: <color=#fd4>mapaste <file></color>");
            public static readonly LangEntry PasteNotFound = new LangEntry("Paste.NotFound", "File <color=#fd4>{0}</color> does not exist.");
            public static readonly LangEntry PasteSuccess = new LangEntry("Paste.Success", "Pasted <color=#fd4>{0}</color> at <color=#fd4>{1}</color> (x<color=#fd4>{2}</color>) and saved to profile <color=#fd4>{3}</color>.");

            public static readonly LangEntry AddonTypeUnknown = new LangEntry("AddonType.Unknown", "Addon");
            public static readonly LangEntry AddonTypeEntity = new LangEntry("AddonType.Entity", "Entity");
            public static readonly LangEntry AddonTypePrefab = new LangEntry("AddonType.Prefab", "Prefab");
            public static readonly LangEntry AddonTypeSpawnPoint = new LangEntry("AddonType.SpawnPoint", "Spawn point");
            public static readonly LangEntry AddonTypePaste = new LangEntry("AddonType.Paste", "Paste");
            public static readonly LangEntry AddonTypeCustom = new LangEntry("AddonType.Custom", "Custom");

            public static readonly LangEntry SpawnGroupCreateSyntax = new LangEntry("SpawnGroup.Create.Syntax", "Syntax: <color=#fd4>{0} create <name></color>");
            public static readonly LangEntry SpawnGroupCreateNameInUse = new LangEntry("SpawnGroup.Create.NameInUse", "There is already a spawn group named <color=#fd4>{0}</color> at monument <color=#fd4>{1}</color> in profile <color=#fd4>{2}</color>. Please use a different name.");
            public static readonly LangEntry SpawnGroupCreateSucces = new LangEntry("SpawnGroup.Create.Success", "Successfully created spawn group <color=#fd4>{0}</color>.");
            public static readonly LangEntry SpawnGroupSetSuccess = new LangEntry("SpawnGroup.Set.Success", "Successfully updated spawn group <color=#fd4>{0}</color> with option <color=#fd4>{1}</color>: <color=#fd4>{2}</color>.");
            public static readonly LangEntry SpawnGroupAddSyntax = new LangEntry("SpawnGroup.Add.Syntax", "Syntax: <color=#fd4>{0} add <entity> <weight></color>");
            public static readonly LangEntry SpawnGroupAddSuccess = new LangEntry("SpawnGroup.Add.Success", "Successfully added entity <color=#fd4>{0}</color> with weight <color=#fd4>{1}</color> to spawn group <color=#fd4>{2}</color>.");
            public static readonly LangEntry SpawnGroupRemoveSyntax = new LangEntry("SpawnGroup.Remove.Syntax2", "Syntax: <color=#fd4>{0} remove <entity></color>");
            public static readonly LangEntry SpawnGroupRemoveMultipleMatches = new LangEntry("SpawnGroup.Remove.MultipleMatches", "Multiple entities in spawn group <color=#fd4>{0}</color> found matching: <color=#fd4>{1}</color>. Please be more specific.");
            public static readonly LangEntry SpawnGroupRemoveNoMatch = new LangEntry("SpawnGroup.Remove.NoMatch", "No entity found in spawn group <color=#fd4>{0}</color> matching <color=#fd4>{1}</color>");
            public static readonly LangEntry SpawnGroupRemoveSuccess = new LangEntry("SpawnGroup.Remove.Success", "Successfully removed entity <color=#fd4>{0}</color> from spawn group <color=#fd4>{1}</color>.");

            public static readonly LangEntry SpawnGroupNotFound = new LangEntry("SpawnGroup.NotFound", "No spawn group found with name: <color=#fd4>{0}</color>");
            public static readonly LangEntry SpawnGroupMultipeMatches = new LangEntry("SpawnGroup.MultipeMatches2", "Multiple spawn groups found matching name: <color=#fd4>{0}</color>");
            public static readonly LangEntry SpawnPointCreateSyntax = new LangEntry("SpawnPoint.Create.Syntax", "Syntax: <color=#fd4>{0} create <group_name></color>");
            public static readonly LangEntry SpawnPointCreateSuccess = new LangEntry("SpawnPoint.Create.Success", "Successfully added spawn point to spawn group <color=#fd4>{0}</color>.");
            public static readonly LangEntry SpawnPointSetSuccess = new LangEntry("SpawnPoint.Set.Success", "Successfully updated spawn point with option <color=#fd4>{0}</color>: <color=#fd4>{1}</color>.");

            public static readonly LangEntry SpawnGroupHelpHeader = new LangEntry("SpawnGroup.Help.Header", "<size=18>Monument Addons Spawn Group Commands</size>");
            public static readonly LangEntry SpawnGroupHelpCreate = new LangEntry("SpawnGroup.Help.Create", "<color=#fd4>{0} create <name></color> - Create a spawn group with a spawn point");
            public static readonly LangEntry SpawnGroupHelpSet = new LangEntry("SpawnGroup.Help.Set", "<color=#fd4>{0} set <option> <value></color> - Set a property of a spawn group");
            public static readonly LangEntry SpawnGroupHelpAdd = new LangEntry("SpawnGroup.Help.Add", "<color=#fd4>{0} add <entity> <weight></color> - Add an entity prefab to a spawn group");
            public static readonly LangEntry SpawnGroupHelpRemove = new LangEntry("SpawnGroup.Help.Remove", "<color=#fd4>{0} remove <entity> <weight></color> - Remove an entity prefab from a spawn group");
            public static readonly LangEntry SpawnGroupHelpSpawn = new LangEntry("SpawnGroup.Help.Spawn", "<color=#fd4>{0} spawn</color> - Run one spawn tick for a spawn group");
            public static readonly LangEntry SpawnGroupHelpRespawn = new LangEntry("SpawnGroup.Help.Respawn", "<color=#fd4>{0} respawn</color> - Despawn entities for a spawn group and run one spawn tick");

            public static readonly LangEntry SpawnPointHelpHeader = new LangEntry("SpawnPoint.Help.Header", "<size=18>Monument Addons Spawn Point Commands</size>");
            public static readonly LangEntry SpawnPointHelpCreate = new LangEntry("SpawnPoint.Help.Create", "<color=#fd4>{0} create <group_name></color> - Create a spawn point");
            public static readonly LangEntry SpawnPointHelpSet = new LangEntry("SpawnPoint.Help.Set", "<color=#fd4>{0} set <option> <value></color> - Set a property of a spawn point");

            public static readonly LangEntry SpawnGroupSetHelpName = new LangEntry("SpawnGroup.Set.Help.Name", "<color=#fd4>Name</color>: string");
            public static readonly LangEntry SpawnGroupSetHelpMaxPopulation = new LangEntry("SpawnGroup.Set.Help.MaxPopulation", "<color=#fd4>MaxPopulation</color>: number");
            public static readonly LangEntry SpawnGroupSetHelpRespawnDelayMin = new LangEntry("SpawnGroup.Set.Help.RespawnDelayMin", "<color=#fd4>RespawnDelayMin</color>: number");
            public static readonly LangEntry SpawnGroupSetHelpRespawnDelayMax = new LangEntry("SpawnGroup.Set.Help.RespawnDelayMax", "<color=#fd4>RespawnDelayMax</color>: number");
            public static readonly LangEntry SpawnGroupSetHelpSpawnPerTickMin = new LangEntry("SpawnGroup.Set.Help.SpawnPerTickMin", "<color=#fd4>SpawnPerTickMin</color>: number");
            public static readonly LangEntry SpawnGroupSetHelpSpawnPerTickMax = new LangEntry("SpawnGroup.Set.Help.SpawnPerTickMax", "<color=#fd4>SpawnPerTickMax</color>: number");
            public static readonly LangEntry SpawnGroupSetHelpInitialSpawn = new LangEntry("SpawnGroup.Set.Help.InitialSpawn", "<color=#fd4>InitialSpawn</color>: true | false");
            public static readonly LangEntry SpawnGroupSetHelpPreventDuplicates = new LangEntry("SpawnGroup.Set.Help.PreventDuplicates", "<color=#fd4>PreventDuplicates</color>: true | false");
            public static readonly LangEntry SpawnGroupSetHelpPauseScheduleWhileFull = new LangEntry("SpawnGroup.Set.Help.PauseScheduleWhileFull","<color=#fd4>PauseScheduleWhileFull</color>: true | false");
            public static readonly LangEntry SpawnGroupSetHelpRespawnWhenNearestPuzzleResets = new LangEntry("SpawnGroup.Set.Help.RespawnWhenNearestPuzzleResets","<color=#fd4>RespawnWhenNearestPuzzleResets</color>: true | false");

            public static readonly LangEntry SpawnPointSetHelpExclusive = new LangEntry("SpawnPoint.Set.Help.Exclusive", "<color=#fd4>Exclusive</color>: true | false");
            public static readonly LangEntry SpawnPointSetHelpSnapToGround = new LangEntry("SpawnPoint.Set.Help.SnapToGround", "<color=#fd4>SnapToGround</color>: true | false");
            public static readonly LangEntry SpawnPointSetHelpCheckSpace = new LangEntry("SpawnPoint.Set.Help.CheckSpace", "<color=#fd4>CheckSpace</color>: true | false");
            public static readonly LangEntry SpawnPointSetHelpRandomRotation = new LangEntry("SpawnPoint.Set.Help.RandomRotation", "<color=#fd4>RandomRotation</color>: true | false");
            public static readonly LangEntry SpawnPointSetHelpRandomRadius = new LangEntry("SpawnPoint.Set.Help.RandomRadius", "<color=#fd4>RandomRadius</color>: number");
            public static readonly LangEntry SpawnPointSetHelpPlayerDetectionRadius = new LangEntry("SpawnPoint.Set.Help.PlayerDetectionRadius", "<color=#fd4>PlayerDetectionRadius</color>: number");

            public static readonly LangEntry PuzzleAddSpawnGroupSyntax = new LangEntry("Puzzle.AddSpawnGroup.Syntax", "Syntax: <color=#fd4>{0} add <group_name></color>");
            public static readonly LangEntry PuzzleAddSpawnGroupSuccess = new LangEntry("Puzzle.AddSpawnGroup.Success", "Successfully added spawn group <color=#fd4>{0}</color> to puzzle.");
            public static readonly LangEntry PuzzleRemoveSpawnGroupSyntax = new LangEntry("Puzzle.RemoveSpawnGroup.Syntax", "Syntax: <color=#fd4>{0} remove <group_name></color>");
            public static readonly LangEntry PuzzleRemoveSpawnGroupSuccess = new LangEntry("Puzzle.RemoveSpawnGroup.Success", "Successfully removed spawn group <color=#fd4>{0}</color> from puzzle.");
            public static readonly LangEntry PuzzleNotPresent = new LangEntry("Puzzle.Error.NotPresent", "That is not a puzzle entity.");
            public static readonly LangEntry PuzzleNotConnected = new LangEntry("Puzzle.Error.NotConnected", "Entity <color=#fd4>{0}</color> is not connected to a puzzle.");
            public static readonly LangEntry PuzzleResetSuccess = new LangEntry("Puzzle.Reset.Success", "Puzzle successfully reset.");
            public static readonly LangEntry PuzzleSetSuccess = new LangEntry("Puzzle.Set.Success", "Successfully updated puzzle with option <color=#fd4>{0}</color>: <color=#fd4>{1}</color>.");

            public static readonly LangEntry PuzzleHelpHeader = new LangEntry("Puzzle.Help.Header", "<size=18>Monument Addons Puzzle Commands</size>");
            public static readonly LangEntry PuzzleHelpReset = new LangEntry("Puzzle.Help.Reset", "<color=#fd4>{0} reset</color> - Reset the puzzle connected to the entity you are looking at");
            public static readonly LangEntry PuzzleHelpSet = new LangEntry("Puzzle.Help.Set", "<color=#fd4>{0} set <option> <value></color> - Set a property of a puzzle");
            public static readonly LangEntry PuzzleHelpAdd = new LangEntry("Puzzle.Help.Add", "<color=#fd4>{0} add <group_name></color> - Associate a spawn group with a puzzle");
            public static readonly LangEntry PuzzleHelpRemove = new LangEntry("Puzzle.Help.Remove", "<color=#fd4>{0} remove <group_name></color> - Disassociate a spawn group with a puzzle");

            public static readonly LangEntry PuzzleSetHelpMaxPlayersBlockReset = new LangEntry("Puzzle.Set.Help.MaxPlayersBlockReset", "<color=#fd4>PlayersBlockReset</color>: true | false");
            public static readonly LangEntry PuzzleSetHelpPlayerDetectionRadius = new LangEntry("Puzzle.Set.Help.PlayerDetectionRadius", "<color=#fd4>PlayerDetectionRadius</color>: number");
            public static readonly LangEntry PuzzleSetHelpSecondsBetweenResets = new LangEntry("Puzzle.Set.Help.SecondsBetweenResets", "<color=#fd4>SecondsBetweenResets</color>: number");

            public static readonly LangEntry ShowVanillaNoSpawnPoints = new LangEntry("Show.Vanilla.NoSpawnPoints", "No spawn points found in <color=#fd4>{0}</color>.");
            public static readonly LangEntry GenerateSuccess = new LangEntry("Generate.Success", "Successfully generated profile <color=#fd4>{0}</color>.");

            public static readonly LangEntry ShowSuccess = new LangEntry("Show.Success", "Showing nearby Monument Addons for <color=#fd4>{0}</color>.");
            public static readonly LangEntry ShowLabelPlugin = new LangEntry("Show.Label.Plugin", "Plugin: {0}");
            public static readonly LangEntry ShowLabelProfile = new LangEntry("Show.Label.Profile", "Profile: {0}");
            public static readonly LangEntry ShowLabelMonument = new LangEntry("Show.Label.Monument", "Monument: {0} (x{1})");
            public static readonly LangEntry ShowLabelMonumentWithTier = new LangEntry("Show.Label.MonumentWithTier", "Monument: {0} (x{1} | {2})");
            public static readonly LangEntry ShowLabelSkin = new LangEntry("Show.Label.Skin", "Skin: {0}");
            public static readonly LangEntry ShowLabelScale = new LangEntry("Show.Label.Scale", "Scale: {0}");
            public static readonly LangEntry ShowLabelRCIdentifier = new LangEntry("Show.Label.RCIdentifier", "RC Identifier: {0}");

            public static readonly LangEntry ShowHeaderEntity = new LangEntry("Show.Header.Entity", "Entity: {0}");
            public static readonly LangEntry ShowHeaderPrefab = new LangEntry("Show.Header.Prefab", "Prefab: {0}");
            public static readonly LangEntry ShowHeaderPuzzle = new LangEntry("Show.Header.Puzzle", "Puzzle");
            public static readonly LangEntry ShowHeaderSpawnGroup = new LangEntry("Show.Header.SpawnGroup", "Spawn Group: {0}");
            public static readonly LangEntry ShowHeaderVanillaSpawnGroup = new LangEntry("Show.Header.Vanilla.SpawnGroup", "Vanilla Spawn Group: {0}");
            public static readonly LangEntry ShowHeaderSpawnPoint = new LangEntry("Show.Header.SpawnPoint", "Spawn Point ({0})");
            public static readonly LangEntry ShowHeaderVanillaSpawnPoint = new LangEntry("Show.Header.Vanilla.SpawnPoint", "Vanilla Spawn Point ({0})");
            public static readonly LangEntry ShowHeaderVanillaIndividualSpawnPoint = new LangEntry("Show.Header.Vanilla.IndividualSpawnPoint", "Vanilla Individual Spawn Point: {0}");
            public static readonly LangEntry ShowHeaderPaste = new LangEntry("Show.Header.Paste", "Paste: {0}");
            public static readonly LangEntry ShowHeaderCustom = new LangEntry("Show.Header.Custom", "Custom Addon: {0}");

            public static readonly LangEntry ShowLabelFlags = new LangEntry("Show.Label.SpawnPoint.Flags", "Flags: {0}");
            public static readonly LangEntry ShowLabelSpawnPointExclusive = new LangEntry("Show.Label.SpawnPoint.Exclusive", "Exclusive");
            public static readonly LangEntry ShowLabelSpawnPointRandomRotation = new LangEntry("Show.Label.SpawnPoint.RandomRotation2", "RandomRotation");
            public static readonly LangEntry ShowLabelSpawnPointSnapToGround = new LangEntry("Show.Label.SpawnPoint.SnapToGround", "SnapToGround");
            public static readonly LangEntry ShowLabelSpawnPointCheckSpace = new LangEntry("Show.Label.SpawnPoint.CheckSpace", "CheckSpace");
            public static readonly LangEntry ShowLabelSpawnPointRandomRadius = new LangEntry("Show.Label.SpawnPoint.RandomRadius", "Random spawn radius: {0:f1}");

            public static readonly LangEntry ShowLabelSpawnPoints = new LangEntry("Show.Label.Points", "Spawn points: {0}");
            public static readonly LangEntry ShowLabelTiers = new LangEntry("Show.Label.Tiers", "Tiers: {0}");
            public static readonly LangEntry ShowLabelSpawnWhenParentSpawns = new LangEntry("Show.Label.SpawnWhenParentSpawns", "Spawn when parent spawns");
            public static readonly LangEntry ShowLabelSpawnOnServerStart = new LangEntry("Show.Label.SpawnOnServerStart", "Spawn on server start");
            public static readonly LangEntry ShowLabelSpawnOnMapWipe = new LangEntry("Show.Label.SpawnOnMapWipe", "Spawn on map wipe");
            public static readonly LangEntry ShowLabelInitialSpawn = new LangEntry("Show.Label.InitialSpawn", "InitialSpawn");
            public static readonly LangEntry ShowLabelPreventDuplicates = new LangEntry("Show.Label.PreventDuplicates2", "PreventDuplicates");
            public static readonly LangEntry ShowLabelPauseScheduleWhileFull = new LangEntry("Show.Label.PauseScheduleWhileFull", "PauseScheduleWhileFull");
            public static readonly LangEntry ShowLabelRespawnWhenNearestPuzzleResets = new LangEntry("Show.Label.RespawnWhenNearestPuzzleResets", "RespawnWhenNearestPuzzleResets");
            public static readonly LangEntry ShowLabelPopulation = new LangEntry("Show.Label.Population", "Population: {0} / {1}");
            public static readonly LangEntry ShowLabelRespawnPerTick = new LangEntry("Show.Label.RespawnPerTick", "Spawn per tick: {0} - {1}");
            public static readonly LangEntry ShowLabelRespawnDelay = new LangEntry("Show.Label.RespawnDelay", "Respawn delay: {0} - {1}");
            public static readonly LangEntry ShowLabelNextSpawn = new LangEntry("Show.Label.NextSpawn", "Next spawn: {0}");
            public static readonly LangEntry ShowLabelNextSpawnQueued = new LangEntry("Show.Label.NextSpawn.Queued", "Queued");
            public static readonly LangEntry ShowLabelNextSpawnPaused = new LangEntry("Show.Label.NextSpawn.Paused", "Paused");
            public static readonly LangEntry ShowLabelEntities = new LangEntry("Show.Label.Entities", "Entities:");
            public static readonly LangEntry ShowLabelEntityDetail = new LangEntry("Show.Label.Entities.Detail2", "{0} | weight: {1} ({2:P1})");
            public static readonly LangEntry ShowLabelNoEntities = new LangEntry("Show.Label.NoEntities", "No entities configured. Run /maspawngroup add <entity> <weight>");
            public static readonly LangEntry ShowLabelPlayerDetectionRadius = new LangEntry("Show.Label.PlayerDetectionRadius", "Player detection radius: {0:f1}");
            public static readonly LangEntry ShowLabelPlayerDetectedInRadius = new LangEntry("Show.Label.PlayerDetectedInRadius", "(!) Player detected in radius (!)");

            public static readonly LangEntry ShowLabelPuzzlePlayersBlockReset = new LangEntry("Show.Label.Puzzle.PlayersBlockReset", "Players block reset progress: {0}");
            public static readonly LangEntry ShowLabelPuzzleTimeBetweenResets = new LangEntry("Show.Label.Puzzle.TimeBetweenResets", "Time between resets: {0}");
            public static readonly LangEntry ShowLabelPuzzleNextReset = new LangEntry("Show.Label.Puzzle.NextReset", "Time until next reset: {0}");
            public static readonly LangEntry ShowLabelPuzzleNextResetOverdue = new LangEntry("Show.Label.Puzzle.NextReset.Overdue", "Any moment now");
            public static readonly LangEntry ShowLabelPuzzleSpawnGroups = new LangEntry("Show.Label.Puzzle.SpawnGroups", "Resets spawn groups: {0}");

            public static readonly LangEntry SkinGet = new LangEntry("Skin.Get", "Skin ID: <color=#fd4>{0}</color>. Run <color=#fd4>{1} <skin id></color> to change it.");
            public static readonly LangEntry SkinSetSyntax = new LangEntry("Skin.Set.Syntax", "Syntax: <color=#fd4>{0} <skin id></color>");
            public static readonly LangEntry SkinSetSuccess = new LangEntry("Skin.Set.Success2", "Updated skin ID to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.");
            public static readonly LangEntry SkinErrorRedirect = new LangEntry("Skin.Error.Redirect", "Error: Skin <color=#fd4>{0}</color> is a redirect skin and cannot be set directly. Instead, spawn the entity as <color=#fd4>{1}</color>.");

            public static readonly LangEntry CCTVSetIdSyntax = new LangEntry("CCTV.SetId.Error.Syntax", "Syntax: <color=#fd4>{0} <id></color>");
            public static readonly LangEntry CCTVSetIdSuccess = new LangEntry("CCTV.SetId.Success2", "Updated CCTV id to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.");
            public static readonly LangEntry CCTVSetDirectionSuccess = new LangEntry("CCTV.SetDirection.Success2", "Updated CCTV direction at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.");

            public static readonly LangEntry SkullNameSyntax = new LangEntry("SkullName.Syntax", "Syntax: <color=#fd4>{0} <name></color>");
            public static readonly LangEntry SkullNameSetSuccess = new LangEntry("SkullName.Set.Success", "Updated skull name to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.");

            public static readonly LangEntry SetHeadNoHeadItem = new LangEntry("Head.Set.NoHeadItem", "Error: You must be holding a head bag item to do that.");
            public static readonly LangEntry SetHeadMismatch = new LangEntry("Head.Set.Mismatch", "Error: That is the wrong type of head for that trophy.");
            public static readonly LangEntry SetHeadSuccess = new LangEntry("Head.Set.Success", "Updated head trophy according to your equipped item at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.");

            public static readonly LangEntry CardReaderSetLevelSyntax = new LangEntry("CardReader.SetLevel.Error.Syntax", "Syntax: <color=#fd4>{0} <1-3></color>");
            public static readonly LangEntry CardReaderSetLevelSuccess = new LangEntry("CardReader.SetLevel.Success", "Updated card reader access level to <color=#fd4>{0}</color>.");

            public static readonly LangEntry ProfileListEmpty = new LangEntry("Profile.List.Empty", "You have no profiles. Create one with <color=#fd4>maprofile create <name></maprofile>");
            public static readonly LangEntry ProfileListHeader = new LangEntry("Profile.List.Header", "<size=18>Monument Addons Profiles</size>");
            public static readonly LangEntry ProfileListItemEnabled = new LangEntry("Profile.List.Item.Enabled2", "<color=#fd4>{0}</color>{1} - <color=#6e6>ENABLED</color>");
            public static readonly LangEntry ProfileListItemDisabled = new LangEntry("Profile.List.Item.Disabled2", "<color=#fd4>{0}</color>{1} - <color=#ccc>DISABLED</color>");
            public static readonly LangEntry ProfileListItemSelected = new LangEntry("Profile.List.Item.Selected2", "<color=#fd4>{0}</color>{1} - <color=#6cf>SELECTED</color>");
            public static readonly LangEntry ProfileByAuthor = new LangEntry("Profile.ByAuthor", " by {0}");

            public static readonly LangEntry ProfileInstallSyntax = new LangEntry("Profile.Install.Syntax", "Syntax: <color=#fd4>maprofile install <url></color>");
            public static readonly LangEntry ProfileInstallShorthandSyntax = new LangEntry("Profile.Install.Shorthand.Syntax", "Syntax: <color=#fd4>mainstall <url></color>");
            public static readonly LangEntry ProfileUrlInvalid = new LangEntry("Profile.Url.Invalid", "Invalid URL: {0}");
            public static readonly LangEntry ProfileAlreadyExistsNotEmpty = new LangEntry("Profile.Error.AlreadyExists.NotEmpty", "Error: Profile <color=#fd4>{0}</color> already exists and is not empty.");
            public static readonly LangEntry ProfileInstallSuccess = new LangEntry("Profile.Install.Success2", "Successfully installed and <color=#6e6>ENABLED</color> profile <color=#fd4>{0}</color>{1}.");
            public static readonly LangEntry ProfileInstallError = new LangEntry("Profile.Install.Error", "Error installing profile from url {0}. See the error logs for more details.");
            public static readonly LangEntry ProfileDownloadError = new LangEntry("Profile.Download.Error", "Error downloading profile from url {0}\nStatus code: {1}");
            public static readonly LangEntry ProfileParseError = new LangEntry("Profile.Parse.Error", "Error parsing profile from url {0}\n{1}");

            public static readonly LangEntry ProfileDescribeSyntax = new LangEntry("Profile.Describe.Syntax", "Syntax: <color=#fd4>maprofile describe <name></color>");
            public static readonly LangEntry ProfileNotFound = new LangEntry("Profile.Error.NotFound", "Error: Profile <color=#fd4>{0}</color> not found.");
            public static readonly LangEntry ProfileEmpty = new LangEntry("Profile.Empty", "Profile <color=#fd4>{0}</color> is empty.");
            public static readonly LangEntry ProfileDescribeHeader = new LangEntry("Profile.Describe.Header", "Describing profile <color=#fd4>{0}</color>.");
            public static readonly LangEntry ProfileDescribeItem = new LangEntry("Profile.Describe.Item2", "{0}: <color=#fd4>{1}</color> x{2} @ {3}");
            public static readonly LangEntry ProfileSelectSyntax = new LangEntry("Profile.Select.Syntax", "Syntax: <color=#fd4>maprofile select <name></color>");
            public static readonly LangEntry ProfileSelectSuccess = new LangEntry("Profile.Select.Success2", "Successfully <color=#6cf>SELECTED</color> profile <color=#fd4>{0}</color>.");
            public static readonly LangEntry ProfileSelectEnableSuccess = new LangEntry("Profile.Select.Enable.Success", "Successfully <color=#6cf>SELECTED</color> and <color=#6e6>ENABLED</color> profile <color=#fd4>{0}</color>.");

            public static readonly LangEntry ProfileEnableSyntax = new LangEntry("Profile.Enable.Syntax", "Syntax: <color=#fd4>maprofile enable <name></color>");
            public static readonly LangEntry ProfileAlreadyEnabled = new LangEntry("Profile.AlreadyEnabled", "Profile <color=#fd4>{0}</color> is already <color=#6e6>ENABLED</color>.");
            public static readonly LangEntry ProfileEnableSuccess = new LangEntry("Profile.Enable.Success", "Profile <color=#fd4>{0}</color> is now: <color=#6e6>ENABLED</color>.");
            public static readonly LangEntry ProfileDisableSyntax = new LangEntry("Profile.Disable.Syntax", "Syntax: <color=#fd4>maprofile disable <name></color>");
            public static readonly LangEntry ProfileAlreadyDisabled = new LangEntry("Profile.AlreadyDisabled2", "Profile <color=#fd4>{0}</color> is already <color=#ccc>DISABLED</color>.");
            public static readonly LangEntry ProfileDisableSuccess = new LangEntry("Profile.Disable.Success2", "Profile <color=#fd4>{0}</color> is now: <color=#ccc>DISABLED</color>.");
            public static readonly LangEntry ProfileReloadSyntax = new LangEntry("Profile.Reload.Syntax", "Syntax: <color=#fd4>maprofile reload <name></color>");
            public static readonly LangEntry ProfileNotEnabled = new LangEntry("Profile.NotEnabled", "Error: Profile <color=#fd4>{0}</color> is not enabled.");
            public static readonly LangEntry ProfileReloadSuccess = new LangEntry("Profile.Reload.Success", "Reloaded profile <color=#fd4>{0}</color>.");

            public static readonly LangEntry ProfileCreateSyntax = new LangEntry("Profile.Create.Syntax", "Syntax: <color=#fd4>maprofile create <name></color>");
            public static readonly LangEntry ProfileAlreadyExists = new LangEntry("Profile.Error.AlreadyExists", "Error: Profile <color=#fd4>{0}</color> already exists.");
            public static readonly LangEntry ProfileCreateSuccess = new LangEntry("Profile.Create.Success", "Successfully created and <color=#6cf>SELECTED</color> profile <color=#fd4>{0}</color>.");
            public static readonly LangEntry ProfileRenameSyntax = new LangEntry("Profile.Rename.Syntax", "Syntax: <color=#fd4>maprofile rename <old name> <new name></color>");
            public static readonly LangEntry ProfileRenameSuccess = new LangEntry("Profile.Rename.Success2", "Successfully renamed profile <color=#fd4>{0}</color> to <color=#fd4>{1}</color>");
            public static readonly LangEntry ProfileClearSyntax = new LangEntry("Profile.Clear.Syntax", "Syntax: <color=#fd4>maprofile clear <name></color>");
            public static readonly LangEntry ProfileClearSuccess = new LangEntry("Profile.Clear.Success", "Successfully cleared profile <color=#fd4>{0}</color>.");
            public static readonly LangEntry ProfileDeleteSyntax = new LangEntry("Profile.Delete.Syntax", "Syntax: <color=#fd4>maprofile delete <name></color>");
            public static readonly LangEntry ProfileDeleteBlocked = new LangEntry("Profile.Delete.Blocked", "Profile <color=#fd4>{0}</color> must be empty or disabled before it can be deleted.");
            public static readonly LangEntry ProfileDeleteSuccess = new LangEntry("Profile.Delete.Success", "Successfully deleted profile <color=#fd4>{0}</color>.");

            public static readonly LangEntry ProfileMoveToSyntax = new LangEntry("Profile.MoveTo.Syntax", "Syntax: <color=#fd4>maprofile moveto <name></color>");
            public static readonly LangEntry ProfileMoveToAlreadyPresent = new LangEntry("Profile.MoveTo.AlreadyPresent", "Error: <color=#fd4>{0}</color> is already part of profile <color=#fd4>{1}</color>.");
            public static readonly LangEntry ProfileMoveToSuccess = new LangEntry("Profile.MoveTo.Success", "Successfully moved <color=#fd4>{0}</color> from profile <color=#fd4>{1}</color> to <color=#fd4>{2}</color>.");

            public static readonly LangEntry ProfileHelpHeader = new LangEntry("Profile.Help.Header", "<size=18>Monument Addons Profile Commands</size>");
            public static readonly LangEntry ProfileHelpList = new LangEntry("Profile.Help.List", "<color=#fd4>maprofile list</color> - List all profiles");
            public static readonly LangEntry ProfileHelpDescribe = new LangEntry("Profile.Help.Describe", "<color=#fd4>maprofile describe <name></color> - Describe profile contents");
            public static readonly LangEntry ProfileHelpEnable = new LangEntry("Profile.Help.Enable", "<color=#fd4>maprofile enable <name></color> - Enable a profile");
            public static readonly LangEntry ProfileHelpDisable = new LangEntry("Profile.Help.Disable", "<color=#fd4>maprofile disable <name></color> - Disable a profile");
            public static readonly LangEntry ProfileHelpReload = new LangEntry("Profile.Help.Reload", "<color=#fd4>maprofile reload <name></color> - Reload a profile from disk");
            public static readonly LangEntry ProfileHelpSelect = new LangEntry("Profile.Help.Select", "<color=#fd4>maprofile select <name></color> - Select a profile");
            public static readonly LangEntry ProfileHelpCreate = new LangEntry("Profile.Help.Create", "<color=#fd4>maprofile create <name></color> - Create a new profile");
            public static readonly LangEntry ProfileHelpRename = new LangEntry("Profile.Help.Rename", "<color=#fd4>maprofile rename <name> <new name></color> - Rename a profile");
            public static readonly LangEntry ProfileHelpClear = new LangEntry("Profile.Help.Clear2", "<color=#fd4>maprofile clear <name></color> - Clear a profile");
            public static readonly LangEntry ProfileHelpDelete = new LangEntry("Profile.Help.Delete", "<color=#fd4>maprofile delete <name></color> - Delete a profile");
            public static readonly LangEntry ProfileHelpMoveTo = new LangEntry("Profile.Help.MoveTo2", "<color=#fd4>maprofile moveto <name></color> - Move an entity to a profile");
            public static readonly LangEntry ProfileHelpInstall = new LangEntry("Profile.Help.Install", "<color=#fd4>maprofile install <url></color> - Install a profile from a URL");

            public static readonly LangEntry WireToolInvisible = new LangEntry("WireTool.Invisible", "Invisible");
            public static readonly LangEntry WireToolInvalidColor = new LangEntry("WireTool.Error.InvalidColor", "Invalid wire color: <color=#fd4>{0}</color>.");
            public static readonly LangEntry WireToolNotEquipped = new LangEntry("WireTool.Error.NotEquipped", "Error: No Wire Tool or Hose Tool equipped.");
            public static readonly LangEntry WireToolActivated = new LangEntry("WireTool.Activated", "Monument Addons Wire Tool activated with color <color=#fd4>{0}</color>.");
            public static readonly LangEntry WireToolDeactivated = new LangEntry("WireTool.Deactivated", "Monument Addons Wire Tool deactivated.");
            public static readonly LangEntry WireToolTypeMismatch = new LangEntry("WireTool.TypeMismatch", "Error: You can only connect slots of the same type. Looking for <color=#fd4>{0}</color>, but found <color=#fd4>{1}</color>.");
            public static readonly LangEntry WireToolProfileMismatch = new LangEntry("WireTool.ProfileMismatch", "Error: You can only connect entities in the same profile. Looking for <color=#fd4>{0}</color>, but found <color=#fd4>{1}</color>.");
            public static readonly LangEntry WireToolMonumentMismatch = new LangEntry("WireTool.MonumentMismatch", "Error: You can only connect entities at the same monument.");

            public static readonly LangEntry HelpHeader = new LangEntry("Help.Header", "<size=18>Monument Addons Help</size>");
            public static readonly LangEntry HelpSpawn = new LangEntry("Help.Spawn", "<color=#fd4>maspawn <entity></color> - Spawn an entity");
            public static readonly LangEntry HelpPrefab = new LangEntry("Help.Prefab", "<color=#fd4>maprefab <prefab></color> - Create a non-entity prefab instance");
            public static readonly LangEntry HelpKill = new LangEntry("Help.Kill", "<color=#fd4>makill</color> - Delete an entity or other addon");
            public static readonly LangEntry HelpUndo = new LangEntry("Help.Undo", "<color=#fd4>maundo</color> - Undo a recent <color=#fd4>makill</color> action");
            public static readonly LangEntry HelpSave = new LangEntry("Help.Save", "<color=#fd4>masave</color> - Save an entity's updated position");
            public static readonly LangEntry HelpSkin = new LangEntry("Help.Skin", "<color=#fd4>maskin <skin id></color> - Change the skin of an entity");
            public static readonly LangEntry HelpSetId = new LangEntry("Help.SetId", "<color=#fd4>masetid <id></color> - Set the id of a CCTV");
            public static readonly LangEntry HelpSetDir = new LangEntry("Help.SetDir", "<color=#fd4>masetdir</color> - Set the direction of a CCTV");
            public static readonly LangEntry HelpSkull = new LangEntry("Help.Skull", "<color=#fd4>maskull <name></color> - Set skull trophy display name");
            public static readonly LangEntry HelpTrophy = new LangEntry("Help.Trophy", "<color=#fd4>matrophy <name></color> - Update a hunting trophy");
            public static readonly LangEntry HelpCardReaderLevel = new LangEntry("Help.CardReaderLevel", "<color=#fd4>macardlevel <1-3></color> - Set a card reader's access level");
            public static readonly LangEntry HelpPuzzle = new LangEntry("Help.Puzzle", "<color=#fd4>mapuzzle</color> - Print puzzle help");
            public static readonly LangEntry HelpSpawnGroup = new LangEntry("Help.SpawnGroup", "<color=#fd4>maspawngroup</color> - Print spawn group help");
            public static readonly LangEntry HelpSpawnPoint = new LangEntry("Help.SpawnPoint", "<color=#fd4>maspawnpoint</color> - Print spawn point help");
            public static readonly LangEntry HelpPaste = new LangEntry("Help.Paste", "<color=#fd4>mapaste <file></color> - Paste a building");
            public static readonly LangEntry HelpShow = new LangEntry("Help.Show", "<color=#fd4>mashow</color> - Show nearby addons");
            public static readonly LangEntry HelpShowVanilla = new LangEntry("Help.ShowVanilla", "<color=#fd4>mashowvanilla</color> - Show vanilla spawn points");
            public static readonly LangEntry HelpProfile = new LangEntry("Help.Profile", "<color=#fd4>maprofile</color> - Print profile help");

            public string Name;
            public string English;

            public LangEntry(string name, string english)
            {
                Name = name;
                English = english;

                AllLangEntries.Add(this);
            }
        }

        // Multi-argument overloads are defined to reduce array allocations.
        private string GetMessage(string playerId, LangEntry langEntry)
        {
            return lang.GetMessage(langEntry.Name, this, playerId);
        }

        private string GetMessage(string playerId, LangEntry langEntry, object arg1)
        {
            return string.Format(GetMessage(playerId, langEntry), arg1);
        }

        private string GetMessage(string playerId, LangEntry langEntry, object arg1, object arg2)
        {
            return string.Format(GetMessage(playerId, langEntry), arg1, arg2);
        }

        private string GetMessage(string playerId, LangEntry langEntry, object arg1, object arg2, string arg3)
        {
            return string.Format(GetMessage(playerId, langEntry), arg1, arg2, arg3);
        }

        private string GetMessage(string playerId, LangEntry langEntry, params object[] args)
        {
            return string.Format(GetMessage(playerId, langEntry), args);
        }


        private void ReplyToPlayer(IPlayer player, LangEntry langEntry)
        {
            player.Reply(GetMessage(player.Id, langEntry));
        }

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, object arg1)
        {
            player.Reply(GetMessage(player.Id, langEntry, arg1));
        }

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, object arg1, object arg2)
        {
            player.Reply(GetMessage(player.Id, langEntry, arg1, arg2));
        }

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, object arg1, object arg2, object arg3)
        {
            player.Reply(GetMessage(player.Id, langEntry, arg1, arg2, arg3));
        }

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, params object[] args)
        {
            player.Reply(GetMessage(player.Id, langEntry, args));
        }


        private void ChatMessage(BasePlayer player, LangEntry langEntry)
        {
            player.ChatMessage(GetMessage(player.UserIDString, langEntry));
        }

        private void ChatMessage(BasePlayer player, LangEntry langEntry, object arg1)
        {
            player.ChatMessage(GetMessage(player.UserIDString, langEntry, arg1));
        }

        private void ChatMessage(BasePlayer player, LangEntry langEntry, object arg1, object arg2)
        {
            player.ChatMessage(GetMessage(player.UserIDString, langEntry, arg1, arg2));
        }

        private void ChatMessage(BasePlayer player, LangEntry langEntry, object arg1, object arg2, object arg3)
        {
            player.ChatMessage(GetMessage(player.UserIDString, langEntry, arg1, arg2, arg3));
        }

        private void ChatMessage(BasePlayer player, LangEntry langEntry, params object[] args)
        {
            player.ChatMessage(GetMessage(player.UserIDString, langEntry, args));
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
        public static TValue GetOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key)
        {
            return dict.TryGetValue(key, out var value)
                ? value
                : default(TValue);
        }

        public static TValue GetOrCreate<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key) where TValue : new()
        {
            var value = dict.GetOrDefault(key);
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
