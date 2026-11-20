// Szenen-Editor: hält beide Fenster (Hierarchy + Inspector) auf dem UI-Thread zusammen.

namespace Paradise.Editor;

internal sealed class EditorContext : ApplicationContext
{
    public readonly HierarchyForm Hierarchy;
    public readonly InspectorForm Inspector;
    bool _exiting;
    string? _lastSelected;

    public EditorContext()
    {
        Hierarchy = new HierarchyForm(this);
        Inspector = new InspectorForm();

        // Wie Unity: Hierarchy links, Inspector rechts
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
        int h = System.Math.Max(500, wa.Height - 80);
        Hierarchy.StartPosition = FormStartPosition.Manual;
        Hierarchy.Bounds = new Rectangle(wa.Left + 20, wa.Top + 40, 440, h);
        Inspector.StartPosition = FormStartPosition.Manual;
        Inspector.Bounds = new Rectangle(wa.Right - 540, wa.Top + 40, 520, h);

        // Inspector schließen = ausblenden (über "Inspector" in der Hierarchy wieder holen); Hierarchy schließen = Editor zu
        Inspector.FormClosing += (_, e) =>
        {
            if (_exiting || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Inspector.Hide();
        };
        MainForm = Hierarchy;   // Schließen des Hauptfensters beendet den UI-Thread
        Inspector.Show();
    }

    public void Apply(SceneSnapshot s)
    {
        if (Hierarchy.IsDisposed) return;
        Hierarchy.Apply(s);
        // Neue Auswahl holt den ausgeblendeten Inspector zurück (Unity: Inspector zeigt immer die Auswahl)
        if (s.SelectedId != null && s.SelectedId != _lastSelected && !Inspector.Visible && !Inspector.IsDisposed) Inspector.Show();
        _lastSelected = s.SelectedId;
        if (!Inspector.IsDisposed && Inspector.Visible) Inspector.Apply(s.Inspector, s.Status);
    }

    public void ShowInspector()
    {
        if (Inspector.IsDisposed) return;
        if (!Inspector.Visible) Inspector.Show();
        if (Inspector.WindowState == FormWindowState.Minimized) Inspector.WindowState = FormWindowState.Normal;
        Inspector.Activate();
    }

    public void FocusAll()
    {
        ShowInspector();
        if (Hierarchy.WindowState == FormWindowState.Minimized) Hierarchy.WindowState = FormWindowState.Normal;
        Hierarchy.Activate();
        Hierarchy.BringToFront();
    }

    protected override void ExitThreadCore()
    {
        _exiting = true;
        try { if (!Inspector.IsDisposed) Inspector.Close(); } catch (Exception) { }
        base.ExitThreadCore();
    }
}
