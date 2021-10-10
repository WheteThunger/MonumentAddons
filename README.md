## Features

Easily spawn permanent entities at monuments, which auto respawn after restarts and wipes.

- Setup is done in-game, no config needed
- Uses familiar command syntax, based on `spawn <entity>`
- Works on any map seed and accounts for terrain height
- Entities are indestructible, have free electricity, and cannot be picked up
- Supports vanilla monuments, custom monuments, train tunnels, underwater labs, and cargo ship

## Required plugins

- [Monument Finder](https://umod.org/plugins/monument-finder) -- Simply install. No configuration needed. You may optionally update that plugin's configuration to change the bounds of monuments.

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

- `monumentaddons.admin` -- Allows use of the `maspawn` and `makill` commands.

## Commands

- `maspawn <entity>` -- Spawns an entity where you are aiming, using the entity short prefab name.
  - You must be at a monument.
  - Works just like the native `spawn` command, so if the entity name isn't specific enough, it will print all matching entity names.
  - Also spawns the entity at other matching monuments (e.g., if at a gas station, will spawn at all gas stations).
    - A monument is considered a match if it has the same short prefab name or the same alias as the monument you are aiming at. The Monument Finder plugin will assign aliases for primarily underground tunnels. For example, `station-sn-0` and `station-we-0` will both use the `TrainStation` alias, allowing all train stations to have the same entities.
  - Saves the entity info to the plugin data file so that reloading the plugin (or restarting the server) will respawn the entity.
- `makill` -- Kills the entity that you are aiming at.
  - Only works on entities that were spawned by this plugin.
  - Also removes the entity from other matching monuments.
  - Removes the entity from the plugin data file so that it won't respawn later.

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.MonumentFinderNotLoaded": "Error: Monument Finder is not loaded.",
  "Error.NoMonuments": "Error: No monuments found.",
  "Error.NotAtMonument": "Error: Not at a monument. Nearest is <color=orange>{0}</color> with distance <color=orange>{1}</color>",
  "Spawn.Error.Syntax": "Syntax: <color=orange>maspawn <entity></color>",
  "Spawn.Error.EntityNotFound": "Error: Entity <color=orange>{0}</color> not found.",
  "Spawn.Error.MultipleMatches": "Multiple matches:\n",
  "Spawn.Error.NoTarget": "Error: No valid spawn position found.",
  "Spawn.Success": "Spawned entity at <color=orange>{0}</color> matching monument(s) and saved to data file for monument <color=orange>{1}</color>.",
  "Kill.Error.EntityNotFound": "Error: No entity found.",
  "Kill.Error.NotEligible": "Error: That entity is not managed by Monument Addons.",
  "Kill.Success": "Killed entity at <color=orange>{0}</color> matching monument(s) and removed from data file."
}
```

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
