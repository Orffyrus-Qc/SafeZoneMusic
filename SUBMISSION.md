# uMod submission draft

Paste-ready fields for the umod.org plugin form.

**Name:** SafeZoneMusic
**Author:** Orffyrus
**Version:** 1.9.1
**Source:** https://github.com/Orffyrus-Qc/SafeZoneMusic
**Icon:** icon-256.png (256x256 PNG, 24-bit RGB with no alpha)
**Upload:** oxide/plugins/SafeZoneMusic.cs (the single .cs file, not the zip)
**Sync on:** release, not push - the repo gets docs and icon commits that are not code changes
**Dependencies:** none
**Licence:** MIT for the code; the songs are the author's own

## Short description (81 chars, limit 100)

Streams your own mp3s to every safe zone and to any boombox, jukebox or car radio

## Documentation

## How it works

Each zone gets one Boom Box, buried a metre under the ground so the model is out of sight while it still plays. It is spawned fresh on load, never saved to the map, and cannot be picked up, damaged or looted. A watchdog re-applies the station every 15 seconds and rebuilds a zone if its boombox disappears.

The plugin also writes every zone stream into the server's radio station list, which is where the Boom Box, the Jukebox and vehicle radios all read their stations from. Players see entries like "SafeZone - Outpost" and can play the music anywhere on the map.

## Setup

1. Put SafeZoneMusic.cs in oxide/plugins/.
2. Let it load once. It creates oxide/data/SafeZoneMusic/ with one folder per zone, and writes oxide/config/SafeZoneMusic.json listing every safe zone found on the map.
3. Put .mp3 files in the folders, then run szmusic.reload.
4. Open the streaming port (TCP 28090 by default) on the firewall, and forward it if the machine is behind NAT. Clients connect to it directly, like any internet radio station.
5. Set "Public address" in the config to the address players connect to. Auto detection returns the WAN address, which a client on the same machine usually cannot reach.

## Music folders

Includes 30 Rust-inspired songs: https://suno.com/@orffyrus

*Can be changed at any time — if you installed only the .cs file, you will find it here:

- Tracks: https://github.com/Orffyrus-Qc/SafeZoneMusic/tree/master/oxide/data/SafeZoneMusic/Default
- Zip: https://github.com/Orffyrus-Qc/SafeZoneMusic/blob/master/dist/SafeZoneMusic-oxide.zip

    oxide/data/SafeZoneMusic/
        Default/            shared playlist, used by any zone without its own folder
        Outpost/
        Bandit Camp/
        Fishing Village/
        Ranch/
        Appartement/

Lookup order for a zone: a folder named after the zone, then its configured folder, then Default, then loose files in the root. Sub folders are included.

## Track format

Tracks must be CONSTANT bitrate. This is not a preference. A variable bitrate mp3 is valid audio that every desktop player handles, and Rust renders it as static with no music at all - the bytes on the wire decode without a single error, and the game still cannot play them.

    ffmpeg -i input.mp3 -c:a libmp3lame -b:a 128k -ar 44100 -ac 2 output.mp3

The plugin inspects every track as its playlist loads and names any file that will not play, rather than serving it as noise. Set "Drop tracks Rust cannot play" to true and those files are left out of the rotation entirely.

Bandwidth is the real limit, not CPU: 128 kbps is about 16 KB/s per listener, so 100 players listening at once is roughly 13 Mbit/s of upload. "Max listeners per station" is a hard ceiling.

## Placement

The ground means the surface a player stands on: the terrain, or the water surface where a monument is built out over the sea. It is deliberately not a ray fired down from the sky, which stops on a roof - about 11 metres up at Outpost and 26 at the Apartment Complex. A fishing village centred on water gets its boombox moved to the nearest dry land automatically.

Placement is logged per zone on load, so it can be checked without going to look:

    Outpost: centre 341 740, radius 122, source at 341 13.8 740 (ground 14.8).

## Commands

| Command | Description |
| --- | --- |
| /szmusic | What is playing, distance to the boombox, and its state |
| /szmusic move | Move the nearest zone's boombox to where you stand, and keep it |
| /szmusic reset | Return that zone to automatic placement |
| /szmusic restart | Stop and restart the boombox, forcing clients to reopen the stream |
| szmusic.status | Zones, track counts, listeners, centres and stream URLs |
| szmusic.skip &lt;zone or all&gt; | Skip the current track |
| szmusic.reload | Re-read the config, the folders and the map |
| szmusic.stations | Print the published station names and URLs |

Permission: safezonemusic.admin

## Configuration

Streaming: port, bind and public address, max listeners per station, burst on connect, shuffle, and whether to drop tracks the game cannot play.

Speakers: the source prefab, burial depth, volume, whether to move the boombox to dry land, and optional connected speakers to spread one zone's sound over a large monument.

Radio stations: whether to publish the zone streams, a station name prefix, whether to keep any station list already set on the server, and extra stations of your own.

Zones: one entry per monument, each with its own music folder, monument keywords, an optional manual position, an optional external stream URL, and a saved boombox position.

## Notes

Setting a custom station list replaces the built in one in the Boom Box UI. Add any stations you want to keep under "Extra stations".

Safe zones that match no monument, such as the travelling vendor, are ignored by default so they do not get a speaker rig left behind where the bubble first appeared.

The monument's own music at Outpost and Bandit Camp cannot be switched off from the server. It is client side audio baked into the monument, with no entity to turn off.

## Tags

rust, oxide, music, audio, boombox, radio, jukebox, safezone, monument, streaming, mp3, ambience

If only a few are allowed: music, boombox, radio, safezone, audio
