using System;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;

namespace SeaPowerUX.Core.Foundation
{
    /// <summary>
    /// Cached MethodInfo/FieldInfo/PropertyInfo lookups shared by NoesisInterop and
    /// XamlInjection. Reflection into game/Noesis types is called every frame in places
    /// (position polling), so results are cached by (type, member name) after first lookup.
    /// This never patches anything — it only resolves member handles for plain gets/sets/calls.
    /// </summary>
    public static class ReflectionCache
    {
        private static readonly ConcurrentDictionary<(Type, string), FieldInfo> Fields =
            new ConcurrentDictionary<(Type, string), FieldInfo>();
        private static readonly ConcurrentDictionary<(Type, string), PropertyInfo> Properties =
            new ConcurrentDictionary<(Type, string), PropertyInfo>();
        private static readonly ConcurrentDictionary<(Type, string), MethodInfo> Methods =
            new ConcurrentDictionary<(Type, string), MethodInfo>();

        public static FieldInfo Field(Type type, string name)
        {
            return Fields.GetOrAdd((type, name), key => AccessTools.Field(key.Item1, key.Item2));
        }

        public static PropertyInfo Property(Type type, string name)
        {
            return Properties.GetOrAdd((type, name), key => AccessTools.Property(key.Item1, key.Item2));
        }

        public static MethodInfo Method(Type type, string name)
        {
            return Methods.GetOrAdd((type, name), key => AccessTools.Method(key.Item1, key.Item2));
        }
    }
}
