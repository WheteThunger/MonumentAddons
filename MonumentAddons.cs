using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Monument Addons", "WhiteThunder", "0.4.0")]
    [Description("Allows privileged players to add permanent entities to monuments.")]
    internal class MonumentAddons : CovalencePlugin
    {
        #region Fields

        private static MonumentAddons _pluginInstance;
        private static Configuration _pluginConfig;

        private const float MaxRaycastDistance = 50;
        private const float TerrainProximityTolerance = 0.001f;

        private const string PermissionAdmin = "monumentaddons.admin";

        private static readonly int HitLayers = Rust.Layers.Mask.Construction
            + Rust.Layers.Mask.Default
            + Rust.Layers.Mask.Deployed
            + Rust.Layers.Mask.Terrain
            + Rust.Layers.Mask.World;

        private static readonly Dictionary<string, Quaternion> StationRotations = new Dictionary<string, Quaternion>()
        {
            ["station-sn-0"] = Quaternion.Euler(0, 180, 0),
            ["station-sn-1"] = Quaternion.identity,
            ["station-sn-2"] = Quaternion.Euler(0, 180, 0),
            ["station-sn-3"] = Quaternion.identity,
            ["station-we-0"] = Quaternion.Euler(0, 90, 0),
            ["station-we-1"] = Quaternion.Euler(0, -90, 0),
            ["station-we-2"] = Quaternion.Euler(0, 90, 0),
            ["station-we-3"] = Quaternion.Euler(0, -90, 0),
        };

        private readonly Dictionary<BaseEntity, EntityData> _spawnedEntities = new Dictionary<BaseEntity, EntityData>();

        private ProtectionProperties ImmortalProtection;
        private StoredData _pluginData;
        private Coroutine _spawnCoroutine;

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            _pluginData = StoredData.Load();

            permission.RegisterPermission(PermissionAdmin, this);

            Unsubscribe(nameof(OnEntitySpawned));
        }

        private void Unload()
        {
            if (_spawnCoroutine != null)
                ServerMgr.Instance.StopCoroutine(_spawnCoroutine);

            foreach (var entity in _spawnedEntities.Keys)
            {
                if (entity != null && !entity.IsDestroyed)
                    entity.Kill();
            }

            UnityEngine.Object.Destroy(ImmortalProtection);

            _pluginConfig = null;
            _pluginInstance = null;
        }

        private void OnServerInitialized()
        {
            ImmortalProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
            ImmortalProtection.name = "MonumentAddonsProtection";
            ImmortalProtection.Add(1);

            _spawnCoroutine = ServerMgr.Instance.StartCoroutine(SpawnSavedEntities());
        }

        private void OnEntitySpawned(CargoShip cargoShip)
        {
            var monumentWrapper = MonumentWrapper.FromCargoShip(cargoShip);

            List<EntityData> entityDataList;
            if (!_pluginData.MonumentMap.TryGetValue(monumentWrapper.SavedName, out entityDataList))
                return;

            foreach (var entityData in entityDataList)
                SpawnEntity(entityData, monumentWrapper);
        }

        private void OnEntityKill(BaseEntity entity)
        {
            _spawnedEntities.Remove(entity);
        }

        // This hook is exposed by plugin: Remover Tool (RemoverTool).
        private object canRemove(BasePlayer player, BaseEntity entity)
        {
            if (_spawnedEntities.ContainsKey(entity))
                return false;

            return null;
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

            MonumentWrapper nearestMonumentWrapper;
            float sqrDistance;
            if (!NearMonument(basePlayer, position, out nearestMonumentWrapper, out sqrDistance))
            {
                if (nearestMonumentWrapper != null)
                    ReplyToPlayer(player, "Error.NotAtMonument", nearestMonumentWrapper.ShortName, Mathf.Sqrt(sqrDistance).ToString("f1"));
                else
                    ReplyToPlayer(player, "Error.NoMonuments");
                return;
            }

            var localPosition = nearestMonumentWrapper.InverseTransformPoint(position);
            var localRotationAngle = basePlayer.HasParent()
                ? 180 - basePlayer.viewAngles.y
                : nearestMonumentWrapper.Rotation.eulerAngles.y - basePlayer.viewAngles.y + 180;

            var entityData = new EntityData
            {
                PrefabName = prefabName,
                Position = localPosition,
                RotationAngle = localRotationAngle,
                OnTerrain = Math.Abs(position.y - TerrainMeta.HeightMap.GetHeight(position)) <= TerrainProximityTolerance
            };

            var matchingMonumentWrappers = FindMatchingMonuments(nearestMonumentWrapper);
            foreach (var wrapper in matchingMonumentWrappers)
            {
                var ent = SpawnEntity(entityData, wrapper);
                if (ent == null)
                {
                    ReplyToPlayer(player, "Spawn.Error.Failed");
                    return;
                }
            }

            _pluginData.AddEntityData(entityData, nearestMonumentWrapper.SavedName);
            ReplyToPlayer(player, "Spawn.Success", matchingMonumentWrappers.Count, nearestMonumentWrapper.SavedName);
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

            EntityData entityData;
            if (!_spawnedEntities.TryGetValue(entity, out entityData))
            {
                ReplyToPlayer(player, "Kill.Error.NotEligible");
                return;
            }

            _pluginData.RemoveEntityData(entityData);

            var matchingEntities = GetMatchingEntities(entityData);
            foreach (var ent in matchingEntities)
            {
                _spawnedEntities.Remove(ent);
                ent.Kill();
            }

            ReplyToPlayer(player, "Kill.Success", matchingEntities.Count);
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

        private static MonumentWrapper FindNearestMonument(Vector3 position, out float sqrDistanceFromBounds)
        {
            MonumentInfo closestMonument = null;
            float shortestSqrDistance = float.MaxValue;

            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                if (monument == null)
                    continue;

                if (_pluginConfig.IgnoredMonuments.Contains(GetShortName(monument.name)))
                    continue;

                var sqrDistance = (monument.ClosestPointOnBounds(position) - position).sqrMagnitude;
                if (sqrDistance < shortestSqrDistance)
                {
                    shortestSqrDistance = sqrDistance;
                    closestMonument = monument;
                }
            }

            sqrDistanceFromBounds = shortestSqrDistance;
            return MonumentWrapper.FromMonument(closestMonument);
        }

        private static List<MonumentWrapper> FindMatchingMonuments(MonumentWrapper monumentWrapper)
        {
            var savedName = monumentWrapper.SavedName;

            var list = new List<MonumentWrapper>();

            if (monumentWrapper.IsMonument)
            {
                foreach (var monument in TerrainMeta.Path.Monuments)
                {
                    if (monument == null)
                        continue;

                    var wrapped = MonumentWrapper.FromMonument(monument);
                    if (wrapped.SavedName == savedName)
                        list.Add(wrapped);
                }
            }
            else if (monumentWrapper.IsDungeon)
            {
                foreach (var dungeon in TerrainMeta.Path.DungeonGridCells)
                {
                    if (dungeon == null)
                        continue;

                    var wrapped = MonumentWrapper.FromDungeon(dungeon);
                    if (wrapped.SavedName == savedName)
                        list.Add(wrapped);
                }
            }
            else if (monumentWrapper.IsCargoShip)
            {
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var cargoShip = entity as CargoShip;
                    if (cargoShip != null)
                        list.Add(MonumentWrapper.FromCargoShip(cargoShip));
                }
            }

            return list;
        }

        private static MonumentWrapper FindNearestTrainStation(Vector3 position, out float sqrDistanceFromBounds)
        {
            DungeonGridCell closestStation = null;
            float shortestSqrDistance = float.MaxValue;

            foreach (var dungeon in TerrainMeta.Path.DungeonGridCells)
            {
                if (dungeon == null)
                    continue;

                if (_pluginConfig.IgnoredMonuments.Contains(GetShortName(dungeon.name)))
                    continue;

                if (!StationRotations.ContainsKey(GetShortName(dungeon.name)))
                    continue;

                var sqrDistance = (dungeon.transform.position - position).sqrMagnitude;
                if (sqrDistance < shortestSqrDistance)
                {
                    shortestSqrDistance = sqrDistance;
                    closestStation = dungeon;
                }
            }

            sqrDistanceFromBounds = shortestSqrDistance;
            return MonumentWrapper.FromDungeon(closestStation);
        }

        private static bool IsCloseEnough(MonumentWrapper monumentWrapper, Vector3 position, float sqrDistanceFromBounds)
        {
            if (monumentWrapper.IsMonument && monumentWrapper.Monument.IsInBounds(position))
                return true;

            return sqrDistanceFromBounds <= Math.Pow(monumentWrapper.MaxAllowedDistance, 2);
        }

        private static bool NearMonument(BasePlayer player, Vector3 position, out MonumentWrapper nearestMonumentWrapper, out float sqrDistanceFromBounds)
        {
            sqrDistanceFromBounds = 0;

            if (OnCargoShip(player, position, out nearestMonumentWrapper))
                return true;

            if (position.y < -100)
            {
                nearestMonumentWrapper = FindNearestTrainStation(position, out sqrDistanceFromBounds);
                return IsCloseEnough(nearestMonumentWrapper, position, sqrDistanceFromBounds);
            }

            nearestMonumentWrapper = FindNearestMonument(position, out sqrDistanceFromBounds);
            return IsCloseEnough(nearestMonumentWrapper, position, sqrDistanceFromBounds);
        }

        private static bool OnCargoShip(BasePlayer player, Vector3 position, out MonumentWrapper monumentWrapper)
        {
            monumentWrapper = null;

            var cargoShip = player.GetParentEntity() as CargoShip;
            if (cargoShip == null)
                return false;

            if (!cargoShip.WorldSpaceBounds().Contains(position))
                return false;

            monumentWrapper = MonumentWrapper.FromCargoShip(cargoShip);
            return true;
        }

        private IEnumerator SpawnSavedEntities()
        {
            var sb = new StringBuilder();

            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                if (monument == null)
                    continue;

                var monumentWrapper = MonumentWrapper.FromMonument(monument);

                List<EntityData> entityDataList;
                if (!_pluginData.MonumentMap.TryGetValue(monumentWrapper.SavedName, out entityDataList))
                    continue;

                foreach (var entityData in entityDataList)
                {
                    var entity = SpawnEntity(entityData, monumentWrapper);
                    sb.AppendLine($" - {monumentWrapper.ShortName}: {entity.ShortPrefabName} at {entity.transform.position}");
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            foreach (var dungeonCell in TerrainMeta.Path.DungeonGridCells)
            {
                if (dungeonCell == null)
                    continue;

                var monumentWrapper = MonumentWrapper.FromDungeon(dungeonCell);

                List<EntityData> entityDataList;
                if (!_pluginData.MonumentMap.TryGetValue(monumentWrapper.SavedName, out entityDataList))
                    continue;

                foreach (var entityData in entityDataList)
                {
                    var entity = SpawnEntity(entityData, monumentWrapper);
                    sb.AppendLine($" - {monumentWrapper.ShortName}: {entity.ShortPrefabName} at {entity.transform.position}");
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            foreach (var serverEntity in BaseNetworkable.serverEntities)
            {
                var cargoShip = serverEntity as CargoShip;
                if (cargoShip == null)
                    continue;

                var monumentWrapper = MonumentWrapper.FromCargoShip(cargoShip);

                List<EntityData> entityDataList;
                if (!_pluginData.MonumentMap.TryGetValue(monumentWrapper.SavedName, out entityDataList))
                    continue;

                foreach (var entityData in entityDataList)
                {
                    var entity = SpawnEntity(entityData, monumentWrapper);
                    sb.AppendLine($" - {monumentWrapper.ShortName}: {entity.ShortPrefabName} at {entity.transform.position}");
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            if (sb.Length > 0)
            {
                sb.Insert(0, "Spawned Entities:\n");
                Puts(sb.ToString());
            }

            // We don't want to be subscribed to OnEntitySpawned(CargoShip) until the coroutine is done.
            // Otherwise, a cargo ship could spawn while the coroutine is running and could get double entities.
            Subscribe(nameof(OnEntitySpawned));
        }

        private BaseEntity SpawnEntity(EntityData entityData, MonumentWrapper monumentWrapper)
        {
            var position = monumentWrapper.TransformPoint(entityData.Position);
            var rotation = Quaternion.Euler(0, monumentWrapper.Rotation.eulerAngles.y - entityData.RotationAngle, 0);

            if (entityData.OnTerrain)
                position.y = TerrainMeta.HeightMap.GetHeight(position);

            var entity = GameManager.server.CreateEntity(entityData.PrefabName, position, rotation);
            if (entity == null)
                return null;

            // In case the plugin doesn't clean it up on server shutdown, make sure it doesn't come back so it's not duplicated.
            entity.enableSaving = false;

            var combatEntity = entity as BaseCombatEntity;
            if (combatEntity != null)
            {
                combatEntity.baseProtection = ImmortalProtection;
                combatEntity.pickup.enabled = false;
            }

            var ioEntity = entity as IOEntity;
            if (ioEntity != null)
            {
                ioEntity.SetFlag(BaseEntity.Flags.On, true);
                ioEntity.SetFlag(IOEntity.Flag_HasPower, true);
            }

            DestroyProblemComponents(entity);

            if (monumentWrapper.IsCargoShip)
            {
                entity.SetParent(monumentWrapper.CargoShip, worldPositionStays: true, sendImmediate: true);
                var mountable = entity as BaseMountable;
                if (mountable != null)
                    mountable.isMobile = true;
            }

            entity.Spawn();

            _spawnedEntities.Add(entity, entityData);

            return entity;
        }

        private List<BaseEntity> GetMatchingEntities(EntityData entityData)
        {
            var list = new List<BaseEntity>();
            foreach (var entry in _spawnedEntities)
            {
                if (entry.Value == entityData)
                    list.Add(entry.Key);
            }
            return list;
        }

        #endregion

        #region Classes

        private class MonumentWrapper
        {
            public static MonumentWrapper FromMonument(MonumentInfo monument) =>
                new MonumentWrapper() { Monument = monument };

            public static MonumentWrapper FromDungeon(DungeonGridCell dungeonCell) =>
                new MonumentWrapper() { DungeonCell = dungeonCell };

            public static MonumentWrapper FromCargoShip(CargoShip cargoShip) =>
                new MonumentWrapper() { CargoShip = cargoShip };

            private Quaternion _trainStationRotation
            {
                get
                {
                    var dungeonShortName = GetShortName(DungeonCell.name);

                    Quaternion rotation;
                    return StationRotations.TryGetValue(dungeonShortName, out rotation)
                        ? rotation
                        : Quaternion.identity;
                }
            }

            public MonumentInfo Monument { get; private set; }
            public DungeonGridCell DungeonCell { get; private set; }
            public CargoShip CargoShip { get; private set; }

            public bool IsMonument => Monument != null;
            public bool IsDungeon => DungeonCell != null;
            public bool IsTrainStation => IsDungeon && StationRotations.ContainsKey(GetShortName(DungeonCell.name));
            public bool IsCargoShip => CargoShip != null;

            public string ShortName
            {
                get
                {
                    var obj = Monument ?? DungeonCell ?? CargoShip as MonoBehaviour;

                    var name = obj.name;
                    if (name.Contains("monument_marker.prefab"))
                        name = obj.transform.root.name;

                    return GetShortName(name);
                }
            }

            public Transform Transform => Monument?.transform ?? DungeonCell?.transform ?? CargoShip?.transform;
            public Vector3 Position => Transform?.position ?? Vector3.zero;
            public Quaternion Rotation => IsTrainStation ? _trainStationRotation : Transform.rotation;

            public Vector3 TransformPoint(Vector3 localPosition) =>
                Transform.TransformPoint(IsTrainStation ? Rotation * localPosition : localPosition);

            public Vector3 InverseTransformPoint(Vector3 localPosition)
            {
                var worldPosition = Transform.InverseTransformPoint(localPosition);
                return IsTrainStation ? Quaternion.Inverse(Rotation) * worldPosition : worldPosition;
            }

            public string SavedName
            {
                get
                {
                    var shortanme = ShortName;
                    string alias;
                    return _pluginConfig.MonumentAliases.TryGetValue(shortanme, out alias)
                        ? alias
                        : shortanme;
                }
            }

            public float MaxAllowedDistance
            {
                get
                {
                    float maxAllowedDistance;
                    return _pluginConfig.MaxDistanceFromMonument.TryGetValue(SavedName, out maxAllowedDistance)
                        ? maxAllowedDistance
                        : 0;
                }
            }
        }

        #endregion

        #region Data

        private class StoredData
        {
            [JsonProperty("Monuments")]
            public Dictionary<string, List<EntityData>> MonumentMap = new Dictionary<string, List<EntityData>>();

            public static StoredData Load() =>
                Interface.Oxide.DataFileSystem.ReadObject<StoredData>(_pluginInstance.Name);

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
                foreach (var entry in MonumentMap)
                {
                    var entityDataList = entry.Value;
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
            [JsonProperty("IgnoredMonuments")]
            public string[] IgnoredMonuments = new string[]
            {
                "power_sub_small_1",
                "power_sub_small_2",
                "power_sub_big_1",
                "power_sub_big_2",
            };

            [JsonProperty("MonumentAliases")]
            public readonly Dictionary<string, string> MonumentAliases = new Dictionary<string, string>()
            {
                ["station-sn-0"] = "TRAIN_STATION",
                ["station-sn-1"] = "TRAIN_STATION",
                ["station-sn-2"] = "TRAIN_STATION",
                ["station-sn-3"] = "TRAIN_STATION",
                ["station-we-0"] = "TRAIN_STATION",
                ["station-we-1"] = "TRAIN_STATION",
                ["station-we-2"] = "TRAIN_STATION",
                ["station-we-3"] = "TRAIN_STATION",
                ["entrance_bunker_a"] = "ENTRANCE_BUNKER",
                ["entrance_bunker_b"] = "ENTRANCE_BUNKER",
                ["entrance_bunker_c"] = "ENTRANCE_BUNKER",
                ["entrance_bunker_d"] = "ENTRANCE_BUNKER",
            };

            [JsonProperty("MaxDistanceFromMonument")]
            public Dictionary<string, float> MaxDistanceFromMonument = new Dictionary<string, float>()
            {
                ["excavator_1"] = 120,
                ["junkyard_1"] = 35,
                ["launch_site_1"] = 80,
                ["lighthouse"] = 70,
                ["military_tunnel_1"] = 40,
                ["mining_quarry_c"] = 15,
                ["OilrigAI"] = 60,
                ["OilrigAI2"] = 85,
                ["sphere_tank"] = 20,
                ["swamp_c"] = 50,
                ["TRAIN_STATION"] = 100,
                ["trainyard_1"] = 40,
                ["water_treatment_plant_1"] = 70,
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
                ["Spawn.Error.Failed"] = "Error: Failed to spawn enttiy.",
                ["Spawn.Success"] = "Spawned entity at <color=orange>{0}</color> matching monument(s) and saved to data file for monument <color=orange>{1}</color>.",
                ["Kill.Error.EntityNotFound"] = "Error: No entity found.",
                ["Kill.Error.NotEligible"] = "Error: That entity is not managed by Monument Addons.",
                ["Kill.Success"] = "Killed entity at <color=orange>{0}</color> matching monument(s) and removed from data file.",
            }, this, "en");
        }

        #endregion
    }
}
