using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NetPulse.Common;
using NetPulse.Unity.Generators;

namespace NetPulse.Unity.Generators.Tests;

internal sealed class RunResult
{
    public RunResult(Compilation output, ImmutableArray<Diagnostic> generatorDiagnostics, IReadOnlyDictionary<string, string> generated)
    {
        Output = output;
        GeneratorDiagnostics = generatorDiagnostics;
        Generated = generated;
    }

    public Compilation Output { get; }

    public ImmutableArray<Diagnostic> GeneratorDiagnostics { get; }

    public IReadOnlyDictionary<string, string> Generated { get; }

    public string AllGenerated => string.Join("\n", Generated.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value));

    public string[] CompileProblems => Output.GetDiagnostics()
        .Where(d => d.Severity >= DiagnosticSeverity.Warning)
        .Where(d => d.Location.SourceTree == null || d.Location.SourceTree.FilePath != Harness.StubPath)
        .Select(d => d.ToString())
        .ToArray();

    public Diagnostic[] Analyze()
    {
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new NetPulseAnalyzer());
        var withAnalyzers = Output.WithAnalyzers(analyzers);
        return withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult().ToArray();
    }

    public string[] AnalyzerIds => Analyze().Select(d => d.Id).OrderBy(i => i, StringComparer.Ordinal).ToArray();

    public Assembly Load()
    {
        using var stream = new MemoryStream();
        var emit = Output.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }
}

internal static class Harness
{
    public const string StubPath = "stub.cs";

    private static readonly Lazy<MetadataReference[]> References = new(BuildReferences);
    private static readonly Lazy<string> StubSource = new(ReadStub);

    private static MetadataReference[] BuildReferences()
    {
        var directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>();

        foreach (var path in Directory.GetFiles(directory, "*.dll"))
        {
            try
            {
                AssemblyName.GetAssemblyName(path);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            references.Add(MetadataReference.CreateFromFile(path));
        }

        references.Add(MetadataReference.CreateFromFile(typeof(NetWriter).Assembly.Location));
        return references.ToArray();
    }

    private static string ReadStub() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RuntimeStub.cs.txt"));

    public static RunResult Run(string source, params string[] defines)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: defines);
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(StubSource.Value, options, StubPath),
            CSharpSyntaxTree.ParseText(source, options, "user.cs")
        };

        var compilation = CSharpCompilation.Create(
            "TestAssembly" + Guid.NewGuid().ToString("N"),
            trees,
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: false));

        var driver = CSharpGeneratorDriver.Create(new[] { new NetPulseGenerator() }, parseOptions: options);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var generated = new Dictionary<string, string>();

        foreach (var tree in output.SyntaxTrees)
        {
            if (tree.FilePath != StubPath && tree.FilePath != "user.cs")
            {
                generated[Path.GetFileName(tree.FilePath)] = tree.GetText().ToString();
            }
        }

        return new RunResult(output, diagnostics, generated);
    }

    public static RunResult RunNoRuntime(string source)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp9);
        var compilation = CSharpCompilation.Create(
            "TestAssembly" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source, options, "user.cs") },
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new[] { new NetPulseGenerator() }, parseOptions: options);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var generated = output.SyntaxTrees.Where(t => t.FilePath != "user.cs").ToDictionary(t => t.FilePath, t => t.GetText().ToString());
        return new RunResult(output, diagnostics, generated);
    }
}
