using Paradise.Core.Flash;

namespace Paradise.Game.Racing;

/// <summary>
/// Eliminator-Rennen („der Letzte fliegt raus“) wie die exe-Komponente LapElimination (Klasse VTable 0x14f5148, Konstruktor
/// 0xb59510, Debugtext "LapElimination : State … TimeWaiting … DidntPass"). Eliminator = Rennen (gmth 3), Rundkurs ("#" ≥ 1) und
/// ig2 "nblp" = 0 (exe 0xb3c450). Ablauf:
///  - Start (0xb5a036): alle Teilnehmer zählen (Anfang = Rest = n), Runden der Checkpoint-Komponente = n − 1 (0x401483).
///  - Prüfung (0xb59e40): Runde k = Anfang − Rest + 1. Haben alle Aktiven bis auf einen Runde k beendet, wartet die exe 1,5 s
///    (0x1482264) und wirft dann diesen einen hinaus; haben alle sie im selben Moment beendet, fliegt der mit der spätesten Zeit.
///  - Ausscheiden (0xb595e0): Platz = Rest, Rest − 1. Ist es der Spieler -> verloren (Zustand 2); bleibt nur einer -> Ende (1).
///    HUD: /HUD/eliminator sichtbar und "on" (Ton eli_kill), /:temp_name = Name des Ausgeschiedenen.
///  - Liste (0xb59560/0xb59320): Zeilen /HUD/eliminator/1..8 in Reihenfolge der Platzierung, "on" solange Platz ≤ Rest, sonst
///    "off", leere Zeilen "empty"; Name in :player_name, bei Änderung update_names(Zeile) (Laufschrift).
/// </summary>
public sealed class LapElimination
{
    /// <summary>Wartezeit, bevor der Letzte ausscheidet (exe 0x1482264: 1,5 s).</summary>
    public const float WaitSeconds = 1.5f;

    public int Initial { get; private set; }
    public int Remaining { get; private set; }
    /// <summary>Endplatz je Teilnehmer (0 = noch dabei).</summary>
    public int[] FinalPlace { get; private set; } = Array.Empty<int>();
    public bool IsOut(int i) => i >= 0 && i < FinalPlace.Length && FinalPlace[i] > 0;
    public int Round => Initial - Remaining + 1;

    float _wait;
    readonly string?[] _rowName = new string?[9];
    readonly string?[] _rowState = new string?[9];

    /// <summary>exe [4] 0xb59e10 + Start 0xb5a036: Anzeige öffnen, Teilnehmer zählen.</summary>
    public void Start(VmfPlayer? h, int participants)
    {
        Initial = Remaining = System.Math.Max(1, participants);
        FinalPlace = new int[Initial];
        _wait = 0f;
        Array.Clear(_rowName); Array.Clear(_rowState);
        ChallengeModes.ShowClip(h, "/HUD/eliminator", true);
    }

    /// <summary>
    /// Ein Bild. lapsDone[i] = beendete Runden, finishTimeOfRound(i, k) = Zeit, zu der i Runde k beendet hat (für den Gleichstand).
    /// Liefert den Index des Ausgeschiedenen oder −1.
    /// </summary>
    public int Update(float dt, IReadOnlyList<int> lapsDone, Func<int, int, float> finishTimeOfRound)
    {
        if (Remaining <= 1) return -1;
        int k = Round;
        int notPassed = 0, last = -1;
        for (int i = 0; i < FinalPlace.Length && i < lapsDone.Count; i++)
        {
            if (IsOut(i)) continue;
            if (lapsDone[i] < k) { notPassed++; last = i; }
        }
        if (notPassed == 0)
        {
            // alle im selben Moment durch: der Späteste fliegt (exe 0xb59efa)
            float worst = float.MinValue; int who = -1;
            for (int i = 0; i < FinalPlace.Length; i++)
            {
                if (IsOut(i)) continue;
                float t = finishTimeOfRound(i, k);
                if (t > worst) { worst = t; who = i; }
            }
            _wait = 0f;
            return who >= 0 ? Eliminate(who) : -1;
        }
        if (notPassed == 1 && last >= 0)
        {
            _wait += dt;
            if (_wait >= WaitSeconds) { _wait = 0f; return Eliminate(last); }
            return -1;
        }
        _wait = 0f;
        return -1;
    }

    int Eliminate(int i)
    {
        FinalPlace[i] = Remaining;
        Remaining--;
        return i;
    }

    /// <summary>Gewinner festhalten (letzter Verbleibender = Platz 1).</summary>
    public void CloseWinner()
    {
        for (int i = 0; i < FinalPlace.Length; i++) if (FinalPlace[i] == 0) FinalPlace[i] = 1;
    }

    /// <summary>exe 0xb595e0 (Anzeige): /HUD/eliminator "on", /:temp_name.</summary>
    public static void ShowEliminated(VmfPlayer? h, string name)
    {
        if (h == null) return;
        h.SetVisible("/HUD/eliminator", true);
        h.GotoLabel("/HUD/eliminator", "on", true);
        h.SetVariable("/:temp_name", name);
    }

    /// <summary>
    /// exe 0xb59560: Liste in Reihenfolge der Platzierung (order = Teilnehmer-Indizes, Bester zuerst). Platz ≤ Rest -> "on",
    /// sonst "off"; Zeilen ohne Teilnehmer "empty".
    /// </summary>
    public void UpdateRows(VmfPlayer? h, IReadOnlyList<int> order, Func<int, string> nameOf)
    {
        if (h == null) return;
        for (int row = 1; row <= 8; row++)
        {
            string path = "/HUD/eliminator/" + row;
            string state, name;
            if (row <= order.Count) { state = row <= Remaining ? "on" : "off"; name = nameOf(order[row - 1]); }
            else { state = "empty"; name = ""; }
            if (_rowState[row] != state) { _rowState[row] = state; h.GotoLabel(path, state, false); }
            if (_rowName[row] != name)
            {
                _rowName[row] = name;
                h.SetVariable(path + ":player_name", name);
                if (name.Length > 0) h.CallClip("/HUD/eliminator", "update_names", (double)row);
            }
        }
    }
}
