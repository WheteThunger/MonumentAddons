# Custom Monument Addons

**Custom addon support is currently experimental!**

## How custom addons work

At a high level, a plugin can register a custom addon definition with Monument Addons, then players can use `maspawn <addon_name>` to spawn that addon as if it were a typical entity.

Additional features allow plugins to save data for each custom addon instance, which will be saved in the data file of the Monument Addons profile, and passed back to the plugin when the entity needs to be spawned.

## API

```csharp
Dictionary<string, object> API_RegisterCustomAddon(Plugin plugin, string addonName, Dictionary<string, object> addonDefinition)
```

The `addonDefinition` dictionary should have the following keys:
- `"Spawn"` -- A method that Monument Addons can call to spawn each instance of the addon.
  - Type: `System.Func<Vector3, Quaternion, JObject, UnityEngine.Component>`
    - `Vector3` -- The world position to at which to spawn the addon.
    - `Quaternion` -- The world rotation to at which to spawn the addon.
    - `JObject` -- The partially serialized representation of the data that you previously set on the addon instance.
    - `UnityEngine.Component` -- The return value of the method should be a Unity component that Monument Addons will keep track of, which will be passed to the `"Kill"` or `"Update"` methods.
- `"Kill"` -- A method that Monument Addons can call to kill each instance of the addon.
  - Type: `System.Action<UnityEngine.Component>`
    - `UnityEngine.Component` -- The object that you returned in the `"Spawn"` method.
- `"Update"` -- A method that Monument Addons can call to apply updated data to each instance of the addon. This method is only required if you intend to call `"SetData"`.
  - Type: `System.Action<UnityEngine.Component, JObject>`
    - `UnityEngine.Component` -- The object that you returned in the `"Spawn"` method.
    - `JObject` -- The partially serialized representation of the data that you previously set on the addon instance.
- `"AddDisplayInfo"` -- An optional method that Monument Addons can call to add display info about each instance of the addon. This will be used to show debug text at the addon location.
  - Type: `System.Action<UnityEngine.Component, JObject, StringBuilder>`
    - `UnityEngine.Component` -- The object that you returned in the `"Spawn"` method.
    - `JObject` -- The partially serialized representation of the data that you previously set on the addon instance.
    - `StringBuilder` -- A StringBuilder to add lines of text to. Ideally you have are using the Oxide Lang API to localize this text.

If the addon was successfully registered, the resulting `Dictionary<string, object>` will have the following keys.
- `"SetData"` -- A method that your plugin can call to apply new data to each instance of the addon. When this method is invoked, Monument Addons will cally your `"Update"` callback for each instance of the addon. If users of your plugin need the ability to customize each addon instance, this
  - `System.Action<UnityEngine.Component, object>`

## Best practices

- Make sure your plugin supports spawning multiple instances per data object. This is necessary because Monument Addons will attempt to spawn the addon at all instances of the monument.
- Disable saving on all entities you spawn. Monument Addons kills addons asynchronously, so it may not have time to kill them during a server reboot, therefore disabling saving prevents the entities from being double spawned on reboot.
- In the `"Kill"` callback, verify that the objects haven't already been destroyed, in case they were killed outside of the context of Monument Addons such as via `ent kill`.
- Make entities you spawn invincible by updating protection properties, disabling stability, etc. This is important because Monument Addons does not currently respawn custom addons that have been destroyed (unless the plugin or profile is reloaded).

## Example

```csharp
// ExampleAddonPlugin.cs

// These type aliases should be declared outside of the namespace.
using SpawnAddonCallback = System.Func<UnityEngine.Vector3, UnityEngine.Quaternion, Newtonsoft.Json.Linq.JObject, UnityEngine.Component>;
using KillAddonCallback = System.Action<UnityEngine.Component>;
using UpdateAddonCallback = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject>;
using AddDisplayInfoCallback = System.Action<UnityEngine.Component, Newtonsoft.Json.Linq.JObject, System.Text.StringBuilder>;
using SetAddonDataCallback = System.Action<UnityEngine.Component, object>;

[PluginReference]
Plugin MonumentAddons;

SetAddonDataCallback _setAddonData;

void OnPluginLoaded(Plugin plugin)
{
    if (plugin == MonumentAddons)
    {
        RegisterCustomAddon();
    }
}

void RegisterCustomAddon()
{
    var registeredAddon = MonumentAddons.Call(
        "API_RegisterCustomAddon",
        this,
        "exampleaddon",
        new Dictionary<string, object>
        {
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
        LogError($"Error registering addon with Monument Addons.");
        return;
    }

    _setAddonData = registeredAddon["SetData"] as SetAddonDataCallback;
    if (_setAddonData == null)
    {
        LogError($"SetData method not present in MonumentAddons return value.");
    }
}

private UnityEngine.Component SpawnCustomAddon(Vector3 position, Quaternion rotation, JObject data)
{
    var entity = GameManager.server.CreateEntity("assets/prefabs/npc/sam_site_turret/sam_site_turret_deployed.prefab", position, rotation) as SamSite;
    entity.EnableSaving(false);
    entity.Spawn();

    var addonData = data?.ToObject<AddonData>();
    if (addonData != null)
    {
        entity.SetHealth(addonData.Health);
    }

    return entity;
}

private void KillCustomAddon(UnityEngine.Component component)
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
    public float Health = 500;
}
```
