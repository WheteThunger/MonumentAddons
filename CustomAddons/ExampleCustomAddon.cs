using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

using InitializeCallback = System.Func<BasePlayer, string[], System.ValueTuple<bool, object>>;
using EditCallback = System.Func<BasePlayer, string[], UnityEngine.Component, Newtonsoft.Json.Linq.JObject, System.ValueTuple<bool, object>>;
using SpawnAddonCallback = System.Func<System.Guid, UnityEngine.Component, UnityEngine.Vector3, UnityEngine.Quaternion, Newtonsoft.Json.Linq.JObject, UnityEngine.Component>;
using KillAddonCallback = System.Action<UnityEngine.Component>;
using UpdateAddonCallback = System.Func<UnityEngine.Component, Newtonsoft.Json.Linq.JObject, UnityEngine.Component>;
using DisplayCallback = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject, BasePlayer, System.Text.StringBuilder, float>;
using SetAddonDataCallback = System.Action<UnityEngine.Component, object>;

namespace Oxide.Plugins;

[Info("Example Custom Addon", "WhiteThunder", "0.3.0")]
[Description("Example plugin which demonstrates how to define a custom addon for Monument Addons.")]
internal class ExampleCustomAddon : CovalencePlugin
{
    private const string AddonName = "exampleaddon";

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
            AddonName,
            new Dictionary<string, object>
            {
                ["Initialize"] = new InitializeCallback(InitializeAddon),
                ["Edit"] = new EditCallback(EditAddon),
                ["Spawn"] = new SpawnAddonCallback(SpawnAddon),
                ["Kill"] = new KillAddonCallback(KillAddon),
                ["Update"] = new UpdateAddonCallback(UpdateAddon),
                ["Display"] = new DisplayCallback(Display),
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

    // Called when a player runs the "maspawn exampleaddon" command.
    private (bool, object) InitializeAddon(BasePlayer player, string[] args)
    {
        // Example: /maspawn exampleaddon 500
        // args[0] == "500"

        if (!VerifyValidArgs(player, "maspawn", args, out var health))
            return (false, null);

        return (true, new AddonData { Health = health });
    }

    // Called when a player runs the "maedit exampleaddon" command while looking at an existing instance.
    private (bool, object) EditAddon(BasePlayer player, string[] args, Component component, JObject data)
    {
        // Example: /maedit exampleaddon 500
        // args[0] == "500"

        if (!VerifyValidArgs(player, "maedit", args, out var health))
            return (false, null);

        return (true, new AddonData { Health = health });
    }

    private bool VerifyValidArgs(BasePlayer player, string cmd, string[] args, out float health)
    {
        if (args.Length == 0)
        {
            // Set health to 1000 if the player doesn't specify it.
            health = 1000f;
            return true;
        }

        if (float.TryParse(args[0], out health))
            return true;

        player.IPlayer.Reply($"Syntax: /{cmd} {AddonName} <health>");
        return false;
    }

    private Component SpawnAddon(Guid guid, Component monument, Vector3 position, Quaternion rotation, JObject data)
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

    // Called when a player runs "maedit" if `EditAddon` succeeded.
    // Called as a result of invoking the `SetData` callback.
    private Component UpdateAddon(Component component, JObject data)
    {
        if (component is not SamSite samSite)
            return component;

        var addonData = data?.ToObject<AddonData>();
        if (addonData != null)
        {
            samSite.SetHealth(addonData.Health);
        }

        // This is where you could return a different object, if you needed to respawn it for some reason.
        // For example, if the edit that the user requested required a different prefab, you can spawn a new one and
        // return that here, and Monument Addons will track that one going forward.
        return component;
    }

    private void KillAddon(Component component)
    {
        var entity = component as BaseEntity;
        if (entity != null && !entity.IsDestroyed)
        {
            entity.Kill();
        }
    }

    private void Display(Component component, JObject data, BasePlayer player, StringBuilder sb, float duration)
    {
        var addonData = data?.ToObject<AddonData>();
        if (addonData != null)
        {
            sb.AppendLine($"Health: {addonData.Health}");
        }
    }

    private class AddonData
    {
        [JsonProperty("Health")]
        public float Health;
    }
}
