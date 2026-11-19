#nullable disable
// Portiert aus dem Unity-Projekt (Runtime/Online/TDU2Net.cs). JsonUtility -> NetJson (System.Text.Json, gleiche Feldnamen),
// PlayerPrefs (Profilliste) -> Datei im Config-Ordner (UserPaths). Neu gegenüber Unity: Offline-Profile (Original-Popup
// "gamespy_off") – ohne Server, der Spielstand liegt dann nur in der lokalen DATA (TDU2SaveGame).
using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paradise.Core.Online
{
    /// <summary>JSON wie Unitys JsonUtility: öffentliche Felder, Feldname = JSON-Name.</summary>
    public static class NetJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        public static string ToJson<T>(T v) => JsonSerializer.Serialize(v, Options);
        public static T FromJson<T>(string s) => JsonSerializer.Deserialize<T>(s, Options);
        /// <summary>Tiefe Kopie über JSON (wie JsonUtility.FromJson(JsonUtility.ToJson(x))).</summary>
        public static T Copy<T>(T v) => v == null ? v : FromJson<T>(ToJson(v));
    }

    /// <summary>
    /// TCP-Verbindung zum TDU2Server: eine JSON-Zeile je Anfrage/Antwort. Anfragen laufen nacheinander im Hintergrund
    /// (Send liefert einen Task); bei einem Verbindungsfehler wird beim nächsten Send neu verbunden (die Server-Sitzung
    /// ist dann weg – <see cref="TDU2Session"/> meldet sich mit dem Merk-Token neu an).
    /// </summary>
    public sealed class TDU2NetClient : IDisposable
    {
        public readonly string Host;
        public readonly int Port;
        public int TimeoutMs = 8000;
        readonly object _lock = new object();
        TcpClient _tcp;
        StreamReader _reader;
        StreamWriter _writer;
        int _generation;

        public TDU2NetClient(string host, int port) { Host = host; Port = port; }

        public bool Connected { get { lock (_lock) return _tcp != null && _tcp.Connected; } }
        /// <summary>Zählt jede neue Verbindung (Sitzung des Servers hängt an der Verbindung).</summary>
        public int Generation => _generation;

        public Task<NetResponse> Send(NetRequest req) => Task.Run(() => SendBlocking(req));

        public NetResponse SendBlocking(NetRequest req)
        {
            lock (_lock)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        EnsureConnected();
                        _writer.Write(NetJson.ToJson(req));
                        _writer.Write('\n');
                        _writer.Flush();
                        string line = _reader.ReadLine();
                        if (line == null) throw new IOException("Verbindung vom Server getrennt");
                        var resp = NetJson.FromJson<NetResponse>(line) ?? NetResponse.Error("bad_response");
                        resp.error ??= "";
                        return resp;
                    }
                    catch (Exception ex)
                    {
                        CloseLocked();
                        // ein zweiter Versuch nur, wenn eine alte Verbindung kaputt war
                        if (attempt == 1 || !(ex is IOException || ex is SocketException || ex is ObjectDisposedException))
                            return NetResponse.Error("net:" + ex.Message);
                    }
                }
                return NetResponse.Error("net");
            }
        }

        bool EnsureConnected()
        {
            if (_tcp != null && _tcp.Connected) return false;
            CloseLocked();
            var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = TimeoutMs, SendTimeout = TimeoutMs };
            var t = tcp.ConnectAsync(Host, Port);
            if (!t.Wait(4000) || !tcp.Connected) { try { tcp.Close(); } catch (Exception) { } throw new SocketException((int)SocketError.TimedOut); }
            var s = tcp.GetStream();
            _tcp = tcp;
            _reader = new StreamReader(s, new UTF8Encoding(false));
            _writer = new StreamWriter(s, new UTF8Encoding(false));
            _generation++;
            return true;
        }

        void CloseLocked()
        {
            try { _reader?.Dispose(); } catch (Exception) { }
            try { _writer?.Dispose(); } catch (Exception) { }
            try { _tcp?.Close(); } catch (Exception) { }
            _reader = null; _writer = null; _tcp = null;
        }

        public void Dispose() { lock (_lock) CloseLocked(); }
    }

    /// <summary>
    /// Angemeldeter Spieler: Konto, Server-Sitzung und Spielstand. Wird vom Login (GAME_MODE_LOGIN) gesetzt und von
    /// Händlern/Garage (eigene Autos), Pausemenü (Geld, Profilname) und dem Autosave benutzt.
    /// Offline-Profil: kein Server, <see cref="Offline"/> = true, Speichern schreibt nur die lokale DATA.
    /// </summary>
    public static class TDU2Session
    {
        public static TDU2NetClient Client { get; private set; }
        public static string Nick { get; private set; }
        public static string Token { get; private set; }
        public static string SessionId { get; private set; }
        public static SaveData Save { get; private set; }
        /// <summary>Antwort des Servers beim Anmelden (Startautos, Start-Spot, Startgeld, MOTD).</summary>
        public static NetResponse Config { get; private set; }
        public static int Online { get; set; }
        /// <summary>Offline-Profil (Original "gamespy_off"): ohne Server, Spielstand nur lokal.</summary>
        public static bool Offline { get; private set; }
        /// <summary>Spieler-Nummer und Schlüssel für die UDP-Synchronisation (vom Server beim Anmelden).</summary>
        public static int PlayerId { get; private set; }
        public static uint UdpKey { get; private set; }
        public static int UdpPort { get; private set; }
        public static bool LoggedIn => Save != null && !string.IsNullOrEmpty(SessionId);
        public static event Action Changed;

        // ---- Radio-Zeitbasis (vom Server): gemeinsame Playlist aller Clients (Hariba/RoadRock)
        public static long RadioEpochMs { get; private set; }
        public static int RadioSeed { get; private set; }
        static long _radioSkewMs;   // Serverzeit − lokale Uhr
        public static bool HasRadioClock => RadioEpochMs != 0;
        public static double RadioElapsedSeconds =>
            ((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _radioSkewMs) - RadioEpochMs) / 1000.0;
        /// <summary>Serveruhr in Sekunden (UTC, um die gemessene Abweichung korrigiert) – gemeinsame Zeitbasis z.B. für die Ampeln.</summary>
        public static double ServerSeconds => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _radioSkewMs) / 1000.0;

        public static void ApplyRadio(NetResponse r)
        {
            if (r == null) return;
            if (r.radioEpoch != 0) { RadioEpochMs = r.radioEpoch; RadioSeed = r.radioSeed; }
            if (r.serverTime != 0) _radioSkewMs = r.serverTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public static void Connect(string host, int port)
        {
            if (string.IsNullOrEmpty(host) || port <= 0) return;
            if (Client != null && Client.Host == host && Client.Port == port) return;
            Client?.Dispose();
            Log.Info($"TDU2Session.Connect({host}:{port})");
            Client = new TDU2NetClient(host, port);
        }

        public static void Begin(NetResponse r)
        {
            Nick = r.nick; Token = r.token; SessionId = r.session;
            Save = r.save ?? new SaveData();
            Normalize(Save);
            Config = r;
            Online = r.online;
            PlayerId = r.playerId; UdpKey = (uint)r.udpKey; UdpPort = r.udpPort;
            Offline = false;
            ApplyRadio(r);
            Changed?.Invoke();
        }

        /// <summary>Offline-Profil anmelden: Spielstand aus der lokalen DATA (bzw. neu), keine Server-Sitzung.</summary>
        public static void BeginOffline(string nick, SaveData save)
        {
            Nick = nick; Token = ""; SessionId = "offline";
            Save = save ?? new SaveData();
            Normalize(Save);
            Config = new NetResponse { ok = true, nick = nick, save = Save };
            Online = 0; PlayerId = 0; UdpKey = 0; UdpPort = 0;
            Offline = true;
            Changed?.Invoke();
        }

        /// <summary>Fehlende Listen anlegen (Server/ältere Spielstände liefern evtl. null).</summary>
        public static void Normalize(SaveData s)
        {
            if (s == null) return;
            s.ownedCars ??= new List<string>();
            s.odometers ??= new List<OdoEntry>();
            s.liveries ??= new List<LiveryEntry>();
            s.stickerBoards ??= new List<int>();
            s.favoriteColors ??= new List<int>();
            s.ownedClothes ??= new List<int>();
            s.bookmarks ??= new List<BookmarkEntry>();
            s.favoriteSpots ??= new List<string>();
            s.houses ??= new List<HouseLayout>();
            s.ownedFurniture ??= new List<int>();
            s.ownedMaterials ??= new List<int>();
            s.myLife ??= new MyLifeData();
            s.carConfigs ??= new List<CarConfigEntry>();
            s.myLife.boxes ??= new List<string>();
            s.myLife.fields ??= new List<HouseEntry>();
            s.raceCinSeen ??= new List<string>();
            s.racing ??= new List<HouseEntry>();
            s.recentPlayers ??= new List<RecentEntry>();
            s.friendNews ??= new List<FriendNewsEntry>();
            s.storyAvatar ??= "";
            s.island ??= ""; s.currentCar ??= ""; s.avatar ??= ""; s.interior ??= ""; s.savedAt ??= "";
        }

        public static void End()
        {
            Nick = null; Token = null; SessionId = null; Save = null; PlayerId = 0; UdpKey = 0; Offline = false;
            RadioEpochMs = 0; RadioSeed = 0; _radioSkewMs = 0;
            Changed?.Invoke();
        }

        // ---- eigene Autos
        public static IReadOnlyList<string> OwnedCars => Save != null ? (IReadOnlyList<string>)Save.ownedCars : Array.Empty<string>();

        public static void AddOwned(string carFile)
        {
            if (Save == null || string.IsNullOrEmpty(carFile)) return;
            if (!Save.ownedCars.Exists(c => string.Equals(c, carFile, StringComparison.OrdinalIgnoreCase))) Save.ownedCars.Add(carFile);
            Changed?.Invoke();
        }

        public static void SetCurrentCar(string carFile)
        {
            if (Save == null || string.IsNullOrEmpty(carFile)) return;
            Save.currentCar = carFile;
        }

        // ---- Speichern
        static Task<NetResponse> _saving;
        /// <summary>Speichern beginnt (für das Speicher-Symbol saving_logo).</summary>
        public static event Action SaveStarted;
        public static bool Saving => _saving != null && !_saving.IsCompleted;
        /// <summary>Kopie des Spielstands, die gerade gespeichert wird (Hauptthread; TDU2SaveGame schreibt sie als Original-DATA).</summary>
        public static event Action<string, SaveData> SaveCopied;

        /// <summary>
        /// Spielstand speichern (im Hintergrund): online an den Server (ist die Sitzung weg, erst mit dem Token neu anmelden),
        /// in beiden Fällen zusätzlich die lokale DATA (SaveCopied -> TDU2SaveGame).
        /// </summary>
        public static Task<NetResponse> SaveAsync()
        {
            if (!LoggedIn) return Task.FromResult(NetResponse.Error("not_logged_in"));
            if (Saving) return _saving;
            Save.savedAt = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            var copy = NetJson.Copy(Save);   // Kopie im Hauptthread ziehen
            try { SaveCopied?.Invoke(Nick, NetJson.Copy(Save)); }
            catch (Exception e) { Log.Warn("Savegame: " + e.Message); }
            SaveStarted?.Invoke();
            if (Offline || Client == null) { _saving = Task.FromResult(new NetResponse { ok = true, nick = Nick }); return _saving; }
            var client = Client; string nick = Nick, token = Token, session = SessionId;
            _saving = Task.Run(() =>
            {
                var r = client.SendBlocking(new NetRequest { op = "save", session = session, save = copy });
                if (!r.ok && (r.error == "not_logged_in" || r.error.StartsWith("net")))
                {
                    var l = client.SendBlocking(new NetRequest { op = "login", nick = nick, token = token });
                    if (!l.ok) return l;
                    SessionId = l.session;
                    PlayerId = l.playerId; UdpKey = (uint)l.udpKey;
                    r = client.SendBlocking(new NetRequest { op = "save", session = l.session, save = copy });
                }
                return r;
            });
            return _saving;
        }

        public static void Logout()
        {
            if (Offline || Client == null || string.IsNullOrEmpty(SessionId)) return;
            try { Client.SendBlocking(new NetRequest { op = "logout", session = SessionId }); } catch (Exception) { }
        }

        // ---- lokale Profile (Merk-Token je Konto, wie die Profilliste im Spiel)
        /// <summary>Profil auf diesem PC. online = Konto auf dem Server (Häkchen connected_profileN), sonst Offline-Profil.</summary>
        public sealed class LocalProfile { public string nick = ""; public string token = ""; public bool online = true; }
        sealed class ProfileList { public List<LocalProfile> list = new List<LocalProfile>(); public string last = ""; }

        /// <summary>Unity: PlayerPrefs "TDU2.Profiles" – hier Documents\KuxiiSoft\Test Drive Unlimited 2\Config\profiles.json.</summary>
        static string ProfilesFile => Path.Combine(UserPaths.Config, "profiles.json");

        public static List<LocalProfile> LoadProfiles(out string last)
        {
            last = "";
            try
            {
                if (File.Exists(ProfilesFile))
                {
                    var p = NetJson.FromJson<ProfileList>(File.ReadAllText(ProfilesFile));
                    if (p != null && p.list != null) { last = p.last ?? ""; p.list.RemoveAll(x => x == null || string.IsNullOrEmpty(x.nick)); return p.list; }
                }
            }
            catch (Exception e) { Log.Warn("Profile: " + ProfilesFile + " nicht lesbar: " + e.Message); }
            return new List<LocalProfile>();
        }

        public static void StoreProfiles(List<LocalProfile> list, string last)
        {
            try { File.WriteAllText(ProfilesFile, NetJson.ToJson(new ProfileList { list = list, last = last ?? "" })); }
            catch (Exception e) { Log.Warn("Profile: " + ProfilesFile + " nicht schreibbar: " + e.Message); }
        }

        /// <summary>Fehlercodes des Servers als Text für die Original-Oberfläche.</summary>
        public static string ErrorText(string error, bool german)
        {
            if (string.IsNullOrEmpty(error)) return "";
            if (error.StartsWith("net")) return german ? "Server nicht erreichbar" : "Server not reachable";
            switch (error)
            {
                case "no_server": return german ? "Kein Server eingestellt – Offline-Profil anlegen" : "No server set – create an offline profile";
                case "invalid_nick": return german ? "Name: 3-16 Zeichen (A-Z, 0-9, _ - .)" : "Name: 3-16 characters (A-Z, 0-9, _ - .)";
                case "weak_password": return german ? "Passwort: mindestens 4 Zeichen" : "Password: at least 4 characters";
                case "nick_taken": return german ? "Dieser Name ist schon vergeben" : "This name is already taken";
                case "unknown_nick": return german ? "Konto nicht gefunden" : "Account not found";
                case "bad_password": return german ? "Falsches Passwort" : "Wrong password";
                case "bad_token": return german ? "Bitte Passwort eingeben" : "Please enter your password";
                case "server_full": return german ? "Server voll" : "Server full";
            }
            return error;
        }
    }
}
