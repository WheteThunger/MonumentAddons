## Features

- Allows spawning permanent entities at monuments, which respawn after server restarts and even map wipes
- Familiar syntax for spawning entities, similar to `spawn <entity>`
- Placement is automatically determined relative to monuments, so you only need to place each entity once, and it will respawn in the correct position and rotation on any map seed for every monument with the same prefab name
- Spawned entities are indestructible, have free electricity, and cannot be picked up

## Getting started

1. After installing the plugin and granting permission, go to a monument where you want to spawn a permanent entity (for example, at Oxum's Gas Station).
2. Aim somewhere where you would like to spawn an entity, such as on a flat surface, wall, or ceiling.
3. Run the `maspawn <entity>` command to spawn an entity of your choice (for example, `maspawn modularcarlift.static`).

This does several things.
- It spawns the entity where you are aiming.
- It spawns the entity at all other identical monuments (for example, at every gas station) using the correct relative position and rotation for those monuments.
- It saves this information in the plugin data file, so that the entity can be respawned when the plugin is reloaded, when the server is restarted, or when the server is wiped, even if using a new map seed (don't worry, this works perfectly).

## Tips

- Be careful where you spawn entities. Make sure you are aiming at a point that is clearly inside a monument, or it may spawn in an unexpected location at other instances of that monument or for different map seeds. If you are aiming at a point where the terrain is not flat, that usually means it is not in the monument, though there are some exceptions.
- When placing an entity that has a matching deployable, equip the corresponding item and use it as a placement guide for pinpoint precision before running the `maspawn` command. Note: You may need to rotate the placement guide.
- If you accidentally `ent kill` an entity that you spawned with this plugin, you can reload the plugin to restore it.

## Troubleshooting

If you are unable to spawn an entity at a particular monument, you may need to add a distance override in the plugin config for that entity's short prefab name.

## Commands

- `maspawn <entity>` -- Spawns an entity where you are aiming, and at all matching monuments.
  - You must be at or near a monument.
  - Works just like the native `spawn` command, so if the entity name isn't specific enough, it will print all matching entity names.
  - A monument is considered a match if it has the same short prefab name or the same configured alias as the monument you are aiming at.
  - This saves the entity info to the plugin data file so that reloading the plugin will respawn the entity.
- `makill` -- Kills the entity that the player is looking at, and all entities places in the same spot at matching monuments.
  - Only works on entities that were spawned by this plugin.
  - This removes the entity from the plugin data file so that it won't respawn later.
  - Tip: Bind this to a key to save time while setting up entities.

## Permissions

- `monumentaddons.admin` -- Required to use the `maspawn` and `makill` commands.

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
    "station-we-3": "TRAIN_STATION"
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
    "TRAIN_STATION": 100.0,
    "trainyard_1": 40.0,
    "water_treatment_plant_1": 70.0
  }
}
```

- `IgnoredMonuments` -- This list allows you to exclude certain monuments from being found when using the `maspawn` command. This is useful for cases where monuments are essentially overlapping each other, since the plugin can have trouble selecting the correct monument.
  - The power substations are ignored by default since they tend to overlap monuments such as the Launch Site.
- `MonumentAliases` -- This allows you to give each monument an alias. Assigning the same alias to multiple monuments causes the same entities to spawn at all of them.
- `MaxDistanceFromMonument` -- These values help the `maspawn` command find nearby monuments that don't have proper bounds. This supports monument short prefab names as well as aliases. Add or update this section if you are seeing the "Not at a monument" error.
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

## Uninstallation

Simply remove the plugin. Spawned entities are automatically removed when the plugin unloads.

If you reinstall the plugin, you may want to delete the data file beforehand, or else entities will spawn in the positions you previously saved.

## Example entities

Structure/Defense:
- `sentry.{bandit|scientist}`
- `sam_static`
- `watchtower`
- `door_barricade_{*}`
- `barricade.{*}`

Utility:
- `modularcarlift.static`
- `computerstation`
- `recycler_static`
- `repairbench_static`
- `workbench{1|2}.static`
- `workbench{1|2|3}.deployed`
- `researchtable_static`
- `npcvendingmachine_{*}"`
- `ceilinglight`
- `simplelight`
- `fireplace`
- `bbq.static`

Role play:
- `chair.static`
- `sofa.deployed`
- `sign.post.town`
- `sign.post.town.roof`
- `phonebooth.static`
- `telephone.deployed`

Fun:
- `arcademachine`
- `xmas_tree.deployed`
- `drumkit.deployed.static`
- `piano.deployed.static`
- `paddlingpool`
- `abovegroundpool`
- `cardtable.static_config{a|b|c|d}`
- `slotmachine`
