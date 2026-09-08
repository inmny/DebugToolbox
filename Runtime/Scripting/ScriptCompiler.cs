using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using HarmonyLib;
using UnityEngine;

namespace DebugToolbox.Runtime.Scripting;

internal sealed class ScriptCompiler
{
    private readonly object _referenceLock = new();
    private readonly Dictionary<string, MetadataReference> _references = new(StringComparer.OrdinalIgnoreCase);

    public ScriptCompilation Compile(string source, string scriptId)
    {
        var className = "Script_" + scriptId.Replace("-", "_");
        var documentName = $"debugtoolbox://scripts/{scriptId}.csx";
        var generated = BuildSource(source, className, documentName);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            SourceText.From(generated, Encoding.UTF8),
            new CSharpParseOptions(LanguageVersion.Latest),
            documentName);

        var assemblyName = "DebugToolbox.Script." + scriptId;
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntaxTree },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: true));

        using var assemblyStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emit = compilation.Emit(
            assemblyStream,
            pdbStream,
            options: new EmitOptions(
                debugInformationFormat: DebugInformationFormat.PortablePdb,
                pdbFilePath: documentName));

        var diagnostics = emit.Diagnostics
            .Where(item => item.Severity == DiagnosticSeverity.Error || item.Severity == DiagnosticSeverity.Warning)
            .Select(item => ConvertDiagnostic(item, source, documentName))
            .ToArray();

        if (!emit.Success)
        {
            return new ScriptCompilation { Success = false, Diagnostics = diagnostics, GeneratedSource = generated };
        }

        return new ScriptCompilation
        {
            Success = true,
            Diagnostics = diagnostics,
            GeneratedSource = generated,
            Script = new CompiledScript
            {
                ScriptId = scriptId,
                AssemblyName = assemblyName,
                EntryType = "DebugToolbox.DynamicScripts." + className,
                DocumentName = documentName,
                Source = source,
                Assembly = assemblyStream.ToArray(),
                Pdb = pdbStream.ToArray(),
                Diagnostics = diagnostics
            }
        };
    }

    private IEnumerable<MetadataReference> GetReferences()
    {
        var requiredAssemblies = new[]
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(ScriptCompiler).Assembly,
            typeof(UnityEngine.Object).Assembly,
            typeof(Harmony).Assembly,
            typeof(JObject).Assembly
        };
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Concat(requiredAssemblies)
            .Where(item => !item.IsDynamic)
            .GroupBy(item => item.GetName().Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());

        lock (_referenceLock)
        {
            foreach (var assembly in assemblies)
            {
                var location = ResolveReferencePath(assembly);
                if (location == null || _references.ContainsKey(location)) continue;
                try
                {
                    _references[location] = MetadataReference.CreateFromFile(location);
                }
                catch (BadImageFormatException)
                {
                    // 原生或混合模式程序集不能作为 Roslyn 引用。
                }
            }
            return _references.Values.ToArray();
        }
    }

    private static string ResolveReferencePath(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (string.IsNullOrEmpty(name)) return null;

        var dataPath = Application.dataPath;
        if (string.Equals(name, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase))
        {
            var publicized = Path.Combine(
                dataPath,
                "StreamingAssets",
                "mods",
                "NML",
                "Assembly-CSharp-Publicized.dll");
            if (IsMatchingAssembly(publicized, name)) return publicized;
        }

        var location = ReadAssemblyLocation(assembly);
        if (IsMatchingAssembly(location, name)) return location;

        var fileName = name + ".dll";
        var candidates = new[]
        {
            Path.Combine(dataPath, "StreamingAssets", "mods", "NML", "CompiledMods", fileName),
            Path.Combine(dataPath, "Managed", fileName),
            Path.Combine(dataPath, "StreamingAssets", "mods", "NML", "Assemblies", fileName),
            Path.Combine(dataPath, "StreamingAssets", "mods", "NML", fileName),
            Path.Combine(dataPath, "StreamingAssets", "mods", fileName)
        };
        return candidates.FirstOrDefault(candidate => IsMatchingAssembly(candidate, name));
    }

    private static string ReadAssemblyLocation(Assembly assembly)
    {
        try
        {
            return assembly.Location;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsMatchingAssembly(string path, string expectedName)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            return string.Equals(
                AssemblyName.GetAssemblyName(path).Name,
                expectedName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is BadImageFormatException ||
            exception is FileLoadException ||
            exception is IOException ||
            exception is UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string BuildSource(string source, string className, string documentName)
    {
        var sourceTree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8),
            new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Regular),
            documentName);
        var root = (CompilationUnitSyntax)sourceTree.GetRoot();
        var output = new StringBuilder();
        var defaultUsings = new HashSet<string>
        {
            "using System;",
            "using System.Collections;",
            "using System.Collections.Generic;",
            "using System.Linq;",
            "using System.Reflection;",
            "using System.Threading;",
            "using System.Threading.Tasks;",
            "using DebugToolbox.Runtime.Scripting;",
            "using DebugToolbox.Runtime.Tracing;",
            "using HarmonyLib;",
            "using Newtonsoft.Json.Linq;",
            "using UnityEngine;"
        };
        foreach (var defaultUsing in defaultUsings)
        {
            output.AppendLine(defaultUsing);
        }
        foreach (var usingDirective in root.Usings)
        {
            var text = usingDirective.WithoutTrivia().ToString();
            if (!defaultUsings.Contains(text))
            {
                output.AppendLine(usingDirective.ToFullString());
            }
        }

        foreach (var declaration in root.Members.Where(item => item is not GlobalStatementSyntax))
        {
            var line = declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            output.AppendLine($"#line {line} \"{documentName}\"");
            output.AppendLine(declaration.ToFullString());
            output.AppendLine("#line hidden");
        }

        output.AppendLine("namespace DebugToolbox.DynamicScripts");
        output.AppendLine("{");
        output.AppendLine($"public sealed class {className} : DebugScriptBase");
        output.AppendLine("{");
        output.AppendLine("#pragma warning disable CS1998");
        output.AppendLine("protected override async Task<object> Execute()");
        output.AppendLine("{");
        foreach (var global in root.Members.OfType<GlobalStatementSyntax>())
        {
            var line = global.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            output.AppendLine($"#line {line} \"{documentName}\"");
            output.AppendLine(global.Statement.ToFullString());
            output.AppendLine("#line hidden");
        }
        output.AppendLine("#pragma warning disable CS0162");
        output.AppendLine("return null;");
        output.AppendLine("#pragma warning restore CS0162");
        output.AppendLine("}");
        output.AppendLine("#pragma warning restore CS1998");
        output.AppendLine("}");
        output.AppendLine("}");
        return output.ToString();
    }

    private static ScriptDiagnostic ConvertDiagnostic(Diagnostic diagnostic, string source, string documentName)
    {
        var result = new ScriptDiagnostic
        {
            Id = diagnostic.Id,
            Severity = diagnostic.Severity.ToString().ToLowerInvariant(),
            Message = diagnostic.GetMessage()
        };

        if (!diagnostic.Location.IsInSource) return result;
        var span = diagnostic.Location.GetMappedLineSpan();
        result.File = string.IsNullOrEmpty(span.Path) ? documentName : span.Path;
        result.Line = span.StartLinePosition.Line + 1;
        result.Column = span.StartLinePosition.Character + 1;
        result.Context = BuildContext(source, result.Line.Value);
        return result;
    }

    private static string BuildContext(string source, int line)
    {
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (line < 1 || line > lines.Length) return null;
        var start = Math.Max(1, line - 3);
        var end = Math.Min(lines.Length, line + 3);
        var width = end.ToString().Length;
        return string.Join(Environment.NewLine, Enumerable.Range(start, end - start + 1)
            .Select(current => $"{(current == line ? '>' : ' ')} {current.ToString().PadLeft(width)} | {lines[current - 1]}"));
    }
}
