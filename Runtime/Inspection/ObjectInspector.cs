using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DebugToolbox.Runtime.Inspection;

internal sealed class ObjectInspector
{
    private const int MaxCollectionItems = 128;
    private readonly ObjectRegistry _registry;

    public ObjectInspector(ObjectRegistry registry)
    {
        _registry = registry;
    }

    public JObject Inspect(object target, int depth = 1, bool includeProperties = false)
    {
        if (target == null)
        {
            return new JObject { ["value"] = JValue.CreateNull() };
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return InspectObject(target, Math.Max(0, depth), includeProperties, visited);
    }

    public JToken DescribeValue(object value, int depth = 0)
    {
        return DescribeValue(value, depth, false, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private JObject InspectObject(object target, int depth, bool includeProperties, HashSet<object> visited)
    {
        var result = _registry.Reference(target);
        if (target is UnityEngine.Object unityObject)
        {
            var destroyed = unityObject == null;
            result["destroyed"] = destroyed;
            if (!destroyed)
            {
                result["name"] = unityObject.name;
                result["instanceId"] = unityObject.GetInstanceID();
            }
        }
        if (!visited.Add(target))
        {
            result["cycle"] = true;
            return result;
        }

        var fields = new JArray();
        foreach (var field in GetFields(target.GetType()))
        {
            var item = new JObject
            {
                ["name"] = field.Name,
                ["declaringType"] = field.DeclaringType?.FullName,
                ["type"] = field.FieldType.FullName,
                ["static"] = field.IsStatic,
                ["visibility"] = GetVisibility(field)
            };

            try
            {
                item["value"] = DescribeValue(field.GetValue(field.IsStatic ? null : target), depth - 1, includeProperties, visited);
            }
            catch (Exception exception)
            {
                item["error"] = exception.Message;
            }
            fields.Add(item);
        }
        result["fields"] = fields;

        if (includeProperties)
        {
            var properties = new JArray();
            foreach (var property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var item = new JObject
                {
                    ["name"] = property.Name,
                    ["declaringType"] = property.DeclaringType?.FullName,
                    ["type"] = property.PropertyType.FullName,
                    ["canRead"] = property.CanRead,
                    ["canWrite"] = property.CanWrite
                };
                properties.Add(item);
            }
            result["properties"] = properties;
        }

        visited.Remove(target);
        return result;
    }

    private JToken DescribeValue(object value, int depth, bool includeProperties, HashSet<object> visited)
    {
        if (value == null)
        {
            return JValue.CreateNull();
        }

        var type = value.GetType();
        if (type.IsEnum)
        {
            return new JObject
            {
                ["name"] = value.ToString(),
                ["value"] = Convert.ToInt64(value, CultureInfo.InvariantCulture),
                ["type"] = type.FullName
            };
        }

        if (value is string || value is char || value is bool || value is DateTime || value is Guid || IsNumber(type))
        {
            return JToken.FromObject(value);
        }

        if (value is Type reflectedType)
        {
            return new JObject { ["type"] = reflectedType.FullName, ["assembly"] = reflectedType.Assembly.GetName().Name };
        }

        if (depth < 0)
        {
            return _registry.Reference(value);
        }

        if (value is IDictionary dictionary)
        {
            var items = new JArray();
            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= MaxCollectionItems)
                {
                    break;
                }
                items.Add(new JObject
                {
                    ["key"] = DescribeValue(entry.Key, depth - 1, includeProperties, visited),
                    ["value"] = DescribeValue(entry.Value, depth - 1, includeProperties, visited)
                });
            }
            return new JObject
            {
                ["type"] = type.FullName,
                ["count"] = dictionary.Count,
                ["items"] = items,
                ["truncated"] = dictionary.Count > MaxCollectionItems
            };
        }

        if (value is IEnumerable enumerable && value is not UnityEngine.Object)
        {
            var items = new JArray();
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= MaxCollectionItems)
                {
                    break;
                }
                items.Add(DescribeValue(item, depth - 1, includeProperties, visited));
            }
            return new JObject
            {
                ["type"] = type.FullName,
                ["items"] = items,
                ["truncated"] = count > MaxCollectionItems
            };
        }

        if (depth <= 0 || value is UnityEngine.Object)
        {
            return _registry.Reference(value);
        }

        return InspectObject(value, depth, includeProperties, visited);
    }

    private static IEnumerable<FieldInfo> GetFields(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                yield return field;
            }
        }
    }

    private static bool IsNumber(Type type)
    {
        return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
               type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    }

    private static string GetVisibility(FieldInfo field)
    {
        if (field.IsPublic) return "public";
        if (field.IsFamily) return "protected";
        if (field.IsAssembly) return "internal";
        return "private";
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
