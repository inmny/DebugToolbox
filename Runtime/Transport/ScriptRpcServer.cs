using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DebugToolbox.Runtime.Scripting;
using DebugToolbox.Runtime.Tracing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DebugToolbox.Runtime.Transport;

internal sealed class ScriptRpcServer : IDisposable
{
    private readonly ScriptExecutor _scripts;
    private readonly MainThreadDispatcher _dispatcher;
    private readonly string _token = Guid.NewGuid().ToString("N");
    private TcpListener _listener;
    private Thread _listenerThread;
    private volatile bool _running;

    public ScriptRpcServer(ScriptExecutor scripts, MainThreadDispatcher dispatcher)
    {
        _scripts = scripts;
        _dispatcher = dispatcher;
    }

    public int Port { get; private set; }
    public string SessionFile { get; private set; }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _running = true;

        var sessionDirectory = Path.Combine(Path.GetTempPath(), "DebugToolbox");
        Directory.CreateDirectory(sessionDirectory);
        SessionFile = Path.Combine(sessionDirectory, "session.json");
        File.WriteAllText(SessionFile, new JObject
        {
            ["protocol"] = "debugtoolbox-ndjson-v1",
            ["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id,
            ["port"] = Port,
            ["token"] = _token,
            ["startedUtc"] = DateTime.UtcNow.ToString("O")
        }.ToString(Formatting.Indented));

        _listenerThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "DebugToolbox Script RPC"
        };
        _listenerThread.Start();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
            catch (SocketException)
            {
                if (_running) throw;
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                JObject request = null;
                try
                {
                    request = JObject.Parse(line);
                    var response = Dispatch(request).GetAwaiter().GetResult();
                    writer.WriteLine(response.ToString(Formatting.None));
                }
                catch (Exception exception)
                {
                    var report = DebugRuntime.Instance.Sources.Report(exception);
                    writer.WriteLine(new JObject
                    {
                        ["id"] = request?["id"],
                        ["error"] = JObject.FromObject(report)
                    }.ToString(Formatting.None));
                }
            }
        }
    }

    private async Task<JObject> Dispatch(JObject request)
    {
        var id = request["id"];
        var method = request.Value<string>("method");
        if (method != "hello" && request.Value<string>("token") != _token)
        {
            throw new UnauthorizedAccessException("Invalid DebugToolbox session token");
        }

        var parameters = request["params"] as JObject ?? new JObject();
        JToken result;
        switch (method)
        {
            case "hello":
                result = new JObject
                {
                    ["protocol"] = "debugtoolbox-ndjson-v1",
                    ["capabilities"] = new JArray(
                        "script.run", "events.read", "objects.inspect", "objects.get_field",
                        "objects.set_field", "objects.invoke", "objects.release", "trace.start",
                        "trace.read", "trace.stop", "runtime.stats"),
                    ["frame"] = DebugRuntime.CurrentFrame
                };
                break;
            case "script.run":
                var source = parameters.Value<string>("source");
                if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("script.run requires params.source");
                var timeoutMs = parameters.Value<int?>("timeoutMs") ?? 30000;
                result = JObject.FromObject(await _scripts.RunAsync(source, timeoutMs));
                break;
            case "events.read":
                var since = parameters.Value<long?>("since") ?? 0;
                var limit = parameters.Value<int?>("limit") ?? 512;
                result = JArray.FromObject(DebugRuntime.Instance.Events.ReadSince(since, limit));
                break;
            case "objects.inspect":
                var handle = parameters.Value<string>("handle");
                var depth = parameters.Value<int?>("depth") ?? 1;
                var includeProperties = parameters.Value<bool?>("includeProperties") ?? false;
                result = await _dispatcher.InvokeAsync(() => DebugRuntime.Instance.Inspector.Inspect(
                    DebugRuntime.Instance.Objects.Resolve(handle), depth, includeProperties));
                break;
            case "objects.get_field":
                result = await _dispatcher.InvokeAsync(() => DebugRuntime.Instance.ObjectOperations.GetField(
                    parameters.Value<string>("handle"),
                    parameters.Value<string>("field"),
                    parameters.Value<int?>("depth") ?? 0));
                break;
            case "objects.set_field":
                result = await _dispatcher.InvokeAsync(() => DebugRuntime.Instance.ObjectOperations.SetField(
                    parameters.Value<string>("handle"),
                    parameters.Value<string>("field"),
                    parameters["value"],
                    parameters.Value<int?>("depth") ?? 0));
                break;
            case "objects.invoke":
                result = await _dispatcher.InvokeAsync(() => DebugRuntime.Instance.ObjectOperations.Invoke(
                    parameters.Value<string>("handle"),
                    parameters.Value<string>("method"),
                    parameters["arguments"] as JArray ?? new JArray(),
                    ReadStringList(parameters["parameterTypes"]),
                    parameters.Value<int?>("depth") ?? 0));
                break;
            case "objects.release":
                var releasedHandle = parameters.Value<string>("handle");
                result = new JObject
                {
                    ["handle"] = releasedHandle,
                    ["released"] = DebugRuntime.Instance.Objects.Release(releasedHandle)
                };
                break;
            case "trace.start":
                var traceOptions = new MethodTraceOptions
                {
                    IncludeArguments = parameters.Value<bool?>("includeArguments") ?? true,
                    IncludeResult = parameters.Value<bool?>("includeResult") ?? true,
                    IncludeStackTrace = parameters.Value<bool?>("includeStackTrace") ?? false,
                    MaxEvents = parameters.Value<int?>("maxEvents") ?? 512
                };
                result = await _dispatcher.InvokeAsync(() => DebugRuntime.Instance.TraceSessions.Start(
                    parameters.Value<string>("type"),
                    parameters.Value<string>("method"),
                    ReadStringList(parameters["parameterTypes"]),
                    traceOptions));
                break;
            case "trace.read":
                result = DebugRuntime.Instance.TraceSessions.Read(parameters.Value<string>("traceId"));
                break;
            case "trace.stop":
                result = await _dispatcher.InvokeAsync(() =>
                    DebugRuntime.Instance.TraceSessions.Stop(parameters.Value<string>("traceId")));
                break;
            case "runtime.stats":
                result = DebugRuntime.Instance.GetStats();
                break;
            default:
                throw new MissingMethodException($"Unknown RPC method: {method}");
        }

        return new JObject { ["id"] = id, ["result"] = result };
    }

    private static IReadOnlyList<string> ReadStringList(JToken token)
    {
        return token is JArray array ? array.Values<string>().ToArray() : null;
    }

    public void Dispose()
    {
        _running = false;
        _listener?.Stop();
        if (!string.IsNullOrEmpty(SessionFile) && File.Exists(SessionFile))
        {
            File.Delete(SessionFile);
        }
    }
}
