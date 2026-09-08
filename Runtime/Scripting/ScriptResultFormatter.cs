using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DebugToolbox.Runtime.Inspection;
using Newtonsoft.Json.Linq;

namespace DebugToolbox.Runtime.Scripting;

internal sealed class ScriptResultFormatter
{
    private const int MaxItems = 256;
    private readonly ObjectRegistry _registry;

    public ScriptResultFormatter(ObjectRegistry registry)
    {
        _registry = registry;
    }

    public JToken Format(object value)
    {
        return Format(value, 4);
    }

    private JToken Format(object value, int depth)
    {
        if (value == null) return JValue.CreateNull();
        if (value is JToken token) return token;

        var type = value.GetType();
        if (type.IsEnum || value is string || value is char || value is bool || value is DateTime ||
            value is Guid || IsNumber(type))
        {
            return JToken.FromObject(value);
        }

        if (depth < 0)
        {
            return _registry.Reference(value);
        }

        if (value is IDictionary dictionary)
        {
            var result = new JObject();
            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= MaxItems) break;
                result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = Format(entry.Value, depth - 1);
            }
            return result;
        }

        if (value is IEnumerable enumerable)
        {
            var result = new JArray();
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= MaxItems) break;
                result.Add(Format(item, depth - 1));
            }
            return result;
        }

        if (depth > 0 && IsStructuredResult(type))
        {
            var result = new JObject();
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(item => item.CanRead && item.GetIndexParameters().Length == 0))
            {
                result[property.Name] = Format(property.GetValue(value), depth - 1);
            }
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                result[field.Name] = Format(field.GetValue(value), depth - 1);
            }
            return result;
        }

        return _registry.Reference(value);
    }

    private static bool IsStructuredResult(Type type)
    {
        return type.Assembly == typeof(ScriptResultFormatter).Assembly ||
               type.GetCustomAttributes(typeof(CompilerGeneratedAttribute), false).Length != 0 ||
               type.Assembly.GetName().Name.StartsWith("DebugToolbox.Script.", StringComparison.Ordinal);
    }

    private static bool IsNumber(Type type)
    {
        return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
               type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    }
}
