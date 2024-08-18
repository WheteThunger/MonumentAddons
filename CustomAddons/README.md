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

The `addonDefinition` dictionary can have the following keys. Note that only `Spawn` and `Kill` are required, the rest are optional.

### `"Initialize"`

A method that Monument Addons will call when a player places this addon via the `maspawn <addon-name> <arg1> <arg2> ...` command. 

Type: `System.Func<BasePlayer, string[], System.ValueTuple<bool, object>>`

- `BasePlayer` -- The player who ran `maspawn`.
- `string[]` -- The arguments the player passed to the `maspawn` command, after the addon name. For example, `/maspawn example foo bar` will provide an array with `foo` and `bar` only.
- (Return) `System.ValueTuple<bool, object>` -- The return value of the method should be a value tuple consisting of the following items.
  - `bool` -- `true` if the `maspawn` command should be allowed, else `false`. When returning `false` you should send a message to the player indicating what the problem was.
  - `object` -- The object that you want to save for the addon's initial data. This may optionally be a `JObject`. This data will be passed to `Spawn` and `Display` as `JObject`. The value of this item is ignored when returning `false` for the preceding item.

### `"Edit"`

A method that Monument Addons will call when the player runs the `maedit <arg1> <arg2> ...` command.

Type: `System.Func<BasePlayer, string[], UnityEngine.Component, JObject, System.ValueTuple<bool, object>>`

- `BasePlayer` -- The player who ran `maedit`.
- `string[]` -- The arguments the player passed to the `maedit` command, after the addon name.
- `UnityEngine.Component` -- The addon object returned by your `"Spawn"` callback.
- `JObject` -- The addon's current data.
- (Return) `System.ValueTuple<bool, object>` -- The return value of the method should be a value tuple consisting of the following items.
  - `bool` -- `true` if the `maedit` command should be allowed, else `false`. When returning `false` you should send a message to the player indicating what the problem was.
  - `object` -- The object that you want to save/overwrite the addon's current data. This may optionally be a `JObject`. This data will be passed to `Spawn` and `Display` as `JObject`. The value of this item is ignored when returning `false` for the preceding item.

### `"Spawn"`

A method that Monument Addons can call to spawn each instance of the addon.

Type: `System.Func<Guid, UnityEngine.Component, Vector3, Quaternion, JObject, UnityEngine.Component>`

- `Guid` -- The unique ID of the addon instance, generated whenever it's created with `maspawn`. If the addon is spawned at multiple instances of the same monument, this ID will be the same for each call to `Spawn`.
- `UnityEngine.Component` -- The monument object. This may derive from `BaseEntity` if it's a dynamic monument or monument registered by another plugin. When this object derives from `BaseEntity`, you are advised to parent your addon to it if feasible, since the monument entity may be mobile, such as Cargo Ship.
- `Vector3` -- The world position at which to spawn the addon.
- `Quaternion` -- The world rotation to at which to spawn the addon.
- `JObject` -- The partially serialized representation of the data that you previously set on the addon instance.
- (Return) `UnityEngine.Component` -- The return value of the method should be a Unity component that Monument Addons will keep track of, which will be passed to the `"Kill"` or `"Update"` methods.

### `"CheckSpace"`

A method that Monument Addons can call to check if there is space to spawn your addon. Applies to spawn points that have the `CheckSpace` property enabled. This is **not** called for addons placed via the `maspawn` command.

Type: `System.Func<Vector3, Quaternion, JObject, bool>`

- `Vector3` -- The position at which the addon is intended to be spawned.
- `Quaternion` -- The rotation at which the addon is intended to be spawned.
- `JObject` -- The data associated with the addon. Note: As of this writing, this will always be `null`, but custom addon data may be supported for spawn points in the future.
- (Return) `bool` -- The return value should be `true` to indicate that there is sufficience space, else `false`.

### `"Kill"`

A method that Monument Addons can call to kill each instance of the addon.

Type: `System.Action<UnityEngine.Component>`

- `UnityEngine.Component` -- The object that you returned in the `"Spawn"` method.

### `"Update"`

A method that Monument Addons can call to apply updated data to each instance of the addon. This will be called as the result of calling `"SetData"`, as the result of a player running the `maedit` command (which calls the `"Edit"` callback to determine the new data), and as the result of a player moving the addon via a supported plugin integration such as [Telekinesis](https://umod.org/plugins/telekinesis).

Type: `System.Func<UnityEngine.Component, JObject, UnityEngine.Component>`

- `UnityEngine.Component` -- The object that you returned in the `"Spawn"` method.
- `JObject` -- The partially serialized representation of the data that you previously set on the addon instance.
- (Return) `UnityEngine.Component` -- The return value should be the original component or a new one if you have to replace it to achieve the desired result. For example, if you need to change the prefab according to the contents of the data object, you can kill the current entity, then spawn and return a new one.

### `"Display"`

An optional method that Monument Addons can call to add display info about each instance of the addon. This will be used to show debug text at the addon location.

Type: `System.Action<UnityEngine.Component, JObject, BasePlayer, StringBuilder, float>`

- `UnityEngine.Component` -- The object that you returned in the `"Spawn"` and/or `"Update"` method.
- `JObject` -- The partially serialized representation of the data that you previously set on the addon instance.
- `BasePlayer` -- The player to whom the information will be displayed. This is useful if you want to localize the text or send ddraw commands for additional gizmos.
- `StringBuilder` -- A StringBuilder to add lines of text to. Ideally you are using the Oxide Lang API to localize this text.
- `float` -- The duration (in seconds) that you should send ddraw commands for if you want to draw additional gizmos.

### Return value

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
