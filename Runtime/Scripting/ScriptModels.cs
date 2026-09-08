using System.Collections.Generic;
using DebugToolbox.Runtime.Diagnostics;
using DebugToolbox.Runtime.Events;
using Newtonsoft.Json.Linq;

namespace DebugToolbox.Runtime.Scripting;

public sealed class ScriptDiagnostic
{
    public string Id { get; set; }
    public string Severity { get; set; }
    public string Message { get; set; }
    public string File { get; set; }
    public int? Line { get; set; }
    public int? Column { get; set; }
    public string Context { get; set; }
}

public sealed class ScriptRunResult
{
    public string ScriptId { get; set; }
    public bool Success { get; set; }
    public double DurationMs { get; set; }
    public JToken Result { get; set; }
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; set; }
    public ExceptionReport Error { get; set; }
    public IReadOnlyList<DebugEventRecord> Events { get; set; }
}

internal sealed class CompiledScript
{
    public string ScriptId { get; set; }
    public string AssemblyName { get; set; }
    public string EntryType { get; set; }
    public string DocumentName { get; set; }
    public string Source { get; set; }
    public byte[] Assembly { get; set; }
    public byte[] Pdb { get; set; }
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; set; }
}

internal sealed class ScriptCompilation
{
    public CompiledScript Script { get; set; }
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; set; }
    public bool Success { get; set; }
    public string GeneratedSource { get; set; }
}
