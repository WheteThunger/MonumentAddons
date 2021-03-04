using Newtonsoft.Json;
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
    [Info("Monument Addons", "WhiteThunder", "0.1.1")]
    [Description("Allows privileged players to add permanent entities to monuments.")]
    internal class MonumentAddons : CovalencePlugin
    {
        #region Fields

        private static MonumentAddons _pluginInstance;

        private const float MaxRaycastDistance = 20;
        private const float MaxDistanceForEqualityCheck = 0.01f;
        private const float SpawnDelayPerEntity = 0.05f;

        private const string PermissionAdmin = "monumentaddons.admin";

        private static readonly int HitLayers = Rust.Layers.Mask.Construction
            + Rust.Layers.Mask.Default
            + Rust.Layers.Mask.Deployed
            + Rust.Layers.Mask.Terrain
            + Rust.Layers.Mask.World;

        private readonly List<uint> _spawnedEntityIds = new List<uint>();

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
        }

        private void Unload()
        {
            if (_spawnCoroutine != null)
                ServerMgr.Instance.StopCoroutine(_spawnCoroutine);

            foreach (var entityId in _spawnedEntityIds)
            {
                var entity = BaseNetworkable.serverEntities.Find(entityId);
                if (entity != null)
                    entity.Kill();
            }

            UnityEngine.Object.Destroy(ImmortalProtection);
            _pluginInstance = null;
        }

        private void OnServerInitialized()
        {
            ImmortalProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
            ImmortalProtection.name = "MonumentAddonsProtection";
            ImmortalProtection.Add(1);

            _spawnCoroutine = ServerMgr.Instance.StartCoroutine(SpawnSavedEntities());
        }

        // This hook is exposed by plugin: Remover Tool (RemoverTool).
        private object canRemove(BasePlayer player, BaseEntity entity)
        {
            if (_spawnedEntityIds.Contains(entity.net.ID))
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

            var basePlayer = player.Object as BasePlayer;

            Vector3 position;
            if (!TryGetHitPosition(basePlayer, out position))
            {
                ReplyToPlayer(player, "Spawn.Error.NoTarget");
                return;
            }

            var nearestMonument = FindNearestMonument(position);
            if (nearestMonument == null || !nearestMonument.IsInBounds(position))
            {
                ReplyToPlayer(player, "Error.NoMonument");
                return;
            }

            var prefabName = matches[0];

            var monumentShortName = GetShortName(nearestMonument.name);
            var localPosition = nearestMonument.transform.InverseTransformPoint(position);
            var localRotationAngle = (nearestMonument.transform.rotation.eulerAngles.y - basePlayer.GetNetworkRotation().eulerAngles.y + 180);

            var entityData = new EntityData
            {
                PrefabName = prefabName,
                Position = localPosition,
                RotationAngle = localRotationAngle,
            };

            var ent = SpawnEntity(entityData, nearestMonument);
            if (ent == null)
            {
                ReplyToPlayer(player, "Spawn.Error.Failed");
                return;
            }

            _pluginData.AddEntityData(entityData, monumentShortName);

            ReplyToPlayer(player, "Spawn.Success", monumentShortName);
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

            if (!_spawnedEntityIds.Contains(entity.net.ID))
            {
                ReplyToPlayer(player, "Kill.Error.NotEligible");
                return;
            }

            var position = entity.transform.position;

            var nearestMonument = FindNearestMonument(position);
            if (nearestMonument == null || !nearestMonument.IsInBounds(position))
            {
                ReplyToPlayer(player, "Error.NoMonument");
                return;
            }

            var localPosition = nearestMonument.transform.InverseTransformPoint(position);

            var monumentShortName = GetShortName(nearestMonument.name);
            if (!_pluginData.TryRemoveEntityData(entity.PrefabName, localPosition, monumentShortName))
            {
                ReplyToPlayer(player, "Kill.Error.NoPositionMatch", monumentShortName, localPosition);
                return;
            }

            _spawnedEntityIds.Remove(entity.net.ID);
            entity.Kill();

            ReplyToPlayer(player, "Kill.Success");
        }

        #endregion

        #region Helper Methods

        private static bool TryGetHitPosition(BasePlayer player, out Vector3 position, float maxDistance = MaxRaycastDistance)
        {
            RaycastHit hit;
            if (Physics.Raycast(player.eyes.HeadRay(), out hit, maxDistance, HitLayers, QueryTriggerInteraction.Ignore))
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

        private string[] FindPrefabMatches(string partialName)
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

        private MonumentInfo FindNearestMonument(Vector3 position)
        {
            MonumentInfo closestMonument = null;
            float shortestDistance = float.MaxValue;

            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                if (monument == null)
                    continue;

                var distance = Vector3.Distance(monument.transform.position, position);
                if (distance < shortestDistance)
                {
                    shortestDistance = distance;
                    closestMonument = monument;
                }
            }

            return closestMonument;
        }

        private IEnumerator SpawnSavedEntities()
        {
            var spawnDelay = Rust.Application.isLoading ? 0 : SpawnDelayPerEntity;
            if (spawnDelay > 0)
                Puts($"Spawning entities with {SpawnDelayPerEntity}s delay after each one...");

            var sb = new StringBuilder();
            sb.AppendLine("Spawned Entities:");

            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                if (monument == null)
                    continue;

                var monumentShortName = GetShortName(monument.name);

                List<EntityData> entityDataList;
                if (!_pluginData.MonumentMap.TryGetValue(monumentShortName, out entityDataList))
                    continue;

                foreach (var entityData in entityDataList)
                {
                    var entity = SpawnEntity(entityData, monument);
                    sb.AppendLine($" - {monumentShortName}: {entity.ShortPrefabName} at {entity.transform.position}");
                    yield return new WaitForSeconds(spawnDelay);
                }
            }

            Puts(sb.ToString());
        }

        private BaseEntity SpawnEntity(EntityData entityData, MonumentInfo monument)
        {
            var position = monument.transform.TransformPoint(entityData.Position);
            var rotation = Quaternion.Euler(0, monument.transform.rotation.eulerAngles.y - entityData.RotationAngle, 0);

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

            entity.Spawn();

            _spawnedEntityIds.Add(entity.net.ID);

            return entity;
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

            public bool TryRemoveEntityData(string prefabName, Vector3 localPosition, string monumentShortName)
            {
                foreach (var entry in MonumentMap)
                {
                    var entityDataList = entry.Value;
                    for (var i = entityDataList.Count - 1; i >= 0; i--)
                    {
                        var entityData = entityDataList[i];
                        if (Vector3.Distance(entityData.Position, localPosition) <= MaxDistanceForEqualityCheck)
                        {
                            entityDataList.RemoveAt(i);

                            if (entityDataList.Count == 0)
                                MonumentMap.Remove(entry.Key);

                            Save();
                            return true;
                        }
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

            [JsonProperty("RotationAngle")]
            public float RotationAngle;
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
                ["Error.NoMonument"] = "Error: Not at a monument",
                ["Spawn.Error.Syntax"] = "Syntax: <color=yellow>maspawn <entity></color>",
                ["Spawn.Error.EntityNotFound"] = "Error: Entity {0} not found.",
                ["Spawn.Error.MultipleMatches"] = "Multiple matches:\n",
                ["Spawn.Error.NoTarget"] = "Error: No valid spawn position found.",
                ["Spawn.Error.Failed"] = "Error: Failed to spawn enttiy.",
                ["Spawn.Success"] = "Spawned entity and saved to data file for monument '{0}'.",
                ["Kill.Error.EntityNotFound"] = "Error: No entity found.",
                ["Kill.Error.NotEligible"] = "Error: That entity is not controlled by Monument Addons.",
                ["Kill.Error.NoPositionMatch"] = "Error: No saved entity found for monument '{0}' at position {1}.",
                ["Kill.Success"] = "Entity killed and removed from data file.",
            }, this, "en");
        }

        #endregion
    }
}
