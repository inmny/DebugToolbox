using System;
using System.Collections.Generic;
using DebugToolbox.Runtime.Reflection;
using Newtonsoft.Json.Linq;

namespace DebugToolbox.Runtime.Tracing;

internal sealed class TraceRegistry : IDisposable
{
    private readonly object _lock = new();
    private readonly MethodTraceService _traces;
    private readonly Dictionary<string, MethodTraceScope> _scopes = new();

    public TraceRegistry(MethodTraceService traces)
    {
        _traces = traces;
    }

    public int ActiveCount
    {
        get
        {
            lock (_lock) return _scopes.Count;
        }
    }

    public JObject Start(string typeName, string methodName, IReadOnlyList<string> parameterTypes,
        MethodTraceOptions options)
    {
        var type = RuntimeReflection.ResolveType(typeName);
        var method = RuntimeReflection.FindMethod(type, methodName, parameterTypes);
        var scope = _traces.Attach(method, options);
        lock (_lock)
        {
            _scopes.Add(scope.Id, scope);
        }
        return new JObject
        {
            ["traceId"] = scope.Id,
            ["method"] = $"{method.DeclaringType?.FullName}.{method.Name}",
            ["parameterTypes"] = JArray.FromObject(Array.ConvertAll(method.GetParameters(), item => item.ParameterType.FullName))
        };
    }

    public JObject Read(string traceId)
    {
        MethodTraceScope scope;
        lock (_lock)
        {
            if (!_scopes.TryGetValue(traceId, out scope))
            {
                throw new KeyNotFoundException($"Unknown trace: {traceId}");
            }
        }
        return new JObject
        {
            ["traceId"] = traceId,
            ["events"] = JArray.FromObject(scope.Events)
        };
    }

    public JObject Stop(string traceId)
    {
        MethodTraceScope scope;
        lock (_lock)
        {
            if (!_scopes.TryGetValue(traceId, out scope))
            {
                throw new KeyNotFoundException($"Unknown trace: {traceId}");
            }
            _scopes.Remove(traceId);
        }
        var events = scope.Events;
        scope.Dispose();
        return new JObject
        {
            ["traceId"] = traceId,
            ["events"] = JArray.FromObject(events)
        };
    }

    public void Dispose()
    {
        MethodTraceScope[] scopes;
        lock (_lock)
        {
            scopes = new MethodTraceScope[_scopes.Count];
            _scopes.Values.CopyTo(scopes, 0);
            _scopes.Clear();
        }
        foreach (var scope in scopes)
        {
            scope.Dispose();
        }
    }
}
