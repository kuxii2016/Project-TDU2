using Paradise.Core.Flash;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Paradise.Core.Net
{
    /// <summary>
    /// Einfacher Paketpuffer zum Schreiben (Push…) und Lesen (Pop…), little-endian. Ein Paket beginnt mit seiner Kennung
    /// (<see cref="Store(ushort)"/>, u16 "magic"); <see cref="PacketRouter"/> verteilt empfangene Pakete nach dieser Kennung an die
    /// [PacketHandler]-Methoden. Lesefehler (zu kurzes Paket) werfen nicht, sondern setzen <see cref="ErrorLess"/> = false und liefern
    /// Standardwerte – Handler prüfen am Ende einmal <see cref="ErrorLess"/>.
    /// Zeichenketten: u16 Länge in Bytes + Daten (ASCII bzw. UTF-8). Objekte: u32 Länge + JSON (System.Text.Json, ersetzt den
    /// veralteten und unsicheren BinaryFormatter – Felder und Eigenschaften werden geschrieben).
    /// </summary>
    public class Store : IDisposable
    {
        private readonly MemoryStream stream;
        private readonly BinaryWriter writer;
        private readonly BinaryReader reader;
        private bool error = false;

        /// <summary>JSON-Einstellungen für Objekte (öffentliche Felder wie JsonUtility in Unity).</summary>
        public static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

        #region Constructors

        /// <summary>Leerer Store (zum Schreiben).</summary>
        public Store()
        {
            stream = new MemoryStream();
            writer = new BinaryWriter(stream, Encoding.UTF8, true);
            reader = new BinaryReader(stream, Encoding.UTF8, true);
        }

        /// <summary>Store über empfangene Daten (zum Lesen, Position 0).</summary>
        public Store(byte[] data) : this(data, 0, data?.Length ?? 0) { }

        /// <summary>Store über einen Ausschnitt empfangener Daten (z.B. hinter dem UDP-Kopf), ohne zu kopieren.</summary>
        public Store(byte[] data, int offset, int count)
        {
            data ??= Array.Empty<byte>();
            offset = System.Math.Clamp(offset, 0, data.Length);
            count = System.Math.Clamp(count, 0, data.Length - offset);
            stream = new MemoryStream(data, offset, count, false, true);
            writer = new BinaryWriter(Stream.Null);   // nur lesen
            reader = new BinaryReader(stream, Encoding.UTF8, true);
        }

        /// <summary>Neues Paket mit Kennung (u16 magic als erstes Feld).</summary>
        public Store(ushort packet) : this()
        {
            Magic = packet;
            PushUshort(packet);
        }

        /// <summary>Inhalt ersetzen (Position danach 0).</summary>
        public void SetBytes(byte[] data)
        {
            Reset();
            PushBytes(data);
            stream.Position = 0;
        }
        #endregion

        #region Properties

        /// <summary>Kennung, mit der das Paket angelegt wurde (0 = ohne).</summary>
        public ushort Magic { get; }

        /// <summary>Gesamtlänge.</summary>
        public int Length => (int)stream.Length;

        /// <summary>Noch nicht gelesene Bytes.</summary>
        public int UnreadLength => (int)(stream.Length - stream.Position);

        /// <summary>Lese-/Schreibposition.</summary>
        public int Position { get => (int)stream.Position; set => stream.Position = System.Math.Clamp(value, 0, (int)stream.Length); }

        /// <summary>true, solange beim Lesen kein Fehler auftrat (Paket lang genug, Daten gültig).</summary>
        public bool ErrorLess => !error;

        /// <summary>Inhalt als Byte-Array (Kopie).</summary>
        public byte[] ToArray() => stream.ToArray();

        /// <summary>Inhalt ohne Kopie (nur für Schreib-Stores; gültig bis zur nächsten Änderung).</summary>
        public ArraySegment<byte> AsSegment() => stream.TryGetBuffer(out var seg) ? seg : new ArraySegment<byte>(stream.ToArray());

        #endregion

        #region Reset / Length

        /// <summary>Leeren (clear) bzw. die letzten 4 gelesenen Bytes zurückgehen (clear = false, Unity-Kompatibilität).</summary>
        public void Reset(bool clear = true)
        {
            if (clear)
            {
                if (stream.CanWrite) stream.SetLength(0);
                stream.Position = 0;
                error = false;
            }
            else
            {
                stream.Position = System.Math.Max(0, stream.Position - 4);
            }
        }

        /// <summary>Wieder von vorne lesen.</summary>
        public void Rewind() { stream.Position = 0; error = false; }

        /// <summary>Aktuelle Gesamtlänge als i32 an den Anfang schreiben (Paketkopf, der dafür vorher reserviert wurde).</summary>
        public void WriteLength()
        {
            long currentPos = stream.Position;
            stream.Position = 0;
            writer.Write((int)stream.Length);
            writer.Flush();
            stream.Position = currentPos;
        }

        /// <summary>u32 an den Anfang schreiben (Sitzung; Platz muss vorher reserviert sein).</summary>
        public void InsertSession(uint value)
        {
            long currentPos = stream.Position;
            stream.Position = 0;
            writer.Write(value);
            writer.Flush();
            stream.Position = System.Math.Max(currentPos, 4);
        }

        #endregion

        #region Push Methods

        /// <summary>Objekt als JSON (u32 Länge + UTF-8). null = Länge 0.</summary>
        public void PushObject<T>(T value)
        {
            if (value == null) { PushUint(0); return; }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), JsonOptions);
            PushUint((uint)bytes.Length);
            PushBytes(bytes);
        }

        /// <summary>Objekt aus JSON lesen (Gegenstück zu <see cref="PushObject{T}"/>).</summary>
        public T? PopObject<T>()
        {
            object? o = PopObject(typeof(T));
            return o is T t ? t : default;
        }

        /// <summary>Objekt eines bekannten Typs aus JSON lesen (null bei Länge 0 oder Fehler).</summary>
        public object? PopObject(Type type)
        {
            uint length = PopUint();
            if (error || length == 0) return null;
            if (length > UnreadLength) { error = true; return null; }
            try
            {
                var span = PopSpan((int)length);
                return JsonSerializer.Deserialize(span, type, JsonOptions);
            }
            catch { error = true; return null; }
        }

        public void PushByte(byte value) => writer.Write(value);
        public void PushBytes(byte[] value) { if (value != null) writer.Write(value); }
        public void PushBytes(ReadOnlySpan<byte> value) => writer.Write(value);
        public void PushUshort(ushort value) => writer.Write(value);
        public void PushShort(short value) => writer.Write(value);
        public void PushUint(uint value) => writer.Write(value);
        public void PushInt(int value) => writer.Write(value);
        public void PushUlong(ulong value) => writer.Write(value);
        public void PushLong(long value) => writer.Write(value);
        public void PushFloat(float value) => writer.Write(value);
        public void PushDouble(double value) => writer.Write(value);
        public void PushBool(bool value) => writer.Write(value);

        /// <summary>ASCII-Text fester Länge (aufgefüllt mit 0, zu lange Texte abgeschnitten).</summary>
        public void PushString(string str, ushort stringLength)
        {
            var text = new byte[stringLength];
            if (!string.IsNullOrEmpty(str))
            {
                var b = Encoding.ASCII.GetBytes(str);
                Array.Copy(b, text, System.Math.Min(b.Length, text.Length));
            }
            PushRaw(text, stringLength);
        }

        public bool PushRaw(byte[] buffer, ushort len)
        {
            if (buffer == null || len == 0) return false;
            int copyLen = System.Math.Min(len, buffer.Length);
            writer.Write(buffer, 0, copyLen);
            return true;
        }

        /// <summary>ASCII-Text: u16 Länge + Daten.</summary>
        public void Push(string value) => PushText(value, Encoding.ASCII);
        public void PushAscii(string value) => PushText(value, Encoding.ASCII);
        /// <summary>UTF-8-Text: u16 Länge in Bytes (vorher die Zeichenzahl – Umlaute wurden abgeschnitten) + Daten.</summary>
        public void PushUTF8(string value) => PushText(value, Encoding.UTF8);

        void PushText(string value, Encoding enc)
        {
            value ??= "";
            byte[] b = enc.GetBytes(value);
            int n = System.Math.Min(b.Length, ushort.MaxValue);
            PushUshort((ushort)n);
            writer.Write(b, 0, n);
        }

        public void PushVector3(Vector3 v)
        {
            PushFloat(v.X);
            PushFloat(v.Y);
            PushFloat(v.Z);
        }

        public void PushQuaternion(Quaternion q)
        {
            PushFloat(q.X);
            PushFloat(q.Y);
            PushFloat(q.Z);
            PushFloat(q.W);
        }

        public void PushColor(Color c)
        {
            PushFloat(c.r);
            PushFloat(c.g);
            PushFloat(c.b);
            PushFloat(c.a);
        }

        public void PushColor32(Color32 c)
        {
            PushByte(c.r);
            PushByte(c.g);
            PushByte(c.b);
            PushByte(c.a);
        }

        /// <summary>Anderen Store einbetten (u16 Länge + Inhalt).</summary>
        public void PushStore(Store other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            var seg = other.AsSegment();
            PushUshort((ushort)System.Math.Min(seg.Count, ushort.MaxValue));
            writer.Write(seg.AsSpan(0, System.Math.Min(seg.Count, ushort.MaxValue)));
        }

        public void PushIpAddress(IPAddress ip)
        {
            byte[] b = ip?.GetAddressBytes() ?? new byte[4];
            PushInt(b.Length);
            PushBytes(b);
        }

        #endregion

        #region Pop Methods

        /// <summary>Lesefehler (Paket zu kurz): merken, Standardwert liefern.</summary>
        bool Need(int bytes)
        {
            if (!error && stream.Length - stream.Position >= bytes) return true;
            error = true;
            stream.Position = stream.Length;
            return false;
        }

        public byte PopByte() => Need(1) ? reader.ReadByte() : (byte)0;
        public byte[] PopBytes(int length) => length >= 0 && Need(length) ? reader.ReadBytes(length) : Array.Empty<byte>();
        public ushort PopUshort() => Need(2) ? reader.ReadUInt16() : (ushort)0;
        public short PopShort() => Need(2) ? reader.ReadInt16() : (short)0;
        public uint PopUint() => Need(4) ? reader.ReadUInt32() : 0u;
        public int PopInt() => Need(4) ? reader.ReadInt32() : 0;
        public ulong PopUlong() => Need(8) ? reader.ReadUInt64() : 0ul;
        public long PopLong() => Need(8) ? reader.ReadInt64() : 0L;
        public float PopFloat() => Need(4) ? reader.ReadSingle() : 0f;
        public double PopDouble() => Need(8) ? reader.ReadDouble() : 0d;
        public bool PopBool() => Need(1) && reader.ReadBoolean();

        public uint PeekUint()
        {
            long pos = stream.Position;
            uint value = PopUint();
            stream.Position = pos;
            return value;
        }

        public ushort PeekUshort()
        {
            long pos = stream.Position;
            ushort value = PopUshort();
            stream.Position = pos;
            return value;
        }

        /// <summary>Bytes ohne Kopie lesen (gültig, solange der Store lebt).</summary>
        ReadOnlySpan<byte> PopSpan(int length)
        {
            if (length < 0 || !Need(length)) return ReadOnlySpan<byte>.Empty;
            if (stream.TryGetBuffer(out var seg))
            {
                var span = seg.AsSpan((int)stream.Position, length);
                stream.Position += length;
                return span;
            }
            return reader.ReadBytes(length);
        }

        public IPAddress PopIpAdress()
        {
            int length = PopInt();
            if (length != 4 && length != 16) { error = true; return IPAddress.Any; }
            var bytes = PopBytes(length);
            return error ? IPAddress.Any : new IPAddress(bytes);
        }

        public string PopAscii() => PopText(Encoding.ASCII);
        public string PopUTF8() => PopText(Encoding.UTF8);

        string PopText(Encoding enc)
        {
            int length = PopUshort();
            var span = PopSpan(length);
            return error ? "" : enc.GetString(span);
        }

        /// <summary>ASCII-Text fester Länge (Gegenstück zu <see cref="PushString"/>, abschließende Nullen entfernt).</summary>
        public string PopString(int bufferSize)
        {
            var span = PopSpan(bufferSize);
            if (error) return "";
            int n = span.IndexOf((byte)0);
            return Encoding.ASCII.GetString(n >= 0 ? span[..n] : span);
        }

        public Vector3 PopVector3() => new(PopFloat(), PopFloat(), PopFloat());
        public Quaternion PopQuaternion() => new(PopFloat(), PopFloat(), PopFloat(), PopFloat());
        public Color PopColor() => new(PopFloat(), PopFloat(), PopFloat(), PopFloat());
        public Color32 PopColor32() => new(PopByte(), PopByte(), PopByte(), PopByte());

        /// <summary>Eingebetteten Store lesen.</summary>
        public Store PopStore()
        {
            ushort length = PopUshort();
            return new Store(PopBytes(length));
        }

        #endregion

        #region Helper

        public void Dispose()
        {
            writer.Dispose();
            reader.Dispose();
            stream.Dispose();
        }

        #endregion
    }
}