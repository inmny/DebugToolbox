using System;
using System.Diagnostics;
using System.Linq;
using DebugToolbox.Runtime.Diagnostics;
using DebugToolbox.Runtime.Events;
using DebugToolbox.Runtime.Inspection;
using DebugToolbox.Runtime.Scripting;
using DebugToolbox.Runtime.Tracing;
using DebugToolbox.Runtime.Transport;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DebugToolbox.Runtime;

internal sealed class DebugRuntime : IDisposable
{
    private readonly long _initialWorkingSet;

    private DebugRuntime()
    {
        Dispatcher = new MainThreadDispatcher();
        Events = new DebugEventHub();
        Objects = new ObjectRegistry();
        Inspector = new ObjectInspector(Objects);
        ObjectOperations = new ObjectOperations(Objects, Inspector);
        Sources = new SourceResolver();
        Traces = new MethodTraceService(Inspector, Objects, Events);
        TraceSessions = new TraceRegistry(Traces);
        var compiler = new ScriptCompiler();
        var formatter = new ScriptResultFormatter(Objects);
        Scripts = new ScriptExecutor(Dispatcher, compiler, formatter, Sources);
        Server = new ScriptRpcServer(Scripts, Dispatcher);
        _initialWorkingSet = Process.GetCurrentProcess().WorkingSet64;
    }

    public static DebugRuntime Instance { get; private set; }
    public static int CurrentFrame { get; private set; }

    public MainThreadDispatcher Dispatcher { get; }
    public DebugEventHub Events { get; }
    public ObjectRegistry Objects { get; }
    public ObjectInspector Inspector { get; }
    public ObjectOperations ObjectOperations { get; }
    public SourceResolver Sources { get; }
    public MethodTraceService Traces { get; }
    public TraceRegistry TraceSessions { get; }
    public ScriptExecutor Scripts { get; }
    public ScriptRpcServer Server { get; }

    public static DebugRuntime Start()
    {
        Instance = new DebugRuntime();
        Instance.Server.Start();
        Instance.Events.Publish("runtime", "info", $"Script RPC listening on 127.0.0.1:{Instance.Server.Port}");
        return Instance;
    }

    public void Update()
    {
        CurrentFrame = Time.frameCount;
        Dispatcher.Update();
    }

    public JObject GetStats()
    {
        var removedHandles = Objects.Cleanup();
        var objectStats = Objects.GetStats();
        var scriptStats = Scripts.GetStats();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var generatedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Count(item => item.GetName().Name.StartsWith("DebugToolbox.Script.", StringComparison.Ordinal));

        return new JObject
        {
            ["frame"] = CurrentFrame,
            ["managedMemoryBytes"] = GC.GetTotalMemory(false),
            ["workingSetBytes"] = process.WorkingSet64,
            ["workingSetGrowthSinceRuntimeStartBytes"] = process.WorkingSet64 - _initialWorkingSet,
            ["loadedAssemblies"] = AppDomain.CurrentDomain.GetAssemblies().Length,
            ["loadedScriptAssemblies"] = generatedAssemblies,
            ["scriptExecution"] = JObject.FromObject(scriptStats),
            ["retainedScriptSymbols"] = Sources.RetainedScriptSymbols,
            ["objectHandles"] = JObject.FromObject(objectStats),
            ["handlesRemovedByThisCheck"] = removedHandles,
            ["activeMethodTraces"] = TraceSessions.ActiveCount,
            ["assembliesCanUnload"] = false,
            ["restartRecommended"] = generatedAssemblies >= 256,
            ["recommendedScriptAssemblyLimit"] = 256
        };
    }

    public void Dispose()
    {
        Server.Dispose();
        TraceSessions.Dispose();
        Sources.Dispose();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}
