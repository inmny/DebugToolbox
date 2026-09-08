# DebugToolbox

DebugToolbox runs unrestricted C# debugging scripts inside WorldBox and exposes the runtime through a local CLI and MCP server.

## Script execution

Start WorldBox with the mod enabled. The runtime writes its current endpoint and token to `%TEMP%\DebugToolbox\session.json`.

Run a script file:

```powershell
dotnet run --project Client/DebugToolbox.Client.csproj -- run script.csx
```

Run a short script directly:

```powershell
dotnet run --project Client/DebugToolbox.Client.csproj -- eval "return Mods.Assemblies();"
```

Scripts execute on Unity's main thread and can directly access all loaded WorldBox, Unity, NML, Harmony and mod types. The generated script base class also exposes `Game`, `Objects`, `Inspect`, `Trace`, `Errors`, `Log`, `Screen` and `Mods`.

```csharp
var objects = Objects.FindUnity<GameObject>()
    .Take(10)
    .Select(item => Inspect.Object(item, depth: 1))
    .ToArray();

await Game.NextFrame();
return objects;
```

Trace a method for the duration of a script:

```csharp
var method = AccessTools.Method(typeof(SomeModType), "UpdateState");
using var trace = Trace.Attach(method, new MethodTraceOptions
{
    IncludeArguments = true,
    IncludeResult = true
});

// Perform game or mod operations here.
await Game.NextFrame();

return trace.Events;
```

Non-JSON game objects are returned as handles. Inspect them later with:

```powershell
dotnet run --project Client/DebugToolbox.Client.csproj -- inspect obj:1 2
```

## MCP

Use the client as a stdio MCP server:

```powershell
dotnet run --project Client/DebugToolbox.Client.csproj -- mcp
```

It exposes these tools:

- `run_csharp`
- `read_debug_events`
- `inspect_object`
- `get_object_field`
- `set_object_field`
- `invoke_object_method`
- `release_object`
- `start_method_trace`
- `read_method_trace`
- `stop_method_trace`
- `runtime_stats`
- `debug_session`

Object field access, method invocation, event reads and method tracing use direct RPC calls and do not compile script assemblies. Use `run_csharp` for multi-step logic that cannot be expressed by those tools.

Runtime memory statistics are also available from the CLI:

```powershell
dotnet run --project Client/DebugToolbox.Client.csproj -- stats
```

Runtime and compilation exceptions include Portable PDB source locations and nearby source when the original source path is available. Script source and PDB files are stored under `%TEMP%\DebugToolbox\symbols\<process-id>`; only the latest 128 script symbol sets are indexed.

Mono cannot unload individual script assemblies. `runtime_stats` reports the loaded script count, process memory, retained symbols, object handles and active traces. It recommends restarting the game after 256 loaded script assemblies. Dead weak object handles are cleaned every 128 registrations and whenever runtime statistics are requested.

Unrestricted scripts share WorldBox's Mono process. A blocking loop cannot be interrupted safely; restart WorldBox to recover from a script that blocks the main thread.
