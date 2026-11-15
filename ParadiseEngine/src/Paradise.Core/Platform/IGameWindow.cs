namespace Paradise.Core.Platform;

/// <summary>Abstraktion über das Fenster. Win32Window implementiert sie; Tests können sie mocken.</summary>
public interface IGameWindow : IDisposable
{
    /// <summary>Natives Fenster-Handle (HWND) für die Vulkan-Surface.</summary>
    nint Handle { get; }
    int Width { get; }
    int Height { get; }
    bool IsClosing { get; }

    /// <summary>Eingabezustand (Tastatur + Maus).</summary>
    InputState Input { get; }

    /// <summary>Verarbeitet anstehende Fenster-Nachrichten (nicht blockierend).</summary>
    void PumpEvents();

    void SetTitle(string title);

    event Action<int, int>? Resized;
    event Action? CloseRequested;
}
