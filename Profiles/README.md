# Community Profiles

The Monument Addons profiles in this folder are created and maintained by the community. Anyone is free to use them.

## How to install a profile

To install a profile from this folder, simply run the command `mainstall <profile>`. For example: `mainstall OutpostAirwolf` (or `/mainstall OutpostAirwolf` in chat). This command will cause the Monument Addons plugin to automatically download the json file from this folder on GitHub to data/MonumentAddons/ folder on your server and then activate the profile.

## How to disable a profile

If you want to uninstall the profile, simply run `maprofile disable <profile>` then `maprofile delete <profile>`. For example, `maprofile disable OutpostAirwolf` then `maprofile delete OutpostAirwolf`.

## How to update a profile

If you want to update a profile after it has been modified here, you have to first delete the profile then install it again. The plugin may support a feature to simply update a profile in the future.

## Found an issue with a profile?

If you encounter any issues with these profiles, please open an issue on this GitHub repository or make a GitHub Pull Request to update the profile with the suggested fix.

## Have a profile you want to share?

If you have a set of addons you want to share in a profile, please follow these steps.

### 1. Create your profile

1. Run the command: `maprofile create <name>`
2. Create addons in the profile using commands such as `maspawn`
3. Move addons from other profiles by aiming at them in-game and running the command `maprofile moveto <name>`

Once your profile is ready to go, you'll find it in the data/MonumentAddons/ folder with the name you chose earlier, with a .json file extension

### 2. Create a GitHub Pull Request

1. Register on GitHub
2. Click [here](https://github.com/WheteThunger/MonumentAddons/fork) to create a fork of this repository
   1. If you already have a fork from a previous contribution, sync your fork before making changes
3. Upload your profile's json file to this folder on your fork of the repository
4. Create the Pull Request
   1. For help, see the GitHub documentation [here](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/proposing-changes-to-your-work-with-pull-requests/creating-a-pull-request-from-a-fork)

### 3. Wait for your profile to be reviewed and merge

This repository's maintainer(s) will review your profile and merge it once approved. You may be asked to make changes to fix issues or make improvements before the profile is accepted.
