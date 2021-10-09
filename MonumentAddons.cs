﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
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
        private Plugin MonumentFinder;

        private static MonumentAddons _pluginInstance;
        private static Configuration _pluginConfig;

        private const float MaxRaycastDistance = 50;
        private const float TerrainProximityTolerance = 0.001f;

        private const string PermissionAdmin = "monumentaddons.admin";

        private const string CargoShipAlias = "cargoshiptest";

        private static readonly int HitLayers = Rust.Layers.Mask.Construction
            + Rust.Layers.Mask.Default
            + Rust.Layers.Mask.Deployed
            + Rust.Layers.Mask.Terrain
            + Rust.Layers.Mask.World;

        private readonly EntityManager _entityManager = new EntityManager();
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
            _coroutineManager.FireAndForgetCoroutine(_entityManager.UnloadRoutine());

            UnityEngine.Object.Destroy(_immortalProtection);

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
            if (!_pluginData.MonumentMap.TryGetValue(CargoShipAlias, out entityDataList))
                return;

            _coroutineManager.StartCoroutine(
                _entityManager.SpawnEntitiesAtMonumentRoutine(entityDataList, new CargoShipMonument(cargoShip))
            );
        }

        // This hook is exposed by plugin: Remover Tool (RemoverTool).
        private object canRemove(BasePlayer player, BaseEntity entity)
        {
            if (AddonComponent.GetForEntity(entity) != null)
                return false;

            return null;
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

        private MonumentProxy GetClosestMonumentProxy(Vector3 position)
        {
            var dictResult = MonumentFinder.Call("API_GetClosest", position) as Dictionary<string, object>;
            if (dictResult == null)
                return null;

            return new MonumentProxy(dictResult);
        }

        private List<BaseMonument> GetMonumentProxiesMatchingAlias(string alias)
        {
            var dictList = MonumentFinder.Call("API_FindByAlias", alias) as List<Dictionary<string, object>>;
            if (dictList == null)
                return null;

            var monumentProxyList = new List<BaseMonument>();
            foreach (var dict in dictList)
                monumentProxyList.Add(new MonumentProxy(dict));

            return monumentProxyList;
        }

        #endregion

        #region Commands

        [Command("maspawn")]
        private void CommandSpawn(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer)
                return;

            if (!player.HasPermission(PermissionAdmin))
            {
                ReplyToPlayer(player, "Error.NoPermission");
                return;
            }

            if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            {
                ReplyToPlayer(player, "Spawn.Error.Syntax");
                return;
            }

            var matches = FindPrefabMatches(args[0]);
            if (matches.Length == 0)
            {
                ReplyToPlayer(player, "Spawn.Error.EntityNotFound", args[0]);
                return;
            }

            if (matches.Length > 1)
            {
                var replyMessage = GetMessage(player, "Spawn.Error.MultipleMatches");
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
                ReplyToPlayer(player, "Spawn.Error.NoTarget");
                return;
            }

            var closestMonument = GetClosestMonument(basePlayer, position);
            if (closestMonument == null)
            {
                ReplyToPlayer(player, "Error.NoMonuments");
                return;
            }

            if (!closestMonument.IsInBounds(position))
            {
                var closestPoint = closestMonument.ClosestPointOnBounds(position);
                var distance = (position - closestPoint).magnitude;
                ReplyToPlayer(player, "Error.NotAtMonument", closestMonument.Alias, distance.ToString("f1"));
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

            var matchingMonuments = GetMonumentsMatchingAlias(closestMonument.Alias);
            _coroutineManager.StartCoroutine(
                _entityManager.SpawnEntityAtMonumentsRoutine(entityData, matchingMonuments)
            );

            _pluginData.AddEntityData(entityData, closestMonument.Alias);
            ReplyToPlayer(player, "Spawn.Success", matchingMonuments.Count, closestMonument.Alias);
        }

        [Command("makill")]
        private void CommandKill(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer)
                return;

            if (!player.HasPermission(PermissionAdmin))
            {
                ReplyToPlayer(player, "Error.NoPermission");
                return;
            }

            var basePlayer = player.Object as BasePlayer;

            var entity = GetLookEntity(basePlayer);
            if (entity == null)
            {
                ReplyToPlayer(player, "Kill.Error.EntityNotFound");
                return;
            }

            var component = AddonComponent.GetForEntity(entity);
            if (component == null)
            {
                ReplyToPlayer(player, "Kill.Error.NotEligible");
                return;
            }

            var controller = component.Adapter.Controller;
            var numEntities = controller.Adapters.Count;
            controller.Destroy();

            _pluginData.RemoveEntityData(controller.EntityData);
            ReplyToPlayer(player, "Kill.Success", numEntities);
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

            return GetClosestMonumentProxy(position);
        }

        private List<BaseMonument> GetMonumentsMatchingAlias(string alias)
        {
            if (alias == CargoShipAlias)
            {
                var cargoShipList = new List<BaseMonument>();
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var cargoShip = entity as CargoShip;
                    if (cargoShip != null)
                        cargoShipList.Add(new CargoShipMonument(cargoShip));
                }
                return cargoShipList;
            }

            return GetMonumentProxiesMatchingAlias(alias);
        }

        private IEnumerator SpawnAllEntitiesRoutine()
        {
            var spawnedEntities = 0;

            foreach (var entry in _pluginData.MonumentMap)
            {
                var monumentAlias = entry.Key;
                var entityDataList = entry.Value;

                var matchingMonuments = GetMonumentsMatchingAlias(monumentAlias);
                if (matchingMonuments == null)
                    continue;

                spawnedEntities += matchingMonuments.Count;

                foreach (var entityData in entityDataList)
                {
                    yield return _entityManager.SpawnEntityAtMonumentsRoutine(entityData, matchingMonuments);
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
            public virtual string Alias => ShortName;
            public virtual Vector3 Position => Object.transform.position;
            public virtual Quaternion Rotation => Object.transform.rotation;

            public BaseMonument(MonoBehaviour behavior)
            {
                Object = behavior;
            }

            public abstract bool IsInBounds(Vector3 position);
            public abstract Vector3 ClosestPointOnBounds(Vector3 position);

            public virtual Vector3 TransformPoint(Vector3 localPosition) =>
                Object.transform.TransformPoint(localPosition);

            public virtual Vector3 InverseTransformPoint(Vector3 worldPosition) =>
                Object.transform.InverseTransformPoint(worldPosition);
        }

        private class MonumentProxy : BaseMonument
        {
            public override string PrefabName => (string)_monumentInfo["PrefabName"];
            public override string ShortName => (string)_monumentInfo["ShortName"];
            public override string Alias => (string)_monumentInfo["Alias"];
            public override Vector3 Position => (Vector3)_monumentInfo["Position"];
            public override Quaternion Rotation => (Quaternion)_monumentInfo["Rotation"];

            private Dictionary<string, object> _monumentInfo;

            public MonumentProxy(Dictionary<string, object> monumentInfo) : base((MonoBehaviour)monumentInfo["Object"])
            {
                _monumentInfo = monumentInfo;
            }

            public override Vector3 TransformPoint(Vector3 localPosition) =>
                ((Func<Vector3, Vector3>)_monumentInfo["TransformPoint"]).Invoke(localPosition);

            public override Vector3 InverseTransformPoint(Vector3 worldPosition) =>
                ((Func<Vector3, Vector3>)_monumentInfo["InverseTransformPoint"]).Invoke(worldPosition);

            public override bool IsInBounds(Vector3 position) =>
                ((Func<Vector3, bool>)_monumentInfo["IsInBounds"]).Invoke(position);

            public override Vector3 ClosestPointOnBounds(Vector3 position) =>
                ((Func<Vector3, Vector3>)_monumentInfo["ClosestPointOnBounds"]).Invoke(position);
        }

        private class CargoShipMonument : BaseMonument
        {
            public CargoShip CargoShip { get; private set; }

            private OBB BoundingBox => CargoShip.WorldSpaceBounds();

            public CargoShipMonument(CargoShip cargoShip) : base(cargoShip)
            {
                CargoShip = cargoShip;
            }

            public override bool IsInBounds(Vector3 position) =>
                BoundingBox.Contains(position);

            public override Vector3 ClosestPointOnBounds(Vector3 position) =>
                BoundingBox.ClosestPoint(position);
        }

        #endregion

        #region Component

        private class AddonComponent : FacepunchBehaviour
        {
            public static void AddToEntity(BaseEntity entity, EntityAdapter adapter, BaseMonument monument) =>
                entity.gameObject.AddComponent<AddonComponent>().Init(adapter, monument);

            public static AddonComponent GetForEntity(BaseEntity entity) =>
                entity.GetComponent<AddonComponent>();

            public EntityAdapter Adapter;

            private BaseEntity _entity;

            private void Awake()
            {
                _entity = GetComponent<BaseEntity>();
            }

            public void Init(EntityAdapter adapter, BaseMonument monument)
            {
                Adapter = adapter;

                // In case the entity persists after unload, ensure it doesn't come back after restart which would cause duplication.
                _entity.EnableSaving(false);

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

                DestroyProblemComponents(_entity);

                var cargoShipMonument = monument as CargoShipMonument;
                if (cargoShipMonument != null)
                {
                    _entity.SetParent(cargoShipMonument.CargoShip, worldPositionStays: true);

                    var mountable = _entity as BaseMountable;
                    if (mountable != null)
                        mountable.isMobile = true;
                }
            }

            private void OnDestroy()
            {
                Adapter.OnEntityKilled(_entity);
            }
        }

        #endregion

        #region Entity Adapter

        private abstract class EntityAdapterBase
        {
            public EntityControllerBase Controller { get; private set; }
            public BaseMonument Monument { get; private set; }

            public EntityAdapterBase(EntityControllerBase controller, BaseMonument monument)
            {
                Controller = controller;
            }

            public abstract void Kill();
            public abstract void OnEntityKilled(BaseEntity entity);
        }

        private class EntityAdapter : EntityAdapterBase
        {
            private BaseEntity _entity;

            public EntityAdapter(EntityControllerBase controller, BaseMonument monument, BaseEntity entity) : base(controller, monument)
            {
                _entity = entity;
            }

            public override void Kill()
            {
                if (!_entity.IsDestroyed)
                    _entity.Kill();
            }

            public override void OnEntityKilled(BaseEntity entity)
            {
                Controller.OnAdapterKilled(this);
            }
        }

        #endregion

        #region Entity Controller

        private abstract class EntityControllerBase
        {
            public EntityManager Manager { get; private set; }
            public EntityData EntityData { get; private set; }
            public List<EntityAdapter> Adapters { get; private set; } = new List<EntityAdapter>();

            public EntityControllerBase(EntityManager manager, EntityData entityData)
            {
                Manager = manager;
                EntityData = entityData;
            }

            public abstract EntityAdapter SpawnAtMonument(BaseMonument monument);

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

            public virtual IEnumerator DestroyRoutine()
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
                Manager.UnregisterController(EntityData);
                _pluginInstance._coroutineManager.FireAndForgetCoroutine(DestroyRoutine());
            }

            public void OnAdapterKilled(EntityAdapter adapter)
            {
                Adapters.Remove(adapter);

                if (Adapters.Count == 0)
                    Manager.UnregisterController(EntityData);
            }
        }

        private class EntityController : EntityControllerBase
        {
            public EntityController(EntityManager manager, EntityData data) : base(manager, data) {}

            public override EntityAdapter SpawnAtMonument(BaseMonument monument)
            {
                var position = monument.TransformPoint(EntityData.Position);
                var rotation = Quaternion.Euler(0, monument.Rotation.eulerAngles.y + EntityData.RotationAngle, 0);

                if (EntityData.OnTerrain)
                    position.y = TerrainMeta.HeightMap.GetHeight(position);

                var entity = GameManager.server.CreateEntity(EntityData.PrefabName, position, rotation);
                if (entity == null)
                    return null;

                // In case the plugin doesn't clean it up on server shutdown, make sure it doesn't come back so it's not duplicated. x
                entity.EnableSaving(false);

                var combatEntity = entity as BaseCombatEntity;
                if (combatEntity != null)
                {
                    combatEntity.baseProtection = _pluginInstance._immortalProtection;
                    combatEntity.pickup.enabled = false;
                }

                var ioEntity = entity as IOEntity;
                if (ioEntity != null)
                {
                    ioEntity.SetFlag(BaseEntity.Flags.On, true);
                    ioEntity.SetFlag(IOEntity.Flag_HasPower, true);
                }

                DestroyProblemComponents(entity);

                var cargoShipMonument = monument as CargoShipMonument;
                if (cargoShipMonument != null)
                {
                    entity.SetParent(cargoShipMonument.CargoShip, worldPositionStays: true, sendImmediate: true);

                    var mountable = entity as BaseMountable;
                    if (mountable != null)
                        mountable.isMobile = true;
                }

                var adapter = new EntityAdapter(this, monument, entity);
                Adapters.Add(adapter);
                AddonComponent.AddToEntity(entity, adapter, monument);

                entity.Spawn();

                return adapter;
            }
        }

        #endregion

        #region Entity Manager

        private class EntityManager
        {
            private Dictionary<EntityData, EntityControllerBase> _controllersByEntityData = new Dictionary<EntityData, EntityControllerBase>();

            public IEnumerator SpawnEntityAtMonumentsRoutine(EntityData entityData, IEnumerable<BaseMonument> monumentList)
            {
                _pluginInstance.TrackStart();
                var controller = EnsureController(entityData);
                _pluginInstance.TrackEnd();
                yield return controller.SpawnAtMonumentsRoutine(monumentList);
            }

            public IEnumerator SpawnEntitiesAtMonumentRoutine(IEnumerable<EntityData> entityDataList, BaseMonument monument)
            {
                foreach (var entityData in entityDataList)
                {
                    // Check for null in case the cargo ship was destroyed.
                    if (monument.Object == null)
                        yield break;

                    _pluginInstance.TrackStart();
                    SpawnEntityAtMonument(entityData, monument);
                    _pluginInstance.TrackEnd();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            public void UnregisterController(EntityData entityData)
            {
                _controllersByEntityData.Remove(entityData);
            }

            public IEnumerator UnloadRoutine()
            {
                foreach (var controller in _controllersByEntityData.Values.ToArray())
                    yield return controller.DestroyRoutine();
            }

            private void SpawnEntityAtMonument(EntityData entityData, BaseMonument monument)
            {
                EnsureController(entityData).SpawnAtMonument(monument);
            }

            private EntityControllerBase GetController(EntityData entityData)
            {
                EntityControllerBase controller;
                return _controllersByEntityData.TryGetValue(entityData, out controller)
                    ? controller
                    : null;
            }

            private EntityControllerBase EnsureController(EntityData entityData)
            {
                var controller = GetController(entityData);
                if (controller == null)
                {
                    controller = new EntityController(this, entityData);
                    _controllersByEntityData[entityData] = controller;
                }
                return controller;
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

            private void Save() =>
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

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player, messageName), args));

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.IPlayer, messageName), args));

        private string GetMessage(IPlayer player, string messageName, params object[] args) =>
            GetMessage(player.Id, messageName, args);

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Error.NoPermission"] = "You don't have permission to do that.",
                ["Error.NoMonuments"] = "Error: No monuments found.",
                ["Error.NotAtMonument"] = "Error: Not at a monument. Nearest is <color=orange>{0}</color> with distance <color=orange>{1}</color>",
                ["Spawn.Error.Syntax"] = "Syntax: <color=orange>maspawn <entity></color>",
                ["Spawn.Error.EntityNotFound"] = "Error: Entity <color=orange>{0}</color> not found.",
                ["Spawn.Error.MultipleMatches"] = "Multiple matches:\n",
                ["Spawn.Error.NoTarget"] = "Error: No valid spawn position found.",
                ["Spawn.Success"] = "Spawned entity at <color=orange>{0}</color> matching monument(s) and saved to data file for monument <color=orange>{1}</color>.",
                ["Kill.Error.EntityNotFound"] = "Error: No entity found.",
                ["Kill.Error.NotEligible"] = "Error: That entity is not managed by Monument Addons.",
                ["Kill.Success"] = "Killed entity at <color=orange>{0}</color> matching monument(s) and removed from data file.",
            }, this, "en");
        }

        #endregion
    }
}
