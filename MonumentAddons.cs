﻿using Newtonsoft.Json;
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

        private const float MaxRaycastDistance = 100;
        private const float TerrainProximityTolerance = 0.001f;
        private const float MaxFindDistanceSquared = 4;
        private const float ShowVanillaDuration = 60;

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

        private readonly Color[] _distinctColors = new Color[]
        {
            Color.HSVToRGB(0, 1, 1),
            Color.HSVToRGB(0.1f, 1, 1),
            Color.HSVToRGB(0.2f, 1, 1),
            Color.HSVToRGB(0.35f, 1, 1),
            Color.HSVToRGB(0.55f, 1, 1),
            Color.HSVToRGB(0.8f, 1, 1),
            new Color(1, 1, 1),
        };

        private readonly object _cancelHook = false;

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
                return _cancelHook;

            return null;
        }

        private object CanChangeGrade(BasePlayer player, BuildingBlock block, BuildingGrade.Enum grade)
        {
            if (_entityTracker.IsMonumentEntity(block) && !HasAdminPermission(player))
                return _cancelHook;

            return null;
        }

        private object CanUpdateSign(BasePlayer player, ISignage signage)
        {
            if (_entityTracker.IsMonumentEntity(signage as BaseEntity) && !HasAdminPermission(player))
            {
                ChatMessage(player, LangEntry.ErrorNoPermission);
                return _cancelHook;
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
        private object CanStartTelekinesis(BasePlayer player, BaseEntity moveEntity)
        {
            if (_entityTracker.IsMonumentEntity(moveEntity) && !HasAdminPermission(player))
                return _cancelHook;

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

            if (!_entityTracker.IsMonumentEntity(moveEntity, out adapter, out controller))
                return;

            if (!adapter.TrySaveAndApplyChanges())
                return;

            if (player != null)
            {
                _entityDisplayManager.ShowAllRepeatedly(player);
                ChatMessage(player, LangEntry.SaveSuccess, controller.Adapters.Count, controller.Profile.Name);
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
            ReplyToPlayer(player, LangEntry.SpawnSuccess, matchingMonuments.Count, profileController.Profile.Name, monument.AliasOrShortName);
        }

        [Command("masave")]
        private void CommandSave(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            SingleEntityAdapter adapter;
            SingleEntityController controller;
            if (!VerifyLookingAtAdapter(player, out adapter, out controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            if (!adapter.TrySaveAndApplyChanges())
            {
                ReplyToPlayer(player, LangEntry.SaveNothingToDo);
                return;
            }

            ReplyToPlayer(player, LangEntry.SaveSuccess, controller.Adapters.Count, controller.Profile.Name);

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
            if (!VerifyLookingAtAdapter(player, out adapter, out controller, LangEntry.ErrorNoSuitableAddonFound))
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
            ReplyToPlayer(player, LangEntry.KillSuccess, GetAddonName(player, adapter.Data), numAdapters, controller.Profile.Name);
        }

        [Command("masetid")]
        private void CommandSetId(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            if (args.Length < 1 || !ComputerStation.IsValidIdentifier(args[0]))
            {
                ReplyToPlayer(player, LangEntry.CCTVSetIdSyntax, cmd);
                return;
            }

            CCTVEntityController controller;
            if (!VerifyLookingAtAdapter(player, out controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            if (controller.EntityData.CCTV == null)
                controller.EntityData.CCTV = new CCTVInfo();

            controller.EntityData.CCTV.RCIdentifier = args[0];
            controller.Profile.Save();
            controller.UpdateIdentifier();

            var basePlayer = player.Object as BasePlayer;
            _entityDisplayManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, LangEntry.CCTVSetIdSuccess, args[0], controller.Adapters.Count, controller.Profile.Name);
        }

        [Command("masetdir")]
        private void CommandSetDirection(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            CCTVEntityAdapter adapter;
            CCTVEntityController controller;
            if (!VerifyLookingAtAdapter(player, out adapter, out controller, LangEntry.ErrorNoSuitableAddonFound))
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
            ReplyToPlayer(player, LangEntry.CCTVSetDirectionSuccess, controller.Adapters.Count, controller.Profile.Name);
        }

        [Command("maskin")]
        private void CommandSkin(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            SingleEntityAdapter adapter;
            SingleEntityController controller;
            if (!VerifyLookingAtAdapter(player, out adapter, out controller, LangEntry.ErrorNoSuitableAddonFound))
                return;

            if (args.Length == 0)
            {
                ReplyToPlayer(player, LangEntry.SkinGet, adapter.Entity.skinID, cmd);
                return;
            }

            ulong skinId;
            if (!ulong.TryParse(args[0], out skinId))
            {
                ReplyToPlayer(player, LangEntry.SkinSetSyntax, cmd);
                return;
            }

            string alternativeShortName;
            if (IsRedirectSkin(skinId, out alternativeShortName))
            {
                ReplyToPlayer(player, LangEntry.SkinErrorRedirect, skinId, alternativeShortName);
                return;
            }

            controller.EntityData.Skin = skinId;
            controller.Profile.Save();
            controller.UpdateSkin();

            var basePlayer = player.Object as BasePlayer;
            _entityDisplayManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, LangEntry.SkinSetSuccess, skinId, controller.Adapters.Count, controller.Profile.Name);
        }

        private void AddProfileDescription(StringBuilder sb, IPlayer player, ProfileController profileController)
        {
            foreach (var summaryEntry in GetProfileSummary(player, profileController.Profile))
            {
                sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileDescribeItem, summaryEntry.AddonType, summaryEntry.AddonName, summaryEntry.Count, summaryEntry.MonumentName));
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
                        ReplyToPlayer(player, LangEntry.ProfileListEmpty);
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
                    ProfileController controller;
                    if (!VerifyProfile(player, args, out controller, LangEntry.ProfileDescribeSyntax))
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
                        ReplyToPlayer(player, LangEntry.ProfileSelectSyntax);
                        return;
                    }

                    ProfileController controller;
                    Profile newProfileData;
                    if (!VerifyProfile(player, args, out controller, LangEntry.ProfileSelectSyntax))
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
                        if (!RefreshProfileAndVerifyJSONSyntax(player, controller.Profile.Name, out newProfileData))
                            return;

                        controller.Enable(newProfileData);
                    }

                    ReplyToPlayer(player, wasEnabled ? LangEntry.ProfileSelectSuccess : LangEntry.ProfileSelectEnableSuccess, controller.Profile.Name);
                    _entityDisplayManager.SetPlayerProfile(basePlayer, controller);
                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);
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
                        _pluginData.SetProfileSelected(player.Id, newName);

                    _pluginData.SetProfileEnabled(newName);

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
                        _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                    }
                    break;
                }

                case "reload":
                {
                    ProfileController controller;
                    if (!VerifyProfile(player, args, out controller, LangEntry.ProfileReloadSyntax))
                        return;

                    if (!controller.IsEnabled)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileNotEnabled, controller.Profile.Name);
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
                    ReplyToPlayer(player, LangEntry.ProfileReloadSuccess, controller.Profile.Name);
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
                        ReplyToPlayer(player, LangEntry.ProfileEnableSyntax);
                        return;
                    }

                    ProfileController controller;
                    Profile newProfileData;
                    if (!VerifyProfileExists(player, args[1], out controller))
                        return;

                    var profileName = controller.Profile.Name;
                    if (controller.IsEnabled)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileAlreadyEnabled, profileName);
                        return;
                    }

                    if (!RefreshProfileAndVerifyJSONSyntax(player, controller.Profile.Name, out newProfileData))
                        return;

                    controller.Enable(newProfileData);
                    ReplyToPlayer(player, LangEntry.ProfileEnableSuccess, profileName);
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
                    if (!VerifyProfile(player, args, out controller, LangEntry.ProfileDisableSyntax))
                        return;

                    var profileName = controller.Profile.Name;
                    if (!controller.IsEnabled)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileAlreadyDisabled, profileName);
                        return;
                    }

                    controller.Disable();
                    _pluginData.SetProfileDisabled(profileName);
                    _pluginData.Save();
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

                    ProfileController controller;
                    if (!VerifyProfile(player, args, out controller, LangEntry.ProfileClearSyntax))
                        return;

                    if (!controller.Profile.IsEmpty())
                        controller.Clear();

                    ReplyToPlayer(player, LangEntry.ProfileClearSuccess, controller.Profile.Name);
                    break;
                }

                case "moveto":
                {
                    BaseController controller;
                    if (!VerifyLookingAtAdapter(player, out controller, LangEntry.ErrorNoSuitableAddonFound))
                        return;

                    ProfileController newProfileController;
                    if (!VerifyProfile(player, args, out newProfileController, LangEntry.ProfileMoveToSyntax))
                        return;

                    var newProfile = newProfileController.Profile;
                    var oldProfile = controller.Profile;

                    var data = controller.Data;
                    var addonName = GetAddonName(player, data);

                    if (newProfileController == controller.ProfileController)
                    {
                        ReplyToPlayer(player, LangEntry.ProfileMoveToAlreadyPresent, addonName, oldProfile.Name);
                        return;
                    }

                    string monumentAliasOrShortName;
                    if (!controller.TryKillAndRemove(out monumentAliasOrShortName))
                        return;

                    newProfile.AddData(monumentAliasOrShortName, data);
                    newProfileController.SpawnNewData(data, GetMonumentsByAliasOrShortName(monumentAliasOrShortName));

                    ReplyToPlayer(player, LangEntry.ProfileMoveToSuccess, addonName, oldProfile.Name, newProfile.Name);
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
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpMoveTo));
            sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileHelpInstall));
            player.Reply(sb.ToString());
        }

        [Command("mainstall")]
        private void CommandInstallProfile(IPlayer player, string cmd, string[] args)
        {
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

                    if (profile.Name.EndsWith(Profile.OriginalSuffix))
                    {
                        LogError($"Profile \"{profile.Name}\" should not end with \"{Profile.OriginalSuffix}\".");
                        ReplyToPlayer(player, LangEntry.ProfileInstallError, url);
                        return;
                    }

                    var profileController = _profileManager.GetProfileController(profile.Name);
                    if (profileController != null && !profileController.Profile.IsEmpty())
                    {
                        ReplyToPlayer(player, LangEntry.ProfileAlreadyExistsNotEmpty, profile.Name);
                        return;
                    }

                    profile.Save();
                    profile.SaveAsOriginal();

                    if (profileController == null)
                        profileController = _profileManager.GetProfileController(profile.Name);

                    if (profileController == null)
                    {
                        LogError($"Profile \"{profile.Name}\" could not be found on disk after download from url: \"{url}\"");
                        ReplyToPlayer(player, LangEntry.ProfileInstallError, url);
                        return;
                    }

                    if (profileController.IsEnabled)
                        profileController.Reload(profile);
                    else
                        profileController.Enable(profile);

                    var sb = new StringBuilder();
                    sb.AppendLine(GetMessage(player.Id, LangEntry.ProfileInstallSuccess, profile.Name, GetAuthorSuffix(player, profile.Author)));
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
                    ReplyToPlayer(player, LangEntry.ProfileNotFound, profileName);
                    return;
                }
            }

            var basePlayer = player.Object as BasePlayer;

            _entityDisplayManager.SetPlayerProfile(basePlayer, profileController);
            _entityDisplayManager.ShowAllRepeatedly(basePlayer, duration);

            ReplyToPlayer(player, LangEntry.ShowSuccess, FormatTime(duration));
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
                        ReplyToPlayer(player, LangEntry.SpawnGroupCreateSyntax, cmd);
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

                    ReplyToPlayer(player, LangEntry.SpawnGroupCreateSucces, spawnGroupName);
                    break;
                }

                case "set":
                {
                    if (args.Length < 3)
                    {
                        ReplyToPlayer(player, LangEntry.ErrorSetSyntax, cmd);
                        return;
                    }

                    SpawnGroupOption spawnGroupOption;
                    if (!TryParseEnum(args[1], out spawnGroupOption))
                    {
                        ReplyToPlayer(player, LangEntry.ErrorSetUnknownOption, args[1]);
                        return;
                    }

                    SpawnPointAdapter spawnPointAdapter;
                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnPointAdapter, out spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
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
                            if (!VerifyValidInt(player, args[2], out maxPopulation, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.MaxPopulation))
                                return;

                            spawnGroupData.MaxPopulation = maxPopulation;
                            break;
                        }

                        case SpawnGroupOption.RespawnDelayMin:
                        {
                            float respawnDelayMin;
                            if (!VerifyValidFloat(player, args[2], out respawnDelayMin, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.RespawnDelayMin))
                                return;

                            spawnGroupData.RespawnDelayMin = respawnDelayMin;
                            spawnGroupData.RespawnDelayMax = Math.Max(respawnDelayMin, spawnGroupData.RespawnDelayMax);
                            setValue = respawnDelayMin;
                            break;
                        }

                        case SpawnGroupOption.RespawnDelayMax:
                        {
                            float respawnDelayMax;
                            if (!VerifyValidFloat(player, args[2], out respawnDelayMax, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.RespawnDelayMax))
                                return;

                            spawnGroupData.RespawnDelayMax = respawnDelayMax;
                            spawnGroupData.RespawnDelayMin = Math.Min(spawnGroupData.RespawnDelayMin, respawnDelayMax);
                            setValue = respawnDelayMax;
                            break;
                        }

                        case SpawnGroupOption.SpawnPerTickMin:
                        {
                            int spawnPerTickMin;
                            if (!VerifyValidInt(player, args[2], out spawnPerTickMin, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.SpawnPerTickMin))
                                return;

                            spawnGroupData.SpawnPerTickMin = spawnPerTickMin;
                            spawnGroupData.SpawnPerTickMax = Math.Max(spawnPerTickMin, spawnGroupData.SpawnPerTickMax);
                            setValue = spawnPerTickMin;
                            break;
                        }

                        case SpawnGroupOption.SpawnPerTickMax:
                        {
                            int spawnPerTickMax;
                            if (!VerifyValidInt(player, args[2], out spawnPerTickMax, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.SpawnPerTickMax))
                                return;

                            spawnGroupData.SpawnPerTickMax = spawnPerTickMax;
                            spawnGroupData.SpawnPerTickMin = Math.Min(spawnGroupData.SpawnPerTickMin, spawnPerTickMax);
                            setValue = spawnPerTickMax;
                            break;
                        }

                        case SpawnGroupOption.PreventDuplicates:
                        {
                            bool preventDuplicates;
                            if (!VerifyValidBool(player, args[2], out preventDuplicates, LangEntry.ErrorSetSyntax, cmd, SpawnGroupOption.PreventDuplicates))
                                return;

                            spawnGroupData.PreventDuplicates = preventDuplicates;
                            setValue = preventDuplicates;
                            break;
                        }
                    }

                    spawnGroupController.UpdateSpawnGroups();
                    spawnGroupController.Profile.Save();

                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);

                    ReplyToPlayer(player, LangEntry.SpawnGroupSetSuccess, spawnGroupData.Name, spawnGroupOption, setValue);
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

                    string prefabPath;
                    if (!VerifyValidPrefab(player, args[1], out prefabPath))
                        return;

                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
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

                    ReplyToPlayer(player, LangEntry.SpawnGroupAddSuccess, prefabData.ShortPrefabName, weight, spawnGroupData.Name);
                    break;
                }

                case "remove":
                {
                    if (args.Length < 2)
                    {
                        ReplyToPlayer(player, LangEntry.SpawnGroupRemoveSyntax, cmd);
                        return;
                    }

                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
                        return;

                    string desiredPrefab = args[1];

                    var spawnGroupData = spawnGroupController.SpawnGroupData;

                    var matchingPrefabs = spawnGroupData.FindPrefabMatches(desiredPrefab);
                    if (matchingPrefabs.Count == 0)
                    {
                        ReplyToPlayer(player, LangEntry.SpawnGroupRemoveNoMatch, spawnGroupData.Name, desiredPrefab);
                        _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                        return;
                    }

                    if (matchingPrefabs.Count > 1)
                    {
                        ReplyToPlayer(player, LangEntry.SpawnGroupRemoveMultipleMatches, spawnGroupData.Name, desiredPrefab);
                        _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                        return;
                    }

                    var prefabMatch = matchingPrefabs[0];

                    spawnGroupData.Prefabs.Remove(prefabMatch);
                    spawnGroupController.KillSpawnedInstances(prefabMatch.PrefabName);
                    spawnGroupController.UpdateSpawnGroups();
                    spawnGroupController.Profile.Save();

                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);

                    ReplyToPlayer(player, LangEntry.SpawnGroupRemoveSuccess, prefabMatch.ShortPrefabName, spawnGroupData.Name);
                    break;
                }

                case "spawn":
                case "tick":
                {
                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
                        return;

                    spawnGroupController.SpawnTick();
                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);
                    break;
                }

                case "respawn":
                {
                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
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
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpHeader, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpCreate, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpSet, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpAdd, cmd));
            sb.AppendLine(GetMessage(player.Id, LangEntry.SpawnGroupHelpRemove, cmd));
            player.Reply(sb.ToString());
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
                        ReplyToPlayer(player, LangEntry.SpawnPointCreateSyntax, cmd);
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

                    ReplyToPlayer(player, LangEntry.SpawnPointCreateSuccess, spawnGroupController.SpawnGroupData.Name);
                    break;
                }

                case "set":
                {
                    if (args.Length < 3)
                    {
                        ReplyToPlayer(player, LangEntry.SpawnPointSetSyntax, cmd);
                        return;
                    }

                    SpawnPointOption spawnPointOption;
                    if (!TryParseEnum(args[1], out spawnPointOption))
                    {
                        ReplyToPlayer(player, LangEntry.ErrorSetUnknownOption, args[1]);
                        return;
                    }

                    SpawnPointAdapter spawnPointAdapter;
                    SpawnGroupController spawnGroupController;
                    if (!VerifyLookingAtAdapter(player, out spawnPointAdapter, out spawnGroupController, LangEntry.ErrorNoSpawnPointFound))
                        return;

                    var spawnPointData = spawnPointAdapter.SpawnPointData;
                    object setValue = args[2];

                    switch (spawnPointOption)
                    {
                        case SpawnPointOption.Exclusive:
                        {
                            bool exclusive;
                            if (!VerifyValidBool(player, args[2], out exclusive, LangEntry.SpawnGroupSetSuccess, LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.Exclusive))
                                return;

                            spawnPointData.Exclusive = exclusive;
                            setValue = spawnPointData.Exclusive;
                            break;
                        }

                        case SpawnPointOption.DropToGround:
                        {
                            bool dropToGround;
                            if (!VerifyValidBool(player, args[2], out dropToGround, LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.DropToGround))
                                return;

                            spawnPointData.DropToGround = dropToGround;
                            setValue = spawnPointData.DropToGround;
                            break;
                        }

                        case SpawnPointOption.CheckSpace:
                        {
                            bool checkSpace;
                            if (!VerifyValidBool(player, args[2], out checkSpace, LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.CheckSpace))
                                return;

                            spawnPointData.CheckSpace = checkSpace;
                            setValue = spawnPointData.CheckSpace;
                            break;
                        }

                        case SpawnPointOption.RandomRotation:
                        {
                            bool randomRotation;
                            if (!VerifyValidBool(player, args[2], out randomRotation, LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.RandomRotation))
                                return;

                            spawnPointData.RandomRotation = randomRotation;
                            setValue = spawnPointData.RandomRotation;
                            break;
                        }

                        case SpawnPointOption.RandomRadius:
                        {
                            float radius;
                            if (!VerifyValidFloat(player, args[2], out radius, LangEntry.ErrorSetSyntax, cmd, SpawnPointOption.RandomRadius))
                                return;

                            spawnPointData.RandomRadius = radius;
                            setValue = spawnPointData.RandomRadius;
                            break;
                        }
                    }

                    spawnGroupController.Profile.Save();

                    _entityDisplayManager.ShowAllRepeatedly(basePlayer);

                    ReplyToPlayer(player, LangEntry.SpawnPointSetSuccess, spawnPointOption, setValue);
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
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            if (args.Length < 1)
            {
                ReplyToPlayer(player, LangEntry.PasteSyntax);
                return;
            }

            if (!PasteUtils.IsCopyPasteCompatible())
            {
                ReplyToPlayer(player, LangEntry.PasteNotCompatible);
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
                ReplyToPlayer(player, LangEntry.PasteNotFound, pasteName);
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

            ReplyToPlayer(player, LangEntry.PasteSuccess, pasteName, monument.AliasOrShortName, matchingMonuments.Count, profileController.Profile.Name);
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
            if (spawnGroup.respawnDelayMin != float.PositiveInfinity)
            {
                sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelRespawnDelay, FormatTime(spawnGroup.respawnDelayMin), FormatTime(spawnGroup.respawnDelayMax)));
            }

            var nextSpawnTime = GetTimeToNextSpawn(spawnGroup);
            if (nextSpawnTime != float.PositiveInfinity && SingletonComponent<SpawnHandler>.Instance.SpawnGroups.Contains(spawnGroup))
            {
                sb.AppendLine(GetMessage(
                    player.Id,
                    LangEntry.ShowLabelNextSpawn,
                    nextSpawnTime <= 0
                        ? GetMessage(player.Id, LangEntry.ShowLabelNextSpawnQueued)
                        : FormatTime(Mathf.CeilToInt(nextSpawnTime))
                ));
            }

            if (spawnGroup.prefabs.Count > 0)
            {
                sb.AppendLine(_pluginInstance.GetMessage(player.Id, LangEntry.ShowLabelEntities));
                foreach (var prefabEntry in spawnGroup.prefabs)
                {
                    sb.AppendLine(_pluginInstance.GetMessage(player.Id, LangEntry.ShowLabelEntityDetail, prefabEntry.prefab.resourcePath, prefabEntry.weight));
                }
            }
            else
            {
                sb.AppendLine(_pluginInstance.GetMessage(player.Id, LangEntry.ShowLabelNoEntities));
            }
        }

        [Command("mashowvanilla")]
        private void CommandShowVanillaSpawns(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            var basePlayer = player.Object as BasePlayer;

            MonoBehaviour parentObject = null;

            RaycastHit hit;
            if (TryRaycast(basePlayer, out hit))
            {
                parentObject = hit.GetEntity();
            }

            if (parentObject == null)
            {
                Vector3 position;
                BaseMonument monument;
                if (!VerifyMonumentFinderLoaded(player)
                    || !VerifyHitPosition(player, out position)
                    || !VerifyAtMonument(player, position, out monument))
                    return;

                parentObject = monument.Object;
            }

            var spawnerList = parentObject.GetComponentsInChildren<ISpawnGroup>();
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
                        Ddraw.Text(basePlayer, spawnGroupPosition + new Vector3(0, tierMask > 0 ? Mathf.Log(tierMask, 2): 0, 0), sb.ToString(), color, ShowVanillaDuration);
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
                            booleanProperties.Add(_pluginInstance.GetMessage(player.Id, LangEntry.ShowLabelSpawnPointExclusive));

                            if (genericSpawnPoint.randomRot)
                            {
                                booleanProperties.Add(_pluginInstance.GetMessage(player.Id, LangEntry.ShowLabelSpawnPointRandomRotation));
                            }

                            if (genericSpawnPoint.dropToGround)
                            {
                                booleanProperties.Add(_pluginInstance.GetMessage(player.Id, LangEntry.ShowLabelSpawnPointDropsToGround));
                            }
                        }

                        var spaceCheckingSpawnPoint = spawnPoint as SpaceCheckingSpawnPoint;
                        if (spaceCheckingSpawnPoint != null)
                        {
                            booleanProperties.Add(_pluginInstance.GetMessage(player.Id, LangEntry.ShowLabelSpawnPointChecksSpace));
                        }

                        if (booleanProperties.Count > 0)
                        {
                            sb.AppendLine(_pluginInstance.GetMessage(player.Id, LangEntry.ShowLabelFlags, string.Join(" | ", booleanProperties)));
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

                        var spawnPointPosition = spawnPoint.transform.position;
                        Ddraw.Sphere(basePlayer, spawnPointPosition, 0.5f, color, ShowVanillaDuration);

                        if (spawnPoint == closestSpawnPoint)
                        {
                            Ddraw.Text(basePlayer, spawnPointPosition + new Vector3(0, tierMask > 0 ? Mathf.Log(tierMask, 2) : 0, 0), sb.ToString(), color, ShowVanillaDuration);
                        }
                        else
                        {
                            Ddraw.Arrow(basePlayer, closestSpawnPointPosition, spawnPointPosition, 0.25f, color, ShowVanillaDuration);
                            Ddraw.Text(basePlayer, spawnPointPosition, sb.ToString(), color, ShowVanillaDuration);
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
                    sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelFlags, $"{GetMessage(player.Id, LangEntry.ShowLabelSpawnPointExclusive)} | {GetMessage(player.Id, LangEntry.ShowLabelSpawnPointChecksSpace)}"));

                    if (individualSpawner.oneTimeSpawner)
                    {
                        sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelSpawnOnMapWipe));
                    }
                    else
                    {
                        if (individualSpawner.respawnDelayMin != float.PositiveInfinity)
                        {
                            sb.AppendLine(GetMessage(player.Id, LangEntry.ShowLabelRespawnDelay, FormatTime(individualSpawner.respawnDelayMin), FormatTime(individualSpawner.respawnDelayMax)));
                        }

                        var nextSpawnTime = GetTimeToNextSpawn(individualSpawner);
                        if (nextSpawnTime != float.PositiveInfinity)
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

                    sb.AppendLine(GetMessage(player.Id, LangEntry.ShowHeaderEntity, individualSpawner.entityPrefab.resourcePath));

                    var spawnPointPosition = individualSpawner.transform.position;
                    Ddraw.Sphere(basePlayer, spawnPointPosition, 0.5f, color, ShowVanillaDuration);
                    Ddraw.Text(basePlayer, spawnPointPosition, sb.ToString(), color, ShowVanillaDuration);

                    sb.Clear();
                    continue;
                }
            }
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
                ReplyToPlayer(player, LangEntry.SpawnErrorEntityNotFound, prefabArg);
                return false;
            }

            // Multiple matches were found, so print them all to the player.
            var replyMessage = GetMessage(player.Id, LangEntry.SpawnErrorMultipleMatches);
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
                ReplyToPlayer(player, LangEntry.SpawnErrorEntityOrAddonNotFound, prefabArg);
                return false;
            }

            // Multiple matches were found, so print them all to the player.
            var replyMessage = GetMessage(player.Id, LangEntry.SpawnErrorMultipleMatches);
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
            ReplyToPlayer(player, LangEntry.SpawnErrorSyntax);
            return false;
        }

        private bool VerifyLookingAtAdapter<TAdapter, TController>(IPlayer player, out AdapterFindResult<TAdapter, TController> findResult, LangEntry errorLangEntry)
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
            where TAdapter : BaseTransformAdapter
            where TController : BaseController
        {
            AdapterFindResult<TAdapter, TController> findResult;
            var result = VerifyLookingAtAdapter(player, out findResult, errorLangEntry);
            adapter = findResult.Adapter;
            controller = findResult.Controller;
            return result;
        }

        // Convenient method that does not require an adapter type.
        private bool VerifyLookingAtAdapter<TController>(IPlayer player, out TController controller, LangEntry errorLangEntry)
            where TController : BaseController
        {
            AdapterFindResult<BaseTransformAdapter, TController> findResult;
            var result = VerifyLookingAtAdapter(player, out findResult, errorLangEntry);
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

        private bool RefreshProfileAndVerifyJSONSyntax(IPlayer player, string profileName, out Profile profile)
        {
            try
            {
                profile = Profile.Load(profileName);
                return true;
            }
            catch (JsonReaderException ex)
            {
                profile = null;
                player.Reply("{0}", string.Empty, ex.Message);
                return false;
            }
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

        private static void EnableSavingRecursive(BaseEntity entity, bool enableSaving)
        {
            entity.EnableSaving(enableSaving);

            foreach (var child in entity.children)
                EnableSavingRecursive(child, enableSaving);
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
                tierList.Add(MonumentTier.Tier0);

            if ((tier & MonumentTier.Tier1) != 0)
                tierList.Add(MonumentTier.Tier1);

            if ((tier & MonumentTier.Tier2) != 0)
                tierList.Add(MonumentTier.Tier2);

            return tierList;
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
                EnableSavingRecursive(entity, false);

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
                            combatEntity.baseProtection = _pluginInstance._immortalProtection;
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
                            || buildingBlock.HasFlag(BuildingBlock.BlockFlags.CanDemolish))
                        {
                            buildingBlock.SetFlag(BuildingBlock.BlockFlags.CanRotate, false, recursive: false, networkupdate: false);
                            buildingBlock.SetFlag(BuildingBlock.BlockFlags.CanDemolish, false, recursive: false, networkupdate: false);
                            buildingBlock.CancelInvoke(buildingBlock.StopBeingRotatable);
                            buildingBlock.CancelInvoke(buildingBlock.StopBeingDemolishable);
                            buildingBlock.SendNetworkUpdate_Flags();
                        }
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
                EnableSavingRecursive(entity, false);

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

            public bool TrySaveAndApplyChanges()
            {
                var hasChanged = false;

                if (!IsAtIntendedPosition)
                {
                    EntityData.Position = LocalPosition;
                    EntityData.RotationAngles = LocalRotation.eulerAngles;
                    EntityData.OnTerrain = IsOnTerrain(Position);
                    hasChanged = true;
                }

                var buildingBlock = Entity as BuildingBlock;
                if (buildingBlock != null && buildingBlock.grade != IntendedBuildingGrade)
                {
                    if (EntityData.BuildingBlock == null)
                    {
                        EntityData.BuildingBlock = new BuildingBlockInfo();
                    }

                    EntityData.BuildingBlock.Grade = buildingBlock.grade;
                    hasChanged = true;
                }

                if (hasChanged)
                {
                    var singleEntityController = Controller as SingleEntityController;
                    singleEntityController.HandleChanges();
                    singleEntityController.Profile.Save();
                }

                return hasChanged;
            }

            public void HandleChanges()
            {
                UpdatePosition();
                UpdateBuildingGrade();
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
                        buildingBlock.SetGrade(IntendedBuildingGrade);

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
                buildingBlock.baseProtection = _pluginInstance._immortalProtection;
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

            public void HandleChanges()
            {
                ProfileController.StartCoroutine(HandleChangesRoutine());
            }

            private IEnumerator UpdateSkinRoutine()
            {
                foreach (var adapter in Adapters.ToArray())
                {
                    var singleAdapter = adapter as SingleEntityAdapter;
                    if (singleAdapter.IsDestroyed)
                        continue;

                    singleAdapter.UpdateSkin();
                    yield return null;
                }
            }

            private IEnumerator UpdateScaleRoutine()
            {
                foreach (var adapter in Adapters.ToArray())
                {
                    var singleAdapter = adapter as SingleEntityAdapter;
                    if (singleAdapter.IsDestroyed)
                        continue;

                    singleAdapter.UpdateScale();
                    yield return null;
                }
            }

            private IEnumerator HandleChangesRoutine()
            {
                foreach (var adapter in Adapters.ToArray())
                {
                    var singleAdapter = adapter as SingleEntityAdapter;
                    if (singleAdapter.IsDestroyed)
                        continue;

                    singleAdapter.HandleChanges();
                    yield return null;
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

        private class BuildingBlockEntityListener : DynamicHookListener
        {
            public BuildingBlockEntityListener()
            {
                _dynamicHookNames = new string[]
                {
                    nameof(CanChangeGrade),
                };
            }

            public override bool InterestedInAdapter(BaseAdapter adapter)
            {
                var entityData = adapter.Data as EntityData;
                if (entityData == null)
                    return false;

                return FindBaseEntityForPrefab(entityData.PrefabName) is BuildingBlock;
            }
        }

        private class AdapterListenerManager
        {
            private AdapterListenerBase[] _listeners = new AdapterListenerBase[]
            {
                new SignEntityListener(),
                new BuildingBlockEntityListener(),
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
            public const int HeaderSize = 25;
            public static readonly string Divider = $"<size={HeaderSize}>------------------------------</size>";

            private const int DisplayIntervalDuration = 2;

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
                _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelProfile, profileController.Profile.Name));

                var vanillaMonument = adapter.Monument.Object as MonumentInfo;
                if (vanillaMonument != null && (int)vanillaMonument.Tier != -1)
                {
                    _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelMonumentWithTier, adapter.Monument.AliasOrShortName, controller.Adapters.Count, vanillaMonument.Tier));
                }
                else
                {
                    _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelMonument, adapter.Monument.AliasOrShortName, controller.Adapters.Count));
                }
            }

            private void ShowEntityInfo(BasePlayer player, EntityAdapterBase adapter, PlayerInfo playerInfo)
            {
                var entityData = adapter.EntityData;
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowHeaderEntity, entityData.ShortPrefabName)}</size>");
                AddCommonInfo(player, profileController, controller, adapter);

                if (entityData.Skin != 0)
                _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelSkin, entityData.Skin));

                if (entityData.Scale != 1)
                 _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelScale, entityData.Scale));

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
                    _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelRCIdentifier, identifier));
                }

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
                _sb.AppendLine($"<size={HeaderSize}>{_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowHeaderSpawnPoint, spawnGroupData.Name)}</size>");
                AddCommonInfo(player, profileController, controller, adapter);

                var booleanProperties = new List<string>();

                if (spawnPointData.Exclusive)
                    booleanProperties.Add(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPointExclusive));

                if (spawnPointData.RandomRotation)
                    booleanProperties.Add(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPointRandomRotation));

                if (spawnPointData.DropToGround)
                    booleanProperties.Add(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPointDropsToGround));

                if (spawnPointData.CheckSpace)
                    booleanProperties.Add(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPointChecksSpace));

                if (booleanProperties.Count > 0)
                     _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelFlags, string.Join(" | ", booleanProperties)));

                if (spawnPointData.RandomRadius > 0)
                    _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPointRandomRadius, spawnPointData.RandomRadius));

                if (showGroupInfo)
                {
                    _sb.AppendLine(Divider);
                    _sb.AppendLine($"<size=25>{_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowHeaderSpawnGroup, spawnGroupData.Name)}</size>");

                    _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelSpawnPoints, spawnGroupData.SpawnPoints.Count));

                    var groupBooleanProperties = new List<string>();

                    if (spawnGroupData.PreventDuplicates)
                        groupBooleanProperties.Add(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelPreventDuplicates));

                    if (groupBooleanProperties.Count > 0)
                        _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelFlags, string.Join(" | ", groupBooleanProperties)));

                    _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelPopulation, spawnGroupAdapter.SpawnGroup.currentPopulation, spawnGroupData.MaxPopulation));
                    _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelRespawnPerTick, spawnGroupData.SpawnPerTickMin, spawnGroupData.SpawnPerTickMax));
                    _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelRespawnDelay, FormatTime(spawnGroupData.RespawnDelayMin), FormatTime(spawnGroupData.RespawnDelayMax)));

                    var nextSpawnTime = GetTimeToNextSpawn(spawnGroupAdapter.SpawnGroup);

                    _sb.AppendLine(_pluginInstance.GetMessage(
                        player.UserIDString,
                        LangEntry.ShowLabelNextSpawn,
                        nextSpawnTime <= 0
                            ? _pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelNextSpawnQueued)
                            : FormatTime(Mathf.CeilToInt(nextSpawnTime))
                    ));

                    if (spawnGroupData.Prefabs.Count > 0)
                    {
                        _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelEntities));
                        foreach (var prefabEntry in spawnGroupData.Prefabs)
                        {
                            _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelEntityDetail, prefabEntry.PrefabName, prefabEntry.Weight));
                        }
                    }
                    else
                    {
                        _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelNoEntities));
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
                var controller = adapter.Controller;
                var profileController = controller.ProfileController;
                var color = DetermineColor(adapter, playerInfo, profileController);

                _sb.Clear();
                _sb.AppendLine($"<size={HeaderSize}>{_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowHeaderPaste, pasteData.Filename)}</size>");
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
                _sb.AppendLine($"<size={HeaderSize}>{_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowHeaderCustom, customAddonData.AddonName)}</size>");
                _sb.AppendLine(_pluginInstance.GetMessage(player.UserIDString, LangEntry.ShowLabelPlugin, addonDefinition.OwnerPlugin.Name));
                AddCommonInfo(player, profileController, controller, adapter);

                addonDefinition.AddDisplayInfo?.Invoke(adapter.Component, customAddonData.GetSerializedData(), _sb);

                Ddraw.Text(player, adapter.Position, _sb.ToString(), color, DisplayIntervalDuration);
            }

            private void ShowNearbyAdapters(BasePlayer player, Vector3 playerPosition, PlayerInfo playerInfo)
            {
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

            var addonTypeEntity = GetMessage(player.Id, LangEntry.AddonTypeEntity);
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

        // Multi-argument overloads are defined to reduce array allocations.
        private string GetMessage(string playerId, LangEntry langEntry) =>
            lang.GetMessage(langEntry.Name, this, playerId);

        private string GetMessage(string playerId, LangEntry langEntry, object arg1) =>
            string.Format(GetMessage(playerId, langEntry), arg1);

        private string GetMessage(string playerId, LangEntry langEntry, object arg1, object arg2) =>
            string.Format(GetMessage(playerId, langEntry), arg1, arg2);

        private string GetMessage(string playerId, LangEntry langEntry, object arg1, object arg2, string arg3) =>
            string.Format(GetMessage(playerId, langEntry), arg1, arg2, arg3);

        private string GetMessage(string playerId, LangEntry langEntry, params object[] args) =>
            string.Format(GetMessage(playerId, langEntry), args);


        private void ReplyToPlayer(IPlayer player, LangEntry langEntry) =>
            player.Reply(GetMessage(player.Id, langEntry));

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, object arg1) =>
            player.Reply(GetMessage(player.Id, langEntry, arg1));

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, object arg1, object arg2) =>
            player.Reply(GetMessage(player.Id, langEntry, arg1, arg2));

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, object arg1, object arg2, object arg3) =>
            player.Reply(GetMessage(player.Id, langEntry, arg1, arg2, arg3));

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, params object[] args) =>
            player.Reply(GetMessage(player.Id, langEntry, args));


        private void ChatMessage(BasePlayer player, LangEntry langEntry) =>
            player.ChatMessage(GetMessage(player.UserIDString, langEntry));

        private void ChatMessage(BasePlayer player, LangEntry langEntry, object arg1) =>
            player.ChatMessage(GetMessage(player.UserIDString, langEntry, arg1));

        private void ChatMessage(BasePlayer player, LangEntry langEntry, object arg1, object arg2) =>
            player.ChatMessage(GetMessage(player.UserIDString, langEntry, arg1, arg2));

        private void ChatMessage(BasePlayer player, LangEntry langEntry, object arg1, object arg2, object arg3) =>
            player.ChatMessage(GetMessage(player.UserIDString, langEntry, arg1, arg2, arg3));

        private void ChatMessage(BasePlayer player, LangEntry langEntry, params object[] args) =>
            player.ChatMessage(GetMessage(player.UserIDString, langEntry, args));


        private string GetAuthorSuffix(IPlayer player, string author)
        {
            return !string.IsNullOrWhiteSpace(author)
                ? GetMessage(player.Id, LangEntry.ProfileByAuthor, author)
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
                return GetMessage(player.Id, LangEntry.AddonTypeSpawnPoint);
            }

            var pasteData = data as PasteData;
            if (pasteData != null)
            {
                return pasteData.Filename;
            }

            return GetMessage(player.Id, LangEntry.AddonTypeUnknown);
        }

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
            public static readonly LangEntry ErrorSetSyntax = new LangEntry("Error.Set.Syntax", "Syntax: <color=#fd4>{0} set {1} <value></color>");
            public static readonly LangEntry ErrorSetUnknownOption = new LangEntry("Error.Set.UnknownOption", "Unrecognized option: <color=#fd4>{0}</color>");

            public static readonly LangEntry SpawnErrorSyntax = new LangEntry("Spawn.Error.Syntax", "Syntax: <color=#fd4>maspawn <entity></color>");
            public static readonly LangEntry SpawnErrorNoProfileSelected = new LangEntry("Spawn.Error.NoProfileSelected", "Error: No profile selected. Run <color=#fd4>maprofile help</color> for help.");
            public static readonly LangEntry SpawnErrorEntityNotFound = new LangEntry("Spawn.Error.EntityNotFound2", "Error: No entity found matching name <color=#fd4>{0}</color>.");
            public static readonly LangEntry SpawnErrorEntityOrAddonNotFound = new LangEntry("Spawn.Error.EntityOrCustomNotFound", "Error: No entity or custom addon found matching name <color=#fd4>{0}</color>.");
            public static readonly LangEntry SpawnErrorMultipleMatches = new LangEntry("Spawn.Error.MultipleMatches", "Multiple matches:\n");
            public static readonly LangEntry ErrorNoSurface = new LangEntry("Error.NoSurface", "Error: No valid surface found.");
            public static readonly LangEntry SpawnSuccess = new LangEntry("Spawn.Success2", "Spawned entity at <color=#fd4>{0}</color> matching monument(s) and saved to <color=#fd4>{1}</color> profile for monument <color=#fd4>{2}</color>.");
            public static readonly LangEntry KillSuccess = new LangEntry("Kill.Success3", "Killed <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and removed from profile <color=#fd4>{2}</color>.");
            public static readonly LangEntry SaveNothingToDo = new LangEntry("Save.NothingToDo", "No changes detected for that entity.");
            public static readonly LangEntry SaveSuccess = new LangEntry("Save.Success", "Updated entity at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.");

            public static readonly LangEntry PasteNotCompatible = new LangEntry("Paste.NotCompatible", "CopyPaste is not loaded or its version is incompatible.");
            public static readonly LangEntry PasteSyntax = new LangEntry("Paste.Syntax", "Syntax: <color=#fd4>mapaste <file></color>");
            public static readonly LangEntry PasteNotFound = new LangEntry("Paste.NotFound", "File <color=#fd4>{0}</color> does not exist.");
            public static readonly LangEntry PasteSuccess = new LangEntry("Paste.Success", "Pasted <color=#fd4>{0}</color> at <color=#fd4>{1}</color> (x<color=#fd4>{2}</color>) and saved to profile <color=#fd4>{3}</color>.");

            public static readonly LangEntry AddonTypeUnknown = new LangEntry("AddonType.Unknown", "Addon");
            public static readonly LangEntry AddonTypeEntity = new LangEntry("AddonType.Entity", "Entity");
            public static readonly LangEntry AddonTypeSpawnPoint = new LangEntry("AddonType.SpawnPoint", "Spawn point");
            public static readonly LangEntry AddonTypePaste = new LangEntry("AddonType.Paste", "Paste");
            public static readonly LangEntry AddonTypeCustom = new LangEntry("AddonType.Custom", "Custom");

            public static readonly LangEntry SpawnGroupCreateSyntax = new LangEntry("SpawnGroup.Create.Syntax", "Syntax: <color=#fd4>{0} create <name></color>");
            public static readonly LangEntry SpawnGroupCreateNameInUse = new LangEntry("SpawnGroup.Create.NameInUse", "There is already a spawn group named <color=#fd4>{0}</color> at monument <color=#fd4>{1}</color> in profile <color=#fd4>{2}</color>. Please use a different name.");
            public static readonly LangEntry SpawnGroupCreateSucces = new LangEntry("SpawnGroup.Create.Success", "Successfully created spawn group <color=#fd4>{0}</color>.");
            public static readonly LangEntry SpawnGroupSetSuccess = new LangEntry("SpawnGroup.Set.Success", "Successfully updated spawn group <color=#fd4>{0}</color> with option <color=#fd4>{1}</color>: <color=#fd4>{2}</color>.");
            public static readonly LangEntry SpawnGroupAddSyntax = new LangEntry("SpawnGroup.Add.Syntax", "Syntax: <color=#fd4>{0} add <entity> <weight></color>");
            public static readonly LangEntry SpawnGroupAddSuccess = new LangEntry("SpawnGroup.Add.Success", "Successfully added entity <color=#fd4>{0}</color> with weight <color=#fd4>{1}</color> to spawn group <color=#fd4>{2}</color>.");
            public static readonly LangEntry SpawnGroupRemoveSyntax = new LangEntry("SpawnGroup.Remove.Syntax", "Syntax: <color=#fd4>{0} remove <entity>/color>");
            public static readonly LangEntry SpawnGroupRemoveMultipleMatches = new LangEntry("SpawnGroup.Remove.MultipleMatches", "Multiple entities in spawn group <color=#fd4>{0}</color> found matching: <color=#fd4>{1}</color>. Please be more specific.");
            public static readonly LangEntry SpawnGroupRemoveNoMatch = new LangEntry("SpawnGroup.Remove.NoMatch", "No entity found in spawn group <color=#fd4>{0}</color> matching <color=#fd4>{1}</color>");
            public static readonly LangEntry SpawnGroupRemoveSuccess = new LangEntry("SpawnGroup.Remove.Success", "Successfully removed entity <color=#fd4>{0}</color> from spawn group <color=#fd4>{1}</color>.");

            public static readonly LangEntry SpawnGroupNotFound = new LangEntry("SpawnGroup.NotFound", "No spawn group found with name: <color=#fd4>{0}</color>");
            public static readonly LangEntry SpawnGroupMultipeMatches = new LangEntry("SpawnGroup.MultipeMatches", "Multiple spawn groupds found matching name: <color=#fd4>{0}</color>");
            public static readonly LangEntry SpawnPointCreateSyntax = new LangEntry("SpawnPoint.Create.Syntax", "Syntax: <color=#fd4>{0} create <group_name></color>");
            public static readonly LangEntry SpawnPointCreateSuccess = new LangEntry("SpawnPoint.Create.Success", "Successfully added spawn point to spawn group <color=#fd4>{0}</color>.");
            public static readonly LangEntry SpawnPointSetSyntax = new LangEntry("SpawnPoint.Set.Syntax", "Syntax: <color=#fd4>{0} set <option> <value></color>");
            public static readonly LangEntry SpawnPointSetSuccess = new LangEntry("SpawnPoint.Set.Success", "Successfully updated spawn point with option <color=#fd4>{0}</color>: <color=#fd4>{1}</color>.");

            public static readonly LangEntry SpawnGroupHelpHeader = new LangEntry("SpawnGroup.Help.Header", "<size=18>Monument Addons Spawn Group Commands</size>");
            public static readonly LangEntry SpawnGroupHelpCreate = new LangEntry("SpawnGroup.Help.Create", "<color=#fd4>{0} create <name></color> - Create a spawn group with a spawn point");
            public static readonly LangEntry SpawnGroupHelpSet = new LangEntry("SpawnGroup.Help.Set", "<color=#fd4>{0} set <option> <value></color> - Set a property of a spawn group");
            public static readonly LangEntry SpawnGroupHelpAdd = new LangEntry("SpawnGroup.Help.Add", "<color=#fd4>{0} add <entity> <weight></color> - Add an entity prefab to a spawn group");
            public static readonly LangEntry SpawnGroupHelpRemove = new LangEntry("SpawnGroup.Help.Remove", "<color=#fd4>{0} remove <entity> <weight></color> - Remove an entity prefab from a spawn group");

            public static readonly LangEntry SpawnPointHelpHeader = new LangEntry("SpawnPoint.Help.Header", "<size=18>Monument Addons Spawn Point Commands</size>");
            public static readonly LangEntry SpawnPointHelpCreate = new LangEntry("SpawnPoint.Help.Create", "<color=#fd4>{0} create <group_name></color> - Create a spawn point");
            public static readonly LangEntry SpawnPointHelpSet = new LangEntry("SpawnPoint.Help.Set", "<color=#fd4>{0} set <option> <value></color> - Set a property of a spawn point");

            public static readonly LangEntry ShowVanillaNoSpawnPoints = new LangEntry("Show.Vanilla.NoSpawnPoints", "No spawn points found in <color=#fd4>{0}</color>.");

            public static readonly LangEntry ShowSuccess = new LangEntry("Show.Success", "Showing nearby Monument Addons for <color=#fd4>{0}</color>.");
            public static readonly LangEntry ShowLabelPlugin = new LangEntry("Show.Label.Plugin", "Plugin: {0}");
            public static readonly LangEntry ShowLabelProfile = new LangEntry("Show.Label.Profile", "Profile: {0}");
            public static readonly LangEntry ShowLabelMonument = new LangEntry("Show.Label.Monument", "Monument: {0} (x{1})");
            public static readonly LangEntry ShowLabelMonumentWithTier = new LangEntry("Show.Label.MonumentWithTier", "Monument: {0} (x{1}, Tier: {2})");
            public static readonly LangEntry ShowLabelSkin = new LangEntry("Show.Label.Skin", "Skin: {0}");
            public static readonly LangEntry ShowLabelScale = new LangEntry("Show.Label.Scale", "Scale: {0}");
            public static readonly LangEntry ShowLabelRCIdentifier = new LangEntry("Show.Label.RCIdentifier", "RC Identifier: {0}");

            public static readonly LangEntry ShowHeaderEntity = new LangEntry("Show.Header.Entity", "Entity: {0}");
            public static readonly LangEntry ShowHeaderSpawnGroup = new LangEntry("Show.Header.SpawnGroup", "Spawn Group: {0}");
            public static readonly LangEntry ShowHeaderVanillaSpawnGroup = new LangEntry("Show.Header.Vanilla.SpawnGroup", "Vanilla Spawn Group: {0}");
            public static readonly LangEntry ShowHeaderSpawnPoint = new LangEntry("Show.Header.SpawnPoint", "Spawn Point ({0})");
            public static readonly LangEntry ShowHeaderVanillaSpawnPoint = new LangEntry("Show.Header.Vanilla.SpawnPoint", "Vanilla Spawn Point ({0})");
            public static readonly LangEntry ShowHeaderVanillaIndividualSpawnPoint = new LangEntry("Show.Header.Vanilla.IndividualSpawnPoint", "Vanilla Individual Spawn Point: {0}");
            public static readonly LangEntry ShowHeaderPaste = new LangEntry("Show.Header.Paste", "Paste: {0}");
            public static readonly LangEntry ShowHeaderCustom = new LangEntry("Show.Header.Custom", "Custom Addon: {0}");

            public static readonly LangEntry ShowLabelFlags = new LangEntry("Show.Label.SpawnPoint.Flags", "Flags: {0}");
            public static readonly LangEntry ShowLabelSpawnPointExclusive = new LangEntry("Show.Label.SpawnPoint.Exclusive", "Exclusive");
            public static readonly LangEntry ShowLabelSpawnPointRandomRotation = new LangEntry("Show.Label.SpawnPoint.RandomRotation", "Random rotation");
            public static readonly LangEntry ShowLabelSpawnPointDropsToGround = new LangEntry("Show.Label.SpawnPoint.DropsToGround", "Drops to ground");
            public static readonly LangEntry ShowLabelSpawnPointChecksSpace = new LangEntry("Show.Label.SpawnPoint.ChecksSpace", "Checks space");
            public static readonly LangEntry ShowLabelSpawnPointRandomRadius = new LangEntry("Show.Label.SpawnPoint.RandomRadius", "Random spawn radius: {0:f1}");

            public static readonly LangEntry ShowLabelSpawnPoints = new LangEntry("Show.Label.Points", "Spawn points: {0}");
            public static readonly LangEntry ShowLabelTiers = new LangEntry("Show.Label.Tiers", "Tiers: {0}");
            public static readonly LangEntry ShowLabelSpawnWhenParentSpawns = new LangEntry("Show.Label.SpawnWhenParentSpawns", "Spawn when parent spawns");
            public static readonly LangEntry ShowLabelSpawnOnServerStart = new LangEntry("Show.Label.SpawnOnServerStart", "Spawn on server start");
            public static readonly LangEntry ShowLabelSpawnOnMapWipe = new LangEntry("Show.Label.SpawnOnMapWipe", "Spawn on map wipe");
            public static readonly LangEntry ShowLabelPreventDuplicates = new LangEntry("Show.Label.PreventDuplicates", "Prevent duplicates");
            public static readonly LangEntry ShowLabelPopulation = new LangEntry("Show.Label.Population", "Population: {0} / {1}");
            public static readonly LangEntry ShowLabelRespawnPerTick = new LangEntry("Show.Label.RespawnPerTick", "Spawn per tick: {0} - {1}");
            public static readonly LangEntry ShowLabelRespawnDelay = new LangEntry("Show.Label.RespawnDelay", "Respawn delay: {0} - {1}");
            public static readonly LangEntry ShowLabelNextSpawn = new LangEntry("Show.Label.NextSpawn", "Next spawn: {0}");
            public static readonly LangEntry ShowLabelNextSpawnQueued = new LangEntry("Show.Label.NextSpawn.Queued", "Queued");
            public static readonly LangEntry ShowLabelEntities = new LangEntry("Show.Label.Entities", "Entities:");
            public static readonly LangEntry ShowLabelEntityDetail = new LangEntry("Show.Label.Entities.Detail", "{0} | weight: {1}");
            public static readonly LangEntry ShowLabelNoEntities = new LangEntry("Show.Label.NoEntities", "No entities configured. Run /maspawngroup add <entity> <weight>");

            public static readonly LangEntry SkinGet = new LangEntry("Skin.Get", "Skin ID: <color=#fd4>{0}</color>. Run <color=#fd4>{1} <skin id></color> to change it.");
            public static readonly LangEntry SkinSetSyntax = new LangEntry("Skin.Set.Syntax", "Syntax: <color=#fd4>{0} <skin id></color>");
            public static readonly LangEntry SkinSetSuccess = new LangEntry("Skin.Set.Success2", "Updated skin ID to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.");
            public static readonly LangEntry SkinErrorRedirect = new LangEntry("Skin.Error.Redirect", "Error: Skin <color=#fd4>{0}</color> is a redirect skin and cannot be set directly. Instead, spawn the entity as <color=#fd4>{1}</color>.");

            public static readonly LangEntry CCTVSetIdSyntax = new LangEntry("CCTV.SetId.Error.Syntax", "Syntax: <color=#fd4>{0} <id></color>");
            public static readonly LangEntry CCTVSetIdSuccess = new LangEntry("CCTV.SetId.Success2", "Updated CCTV id to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.");
            public static readonly LangEntry CCTVSetDirectionSuccess = new LangEntry("CCTV.SetDirection.Success2", "Updated CCTV direction at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.");

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
            public static readonly LangEntry ProfileRenameSuccess = new LangEntry("Profile.Rename.Success", "Successfully renamed profile <color=#fd4>{0}</color> to <color=#fd4>{1}</color>. You must manually delete the old <color=#fd4>{0}</color> data file.");
            public static readonly LangEntry ProfileClearSyntax = new LangEntry("Profile.Clear.Syntax", "Syntax: <color=#fd4>maprofile clear <name></color>");
            public static readonly LangEntry ProfileClearSuccess = new LangEntry("Profile.Clear.Success", "Successfully cleared profile <color=#fd4>{0}</color>.");
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
            public static readonly LangEntry ProfileHelpClear = new LangEntry("Profile.Help.Clear", "<color=#fd4>maprofile clear <name></color> - Clears a profile");
            public static readonly LangEntry ProfileHelpMoveTo = new LangEntry("Profile.Help.MoveTo2", "<color=#fd4>maprofile moveto <name></color> - Move an entity to a profile");
            public static readonly LangEntry ProfileHelpInstall = new LangEntry("Profile.Help.Install", "<color=#fd4>maprofile install <url></color> - Install a profile from a URL");

            public string Name;
            public string English;

            public LangEntry(string name, string english)
            {
                Name = name;
                English = english;

                AllLangEntries.Add(this);
            }
        }

        protected override void LoadDefaultMessages()
        {
            var englishLangKeys = new Dictionary<string, string>();

            foreach (var langEntry in LangEntry.AllLangEntries)
            {
                englishLangKeys[langEntry.Name] = langEntry.English;
            }

            lang.RegisterMessages(englishLangKeys, this, "en");
        }

        #endregion
    }
}
