using Newtonsoft.Json;
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

namespace Oxide.Plugins
{
    [Info("Monument Addons", "WhiteThunder", "0.9.0")]
    [Description("Allows privileged players to add permanent entities to monuments.")]
    internal class MonumentAddons : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin EntityScaleManager, MonumentFinder, SignArtist;

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
        private readonly EntityListenerManager _entityListenerManager = new EntityListenerManager();
        private readonly ControllerFactory _entityControllerFactoryResolver = new ControllerFactory();

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

            _entityListenerManager.Init();
        }

        private void OnServerInitialized()
        {
            _waterDefinition = ItemManager.FindItemDefinition("water");

            _immortalProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
            _immortalProtection.name = "MonumentAddonsProtection";
            _immortalProtection.Add(1);

            _entityListenerManager.OnServerInitialized();

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

        private void OnEntitySpawned(CargoShip cargoShip)
        {
            var cargoShipMonument = new CargoShipMonument(cargoShip);
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

            var controller = component.Adapter.Controller as SignEntityController;
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

        #endregion

        #region Commands

        [Command("maspawn")]
        private void CommandSpawn(IPlayer player, string cmd, string[] args)
        {
            ProfileController profileController;
            string prefabName;
            Vector3 position;
            BaseMonument monument;

            if (player.IsServer
                || !VerifyHasPermission(player)
                || !VerifyMonumentFinderLoaded(player)
                || !VerifyProfileSelected(player, out profileController)
                || !VerifyValidPrefabOrDeployable(player, args, out prefabName)
                || !VerifyHitPosition(player, out position)
                || !VerifyAtMonument(player, position, out monument))
                return;

            var basePlayer = player.Object as BasePlayer;

            Vector3 localPosition;
            Vector3 localRotationAngles;
            DetermineLocalTransformData(position, basePlayer, monument, out localPosition, out localRotationAngles);

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

            var entityData = new EntityData
            {
                Id = Guid.NewGuid(),
                PrefabName = prefabName,
                Position = localPosition,
                RotationAngles = localRotationAngles,
                OnTerrain = IsOnTerrain(position),
            };

            var matchingMonuments = GetMonumentsByAliasOrShortName(monument.AliasOrShortName);

            profileController.Profile.AddData(monument.AliasOrShortName, entityData);
            profileController.SpawnNewData(entityData, matchingMonuments);

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
            if (!VerifyLookingAtAdapter(player, out adapter, out controller))
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

            EntityControllerBase controller;
            if (!VerifyLookingAtAdapter(player, out controller))
                return;

            var numAdapters = controller.Adapters.Count;;
            if (!controller.TryDestroyAndRemove())
                return;

            var basePlayer = player.Object as BasePlayer;
            _entityDisplayManager.ShowAllRepeatedly(basePlayer);
            ReplyToPlayer(player, Lang.KillSuccess, numAdapters, controller.Profile.Name);
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
            if (!VerifyLookingAtAdapter(player, out controller))
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
            if (!VerifyLookingAtAdapter(player, out adapter, out controller))
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
            if (!VerifyLookingAtAdapter(player, out adapter, out controller))
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
            foreach (var monumentEntry in profileController.Profile.GetEntityAggregates())
            {
                var aliasOrShortName = monumentEntry.Key;
                foreach (var countEntry in monumentEntry.Value)
                    sb.AppendLine(GetMessage(player, Lang.ProfileDescribeItem, GetShortName(countEntry.Key), countEntry.Value, aliasOrShortName));
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
                        player.Reply(ex.Message);
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
                    if (!VerifyLookingAtAdapter(player, out controller))
                        return;

                    ProfileController newProfileController;
                    if (!VerifyProfile(player, args, out newProfileController, Lang.ProfileMoveToSyntax))
                        return;

                    var newProfile = newProfileController.Profile;
                    var oldProfile = controller.Profile;

                    var data = controller.Data;
                    var entityData = controller.Data as EntityData;

                    var objectName = entityData != null
                        ? entityData.ShortPrefabName
                        : GetMessage(player, Lang.AddonTypeSpawnPoint);

                    if (newProfileController == controller.ProfileController)
                    {
                        ReplyToPlayer(player, Lang.ProfileMoveToAlreadyPresent, objectName, oldProfile.Name);
                        return;
                    }

                    string monumentAliasOrShortName;
                    if (!controller.TryDestroyAndRemove(out monumentAliasOrShortName))
                        return;

                    newProfile.AddData(monumentAliasOrShortName, data);
                    newProfileController.SpawnNewData(data, GetMonumentsByAliasOrShortName(monumentAliasOrShortName), delay: true);

                    ReplyToPlayer(player, Lang.ProfileMoveToSuccess, objectName, oldProfile.Name, newProfile.Name);
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

        #endregion

        #region Helper Methods - Command Checks

        private bool VerifyHasPermission(IPlayer player, string perm = PermissionAdmin)
        {
            if (player.HasPermission(perm))
                return true;

            ReplyToPlayer(player, Lang.ErrorNoPermission);
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

        private bool VerifyValidPrefab(IPlayer player, string desiredPrefabName, out string prefabPath)
        {
            prefabPath = null;

            var matches = FindPrefabMatches(desiredPrefabName);
            if (matches.Length == 0)
            {
                ReplyToPlayer(player, Lang.SpawnErrorEntityNotFound, desiredPrefabName);
                return false;
            }
            else if (matches.Length == 1)
            {
                prefabPath = matches[0];
                return true;
            }

            // Multiple matches were found
            var replyMessage = GetMessage(player, Lang.SpawnErrorMultipleMatches);
            foreach (var match in matches)
                replyMessage += $"\n{GetShortName(match)}";

            player.Reply(replyMessage);
            return false;
        }

        private bool VerifyValidPrefabOrDeployable(IPlayer player, string[] args, out string prefabPath)
        {
            // An explicit entity name takes precedence.
            // Ignore "True" argument because that simply means the player used a key bind.
            if (args.Length > 0 && args[0] != "True" && !string.IsNullOrWhiteSpace(args[0]))
            {
                return VerifyValidPrefab(player, args[0], out prefabPath);
            }

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

        private bool VerifyLookingAtAdapter<TAdapter, TController>(IPlayer player, out AdapterFindResult<TAdapter, TController> findResult)
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
                ReplyToPlayer(player, Lang.ErrorNoSuitableEntityFound);
            }

            findResult = default(AdapterFindResult<TAdapter, TController>);
            return false;
        }

        private bool VerifyLookingAtAdapter<TAdapter, TController>(IPlayer player, out TAdapter adapter, out TController controller)
            where TAdapter : BaseTransformAdapter
            where TController : BaseController
        {
            AdapterFindResult<TAdapter, TController> findResult;
            var result = VerifyLookingAtAdapter(player, out findResult);
            adapter = findResult.Adapter;
            controller = findResult.Controller;
            return result;
        }

        // Convenient method that does not require an adapter type.
        private bool VerifyLookingAtAdapter<TController>(IPlayer player, out TController controller)
            where TController : BaseController
        {
            AdapterFindResult<BaseTransformAdapter, TController> findResult;
            var result = VerifyLookingAtAdapter(player, out findResult);
            controller = findResult.Controller;
            return result;
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
                player.Reply(ex.Message);
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

        private static void DetermineLocalTransformData(Vector3 position, BasePlayer basePlayer, BaseMonument monument, out Vector3 localPosition, out Vector3 localRotationAngles)
        {
            localPosition = monument.InverseTransformPoint(position);

            var localRotationAngle = basePlayer.HasParent()
                ? basePlayer.viewAngles.y - 180
                : basePlayer.viewAngles.y - monument.Rotation.eulerAngles.y + 180;

            localRotationAngles = new Vector3(0, (localRotationAngle + 360) % 360, 0);
        }

        private static void DestroyProblemComponents(BaseEntity entity)
        {
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<DestroyOnGroundMissing>());
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<GroundWatch>());
        }

        private static string[] FindPrefabMatches(string partialName)
        {
            var matches = new List<string>();

            foreach (var path in GameManifest.Current.entities)
            {
                if (string.Compare(GetShortName(path), partialName, StringComparison.OrdinalIgnoreCase) == 0)
                    return new string[] { path.ToLower() };

                if (GetShortName(path).Contains(partialName, CompareOptions.IgnoreCase))
                    matches.Add(path.ToLower());
            }

            return matches.ToArray();
        }

        private static bool OnCargoShip(BasePlayer player, Vector3 position, out CargoShipMonument cargoShipMonument)
        {
            cargoShipMonument = null;

            var cargoShip = player.GetParentEntity() as CargoShip;
            if (cargoShip == null)
                return false;

            cargoShipMonument = new CargoShipMonument(cargoShip);

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

        private static IEnumerator WaitForFrames(int frames)
        {
            for (var elapsed = 0; elapsed < frames; elapsed++)
            {
                yield return CoroutineEx.waitForEndOfFrame;
            }
        }

        private bool HasAdminPermission(string userId) =>
            permission.UserHasPermission(userId, PermissionAdmin);

        private bool HasAdminPermission(BasePlayer player) =>
            HasAdminPermission(player.UserIDString);

        private BaseMonument GetClosestMonument(BasePlayer player, Vector3 position)
        {
            CargoShipMonument cargoShipMonument;
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
                        cargoShipList.Add(new CargoShipMonument(cargoShip));
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

                UnityEngine.Object.Destroy(_coroutineComponent?.gameObject);
            }
        }

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

        private class CargoShipMonument : BaseMonument
        {
            public CargoShip CargoShip { get; private set; }
            public override bool IsValid => base.IsValid && !CargoShip.IsDestroyed;

            private OBB BoundingBox => CargoShip.WorldSpaceBounds();

            public CargoShipMonument(CargoShip cargoShip) : base(cargoShip)
            {
                CargoShip = cargoShip;
            }

            public override Vector3 ClosestPointOnBounds(Vector3 position) =>
                BoundingBox.ClosestPoint(position);

            public override bool IsInBounds(Vector3 position) =>
                BoundingBox.Contains(position);
        }

        #endregion

        #region Entity Component

        private class MonumentEntityComponent : FacepunchBehaviour
        {
            public static void AddToEntity(BaseEntity entity, EntityAdapterBase adapter, BaseMonument monument) =>
                entity.gameObject.AddComponent<MonumentEntityComponent>().Init(adapter, monument);

            public static MonumentEntityComponent GetForEntity(BaseEntity entity) =>
                entity.GetComponent<MonumentEntityComponent>();

            public static MonumentEntityComponent GetForEntity(uint id) =>
                BaseNetworkable.serverEntities.Find(id)?.GetComponent<MonumentEntityComponent>();

            public EntityAdapterBase Adapter;
            private BaseEntity _entity;

            private void Awake()
            {
                _entity = GetComponent<BaseEntity>();
                _pluginInstance?._entityTracker.RegisterEntity(_entity);
            }

            public void Init(EntityAdapterBase adapter, BaseMonument monument)
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

        #endregion

        #region Adapter/Controller - Base

        // Represents a single entity, spawn group, or spawn point at a single monument.
        private abstract class BaseAdapter
        {
            public BaseIdentifiableData Data { get; private set; }
            public BaseController Controller { get; private set; }
            public BaseMonument Monument { get; private set; }

            public BaseAdapter(BaseIdentifiableData data, BaseController controller, BaseMonument monument)
            {
                Data = data;
                Controller = controller;
                Monument = monument;
            }

            public abstract void Spawn();
            public abstract void Kill();
        }

        // Represents a single entity or spawn point at a single monument.
        private abstract class BaseTransformAdapter : BaseAdapter
        {
            public BaseTransformData TransformData { get; private set; }

            protected Transform _transform;
            public virtual Vector3 Position => _transform.position;
            public virtual Quaternion Rotation => _transform.rotation;

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

            public BaseController(ProfileController profileController, BaseIdentifiableData data)
            {
                ProfileController = profileController;
                Data = data;
            }

            public abstract BaseAdapter CreateAdapter(BaseMonument monument);

            public virtual void PreUnload() {}
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

            public bool TryDestroyAndRemove(out string monumentAliasOrShortName)
            {
                var profile = ProfileController.Profile;
                if (!profile.RemoveData(Data, out monumentAliasOrShortName))
                {
                    _pluginInstance?.LogError($"Unexpected error: {Data.GetType()} {Data.Id} was not found in profile {profile.Name}");
                    return false;
                }

                Destroy();
                return true;
            }

            public bool TryDestroyAndRemove()
            {
                string monumentAliasOrShortName;
                return TryDestroyAndRemove(out monumentAliasOrShortName);
            }

            public IEnumerator SpawnAtMonumentsRoutine(IEnumerable<BaseMonument> monumentList)
            {
                foreach (var monument in monumentList)
                {
                    _pluginInstance.TrackStart();
                    SpawnAtMonument(monument);
                    _pluginInstance.TrackEnd();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            public IEnumerator DestroyRoutine()
            {
                foreach (var adapter in Adapters.ToArray())
                {
                    _pluginInstance?.TrackStart();
                    adapter.Kill();
                    _pluginInstance?.TrackEnd();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            protected void Destroy()
            {
                PreUnload();

                if (Adapters.Count > 0)
                {
                    CoroutineManager.StartGlobalCoroutine(DestroyRoutine());
                }

                ProfileController.OnControllerKilled(this);
            }
        }

        #endregion

        #region Entity Adapter/Controller - Base

        private abstract class EntityAdapterBase : BaseTransformAdapter
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

                var cargoShipMonument = Monument as CargoShipMonument;
                if (cargoShipMonument != null)
                {
                    entity.SetParent(cargoShipMonument.CargoShip, worldPositionStays: true);

                    var mountable = entity as BaseMountable;
                    if (mountable != null)
                        mountable.isMobile = true;
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
                _pluginInstance?._entityListenerManager.OnAdapterSpawned(adapter as EntityAdapterBase);
            }

            public override void OnAdapterKilled(BaseAdapter adapter)
            {
                base.OnAdapterKilled(adapter);
                _pluginInstance?._entityListenerManager.OnAdapterKilled(adapter as EntityAdapterBase);
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

            public SingleEntityAdapter(EntityControllerBase controller, EntityData entityData, BaseMonument monument) : base(controller, monument, entityData) {}

            public override void Spawn()
            {
                Entity = CreateEntity(EntityData.PrefabName, IntendedPosition, IntendedRotation);
                _transform = Entity.transform;

                OnEntitySpawn();
                Entity.Spawn();
                OnEntitySpawned();
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

            protected virtual void OnEntitySpawn()
            {
                if (EntityData.Skin != 0)
                    Entity.skinID = EntityData.Skin;

                var combatEntity = Entity as BaseCombatEntity;
                if (combatEntity != null)
                {
                    if (ShouldBeImmortal())
                    {
                        combatEntity.baseProtection = _pluginInstance._immortalProtection;
                    }

                    combatEntity.pickup.enabled = false;
                }

                var stabilityEntity = Entity as StabilityEntity;
                if (stabilityEntity != null)
                {
                    stabilityEntity.grounded = true;
                }

                var ioEntity = Entity as IOEntity;
                if (ioEntity != null)
                {
                    ioEntity.SetFlag(BaseEntity.Flags.On, true);
                    ioEntity.SetFlag(IOEntity.Flag_HasPower, true);
                }
            }

            protected virtual void OnEntitySpawned()
            {
                // Disable saving after spawn to make sure children that are spawned late also have saving disabled.
                // For example, the Lift class spawns a sub entity.
                EnableSavingResursive(Entity, false);

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
                var cargoShip = Entity.GetParentEntity() as CargoShip;
                if (cargoShip != null)
                {
                    var cargoCameraList = new List<CCTV_RC>();
                    foreach (var child in cargoShip.children)
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

            protected override void OnEntitySpawn()
            {
                base.OnEntitySpawn();

                (Entity as Signage)?.EnsureInitialized();

                var carvablePumpkin = Entity as CarvablePumpkin;
                if (carvablePumpkin != null)
                {
                    carvablePumpkin.EnsureInitialized();
                    carvablePumpkin.SetFlag(BaseEntity.Flags.On, true);
                }

                Entity.SetFlag(BaseEntity.Flags.Locked, true);
            }

            protected override void OnEntitySpawned()
            {
                base.OnEntitySpawned();

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

            protected override void OnEntitySpawn()
            {
                base.OnEntitySpawn();

                UpdateIdentifier();
                UpdateDirection();
            }

            protected override void OnEntitySpawned()
            {
                base.OnEntitySpawned();

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

            public void ResetIdentifier() => SetIdentifier(string.Empty);

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
                var cargoShip = Entity.GetParentEntity() as CargoShip;
                if (cargoShip != null)
                {
                    var cargoComputerStationList = new List<ComputerStation>();
                    foreach (var child in cargoShip.children)
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

            // Ensure the RC identifiers are freed up as soon as possible to avoid conflicts when reloading.
            public override void PreUnload() => ResetIdentifier();

            public void UpdateIdentifier()
            {
                ProfileController.StartCoroutine(UpdateIdentifierRoutine());
            }

            public void ResetIdentifier()
            {
                foreach (var adapter in Adapters)
                    (adapter as CCTVEntityAdapter).ResetIdentifier();
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

        private abstract class EntityListenerBase
        {
            public virtual void Init() {}
            public virtual void OnServerInitialized() {}
            public abstract bool InterestedInEntity(EntityAdapterBase entityAdapter);
            public abstract void OnAdapterSpawned(EntityAdapterBase entityAdapter);
            public abstract void OnAdapterKilled(EntityAdapterBase entityAdapter);
        }

        private abstract class DynamicHookListener : EntityListenerBase
        {
            protected string[] _dynamicHookNames;
            private int _controllerCount;

            public override void Init()
            {
                UnsubscribeHooks();
            }

            public override void OnAdapterSpawned(EntityAdapterBase entityAdapter)
            {
                _controllerCount++;

                if (_controllerCount == 1)
                    SubscribeHooks();
            }

            public override void OnAdapterKilled(EntityAdapterBase entityAdapter)
            {
                _controllerCount--;

                if (_controllerCount == 0)
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

            public override bool InterestedInEntity(EntityAdapterBase entityAdapterBase) =>
                FindBaseEntityForPrefab(entityAdapterBase.EntityData.PrefabName) is ISignage;
        }

        private class EntityListenerManager
        {
            private EntityListenerBase[] _listeners = new EntityListenerBase[]
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
                    if (listener.InterestedInEntity(entityAdapter))
                        listener.OnAdapterSpawned(entityAdapter);
                }
            }

            public void OnAdapterKilled(EntityAdapterBase entityAdapter)
            {
                foreach (var listener in _listeners)
                {
                    if (listener.InterestedInEntity(entityAdapter))
                        listener.OnAdapterKilled(entityAdapter);
                }
            }
        }

        #endregion

        #region Spawn Group Components

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
                var vehicle = GetComponent<BaseVehicle>();
                if (vehicle != null && !vehicle.IsDestroyed)
                {
                    EnableSavingResursive(vehicle, true);
                }

                Destroy(GetComponent<SpawnPointInstance>());
                Destroy(this);
            }
        }

        private class CustomSpawnPoint : BaseSpawnPoint
        {
            public SpawnPointData SpawnPointData;

            private Transform _transform;
            private List<SpawnPointInstance> _instances = new List<SpawnPointInstance>();

            private void Awake()
            {
                _transform = transform;
            }

            public override void GetLocation(out Vector3 position, out Quaternion rotation)
            {
                position = _transform.position;
                rotation = _transform.rotation;

                if (SpawnPointData.RandomRadius > 0)
                {
                    Vector2 vector = UnityEngine.Random.insideUnitCircle * SpawnPointData.RandomRadius;
                    position += new Vector3(vector.x, 0f, vector.y);
                }

                if (SpawnPointData.RandomRotation)
                {
                    rotation *= Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                }

                if (SpawnPointData.DropToGround)
                {
                    DropToGround(ref position, ref rotation);
                }
            }

            public override void ObjectSpawned(SpawnPointInstance instance)
            {
                _instances.Add(instance);

                var entity = instance.GetComponent<BaseEntity>();

                var vehicle = entity as BaseVehicle;
                if (vehicle != null)
                {
                    instance.gameObject.AddComponent<SpawnedVehicleComponent>();
                }
            }

            public override void ObjectRetired(SpawnPointInstance instance)
            {
                _instances.Remove(instance);
            }

            public override bool IsAvailableTo(GameObjectRef prefabRef)
            {
                if (SpawnPointData.Exclusive && _instances.Count > 0)
                {
                    return false;
                }

                if (SpawnPointData.CheckSpace)
                {
                    return SingletonComponent<SpawnHandler>.Instance.CheckBounds(prefabRef.Get(), _transform.position, _transform.rotation, Vector3.one);
                }

                return true;
            }

            public void OnDestroy()
            {
                for (var i = _instances.Count - 1; i >= 0; i--)
                {
                    var entity = _instances[i].GetComponent<BaseEntity>();
                    if (entity != null && !entity.IsDestroyed)
                    {
                        entity.Kill();
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

            private AIInformationZone _cachedInfoZone;
            private bool _didLookForInfoZone;

            public void UpdateSpawnClock()
            {
                spawnClock = new LocalClock();
                spawnClock.Add(GetSpawnDelta(), GetSpawnVariance(), Spawn);
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

                        var humanNpc = npcPlayer as HumanNPC;
                        if (humanNpc != null)
                        {
                            virtualInfoZone.RegisterSleepableEntity(humanNpc.Brain);
                        }
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
            }
        }

        #endregion

        #region SpawnGroup Adapter/Controller

        private class SpawnPointAdapter : BaseTransformAdapter
        {
            public SpawnPointData SpawnPointData { get; private set; }
            public CustomSpawnPoint SpawnPoint { get; private set; }

            public SpawnPointAdapter(SpawnPointData spawnPointData, BaseController controller, BaseMonument monument) : base(spawnPointData, controller, monument)
            {
                SpawnPointData = spawnPointData;
            }

            public override void Spawn()
            {
                var gameObject = new GameObject();
                _transform = gameObject.transform;
                _transform.SetPositionAndRotation(IntendedPosition, IntendedRotation);

                SpawnPoint = gameObject.AddComponent<CustomSpawnPoint>();
                SpawnPoint.SpawnPointData = SpawnPointData;
            }

            public override void Kill()
            {
                UnityEngine.Object.Destroy(SpawnPoint?.gameObject);
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

                if (SpawnGroup.prefabs == null)
                    SpawnGroup.prefabs = new List<SpawnGroup.SpawnEntry>();

                UpdateMaxPopulation();
                UpdateRespawnDelay();
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
                foreach (var spawnPointAdapter in Adapters.ToArray())
                {
                    spawnPointAdapter.Kill();
                }

                UnityEngine.Object.Destroy(SpawnGroup?.gameObject);

                Controller.OnAdapterKilled(this);
            }

            private void UpdateMaxPopulation()
            {
                SpawnGroup.numToSpawnPerTickMin = SpawnGroupData.MaxPopulation;
                SpawnGroup.numToSpawnPerTickMax = SpawnGroupData.MaxPopulation;
                SpawnGroup.maxPopulation = SpawnGroupData.MaxPopulation;
            }

            private void UpdateRespawnDelay()
            {
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
                    return;

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
                UpdateMaxPopulation();
                UpdateRespawnDelay();
                UpdatePrefabEntries();
                UpdateSpawnPointReferences();
            }

            public void AddSpawnPoint(SpawnPointData spawnPointData)
            {
                var spawnPointAdapter = new SpawnPointAdapter(spawnPointData, Controller, Monument);
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
                Adapters.Remove(spawnPointAdapter);

                UpdateSpawnPointReferences();
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
        }

        #endregion

        #region Entity Controller Factories

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

                ShowNearbyAdapters(player, player.transform.position, playerInfo);

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
                _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelMonument, adapter.Monument.AliasOrShortName, entityController.Adapters.Count));
                _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelProfile, profileController.Profile.Name));

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
                    _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnGroupPopulation, spawnGroupAdapter.SpawnGroup.currentPopulation, spawnGroupData.MaxPopulation));
                    _sb.AppendLine(_pluginInstance.GetMessage(player, Lang.ShowLabelSpawnGroupRespanDelay, FormatTime(spawnGroupData.RespawnDelayMin), FormatTime(spawnGroupData.RespawnDelayMax)));

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
                            ShowEntityInfo(player, entityAdapter, playerInfo);

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

        #region Base Data

        private abstract class BaseIdentifiableData
        {
            [JsonProperty("Id", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Guid Id;
        }

        private abstract class BaseTransformData : BaseIdentifiableData
        {
            [JsonProperty("Position")]
            public Vector3 Position;

            // Kept for backwards compatibility.
            [JsonProperty("RotationAngle", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float DeprecatedRotationAngle { set { RotationAngles = new Vector3(0, value, 0); } }

            [JsonProperty("RotationAngles", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Vector3 RotationAngles;

            [JsonProperty("OnTerrain", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool OnTerrain = false;
        }

        #endregion

        #region Entity Data

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

            [JsonProperty("RespawnDelayMin")]
            public float RespawnDelayMin = 10;

            [JsonProperty("RespawnDelayMax")]
            public float RespawnDelayMax = 20;

            [JsonProperty("Prefabs")]
            public List<WeightedPrefabData> Prefabs = new List<WeightedPrefabData>();

            [JsonProperty("SpawnPoints")]
            public List<SpawnPointData> SpawnPoints = new List<SpawnPointData>();
        }

        private class MonumentData
        {
            [JsonProperty("Entities")]
            public List<EntityData> Entities = new List<EntityData>();

            [JsonProperty("SpawnGroups")]
            public List<SpawnGroupData> SpawnGroups = new List<SpawnGroupData>();

            [JsonIgnore]
            public int NumSpawnables => Entities.Count + SpawnGroups.Count;

            public ICollection<BaseIdentifiableData> GetSpawnables()
            {
                var list = new List<BaseIdentifiableData>(NumSpawnables);
                list.AddRange(Entities);
                list.AddRange(SpawnGroups);
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

                _pluginInstance.LogError($"RemoveData not implemented for type: {data.GetType()}");
                return false;
            }
        }

        #endregion

        #region Profile Data

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

        #region Profile Controller

        private enum ProfileState { Loading, Loaded, Unloading, Unloaded }

        private class ProfileController
        {
            public Profile Profile { get; private set; }
            public ProfileState ProfileState { get; private set; } = ProfileState.Unloaded;
            public WaitUntil WaitUntilLoaded;
            public WaitUntil WaitUntilUnloaded;

            private CoroutineManager _coroutineManager = new CoroutineManager();
            private Dictionary<BaseIdentifiableData, BaseController> _controllersByData = new Dictionary<BaseIdentifiableData, BaseController>();

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
                foreach (var controller in _controllersByData.Values)
                {
                    var controllerOfType = controller as T;
                    if (controllerOfType != null)
                        yield return controllerOfType;
                }
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

            public void Load(ReferenceTypeWrapper<int> entityCounter = null)
            {
                if (ProfileState == ProfileState.Loading || ProfileState == ProfileState.Loaded)
                    return;

                ProfileState = ProfileState.Loading;
                StartCoroutine(LoadRoutine(entityCounter));
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
                if (ProfileState == ProfileState.Loading)
                    yield break;

                ProfileState = ProfileState.Loading;
                StartCoroutine(PartialLoadForLateMonumentRoutine(dataList, monument));
                yield return WaitUntilLoaded;
            }

            public void SpawnNewData(BaseIdentifiableData data, ICollection<BaseMonument> monuments, bool delay = false)
            {
                if (ProfileState == ProfileState.Unloading || ProfileState == ProfileState.Unloaded)
                    return;

                ProfileState = ProfileState.Loading;
                StartCoroutine(PartialLoadForLateDataRoutine(data, monuments, delay));
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
                    _controllersByData[data] = controller;
                }
                return controller;
            }

            private IEnumerator LoadRoutine(ReferenceTypeWrapper<int> entityCounter)
            {
                foreach (var entry in Profile.MonumentDataMap.ToArray())
                {
                    var monumentData = entry.Value;
                    if (monumentData.NumSpawnables == 0)
                        continue;

                    var monumentAliasOrShortName = entry.Key;
                    var matchingMonuments = _pluginInstance.GetMonumentsByAliasOrShortName(monumentAliasOrShortName);
                    if (matchingMonuments == null)
                        continue;

                    if (entityCounter != null)
                        entityCounter.Value += matchingMonuments.Count * monumentData.Entities.Count;

                    foreach (var data in monumentData.GetSpawnables())
                        yield return SpawnAtMonumentsRoutine(this, data, matchingMonuments);
                }

                ProfileState = ProfileState.Loaded;
            }

            private IEnumerator UnloadRoutine()
            {
                foreach (var controller in _controllersByData.Values.ToArray())
                {
                    yield return controller.DestroyRoutine();
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

            private IEnumerator PartialLoadForLateMonumentRoutine(ICollection<BaseIdentifiableData> dataList, BaseMonument monument)
            {
                foreach (var entityData in dataList)
                {
                    // Check for null in case the cargo ship was destroyed.
                    if (!monument.IsValid)
                        break;

                    _pluginInstance.TrackStart();
                    EnsureController(entityData).SpawnAtMonument(monument);
                    _pluginInstance.TrackEnd();
                    yield return CoroutineEx.waitForEndOfFrame;
                }

                ProfileState = ProfileState.Loaded;
            }

            private IEnumerator SpawnAtMonumentsRoutine(ProfileController profileController, BaseIdentifiableData data, IEnumerable<BaseMonument> monumentList)
            {
                _pluginInstance.TrackStart();
                var controller = GetController(data);
                if (controller != null)
                {
                    // If the controller already exists, the entity was added while the plugin was still spawning entities.
                    _pluginInstance.TrackEnd();
                    yield break;
                }

                controller = EnsureController(data);
                _pluginInstance.TrackEnd();

                yield return controller.SpawnAtMonumentsRoutine(monumentList);
            }

            private IEnumerator PartialLoadForLateDataRoutine(BaseIdentifiableData data, ICollection<BaseMonument> monumentList, bool delay)
            {
                if (delay)
                {
                    // Since the previous profile will despawn one object per frame, wait exactly that long before respawning.
                    // One reason to do this is to make sure spawn points that check for space don't find the previous entity in the way.
                    yield return WaitForFrames(monumentList.Count);
                }

                yield return SpawnAtMonumentsRoutine(this, data, monumentList);
                ProfileState = ProfileState.Loaded;
            }
        }

        #endregion

        #region Profile Manager

        // This works around coroutines not allowing ref/out parameters.
        private class ReferenceTypeWrapper<T>
        {
            public T Value;

            public ReferenceTypeWrapper(T value = default(T))
            {
                Value = value;
            }
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

                    var entityCounter = new ReferenceTypeWrapper<int>();

                    controller.Load(entityCounter);
                    yield return controller.WaitUntilLoaded;

                    var profile = controller.Profile;
                    var byAuthor = !string.IsNullOrWhiteSpace(profile.Author) ? $" by {profile.Author}" : string.Empty;

                    _pluginInstance.Puts($"Loaded profile {profile.Name}{byAuthor} ({entityCounter.Value} entities spawned).");
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

            public IEnumerable<T> GetEnabledAdapters<T>() where T : BaseAdapter
            {
                foreach (var profileControler in GetEnabledProfileControllers())
                {
                    foreach (var adapter in profileControler.GetAdapters<T>())
                        yield return adapter;
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

            private IEnumerable<ProfileController> GetEnabledProfileControllers()
            {
                foreach (var profileControler in _profileControllers)
                {
                    if (profileControler.IsEnabled)
                    {
                        yield return profileControler;
                    }
                }
            }
        }

        #endregion

        #region Data

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

        #endregion

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

        private class Lang
        {
            public const string ErrorNoPermission = "Error.NoPermission";
            public const string ErrorMonumentFinderNotLoaded = "Error.MonumentFinderNotLoaded";
            public const string ErrorNoMonuments = "Error.NoMonuments";
            public const string ErrorNotAtMonument = "Error.NotAtMonument";
            public const string ErrorNoSuitableEntityFound = "Error.NoSuitableEntityFound";
            public const string ErrorEntityNotEligible = "Error.EntityNotEligible";

            public const string SpawnErrorSyntax = "Spawn.Error.Syntax";
            public const string SpawnErrorNoProfileSelected = "Spawn.Error.NoProfileSelected";
            public const string SpawnErrorEntityNotFound = "Spawn.Error.EntityNotFound";
            public const string SpawnErrorMultipleMatches = "Spawn.Error.MultipleMatches";
            public const string SpawnErrorNoTarget = "Spawn.Error.NoTarget";
            public const string SpawnSuccess = "Spawn.Success2";
            public const string KillSuccess = "Kill.Success2";
            public const string MoveNothingToDo = "Move.NothingToDo";
            public const string MoveSuccess = "Move.Success";
            public const string AddonTypeSpawnPoint = "AddonType.SpawnPoint";

            public const string ShowSuccess = "Show.Success";
            public const string ShowHeaderEntity = "Show.Header.Entity";
            public const string ShowHeaderSpawnGroup = "Show.Header.SpawnGroup";
            public const string ShowHeaderSpawnPoint = "Show.Header.SpawnPoint";
            public const string ShowLabelMonument = "Show.Label.Monument";
            public const string ShowLabelProfile = "Show.Label.Profile";
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
            public const string ShowLabelSpawnGroupPopulation = "Show.Label.SpawnGroup.Population";
            public const string ShowLabelSpawnGroupRespanDelay = "Show.Label.SpawnGroup.RespawnDelay";
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
            public const string ProfileDescribeItem = "Profile.Describe.Item";
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
                [Lang.ErrorNoSuitableEntityFound] = "Error: No suitable entity found.",
                [Lang.ErrorEntityNotEligible] = "Error: That entity is not managed by Monument Addons.",

                [Lang.SpawnErrorSyntax] = "Syntax: <color=#fd4>maspawn <entity></color>",
                [Lang.SpawnErrorNoProfileSelected] = "Error: No profile selected. Run <color=#fd4>maprofile help</color> for help.",
                [Lang.SpawnErrorEntityNotFound] = "Error: Entity <color=#fd4>{0}</color> not found.",
                [Lang.SpawnErrorMultipleMatches] = "Multiple matches:\n",
                [Lang.SpawnErrorNoTarget] = "Error: No valid spawn position found.",
                [Lang.SpawnSuccess] = "Spawned entity at <color=#fd4>{0}</color> matching monument(s) and saved to <color=#fd4>{1}</color> profile for monument <color=#fd4>{2}</color>.",
                [Lang.KillSuccess] = "Killed entity at <color=#fd4>{0}</color> matching monument(s) and removed from profile <color=#fd4>{1}</color>.",
                [Lang.MoveNothingToDo] = "That entity is already at the saved position.",
                [Lang.MoveSuccess] = "Updated entity position at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.",
                [Lang.AddonTypeSpawnPoint] = "Spawn point",

                [Lang.ShowSuccess] = "Showing nearby Monument Addons for <color=#fd4>{0}</color>.",
                [Lang.ShowLabelMonument] = "Monument: {0} (x{1})",
                [Lang.ShowLabelProfile] = "Profile: {0}",
                [Lang.ShowLabelSkin] = "Skin: {0}",
                [Lang.ShowLabelScale] = "Scale: {0}",
                [Lang.ShowLabelRCIdentifier] = "RC Identifier: {0}",

                [Lang.ShowHeaderEntity] = "Entity: {0}",
                [Lang.ShowHeaderSpawnGroup] = "Spawn Group: {0}",
                [Lang.ShowHeaderSpawnPoint] = "Spawn Point ({0})",

                [Lang.ShowLabelFlags] = "Flags: {0}",
                [Lang.ShowLabelSpawnPointExclusive] = "Exclusive",
                [Lang.ShowLabelSpawnPointRandomRotation] = "Random rotation",
                [Lang.ShowLabelSpawnPointDropsToGround] = "Drops to ground",
                [Lang.ShowLabelSpawnPointChecksSpace] = "Checks space",
                [Lang.ShowLabelSpawnPointRandomRadius] = "Random spawn radius: {0:f1}",

                [Lang.ShowLabelSpawnGroupPoints] = "Spawn points: {0}",
                [Lang.ShowLabelSpawnGroupPopulation] = "Population: {0} / {1}",
                [Lang.ShowLabelSpawnGroupRespanDelay] = "Respawn delay: {0} - {1}",
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
                [Lang.ProfileDescribeItem] = "<color=#fd4>{0}</color> x{1} @ {2}",
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
