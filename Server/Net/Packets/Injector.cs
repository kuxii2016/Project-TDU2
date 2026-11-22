#nullable enable
using System.Reflection;

namespace TDU2Server.Net.Packets
{
    class InjectionError : Exception
    {
        public InjectionError(string msg) : base("InjectionError: " + msg)
        { }
    }

    /// <summary>
    /// Kleiner Dienst-Container: Instanzen je Typ; Konstruktor-Parameter werden aus bereits registrierten Instanzen gefüllt
    /// (z.B. Paket-Handler-Klassen mit ServerContext/UdpServer). Erneutes Registrieren ersetzt die alte Instanz (kein Absturz bei
    /// doppeltem Aufruf). Thread-sicher.
    /// </summary>
    public class Injector
    {
        private static readonly Dictionary<Type, object> instances = new();
        private static readonly object _lock = new();

        public static T RegisterInstance<T>(T instance) where T : notnull
        {
            lock (_lock) instances[typeof(T)] = instance;
            return instance;
        }

        // TType is the type that will be used in the dictionary
        // TClass is the type of the actual class
        // TType may be the same as TClass, but TType can instead be an interface that TClass implements
        public static TType Register<TType, TClass>()
        {
            object instance = Create(typeof(TClass));
            lock (_lock) instances[typeof(TType)] = instance;
            return (TType)instance;
        }

        // Helper method incase the base type of the class is the same as the class itself
        public static TType Register<TType>() => Register<TType, TType>();

        public static object Register(Type type)
        {
            object instance = Create(type);
            lock (_lock) instances[type] = instance;
            return instance;
        }

        static object Create(Type type)
        {
            ConstructorInfo? constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderByDescending(c => c.IsPublic).FirstOrDefault();
            if (constructor == null) throw new InjectionError("No constructor found for " + type.FullName);
            ParameterInfo[] paramInfos = constructor.GetParameters();
            object[] constructorParams = new object[paramInfos.Length];
            for (int i = 0; i < paramInfos.Length; i++) constructorParams[i] = GetInstanceByType(paramInfos[i].ParameterType);
            return constructor.Invoke(constructorParams);
        }

        private static object GetInstanceByType(Type type)
        {
            lock (_lock)
                if (instances.TryGetValue(type, out var result) && result != null) return result;
            throw new InjectionError("Attempted to inject unregistered type " + type.Name);
        }

        public static T GetInstance<T>() => (T)GetInstanceByType(typeof(T));

        public static object GetInstance(Type type) => GetInstanceByType(type);

        public static object RegisterInstance(Type type, object instance)
        {
            lock (_lock) instances[type] = instance;
            return instance;
        }

        public static bool TryGetInstance(Type type, out object? instance)
        {
            lock (_lock) return instances.TryGetValue(type, out instance);
        }
    }
}
