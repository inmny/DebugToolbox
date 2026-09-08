using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using DebugToolbox.Runtime.Events;
using DebugToolbox.Runtime.Inspection;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace DebugToolbox.Runtime.Tracing;

public sealed class MethodTraceOptions
{
    public bool IncludeArguments { get; set; } = true;
    public bool IncludeResult { get; set; } = true;
    public bool IncludeStackTrace { get; set; }
    public int MaxEvents { get; set; } = 512;
}

public sealed class MethodTraceEvent
{
    public long CallId { get; set; }
    public long? ParentCallId { get; set; }
    public string TraceId { get; set; }
    public string Method { get; set; }
    public string TimestampUtc { get; set; }
    public int Frame { get; set; }
    public int ThreadId { get; set; }
    public double DurationMs { get; set; }
    public JToken Instance { get; set; }
    public JObject Arguments { get; set; }
    public JToken Result { get; set; }
    public string ExceptionType { get; set; }
    public string ExceptionMessage { get; set; }
    public string StackTrace { get; set; }
}

public sealed class MethodTraceScope : IDisposable
{
    private readonly MethodTraceService _service;
    private readonly TraceSubscription _subscription;
    private bool _disposed;

    internal MethodTraceScope(MethodTraceService service, TraceSubscription subscription)
    {
        _service = service;
        _subscription = subscription;
    }

    public string Id => _subscription.Id;
    public MethodBase Method => _subscription.Method;
    public IReadOnlyList<MethodTraceEvent> Events => _subscription.Snapshot();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.Detach(_subscription);
    }
}

internal sealed class TraceSubscription
{
    private readonly object _lock = new();
    private readonly Queue<MethodTraceEvent> _events = new();

    public string Id { get; set; }
    public MethodBase Method { get; set; }
    public MethodTraceOptions Options { get; set; }

    public void Add(MethodTraceEvent record)
    {
        lock (_lock)
        {
            _events.Enqueue(record);
            while (_events.Count > Options.MaxEvents)
            {
                _events.Dequeue();
            }
        }
    }

    public IReadOnlyList<MethodTraceEvent> Snapshot()
    {
        lock (_lock)
        {
            return _events.ToArray();
        }
    }
}

internal sealed class MethodTraceService
{
    private sealed class TargetRegistration
    {
        public MethodBase Method { get; set; }
        public MethodInfo Postfix { get; set; }
        public List<TraceSubscription> Subscriptions { get; } = new();
    }

    internal sealed class Invocation
    {
        public long CallId { get; set; }
        public long? ParentCallId { get; set; }
        public long StartedAt { get; set; }
        public MethodBase Method { get; set; }
        public JToken Instance { get; set; }
        public JObject Arguments { get; set; }
        public string StackTrace { get; set; }
        public TraceSubscription[] Subscriptions { get; set; }
    }

    private const string HarmonyId = "inmny.debugtoolbox.method-tracing";
    private static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(MethodTracePatchBridge), nameof(MethodTracePatchBridge.Prefix));
    private static readonly MethodInfo VoidPostfixMethod = AccessTools.Method(typeof(MethodTracePatchBridge), nameof(MethodTracePatchBridge.Postfix));
    private static readonly MethodInfo ResultPostfixMethod = AccessTools.Method(typeof(MethodTracePatchBridge), nameof(MethodTracePatchBridge.PostfixWithResult));
    private static readonly MethodInfo FinalizerMethod = AccessTools.Method(typeof(MethodTracePatchBridge), nameof(MethodTracePatchBridge.Finalizer));

    private readonly object _lock = new();
    private readonly Dictionary<MethodBase, TargetRegistration> _targets = new();
    private readonly Harmony _harmony = new(HarmonyId);
    private readonly ObjectInspector _inspector;
    private readonly ObjectRegistry _registry;
    private readonly DebugEventHub _events;
    private long _nextCallId;

    public MethodTraceService(ObjectInspector inspector, ObjectRegistry registry, DebugEventHub events)
    {
        _inspector = inspector;
        _registry = registry;
        _events = events;
        MethodTracePatchBridge.Service = this;
    }

    public MethodTraceScope Attach(MethodBase method, MethodTraceOptions options = null)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        options ??= new MethodTraceOptions();
        if (options.MaxEvents < 1) throw new ArgumentOutOfRangeException(nameof(options.MaxEvents));

        var subscription = new TraceSubscription
        {
            Id = "trace:" + Guid.NewGuid().ToString("N"),
            Method = method,
            Options = options
        };

        lock (_lock)
        {
            if (!_targets.TryGetValue(method, out var target))
            {
                target = Patch(method);
                _targets.Add(method, target);
            }
            target.Subscriptions.Add(subscription);
        }
        return new MethodTraceScope(this, subscription);
    }

    internal void Detach(TraceSubscription subscription)
    {
        lock (_lock)
        {
            if (!_targets.TryGetValue(subscription.Method, out var target)) return;
            target.Subscriptions.Remove(subscription);
            if (target.Subscriptions.Count != 0) return;

            _harmony.Unpatch(target.Method, PrefixMethod);
            _harmony.Unpatch(target.Method, target.Postfix);
            _harmony.Unpatch(target.Method, FinalizerMethod);
            _targets.Remove(target.Method);
        }
    }

    internal Invocation Begin(MethodBase method, object instance, object[] arguments, long? parentCallId)
    {
        TraceSubscription[] subscriptions;
        lock (_lock)
        {
            if (!_targets.TryGetValue(method, out var target) || target.Subscriptions.Count == 0)
            {
                return null;
            }
            subscriptions = target.Subscriptions.ToArray();
        }

        var includeArguments = subscriptions.Any(item => item.Options.IncludeArguments);
        var includeStack = subscriptions.Any(item => item.Options.IncludeStackTrace);
        return new Invocation
        {
            CallId = Interlocked.Increment(ref _nextCallId),
            ParentCallId = parentCallId,
            StartedAt = Stopwatch.GetTimestamp(),
            Method = method,
            Instance = instance == null ? null : _registry.Reference(instance),
            Arguments = includeArguments ? FormatArguments(method, arguments) : null,
            StackTrace = includeStack ? Environment.StackTrace : null,
            Subscriptions = subscriptions
        };
    }

    internal void End(Invocation invocation, object result, bool hasResult, Exception exception)
    {
        if (invocation == null) return;
        var elapsed = (Stopwatch.GetTimestamp() - invocation.StartedAt) * 1000d / Stopwatch.Frequency;

        foreach (var subscription in invocation.Subscriptions)
        {
            var record = new MethodTraceEvent
            {
                CallId = invocation.CallId,
                ParentCallId = invocation.ParentCallId,
                TraceId = subscription.Id,
                Method = FormatMethod(invocation.Method),
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                Frame = DebugRuntime.CurrentFrame,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                DurationMs = elapsed,
                Instance = invocation.Instance,
                Arguments = subscription.Options.IncludeArguments ? invocation.Arguments : null,
                Result = subscription.Options.IncludeResult && hasResult ? _inspector.DescribeValue(result, -1) : null,
                ExceptionType = exception?.GetType().FullName,
                ExceptionMessage = exception?.Message,
                StackTrace = subscription.Options.IncludeStackTrace ? invocation.StackTrace : null
            };
            subscription.Add(record);
            _events.Publish("trace", exception == null ? "info" : "error", record.Method, JObject.FromObject(record));
        }
    }

    private TargetRegistration Patch(MethodBase method)
    {
        MethodInfo postfix = VoidPostfixMethod;
        if (method is MethodInfo methodInfo && methodInfo.ReturnType != typeof(void) &&
            !methodInfo.ReturnType.IsByRef && !methodInfo.ReturnType.ContainsGenericParameters)
        {
            postfix = ResultPostfixMethod.MakeGenericMethod(methodInfo.ReturnType);
        }

        _harmony.Patch(method,
            prefix: new HarmonyMethod(PrefixMethod),
            postfix: new HarmonyMethod(postfix),
            finalizer: new HarmonyMethod(FinalizerMethod));

        return new TargetRegistration { Method = method, Postfix = postfix };
    }

    private JObject FormatArguments(MethodBase method, object[] arguments)
    {
        var result = new JObject();
        var parameters = method.GetParameters();
        for (var index = 0; index < arguments.Length; index++)
        {
            var name = index < parameters.Length ? parameters[index].Name : "arg" + index;
            result[name] = _inspector.DescribeValue(arguments[index], -1);
        }
        return result;
    }

    private static string FormatMethod(MethodBase method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(item => item.ParameterType.Name));
        return $"{method.DeclaringType?.FullName}.{method.Name}({parameters})";
    }
}

internal static class MethodTracePatchBridge
{
    [ThreadStatic]
    private static List<MethodTraceService.Invocation> _calls;

    public static MethodTraceService Service { get; set; }

    public static void Prefix(MethodBase __originalMethod, object __instance, object[] __args)
    {
        _calls ??= new List<MethodTraceService.Invocation>();
        var parent = _calls.Count == 0 ? (long?)null : _calls[_calls.Count - 1].CallId;
        var invocation = Service.Begin(__originalMethod, __instance, __args, parent);
        if (invocation != null)
        {
            _calls.Add(invocation);
        }
    }

    public static void Postfix(MethodBase __originalMethod)
    {
        Complete(__originalMethod, null, false, null);
    }

    public static void PostfixWithResult<TResult>(MethodBase __originalMethod, TResult __result)
    {
        Complete(__originalMethod, __result, true, null);
    }

    public static Exception Finalizer(MethodBase __originalMethod, Exception __exception)
    {
        if (__exception != null)
        {
            Complete(__originalMethod, null, false, __exception);
        }
        return __exception;
    }

    private static void Complete(MethodBase method, object result, bool hasResult, Exception exception)
    {
        if (_calls == null || _calls.Count == 0) return;
        for (var index = _calls.Count - 1; index >= 0; index--)
        {
            if (_calls[index].Method != method) continue;
            var invocation = _calls[index];
            _calls.RemoveAt(index);
            Service.End(invocation, result, hasResult, exception);
            return;
        }
    }
}
