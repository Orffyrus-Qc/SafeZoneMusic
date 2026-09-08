# oxide/data

Oxide keeps each plugin's saved data here, one folder per plugin.

    data/
      SafeZoneMusic/          <- this plugin's folder
        Default/              <- shared playlist, used by any zone without its own folder
        Outpost/
        Bandit Camp/
        Fishing Village/
        Ranch/
        Appartement/

Drop `.mp3` files into the folder named after a monument to give that safe zone its own
music, or into `Default` for every zone to share one playlist.

Tracks must be **constant bitrate**. Rust plays a variable bitrate mp3 as static:

    ffmpeg -i input.mp3 -c:a libmp3lame -b:a 128k -ar 44100 -ac 2 output.mp3
