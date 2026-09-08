// Requires: Oxide / uMod for Rust
//
// SafeZoneMusic
// -------------
// Rust clients can only play audio that is bundled inside the game, with ONE exception:
// the BoomBox audio-stream system (deployable Boom Box, handheld Boom Box, Jukebox and
// vehicle radios). Those connect straight from the client to an HTTP URL and decode the
// MP3 stream themselves.
//
// So this plugin does two things:
//   1. It hosts every .mp3 found under oxide/data/SafeZoneMusic/<Zone>/ as a continuous,
//      Icecast-style MP3 stream on its own TCP port.
//   2. It spawns a hidden speaker (a BoomBox entity) at the centre of each safe zone and
//      locks it onto that zone's stream, and it registers every stream in the server's
//      radio station list so players can pick them on their own Boom Box / Jukebox / car radio.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SafeZoneMusic", "Orffyrus", "1.9.1")]
    [Description("Plays .mp3 files from oxide/data/SafeZoneMusic at the centre of every safe zone, and exposes them as radio stations for boomboxes, jukeboxes and vehicle radios.")]
    public class SafeZoneMusic : RustPlugin
    {
        private const string PermAdmin = "safezonemusic.admin";

        private Configuration _config;
        private MusicServer _server;
        private string _dataRoot;
        private string _publicAddress;
        private string _savedStationList;
        private bool _stationListChanged;

        private readonly List<Zone> _zones = new List<Zone>();
        private readonly HashSet<BaseEntity> _speakers = new HashSet<BaseEntity>();
        private Oxide.Plugins.Timer _watchdog;
        private int _startAttempts;
        private bool _hooksActive;
        private bool _unloaded;
        private Oxide.Plugins.Timer _retry;

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Auto detect safe zones")]
            public bool AutoDetect = true;

            [JsonProperty("Add newly detected safe zones to this config")]
            public bool AutoAddZones = true;

            [JsonProperty("Ignore safe zones that match no monument (travelling vendor, events)")]
            public bool IgnoreUnnamedZones = true;

            [JsonProperty("Streaming")]
            public StreamingOptions Streaming = new StreamingOptions();

            [JsonProperty("Speakers")]
            public SpeakerOptions Speakers = new SpeakerOptions();

            [JsonProperty("Radio stations")]
            public StationOptions Stations = new StationOptions();

            [JsonProperty("Zones")]
            public List<ZoneConfig> Zones = new List<ZoneConfig>();
        }

        private class StreamingOptions
        {
            [JsonProperty("Enabled (turn off to use external stream URLs only)")]
            public bool Enabled = true;

            [JsonProperty("Bind address")]
            public string BindAddress = "0.0.0.0";

            [JsonProperty("Port")]
            public int Port = 28090;

            [JsonProperty("Public address (leave empty to auto detect)")]
            public string PublicAddress = "";

            [JsonProperty("Max listeners per station")]
            public int MaxListeners = 150;

            [JsonProperty("Burst on connect (KB)")]
            public int BurstKb = 48;

            [JsonProperty("Drop tracks Rust cannot play instead of streaming them as static")]
            public bool SkipUnplayable = false;

            [JsonProperty("Shuffle playlists")]
            public bool Shuffle = true;

            [JsonProperty("Log listener connections")]
            public bool LogConnections = false;
        }

        private class SpeakerOptions
        {
            [JsonProperty("Spawn speakers in safe zones")]
            public bool Enabled = true;

            [JsonProperty("Source prefab (the boombox that holds the stream)")]
            public string Prefab = "assets/prefabs/voiceaudio/boombox/boombox.deployed.static.prefab";

            [JsonProperty("Hide the source boombox under the ground")]
            public bool Hide = true;

            [JsonProperty("How deep to bury it (metres) - just enough to sink the model")]
            public float HideDepth = 1f;

            [JsonProperty("Move the boombox to dry land when the centre sits over water")]
            public bool PreferLand = true;

            [JsonProperty("Height above ground when it is not hidden")]
            public float HeightOffset = 4f;

            [JsonProperty("Volume, the game clamps this to 0.2 - 1 (-1 keeps the prefab default)")]
            public float Volume = 1f;

            [JsonProperty("Spread the sound over the monument with connected speakers")]
            public bool UseConnectedSpeakers = false;

            [JsonProperty("Connected speaker prefab")]
            public string SpeakerPrefab = "assets/prefabs/voiceaudio/hornspeaker/connectedspeaker.deployed.static.prefab";

            [JsonProperty("How far one speaker carries (metres) - drives how many are placed")]
            public float CoverageRadius = 40f;

            [JsonProperty("Speaker height above the ground (negative buries them too)")]
            public float SpeakerHeight = 2f;

            [JsonProperty("Maximum speakers per zone")]
            public int MaxSpeakers = 12;

            [JsonProperty("Keep clear of the monument's own speakers (metres, 0 = ignore them)")]
            public float KeepAwayFromMonumentAudio = 0f;

            [JsonProperty("Protect speakers from damage, pickup and looting")]
            public bool Protect = true;

            [JsonProperty("Re-assert station every X seconds (0 = never)")]
            public float ReassertSeconds = 15f;
        }

        private class StationOptions
        {
            [JsonProperty("Register zone streams as server radio stations (boombox / jukebox / vehicle radios)")]
            public bool Register = true;

            [JsonProperty("Station name prefix")]
            public string Prefix = "SafeZone - ";

            [JsonProperty("Keep the station list that was already set on the server")]
            public bool KeepExisting = true;

            [JsonProperty("Extra stations")]
            public List<StationEntry> Extra = new List<StationEntry>();
        }

        private class StationEntry
        {
            [JsonProperty("Name")] public string Name = "";
            [JsonProperty("Url")] public string Url = "";
        }

        private class ZoneConfig
        {
            [JsonProperty("Name")] public string Name = "";
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Music folder (relative to oxide/data/SafeZoneMusic)")] public string Folder = "";
            [JsonProperty("Monument keywords")] public List<string> Keywords = new List<string>();
            [JsonProperty("Manual position 'x y z' (leave empty to auto detect)")] public string Position = "";
            [JsonProperty("Use this stream URL instead of the built in server")] public string StreamUrl = "";
            [JsonProperty("Spawn a speaker for this zone")] public bool Speaker = true;
            [JsonProperty("Keep clear of the monument's own speakers (metres, -1 = use the Speakers setting)")] public float KeepAway = -1f;
            [JsonProperty("Boombox position 'x y z' set by /szmusic move (empty = work it out automatically)")] public string SourcePosition = "";
            [JsonProperty("Move the rig this far off the centre (metres, 0 = stay in the middle)")] public float Offset = 0f;
            [JsonProperty("Direction to move it, degrees clockwise from north (-1 = away from the monument's own speakers)")] public float OffsetAngle = -1f;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
            _config.Zones = new List<ZoneConfig>
            {
                NewZone("Outpost", "Outpost", "outpost", "compound"),
                NewZone("Bandit Camp", "Bandit Camp", "bandit"),
                NewZone("Fishing Village", "Fishing Village", "fishing village"),
                NewZone("Ranch", "Ranch", "ranch", "barn"),
                NewZone("Appartement", "Appartement", "appartement", "apartment")
            };
            PrintWarning("Created a new configuration file.");
        }

        private static ZoneConfig NewZone(string name, string folder, params string[] keywords)
        {
            return new ZoneConfig
            {
                Name = name,
                Folder = folder,
                Keywords = keywords.ToList()
            };
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new JsonException("empty config");
            }
            catch (Exception ex)
            {
                PrintError($"Configuration is invalid ({ex.Message}), loading defaults.");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            _dataRoot = Path.Combine(Interface.Oxide.DataDirectory, Name);

            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(CanPickupEntity));
            Unsubscribe(nameof(CanLootEntity));
            _hooksActive = false;
        }

        private void OnServerInitialized()
        {
            EnsureDataFolders();
            ResolvePublicAddress();
            DiscoverZones();
            LoadPlaylists();
            StartAudio();

            if (_config.Speakers.ReassertSeconds > 0f)
                _watchdog = timer.Every(_config.Speakers.ReassertSeconds, Watchdog);

            var withMusic = _zones.Count(z => z.Files.Count > 0);
            Puts($"Ready: {_zones.Count} safe zone(s) detected, {withMusic} with music.");
            if (withMusic == 0)
                PrintWarning($"No .mp3 files found. Drop some into {_dataRoot} (one sub folder per zone) and run 'szmusic.reload'.");
        }

        private void Unload()
        {
            _unloaded = true;

            // Release the listening socket first: a reload rebinds it moments later.
            _retry?.Destroy();
            _server?.Stop();
            _server = null;

            _watchdog?.Destroy();
            RestoreStationList();
            RemoveSpeakers();
        }

        // The socket of a previous instance can still be closing when a reload lands, so a
        // failed bind is retried rather than leaving the plugin silently without music.
        private void StartAudio()
        {
            if (_unloaded) return;

            if (!StartStreaming())
            {
                if (++_startAttempts <= 5)
                {
                    PrintWarning($"Music port {_config.Streaming.Port} is busy, retrying in 2s ({_startAttempts}/5). This is normal right after a reload.");
                    _retry = timer.Once(2f, StartAudio);
                    return;
                }

                PrintError($"Music port {_config.Streaming.Port} stayed busy. Pick a free port in the config and run szmusic.reload, or point the zones at external stream URLs.");
            }

            RegisterStations();
            if (_config.Speakers.Enabled) SpawnSpeakers();
        }

        private void EnsureDataFolders()
        {
            try
            {
                Directory.CreateDirectory(_dataRoot);
                Directory.CreateDirectory(Path.Combine(_dataRoot, "Default"));
                foreach (var zone in _config.Zones)
                {
                    var folder = string.IsNullOrEmpty(zone.Folder) ? zone.Name : zone.Folder;
                    if (!string.IsNullOrEmpty(folder))
                        Directory.CreateDirectory(Path.Combine(_dataRoot, Sanitize(folder)));
                }
            }
            catch (Exception ex)
            {
                PrintError($"Could not create the data folders: {ex.Message}");
            }
        }

        private void ResolvePublicAddress()
        {
            _publicAddress = _config.Streaming.PublicAddress?.Trim();
            if (!string.IsNullOrEmpty(_publicAddress)) return;

            try { _publicAddress = covalence.Server.Address?.ToString(); } catch { }

            if (IsLocalAddress(_publicAddress))
            {
                try { _publicAddress = ConVar.Server.ip; } catch { }
            }

            if (IsLocalAddress(_publicAddress))
            {
                PrintWarning("Could not work out the public address of this server. Set \"Public address\" in the config, otherwise clients will not be able to reach the music stream.");
                if (string.IsNullOrEmpty(_publicAddress)) _publicAddress = "127.0.0.1";
            }
        }

        private static bool IsLocalAddress(string address)
        {
            return string.IsNullOrEmpty(address) || address == "0.0.0.0" || address.StartsWith("127.");
        }

        #endregion

        #region Zone discovery

        private class Zone
        {
            public string Name;
            public string Slug;
            public string Folder;
            public Vector3 Center;
            public float Radius;
            public bool WantsSpeaker = true;
            public string StreamUrl;
            public List<string> Files = new List<string>();
            public BaseEntity Source;
            public readonly List<BaseEntity> Speakers = new List<BaseEntity>();
            public List<Vector3> MonumentAudio = new List<Vector3>();
            public float KeepAway;
            public float Offset;
            public float OffsetAngle;
            public ZoneConfig Config;
            public Vector3? SourceOverride;
            public RadioStation Station;
        }

        private void DiscoverZones()
        {
            _zones.Clear();

            var detected = _config.AutoDetect ? DetectSafeZones() : new List<KeyValuePair<Vector3, float>>();
            var configAdded = false;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matched = new HashSet<ZoneConfig>();

            foreach (var hit in detected)
            {
                var monument = GetMonumentName(hit.Key);
                var cfg = MatchZoneConfigByPosition(hit.Key);
                var claimed = cfg != null;
                if (cfg == null) cfg = MatchZoneConfig(monument);

                if (cfg == null)
                {
                    if (_config.IgnoreUnnamedZones && monument == UnknownMonument) continue;
                    if (!_config.AutoAddZones) continue;
                    cfg = NewZone(monument, Sanitize(monument));
                    _config.Zones.Add(cfg);
                    configAdded = true;
                    Puts($"Detected a new safe zone '{monument}' and added it to the config.");
                }

                if (!cfg.Enabled) continue;
                if (!claimed && !string.IsNullOrEmpty(cfg.Position)) continue;

                matched.Add(cfg);
                AddZone(cfg, hit.Key, hit.Value, usedNames, monument);
            }

            foreach (var cfg in _config.Zones)
            {
                if (!cfg.Enabled || string.IsNullOrEmpty(cfg.Position)) continue;
                if (matched.Contains(cfg)) continue;

                Vector3 position;
                if (!TryParseVector(cfg.Position, out position))
                {
                    PrintError($"Zone '{cfg.Name}' has an invalid position '{cfg.Position}'. Expected the format 'x y z'.");
                    continue;
                }
                matched.Add(cfg);
                AddZone(cfg, position, 0f, usedNames);
            }

            foreach (var cfg in _config.Zones)
            {
                if (!cfg.Enabled || matched.Contains(cfg)) continue;
                PrintWarning($"Zone '{cfg.Name}' was not found on this map. If it is a custom safe zone, put its centre in \"Manual position\" as 'x y z'.");
            }

            if (configAdded) SaveConfig();
        }

        private static Vector3? ParseOverride(string value)
        {
            Vector3 position;
            return TryParseVector(value, out position) ? position : (Vector3?)null;
        }

        private void AddZone(ZoneConfig cfg, Vector3 center, float radius, HashSet<string> usedNames, string detectedName = null)
        {
            var baseName = !string.IsNullOrEmpty(detectedName)
                ? detectedName
                : string.IsNullOrEmpty(cfg.Name) ? "Safe Zone" : cfg.Name;
            var name = baseName;
            var index = 2;
            while (!usedNames.Add(name)) name = $"{baseName} {index++}";

            _zones.Add(new Zone
            {
                Name = name,
                Slug = Slugify(name),
                Folder = string.IsNullOrEmpty(cfg.Folder) ? Sanitize(baseName) : Sanitize(cfg.Folder),
                Center = center,
                Radius = radius,
                WantsSpeaker = cfg.Speaker,
                KeepAway = cfg.KeepAway >= 0f ? cfg.KeepAway : _config.Speakers.KeepAwayFromMonumentAudio,
                Offset = cfg.Offset,
                OffsetAngle = cfg.OffsetAngle,
                Config = cfg,
                SourceOverride = ParseOverride(cfg.SourcePosition),
                StreamUrl = string.IsNullOrEmpty(cfg.StreamUrl) ? null : cfg.StreamUrl.Trim()
            });
        }

        // Safe zone triggers are the only reliable marker: Outpost, Bandit Camp, every Fishing
        // Village, the Ranch / Large Barn and any custom monument that was built with one all
        // carry them. A monument can hold several triggers, so nearby hits are merged.
        private List<KeyValuePair<Vector3, float>> DetectSafeZones()
        {
            var result = new List<KeyValuePair<Vector3, float>>();

            TriggerSafeZone[] triggers;
            try
            {
                triggers = UnityEngine.Object.FindObjectsOfType<TriggerSafeZone>();
            }
            catch (Exception ex)
            {
                PrintError($"Safe zone detection failed: {ex.Message}");
                return result;
            }

            foreach (var trigger in triggers)
            {
                if (trigger == null) continue;

                var center = trigger.transform.position;
                var radius = 50f;
                var collider = trigger.GetComponent<Collider>();
                if (collider != null)
                {
                    var bounds = collider.bounds;
                    center = bounds.center;
                    radius = Mathf.Max(bounds.extents.x, bounds.extents.z);
                }

                var merged = false;
                for (var i = 0; i < result.Count; i++)
                {
                    if (Distance2D(result[i].Key, center) > 80f) continue;
                    if (radius > result[i].Value) result[i] = new KeyValuePair<Vector3, float>(center, radius);
                    merged = true;
                    break;
                }
                if (!merged) result.Add(new KeyValuePair<Vector3, float>(center, radius));
            }
            return result;
        }

        private const string UnknownMonument = "Safe Zone";

        private static string GetMonumentName(Vector3 position)
        {
            try
            {
                var monuments = TerrainMeta.Path?.Monuments;
                if (monuments == null) return "Safe Zone";

                MonumentInfo best = null;
                var bestDistance = float.MaxValue;
                foreach (var monument in monuments)
                {
                    if (monument == null) continue;
                    var distance = Distance2D(monument.transform.position, position);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = monument;
                }
                if (best == null || bestDistance > 400f) return "Safe Zone";

                var name = best.displayPhrase.english;
                if (string.IsNullOrEmpty(name)) name = best.name;
                return CleanMonumentName(name);
            }
            catch
            {
                return "Safe Zone";
            }
        }

        private static string CleanMonumentName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Safe Zone";

            var name = raw.Replace("(Clone)", string.Empty).Replace(".prefab", string.Empty);
            var slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            name = name.Replace('_', ' ').Trim();
            return string.IsNullOrEmpty(name) ? "Safe Zone" : name;
        }

        // A positioned entry wins over a keyword one when the detected zone is right next to it.
        private ZoneConfig MatchZoneConfigByPosition(Vector3 center)
        {
            foreach (var cfg in _config.Zones)
            {
                if (!cfg.Enabled || string.IsNullOrEmpty(cfg.Position)) continue;

                Vector3 position;
                if (!TryParseVector(cfg.Position, out position)) continue;
                if (Distance2D(position, center) > 150f) continue;

                return cfg;
            }
            return null;
        }

        private ZoneConfig MatchZoneConfig(string monumentName)
        {
            foreach (var cfg in _config.Zones)
            {
                if (!string.IsNullOrEmpty(cfg.Name) && string.Equals(cfg.Name, monumentName, StringComparison.OrdinalIgnoreCase))
                    return cfg;

                if (cfg.Keywords == null) continue;
                foreach (var keyword in cfg.Keywords)
                {
                    if (string.IsNullOrEmpty(keyword)) continue;
                    if (monumentName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return cfg;
                }
            }
            return null;
        }

        #endregion

        #region Playlists

        private void LoadPlaylists()
        {
            foreach (var zone in _zones)
            {
                zone.MonumentAudio = FindMonumentAudio(zone);

                zone.Files = CollectTracks(zone);
                CheckPlaylistFormat(zone);
                if (zone.Files.Count == 0 && zone.StreamUrl == null)
                    PrintWarning($"No .mp3 found for '{zone.Name}' (looked in {Path.Combine(_dataRoot, zone.Folder)}).");
            }
        }

        // Folder lookup order: the zone folder, the zone name without its numeric suffix,
        // then Default, then any loose .mp3 sitting in the root of the data folder.
        private List<string> CollectTracks(Zone zone)
        {
            var candidates = new List<string>
            {
                Path.Combine(_dataRoot, Sanitize(zone.Name)),
                Path.Combine(_dataRoot, zone.Folder),
                Path.Combine(_dataRoot, "Default"),
                _dataRoot
            };

            foreach (var folder in candidates)
            {
                var files = ReadFolder(folder, folder != _dataRoot);
                if (files.Count > 0) return files;
            }
            return new List<string>();
        }

        // Warns about anything the game will not play, naming the files, so a silent or hissing
        // zone can be traced to its cause instead of being hunted through the plugin.
        private void CheckPlaylistFormat(Zone zone)
        {
            var variable = new List<string>();
            var oddRate = new List<string>();

            for (var i = zone.Files.Count - 1; i >= 0; i--)
            {
                var path = zone.Files[i];

                int rate;
                bool isVariable;
                if (!InspectTrack(path, out rate, out isVariable)) continue;

                if (isVariable)
                {
                    variable.Add(Path.GetFileName(path));
                    if (_config.Streaming.SkipUnplayable) zone.Files.RemoveAt(i);
                }
                else if (rate != 44100)
                {
                    oddRate.Add($"{Path.GetFileName(path)} ({rate}Hz)");
                }
            }

            if (variable.Count > 0)
            {
                variable.Reverse();
                PrintWarning($"{zone.Name}: {variable.Count} of {zone.Files.Count + (_config.Streaming.SkipUnplayable ? variable.Count : 0)} track(s) use a variable bitrate, which Rust plays as static instead of music{(_config.Streaming.SkipUnplayable ? " (skipped)" : "")}: {string.Join(", ", variable.GetRange(0, Math.Min(3, variable.Count)).ToArray())}{(variable.Count > 3 ? ", ..." : "")}");
                PrintWarning("Re-encode them at a constant bitrate: ffmpeg -i in.mp3 -c:a libmp3lame -b:a 128k -ar 44100 -ac 2 out.mp3");
            }

            if (oddRate.Count > 0)
                PrintWarning($"{zone.Name}: {oddRate.Count} track(s) are not 44100 Hz, which some clients handle badly: {string.Join(", ", oddRate.GetRange(0, Math.Min(3, oddRate.Count)).ToArray())}");
        }

        private bool InspectTrack(string path, out int sampleRate, out bool variable)
        {
            sampleRate = 0;
            variable = false;

            try
            {
                byte[] head;
                using (var file = File.OpenRead(path))
                {
                    var length = (int)Math.Min(file.Length, 512 * 1024);
                    head = new byte[length];
                    file.Read(head, 0, length);
                }
                return Mp3.Inspect(head, out sampleRate, out variable);
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not inspect {Path.GetFileName(path)}: {ex.Message}");
                return false;
            }
        }

        private List<string> ReadFolder(string folder, bool recursive)
        {
            var files = new List<string>();
            try
            {
                if (!Directory.Exists(folder)) return files;
                files.AddRange(Directory.GetFiles(folder, "*.mp3",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));
                files.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                PrintError($"Could not read {folder}: {ex.Message}");
            }
            return files;
        }

        #endregion

        #region Streaming

        private bool StartStreaming()
        {
            if (_unloaded) return true;

            if (!_config.Streaming.Enabled)
            {
                Puts("Built in streaming is disabled, only zones with a custom stream URL will play.");
                return true;
            }

            IPAddress bind;
            if (!IPAddress.TryParse(_config.Streaming.BindAddress, out bind)) bind = IPAddress.Any;

            var server = new MusicServer(this, _config.Streaming.LogConnections);

            string error;
            if (!server.Bind(bind, _config.Streaming.Port, out error))
            {
                PrintError($"Could not open the music port {_config.Streaming.Port}: {error}");
                server.Stop();
                return false;
            }

            _server = server;

            var burst = Mathf.Clamp(_config.Streaming.BurstKb, 0, 512) * 1024;
            foreach (var zone in _zones)
            {
                if (zone.StreamUrl != null || zone.Files.Count == 0) continue;

                zone.Station = _server.AddStation(zone.Slug, zone.Name, zone.Files, _config.Streaming.Shuffle,
                    burst, Mathf.Max(1, _config.Streaming.MaxListeners));
                zone.StreamUrl = $"http://{_publicAddress}:{_config.Streaming.Port}/{zone.Slug}";
            }

            _server.BeginAccept();

            Puts($"Music server listening on {bind}:{_config.Streaming.Port}, streams published as http://{_publicAddress}:{_config.Streaming.Port}/<zone>");
            Puts("Remember to open that TCP port on the firewall, clients connect to it directly.");
            return true;
        }

        #endregion

        #region Server radio stations (boombox / jukebox / vehicle radios)

        // Everything in Rust that streams audio - the deployable Boom Box, the handheld Boom Box,
        // the Jukebox and vehicle radios - picks its stations from the same server side list
        // (boombox.serverurllist, "Name,Url,Name,Url"). Publishing the zone streams there is what
        // lets a player select the safe zone music on their own device.
        private void RegisterStations()
        {
            if (!_config.Stations.Register) return;

            _savedStationList = GetStationList() ?? string.Empty;

            var entries = new List<string>();
            if (_config.Stations.KeepExisting && !string.IsNullOrEmpty(_savedStationList))
                entries.Add(_savedStationList.Trim().Trim(','));

            foreach (var extra in _config.Stations.Extra)
            {
                if (string.IsNullOrEmpty(extra?.Name) || string.IsNullOrEmpty(extra.Url)) continue;
                entries.Add($"{CleanStationName(extra.Name)},{extra.Url.Trim()}");
            }

            foreach (var zone in _zones)
            {
                if (string.IsNullOrEmpty(zone.StreamUrl)) continue;
                entries.Add($"{CleanStationName(_config.Stations.Prefix + zone.Name)},{zone.StreamUrl}");
            }

            if (entries.Count == 0) return;

            var value = string.Join(",", entries.ToArray());
            if (SetStationList(value))
            {
                _stationListChanged = true;
                Puts($"Published {entries.Count} station(s) to the server radio list (boombox, jukebox, vehicle radios).");
                if (string.IsNullOrEmpty(_savedStationList))
                    PrintWarning("The server had no custom station list before, so this list now replaces the built in one. Add any vanilla stations you want to keep under \"Extra stations\" in the config.");
            }
        }

        private void RestoreStationList()
        {
            if (!_stationListChanged) return;
            SetStationList(_savedStationList ?? string.Empty);
            _stationListChanged = false;
        }

        private static string CleanStationName(string name)
        {
            return (name ?? string.Empty).Replace(',', ' ').Trim();
        }

        // Read and write through reflection when the field is where we expect it, and always
        // mirror the write through the console variable so the game refreshes its own cache.
        private string GetStationList()
        {
            try
            {
                var member = FindStationListField();
                if (member != null) return member.GetValue(null) as string;
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not read the current station list: {ex.Message}");
            }
            return string.Empty;
        }

        private bool SetStationList(string value)
        {
            var ok = false;
            try
            {
                var member = FindStationListField();
                if (member != null)
                {
                    member.SetValue(null, value);
                    ok = true;
                }
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not set the station list directly: {ex.Message}");
            }

            try
            {
                ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "boombox.serverurllist", value);
                ok = true;
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not run boombox.serverurllist: {ex.Message}");
            }
            return ok;
        }

        private static FieldInfo _stationListField;
        private static bool _stationListFieldSearched;

        private static FieldInfo FindStationListField()
        {
            if (_stationListFieldSearched) return _stationListField;
            _stationListFieldSearched = true;

            var fields = typeof(BoomBox).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var field in fields)
            {
                if (field.FieldType != typeof(string)) continue;
                if (field.Name.IndexOf("UrlList", StringComparison.OrdinalIgnoreCase) < 0) continue;
                _stationListField = field;
                break;
            }
            return _stationListField;
        }

        #endregion

        #region Safe zone speakers

        // One boombox per zone holds the stream, and it can sit buried out of sight because the
        // audible part of the rig is a set of connected speakers wired to it. Speakers replay
        // their source, so they stay in step with each other; independent boomboxes would each
        // open their own stream and phase against one another.
        private void SpawnSpeakers()
        {
            foreach (var zone in _zones)
            {
                if (!zone.WantsSpeaker || string.IsNullOrEmpty(zone.StreamUrl)) continue;
                SpawnZoneAudio(zone);
            }

            if (_speakers.Count > 0)
                Puts($"Spawned {_speakers.Count} audio entities across {_zones.Count(z => z.Source != null)} zone(s).");
        }

        private void SpawnZoneAudio(Zone zone)
        {
            var source = SpawnSource(zone);
            if (source == null) return;

            if (!_config.Speakers.UseConnectedSpeakers)
            {
                if (_config.Speakers.Hide && _config.Speakers.HideDepth > 10f)
                    PrintWarning($"{zone.Name}: connected speakers are off and the boombox is buried {_config.Speakers.HideDepth:0}m down, so nothing will be audible. Turn the speakers back on, or reduce the burial depth.");
                return;
            }

            var positions = BuildSpeakerLayout(zone);

            // A shallowly buried boombox is still an audio source, so a speaker sitting on top
            // of it just replays the same stream a moment later and you hear the track twice.
            // Bury it out of earshot instead and the centre speaker is welcome back.
            var sourceAudible = !_config.Speakers.Hide || Mathf.Abs(_config.Speakers.HideDepth) < 15f;
            if (sourceAudible)
            {
                var origin = source.transform.position;
                var removed = positions.RemoveAll(point => Distance2D(point, origin) < 15f);
                if (removed > 0)
                    Puts($"{zone.Name}: skipped {removed} speaker(s) next to the boombox, which is audible at {Mathf.Abs(_config.Speakers.HideDepth):0}m deep.");
            }

            var placed = 0;
            foreach (var position in positions)
            {
                if (SpawnConnectedSpeaker(zone, position, source) != null) placed++;
            }

            if (placed < positions.Count)
                PrintWarning($"{zone.Name}: only {placed} of {positions.Count} speakers could be placed.");
        }

        private BaseEntity SpawnSource(Zone zone)
        {
            var wanted = zone.SourceOverride ?? ShorePosition(zone, ChooseSourcePosition(zone));
            var ground = GroundLevel(wanted);
            var position = BuryPosition(wanted);

            Puts($"{zone.Name}: centre {zone.Center.x:0} {zone.Center.z:0}, radius {zone.Radius:0}, source at {position.x:0} {position.y:0.0} {position.z:0} (ground {ground:0.0}).");

            var entity = CreateEntity(_config.Speakers.Prefab, position);
            if (entity == null) return null;

            var boomBox = GetBoomBox(entity);
            if (boomBox == null)
            {
                PrintError($"'{_config.Speakers.Prefab}' has no BoomBox component, it cannot stream audio.");
                entity.Kill();
                return null;
            }

            var volume = SetVolume(entity);
            ApplyStation(entity, boomBox, zone.StreamUrl);

            // A static boombox finishes its own server side setup after Spawn() returns, and
            // that puts the prefab's default station (rustradio.facepunch.com) back over ours.
            // Re-apply once it has settled instead of waiting for the watchdog.
            foreach (var delay in new[] { 0.5f, 2f, 5f, 10f, 20f }) ReapplyStationLater(zone, entity, delay);

            var valid = "unknown";
            try { valid = BoomBox.IsStationValid(zone.StreamUrl).ToString(); } catch { }
            Puts($"{zone.Name}: volume {volume:0.00} (game allows {DeployableBoomBox.MinVolume:0.00} to {DeployableBoomBox.MaxVolume:0.00}), station accepted by the game: {valid}.");

            zone.Source = entity;
            Track(entity);
            return entity;
        }

        private Vector3 BuryPosition(Vector3 position)
        {
            var ground = GroundLevel(position);
            position.y = _config.Speakers.Hide
                ? ground - Mathf.Abs(_config.Speakers.HideDepth)
                : ground + _config.Speakers.HeightOffset;
            return position;
        }

        private BaseEntity SpawnConnectedSpeaker(Zone zone, Vector3 position, BaseEntity source)
        {
            position.y = GroundLevel(position) + _config.Speakers.SpeakerHeight;

            var speaker = CreateEntity(_config.Speakers.SpeakerPrefab, position);
            if (speaker == null) return null;

            if (!Wire(source, speaker))
            {
                PrintError($"Could not wire a speaker to the {zone.Name} source, so it would stay silent.");
                speaker.Kill();
                return null;
            }

            zone.Speakers.Add(speaker);
            Track(speaker);
            return speaker;
        }

        private BaseEntity CreateEntity(string prefab, Vector3 position)
        {
            BaseEntity entity;
            try
            {
                entity = GameManager.server.CreateEntity(prefab, position, Quaternion.identity);
            }
            catch (Exception ex)
            {
                PrintError($"Could not create '{prefab}': {ex.Message}");
                return null;
            }

            if (entity == null)
            {
                PrintError($"Unknown prefab '{prefab}'.");
                return null;
            }

            entity.enableSaving = false;
            entity.Spawn();
            return entity;
        }

        // Slot layout is prefab data, so the audio pair is found by matching slot types instead
        // of trusting an index that a Rust update could move.
        private bool Wire(BaseEntity sourceEntity, BaseEntity speakerEntity)
        {
            var source = sourceEntity as IOEntity;
            var speaker = speakerEntity as IOEntity;
            if (source?.outputs == null || speaker?.inputs == null) return false;

            for (var input = 0; input < speaker.inputs.Length; input++)
            {
                for (var output = 0; output < source.outputs.Length; output++)
                {
                    if (source.outputs[output].type != speaker.inputs[input].type) continue;

                    source.ConnectTo(speaker, output, input);
                    if (source.outputs[output].connectedTo.Get(true) != speaker) continue;

                    source.MarkDirtyForceUpdateOutputs();
                    source.SendIONetworkUpdate();
                    speaker.SendIONetworkUpdate();
                    return true;
                }
            }
            return false;
        }

        private void ReapplyStationLater(Zone zone, BaseEntity entity, float delay)
        {
            timer.Once(delay, () =>
            {
                if (entity == null || entity.IsDestroyed || zone.Source != entity) return;

                var boomBox = GetBoomBox(entity);
                if (boomBox == null || boomBox.CurrentRadioIp == zone.StreamUrl) return;

                PrintWarning($"{zone.Name}: boombox had reset itself to '{boomBox.CurrentRadioIp}' after {delay:0.#}s, re-applying our station.");
                ApplyStation(entity, boomBox, zone.StreamUrl);
            });
        }

        // Returns the volume actually applied, which the game clamps to its own range.
        private float SetVolume(BaseEntity entity)
        {
            if (_config.Speakers.Volume < 0f) return -1f;

            var deployable = entity as DeployableBoomBox;
            if (deployable == null) return -1f;

            try
            {
                var volume = Mathf.Clamp(_config.Speakers.Volume, DeployableBoomBox.MinVolume, DeployableBoomBox.MaxVolume);
                deployable.Volume = volume;
                entity.SendNetworkUpdate();
                return volume;
            }
            catch (Exception ex)
            {
                PrintError($"Could not set the speaker volume: {ex.Message}");
                return -1f;
            }
        }

        // Rings sized from the safe zone trigger, so a fishing village gets a single speaker
        // while Outpost gets a spread that actually reaches its edges.
        private List<Vector3> BuildSpeakerLayout(Zone zone)
        {
            var coverage = Mathf.Max(5f, _config.Speakers.CoverageRadius);
            var max = Mathf.Max(1, _config.Speakers.MaxSpeakers);
            var radius = zone.Radius > 0f ? zone.Radius : coverage;

            var keepAway = zone.KeepAway;
            var points = new List<Vector3>();
            if (keepAway <= 0f || DistanceToMonumentAudio(zone, zone.Center) >= keepAway) points.Add(zone.Center);
            if (radius <= coverage) return points.Count > 0 ? points : new List<Vector3> { zone.Center };

            for (var distance = coverage * 1.4f; distance < radius && points.Count < max; distance += coverage * 1.4f)
            {
                var count = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * distance / (coverage * 1.6f)), 3, 16);
                for (var i = 0; i < count && points.Count < max; i++)
                {
                    var angle = i * 360f / count;
                    var point = zone.Center + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * distance;
                    if (keepAway > 0f && DistanceToMonumentAudio(zone, point) < keepAway) continue;
                    points.Add(point);
                }
            }
            return points;
        }

        // Monument music is client side, but the objects that emit it still exist in the
        // server's copy of the monument with their audio components stripped. Finding them by
        // name is what lets the rig be placed away from them, instead of both playing at once.
        private static readonly string[] MonumentAudioNames = { "music_stage", "musiczone", "speaker" };

        private List<Vector3> FindMonumentAudio(Zone zone)
        {
            var found = new List<Vector3>();
            if (zone.KeepAway <= 0f && zone.Offset <= 0f) return found;

            var monuments = TerrainMeta.Path?.Monuments;
            if (monuments == null) return found;

            var radius = zone.Radius > 0f ? zone.Radius : 100f;

            foreach (var monument in monuments)
            {
                if (monument == null) continue;
                if (Distance2D(monument.transform.position, zone.Center) > radius + 300f) continue;

                var stack = new Stack<Transform>();
                stack.Push(monument.transform);

                var visited = 0;
                while (stack.Count > 0 && visited < 40000)
                {
                    var transform = stack.Pop();
                    visited++;

                    if (IsMonumentAudioName(transform.name) && Distance2D(transform.position, zone.Center) <= radius)
                        found.Add(transform.position);

                    for (var i = 0; i < transform.childCount; i++) stack.Push(transform.GetChild(i));
                }
            }

            if (found.Count > 0)
                Puts($"{zone.Name}: {found.Count} monument speaker(s) to keep clear of.");

            return found;
        }

        private static bool IsMonumentAudioName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            foreach (var candidate in MonumentAudioNames)
            {
                if (name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // A fishing village is built out over the sea, so its centre is water and a buried
        // boombox ends up under the seabed. Walk outwards to the nearest dry land instead.
        private Vector3 ShorePosition(Zone zone, Vector3 position)
        {
            if (!_config.Speakers.PreferLand || IsDryLand(position)) return position;

            var radius = zone.Radius > 0f ? zone.Radius : 60f;

            for (var distance = 10f; distance <= radius; distance += 10f)
            {
                for (var step = 0; step < 12; step++)
                {
                    var angle = step * 30f;
                    var candidate = zone.Center + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * distance;
                    if (!IsDryLand(candidate)) continue;

                    Puts($"{zone.Name}: centre is over water, moved the boombox {distance:0}m to the shore.");
                    return candidate;
                }
            }

            PrintWarning($"{zone.Name}: no dry land inside the zone, leaving the boombox in the water.");
            return position;
        }

        private static bool IsDryLand(Vector3 position)
        {
            // Water depth has to be sampled at the ground, not at the position handed in: ask
            // about a point already at the surface and the answer is always zero.
            var terrain = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(position) : position.y;

            try
            {
                return WaterLevel.GetWaterDepth(new Vector3(position.x, terrain, position.z), false, false, null) <= 0.2f;
            }
            catch
            {
                return terrain > 1f;
            }
        }

        private float BearingAwayFromMonumentAudio(Zone zone)
        {
            var nearest = Vector3.zero;
            var best = float.MaxValue;
            foreach (var emitter in zone.MonumentAudio)
            {
                var distance = Distance2D(emitter, zone.Center);
                if (distance >= best) continue;
                best = distance;
                nearest = emitter;
            }

            if (best == float.MaxValue) return 0f;

            var away = zone.Center - nearest;
            if (Mathf.Abs(away.x) < 0.01f && Mathf.Abs(away.z) < 0.01f) return 0f;

            return Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg;
        }

        private float DistanceToMonumentAudio(Zone zone, Vector3 position)
        {
            var nearest = float.MaxValue;
            foreach (var emitter in zone.MonumentAudio)
            {
                var distance = Distance2D(emitter, position);
                if (distance < nearest) nearest = distance;
            }
            return nearest;
        }

        // Where the monument's own audio cannot be found by name - the fishing villages and the
        // barns - the rig is simply moved off centre by a distance the config names, so the two
        // are not on top of each other.
        private Vector3 ChooseSourcePosition(Zone zone)
        {
            if (zone.Offset > 0f)
            {
                var bearing = zone.OffsetAngle;
                if (bearing < 0f) bearing = BearingAwayFromMonumentAudio(zone);

                var limit = zone.Radius > 0f ? Mathf.Max(0f, zone.Radius - 5f) : zone.Offset;
                var distance = Mathf.Min(zone.Offset, limit);
                var moved = zone.Center + Quaternion.Euler(0f, bearing, 0f) * Vector3.forward * distance;

                Puts($"{zone.Name}: rig moved {distance:0}m off centre on bearing {bearing:0}.");
                return moved;
            }

            var keepAway = zone.KeepAway;
            if (keepAway <= 0f || zone.MonumentAudio.Count == 0) return zone.Center;

            var best = zone.Center;
            var bestDistance = DistanceToMonumentAudio(zone, zone.Center);
            if (bestDistance >= keepAway) return best;

            var reach = zone.Radius > 0f ? zone.Radius * 0.8f : keepAway;

            for (var step = 0; step < 12; step++)
            {
                var angle = step * 30f;
                var candidate = zone.Center + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * reach;
                var distance = DistanceToMonumentAudio(zone, candidate);
                if (distance <= bestDistance) continue;

                bestDistance = distance;
                best = candidate;
            }

            Puts($"{zone.Name}: moved the source {Distance2D(zone.Center, best):0}m off centre, now {bestDistance:0}m from the monument's speakers.");
            return best;
        }

        private void Track(BaseEntity entity)
        {
            _speakers.Add(entity);
            UpdateProtectionHooks();
        }

        // The protection hooks sit on a hot path, so they are only subscribed while there is
        // something to protect.
        private void UpdateProtectionHooks()
        {
            var wanted = _config.Speakers.Protect && _speakers.Count > 0;
            if (wanted == _hooksActive) return;
            _hooksActive = wanted;

            if (wanted)
            {
                Subscribe(nameof(OnEntityTakeDamage));
                Subscribe(nameof(CanPickupEntity));
                Subscribe(nameof(CanLootEntity));
                return;
            }

            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(CanPickupEntity));
            Unsubscribe(nameof(CanLootEntity));
        }

        private void ApplyStation(BaseEntity entity, BoomBox boomBox, string url)
        {
            try
            {
                var io = entity as IOEntity;
                if (io != null) io.UpdateHasPower(io.ConsumptionAmount(), 0);

                boomBox.CurrentRadioIp = url;
                TogglePlay(boomBox, true);
                if (!boomBox.IsOn()) boomBox.SetFlag(BaseEntity.Flags.On, true);
                entity.SendNetworkUpdate();
            }
            catch (Exception ex)
            {
                PrintError($"Could not start playback on a speaker: {ex.Message}");
            }
        }

        private static MethodInfo _togglePlay;
        private static bool _togglePlaySearched;

        private static void TogglePlay(BoomBox boomBox, bool play)
        {
            if (!_togglePlaySearched)
            {
                _togglePlaySearched = true;
                _togglePlay = typeof(BoomBox).GetMethod("ServerTogglePlay",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(bool) }, null);
            }
            if (_togglePlay != null) _togglePlay.Invoke(boomBox, new object[] { play });
        }

        private static BoomBox GetBoomBox(BaseEntity entity)
        {
            var deployable = entity as DeployableBoomBox;
            if (deployable != null && deployable.BoxController != null) return deployable.BoxController;
            return entity.GetComponent<BoomBox>();
        }

        // Players can turn a boombox off and entities can go missing, so the state is
        // re-asserted on a timer rather than trusted after one shot setup.
        private void Watchdog()
        {
            foreach (var zone in _zones)
            {
                if (!zone.WantsSpeaker || string.IsNullOrEmpty(zone.StreamUrl)) continue;

                if (zone.Source == null || zone.Source.IsDestroyed)
                {
                    ClearZoneAudio(zone);
                    SpawnZoneAudio(zone);
                    continue;
                }

                var boomBox = GetBoomBox(zone.Source);
                if (boomBox == null) continue;
                if (boomBox.CurrentRadioIp == zone.StreamUrl && boomBox.IsOn()) continue;

                ApplyStation(zone.Source, boomBox, zone.StreamUrl);
            }
        }

        private void ClearZoneAudio(Zone zone)
        {
            foreach (var speaker in zone.Speakers)
            {
                _speakers.Remove(speaker);
                if (speaker == null || speaker.IsDestroyed) continue;
                try { speaker.Kill(); } catch { }
            }
            zone.Speakers.Clear();

            if (zone.Source != null)
            {
                _speakers.Remove(zone.Source);
                if (!zone.Source.IsDestroyed)
                {
                    try { zone.Source.Kill(); } catch { }
                }
            }

            zone.Source = null;
            UpdateProtectionHooks();
        }

        private void RemoveSpeakers()
        {
            foreach (var entity in _speakers)
            {
                if (entity == null || entity.IsDestroyed) continue;
                try { entity.Kill(); } catch { }
            }

            _speakers.Clear();
            foreach (var zone in _zones)
            {
                zone.Speakers.Clear();
                zone.Source = null;
            }
            UpdateProtectionHooks();
        }

        // The ground is the surface a player stands on: the terrain, or the water surface where
        // a monument is built out over the sea. A ray fired down from the sky is no good here,
        // because at Outpost or the Apartment Complex it stops on a roof tens of metres up.
        private static float GroundLevel(Vector3 position)
        {
            try
            {
                return WaterLevel.GetWaterOrTerrainSurface(position, false, false, null);
            }
            catch
            {
                // Fall through to the height map below.
            }

            if (TerrainMeta.HeightMap != null) return TerrainMeta.HeightMap.GetHeight(position);
            return position.y;
        }

        #endregion

        #region Protection hooks

        private object CanPickupEntity(BasePlayer player, BaseCombatEntity entity)
        {
            if (!_config.Speakers.Protect || !IsSpeaker(entity)) return null;
            return false;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!_config.Speakers.Protect || !IsSpeaker(entity)) return null;
            return true;
        }

        private object CanLootEntity(BasePlayer player, StorageContainer container)
        {
            if (!_config.Speakers.Protect || !IsSpeaker(container)) return null;
            return false;
        }

        private bool IsSpeaker(BaseEntity entity)
        {
            return entity != null && _speakers.Contains(entity);
        }

        #endregion

        #region Commands

        [ChatCommand("szmusic")]
        private void ChatCommand(BasePlayer player, string command, string[] args)
        {
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

            if (sub == "move" || sub == "reset" || sub == "restart")
            {
                if (!HasAccess(player))
                {
                    player.ChatMessage(Lang("NoPermission", player.UserIDString));
                    return;
                }

                if (sub == "move") MoveSourceHere(player);
                else if (sub == "restart") RestartPlayback(player);
                else ResetSource(player);
                return;
            }

            var zone = NearestZone(player.transform.position);
            if (zone == null)
            {
                player.ChatMessage(Lang("NotInZone", player.UserIDString));
                return;
            }

            var source = zone.Source;
            var alive = source != null && !source.IsDestroyed;
            var boomBox = alive ? GetBoomBox(source) : null;
            var distance = alive ? Vector3.Distance(source.transform.position, player.transform.position) : -1f;

            player.ChatMessage($"{zone.Name}: {(zone.Station == null ? "external stream" : zone.Station.NowPlaying)}");
            player.ChatMessage($"boombox {(alive ? $"{distance:0}m away" : "MISSING")}, playing={(boomBox != null && boomBox.IsOn())}, powered={(boomBox != null && boomBox.IsPowered())}, station={(boomBox != null && boomBox.CurrentRadioIp == zone.StreamUrl ? "ours" : "'" + (boomBox == null ? "?" : boomBox.CurrentRadioIp) + "'")}");
            player.ChatMessage($"url {zone.StreamUrl}, listeners {(zone.Station == null ? 0 : zone.Station.ListenerCount)}");

            if (alive && distance > _config.Speakers.CoverageRadius)
                player.ChatMessage($"you are further than {_config.Speakers.CoverageRadius:0}m from it, which is about as far as a boombox carries.");
        }

        [ConsoleCommand("szmusic.status")]
        private void CommandStatus(ConsoleSystem.Arg arg)
        {
            if (!HasAccess(arg)) return;

            var sb = new StringBuilder();
            sb.AppendLine($"SafeZoneMusic - {_zones.Count} zone(s), data folder {_dataRoot}");
            foreach (var zone in _zones)
            {
                var listeners = zone.Station != null ? zone.Station.ListenerCount.ToString() : "-";
                var track = zone.Station != null ? zone.Station.NowPlaying : "(external stream)";
                sb.AppendLine($"  {zone.Name}: {zone.Files.Count} track(s), {zone.Speakers.Count} speaker(s), {listeners} listener(s)");
                sb.AppendLine($"    centre {zone.Center.x:0} {zone.Center.y:0} {zone.Center.z:0}   url {zone.StreamUrl ?? "none"}");
                sb.AppendLine($"    playing {track}");
            }
            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("szmusic.skip")]
        private void CommandSkip(ConsoleSystem.Arg arg)
        {
            if (!HasAccess(arg)) return;

            var target = arg.GetString(0, "all");
            var skipped = 0;
            foreach (var zone in _zones)
            {
                if (zone.Station == null) continue;
                if (!target.Equals("all", StringComparison.OrdinalIgnoreCase) &&
                    !zone.Slug.Equals(Slugify(target), StringComparison.OrdinalIgnoreCase)) continue;

                zone.Station.Skip();
                skipped++;
            }
            arg.ReplyWith($"Skipped the current track on {skipped} station(s).");
        }

        [ConsoleCommand("szmusic.reload")]
        private void CommandReload(ConsoleSystem.Arg arg)
        {
            if (!HasAccess(arg)) return;

            _watchdog?.Destroy();
            RestoreStationList();
            RemoveSpeakers();
            _server?.Stop();
            _server = null;

            LoadConfig();
            OnServerInitialized();
            arg.ReplyWith("SafeZoneMusic reloaded.");
        }

        [ConsoleCommand("szmusic.stations")]
        private void CommandStations(ConsoleSystem.Arg arg)
        {
            if (!HasAccess(arg)) return;

            var sb = new StringBuilder("Stations published to boomboxes, jukeboxes and vehicle radios:");
            foreach (var zone in _zones)
            {
                if (string.IsNullOrEmpty(zone.StreamUrl)) continue;
                sb.AppendLine();
                sb.Append($"  {CleanStationName(_config.Stations.Prefix + zone.Name)} -> {zone.StreamUrl}");
            }
            arg.ReplyWith(sb.ToString());
        }

        private bool HasAccess(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || HasAccess(player)) return true;
            arg.ReplyWith("You do not have permission to use this command.");
            return false;
        }

        private bool HasAccess(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin));
        }

        // Moves the nearest zone's boombox to where the admin is standing, buried as usual, and
        // remembers it. A zone whose config entry is shared with others - the three fishing
        // villages all match one entry - gets its own entry pinned to its centre first, so the
        // move does not drag its siblings along.
        private void MoveSourceHere(BasePlayer player)
        {
            var zone = NearestZone(player.transform.position);
            if (zone == null)
            {
                player.ChatMessage(Lang("MoveNoZone", player.UserIDString));
                return;
            }

            var target = player.transform.position;
            var moved = zone.Source != null && !zone.Source.IsDestroyed
                ? Distance2D(zone.Source.transform.position, target)
                : Distance2D(zone.Center, target);

            zone.SourceOverride = target;
            PersistSourcePosition(zone, $"{target.x:0.##} {target.y:0.##} {target.z:0.##}");

            var source = zone.Source;
            if (source != null && !source.IsDestroyed && !_config.Speakers.UseConnectedSpeakers)
            {
                source.ServerPosition = BuryPosition(target);
                source.UpdateNetworkGroup();
                source.SendNetworkUpdateImmediate();

                var boomBox = GetBoomBox(source);
                if (boomBox != null) ApplyStation(source, boomBox, zone.StreamUrl);
            }
            else
            {
                ClearZoneAudio(zone);
                SpawnZoneAudio(zone);
            }

            var reach = _config.Speakers.CoverageRadius;
            var fromCentre = Distance2D(zone.Center, target);
            if (fromCentre > reach)
                player.ChatMessage($"{zone.Name}: that spot is {fromCentre:0}m from the zone centre and the boombox carries about {reach:0}m, so the middle of the monument will not hear it.");

            var depth = _config.Speakers.Hide ? Mathf.Abs(_config.Speakers.HideDepth) : 0f;
            player.ChatMessage(Lang("MoveDone", player.UserIDString, zone.Name, moved.ToString("0"), depth.ToString("0.#")));
            Puts($"{player.displayName} moved the {zone.Name} boombox to {target.x:0} {target.z:0}.");

            // Report what the boombox is actually doing once it has settled, so a move that
            // silently fails or falls back to the prefab's own station is visible immediately.
            timer.Once(2f, () =>
            {
                var source = zone.Source;
                if (source == null || source.IsDestroyed)
                {
                    var gone = $"{zone.Name}: the boombox did not survive the move.";
                    player.ChatMessage(gone);
                    PrintWarning(gone);
                    return;
                }

                var boomBox = GetBoomBox(source);
                var at = source.transform.position;
                var station = boomBox == null ? "no BoomBox component" : boomBox.CurrentRadioIp;
                var playing = boomBox != null && boomBox.IsOn();
                var ours = station == zone.StreamUrl;

                var report = $"{zone.Name}: boombox at {at.x:0} {at.y:0.0} {at.z:0}, playing={playing}, station={(ours ? "ours" : "'" + station + "'")}";
                player.ChatMessage(report);
                Puts(report);
            });
        }

        // Forces the client to tear the stream down and open it again: a boombox that a client
        // has already given up on will not start by itself just because the server is happy.
        private void RestartPlayback(BasePlayer player)
        {
            var zone = NearestZone(player.transform.position);
            if (zone == null || zone.Source == null || zone.Source.IsDestroyed)
            {
                player.ChatMessage(Lang("MoveNoZone", player.UserIDString));
                return;
            }

            var entity = zone.Source;
            var boomBox = GetBoomBox(entity);
            if (boomBox == null) return;

            TogglePlay(boomBox, false);
            boomBox.SetFlag(BaseEntity.Flags.On, false);
            entity.SendNetworkUpdateImmediate();

            timer.Once(1.5f, () =>
            {
                if (entity == null || entity.IsDestroyed) return;

                var box = GetBoomBox(entity);
                if (box == null) return;

                ApplyStation(entity, box, zone.StreamUrl);
                player.ChatMessage($"{zone.Name}: playback restarted, station reapplied.");
            });

            player.ChatMessage($"{zone.Name}: stopping and restarting the boombox...");
        }

        private void ResetSource(BasePlayer player)
        {
            var zone = NearestZone(player.transform.position);
            if (zone == null)
            {
                player.ChatMessage(Lang("MoveNoZone", player.UserIDString));
                return;
            }

            zone.SourceOverride = null;
            PersistSourcePosition(zone, string.Empty);

            ClearZoneAudio(zone);
            SpawnZoneAudio(zone);

            player.ChatMessage(Lang("ResetDone", player.UserIDString, zone.Name));
        }

        private void PersistSourcePosition(Zone zone, string position)
        {
            var cfg = zone.Config;

            if (cfg == null || _zones.Count(z => z.Config == cfg) > 1)
            {
                cfg = new ZoneConfig
                {
                    Name = zone.Name,
                    Enabled = true,
                    Folder = zone.Folder,
                    Keywords = new List<string>(),
                    Position = $"{zone.Center.x:0.##} {zone.Center.y:0.##} {zone.Center.z:0.##}",
                    Speaker = zone.WantsSpeaker,
                    KeepAway = zone.KeepAway,
                    Offset = zone.Offset,
                    OffsetAngle = zone.OffsetAngle
                };
                _config.Zones.Add(cfg);
                zone.Config = cfg;
                Puts($"Added a config entry for '{zone.Name}' so its placement is kept separate from the zones sharing its old entry.");
            }

            cfg.SourcePosition = position;
            SaveConfig();
        }

        private Zone NearestZone(Vector3 position)
        {
            Zone best = null;
            var bestDistance = float.MaxValue;

            foreach (var zone in _zones)
            {
                var origin = zone.Source != null && !zone.Source.IsDestroyed
                    ? zone.Source.transform.position
                    : zone.Center;

                var distance = Distance2D(origin, position);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = zone;
            }
            return best;
        }

        private Zone FindZoneAt(Vector3 position)
        {
            Zone best = null;
            var bestDistance = float.MaxValue;
            foreach (var zone in _zones)
            {
                var distance = Distance2D(zone.Center, position);
                var radius = zone.Radius > 0f ? zone.Radius : 100f;
                if (distance > radius || distance >= bestDistance) continue;
                bestDistance = distance;
                best = zone;
            }
            return best;
        }

        #endregion

        #region Music server

        // Small purpose built HTTP server. Rust clients open a plain HTTP connection to the URL
        // stored on the boombox and decode whatever MP3 bytes come back, so the stream is paced
        // frame by frame at real time speed the way an internet radio station does it.
        private class MusicServer
        {
            private readonly SafeZoneMusic _plugin;
            private readonly bool _logConnections;
            private readonly Dictionary<string, RadioStation> _stations = new Dictionary<string, RadioStation>(StringComparer.OrdinalIgnoreCase);

            private TcpListener _listener;
            private Thread _accept;
            private volatile bool _running;

            public MusicServer(SafeZoneMusic plugin, bool logConnections)
            {
                _plugin = plugin;
                _logConnections = logConnections;
            }

            public bool Bind(IPAddress bind, int port, out string error)
            {
                error = null;
                try
                {
                    _listener = new TcpListener(bind, port);
                    DisableHandleInheritance(_listener.Server);

                    // Every closed listener or status request leaves the port in TIME_WAIT on
                    // Windows, which otherwise blocks the rebind after a plugin reload for
                    // minutes and leaves the safe zones silent.
                    _listener.ExclusiveAddressUse = false;
                    _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                    _listener.Start();
                    _running = true;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    try { if (_listener != null) _listener.Stop(); } catch { }
                    _listener = null;
                    _running = false;
                    return false;
                }
            }

            private const uint HandleFlagInherit = 1;

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

            // Oxide compiles plugins in a child process (Oxide.Compiler.exe) that is started
            // while the previous instance is still listening. On Windows that child inherits
            // open socket handles, so the old listening socket stays alive after the plugin is
            // unloaded, wins incoming connections and answers none of them until the compiler
            // idles out. Marking the handle non inheritable keeps the port ours alone.
            private static void DisableHandleInheritance(Socket socket)
            {
                try
                {
                    if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
                    SetHandleInformation(socket.Handle, HandleFlagInherit, 0);
                }
                catch
                {
                    // Not fatal: without it a reload can be silent until the compiler exits.
                }
            }

            public void BeginAccept()
            {
                _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "SafeZoneMusic.accept" };
                _accept.Start();
            }

            public RadioStation AddStation(string slug, string title, List<string> files, bool shuffle, int burstBytes, int maxListeners)
            {
                var station = new RadioStation(_plugin, slug, title, files, shuffle, burstBytes, maxListeners);
                _stations[slug] = station;
                station.Start();
                return station;
            }

            public void Stop()
            {
                _running = false;

                var listener = _listener;
                _listener = null;
                if (listener != null)
                {
                    try { listener.Stop(); } catch (Exception ex) { _plugin.Warn($"Could not stop the listener: {ex.Message}"); }
                    try { listener.Server?.Close(); } catch { }
                }

                foreach (var station in _stations.Values) station.Stop();
                _stations.Clear();
            }

            private void AcceptLoop()
            {
                while (_running)
                {
                    try
                    {
                        var listener = _listener;
                        if (listener == null) return;

                        if (!listener.Pending())
                        {
                            Thread.Sleep(50);
                            continue;
                        }

                        var client = listener.AcceptTcpClient();
                        DisableHandleInheritance(client.Client);
                        ThreadPool.QueueUserWorkItem(Handshake, client);
                    }
                    catch
                    {
                        if (!_running) return;
                        Thread.Sleep(250);
                    }
                }
            }

            private void Handshake(object state)
            {
                var tcp = (TcpClient)state;
                NetworkStream stream = null;
                try
                {
                    tcp.NoDelay = true;
                    tcp.ReceiveTimeout = 5000;
                    tcp.SendTimeout = 15000;
                    stream = tcp.GetStream();

                    var path = ReadRequestPath(stream);
                    if (path == null)
                    {
                        Close(tcp);
                        return;
                    }

                    if (path.Length == 0 || path.Equals("status", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteText(stream, "200 OK", BuildIndex());
                        Close(tcp);
                        return;
                    }

                    RadioStation station;
                    if (!_stations.TryGetValue(path, out station))
                    {
                        WriteText(stream, "404 Not Found", "No such station.\n");
                        Close(tcp);
                        return;
                    }

                    if (station.ListenerCount >= station.MaxListeners)
                    {
                        WriteText(stream, "503 Service Unavailable", "Station is full.\n");
                        Close(tcp);
                        return;
                    }

                    var headers = new StringBuilder();
                    headers.Append("HTTP/1.0 200 OK\r\n");
                    headers.Append("Content-Type: audio/mpeg\r\n");
                    headers.Append($"icy-name: {station.Title}\r\n");
                    headers.Append("icy-genre: Game\r\n");
                    headers.Append("icy-pub: 0\r\n");
                    headers.Append("Cache-Control: no-cache\r\n");
                    headers.Append("Pragma: no-cache\r\n");
                    headers.Append("Connection: close\r\n\r\n");

                    var bytes = Encoding.ASCII.GetBytes(headers.ToString());
                    stream.Write(bytes, 0, bytes.Length);

                    if (_logConnections)
                    {
                        var remote = tcp.Client.RemoteEndPoint == null ? "?" : tcp.Client.RemoteEndPoint.ToString();
                        _plugin.LogFromThread($"{remote} started listening to '{station.Title}'.");
                    }

                    station.AddListener(tcp, stream);
                }
                catch
                {
                    Close(tcp);
                }
            }

            private static void Close(TcpClient tcp)
            {
                try { tcp.Close(); } catch { }
            }

            private string BuildIndex()
            {
                var sb = new StringBuilder("SafeZoneMusic stations\n");
                foreach (var station in _stations.Values)
                    sb.Append($"  /{station.Slug}  {station.Title}  ({station.ListenerCount} listener(s))  now playing: {station.NowPlaying}\n");
                return sb.ToString();
            }

            private static void WriteText(NetworkStream stream, string status, string body)
            {
                var payload = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.0 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                stream.Write(header, 0, header.Length);
                stream.Write(payload, 0, payload.Length);
            }

            // Reads the request head and returns the requested station slug, or null when the
            // request is not a GET / HEAD we can answer.
            private static string ReadRequestPath(NetworkStream stream)
            {
                var buffer = new byte[1];
                var head = new StringBuilder();

                while (head.Length < 8192)
                {
                    var read = stream.Read(buffer, 0, 1);
                    if (read <= 0) return null;

                    head.Append((char)buffer[0]);
                    var text = head.ToString();
                    if (text.EndsWith("\r\n\r\n") || text.EndsWith("\n\n")) break;
                }

                var first = head.ToString().Split('\n')[0].Trim();
                var parts = first.Split(' ');
                if (parts.Length < 2) return null;
                if (!parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    !parts[0].Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return null;

                var path = parts[1];
                var query = path.IndexOf('?');
                if (query >= 0) path = path.Substring(0, query);

                path = path.Trim('/');
                if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 4);

                try { path = Uri.UnescapeDataString(path); } catch { }
                return path;
            }
        }

        #endregion

        #region Radio station

        private class RadioStation
        {
            private const double ChunkSeconds = 0.25;
            private const double PrebufferSeconds = 2.0;

            private readonly SafeZoneMusic _plugin;
            private readonly List<string> _files;
            private readonly bool _shuffle;
            private readonly int _burstBytes;
            private readonly List<StreamClient> _clients = new List<StreamClient>();
            private readonly Queue<byte[]> _burst = new Queue<byte[]>();
            private readonly System.Random _random = new System.Random();

            private int _burstSize;
            private Thread _pump;
            private volatile bool _running;
            private volatile bool _skip;

            public readonly string Slug;
            public readonly string Title;
            public readonly int MaxListeners;
            public volatile string NowPlaying = string.Empty;

            public int ListenerCount
            {
                get { lock (_clients) return _clients.Count; }
            }

            public RadioStation(SafeZoneMusic plugin, string slug, string title, List<string> files, bool shuffle, int burstBytes, int maxListeners)
            {
                _plugin = plugin;
                Slug = slug;
                Title = title;
                _files = new List<string>(files);
                _shuffle = shuffle;
                _burstBytes = burstBytes;
                MaxListeners = maxListeners;
            }

            public void Start()
            {
                _running = true;
                _pump = new Thread(PumpLoop) { IsBackground = true, Name = $"SafeZoneMusic.{Slug}" };
                _pump.Start();
            }

            public void Stop()
            {
                _running = false;
                lock (_clients)
                {
                    foreach (var client in _clients) client.Close();
                    _clients.Clear();
                }
                _pump = null;
            }

            public void Skip() => _skip = true;

                public void AddListener(TcpClient tcp, NetworkStream stream)
            {
                var client = new StreamClient(tcp, stream);

                // Hold the client list across the whole handover. Snapshotting the burst and
                // then joining the list leaves a gap: anything broadcast in between is never
                // sent to this listener, and the missing frames arrive as a burst of noise.
                lock (_clients)
                {
                    byte[][] warmup;
                    lock (_burst) warmup = _burst.ToArray();

                    foreach (var chunk in warmup)
                    {
                        if (client.Send(chunk, 0, chunk.Length)) continue;
                        client.Close();
                        return;
                    }

                    _clients.Add(client);
                }
            }

            private void PumpLoop()
            {
                var order = BuildOrder();
                var index = 0;

                while (_running)
                {
                    if (order.Count == 0)
                    {
                        Thread.Sleep(1000);
                        order = BuildOrder();
                        continue;
                    }

                    if (index >= order.Count)
                    {
                        order = BuildOrder();
                        index = 0;
                    }

                    PlayTrack(order[index++]);
                }
            }

            private List<string> BuildOrder()
            {
                var order = new List<string>(_files);
                if (!_shuffle) return order;

                for (var i = order.Count - 1; i > 0; i--)
                {
                    var j = _random.Next(i + 1);
                    var swap = order[i];
                    order[i] = order[j];
                    order[j] = swap;
                }
                return order;
            }

            // Walks the file frame by frame and sends the audio at the speed it plays back, so
            // every listener stays roughly in sync and nobody can outrun the stream.
            private void PlayTrack(string path)
            {
                byte[] data;
                try
                {
                    data = File.ReadAllBytes(path);
                }
                catch (Exception ex)
                {
                    _plugin.LogFromThread($"Could not read {path}: {ex.Message}");
                    Thread.Sleep(1000);
                    return;
                }

                NowPlaying = Path.GetFileNameWithoutExtension(path);
                _skip = false;

                var position = Mp3.SkipLeadingTag(data);
                var end = Mp3.TrimTrailingTag(data);
                var started = DateTime.UtcNow;
                var emitted = 0.0;
                var junk = 0;

                while (_running && !_skip && position < end)
                {
                    var chunkStart = position;
                    var chunkSeconds = 0.0;

                    while (position < end && chunkSeconds < ChunkSeconds)
                    {
                        int length, sampleRate, samples;
                        if (Mp3.TryReadFrame(data, position, end, out length, out sampleRate, out samples))
                        {
                            position += length;
                            chunkSeconds += (double)samples / sampleRate;
                            junk = 0;
                            continue;
                        }

                        position++;
                        if (++junk < 262144) continue;

                        _plugin.LogFromThread($"'{Path.GetFileName(path)}' contains no readable MPEG layer III frames, skipping it. Re-encode it as a normal .mp3.");
                        return;
                    }

                    if (position > chunkStart) Broadcast(data, chunkStart, position - chunkStart);
                    emitted += chunkSeconds;

                    var ahead = emitted - (DateTime.UtcNow - started).TotalSeconds - PrebufferSeconds;
                    if (ahead > 0) Thread.Sleep((int)(ahead * 1000));
                }
            }

            private void Broadcast(byte[] data, int offset, int count)
            {
                var chunk = new byte[count];
                Buffer.BlockCopy(data, offset, chunk, 0, count);

                lock (_clients)
                {
                    for (var i = _clients.Count - 1; i >= 0; i--)
                    {
                        if (_clients[i].Send(chunk, 0, count)) continue;
                        _clients[i].Close();
                        _clients.RemoveAt(i);
                    }
                }

                lock (_burst)
                {
                    _burst.Enqueue(chunk);
                    _burstSize += count;
                    while (_burstSize > _burstBytes && _burst.Count > 0) _burstSize -= _burst.Dequeue().Length;
                }
            }
        }

        private class StreamClient
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _stream;

            public StreamClient(TcpClient tcp, NetworkStream stream)
            {
                _tcp = tcp;
                _stream = stream;
                try { tcp.LingerState = new LingerOption(true, 0); } catch { }
            }

            public bool Send(byte[] buffer, int offset, int count)
            {
                try
                {
                    _stream.Write(buffer, offset, count);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            public void Close()
            {
                try { _stream.Close(); } catch { }
                try { _tcp.Close(); } catch { }
            }
        }

        #endregion

        #region MP3 frame reader

        private static class Mp3
        {
            private static readonly int[] BitrateV1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
            private static readonly int[] BitrateV2L3 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
            private static readonly int[] SampleRateV1 = { 44100, 48000, 32000, 0 };
            private static readonly int[] SampleRateV2 = { 22050, 24000, 16000, 0 };
            private static readonly int[] SampleRateV25 = { 11025, 12000, 8000, 0 };

            // ID3v2 tags can hold megabytes of cover art. Sending those down an audio stream
            // wastes bandwidth and confuses some decoders, so they are skipped.
            public static int SkipLeadingTag(byte[] data)
            {
                if (data.Length < 10) return 0;
                if (data[0] != 'I' || data[1] != 'D' || data[2] != '3') return 0;

                var size = ((data[6] & 0x7F) << 21) | ((data[7] & 0x7F) << 14) | ((data[8] & 0x7F) << 7) | (data[9] & 0x7F);
                var skip = 10 + size;
                if ((data[5] & 0x10) != 0) skip += 10;
                return skip > 0 && skip < data.Length ? skip : 0;
            }

            public static int TrimTrailingTag(byte[] data)
            {
                if (data.Length > 128 && data[data.Length - 128] == 'T' && data[data.Length - 127] == 'A' && data[data.Length - 126] == 'G')
                    return data.Length - 128;
                return data.Length;
            }


            // Rust's stream decoder needs a constant bitrate: a variable bitrate file is valid
            // MPEG layer III, decodes cleanly in any desktop player, and still arrives in game
            // as static. Frames are walked here purely to spot that before it reaches a player.
            public static bool Inspect(byte[] data, out int sampleRate, out bool variable)
            {
                sampleRate = 0;
                variable = false;

                var position = SkipLeadingTag(data);
                var end = TrimTrailingTag(data);
                var frames = 0;
                var bitrate = 0;

                while (position < end - 4 && frames < 400)
                {
                    if (data[position] != 0xFF || (data[position + 1] & 0xE0) != 0xE0)
                    {
                        position++;
                        continue;
                    }

                    var version = (data[position + 1] >> 3) & 0x03;
                    var layer = (data[position + 1] >> 1) & 0x03;
                    var bitrateIndex = (data[position + 2] >> 4) & 0x0F;
                    var sampleIndex = (data[position + 2] >> 2) & 0x03;
                    var padding = (data[position + 2] >> 1) & 0x01;

                    if (version == 1 || layer != 1 || bitrateIndex == 0 || bitrateIndex == 15 || sampleIndex == 3)
                    {
                        position++;
                        continue;
                    }

                    var version1 = version == 3;
                    var frameBitrate = (version1 ? BitrateV1L3[bitrateIndex] : BitrateV2L3[bitrateIndex]) * 1000;
                    var frameRate = version1 ? SampleRateV1[sampleIndex] : (version == 2 ? SampleRateV2[sampleIndex] : SampleRateV25[sampleIndex]);
                    if (frameBitrate == 0 || frameRate == 0)
                    {
                        position++;
                        continue;
                    }

                    var length = (version1 ? 144 * frameBitrate / frameRate : 72 * frameBitrate / frameRate) + padding;
                    if (length < 4 || position + length > end)
                    {
                        position++;
                        continue;
                    }

                    // The Xing/Info header frame of a VBR file carries no audio, so its bitrate
                    // is not part of the comparison.
                    var isXing = HasTag(data, position, length, "Xing") || HasTag(data, position, length, "Info");

                    if (!isXing)
                    {
                        if (frames == 0)
                        {
                            bitrate = frameBitrate;
                            sampleRate = frameRate;
                        }
                        else if (frameBitrate != bitrate)
                        {
                            variable = true;
                        }
                        frames++;
                    }

                    position += length;
                }

                return frames > 0;
            }

            private static bool HasTag(byte[] data, int offset, int length, string tag)
            {
                for (var i = offset + 4; i < offset + length - 4 && i < offset + 64; i++)
                {
                    if (data[i] != tag[0]) continue;
                    if (data[i + 1] == tag[1] && data[i + 2] == tag[2] && data[i + 3] == tag[3]) return true;
                }
                return false;
            }

            public static bool TryReadFrame(byte[] data, int offset, int end, out int length, out int sampleRate, out int samples)
            {
                length = 0;
                sampleRate = 0;
                samples = 0;

                if (offset + 4 > end) return false;
                if (data[offset] != 0xFF || (data[offset + 1] & 0xE0) != 0xE0) return false;

                var version = (data[offset + 1] >> 3) & 0x03;   // 0 = 2.5, 1 = reserved, 2 = 2, 3 = 1
                var layer = (data[offset + 1] >> 1) & 0x03;     // 1 = layer III
                if (version == 1 || layer != 1) return false;

                var bitrateIndex = (data[offset + 2] >> 4) & 0x0F;
                var sampleIndex = (data[offset + 2] >> 2) & 0x03;
                var padding = (data[offset + 2] >> 1) & 0x01;
                if (bitrateIndex == 0 || bitrateIndex == 15 || sampleIndex == 3) return false;

                var version1 = version == 3;
                var bitrate = (version1 ? BitrateV1L3[bitrateIndex] : BitrateV2L3[bitrateIndex]) * 1000;
                sampleRate = version1 ? SampleRateV1[sampleIndex] : (version == 2 ? SampleRateV2[sampleIndex] : SampleRateV25[sampleIndex]);
                if (bitrate == 0 || sampleRate == 0) return false;

                samples = version1 ? 1152 : 576;
                length = (version1 ? 144 * bitrate / sampleRate : 72 * bitrate / sampleRate) + padding;
                if (length < 4 || offset + length > end) return false;

                // A real frame is followed by another sync word, which keeps the resync scan honest.
                var next = offset + length;
                if (next + 1 < end && (data[next] != 0xFF || (data[next + 1] & 0xE0) != 0xE0)) return false;

                return true;
            }
        }

        #endregion

        #region Helpers

        // Called from the streaming threads, so the message is handed back to the main thread.
        internal void Warn(string message) => PrintWarning(message);

        private void LogFromThread(string message)
        {
            Interface.Oxide.NextTick(() => PrintWarning(message));
        }

        private static float Distance2D(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Default";

            var sb = new StringBuilder(name.Length);
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "Default" : result;
        }

        private static string Slugify(string name)
        {
            if (string.IsNullOrEmpty(name)) return "zone";

            var sb = new StringBuilder(name.Length);
            foreach (var c in name.ToLowerInvariant())
            {
                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9') sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }

            var slug = sb.ToString().Trim('-');
            return string.IsNullOrEmpty(slug) ? "zone" : slug;
        }

        private static bool TryParseVector(string value, out Vector3 result)
        {
            result = Vector3.zero;
            if (string.IsNullOrEmpty(value)) return false;

            var parts = value.Replace('(', ' ').Replace(')', ' ').Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;

            float x, y, z;
            var style = System.Globalization.NumberStyles.Float;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0], style, culture, out x)) return false;
            if (!float.TryParse(parts[1], style, culture, out y)) return false;
            if (!float.TryParse(parts[2], style, culture, out z)) return false;

            result = new Vector3(x, y, z);
            return true;
        }

        private string Lang(string key, string userId = null, params object[] args)
        {
            var message = lang.GetMessage(key, this, userId);
            return args.Length == 0 ? message : string.Format(message, args);
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotInZone"] = "You are not standing in a safe zone that has music.",
                ["NowPlaying"] = "{0} is playing: {1}",
                ["ZoneNoTrack"] = "{0} has no track playing right now.",
                ["MoveDone"] = "{0}: boombox moved here, {1}m from where it was, buried {2}m down.",
                ["ResetDone"] = "{0}: boombox placement is back to automatic.",
                ["MoveNoZone"] = "No safe zone with music was found on this map.",
                ["NoPermission"] = "You do not have permission to do that."
            }, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotInZone"] = "Vous n'etes pas dans une zone sure avec de la musique.",
                ["NowPlaying"] = "{0} joue : {1}",
                ["ZoneNoTrack"] = "{0} ne joue aucune piste pour le moment.",
                ["MoveDone"] = "{0} : boombox deplacee ici, a {1}m de sa position, enterree a {2}m.",
                ["ResetDone"] = "{0} : placement de la boombox remis en automatique.",
                ["MoveNoZone"] = "Aucune zone sure avec musique sur cette carte.",
                ["NoPermission"] = "Vous n'avez pas la permission de faire cela."
            }, this, "fr");
        }

        #endregion
    }
}
