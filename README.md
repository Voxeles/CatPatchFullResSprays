### An unofficial mod for CatPatch for viewing full resolution sprays

CatPatch: https://www.nexusmods.com/scavprototype/mods/69

Allows you to view and download other player's sprays in full resolution.
Both the host and the client must have this mod installed for this to work.

**NOTE**: This mod is still in development! There will be bugs!
It doesn't have any clever cache flushing nor OOM prevention - it is *not* recommended to use this mod on long-running dedicated servers!

#### Installation

1. Download CatPatchFullResSprays.zip from [Releases](https://github.com/Voxeles/CatPatchFullResSprays/releases)
2. Extract the "CatPatchFullResSprays.dll" file in your "Casualties Unknown Demo/BepInEx/plugins" folder

#### Config

After installing the mod and running the game once, a config file will be created in "Casualties Unknown Demo/BepInEx/config/meowharder.catpatch.fullressprays.cfg".
It allows you to change the maximum size, in bytes, of a spray that the server will allow. By default, it is set to 10MB.

#### Implementation Details

Normally, CatPatch sends all of your sprays to an external sever from which all clients download the spray image.
This server compresses PNGs when storing them, so there's no way to download the full resolution image.

This mod allows clients to send the uncompressed image to the host directly.
Other clients can then download the original PNG from the host, rather than the compressed version from the external server.

The spray is still sent to the external server as a backup, in case a client doesn't have this mod or the host rejects the spray.
