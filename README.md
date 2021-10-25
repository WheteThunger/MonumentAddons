## Features

Easily spawn permanent entities at monuments, which auto respawn after restarts and wipes.

- Setup is done in-game, no config needed
- Uses familiar command syntax, inspired by `spawn <entity>`
- Works on any map seed and accounts for terrain height
- Entities are indestructible, have free electricity, and cannot be picked up
- Supports vanilla monuments, custom monuments, train tunnels, underwater labs, and cargo ship
- [Sign Artist](https://umod.org/plugins/sign-artist) integration allows persisting sign images
- [Entity Scale Manager](https://umod.org/plugins/entity-scale-manager) integration allows persisting entity scale

## Required plugins

- [Monument Finder](https://umod.org/plugins/monument-finder) -- Simply install. No configuration or permissions needed.

## Recommended compatible plugins

- [Custom Vending Setup](https://umod.org/plugins/custom-vending-setup) -- Allows customizing sale orders and names of vending machines at monuments. Works out-of-the-box with NPC vending machines spawned by this plugin.

## Getting started

1. Install the plugin and grant the `monumentaddons.admin` permission.
2. Go to any monument, such as a gas station.
3. Aim somewhere, such as a floor, wall or ceiling.
4. Run the `maspawn <entity>` command to spawn an entity of your choice. For example, `maspawn modularcarlift.static`.

This does several things.
- It spawns the entity where you are aiming.
- It spawns the entity at all other identical monuments (for example, at every gas station) using the correct relative position and rotation for those monuments.
- It saves this information in the plugin data file, so that the entity can be respawned when the plugin is reloaded, when the server is restarted, or when the server is wiped, even if using a new map seed (don't worry, this works well as long as the monuments don't significantly change between Rust updates).

## How underwater labs work

Since underwater labs are procedurally generated, this plugin does not spawn entities relative to the monuments themselves. Instead, entities are spawned relative to specific rooms. For example, if you spawn an entity in a moonpool room, the entity will also be spawned at all moonpool rooms in the same lab and other labs.

Note that some rooms have multiple possible vanilla configurations, so multiple instances of the same room might have slightly different placement of vanilla objects. This plugin does not currently differentiate between these room-specific configurations, so after spawning something into a lab room, be sure to inspect other instances of that room to make sure the entity placement makes sense for all of them.

## Permissions

- `monumentaddons.admin` -- Allows all commands.

## Commands

- `maspawn <entity>` -- Spawns an entity where you are aiming, using the entity short prefab name.
  - You must be at a monument.
  - Works just like the native `spawn` command, so if the entity name isn't specific enough, it will print all matching entity names.
  - Also spawns the entity at other matching monuments (e.g., if at a gas station, will spawn at all gas stations).
    - A monument is considered a match if it has the same short prefab name or the same alias as the monument you are aiming at. The Monument Finder plugin will assign aliases for primarily underground tunnels. For example, `station-sn-0` and `station-we-0` will both use the `TrainStation` alias, allowing all train stations to have the same entities.
  - Saves the entity info to the plugin data file so that reloading the plugin (or restarting the server) will respawn the entity.

The following commands only work on entities spawned by this plugin. The effect of these commands automatically applies to all copies of the entity at matching monuments, and also applies updates the data file.

- `makill` -- Kills the entity that you are aiming at.
- `maskin <skin id>` -- Updates the skin of the entity you are aiming at.
- `masetid <id>` -- Updates the RC identifier of the CCTV you are aiming at.
  - You must be aiming at the base of the camera.
  - Note: Each CCTV's RC identifier will have a numeric suffix like `1`, `2`, `3` and so on. This is done because each CCTV must have a unique identifier.
- `masetdir` -- Updates the direction of the CCTV you are aiming at, so that it points toward you.
  - You must be aiming at the base of the camera.

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.MonumentFinderNotLoaded": "Error: Monument Finder is not loaded.",
  "Error.NoMonuments": "Error: No monuments found.",
  "Error.NotAtMonument": "Error: Not at a monument. Nearest is <color=orange>{0}</color> with distance <color=orange>{1}</color>",
  "Error.NoSuitableEntityFound": "Error: No suitable entity found.",
  "Error.EntityNotEligible": "Error: That entity is not managed by Monument Addons.",
  "Spawn.Error.Syntax": "Syntax: <color=orange>maspawn <entity></color>",
  "Spawn.Error.EntityNotFound": "Error: Entity <color=orange>{0}</color> not found.",
  "Spawn.Error.MultipleMatches": "Multiple matches:\n",
  "Spawn.Error.NoTarget": "Error: No valid spawn position found.",
  "Spawn.Success": "Spawned entity at <color=orange>{0}</color> matching monument(s) and saved to data file for monument <color=orange>{1}</color>.",
  "Kill.Success": "Killed entity at <color=orange>{0}</color> matching monument(s) and removed from data file.",
  "Skin.Get": "Skin ID: <color=orange>{0}</color>. Run <color=orange>{1} <skin id></color> to change it.",
  "Skin.Set.Syntax": "Syntax: <color=orange>{0} <skin id></color>",
  "Skin.Set.Success": "Updated skin ID to <color=orange>{0}</color> at <color=orange>{1}</color> matching monument(s) and saved to data file.",
  "Skin.Error.Redirect": "Error: Skin <color=orange>{0}</color> is a redirect skin and cannot be set directly. Instead, spawn the entity as <color=orange>{1}</color>.",
  "CCTV.SetId.Error.Syntax": "Syntax: <color=orange>{0} <id></color>",
  "CCTV.SetId.Success": "Updated CCTV id to <color=orange>{0}</color> at <color=orange>{1}</color> matching monument(s) and saved to data file. Nearby static computer stations will automatically register this CCTV.",
  "CCTV.SetDirection.Success": "Updated CCTV direction at <color=orange>{0}</color> matching monument(s) and saved to data file."
}
```

## Instructions for plugin integrations

### Sign Arist integration

Use the following steps to set persistent images for signs or photo frames. Requires the [Sign Artist](https://umod.org/plugins/sign-artist) plugin to be installed with the appropriate permissions granted.

1. Spawn a sign with `maspawn sign.large`. You can also use other sign entities or photo frames, but neon signs are currently **not** supported.
2. Use a Sign Artist command such as `sil`, `silt` or `sili` to apply an image to the sign.

That's all you need to do. This plugin detects when you use a Sign Artist command and automatically saves the corresponding image URL or item short name in the data file for that particular sign or photo frame. When the plugin reloads, Sign Artist is called to reapply that image. Any change to a sign will also automatically propagate to all copies of that sign at other monuments.

Note: Only players with the `monumentaddons.admin` permission can edit signs that are managed by this plugin, so you don't have to worry about random players vandalizing the signs.

### Entity Scale Manager integration

Use the following steps to resize entities. Requires the [Entity Scale Manager](https://umod.org/plugins/entity-scale-manager) plugin to be installed with the appropriate permissions granted.

1. Spawn any entity with `maspawn <entity>`.
2. Resize the entity with `scale <size>`.

That's all you need to do. This plugin detects when an entity is resized and automatically applies that scale to copies of the entity as matching monuments, and saves the scale in the data file for that particular entity. When the plugin reloads, Entity Scale manager is called to reapply that scale.

## Instructions for specific entities

### CCTV cameras & computer stations

Use the following steps to set up CCTV cameras and computer stations.

1. Spawn a CCTV camera with `maspawn cctv.static`.
2. Update the camera's RC identifier with `masetid <id>` while looking at the base of the camera.
3. Update the direction the camera is facing with `masetdir` while looking at the base of the camera. This will cause the camera to face you, just like with deployable CCTV cameras.
4. Spawn a static computer station with `maspawn computerstation.static`.

That's all you need to do. The rest is automatic. Read below if you are interested in how it works.
- When a camera spawns, or when its RC identifier changes, it automatically adds itself to nearby static computer stations, including vanilla static computer stations (e.g., at bunker entrances or underwater labs).
- When a camera despawns, it automatically removes itself from nearby static computer stations.
- When a static computer station spawns, it automatically adds nearby static CCTV cameras.

Note: As of this writing, there is currently a client bug where having lots of RC identifiers saved in a computer station may cause some of them to not be displayed. This is especially an issue at large underwater labs. If you add custom CCTVs in such a location, some of them may not show up in the list at the computer station. One thing you can do to partially mitigate this issue is to use shorter RC identifier names.

### Bandit wheel

Use the following steps to set up a custom bandit wheel to allow players to gamble scrap.

1. Spawn a chair with `maspawn chair.static`. You can also use other mountable entities.
2. Spawn a betting terminal next to the chair with `maspawn bigwheelbettingterminal`. This needs to be close to the front of the chair so that players can reach it while sitting. Getting this position correct is the hardest part.
3. Keep spawning as many chairs and betting terminals as you want.
4. Spawn a bandit wheel with `maspawn big_wheel`. This needs to be within 30 meters (equivalent to 10 foundations) of the betting terminals to find them.

Notes:
- The `big_wheel` entity does not have a collider so you cannot currently use `makill` on it. If you need to reposition it, you will have to remove it from the data file and reload the plugin.
- If a betting terminal spawns more than 3 seconds after the wheel, the wheel won't know about it. This means that if you add more betting terminals after spawning the wheel, you will likely have to reload the plugin to respawn the wheel so that it can find all the betting terminals.

## Example entities

Structure/Defense:
- `barricade.{*}`
- `door_barricade_{*}`
- `sam_static`
- `sentry.{bandit|scientist}`
- `watchtower`

Utility:
- `bbq.static`
- `ceilinglight`
- `computerstation`
- `fireplace`
- `modularcarlift.static`
- `npcvendingmachine_{*}"`
- `phonebooth.static`
- `recycler_static`
- `repairbench_static`
- `researchtable_static`
- `simplelight`
- `telephone.deployed`
- `workbench{1|2}.static`
- `workbench{1|2|3}.deployed`

Fun / role play:
- `abovegroundpool`
- `arcademachine`
- `cardtable.static_config{a|b|c|d}`
- `chair.static`
- `paddlingpool`
- `piano.deployed.static`
- `sign.post.town.roof`
- `sign.post.town`
- `slotmachine`
- `sofa.deployed`
- `xmas_tree.deployed`

## Tips

- Be careful where you spawn entities. Make sure you are aiming at a point that is clearly inside a monument, or it may spawn in an unexpected location at other instances of that monument or for different map seeds. If you are aiming at a point where the terrain is not flat, that usually means it is not in the monument, though there are some exceptions.
- When placing an entity that has a matching deployable, equip the corresponding item and use it as a placement guide for pinpoint precision before running the `maspawn` command. Note: You may need to rotate the placement guide.
- Bind `makill` to a key while setting up entities to save time, since you may need to remove and replace an entity a few times to get it where you want.

## Troubleshooting

- If you receive the "Not at a monument" error, and you think you are at a monument, it means the Monument Finder plugin may have inaccurate bounds for that monument, such as if the monument is new to the game, or if it's a custom monument. Monument Finder provides commands to visualize the monument bounds, and configuration options to change them per monument.
- If you accidentally `ent kill` an entity that you spawned with this plugin, you can reload the plugin to restore it.
- If you spawn an entity that is either invisible or doesn't have a collider, and you want to remove it, you can unload the plugin, remove the entity from the plugin's data file, and then reload the plugin.

## Uninstallation

Simply remove the plugin. Spawned entities are automatically removed when the plugin unloads.
