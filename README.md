# An unofficial mod for CatPatch for viewing full resolution sprays

CatPatch: https://www.nexusmods.com/scavprototype/mods/69

Allows you to view and download other player's sprays in full resolution.
Both the host and the client must have this mod installed for this to work.

## Installation

1. Download CatPatchFullResSprays.zip from [Releases](https://github.com/Voxeles/CatPatchFullResSprays/releases)
2. Extract the "CatPatchFullResSprays.dll" file in your "Casualties Unknown Demo/BepInEx/plugins" folder

## Config

After installing the mod and running the game once, a config file will be created
in "Casualties Unknown Demo/BepInEx/config/meowharder.catpatch.fullressprays.cfg".

Configuration options:
- `showSpraysOnMainMenu` - Set to true to view sprays created in the main menu. Toggleable in-game in CatPatch's settings (click the little square button at the bottom)
- `memoryCacheMiB` - Maximum size for the RAM cache
- `diskCacheMiB` - Maximum size for the on-disk secondary cache
- `clearDiskCacheOnExit` - Clears the on-disk cache on game exit. This is about this mod's secondary cache located in 'BepInEx/config/Cat-Patch/FullResSpraysCache' - Your spray library is never deleted
- `maxSpraySizeMiB` - As host, the maximum image size you'll accept

## Implementation Details

Normally, CatPatch sends all of your sprays to an external sever from which all clients download the spray image.
This server compresses PNGs when storing them, so there's no way to download the full resolution image.

This mod allows clients to send the uncompressed image to the host directly.
Other clients can then download the original PNG from the host, rather than the compressed version from the external server.

The spray is still sent to the external server as a backup, in case a client doesn't have this mod or the host rejects the spray.

The full resolution sprays are saved in RAM and on-disk (See the `memoryCacheMiB` and `diskCacheMiB` settings).
You can view the contents of the on-disk cache in 'BepInEx/config/Cat-Patch/FullResSpraysCache'.

The on-disk cache is cleared on game exit by default. Disable `clearDiskCacheOnExit` if you want to collect other user's sprays.
