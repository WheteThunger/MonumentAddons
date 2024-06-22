## Video Tutorial

[![Video Tutorial](https://img.youtube.com/vi/fkGpFBNs8_A/maxresdefault.jpg)](https://www.youtube.com/watch?v=fkGpFBNs8_A)

## Features

Easily spawn permanent entities at monuments, which auto respawn after restarts and wipes.

- Setup is done in-game, no config needed
- Uses familiar command syntax, inspired by `spawn <entity>`
- Works on any map seed and accounts for terrain height
- Entities are indestructible, have no stability, free electricity, and cannot be picked up
- Supports vanilla monuments, custom monuments, train tunnels, underwater labs, and cargo ship
- Allows defining any entity prefab as a monument, in order to attach addons to it
- Allows skinning spawned entities
- (Advanced) Allows building monument puzzles
- (Advanced) Allows placing spawn points for loot containers, key cards, vehicles, and more
- [Sign Artist](https://umod.org/plugins/sign-artist) integration allows persisting sign images
- [Entity Scale Manager](https://umod.org/plugins/entity-scale-manager) integration allows persisting entity scale
- [Telekinesis](https://umod.org/plugins/telekinesis) integration allows easily moving and rotating entities

## Required plugins

- [Monument Finder](https://umod.org/plugins/monument-finder) -- Simply install. No configuration or permissions needed except for custom monuments.

## Recommended compatible plugins

- [Telekinesis](https://umod.org/plugins/telekinesis) -- Allows moving and rotating entities in-place. Very useful for precisely placing entities. Monument Addons integrates with it to automatically save updated positions.
- [Custom Vending Setup](https://umod.org/plugins/custom-vending-setup) -- Allows customizing monument vending machines. Works with vending machines spawned by this plugin that use the `npcvendingmachine` or `shopkeeper_vm_invis` prefabs.

## What kinds of things can I do with this plugin?

- Add signs to personalize your server or advertise sponsors
- Add airwolf and boat vendors
- Add vending machines
- Add car lifts, recyclers, research tables, workbenches, repair benches
- Add BBQs, furnaces, refineries, fireplaces, hobo barrels
- Add CCTVs and computer stations
- Add instruments, swimming pools, arcade machines
- Add gambling wheel, slot machines, poker tables
- Add drone marketplaces
- Add CH47 drop zones
- Add barricades and walls to block off sections of monuments
- Periodically spawn loot containers, key cards, vehicles, and more
- Dynamically change monuments throughout a wipe by enabling/disabling profiles via other plugins

List of spawnable entities: [https://github.com/OrangeWulf/Rust-Docs/blob/master/Entities.md](https://github.com/OrangeWulf/Rust-Docs/blob/master/Entities.md)

## Permissions

- `monumentaddons.admin` -- Allows all commands. Grant this to admins or moderators, not to normal players.

## Getting started

### Installing existing profiles

Let's face it, you are probably planning to use this plugin the same way as many other server owners. That means there may already be profiles that you can install to do just that.

Several example profiles are included below. Run the corresponding command snippet to install each profile.

- `mainstall BarnAirwolf` -- Adds an Air Wolf vendor to Large Barn and Ranch.
- `mainstall CargoShipCCTV` -- Adds 7 CCTVs and one computer station to cargo ship (same as the Cargo Ship CCTV plugin).
- `mainstall FishingVillageAirwolf` -- Adds an Air Wolf vendor to Large Fishing Village and to one of the small Fishing Villages.
- `mainstall MonumentCooking` -- Adds a cooking static (BBQ / camp fire / hobo barrel) to safe zones and low-level named monuments that lack them.
- `mainstall MonumentLifts` -- Adds car lifts to gas station and supermarket (same as the MonumentLifts plugin).
- `mainstall MonumentsRecycler` -- Adds recyclers to Cargo Ship, Oilrigs, Dome and Fishing Villages (same as the MonumentsRecycler plugin).
- `mainstall OilRigSharks` -- Adds one shark to small rig and two sharks to lage rig.
- `mainstall OutpostAirwolf` -- Adds an Air Wolf vendor to Outpost, with some ladders to allow access.
- `mainstall SafeZoneRecyclers` -- Adds a recycler to Fishing Villages, Large Barn, and Ranch (different locations than MonumentsRecycler for compatibility).
- `mainstall TrainStationCCTV` -- Adds 6 CCTVs and one computer station to each underground Train Station.

These example profiles are installed from https://github.com/WheteThunger/MonumentAddons/blob/master/Profiles/.
Don't see what you're looking for? Want to showcase a profile you created? Fork the repository on [GitHub](https://github.com/WheteThunger/MonumentAddons), commit the changes, and submit a pull request!

### Spawning static entities

Follow these steps to spawn static entities.

1. Go to any monument, such as a gas station.
2. Aim somewhere, such as a floor, wall or ceiling.
3. Run the command `maspawn <entity>` to spawn an entity of your choice. For example, `maspawn modularcarlift.static`. Alternatively, if you are holding a deployable item, you can simply run `maspawn` to spawn the corresponding entity.

How this works:
- It spawns the entity where you are aiming.
- It spawns the entity at all other identical monuments (for example, at every gas station) using the correct relative position and rotation for those monuments.
- It saves this information in the plugin data files, so that the entity can be respawned when the plugin is reloaded, when the server is restarted, or when the server is wiped, even if using a new map seed (don't worry, this works well as long as the monuments don't significantly change between Rust updates).

### Creating puzzles

Follow these steps to create an example puzzle.

1. Go to any monument, such as a gas station.
2. Aim at the floor, then run the command `maspawn generator.static`. This will be the root of your puzzle, automatically resetting it on a schedule. It's recommended to place the generator near the center of the puzzle so it can evenly detect nearby players for the purpose of delaying the puzzle from resetting.
3. Aim at a wall, outside the puzzle room, then run the command `maspawn fusebox`.
4. Aim at the floor in a doorway, then run the command `maspawn security.blue`. Reposition the door with Telekinesis if needed.
5. Aim at the wall next to the front side of the doorway, then run the commands `maspawn cardreader` and `maspawn doormanipulator`.
6. Aim at the wall next to the back side of the doorway, then run the commands `maspawn pressbutton`, and `maspawn orswitch`.
7. Aim at the card reader, then run the command `macardlevel 2` to make it a blue card reader.
8. Equip a wire tool, then run the command `mawire`.
9. Connect `generator.static` -> `fusebox` -> `cardreader` -> `orswitch` -> `doormanipulator`.
10. Connect `pressbutton` -> `orswitch`.

### Creating spawn points

Follow these steps to create example spawn points.

1. Go to any monument, such as a gas station.
2. Aim somewhere on the ground.
3. Run the command `maspawngroup create MyFirstSpawnGroup` to create a spawn group **and** a spawn point.
4. Run the command `maspawngroup add radtown/crate_normal 30`.
5. Run the command `maspawngroup add radtown/crate_normal_2 70`.
6. Run the command `maspawngroup add crate_basic 100`.
7. Aim somewhere else on the ground, and run the command `maspawnpoint create MyFirstSpawnGroup` to create a second spawn point in the spawn group you created earlier.
8. Aim at the 2nd spawn point, and run the command `maspawnpoint set RandomRadius 1.5`.
9. Aim at either spawn point, and run the command `maspawngroup set MaxPopulation 2`.
10. Aim at either spawn point, and run the command `maspawngroup respawn`. Run this as many times as you want to see the loot crates respawn.

## Commands

- `mahelp` -- Prints help information about available commands.
- `maspawn <entity>` -- Spawns an entity where you are aiming, using the entity prefab name.
  - If you are holding a deployable item, you can simply run `maspawn` without specifying the entity name.
  - You must be at a monument, as determined by Monument Finder.
  - Works like the native `spawn` command, so if the entity name isn't specific enough, it will print all matching entity names.
  - Also spawns the entity at other matching monuments (e.g., if at a gas station, will spawn at all gas stations).
    - A monument is considered a match if it has the same short prefab name or the same alias as the monument you are aiming at. The Monument Finder plugin will assign aliases for primarily underground tunnels. For example, `station-sn-0` and `station-we-0` will both use the `TrainStation` alias, allowing all train stations to have the same entities.
  - Saves the entity info to the plugin data file so that reloading the plugin (or restarting the server) will respawn the entity.
- `maprefab <prefab>` -- Creates an instance of a non-entity prefab. Note: This is **very** limited. Only prefabs with the `assets/bundled/prefabs/modding` path are supported, and the prefab instances are not networked to clients (because the game does not offer that capability) so they will be invisible, despite having real effects on the server side. This command is intended primarily for placing CH47 drop zones (`maprefab dropzone`), but it can also be used to place loot and NPC spawners that custom maps tend to use, although you will have much greater control of spawners when using the spawn point capabilities of the plugin instead.
- `mapaste <file>` -- Pastes a building from the CopyPaste plugin, using the specified file name.
- `maundo` -- Undo a recent `makill` action.
- `mashow <optional_profile_name> <optional_duration_in_seconds>` -- Shows debug information about nearby entities spawned by this plugin, for the specified duration. Defaults to 60 seconds.
  - Debug information is also automatically displayed for at least 60 seconds when using other commands.
  - When specifying a profile name, entities belonging to other profiles will have gray text.

The following commands only work on objects managed by this plugin. The effect of these commands automatically applies to all copies of the object at matching monuments, and also updates the data files.

- `makill` -- Deletes the entity or spawn point that you are aiming at.
  - For addons that do not have colliders, such as spawn points, the plugin will attempt to find a nearby addon within 2 meters of the surface you are looking at. If this does not work, you have to remove the addon from the profile data file manually.
- `masave` -- Saves the current position and rotation of the entity you are aiming at. This is useful if you moved the entity with a plugin such as Edit Tool or Uber Tool. This is not necessary if you are repositioning entities with [Telekinesis](https://umod.org/plugins/telekinesis) since that will be automatically detected.
  - Also saves the building grade if looking at a foundation, wall, floor, etc.
- `maflag <flag>` -- (Advanced) Toggles a flag between enabled/disabled/unspecified on the entity you are aiming at. When running this command without specifying a flag, the current/enabled/disabled flags will be printed. Allowed flags as of this writing: `Placeholder`, `On`, `OnFire`, `Open`, `Locked`, `Debugging`, `Disabled`, `Reserved1`, `Reserved2`, `Reserved3`, `Reserved4`, `Reserved5`, `Broken`, `Busy`, `Reserved6`, `Reserved7`, `Reserved8`, `Reserved9`, `Reserved10`, `Reserved11`, `InUse`, `Reserved12`, `Reserved13`, `Unused23`, `Protected`, `Transferring`.
  - This plugin forces certain flags in some cases, meaning that enabling or disabling flags via this command may not always work as expected. If you discover an issue with the flags the plugin is overriding, open a support thread to discuss the use case.
  - Even if you enable or disable a flag for a given entity, the game (or another plugin) may toggle the flag at any time (e.g., a recycler will toggle the `On` flag whenever it turns on/off). Overriding a flag with this plugin only ensures that the flag is enabled or disabled when the entity is spawned and when the profile or plugin reloads.
  - **Don't ask me what each flag does.** These flags are defined by the game itself, not a concept introduced by this plugin. The function of each flag depends on the entity it's applied to. Most flags will have no effect on most entities. Some example use cases are described below, but please understand that it's not feasible to describe what each flag does in this documentation. To really understand what every flag does, you must read the game assemblies. You should only override flags when recommended for a specific use case by someone who has read the game assemblies. Experiment with flags at your own risk.
    - `On` -- Determines some functional and cosmetic effects for various entities, such as furnaces, recyclers, some lights.
    - `Locked` -- Determines whether a door or storage container can be opened.
    - `Busy` -- Determines whether the entity can be interacted with.
    - `Disabled` -- Determines whether the entity is visible.
    - `Reserved8` -- Used by electrical entities to denote whether there is sufficient electricity.
    - `Reserved9` -- Used by recyclers to determine recycle efficiency (by default, this flag is enabled on recyclers only in safe zones). You can forcibly enable or disable this flag to achieved the desired recycler efficiency (40% or 60%).
- `maskin <skin id>` -- Updates the skin of the entity you are aiming at.
- `masetid <id>` -- Updates the RC identifier of the CCTV camera you are aiming at.
  - Note: Each CCTV's RC identifier will have a numeric suffix like `1`, `2`, `3` and so on. This is done because some monuments may be duplicated, and each CCTV must have a unique identifier.
- `masetdir` -- Updates the direction of the CCTV you are aiming at, so that it points toward you.
- `maskull <name>` -- Sets the display name of the skull trophy you are aiming at.
- `matrophy` -- Mounts a copy of your currently held head bag to the hunting trophy you are aiming at.

### Puzzles

- `mawire <optional color>` -- Temporarily enhances your currently held wire tool, allowing you to connect electrical entities spawned via `maspawn`. Allowed colors: `Default`, `Red`, `Green`, `Blue`, `Yellow`, `Pink`, `Purple`, `Orange`, `White`, `LightBlue`, `Invisible`. Note: When using the `Invisible` color, you can only directly connect entity inputs and outputs, not place intermediate wire points.
- `macardlevel <1-3>` (1 = green, 2 = blue, 3 = red) -- Sets the access level of the card reader you are aiming at. For example, `macardlevel 2` will make the card reader visually blue and require a blue key card.
- `mapuzzle reset` -- Resets the puzzle connected to the entity you are looking at. For example, when looking at a static generator (i.e., `generator.static`) or an entity connected directly or indirectly to a static generator.
- `mapuzzle set <option> <value>` -- Sets a property of the puzzle root entity you are looking at. This applies only to static generators (i.e., `generator.static`).
  - `PlayersBlockReset`: true/false -- While `true`, the puzzle will not make progress toward the next reset while any players are within the distance `PlayerDetectionRadius`.
  - `PlayerDetectionRadius`: number -- The distance in which players can block the puzzle from making progress toward its next reset.
  - `SecondsBetweenReset`: number -- The number of seconds between puzzle resets. Note: Reset progress does not advance while `PlayersBlockReset` is enabled and players are nearby.
- `mapuzzle add <group_name>` -- Associates a spawn group with the puzzle root entity you are aiming at (i.e., `generator.static`). Whenever a puzzle resets, associated spawn groups will despawn and respawn entities, allowing you to synchronize loot, NPCs and puzzle doors. To associate a spawn group, it must be created by the plugin, stored under the same profile, and be at the same monument.
- `mapuzzle remove <group_name>` -- Disassociates a spawn group with the puzzle root entity you are aiming at (i.e., `generator.static`).

### Spawn points and spawn groups

- `mashowvanilla` -- For educational purposes, this shows debug information for 60 seconds about vanilla spawn points at the monument you are aiming at. If you are aiming at an entity (e.g., a junk pile, dwelling, or cargo ship), this will instead show spawn points that are parented to that entity.

#### Spawn groups

- `maspawngroup create <name>` -- Creates a spawn group **and** a spawn point where you are looking.
- `maspawngroup set <option> <value>` -- Sets a property of the spawn group you are looking at.
  - `Name`: string -- This name must be unique for the given profile + monument. This name can be used to create additional spawn points for this spawn group using `maspawnpoint create <group_name>`.
  - `MaxPopulation`: number -- The maximum number of entities that can be spawned across all spawn points in this spawn group.
  - `RespawnDelayMin`: number -- The minimum time in seconds to wait between spawning entities.
  - `RespawnDelayMax`: number -- The maximum time in seconds to wait between spawning entities. Set to `0` to disable automated respawns, which is useful if you are associating the spawn group with a puzzle.
  - `SpawnPerTickMin`: number -- The minimum number of entities to try to spawn in a batch.
  - `SpawnPerTickMax`: number -- The maximum number of entities to try to spawn in a batch.
  - `InitialSpawn`: true/false -- While `true`, the spawn group will spawn entities as soon as the spawn group is created (e.g., when the profile loads, when the plugin loads, or when the server reboots). While `false`, the spawn group will not spawn any entities initially, but will still spawn them later according to the defined schedule.
  - `PreventDuplicates`: true/false -- While `true`, only one of each entity prefab can be present across all spawn points in the spawn group. Vanilla Rust uses this property for spawning modules at desert military bases.
  - `PauseScheduleWhileFull`: true/false -- While `true`, the next spawn will not be scheduled until the spawn group is below its max population. For instance, if the spawn group is at 2/2 population, and respawn delay is set to 30 minutes, once the population reaches 1/2, the 30 minute respawn timer will be started. In vanilla Rust, this feature does not exist, meaning the timer is always going, allowing for situations where loot spawns shortly after loot is taken.
  - `RespawnWhenNearestPuzzleResets`: true/false -- While `true`, the spawn group will associate with the closest nearby vanilla puzzle. When that puzzle resets, the spawn group will despawn all entities and run one spawn tick (same behavior as when you run `maspawngroup respawn`). This is useful if you want to add spawn points for extra loot at vanilla puzzles. Note: This can only associate a custom spawn group with a **vanilla** puzzle. If you want to associate a custom spawn group with a **custom** puzzle, you must associate directly (e.g., `mapuzzle add <group_name>` while looking at the puzzle root entity).
- `maspawngroup add <entity> <weight>` -- Adds the specified entity prefab to the spawn group you are looking at.
- `maspawngroup remove <entity>` -- Removes the specified entity prefab from the spawn group you are looking at.
- `maspawngroup spawn` -- Runs one spawn tick for the spawn group. For example, if you have set `SpawnPerTickMin` to 1 and `SpawnPerTickMax` to 2, running this command will spawn 1-2 entities, as long as there are available spawn points and sufficient population headroom.
- `maspawngroup respawn` -- Despawns all entities across the spawn group and runs one spawn tick.

Note: `masg` can be used in place of `maspawngroup`.

#### Spawn points

- `maspawnpoint create <group_name>` -- Creates a spawn point where you are looking, for the specified spawn group. The spawn group must be in your selected profile and be at the same monument.
- `maspawnpoint set <option> <value>` -- Sets a property of the spawn group you are looking at.
  - `Exclusive`: true/false -- While `true`, only one entity can be spawned at this spawn point at a time.
  - `DropToGround`: true/false -- While `true`, entities will be spawned on the nearest flat surface below the spawn point.
  - `CheckSpace`: true/false -- While `true`, entities can only spawn at this spawn point when there is sufficient space. This option is recommended for vehicle spawn points.
  - `RandomRotation` : true/false -- While `true`, entities will spawn with random rotation at this spawn point, instead of following the rotation of the spawn point itself.
  - `RandomRadius`: number -- This number determines how far away entities can spawn from this spawn point. The default is `0.0`.
  - `PlayerDetectionRadius`: number -- This number determines how far away players must be, in order for this spawn point to spawn an entity. By default, vanilla behavior checks within `2` meters for normal spawn points, or `RandomRadius` + `1` meter for radial spawn points. Setting this value to greater than `0` will override the vanilla behavior, allowing you to enlarge or shrink the detection radius. While a player is detected within the radius, the spawn point is considered unavailable, so another spawn point within the same spawn group may be selected for spawning an entity.

Note: `masp` can be used in place of `maspawnpoint`.

### Profiles

Profiles allow you to organize entities into groups. Each profile can be independently enabled or reloaded. Each profile uses a separate data file, so you can easily share profiles with others.

- `maprofile` -- Prints help info about all profile commands.
- `maprofile list` -- Lists all profiles in the `oxide/data/MonumentAddons/` directory.
- `maprofile describe <name>` -- Describes all addons within the specified profile.
- `maprofile enable <name>` -- Enables the specified profile. Enabling a profile spawns all of the profile's addons, and marks the profile as enabled in the data file so it will automatically load when the the plugin does.
- `maprofile disable <name>` -- Disables the specified profile. Disabling a profile despawns all of the profile's addons, and marks the profile as disabled in the data file so it won't automatically load when the plugin does.
- `maprofile reload <name>` -- Reloads the specified profile from disk. This despawns all the profile's addons, re-reads the data file, then respawns all the profile's addons. This is useful if you downloaded a new version of a profile or if you made manual edits to the data file.
- `maprofile select <name>` -- Selects and enables the specified profile. Running `maspawn <entity>` will save addons to the currently selected profile. Each player can have a separate profile selected, allowing multiple players to work on different profiles at the same time.
- `maprofile create <name>` -- Creates a new profile, enables it and selects it.
- `maprofile rename <name> <new name>` -- Renames the specified profile.
- `maprofile clear <name>` -- Removes all addons from the specified profile.
- `maprofile delete <name>` -- Deletes the specified profile. The profile must first be empty or disabled.
- `maprofile moveto <name>` -- Moves the addon you are looking at to the specified profile.
- `maprofile install <url>` -- Installs a profile from a URL.
  - Abbreviated command: `mainstall <url>`.
  - You may replace the URL with simply the profile name if installing from [https://github.com/WheteThunger/MonumentAddons/tree/master/Profiles](https://github.com/WheteThunger/MonumentAddons/tree/master/Profiles). For example, `maprofile install OutpostAirwolf` or `mainstall OutpostAirwolf`.

## Configuration

```json
{
  "Debug display distance": 150.0,
  "Persist entities while the plugin is unloaded": false,
  "Dynamic monuments": {
    "Entity prefabs to consider as monuments": [
      "assets/content/vehicles/boats/cargoship/cargoshiptest.prefab"
    ]
  },
  "Deployable overrides": {
    "arcade.machine.chippy": "assets/bundled/prefabs/static/chippyarcademachine.static.prefab",
    "autoturret": "assets/content/props/sentry_scientists/sentry.bandit.static.prefab",
    "bbq": "assets/bundled/prefabs/static/bbq.static.prefab",
    "boombox": "assets/prefabs/voiceaudio/boombox/boombox.static.prefab",
    "box.repair.bench": "assets/bundled/prefabs/static/repairbench_static.prefab",
    "cctv.camera": "assets/prefabs/deployable/cctvcamera/cctv.static.prefab",
    "chair": "assets/bundled/prefabs/static/chair.static.prefab",
    "computerstation": "assets/prefabs/deployable/computerstation/computerstation.static.prefab",
    "connected.speaker": "assets/prefabs/voiceaudio/hornspeaker/connectedspeaker.deployed.static.prefab",
    "hobobarrel": "assets/bundled/prefabs/static/hobobarrel_static.prefab",
    "microphonestand": "assets/prefabs/voiceaudio/microphonestand/microphonestand.deployed.static.prefab",
    "modularcarlift": "assets/bundled/prefabs/static/modularcarlift.static.prefab",
    "research.table": "assets/bundled/prefabs/static/researchtable_static.prefab",
    "samsite": "assets/prefabs/npc/sam_site_turret/sam_static.prefab",
    "small.oil.refinery": "assets/bundled/prefabs/static/small_refinery_static.prefab",
    "telephone": "assets/bundled/prefabs/autospawn/phonebooth/phonebooth.static.prefab",
    "vending.machine": "assets/prefabs/deployable/vendingmachine/npcvendingmachine.prefab",
    "wall.frame.shopfront.metal": "assets/bundled/prefabs/static/wall.frame.shopfront.metal.static.prefab",
    "workbench1": "assets/bundled/prefabs/static/workbench1.static.prefab",
    "workbench2": "assets/bundled/prefabs/static/workbench2.static.prefab"
  },
  "Xmas tree decorations (item short names)": [
    "xmas.decoration.gingerbreadmen",
    "xmas.decoration.star",
    "xmas.decoration.tinsel",
    "xmas.decoration.candycanes",
    "xmas.decoration.pinecone",
    "xmas.decoration.lights"
  ]
}
```

- `Debug display distance` -- Determines how far away you can see debug information about entities (i.e., when using `mashow`).
- `Persist entities while the plugin is unloaded` (`true` or `false`) -- Determines whether entities spawned by `maspawn` will remain while the plugin is unloaded. Please carefully read and understand the documentation about this option before enabling it. Note: This option currently has no effect on Pastes, Spawn Groups or Custom Addons, meaning that those will always be despawned/respawned when the plugin reloads.
  - While `false` (default), when the plugin unloads, it will despawn all entities spawned via `maspawn`. When the plugin subsequently reloads, those entities will be respawned from scratch. This means, for entities that maintain state (such as player items temporarily residing in recyclers), that state will be lost whenever the plugin unloads. The most practical consequence of using this mode is that player items inside containers will be lost when a profile is reloaded, when the plugin is reloaded, or when the server reboots. Despite that limitation, `false` is the most simple and stable value for this option because it ensures consistent reproducibility across plugin reloads.
  - While `true`, when the plugin unloads, all entities spawned by via `maspawn` will remain, in order to preserve their state (e.g., items inside a recycler). When the plugin subsequently reloads, it will find the existing entities, reconcile how they differ from the enabled profiles, and despawn/respawn/reposition/modify them as needed. The plugin will try to avoid despawning/respawning an entity that is already present, in order to preserve the entity's state. Despite this sounding like the more obvious mode of the plugin, it is more complex and less stable than the default mode, and should therefore be enabled with caution.
- `Dynamic monuments`
  - `Entity prefabs to consider as monuments` -- Determines which entities are considered dynamic monuments. When an entity is considered a dynamic monument, you can define addons for it via `maspawn` and similar commands, and the plugin will ensure every instance of that entity has those addons attached. For example, Cargo Ship has been considered a dynamic monument since an early version of this plugin, but now you can define additional ones such as desert military base modules and road-side junk piles.
    - Note: Updating this configuration is only necessary if you want to use `maspawn` and similar commands to recognize the entity as a monument. If you want to install an external profile that defines addons for a dynamic monument (such as the CargoShipCCTV profile), it isn't necessary to update this configuration because the plugin will automatically determine that the entity is a dynamic monument by reading the profile. Additionally, if you install an external profile which defines addons for a given dynamic monument, `maspawn` and similar commands will automatically recognize that entity as a dynamic monument. 
- `Deployable overrides` -- Determines which entity will be spawned when using `maspawn` if you don't specify the entity name in the command. For example, while you are holding an auto turret, running `maspawn` will spawn the `sentry.bandit.static` prefab instead of the `autoturret_deployed` prefab.
- `Xmas tree decorations (item short names)` -- Determines which decorations will be automatically added to `xmas_tree.deployed` entities spawned via `maspawn`.

## Localization

## How underwater labs work

Since underwater labs are procedurally generated, this plugin does not spawn entities relative to the monuments themselves. Instead, entities are spawned relative to specific modules. For example, if you spawn an entity in a moonpool module, the entity will also be spawned at all moonpool modules in the same lab and other labs.

Note that some modules have multiple possible vanilla configurations, so multiple instances of the same module might have slightly different placement of vanilla objects. That happens because Rust spawns semi-random dwelling entities in them, which you can learn about via the `mashowvanilla` command. You can use that command to get an idea of where dwellings spawn, in order to avoid placing your addons at those locations. After spawning something into a lab module, it's also recommended to inspect other instances of that module to make sure the entity placement isn't overlapping a dwelling entity.

## Instructions for sharing profiles

### How to share a profile via a website

Sharing a profile via a website allows recipients to install the profile with a single command. Here's how it works:

1. Profile author locates the `oxide/data/MonumentAddons/PROFILE_NAME.json` file, where `PROFILE_NAME` is replaced with the profile's actual name.
2. Profile author uploads the file to a website of their choice and obtains a *raw* download link. For example, if hosting on pastebin or GitHub, click the "raw" button before copying the URL.
3. Recipient runs a command like `mainstall <url>` using the URL provided by the profile author.

Sharing a profile via a website will eventually have additional perks, including the ability to download updates to the profile automatically, while preserving customizations you have made.

### How to share a profile via direct file transfer

If you don't have a website to host a profile, you can simply send the profile data file to someone else over Discord or wherever. Here's how it works:

1. Profile author locates the `oxide/data/MonumentAddons/PROFILE_NAME.json` file, where `PROFILE_NAME` is replaced with the profile's actual name.
2. Profile author sends the file to the recipient.
3. Recipient downloads the file and places it in the same location on their server.
4. Recipient runs the command: `maprofile enable <name>`. Alternatively, if the recipient already had a version of the profile installed and enabled, they would run `maprofile reload <name>`.

## Instructions for plugin integrations

### Sign Artist integration

Use the following steps to set persistent images for signs or photo frames. Requires the [Sign Artist](https://umod.org/plugins/sign-artist) plugin to be installed with the appropriate permissions granted.

1. Spawn a sign with `maspawn sign.large`. You can also use other sign entities, photo frames, neon signs, or carvable pumpkins.
2. Use a Sign Artist command such as `sil`, `silt` or `sili` to apply an image to the sign.

That's all you need to do. This plugin detects when you use a Sign Artist command and automatically saves the corresponding image URL or item short name in the profile's data file for that particular sign. When the plugin reloads, Sign Artist is called to reapply that image. Any change to a sign will also automatically propagate to all copies of that sign at duplicate monuments.

Notes:
- Only players with the `monumentaddons.admin` permission can edit signs that are managed by this plugin, so you don't have to worry about random players vandalizing the signs.
- Due to a client bug with parenting, having multiple signs on a dynamic monument (such as Cargo Ship) will cause them to all display the same image.

### Entity Scale Manager integration

Use the following steps to resize entities. Requires the [Entity Scale Manager](https://umod.org/plugins/entity-scale-manager) plugin to be installed with the appropriate permissions granted.

1. Spawn any entity with `maspawn <entity>`.
2. Resize the entity with `scale <size>`.

That's all you need to do. This plugin detects when an entity is resized and automatically applies that scale to copies of the entity as matching monuments, and saves the scale in the profile's data file. When the plugin reloads, Entity Scale manager is called to reapply that scale.

## Instructions for specific addons

### Heli & boat vendors

Use the following steps to set up a heli vendor at a custom location.

1. Aim where you want to spawn the vendor and run `maspawn bandit_conversationalist`.
2. Aim where you want purchased helicopters to spawn and run `maspawn airwolfspawner`.

Use the following steps to set up a boat vendor at a custom location.

1. Aim where you want to spawn the vendor and run `maspawn boat_shopkeeper`.
2. Aim where you want purchased boats to spawn and run `maspawn boatspawner`.

Notes:
- The heli vendor and spawner must be within 40m of each other to work.
- The boat vendor and spawner must be within 20m of each other to work.
- The boat vendor will not have a vending machine. This can be improved upon request.

### CCTV cameras & computer stations

Use the following steps to set up CCTV cameras and computer stations.

1. Spawn a CCTV camera with `maspawn cctv.static`.
2. Update the camera's RC identifier with `masetid <id>` while looking at the camera.
3. Update the direction the camera is facing with `masetdir` while looking at the camera. This will cause the camera to face you, just like with deployable CCTV cameras.
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
- If a betting terminal spawns more than 3 seconds after the wheel, the wheel won't know about it. This means that if you add more betting terminals after spawning the wheel, you will likely have to reload the profile to respawn the wheel so that it can find all the betting terminals.

### CH47 drop zones

To place a CH47 drop zone, run the command `maprefab dropzone`. It can be removed using the `makill` command.

Note: In order for a CH47 to drop a crate at this location, it must be within a monument that the chinook will visit. You can use the [Better Chinook Patrol](https://umod.org/plugins/better-chinook-patrol) plugin to customize which monuments can be visited.

### Common puzzle entities

- `generator.static` (not `generator.small`)
- `fusebox`
- `cardreader`
- `pressbutton` (not `button`)
- `doormanipulator` (not `doorcontroller.deployed`)
- `simpleswitch` (not `switch`)
- `orswitch` (not `orswitch.entity`)
- `timerswitch` (not `timer`)
- `xorswitch` (not `xorswitch.entity`)
- `door.hinged.security.green`, `door.hinged.security.blue`, `door.hinged.security.red`, `door.hinged.underwater_labs.security`, `door.hinged.garage_security`

Note: Kinetic IO elements such as `wheelswitch` and `sliding_blast_door` are not currently able to be controlled by electricity.

## Tips

- Bind `maspawn` and `makill` to keys while setting up entities to save time. Remember that running `maspawn` without specifying the entity name will spawn whichever deployable you are currently holding. Note: You may need to rotate the placement guide in some cases because the server cannot detect which way you have it rotated.
- Install [Telekinesis](https://umod.org/plugins/telekinesis) and bind `tls` to a key to quickly toggle entity movement controls.
- Be careful where you spawn entities. Make sure you are aiming at a point that is clearly inside a monument, or it may spawn in an unexpected location at other instances of that monument or for different map seeds. If you are aiming at a point where the terrain is not flat, that usually means it is not in the monument, though there are some exceptions.

## Troubleshooting

- If you receive the "Not at a monument" error, and you think you are at a monument, it means the Monument Finder plugin may have inaccurate bounds for that monument, such as if the monument is new to the game, or if it's a custom monument. Monument Finder provides commands to visualize the monument bounds, and configuration options to change them per monument.
- If you accidentally `ent kill` an entity that you spawned with this plugin, you can reload the entity's profile (or reload the whole plugin) to restore it.

## Uninstallation

Ensure the plugin is loaded with `Persist entities while the plugin is unloaded` set to `false`, then simply remove the plugin. All addons will be automatically removed.
