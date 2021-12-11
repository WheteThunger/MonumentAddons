using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Configuration;
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
    [Info("Monument Addons", "WhiteThunder", "0.7.4")]
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

        private const string PermissionAdmin = "monumentaddons.admin";

        private const string CargoShipShortName = "cargoshiptest";

        private const string DefaultProfileName = "Default";

        private static readonly int HitLayers = Rust.Layers.Mask.Construction
            + Rust.Layers.Mask.Default
            + Rust.Layers.Mask.Deployed
            + Rust.Layers.Mask.Terrain
            + Rust.Layers.Mask.World;

        private readonly EntityManager _entityManager = new EntityManager();
        private readonly CoroutineManager _coroutineManager = new CoroutineManager();
        private ProfileManager _profileManager;

        private ProtectionProperties _immortalProtection;

        private Coroutine _startupCoroutine;
        private bool _serverInitialized = false;

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            _pluginData = StoredData.Load();

            _profileManager = new ProfileManager(_entityManager);

            // Ensure the profile folder is created to avoid errors.
            Profile.LoadDefaultProfile();

            permission.RegisterPermission(PermissionAdmin, this);

            Unsubscribe(nameof(OnEntitySpawned));

            _entityManager.Init();
        }

        private void OnServerInitialized()
        {
            _coroutineManager.OnServerInitialized();

            _immortalProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
            _immortalProtection.name = "MonumentAddonsProtection";
            _immortalProtection.Add(1);

            if (CheckDependencies())
                StartupRoutine();

            _serverInitialized = true;
        }

        private void Unload()
        {
            _coroutineManager.Destroy();
            _profileManager.UnloadAllProfiles();

            UnityEngine.Object.Destroy(_immortalProtection);

            EntityAdapterBase.ClearEntityCache();

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
            if (EntityAdapterBase.IsMonumentEntity(entity))
                return false;

            return null;
        }

        private bool? CanUpdateSign(BasePlayer player, ISignage signage)
        {
            if (EntityAdapterBase.IsMonumentEntity(signage as BaseEntity)
                && !permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                ChatMessage(player, Lang.ErrorNoPermission);
                return false;
            }

            return null;
        }

        private void OnSignUpdated(ISignage signage, BasePlayer player)
        {
            if (!EntityAdapterBase.IsMonumentEntity(signage as BaseEntity))
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
            if (!EntityAdapterBase.IsMonumentEntity(signage as BaseEntity))
                return;

            var component = MonumentEntityComponent.GetForEntity(signage.NetworkID);
            if (component == null)
                return;

            var controller = component.Adapter.Controller as SignEntityController;
            if (controller == null)
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
            controller.ProfileController.Profile.Save();
        }

        private void OnEntityScaled(BaseEntity entity, float scale)
        {
            if (!EntityAdapterBase.IsMonumentEntity(entity))
                return;

            var component = MonumentEntityComponent.GetForEntity(entity);
            if (component == null)
                return;

            var controller = component.Adapter.Controller as SingleEntityController;
            if (controller == null || controller.EntityData.Scale == scale)
                return;

            controller.EntityData.Scale = scale;
            controller.UpdateScale();
            controller.ProfileController.Profile.Save();
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

        private void ScaleEntity(BaseEntity entity, float scale)
        {
            EntityScaleManager?.Call("API_ScaleEntity", entity, scale);
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
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            if (MonumentFinder == null)
            {
                ReplyToPlayer(player, Lang.ErrorMonumentFinderNotLoaded);
                return;
            }

            var controller = _profileManager.GetPlayerProfileControllerOrDefault(player.Id);
            if (controller == null)
            {
                ReplyToPlayer(player, Lang.SpawnErrorNoProfileSelected);
                return;
            }

            if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            {
                ReplyToPlayer(player, Lang.SpawnErrorSyntax);
                return;
            }

            var matches = FindPrefabMatches(args[0]);
            if (matches.Length == 0)
            {
                ReplyToPlayer(player, Lang.SpawnErrorEntityNotFound, args[0]);
                return;
            }

            if (matches.Length > 1)
            {
                var replyMessage = GetMessage(player, Lang.SpawnErrorMultipleMatches);
                foreach (var match in matches)
                    replyMessage += $"\n{GetShortName(match)}";

                player.Reply(replyMessage);
                return;
            }

            var prefabName = matches[0];
            var basePlayer = player.Object as BasePlayer;

            Vector3 position;
            if (!TryGetHitPosition(basePlayer, out position))
            {
                ReplyToPlayer(player, Lang.SpawnErrorNoTarget);
                return;
            }

            var closestMonument = GetClosestMonument(basePlayer, position);
            if (closestMonument == null)
            {
                ReplyToPlayer(player, Lang.ErrorNoMonuments);
                return;
            }

            if (!closestMonument.IsInBounds(position))
            {
                var closestPoint = closestMonument.ClosestPointOnBounds(position);
                var distance = (position - closestPoint).magnitude;
                ReplyToPlayer(player, Lang.ErrorNotAtMonument, closestMonument.AliasOrShortName, distance.ToString("f1"));
                return;
            }

            var localPosition = closestMonument.InverseTransformPoint(position);
            var localRotationAngle = basePlayer.HasParent()
                ? basePlayer.viewAngles.y - 180
                : basePlayer.viewAngles.y - closestMonument.Rotation.eulerAngles.y + 180;

            var entityData = new EntityData
            {
                PrefabName = prefabName,
                Position = localPosition,
                RotationAngle = (localRotationAngle + 360) % 360,
                OnTerrain = Math.Abs(position.y - TerrainMeta.HeightMap.GetHeight(position)) <= TerrainProximityTolerance
            };

            var matchingMonuments = GetMonumentsByAliasOrShortName(closestMonument.AliasOrShortName);

            controller.Profile.AddEntityData(closestMonument.AliasOrShortName, entityData);
            controller.SpawnNewEntity(entityData, matchingMonuments);

            ReplyToPlayer(player, Lang.SpawnSuccess, matchingMonuments.Count, controller.Profile.Name, closestMonument.AliasOrShortName);
        }

        [Command("makill")]
        private void CommandKill(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            BaseEntity entity;
            EntityControllerBase controller;
            if (!VerifyLookEntity(player, out entity, out controller))
                return;

            var numEntities = controller.Adapters.Count;
            controller.Destroy();

            var profile = controller.ProfileController.Profile;
            profile.RemoveEntityData(controller.EntityData);
            ReplyToPlayer(player, Lang.KillSuccess, numEntities, profile.Name);
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

            CCTV_RC cctv;
            CCTVEntityController controller;
            if (!VerifyLookEntity(player, out cctv, out controller))
                return;

            if (controller.EntityData.CCTV == null)
                controller.EntityData.CCTV = new CCTVInfo();

            controller.EntityData.CCTV.RCIdentifier = args[0];
            controller.UpdateIdentifier();

            var profile = controller.ProfileController.Profile;
            profile.Save();

            ReplyToPlayer(player, Lang.CCTVSetIdSuccess, args[0], controller.Adapters.Count, profile.Name);
        }

        [Command("masetdir")]
        private void CommandSetDirection(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            CCTV_RC cctv;
            CCTVEntityController controller;
            if (!VerifyLookEntity(player, out cctv, out controller))
                return;

            var basePlayer = player.Object as BasePlayer;
            var direction = Vector3Ex.Direction(basePlayer.eyes.position, cctv.transform.position);
            direction = cctv.transform.InverseTransformDirection(direction);
            var lookAngles = BaseMountable.ConvertVector(Quaternion.LookRotation(direction).eulerAngles);

            if (controller.EntityData.CCTV == null)
                controller.EntityData.CCTV = new CCTVInfo();

            controller.EntityData.CCTV.Pitch = lookAngles.x;
            controller.EntityData.CCTV.Yaw = lookAngles.y;
            controller.UpdateDirection();

            var profile = controller.ProfileController.Profile;
            profile.Save();

            ReplyToPlayer(player, Lang.CCTVSetDirectionSuccess, controller.Adapters.Count, profile.Name);
        }

        [Command("maskin")]
        private void CommandSkin(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            BaseEntity entity;
            SingleEntityController controller;
            if (!VerifyLookEntity(player, out entity, out controller))
                return;

            if (args.Length == 0)
            {
                ReplyToPlayer(player, Lang.SkinGet, entity.skinID, cmd);
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
            controller.UpdateSkin();

            var profile = controller.ProfileController.Profile;
            profile.Save();

            ReplyToPlayer(player, Lang.SkinSetSuccess, skinId, controller.Adapters.Count, profile.Name);
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
                SubCommandHelp(player);
                return;
            }

            switch (args[0].ToLower())
            {
                case "list":
                {
                    var profileList = ProfileInfo.GetList();
                    if (profileList.Length == 0)
                    {
                        ReplyToPlayer(player, Lang.ProfileListEmpty);
                        return;
                    }

                    var playerProfileName = player.IsServer ? null : _pluginData.GetSelectedProfileName(player.Id);

                    profileList = profileList
                        .OrderByDescending(profile => profile.Name == playerProfileName)
                        .ThenByDescending(profile => profile.Enabled)
                        .ThenBy(profile => profile.Name)
                        .ToArray();

                    var sb = new StringBuilder();
                    sb.AppendLine(GetMessage(player, Lang.ProfileListHeader));
                    foreach (var profile in profileList)
                    {
                        var messageName = profile.Name == playerProfileName
                            ? Lang.ProfileListItemSelected
                            : profile.Enabled
                            ? Lang.ProfileListItemEnabled
                            : Lang.ProfileListItemDisabled;

                        sb.AppendLine(GetMessage(player, messageName, profile.Name));
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
                    foreach (var monumentEntry in controller.Profile.GetEntityAggregates())
                    {
                        var aliasOrShortName = monumentEntry.Key;
                        foreach (var countEntry in monumentEntry.Value)
                            sb.AppendLine(GetMessage(player, Lang.ProfileDescribeItem, GetShortName(countEntry.Key), countEntry.Value, aliasOrShortName));
                    }
                    player.Reply(sb.ToString());
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
                    controller.Enable();
                    ReplyToPlayer(player, Lang.ProfileSelectSuccess, controller.Profile.Name);
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

                    var controller = _profileManager.CreateProfile(newName);
                    if (!player.IsServer)
                        _pluginData.SetProfileSelected(player.Id, newName);

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
                    break;
                }

                case "reload":
                {
                    ProfileController controller;
                    if (!VerifyProfile(player, args, out controller, Lang.ProfileReloadSyntax))
                        return;

                    controller.Reload();
                    ReplyToPlayer(player, Lang.ProfileReloadSuccess, controller.Profile.Name);
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
                    _pluginData.SetProfileEnabled(profileName);
                    _pluginData.Save();
                    ReplyToPlayer(player, Lang.ProfileEnableSuccess, profileName);
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

                case "moveto":
                {
                    BaseEntity entity;
                    EntityControllerBase entityController;
                    if (!VerifyLookEntity(player, out entity, out entityController))
                        return;

                    ProfileController profileController;
                    if (!VerifyProfile(player, args, out profileController, Lang.ProfileMoveToSyntax))
                        return;

                    var entityData = entityController.EntityData;
                    var oldProfile = entityController.ProfileController.Profile;

                    if (profileController == entityController.ProfileController)
                    {
                        ReplyToPlayer(player, Lang.ProfileMoveToAlreadyPresent, GetShortName(entityData.PrefabName), oldProfile.Name);
                        return;
                    }

                    string monumentAliasOrShortName;
                    if (!oldProfile.RemoveEntityData(entityData, out monumentAliasOrShortName))
                        return;

                    if (profileController.ProfileState == ProfileState.Unloading
                        || profileController.ProfileState == ProfileState.Unloaded)
                    {
                        entityController.PreUnload();
                        entityController.Destroy();
                    }

                    entityController.ProfileController = profileController;

                    var newProfile = profileController.Profile;
                    newProfile.AddEntityData(monumentAliasOrShortName, entityData);
                    ReplyToPlayer(player, Lang.ProfileMoveToSuccess, GetShortName(entityData.PrefabName), oldProfile.Name, newProfile.Name);
                    break;
                }

                default:
                {
                    SubCommandHelp(player);
                    break;
                }
            }
        }

        private void SubCommandHelp(IPlayer player)
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
            sb.AppendLine(GetMessage(player, Lang.ProfileHelpMoveTo));
            ReplyToPlayer(player, sb.ToString());
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

        private bool VerifyLookEntity<T>(IPlayer player, out T entity) where T : BaseEntity
        {
            var basePlayer = player.Object as BasePlayer;
            entity = GetLookEntity(basePlayer) as T;
            if (entity != null)
                return true;

            ReplyToPlayer(player, Lang.ErrorNoSuitableEntityFound);
            return false;
        }

        private bool VerifyLookEntity<TEntity, TController>(IPlayer player, out TEntity entity, out TController controller)
            where TEntity : BaseEntity where TController : EntityControllerBase
        {
            controller = null;

            if (!VerifyLookEntity(player, out entity))
                return false;

            var component = MonumentEntityComponent.GetForEntity(entity);
            if (component == null)
            {
                ReplyToPlayer(player, Lang.ErrorEntityNotEligible);
                return false;
            }

            controller = component.Adapter.Controller as TController;
            if (controller == null)
            {
                ReplyToPlayer(player, Lang.ErrorNoSuitableEntityFound);
                return false;
            }

            return true;
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
            controller = _profileManager.GetProfileController(profileName);
            if (controller != null)
                return true;

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

        #region Helper Methods

        private static bool TryGetHitPosition(BasePlayer player, out Vector3 position, float maxDistance = MaxRaycastDistance)
        {
            var layers = HitLayers;
            if (player.GetParentEntity() is CargoShip)
                layers += Rust.Layers.Mask.Vehicle_Large;

            RaycastHit hit;
            if (Physics.Raycast(player.eyes.HeadRay(), out hit, maxDistance, layers, QueryTriggerInteraction.Ignore))
            {
                position = hit.point;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private static BaseEntity GetLookEntity(BasePlayer basePlayer, float maxDistance = MaxRaycastDistance)
        {
            RaycastHit hit;
            return Physics.Raycast(basePlayer.eyes.HeadRay(), out hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                ? hit.GetEntity()
                : null;
        }

        private static string GetShortName(string prefabName)
        {
            var slashIndex = prefabName.LastIndexOf("/");
            var baseName = (slashIndex == -1) ? prefabName : prefabName.Substring(slashIndex + 1);
            return baseName.Replace(".prefab", "");
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

            public void OnServerInitialized()
            {
                _coroutineComponent = new GameObject().AddComponent<EmptyMonoBehavior>();
            }

            public Coroutine StartCoroutine(IEnumerator enumerator)
            {
                return _coroutineComponent.StartCoroutine(enumerator);
            }

            public void StopAll()
            {
                _coroutineComponent.StopAllCoroutines();
            }

            public void Destroy()
            {
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
            }

            public void Init(EntityAdapterBase adapter, BaseMonument monument)
            {
                Adapter = adapter;
            }

            private void OnDestroy()
            {
                Adapter.OnEntityDestroyed(_entity);
            }
        }

        #endregion

        #region Entity Adapter/Controller - Base

        private abstract class EntityAdapterBase
        {
            public static bool IsMonumentEntity(BaseEntity entity) =>
                entity != null && _registeredEntities.Contains(entity);

            public static void ClearEntityCache() => _registeredEntities.Clear();

            public EntityControllerBase Controller { get; private set; }
            public EntityData EntityData { get; private set; }
            public BaseMonument Monument { get; private set; }
            public virtual bool IsDestroyed { get; }

            protected static readonly HashSet<BaseEntity> _registeredEntities = new HashSet<BaseEntity>();

            public EntityAdapterBase(EntityControllerBase controller, EntityData entityData, BaseMonument monument)
            {
                Controller = controller;
                EntityData = entityData;
                Monument = monument;
            }

            public abstract void Spawn();
            public abstract void Kill();
            public abstract void OnEntityDestroyed(BaseEntity entity);

            protected BaseEntity CreateEntity(string prefabName, Vector3 position, Quaternion rotation)
            {
                var entity = GameManager.server.CreateEntity(EntityData.PrefabName, position, rotation);
                if (entity == null)
                    return null;

                // In case the plugin doesn't clean it up on server shutdown, make sure it doesn't come back so it's not duplicated.
                entity.EnableSaving(false);

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
                _registeredEntities.Add(entity);

                return entity;
            }
        }

        private abstract class EntityControllerBase
        {
            public EntityManager Manager { get; private set; }
            public ProfileController ProfileController;
            public EntityData EntityData { get; private set; }
            public List<EntityAdapterBase> Adapters { get; private set; } = new List<EntityAdapterBase>();

            public EntityControllerBase(EntityManager manager, ProfileController profileController, EntityData entityData)
            {
                Manager = manager;
                ProfileController = profileController;
                EntityData = entityData;
            }

            public abstract EntityAdapterBase CreateAdapter(BaseMonument monument);

            public virtual void PreUnload() {}

            public EntityAdapterBase SpawnAtMonument(BaseMonument monument)
            {
                var adapter = CreateAdapter(monument);
                Adapters.Add(adapter);
                adapter.Spawn();
                OnAdapterSpawned(adapter);
                return adapter;
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

            public void Destroy()
            {
                if (Adapters.Count > 0)
                    CoroutineManager.StartGlobalCoroutine(DestroyRoutine());

                Manager.OnControllerDestroyed(this);
            }

            public virtual void OnAdapterSpawned(EntityAdapterBase adapter) {}

            public virtual void OnAdapterDestroyed(EntityAdapterBase adapter)
            {
                Adapters.Remove(adapter);

                if (Adapters.Count == 0)
                    Manager.OnControllerDestroyed(this);
            }
        }

        #endregion

        #region Entity Adapter/Controller - Single

        private class SingleEntityAdapter : EntityAdapterBase
        {
            protected BaseEntity _entity;

            public override bool IsDestroyed => _entity == null || _entity.IsDestroyed;

            public SingleEntityAdapter(EntityControllerBase controller, EntityData entityData, BaseMonument monument) : base(controller, entityData, monument) {}

            public override void Spawn()
            {
                var position = Monument.TransformPoint(EntityData.Position);
                var rotation = Quaternion.Euler(0, Monument.Rotation.eulerAngles.y + EntityData.RotationAngle, 0);

                if (EntityData.OnTerrain)
                    position.y = TerrainMeta.HeightMap.GetHeight(position);

                _entity = CreateEntity(EntityData.PrefabName, position, rotation);
                OnEntitySpawn();
                _entity.Spawn();
                OnEntitySpawned();
            }

            public override void Kill()
            {
                if (IsDestroyed)
                    return;

                _entity.Kill();
            }

            public override void OnEntityDestroyed(BaseEntity entity)
            {
                _pluginInstance?.TrackStart();

                if (entity.net != null)
                    _registeredEntities.Remove(entity);

                Controller.OnAdapterDestroyed(this);

                _pluginInstance?.TrackEnd();
            }

            public void UpdateScale()
            {
                _pluginInstance.ScaleEntity(_entity, EntityData.Scale);
            }

            public void UpdateSkin()
            {
                if (_entity.skinID == EntityData.Skin)
                    return;

                _entity.skinID = EntityData.Skin;
                _entity.SendNetworkUpdate();
            }

            private bool ShouldBeImmortal()
            {
                var samSite = _entity as SamSite;
                if (samSite != null && samSite.staticRespawn)
                    return false;

                return true;
            }

            protected virtual void OnEntitySpawn()
            {
                if (EntityData.Skin != 0)
                    _entity.skinID = EntityData.Skin;

                var combatEntity = _entity as BaseCombatEntity;
                if (combatEntity != null)
                {
                    if (ShouldBeImmortal())
                    {
                        combatEntity.baseProtection = _pluginInstance._immortalProtection;
                    }

                    combatEntity.pickup.enabled = false;
                }

                var stabilityEntity = _entity as StabilityEntity;
                if (stabilityEntity != null)
                {
                    stabilityEntity.grounded = true;
                }

                var ioEntity = _entity as IOEntity;
                if (ioEntity != null)
                {
                    ioEntity.SetFlag(BaseEntity.Flags.On, true);
                    ioEntity.SetFlag(IOEntity.Flag_HasPower, true);
                }

                if (_entity is BigWheelGame)
                    _entity.transform.eulerAngles = _entity.transform.eulerAngles.WithX(90);
            }

            protected virtual void OnEntitySpawned()
            {
                // Disable saving after spawn to make sure children that are spawned late also have saving disabled.
                // For example, the Lift class spawns a sub entity.
                EnableSavingResursive(_entity, false);

                if (_entity is NPCVendingMachine && EntityData.Skin != 0)
                    UpdateSkin();

                var computerStation = _entity as ComputerStation;
                if (computerStation != null && computerStation.isStatic)
                {
                    computerStation.CancelInvoke(computerStation.GatherStaticCameras);
                    computerStation.Invoke(() =>
                    {
                        _pluginInstance.TrackStart();
                        GatherStaticCameras(computerStation);
                        _pluginInstance.TrackEnd();
                    }, 1);
                }

                if (EntityData.Scale != 1)
                    UpdateScale();
            }

            private void EnableSavingResursive(BaseEntity entity, bool enableSaving)
            {
                entity.EnableSaving(enableSaving);

                foreach (var child in entity.children)
                    EnableSavingResursive(child, enableSaving);
            }

            private List<CCTV_RC> GetNearbyStaticCameras()
            {
                var cargoShip = _entity.GetParentEntity() as CargoShip;
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
                Vis.Entities(_entity.transform.position, 100, entityList, Rust.Layers.Mask.Deployed, QueryTriggerInteraction.Ignore);
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
        }

        private class SingleEntityController : EntityControllerBase
        {
            public SingleEntityController(EntityManager manager, ProfileController profileController, EntityData data)
                : base(manager, profileController, data) {}

            public override EntityAdapterBase CreateAdapter(BaseMonument monument) =>
                new SingleEntityAdapter(this, EntityData, monument);

            public void UpdateSkin()
            {
                ProfileController.CoroutineManager.StartCoroutine(UpdateSkinRoutine());
            }

            public void UpdateScale()
            {
                ProfileController.CoroutineManager.StartCoroutine(UpdateScaleRoutine());
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

            public uint[] GetTextureIds() => (_entity as ISignage)?.GetTextureCRCs();

            public void SetTextureIds(uint[] textureIds)
            {
                var sign = _entity as ISignage;
                if (textureIds == null || textureIds.Equals(sign.GetTextureCRCs()))
                    return;

                sign.SetTextureCRCs(textureIds);
            }

            public void SkinSign()
            {
                if (EntityData.SignArtistImages == null)
                    return;

                _pluginInstance.SkinSign(_entity as ISignage, EntityData.SignArtistImages);
            }

            protected override void OnEntitySpawn()
            {
                base.OnEntitySpawn();

                (_entity as Signage)?.EnsureInitialized();

                var carvablePumpkin = _entity as CarvablePumpkin;
                if (carvablePumpkin != null)
                {
                    carvablePumpkin.EnsureInitialized();
                    carvablePumpkin.SetFlag(BaseEntity.Flags.On, true);
                }

                _entity.SetFlag(BaseEntity.Flags.Locked, true);
            }

            protected override void OnEntitySpawned()
            {
                base.OnEntitySpawned();

                // This must be done after spawning to allow the animation to work.
                var neonSign = _entity as NeonSign;
                if (neonSign != null)
                    neonSign.UpdateFromInput(neonSign.ConsumptionAmount(), 0);
            }
        }

        private class SignEntityController : SingleEntityController
        {
            public SignEntityController(EntityManager manager, ProfileController profileController, EntityData data)
                : base(manager, profileController, data) {}

            // Sign artist will only be called for the primary adapter.
            // Texture ids are copied to the others.
            protected SignEntityAdapter _primaryAdapter;

            public override EntityAdapterBase CreateAdapter(BaseMonument monument) =>
                new SignEntityAdapter(this, EntityData, monument);

            public override void OnAdapterSpawned(EntityAdapterBase adapter)
            {
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

            public override void OnAdapterDestroyed(EntityAdapterBase adapter)
            {
                base.OnAdapterDestroyed(adapter);

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

            public override void OnEntityDestroyed(BaseEntity entity)
            {
                base.OnEntityDestroyed(entity);

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

                if (_entity.IsFullySpawned())
                {
                    _entity.SendNetworkUpdate();

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

                var cctv = _entity as CCTV_RC;
                cctv.pitchAmount = cctvInfo.Pitch;
                cctv.yawAmount = cctvInfo.Yaw;

                cctv.pitchAmount = Mathf.Clamp(cctv.pitchAmount, cctv.pitchClamp.x, cctv.pitchClamp.y);
                cctv.yawAmount = Mathf.Clamp(cctv.yawAmount, cctv.yawClamp.x, cctv.yawClamp.y);

                cctv.pitch.transform.localRotation = Quaternion.Euler(cctv.pitchAmount, 0f, 0f);
                cctv.yaw.transform.localRotation = Quaternion.Euler(0f, cctv.yawAmount, 0f);

                if (_entity.IsFullySpawned())
                    _entity.SendNetworkUpdate();
            }

            private void SetIdentifier(string id) =>
                (_entity as CCTV_RC).rcIdentifier = id;

            private List<ComputerStation> GetNearbyStaticComputerStations()
            {
                var cargoShip = _entity.GetParentEntity() as CargoShip;
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
                Vis.Entities(_entity.transform.position, 100, entityList, Rust.Layers.Mask.Deployed, QueryTriggerInteraction.Ignore);
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

            public CCTVEntityController(EntityManager manager, ProfileController profileController, EntityData data)
                : base(manager, profileController, data) {}

            public override EntityAdapterBase CreateAdapter(BaseMonument monument) =>
                new CCTVEntityAdapter(this, EntityData, monument, _nextId++);

            // Ensure the RC identifiers are freed up as soon as possible to avoid conflicts when reloading.
            public override void PreUnload() => ResetIdentifier();

            public void UpdateIdentifier()
            {
                ProfileController.CoroutineManager.StartCoroutine(UpdateIdentifierRoutine());
            }

            public void ResetIdentifier()
            {
                foreach (var adapter in Adapters)
                    (adapter as CCTVEntityAdapter).ResetIdentifier();
            }

            public void UpdateDirection()
            {
                ProfileController.CoroutineManager.StartCoroutine(UpdateDirectionRoutine());
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

        #region Entity Manager

        private abstract class EntityControllerFactoryBase
        {
            protected string[] _dynamicHookNames;
            private int _controllerCount;

            public abstract bool AppliesToEntity(BaseEntity entity);
            public abstract EntityControllerBase CreateController(EntityManager manager, ProfileController controller, EntityData entityData);

            public void OnControllerCreated()
            {
                _controllerCount++;

                if (_controllerCount == 1)
                    SubscribeHooks();
            }

            public void OnControllerDestroyed()
            {
                _controllerCount--;

                if (_controllerCount == 0)
                    UnsubscribeHooks();
            }

            public void SubscribeHooks()
            {
                if (_dynamicHookNames == null)
                    return;

                foreach (var hookName in _dynamicHookNames)
                    _pluginInstance?.Subscribe(hookName);
            }

            public void UnsubscribeHooks()
            {
                if (_dynamicHookNames == null)
                    return;

                foreach (var hookName in _dynamicHookNames)
                    _pluginInstance?.Unsubscribe(hookName);
            }
        }

        private class SingleEntityControllerFactory : EntityControllerFactoryBase
        {
            public override bool AppliesToEntity(BaseEntity entity) => true;

            public override EntityControllerBase CreateController(EntityManager manager, ProfileController controller, EntityData entityData) =>
                new SingleEntityController(manager, controller, entityData);
        }

        private class SignEntityControllerFactory : SingleEntityControllerFactory
        {
            public SignEntityControllerFactory()
            {
                _dynamicHookNames = new string[]
                {
                    nameof(CanUpdateSign),
                    nameof(OnSignUpdated),
                    nameof(OnImagePost),
                };
            }

            public override bool AppliesToEntity(BaseEntity entity) => entity is ISignage;

            public override EntityControllerBase CreateController(EntityManager manager, ProfileController controller, EntityData entityData) =>
                new SignEntityController(manager, controller, entityData);
        }

        private class CCTVEntityControllerFactory : SingleEntityControllerFactory
        {
            public override bool AppliesToEntity(BaseEntity entity) => entity is CCTV_RC;

            public override EntityControllerBase CreateController(EntityManager manager, ProfileController controller, EntityData entityData) =>
                new CCTVEntityController(manager, controller, entityData);
        }

        private class EntityManager
        {
            private List<EntityControllerFactoryBase> _entityFactories = new List<EntityControllerFactoryBase>
            {
                // The first that matches will be used.
                new CCTVEntityControllerFactory(),
                new SignEntityControllerFactory(),
                new SingleEntityControllerFactory(),
            };

            private Dictionary<EntityData, EntityControllerBase> _controllersByEntityData = new Dictionary<EntityData, EntityControllerBase>();

            public void Init()
            {
                foreach (var entityInfo in _entityFactories)
                    entityInfo.UnsubscribeHooks();
            }

            public void OnControllerDestroyed(EntityControllerBase controller)
            {
                _controllersByEntityData.Remove(controller.EntityData);
                GetControllerFactory(controller.EntityData).OnControllerDestroyed();
            }

            public IEnumerator SpawnEntityAtMonumentsRoutine(ProfileController profileController, EntityData entityData, IEnumerable<BaseMonument> monumentList)
            {
                _pluginInstance.TrackStart();
                var controller = GetController(entityData);
                if (controller != null)
                {
                    // If the controller already exists, the entity was added while the plugin was still spawning entities.
                    _pluginInstance.TrackEnd();
                    yield break;
                }

                controller = EnsureController(entityData, profileController);
                _pluginInstance.TrackEnd();
                yield return controller.SpawnAtMonumentsRoutine(monumentList);
            }

            public IEnumerator SpawnEntitiesAtMonumentRoutine(ProfileController profileController, IEnumerable<EntityData> entityDataList, BaseMonument monument)
            {
                foreach (var entityData in entityDataList)
                {
                    // Check for null in case the cargo ship was destroyed.
                    if (!monument.IsValid)
                        yield break;

                    _pluginInstance.TrackStart();
                    EnsureController(entityData, profileController).SpawnAtMonument(monument);
                    _pluginInstance.TrackEnd();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            public EntityControllerBase GetController(EntityData entityData)
            {
                EntityControllerBase controller;
                return _controllersByEntityData.TryGetValue(entityData, out controller)
                    ? controller
                    : null;
            }

            private EntityControllerFactoryBase GetControllerFactory(EntityData entityData)
            {
                var prefab = GameManager.server.FindPrefab(entityData.PrefabName);
                if (prefab == null)
                    return null;

                var baseEntity = prefab.GetComponent<BaseEntity>();
                if (baseEntity == null)
                    return null;

                foreach (var controllerFactory in _entityFactories)
                {
                    if (controllerFactory.AppliesToEntity(baseEntity))
                        return controllerFactory;
                }

                return null;
            }

            private EntityControllerBase EnsureController(EntityData entityData, ProfileController profileController)
            {
                var controller = GetController(entityData);
                if (controller == null)
                {
                    var controllerFactory = GetControllerFactory(entityData);
                    controller = controllerFactory.CreateController(this, profileController, entityData);
                    controllerFactory.OnControllerCreated();
                    _controllersByEntityData[entityData] = controller;
                }
                return controller;
            }
        }

        #endregion

        #region Profile Data

        private class Profile
        {
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
                Interface.Oxide.DataFileSystem.ExistsDatafile(GetProfilePath(profileName));

            public static Profile Load(string profileName)
            {
                var profile = Interface.Oxide.DataFileSystem.ReadObject<Profile>(GetProfilePath(profileName)) ?? new Profile();
                profile.Name = GetActualFileName(profileName);

                // Fix issue caused by v0.7.0 for first time users.
                if (profile.MonumentMap == null)
                    profile.MonumentMap = new Dictionary<string, List<EntityData>>();

                return profile;
            }

            public static Profile LoadIfExists(string profileName) =>
                Exists(profileName) ? Load(profileName) : null;

            public static Profile LoadDefaultProfile() => Load(DefaultProfileName);

            public static Profile Create(string profileName)
            {
                var profile = new Profile { Name = profileName };
                profile.Save();
                return profile;
            }

            [JsonIgnore]
            public string Name;

            [JsonProperty("Monuments")]
            public Dictionary<string, List<EntityData>> MonumentMap = new Dictionary<string, List<EntityData>>();

            public void Save() =>
                Interface.Oxide.DataFileSystem.WriteObject(GetProfilePath(Name), this);

            public void CopyTo(string newName)
            {
                Name = newName;
                Save();
            }

            public bool IsEmpty()
            {
                if (MonumentMap == null || MonumentMap.IsEmpty())
                    return true;

                foreach (var entityDataList in MonumentMap.Values)
                {
                    if (!entityDataList.IsEmpty())
                        return false;
                }

                return true;
            }

            public Dictionary<string, Dictionary<string, int>> GetEntityAggregates()
            {
                var aggregateData = new Dictionary<string, Dictionary<string, int>>();

                foreach (var entry in MonumentMap)
                {
                    var entityDataList = entry.Value;
                    if (entityDataList.Count == 0)
                        continue;

                    var monumentAliasOrShortName = entry.Key;

                    Dictionary<string, int> monumentData;
                    if (!aggregateData.TryGetValue(monumentAliasOrShortName, out monumentData))
                    {
                        monumentData = new Dictionary<string, int>();
                        aggregateData[monumentAliasOrShortName] = monumentData;
                    }

                    foreach (var entityData in entityDataList)
                    {
                        int count;
                        if (!monumentData.TryGetValue(entityData.PrefabName, out count))
                            count = 0;

                        monumentData[entityData.PrefabName] = count + 1;
                    }
                }

                return aggregateData;
            }

            public void AddEntityData(string monumentAliasOrShortName, EntityData entityData)
            {
                List<EntityData> entityDataList;
                if (!MonumentMap.TryGetValue(monumentAliasOrShortName, out entityDataList))
                {
                    entityDataList = new List<EntityData>();
                    MonumentMap[monumentAliasOrShortName] = entityDataList;
                }

                entityDataList.Add(entityData);
                Save();
            }

            public bool RemoveEntityData(EntityData entityData, out string monumentAliasOrShortName)
            {
                foreach (var entry in MonumentMap)
                {
                    if (entry.Value.Remove(entityData))
                    {
                        monumentAliasOrShortName = entry.Key;
                        Save();
                        return true;
                    }
                }

                monumentAliasOrShortName = null;
                return false;
            }

            public bool RemoveEntityData(EntityData entityData)
            {
                string monumentShortName;
                return RemoveEntityData(entityData, out monumentShortName);
            }
        }

        #endregion

        #region Profile Controller

        private enum ProfileState { Loading, Loaded, Unloading, Unloaded }

        private class ProfileController
        {
            public Profile Profile { get; private set; }
            public ProfileState ProfileState { get; private set; } = ProfileState.Unloaded;
            public CoroutineManager CoroutineManager { get; private set; }
            public WaitUntil WaitUntilLoaded;
            public WaitUntil WaitUntilUnloaded;

            private EntityManager _entityManager;

            public bool IsEnabled =>
                _pluginData.IsProfileEnabled(Profile.Name);

            public ProfileController(EntityManager entityManager, Profile profile)
            {
                _entityManager = entityManager;
                Profile = profile;
                WaitUntilLoaded = new WaitUntil(() => ProfileState == ProfileState.Loaded);
                WaitUntilUnloaded = new WaitUntil(() => ProfileState == ProfileState.Unloaded);

                if (profile.IsEmpty() && IsEnabled)
                    ProfileState = ProfileState.Loaded;
            }

            public void Load(ReferenceTypeWrapper<int> entityCounter = null)
            {
                if (ProfileState == ProfileState.Loading || ProfileState == ProfileState.Loaded)
                    return;

                ProfileState = ProfileState.Loading;
                EnsureCoroutineManager().StartCoroutine(LoadRoutine(entityCounter));
            }

            public void PreUnload()
            {
                if (CoroutineManager != null)
                {
                    CoroutineManager.Destroy();
                    CoroutineManager = null;
                }

                foreach (var entityDataList in Profile.MonumentMap.Values)
                {
                    foreach (var entityData in entityDataList)
                    {
                        var controller = _entityManager.GetController(entityData);
                        if (controller == null)
                            continue;

                        controller.PreUnload();
                    }
                }
            }

            public void Unload()
            {
                if (ProfileState == ProfileState.Unloading || ProfileState == ProfileState.Unloaded)
                    return;

                ProfileState = ProfileState.Unloading;
                CoroutineManager.StartGlobalCoroutine(UnloadRoutine());
            }

            public void Reload()
            {
                EnsureCoroutineManager();
                CoroutineManager.StopAll();
                CoroutineManager.StartCoroutine(ReloadRoutine());
            }

            public IEnumerator PartialLoadForLateMonument(List<EntityData> entityDataList, BaseMonument monument)
            {
                if (ProfileState == ProfileState.Loading)
                    yield break;

                ProfileState = ProfileState.Loading;
                EnsureCoroutineManager().StartCoroutine(PartialLoadForLateMonumentRoutine(entityDataList, monument));
                yield return WaitUntilLoaded;
            }

            public void SpawnNewEntity(EntityData entityData, IEnumerable<BaseMonument> monument)
            {
                if (ProfileState == ProfileState.Unloading || ProfileState == ProfileState.Unloaded)
                    return;

                ProfileState = ProfileState.Loading;
                EnsureCoroutineManager().StartCoroutine(PartialLoadForLateEntityRoutine(entityData, monument));
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

                Load();
            }

            public void Disable()
            {
                if (!IsEnabled)
                    return;

                PreUnload();
                Unload();
            }

            private CoroutineManager EnsureCoroutineManager()
            {
                if (CoroutineManager == null)
                {
                    CoroutineManager = new CoroutineManager();
                    CoroutineManager.OnServerInitialized();
                }
                return CoroutineManager;
            }

            private IEnumerator LoadRoutine(ReferenceTypeWrapper<int> entityCounter)
            {
                foreach (var entry in Profile.MonumentMap.ToArray())
                {
                    if (entry.Value.Count == 0)
                        continue;

                    var matchingMonuments = _pluginInstance.GetMonumentsByAliasOrShortName(entry.Key);
                    if (matchingMonuments == null)
                        continue;

                    if (entityCounter != null)
                        entityCounter.Value += matchingMonuments.Count * entry.Value.Count;

                    foreach (var entityData in entry.Value.ToArray())
                        yield return _entityManager.SpawnEntityAtMonumentsRoutine(this, entityData, matchingMonuments);
                }

                ProfileState = ProfileState.Loaded;
            }

            private IEnumerator UnloadRoutine()
            {
                foreach (var entityDataList in Profile.MonumentMap.Values.ToArray())
                {
                    foreach (var entityData in entityDataList.ToArray())
                    {
                        _pluginInstance?.TrackStart();
                        var controller = _entityManager.GetController(entityData);
                        _pluginInstance?.TrackEnd();

                        if (controller == null)
                            continue;

                        yield return controller.DestroyRoutine();
                    }
                }

                ProfileState = ProfileState.Unloaded;
            }

            private IEnumerator ReloadRoutine()
            {
                Unload();
                yield return WaitUntilUnloaded;

                Profile = Profile.Load(Profile.Name);

                Load();
                yield return WaitUntilLoaded;
            }

            private IEnumerator PartialLoadForLateMonumentRoutine(List<EntityData> entityDataList, BaseMonument monument)
            {
                yield return _entityManager.SpawnEntitiesAtMonumentRoutine(this, entityDataList, monument);
                ProfileState = ProfileState.Loaded;
            }

            private IEnumerator PartialLoadForLateEntityRoutine(EntityData entityData, IEnumerable<BaseMonument> monument)
            {
                yield return _entityManager.SpawnEntityAtMonumentsRoutine(this, entityData, monument);
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
            public static ProfileInfo[] GetList()
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
                    };
                }

                return profileInfoList;
            }

            public string Name;
            public bool Enabled;
        }

        private class ProfileManager
        {
            private EntityManager _entityManager;
            private List<ProfileController> _profileControllers = new List<ProfileController>();

            public ProfileManager(EntityManager entityManager)
            {
                _entityManager = entityManager;
            }

            public IEnumerator LoadAllProfilesRoutine()
            {
                foreach (var profileName in _pluginData.EnabledProfiles.ToArray())
                {
                    var controller = GetProfileController(profileName);
                    if (controller == null)
                        continue;

                    var entityCounter = new ReferenceTypeWrapper<int>();

                    controller.Load(entityCounter);
                    yield return controller.WaitUntilLoaded;

                    if (entityCounter.Value > 0)
                        _pluginInstance.Puts($"Loaded profile {controller.Profile.Name} with {entityCounter.Value} entities.");
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

                    List<EntityData> entityDataList;
                    if (!controller.Profile.MonumentMap.TryGetValue(monument.AliasOrShortName, out entityDataList))
                        continue;

                    yield return controller.PartialLoadForLateMonument(entityDataList, monument);
                }
            }

            public ProfileController GetProfileController(string profileName)
            {
                foreach (var cachedController in _profileControllers)
                {
                    if (cachedController.Profile.Name.ToLower() == profileName.ToLower())
                        return cachedController;
                }

                var profile = Profile.LoadIfExists(profileName);
                if (profile != null)
                {
                    var controller = new ProfileController(_entityManager, profile);
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
                foreach (var cachedController in _profileControllers)
                {
                    if (cachedController.Profile.Name.ToLower() == profileName)
                        return true;
                }

                return Profile.Exists(profileName);
            }

            public ProfileController CreateProfile(string profileName)
            {
                var profile = Profile.Create(profileName);
                _pluginData.SetProfileEnabled(profileName);
                _pluginData.Save();

                var controller = new ProfileController(_entityManager, profile);
                _profileControllers.Add(controller);
                return controller;
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

        private interface IDataMigration
        {
            bool Migrate(StoredData data);
        }

        private class DataMigrationV1 : IDataMigration
        {
            private static readonly Dictionary<string, string> MigrateMonumentNames = new Dictionary<string, string>
            {
                ["TRAIN_STATION"] = "TrainStation",
                ["BARRICADE_TUNNEL"] = "BarricadeTunnel",
                ["LOOT_TUNNEL"] = "LootTunnel",
                ["3_WAY_INTERSECTION"] = "Intersection",
                ["4_WAY_INTERSECTION"] = "LargeIntersection",
            };

            public bool Migrate(StoredData data)
            {
                if (data.DataFileVersion != 0)
                    return false;

                data.DataFileVersion++;

                var dataMigrated = false;

                if (data.MonumentMap != null)
                {
                    foreach (var monumentEntry in data.MonumentMap.ToArray())
                    {
                        var alias = monumentEntry.Key;
                        var entityList = monumentEntry.Value;

                        string newAlias;
                        if (MigrateMonumentNames.TryGetValue(alias, out newAlias))
                        {
                            data.MonumentMap[newAlias] = entityList;
                            data.MonumentMap.Remove(alias);
                            alias = newAlias;
                        }

                        foreach (var entityData in entityList)
                        {
                            if (alias == "LootTunnel" || alias == "BarricadeTunnel")
                            {
                                // Migrate from the original rotations to the rotations used by MonumentFinder.
                                entityData.RotationAngle = (entityData.RotationAngle + 180) % 360;
                                entityData.Position = Quaternion.Euler(0, 180, 0) * entityData.Position;
                                dataMigrated = true;
                            }

                            // Migrate from the backwards rotations to the correct ones.
                            var newAngle = (720 - entityData.RotationAngle) % 360;
                            entityData.RotationAngle = newAngle;
                            dataMigrated = true;
                        }
                    }
                }

                return dataMigrated;
            }
        }

        private class DataMigrationV2 : IDataMigration
        {
            public bool Migrate(StoredData data)
            {
                if (data.DataFileVersion != 1)
                    return false;

                data.DataFileVersion++;

                var profile = new Profile
                {
                    Name = DefaultProfileName,
                };

                if (data.MonumentMap != null)
                    profile.MonumentMap = data.MonumentMap;

                profile.Save();

                data.MonumentMap = null;
                data.EnabledProfiles.Add(DefaultProfileName);

                return true;
            }
        }

        private class StoredData
        {
            public static StoredData Load()
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(_pluginInstance.Name) ?? new StoredData();

                var dataMigrated = false;
                var migrationList = new List<IDataMigration>
                {
                    new DataMigrationV1(),
                    new DataMigrationV2(),
                };

                foreach (var migration in migrationList)
                {
                    if (migration.Migrate(data))
                        dataMigrated = true;
                }

                if (dataMigrated)
                {
                    _pluginInstance.LogWarning("Data file has been automatically migrated.");
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
            public Dictionary<string, List<EntityData>> MonumentMap;

            public void Save() =>
                Interface.Oxide.DataFileSystem.WriteObject(_pluginInstance.Name, this);

            public bool IsProfileEnabled(string profileName) => EnabledProfiles.Contains(profileName);

            public void SetProfileEnabled(string profileName)
            {
                EnabledProfiles.Add(profileName);
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
                return SelectedProfiles.TryGetValue(userId, out profileName)
                    ? profileName
                    : null;
            }

            public void SetProfileSelected(string userId, string profileName)
            {
                SelectedProfiles[userId] = profileName;
                SetProfileEnabled(profileName);
                Save();
            }
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

        private class EntityData
        {
            [JsonProperty("PrefabName")]
            public string PrefabName;

            [JsonProperty("Position")]
            public Vector3 Position;

            [JsonProperty("RotationAngle", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float RotationAngle;

            [JsonProperty("OnTerrain", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool OnTerrain = false;

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

        #region Configuration

        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("Debug", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool Debug = false;
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

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player, messageName), args));

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.UserIDString, messageName), args));

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

            public const string SkinGet = "Skin.Get";
            public const string SkinSetSyntax = "Skin.Set.Syntax";
            public const string SkinSetSuccess = "Skin.Set.Success2";
            public const string SkinErrorRedirect = "Skin.Error.Redirect";

            public const string CCTVSetIdSyntax = "CCTV.SetId.Error.Syntax";
            public const string CCTVSetIdSuccess = "CCTV.SetId.Success2";
            public const string CCTVSetDirectionSuccess = "CCTV.SetDirection.Success2";

            public const string ProfileListEmpty = "Profile.List.Empty";
            public const string ProfileListHeader = "Profile.List.Header";
            public const string ProfileListItemEnabled = "Profile.List.Item.Enabled";
            public const string ProfileListItemDisabled = "Profile.List.Item.Disabled";
            public const string ProfileListItemSelected = "Profile.List.Item.Selected";

            public const string ProfileDescribeSyntax = "Profile.Describe.Syntax";
            public const string ProfileNotFound = "Profile.Error.NotFound";
            public const string ProfileEmpty = "Profile.Empty";
            public const string ProfileDescribeHeader = "Profile.Describe.Header";
            public const string ProfileDescribeItem = "Profile.Describe.Item";
            public const string ProfileSelectSyntax = "Profile.Select.Syntax";
            public const string ProfileSelectSuccess = "Profile.Select.Success";

            public const string ProfileEnableSyntax = "Profile.Enable.Syntax";
            public const string ProfileAlreadyEnabled = "Profile.AlreadyEnabled";
            public const string ProfileEnableSuccess = "Profile.Enable.Success";
            public const string ProfileDisableSyntax = "Profile.Disable.Syntax";
            public const string ProfileAlreadyDisabled = "Profile.AlreadyDisabled";
            public const string ProfileDisableSuccess = "Profile.Disable.Success";
            public const string ProfileReloadSyntax = "Profile.Reload.Syntax";
            public const string ProfileReloadSuccess = "Profile.Reload.Success";

            public const string ProfileCreateSyntax = "Profile.Create.Syntax";
            public const string ProfileAlreadyExists = "Profile.Error.AlreadyExists";
            public const string ProfileCreateSuccess = "Profile.Create.Success";
            public const string ProfileRenameSyntax = "Profile.Rename.Syntax";
            public const string ProfileRenameSuccess = "Profile.Rename.Success";
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
            public const string ProfileHelpMoveTo = "Profile.Help.MoveTo";
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

                [Lang.SkinGet] = "Skin ID: <color=#fd4>{0}</color>. Run <color=#fd4>{1} <skin id></color> to change it.",
                [Lang.SkinSetSyntax] = "Syntax: <color=#fd4>{0} <skin id></color>",
                [Lang.SkinSetSuccess] = "Updated skin ID to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.",
                [Lang.SkinErrorRedirect] = "Error: Skin <color=#fd4>{0}</color> is a redirect skin and cannot be set directly. Instead, spawn the entity as <color=#fd4>{1}</color>.",

                [Lang.CCTVSetIdSyntax] = "Syntax: <color=#fd4>{0} <id></color>",
                [Lang.CCTVSetIdSuccess] = "Updated CCTV id to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.",
                [Lang.CCTVSetDirectionSuccess] = "Updated CCTV direction at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.",

                [Lang.ProfileListEmpty] = "You have no profiles. Create one with <color=#fd4>maprofile create <name></maprofile>",
                [Lang.ProfileListHeader] = "<size=18>Monument Addons Profiles</size>",
                [Lang.ProfileListItemEnabled] = "<color=#fd4>{0}</color> - <color=#6e6>ENABLED</color>",
                [Lang.ProfileListItemDisabled] = "<color=#fd4>{0}</color> - <color=#f44>DISABLED</color>",
                [Lang.ProfileListItemSelected] = "<color=#fd4>{0}</color> - <color=#6cf>SELECTED</color>",

                [Lang.ProfileDescribeSyntax] = "Syntax: <color=#fd4>maprofile describe <name></color>",
                [Lang.ProfileNotFound] = "Error: Profile <color=#fd4>{0}</color> not found.",
                [Lang.ProfileEmpty] = "Profile <color=#fd4>{0}</color> is empty.",
                [Lang.ProfileDescribeHeader] = "Describing profile <color=#fd4>{0}</color>.",
                [Lang.ProfileDescribeItem] = "<color=#fd4>{0}</color> x{1} @ {2}",
                [Lang.ProfileSelectSyntax] = "Syntax: <color=#fd4>maprofile select <name></color>",
                [Lang.ProfileSelectSuccess] = "Successfully <color=#6cf>SELECTED</color> and <color=#6e6>ENABLED</color> profile <color=#fd4>{0}</color>.",

                [Lang.ProfileEnableSyntax] = "Syntax: <color=#fd4>maprofile enable <name></color>",
                [Lang.ProfileAlreadyEnabled] = "Profile <color=#fd4>{0}</color> is already <color=#6e6>ENABLED</color>.",
                [Lang.ProfileEnableSuccess] = "Profile <color=#fd4>{0}</color> is now: <color=#6e6>ENABLED</color>.",
                [Lang.ProfileDisableSyntax] = "Syntax: <color=#fd4>maprofile disable <name></color>",
                [Lang.ProfileAlreadyDisabled] = "Profile <color=#fd4>{0}</color> is already <color=#f44>DISABLED</color>.",
                [Lang.ProfileDisableSuccess] = "Profile <color=#fd4>{0}</color> is now: <color=#f44>DISABLED</color>.",
                [Lang.ProfileReloadSyntax] = "Syntax: <color=#fd4>maprofile reload <name></color>",
                [Lang.ProfileReloadSuccess] = "Reloaded profile <color=#fd4>{0}</color>.",

                [Lang.ProfileCreateSyntax] = "Syntax: <color=#fd4>maprofile create <name></color>",
                [Lang.ProfileAlreadyExists] = "Error: Profile <color=#fd4>{0}</color> already exists.",
                [Lang.ProfileCreateSuccess] = "Successfully created and <color=#6cf>SELECTED</color> profile <color=#fd4>{0}</color>.",
                [Lang.ProfileRenameSyntax] = "Syntax: <color=#fd4>maprofile rename <old name> <new name></color>",
                [Lang.ProfileRenameSuccess] = "Successfully renamed profile <color=#fd4>{0}</color> to <color=#fd4>{1}</color>. You must manually delete the old <color=#fd4>{0}</color> data file.",
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
                [Lang.ProfileHelpMoveTo] = "<color=#fd4>maprofile disable <name></color> - Move an entity to a profile",
            }, this, "en");
        }

        #endregion
    }
}
