# Custom Monuments API

***This document describes how plugin developers can register arbitrary objects/areas as monuments. If you simply want to utilize custom monuments added by map developers, see the Monument Finder documentation.***

## Use cases

### Event plugins

A plugin that creates an event can register the area of the event as a custom monument, allowing server owners to then attach entities and spawn points to the event. When the event ends, the addons will be automatically removed. This grants server owners significant configurability over events at very little cost to the event plugin developer.

### Custom monuments without monument markers

Sometimes map developers decide to create custom monuments without a corresponding monument marker (i.e., a map marker which says the name of the monument). When there is no monument marker, the Monument Finder plugin (and Monument Addons by extension) will be unable to recognize it as a monument, so server owners canot place addons there. To solve this, a plugin can register the custom monument directly, allowing server owners to utilize all capabilities of Monument Addons.

## Overall sequence

Here is the overall sequence

1. Monument Addons loads
2. Your plugin creates a monument or finds an object it wants to tell Monument Addons about
3. Your plugin calls `API_RegisterCustomMonument` to register the monument
4. Server owner uses commands like `maspawn` to place addons at the monument
5. Your plugin tears down the monument at some point, and Monument Addons automatically despawns the addons
6. Your plugin recreates and/or re-registers the monument at some point (e.g., when your plugin reloads, or when the server restarts), and Monument Addons automatically respawns the addons from earlier

## API

### API_RegisterCustomMonument

```cs
object API_RegisterCustomMonument(Plugin plugin, string monumentName, UnityEngine.Component component, Bounds bounds)
```

- Registers the specificied object as a monument with the specified name and bounds
- If there are already addons associated with that monument name, the addons will spawn automatically
- Returns `true` if the object was successfully registered as a custom monument
- Returns `false` if there was a problem registering the object as a custom monument

Parameter guidance:
- `plugin` -- Always pass your own plugin (i.e., `this`).
- `monumentName` -- The name you want to assign to the monument. The name should be relatively unique to avoid name collisions with other plugins that want to register custom monuments. It is possible for multiple plugins to register monuments with the same name if desired. All monuments with the same name will have the same addons automatically. The name will be displayed when using `mashow` and will be saved in Monument Addons profile data files when a server owner places addons at the monument.
- `component` -- The entity or component that represents the monument.
- `bounds` -- The bounds must be **relative** to the object's `transform`, **not** using world coordinates. The bounds will be displayed when using `mashow`.
    - For example, to place a 30x30x30 box, with the center point of the box being the entity position, supply `new Bounds(new Vector3(0, 0, 0), new Vector3(30, 30, 30))`.
    - For example, to place a 30x30x30 box, with the center point being 10m above the entity's position, supply `new Bounds(new Vector3(0, 10, 0), new Vector3(30, 30, 30))`.

### API_UnregisterCustomMonument

```
object API_UnregisterCustomMonument(Plugin plugin, UnityEngine.Component component)
```

- Returns `true` if the object was successfully unregistered or is not a custom monument
- Returns `false` if unregistering the monument was denied due to being registered by a different plugin

Note that it is not necessary to call this API under most circumstances. For more details, see below.

## How to teardown custom monuments

When you want to teardown a custom monument, there are multiple options depending on your use case.

- If your plugin is unloading, you don't have to do anything because Monument Addons will automatically tear down all custom monuments registered by your plugin when your plugin unloads
  - However, if your plugin created the object that you provided to the `API_RegisterCustomMonument` call, you are strongly advised to destroy the object to avoid leaking it
- If your plugin is not unloading, but you want to teardown the monument for some reason (such as due to the event ending), you can either destroy the monument object if you created it, or you can call `API_UnregisterCustomMonument` if you want to leave the object as-is (i.e., if you didn't create it)
  - To destroy a monument object, if it derives from `BaseEntity`, simply call `entity.Kill()`; if it **does not** derive from `BaseEntity`, destroy the `GameObject` via `UnityEngine.Object.Destroy(component.gameObject)`

Note: If you are thinking of registering a mobile entity as a monument, such as a ferry ship, please be aware that only some addon types will be parented to the entity, such as entities and spawn points. Pastes will not be parented to the entity, meaning they won't move with the entity. This is the same behavior as for dynamic monuments defined in the plugin configuration.

## Best practices

- Register your custom monument with Monument Addons in two places: (1) In `OnServerInitialized` hook, and (2) in the `OnPluginLoaded` hook in case MonumentAddons loads late or reloads
- When creating a new object to represent a custom monument, if not using an entity, when cleaning up your monument (e.g., when the event is over, or when your plugin unloads), make sure to destroy the `GameObject`, not just the `Component`.
