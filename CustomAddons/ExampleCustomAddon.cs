using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using UnityEngine;

using InitializeCallback = System.Func<BasePlayer, string[], object>;
using SpawnAddonCallback = System.Func<System.Guid, UnityEngine.Component, UnityEngine.Vector3, UnityEngine.Quaternion, Newtonsoft.Json.Linq.JObject, UnityEngine.Component>;
using KillAddonCallback = System.Action<UnityEngine.Component>;
using UpdateAddonCallback = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject>;
using AddDisplayInfoCallback = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject, System.Text.StringBuilder>;
using SetAddonDataCallback = System.Action<UnityEngine.Component, object>;

namespace Oxide.Plugins;

[Info("Example Custom Addon", "WhiteThunder", "0.2.0")]
[Description("Example plugin which demonstrates how to define a custom addon for Monument Addons.")]
internal class ExampleCustomAddon : CovalencePlugin
{
    [PluginReference]
    private readonly Plugin MonumentAddons;

    private SetAddonDataCallback _setAddonData;

    private void OnServerInitialized()
    {
        if (MonumentAddons != null)
        {
            RegisterCustomAddon();
        }
    }

    private void OnPluginLoaded(Plugin plugin)
    {
        if (plugin.Name == nameof(MonumentAddons))
        {
            RegisterCustomAddon();
        }
    }

    private void RegisterCustomAddon()
    {
        var registeredAddon = MonumentAddons.Call(
            "API_RegisterCustomAddon",
            this,
            "exampleaddon",
            new Dictionary<string, object>
            {
                ["Initialize"] = new InitializeCallback(InitializeAddon),
                ["Spawn"] = new SpawnAddonCallback(SpawnCustomAddon),
                ["Kill"] = new KillAddonCallback(KillCustomAddon),
                ["Update"] = new UpdateAddonCallback((component, data) =>
                {
                    var entity = component as SamSite;
                    var addonData = data.ToObject<AddonData>();
                    if (addonData != null)
                    {
                        entity.SetHealth(addonData.Health);
                    }
                }),
                ["AddDisplayInfo"] = new AddDisplayInfoCallback((component, data, sb) =>
                {
                    var addonData = data?.ToObject<AddonData>();
                    if (addonData != null)
                    {
                        sb.AppendLine($"Health: {addonData.Health}");
                    }
                }),
            }
        ) as Dictionary<string, object>;

        if (registeredAddon == null)
        {
            LogError("Error registering addon with Monument Addons.");
            return;
        }

        _setAddonData = registeredAddon["SetData"] as SetAddonDataCallback;
        if (_setAddonData == null)
        {
            LogError("SetData method not present in MonumentAddons return value.");
        }
    }

    private object InitializeAddon(BasePlayer player, string[] args)
    {
        // Example: /maspawn exampleaddon 500
        // args[0] == "500"

        if (args.Length < 1 || !float.TryParse(args[0], out var health))
        {
            // Set health to 500 if not specified via /maspawn.
            health = 500;
        }

        return new AddonData { Health = health };
    }

    private Component SpawnCustomAddon(Guid guid, Component monument, Vector3 position, Quaternion rotation, JObject data)
    {
        var entity = GameManager.server.CreateEntity("assets/prefabs/npc/sam_site_turret/sam_site_turret_deployed.prefab", position, rotation) as SamSite;
        if (entity == null)
            return null;

        UnityEngine.Object.DestroyImmediate(entity.GetComponent<DestroyOnGroundMissing>());
        entity.EnableSaving(false);
        entity.Spawn();

        var addonData = data?.ToObject<AddonData>();
        if (addonData != null)
        {
            entity.SetHealth(addonData.Health);
        }

        return entity;
    }

    private void KillCustomAddon(Component component)
    {
        var entity = component as BaseEntity;
        if (entity != null && !entity.IsDestroyed)
        {
            entity.Kill();
        }
    }

    private class AddonData
    {
        [JsonProperty("Health")]
        public float Health;
    }
}
