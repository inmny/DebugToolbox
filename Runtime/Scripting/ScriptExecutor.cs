using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DebugToolbox.Runtime.Diagnostics;
using DebugToolbox.Runtime.Events;
using Newtonsoft.Json.Linq;

namespace DebugToolbox.Runtime.Scripting;

internal sealed class ScriptExecutor
{
    public sealed class ExecutorStats
    {
        public long CompilationAttempts { get; set; }
        public long CompilationFailures { get; set; }
        public long CompiledScripts { get; set; }
        public long LoadedScriptAssemblies { get; set; }
        public long CompletedScripts { get; set; }
        public long FailedScripts { get; set; }
        public long ActiveScripts { get; set; }
        public long EmittedAssemblyAndPdbBytes { get; set; }
    }

    private readonly MainThreadDispatcher _dispatcher;
    private readonly ScriptCompiler _compiler;
    private readonly ScriptResultFormatter _formatter;
    private readonly SourceResolver _sources;
    private long _compilationAttempts;
    private long _compilationFailures;
    private long _compiledScripts;
    private long _loadedScriptAssemblies;
    private long _completedScripts;
    private long _failedScripts;
    private long _activeScripts;
    private long _emittedBytes;

    public ScriptExecutor(MainThreadDispatcher dispatcher, ScriptCompiler compiler,
        ScriptResultFormatter formatter, SourceResolver sources)
    {
        _dispatcher = dispatcher;
        _compiler = compiler;
        _formatter = formatter;
        _sources = sources;
    }

    public Task<ScriptRunResult> RunAsync(string source, int timeoutMs = 30000)
    {
        Interlocked.Increment(ref _compilationAttempts);
        var scriptId = Guid.NewGuid().ToString("N");
        var compilation = _compiler.Compile(source, scriptId);
        if (!compilation.Success)
        {
            Interlocked.Increment(ref _compilationFailures);
            return Task.FromResult(new ScriptRunResult
            {
                ScriptId = scriptId,
                Success = false,
                Diagnostics = compilation.Diagnostics,
                Events = Array.Empty<Events.DebugEventRecord>()
            });
        }

        Interlocked.Increment(ref _compiledScripts);
        Interlocked.Add(ref _emittedBytes,
            compilation.Script.Assembly.LongLength + compilation.Script.Pdb.LongLength);

        var completion = new TaskCompletionSource<ScriptRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() => Start(compilation.Script, timeoutMs, completion));
        return completion.Task;
    }

    public ExecutorStats GetStats()
    {
        return new ExecutorStats
        {
            CompilationAttempts = Interlocked.Read(ref _compilationAttempts),
            CompilationFailures = Interlocked.Read(ref _compilationFailures),
            CompiledScripts = Interlocked.Read(ref _compiledScripts),
            LoadedScriptAssemblies = Interlocked.Read(ref _loadedScriptAssemblies),
            CompletedScripts = Interlocked.Read(ref _completedScripts),
            FailedScripts = Interlocked.Read(ref _failedScripts),
            ActiveScripts = Interlocked.Read(ref _activeScripts),
            EmittedAssemblyAndPdbBytes = Interlocked.Read(ref _emittedBytes)
        };
    }

    private void Start(CompiledScript compiled, int timeoutMs, TaskCompletionSource<ScriptRunResult> completion)
    {
        Interlocked.Increment(ref _activeScripts);
        var stopwatch = Stopwatch.StartNew();
        var eventCursor = DebugRuntime.Instance.Events.CurrentSequence;
        var cancellation = new CancellationTokenSource();
        if (timeoutMs > 0) cancellation.CancelAfter(timeoutMs);
        var context = new ScriptContext(compiled.ScriptId, eventCursor, cancellation.Token);

        try
        {
            var assembly = Assembly.Load(compiled.Assembly);
            Interlocked.Increment(ref _loadedScriptAssemblies);
            _sources.Register(assembly, compiled.Pdb, compiled.Source, compiled.DocumentName);

            var type = assembly.GetType(compiled.EntryType, true);
            var script = (IDebugScript)Activator.CreateInstance(type);
            ScriptExecutionContext.CurrentScriptId = compiled.ScriptId;
            Task<object> task;
            try
            {
                task = script.Run(context);
            }
            finally
            {
                ScriptExecutionContext.CurrentScriptId = null;
            }

            task.ContinueWith(finished => _dispatcher.Post(() => Finish(
                compiled, finished, context, cancellation, stopwatch, eventCursor, completion)),
                TaskScheduler.Default);
        }
        catch (Exception exception)
        {
            CompleteFailure(compiled, exception, context, cancellation, stopwatch, eventCursor, completion);
        }
    }

    private void Finish(CompiledScript compiled, Task<object> task, ScriptContext context,
        CancellationTokenSource cancellation, Stopwatch stopwatch, long eventCursor,
        TaskCompletionSource<ScriptRunResult> completion)
    {
        ScriptExecutionContext.CurrentScriptId = compiled.ScriptId;
        try
        {
            if (task.IsCanceled)
            {
                throw new OperationCanceledException("Script execution was cancelled");
            }
            if (task.IsFaulted)
            {
                throw task.Exception;
            }

            var result = new ScriptRunResult
            {
                ScriptId = compiled.ScriptId,
                Success = true,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                Result = _formatter.Format(task.Result),
                Diagnostics = compiled.Diagnostics,
                Events = DebugRuntime.Instance.Events.ReadSince(eventCursor, 4096)
            };
            DebugRuntime.Instance.Events.Publish("script", "info", $"Script {compiled.ScriptId} completed");
            Interlocked.Increment(ref _completedScripts);
            completion.SetResult(result);
        }
        catch (Exception exception)
        {
            CompleteFailure(compiled, exception, context, cancellation, stopwatch, eventCursor, completion);
            return;
        }
        finally
        {
            ScriptExecutionContext.CurrentScriptId = null;
        }

        context.Dispose();
        cancellation.Dispose();
        Interlocked.Decrement(ref _activeScripts);
    }

    private void CompleteFailure(CompiledScript compiled, Exception exception, ScriptContext context,
        CancellationTokenSource cancellation, Stopwatch stopwatch, long eventCursor,
        TaskCompletionSource<ScriptRunResult> completion)
    {
        Interlocked.Increment(ref _failedScripts);
        var report = _sources.Report(exception);
        DebugRuntime.Instance.Events.Publish("script", "error", report.Message, JObject.FromObject(report));
        completion.TrySetResult(new ScriptRunResult
        {
            ScriptId = compiled.ScriptId,
            Success = false,
            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
            Diagnostics = compiled.Diagnostics,
            Error = report,
            Events = DebugRuntime.Instance.Events.ReadSince(eventCursor, 4096)
        });
        context.Dispose();
        cancellation.Dispose();
        Interlocked.Decrement(ref _activeScripts);
    }
}
