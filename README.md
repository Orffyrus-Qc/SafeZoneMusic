# SafeZoneMusic

Plays your own `.mp3` files at the centre of every Rust safe zone (Outpost, Bandit Camp,
Fishing Villages, Ranch / Large Barn and any custom zone such as an "Appartement"), and
publishes the same music as radio stations that players can pick on a Boom Box, a Jukebox
or a vehicle radio.

## How it works, and why

A Rust client can only play audio that ships inside the game bundle. A server plugin
cannot push a local `.mp3` to a client and cannot play one through an effect prefab.

The one exception is Rust's audio stream system, used by the Boom Box family: the client
opens a plain HTTP connection to a URL and decodes the MP3 stream itself.

So the plugin:

1. Hosts everything under `oxide/data/SafeZoneMusic/` on its own TCP port as continuous,
   Icecast style MP3 streams, one per zone, paced frame by frame at real playback speed so
   every listener stays in sync and nobody can run ahead of the stream.
2. Spawns a Boom Box entity at the centre of each safe zone, locks it onto that zone's
   stream and keeps it there.
3. Registers every zone stream in the server radio station list, which is what the Boom Box,
   the Jukebox and vehicle radios read their station list from.

## Install

The quickest way is `dist/SafeZoneMusic-oxide.zip`. Extract it **into your server's `oxide/`
folder** and it drops the plugin, the config, both language files and the whole music folder
structure into place at once:

    rust/oxide/plugins/SafeZoneMusic.cs
    rust/oxide/config/SafeZoneMusic.json
    rust/oxide/lang/en/SafeZoneMusic.json
    rust/oxide/lang/fr/SafeZoneMusic.json
    rust/oxide/data/SafeZoneMusic/{Default,Outpost,Bandit Camp,Fishing Village,Ranch,Appartement}/

The music folders ship empty - the tracks are yours to add. The config in the zip is the one
from a live server, so review `Public address`, `Port` and the `Zones` list before running it
anywhere else.

Or by hand:

1. Drop `SafeZoneMusic.cs` into `oxide/plugins/`.
2. Let it load once. It creates `oxide/data/SafeZoneMusic/` with one folder per zone and
   writes `oxide/config/SafeZoneMusic.json` listing every safe zone it found on the map.
3. Put `.mp3` files into the zone folders, then run `szmusic.reload` in the server console.
4. Open the streaming port (default TCP **28090**) on the firewall and, if the machine is
   behind NAT, forward it. Clients connect to that port directly, exactly as they would to
   any internet radio station.

## Music folders

```
oxide/data/SafeZoneMusic/
    Outpost/            track1.mp3, track2.mp3 ...
    Bandit Camp/
    Fishing Village/
    Ranch/
    Appartement/
    Default/            used by any zone with no folder of its own
```

Lookup order for a zone: its configured folder, then a folder named after the zone, then
`Default`, then any loose `.mp3` in the root of the data folder. Sub folders are included.

**Encode as CBR 128 kbps at 44.1 kHz. This is not a preference, it is a requirement.**

    ffmpeg -i input.mp3 -c:a libmp3lame -b:a 128k -ar 44100 -ac 2 output.mp3

Rust's stream decoder is stricter than a desktop player. **Variable bitrate is the thing it
cannot handle.** A VBR file streams as perfectly valid MPEG layer III - a capture of the live
stream decodes without a single error - and still arrives in the game as static, with no music
at all. This was isolated by running two zones side by side on the same source material at the
same 44.1 kHz, one converted to CBR and one left VBR: the CBR zone played, the VBR zone hissed.
Sample rate turned out not to be the culprit, though 44.1 kHz remains the safer choice.

The plugin checks this for you. Every track is inspected as its playlist loads, and anything
that will not play is named in the console rather than quietly turned into noise:

    Ranch: 5 of 30 track(s) use a variable bitrate, which Rust plays as static instead of
    music: 03-Raid me.mp3, 09-Dog Eat Dog.mp3, 17-My Clan.mp3
    Re-encode them at a constant bitrate: ffmpeg -i in.mp3 -c:a libmp3lame -b:a 128k ...

Set `Drop tracks Rust cannot play` to true and those files are left out of the rotation
altogether, so a mixed folder plays its usable tracks instead of hissing through the rest.

Bandwidth is the real limit, not CPU: 128 kbps per listener means about 16 KB/s each, so
100 players listening at once is roughly 13 Mbit/s of upload. Drop to 96 or 64 kbps for a
large server, and use `Max listeners per station` as a hard ceiling.

## Car radio, Jukebox and Boom Box

Yes, this part works, and it is the same mechanism for all of them.

Everything in Rust that streams audio - the deployable Boom Box, the handheld Boom Box, the
Jukebox and vehicle radios - takes its station list from one server side convar,
`boombox.serverurllist`, in the form `Name,Url,Name,Url`. With
`Register zone streams as server radio stations` enabled, the plugin writes each zone stream
into that list, so a player opening any of those devices sees entries like
`SafeZone - Outpost` and can play the safe zone music anywhere on the map.

Two things worth knowing:

* Once a custom list is set, it replaces the built in station list in that UI. Any vanilla
  or third party stations you want to keep must be listed under `Extra stations` in the
  config, as `Name` / `Url` pairs. The plugin restores whatever list was set before it
  loaded when it unloads.
* If another plugin also writes `boombox.serverurllist`, load order decides who wins. Keep
  `Keep the station list that was already set on the server` enabled so the two lists merge
  instead of overwriting each other.

The in zone speakers do not depend on this: they are set server side and play even if
station registration is turned off.

## Zones

Safe zones are detected from the safe zone triggers on the map, which is what Outpost,
Bandit Camp, every Fishing Village, the Ranch / Large Barn and custom monuments built with
one all carry. Each detected zone is named after the nearest monument and written into the
config, so after the first boot you can see exactly what was found and tune it.

On a current Rust map this finds Outpost, Bandit Camp, Ranch, Large Barn, the Fishing
Villages and **Apartment Complex**, which is the safe zone the `Appartement` config entry
matches by keyword. Zones keep their real monument name, so two of the same kind are told
apart as `Fishing Village` and `Large Fishing Village`.

Safe zones that match no monument - the travelling vendor and event bubbles, which appear
while the server is running and move - are ignored, otherwise they would each be given a
speaker rig that stays behind where the bubble first appeared. Turn that off with
`Ignore safe zones that match no monument`.

A zone that has no trigger, or a custom build such as an apartment block, is added by hand:
stand in the middle of it, read your position with `printpos` in F1, and fill in
`Manual position` as `x y z`. The plugin warns on load about any configured zone it could
not find on the map.

Per zone options: `Enabled`, its music `Folder`, `Monument keywords` used to match the
monument name, `Manual position`, `Use this stream URL instead of the built in server` (for
an existing Icecast / Azuracast stream) and `Spawn a speaker for this zone`.

Two more control where the rig sits, for monuments that already play their own music:

* `Move the rig this far off the centre` - metres. The fishing villages and the barns have
  their own audio in the middle, so moving ours out towards the edge keeps the two apart.
  Capped at the zone radius less 5 metres, so it stays inside the safe zone.
* `Direction to move it` - degrees clockwise from north, or -1 to head directly away from the
  monument's own speakers where the plugin can find them. Set a bearing yourself when it
  cannot, which is the case at most fishing villages and both barns.

An entry with a `Manual position` claims the detected safe zone within 150 metres of it, so a
single fishing village can be given its own settings without the other two inheriting them.

## Speakers

Each zone gets **one Boom Box**, buried just under the ground so the model is out of sight while
it still plays. That is the whole rig by default, and it covers roughly its own audible range,
about 40 metres around wherever it sits.

**What counts as the ground.** The surface a player stands on: the terrain, or the water
surface where a monument is built out over the sea, like a fishing village. It is deliberately
not a ray fired down from the sky - at Outpost that stops on a roof about 11 metres up, and at
the Apartment Complex about 26 metres up, which is where the Boom Box used to end up hanging.
Placement is logged for each zone on load (`Outpost: ground 14.8, source at y=13.8`) so you can
check it without going to look.

**Villages built over the sea.** A fishing village's safe zone is centred on the water, so
burying the Boom Box at the centre puts it under the seabed. With `Move the boombox to dry land
when the centre sits over water` on, the plugin walks outwards in ten metre rings until it finds
ground the water does not cover, and buries it there - the beach closest to the village, which
keeps it near the players rather than pushing it inland. Water depth is sampled at the terrain
surface, not at the position being tested, or the answer is always zero.

`How deep to bury it` defaults to 1 metre, enough to sink the model. `Volume` sets its loudness
(0 to 1). The rig is spawned fresh on load, is not saved to the map, and cannot be picked up,
damaged or looted while `Protect speakers` is on. The watchdog re-applies the station every 15
seconds and rebuilds a zone if its Boom Box disappears.

### Covering a whole monument

One Boom Box does not reach the edges of somewhere as large as Outpost. Turning on
`Spread the sound over the monument with connected speakers` adds horn speakers wired to it,
laid out from the zone's real radius: Rust's own mechanism for spreading one source over an
area, and the reason they stay in step - they replay their source rather than each opening its
own stream.

Two things to know before enabling it. The speakers are visible objects mounted above the
ground, and a speaker sitting on top of an audible Boom Box makes you hear the track twice, so
any within 15 metres of it is skipped automatically; bury the Boom Box deeper than 15 metres and
the centre speaker comes back, because the Boom Box is then out of earshot.

## The monument's own music

Outpost and Bandit Camp play their own music, and it keeps going underneath this plugin's
stream. It cannot be switched off from the server: there is no entity to turn off. Scanning
every entity inside those zones (389 at Outpost, 426 at Bandit Camp, 1449 at the Apartment
Complex) finds no audio entity at all, the only static boomboxes on the map are at the Oil Rigs
and Underwater Labs and they are switched off, and `MusicZone` - the component that drives
monument music - is client side data with no networking. Outpost's comes from
`assets/scenes/prefabs/compound/music_stage.prefab`, which is monument scene art each client
loads for itself.

It is not on the music bus either, so `audio.musicvolume 0` does not touch it. What is left is
a client side audio setting each player can choose, `audio.ambience false` or `audio.game 0` in
the F1 console. The plugin's own audio should survive both, because the boombox family sits on
its own bus.

The emitters can at least be **found**, even though they cannot be silenced: the monument
objects still exist in the server's copy of the map with their audio components stripped, so
they are locatable by name - `Music_Stage` and four `speaker` objects at Outpost, one `speaker`
at Bandit Camp, three `security_speaker_medium` at the Apartment Complex.
`Keep clear of the monument's own speakers` uses that to place the rig away from them, leaving
the area round the stage to the monument's own music. It is **off by default** (0); set it to
about 60 to push the boombox and any nearby speakers out of that radius.

## Commands

| Command | Where | What it does |
| --- | --- | --- |
| `/szmusic` | chat | Shows what is playing in the zone you are standing in |
| `/szmusic move` | chat | Moves the nearest zone's boombox to where you stand, buried, and keeps it |
| `/szmusic reset` | chat | Puts that zone's boombox back to automatic placement |
| `szmusic.status` | console | Zones, track counts, listeners, centres and stream URLs |
| `szmusic.skip <zone or all>` | console | Skips the current track |
| `szmusic.reload` | console | Re-reads the config, the folders and the map |
| `szmusic.stations` | console | Prints the published station names and URLs |

`/szmusic move` is the easy way to place a boombox: stand where you want the music to come
from and run it. The position is written to the config as that zone's `Boombox position`, so it
survives reloads and restarts, and it overrides the shore search and the off centre offset. The
burial depth still applies, so it goes under the ground where you are standing rather than at
your feet.

Zones that share a config entry - the three fishing villages all match one - get their own entry
pinned to their centre the first time you move one, so the other two stay where they are.

Both need the `safezonemusic.admin` permission, or auth level admin. Console commands need the
same permission when run by a player.

    oxide.grant user <name> safezonemusic.admin

## Troubleshooting

* **Silence in the zone, no errors.** Almost always the port. Open
  `http://<your server ip>:28090/status` in a browser from another machine: you should see
  the station list. If that page does not load, the port is closed or not forwarded.
* **Testing on the same machine as the server.** Auto detection returns the WAN address,
  which a client on that same machine usually cannot reach. Set `Public address` to
  `127.0.0.1` while testing locally, and back to the real address before players connect.
* **`Music port is busy, retrying`.** Normal for a second or two after a reload while the old
  socket closes; the plugin retries five times and then tells you to pick another port.
* **Reloading the plugin briefly interrupts the stream.** Expect well under a second while the
  port changes hands. If instead the music stays dead for about a minute after every reload,
  the listening socket is being held open by the Oxide compiler child process on Windows; the
  plugin marks its sockets non inheritable to prevent exactly that, so check that change is
  still present in `MusicServer`.
* **`Could not work out the public address of this server`.** Set `Public address` in the
  config to the IP or hostname players connect to. Clients resolve the stream URL themselves,
  so a LAN or loopback address will not reach them.
* **`contains no readable MPEG layer III frames`.** The file is not really an MP3, or it is
  MPEG layer II. Re-encode it with the ffmpeg line above.
* **The plugin does not compile after a Rust update.** Two places touch the game audio API:
  `BoomBox.CurrentRadioIp` in `ApplyStation`, and the `ServerTogglePlay` lookup just below it
  (already called through reflection so a rename there cannot break the build).
