## Features

Easily spawn permanent entities at monuments, which auto respawn after restarts and wipes.

- Setup is done in-game, no config needed
- Uses familiar command syntax, based on `spawn <entity>`
- Works on any map seed and accounts for terrain height
- Entities are indestructible, have free electricity, and cannot be picked up
- Supports custom monuments, train stations and cargo ship

### Compared to similar plugins

Monument Plus Lite and Monument Entities fulfill the same purpose, but those plugins require a multi-step process where you use a command in game, type something into the config, and then reload the plugin.

This plugin allows you to simply spawn the entity with a command in-game. The entity is automatically spawned at all similar monuments and saved in the data file so it can be respawned later.

## Getting started

1. Install the plugin and grant the `monumentaddons.admin` permission.
2. Go to any monument, such as a gas station.
3. Aim somewhere, such as a floor, wall or ceiling.
4. Run the `maspawn <entity>` command to spawn an entity of your choice. For example, `maspawn modularcarlift.static`.

This will do several things.
- It spawns the entity where you are aiming.
- It spawns the entity at all other identical monuments (for example, at every gas station) using the correct relative position and rotation for those monuments.
- It saves this information in the plugin data file, so that the entity can be respawned when the plugin is reloaded, when the server is restarted, or when the server is wiped, even if using a new map seed (don't worry, this works perfectly).

## Permissions

- `monumentaddons.admin` -- Allows use of the `maspawn` and `makill` commands.

## Commands

- `maspawn <entity>` -- Spawns an entity where you are aiming, using the entity short prefab name.
  - You must be at or near a monument.
  - Works just like the native `spawn` command, so if the entity name isn't specific enough, it will print all matching entity names.
  - Also spawns the entity at other matching monuments (e.g., if at a gas station, will spawn at all gas stations).
    - A monument is considered a match if it has the same short prefab name or the same configured alias as the monument you are aiming at.
  - This saves the entity info to the plugin data file so that reloading the plugin (or restarting the server) will respawn the entity.
- `makill` -- Kills the entity that you are aiming at.
  - Only works on entities that were spawned by this plugin.
  - Also removes the entity from other matching monuments.
  - This removes the entity from the plugin data file so that it won't respawn later.

## Configuration

Default configuration:

```json
{
  "IgnoredMonuments": [
    "power_sub_small_1",
    "power_sub_small_2",
    "power_sub_big_1",
    "power_sub_big_2"
  ],
  "MonumentAliases": {
    "station-sn-0": "TRAIN_STATION",
    "station-sn-1": "TRAIN_STATION",
    "station-sn-2": "TRAIN_STATION",
    "station-sn-3": "TRAIN_STATION",
    "station-we-0": "TRAIN_STATION",
    "station-we-1": "TRAIN_STATION",
    "station-we-2": "TRAIN_STATION",
    "station-we-3": "TRAIN_STATION",
    "straight-sn-0": "LOOT_TUNNEL",
    "straight-sn-1": "LOOT_TUNNEL",
    "straight-we-0": "LOOT_TUNNEL",
    "straight-we-1": "LOOT_TUNNEL",
    "intersection": "4_WAY_INTERSECTION",
    "intersection-n": "3_WAY_INTERSECTION",
    "intersection-e": "3_WAY_INTERSECTION",
    "intersection-s": "3_WAY_INTERSECTION",
    "intersection-w": "3_WAY_INTERSECTION",
    "entrance_bunker_a": "ENTRANCE_BUNKER",
    "entrance_bunker_b": "ENTRANCE_BUNKER",
    "entrance_bunker_c": "ENTRANCE_BUNKER",
    "entrance_bunker_d": "ENTRANCE_BUNKER"
  },
  "MaxDistanceFromMonument": {
    "excavator_1": 120.0,
    "junkyard_1": 35.0,
    "launch_site_1": 80.0,
    "lighthouse": 70.0,
    "military_tunnel_1": 40.0,
    "mining_quarry_c": 15.0,
    "OilrigAI": 60.0,
    "OilrigAI2": 85.0,
    "sphere_tank": 20.0,
    "swamp_c": 50.0,
    "trainyard_1": 40.0,
    "water_treatment_plant_1": 70.0
  }
}
```

- `IgnoredMonuments` -- This list allows you to exclude certain monuments from being found when using the `maspawn` command. This is useful for cases where monuments are essentially overlapping each other, since the plugin can have trouble selecting the correct monument.
  - The power substations are ignored by default since they tend to overlap monuments such as the Launch Site.
- `MonumentAliases` -- This allows you to give each monument an alias. Assigning the same alias to multiple monuments causes the same entities to spawn at all of them.
- `MaxDistanceFromMonument` -- These values help the `maspawn` command find nearby monuments that don't have proper bounds. This supports monument short prefab names as well as aliases. Add to or update this section if you are seeing the "Not at a monument" error.
  - Caution: Avoid setting these limits too high. Having a low limit helps prevent you from accidentally spawning an entity outside of a monument, or relative to a monument that is very far away.

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.NoMonuments": "Error: No monuments found.",
  "Error.NotAtMonument": "Error: Not at a monument. Nearest is <color=orange>{0}</color> with distance <color=orange>{1}</color>",
  "Spawn.Error.Syntax": "Syntax: <color=orange>maspawn <entity></color>",
  "Spawn.Error.EntityNotFound": "Error: Entity <color=orange>{0}</color> not found.",
  "Spawn.Error.MultipleMatches": "Multiple matches:\n",
  "Spawn.Error.NoTarget": "Error: No valid spawn position found.",
  "Spawn.Error.Failed": "Error: Failed to spawn enttiy.",
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

- If you receive the "Not at a monument" error, you may need to update the `MaxDistanceFromMonument` config for that monument. This is needed for custom monuments.
- If you accidentally `ent kill` an entity that you spawned with this plugin, you can reload the plugin to restore it.
- If you spawn an entity that is either invisible or doesn't have a collider, and you want to remove it, you can unload the plugin, remove the entity from the plugin's data file, and then reload the plugin.

## Uninstallation

Simply remove the plugin. Spawned entities are automatically removed when the plugin unloads.
