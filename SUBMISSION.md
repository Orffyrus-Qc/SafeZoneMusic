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

## Description

**Includes 30 Rust-inspired songs:** https://suno.com/@orffyrus

A Rust client can only play audio that ships inside the game, with one exception: the
BoomBox stream system, where the client opens an HTTP connection and decodes the audio
itself. SafeZoneMusic uses that. It hosts the mp3 files in `oxide/data/SafeZoneMusic`
as continuous Icecast style streams on its own port, paced frame by frame at real
playback speed, and locks a hidden boombox at the centre of every safe zone onto its
zone's stream.

Safe zones are found from their triggers, so Outpost, Bandit Camp, every Fishing
Village, the Ranch, the Large Barn and the Apartment Complex are picked up
automatically and written into the config on first boot. A village built over the sea
gets its boombox moved to the nearest dry land. Each zone can have its own folder of
music, or share the Default playlist.

Every stream is also published to the server radio station list, so players can select
`SafeZone - Outpost` on their own Boom Box, Jukebox or vehicle radio anywhere on the map.

### Features

- One folder of mp3s per monument, or one shared playlist for all of them
- Safe zones detected automatically, no coordinates to enter
- The boombox is buried out of sight and cannot be picked up, damaged or looted
- `/szmusic move` places a zone's boombox where you stand, and remembers it
- Stations appear on Boom Boxes, Jukeboxes and vehicle radios
- Tracks that Rust cannot play are named in the console instead of being served as static

### Requirements

Open the streaming TCP port (28090 by default) on the firewall. Clients connect to it
directly, exactly as they would to any internet radio station.

Tracks must be **constant bitrate**. A variable bitrate mp3 is valid audio that every
desktop player handles, and Rust renders it as static:

    ffmpeg -i input.mp3 -c:a libmp3lame -b:a 128k -ar 44100 -ac 2 output.mp3

## Commands

| Command | Description |
| --- | --- |
| `/szmusic` | What is playing, distance to the boombox, and its state |
| `/szmusic move` | Move the nearest zone's boombox to where you stand |
| `/szmusic reset` | Return that zone to automatic placement |
| `/szmusic restart` | Stop and restart the boombox, forcing clients to reopen the stream |
| `szmusic.status` | Zones, track counts, listeners and stream URLs |
| `szmusic.skip <zone or all>` | Skip the current track |
| `szmusic.reload` | Re-read the config, the folders and the map |
| `szmusic.stations` | Print the published station names and URLs |

**Permission:** `safezonemusic.admin`

## Tags

rust, oxide, music, audio, boombox, radio, jukebox, safezone, monument, streaming, mp3, ambience

If only a few are allowed: music, boombox, radio, safezone, audio
