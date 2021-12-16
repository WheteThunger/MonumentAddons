## Video Tutorial

[![Video Tutorial](https://img.youtube.com/vi/fkGpFBNs8_A/mqdefault.jpg)](https://www.youtube.com/watch?v=fkGpFBNs8_A)

## Features

Easily spawn permanent entities at monuments, which auto respawn after restarts and wipes.

- Setup is done in-game, no config needed
- Uses familiar command syntax, inspired by `spawn <entity>`
- Works on any map seed and accounts for terrain height
- Entities are indestructible, have no stability, free electricity, and cannot be picked up
- Supports vanilla monuments, custom monuments, train tunnels, underwater labs, and cargo ship
- Allows skinning spawned entities
- [Sign Artist](https://umod.org/plugins/sign-artist) integration allows persisting sign images
- [Entity Scale Manager](https://umod.org/plugins/entity-scale-manager) integration allows persisting entity scale

## Required plugins

- [Monument Finder](https://umod.org/plugins/monument-finder) -- Simply install. No configuration or permissions needed.

## Recommended compatible plugins

- [Custom Vending Setup](https://umod.org/plugins/custom-vending-setup) -- Allows customizing monument vending machines. Works with vending machines spawned by this plugin that use the `npcvendingmachine` or `shopkeeper_vm_invis` prefabs.

## Getting started

1. Install the plugin and grant the `monumentaddons.admin` permission to admins or moderators (not to normal players).
2. Go to any monument, such as a gas station.
3. Aim somewhere, such as a floor, wall or ceiling.
4. Run the `maspawn <entity>` command to spawn an entity of your choice. For example, `maspawn modularcarlift.static`.

This does several things.
- It spawns the entity where you are aiming.
- It spawns the entity at all other identical monuments (for example, at every gas station) using the correct relative position and rotation for those monuments.
- It saves this information in the plugin data files, so that the entity can be respawned when the plugin is reloaded, when the server is restarted, or when the server is wiped, even if using a new map seed (don't worry, this works well as long as the monuments don't significantly change between Rust updates).

## Permissions

- `monumentaddons.admin` -- Allows all commands.

## Commands

- `maspawn <entity>` -- Spawns an entity where you are aiming, using the entity short prefab name.
  - You must be at a monument, as determined by Monument Finder.
  - Works just like the native `spawn` command, so if the entity name isn't specific enough, it will print all matching entity names.
  - Also spawns the entity at other matching monuments (e.g., if at a gas station, will spawn at all gas stations).
    - A monument is considered a match if it has the same short prefab name or the same alias as the monument you are aiming at. The Monument Finder plugin will assign aliases for primarily underground tunnels. For example, `station-sn-0` and `station-we-0` will both use the `TrainStation` alias, allowing all train stations to have the same entities.
  - Saves the entity info to the plugin data file so that reloading the plugin (or restarting the server) will respawn the entity.

The following commands only work on entities spawned by this plugin. The effect of these commands automatically applies to all copies of the entity at matching monuments, and also applies updates the data files.

- `makill` -- Kills the entity that you are aiming at.
- `maskin <skin id>` -- Updates the skin of the entity you are aiming at.
- `masetid <id>` -- Updates the RC identifier of the CCTV camera you are aiming at.
  - You must be aiming at the base of the camera.
  - Note: Each CCTV's RC identifier will have a numeric suffix like `1`, `2`, `3` and so on. This is done because each CCTV must have a unique identifier.
- `masetdir` -- Updates the direction of the CCTV you are aiming at, so that it points toward you.
  - You must be aiming at the base of the camera.

### Profiles

Profiles allow you to organize entities into groups. Each profile can be independently enabled or reloaded. Each profile uses a separate data file, so you can easily share profiles with others.

- `maprofile` -- Prints help info about all profile commands.
- `maprofile list` -- Lists all profiles in the `oxide/data/MonumentAddons/` directory.
- `maprofile describe <name>` -- Describes all entities within the specified profile.
- `maprofile enable <name>` -- Enables the specified profile. Enabling a profile spawns all of the profile's entities, and marks the profile as enabled in the data file so it will automatically load when the the plugin does.
- `maprofile disable <name>` -- Disables the specified profile. Disabling a profile despawns all of the profile's entities, and marks the profile as disabled in the data file so it won't automatically load when the plugin does.
- `maprofile reload <name>` -- Reloads the specified profile from disk. This despawns all the profile's entities, re-reads the data file, then respawns all the profile's entities. This is useful if you downloaded a new version of a profile or if you made manual edits to the data file.
- `maprofile select <name>` -- Selects and enables the specified profile. Running `maspawn <entity>` will save entities to the currently selected profile. Each player can have a separate profile selected, allowing multiple players to work on different profiles at the same time.
- `maprofile create <name>` -- Creats a new profile, enables it and selects it.
- `maprofile rename <name> <new name>` -- Renames the specified profile. The plugin cannot delete the data file for the old name, so you will have to delete it yourself at `oxide/data/MonumentAddons/{name}.json`.
- `maprofile clear <name>` -- Removes all entities from the specified profile.
- `maprofile moveto <name>` -- Moves the entity you are looking at to the specified profile.
- `maprofile install <url>` -- Installs a profile from a URL.

## How underwater labs work

Since underwater labs are procedurally generated, this plugin does not spawn entities relative to the monuments themselves. Instead, entities are spawned relative to specific modules. For example, if you spawn an entity in a moonpool module, the entity will also be spawned at all moonpool modules in the same lab and other labs.

Note that some modules have multiple possible vanilla configurations, so multiple instances of the same module might have slightly different placement of vanilla objects. This plugin does not currently differentiate between these module-specific configurations, so after spawning something into a lab module, be sure to inspect other instances of that module to make sure the entity placement makes sense for all of them.

## Example profiles

Several example profiles are included below. Run the corresponding command snippet to install the profile.

Want to showcase a profile you created? Fork the repository on [GitHub](https://github.com/WheteThunger/MonumentAddons), commit the changes, and submit a pull request!

#### TrainStationCCTV

Adds 6 CCTVs and one computer station to each underground Train Station.

```
maprofile install https://github.com/WheteThunger/MonumentAddons/blob/master/Profiles/TrainStationCCTV.json?raw=true
```

#### MonumentLifts

Adds car lifts to gas station and supermarket.

```
maprofile install https://github.com/WheteThunger/MonumentAddons/blob/master/Profiles/MonumentLifts.json?raw=true
```

## Instructions for sharing profiles

### How to share a profile via a website

Sharing a profile via a website allows recipients to install the profile with a single command. Here's how it works:

1. Profile author locates the `oxide/data/MonumentAddons/PROFILE_NAME.json` file, where `PROFILE_NAME` is replaced with the profile's actual name.
2. Profile author uploads the file to a website of their choice and obtains a *raw* download link. For example, if hosting on pastebin or GitHub, click the "raw" button before copying the URL.
3. Recipient runs a command like `maprofile install <url>` using the URL provided by the profile author.

Sharing a profile via a website will eventually have additional perks, including the ability to download updates to the profile automatically, while preserving customizations you have made.

### How to share a profile via direct file transfer

If you don't have a website to host a profile, you can simply send the profile data file to someone else over Discord or wherever. Here's how it works:

1. Profile author locates the `oxide/data/MonumentAddons/PROFILE_NAME.json` file, where `PROFILE_NAME` is replaced with the profile's actual name.
2. Profile author sends the file to the recipient.
3. Recipient downloads the file and places it in the same location on their server.
4. Recipient runs the command: `maprofile enable <name>`. Alternatively, if the recipient already had a version of the profile installed and enabled, they would run `maprofile reload <name>`.

## Instructions for plugin integrations

### Sign Artist integration

**Please donate if you use this feature for sponsorship revenue.**

Use the following steps to set persistent images for signs or photo frames. Requires the [Sign Artist](https://umod.org/plugins/sign-artist) plugin to be installed with the appropriate permissions granted.

1. Spawn a sign with `maspawn sign.large`. You can also use other sign entities, photo frames, neon signs, or carvable pumpkins.
2. Use a Sign Artist command such as `sil`, `silt` or `sili` to apply an image to the sign.

That's all you need to do. This plugin detects when you use a Sign Artist command and automatically saves the corresponding image URL or item short name in the profile's data file for that particular sign. When the plugin reloads, Sign Artist is called to reapply that image. Any change to a sign will also automatically propagate to all copies of that sign at other monuments.

Notes:
- Only players with the `monumentaddons.admin` permission can edit signs that are managed by this plugin, so you don't have to worry about random players vandalizing the signs.
- Due to a client bug with parenting, having multiple signs on cargo ship will cause them to all display the same image.

### Entity Scale Manager integration

Use the following steps to resize entities. Requires the [Entity Scale Manager](https://umod.org/plugins/entity-scale-manager) plugin to be installed with the appropriate permissions granted.

1. Spawn any entity with `maspawn <entity>`.
2. Resize the entity with `scale <size>`.

That's all you need to do. This plugin detects when an entity is resized and automatically applies that scale to copies of the entity as matching monuments, and saves the scale in the profile's data file. When the plugin reloads, Entity Scale manager is called to reapply that scale.

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
- The `big_wheel` entity does not have a collider so you cannot currently use `makill` on it. If you need to reposition it, you will have to remove it from the profile's data file and reload the profile.
- If a betting terminal spawns more than 3 seconds after the wheel, the wheel won't know about it. This means that if you add more betting terminals after spawning the wheel, you will likely have to reload the profile to respawn the wheel so that it can find all the betting terminals.

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
- If you spawn an entity that is either invisible or doesn't have a collider, and you want to remove it, you can remove the entity from the profile's data file and then reload the profile.

## Uninstallation

Simply remove the plugin. Spawned entities are automatically removed when the plugin unloads.

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.MonumentFinderNotLoaded": "Error: Monument Finder is not loaded.",
  "Error.NoMonuments": "Error: No monuments found.",
  "Error.NotAtMonument": "Error: Not at a monument. Nearest is <color=#fd4>{0}</color> with distance <color=#fd4>{1}</color>",
  "Error.NoSuitableEntityFound": "Error: No suitable entity found.",
  "Error.EntityNotEligible": "Error: That entity is not managed by Monument Addons.",
  "Spawn.Error.Syntax": "Syntax: <color=#fd4>maspawn <entity></color>",
  "Spawn.Error.NoProfileSelected": "Error: No profile selected. Run <color=#fd4>maprofile help</color> for help.",
  "Spawn.Error.EntityNotFound": "Error: Entity <color=#fd4>{0}</color> not found.",
  "Spawn.Error.MultipleMatches": "Multiple matches:\n",
  "Spawn.Error.NoTarget": "Error: No valid spawn position found.",
  "Spawn.Success2": "Spawned entity at <color=#fd4>{0}</color> matching monument(s) and saved to <color=#fd4>{1}</color> profile for monument <color=#fd4>{2}</color>.",
  "Kill.Success2": "Killed entity at <color=#fd4>{0}</color> matching monument(s) and removed from profile <color=#fd4>{1}</color>.",
  "Skin.Get": "Skin ID: <color=#fd4>{0}</color>. Run <color=#fd4>{1} <skin id></color> to change it.",
  "Skin.Set.Syntax": "Syntax: <color=#fd4>{0} <skin id></color>",
  "Skin.Set.Success2": "Updated skin ID to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.",
  "Skin.Error.Redirect": "Error: Skin <color=#fd4>{0}</color> is a redirect skin and cannot be set directly. Instead, spawn the entity as <color=#fd4>{1}</color>.",
  "CCTV.SetId.Error.Syntax": "Syntax: <color=#fd4>{0} <id></color>",
  "CCTV.SetId.Success2": "Updated CCTV id to <color=#fd4>{0}</color> at <color=#fd4>{1}</color> matching monument(s) and saved to profile <color=#fd4>{2}</color>.",
  "CCTV.SetDirection.Success2": "Updated CCTV direction at <color=#fd4>{0}</color> matching monument(s) and saved to profile <color=#fd4>{1}</color>.",
  "Profile.List.Empty": "You have no profiles. Create one with <color=#fd4>maprofile create <name></maprofile>",
  "Profile.List.Header": "<size=18>Monument Addons Profiles</size>",
  "Profile.List.Item.Enabled": "<color=#fd4>{0}</color> - <color=#6e6>ENABLED</color>",
  "Profile.List.Item.Disabled": "<color=#fd4>{0}</color> - <color=#f44>DISABLED</color>",
  "Profile.List.Item.Selected": "<color=#fd4>{0}</color> - <color=#6cf>SELECTED</color>",
  "Profile.Install.Syntax": "Syntax: <color=#fd4>maprofile install <url></color>",
  "Profile.Url.Invalid": "Invalid URL: {0}",
  "Profile.Error.AlreadyExists.NotEmpty": "Error: Profile <color=#fd4>{0}</color> already exists and is not empty.",
  "Profile.Install.Success": "Successfully installed and <color=#6e6>ENABLED</color> profile <color=#fd4>{0}</color>",
  "Profile.Install.Error": "Error installing profile from url {0}. See the error logs for more details.",
  "Profile.Describe.Syntax": "Syntax: <color=#fd4>maprofile describe <name></color>",
  "Profile.Error.NotFound": "Error: Profile <color=#fd4>{0}</color> not found.",
  "Profile.Empty": "Profile <color=#fd4>{0}</color> is empty.",
  "Profile.Describe.Header": "Describing profile <color=#fd4>{0}</color>.",
  "Profile.Describe.Item": "<color=#fd4>{0}</color> x{1} @ {2}",
  "Profile.Select.Syntax": "Syntax: <color=#fd4>maprofile select <name></color>",
  "Profile.Select.Success": "Successfully <color=#6cf>SELECTED</color> and <color=#6e6>ENABLED</color> profile <color=#fd4>{0}</color>.",
  "Profile.Enable.Syntax": "Syntax: <color=#fd4>maprofile enable <name></color>",
  "Profile.AlreadyEnabled": "Profile <color=#fd4>{0}</color> is already <color=#6e6>ENABLED</color>.",
  "Profile.Enable.Success": "Profile <color=#fd4>{0}</color> is now: <color=#6e6>ENABLED</color>.",
  "Profile.Disable.Syntax": "Syntax: <color=#fd4>maprofile disable <name></color>",
  "Profile.AlreadyDisabled": "Profile <color=#fd4>{0}</color> is already <color=#f44>DISABLED</color>.",
  "Profile.Disable.Success": "Profile <color=#fd4>{0}</color> is now: <color=#f44>DISABLED</color>.",
  "Profile.Reload.Syntax": "Syntax: <color=#fd4>maprofile reload <name></color>",
  "Profile.Reload.Success": "Reloaded profile <color=#fd4>{0}</color>.",
  "Profile.Create.Syntax": "Syntax: <color=#fd4>maprofile create <name></color>",
  "Profile.Error.AlreadyExists": "Error: Profile <color=#fd4>{0}</color> already exists.",
  "Profile.Create.Success": "Successfully created and <color=#6cf>SELECTED</color> profile <color=#fd4>{0}</color>.",
  "Profile.Rename.Syntax": "Syntax: <color=#fd4>maprofile rename <old name> <new name></color>",
  "Profile.Rename.Success": "Successfully renamed profile <color=#fd4>{0}</color> to <color=#fd4>{1}</color>. You must manually delete the old <color=#fd4>{0}</color> data file.",
  "Profile.MoveTo.Syntax": "Syntax: <color=#fd4>maprofile moveto <name></color>",
  "Profile.MoveTo.AlreadyPresent": "Error: <color=#fd4>{0}</color> is already part of profile <color=#fd4>{1}</color>.",
  "Profile.MoveTo.Success": "Successfully moved <color=#fd4>{0}</color> from profile <color=#fd4>{1}</color> to <color=#fd4>{2}</color>.",
  "Profile.Help.Header": "<size=18>Monument Addons Profile Commands</size>",
  "Profile.Help.List": "<color=#fd4>maprofile list</color> - List all profiles",
  "Profile.Help.Describe": "<color=#fd4>maprofile describe <name></color> - Describe profile contents",
  "Profile.Help.Enable": "<color=#fd4>maprofile enable <name></color> - Enable a profile",
  "Profile.Help.Disable": "<color=#fd4>maprofile disable <name></color> - Disable a profile",
  "Profile.Help.Reload": "<color=#fd4>maprofile reload <name></color> - Reload a profile from disk",
  "Profile.Help.Select": "<color=#fd4>maprofile select <name></color> - Select a profile",
  "Profile.Help.Create": "<color=#fd4>maprofile create <name></color> - Create a new profile",
  "Profile.Help.Rename": "<color=#fd4>maprofile rename <name> <new name></color> - Rename a profile",
  "Profile.Help.MoveTo2": "<color=#fd4>maprofile moveto <name></color> - Move an entity to a profile",
  "Profile.Help.Install": "<color=#fd4>maprofile install <url></color> - Install a profile from a URL"
}
```
