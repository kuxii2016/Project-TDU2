namespace Paradise.Core.Engine;

/// <summary>
/// Ein Baustein des Spiels (Weltstreaming, Fahrzeuge, UI, Netzwerk …).
/// Das Game-Projekt registriert seine Module beim GameLoop; der Loop ruft
/// sie in fester Reihenfolge auf. So bleibt Core frei von Spiel-Logik.
/// </summary>
public interface IGameModule
{
    void Load();
    /// <summary>Fester Takt für Physik/Netzwerk.</summary>
    void FixedUpdate(in GameTime time);
    /// <summary>
    /// Pro Frame vor Flash-Update und allen normalen Modul-Updates: Tastatur/Gamepad/Lenkrad einmal je Bild übernehmen,
    /// damit Flash-Menüs und Fahrzeuge auf denselben Eingabezustand reagieren.
    /// </summary>
    void EarlyUpdate(in GameTime time) { }
    /// <summary>Pro Frame für Input/Kamera/Animation.</summary>
    void Update(in GameTime time);
    /// <summary>Pro Frame fürs Zeichnen.</summary>
    void Render(in GameTime time);
    void Unload();
}

/// <summary>Bequeme Basisklasse: nur überschreiben, was gebraucht wird.</summary>
public abstract class GameModule : IGameModule
{
    public virtual void Load() { }
    public virtual void FixedUpdate(in GameTime time) { }
    public virtual void EarlyUpdate(in GameTime time) { }
    public virtual void Update(in GameTime time) { }
    public virtual void Render(in GameTime time) { }
    public virtual void Unload() { }
}