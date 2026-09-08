using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DebugToolbox.Runtime.Events;
using DebugToolbox.Runtime.Inspection;
using DebugToolbox.Runtime.Tracing;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DebugToolbox.Runtime.Scripting;

public interface IDebugScript
{
    Task<object> Run(ScriptContext context);
}

public abstract class DebugScriptBase : IDebugScript
{
    private ScriptContext _context;

    protected ScriptContext Context => _context;
    protected GameFacade Game => _context.Game;
    protected ObjectFacade Objects => _context.Objects;
    protected InspectorFacade Inspect => _context.Inspect;
    protected TraceFacade Trace => _context.Trace;
    protected ErrorFacade Errors => _context.Errors;
    protected ScriptLog Log => _context.Log;
    protected ScreenFacade Screen => _context.Screen;
    protected ModFacade Mods => _context.Mods;

    public Task<object> Run(ScriptContext context)
    {
        _context = context;
        return Execute();
    }

    protected abstract Task<object> Execute();
}

public sealed class ScriptContext : IDisposable
{
    private readonly List<IDisposable> _resources = new();

    internal ScriptContext(string scriptId, long eventCursor, CancellationToken cancellationToken)
    {
        ScriptId = scriptId;
        EventCursor = eventCursor;
        CancellationToken = cancellationToken;
        Game = new GameFacade(DebugRuntime.Instance.Dispatcher, cancellationToken);
        Objects = new ObjectFacade(DebugRuntime.Instance.Objects, DebugRuntime.Instance.Inspector);
        Inspect = new InspectorFacade(Objects);
        Trace = new TraceFacade(DebugRuntime.Instance.Traces, Track);
        Errors = new ErrorFacade(DebugRuntime.Instance.Events, eventCursor);
        Log = new ScriptLog(DebugRuntime.Instance.Events);
        Screen = new ScreenFacade();
        Mods = new ModFacade();
    }

    public string ScriptId { get; }
    public long EventCursor { get; }
    public CancellationToken CancellationToken { get; }
    public GameFacade Game { get; }
    public ObjectFacade Objects { get; }
    public InspectorFacade Inspect { get; }
    public TraceFacade Trace { get; }
    public ErrorFacade Errors { get; }
    public ScriptLog Log { get; }
    public ScreenFacade Screen { get; }
    public ModFacade Mods { get; }

    internal T Track<T>(T resource) where T : IDisposable
    {
        _resources.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        for (var index = _resources.Count - 1; index >= 0; index--)
        {
            _resources[index].Dispose();
        }
        _resources.Clear();
    }
}

public sealed class GameFacade
{
    private readonly MainThreadDispatcher _dispatcher;
    private readonly CancellationToken _cancellationToken;

    internal GameFacade(MainThreadDispatcher dispatcher, CancellationToken cancellationToken)
    {
        _dispatcher = dispatcher;
        _cancellationToken = cancellationToken;
    }

    public void Pause() => Time.timeScale = 0f;
    public void Resume(float timeScale = 1f) => Time.timeScale = timeScale;
    public Task NextFrame() => _dispatcher.NextFrame(_cancellationToken);

    public async Task Step(float timeScale = 1f)
    {
        Resume(timeScale);
        await NextFrame();
        Pause();
    }

    public async Task WaitUntil(Func<bool> predicate, int timeoutFrames = 600)
    {
        for (var frame = 0; frame < timeoutFrames; frame++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (predicate()) return;
            await NextFrame();
        }
        throw new TimeoutException($"Condition was not met within {timeoutFrames} frames");
    }
}

public sealed class ObjectFacade
{
    private readonly ObjectRegistry _registry;
    private readonly ObjectInspector _inspector;

    internal ObjectFacade(ObjectRegistry registry, ObjectInspector inspector)
    {
        _registry = registry;
        _inspector = inspector;
    }

    public string Handle(object value) => _registry.Register(value);
    public object Resolve(string handle) => _registry.Resolve(handle);
    public T Resolve<T>(string handle) => (T)_registry.Resolve(handle);
    public JObject Inspect(object value, int depth = 1, bool includeProperties = false) => _inspector.Inspect(value, depth, includeProperties);
    public JObject Inspect(string handle, int depth = 1, bool includeProperties = false) => Inspect(Resolve(handle), depth, includeProperties);

    public object GetField(object target, string fieldName)
    {
        return FindField(target.GetType(), fieldName).GetValue(target);
    }

    public void SetField(object target, string fieldName, object value)
    {
        FindField(target.GetType(), fieldName).SetValue(target, value);
    }

    public object Invoke(object target, string methodName, params object[] arguments)
    {
        var method = AccessTools.GetDeclaredMethods(target.GetType())
            .FirstOrDefault(item => item.Name == methodName && item.GetParameters().Length == arguments.Length);
        if (method == null) throw new MissingMethodException(target.GetType().FullName, methodName);
        return method.Invoke(target, arguments);
    }

    public UnityEngine.Object[] FindUnity(Type type) => Resources.FindObjectsOfTypeAll(type);
    public T[] FindUnity<T>() where T : UnityEngine.Object => Resources.FindObjectsOfTypeAll<T>();

    private static FieldInfo FindField(Type type, string name)
    {
        var field = AccessTools.Field(type, name);
        if (field == null) throw new MissingFieldException(type.FullName, name);
        return field;
    }
}

public sealed class InspectorFacade
{
    private readonly ObjectFacade _objects;

    internal InspectorFacade(ObjectFacade objects)
    {
        _objects = objects;
    }

    public JObject Object(object value, int depth = 1, bool includeProperties = false) => _objects.Inspect(value, depth, includeProperties);
    public JObject Object(string handle, int depth = 1, bool includeProperties = false) => _objects.Inspect(handle, depth, includeProperties);
}

public sealed class TraceFacade
{
    private readonly MethodTraceService _service;
    private readonly Func<MethodTraceScope, MethodTraceScope> _track;

    internal TraceFacade(MethodTraceService service, Func<MethodTraceScope, MethodTraceScope> track)
    {
        _service = service;
        _track = track;
    }

    public MethodTraceScope Attach(MethodBase method, MethodTraceOptions options = null)
    {
        return _track(_service.Attach(method, options));
    }

    public MethodTraceScope Method(Type type, string methodName, bool includeArguments = true,
        bool includeResult = true, Type[] argumentTypes = null)
    {
        var method = argumentTypes == null
            ? AccessTools.Method(type, methodName)
            : AccessTools.Method(type, methodName, argumentTypes);
        if (method == null) throw new MissingMethodException(type.FullName, methodName);
        return Attach(method, new MethodTraceOptions
        {
            IncludeArguments = includeArguments,
            IncludeResult = includeResult
        });
    }
}

public sealed class ErrorFacade
{
    private readonly DebugEventHub _events;
    private readonly long _start;

    internal ErrorFacade(DebugEventHub events, long start)
    {
        _events = events;
        _start = start;
    }

    public IReadOnlyList<DebugEventRecord> SinceScriptStart => _events.ReadSince(_start, 4096)
        .Where(item => item.Level == "error").ToArray();
}

public sealed class ScriptLog
{
    private readonly DebugEventHub _events;

    internal ScriptLog(DebugEventHub events)
    {
        _events = events;
    }

    public void Info(object value) => _events.Publish("script", "info", value?.ToString() ?? "null");
    public void Error(object value) => _events.Publish("script", "error", value?.ToString() ?? "null");
}

public sealed class ScreenFacade
{
    public string Capture(string fileName = null)
    {
        var directory = Path.Combine(Application.persistentDataPath, "DebugToolbox", "Captures");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png");
        ScreenCapture.CaptureScreenshot(path);
        return path;
    }
}

public sealed class ModFacade
{
    public object[] Assemblies()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(item => !item.IsDynamic)
            .OrderBy(item => item.GetName().Name)
            .Select(item => (object)new
            {
                Name = item.GetName().Name,
                Version = item.GetName().Version?.ToString(),
                Location = item.Location
            })
            .ToArray();
    }
}
