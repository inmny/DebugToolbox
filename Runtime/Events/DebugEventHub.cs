using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace DebugToolbox.Runtime.Events;

public sealed class DebugEventRecord
{
    public long Sequence { get; set; }
    public string TimestampUtc { get; set; }
    public int Frame { get; set; }
    public string Category { get; set; }
    public string Level { get; set; }
    public string ScriptId { get; set; }
    public string Message { get; set; }
    public JToken Data { get; set; }
}

internal sealed class DebugEventHub
{
    private const int Capacity = 4096;
    private readonly object _lock = new();
    private readonly Queue<DebugEventRecord> _events = new();
    private long _sequence;

    public long CurrentSequence => Interlocked.Read(ref _sequence);

    public DebugEventRecord Publish(string category, string level, string message, JToken data = null)
    {
        var record = new DebugEventRecord
        {
            Sequence = Interlocked.Increment(ref _sequence),
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            Frame = DebugRuntime.CurrentFrame,
            Category = category,
            Level = level,
            ScriptId = ScriptExecutionContext.CurrentScriptId,
            Message = message,
            Data = data
        };

        lock (_lock)
        {
            _events.Enqueue(record);
            while (_events.Count > Capacity)
            {
                _events.Dequeue();
            }
        }
        return record;
    }

    public IReadOnlyList<DebugEventRecord> ReadSince(long sequence, int limit = 512)
    {
        lock (_lock)
        {
            return _events.Where(item => item.Sequence > sequence).Take(limit).ToArray();
        }
    }
}

internal static class ScriptExecutionContext
{
    private static readonly AsyncLocal<string> ScriptId = new();

    public static string CurrentScriptId
    {
        get => ScriptId.Value;
        set => ScriptId.Value = value;
    }
}
