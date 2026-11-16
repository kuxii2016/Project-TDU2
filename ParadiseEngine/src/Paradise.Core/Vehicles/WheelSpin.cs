using Paradise.Core.Math;
using System.Numerics;

namespace Paradise.Core.Vehicles
{
    /// <summary>
    /// Räder eines Anzeige-Autos (ohne Physik, z.B. Titel-Kino, Ausfahrt aus der Garage) drehen sich mit der gefahrenen Strecke:
    /// Winkel += Weg / Reifenradius, wie Vehicle (DISC_xx um die lokale X-Achse).
    /// (dort ein MonoBehaviour; hier ruft der Besitzer <see cref="Update"/> mit der Lage im Spielraum auf).
    /// </summary>
    public sealed class WheelSpin
    {
        sealed class Wheel { public CarNode? Disc, Spin2; public Quaternion DiscBase, Spin2Base; public float Radius, Angle; }

        readonly Wheel[] _wheels;
        Vector3 _last;
        bool _haveLast;

        WheelSpin(Wheel[] w) { _wheels = w; }

        public static WheelSpin? Attach(CarModel? model, CarAssets? assets)
        {
            if (model == null) return null;
            var list = new List<Wheel>();
            bool bike = assets?.Car != null && assets.Car.IsBike;
            foreach (var n in bike ? new[] { "F", "R" } : new[] { "FL", "FR", "RL", "RR" })
            {
                bool front = n[0] == 'F';
                var disc = bike ? (model.Find("D_RIM_" + n) ?? model.Find("RIM_" + n)) : (model.Find("DISC_" + n) ?? model.Find("D_RIM_" + n));
                var spin2 = bike ? model.Find("TIRE_" + n) : null;
                if (disc == null && spin2 == null) continue;
                float r = model.TireRadiusFx(front);
                list.Add(new Wheel
                {
                    Disc = disc,
                    Spin2 = spin2,
                    DiscBase = disc != null ? disc.LocalRotation : Quaternion.Identity,
                    Spin2Base = spin2 != null ? spin2.LocalRotation : Quaternion.Identity,
                    Radius = Mathf.Max(0.15f, r),
                });
            }
            return list.Count == 0 ? null : new WheelSpin(list.ToArray());
        }

        /// <summary>Neu ansetzen (z.B. nach einem Schnitt).</summary>
        public void Reset() => _haveLast = false;

        /// <summary>Jedes Bild: Lage des Autos im Spielraum (Position, Vorwärtsrichtung).</summary>
        public void Update(Vector3 position, Vector3 forward)
        {
            if (!_haveLast) { _last = position; _haveLast = true; return; }
            var d = position - _last;
            _last = position;
            // Schnitt/Sprung (Kino-Schnitt, Neustart der Schleife): nicht drehen
            if (d.LengthSquared() > 25f) return;
            float dist = Vector3.Dot(d, forward);
            if (MathF.Abs(dist) < 1e-5f) return;
            foreach (var w in _wheels)
            {
                w.Angle = (w.Angle + dist / w.Radius * (180f / MathF.PI)) % 360f;
                if (w.Angle < 0f) w.Angle += 360f;
                var spin = Quaternion.CreateFromAxisAngle(Vector3.UnitX, w.Angle * (MathF.PI / 180f));
                if (w.Disc != null) w.Disc.LocalRotation = w.DiscBase * spin;
                if (w.Spin2 != null) w.Spin2.LocalRotation = w.Spin2Base * spin;
            }
        }
    }
}
