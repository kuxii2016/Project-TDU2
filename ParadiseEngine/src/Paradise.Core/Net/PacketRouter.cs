using Paradise.Core.Diagnostics;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;

namespace Paradise.Core.Net
{
    /// <summary>Markiert eine Methode als Empfänger für Pakete mit dieser Kennung (u16 magic, erstes Feld des <see cref="Store"/>).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class PacketHandler : Attribute
    {
        private readonly ushort packetType;

        public PacketHandler(ushort packetMagic) { packetType = packetMagic; }

        public ushort GetPacketType() => packetType;
    }

    /// <summary>Herkunft eines empfangenen Pakets.</summary>
    public sealed class PacketContext
    {
        /// <summary>Absender (Client: Server-Endpunkt; Server: Endpunkt des Spielers).</summary>
        public IPEndPoint? EndPoint;
        /// <summary>Spielernummer des Absenders (0 = Server).</summary>
        public int SenderId;
        /// <summary>Absender-Objekt der Gegenseite (Server: Sitzung des Spielers; Client: null).</summary>
        public object? Sender;
    }

    /// <summary>
    /// Verteilt Pakete nach ihrer Kennung an [PacketHandler]-Methoden. Handler-Parameter werden nach Typ gefüllt:
    /// <see cref="Store"/> (Nutzdaten hinter der Kennung), <see cref="IPEndPoint"/>, <see cref="PacketContext"/>, int (Spielernummer
    /// des Absenders) oder der Typ des Absender-Objekts (Server: Session). Beispiel: <c>[PacketHandler(1499)] void Ping(Store s, IPEndPoint ep)</c>.
    /// Aufrufe laufen über vorab erzeugte Delegaten (kein MethodInfo.Invoke je Paket). Der Store wird nach dem Handler freigegeben –
    /// nichts daraus aufheben. Registrieren ist mehrfach erlaubt (gleiche Klasse wird nur einmal angemeldet).
    /// </summary>
    public class PacketRouter
    {
        public class PacketHandlerInfo
        {
            public object? Instance { get; set; }
            public MethodInfo Method { get; set; } = null!;
            public Action<object?, Store, PacketContext> Invoke { get; set; } = null!;
        }

        public static readonly Dictionary<ushort, PacketHandlerInfo> handlers = new();
        static readonly HashSet<Type> _registered = new();
        static readonly object _lock = new();

        public static ulong CompletePackets = 0;
        public static ulong FailedPackets = 0;
        public static ulong UnknownPackets = 0;

        /// <summary>Handler einer Klasse anmelden (Instanz über den <see cref="Injector"/>).</summary>
        public static void RegisterHandlers<T>() where T : class => RegisterHandlers(typeof(T));

        /// <summary>Handler einer Klasse anmelden; existingInstance = vorhandenes Objekt statt neu anzulegen.</summary>
        public static void RegisterHandlers(Type type, object? existingInstance = null)
        {
            lock (_lock)
            {
                if (!_registered.Add(type)) return;
                object? instance = null;
                bool needsInstance = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(m => m.GetCustomAttribute<PacketHandler>() != null);
                if (needsInstance)
                    instance = existingInstance != null ? Injector.RegisterInstance(type, existingInstance)
                             : Injector.TryGetInstance(type, out var have) ? have : Injector.Register(type);

                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var handler = method.GetCustomAttribute<PacketHandler>();
                    if (handler == null) continue;
                    ushort magic = handler.GetPacketType();
                    if (handlers.ContainsKey(magic))
                    {
                        Log.Error("Duplicate Packet Handler: " + magic + " (" + type.Name + "." + method.Name + ")");
                        continue;
                    }
                    try
                    {
                        handlers.Add(magic, new PacketHandlerInfo
                        {
                            Instance = method.IsStatic ? null : instance,
                            Method = method,
                            Invoke = Compile(method),
                        });
                    }
                    catch (Exception ex) { Log.Error("Packet Handler " + type.Name + "." + method.Name + ": " + ex.Message); }
                }
            }
        }

        /// <summary>(instance, store, ctx) => method(params nach Typ) – einmal je Handler erzeugt.</summary>
        static Action<object?, Store, PacketContext> Compile(MethodInfo method)
        {
            var inst = Expression.Parameter(typeof(object), "inst");
            var store = Expression.Parameter(typeof(Store), "store");
            var ctx = Expression.Parameter(typeof(PacketContext), "ctx");
            var args = new List<Expression>();
            foreach (var p in method.GetParameters())
            {
                var t = p.ParameterType;
                if (t == typeof(Store)) args.Add(store);
                else if (t == typeof(PacketContext)) args.Add(ctx);
                else if (t == typeof(IPEndPoint)) args.Add(Expression.Field(ctx, nameof(PacketContext.EndPoint)));
                else if (t == typeof(int)) args.Add(Expression.Field(ctx, nameof(PacketContext.SenderId)));
                else if (!t.IsValueType) args.Add(Expression.TypeAs(Expression.Field(ctx, nameof(PacketContext.Sender)), t));
                else throw new NotSupportedException("Parameter " + p.Name + " (" + t.Name + ") wird nicht unterstützt");
            }
            Expression call = method.IsStatic
                ? Expression.Call(method, args)
                : Expression.Call(Expression.Convert(inst, method.DeclaringType!), method, args);
            return Expression.Lambda<Action<object?, Store, PacketContext>>(call, inst, store, ctx).Compile();
        }

        /// <summary>Kompatibel zum ersten Entwurf: Paket ohne Absender-Informationen.</summary>
        public static void Handle(ushort magic, Store store, IPEndPoint endPoint) => Handle(magic, store, new PacketContext { EndPoint = endPoint });

        /// <summary>Paket an seinen Handler (Store steht hinter der Kennung). false = keiner angemeldet.</summary>
        public static bool Handle(ushort magic, Store store, PacketContext ctx)
        {
            PacketHandlerInfo? handlerInfo;
            lock (_lock) handlers.TryGetValue(magic, out handlerInfo);
            ProfilerNetwork.StoreIn(magic);   // Profiler: Häufigkeit je Store-Pakettyp
            using (store)
            {
                if (handlerInfo == null) { UnknownPackets++; return false; }
                try
                {
                    handlerInfo.Invoke(handlerInfo.Instance, store, ctx);
                    if (store.ErrorLess) CompletePackets++;
                    else { FailedPackets++; Log.Warn("Paket " + magic + " zu kurz/ungültig (" + handlerInfo.Method.Name + ")"); }
                }
                catch (Exception ex)
                {
                    Log.Error("Error handling packet " + magic + ": " + ex.Message);
                    FailedPackets++;
                }
            }
            return true;
        }

        /// <summary>Vollständiges Paket (Kennung + Nutzdaten) verteilen.</summary>
        public static bool HandleRaw(byte[] data, int offset, int count, PacketContext ctx)
        {
            var store = new Store(data, offset, count);
            ushort magic = store.PopUshort();
            if (!store.ErrorLess) { store.Dispose(); FailedPackets++; return false; }
            return Handle(magic, store, ctx);
        }
    }
}