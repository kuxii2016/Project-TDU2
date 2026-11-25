#nullable disable
// Portiert aus dem Unity-Projekt (Runtime/Savegame/TDU2SaveGame.cs). Unterschiede: Ablage unter UserPaths.Saves,
// Story-Schritt direkt aus SaveData.story (TDU2StoryProgress folgt mit der Geschichte), Avatar bleibt wie in der Vorlage,
// bis die Avatare (AvatarConfig/AvatarLibrary) portiert sind.
using EdenSavegame;
using Paradise.Core.Avatars;
using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using System.Globalization;
using System.IO.Compression;

namespace Paradise.Core.Savegame
{
    /// <summary>
    /// Spielstand im Original-Format von TDU2 (PLAYERSAVE/DATA: XMBF, DES-CBC wie das Spiel, Schlüssel aus dem Profilnamen),
    /// abgelegt unter Dokumente/KuxiiSoft/Test Drive Unlimited 2/Saves/&lt;Profil&gt;/PLAYERSAVE/DATA.
    ///  - Online: Quelle ist der Server, bei jedem Speichern (TDU2Session.SaveAsync) wird die Kopie auch lokal geschrieben,
    ///    beim Anmelden wird eine fehlende Datei aus dem Server-Spielstand neu gebaut (GAME_MODE_LOGIN).
    ///  - Offline: die DATA ist der Spielstand (Lesen beim Anmelden, Schreiben beim Autosave).
    /// </summary>
    public static class TDU2SaveGame
    {
        public const string FileName = "DATA";
        const string Container = "PLAYERSAVE";
        const ulong NoSpot = 0xFFFFFFFFFFFFFFFFUL;
        const int OutfitSlots = 11;
        /// <summary>Reihenfolge der 11 Kleidungsplätze in AvatarSave.Config.ClotheIDArray (zweimal: normal, fahren) – Unity TDU2SaveGame.</summary>
        static readonly ClothePart[] SlotParts =
        {
            ClothePart.Hair, ClothePart.Top, ClothePart.Bottom, ClothePart.Gloves, ClothePart.Shoes, ClothePart.Glasses,
            ClothePart.Helmet, ClothePart.Dress, ClothePart.Beard, ClothePart.Accessories, ClothePart.Belt,
        };
        /// <summary>Umgesetzte Story-Schritte (Unity TDU2StoryProgress.Implemented).</summary>
        public const int StoryImplemented = 11;
        static readonly object _lock = new object();
        static bool _warnedNoDb;

        /// <summary>Fahrzeugdatenbank (TDU2Login setzt sie beim Anmelden).</summary>
        public static CarDatabase Database { get; set; }

        /// <summary>Einmal beim Start (Unity: RuntimeInitializeOnLoadMethod): jede Speicherung auch als DATA schreiben.</summary>
        public static void Hook()
        {
            TDU2Session.SaveCopied -= OnSaveCopied;
            TDU2Session.SaveCopied += OnSaveCopied;
        }

        public static string Folder => UserPaths.Saves;

        public static string PathFor(string nick)
        {
            string n = string.IsNullOrEmpty(nick) ? "_offline" : nick;
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return Path.Combine(Folder, n, Container, FileName);
        }

        public static bool Exists(string nick)
        {
            try { return File.Exists(PathFor(nick)); } catch (Exception) { return false; }
        }

        static CarDatabase Db()
        {
            return Database;
        }

        /// <summary>Aus TDU2Session.SaveAsync (Hauptthread, Kopie des Spielstands).</summary>
        static void OnSaveCopied(string nick, SaveData copy)
        {
            var db = Db();
            if (db == null)
            {
                if (!_warnedNoDb) Log.Warn("Vehicle database not yet loaded – DATA not written");
                _warnedNoDb = true;
                return;
            }
            Task.Run(() => Write(nick, copy, db, false));
        }

        /// <summary>Lokale DATA aus einem Spielstand schreiben (Kopie wird im Hauptthread gezogen, Schreiben im Hintergrund).</summary>
        public static Task WriteAsync(string nick, SaveData save, CarDatabase db, string reason)
        {
            if (save == null || string.IsNullOrEmpty(nick)) return Task.CompletedTask;
            db = db ?? Db();
            if (db == null) { Log.Warn("Vehiclebank missing – DATA not Written"); return Task.CompletedTask; }
            var copy = NetJson.Copy(save);
            if (!string.IsNullOrEmpty(reason)) Log.Info("Savegame: " + reason + " -> " + PathFor(nick));
            return Task.Run(() => Write(nick, copy, db, true));
        }

        // ------------------------------------------------------------------ Schreiben

        static bool Write(string nick, SaveData s, CarDatabase db, bool verbose)
        {
            try
            {
                byte[] xmbf = Build(nick, s, db, out string info);
                byte[] file = SaveCrypto.Encrypt(xmbf, nick, FileName, null, Container);
                string path = PathFor(nick);
                lock (_lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, file);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                if (verbose) Log.Info($"Savegame geschrieben: {path} ({file.Length} Bytes) – {info}");
                return true;
            }
            catch (Exception e)
            {
                Log.Error("Failed to write DATA: " + e.Message);
                return false;
            }
        }

        static byte[] Template()
        {
            byte[] gz = Convert.FromBase64String(TDU2SaveTemplate.GzipBase64);
            using (var src = new MemoryStream(gz))
            using (var z = new GZipStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream(TDU2SaveTemplate.Length))
            {
                z.CopyTo(dst);
                return dst.ToArray();
            }
        }

        /// <summary>Spielstand -> XMBF (unverschlüsselt) auf Basis der Vorlage.</summary>
        public static byte[] Build(string nick, SaveData s, CarDatabase db, out string info)
        {
            var doc = Xmbf.Decode(Template());
            var root = doc.Root;
            var ic = CultureInfo.InvariantCulture;
            string island = IslandKey(s.island);

            // ---- Häuser: die besessenen (Original: nur deren Plätze stehen in Garage[]; Kauf/Verkauf GM_Estates 0xAED200/0xAED080)
            var houses = TDU2Garage.Houses(db, s);

            string[] ip = (s.interior ?? "").Split('|');
            string inHouse = ip.Length > 2 && IsHouse(db, ip[2]) ? ip[2] : (ip[0].Length > 0 ? UniqueHouseOfCode(db, ip[0]) : null);
            if (inHouse != null && !TDU2Garage.Owns(s, inHouse)) inHouse = null;   // fremdes Haus (Besichtigung) ist kein Wohnsitz
            string home = inHouse ?? DefaultHouse(houses, island);
            // Garage des aktuellen Hauses zuerst, dann die übrigen wie in DBHouse
            houses.Sort((a, b) => (Same(a.SpotName, home) ? 0 : 1).CompareTo(Same(b.SpotName, home) ? 0 : 1));

            // ---- Garage
            var gArr = (XArray)root["Garage"];
            var emptySlot = gArr.Items.Count > 0 ? (XObject)gArr.Items[0] : null;
            if (emptySlot == null) throw new InvalidDataException("Vorlage ohne Garage");
            var cars = new List<CarPhysics>();
            var seenCars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in s.ownedCars ?? new List<string>())
            {
                if (string.IsNullOrEmpty(f) || !seenCars.Add(f)) continue;
                var c = FindCar(db, f);
                if (c != null) cars.Add(c);
                else Log.Warn("Savegame: Auto " + f + " nicht in DBCarPhysicsData – nicht in der Garage");
            }
            var garage = new XArray { ElementType = XType.Object };
            int ci = 0, current = -1, idx = 0, dropped = 0;
            // Stellplätze aus SaveData.garage (Garage: Platz wechseln); fehlende Autos verteilt Normalize wie bisher (Haus zuerst)
            TDU2Garage.Normalize(s, db, home);
            foreach (var h in houses)
            {
                ulong hc = HouseHash(h.SpotName);
                for (int k = 0; k < h.CarSlots; k++, idx++)
                {
                    var e = (XObject)Clone(emptySlot);
                    Set(e, "IndexInGarage", idx);
                    Set(e, "House", hc);
                    Set(e, "XTradeID", 0);
                    string file = TDU2Garage.CarAt(s, h.SpotName, k);
                    var car = file != null ? cars.Find(c => string.Equals(c.FileName, file, StringComparison.OrdinalIgnoreCase)) : null;
                    if (car != null)
                    {
                        FillCar((XObject)e["Car"], car, ci + 1, s, db);
                        if (current < 0 && string.Equals(car.FileName, s.currentCar, StringComparison.OrdinalIgnoreCase)) current = idx;
                        ci++;
                    }
                    garage.Items.Add(e);
                }
            }
            dropped = cars.Count - ci;
            root["Garage"] = garage;

            // ---- Fahrer
            var drv = (XObject)root["Driver"];
            Set(drv, "Name", nick ?? "");
            Set(drv, "Money", System.Math.Max(0L, s.money));
            double km = 0;
            foreach (var o in s.odometers ?? new List<OdoEntry>()) km += System.Math.Max(0, o.meters) / 1000.0;
            Set(drv, "OdometerKms", km);
            Set(drv, "CurrentCar", current >= 0 ? current : 0);
            string residence = !string.IsNullOrEmpty(s.homeHouse) && TDU2Garage.Owns(s, s.homeHouse) ? s.homeHouse : home;
            Set(drv, "CurrentHouse", residence != null ? HouseHash(residence) : 0UL);
            Set(drv, "LastSpotWorld", inHouse != null ? HouseHash(inHouse) : NoSpot);
            int step = System.Math.Clamp(s.story, 0, StoryImplemented);
            Set(drv, "BeginGameState", step);
            var pos = (XArray)drv["LastPosWorld"];
            if (pos != null && pos.Items.Count == 16)
            {
                float yaw = s.hasPosition ? s.heading * (MathF.PI / 180f) : 0f;
                float cs = MathF.Cos(yaw), sn = MathF.Sin(yaw);
                // Zeilenweise 4x4, Drehung um Y (Vorwärts = Spalte 3 = (sin, 0, cos)), Verschiebung = Spielkoordinaten
                float[] m = s.hasPosition
                    ? new[] { cs, 0f, sn, s.px, 0f, 1f, 0f, s.py, -sn, 0f, cs, s.pz, 0f, 0f, 0f, 1f }
                    : new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f };
                for (int i = 0; i < 16; i++) ((XPrim)pos.Items[i]).Value = (double)m[i];
            }

            // ---- Häuser (My House)
            var my = (XObject)root["MyHouse"];
            var hArr = new XArray { ElementType = XType.Object };
            foreach (var h in houses)
            {
                var e = new XObject();
                e["HouseHC"] = new XPrim(XType.UInt64, HouseHash(h.SpotName));
                e["LivingMaterials"] = PrimArray(XType.UInt32, 0UL, 0UL);
                e["GarageMaterials"] = PrimArray(XType.UInt32, 0UL, 0UL);
                var frames = new XArray { ElementType = XType.Object };
                // Bilderrahmen (exe: Material MAT_FRAME_%02d, 0xd394e8): Dateiname des Fotos aus dem Album, "" = leer
                HouseLayout lay = null;
                if (s.houses != null) foreach (var l in s.houses) if (l != null && Same(l.spot, h.SpotName)) { lay = l; break; }
                for (int i = 0; i < 5; i++)
                {
                    string fn = "";
                    if (lay?.frames != null) foreach (var f in lay.frames) if (f != null && f.k == (i + 1).ToString(CultureInfo.InvariantCulture)) fn = f.v ?? "";
                    var fr = new XObject(); fr["FrameFilename"] = new XPrim(XType.String, fn); frames.Items.Add(fr);
                }
                e["HouseFrames"] = frames;
                hArr.Items.Add(e);
            }
            my["House"] = hArr;
            var stats = root["StatisticsData"] as XObject;
            if (stats != null) Set(stats, "NbHouseBought", houses.Count);
            if (stats != null && s.housesSold > 0) Set(stats, "NbHouseSold", s.housesSold);

            // ---- Lesezeichen je Insel
            var bm = (XObject)root["Bookmarks"];
            var bI = new XArray { ElementType = XType.Object }; var bH = new XArray { ElementType = XType.Object }; var bC = new XArray { ElementType = XType.Object };
            foreach (var b in s.bookmarks ?? new List<BookmarkEntry>())
            {
                var e = new XObject();
                e["x"] = new XPrim(XType.Float, (double)b.x); e["y"] = new XPrim(XType.Float, (double)b.y);
                e["z"] = new XPrim(XType.Float, (double)b.z); e["w"] = new XPrim(XType.Float, 1.0);
                string k = IslandKey(b.island);
                (k == "hawai" ? bH : k == "casino" ? bC : bI).Items.Add(e);
            }
            bm["Bookmarks_IBIZA"] = bI; bm["Bookmarks_HAWAI"] = bH; bm["Bookmarks_CASINO"] = bC;

            // ---- eigene Farben (Sticker-Shop)
            var fav = (root["Stickers"] as XObject)?["FavoriteColors"] as XArray;
            if (fav != null)
                for (int i = 0; i < fav.Items.Count; i++)
                {
                    var e = (XObject)fav.Items[i];
                    int col = s.favoriteColors != null && i < s.favoriteColors.Count ? s.favoriteColors[i] : -1;
                    Set(e, "Color", col < 0 ? 0xFFFFFFFFUL : 0xFF000000UL | (ulong)(uint)(col & 0xFFFFFF));
                    Set(e, "IsUsed", col >= 0);
                }

            // ---- Avatar
            string av = WriteAvatar(root, s.avatar);
            WriteSurgery(root, s.surgery);
            WriteClothesSold(root, s);

            // ---- Profil
            var lv = root["PlayerLevels"] as XObject;
            if (lv != null) { Set(lv, "PlayedTheGame", true); Set(lv, "TutoSeen", step >= StoryImplemented); }
            var life = root["MyLife"] as XObject;
            if (life != null && s.myLife != null && !string.IsNullOrEmpty(s.myLife.moodText)) Set(life, "PersonalQuote", s.myLife.moodText);
            if (life != null && s.myLife?.fields != null)
                foreach (var (id, key) in LifeKeys)
                    foreach (var f in s.myLife.fields)
                        if (f != null && f.k == id && int.TryParse(f.v, out int v) && v >= 0 && life[key] is XPrim) Set(life, key, v);

            // ---- Autoradio (Original: RadioOptions mit StationHashCode/Volume)
            if (s.radioVolume >= 0)
            {
                var radio = FindObject(root, o => o["StationHashCode"] is XPrim && o["Volume"] is XPrim);
                if (radio != null)
                {
                    // Hashcode als 32-Bit-Muster: vorzeichenlose Felder bekommen den uint-Wert
                    var hp = (XPrim)radio["StationHashCode"];
                    bool unsigned = hp.Type == XType.UInt8 || hp.Type == XType.UInt16 || hp.Type == XType.UInt32 || hp.Type == XType.UInt64;
                    Set(radio, "StationHashCode", unsigned ? (object)(uint)s.radioStation : s.radioStation);
                    Set(radio, "Volume", s.radioVolume / 10f);   // Original: Gleitkomma 0..1 (Radio 0..10 -> /10)
                }
            }

            // ---- Zusatz für den Unity-Port (Original kennt keine Insel/Interieur im DATA-Fahrerteil)
            var env = (root["CEnvironment"] as XObject)?["Variables"] as XArray;
            if (env != null)
            {
                SetVar(env, "U3D_Island", island);
                SetVar(env, "U3D_Story", s.story.ToString(ic));
                SetVar(env, "U3D_Pos", s.hasPosition ? "1" : "0");
                SetVar(env, "U3D_Heading", s.heading.ToString("R", ic));
                SetVar(env, "U3D_Interior", s.interior ?? "");
                SetVar(env, "U3D_Car", s.currentCar ?? "");
                SetVar(env, "U3D_Avatar", s.avatar ?? "");   // vollständiger Code (AvatarSave.Config kennt nur DBClothe-Ids, keine Hashes)
                if (s.radioVolume >= 0) SetVar(env, "U3D_Radio", s.radioStationName ?? "");   // Sendername (auch "aus" = Hashcode 0)
                SetVar(env, "U3D_Options", s.options ?? "");
                SetVar(env, "U3D_Keys", s.keys ?? "");
                // Geschichte (Schritt selbst: Driver.BeginGameState + U3D_Story), TDU Media, Rennen, Spielerlisten
                SetVar(env, "U3D_StoryAvatar", s.storyAvatar ?? "");
                SetVar(env, "U3D_StoryGave", s.storyGaveCar ? "1" : "0");
                SetVar(env, "U3D_NewsSeen", s.newsSeen.ToString(ic) + "," + s.updatesSeen.ToString(ic));
                SetVar(env, "U3D_RaceCin", Join(s.raceCinSeen, x => new[] { x }));
                SetVar(env, "U3D_Racing", Join(s.racing, e => new[] { e.k, e.v }));
                SetVar(env, "U3D_Wrecks", Join(s.foundWrecks, e => new[] { e.island, string.Join(",", e.indices) }));
                SetVar(env, "U3D_WreckEvents", Join(s.wreckEvents, e => new[] { e.k, e.v }));
                // Cruising/Road-Discovery: eigene U3D_Variablen, damit alte DATA-Dateien leer migrieren
                SetVar(env, "U3D_RoadDiscoveryVersion", s.roadDiscoveryVersion.ToString(ic));
                SetVar(env, "U3D_ExploredRoads", Join(s.exploredRoads, e => new[] { e.island, string.Join(",", e.ids), Join(e.areas, a => new[] { a.k, a.v }) }));
                SetVar(env, "U3D_UnlockedDealers", string.Join(",", s.unlockedDealers ?? new List<string>()));
                SetVar(env, "U3D_CruisingRoadPoints", s.cruisingRoadPoints.ToString(ic));
                SetVar(env, "U3D_CruisingRoadEvents", Join(s.cruisingRoadEvents, e => new[] { e.k, e.v }));
                SetVar(env, "U3D_Recent", Join(s.recentPlayers, e => new[] { e.nick, e.lastSeen, e.carId.ToString(ic), e.color.ToString(ic), e.nbCars.ToString(ic), e.money.ToString(ic), e.nationality.ToString(ic) }));
                SetVar(env, "U3D_FriendNews", Join(s.friendNews, e => new[] { e.kind.ToString(ic), e.nick, e.date, e.read ? "1" : "0" }));
                // Community Racing Center: vom Editor hochgeladene Challenge-Ids ("Meine Challenges")
                SetVar(env, "U3D_UploadedChallenges", string.Join(",", s.uploadedChallenges ?? new List<int>()));
                SetVar(env, "U3D_HousesVersion", "1");   // ab hier: Garage[] = nur besessene Häuser (ältere Dateien: alle)
                SetVar(env, "U3D_Saved", DateTime.UtcNow.ToString("o", ic));
            }

            info = $"Insel {island}, {ci} Autos in {houses.Count} Häusern ({idx} Stellplätze{(dropped > 0 ? ", " + dropped + " ohne Platz" : "")}), "
                 + $"aktuelles Auto {(current >= 0 ? "#" + current : "-")}, Haus {home ?? "-"}{(inHouse != null ? " (drin)" : "")}, Geld {s.money}, "
                 + $"Story {s.story}, Avatar {av}";
            return Xmbf.Encode(doc);
        }

        static void FillCar(XObject c, CarPhysics car, int id, SaveData s, CarDatabase db)
        {
            CarConfigEntry cfg = null;
            if (s.carConfigs != null)
                foreach (var e in s.carConfigs) if (e != null && string.Equals(e.car, car.FileName, StringComparison.OrdinalIgnoreCase)) cfg = e;
            int paint = cfg != null ? System.Math.Max(0, cfg.color) : 0;
            int rim = 0, interior = 0;
            if (cfg != null && cfg.rim != 0 && db.CarRims.TryGetValue(car.Id, out var rims)) rim = System.Math.Max(0, rims.IndexOf(cfg.rim));
            if (cfg != null && cfg.interior != 0 && db.Colors.TryGetValue(car.Id, out var cols) && paint < cols.Count)
                interior = System.Math.Max(0, Array.IndexOf(cols[paint].Interiors, cfg.interior));
            double km = 0;
            if (s.odometers != null) foreach (var o in s.odometers) if (o != null && o.carId == car.Id) km = o.meters / 1000.0;
            bool sticker = false;
            if (s.liveries != null) foreach (var l in s.liveries) if (l != null && string.Equals(l.car, car.FileName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(l.data)) sticker = true;

            Set(c, "Status", 0);
            Set(c, "ID", (ulong)id);
            Set(c, "Archetype", car.Id);
            Set(c, "VIN", 0x6700000000000000UL | ((ulong)(uint)car.Id << 16) | (ulong)(uint)(id & 0xFFFF));
            Set(c, "PaintIndice", System.Math.Min(paint, 254));
            Set(c, "RimIndice", System.Math.Min(rim, 254));
            Set(c, "InteriorIndice", System.Math.Min(interior, 254));
            Set(c, "OdometerKms", km);
            Set(c, "HasSticker", sticker);

            // Tuning wie im Spiel: Car/Car Upgrades { Acceleration, Top Speed, Braking } (je 0..4; CarUpgrades)
            if (c["Car Upgrades"] is XObject cu)
            {
                var lv = default(UpgradeLevels);
                if (s.carUpgrades != null)
                    foreach (var u in s.carUpgrades)
                        if (u != null && string.Equals(u.car, car.FileName, StringComparison.OrdinalIgnoreCase)) lv = UpgradeLevels.Unpack(u.upgrades);
                Set(cu, "Acceleration", (int)lv.Acceleration);
                Set(cu, "Top Speed", (int)lv.TopSpeed);
                Set(cu, "Braking", (int)lv.Braking);
            }
            // Schmutz wie im Spiel: Car/Car Dirt/Dirt Data (48 Werte 0..255; CarDirt-Stand gleichmäßig, Kratzer haben dort kein Feld)
            if (c["Car Dirt"] is XObject cd && cd["Dirt Data"] is XArray dd)
            {
                float level = 0f;
                if (s.carDirt != null)
                    foreach (var e in s.carDirt)
                        if (e != null && string.Equals(e.car, car.FileName, StringComparison.OrdinalIgnoreCase)) level = e.level;
                ulong b = (ulong)System.Math.Clamp((int)MathF.Round(level * 255f), 0, 255);
                foreach (var it in dd.Items) if (it is XPrim p) p.Value = b;
            }
        }

        /// <summary>
        /// Avatar in AvatarSave.Config wie das Original (Port Unity WriteAvatar): BodyID, Augen/Brauen, 2 x 11 Kleidungsplätze (DBClothe-Ids),
        /// dazu das Gesicht der Schönheitsklinik: FaceParamArray (49 x i8), age, freckle, ScatteringCoeff (Tint, nur wenn gesetzt).
        /// </summary>
        static string WriteAvatar(XObject root, string code)
        {
            var cfg = AvatarConfig.Decode(code);
            var conf = (root["AvatarSave"] as XObject)?["Config"] as XObject;
            if (cfg == null || conf == null) return "Vorlage";
            if (!HasClothes()) return "Vorlage (Kleidung nicht geladen)";
            Set(conf, "BodyID", cfg.Ethnic * 2 + cfg.Sex);
            if (cfg.EyeId >= 0) Set(conf, "EyeID", cfg.EyeId);
            if (cfg.BrowId >= 0) Set(conf, "EyebrowsID", cfg.BrowId);
            if (cfg.HasBrowColor)
            {
                Set(conf, "EyebrowsColor_R", (int)MathF.Round(System.Math.Clamp(cfg.BrowColor.X, 0f, 1f) * 255f));
                Set(conf, "EyebrowsColor_G", (int)MathF.Round(System.Math.Clamp(cfg.BrowColor.Y, 0f, 1f) * 255f));
                Set(conf, "EyebrowsColor_B", (int)MathF.Round(System.Math.Clamp(cfg.BrowColor.Z, 0f, 1f) * 255f));
            }
            // Gesicht (GMSurgery: FaceParamArray +0xA2, age +0xD4, freckle +0xD8, ScatteringCoeff +0x54)
            if (conf["FaceParamArray"] is XArray fp)
                for (int i = 0; i < fp.Items.Count && i < AvatarConfig.FaceParamCount; i++)
                    if (fp.Items[i] is XPrim p) p.Value = (long)cfg.Face[i];
            if (conf["age"] is XPrim) Set(conf, "age", cfg.Age);
            if (conf["freckle"] is XPrim) Set(conf, "freckle", cfg.Freckle);
            if (cfg.Tint >= 0f && conf["ScatteringCoeff"] is XPrim) Set(conf, "ScatteringCoeff", cfg.Tint);
            var ids = conf["ClotheIDArray"] as XArray; var cols = conf["ColorIDArray"] as XArray;
            if (ids == null || cols == null || ids.Items.Count < OutfitSlots * 2 || cols.Items.Count < OutfitSlots * 2) return "Vorlage (Feld fehlt)";
            int dbSex = cfg.Sex == 1 ? 1 : 2;   // DBClothe._sex: 1 Frau, 2 Mann
            int set = 0;
            for (int slot = 0; slot < OutfitSlots; slot++)
            {
                var part = SlotParts[slot];
                ClotheInfo item = null; int color = 0;
                for (int i = 0; i < cfg.Items.Count; i++)
                {
                    var c = AvatarLibrary.Clothe(cfg.Items[i]);
                    if (c != null && c.Part == part) { item = c; color = i < cfg.Colors.Count ? cfg.Colors[i] : 0; }
                }
                if (item == null) item = Nothing(part, dbSex);
                else set++;
                ulong id = item != null ? (ulong)(uint)item.IdNum : 65535UL;
                // erste 11 = normale Kleidung, zweite 11 = beim Fahren (hier gleich)
                ((XPrim)ids.Items[slot]).Value = id; ((XPrim)ids.Items[slot + OutfitSlots]).Value = id;
                ((XPrim)cols.Items[slot]).Value = (long)color; ((XPrim)cols.Items[slot + OutfitSlots]).Value = (long)color;
            }
            return $"{(cfg.Sex == 1 ? "Frau" : "Mann")}, {set} Teile{(cfg.HasFace ? ", Gesicht" : "")}";
        }

        static bool HasClothes() { foreach (var _ in AvatarLibrary.AllClothes) return true; return false; }

        static ClotheInfo Nothing(ClothePart part, int dbSex)
        {
            ClotheInfo any = null;
            foreach (var c in AvatarLibrary.AllClothes)
                if (c.Part == part && c.IsNothing)
                {
                    if (c.Sex == dbSex) return c;
                    if (any == null && (c.Sex == 0 || c.Sex == 3)) any = c;
                }
            return any;
        }

        /// <summary>Feld im Baum suchen (erstes Objekt, das es hat) und setzen; fehlt es in der Vorlage, bleibt es aus.</summary>
        static void SetDeep(XObject root, string key, object value)
        {
            var o = FindObject(root, x => x[key] is XPrim);
            if (o != null) Set(o, key, value);
        }

        /// <summary>
        /// Gekaufte Kleidung wie das Original: AvatarSave.ClothesSold (4096 x s32, Index = DBClothe._id_num, Bit c = Farbe c gekauft;
        /// exe 0x687AC0 setzt 1 &lt;&lt; Farbe, 0x687AF0 alle Farben). Die Vorgabe der Vorlage (Standardteile) bleibt, Käufe werden hineingeodert.
        /// Ohne Farbeintrag (ältere Käufe) zählt die getragene Farbe, sonst Farbe 0.
        /// </summary>
        static void WriteClothesSold(XObject root, SaveData s)
        {
            if (!HasClothes()) return;
            var arr = (root["AvatarSave"] as XObject)?["ClothesSold"] as XArray;
            if (arr == null) return;
            var done = new HashSet<int>();
            void Put(int hash, int mask)
            {
                var c = AvatarLibrary.Clothe(hash);
                if (c == null || mask == 0 || c.IdNum < 0 || c.IdNum >= arr.Items.Count || !(arr.Items[c.IdNum] is XPrim p)) return;
                p.Value = (long)(int)((uint)p.AsLong() | (uint)mask);
            }
            if (s.clothesSold != null)
                foreach (var e in s.clothesSold) if (e != null && e.clothe != 0) { Put(e.clothe, e.colors); done.Add(e.clothe); }
            if (s.ownedClothes != null)
            {
                var cfg = AvatarConfig.Decode(s.avatar ?? "");
                foreach (int h in s.ownedClothes)
                    if (!done.Contains(h)) Put(h, cfg != null && cfg.Items.Contains(h) ? 1 << System.Math.Clamp(cfg.ColorOf(h), 0, 31) : 1);
            }
        }

        /// <summary>Gegenstück zu <see cref="WriteClothesSold"/>: gekaufte Stücke (ohne Standard-/Nichts-Teile) mit ihren Farben.</summary>
        static void ReadClothesSold(XObject root, SaveData s)
        {
            var arr = (root["AvatarSave"] as XObject)?["ClothesSold"] as XArray;
            if (arr == null || !HasClothes()) return;
            var byNum = new Dictionary<int, ClotheInfo>();
            foreach (var c in AvatarLibrary.AllClothes) if (!byNum.ContainsKey(c.IdNum)) byNum[c.IdNum] = c;
            s.clothesSold ??= new List<ClothesSoldEntry>();
            s.ownedClothes ??= new List<int>();
            for (int i = 0; i < arr.Items.Count; i++)
            {
                if (!(arr.Items[i] is XPrim p)) continue;
                int mask = (int)p.AsLong();
                if (mask == 0 || !byNum.TryGetValue(i, out var c)) continue;
                // Standardteile (Vorlage: alle Farben, $999.999) und "Nichts"-Teile sind keine Käufe
                if (c.IsNothing || c.Price >= 999999 || mask == -1) continue;
                bool found = false;
                foreach (var e in s.clothesSold) if (e.clothe == c.Hash) { e.colors |= mask; found = true; break; }
                if (!found) s.clothesSold.Add(new ClothesSoldEntry { clothe = c.Hash, colors = mask });
                if (!s.ownedClothes.Contains(c.Hash)) s.ownedClothes.Add(c.Hash);
            }
        }

        /// <summary>DBClothe-Id (u16) eines Teils, ohne Teil das "Nichts"-Teil des Platzes (das Original sichert immer ein getragenes Teil).</summary>
        static int ClotheId(int hash, ClothePart part)
        {
            var c = hash != 0 ? AvatarLibrary.Clothe(hash) : null;
            if (c == null) c = Nothing(part, 0);
            return c != null ? c.IdNum : 65535;
        }

        /// <summary>
        /// Schönheitsklinik in die Original-Felder (PlayerData/Driver, exe Profil +0xD8 / +0xF8…+0x110, StatisticsData._time_spent_in_surgery).
        /// </summary>
        static void WriteSurgery(XObject root, SurgeryData sg)
        {
            if (sg == null) return;
            SetDeep(root, "NbSurgeries", sg.nbSurgeries);
            SetDeep(root, "BeenInSurgeryShop", sg.beenInSurgeryShop);
            SetDeep(root, "Bandage", sg.bandage);
            SetDeep(root, "BandageTime", (ulong)System.Math.Max(0L, sg.bandageTime));
            SetDeep(root, "NeedBandageNews", sg.needBandageNews);
            SetDeep(root, "MultiChallengesWithBandages", sg.multiChallengesWithBandages);
            SetDeep(root, "_time_spent_in_surgery", sg.timeSpentInSurgery);
            if (!HasClothes()) return;
            SetDeep(root, "HelmetBandage", ClotheId(sg.helmetBandage, ClothePart.Helmet));
            SetDeep(root, "BikeHelmetBandage", ClotheId(sg.bikeHelmetBandage, ClothePart.Helmet));
            SetDeep(root, "GlassesBandage", ClotheId(sg.glassesBandage, ClothePart.Glasses));
            SetDeep(root, "BeardBandage", ClotheId(sg.beardBandage, ClothePart.Beard));
            SetDeep(root, "HairBandage", ClotheId(sg.hairBandage, ClothePart.Hair));
            SetDeep(root, "HelmetColorBandage", sg.helmetColorBandage);
            SetDeep(root, "BikeHelmetColorBandage", sg.bikeHelmetColorBandage);
            SetDeep(root, "GlassesColorBandage", sg.glassesColorBandage);
            SetDeep(root, "BeardColorBandage", sg.beardColorBandage);
            SetDeep(root, "HairColorBandage", sg.hairColorBandage);
        }

        /// <summary>Schönheitsklinik aus der DATA (Gegenstück zu <see cref="WriteSurgery"/>); Teile über ihre DBClothe-Id zurück zum Hash.</summary>
        static SurgeryData ReadSurgery(XObject root)
        {
            var sg = new SurgeryData();
            long L(string k) { var o = FindObject(root, x => x[k] is XPrim); return o != null ? Long(o, k) : 0; }
            double D(string k) { var o = FindObject(root, x => x[k] is XPrim); return o != null ? Double(o, k) : 0; }
            sg.nbSurgeries = (int)L("NbSurgeries");
            sg.beenInSurgeryShop = L("BeenInSurgeryShop") != 0;
            sg.bandage = L("Bandage") != 0;
            sg.bandageTime = L("BandageTime");
            sg.needBandageNews = L("NeedBandageNews") != 0;
            sg.multiChallengesWithBandages = (int)L("MultiChallengesWithBandages");
            double t = D("_time_spent_in_surgery");
            sg.timeSpentInSurgery = double.IsFinite(t) && t > 0 ? (float)t : 0f;
            if (HasClothes())
            {
                var byNum = new Dictionary<int, ClotheInfo>();
                foreach (var c in AvatarLibrary.AllClothes) if (!byNum.ContainsKey(c.IdNum)) byNum[c.IdNum] = c;
                int H(string k) { int n = (int)L(k); return byNum.TryGetValue(n, out var c) && !c.IsNothing ? c.Hash : 0; }
                sg.helmetBandage = H("HelmetBandage"); sg.bikeHelmetBandage = H("BikeHelmetBandage"); sg.glassesBandage = H("GlassesBandage");
                sg.beardBandage = H("BeardBandage"); sg.hairBandage = H("HairBandage");
            }
            sg.helmetColorBandage = (int)L("HelmetColorBandage"); sg.bikeHelmetColorBandage = (int)L("BikeHelmetColorBandage");
            sg.glassesColorBandage = (int)L("GlassesColorBandage"); sg.beardColorBandage = (int)L("BeardColorBandage");
            sg.hairColorBandage = (int)L("HairColorBandage");
            return sg;
        }

        // ------------------------------------------------------------------ Lesen (lokale DATA -> Spielstand)

        /// <summary>Lokale DATA lesen und in einen Spielstand umsetzen. Hauptthread (Avatar-/Fahrzeugdaten), Dauer wenige ms.</summary>
        public static bool TryRead(string nick, CarDatabase db, out SaveData s, out string why)
        {
            s = null; why = "";
            db = db ?? Db();
            string path = PathFor(nick);
            try
            {
                if (!File.Exists(path)) { why = "keine Datei"; return false; }
                if (db == null) { why = "Fahrzeugdatenbank fehlt"; return false; }
                byte[] file;
                lock (_lock) file = File.ReadAllBytes(path);
                var dec = SaveCrypto.Decrypt(file, nick, FileName, Container);
                var doc = Xmbf.Decode(dec.Xmbf);
                s = Map(doc.Root, db, out why);
                return true;
            }
            catch (Exception e)
            {
                why = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        static SaveData Map(XObject root, CarDatabase db, out string info)
        {
            var ic = CultureInfo.InvariantCulture;
            var s = new SaveData();
            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            var env = (root["CEnvironment"] as XObject)?["Variables"] as XArray;
            if (env != null)
                foreach (var v in env.Items)
                    if (v is XObject o) { string n = Str(o, "Name"); if (!string.IsNullOrEmpty(n)) vars[n] = Str(o, "Value") ?? ""; }

            var drv = (XObject)root["Driver"];
            s.money = System.Math.Max(0L, Long(drv, "Money"));
            ulong curHouse = ULong(drv, "CurrentHouse"), lastSpot = ULong(drv, "LastSpotWorld");
            string curHouseName = HouseName(db, curHouse), lastSpotName = HouseName(db, lastSpot);

            // Garage -> eigene Autos, Ausstattung, Kilometer
            var garage = root["Garage"] as XArray;
            int curIdx = (int)Long(drv, "CurrentCar");
            if (garage != null)
                for (int gi = 0; gi < garage.Items.Count; gi++)
                {
                    var c = (garage.Items[gi] as XObject)?["Car"] as XObject;
                    if (c == null || Long(c, "Status") != 0) continue;
                    int arch = (int)Long(c, "Archetype");
                    if (!db.ById.TryGetValue(arch, out var car) || string.IsNullOrEmpty(car.FileName)) continue;
                    if (s.ownedCars.Exists(f => string.Equals(f, car.FileName, StringComparison.OrdinalIgnoreCase))) continue;
                    s.ownedCars.Add(car.FileName);
                    if (gi == curIdx) s.currentCar = car.FileName;
                    int paint = (int)Long(c, "PaintIndice"), rimI = (int)Long(c, "RimIndice"), intI = (int)Long(c, "InteriorIndice");
                    if (paint >= 255) paint = 0;
                    var cfg = new CarConfigEntry { car = car.FileName, color = paint };
                    if (rimI < 255 && db.CarRims.TryGetValue(arch, out var rims) && rimI < rims.Count && rimI > 0) cfg.rim = rims[rimI];
                    if (intI < 255 && intI > 0 && db.Colors.TryGetValue(arch, out var cols) && paint < cols.Count && intI < cols[paint].Interiors.Length)
                        cfg.interior = cols[paint].Interiors[intI];
                    if (cfg.color != 0 || cfg.rim != 0 || cfg.interior != 0) s.carConfigs.Add(cfg);
                    double km = Double(c, "OdometerKms");
                    if (km > 0) s.odometers.Add(new OdoEntry { carId = arch, meters = km * 1000.0 });
                    // Tuning (Car Upgrades) und Schmutz (Car Dirt) wie im Spiel
                    if (c["Car Upgrades"] is XObject cu)
                    {
                        var lv = new UpgradeLevels();
                        lv.Set(CarUpgrades.Acceleration, (int)Long(cu, "Acceleration"));
                        lv.Set(CarUpgrades.TopSpeed, (int)Long(cu, "Top Speed"));
                        lv.Set(CarUpgrades.Braking, (int)Long(cu, "Braking"));
                        if (lv.Any) (s.carUpgrades ??= new List<UpgradeEntry>()).Add(new UpgradeEntry { car = car.FileName, upgrades = lv.Pack() });
                    }
                    if (c["Car Dirt"] is XObject cd && cd["Dirt Data"] is XArray dd && dd.Items.Count > 0)
                    {
                        double sum = 0; foreach (var it in dd.Items) sum += (it as XPrim)?.AsDouble() ?? 0;
                        float level = (float)(sum / dd.Items.Count / 255.0);
                        if (level > 0.001f) (s.carDirt ??= new List<DirtEntry>()).Add(new DirtEntry { car = car.FileName, level = level });
                    }
                }
            if (root["StatisticsData"] is XObject stR) s.housesSold = (int)Long(stR, "NbHouseSold");
            ReadHouses(db, garage, vars, curHouseName, s);
            ReadHouseFrames(db, root, s);
            if (vars.TryGetValue("U3D_Car", out string vc) && s.ownedCars.Exists(f => string.Equals(f, vc, StringComparison.OrdinalIgnoreCase))) s.currentCar = vc;
            if (string.IsNullOrEmpty(s.currentCar) && s.ownedCars.Count > 0) s.currentCar = s.ownedCars[0];
            s.starterChosen = s.ownedCars.Count > 0;

            // Insel, Position, Interieur, Story
            s.island = vars.TryGetValue("U3D_Island", out string vi) && vi.Length > 0 ? vi
                     : (lastSpotName ?? curHouseName ?? "").EndsWith("I", StringComparison.Ordinal) ? "ibiza" : "hawai";
            var pos = drv["LastPosWorld"] as XArray;
            if (pos != null && pos.Items.Count == 16)
            {
                double D(int i) => (pos.Items[i] as XPrim)?.AsDouble() ?? 0;
                float px = (float)D(3), py = (float)D(7), pz = (float)D(11);
                bool has = vars.TryGetValue("U3D_Pos", out string vp) ? vp == "1" : (System.Math.Abs(px) > 1f || System.Math.Abs(pz) > 1f);
                if (has && Finite(px) && Finite(py) && Finite(pz))
                {
                    s.hasPosition = true; s.px = px; s.py = py; s.pz = pz;
                    s.heading = vars.TryGetValue("U3D_Heading", out string vh) && float.TryParse(vh, NumberStyles.Float, ic, out float hd)
                        ? hd : (float)(System.Math.Atan2(D(2), D(10)) * 180.0 / System.Math.PI);
                }
            }
            if (vars.TryGetValue("U3D_Interior", out string vin)) s.interior = vin;
            else if (lastSpotName != null) s.interior = Code(lastSpotName) + "||" + lastSpotName;
            int bgs = (int)Long(drv, "BeginGameState");
            s.story = vars.TryGetValue("U3D_Story", out string vs) && int.TryParse(vs, NumberStyles.Integer, ic, out int st) ? st : bgs;

            // Lesezeichen
            var bm = root["Bookmarks"] as XObject;
            int bid = 1;
            if (bm != null)
                foreach (var (key, isl) in new[] { ("Bookmarks_IBIZA", "ibiza"), ("Bookmarks_HAWAI", "hawai"), ("Bookmarks_CASINO", "casino") })
                    if (bm[key] is XArray a)
                        foreach (var it in a.Items)
                            if (it is XObject b)
                                s.bookmarks.Add(new BookmarkEntry { id = bid++, island = isl, x = (float)Double(b, "x"), y = (float)Double(b, "y"), z = (float)Double(b, "z") });

            // eigene Farben
            if ((root["Stickers"] as XObject)?["FavoriteColors"] is XArray fav)
                foreach (var it in fav.Items)
                {
                    var e = it as XObject;
                    bool used = e != null && Long(e, "IsUsed") != 0;
                    s.favoriteColors.Add(used ? (int)(ULong(e, "Color") & 0xFFFFFF) : -1);
                }

            // Autoradio
            var radioObj = FindObject(root, o => o["StationHashCode"] is XPrim && o["Volume"] is XPrim);
            if (radioObj != null)
            {
                // DBRadioStation._hashcode: 1 Kalua, 2 Hariba, 3 RoadRock (0 = aus). Lautstärke Gleitkomma 0..1 – eine frische Vorlage hat ~0
                // (nie eingestellt): dann Grundwert aus AudioConfig statt stumm.
                s.radioStation = unchecked((int)(uint)ULong(radioObj, "StationHashCode"));
                double vol = Double(radioObj, "Volume");
                bool ours = vars.ContainsKey("U3D_Radio");
                if (ours || vol > 0.01) s.radioVolume = (int)System.Math.Round(System.Math.Clamp(vol, 0.0, 1.1) * 10.0);
            }
            if (vars.TryGetValue("U3D_Radio", out string vr)) s.radioStationName = vr;
            if (vars.TryGetValue("U3D_Options", out string vo)) s.options = vo;
            if (vars.TryGetValue("U3D_Keys", out string vk)) s.keys = vk;
            if (vars.TryGetValue("U3D_StoryAvatar", out string vsa)) s.storyAvatar = vsa;
            if (vars.TryGetValue("U3D_StoryGave", out string vsg)) s.storyGaveCar = vsg == "1";
            if (vars.TryGetValue("U3D_NewsSeen", out string vns))
            {
                var p = vns.Split(',');
                if (p.Length > 0 && int.TryParse(p[0], NumberStyles.Integer, ic, out int ns)) s.newsSeen = ns;
                if (p.Length > 1 && int.TryParse(p[1], NumberStyles.Integer, ic, out int us)) s.updatesSeen = us;
            }
            if (vars.TryGetValue("U3D_RaceCin", out string vrc)) foreach (var f in Split(vrc)) if (f.Length > 0 && f[0].Length > 0) s.raceCinSeen.Add(f[0]);
            if (vars.TryGetValue("U3D_Racing", out string vrr)) foreach (var f in Split(vrr)) if (f.Length > 1) s.racing.Add(new HouseEntry { k = f[0], v = f[1] });
            // Wracks: 0-basierte XMB-Indizes je Insel (das Original speichert FoundWrecks[island] als uint-Vektor)
            if (vars.TryGetValue("U3D_Wrecks", out string vwr))
                foreach (var f in Split(vwr))
                    if (f.Length > 1)
                    {
                        var we = new WreckFoundEntry { island = f[0] };
                        foreach (var t in f[1].Split(','))
                            if (int.TryParse(t, NumberStyles.Integer, ic, out int ix)) we.indices.Add(ix);
                        s.foundWrecks.Add(we);
                    }
            if (vars.TryGetValue("U3D_WreckEvents", out string vwe))
                foreach (var f in Split(vwe)) if (f.Length > 1) s.wreckEvents.Add(new HouseEntry { k = f[0], v = f[1] });
            // Cruising/Road-Discovery: alte Spielstände ohne diese Variablen bleiben leer (Version 0)
            if (vars.TryGetValue("U3D_RoadDiscoveryVersion", out string vrd))
                s.roadDiscoveryVersion = Int(vrd);
            if (vars.TryGetValue("U3D_ExploredRoads", out string ver))
                foreach (var f in Split(ver))
                {
                    if (f.Length < 2) continue;
                    var re = new RoadDiscoveryEntry { island = f[0] };
                    foreach (var id in f[1].Split(',')) if (id.Length > 0) re.ids.Add(id);
                    if (f.Length > 2)
                        foreach (var a in Split(f[2])) if (a.Length > 1) re.areas.Add(new HouseEntry { k = a[0], v = a[1] });
                    s.exploredRoads.Add(re);
                }
            if (vars.TryGetValue("U3D_UnlockedDealers", out string vud))
                foreach (var c in vud.Split(',')) if (c.Length > 0) s.unlockedDealers.Add(c);
            if (vars.TryGetValue("U3D_CruisingRoadPoints", out string vroadpts)) s.cruisingRoadPoints = Int(vroadpts);
            if (vars.TryGetValue("U3D_CruisingRoadEvents", out string vce))
                foreach (var f in Split(vce)) if (f.Length > 1) s.cruisingRoadEvents.Add(new HouseEntry { k = f[0], v = f[1] });
            if (vars.TryGetValue("U3D_Recent", out string vre))
                foreach (var f in Split(vre))
                    if (f.Length > 6)
                        s.recentPlayers.Add(new RecentEntry
                        {
                            nick = f[0],
                            lastSeen = f[1],
                            carId = Int(f[2]),
                            color = Int(f[3]),
                            nbCars = Int(f[4]),
                            money = long.TryParse(f[5], NumberStyles.Integer, ic, out long mo) ? mo : 0,
                            nationality = Int(f[6])
                        });
            if (vars.TryGetValue("U3D_FriendNews", out string vfn))
                foreach (var f in Split(vfn))
                    if (f.Length > 3) s.friendNews.Add(new FriendNewsEntry { kind = Int(f[0]), nick = f[1], date = f[2], read = f[3] == "1" });
            if (vars.TryGetValue("U3D_UploadedChallenges", out string vuc))
                foreach (var f in vuc.Split(',')) if (int.TryParse(f, NumberStyles.Integer, ic, out int cid) && cid > 0) s.uploadedChallenges.Add(cid);

            // Avatar: der eigene vollständige Code (U3D_Avatar, mit Hashes und Gesicht) zuerst; Original-Spielstände über AvatarSave.Config
            string avInfo;
            if (vars.TryGetValue("U3D_Avatar", out string va) && !string.IsNullOrEmpty(va)) { s.avatar = va; avInfo = "U3D_Avatar"; }
            else { string av = ReadAvatar(root, out avInfo); if (av != null) s.avatar = av; }
            s.surgery = ReadSurgery(root);
            ReadClothesSold(root, s);

            // Profil
            var life = root["MyLife"] as XObject;
            string quote = life != null ? Str(life, "PersonalQuote") : null;
            if (!string.IsNullOrEmpty(quote) && quote != "Test Drive Unlimited 2") s.myLife.moodText = quote.Length > 60 ? quote.Substring(0, 60) : quote;
            if (life != null)
                foreach (var (id, key) in LifeKeys)
                {
                    if (!(life[key] is XPrim lp)) continue;
                    long v = lp.AsLong();
                    if (v < 0 || v > int.MaxValue) continue;   // ungesetzt (z. B. 0xFFFFFFFF)
                    s.myLife.fields ??= new List<HouseEntry>();
                    s.myLife.fields.RemoveAll(e => e == null || e.k == id);
                    s.myLife.fields.Add(new HouseEntry { k = id, v = v.ToString(CultureInfo.InvariantCulture) });
                }

            info = $"{s.ownedCars.Count} Autos, aktuell {s.currentCar}, Insel {s.island}, Position={s.hasPosition}, Interieur '{s.interior}', Geld {s.money}, Story {s.story}, Avatar {avInfo}";
            return s;
        }

        /// <summary>Avatar aus AvatarSave.Config (Port Unity ReadAvatar) samt Gesicht (FaceParamArray, age, freckle, ScatteringCoeff).</summary>
        static string ReadAvatar(XObject root, out string info)
        {
            info = "-";
            var conf = (root["AvatarSave"] as XObject)?["Config"] as XObject;
            if (conf == null || !HasClothes()) { info = "nicht gelesen"; return null; }
            var byNum = new Dictionary<int, ClotheInfo>();
            foreach (var c in AvatarLibrary.AllClothes) if (!byNum.ContainsKey(c.IdNum)) byNum[c.IdNum] = c;
            int body = (int)Long(conf, "BodyID");
            var cfg = new AvatarConfig { Sex = body & 1, Ethnic = System.Math.Clamp(body >> 1, 0, 2) };
            var ids = conf["ClotheIDArray"] as XArray; var cols = conf["ColorIDArray"] as XArray;
            if (ids != null)
                for (int i = 0; i < System.Math.Min(OutfitSlots, ids.Items.Count); i++)
                {
                    int num = (int)((ids.Items[i] as XPrim)?.AsLong() ?? 65535);
                    if (num == 65535 || !byNum.TryGetValue(num, out var c) || c.IsNothing) continue;
                    int col = cols != null && i < cols.Items.Count ? (int)((cols.Items[i] as XPrim)?.AsLong() ?? 0) : 0;
                    cfg.Items.Add(c.Hash); cfg.Colors.Add(System.Math.Max(0, col));
                }
            cfg.EyeId = (int)Long(conf, "EyeID");
            cfg.BrowId = (int)Long(conf, "EyebrowsID");
            cfg.BrowColor = new System.Numerics.Vector4(Long(conf, "EyebrowsColor_R") / 255f, Long(conf, "EyebrowsColor_G") / 255f, Long(conf, "EyebrowsColor_B") / 255f, 1f);
            if (conf["FaceParamArray"] is XArray fp)
                for (int i = 0; i < fp.Items.Count && i < AvatarConfig.FaceParamCount; i++)
                    cfg.Face[i] = unchecked((sbyte)((fp.Items[i] as XPrim)?.AsLong() ?? 0));
            cfg.Age = (float)Double(conf, "age");
            cfg.Freckle = (float)Double(conf, "freckle");
            if (conf["ScatteringCoeff"] is XPrim) cfg.Tint = (float)Double(conf, "ScatteringCoeff");
            info = $"{(cfg.Sex == 1 ? "Frau" : "Mann")}, {cfg.Items.Count} Teile";
            return cfg.Encode();
        }

        /// <summary>Übernimmt aus dem (leeren) Server-Spielstand, was die DATA nicht enthält (MyTDULife, Lieblings-Spots, Sticker, Kleidung …).</summary>
        public static void KeepServerOnly(SaveData local, SaveData server)
        {
            if (local == null || server == null) return;
            if (server.myLife != null)
            {
                string quote = local.myLife?.moodText;
                local.myLife = server.myLife;
                if (string.IsNullOrEmpty(local.myLife.moodText) && !string.IsNullOrEmpty(quote)) local.myLife.moodText = quote;
            }
            if (server.favoriteSpots != null && server.favoriteSpots.Count > 0) local.favoriteSpots = server.favoriteSpots;
            if (server.liveries != null && server.liveries.Count > 0) local.liveries = server.liveries;
            if (server.stickerBoards != null && server.stickerBoards.Count > 0) local.stickerBoards = server.stickerBoards;
            if (server.ownedClothes != null && server.ownedClothes.Count > 0 && (local.ownedClothes == null || local.ownedClothes.Count == 0)) local.ownedClothes = server.ownedClothes;
            if (server.clothesSold != null && server.clothesSold.Count > 0 && (local.clothesSold == null || local.clothesSold.Count == 0)) local.clothesSold = server.clothesSold;
            if (server.houses != null && server.houses.Count > 0) local.houses = server.houses;
            if (server.ownedFurniture != null && server.ownedFurniture.Count > 0) local.ownedFurniture = server.ownedFurniture;
            if (server.ownedMaterials != null && server.ownedMaterials.Count > 0) local.ownedMaterials = server.ownedMaterials;
            if (string.IsNullOrEmpty(local.avatar)) local.avatar = server.avatar ?? "";
            if (server.surgery != null && (local.surgery == null || (local.surgery.nbSurgeries == 0 && !local.surgery.bandage))) local.surgery = server.surgery;
            if (local.bookmarks.Count == 0 && server.bookmarks != null) local.bookmarks = server.bookmarks;
            if (local.foundWrecks.Count == 0 && server.foundWrecks != null) local.foundWrecks = server.foundWrecks;
            if (local.wreckEvents.Count == 0 && server.wreckEvents != null) local.wreckEvents = server.wreckEvents;
            // Road-Discovery liegt nur in der lokalen DATA, wenn der Server sie nicht (mehr) speichert
            if (local.exploredRoads.Count == 0 && server.exploredRoads != null) local.exploredRoads = server.exploredRoads;
            if (local.unlockedDealers.Count == 0 && server.unlockedDealers != null) local.unlockedDealers = server.unlockedDealers;
            if (local.cruisingRoadPoints == 0 && server.cruisingRoadPoints != 0) local.cruisingRoadPoints = server.cruisingRoadPoints;
            if (local.cruisingRoadEvents.Count == 0 && server.cruisingRoadEvents != null) local.cruisingRoadEvents = server.cruisingRoadEvents;
            if (local.roadDiscoveryVersion <= 0) local.roadDiscoveryVersion = server.roadDiscoveryVersion > 0 ? server.roadDiscoveryVersion : 1;
            // CRC: hochgeladene Challenge-Ids vom Server uebernehmen, wenn die lokale DATA keine hat
            if ((local.uploadedChallenges == null || local.uploadedChallenges.Count == 0) && server.uploadedChallenges != null && server.uploadedChallenges.Count > 0)
                local.uploadedChallenges = server.uploadedChallenges;
        }

        // ------------------------------------------------------------------ Häuser / Spots

        /// <summary>Hashcode eines Spots wie im Spielstand (Garage.House, Driver.CurrentHouse/LastSpotWorld): Zeichen des vollen
        /// Namens auf 8 Bytes aufsummiert (Byte k = Summe der Zeichen k, k+8, …).</summary>
        public static ulong HouseHash(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            var sum = new int[8];
            string up = name.ToUpperInvariant();
            for (int i = 0; i < up.Length; i++) sum[i & 7] += up[i];
            ulong k = 0;
            for (int b = 0; b < 8; b++) k |= (ulong)(byte)sum[b] << (8 * b);
            return k;
        }

        /// <summary>
        /// Besessene Häuser und Stellplätze aus Garage[] (Original: die Häuser, deren Plätze dort stehen; Platz k = k-ter Eintrag des
        /// Hauses). Dateien des Ports (U3D_Saved) vor U3D_HousesVersion schrieben alle Häuser: dort gelten nur Häuser mit Autos, der Wohnsitz
        /// und das Starthaus als besessen.
        /// </summary>
        /// <summary>Bilderrahmen der Häuser (MyHouse.House[].HouseHC / HouseFrames[0..4].FrameFilename) in s.houses[].frames.</summary>
        static void ReadHouseFrames(CarDatabase db, XObject root, SaveData s)
        {
            if (root["MyHouse"] is not XObject my || my["House"] is not XArray arr) return;
            foreach (var it in arr.Items)
            {
                if (it is not XObject e || e["HouseFrames"] is not XArray fa) continue;
                string hn = HouseName(db, ULong(e, "HouseHC"));
                if (hn == null) continue;
                HouseLayout lay = null;
                s.houses ??= new List<HouseLayout>();
                foreach (var l in s.houses) if (l != null && Same(l.spot, hn)) { lay = l; break; }
                for (int i = 0; i < fa.Items.Count && i < 5; i++)
                {
                    string fn = fa.Items[i] is XObject fo ? Str(fo, "FrameFilename") : null;
                    if (string.IsNullOrEmpty(fn)) continue;
                    if (lay == null) { lay = new HouseLayout { spot = hn }; s.houses.Add(lay); }
                    lay.frames ??= new List<HouseEntry>();
                    string k = (i + 1).ToString(CultureInfo.InvariantCulture);
                    lay.frames.RemoveAll(f => f == null || f.k == k);
                    lay.frames.Add(new HouseEntry { k = k, v = fn });
                }
            }
        }

        static void ReadHouses(CarDatabase db, XArray garage, Dictionary<string, string> vars, string curHouseName, SaveData s)
        {
            var inGarage = new List<string>();
            var withCars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var counter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var slots = new List<GarageSlotEntry>();
            if (garage != null)
                foreach (var it in garage.Items)
                {
                    if (it is not XObject e) continue;
                    string hn = HouseName(db, ULong(e, "House"));
                    if (hn == null) continue;
                    if (!inGarage.Exists(x => Same(x, hn))) inGarage.Add(hn);
                    counter.TryGetValue(hn, out int k); counter[hn] = k + 1;
                    var c = e["Car"] as XObject;
                    if (c == null || Long(c, "Status") != 0 || !db.ById.TryGetValue((int)Long(c, "Archetype"), out var car) || string.IsNullOrEmpty(car.FileName)) continue;
                    withCars.Add(hn);
                    if (!slots.Exists(x => Same(x.car, car.FileName))) slots.Add(new GarageSlotEntry { house = hn, slot = k, car = car.FileName });
                }
            int allHouses = 0;
            foreach (var h in db.Houses) if (h.SpotName != null && h.SpotName.StartsWith("EPH_", StringComparison.OrdinalIgnoreCase) && h.CarSlots > 0) allHouses++;
            bool legacy = vars.ContainsKey("U3D_Saved") && !vars.ContainsKey("U3D_HousesVersion") && inGarage.Count >= allHouses && allHouses > 1;   // nur Dateien des Ports (Original-Spielstände haben keine U3D_-Variablen)
            s.ownedHouses = new List<string>();
            if (legacy)
            {
                s.ownedHouses.Add(TDU2Garage.StarterHouse);
                if (curHouseName != null && !s.ownedHouses.Exists(x => Same(x, curHouseName))) s.ownedHouses.Add(curHouseName);
                foreach (var hn in inGarage) if (withCars.Contains(hn) && !s.ownedHouses.Exists(x => Same(x, hn))) s.ownedHouses.Add(hn);
            }
            else s.ownedHouses.AddRange(inGarage);
            s.homeHouse = curHouseName != null && s.ownedHouses.Exists(x => Same(x, curHouseName)) ? curHouseName : null;
            s.garage = slots;
        }

        static string HouseName(CarDatabase db, ulong hc)
        {
            if (hc == 0 || hc == NoSpot) return null;
            foreach (var h in db.Houses) if (h.SpotName != null && HouseHash(h.SpotName) == hc) return h.SpotName;
            return null;
        }

        static bool IsHouse(CarDatabase db, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var h in db.Houses) if (Same(h.SpotName, name)) return h.SpotName.StartsWith("EPH_", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        /// <summary>Haus (DBHouse-Name) zu einem Spot der Insel, sonst null (Läden, Hotels …).</summary>
        public static string HouseOfSpot(CarDatabase db, Spot spot)
        {
            if (db == null || spot == null || string.IsNullOrEmpty(spot.Code) || !spot.Code.StartsWith("EPH", StringComparison.OrdinalIgnoreCase)) return null;
            var one = new List<Spot> { spot };
            foreach (var h in db.Houses)
                if (h.SpotName != null && h.SpotName.StartsWith("EPH_", StringComparison.OrdinalIgnoreCase) && string.Equals(Code(h.SpotName), spot.Code, StringComparison.OrdinalIgnoreCase)
                    && GamePC.FindSpot(one, h.SpotName) == spot)
                    return h.SpotName;
            return null;
        }

        static string UniqueHouseOfCode(CarDatabase db, string code)
        {
            string found = null;
            foreach (var h in db.Houses)
                if (h.SpotName != null && h.SpotName.StartsWith("EPH_", StringComparison.OrdinalIgnoreCase) && string.Equals(Code(h.SpotName), code, StringComparison.OrdinalIgnoreCase))
                {
                    if (found != null) return null;   // mehrdeutig (mehrere Häuser mit gleichem Code)
                    found = h.SpotName;
                }
            return found;
        }

        /// <summary>Standard-Haus der Insel (Wohnwagen Ibiza EPH_L0C_1254I, Hawaii EPH_L0A_1276), sonst das erste der Insel.</summary>
        static string DefaultHouse(List<HouseInfo> houses, string island)
        {
            string want = island == "hawai" ? "EPH_L0A_1276" : "EPH_L0C_1254I";
            foreach (var h in houses) if (Same(h.SpotName, want)) return h.SpotName;
            bool ibiza = island != "hawai";
            foreach (var h in houses) if (h.SpotName.EndsWith("I", StringComparison.Ordinal) == ibiza) return h.SpotName;
            return houses.Count > 0 ? houses[0].SpotName : null;
        }

        static string Code(string name) { int us = name.LastIndexOf('_'); return us > 0 ? name.Substring(0, us) : name; }

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static string IslandKey(string island)
        {
            if (string.IsNullOrEmpty(island)) return "ibiza";
            if (island.StartsWith("h", StringComparison.OrdinalIgnoreCase)) return "hawai";
            if (island.StartsWith("cas", StringComparison.OrdinalIgnoreCase)) return "casino";
            return "ibiza";
        }

        static CarPhysics FindCar(CarDatabase db, string file)
        {
            foreach (var c in db.Cars) if (string.Equals(c.FileName, file, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        // ------------------------------------------------------------------ XMBF-Helfer

        static XValue Clone(XValue v)
        {
            switch (v)
            {
                case XObject o:
                    var no = new XObject();
                    foreach (var kv in o.Items) no[kv.Key] = Clone(kv.Value);
                    return no;
                case XArray a:
                    var na = new XArray { ElementType = a.ElementType };
                    foreach (var it in a.Items) na.Items.Add(Clone(it));
                    return na;
                case XPrim p:
                    return new XPrim(p.Type, p.Value);
                default:
                    return v;
            }
        }

        static XArray PrimArray(XType t, params object[] values)
        {
            var a = new XArray { ElementType = t };
            foreach (var v in values) a.Items.Add(new XPrim(t, v));
            return a;
        }

        /// <summary>Wert eines vorhandenen Feldes setzen (Typ bleibt der der Vorlage; Encode wandelt passend).</summary>
        static void Set(XObject o, string key, object value)
        {
            if (!(o[key] is XPrim p)) throw new InvalidDataException("Feld " + key + " fehlt in der Vorlage");
            switch (value)
            {
                case bool b: p.Value = p.Type == XType.Bool ? (object)b : (b ? 1L : 0L); break;
                case int i: p.Value = (long)i; break;
                case uint u: p.Value = (ulong)u; break;
                case long l: p.Value = l; break;
                case ulong ul: p.Value = ul; break;
                case float f: p.Value = (double)f; break;
                case double d: p.Value = d; break;
                case string s: p.Value = s; break;
                default: p.Value = value; break;
            }
        }

        // Listen in einer Umgebungsvariablen: Einträge mit ';', Felder mit ',' getrennt, jedes Feld URL-kodiert (kein JSON)
        static string Join<T>(List<T> list, Func<T, string[]> fields)
        {
            if (list == null || list.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var e in list)
            {
                if (e == null) continue;
                if (sb.Length > 0) sb.Append(';');
                var f = fields(e);
                for (int i = 0; i < f.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Uri.EscapeDataString(f[i] ?? "")); }
            }
            return sb.ToString();
        }

        static IEnumerable<string[]> Split(string v)
        {
            if (string.IsNullOrEmpty(v)) yield break;
            foreach (var rec in v.Split(';'))
            {
                if (rec.Length == 0) continue;
                var f = rec.Split(',');
                for (int i = 0; i < f.Length; i++) f[i] = Uri.UnescapeDataString(f[i]);
                yield return f;
            }
        }

        static int Int(string v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

        static void SetVar(XArray vars, string name, string value)
        {
            foreach (var it in vars.Items)
                if (it is XObject o && Str(o, "Name") == name) { o["Value"] = new XPrim(XType.String, value ?? ""); return; }
            var e = new XObject();
            e["Name"] = new XPrim(XType.String, name);
            e["Value"] = new XPrim(XType.String, value ?? "");
            vars.Items.Add(e);
        }

        /// <summary>Erstes Objekt im Baum (Tiefensuche), auf das <paramref name="match"/> zutrifft.</summary>
        static XObject FindObject(XValue v, Func<XObject, bool> match)
        {
            if (v is XObject o)
            {
                if (match(o)) return o;
                foreach (var kv in o.Items) { var r = FindObject(kv.Value, match); if (r != null) return r; }
            }
            else if (v is XArray a)
                foreach (var it in a.Items) { var r = FindObject(it, match); if (r != null) return r; }
            return null;
        }

        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        /// <summary>MyTDULife-Profilangaben: Feld in MyLifeData.fields -> Feld der Original-Struktur PlayerData.MyLife
        /// (Feldnamen aus unpacked.exe, MngMyLife). Wert = Listenindex [vermutet].</summary>
        static readonly (string id, string key)[] LifeKeys =
        {
            ("attitude", "AttitudeId"), ("driving_camera", "FavCamId"), ("driving_help", "DrivingHelpId"),
            ("gamer_type", "GamerTypeId"), ("game_genre", "GamerGenreId"), ("gamer_kind", "GamerKindId"),
            ("like_tdu", "LikeId"), ("fav_game_mode", "FavGameMode"), ("fav_island", "FavIsland"), ("fav_car_category", "AFavcarCategory"),
        };

        static string Str(XObject o, string k) => (o[k] as XPrim)?.AsString();
        static long Long(XObject o, string k) => (o[k] as XPrim)?.AsLong() ?? 0;
        static ulong ULong(XObject o, string k) => (o[k] as XPrim)?.AsULong() ?? 0;
        static double Double(XObject o, string k) => (o[k] as XPrim)?.AsDouble() ?? 0;
    }
}