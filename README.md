## Features

- Allows privileged players to spawn entities at monuments
- Automatically saves spawned entities and restores them on plugin reload
- Saves positions relative to monuments, allowing them to work across multiple map seeds
- Prevents all damage to spawned entities
- Provides free power to spawned electrical entities

### Example entities

- `recycler_static`
- `repairbench_static`
- `workbench{1|2}.static`
- `workbench{1|2|3}.deployed`
- `chair.static`
- `bbq.static`
- `sentry.{bandit|scientist}`
- `watchtower`

## Commands

- `maspawn <entity>` -- Spawn an entity where you are aiming. Works just like the native `spawn` command.
  - This saves the entity to the plugin data file so that reloading the plugin will respawn the entity.
  - Tip: When figuring out the placement for a deployable entity, pull out the corresponding item, rotate it 180 degrees, then use it as a guide before running the command.
- `makill` -- Kills the spawned entity that the player is looking at. Only works on entities that were spawned by this plugin.
  - This removes the entity from the plugin data file so that it won't respawn later.
  - Note: If you kill an entity with the native `kill` command, it will respawn on plugin reload.
  - Tip: Bind this to a key to save time while setting up entities.

## Permissions

- `monumentaddons.admin` -- Required to use the `maspawn` and `makill` commands.

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.NoMonument": "Error: Not at a monument",
  "Spawn.Error.Syntax": "Syntax: <color=yellow>maspawn <entity></color>",
  "Spawn.Error.EntityNotFound": "Error: Entity {0} not found.",
  "Spawn.Error.MultipleMatches": "Multiple matches:\n",
  "Spawn.Error.NoTarget": "Error: No valid spawn position found.",
  "Spawn.Error.Failed": "Error: Failed to spawn enttiy.",
  "Spawn.Success": "Spawned entity and saved to data file for monument '{0}'.",
  "Kill.Error.EntityNotFound": "Error: No entity found.",
  "Kill.Error.NotEligible": "Error: That entity is not controlled by Monument Addons.",
  "Kill.Error.NoPositionMatch": "Error: No saved entity found for monument '{0}' at position {1}.",
  "Kill.Success": "Entity killed and removed from data file."
}
```

## Known issues

- Some deployable entities can be picked up, such as mixing tables

## Uninstallation

Simply remove the plugin. Spawned entities are automatically killed when the plugin unloads.

If you reinstall the plugin, you may want to delete the data file beforehand or entities will spawn in the positions you previously configured.
