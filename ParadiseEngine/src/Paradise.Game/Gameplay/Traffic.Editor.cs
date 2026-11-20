using Paradise.Core.Vehicles;
using System.Numerics;

namespace Paradise.Game.Gameplay;

/// <summary>Nur-lese-Sicht auf die Verkehrsautos für den Szenen-Editor (F1 &gt; Open Editor). Hauptthread.</summary>
public sealed partial class Traffic
{
    public readonly record struct EditorCar(uint Id, string Type, CarModel? Model, bool Visible, bool Mine, int Mode, Vector3 Position, float Speed);

    public List<EditorCar> EditorCars()
    {
        var l = new List<EditorCar>(_cars.Count);
        foreach (var c in _cars.Values)
            l.Add(new EditorCar(c.Id, c.Type.Name, c.Model, c.Visible && !c.Removed, c.Mine, c.Mode, c.Pos, c.V));
        l.Sort((a, b) => a.Id.CompareTo(b.Id));
        return l;
    }
}
