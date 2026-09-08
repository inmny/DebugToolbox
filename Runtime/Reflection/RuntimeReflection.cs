using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DebugToolbox.Runtime.Reflection;

internal static class RuntimeReflection
{
    private static readonly Dictionary<string, Type> Aliases = new(StringComparer.Ordinal)
    {
        ["bool"] = typeof(bool),
        ["byte"] = typeof(byte),
        ["char"] = typeof(char),
        ["decimal"] = typeof(decimal),
        ["double"] = typeof(double),
        ["float"] = typeof(float),
        ["int"] = typeof(int),
        ["long"] = typeof(long),
        ["object"] = typeof(object),
        ["sbyte"] = typeof(sbyte),
        ["short"] = typeof(short),
        ["string"] = typeof(string),
        ["uint"] = typeof(uint),
        ["ulong"] = typeof(ulong),
        ["ushort"] = typeof(ushort)
    };

    public static Type ResolveType(string typeName)
    {
        if (Aliases.TryGetValue(typeName, out var alias)) return alias;
        var type = Type.GetType(typeName, false);
        if (type != null) return type;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(typeName, false);
            if (type != null) return type;
        }
        throw new TypeLoadException($"Type not found in loaded assemblies: {typeName}");
    }

    public static FieldInfo FindField(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(name,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw new MissingFieldException(type.FullName, name);
    }

    public static IEnumerable<MethodInfo> FindMethods(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var method in current.GetMethods(
                         BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.Name == name && !method.ContainsGenericParameters)
                {
                    yield return method;
                }
            }
        }
    }

    public static MethodInfo FindMethod(Type type, string name, IReadOnlyList<string> parameterTypeNames)
    {
        var methods = FindMethods(type, name).ToArray();
        if (parameterTypeNames != null)
        {
            var parameterTypes = parameterTypeNames.Select(ResolveType).ToArray();
            methods = methods.Where(method => ParametersMatch(method.GetParameters(), parameterTypes)).ToArray();
        }

        if (methods.Length == 0) throw new MissingMethodException(type.FullName, name);
        if (methods.Length > 1) throw new AmbiguousMatchException($"Method is overloaded; provide parameterTypes: {type.FullName}.{name}");
        return methods[0];
    }

    private static bool ParametersMatch(ParameterInfo[] parameters, Type[] expected)
    {
        if (parameters.Length != expected.Length) return false;
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameterType = parameters[index].ParameterType;
            if (parameterType.IsByRef) parameterType = parameterType.GetElementType();
            if (parameterType != expected[index]) return false;
        }
        return true;
    }
}
