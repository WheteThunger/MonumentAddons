# Custom Addons API

***This document describes how plugin developers can define custom addons and register them with Monument Addons***

**Important: Custom addon support is currently experimental!**

## How custom addons work

At a high level, a plugin can register a custom addon definition with Monument Addons, then server administrators can use `maspawn <addon_name>` to spawn that addon as if it were a typical entity.

Additional features allow plugins to save data for each custom addon instance, which will be saved in the data file of the Monument Addons profile, and passed back to the plugin when the entity needs to be spawned.

Basically any type of addon currently supported by Monument Addons, such as entities, spawn points and pastes could have each been registered as a type of custom addon.

## API

```cs
Dictionary<string, object> API_RegisterCustomAddon(Plugin plugin, string addonName, Dictionary<string, object> addonDefinition)
```

The `addonDefinition` dictionary should have the following keys:
- `"Initialize"` -- A method that Monument Addons will call when a player places this addon via the `maspawn` command.
  - Type: `System.Func<BasePlayer, string[], object>`
    - `BasePlayer` -- The player who ran `maspawn`.
    - `string[]` -- The arguments the player passed to the `maspawn` command. This includes the addon name as the first argument.
    - `object` -- The return value of the method should be an object that you want to save for the addon's initial data. This may optionally be a `JObject`. This data will be passed to `Spawn` and `AddDisplayInfo` as `JObject`.
- `"Spawn"` -- A method that Monument Addons can call to spawn each instance of the addon.
  - Type: `System.Func<Guid, UnityEngine.Component, Vector3, Quaternion, JObject, UnityEngine.Component>`
    - `Guid` -- The unique ID of the monument, generated whenever it's created with `maspawn`. If the addon is spawned at multiple instances of the same monument, this ID will be the same for each call to `Spawn`.
    - `UnityEngine.Component` -- The monument object. This may derive from `BaseEntity` if it's a dynamic monument or monument registered by another plugin. When this object derives from `BaseEntity`, you are advised to parent your addon to it if feasible, since the monument entity may be mobile, such as Cargo Ship.
    - `Vector3` -- The world position at which to spawn the addon.
    - `Quaternion` -- The world rotation to at which to spawn the addon.
    - `JObject` -- The partially serialized representation of the data that you previously set on the addon instance.
    - `UnityEngine.Component` -- The return value of the method should be a Unity component that Monument Addons will keep track of, which will be passed to the `"Kill"` or `"Update"` methods.
- `"CheckSpace"` -- A method that Monument Addons can call to check if there is space to spawn your addon. Applies to spawn points that have the `CheckSpace` property enabled.
  - Type: `System.Func<Vector3, Quaternion, bool>`
    - `Vector3` -- The position at which to check.
    - `Quaternion` -- The rotation that the addon is intended to be spawned at.
    - `bool` -- The return value should be `true` to indicate that there is sufficience space, else `false`.
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

## Examples

For example code, see the files in this folder.
