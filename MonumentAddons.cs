using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Monument Addons", "WhiteThunder", "0.5.0")]
    [Description("Allows privileged players to add permanent entities to monuments.")]
    internal class MonumentAddons : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin EntityScaleManager, MonumentFinder, SignArtist;

        private static MonumentAddons _pluginInstance;
        private static Configuration _pluginConfig;

        private const float MaxRaycastDistance = 50;
        private const float TerrainProximityTolerance = 0.001f;

        private const string PermissionAdmin = "monumentaddons.admin";

        private const string CargoShipShortName = "cargoshiptest";

        private static readonly int HitLayers = Rust.Layers.Mask.Construction
            + Rust.Layers.Mask.Default
            + Rust.Layers.Mask.Deployed
            + Rust.Layers.Mask.Terrain
            + Rust.Layers.Mask.World;

        private readonly CentralEntityManager _centralEntityManager = new CentralEntityManager();
        private readonly CoroutineManager _coroutineManager = new CoroutineManager();

        private ProtectionProperties _immortalProtection;
        private StoredData _pluginData;

        private Coroutine _startupCoroutine;
        private bool _serverInitialized = false;

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            _pluginData = StoredData.Load();

            permission.RegisterPermission(PermissionAdmin, this);

            Unsubscribe(nameof(OnEntitySpawned));

            _centralEntityManager.Init();
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
            _coroutineManager.Unload();
            _coroutineManager.FireAndForgetCoroutine(_centralEntityManager.UnloadRoutine());

            UnityEngine.Object.Destroy(_immortalProtection);

            EntityAdapterBase.ClearEntityCache();

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
            List<EntityData> entityDataList;
            if (!_pluginData.MonumentMap.TryGetValue(CargoShipShortName, out entityDataList))
                return;

            _coroutineManager.StartCoroutine(
                _centralEntityManager.SpawnEntitiesAtMonumentRoutine(entityDataList, new CargoShipMonument(cargoShip))
            );
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
        private void OnImagePost(BasePlayer player, string url, bool raw, ISignage signage)
        {
            if (!EntityAdapterBase.IsMonumentEntity(signage as BaseEntity))
                return;

            var component = MonumentEntityComponent.GetForEntity(signage.NetworkID);
            if (component == null)
                return;

            var controller = component.Adapter.Controller as SignEntityController;
            if (controller == null)
                return;

            controller.EntityData.SignArtistImages = new SignArtistImage[]
            {
                new SignArtistImage { Url = url, Raw = raw },
            };
            _pluginData.Save();
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
            _pluginData.Save();
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
            if (signArtistImages.Length == 0)
                return;

            if (signage is Signage)
                SignArtist?.Call("API_SkinSign", (BasePlayer)null, signage as Signage, signArtistImages[0].Url, signArtistImages[0].Raw);
            else if (signage is PhotoFrame)
                SignArtist?.Call("API_SkinPhotoFrame", (BasePlayer)null, signage as PhotoFrame, signArtistImages[0].Url, signArtistImages[0].Raw);
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
            _coroutineManager.StartCoroutine(
                _centralEntityManager.SpawnEntityAtMonumentsRoutine(entityData, matchingMonuments)
            );

            _pluginData.AddEntityData(entityData, closestMonument.AliasOrShortName);
            ReplyToPlayer(player, Lang.SpawnSuccess, matchingMonuments.Count, closestMonument.AliasOrShortName);
        }

        [Command("makill")]
        private void CommandKill(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            BaseEntity entity;
            if (!VerifyLookEntity(player, out entity, Lang.ErrorSuitableEntityNotFound))
                return;

            MonumentEntityComponent component;
            if (!VerifyMonumentEntity(player, entity, out component))
                return;

            var controller = component.Adapter.Controller;
            var numEntities = controller.Adapters.Count;
            controller.Destroy();

            _pluginData.RemoveEntityData(controller.EntityData);
            ReplyToPlayer(player, Lang.KillSuccess, numEntities);
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
            if (!VerifyLookEntity(player, out cctv, Lang.ErrorSuitableEntityNotFound))
                return;

            MonumentEntityComponent component;
            if (!VerifyMonumentEntity(player, cctv, out component))
                return;

            CCTVEntityController controller;
            if (!VerifyControllerType(player, component, out controller))
                return;

            if (controller.EntityData.CCTV == null)
                controller.EntityData.CCTV = new CCTVInfo();

            controller.EntityData.CCTV.RCIdentifier = args[0];
            controller.UpdateIdentifier();
            _pluginData.Save();

            ReplyToPlayer(player, Lang.CCTVSetIdSuccess, args[0], controller.Adapters.Count);
        }

        [Command("masetdir")]
        private void CommandSetDirection(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !VerifyHasPermission(player))
                return;

            CCTV_RC cctv;
            if (!VerifyLookEntity(player, out cctv, Lang.ErrorSuitableEntityNotFound))
                return;

            MonumentEntityComponent component;
            if (!VerifyMonumentEntity(player, cctv, out component))
                return;

            CCTVEntityController controller;
            if (!VerifyControllerType(player, component, out controller))
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
            _pluginData.Save();

            ReplyToPlayer(player, Lang.CCTVSetDirectionSuccess, controller.Adapters.Count);
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

        private bool VerifyMonumentEntity(IPlayer player, BaseEntity entity, out MonumentEntityComponent component)
        {
            component = MonumentEntityComponent.GetForEntity(entity);
            if (component != null)
                return true;

            ReplyToPlayer(player, Lang.ErrorEntityNotEligible);
            return false;
        }

        private bool VerifyLookEntity<T>(IPlayer player, out T entity, string errorMessageName) where T : BaseEntity
        {
            var basePlayer = player.Object as BasePlayer;
            entity = GetLookEntity(basePlayer) as T;
            if (entity != null)
                return true;

            ReplyToPlayer(player, errorMessageName);
            return false;
        }

        private bool VerifyControllerType<T>(IPlayer player, MonumentEntityComponent component, out T controller) where T : EntityControllerBase
        {
            controller = component.Adapter.Controller as T;
            if (controller.GetType() == typeof(T))
                return true;

            ReplyToPlayer(player, Lang.ErrorUnexpected);
            return false;
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

        private IEnumerator SpawnAllEntitiesRoutine()
        {
            var spawnedEntities = 0;

            foreach (var entry in _pluginData.MonumentMap.ToArray())
            {
                var matchingMonuments = GetMonumentsByAliasOrShortName(entry.Key);
                if (matchingMonuments == null)
                    continue;

                spawnedEntities += matchingMonuments.Count;

                foreach (var entityData in entry.Value.ToArray())
                {
                    yield return _centralEntityManager.SpawnEntityAtMonumentsRoutine(entityData, matchingMonuments);
                }
            }

            if (spawnedEntities > 0)
                Puts($"Spawned {spawnedEntities} entities at monuments.");

            // We don't want to be subscribed to OnEntitySpawned(CargoShip) until the coroutine is done.
            // Otherwise, a cargo ship could spawn while the coroutine is running and could get double entities.
            Subscribe(nameof(OnEntitySpawned));
        }

        private void StartupRoutine()
        {
            // Don't spawn entities if that's already been done.
            if (_startupCoroutine != null)
                return;

            _startupCoroutine = _coroutineManager.StartCoroutine(SpawnAllEntitiesRoutine());
        }

        #endregion

        #region Coroutine Manager

        private class EmptyMonoBehavior : MonoBehaviour {}

        private class CoroutineManager
        {
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

            public void FireAndForgetCoroutine(IEnumerator enumerator)
            {
                ServerMgr.Instance?.StartCoroutine(enumerator);
            }

            public void Unload()
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
                Adapter.OnEntityKilled(_entity);
            }
        }

        #endregion

        #region Entity Adapter/Controller/Manager - Base

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
            public abstract void OnEntityKilled(BaseEntity entity);

            protected BaseEntity CreateEntity(string prefabName, Vector3 position, Quaternion rotation)
            {
                var entity = GameManager.server.CreateEntity(EntityData.PrefabName, position, rotation);
                if (entity == null)
                    return null;

                // In case the plugin doesn't clean it up on server shutdown, make sure it doesn't come back so it's not duplicated. x
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
            public EntityManagerBase Manager { get; private set; }
            public EntityData EntityData { get; private set; }
            public List<EntityAdapterBase> Adapters { get; private set; } = new List<EntityAdapterBase>();

            public EntityControllerBase(EntityManagerBase manager, EntityData entityData)
            {
                Manager = manager;
                EntityData = entityData;
            }

            public abstract EntityAdapterBase CreateAdapter(BaseMonument monument);

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
                    // Null check the plugin instance in case this is running after plugin unload.
                    _pluginInstance?.TrackStart();
                    adapter.Kill();
                    _pluginInstance?.TrackEnd();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            public void Destroy()
            {
                if (Adapters.Count > 0)
                    _pluginInstance._coroutineManager.FireAndForgetCoroutine(DestroyRoutine());

                Manager.OnControllerKilled(this);
            }

            public virtual void OnAdapterSpawned(EntityAdapterBase adapter) {}

            public virtual void OnAdapterKilled(EntityAdapterBase adapter)
            {
                Adapters.Remove(adapter);

                if (Adapters.Count == 0)
                    Manager.OnControllerKilled(this);
            }
        }

        private abstract class EntityManagerBase
        {
            protected Dictionary<EntityData, EntityControllerBase> _controllersByEntityData = new Dictionary<EntityData, EntityControllerBase>();

            protected string[] _dynamicHookNames;

            public abstract bool AppliesToEntity(BaseEntity entity);
            public abstract EntityControllerBase CreateController(EntityData entityData);

            public virtual void Init()
            {
                UnsubscribeHooks();
            }

            public virtual void PreUnload() {}

            public IEnumerator UnloadRoutine()
            {
                foreach (var controller in _controllersByEntityData.Values.ToArray())
                    yield return controller.DestroyRoutine();
            }

            public IEnumerator SpawnAtMonumentsRoutine(EntityData entityData, IEnumerable<BaseMonument> monumentList)
            {
                _pluginInstance.TrackStart();
                var controller = GetController(entityData);
                if (controller != null)
                {
                    // If the controller already exists, the entity was added while the plugin was still spawning entities.
                    _pluginInstance.TrackEnd();
                    yield break;
                }

                controller = EnsureController(entityData);
                _pluginInstance.TrackEnd();
                yield return controller.SpawnAtMonumentsRoutine(monumentList);
            }

            public void SpawnAtMonument(EntityData entityData, BaseMonument monument)
            {
                EnsureController(entityData).SpawnAtMonument(monument);
            }

            public virtual void OnControllerCreated(EntityControllerBase controller)
            {
                _controllersByEntityData[controller.EntityData] = controller;

                if (_controllersByEntityData.Count == 1)
                    SubscribeHooks();
            }

            public virtual void OnControllerKilled(EntityControllerBase controller)
            {
                _controllersByEntityData.Remove(controller.EntityData);

                if (_controllersByEntityData.Count == 0)
                    UnsubscribeHooks();
            }

            private EntityControllerBase EnsureController(EntityData entityData)
            {
                var controller = GetController(entityData);
                if (controller == null)
                {
                    controller = CreateController(entityData);
                    OnControllerCreated(controller);
                }
                return controller;
            }

            private EntityControllerBase GetController(EntityData entityData)
            {
                EntityControllerBase controller;
                return _controllersByEntityData.TryGetValue(entityData, out controller)
                    ? controller
                    : null;
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

        #endregion

        #region Entity Adapter/Controller/Manager - Single

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
                if (_entity == null || _entity.IsDestroyed)
                    return;

                _entity.Kill();
            }

            public override void OnEntityKilled(BaseEntity entity)
            {
                _pluginInstance?.TrackStart();

                if (entity.net != null)
                    _registeredEntities.Remove(entity);

                Controller.OnAdapterKilled(this);

                _pluginInstance?.TrackEnd();
            }

            public void UpdateScale()
            {
                _pluginInstance.ScaleEntity(_entity, EntityData.Scale);
            }

            protected virtual void OnEntitySpawn()
            {
                var combatEntity = _entity as BaseCombatEntity;
                if (combatEntity != null)
                {
                    combatEntity.baseProtection = _pluginInstance._immortalProtection;
                    combatEntity.pickup.enabled = false;
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
                var computerStation = _entity as ComputerStation;
                if (computerStation != null && computerStation.isStatic)
                {
                    computerStation.CancelInvoke(computerStation.GatherStaticCameras);
                    computerStation.Invoke(computerStation.GatherStaticCameras, 1);
                }

                if (EntityData.Scale != 1)
                    UpdateScale();
            }
        }

        private class SingleEntityController : EntityControllerBase
        {
            public SingleEntityController(EntityManagerBase manager, EntityData data) : base(manager, data) {}

            public override EntityAdapterBase CreateAdapter(BaseMonument monument) =>
                new SingleEntityAdapter(this, EntityData, monument);

            public void UpdateScale()
            {
                _pluginInstance._coroutineManager.StartCoroutine(UpdateScaleRoutine());
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

        private class SingleEntityManager : EntityManagerBase
        {
            public override bool AppliesToEntity(BaseEntity entity) => true;

            public override EntityControllerBase CreateController(EntityData entityData) =>
                new SingleEntityController(this, entityData);
        }

        #endregion

        #region Entity Adapter/Controller/Manager - Signs

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
            public SignEntityController(EntityManagerBase manager, EntityData data) : base(manager, data) {}

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

            public override void OnAdapterKilled(EntityAdapterBase adapter)
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

        private class SignEntityManager : EntityManagerBase
        {
            public SignEntityManager()
            {
                _dynamicHookNames = new string[]
                {
                    nameof(CanUpdateSign),
                    nameof(OnSignUpdated),
                    nameof(OnImagePost),
                };
            }

            public override bool AppliesToEntity(BaseEntity entity) => entity is ISignage;

            public override EntityControllerBase CreateController(EntityData entityData) =>
                new SignEntityController(this, entityData);
        }

        #endregion

        #region Entity Adapter/Controller/Manager - CCTV

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

            public CCTVEntityController(EntityManagerBase manager, EntityData data) : base(manager, data) {}

            public override EntityAdapterBase CreateAdapter(BaseMonument monument) =>
                new CCTVEntityAdapter(this, EntityData, monument, _nextId++);

            public void UpdateIdentifier()
            {
                _pluginInstance._coroutineManager.StartCoroutine(UpdateIdentifierRoutine());
            }

            public void ResetIdentifier()
            {
                foreach (var adapter in Adapters)
                    (adapter as CCTVEntityAdapter).ResetIdentifier();
            }

            public void UpdateDirection()
            {
                _pluginInstance._coroutineManager.StartCoroutine(UpdateDirectionRoutine());
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

        private class CCTVEntityManager : EntityManagerBase
        {
            public override bool AppliesToEntity(BaseEntity entity) => entity is CCTV_RC;

            public override EntityControllerBase CreateController(EntityData entityData) =>
                new CCTVEntityController(this, entityData);

            public override void PreUnload()
            {
                // Make sure the ids are cleared immediately, so if reloading, newly spawned entities won't have id conflicts.
                foreach (var controller in _controllersByEntityData.Values)
                    (controller as CCTVEntityController).ResetIdentifier();
            }
        }

        #endregion

        #region Central Entity Manager

        private class CentralEntityManager
        {
            private List<EntityManagerBase> _managers = new List<EntityManagerBase>
            {
                // The first manager that matches will be used.
                new CCTVEntityManager(),
                new SignEntityManager(),
                new SingleEntityManager(),
            };

            public void Init()
            {
                foreach (var manager in _managers)
                    manager.Init();
            }

            public IEnumerator SpawnEntityAtMonumentsRoutine(EntityData entityData, IEnumerable<BaseMonument> monumentList)
            {
                _pluginInstance.TrackStart();
                var manager = DetermineManager(entityData);
                _pluginInstance.TrackEnd();
                yield return manager.SpawnAtMonumentsRoutine(entityData, monumentList);
            }

            public IEnumerator SpawnEntitiesAtMonumentRoutine(IEnumerable<EntityData> entityDataList, BaseMonument monument)
            {
                foreach (var entityData in entityDataList)
                {
                    // Check for null in case the cargo ship was destroyed.
                    if (!monument.IsValid)
                        yield break;

                    _pluginInstance.TrackStart();
                    DetermineManager(entityData).SpawnAtMonument(entityData, monument);
                    _pluginInstance.TrackEnd();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            public IEnumerator UnloadRoutine()
            {
                foreach (var manager in _managers)
                    manager.PreUnload();

                foreach (var manager in _managers)
                    yield return manager.UnloadRoutine();
            }

            private EntityManagerBase DetermineManager(EntityData entityData)
            {
                var prefab = GameManager.server.FindPrefab(entityData.PrefabName);
                if (prefab == null)
                    return null;

                var baseEntity = prefab.GetComponent<BaseEntity>();
                if (baseEntity == null)
                    return null;

                foreach (var manager in _managers)
                {
                    if (manager.AppliesToEntity(baseEntity))
                        return manager;
                }

                return null;
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

                return dataMigrated;
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

            [JsonProperty("Monuments")]
            public Dictionary<string, List<EntityData>> MonumentMap = new Dictionary<string, List<EntityData>>();

            public void Save() =>
                Interface.Oxide.DataFileSystem.WriteObject(_pluginInstance.Name, this);

            public void AddEntityData(EntityData entityData, string monumentShortName)
            {
                List<EntityData> entityDataList;
                if (!MonumentMap.TryGetValue(monumentShortName, out entityDataList))
                {
                    entityDataList = new List<EntityData>();
                    MonumentMap[monumentShortName] = entityDataList;
                }

                entityDataList.Add(entityData);
                Save();
            }

            public bool RemoveEntityData(EntityData entityData)
            {
                foreach (var entityDataList in MonumentMap.Values)
                {
                    if (entityDataList.Remove(entityData))
                    {
                        Save();
                        return true;
                    }
                }

                return false;
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
            public const string ErrorUnexpected = "Error.Unexpected";
            public const string ErrorMonumentFinderNotLoaded = "Error.MonumentFinderNotLoaded";
            public const string ErrorNoMonuments = "Error.NoMonuments";
            public const string ErrorNotAtMonument = "Error.NotAtMonument";
            public const string ErrorSuitableEntityNotFound = "Error.SuitableEntityNotFound";
            public const string ErrorEntityNotEligible = "Error.EntityNotEligible";

            public const string SpawnErrorSyntax = "Spawn.Error.Syntax";
            public const string SpawnErrorEntityNotFound = "Spawn.Error.EntityNotFound";
            public const string SpawnErrorMultipleMatches = "Spawn.Error.MultipleMatches";
            public const string SpawnErrorNoTarget = "Spawn.Error.NoTarget";
            public const string SpawnSuccess = "Spawn.Success";
            public const string KillSuccess = "Kill.Success";

            public const string CCTVSetIdSyntax = "CCTV.SetId.Error.Syntax";
            public const string CCTVSetIdSuccess = "CCTV.SetId.Success";
            public const string CCTVSetDirectionSuccess = "CCTV.SetDirection.Success";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.ErrorNoPermission] = "You don't have permission to do that.",
                [Lang.ErrorUnexpected] = "An unexpected error occurred. Please notify the plugin developer.",
                [Lang.ErrorMonumentFinderNotLoaded] = "Error: Monument Finder is not loaded.",
                [Lang.ErrorNoMonuments] = "Error: No monuments found.",
                [Lang.ErrorNotAtMonument] = "Error: Not at a monument. Nearest is <color=orange>{0}</color> with distance <color=orange>{1}</color>",
                [Lang.ErrorSuitableEntityNotFound] = "Error: No suitable entity found.",
                [Lang.ErrorEntityNotEligible] = "Error: That entity is not managed by Monument Addons.",

                [Lang.SpawnErrorSyntax] = "Syntax: <color=orange>maspawn <entity></color>",
                [Lang.SpawnErrorEntityNotFound] = "Error: Entity <color=orange>{0}</color> not found.",
                [Lang.SpawnErrorMultipleMatches] = "Multiple matches:\n",
                [Lang.SpawnErrorNoTarget] = "Error: No valid spawn position found.",
                [Lang.SpawnSuccess] = "Spawned entity at <color=orange>{0}</color> matching monument(s) and saved to data file for monument <color=orange>{1}</color>.",
                [Lang.KillSuccess] = "Killed entity at <color=orange>{0}</color> matching monument(s) and removed from data file.",

                [Lang.CCTVSetIdSyntax] = "Syntax: <color=orange>{0} <id></color>",
                [Lang.CCTVSetIdSuccess] = "Updated CCTV id to <color=orange>{0}</color> at <color=orange>{1}</color> matching monument(s) and saved to data file. Nearby static computer stations will automatically register this CCTV.",
                [Lang.CCTVSetDirectionSuccess] = "Updated CCTV direction at <color=orange>{0}</color> matching monument(s) and saved to data file.",
            }, this, "en");
        }

        #endregion
    }
}
