using System;
using DebugToolbox.Runtime.Diagnostics;
using DebugToolbox.Runtime.Events;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DebugToolbox;

public sealed class FullExceptionTracker : IDisposable
{
    private readonly DebugEventHub _events;
    private readonly SourceResolver _sources;

    internal FullExceptionTracker(DebugEventHub events, SourceResolver sources)
    {
        _events = events;
        _sources = sources;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        Application.logMessageReceivedThreaded += OnUnityLog;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            var report = _sources.Report(exception);
            _events.Publish("exception", "error", report.Message, JObject.FromObject(report));
        }
    }

    private void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        _events.Publish("unity", "error", condition, new JObject
        {
            ["logType"] = type.ToString(),
            ["stackTrace"] = stackTrace,
            ["frames"] = JArray.FromObject(_sources.ResolveLogStack(stackTrace))
        });
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        Application.logMessageReceivedThreaded -= OnUnityLog;
    }
}
