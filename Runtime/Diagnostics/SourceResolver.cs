using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;

namespace DebugToolbox.Runtime.Diagnostics;

public sealed class SourceFrame
{
    public string Method { get; set; }
    public string Assembly { get; set; }
    public string File { get; set; }
    public int? Line { get; set; }
    public int IlOffset { get; set; }
    public string Context { get; set; }
}

public sealed class ExceptionReport
{
    public string Type { get; set; }
    public string Message { get; set; }
    public string StackTrace { get; set; }
    public IReadOnlyList<SourceFrame> Frames { get; set; }
    public ExceptionReport Cause { get; set; }
}

internal sealed class SourceResolver : IDisposable
{
    private const int MaxRetainedScriptSymbols = 128;

    private sealed class SymbolSource
    {
        public string PdbPath { get; set; }
        public string SourcePath { get; set; }
        public string DocumentName { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<Guid, SymbolSource> _sources = new();
    private readonly Dictionary<Guid, SymbolSource> _assemblySymbols = new();
    private readonly Queue<Guid> _scriptOrder = new();
    private readonly string _scriptDirectory;

    public SourceResolver()
    {
        _scriptDirectory = Path.Combine(Path.GetTempPath(), "DebugToolbox", "symbols",
            Process.GetCurrentProcess().Id.ToString());
        Directory.CreateDirectory(_scriptDirectory);
    }

    public int RetainedScriptSymbols
    {
        get
        {
            lock (_lock) return _sources.Count;
        }
    }

    public void Register(Assembly assembly, byte[] pdb, string source, string documentName)
    {
        var moduleId = assembly.ManifestModule.ModuleVersionId;
        var basePath = Path.Combine(_scriptDirectory, moduleId.ToString("N"));
        var pdbPath = basePath + ".pdb";
        var sourcePath = basePath + ".csx";
        File.WriteAllBytes(pdbPath, pdb);
        File.WriteAllText(sourcePath, source, Encoding.UTF8);

        lock (_lock)
        {
            _sources[moduleId] = new SymbolSource
            {
                PdbPath = pdbPath,
                SourcePath = sourcePath,
                DocumentName = documentName
            };
            _scriptOrder.Enqueue(moduleId);
            while (_scriptOrder.Count > MaxRetainedScriptSymbols)
            {
                var expiredId = _scriptOrder.Dequeue();
                if (!_sources.TryGetValue(expiredId, out var expired)) continue;
                _sources.Remove(expiredId);
                File.Delete(expired.PdbPath);
                File.Delete(expired.SourcePath);
            }
        }
    }

    public ExceptionReport Report(Exception exception)
    {
        exception = Unwrap(exception);
        return new ExceptionReport
        {
            Type = exception.GetType().FullName,
            Message = exception.Message,
            StackTrace = exception.ToString(),
            Frames = ResolveFrames(exception),
            Cause = exception.InnerException == null ? null : Report(exception.InnerException)
        };
    }

    public IReadOnlyList<SourceFrame> ResolveLogStack(string stackTrace)
    {
        var frames = new List<SourceFrame>();
        foreach (var stackLine in (stackTrace ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var match = Regex.Match(stackLine, @"\(at (?<file>.+):(?<line>\d+)\)\s*$");
            if (!match.Success)
            {
                match = Regex.Match(stackLine, @"\sin\s(?<file>.+):(?<line>\d+)\s*$");
            }
            if (!match.Success) continue;

            var file = match.Groups["file"].Value;
            var line = int.Parse(match.Groups["line"].Value);
            string source = null;
            if (line > 0 && File.Exists(file))
            {
                source = File.ReadAllText(file);
            }
            frames.Add(new SourceFrame
            {
                Method = stackLine.Substring(0, match.Index).Trim(),
                File = file,
                Line = line > 0 ? line : (int?)null,
                Context = line > 0 ? BuildContext(source, line) : null
            });
        }
        return frames;
    }

    private IReadOnlyList<SourceFrame> ResolveFrames(Exception exception)
    {
        var frames = new List<SourceFrame>();
        foreach (var frame in new StackTrace(exception, true).GetFrames() ?? Array.Empty<StackFrame>())
        {
            var method = frame.GetMethod();
            if (method == null) continue;

            var sourceFrame = new SourceFrame
            {
                Method = FormatMethod(method),
                Assembly = method.Module.Assembly.GetName().Name,
                IlOffset = frame.GetILOffset()
            };

            if (!TryResolve(method, frame.GetILOffset(), out var file, out var line, out var source))
            {
                file = frame.GetFileName();
                line = frame.GetFileLineNumber();
                if (line > 0 && !string.IsNullOrEmpty(file) && File.Exists(file))
                {
                    source = File.ReadAllText(file);
                }
            }

            if (line > 0)
            {
                sourceFrame.File = file;
                sourceFrame.Line = line;
                sourceFrame.Context = BuildContext(source, line);
            }
            frames.Add(sourceFrame);
        }
        return frames;
    }

    private bool TryResolve(MethodBase method, int ilOffset, out string file, out int line, out string source)
    {
        file = null;
        line = 0;
        source = null;
        var symbols = GetSymbols(method.Module);
        if (symbols == null || !File.Exists(symbols.PdbPath) || ilOffset < 0) return false;

        try
        {
            using var stream = File.OpenRead(symbols.PdbPath);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            var reader = provider.GetMetadataReader();
            var token = method.MetadataToken;
            if ((token & unchecked((int)0xff000000)) != 0x06000000) return false;

            var handle = MetadataTokens.MethodDefinitionHandle(token & 0x00ffffff);
            var debugInfo = reader.GetMethodDebugInformation(handle);
            SequencePoint? selected = null;
            foreach (var point in debugInfo.GetSequencePoints())
            {
                if (point.IsHidden || point.Offset > ilOffset) continue;
                if (selected == null || point.Offset >= selected.Value.Offset)
                {
                    selected = point;
                }
            }
            if (selected == null) return false;

            var documentHandle = selected.Value.Document.IsNil ? debugInfo.Document : selected.Value.Document;
            file = documentHandle.IsNil ? symbols.DocumentName : reader.GetString(reader.GetDocument(documentHandle).Name);
            line = selected.Value.StartLine;
            if (!string.IsNullOrEmpty(symbols.SourcePath) && File.Exists(symbols.SourcePath))
            {
                source = File.ReadAllText(symbols.SourcePath);
            }
            if (source == null && !string.IsNullOrEmpty(file) && File.Exists(file))
            {
                source = File.ReadAllText(file);
            }
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private SymbolSource GetSymbols(Module module)
    {
        var moduleId = module.ModuleVersionId;
        lock (_lock)
        {
            if (_sources.TryGetValue(moduleId, out var source)) return source;
            if (_assemblySymbols.TryGetValue(moduleId, out source)) return source;
        }

        var assemblyPath = module.Assembly.Location;
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(pdbPath)) return null;

        var symbols = new SymbolSource { PdbPath = pdbPath };
        lock (_lock)
        {
            _assemblySymbols[moduleId] = symbols;
        }
        return symbols;
    }

    public void Dispose()
    {
        if (Directory.Exists(_scriptDirectory))
        {
            Directory.Delete(_scriptDirectory, true);
        }
    }

    private static string BuildContext(string source, int line)
    {
        if (string.IsNullOrEmpty(source)) return null;
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (line < 1 || line > lines.Length) return null;

        var start = Math.Max(1, line - 4);
        var end = Math.Min(lines.Length, line + 4);
        var width = end.ToString().Length;
        var output = new List<string>();
        for (var current = start; current <= end; current++)
        {
            output.Add($"{(current == line ? '>' : ' ')} {current.ToString().PadLeft(width)} | {lines[current - 1]}");
        }
        return string.Join(Environment.NewLine, output);
    }

    private static string FormatMethod(MethodBase method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(item => item.ParameterType.Name));
        return $"{method.DeclaringType?.FullName}.{method.Name}({parameters})";
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException && exception.InnerException != null)
        {
            exception = exception.InnerException;
        }

        if (exception is AggregateException aggregate)
        {
            exception = aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? exception;
        }
        return exception;
    }
}
