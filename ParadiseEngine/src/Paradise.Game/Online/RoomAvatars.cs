using Paradise.Core.Avatars;
using Paradise.Game.Config;
using Paradise.Game.Gamemodes;
using System.Numerics;

namespace Paradise.Game.Online;

/// <summary>
/// Mitspieler als Spielfigur im Interieur – Port von Unity TDU2Multiplayer.UpdateRoomAvatars: wer im selben Raum ist
/// (Raumkennung InteriorUtils.PresenceKey aus dem Zustandspaket, Erweiterung 0xA1), erscheint mit seinem Aussehen an seiner
/// Position (Füße im Raum der Szene) und läuft bzw. steht (cd_walk-m / co_walkt02-w, co_stand-m/-w). Häuser nur für die eigene
/// Gruppe (Party.InGroup). Türen/Fenster am Ausstellungsauto, das der Mitspieler bedient (Erweiterung 0xA2), werden übernommen.
/// </summary>
internal static class RoomAvatars
{
    sealed class Fig
    {
        public AvatarPreview Preview = null!;
        public string Cfg = "";
        public uint Shown;
        public Vector3 Vis; public float VisYaw;
        public int CarApplied, BitsApplied = -1;
    }

    static readonly Dictionary<int, Fig> _figs = new();
    static readonly List<int> _gone = new();

    /// <summary>Jedes Bild (ScreenModule nach Multiplayer).</summary>
    public static void Update(IGameModeContext ctx, float dt)
    {
        var rend = ctx.Vehicles?.Renderer;
        uint myRoom = InteriorUtils.Inside && !Multiplayer.Hidden && OnlineConfig.ShowPlayersInInteriors && rend != null ? InteriorUtils.PresenceKey : 0;
        bool priv = myRoom != 0 && InteriorUtils.IsPrivateRoom;
        _gone.Clear();
        foreach (var id in _figs.Keys) _gone.Add(id);
        foreach (var r in RemoteCars.All)
        {
            _gone.Remove(r.Id);
            _figs.TryGetValue(r.Id, out var f);
            bool want = myRoom != 0 && r.Room == myRoom && Multiplayer.FrameClock - r.RoomPacket < 3f && AvatarLibrary.Ready;
            // Haus: nur Mitspieler aus der eigenen Gruppe (Einladung/Mitfahrer)
            if (want && priv) want = Party.InGroup(r.Id);
            string? cfg = want ? AvatarConfigs.RemoteCode(r.Id) ?? "" : null;
            if (f != null && (!want || f.Shown != myRoom || cfg != f.Cfg)) { f.Preview.Dispose(); _figs.Remove(r.Id); f = null; }
            if (!want) continue;
            if (f == null)
            {
                f = new Fig { Preview = new AvatarPreview(rend!), Cfg = cfg!, Shown = myRoom, Vis = r.RoomPos, VisYaw = r.RoomYaw };
                f.Preview.Show(AvatarConfigs.ForPlayer(r.Id), true);
                _figs[r.Id] = f;
            }
            // Türen/Fenster am Ausstellungsauto, das der Mitspieler bedient (nur bei Änderung übernehmen)
            if (r.RoomCar != 0 && (r.RoomCar != f.CarApplied || r.RoomCarBits != f.BitsApplied)
                && InteriorUtils.ApplyRemoteCarState(r.RoomCar, r.RoomCarBits, r.RoomCarFlags))
            { f.CarApplied = r.RoomCar; f.BitsApplied = r.RoomCarBits; }
            // weich zum Ziel (Pakete ~20/s), Sprünge > 3 m direkt
            if ((r.RoomPos - f.Vis).LengthSquared() > 9f) f.Vis = r.RoomPos;
            else f.Vis = Vector3.Lerp(f.Vis, r.RoomPos, 1f - MathF.Exp(-dt * 10f));
            f.VisYaw = LerpAngle(f.VisYaw, r.RoomYaw, 1f - MathF.Exp(-dt * 8f));
            var p = f.Preview;
            p.Pose = Matrix4x4.CreateRotationY(f.VisYaw * MathF.PI / 180f) * Matrix4x4.CreateTranslation(f.Vis);
            bool woman = AvatarConfigs.ForPlayer(r.Id).Sex == 1;
            var clip = AvatarLibrary.Clip(r.RoomWalk ? (woman ? "co_walkt02-w" : "cd_walk-m") : (woman ? "co_stand-w" : "co_stand-m"))
                       ?? AvatarLibrary.Clip(woman ? "co_stand-w" : "co_stand-m");
            p.Clip = clip;
            p.ClipPositions = !r.RoomWalk;   // Laufen: ohne Wurzel-Versatz der Animation (Position kommt aus dem Netz)
            p.Update(dt);
        }
        foreach (var id in _gone) { _figs[id].Preview.Dispose(); _figs.Remove(id); }
    }

    /// <summary>InteriorUtils.RenderCars: Spielfiguren im Szenenrahmen zeichnen.</summary>
    public static void Render(Matrix4x4 sceneToRender)
    {
        foreach (var f in _figs.Values) f.Preview.Render(sceneToRender);
    }

    public static void Clear()
    {
        foreach (var f in _figs.Values) f.Preview.Dispose();
        _figs.Clear();
    }

    /// <summary>Unity Mathf.LerpAngle (Grad, kürzester Weg).</summary>
    static float LerpAngle(float a, float b, float t)
    {
        float d = ((b - a) % 360f + 540f) % 360f - 180f;
        return a + d * System.Math.Clamp(t, 0f, 1f);
    }
}
