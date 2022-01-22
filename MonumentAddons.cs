using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

using CustomSpawnCallback = System.Func<UnityEngine.Vector3, UnityEngine.Quaternion, Newtonsoft.Json.Linq.JObject, UnityEngine.Component>;
using CustomKillCallback = System.Action<UnityEngine.Component>;
using CustomUpdateCallback = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject>;
using CustomAddDisplayInfoCallback = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject, System.Text.StringBuilder>;
using CustomSetDataCallback = System.Action<UnityEngine.Component, object>;

namespace Oxide.Plugins
{
    [Info("Monument Addons", "WhiteThunder", "0.10.0")]
    [Description("Allows privileged players to add permanent entities to monuments.")]
    internal class MonumentAddons : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin CopyPaste, EntityScaleManager, MonumentFinder, SignArtist;

        private static MonumentAddons _pluginInstance;
        private static Configuration _pluginConfig;
        private static StoredData _pluginData;

        private const float MaxRaycastDistance = 50;
        private const float TerrainProximityTolerance = 0.001f;
        private const float MaxFindDistanceSquared = 4;

        private const string PermissionAdmin = "monumentaddons.admin";

        private const string CargoShipShortName = "cargoshiptest";
        private const string DefaultProfileName = "Default";
        private const string DefaultUrlPattern = "https://github.com/WheteThunger/MonumentAddons/blob/master/Profiles/{0}.json?raw=true";

        private static readonly int HitLayers = Rust.Layers.Solid
            | Rust.Layers.Mask.Water;

        private readonly Dictionary<string, string> DownloadRequestHeaders = new Dictionary<string, string>
        {
            { "Content-Type", "application/json" }
        };

        private readonly ProfileManager _profileManager = new ProfileManager();
        private readonly CoroutineManager _coroutineManager = new CoroutineManager();
        private readonly MonumentEntityTracker _entityTracker = new MonumentEntityTracker();
        private readonly AdapterDisplayManager _entityDisplayManager = new AdapterDisplayManager();
        private readonly AdapterListenerManager _adapterListenerManager = new AdapterListenerManager();
        private readonly ControllerFactory _entityControllerFactoryResolver = new ControllerFactory();
        private readonly CustomAddonManager _customAddonManager = new CustomAddonManager();

        private ItemDefinition _waterDefinition;
        private ProtectionProperties _immortalProtection;

        private Coroutine _startupCoroutine;
        private bool _serverInitialized = false;

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            _pluginData = StoredData.Load();

            // Ensure the profile folder is created to avoid errors.
            Profile.LoadDefaultProfile();

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

            _adapterListenerManager.OnServerInitialized();

            if (CheckDependencies())
                StartupRoutine();

            _serverInitialized = true;
        }

        private void Unload()
        {
            _coroutineManager.Destroy();
            _profileManager.UnloadAllProfiles();

            UnityEngine.Object.Destroy(_immortalProtection);

            _pluginData = null;
            _pluginConfig = null;
            _pluginInstance = null;
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
            {
                return;
            }

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
                return false;

            return null;
        }

        private bool? CanUpdateSign(BasePlayer player, ISignage signage)
        {
            if (_entityTracker.IsMonumentEntity(signage as BaseEntity) && !HasAdminPermission(player))
            {
                ChatMessage(player, Lang.ErrorNoPermission);
                return false;
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

            var adapter = component.Adapter as SignEntityAdapter;
            if (adapter == null)
                return;

            var controller = adapter.Controller as SignEntityController;
            if (controller == null)
                return;

            controller.UpdateSign(signage.GetTextureCRCs());
        }

        // This hook is exposed by plugin: Sign Arist (SignArtist).
        private void OnImagePost(BasePlayer player, string url, bool raw, ISignage signage, uint textureIndex = 0)
        {
            SignEntityController controller;

            if (!_entityTracker.IsMonumentEntity(signage as BaseEntity, out controller))
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
            controller.Profile.Save();
        }

        private void OnEntityScaled(BaseEntity entity, float scale)
        {
            SingleEntityController controller;

            if (!_entityTracker.IsMonumentEntity(entity, out controller)
                || controller.EntityData.Scale == scale)
                return;

            controller.EntityData.Scale = scale;
            controller.UpdateScale();
            controller.Profile.Save();
        }

        // This hook is exposed by plugin: Telekinesis.
        private BaseEntity OnTelekinesisFindFailed(BasePlayer player)
        {
            if (!HasAdminPermission(player))
                return null;

            return FindAdapter<SingleEntityAdapter>(player).Adapter?.Entity;
        }

        // This hook is exposed by plugin: Telekinesis.
        private bool? CanStartTelekinesis(BasePlayer player, BaseEntity moveEntity)
        {
            if (_entityTracker.IsMonumentEntity(moveEntity) && !HasAdminPermission(player))
                return false;

            return null;
        }

        // This hook is exposed by plugin: Telekinesis.
        private void OnTelekinesisStarted(BasePlayer player, BaseEntity moveEntity, BaseEntity rotateEntity)
        {
            if (_entityTracker.IsMonumentEntity(moveEntity))
                _entityDisplayManager.ShowAllRepeatedly(player);
        }

        // This hook is exposed by plugin: Telekinesis.
        private void OnTelekinesisStopped(BasePlayer player, BaseEntity moveEntity, BaseEntity rotateEntity)
        {
            SingleEntityAdapter adapter;
            SingleEntityController controller;

            if (!_entityTracker.IsMonumentEntity(moveEntity, out adapter, out controller)
                || adapter.IsAtIntendedPosition)
                return;

            HandleAdapterMoved(adapter, controller);

            if (player != null)
            {
                _entityDisplayManager.ShowAllRepeatedly(player);
                ChatMessage(player, Lang.MoveSuccess, controller.Adapters.Count, controller.Profile.Name);
            }
        }

        #endregion

        #region Dependencies

        private bool CheckDependencies()
        {
            if (MonumentFinder == null)
            {
                LogError("MonumentFinder is not loaded, get it at http://umod.org.");
                return false;
            }

            return true;
        }

        private MonumentAdapter GetClosestMonumentAdapter(Vector3 position)
        {
            var dictResult = MonumentFinder.Call("API_GetClosest", position) as Dictionary<string, object>;
            if (dictResult == null)
                return null;

            return new MonumentAdapter(dictResult);
        }

        private List<BaseMonument> WrapFindMonumentResults(List<Dictionary<string, object>> dictList)
        {
            if (dictList == null)
                return null;

            var monumentList = new List<BaseMonument>();
            foreach (var dict in dictList)
                monumentList.Add(new MonumentAdapter(dict));

            return monumentList;
        }

        private List<BaseMonument> FindMonumentsByAlias(string alias) =>
            WrapFindMonumentResults(MonumentFinder.Call("API_FindByAlias", alias) as List<Dictionary<string, object>>);

        private List<BaseMonument> FindMonumentsByShortName(string shortName) =>
            WrapFindMonumentResults(MonumentFinder.Call("API_FindByShortName", shortName) as List<Dictionary<string, object>>);

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

                SignArtist.Call(apiName, null, signage as ISignage, imageInfo.Url, imageInfo.Raw, textureIndex);
            }
        }

        private static class PasteUtils
        {
            private static readonly string[] CopyPasteArgs = new string[]
            {
                "stability", "false",
            };

            private static VersionNumber _requiredVersion = new VersionNumber(4, 2, 0);

            public static bool IsCopyPasteCompatible()
            {
                var copyPaste = _pluginInstance?.CopyPaste;
                return copyPaste != null && copyPaste.Version >= _requiredVersion;
            }

            public static bool DoesPasteExist(string filename)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile("copypaste/" + filename);
            }

            public static Action PasteWithCancelCallback(PasteData pasteData, Vector3 position, float yRotation, Action<BaseEntity> onEntityPasted, Action onPasteCompleted)
            {
                var copyPaste = _pluginInstance?.CopyPaste;

                if (copyPaste == null)
                {
                    return null;
                }

                var result = copyPaste.Call("TryPasteFromVector3Cancellable", position, yRotation, pasteData.Filename, CopyPasteArgs, onPasteCompleted, onEntityPasted);
                if (!(result is ValueTuple<object, Action>))
                {
                    // throw new PasteException($"CopyPaste returned an unexpected response: {pasteResult}. Is CopyPaste up-to-date?");
                    _pluginInstance?.LogError($"CopyPaste returned an unexpected response for paste \"{pasteData.Filename}\": {result}. Is CopyPaste up-to-date?");
                    return null;
                }

                var pasteResult = (ValueTuple<object, Action>)result;
                if (!true.Equals(pasteResult.Item1))
                {
                    _pluginInstance?.LogError($"CopyPaste returned an unexpected response for paste \"{pasteData.Filename}\": {pasteResult.Item1}.");
                    return null;
                }

                return pasteResult.Item2;
            }
        }

        #endregion

        #region Commands

        private enum SpawnGroupOption
        {
            Name,
            MaxPopulation,
            RespawnDelayMin,
            RespawnDelayMax,
            SpawnPerTickMin,
            SpawnPerTickMax,
            PreventDuplicates,
        }

        private enum SpawnPointOption
        {
            Exclusive,
            DropToGround,
            CheckSpace,
            RandomRotation,
            RandomRadius,
        }

        [Command("maspawn")]
        private void CommandSpawn(IPlayer player, string cmd, string[] args)
        {
            ProfileController profileController;
            string prefabName;
            Vector3 position;
            BaseMonument monument;
            CustomAddonDefinition addonDefinition;

            if (player.IsServer
                || !VerifyHasPermission(player)
                || !VerifyMonumentFinderLoaded(player)
                || !VerifyProfileSelected(player, out profileController)
                || !VerifyValidPrefabOrDeployable(player, args, out prefabName, out addonDefinition)
                || !VerifyHitPosition(player, out position)
                || !VerifyAtMonument(player, position, out monument))
                return;

            var basePlayer = player.Object as BasePlayer;

            Vector3 localPosition;
            Vector3 localRotationAngles;
            bool isOnTerrain;
            DetermineLocalTransformData(position, basePlayer, monument, out localPosition, out localRotationAngles, out isOnTerrain);

            BaseIdentifiableData addonData = null;

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

                addonData = new EntityData
                {
                    Id = Guid.NewGuid(),
                    PrefabName = prefabName,
                    Position = localPosition,
                    RotationAngles = localRotationAngles,
                    OnTerrain = isOnTerrain,
                };

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
                    OnTerrain = isOnTerrain,
                };
            }

            var matchingMonuments = GetMonumentsByAliasOrShortName(monument.AliasOrShortName);

            profileController.Profile.AddData(monument.AliasOrShortName, addonData);
            profileController.SpawnNewData(addonData, matchingMonuments);

            _entityDisplayManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.SpawnSuccess, matchingMonuments.Count, profileController.Profile.Name, monument.AliasOrShortName);
        }

        [Command("masave")]
        private void CommandSave(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            SingleEntityAdapter adapter;
            SingleEntityController controller;
            if (!VerifyLookingAtAdapter(player, out adapter, out controller, Lang.ErrorNoSuitableAddonFound))
                return;

            if (adapter.IsAtIntendedPosition)
            {
                ReplyToPlayer(player, Lang.MoveNothingToDo);
                return;
            }

            HandleAdapterMoved(adapter, controller);
            ReplyToPlayer(player, Lang.MoveSuccess, controller.Adapters.Count, controller.Profile.Name);

            var basePlayer = player.Object as BasePlayer;
            _entityDisplayManager.ShowAllRepeatedly(basePlayer);
        }

        [Command("makill")]
        private void CommandKill(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            BaseController controller;
            BaseTransformAdapter adapter;
            if (!VerifyLookingAtAdapter(player, out adapter, out controller, Lang.ErrorNoSuitableAddonFound))
                return;

            var numAdapters = controller.Adapters.Count;

            var spawnPointAdapter = adapter as SpawnPointAdapter;
            if (spawnPointAdapter != null)
            {
                var spawnGroupController = controller as SpawnGroupController;
                var spawnPointData = spawnPointAdapter.SpawnPointData;
                spawnGroupController.RemoveSpawnPoint(spawnPointData);
                spawnGroupController.Profile.RemoveSpawnPoint(spawnGroupController.SpawnGroupData, spawnPointData);
            }
            else
            {
                controller.TryKillAndRemove();
            }

            var basePlayer = player.Object as BasePlayer;
            _entityDisplayManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.KillSuccess, GetAddonName(player, adapter.Data), numAdapters, controller.Profile.Name);
        }

        [Command("masetid")]
        private void CommandSetId(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            if (args.Length < 1 || !ComputerStation.IsValidIdentifier(args[0]))
            {
                ReplyToPlayer(player, Lang.CCTVSetIdSyntax, cmd);
                return;
            }

            CCTVEntityController controller;
            if (!VerifyLookingAtAdapter(player, out controller, Lang.ErrorNoSuitableAddonFound))
                return;

            if (controller.EntityData.CCTV == null)
                controller.EntityData.CCTV = new CCTVInfo();

            controller.EntityData.CCTV.RCIdentifier = args[0];
            controller.Profile.Save();
            controller.UpdateIdentifier();

            var basePlayer = player.Object as BasePlayer;
            _entityDisplayManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.CCTVSetIdSuccess, args[0], controller.Adapters.Count, controller.Profile.Name);
        }

        [Command("masetdir")]
        private void CommandSetDirection(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            CCTVEntityAdapter adapter;
            CCTVEntityController controller;
            if (!VerifyLookingAtAdapter(player, out adapter, out controller, Lang.ErrorNoSuitableAddonFound))
                return;

            var cctv = adapter.Entity as CCTV_RC;

            var basePlayer = player.Object as BasePlayer;
            var direction = Vector3Ex.Direction(basePlayer.eyes.position, cctv.transform.position);
            direction = cctv.transform.InverseTransformDirection(direction);
            var lookAngles = BaseMountable.ConvertVector(Quaternion.LookRotation(direction).eulerAngles);

            if (controller.EntityData.CCTV == null)
                controller.EntityData.CCTV = new CCTVInfo();

            controller.EntityData.CCTV.Pitch = lookAngles.x;
            controller.EntityData.CCTV.Yaw = lookAngles.y;
            controller.Profile.Save();
            controller.UpdateDirection();

            _entityDisplayManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.CCTVSetDirectionSuccess, controller.Adapters.Count, controller.Profile.Name);
        }

        [Command("maskin")]
        private void CommandSkin(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            SingleEntityAdapter adapter;
            SingleEntityController controller;
            if (!VerifyLookingAtAdapter(player, out adapter, out controller, Lang.ErrorNoSuitableAddonFound))
                return;

            if (args.Length == 0)
            {
                ReplyToPlayer(player, Lang.SkinGet, adapter.Entity.skinID, cmd);
                return;
            }

            ulong skinId;
            if (!ulong.TryParse(args[0], out skinId))
            {
                ReplyToPlayer(player, Lang.SkinSetSyntax, cmd);
                return;
            }

            string alternativeShortName;
            if (IsRedirectSkin(skinId, out alternativeShortName))
            {
                ReplyToPlayer(player, Lang.SkinErrorRedirect, skinId, alternativeShortName);
                return;
            }

            controller.EntityData.Skin = skinId;
            controller.Profile.Save();
            controller.UpdateSkin();

            var basePlayer = player.Object as BasePlayer;
            _entityDisplayManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.SkinSetSuccess, skinId, controller.Adapters.Count, controller.Profile.Name);
        }

        private void AddProfileDescription(StringBuilder sb, IPlayer player, ProfileController profileController)
        {
            foreach (var summaryEntry in GetProfileSummary(player, profileController.Profile))
            {
                sb.AppendLine(GetMessage(player, Lang.ProfileDescribeItem, summaryEntry.AddonType, summaryEntry.AddonName, summaryEntry.Count, summaryEntry.MonumentName));
            }
        }

        [Command("maprofile")]
        private void CommandProfile(IPlayer player, string cmd, string[] args)
        {
            if (!_serverInitialized)
                return;

            if (!player.IsServer && !VerifyHasPermission(player))
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
                    var profileList = ProfileInfo.GetList(_profileManager);
                    if (profileList.Length == 0)
                    {
                        ReplyToPlayer(player, Lang.ProfileListEmpty);
                        return;
                    }

                    var playerProfileName = player.IsServer ? null : _pluginData.GetSelectedProfileName(player.Id);

                    profileList = profileList
                        .Where(profile => !profile.Name.EndsWith(Profile.OriginalSuffix))
                        .OrderByDescending(profile => profile.Enabled && profile.Name == playerProfileName)
                        .ThenByDescending(profile => profile.Enabled)
                        .ThenBy(profile => profile.Name)
                        .ToArray();

                    var sb = new StringBuilder();
                    sb.AppendLine(GetMessage(player, Lang.ProfileListHeader));
                    foreach (var profile in profileList)
                    {
                        var messageName = profile.Enabled && profile.Name == playerProfileName
                            ? Lang.ProfileListItemSelected
                            : profile.Enabled
                            ? Lang.ProfileListItemEnabled
                            : Lang.ProfileListItemDisabled;

                        sb.AppendLine(GetMessage(player, messageName, profile.Name, GetAuthorSuffix(player, profile.Profile?.Author)));
                    }
                    player.Reply(sb.ToString());
                    break;
                }

                case "describe":
                {
                    ProfileController controller;
                    if (!VerifyProfile(player, args, out controller, Lang.ProfileDescribeSyntax))
                        return;

                    if (controller.Profile.IsEmpty())
                    {
                        ReplyToPlayer(player, Lang.ProfileEmpty, controller.Profile.Name);
                        return;
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine(GetMessage(player, Lang.ProfileDescribeHeader, controller.Profile.Name));
                    AddProfileDescription(sb, player, controller);

                    player.Reply(sb.ToString());

                    if (!player.IsServer)
                    {
                        _entityDisplayManager.SetPlayerProfile(basePlayer, controller);
                        _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                    }
                    break;
                }

                case "select":
                {
                    if (player.IsServer)
                        return;

                    if (args.Length <= 1)
                    {
                        ReplyToPlayer(player, Lang.ProfileSelectSyntax);
                        return;
                    }

                    ProfileController controller;
                    if (!VerifyProfile(player, args, out controller, Lang.ProfileSelectSyntax))
                        return;

                    _pluginData.SetProfileSelected(player.Id, controller.Profile.Name);
                    var wasEnabled = controller.IsEnabled;
                    if (wasEnabled)
                    {
                        // Only save if the profile is not enabled, since enabling it will already save the main data file.
                        _pluginData.Save();
                    }
                    else
                    {
                        controller.Enable();
                    }

                    ReplyToPlayer(player, wasEnabled ? Lang.ProfileSelectSuccess : Lang.ProfileSelectEnableSuccess, controller.Profile.Name);
                    _entityDisplayManager.SetPlayerProfile(basePlayer, controller);
                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                    break;
                }

                case "create":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, Lang.ProfileCreateSyntax);
                        return;
                    }

                    var newName = DynamicConfigFile.SanitizeName(args[1]);
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        ReplyToPlayer(player, Lang.ProfileCreateSyntax);
                        return;
                    }

                    if (!VerifyProfileNameAvailable(player, newName))
                        return;

                    var controller = _profileManager.CreateProfile(newName, basePlayer?.displayName);

                    if (!player.IsServer)
                        _pluginData.SetProfileSelected(player.Id, newName);

                    _pluginData.SetProfileEnabled(newName);

                    ReplyToPlayer(player, Lang.ProfileCreateSuccess, controller.Profile.Name);
                    break;
                }

                case "rename":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, Lang.ProfileRenameSyntax);
                        return;
                    }

                    ProfileController controller;
                    if (args.Length == 2)
                    {
                        controller = player.IsServer ? null : _profileManager.GetPlayerProfileController(player.Id);
                        if (controller == null)
                        {
                            ReplyToPlayer(player, Lang.ProfileRenameSyntax);
                            return;
                        }
                    }
                    else if (!VerifyProfileExists(player, args[1], out controller))
                        return;

                    string newName = DynamicConfigFile.SanitizeName(args.Length == 2 ? args[1] : args[2]);
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        ReplyToPlayer(player, Lang.ProfileRenameSyntax);
                        return;
                    }

                    if (!VerifyProfileNameAvailable(player, newName))
                        return;

                    // Cache the actual old name in case it was case-insensitive matched.
                    var actualOldName = controller.Profile.Name;

                    controller.Rename(newName);
                    ReplyToPlayer(player, Lang.ProfileRenameSuccess, actualOldName, controller.Profile.Name);
                    if (!player.IsServer)
                    {
                        _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                    }
                    break;
                }

                case "reload":
                {
                    ProfileController controller;
                    if (!VerifyProfile(player, args, out controller, Lang.ProfileReloadSyntax))
                        return;

                    if (!controller.IsEnabled)
                    {
                        ReplyToPlayer(player, Lang.ProfileNotEnabled, controller.Profile.Name);
                        return;
                    }

                    Profile newProfileData;
                    try
                    {
                        newProfileData = Profile.Load(controller.Profile.Name);
                    }
                    catch (JsonReaderException ex)
                    {
                        player.Reply("{0}", string.Empty, ex.Message);
                        return;
                    }

                    controller.Reload(newProfileData);
                    ReplyToPlayer(player, Lang.ProfileReloadSuccess, controller.Profile.Name);
                    if (!player.IsServer)
                    {
                        _entityDisplayManager.SetPlayerProfile(basePlayer, controller);
                        _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                    }
                    break;
                }

                case "enable":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, Lang.ProfileEnableSyntax);
                        return;
                    }

                    ProfileController controller;
                    if (!VerifyProfileExists(player, args[1], out controller))
                        return;

                    var profileName = controller.Profile.Name;
                    if (controller.IsEnabled)
                    {
                        ReplyToPlayer(player, Lang.ProfileAlreadyEnabled, profileName);
                        return;
                    }

                    controller.Enable();
                    ReplyToPlayer(player, Lang.ProfileEnableSuccess, profileName);
                    if (!player.IsServer)
                    {
                        _entityDisplayManager.SetPlayerProfile(basePlayer, controller);
                        _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                    }
                    break;
                }

                case "disable":
                {
                    ProfileController controller;
                    if (!VerifyProfile(player, args, out controller, Lang.ProfileDisableSyntax))
                        return;

                    var profileName = controller.Profile.Name;
                    if (!controller.IsEnabled)
                    {
                        ReplyToPlayer(player, Lang.ProfileAlreadyDisabled, profileName);
                        return;
                    }

                    controller.Disable();
                    _pluginData.SetProfileDisabled(profileName);
                    _pluginData.Save();
                    ReplyToPlayer(player, Lang.ProfileDisableSuccess, profileName);
                    break;
                }

                case "clear":
                {
                    if (args.Length <= 1)
                    {
                        ReplyToPlayer(player, Lang.ProfileClearSyntax);
                        return;
                    }

                    ProfileController controller;
                    if (!VerifyProfile(player, args, out controller, Lang.ProfileClearSyntax))
                        return;

                    if (!controller.Profile.IsEmpty())
                        controller.Clear();

                    ReplyToPlayer(player, Lang.ProfileClearSuccess, controller.Profile.Name);
                    break;
                }

                case "moveto":
                {
                    BaseController controller;
                    if (!VerifyLookingAtAdapter(player, out controller, Lang.ErrorNoSuitableAddonFound))
                        return;

                    ProfileController newProfileController;
                    if (!VerifyProfile(player, args, out newProfileController, Lang.ProfileMoveToSyntax))
                        return;

                    var newProfile = newProfileController.Profile;
                    var oldProfile = controller.Profile;

                    var data = controller.Data;
                    var addonName = GetAddonName(player, data);

                    if (newProfileController == controller.ProfileController)
                    {
                        ReplyToPlayer(player, Lang.ProfileMoveToAlreadyPresent, addonName, oldProfile.Name);
                        return;
                    }

                    string monumentAliasOrShortName;
                    if (!controller.TryKillAndRemove(out monumentAliasOrShortName))
                        return;

                    newProfile.AddData(monumentAliasOrShortName, data);
                    newProfileController.SpawnNewData(data, GetMonumentsByAliasOrShortName(monumentAliasOrShortName));

                    ReplyToPlayer(player, Lang.ProfileMoveToSuccess, addonName, oldProfile.Name, newProfile.Name);
                    if (!player.IsServer)
                    {
                        _entityDisplayManager.SetPlayerProfile(basePlayer, newProfileController);
                        _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                    }
                    break;
                }

                case "install":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, Lang.ProfileInstallSyntax);
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
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpHeader));
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpList));
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpDescribe));
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpEnable));
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpDisable));
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpReload));
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpSelect));
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpCreate));
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpRename));
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpClear));
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpMoveTo));
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpInstall));
            ReplyToPlayer(player, sb.ToString());
        }

        [Command("mainstall")]
        private void CommandInstallProfile(IPlayer player, string cmd, string[] args)
        {
            if (args.Length < 1)
            {
                ReplyToPlayer(player, Lang.ProfileInstallShorthandSyntax);
                return;
            }

            SharedCommandInstallProfile(player, args);
        }

        private void SharedCommandInstallProfile(IPlayer player, string[] args)
        {
            var url = args[0];
            Uri parsedUri;

            if (!Uri.TryCreate(url, UriKind.Absolute, out parsedUri))
            {
                var fallbackUrl = string.Format(DefaultUrlPattern, url);
                if (Uri.TryCreate(fallbackUrl, UriKind.Absolute, out parsedUri))
                {
                    url = fallbackUrl;
                }
                else
                {
                    ReplyToPlayer(player, Lang.ProfileUrlInvalid, url);
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
                            ReplyToPlayer(player, Lang.ProfileInstallError, url);
                            return;
                        }

                        profile.Name = urlDerivedProfileName;
                    }

                    if (profile.Name.EndsWith(Profile.OriginalSuffix))
                    {
                        LogError($"Profile \"{profile.Name}\" should not end with \"{Profile.OriginalSuffix}\".");
                        ReplyToPlayer(player, Lang.ProfileInstallError, url);
                        return;
                    }

                    var profileController = _profileManager.GetProfileController(profile.Name);
                    if (profileController != null && !profileController.Profile.IsEmpty())
                    {
                        ReplyToPlayer(player, Lang.ProfileAlreadyExistsNotEmpty, profile.Name);
                        return;
                    }

                    profile.Save();
                    profile.SaveAsOriginal();

                    if (profileController == null)
                        profileController = _profileManager.GetProfileController(profile.Name);

                    if (profileController == null)
                    {
                        LogError($"Profile \"{profile.Name}\" could not be found on disk after download from url: \"{url}\"");
                        ReplyToPlayer(player, Lang.ProfileInstallError, url);
                        return;
                    }

                    if (profileController.IsEnabled)
                        profileController.Reload(profile);
                    else
                        profileController.Enable();

                    var sb = new StringBuilder();
                    sb.AppendLine(GetMessage(player, Lang.ProfileInstallSuccess, profile.Name, GetAuthorSuffix(player, profile.Author)));
                    AddProfileDescription(sb, player, profileController);
                    player.Reply(sb.ToString());

                    if (!player.IsServer)
                    {
                        var basePlayer = player.Object as BasePlayer;
                        _entityDisplayManager.SetPlayerProfile(basePlayer, profileController);
                        _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                    }
                },
                errorCallback: errorMessage =>
                {
                    player.Reply(errorMessage);
                }
            );
        }

        [Command("mashow")]
        private void CommandShow(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            int duration = AdapterDisplayManager.DefaultDisplayDuration;
            string profileName = null;

            foreach (var arg in args)
            {
                int argIntValue;
                if (int.TryParse(arg, out argIntValue))
                {
                    duration = argIntValue;
                    continue;
                }

                if (profileName == null)
                {
                    profileName = arg;
                }
            }

            ProfileController profileController = null;
            if (profileName != null)
            {
                profileController = _profileManager.GetProfileController(profileName);
                if (profileController == null)
                {
                    ReplyToPlayer(player, Lang.ProfileNotFound, profileName);
                    return;
                }
            }

            var basePlayer = player.Object as BasePlayer;

            _entityDisplayManager.SetPlayerProfile(basePlayer, profileController);
            _entityDisplayManager.ShowAllRepeatedly(basePlayer, duration);

            ReplyToPlayer(player, Lang.ShowSuccess, FormatTime(duration));
        }

        [Command("maspawngroup", "masg")]
        private void CommandSpawnGroup(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            if (args.Length < 1)
            {
                SubCommandSpawnGroupHelp(player, cmd);
                return;
            }

            var basePlayer = player.Object as BasePlayer;

            switch (args[0].ToLower())
            {
                case "create":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, Lang.SpawnGroupCreateSyntax, cmd);
                        return;
                    }

                    var spawnGroupName = args[1];

                    ProfileController profileController;
                    Vector3 position;
                    BaseMonument monument;
                    if (!VerifyMonumentFinderLoaded(player)
                        || !VerifyProfileSelected(player, out profileController)
                        || !VerifyHitPosition(player, out position)
                        || !VerifyAtMonument(player, position, out monument)
                        || !VerifySpawnGroupNameAvailable(player, profileController.Profile, monument, spawnGroupName))
                        return;

                    Vector3 localPosition;
                    Vector3 localRotationAngles;
                    bool isOnTerrain;
                    DetermineLocalTransformData(position, basePlayer, monument, out localPosition, out localRotationAngles, out isOnTerrain);

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
                                OnTerrain = isOnTerrain,
                            },
                        },
                    };

                    var matchingMonuments = GetMonumentsByAliasOrShortName(monument.AliasOrShortName);

                    profileController.Profile.AddData(monument.AliasOrShortName, spawnGroupData);
                    profileController.SpawnNewData(spawnGroupData, matchingMonuments);

                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);

                    ReplyToPlayer(player, Lang.SpawnGroupCreateSucces, spawnGroupName);
                    break;
                }

                case "set":
                {
                    if (args.Length < 3)
                    {
                        ReplyToPlayer(player, Lang.ErrorSetSyntax, cmd);
                        return;
                    }

                    SpawnGroupOption spawnGroupOption;
                    if (!TryParseEnum(args[1], out spawnGroupOption))
                    {
                        ReplyToPlayer(player, Lang.ErrorSetUnknownOption, args[1]);
                        return;
                    }

                    SpawnPointAdapter spawnPointAdapter;
                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnPointAdapter, out spawnGroupController, Lang.ErrorNoSpawnPointFound))
                        return;

                    var spawnGroupData = spawnGroupController.SpawnGroupData;
                    object setValue = args[2];

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
                            int maxPopulation;
                            if (!VerifyValidInt(player, args[2], out maxPopulation, Lang.ErrorSetSyntax, cmd, SpawnGroupOption.MaxPopulation))
                                return;

                            spawnGroupData.MaxPopulation = maxPopulation;
                            break;
                        }

                        case SpawnGroupOption.RespawnDelayMin:
                        {
                            float respawnDelayMin;
                            if (!VerifyValidFloat(player, args[2], out respawnDelayMin, Lang.ErrorSetSyntax, cmd, SpawnGroupOption.RespawnDelayMin))
                                return;

                            spawnGroupData.RespawnDelayMin = respawnDelayMin;
                            spawnGroupData.RespawnDelayMax = Math.Max(respawnDelayMin, spawnGroupData.RespawnDelayMax);
                            setValue = respawnDelayMin;
                            break;
                        }

                        case SpawnGroupOption.RespawnDelayMax:
                        {
                            float respawnDelayMax;
                            if (!VerifyValidFloat(player, args[2], out respawnDelayMax, Lang.ErrorSetSyntax, cmd, SpawnGroupOption.RespawnDelayMax))
                                return;

                            spawnGroupData.RespawnDelayMax = respawnDelayMax;
                            spawnGroupData.RespawnDelayMin = Math.Min(spawnGroupData.RespawnDelayMin, respawnDelayMax);
                            setValue = respawnDelayMax;
                            break;
                        }

                        case SpawnGroupOption.SpawnPerTickMin:
                        {
                            int spawnPerTickMin;
                            if (!VerifyValidInt(player, args[2], out spawnPerTickMin, Lang.ErrorSetSyntax, cmd, SpawnGroupOption.SpawnPerTickMin))
                                return;

                            spawnGroupData.SpawnPerTickMin = spawnPerTickMin;
                            spawnGroupData.SpawnPerTickMax = Math.Max(spawnPerTickMin, spawnGroupData.SpawnPerTickMax);
                            setValue = spawnPerTickMin;
                            break;
                        }

                        case SpawnGroupOption.SpawnPerTickMax:
                        {
                            int spawnPerTickMax;
                            if (!VerifyValidInt(player, args[2], out spawnPerTickMax, Lang.ErrorSetSyntax, cmd, SpawnGroupOption.SpawnPerTickMax))
                                return;

                            spawnGroupData.SpawnPerTickMax = spawnPerTickMax;
                            spawnGroupData.SpawnPerTickMin = Math.Min(spawnGroupData.SpawnPerTickMin, spawnPerTickMax);
                            setValue = spawnPerTickMax;
                            break;
                        }

                        case SpawnGroupOption.PreventDuplicates:
                        {
                            bool preventDuplicates;
                            if (!VerifyValidBool(player, args[2], out preventDuplicates, Lang.ErrorSetSyntax, cmd, SpawnGroupOption.PreventDuplicates))
                                return;

                            spawnGroupData.PreventDuplicates = preventDuplicates;
                            setValue = preventDuplicates;
                            break;
                        }
                    }

                    spawnGroupController.UpdateSpawnGroups();
                    spawnGroupController.Profile.Save();

                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);

                    ReplyToPlayer(player, Lang.SpawnGroupSetSuccess, spawnGroupData.Name, spawnGroupOption, setValue);
                    break;
                }

                case "add":
                {
                    var weight = 100;
                    if (args.Length < 2 || args.Length >= 3 && !int.TryParse(args[2], out weight))
                    {
                        ReplyToPlayer(player, Lang.SpawnGroupAddSyntax, cmd);
                        return;
                    }

                    string prefabPath;
                    if (!VerifyValidPrefab(player, args[1], out prefabPath))
                        return;

                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnGroupController, Lang.ErrorNoSpawnPointFound))
                        return;

                    var spawnGroupData = spawnGroupController.SpawnGroupData;
                    var prefabData = spawnGroupData.Prefabs.Where(entry => entry.PrefabName == prefabPath).FirstOrDefault();
                    if (prefabData != null)
                    {
                        prefabData.Weight = weight;
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
                    spawnGroupController.Profile.Save();

                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);

                    ReplyToPlayer(player, Lang.SpawnGroupAddSuccess, prefabData.ShortPrefabName, weight, spawnGroupData.Name);
                    break;
                }

                case "remove":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, Lang.SpawnGroupRemoveSyntax, cmd);
                        return;
                    }

                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnGroupController, Lang.ErrorNoSpawnPointFound))
                        return;

                    string desiredPrefab = args[1];

                    var spawnGroupData = spawnGroupController.SpawnGroupData;

                    var matchingPrefabs = spawnGroupData.FindPrefabMatches(desiredPrefab);
                    if (matchingPrefabs.Count == 0)
                    {
                        ReplyToPlayer(player, Lang.SpawnGroupRemoveNoMatch, spawnGroupData.Name, desiredPrefab);
                        _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                        return;
                    }

                    if (matchingPrefabs.Count > 1)
                    {
                        ReplyToPlayer(player, Lang.SpawnGroupRemoveMultipleMatches, spawnGroupData.Name, desiredPrefab);
                        _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                        return;
                    }

                    var prefabMatch = matchingPrefabs[0];

                    spawnGroupData.Prefabs.Remove(prefabMatch);
                    spawnGroupController.KillSpawnedInstances(prefabMatch.PrefabName);
                    spawnGroupController.UpdateSpawnGroups();
                    spawnGroupController.Profile.Save();

                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);

                    ReplyToPlayer(player, Lang.SpawnGroupRemoveSuccess, prefabMatch.ShortPrefabName, spawnGroupData.Name);
                    break;
                }

                case "spawn":
                case "tick":
                {
                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnGroupController, Lang.ErrorNoSpawnPointFound))
                        return;

                    spawnGroupController.SpawnTick();
                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                    break;
                }

                case "respawn":
                {
                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnGroupController, Lang.ErrorNoSpawnPointFound))
                        return;

                    spawnGroupController.Respawn();
                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);
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
            sb.AppendLine(GetMessage(player, Lang.SpawnGroupHelpHeader, cmd));
            sb.AppendLine(GetMessage(player, Lang.SpawnGroupHelpCreate, cmd));
            sb.AppendLine(GetMessage(player, Lang.SpawnGroupHelpSet, cmd));
            sb.AppendLine(GetMessage(player, Lang.SpawnGroupHelpAdd, cmd));
            sb.AppendLine(GetMessage(player, Lang.SpawnGroupHelpRemove, cmd));
            ReplyToPlayer(player, sb.ToString());
        }

        [Command("maspawnpoint", "masp")]
        private void CommandSpawnPoint(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            if (args.Length < 1)
            {
                SubCommandSpawnPointHelp(player, cmd);
                return;
            }

            var basePlayer = player.Object as BasePlayer;

            switch (args[0].ToLower())
            {
                case "create":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, Lang.SpawnPointCreateSyntax, cmd);
                        return;
                    }

                    Vector3 position;
                    BaseMonument monument;
                    if (!VerifyMonumentFinderLoaded(player)
                        || !VerifyHitPosition(player, out position)
                        || !VerifyAtMonument(player, position, out monument))
                        return;

                    SpawnGroupController spawnGroupController;
                    if (!VerifySpawnGroupFound(player, args[1], monument, out spawnGroupController))
                        return;

                    Vector3 localPosition;
                    Vector3 localRotationAngles;
                    bool isOnTerrain;
                    DetermineLocalTransformData(position, basePlayer, monument, out localPosition, out localRotationAngles, out isOnTerrain);

                    var spawnPointData = new SpawnPointData
                    {
                        Id = Guid.NewGuid(),
                        Position = localPosition,
                        RotationAngles = localRotationAngles,
                        OnTerrain = isOnTerrain,
                    };

                    spawnGroupController.SpawnGroupData.SpawnPoints.Add(spawnPointData);
                    spawnGroupController.Profile.Save();
                    spawnGroupController.AddSpawnPoint(spawnPointData);

                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);

                    ReplyToPlayer(player, Lang.SpawnPointCreateSuccess, spawnGroupController.SpawnGroupData.Name);
                    break;
                }

                case "set":
                {
                    if (args.Length < 3)
                    {
                        ReplyToPlayer(player, Lang.SpawnPointSetSyntax, cmd);
                        return;
                    }

                    SpawnPointOption spawnPointOption;
                    if (!TryParseEnum(args[1], out spawnPointOption))
                    {
                        ReplyToPlayer(player, Lang.ErrorSetUnknownOption, args[1]);
                        return;
                    }

                    SpawnPointAdapter spawnPointAdapter;
                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnPointAdapter, out spawnGroupController, Lang.ErrorNoSpawnPointFound))
                        return;

                    var spawnPointData = spawnPointAdapter.SpawnPointData;
                    object setValue = args[2];

                    switch (spawnPointOption)
                    {
                        case SpawnPointOption.Exclusive:
                        {
                            bool exclusive;
                            if (!VerifyValidBool(player, args[2], out exclusive, Lang.SpawnGroupSetSuccess, Lang.ErrorSetSyntax, cmd, SpawnPointOption.Exclusive))
                                return;

                            spawnPointData.Exclusive = exclusive;
                            setValue = spawnPointData.Exclusive;
                            break;
                        }

                        case SpawnPointOption.DropToGround:
                        {
                            bool dropToGround;
                            if (!VerifyValidBool(player, args[2], out dropToGround, Lang.ErrorSetSyntax, cmd, SpawnPointOption.DropToGround))
                                return;

                            spawnPointData.DropToGround = dropToGround;
                            setValue = spawnPointData.DropToGround;
                            break;
                        }

                        case SpawnPointOption.CheckSpace:
                        {
                            bool checkSpace;
                            if (!VerifyValidBool(player, args[2], out checkSpace, Lang.ErrorSetSyntax, cmd, SpawnPointOption.CheckSpace))
                                return;

                            spawnPointData.CheckSpace = checkSpace;
                            setValue = spawnPointData.CheckSpace;
                            break;
                        }

                        case SpawnPointOption.RandomRotation:
                        {
                            bool randomRotation;
                            if (!VerifyValidBool(player, args[2], out randomRotation, Lang.ErrorSetSyntax, cmd, SpawnPointOption.RandomRotation))
                                return;

                            spawnPointData.RandomRotation = randomRotation;
                            setValue = spawnPointData.RandomRotation;
                            break;
                        }

                        case SpawnPointOption.RandomRadius:
                        {
                            float radius;
                            if (!VerifyValidFloat(player, args[2], out radius, Lang.ErrorSetSyntax, cmd, SpawnPointOption.RandomRadius))
                                return;

                            spawnPointData.RandomRadius = radius;
                            setValue = spawnPointData.RandomRadius;
                            break;
                        }
                    }

                    spawnGroupController.Profile.Save();

                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);

                    ReplyToPlayer(player, Lang.SpawnPointSetSuccess, spawnPointOption, setValue);
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
            sb.AppendLine(GetMessage(player, Lang.SpawnPointHelpHeader, cmd));
            sb.AppendLine(GetMessage(player, Lang.SpawnPointHelpCreate, cmd));
            sb.AppendLine(GetMessage(player, Lang.SpawnPointHelpSet, cmd));
            ReplyToPlayer(player, sb.ToString());
        }

        [Command("mapaste")]
        private void CommandPaste(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            if (args.Length < 1)
            {
                ReplyToPlayer(player, Lang.PasteSyntax);
                return;
            }

            if (!PasteUtils.IsCopyPasteCompatible())
            {
                ReplyToPlayer(player, Lang.PasteNotCompatible);
                return;
            }

            ProfileController profileController;
            Vector3 position;
            BaseMonument monument;
            if (!VerifyMonumentFinderLoaded(player)
                || !VerifyProfileSelected(player, out profileController)
                || !VerifyHitPosition(player, out position)
                || !VerifyAtMonument(player, position, out monument))
                return;

            var pasteName = args[0];

            if (!PasteUtils.DoesPasteExist(pasteName))
            {
                ReplyToPlayer(player, Lang.PasteNotFound, pasteName);
                return;
            }

            var basePlayer = player.Object as BasePlayer;

            Vector3 localPosition;
            Vector3 localRotationAngles;
            bool isOnTerrain;
            DetermineLocalTransformData(position, basePlayer, monument, out localPosition, out localRotationAngles, out isOnTerrain, flipRotation: false);

            var pasteData = new PasteData
            {
                Id = Guid.NewGuid(),
                Position = localPosition,
                RotationAngles = localRotationAngles,
                OnTerrain = isOnTerrain,
                Filename = pasteName,
            };

            var matchingMonuments = GetMonumentsByAliasOrShortName(monument.AliasOrShortName);

            profileController.Profile.AddData(monument.AliasOrShortName, pasteData);
            profileController.SpawnNewData(pasteData, matchingMonuments);

            _entityDisplayManager.ShowAllRepeatedly(basePlayer);

            ReplyToPlayer(player, Lang.PasteSuccess, pasteName, monument.AliasOrShortName, matchingMonuments.Count, profileController.Profile.Name);
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

            Plugin otherPlugin;
            if (_customAddonManager.IsRegistered(addonName, out otherPlugin))
            {
                LogError($"Unable to register custom addon \"{addonName}\" for plugin {plugin.Name} because it's already been registered by plugin {otherPlugin.Name}.");
                return null;
            }

            _customAddonManager.RegisterAddon(addonDefinition);

            return addonDefinition.ToApiResult();
        }

        #endregion

        #region Utilities

        #region Helper Methods - Command Checks

        private bool VerifyHasPermission(IPlayer player, string perm = PermissionAdmin)
        {
            if (player.HasPermission(perm))
                return true;

            ReplyToPlayer(player, Lang.ErrorNoPermission);
            return false;
        }

        private bool VerifyValidInt(IPlayer player, string arg, out int value, string errorMessageName, params object[] args)
        {
            if (int.TryParse(arg, out value))
                return true;

            ReplyToPlayer(player, errorMessageName, args);
            return false;
        }

        private bool VerifyValidFloat(IPlayer player, string arg, out float value, string errorMessageName, params object[] args)
        {
            if (float.TryParse(arg, out value))
                return true;

            ReplyToPlayer(player, errorMessageName, args);
            return false;
        }

        private bool VerifyValidBool(IPlayer player, string arg, out bool value, string errorMessageName, params object[] args)
        {
            if (BooleanParser.TryParse(arg, out value))
                return true;

            ReplyToPlayer(player, errorMessageName, args);
            return false;
        }

        private bool VerifyMonumentFinderLoaded(IPlayer player)
        {
            if (MonumentFinder != null)
                return true;

            ReplyToPlayer(player, Lang.ErrorMonumentFinderNotLoaded);
            return false;
        }

        private bool VerifyProfileSelected(IPlayer player, out ProfileController profileController)
        {
            profileController = _profileManager.GetPlayerProfileControllerOrDefault(player.Id);
            if (profileController != null)
                return true;

            ReplyToPlayer(player, Lang.SpawnErrorNoProfileSelected);
            return false;
        }

        private bool VerifyHitPosition(IPlayer player, out Vector3 position)
        {
            if (TryGetHitPosition(player.Object as BasePlayer, out position))
                return true;

            ReplyToPlayer(player, Lang.SpawnErrorNoTarget);
            return false;
        }

        private bool VerifyAtMonument(IPlayer player, Vector3 position, out BaseMonument closestMonument)
        {
            closestMonument = GetClosestMonument(player.Object as BasePlayer, position);
            if (closestMonument == null)
            {
                ReplyToPlayer(player, Lang.ErrorNoMonuments);
                return false;
            }

            if (!closestMonument.IsInBounds(position))
            {
                var closestPoint = closestMonument.ClosestPointOnBounds(position);
                var distance = (position - closestPoint).magnitude;
                ReplyToPlayer(player, Lang.ErrorNotAtMonument, closestMonument.AliasOrShortName, distance.ToString("f1"));
                return false;
            }

            return true;
        }

        private bool VerifyValidPrefab(IPlayer player, string prefabArg, out string prefabPath)
        {
            prefabPath = null;

            var matches = new List<string>();

            if (AddExactPrefabMatches(prefabArg, matches) == 1)
            {
                prefabPath = matches.First();
                return true;
            }

            if (matches.Count == 0 && AddPartialPrefabMatches(prefabArg, matches) == 1)
            {
                prefabPath = matches.First();
                return true;
            }

            if (matches.Count == 0)
            {
                ReplyToPlayer(player, Lang.SpawnErrorEntityNotFound, prefabArg);
                return false;
            }

            // Multiple matches were found, so print them all to the player.
            var replyMessage = GetMessage(player, Lang.SpawnErrorMultipleMatches);
            foreach (var match in matches)
            {
                replyMessage += $"\n{GetShortName(match)}";
            }

            player.Reply(replyMessage);
            return false;
        }

        private bool VerifyValidPrefabOrCustomAddon(IPlayer player, string prefabArg, out string prefabPath, out CustomAddonDefinition addonDefinition)
        {
            prefabPath = null;
            addonDefinition = null;

            var prefabMatches = new List<string>();
            var customAddonMatches = new List<CustomAddonDefinition>();

            var matchCount = AddExactPrefabMatches(prefabArg, prefabMatches)
                + AddExactCustomAddonMatches(prefabArg, customAddonMatches);

            if (matchCount == 0)
            {
                matchCount = AddPartialPrefabMatches(prefabArg, prefabMatches)
                    + AddPartialAddonMatches(prefabArg, customAddonMatches);
            }

            if (matchCount == 1)
            {
                if (prefabMatches.Count == 1)
                {
                    prefabPath = prefabMatches.First();
                }
                else
                {
                    addonDefinition = customAddonMatches.First();
                }
                return true;
            }

            if (matchCount == 0)
            {
                ReplyToPlayer(player, Lang.SpawnErrorEntityOrAddonNotFound, prefabArg);
                return false;
            }

            // Multiple matches were found, so print them all to the player.
            var replyMessage = GetMessage(player, Lang.SpawnErrorMultipleMatches);
            foreach (var match in prefabMatches)
            {
                replyMessage += $"\n{GetShortName(match)}";
            }
            foreach (var match in customAddonMatches)
            {
                replyMessage += $"\n{match.AddonName}";
            }

            player.Reply(replyMessage);
            return false;
        }

        private bool VerifyValidPrefabOrDeployable(IPlayer player, string[] args, out string prefabPath, out CustomAddonDefinition addonDefinition)
        {
            var prefabArg = args.FirstOrDefault();

            // Ignore "True" argument because that simply means the player used a key bind.
            if (!string.IsNullOrWhiteSpace(prefabArg) && prefabArg != "True")
            {
                return VerifyValidPrefabOrCustomAddon(player, prefabArg, out prefabPath, out addonDefinition);
            }

            addonDefinition = null;

            var basePlayer = player.Object as BasePlayer;
            var deployablePrefab = DeterminePrefabFromPlayerActiveDeployable(basePlayer);
            if (!string.IsNullOrEmpty(deployablePrefab))
            {
                prefabPath = deployablePrefab;
                return true;
            }

            prefabPath = null;
            ReplyToPlayer(player, Lang.SpawnErrorSyntax);
            return false;
        }

        private bool VerifyLookingAtAdapter<TAdapter, TController>(IPlayer player, out AdapterFindResult<TAdapter, TController> findResult, string errorMessageName)
            where TAdapter : BaseTransformAdapter
            where TController : BaseController
        {
            var basePlayer = player.Object as BasePlayer;

            RaycastHit hit;
            var hitResult = FindHitAdapter<TAdapter, TController>(basePlayer, out hit);
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
                ReplyToPlayer(player, Lang.ErrorEntityNotEligible);
            }
            else
            {
                // Maybe found an entity, but it did not match the adapter/controller type.
                ReplyToPlayer(player, errorMessageName);
            }

            findResult = default(AdapterFindResult<TAdapter, TController>);
            return false;
        }

        private bool VerifyLookingAtAdapter<TAdapter, TController>(IPlayer player, out TAdapter adapter, out TController controller, string errorMessageName)
            where TAdapter : BaseTransformAdapter
            where TController : BaseController
        {
            AdapterFindResult<TAdapter, TController> findResult;
            var result = VerifyLookingAtAdapter(player, out findResult, errorMessageName);
            adapter = findResult.Adapter;
            controller = findResult.Controller;
            return result;
        }

        // Convenient method that does not require an adapter type.
        private bool VerifyLookingAtAdapter<TController>(IPlayer player, out TController controller, string errorMessageName)
            where TController : BaseController
        {
            AdapterFindResult<BaseTransformAdapter, TController> findResult;
            var result = VerifyLookingAtAdapter(player, out findResult, errorMessageName);
            controller = findResult.Controller;
            return result;
        }

        private bool VerifySpawnGroupFound(IPlayer player, string partialGroupName, BaseMonument closestMonument, out SpawnGroupController spawnGroupController)
        {
            var matches = FindSpawnGroups(partialGroupName, closestMonument.AliasOrShortName, partialMatch: true).ToList();

            spawnGroupController = matches.FirstOrDefault();

            if (matches.Count == 1)
            {
                return true;
            }

            if (matches.Count == 0)
            {
                ReplyToPlayer(player, Lang.SpawnGroupNotFound, partialGroupName);
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

            ReplyToPlayer(player, Lang.SpawnGroupMultipeMatches, partialGroupName);
            return false;
        }

        private bool VerifyProfileNameAvailable(IPlayer player, string profileName)
        {
            if (!_profileManager.ProfileExists(profileName))
                return true;

            ReplyToPlayer(player, Lang.ProfileAlreadyExists, profileName);
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

            ReplyToPlayer(player, Lang.ProfileNotFound, profileName);
            return false;
        }

        private bool VerifyProfile(IPlayer player, string[] args, out ProfileController controller, string syntaxMessageName)
        {
            if (args.Length <= 1)
            {
                controller = player.IsServer ? null : _profileManager.GetPlayerProfileControllerOrDefault(player.Id);
                if (controller != null)
                    return true;

                ReplyToPlayer(player, syntaxMessageName);
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

            ReplyToPlayer(player, Lang.SpawnGroupCreateNameInUse, spawnGroupName, monument.AliasOrShortName, profile.Name);
            return false;
        }

        #endregion

        #region Helper Methods - Finding Adapters

        private struct AdapterFindResult<TAdapter, TController>
            where TAdapter : BaseTransformAdapter
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
            where TAdapter : BaseTransformAdapter
            where TController : BaseController
        {
            if (!TryRaycast(basePlayer, out hit))
                return default(AdapterFindResult<TAdapter, TController>);

            var entity = hit.GetEntity();
            if (entity == null)
                return default(AdapterFindResult<TAdapter, TController>);

            return new AdapterFindResult<TAdapter, TController>(entity);
        }

        private AdapterFindResult<TAdapter, TController> FindClosestNearbyAdapter<TAdapter, TController>(Vector3 position)
            where TAdapter : BaseTransformAdapter
            where TController : BaseController
        {
            TAdapter closestNearbyAdapter = null;
            TController associatedController = null;
            var closestDistanceSquared = float.MaxValue;

            foreach (var adapter in _profileManager.GetEnabledAdapters<TAdapter>())
            {
                var controllerOfType = adapter.Controller as TController;
                if (controllerOfType == null)
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
            where TAdapter : BaseTransformAdapter
            where TController : BaseController
        {
            RaycastHit hit;
            var hitResult = FindHitAdapter<TAdapter, TController>(basePlayer, out hit);
            if (hitResult.Controller != null)
                return hitResult;

            return FindClosestNearbyAdapter<TAdapter, TController>(hit.point);
        }

        // Convenient method that does not require a controller type.
        private AdapterFindResult<TAdapter, BaseController> FindAdapter<TAdapter>(BasePlayer basePlayer)
            where TAdapter : BaseTransformAdapter
        {
            return FindAdapter<TAdapter, BaseController>(basePlayer);
        }

        private IEnumerable<SpawnGroupController> FindSpawnGroups(string partialGroupName, string monumentAliasOrShortName, Profile profile = null, bool partialMatch = false)
        {
            foreach (var spawnGroupController in _profileManager.GetEnabledControllers<SpawnGroupController>())
            {
                if (profile != null && spawnGroupController.Profile != profile)
                    continue;

                if (partialMatch)
                {
                    if (spawnGroupController.SpawnGroupData.Name.IndexOf(partialGroupName, StringComparison.InvariantCultureIgnoreCase) == -1)
                    {
                        continue;
                    }
                }
                else if (!spawnGroupController.SpawnGroupData.Name.Equals(partialGroupName, StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                // Can only select a spawn group for the same monument.
                // This a slightly hacky way to check this, since data and controllers aren't directly aware of monuments.
                if (spawnGroupController.Adapters.FirstOrDefault()?.Monument.AliasOrShortName != monumentAliasOrShortName)
                    continue;

                yield return spawnGroupController;
            }
        }

        #endregion

        #region Helper Methods

        private static bool TryRaycast(BasePlayer player, out RaycastHit hit, float maxDistance = MaxRaycastDistance)
        {
            return Physics.Raycast(player.eyes.HeadRay(), out hit, maxDistance, HitLayers, QueryTriggerInteraction.Ignore);
        }

        private static bool TryGetHitPosition(BasePlayer player, out Vector3 position)
        {
            RaycastHit hit;
            if (TryRaycast(player, out hit))
            {
                position = hit.point;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private static bool IsOnTerrain(Vector3 position) =>
            Math.Abs(position.y - TerrainMeta.HeightMap.GetHeight(position)) <= TerrainProximityTolerance;

        private static string GetShortName(string prefabName)
        {
            var slashIndex = prefabName.LastIndexOf("/");
            var baseName = (slashIndex == -1) ? prefabName : prefabName.Substring(slashIndex + 1);
            return baseName.Replace(".prefab", "");
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
                    alternativeShortName = GetShortName(modDeployable.entityPrefab.resourcePath);

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

        private static string FormatTime(double seconds) =>
            TimeSpan.FromSeconds(seconds).ToString("g");

        private static void BroadcastEntityTransformChange(BaseEntity entity)
        {
            var wasSyncPosition = entity.syncPosition;
            entity.syncPosition = true;
            entity.TransformChanged();
            entity.syncPosition = wasSyncPosition;

            entity.transform.hasChanged = false;
        }

        private static void EnableSavingResursive(BaseEntity entity, bool enableSaving)
        {
            entity.EnableSaving(enableSaving);

            foreach (var child in entity.children)
                EnableSavingResursive(child, enableSaving);
        }

        private static IEnumerator WaitWhileWithTimeout(Func<bool> predicate, float timeoutSeconds)
        {
            var timeoutAt = UnityEngine.Time.time + timeoutSeconds;

            while (predicate() && UnityEngine.Time.time < timeoutAt)
            {
                yield return CoroutineEx.waitForEndOfFrame;
            }
        }

        private static bool TryParseEnum<TEnum>(string arg, out TEnum enumValue) where TEnum : struct
        {
            foreach (var value in Enum.GetValues(typeof(TEnum)))
            {
                if (value.ToString().IndexOf(arg, StringComparison.InvariantCultureIgnoreCase) >= 0)
                {
                    enumValue = (TEnum)value;
                    return true;
                }
            }

            enumValue = default(TEnum);
            return false;
        }

        private bool HasAdminPermission(string userId) =>
            permission.UserHasPermission(userId, PermissionAdmin);

        private bool HasAdminPermission(BasePlayer player) =>
            HasAdminPermission(player.UserIDString);

        private BaseMonument GetClosestMonument(BasePlayer player, Vector3 position)
        {
            BaseMonument cargoShipMonument;
            if (OnCargoShip(player, position, out cargoShipMonument))
                return cargoShipMonument;

            return GetClosestMonumentAdapter(position);
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

            var monuments = FindMonumentsByAlias(aliasOrShortName);
            if (monuments.Count > 0)
                return monuments;

            return FindMonumentsByShortName(aliasOrShortName);
        }

        private IEnumerator SpawnAllProfilesRoutine()
        {
            // Delay slightly to allow Monument Finder to finish loading.
            yield return CoroutineEx.waitForEndOfFrame;
            yield return _profileManager.LoadAllProfilesRoutine();

            // We don't want to be subscribed to OnEntitySpawned(CargoShip) until the coroutine is done.
            // Otherwise, a cargo ship could spawn while the coroutine is running and could get double entities.
            Subscribe(nameof(OnEntitySpawned));
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
                    if (_pluginInstance == null)
                    {
                        // Ignore the response because the plugin was unloaded.
                        return;
                    }

                    if (statusCode != 200)
                    {
                        errorCallback(GetMessage(player, Lang.ProfileDownloadError, url, statusCode));
                        return;
                    }

                    Profile profile;
                    try
                    {
                        profile = JsonConvert.DeserializeObject<Profile>(responseBody);
                    }
                    catch (Exception ex)
                    {
                        errorCallback(GetMessage(player, Lang.ProfileParseError, url, ex.Message));
                        return;
                    }

                    ProfileDataMigration.MigrateToLatest(profile);

                    profile.Url = url;
                    successCallback(profile);
                },
                owner: this,
                method: RequestMethod.GET,
                headers: DownloadRequestHeaders,
                timeout: 5000
            );
        }

        private void HandleAdapterMoved(SingleEntityAdapter adapter, SingleEntityController controller)
        {
            adapter.EntityData.Position = adapter.LocalPosition;
            adapter.EntityData.RotationAngles = adapter.LocalRotation.eulerAngles;
            adapter.EntityData.OnTerrain = IsOnTerrain(adapter.Position);
            controller.Profile.Save();
            controller.UpdatePosition();
        }

        private int AddExactPrefabMatches(string partialName, List<string> matches)
        {
            foreach (var path in GameManifest.Current.entities)
            {
                if (string.Compare(GetShortName(path), partialName, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    matches.Add(path.ToLower());
                }
            }

            return matches.Count;
        }

        private int AddPartialPrefabMatches(string partialName, List<string> matches)
        {
            foreach (var path in GameManifest.Current.entities)
            {
                if (GetShortName(path).Contains(partialName, CompareOptions.IgnoreCase))
                {
                    matches.Add(path.ToLower());
                }
            }

            return matches.Count;
        }

        private int AddExactCustomAddonMatches(string partialName, List<CustomAddonDefinition> matches)
        {
            foreach (var addonDefinition in _customAddonManager.GetAllAddons())
            {
                if (string.Compare(addonDefinition.AddonName, partialName, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    matches.Add(addonDefinition);
                }
            }

            return matches.Count;
        }

        private int AddPartialAddonMatches(string partialName, List<CustomAddonDefinition> matches)
        {
            foreach (var addonDefinition in _customAddonManager.GetAllAddons())
            {
                if (addonDefinition.AddonName.Contains(partialName, CompareOptions.IgnoreCase))
                {
                    matches.Add(addonDefinition);
                }
            }

            return matches.Count;
        }

        private string DeterminePrefabFromPlayerActiveDeployable(BasePlayer basePlayer)
        {
            var activeItem = basePlayer.GetActiveItem();
            if (activeItem == null)
                return null;

            string overridePrefabPath;
            if (_pluginConfig.DeployableOverrides.TryGetValue(activeItem.info.shortname, out overridePrefabPath))
                return overridePrefabPath;

            var itemModDeployable = activeItem.info.GetComponent<ItemModDeployable>();
            if (itemModDeployable == null)
                return null;

            return itemModDeployable.entityPrefab.resourcePath;
        }

        #endregion

        #region Boolean Parser

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

        #endregion

        #region Ddraw

        private static class Ddraw
        {
            public static void Sphere(BasePlayer player, Vector3 origin, float radius, Color color, float duration) =>
                player.SendConsoleCommand("ddraw.sphere", duration, color, origin, radius);

            public static void Line(BasePlayer player, Vector3 origin, Vector3 target, Color color, float duration) =>
                player.SendConsoleCommand("ddraw.line", duration, color, origin, target);

            public static void Arrow(BasePlayer player, Vector3 origin, Vector3 target, float headSize, Color color, float duration) =>
                player.SendConsoleCommand("ddraw.arrow", duration, color, origin, target, headSize);

            public static void Text(BasePlayer player, Vector3 origin, string text, Color color, float duration) =>
                player.SendConsoleCommand("ddraw.text", duration, color, origin, text);
        }

        #endregion

        #region Entity Utilities

        private static class EntityUtils
        {
            public static T GetNearbyEntity<T>(BaseEntity originEntity, float maxDistance, int layerMask, string filterShortPrefabName = null) where T : BaseEntity
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
                }

                DestroyProblemComponents(entity);
            }

            public static void PostSpawnShared(BaseEntity entity)
            {
                // Disable saving after spawn to make sure children that are spawned late also have saving disabled.
                // For example, the Lift class spawns a sub entity.
                EnableSavingResursive(entity, false);

                var combatEntity = entity as BaseCombatEntity;
                if (combatEntity != null)
                {
                    if (ShouldBeImmortal(entity))
                    {
                        // Must set after spawn for building blocks.
                        combatEntity.baseProtection = _pluginInstance._immortalProtection;
                    }
                }

                var decayEntity = entity as DecayEntity;
                if (decayEntity != null)
                {
                    decayEntity.decay = null;

                    var buildingBlock = entity as BuildingBlock;
                    if (buildingBlock != null && buildingBlock.HasFlag(BuildingBlock.BlockFlags.CanDemolish))
                    {
                        // Must be set after spawn for some reason.
                        buildingBlock.StopBeingDemolishable();
                    }
                }
            }
        }

        #endregion

        #region Coroutine Manager

        private class EmptyMonoBehavior : MonoBehaviour {}

        private class CoroutineManager
        {
            public static Coroutine StartGlobalCoroutine(IEnumerator enumerator) =>
                ServerMgr.Instance?.StartCoroutine(enumerator);

            // Object for tracking all coroutines for spawning or updating entities.
            // This allows easily stopping all those coroutines by simply destroying the game object.
            private MonoBehaviour _coroutineComponent;

            public Coroutine StartCoroutine(IEnumerator enumerator)
            {
                if (_coroutineComponent == null)
                    _coroutineComponent = new GameObject().AddComponent<EmptyMonoBehavior>();

                return _coroutineComponent.StartCoroutine(enumerator);
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

        #endregion

        #endregion

        #region Monuments

        private abstract class BaseMonument
        {
            public MonoBehaviour Object { get; private set; }
            public virtual string PrefabName => Object.name;
            public virtual string ShortName => GetShortName(PrefabName);
            public virtual string Alias => null;
            public virtual string AliasOrShortName => Alias ?? ShortName;
            public virtual Vector3 Position => Object.transform.position;
            public virtual Quaternion Rotation => Object.transform.rotation;
            public virtual bool IsValid => Object != null;

            public BaseMonument(MonoBehaviour behavior)
            {
                Object = behavior;
            }

            public virtual Vector3 TransformPoint(Vector3 localPosition) =>
                Object.transform.TransformPoint(localPosition);

            public virtual Vector3 InverseTransformPoint(Vector3 worldPosition) =>
                Object.transform.InverseTransformPoint(worldPosition);

            public abstract Vector3 ClosestPointOnBounds(Vector3 position);
            public abstract bool IsInBounds(Vector3 position);
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

            public override Vector3 TransformPoint(Vector3 localPosition) =>
                ((Func<Vector3, Vector3>)_monumentInfo["TransformPoint"]).Invoke(localPosition);

            public override Vector3 InverseTransformPoint(Vector3 worldPosition) =>
                ((Func<Vector3, Vector3>)_monumentInfo["InverseTransformPoint"]).Invoke(worldPosition);

            public override Vector3 ClosestPointOnBounds(Vector3 position) =>
                ((Func<Vector3, Vector3>)_monumentInfo["ClosestPointOnBounds"]).Invoke(position);

            public override bool IsInBounds(Vector3 position) =>
                ((Func<Vector3, bool>)_monumentInfo["IsInBounds"]).Invoke(position);
        }

        private class DynamicMonument : BaseMonument
        {
            public BaseEntity RootEntity { get; private set; }
            public bool IsMobile { get; private set; }
            public override bool IsValid => base.IsValid && !RootEntity.IsDestroyed;

            protected OBB BoundingBox => RootEntity.WorldSpaceBounds();

            public DynamicMonument(BaseEntity entity, bool isMobile) : base(entity)
            {
                RootEntity = entity;
                IsMobile = isMobile;
            }

            public override Vector3 ClosestPointOnBounds(Vector3 position) =>
                BoundingBox.ClosestPoint(position);

            public override bool IsInBounds(Vector3 position) =>
                BoundingBox.Contains(position);
        }

        #endregion

        #region Adapters/Controllers

        #region Entity Component

        private class MonumentEntityComponent : FacepunchBehaviour
        {
            public static void AddToEntity(BaseEntity entity, IEntityAdapter adapter, BaseMonument monument) =>
                entity.gameObject.AddComponent<MonumentEntityComponent>().Init(adapter, monument);

            public static MonumentEntityComponent GetForEntity(BaseEntity entity) =>
                entity.GetComponent<MonumentEntityComponent>();

            public static MonumentEntityComponent GetForEntity(uint id) =>
                BaseNetworkable.serverEntities.Find(id)?.GetComponent<MonumentEntityComponent>();

            public IEntityAdapter Adapter;
            private BaseEntity _entity;

            private void Awake()
            {
                _entity = GetComponent<BaseEntity>();
                _pluginInstance?._entityTracker.RegisterEntity(_entity);
            }

            public void Init(IEntityAdapter adapter, BaseMonument monument)
            {
                Adapter = adapter;
            }

            private void OnDestroy()
            {
                _pluginInstance?._entityTracker.UnregisterEntity(_entity);
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
                where TAdapter : EntityAdapterBase
                where TController : EntityControllerBase
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
                where TController : EntityControllerBase
            {
                EntityAdapterBase adapter;
                return IsMonumentEntity(entity, out adapter, out controller);
            }

            public void RegisterEntity(BaseEntity entity) => _trackedEntities.Add(entity);
            public void UnregisterEntity(BaseEntity entity) => _trackedEntities.Remove(entity);
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
            public BaseIdentifiableData Data { get; private set; }
            public BaseController Controller { get; private set; }
            public BaseMonument Monument { get; private set; }

            // Subclasses can override this to wait more than one frame for spawn/kill operations.
            public IEnumerator WaitInstruction { get; protected set; }

            public BaseAdapter(BaseIdentifiableData data, BaseController controller, BaseMonument monument)
            {
                Data = data;
                Controller = controller;
                Monument = monument;
            }

            public abstract void Spawn();
            public abstract void Kill();

            // Called immediately for all adapters when the controller needs to be killed.
            public virtual void PreUnload() {}
        }

        // Represents a single entity or spawn point at a single monument.
        private abstract class BaseTransformAdapter : BaseAdapter
        {
            public BaseTransformData TransformData { get; private set; }

            public abstract Vector3 Position { get; }
            public abstract Quaternion Rotation { get; }

            public Vector3 LocalPosition => Monument.InverseTransformPoint(Position);
            public Quaternion LocalRotation => Quaternion.Inverse(Monument.Rotation) * Rotation;

            public Vector3 IntendedPosition
            {
                get
                {
                    var intendedPosition = Monument.TransformPoint(TransformData.Position);

                    if (TransformData.OnTerrain)
                        intendedPosition.y = TerrainMeta.HeightMap.GetHeight(intendedPosition);

                    return intendedPosition;
                }
            }

            public Quaternion IntendedRotation => Monument.Rotation * Quaternion.Euler(TransformData.RotationAngles);

            public virtual bool IsAtIntendedPosition =>
                Position == IntendedPosition && Rotation == IntendedRotation;

            public BaseTransformAdapter(BaseTransformData transformData, BaseController controller, BaseMonument monument) : base(transformData, controller, monument)
            {
                TransformData = transformData;
            }
        }

        // Represents an entity or spawn point across one or more identical monuments.
        private abstract class BaseController
        {
            public ProfileController ProfileController { get; private set; }
            public BaseIdentifiableData Data { get; private set; }
            public List<BaseAdapter> Adapters { get; private set; } = new List<BaseAdapter>();

            public Profile Profile => ProfileController.Profile;

            private bool _wasKilled;

            public BaseController(ProfileController profileController, BaseIdentifiableData data)
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
                    if (_wasKilled)
                    {
                        yield break;
                    }

                    _pluginInstance.TrackStart();
                    var adapter = SpawnAtMonument(monument);
                    _pluginInstance.TrackEnd();
                    yield return adapter.WaitInstruction;
                }
            }

            public void PreUnload()
            {
                foreach (var adapter in Adapters)
                {
                    adapter.PreUnload();
                }
            }

            public bool TryKillAndRemove(out string monumentAliasOrShortName)
            {
                var profile = ProfileController.Profile;
                if (!profile.RemoveData(Data, out monumentAliasOrShortName))
                {
                    _pluginInstance?.LogError($"Unexpected error: {Data.GetType()} {Data.Id} was not found in profile {profile.Name}");
                    return false;
                }

                Kill();
                return true;
            }

            public bool TryKillAndRemove()
            {
                string monumentAliasOrShortName;
                return TryKillAndRemove(out monumentAliasOrShortName);
            }

            public IEnumerator KillRoutine()
            {
                foreach (var adapter in Adapters.ToArray())
                {
                    _pluginInstance?.TrackStart();
                    adapter.Kill();
                    _pluginInstance?.TrackEnd();
                    yield return adapter.WaitInstruction;
                }
            }

            protected void Kill()
            {
                // Stop the controller from spawning more adapters.
                _wasKilled = true;

                PreUnload();

                if (Adapters.Count > 0)
                {
                    CoroutineManager.StartGlobalCoroutine(KillRoutine());
                }

                ProfileController.OnControllerKilled(this);
            }
        }

        #endregion

        #region Entity Adapter/Controller - Base

        private abstract class EntityAdapterBase : BaseTransformAdapter, IEntityAdapter
        {
            public EntityData EntityData { get; private set; }
            public virtual bool IsDestroyed { get; }

            public EntityAdapterBase(EntityControllerBase controller, BaseMonument monument, EntityData entityData) : base(entityData, controller, monument)
            {
                EntityData = entityData;
            }

            public abstract void OnEntityKilled(BaseEntity entity);
            public abstract void UpdatePosition();

            protected BaseEntity CreateEntity(string prefabName, Vector3 position, Quaternion rotation)
            {
                var entity = GameManager.server.CreateEntity(EntityData.PrefabName, position, rotation);
                if (entity == null)
                    return null;

                // In case the plugin doesn't clean it up on server shutdown, make sure it doesn't come back so it's not duplicated.
                EnableSavingResursive(entity, false);

                var dynamicMonument = Monument as DynamicMonument;
                if (dynamicMonument != null)
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

                DestroyProblemComponents(entity);

                MonumentEntityComponent.AddToEntity(entity, this, Monument);

                return entity;
            }
        }

        private abstract class EntityControllerBase : BaseController
        {
            public EntityData EntityData { get; private set; }

            public EntityControllerBase(ProfileController profileController, EntityData entityData) : base(profileController, entityData)
            {
                EntityData = entityData;
            }

            public override void OnAdapterSpawned(BaseAdapter adapter)
            {
                base.OnAdapterSpawned(adapter);
                _pluginInstance?._adapterListenerManager.OnAdapterSpawned(adapter as EntityAdapterBase);
            }

            public override void OnAdapterKilled(BaseAdapter adapter)
            {
                base.OnAdapterKilled(adapter);
                _pluginInstance?._adapterListenerManager.OnAdapterKilled(adapter as EntityAdapterBase);
            }

            public void UpdatePosition()
            {
                ProfileController.StartCoroutine(UpdatePositionRoutine());
            }

            private IEnumerator UpdatePositionRoutine()
            {
                foreach (var adapter in Adapters.ToArray())
                {
                    var entityAdapter = adapter as EntityAdapterBase;
                    if (entityAdapter.IsDestroyed)
                        continue;

                    entityAdapter.UpdatePosition();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }
        }

        #endregion

        #region Entity Adapter/Controller - Single

        private class SingleEntityAdapter : EntityAdapterBase
        {
            public BaseEntity Entity { get; private set; }
            public override bool IsDestroyed => Entity == null || Entity.IsDestroyed;
            public override Vector3 Position => _transform.position;
            public override Quaternion Rotation => _transform.rotation;

            private Transform _transform;

            public SingleEntityAdapter(EntityControllerBase controller, EntityData entityData, BaseMonument monument) : base(controller, monument, entityData) {}

            public override void Spawn()
            {
                Entity = CreateEntity(EntityData.PrefabName, IntendedPosition, IntendedRotation);
                _transform = Entity.transform;

                PreEntitySpawn();
                Entity.Spawn();
                PostEntitySpawn();
            }

            public override void Kill()
            {
                if (IsDestroyed)
                    return;

                Entity.Kill();
            }

            public override void OnEntityKilled(BaseEntity entity)
            {
                _pluginInstance?.TrackStart();

                // Only consider the adapter destroyed if the main entity was destroyed.
                // For example, the scaled sphere parent may be killed if resized to default scale.
                if (entity == Entity)
                    Controller.OnAdapterKilled(this);

                _pluginInstance?.TrackEnd();
            }

            public override void UpdatePosition()
            {
                if (IsAtIntendedPosition)
                    return;

                var entityToMove = GetEntityToMove();
                var entityToRotate = Entity;

                entityToMove.transform.position = IntendedPosition;
                entityToRotate.transform.rotation = IntendedRotation;

                BroadcastEntityTransformChange(entityToMove);

                if (entityToRotate != entityToMove)
                    BroadcastEntityTransformChange(entityToRotate);
            }

            public void UpdateScale()
            {
                if (_pluginInstance.TryScaleEntity(Entity, EntityData.Scale))
                {
                    var parentSphere = Entity.GetParentEntity() as SphereEntity;
                    if (parentSphere == null)
                        return;

                    if (_pluginInstance._entityTracker.IsMonumentEntity(parentSphere))
                        return;

                    MonumentEntityComponent.AddToEntity(parentSphere, this, Monument);
                }
            }

            public void UpdateSkin()
            {
                if (Entity.skinID == EntityData.Skin)
                    return;

                Entity.skinID = EntityData.Skin;
                Entity.SendNetworkUpdate();
            }

            protected virtual void PreEntitySpawn()
            {
                if (EntityData.Skin != 0)
                    Entity.skinID = EntityData.Skin;

                EntitySetupUtils.PreSpawnShared(Entity);

                var buildingBlock = Entity as BuildingBlock;
                if (buildingBlock != null)
                {
                    buildingBlock.blockDefinition = PrefabAttribute.server.Find<Construction>(buildingBlock.prefabID);
                    if (buildingBlock.blockDefinition != null)
                    {
                        var buildingGrade = EntityData.BuildingBlock?.Grade ?? buildingBlock.blockDefinition.defaultGrade.gradeBase.type;
                        buildingBlock.SetGrade(buildingGrade);

                        var maxHealth = buildingBlock.currentGrade.maxHealth;
                        buildingBlock.InitializeHealth(maxHealth, maxHealth);
                        buildingBlock.ResetLifeStateOnSpawn = false;
                    }
                }

                var ioEntity = Entity as IOEntity;
                if (ioEntity != null)
                {
                    ioEntity.SetFlag(BaseEntity.Flags.On, true);
                    ioEntity.SetFlag(IOEntity.Flag_HasPower, true);
                }
            }

            protected virtual void PostEntitySpawn()
            {
                EntitySetupUtils.PostSpawnShared(Entity);

                if (Entity is NPCVendingMachine && EntityData.Skin != 0)
                    UpdateSkin();

                var computerStation = Entity as ComputerStation;
                if (computerStation != null && computerStation.isStatic)
                {
                    computerStation.CancelInvoke(computerStation.GatherStaticCameras);
                    computerStation.Invoke(() =>
                    {
                        _pluginInstance?.TrackStart();
                        GatherStaticCameras(computerStation);
                        _pluginInstance?.TrackEnd();
                    }, 1);
                }

                var paddlingPool = Entity as PaddlingPool;
                if (paddlingPool != null)
                {
                    paddlingPool.inventory.AddItem(_pluginInstance._waterDefinition, paddlingPool.inventory.maxStackSize);

                    // Disallow adding or removing water.
                    paddlingPool.SetFlag(BaseEntity.Flags.Busy, true);
                }

                var vehicleSpawner = Entity as VehicleSpawner;
                if (vehicleSpawner != null)
                {
                    vehicleSpawner.Invoke(() =>
                    {
                        _pluginInstance?.TrackStart();
                        EntityUtils.ConnectNearbyVehicleVendor(vehicleSpawner);
                        _pluginInstance?.TrackEnd();
                    }, 1);
                }

                var vehicleVendor = Entity as VehicleVendor;
                if (vehicleVendor != null)
                {
                    // Use a slightly longer delay than the vendor check check since this can short-circuit as an optimization.
                    vehicleVendor.Invoke(() =>
                    {
                        _pluginInstance?.TrackStart();
                        EntityUtils.ConnectNearbyVehicleSpawner(vehicleVendor);
                        _pluginInstance?.TrackEnd();
                    }, 2);
                }

                if (EntityData.Scale != 1)
                    UpdateScale();
            }

            private List<CCTV_RC> GetNearbyStaticCameras()
            {
                var dynamicMonument = Monument as DynamicMonument;
                if (dynamicMonument != null && dynamicMonument.RootEntity == Entity.GetParentEntity())
                {
                    var cargoCameraList = new List<CCTV_RC>();
                    foreach (var child in dynamicMonument.RootEntity.children)
                    {
                        var cctv = child as CCTV_RC;
                        if (cctv != null && cctv.isStatic)
                            cargoCameraList.Add(cctv);
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
                        cameraList.Add(cctv);
                }
                return cameraList;
            }

            private void GatherStaticCameras(ComputerStation computerStation)
            {
                var cameraList = GetNearbyStaticCameras();
                if (cameraList == null)
                    return;

                foreach (var cctv in cameraList)
                    computerStation.ForceAddBookmark(cctv.rcIdentifier);
            }

            private BaseEntity GetEntityToMove()
            {
                if (EntityData.Scale != 1 && _pluginInstance.GetEntityScale(Entity) != 1)
                {
                    var parentSphere = Entity.GetParentEntity() as SphereEntity;
                    if (parentSphere != null)
                        return parentSphere;
                }

                return Entity;
            }

            private bool ShouldBeImmortal()
            {
                var samSite = Entity as SamSite;
                if (samSite != null && samSite.staticRespawn)
                    return false;

                return true;
            }
        }

        private class SingleEntityController : EntityControllerBase
        {
            public SingleEntityController(ProfileController profileController, EntityData data)
                : base(profileController, data) {}

            public override BaseAdapter CreateAdapter(BaseMonument monument) =>
                new SingleEntityAdapter(this, EntityData, monument);

            public void UpdateSkin()
            {
                ProfileController.StartCoroutine(UpdateSkinRoutine());
            }

            public void UpdateScale()
            {
                ProfileController.StartCoroutine(UpdateScaleRoutine());
            }

            public IEnumerator UpdateSkinRoutine()
            {
                foreach (var adapter in Adapters.ToArray())
                {
                    var singleAdapter = adapter as SingleEntityAdapter;
                    if (singleAdapter.IsDestroyed)
                        continue;

                    singleAdapter.UpdateSkin();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            public IEnumerator UpdateScaleRoutine()
            {
                foreach (var adapter in Adapters.ToArray())
                {
                    var singleAdapter = adapter as SingleEntityAdapter;
                    if (singleAdapter.IsDestroyed)
                        continue;

                    singleAdapter.UpdateScale();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }
        }

        #endregion

        #region Entity Adapter/Controller - Signs

        private class SignEntityAdapter : SingleEntityAdapter
        {
            public SignEntityAdapter(EntityControllerBase controller, EntityData entityData, BaseMonument monument) : base(controller, entityData, monument) {}

            public uint[] GetTextureIds() => (Entity as ISignage)?.GetTextureCRCs();

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

                _pluginInstance.SkinSign(Entity as ISignage, EntityData.SignArtistImages);
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

            protected override void PostEntitySpawn()
            {
                base.PostEntitySpawn();

                // This must be done after spawning to allow the animation to work.
                var neonSign = Entity as NeonSign;
                if (neonSign != null)
                    neonSign.UpdateFromInput(neonSign.ConsumptionAmount(), 0);
            }
        }

        private class SignEntityController : SingleEntityController
        {
            public SignEntityController(ProfileController profileController, EntityData data)
                : base(profileController, data) {}

            // Sign artist will only be called for the primary adapter.
            // Texture ids are copied to the others.
            protected SignEntityAdapter _primaryAdapter;

            public override BaseAdapter CreateAdapter(BaseMonument monument) =>
                new SignEntityAdapter(this, EntityData, monument);

            public override void OnAdapterSpawned(BaseAdapter adapter)
            {
                base.OnAdapterSpawned(adapter);

                var signEntityAdapter = adapter as SignEntityAdapter;

                if (_primaryAdapter != null)
                {
                    var textureIds = _primaryAdapter.GetTextureIds();
                    if (textureIds != null)
                        signEntityAdapter.SetTextureIds(textureIds);
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
                    _primaryAdapter = Adapters.FirstOrDefault() as SignEntityAdapter;
            }

            public void UpdateSign(uint[] textureIds)
            {
                foreach (var adapter in Adapters)
                    (adapter as SignEntityAdapter).SetTextureIds(textureIds);
            }
        }

        #endregion

        #region Entity Adapter/Controller - CCTV

        private class CCTVEntityAdapter : SingleEntityAdapter
        {
            private int _idSuffix;
            private string _cachedIdentifier;
            private string _savedIdentifier => EntityData.CCTV?.RCIdentifier;

            public CCTVEntityAdapter(EntityControllerBase controller, EntityData entityData, BaseMonument monument, int idSuffix) : base(controller, entityData, monument)
            {
                _idSuffix = idSuffix;
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
                            computerStation.ForceAddBookmark(_cachedIdentifier);
                    }
                }
            }

            public override void OnEntityKilled(BaseEntity entity)
            {
                base.OnEntityKilled(entity);

                _pluginInstance?.TrackStart();

                if (_cachedIdentifier != null)
                {
                    var computerStationList = GetNearbyStaticComputerStations();
                    if (computerStationList != null)
                    {
                        foreach (var computerStation in computerStationList)
                            computerStation.controlBookmarks.Remove(_cachedIdentifier);
                    }
                }

                _pluginInstance?.TrackEnd();
            }

            // Ensure the RC identifiers are freed up as soon as possible to avoid conflicts when reloading.
            public override void PreUnload() => SetIdentifier(string.Empty);

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
                    _pluginInstance.LogWarning($"CCTV ID in use: {newIdentifier}");
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
                    Entity.SendNetworkUpdate();
            }

            public string GetIdentifier() =>
                (Entity as CCTV_RC).rcIdentifier;

            private void SetIdentifier(string id) =>
                (Entity as CCTV_RC).rcIdentifier = id;

            private List<ComputerStation> GetNearbyStaticComputerStations()
            {
                var dynamicMonument = Monument as DynamicMonument;
                if (dynamicMonument != null && dynamicMonument.RootEntity == Entity.GetParentEntity())
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
                        computerStationList.Add(computerStation);
                }
                return computerStationList;
            }
        }

        private class CCTVEntityController : SingleEntityController
        {
            private int _nextId = 1;

            public CCTVEntityController(ProfileController profileController, EntityData data)
                : base(profileController, data) {}

            public override BaseAdapter CreateAdapter(BaseMonument monument) =>
                new CCTVEntityAdapter(this, EntityData, monument, _nextId++);

            public void UpdateIdentifier()
            {
                ProfileController.StartCoroutine(UpdateIdentifierRoutine());
            }

            public void UpdateDirection()
            {
                ProfileController.StartCoroutine(UpdateDirectionRoutine());
            }

            private IEnumerator UpdateIdentifierRoutine()
            {
                foreach (var adapter in Adapters.ToArray())
                {
                    var cctvAdapter = adapter as CCTVEntityAdapter;
                    if (cctvAdapter.IsDestroyed)
                        continue;

                    cctvAdapter.UpdateIdentifier();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            private IEnumerator UpdateDirectionRoutine()
            {
                foreach (var adapter in Adapters.ToArray())
                {
                    var cctvAdapter = adapter as CCTVEntityAdapter;
                    if (cctvAdapter.IsDestroyed)
                        continue;

                    cctvAdapter.UpdateDirection();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
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
        }

        private abstract class DynamicHookListener : AdapterListenerBase
        {
            protected string[] _dynamicHookNames;

            private HashSet<BaseAdapter> _adapters = new HashSet<BaseAdapter>();

            public override void Init()
            {
                UnsubscribeHooks();
            }

            public override void OnAdapterSpawned(BaseAdapter adapter)
            {
                _adapters.Add(adapter);

                if (_adapters.Count == 1)
                    SubscribeHooks();
            }

            public override void OnAdapterKilled(BaseAdapter adapter)
            {
                _adapters.Remove(adapter);

                if (_adapters.Count == 0)
                    UnsubscribeHooks();
            }

            private void SubscribeHooks()
            {
                if (_dynamicHookNames == null)
                    return;

                foreach (var hookName in _dynamicHookNames)
                    _pluginInstance?.Subscribe(hookName);
            }

            private void UnsubscribeHooks()
            {
                if (_dynamicHookNames == null)
                    return;

                foreach (var hookName in _dynamicHookNames)
                    _pluginInstance?.Unsubscribe(hookName);
            }
        }

        private class SignEntityListener : DynamicHookListener
        {
            public SignEntityListener()
            {
                _dynamicHookNames = new string[]
                {
                    nameof(CanUpdateSign),
                    nameof(OnSignUpdated),
                    nameof(OnImagePost),
                };
            }

            public override bool InterestedInAdapter(BaseAdapter adapter)
            {
                var entityData = adapter.Data as EntityData;
                if (entityData == null)
                    return false;

                return FindBaseEntityForPrefab(entityData.PrefabName) is ISignage;
            }
        }

        private class AdapterListenerManager
        {
            private AdapterListenerBase[] _listeners = new AdapterListenerBase[]
            {
                new SignEntityListener(),
            };

            public void Init()
            {
                foreach (var listener in _listeners)
                    listener.Init();
            }

            public void OnServerInitialized()
            {
                foreach (var listener in _listeners)
                    listener.OnServerInitialized();
            }

            public void OnAdapterSpawned(EntityAdapterBase entityAdapter)
            {
                foreach (var listener in _listeners)
                {
                    if (listener.InterestedInAdapter(entityAdapter))
                        listener.OnAdapterSpawned(entityAdapter);
                }
            }

            public void OnAdapterKilled(EntityAdapterBase entityAdapter)
            {
                foreach (var listener in _listeners)
                {
                    if (listener.InterestedInAdapter(entityAdapter))
                        listener.OnAdapterKilled(entityAdapter);
                }
            }
        }

        #endregion

        #region SpawnGroup Adapter/Controller

        private class SpawnedVehicleComponent : FacepunchBehaviour
        {
            private const float MaxDistanceSquared = 1;

            private Vector3 _originalPosition;
            private Transform _transform;

            private void Awake()
            {
                _transform = transform;
                _originalPosition = _transform.position;

                InvokeRandomized(CheckPositionTracked, 10, 10, 1);
            }

            private void CheckPositionTracked()
            {
                _pluginInstance?.TrackStart();
                CheckPosition();
                _pluginInstance?.TrackEnd();
            }

            private void CheckPosition()
            {
                if ((_transform.position - _originalPosition).sqrMagnitude < MaxDistanceSquared)
                    return;

                // Vehicle has moved from its spawn point, so unregister it and re-enable saving.
                var vehicle = GetComponent<BaseEntity>();
                if (vehicle != null && !vehicle.IsDestroyed)
                {
                    EnableSavingResursive(vehicle, true);

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
            private SpawnPointAdapter _adapter;
            private SpawnPointData _spawnPointData;
            private Transform _transform;
            private BaseEntity _parentEntity;
            private List<SpawnPointInstance> _instances = new List<SpawnPointInstance>();

            public void Init(SpawnPointAdapter adapter, SpawnPointData spawnPointData)
            {
                _adapter = adapter;
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

                if (_spawnPointData.DropToGround)
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
                    instance.gameObject.AddComponent<SpawnedVehicleComponent>();
                    entity.Invoke(() => DisableVehicleDecay(entity), 5);
                }
            }

            public override void ObjectRetired(SpawnPointInstance instance)
            {
                _instances.Remove(instance);
            }

            public override bool IsAvailableTo(GameObjectRef prefabRef)
            {
                if (!base.IsAvailableTo(prefabRef))
                {
                    return false;
                }

                if (_spawnPointData.Exclusive && _instances.Count > 0)
                {
                    return false;
                }

                if (_spawnPointData.CheckSpace)
                {
                    return SingletonComponent<SpawnHandler>.Instance.CheckBounds(prefabRef.Get(), _transform.position, _transform.rotation, Vector3.one);
                }

                return true;
            }

            public void OnDestroy()
            {
                KillSpawnedInstances();
                _adapter.OnSpawnPointKilled(this);
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

                var heli = vehicle as MiniCopter;
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

            private SpawnGroupAdapter _spawnGroupAdapter;
            private AIInformationZone _cachedInfoZone;
            private bool _didLookForInfoZone;

            public void Init(SpawnGroupAdapter spawnGroupAdapter)
            {
                _spawnGroupAdapter = spawnGroupAdapter;
            }

            public void UpdateSpawnClock()
            {
                if (spawnClock.events.Count > 0)
                {
                    var clockEvent = spawnClock.events[0];
                    var timeUntilSpawn = clockEvent.time - UnityEngine.Time.time;

                    if (timeUntilSpawn > _spawnGroupAdapter.SpawnGroupData.RespawnDelayMax)
                    {
                        clockEvent.time = UnityEngine.Time.time + _spawnGroupAdapter.SpawnGroupData.RespawnDelayMax;
                        spawnClock.events[0] = clockEvent;
                    }
                }
            }

            public float GetTimeToNextSpawn()
            {
                if (spawnClock.events.Count == 0)
                    return float.PositiveInfinity;

                return spawnClock.events.First().time - UnityEngine.Time.time;
            }

            protected override void PostSpawnProcess(BaseEntity entity, BaseSpawnPoint spawnPoint)
            {
                base.PostSpawnProcess(entity, spawnPoint);

                EnableSavingResursive(entity, false);

                var npcPlayer = entity as NPCPlayer;
                if (npcPlayer != null)
                {
                    var virtualInfoZone = GetVirtualInfoZone();
                    if (virtualInfoZone != null)
                    {
                        npcPlayer.VirtualInfoZone = virtualInfoZone;
                    }

                    var humanNpc = npcPlayer as global::HumanNPC;
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
                            navigator.PlaceOnNavMesh();
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

            private void OnDestroy()
            {
                SingletonComponent<SpawnHandler>.Instance.SpawnGroups.Remove(this);
                _spawnGroupAdapter.OnSpawnGroupKilled(this);
            }
        }

        private class SpawnPointAdapter : BaseTransformAdapter
        {
            public SpawnPointData SpawnPointData { get; private set; }
            public CustomSpawnPoint SpawnPoint { get; private set; }
            public override Vector3 Position => _transform.position;
            public override Quaternion Rotation => _transform.rotation;

            private Transform _transform;
            private SpawnGroupAdapter _spawnGroupAdapter;

            public SpawnPointAdapter(SpawnPointData spawnPointData, SpawnGroupAdapter spawnGroupAdapter, BaseController controller, BaseMonument monument) : base(spawnPointData, controller, monument)
            {
                SpawnPointData = spawnPointData;
                _spawnGroupAdapter = spawnGroupAdapter;
            }

            public override void Spawn()
            {
                var gameObject = new GameObject();
                _transform = gameObject.transform;
                _transform.SetPositionAndRotation(IntendedPosition, IntendedRotation);

                var dynamicMonument = Monument as DynamicMonument;
                if (dynamicMonument != null)
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

            public void OnSpawnPointKilled(CustomSpawnPoint spawnPoint)
            {
                _spawnGroupAdapter.OnSpawnPointAdapterKilled(this);
            }

            public void KillSpawnedInstances(string prefabName)
            {
                SpawnPoint.KillSpawnedInstances(prefabName);
            }
        }

        private class SpawnGroupAdapter : BaseAdapter
        {
            public SpawnGroupData SpawnGroupData { get; private set; }
            public List<SpawnPointAdapter> Adapters { get; private set; } = new List<SpawnPointAdapter>();
            public CustomSpawnGroup SpawnGroup { get; private set; }

            public SpawnGroupAdapter(SpawnGroupData spawnGroupData, BaseController controller, BaseMonument monument) : base(spawnGroupData, controller, monument)
            {
                SpawnGroupData = spawnGroupData;
            }

            public override void Spawn()
            {
                var spawnGroupGameObject = new GameObject();
                spawnGroupGameObject.SetActive(false);

                // Configure the spawn group and create spawn points before enabling the group.
                // This allows the vanilla Awake() method to perform initial spawn and schedule spawns.
                SpawnGroup = spawnGroupGameObject.AddComponent<CustomSpawnGroup>();
                SpawnGroup.Init(this);

                if (SpawnGroup.prefabs == null)
                    SpawnGroup.prefabs = new List<SpawnGroup.SpawnEntry>();

                UpdateProperties();
                UpdatePrefabEntries();

                // This will call Awake() on the CustomSpawnGroup component.
                spawnGroupGameObject.SetActive(true);

                foreach (var spawnPointData in SpawnGroupData.SpawnPoints)
                {
                    AddSpawnPoint(spawnPointData);
                }

                UpdateSpawnPointReferences();

                // Do initial spawn.
                SpawnGroup.Spawn();
            }

            public override void Kill()
            {
                UnityEngine.Object.Destroy(SpawnGroup?.gameObject);
            }

            public override void PreUnload()
            {
                foreach (var adapter in Adapters)
                {
                    adapter.PreUnload();
                }
            }

            public void OnSpawnPointAdapterKilled(SpawnPointAdapter spawnPointAdapter)
            {
                Adapters.Remove(spawnPointAdapter);

                if (SpawnGroup != null)
                {
                    UpdateSpawnPointReferences();
                }

                if (Adapters.Count == 0)
                {
                    Controller.OnAdapterKilled(this);
                }
            }

            public void OnSpawnGroupKilled(CustomSpawnGroup spawnGroup)
            {
                foreach (var spawnPointAdapter in Adapters.ToArray())
                {
                    spawnPointAdapter.Kill();
                }
            }

            private void UpdateProperties()
            {
                SpawnGroup.preventDuplicates = SpawnGroupData.PreventDuplicates;
                SpawnGroup.maxPopulation = SpawnGroupData.MaxPopulation;
                SpawnGroup.numToSpawnPerTickMin = SpawnGroupData.SpawnPerTickMin;
                SpawnGroup.numToSpawnPerTickMax = SpawnGroupData.SpawnPerTickMax;

                var respawnDelayMinChanged = SpawnGroup.respawnDelayMin != SpawnGroupData.RespawnDelayMin;
                var respawnDelayMaxChanged = SpawnGroup.respawnDelayMax != SpawnGroupData.RespawnDelayMax;

                SpawnGroup.respawnDelayMin = SpawnGroupData.RespawnDelayMin;
                SpawnGroup.respawnDelayMax = SpawnGroupData.RespawnDelayMax;

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
                    string guid;
                    if (!GameManifest.pathToGuid.TryGetValue(prefabEntry.PrefabName, out guid))
                    {
                        continue;
                    }

                    SpawnGroup.prefabs.Add(new SpawnGroup.SpawnEntry
                    {
                        prefab = new GameObjectRef { guid = guid },
                        weight = prefabEntry.Weight,
                    });
                }
            }

            private void UpdateSpawnPointReferences()
            {
                if (!SpawnGroup.gameObject.activeSelf || SpawnGroup.spawnPoints.Length == Adapters.Count)
                    return;

                SpawnGroup.spawnPoints = new BaseSpawnPoint[Adapters.Count];

                for (var i = 0; i < Adapters.Count; i++)
                {
                    SpawnGroup.spawnPoints[i] = Adapters[i].SpawnPoint;
                }
            }

            public void UpdateSpawnGroup()
            {
                UpdateProperties();
                UpdatePrefabEntries();
                UpdateSpawnPointReferences();
            }

            public void SpawnTick()
            {
                SpawnGroup.Spawn();
            }

            public void KillSpawnedInstances(string prefabName)
            {
                foreach (var spawnPointAdapter in Adapters)
                    spawnPointAdapter.KillSpawnedInstances(prefabName);
            }

            public void AddSpawnPoint(SpawnPointData spawnPointData)
            {
                var spawnPointAdapter = new SpawnPointAdapter(spawnPointData, this, Controller, Monument);
                Adapters.Add(spawnPointAdapter);
                spawnPointAdapter.Spawn();

                if (SpawnGroup.gameObject.activeSelf)
                {
                    UpdateSpawnPointReferences();
                }
            }

            public void RemoveSpawnPoint(SpawnPointData spawnPointData)
            {
                var spawnPointAdapter = FindSpawnPoint(spawnPointData);
                if (spawnPointAdapter == null)
                    return;

                spawnPointAdapter.Kill();
            }

            private SpawnPointAdapter FindSpawnPoint(SpawnPointData spawnPointData)
            {
                foreach (var spawnPointAdapter in Adapters)
                {
                    if (spawnPointAdapter.SpawnPointData == spawnPointData)
                        return spawnPointAdapter;
                }

                return null;
            }
        }

        private class SpawnGroupController : BaseController
        {
            public SpawnGroupData SpawnGroupData { get; private set; }
            public IEnumerable<SpawnGroupAdapter> SpawnGroupAdapters { get; private set; }

            public SpawnGroupController(ProfileController profileController, SpawnGroupData spawnGroupData) : base(profileController, spawnGroupData)
            {
                SpawnGroupData = spawnGroupData;
                SpawnGroupAdapters = Adapters.Cast<SpawnGroupAdapter>();
            }

            public override BaseAdapter CreateAdapter(BaseMonument monument) =>
                new SpawnGroupAdapter(SpawnGroupData, this, monument);

            public void AddSpawnPoint(SpawnPointData spawnPointData)
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters)
                    spawnGroupAdapter.AddSpawnPoint(spawnPointData);
            }

            public void RemoveSpawnPoint(SpawnPointData spawnPointData)
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters)
                    spawnGroupAdapter.RemoveSpawnPoint(spawnPointData);
            }

            public void UpdateSpawnGroups()
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters)
                    spawnGroupAdapter.UpdateSpawnGroup();
            }

            public void SpawnTick() =>
                ProfileController.StartCoroutine(SpawnTickRoutine());

            public void KillSpawnedInstances(string prefabName) =>
                ProfileController.StartCoroutine(KillSpawnedInstancesRoutine(prefabName));

            public void Respawn() =>
                ProfileController.StartCoroutine(RespawnRoutine());

            private IEnumerator SpawnTickRoutine()
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters.ToArray())
                {
                    spawnGroupAdapter.SpawnTick();
                    yield return null;
                }
            }

            private IEnumerator KillSpawnedInstancesRoutine(string prefabName = null)
            {
                foreach (var spawnGroupAdapter in SpawnGroupAdapters.ToArray())
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

        private class PasteAdapter : BaseTransformAdapter, IEntityAdapter
        {
            private const float CopyPasteMagicRotationNumber = 57.2958f;

            public PasteData PasteData { get; private set; }
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

                _cancelPaste = PasteUtils.PasteWithCancelCallback(PasteData, _position, _rotation.eulerAngles.y / CopyPasteMagicRotationNumber, OnEntityPasted, OnPasteComplete);

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
                var pastedEntities = _pastedEntities.ToArray();

                // Remove the entities in reverse order. Hopefully this makes the top of the building get removed first.
                for (var i = pastedEntities.Length - 1; i >= 0; i--)
                {
                    var entity = pastedEntities[i];
                    if (entity != null && !entity.IsDestroyed)
                    {
                        _pluginInstance?.TrackStart();
                        entity.Kill();
                        _pluginInstance?.TrackEnd();
                        yield return null;
                    }
                }

                _isWorking = false;
            }

            private void OnEntityPasted(BaseEntity entity)
            {
                EntitySetupUtils.PreSpawnShared(entity);
                EntitySetupUtils.PostSpawnShared(entity);

                MonumentEntityComponent.AddToEntity(entity, this, Monument);
                _pastedEntities.Add(entity);
            }

            private void OnPasteComplete()
            {
                _isWorking = false;
            }
        }

        private class PasteController : BaseController
        {
            public PasteData PasteData { get; private set; }
            public IEnumerable<PasteAdapter> PasteAdapters { get; private set; }

            public PasteController(ProfileController profileController, PasteData pasteData) : base(profileController, pasteData)
            {
                PasteData = pasteData;
                PasteAdapters = Adapters.Cast<PasteAdapter>();
            }

            public override BaseAdapter CreateAdapter(BaseMonument monument) =>
                new PasteAdapter(PasteData, this, monument);

            public override IEnumerator SpawnAtMonumentsRoutine(IEnumerable<BaseMonument> monumentList)
            {
                if (!PasteUtils.IsCopyPasteCompatible())
                {
                    _pluginInstance?.LogError($"Unable to paste \"{PasteData.Filename}\" for profile \"{Profile.Name}\" because CopyPaste is not loaded or its version is incompatible.");
                    yield break;
                }

                if (!PasteUtils.DoesPasteExist(PasteData.Filename))
                {
                    _pluginInstance?.LogError($"Unable to paste \"{PasteData.Filename}\" for profile \"{Profile.Name}\" because the file does not exist.");
                    yield break;
                }

                yield return base.SpawnAtMonumentsRoutine(monumentList);
            }
        }

        #endregion

        #region Custom Adapter/Controller

        private class CustomAddonDefinition
        {
            public static CustomAddonDefinition FromDictionary(string addonName, Plugin plugin, Dictionary<string, object> addonSpec)
            {
                var addonDefinition = new CustomAddonDefinition
                {
                    AddonName = addonName,
                    OwnerPlugin = plugin,
                };

                object spawnCallback, killCallback, updateCallback, addDataCallback;

                if (addonSpec.TryGetValue("Spawn", out spawnCallback))
                    addonDefinition.Spawn = spawnCallback as CustomSpawnCallback;

                if (addonSpec.TryGetValue("Kill", out killCallback))
                    addonDefinition.Kill = killCallback as CustomKillCallback;

                if (addonSpec.TryGetValue("Update", out updateCallback))
                    addonDefinition.Update = updateCallback as CustomUpdateCallback;

                if (addonSpec.TryGetValue("AddDisplayInfo", out addDataCallback))
                    addonDefinition.AddDisplayInfo = addDataCallback as CustomAddDisplayInfoCallback;

                return addonDefinition;
            }

            public string AddonName;
            public Plugin OwnerPlugin;
            public CustomSpawnCallback Spawn;
            public CustomKillCallback Kill;
            public CustomUpdateCallback Update;
            public CustomAddDisplayInfoCallback AddDisplayInfo;

            public List<CustomAddonAdapter> AdapterUsers = new List<CustomAddonAdapter>();

            public Dictionary<string, object> ToApiResult()
            {
                return new Dictionary<string, object>
                {
                    ["SetData"] = new CustomSetDataCallback(
                        (component, data) =>
                        {
                            if (Update == null)
                            {
                                _pluginInstance?.LogError($"Unable to set data for custom addon \"{AddonName}\" due to missing Update method.");
                                return;
                            }

                            var matchingAdapter = AdapterUsers.FirstOrDefault(adapter => adapter.Component == component);
                            if (matchingAdapter == null)
                            {
                                _pluginInstance?.LogError($"Unable to set data for custom addon \"{AddonName}\" because it has no spawned instances.");
                                return;
                            }

                            var controller = matchingAdapter.Controller as CustomAddonController;
                            controller.CustomAddonData.SetData(data);
                            controller.Profile.Save();

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
            private Dictionary<string, CustomAddonDefinition> _customAddonsByName = new Dictionary<string, CustomAddonDefinition>();
            private Dictionary<string, List<CustomAddonDefinition>> _customAddonsByPlugin = new Dictionary<string, List<CustomAddonDefinition>>();

            public IEnumerable<CustomAddonDefinition> GetAllAddons() => _customAddonsByName.Values;

            public bool IsRegistered(string addonName, out Plugin otherPlugin)
            {
                otherPlugin = null;
                CustomAddonDefinition existingAddon;
                if (_customAddonsByName.TryGetValue(addonName, out existingAddon))
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

                if (_pluginInstance._serverInitialized)
                {
                    foreach (var profileController in _pluginInstance._profileManager.GetEnabledProfileControllers())
                    {
                        foreach (var monumentEntry in profileController.Profile.MonumentDataMap)
                        {
                            var monumentName = monumentEntry.Key;
                            var monumentData = monumentEntry.Value;

                            foreach (var customAddonData in monumentData.CustomAddons)
                            {
                                if (customAddonData.AddonName == addonDefinition.AddonName)
                                {
                                    profileController.SpawnNewData(customAddonData, _pluginInstance.GetMonumentsByAliasOrShortName(monumentName));
                                }
                            }
                        }
                    }
                }
            }

            public void UnregisterAllForPlugin(Plugin plugin)
            {
                if (_customAddonsByName.Count == 0)
                {
                    return;
                }

                var addonsForPlugin = GetAddonsForPlugin(plugin);
                if (addonsForPlugin == null)
                {
                    return;
                }

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
                CustomAddonDefinition addonDefinition;
                return _customAddonsByName.TryGetValue(addonName, out addonDefinition)
                    ? addonDefinition
                    : null;
            }

            private List<CustomAddonDefinition> GetAddonsForPlugin(Plugin plugin)
            {
                List<CustomAddonDefinition> addonsForPlugin;
                return _customAddonsByPlugin.TryGetValue(plugin.Name, out addonsForPlugin)
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

        private class CustomAddonAdapter : BaseTransformAdapter, IEntityAdapter
        {
            private class CustomAddonComponent : MonoBehaviour
            {
                public CustomAddonAdapter Adapter;

                private void OnDestroy() => Adapter.OnAddonDestroyed();
            }

            public CustomAddonData CustomAddonData { get; private set; }
            public CustomAddonDefinition AddonDefinition { get; private set; }
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
                    MonumentEntityComponent.AddToEntity(entity, this, Monument);
                }
                else
                {
                    Component.gameObject.AddComponent<CustomAddonComponent>().Adapter = this;
                }
            }

            public override void Kill()
            {
                if (_wasKilled)
                {
                    return;
                }

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
            public CustomAddonData CustomAddonData { get; private set; }

            private CustomAddonDefinition _addonDefinition;

            public CustomAddonController(ProfileController profileController, CustomAddonData customAddonData, CustomAddonDefinition addonDefinition) : base(profileController, customAddonData)
            {
                CustomAddonData = customAddonData;
                _addonDefinition = addonDefinition;
            }

            public override BaseAdapter CreateAdapter(BaseMonument monument) =>
                new CustomAddonAdapter(CustomAddonData, this, monument, _addonDefinition);
        }

        #endregion

        #region Controller Factories

        private abstract class EntityControllerFactoryBase
        {
            public abstract bool AppliesToEntity(BaseEntity entity);
            public abstract EntityControllerBase CreateController(ProfileController controller, EntityData entityData);
        }

        private class SingleEntityControllerFactory : EntityControllerFactoryBase
        {
            public override bool AppliesToEntity(BaseEntity entity) => true;

            public override EntityControllerBase CreateController(ProfileController controller, EntityData entityData) =>
                new SingleEntityController(controller, entityData);
        }

        private class SignEntityControllerFactory : SingleEntityControllerFactory
        {
            public override bool AppliesToEntity(BaseEntity entity) => entity is ISignage;

            public override EntityControllerBase CreateController(ProfileController controller, EntityData entityData) =>
                new SignEntityController(controller, entityData);
        }

        private class CCTVEntityControllerFactory : SingleEntityControllerFactory
        {
            public override bool AppliesToEntity(BaseEntity entity) => entity is CCTV_RC;

            public override EntityControllerBase CreateController(ProfileController controller, EntityData entityData) =>
                new CCTVEntityController(controller, entityData);
        }

        private class ControllerFactory
        {
            private static ControllerFactory _instance = new ControllerFactory();
            public static ControllerFactory Instance => _instance;

            private List<EntityControllerFactoryBase> _entityFactories = new List<EntityControllerFactoryBase>
            {
                // The first that matches will be used.
                new CCTVEntityControllerFactory(),
                new SignEntityControllerFactory(),
                new SingleEntityControllerFactory(),
            };

            public BaseController CreateController(ProfileController profileController, BaseIdentifiableData data)
            {
                var spawnGroupData = data as SpawnGroupData;
                if (spawnGroupData != null)
                {
                    return new SpawnGroupController(profileController, spawnGroupData);
                }

                var pasteData = data as PasteData;
                if (pasteData != null)
                {
                    return new PasteController(profileController, pasteData);
                }

                var customAddonData = data as CustomAddonData;
                if (customAddonData != null)
                {
                    var addonDefinition = _pluginInstance._customAddonManager.GetAddon(customAddonData.AddonName);
                    return addonDefinition != null
                        ? new CustomAddonController(profileController, customAddonData, addonDefinition)
                        : null;
                }

                var entityData = data as EntityData;
                if (entityData != null)
                {
                    return ResolveEntityFactory(entityData)?.CreateController(profileController, entityData);
                }

                return null;
            }

            private EntityControllerFactoryBase ResolveEntityFactory(EntityData entityData)
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

        #region Adapter Display Manager

        private class AdapterDisplayManager
        {
            public const int DefaultDisplayDuration = 60;
            private const int DisplayIntervalDuration = 2;
            private const int HeaderSize = 25;
            private string Divider = $"<size={HeaderSize}>------------------------------</size>";

            private class PlayerInfo
            {
                public Timer Timer;
                public ProfileController ProfileController;
            }

            private float DisplayDistanceSquared => Mathf.Pow(_pluginConfig.DebugDisplayDistance, 2);

            private StringBuilder _sb = new StringBuilder(200);
            private Dictionary<ulong, PlayerInfo> _playerInfo = new Dictionary<ulong, PlayerInfo>();

            public void SetPlayerProfile(BasePlayer player, ProfileController profileController)
            {
                GetOrCreatePlayerInfo(player).ProfileController = profileController;
            }

            public void ShowAllRepeatedly(BasePlayer player, int duration = -1)
            {
                var playerInfo = GetOrCreatePlayerInfo(player);

                // Only show initial debug info if there is no pending timer.
                // This is done to avoid the text looking broken when the number of lines change and overlaps.
                if (playerInfo.Timer == null || playerInfo.Timer.Destroyed)
                {
                    ShowNearbyAdapters(player, player.transform.position, playerInfo);
                }

                if (playerInfo.Timer != null && !playerInfo.Timer.Destroyed)
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
                    duration = DefaultDisplayDuration;

                // Ensure repetitions is not 0 since that would result in infintire repetitions.
                var repetitions = Math.Max(duration / DisplayIntervalDuration, 1);

                playerInfo.Timer = _pluginInstance.timer.Repeat(DisplayIntervalDuration - 0.2f, repetitions, () =>
                {
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

            private void ShowEntityInfo(BasePlayer player, EntityAdapterBase adapter, PlayerInfo playerInfo)
            {
                var entityData = adapter.EntityData;
                var entityController = adapter.Controller;
                var profileController = entityController.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_pluginInstance.GetMessage(player, Lang.ShowHeaderEntity, entityData.ShortPrefabName)}</size>");
                _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelProfile, profileController.Profile.Name));
                _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelMonument, adapter.Monument.AliasOrShortName, entityController.Adapters.Count));

                if (entityData.Skin != 0)
                    _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelSkin, entityData.Skin));

                if (entityData.Scale != 1)
                    _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelScale, entityData.Scale));

                var singleEntityAdapter = adapter as SingleEntityAdapter;
                if (singleEntityAdapter != null)
                {
                    var vehicleVendor = singleEntityAdapter.Entity as VehicleVendor;
                    if (vehicleVendor != null)
                    {
                        var vehicleSpawner = vehicleVendor.GetVehicleSpawner();
                        if (vehicleSpawner != null)
                        {
                            Ddraw.Arrow(player, adapter.Position + new Vector3(0, 1.5f, 0), vehicleSpawner.transform.position, 0.25f, color, DisplayIntervalDuration);
                        }
                    }
                }

                var cctvIdentifier = entityData.CCTV?.RCIdentifier;
                if (cctvIdentifier != null)
                {
                    var identifier = (adapter as CCTVEntityAdapter)?.GetIdentifier();
                    if (identifier != null)
                        _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelRCIdentifier, identifier));
                }

                Ddraw.Text(player, adapter.Position, _sb.ToString(), color, DisplayIntervalDuration);
            }

            private void ShowSpawnPointInfo(BasePlayer player, SpawnPointAdapter adapter, SpawnGroupAdapter spawnGroupAdapter, PlayerInfo playerInfo, bool showGroupInfo)
            {
                var spawnPointData = adapter.SpawnPointData;
                var entityController = adapter.Controller;
                var profileController = entityController.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);

                var spawnGroupData = spawnGroupAdapter.SpawnGroupData;

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_pluginInstance.GetMessage(player, Lang.ShowHeaderSpawnPoint, spawnGroupData.Name)}</size>");
                _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelProfile, profileController.Profile.Name));
                _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelMonument, adapter.Monument.AliasOrShortName, entityController.Adapters.Count));

                var booleanProperties = new List<string>();

                if (spawnPointData.Exclusive)
                    booleanProperties.Add(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnPointExclusive));

                if (spawnPointData.RandomRotation)
                    booleanProperties.Add(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnPointRandomRotation));

                if (spawnPointData.DropToGround)
                    booleanProperties.Add(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnPointDropsToGround));

                if (spawnPointData.CheckSpace)
                    booleanProperties.Add(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnPointChecksSpace));

                if (booleanProperties.Count > 0)
                    _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelFlags, string.Join(" | ", booleanProperties)));

                if (spawnPointData.RandomRadius > 0)
                    _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnPointRandomRadius, spawnPointData.RandomRadius));

                if (showGroupInfo)
                {
                    _sb.AppendLine(Divider);
                    _sb.AppendLine($"<size=25>{_pluginInstance.GetMessage(player, Lang.ShowHeaderSpawnGroup, spawnGroupData.Name)}</size>");

                    _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnGroupPoints, spawnGroupData.SpawnPoints.Count));

                    var groupBooleanProperties = new List<string>();

                    if (spawnGroupData.PreventDuplicates)
                        groupBooleanProperties.Add(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnGroupPreventDuplicates));

                    if (groupBooleanProperties.Count > 0)
                        _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelFlags, string.Join(" | ", groupBooleanProperties)));

                    _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnGroupPopulation, spawnGroupAdapter.SpawnGroup.currentPopulation, spawnGroupData.MaxPopulation));
                    _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnGroupRespawnPerTick, spawnGroupData.SpawnPerTickMin, spawnGroupData.SpawnPerTickMax));
                    _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnGroupRespawnDelay, FormatTime(spawnGroupData.RespawnDelayMin), FormatTime(spawnGroupData.RespawnDelayMax)));

                    var nextSpawnTime = spawnGroupAdapter.SpawnGroup.GetTimeToNextSpawn();

                    _sb.AppendLine(_pluginInstance.GetMessage(
                        player,
                        Lang.ShowLabelSpawnGroupNextSpawn,
                        nextSpawnTime <= 0
                            ? _pluginInstance.GetMessage(player, Lang.ShowLabelSpawnGroupNextSpawnQueued)
                            : FormatTime(Mathf.CeilToInt(nextSpawnTime))
                    ));

                    if (spawnGroupData.Prefabs.Count > 0)
                    {
                        _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnGroupEntities));
                        foreach (var prefabEntry in spawnGroupData.Prefabs)
                        {
                            _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnGroupEntityDetail, prefabEntry.PrefabName, prefabEntry.Weight));
                        }
                    }
                    else
                    {
                        _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnGroupNoEntities));
                    }

                    foreach (var otherAdapter in spawnGroupAdapter.Adapters)
                    {
                        Ddraw.Arrow(player, otherAdapter.Position + new Vector3(0, 0.5f, 0), adapter.Position + new Vector3(0, 0.5f, 0), 0.25f, color, DisplayIntervalDuration);
                    }
                }

                Ddraw.Sphere(player, adapter.Position, 0.5f, color, DisplayIntervalDuration);
                Ddraw.Text(player, adapter.Position, _sb.ToString(), color, DisplayIntervalDuration);
            }

            private void ShowPasteInfo(BasePlayer player, PasteAdapter adapter, PlayerInfo playerInfo)
            {
                var pasteData = adapter.PasteData;
                var entityController = adapter.Controller;
                var profileController = entityController.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_pluginInstance.GetMessage(player, Lang.ShowHeaderPaste, pasteData.Filename)}</size>");
                _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelProfile, profileController.Profile.Name));
                _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelMonument, adapter.Monument.AliasOrShortName, entityController.Adapters.Count));

                Ddraw.Text(player, adapter.Position, _sb.ToString(), color, DisplayIntervalDuration);
            }

            private void ShowCustomAddonInfo(BasePlayer player, CustomAddonAdapter adapter, PlayerInfo playerInfo)
            {
                var customAddonData = adapter.CustomAddonData;
                var entityController = adapter.Controller;
                var profileController = entityController.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);

                var addonDefinition = adapter.AddonDefinition;

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_pluginInstance.GetMessage(player, Lang.ShowHeaderCustom, customAddonData.AddonName)}</size>");
                _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelPlugin, addonDefinition.OwnerPlugin.Name));
                _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelProfile, profileController.Profile.Name));
                _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelMonument, adapter.Monument.AliasOrShortName, entityController.Adapters.Count));

                addonDefinition.AddDisplayInfo?.Invoke(adapter.Component, customAddonData.GetSerializedData(), _sb);

                Ddraw.Text(player, adapter.Position, _sb.ToString(), color, DisplayIntervalDuration);
            }

            private void ShowNearbyAdapters(BasePlayer player, Vector3 playerPosition, PlayerInfo playerInfo)
            {
                if (!player.IsConnected)
                {
                    playerInfo.Timer.Destroy();
                    _playerInfo.Remove(player.userID);
                    return;
                }

                var isAdmin = player.IsAdmin;
                if (!isAdmin)
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                foreach (var adapter in _pluginInstance._profileManager.GetEnabledAdapters<BaseAdapter>())
                {
                    var entityAdapter = adapter as EntityAdapterBase;
                    if (entityAdapter != null)
                    {
                        if ((playerPosition - entityAdapter.Position).sqrMagnitude <= DisplayDistanceSquared)
                        {
                            ShowEntityInfo(player, entityAdapter, playerInfo);
                        }

                        continue;
                    }

                    var spawnGroupAdapter = adapter as SpawnGroupAdapter;
                    if (spawnGroupAdapter != null)
                    {
                        SpawnPointAdapter closestSpawnPointAdapter = null;
                        var closestDistanceSquared = float.MaxValue;

                        foreach (var spawnPointAdapter in spawnGroupAdapter.Adapters)
                        {
                            var adapterDistanceSquared = (spawnPointAdapter.Position - playerPosition).sqrMagnitude;
                            if (adapterDistanceSquared < closestDistanceSquared)
                            {
                                closestSpawnPointAdapter = spawnPointAdapter;
                                closestDistanceSquared = adapterDistanceSquared;
                            }
                        }

                        if (closestDistanceSquared <= DisplayDistanceSquared)
                        {
                            foreach (var spawnPointAdapter in spawnGroupAdapter.Adapters)
                            {
                                ShowSpawnPointInfo(player, spawnPointAdapter, spawnGroupAdapter, playerInfo, showGroupInfo: spawnPointAdapter == closestSpawnPointAdapter);
                            }
                        }

                        continue;
                    }

                    var pasteAdapter = adapter as PasteAdapter;
                    if (pasteAdapter != null)
                    {
                        if ((playerPosition - pasteAdapter.Position).sqrMagnitude <= DisplayDistanceSquared)
                        {
                            ShowPasteInfo(player, pasteAdapter, playerInfo);
                        }

                        continue;
                    }

                    var customAddonAdapter = adapter as CustomAddonAdapter;
                    if (customAddonAdapter != null)
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
                PlayerInfo playerInfo;
                if (!_playerInfo.TryGetValue(player.userID, out playerInfo))
                {
                    playerInfo = new PlayerInfo();
                    _playerInfo[player.userID] = playerInfo;
                }
                return playerInfo;
            }
        }

        #endregion

        #region Profile Management

        private enum ProfileState { Loading, Loaded, Unloading, Unloaded }

        private struct SpawnQueueItem
        {
            public BaseIdentifiableData Data;
            public BaseMonument Monument;
            public ICollection<BaseMonument> MonumentList;

            public SpawnQueueItem(BaseIdentifiableData data, ICollection<BaseMonument> monumentList)
            {
                Data = data;
                Monument = null;
                MonumentList = monumentList;
            }

            public SpawnQueueItem(BaseIdentifiableData data, BaseMonument monument)
            {
                Data = data;
                Monument = monument;
                MonumentList = null;
            }
        }

        private class ProfileController
        {
            public Profile Profile { get; private set; }
            public ProfileState ProfileState { get; private set; } = ProfileState.Unloaded;
            public WaitUntil WaitUntilLoaded;
            public WaitUntil WaitUntilUnloaded;

            private CoroutineManager _coroutineManager = new CoroutineManager();
            private Dictionary<BaseIdentifiableData, BaseController> _controllersByData = new Dictionary<BaseIdentifiableData, BaseController>();
            private Queue<SpawnQueueItem> _spawnQueue = new Queue<SpawnQueueItem>();

            public bool IsEnabled =>
                _pluginData.IsProfileEnabled(Profile.Name);

            public ProfileController(Profile profile, bool startLoaded = false)
            {
                Profile = profile;
                WaitUntilLoaded = new WaitUntil(() => ProfileState == ProfileState.Loaded);
                WaitUntilUnloaded = new WaitUntil(() => ProfileState == ProfileState.Unloaded);

                if (startLoaded || (profile.IsEmpty() && IsEnabled))
                    ProfileState = ProfileState.Loaded;
            }

            public void OnControllerKilled(BaseController controller) =>
                _controllersByData.Remove(controller.Data);

            public void StartCoroutine(IEnumerator enumerator) =>
                _coroutineManager.StartCoroutine(enumerator);

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
                        var adapterOfType = adapter as T;
                        if (adapterOfType != null)
                        {
                            yield return adapterOfType;
                            continue;
                        }

                        var spawnGroupAdapter = adapter as SpawnGroupAdapter;
                        if (spawnGroupAdapter != null)
                        {
                            foreach (var childAdapter in spawnGroupAdapter.Adapters.OfType<T>())
                            {
                                yield return childAdapter;
                            }
                        }
                    }
                }
            }

            public void Load(ProfileCounts profileCounts = null)
            {
                if (ProfileState == ProfileState.Loading || ProfileState == ProfileState.Loaded)
                    return;

                ProfileState = ProfileState.Loading;
                StartCoroutine(LoadRoutine(profileCounts));
            }

            public void PreUnload()
            {
                _coroutineManager.Destroy();

                foreach (var controller in _controllersByData.Values)
                {
                    controller.PreUnload();
                }
            }

            public void Unload()
            {
                if (ProfileState == ProfileState.Unloading || ProfileState == ProfileState.Unloaded)
                    return;

                ProfileState = ProfileState.Unloading;
                CoroutineManager.StartGlobalCoroutine(UnloadRoutine());
            }

            public void Reload(Profile newProfileData)
            {
                _coroutineManager.StopAll();
                StartCoroutine(ReloadRoutine(newProfileData));
            }

            public IEnumerator PartialLoadForLateMonument(ICollection<BaseIdentifiableData> dataList, BaseMonument monument)
            {
                foreach (var data in dataList)
                {
                    Enqueue(new SpawnQueueItem(data, monument));
                }

                yield return WaitUntilLoaded;
            }

            public void SpawnNewData(BaseIdentifiableData data, ICollection<BaseMonument> monumentList)
            {
                if (ProfileState == ProfileState.Unloading || ProfileState == ProfileState.Unloaded)
                    return;

                Enqueue(new SpawnQueueItem(data, monumentList));
            }

            public void Rename(string newName)
            {
                _pluginData.RenameProfileReferences(Profile.Name, newName);
                Profile.CopyTo(newName);
            }

            public void Enable()
            {
                if (IsEnabled)
                    return;

                _pluginData.SetProfileEnabled(Profile.Name);
                Load();
            }

            public void Disable()
            {
                if (!IsEnabled)
                    return;

                PreUnload();
                Unload();
            }

            public void Clear()
            {
                if (!IsEnabled)
                {
                    Profile.MonumentDataMap.Clear();
                    Profile.Save();
                    return;
                }

                _coroutineManager.StopAll();
                StartCoroutine(ClearRoutine());
            }

            private BaseController GetController(BaseIdentifiableData data)
            {
                BaseController controller;
                return _controllersByData.TryGetValue(data, out controller)
                    ? controller
                    : null;
            }

            private BaseController EnsureController(BaseIdentifiableData data)
            {
                var controller = GetController(data);
                if (controller == null)
                {
                    controller = ControllerFactory.Instance.CreateController(this, data);
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
                    ProfileState = ProfileState.Loading;
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
                    var matchingMonuments = _pluginInstance.GetMonumentsByAliasOrShortName(monumentAliasOrShortName);
                    if (matchingMonuments == null)
                        continue;

                    if (profileCounts != null)
                    {
                        profileCounts.EntityCount += matchingMonuments.Count * monumentData.Entities.Count;
                        profileCounts.SpawnPointCount += matchingMonuments.Count * monumentData.NumSpawnPoints;
                        profileCounts.PasteCount += matchingMonuments.Count * monumentData.Pastes.Count;
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

                SpawnQueueItem queueItem;
                while (_spawnQueue.TryDequeue(out queueItem))
                {
                    _pluginInstance?.TrackStart();
                    var controller = EnsureController(queueItem.Data);
                    _pluginInstance?.TrackEnd();

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

                ProfileState = ProfileState.Loaded;
            }

            private IEnumerator LoadRoutine(ProfileCounts profileCounts)
            {
                EnqueueAll(profileCounts);
                yield return WaitUntilLoaded;
            }

            private IEnumerator UnloadRoutine()
            {
                foreach (var controller in _controllersByData.Values.ToArray())
                {
                    yield return controller.KillRoutine();
                }

                ProfileState = ProfileState.Unloaded;
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
                Profile.Save();
                ProfileState = ProfileState.Loaded;
            }
        }

        private class ProfileCounts
        {
            public int EntityCount;
            public int SpawnPointCount;
            public int PasteCount;
        }

        private struct ProfileInfo
        {
            public static ProfileInfo[] GetList(ProfileManager profileManager)
            {
                var profileNameList = Profile.GetProfileNames();
                var profileInfoList = new ProfileInfo[profileNameList.Length];

                for (var i = 0; i < profileNameList.Length; i++)
                {
                    var profileName = profileNameList[i];
                    profileInfoList[i] = new ProfileInfo
                    {
                        Name = profileName,
                        Enabled = _pluginData.EnabledProfiles.Contains(profileName),
                        Profile = profileManager.GetCachedProfileController(profileName)?.Profile
                    };
                }

                return profileInfoList;
            }

            public string Name;
            public bool Enabled;
            public Profile Profile;
        }

        private class ProfileManager
        {
            private List<ProfileController> _profileControllers = new List<ProfileController>();

            public IEnumerator LoadAllProfilesRoutine()
            {
                foreach (var profileName in _pluginData.EnabledProfiles.ToArray())
                {
                    ProfileController controller;
                    try
                    {
                        controller = GetProfileController(profileName);
                    }
                    catch (Exception ex)
                    {
                        _pluginData.SetProfileDisabled(profileName);
                        _pluginInstance.LogError($"Disabled profile {profileName} due to error: {ex.Message}");
                        continue;
                    }

                    if (controller == null)
                    {
                        _pluginData.SetProfileDisabled(profileName);
                        _pluginInstance.LogWarning($"Disabled profile {profileName} because its data file was not found.");
                        continue;
                    }

                    var profileCounts = new ProfileCounts();

                    controller.Load(profileCounts);
                    yield return controller.WaitUntilLoaded;

                    var profile = controller.Profile;
                    var byAuthor = !string.IsNullOrWhiteSpace(profile.Author) ? $" by {profile.Author}" : string.Empty;

                    var spawnablesSummaryList = new List<string>();
                    if (profileCounts.EntityCount > 0)
                        spawnablesSummaryList.Add($"{profileCounts.EntityCount} entities");

                    if (profileCounts.SpawnPointCount > 0)
                        spawnablesSummaryList.Add($"{profileCounts.SpawnPointCount} spawn points");

                    if (profileCounts.PasteCount > 0)
                        spawnablesSummaryList.Add($"{profileCounts.PasteCount} pastes");

                    var spawnablesSummary = spawnablesSummaryList.Count > 0
                        ? string.Join(", ", spawnablesSummaryList)
                        : "Empty";

                    _pluginInstance.Puts($"Loaded profile {profile.Name}{byAuthor} ({spawnablesSummary}).");
                }
            }

            public void UnloadAllProfiles()
            {
                foreach (var controller in _profileControllers)
                    controller.PreUnload();

                CoroutineManager.StartGlobalCoroutine(UnloadAllProfilesRoutine());
            }

            public IEnumerator PartialLoadForLateMonumentRoutine(BaseMonument monument)
            {
                foreach (var controller in _profileControllers)
                {
                    if (!controller.IsEnabled)
                        continue;

                    MonumentData monumentData;
                    if (!controller.Profile.MonumentDataMap.TryGetValue(monument.AliasOrShortName, out monumentData))
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

                var profile = Profile.LoadIfExists(profileName);
                if (profile != null)
                {
                    var controller = new ProfileController(profile);
                    _profileControllers.Add(controller);
                    return controller;
                }

                return null;
            }

            public ProfileController GetPlayerProfileController(string userId)
            {
                string profileName;
                return _pluginData.SelectedProfiles.TryGetValue(userId, out profileName)
                    ? GetProfileController(profileName)
                    : null;
            }

            public ProfileController GetPlayerProfileControllerOrDefault(string userId)
            {
                var controller = GetPlayerProfileController(userId);
                if (controller != null)
                    return controller;

                controller = GetProfileController(DefaultProfileName);
                return controller != null && controller.IsEnabled
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

                return Profile.Exists(profileName);
            }

            public ProfileController CreateProfile(string profileName, string authorName)
            {
                var profile = Profile.Create(profileName, authorName);
                var controller = new ProfileController(profile, startLoaded: true);
                _profileControllers.Add(controller);
                return controller;
            }

            public IEnumerable<ProfileController> GetEnabledProfileControllers()
            {
                foreach (var profileControler in _profileControllers)
                {
                    if (profileControler.IsEnabled)
                    {
                        yield return profileControler;
                    }
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

        private abstract class BaseIdentifiableData
        {
            [JsonProperty("Id", Order = -5)]
            public Guid Id;
        }

        private abstract class BaseTransformData : BaseIdentifiableData
        {
            [JsonProperty("Position", Order = -4)]
            public Vector3 Position;

            // Kept for backwards compatibility.
            [JsonProperty("RotationAngle", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float DeprecatedRotationAngle { set { RotationAngles = new Vector3(0, value, 0); } }

            [JsonProperty("RotationAngles", Order = -3, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Vector3 RotationAngles;

            [JsonProperty("OnTerrain", Order = -2, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool OnTerrain = false;
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

        private class EntityData : BaseTransformData
        {
            [JsonProperty("PrefabName")]
            public string PrefabName;

            private string _shortPrefabName;

            [JsonIgnore]
            public string ShortPrefabName
            {
                get
                {
                    if (_shortPrefabName == null)
                        _shortPrefabName = GetShortName(PrefabName);

                    return _shortPrefabName;
                }
            }

            [JsonProperty("Skin", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ulong Skin;

            [JsonProperty("Scale", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(1f)]
            public float Scale = 1;

            [JsonProperty("BuildingBlock", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public BuildingBlockInfo BuildingBlock;

            [JsonProperty("CCTV", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public CCTVInfo CCTV;

            [JsonProperty("SignArtistImages", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public SignArtistImage[] SignArtistImages;
        }

        #endregion

        #region Spawn Group Data

        private class SpawnPointData : BaseTransformData
        {
            [JsonProperty("Exclusive")]
            public bool Exclusive = true;

            [JsonProperty("DropToGround")]
            public bool DropToGround = true;

            [JsonProperty("CheckSpace")]
            public bool CheckSpace = false;

            [JsonProperty("RandomRotation")]
            public bool RandomRotation = false;

            [JsonProperty("RandomRadius")]
            public float RandomRadius = 0;
        }

        private class WeightedPrefabData
        {
            [JsonProperty("PrefabName")]
            public string PrefabName;

            private string _shortPrefabName;

            [JsonIgnore]
            public string ShortPrefabName
            {
                get
                {
                    if (_shortPrefabName == null)
                        _shortPrefabName = GetShortName(PrefabName);

                    return _shortPrefabName;
                }
            }

            [JsonProperty("Weight")]
            public int Weight = 1;
        }

        private class SpawnGroupData : BaseIdentifiableData
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
            public float RespawnDelayMin = 30;

            [JsonProperty("RespawnDelayMax")]
            public float RespawnDelayMax = 60;

            [JsonProperty("PreventDuplicates")]
            public bool PreventDuplicates;

            [JsonProperty("Prefabs")]
            public List<WeightedPrefabData> Prefabs = new List<WeightedPrefabData>();

            [JsonProperty("SpawnPoints")]
            public List<SpawnPointData> SpawnPoints = new List<SpawnPointData>();

            public List<WeightedPrefabData> FindPrefabMatches(string prefabName)
            {
                var matches = new List<WeightedPrefabData>();

                // Search for exact matches first.
                foreach (var prefabData in Prefabs)
                {
                    if (prefabData.PrefabName == prefabName)
                        matches.Add(prefabData);
                }

                if (matches.Count > 0)
                    return matches;

                // No exact matches found, so search for exact matches by short prefab name.
                foreach (var prefabData in Prefabs)
                {
                    if (prefabData.ShortPrefabName == prefabName)
                        matches.Add(prefabData);
                }

                if (matches.Count > 0)
                    return matches;

                // No exact matches for short prefab name, so search for partial matches.
                foreach (var prefabData in Prefabs)
                {
                    if (prefabData.PrefabName.Contains(prefabName))
                        matches.Add(prefabData);
                }

                return matches;
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
                var jObject = data as JObject;
                if (jObject == null)
                {
                    jObject = JObject.FromObject(data);
                }
                PluginData = jObject;
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

            var addonTypeEntity = GetMessage(player, Lang.AddonTypeEntity);
            var addonTypePaste = GetMessage(player, Lang.AddonTypePaste);
            var addonTypeSpawnPoint = GetMessage(player, Lang.AddonTypeSpawnPoint);
            var addonTypeCustom = GetMessage(player, Lang.AddonTypeCustom);

            foreach (var monumentEntry in profile.MonumentDataMap)
            {
                var monumentName = monumentEntry.Key;
                var monumentData = monumentEntry.Value;

                var entryMap = new Dictionary<string, ProfileSummaryEntry>();

                foreach (var entityData in monumentData.Entities)
                {
                    ProfileSummaryEntry summaryEntry;
                    if (!entryMap.TryGetValue(entityData.ShortPrefabName, out summaryEntry))
                    {
                        summaryEntry = new ProfileSummaryEntry
                        {
                            MonumentName = monumentName,
                            AddonType = addonTypeEntity,
                            AddonName = entityData.ShortPrefabName,
                        };
                        entryMap[entityData.ShortPrefabName] = summaryEntry;
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
                    ProfileSummaryEntry summaryEntry;
                    if (!entryMap.TryGetValue(pasteData.Filename, out summaryEntry))
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
                    ProfileSummaryEntry summaryEntry;
                    if (!entryMap.TryGetValue(customAddonData.AddonName, out summaryEntry))
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
            public int NumSpawnables => Entities.Count
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

            public IEnumerable<BaseIdentifiableData> GetSpawnablesLazy()
            {
                foreach (var entityData in Entities)
                    yield return entityData;

                foreach (var spawnGroupData in SpawnGroups)
                    yield return spawnGroupData;

                foreach (var pasteData in Pastes)
                    yield return pasteData;

                foreach (var customAddonData in CustomAddons)
                    yield return customAddonData;
            }

            public ICollection<BaseIdentifiableData> GetSpawnables()
            {
                var list = new List<BaseIdentifiableData>(NumSpawnables);
                foreach (var spawnable in GetSpawnablesLazy())
                {
                    list.Add(spawnable);
                }
                return list;
            }

            public void AddData(BaseIdentifiableData data)
            {
                var entityData = data as EntityData;
                if (entityData != null)
                {
                    Entities.Add(entityData);
                    return;
                }

                var spawnGroupData = data as SpawnGroupData;
                if (spawnGroupData != null)
                {
                    SpawnGroups.Add(spawnGroupData);
                    return;
                }

                var pasteData = data as PasteData;
                if (pasteData != null)
                {
                    Pastes.Add(pasteData);
                    return;
                }

                var customAddonData = data as CustomAddonData;
                if (customAddonData != null)
                {
                    CustomAddons.Add(customAddonData);
                    return;
                }

                _pluginInstance?.LogError($"AddData not implemented for type: {data.GetType()}");
            }

            public bool RemoveData(BaseIdentifiableData data)
            {
                var entityData = data as EntityData;
                if (entityData != null)
                {
                    return Entities.Remove(entityData);
                }

                var spawnGroupData = data as SpawnGroupData;
                if (spawnGroupData != null)
                {
                    return SpawnGroups.Remove(spawnGroupData);
                }

                var pasteData = data as PasteData;
                if (pasteData != null)
                {
                    return Pastes.Remove(pasteData);
                }

                var customAddonData = data as CustomAddonData;
                if (customAddonData != null)
                {
                    return CustomAddons.Remove(customAddonData);
                }

                _pluginInstance.LogError($"RemoveData not implemented for type: {data.GetType()}");
                return false;
            }
        }

        private static class ProfileDataMigration
        {
            public static bool MigrateToLatest(Profile data)
            {
                // Using single | to avoid short-circuiting.
                return MigrateV0ToV1(data)
                    | MigrateV1ToV2(data);
            }

            public static bool MigrateV0ToV1(Profile data)
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
                            if (entityData.ShortPrefabName == "big_wheel"
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

            public static bool MigrateV1ToV2(Profile data)
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
        }

        private class Profile
        {
            public const string OriginalSuffix = "_original";

            public static string[] GetProfileNames()
            {
                var filenameList = Interface.Oxide.DataFileSystem.GetFiles(_pluginInstance.Name);
                for (var i = 0; i < filenameList.Length; i++)
                {
                    var filename = filenameList[i];
                    var start = filename.LastIndexOf(System.IO.Path.DirectorySeparatorChar) + 1;
                    var end = filename.LastIndexOf(".");
                    filenameList[i] = filename.Substring(start, end - start);
                }

                return filenameList;
            }

            private static string GetActualFileName(string profileName)
            {
                foreach (var name in GetProfileNames())
                {
                    if (name.ToLower() == profileName.ToLower())
                        return name;
                }
                return profileName;
            }

            private static string GetProfilePath(string profileName) => $"{_pluginInstance.Name}/{profileName}";

            public static bool Exists(string profileName) =>
                !profileName.EndsWith(OriginalSuffix)
                && Interface.Oxide.DataFileSystem.ExistsDatafile(GetProfilePath(profileName));

            public static Profile Load(string profileName)
            {
                if (profileName.EndsWith(OriginalSuffix))
                    return null;

                var profile = Interface.Oxide.DataFileSystem.ReadObject<Profile>(GetProfilePath(profileName)) ?? new Profile();
                profile.Name = GetActualFileName(profileName);

                var originalSchemaVersion = profile.SchemaVersion;

                if (ProfileDataMigration.MigrateToLatest(profile))
                    _pluginInstance.LogWarning($"Profile {profile.Name} has been automatically migrated.");

                // Backfill ids if missing.
                foreach (var monumentData in profile.MonumentDataMap.Values)
                {
                    foreach (var entityData in monumentData.Entities)
                    {
                        if (entityData.Id == Guid.Empty)
                            entityData.Id = Guid.NewGuid();
                    }
                }

                if (profile.SchemaVersion != originalSchemaVersion)
                    profile.Save();

                return profile;
            }

            public static Profile LoadIfExists(string profileName) =>
                Exists(profileName) ? Load(profileName) : null;

            public static Profile LoadDefaultProfile() => Load(DefaultProfileName);

            public static Profile Create(string profileName, string authorName)
            {
                var profile = new Profile
                {
                    Name = profileName,
                    Author = authorName,
                };
                ProfileDataMigration.MigrateToLatest(profile);
                profile.Save();
                return profile;
            }

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

            public void Save() =>
                Interface.Oxide.DataFileSystem.WriteObject(GetProfilePath(Name), this);

            public void SaveAsOriginal() =>
                Interface.Oxide.DataFileSystem.WriteObject(GetProfilePath(Name) + OriginalSuffix, this);

            public Profile LoadOriginalIfExists()
            {
                var originalPath = GetProfilePath(Name) + OriginalSuffix;
                if (!Interface.Oxide.DataFileSystem.ExistsDatafile(originalPath))
                    return null;

                var original = Interface.Oxide.DataFileSystem.ReadObject<Profile>(originalPath) ?? new Profile();
                original.Name = Name;
                return original;
            }

            public void CopyTo(string newName)
            {
                var original = LoadOriginalIfExists();
                if (original != null)
                {
                    original.Name = newName;
                    original.SaveAsOriginal();
                }

                Name = newName;
                Save();
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

            public Dictionary<string, Dictionary<string, int>> GetEntityAggregates()
            {
                var aggregateData = new Dictionary<string, Dictionary<string, int>>();

                foreach (var entry in MonumentDataMap)
                {
                    var monumentAliasOrShortName = entry.Key;
                    var monumentData = entry.Value;

                    if (monumentData.Entities.Count == 0)
                        continue;

                    Dictionary<string, int> monumentAggregateData;
                    if (!aggregateData.TryGetValue(monumentAliasOrShortName, out monumentAggregateData))
                    {
                        monumentAggregateData = new Dictionary<string, int>();
                        aggregateData[monumentAliasOrShortName] = monumentAggregateData;
                    }

                    foreach (var entityData in monumentData.Entities)
                    {
                        int count;
                        if (!monumentAggregateData.TryGetValue(entityData.PrefabName, out count))
                            count = 0;

                        monumentAggregateData[entityData.PrefabName] = count + 1;
                    }
                }

                return aggregateData;
            }

            public void AddData(string monumentAliasOrShortName, BaseIdentifiableData data)
            {
                EnsureMonumentData(monumentAliasOrShortName).AddData(data);
                Save();
            }

            public bool RemoveData(BaseIdentifiableData data, out string monumentAliasOrShortName)
            {
                foreach (var entry in MonumentDataMap)
                {
                    if (entry.Value.RemoveData(data))
                    {
                        monumentAliasOrShortName = entry.Key;
                        Save();
                        return true;
                    }
                }

                monumentAliasOrShortName = null;
                return false;
            }

            public bool RemoveSpawnPoint(SpawnGroupData spawnGroupData, SpawnPointData spawnPointData)
            {
                var removed = spawnGroupData.SpawnPoints.Remove(spawnPointData);
                if (spawnGroupData.SpawnPoints.Count > 0)
                {
                    Save();
                    return removed;
                }

                string monumentAliasOrShortName;
                return RemoveData(spawnGroupData, out monumentAliasOrShortName);
            }

            private MonumentData EnsureMonumentData(string monumentAliasOrShortName)
            {
                MonumentData monumentData;
                if (!MonumentDataMap.TryGetValue(monumentAliasOrShortName, out monumentData))
                {
                    monumentData = new MonumentData();
                    MonumentDataMap[monumentAliasOrShortName] = monumentData;
                }

                return monumentData;
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

            public static bool MigrateToLatest(StoredData data)
            {
                // Using single | to avoid short-circuiting.
                return MigrateV0ToV1(data)
                    | MigrateV1ToV2(data);
            }

            public static bool MigrateV0ToV1(StoredData data)
            {
                if (data.DataFileVersion != 0)
                    return false;

                data.DataFileVersion++;

                var contentChanged = false;

                if (data.DeprecatedMonumentMap != null)
                {
                    foreach (var monumentEntry in data.DeprecatedMonumentMap.ToArray())
                    {
                        var alias = monumentEntry.Key;
                        var entityList = monumentEntry.Value;

                        string newAlias;
                        if (MigrateMonumentNames.TryGetValue(alias, out newAlias))
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
                                contentChanged = true;
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

            public static bool MigrateV1ToV2(StoredData data)
            {
                if (data.DataFileVersion != 1)
                    return false;

                data.DataFileVersion++;

                var profile = new Profile
                {
                    Name = DefaultProfileName,
                };

                if (data.DeprecatedMonumentMap != null)
                    profile.DeprecatedMonumentMap = data.DeprecatedMonumentMap;

                profile.Save();

                data.DeprecatedMonumentMap = null;
                data.EnabledProfiles.Add(DefaultProfileName);

                return true;
            }
        }

        private class StoredData
        {
            public static StoredData Load()
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(_pluginInstance.Name) ?? new StoredData();

                var originalDataFileVersion = data.DataFileVersion;

                if (StoredDataMigration.MigrateToLatest(data))
                    _pluginInstance.LogWarning("Data file has been automatically migrated.");

                if (data.DataFileVersion != originalDataFileVersion)
                    data.Save();

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

            public void Save() =>
                Interface.Oxide.DataFileSystem.WriteObject(_pluginInstance.Name, this);

            public bool IsProfileEnabled(string profileName) => EnabledProfiles.Contains(profileName);

            public void SetProfileEnabled(string profileName)
            {
                EnabledProfiles.Add(profileName);
                Save();
            }

            public void SetProfileDisabled(string profileName)
            {
                if (!EnabledProfiles.Remove(profileName))
                    return;

                foreach (var entry in SelectedProfiles.ToArray())
                {
                    if (entry.Value == profileName)
                        SelectedProfiles.Remove(entry.Key);
                }

                Save();
            }

            public void RenameProfileReferences(string oldName, string newName)
            {
                foreach (var entry in SelectedProfiles.ToArray())
                {
                    if (entry.Value == oldName)
                        SelectedProfiles[entry.Key] = newName;
                }

                if (EnabledProfiles.Remove(oldName))
                    EnabledProfiles.Add(newName);

                Save();
            }

            public string GetSelectedProfileName(string userId)
            {
                string profileName;
                if (SelectedProfiles.TryGetValue(userId, out profileName))
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

        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("Debug", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Debug = false;

            [JsonProperty("DebugDisplayDistance")]
            public float DebugDisplayDistance = 150;

            [JsonProperty("DeployableOverrides")]
            public Dictionary<string, string> DeployableOverrides = new Dictionary<string, string>
            {
                ["arcade.machine.chippy"] = "assets/bundled/prefabs/static/chippyarcademachine.static.prefab",
                ["autoturret"] = "assets/content/props/sentry_scientists/sentry.bandit.static.prefab",
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
                ["telephone"] = "assets/bundled/prefabs/autospawn/phonebooth/phonebooth.static.prefab",
                ["vending.machine"] = "assets/prefabs/deployable/vendingmachine/npcvendingmachine.prefab",
                ["wall.frame.shopfront.metal"] = "assets/bundled/prefabs/static/wall.frame.shopfront.metal.static.prefab",
                ["workbench1"] = "assets/bundled/prefabs/static/workbench1.static.prefab",
                ["workbench2"] = "assets/bundled/prefabs/static/workbench2.static.prefab",
            };
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #region Configuration Boilerplate

        private class SerializableConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

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

        private bool MaybeUpdateConfig(SerializableConfiguration config)
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
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                            changed = true;
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

        protected override void LoadDefaultConfig() => _pluginConfig = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _pluginConfig = Config.ReadObject<Configuration>();
                if (_pluginConfig == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_pluginConfig))
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
            Config.WriteObject(_pluginConfig, true);
        }

        #endregion

        #endregion

        #region Localization

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private string GetMessage(IPlayer player, string messageName, params object[] args) =>
            GetMessage(player.Id, messageName, args);

        private string GetMessage(BasePlayer player, string messageName, params object[] args) =>
            GetMessage(player.UserIDString, messageName, args);

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player, messageName), args));

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.UserIDString, messageName), args));

        private string GetAuthorSuffix(IPlayer player, string author)
        {
            return !string.IsNullOrWhiteSpace(author)
                ? GetMessage(player, Lang.ProfileByAuthor, author)
                : string.Empty;
        }

        private string GetAddonName(IPlayer player, BaseIdentifiableData data)
        {
            var entityData = data as EntityData;
            if (entityData != null)
            {
                return entityData.ShortPrefabName;
            }

            var spawnPointData = data as SpawnPointData;
            if (spawnPointData != null)
            {
                return GetMessage(player, Lang.AddonTypeSpawnPoint);
            }

            var pasteData = data as PasteData;
            if (pasteData != null)
            {
                return pasteData.Filename;
            }

            return GetMessage(player, Lang.AddonTypeUnknown);
        }

        private class Lang
        {
            public const string ErrorNoPermission = "Error.NoPermission";
            public const string ErrorMonumentFinderNotLoaded = "Error.MonumentFinderNotLoaded";
            public const string ErrorNoMonuments = "Error.NoMonuments";
            public const string ErrorNotAtMonument = "Error.NotAtMonument";
            public const string ErrorNoSuitableAddonFound = "Error.NoSuitableAddonFound";
            public const string ErrorEntityNotEligible = "Error.EntityNotEligible";
            public const string ErrorNoSpawnPointFound = "Error.NoSpawnPointFound";
            public const string ErrorSetSyntax = "Error.Set.Syntax";
            public const string ErrorSetUnknownOption = "Error.Set.UnknownOption";

            public const string SpawnErrorSyntax = "Spawn.Error.Syntax";
            public const string SpawnErrorNoProfileSelected = "Spawn.Error.NoProfileSelected";
            public const string SpawnErrorEntityNotFound = "Spawn.Error.EntityNotFound2";
            public const string SpawnErrorEntityOrAddonNotFound = "Spawn.Error.EntityOrCustomNotFound";
            public const string SpawnErrorMultipleMatches = "Spawn.Error.MultipleMatches";
            public const string SpawnErrorNoTarget = "Spawn.Error.NoTarget";
            public const string SpawnSuccess = "Spawn.Success2";
            public const string KillSuccess = "Kill.Success3";
            public const string MoveNothingToDo = "Move.NothingToDo";
            public const string MoveSuccess = "Move.Success";

            public const string PasteNotCompatible = "Paste.NotCompatible";
            public const string PasteSyntax = "Paste.Syntax";
            public const string PasteNotFound = "Paste.NotFound";
            public const string PasteSuccess = "Paste.Success";

            public const string AddonTypeUnknown = "AddonType.Unknown";
            public const string AddonTypeEntity = "AddonType.Entity";
            public const string AddonTypeSpawnPoint = "AddonType.SpawnPoint";
            public const string AddonTypePaste = "AddonType.Paste";
            public const string AddonTypeCustom = "AddonType.Custom";

            public const string SpawnGroupCreateSyntax = "SpawnGroup.Create.Syntax";
            public const string SpawnGroupCreateNameInUse = "SpawnGroup.Create.NameInUse";
            public const string SpawnGroupCreateSucces = "SpawnGroup.Create.Success";
            public const string SpawnGroupSetSuccess = "SpawnGroup.Set.Success";
            public const string SpawnGroupAddSyntax = "SpawnGroup.Add.Syntax";
            public const string SpawnGroupAddSuccess = "SpawnGroup.Add.Success";
            public const string SpawnGroupRemoveSyntax = "SpawnGroup.Remove.Syntax";
            public const string SpawnGroupRemoveMultipleMatches = "SpawnGroup.Remove.MultipleMatches";
            public const string SpawnGroupRemoveNoMatch = "SpawnGroup.Remove.NoMatch";
            public const string SpawnGroupRemoveSuccess = "SpawnGroup.Remove.Success";

            public const string SpawnGroupNotFound = "SpawnGroup.NotFound";
            public const string SpawnGroupMultipeMatches = "SpawnGroup.MultipeMatches";
            public const string SpawnPointCreateSyntax = "SpawnPoint.Create.Syntax";
            public const string SpawnPointCreateSuccess = "SpawnPoint.Create.Success";
            public const string SpawnPointSetSyntax = "SpawnPoint.Set.Syntax";
            public const string SpawnPointSetSuccess = "SpawnPoint.Set.Success";

            public const string SpawnGroupHelpHeader = "SpawnGroup.Help.Header";
            public const string SpawnGroupHelpCreate = "SpawnGroup.Help.Create";
            public const string SpawnGroupHelpSet = "SpawnGroup.Help.Set";
            public const string SpawnGroupHelpAdd = "SpawnGroup.Help.Add";
            public const string SpawnGroupHelpRemove = "SpawnGroup.Help.Remove";
            public const string SpawnPointHelpHeader = "SpawnPoint.Help.Header";
            public const string SpawnPointHelpCreate = "SpawnPoint.Help.Create";
            public const string SpawnPointHelpSet = "SpawnPoint.Help.Set";

            public const string ShowSuccess = "Show.Success";
            public const string ShowHeaderEntity = "Show.Header.Entity";
            public const string ShowHeaderSpawnGroup = "Show.Header.SpawnGroup";
            public const string ShowHeaderSpawnPoint = "Show.Header.SpawnPoint";
            public const string ShowHeaderPaste = "Show.Header.Paste";
            public const string ShowHeaderCustom = "Show.Header.Custom";
            public const string ShowLabelPlugin = "Show.Label.Plugin";
            public const string ShowLabelProfile = "Show.Label.Profile";
            public const string ShowLabelMonument = "Show.Label.Monument";
            public const string ShowLabelSkin = "Show.Label.Skin";
            public const string ShowLabelScale = "Show.Label.Scale";
            public const string ShowLabelRCIdentifier = "Show.Label.RCIdentifier";

            public const string ShowLabelFlags = "Show.Label.SpawnPoint.Flags";
            public const string ShowLabelSpawnPointExclusive = "Show.Label.SpawnPoint.Exclusive";
            public const string ShowLabelSpawnPointRandomRotation = "Show.Label.SpawnPoint.RandomRotation";
            public const string ShowLabelSpawnPointDropsToGround = "Show.Label.SpawnPoint.DropsToGround";
            public const string ShowLabelSpawnPointChecksSpace = "Show.Label.SpawnPoint.ChecksSpace";
            public const string ShowLabelSpawnPointRandomRadius = "Show.Label.SpawnPoint.RandomRadius";

            public const string ShowLabelSpawnGroupPoints = "Show.Label.SpawnGroup.Points";
            public const string ShowLabelSpawnGroupPreventDuplicates = "Show.Label.SpawnGroup.PreventDuplicates";
            public const string ShowLabelSpawnGroupPopulation = "Show.Label.SpawnGroup.Population";
            public const string ShowLabelSpawnGroupRespawnPerTick = "Show.Label.SpawnGroup.RespawnPerTick";
            public const string ShowLabelSpawnGroupRespawnDelay = "Show.Label.SpawnGroup.RespawnDelay";
            public const string ShowLabelSpawnGroupNextSpawn = "Show.Label.SpawnGroup.NextSpawn";
            public const string ShowLabelSpawnGroupNextSpawnQueued = "Show.Label.SpawnGroup.NextSpawn.Queued";
            public const string ShowLabelSpawnGroupEntities = "Show.Label.SpawnGroup.Entities";
            public const string ShowLabelSpawnGroupEntityDetail = "Show.Label.SpawnGroup.Entities.Detail";
            public const string ShowLabelSpawnGroupNoEntities = "Show.Label.SpawnGroup.NoEntities";

            public const string SkinGet = "Skin.Get";
            public const string SkinSetSyntax = "Skin.Set.Syntax";
            public const string SkinSetSuccess = "Skin.Set.Success2";
            public const string SkinErrorRedirect = "Skin.Error.Redirect";

            public const string CCTVSetIdSyntax = "CCTV.SetId.Error.Syntax";
            public const string CCTVSetIdSuccess = "CCTV.SetId.Success2";
            public const string CCTVSetDirectionSuccess = "CCTV.SetDirection.Success2";

            public const string ProfileListEmpty = "Profile.List.Empty";
            public const string ProfileListHeader = "Profile.List.Header";
            public const string ProfileListItemEnabled = "Profile.List.Item.Enabled2";
            public const string ProfileListItemDisabled = "Profile.List.Item.Disabled2";
            public const string ProfileListItemSelected = "Profile.List.Item.Selected2";
            public const string ProfileByAuthor = "Profile.ByAuthor";

            public const string ProfileInstallSyntax = "Profile.Install.Syntax";
            public const string ProfileInstallShorthandSyntax = "Profile.Install.Shorthand.Syntax";
            public const string ProfileUrlInvalid = "Profile.Url.Invalid";
            public const string ProfileAlreadyExistsNotEmpty = "Profile.Error.AlreadyExists.NotEmpty";
            public const string ProfileInstallSuccess = "Profile.Install.Success2";
            public const string ProfileInstallError = "Profile.Install.Error";
            public const string ProfileDownloadError = "Profile.Download.Error";
            public const string ProfileParseError = "Profile.Parse.Error";

            public const string ProfileDescribeSyntax = "Profile.Describe.Syntax";
            public const string ProfileNotFound = "Profile.Error.NotFound";
            public const string ProfileEmpty = "Profile.Empty";
            public const string ProfileDescribeHeader = "Profile.Describe.Header";
            public const string ProfileDescribeItem = "Profile.Describe.Item2";
            public const string ProfileSelectSyntax = "Profile.Select.Syntax";
            public const string ProfileSelectSuccess = "Profile.Select.Success2";
            public const string ProfileSelectEnableSuccess = "Profile.Select.Enable.Success";

            public const string ProfileEnableSyntax = "Profile.Enable.Syntax";
            public const string ProfileAlreadyEnabled = "Profile.AlreadyEnabled";
            public const string ProfileEnableSuccess = "Profile.Enable.Success";
            public const string ProfileDisableSyntax = "Profile.Disable.Syntax";
            public const string ProfileAlreadyDisabled = "Profile.AlreadyDisabled2";
            public const string ProfileDisableSuccess = "Profile.Disable.Success2";
            public const string ProfileReloadSyntax = "Profile.Reload.Syntax";
            public const string ProfileNotEnabled = "Profile.NotEnabled";
            public const string ProfileReloadSuccess = "Profile.Reload.Success";

            public const string ProfileCreateSyntax = "Profile.Create.Syntax";
            public const string ProfileAlreadyExists = "Profile.Error.AlreadyExists";
            public const string ProfileCreateSuccess = "Profile.Create.Success";
            public const string ProfileRenameSyntax = "Profile.Rename.Syntax";
            public const string ProfileRenameSuccess = "Profile.Rename.Success";
            public const string ProfileClearSyntax = "Profile.Clear.Syntax";
            public const string ProfileClearSuccess = "Profile.Clear.Success";
            public const string ProfileMoveToSyntax = "Profile.MoveTo.Syntax";
            public const string ProfileMoveToAlreadyPresent = "Profile.MoveTo.AlreadyPresent";
            public const string ProfileMoveToSuccess = "Profile.MoveTo.Success";

            public const string ProfileHelpHeader = "Profile.Help.Header";
            public const string ProfileHelpList = "Profile.Help.List";
            public const string ProfileHelpDescribe = "Profile.Help.Describe";
            public const string ProfileHelpEnable = "Profile.Help.Enable";
            public const string ProfileHelpDisable = "Profile.Help.Disable";
            public const string ProfileHelpReload = "Profile.Help.Reload";
            public const string ProfileHelpSelect = "Profile.Help.Select";
            public const string ProfileHelpCreate = "Profile.Help.Create";
            public const string ProfileHelpRename = "Profile.Help.Rename";
            public const string ProfileHelpClear = "Profile.Help.Clear";
            public const string ProfileHelpMoveTo = "Profile.Help.MoveTo2";
            public const string ProfileHelpInstall = "Profile.Help.Install";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.ErrorNoPermission] = "You don't have permission to do that.",
                [Lang.ErrorMonumentFinderNotLoaded] = "Error: Monument Finder is not loaded.",
                [Lang.ErrorNoMonuments] = "Error: No monuments found.",
                [Lang.ErrorNotAtMonument] = "Error: Not at a monument. Nearest is <color=#fd4>{0}</color> with distance <color=#fd4>{1}</color>",
                [Lang.ErrorNoSuitableAddonFound] = "Error: No suitable addon found.",
                [Lang.ErrorEntityNotEligible] = "Error: That entity is not managed by Monument Addons.",
                [Lang.ErrorNoSpawnPointFound] = "Error: No spawn point found.",
                [Lang.ErrorSetSyntax] = "Syntax: <color=#fd4>{0} set {1} <value></color>",
                [Lang.ErrorSetUnknownOption] = "Unrecognized option: <color=#fd4>{0}</color>",

                [Lang.SpawnErrorSyntax] = "Syntax: <color=#fd4>maspawn <entity></color>",
                [Lang.SpawnErrorNoProfileSelected] = "Error: No profile selected. Run <color=#fd4>maprofile help</color> for help.",
                [Lang.SpawnErrorEntityNotFound] = "Error: No entity found matching name <color=#fd4>{0}</color>.",
                [Lang.SpawnErrorEntityOrAddonNotFound] = "Error: No entity or custom addon found matching name <color=#fd4>{0}</color>.",
                [Lang.SpawnErrorMultipleMatches] = "Multiple matches:\n",
                [Lang.SpawnErrorNoTarget] = "Error: No valid spawn position found.",
                [Lang.SpawnSuccess] = "Spawned entity at <color=#fd4>{0}</color> matching monument(s) and saved to <color=#fd4>{1}</color> profile for monument <color=#fd4>{2}</color>.",
                [Lang.KillSuccess] = "Killed <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and removed from profile <color=#fd4>{2}</color>.",
                [Lang.MoveNothingToDo] = "That entity is already at the saved position.",
                [Lang.MoveSuccess] = "Updated entity position at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.",

                [Lang.PasteNotCompatible] = "CopyPaste is not loaded or its version is incompatible.",
                [Lang.PasteSyntax] = "Syntax: <color=#fd4>mapaste <file></color>",
                [Lang.PasteNotFound] = "File <color=#fd4>{0}</color> does not exist.",
                [Lang.PasteSuccess] = "Pasted <color=#fd4>{0}</color> at <color=#fd4>{1}</color> (x<color=#fd4>{2}</color>) and saved to profile <color=#fd4>{3}</color>.",

                [Lang.AddonTypeUnknown] = "Addon",
                [Lang.AddonTypeEntity] = "Entity",
                [Lang.AddonTypeSpawnPoint] = "Spawn point",
                [Lang.AddonTypePaste] = "Paste",
                [Lang.AddonTypeCustom] = "Custom",

                [Lang.SpawnGroupCreateSyntax] = "Syntax: <color=#fd4>{0} create <name></color>",
                [Lang.SpawnGroupCreateNameInUse] = "There is already a spawn group named <color=#fd4>{0}</color> at monument <color=#fd4>{1}</color> in profile <color=#fd4>{2}</color>. Please use a different name.",
                [Lang.SpawnGroupCreateSucces] = "Successfully created spawn group <color=#fd4>{0}</color>.",
                [Lang.SpawnGroupSetSuccess] = "Successfully updated spawn group <color=#fd4>{0}</color> with option <color=#fd4>{1}</color>: <color=#fd4>{2}</color>.",
                [Lang.SpawnGroupAddSyntax] = "Syntax: <color=#fd4>{0} add <entity> <weight></color>",
                [Lang.SpawnGroupAddSuccess] = "Successfully added entity <color=#fd4>{0}</color> with weight <color=#fd4>{1}</color> to spawn group <color=#fd4>{2}</color>.",
                [Lang.SpawnGroupRemoveSyntax] = "Syntax: <color=#fd4>{0} remove <entity>/color>",
                [Lang.SpawnGroupRemoveMultipleMatches] = "Multiple entities in spawn group <color=#fd4>{0}</color> found matching: <color=#fd4>{1}</color>. Please be more specific.",
                [Lang.SpawnGroupRemoveNoMatch] = "No entity found in spawn group <color=#fd4>{0}</color> matching <color=#fd4>{1}</color>",
                [Lang.SpawnGroupRemoveSuccess] = "Successfully removed entity <color=#fd4>{0}</color> from spawn group <color=#fd4>{1}</color>.",

                [Lang.SpawnGroupNotFound] = "No spawn group found with name: <color=#fd4>{0}</color>",
                [Lang.SpawnGroupMultipeMatches] = "Multiple spawn groupds found matching name: <color=#fd4>{0}</color>",
                [Lang.SpawnPointCreateSyntax] = "Syntax: <color=#fd4>{0} create <group_name></color>",
                [Lang.SpawnPointCreateSuccess] = "Successfully added spawn point to spawn group <color=#fd4>{0}</color>.",
                [Lang.SpawnPointSetSyntax] = "Syntax: <color=#fd4>{0} set <option> <value></color>",
                [Lang.SpawnPointSetSuccess] = "Successfully updated spawn point with option <color=#fd4>{0}</color>: <color=#fd4>{1}</color>.",

                [Lang.SpawnGroupHelpHeader] = "<size=18>Monument Addons Spawn Group Commands</size>",
                [Lang.SpawnGroupHelpCreate] = "<color=#fd4>{0} create <name></color> - Create a spawn group with a spawn point",
                [Lang.SpawnGroupHelpSet] = "<color=#fd4>{0} set <option> <value></color> - Set a property of a spawn group",
                [Lang.SpawnGroupHelpAdd] = "<color=#fd4>{0} add <entity> <weight></color> - Add an entity prefab to a spawn group",
                [Lang.SpawnGroupHelpRemove] = "<color=#fd4>{0} remove <entity> <weight></color> - Remove an entity prefab from a spawn group",

                [Lang.SpawnPointHelpHeader] = "<size=18>Monument Addons Spawn Point Commands</size>",
                [Lang.SpawnPointHelpCreate] = "<color=#fd4>{0} create <group_name></color> - Create a spawn point",
                [Lang.SpawnPointHelpSet] = "<color=#fd4>{0} set <option> <value></color> - Set a property of a spawn point",

                [Lang.ShowSuccess] = "Showing nearby Monument Addons for <color=#fd4>{0}</color>.",
                [Lang.ShowLabelPlugin] = "Plugin: {0}",
                [Lang.ShowLabelProfile] = "Profile: {0}",
                [Lang.ShowLabelMonument] = "Monument: {0} (x{1})",
                [Lang.ShowLabelSkin] = "Skin: {0}",
                [Lang.ShowLabelScale] = "Scale: {0}",
                [Lang.ShowLabelRCIdentifier] = "RC Identifier: {0}",

                [Lang.ShowHeaderEntity] = "Entity: {0}",
                [Lang.ShowHeaderSpawnGroup] = "Spawn Group: {0}",
                [Lang.ShowHeaderSpawnPoint] = "Spawn Point ({0})",
                [Lang.ShowHeaderPaste] = "Paste: {0}",
                [Lang.ShowHeaderCustom] = "Custom Addon: {0}",

                [Lang.ShowLabelFlags] = "Flags: {0}",
                [Lang.ShowLabelSpawnPointExclusive] = "Exclusive",
                [Lang.ShowLabelSpawnPointRandomRotation] = "Random rotation",
                [Lang.ShowLabelSpawnPointDropsToGround] = "Drops to ground",
                [Lang.ShowLabelSpawnPointChecksSpace] = "Checks space",
                [Lang.ShowLabelSpawnPointRandomRadius] = "Random spawn radius: {0:f1}",

                [Lang.ShowLabelSpawnGroupPoints] = "Spawn points: {0}",
                [Lang.ShowLabelSpawnGroupPreventDuplicates] = "Prevent duplicates",
                [Lang.ShowLabelSpawnGroupPopulation] = "Population: {0} / {1}",
                [Lang.ShowLabelSpawnGroupRespawnPerTick] = "Spawn per tick: {0} - {1}",
                [Lang.ShowLabelSpawnGroupRespawnDelay] = "Respawn delay: {0} - {1}",
                [Lang.ShowLabelSpawnGroupNextSpawn] = "Next spawn: {0}",
                [Lang.ShowLabelSpawnGroupNextSpawnQueued] = "Queued",
                [Lang.ShowLabelSpawnGroupEntities] = "Entities:",
                [Lang.ShowLabelSpawnGroupEntityDetail] = "{0} | weight: {1}",
                [Lang.ShowLabelSpawnGroupNoEntities] = "No entities configured. Run /maspawngroup add <entity> <weight>",

                [Lang.SkinGet] = "Skin ID: <color=#fd4>{0}</color>. Run <color=#fd4>{1} <skin id></color> to change it.",
                [Lang.SkinSetSyntax] = "Syntax: <color=#fd4>{0} <skin id></color>",
                [Lang.SkinSetSuccess] = "Updated skin ID to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.",
                [Lang.SkinErrorRedirect] = "Error: Skin <color=#fd4>{0}</color> is a redirect skin and cannot be set directly. Instead, spawn the entity as <color=#fd4>{1}</color>.",

                [Lang.CCTVSetIdSyntax] = "Syntax: <color=#fd4>{0} <id></color>",
                [Lang.CCTVSetIdSuccess] = "Updated CCTV id to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.",
                [Lang.CCTVSetDirectionSuccess] = "Updated CCTV direction at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.",

                [Lang.ProfileListEmpty] = "You have no profiles. Create one with <color=#fd4>maprofile create <name></maprofile>",
                [Lang.ProfileListHeader] = "<size=18>Monument Addons Profiles</size>",
                [Lang.ProfileListItemEnabled] = "<color=#fd4>{0}</color>{1} - <color=#6e6>ENABLED</color>",
                [Lang.ProfileListItemDisabled] = "<color=#fd4>{0}</color>{1} - <color=#ccc>DISABLED</color>",
                [Lang.ProfileListItemSelected] = "<color=#fd4>{0}</color>{1} - <color=#6cf>SELECTED</color>",
                [Lang.ProfileByAuthor] = " by {0}",

                [Lang.ProfileInstallSyntax] = "Syntax: <color=#fd4>maprofile install <url></color>",
                [Lang.ProfileInstallShorthandSyntax] = "Syntax: <color=#fd4>mainstall <url></color>",
                [Lang.ProfileUrlInvalid] = "Invalid URL: {0}",
                [Lang.ProfileAlreadyExistsNotEmpty] = "Error: Profile <color=#fd4>{0}</color> already exists and is not empty.",
                [Lang.ProfileInstallSuccess] = "Successfully installed and <color=#6e6>ENABLED</color> profile <color=#fd4>{0}</color>{1}.",
                [Lang.ProfileInstallError] = "Error installing profile from url {0}. See the error logs for more details.",
                [Lang.ProfileDownloadError] = "Error downloading profile from url {0}\nStatus code: {1}",
                [Lang.ProfileParseError] = "Error parsing profile from url {0}\n{1}",

                [Lang.ProfileDescribeSyntax] = "Syntax: <color=#fd4>maprofile describe <name></color>",
                [Lang.ProfileNotFound] = "Error: Profile <color=#fd4>{0}</color> not found.",
                [Lang.ProfileEmpty] = "Profile <color=#fd4>{0}</color> is empty.",
                [Lang.ProfileDescribeHeader] = "Describing profile <color=#fd4>{0}</color>.",
                [Lang.ProfileDescribeItem] = "{0}: <color=#fd4>{1}</color> x{2} @ {3}",
                [Lang.ProfileSelectSyntax] = "Syntax: <color=#fd4>maprofile select <name></color>",
                [Lang.ProfileSelectSuccess] = "Successfully <color=#6cf>SELECTED</color> profile <color=#fd4>{0}</color>.",
                [Lang.ProfileSelectEnableSuccess] = "Successfully <color=#6cf>SELECTED</color> and <color=#6e6>ENABLED</color> profile <color=#fd4>{0}</color>.",

                [Lang.ProfileEnableSyntax] = "Syntax: <color=#fd4>maprofile enable <name></color>",
                [Lang.ProfileAlreadyEnabled] = "Profile <color=#fd4>{0}</color> is already <color=#6e6>ENABLED</color>.",
                [Lang.ProfileEnableSuccess] = "Profile <color=#fd4>{0}</color> is now: <color=#6e6>ENABLED</color>.",
                [Lang.ProfileDisableSyntax] = "Syntax: <color=#fd4>maprofile disable <name></color>",
                [Lang.ProfileAlreadyDisabled] = "Profile <color=#fd4>{0}</color> is already <color=#ccc>DISABLED</color>.",
                [Lang.ProfileDisableSuccess] = "Profile <color=#fd4>{0}</color> is now: <color=#ccc>DISABLED</color>.",
                [Lang.ProfileReloadSyntax] = "Syntax: <color=#fd4>maprofile reload <name></color>",
                [Lang.ProfileNotEnabled] = "Error: Profile <color=#fd4>{0}</color> is not enabled.",
                [Lang.ProfileReloadSuccess] = "Reloaded profile <color=#fd4>{0}</color>.",

                [Lang.ProfileCreateSyntax] = "Syntax: <color=#fd4>maprofile create <name></color>",
                [Lang.ProfileAlreadyExists] = "Error: Profile <color=#fd4>{0}</color> already exists.",
                [Lang.ProfileCreateSuccess] = "Successfully created and <color=#6cf>SELECTED</color> profile <color=#fd4>{0}</color>.",
                [Lang.ProfileRenameSyntax] = "Syntax: <color=#fd4>maprofile rename <old name> <new name></color>",
                [Lang.ProfileRenameSuccess] = "Successfully renamed profile <color=#fd4>{0}</color> to <color=#fd4>{1}</color>. You must manually delete the old <color=#fd4>{0}</color> data file.",
                [Lang.ProfileClearSyntax] = "Syntax: <color=#fd4>maprofile clear <name></color>",
                [Lang.ProfileClearSuccess] = "Successfully cleared profile <color=#fd4>{0}</color>.",
                [Lang.ProfileMoveToSyntax] = "Syntax: <color=#fd4>maprofile moveto <name></color>",
                [Lang.ProfileMoveToAlreadyPresent] = "Error: <color=#fd4>{0}</color> is already part of profile <color=#fd4>{1}</color>.",
                [Lang.ProfileMoveToSuccess] = "Successfully moved <color=#fd4>{0}</color> from profile <color=#fd4>{1}</color> to <color=#fd4>{2}</color>.",

                [Lang.ProfileHelpHeader] = "<size=18>Monument Addons Profile Commands</size>",
                [Lang.ProfileHelpList] = "<color=#fd4>maprofile list</color> - List all profiles",
                [Lang.ProfileHelpDescribe] = "<color=#fd4>maprofile describe <name></color> - Describe profile contents",
                [Lang.ProfileHelpEnable] = "<color=#fd4>maprofile enable <name></color> - Enable a profile",
                [Lang.ProfileHelpDisable] = "<color=#fd4>maprofile disable <name></color> - Disable a profile",
                [Lang.ProfileHelpReload] = "<color=#fd4>maprofile reload <name></color> - Reload a profile from disk",
                [Lang.ProfileHelpSelect] = "<color=#fd4>maprofile select <name></color> - Select a profile",
                [Lang.ProfileHelpCreate] = "<color=#fd4>maprofile create <name></color> - Create a new profile",
                [Lang.ProfileHelpRename] = "<color=#fd4>maprofile rename <name> <new name></color> - Rename a profile",
                [Lang.ProfileHelpClear] = "<color=#fd4>maprofile clear <name></color> - Clears a profile",
                [Lang.ProfileHelpMoveTo] = "<color=#fd4>maprofile moveto <name></color> - Move an entity to a profile",
                [Lang.ProfileHelpInstall] = "<color=#fd4>maprofile install <url></color> - Install a profile from a URL"
            }, this, "en");
        }

        #endregion
    }
}
