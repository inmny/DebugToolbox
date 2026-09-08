using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DebugToolbox.Runtime.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DebugToolbox.Runtime.Inspection;

internal sealed class ObjectOperations
{
    private readonly ObjectRegistry _registry;
    private readonly ObjectInspector _inspector;

    public ObjectOperations(ObjectRegistry registry, ObjectInspector inspector)
    {
        _registry = registry;
        _inspector = inspector;
    }

    public JObject GetField(string handle, string fieldName, int depth)
    {
        var target = _registry.Resolve(handle);
        var field = RuntimeReflection.FindField(target.GetType(), fieldName);
        var value = field.GetValue(field.IsStatic ? null : target);
        return new JObject
        {
            ["field"] = field.Name,
            ["fieldType"] = field.FieldType.FullName,
            ["value"] = _inspector.DescribeValue(value, depth)
        };
    }

    public JObject SetField(string handle, string fieldName, JToken value, int depth)
    {
        var target = _registry.Resolve(handle);
        var field = RuntimeReflection.FindField(target.GetType(), fieldName);
        var converted = ConvertValue(value, field.FieldType);
        field.SetValue(field.IsStatic ? null : target, converted);
        return GetField(handle, fieldName, depth);
    }

    public JObject Invoke(string handle, string methodName, JArray arguments,
        IReadOnlyList<string> parameterTypeNames, int depth)
    {
        var target = _registry.Resolve(handle);
        var candidates = RuntimeReflection.FindMethods(target.GetType(), methodName)
            .Where(method => method.GetParameters().Length == arguments.Count)
            .ToArray();

        MethodInfo method;
        object[] converted;
        if (parameterTypeNames != null)
        {
            method = RuntimeReflection.FindMethod(target.GetType(), methodName, parameterTypeNames);
            if (method.GetParameters().Length != arguments.Count)
            {
                throw new TargetParameterCountException($"Expected {method.GetParameters().Length} arguments, received {arguments.Count}");
            }
            converted = ConvertArguments(arguments, method.GetParameters());
        }
        else
        {
            var matches = new List<(MethodInfo Method, object[] Arguments)>();
            foreach (var candidate in candidates)
            {
                try
                {
                    matches.Add((candidate, ConvertArguments(arguments, candidate.GetParameters())));
                }
                catch (Exception exception) when (
                    exception is JsonException || exception is ArgumentException ||
                    exception is FormatException || exception is InvalidCastException ||
                    exception is OverflowException)
                {
                    // This overload cannot accept the supplied JSON values.
                }
            }
            if (matches.Count == 0) throw new MissingMethodException(target.GetType().FullName, methodName);
            if (matches.Count > 1) throw new AmbiguousMatchException($"Method is overloaded; provide parameterTypes: {target.GetType().FullName}.{methodName}");
            method = matches[0].Method;
            converted = matches[0].Arguments;
        }

        var result = method.Invoke(method.IsStatic ? null : target, converted);
        return new JObject
        {
            ["method"] = $"{method.DeclaringType?.FullName}.{method.Name}",
            ["returnType"] = method.ReturnType.FullName,
            ["result"] = method.ReturnType == typeof(void) ? JValue.CreateNull() : _inspector.DescribeValue(result, depth)
        };
    }

    private object[] ConvertArguments(JArray arguments, ParameterInfo[] parameters)
    {
        var result = new object[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameterType = parameters[index].ParameterType;
            if (parameterType.IsByRef) parameterType = parameterType.GetElementType();
            result[index] = ConvertValue(arguments[index], parameterType);
        }
        return result;
    }

    private object ConvertValue(JToken token, Type targetType)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is JObject reference && reference.Value<string>("handle") is string handle)
        {
            return _registry.Resolve(handle);
        }
        if (targetType.IsEnum)
        {
            return token.Type == JTokenType.String
                ? Enum.Parse(targetType, token.Value<string>(), true)
                : Enum.ToObject(targetType, token.ToObject(Enum.GetUnderlyingType(targetType)));
        }
        return token.ToObject(targetType);
    }
}
