using System.Reflection;
using System.Runtime.Loader;
using Iyu.Data.WriteRules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Data;

/// <summary>
/// What assembly scanning does when the assembly it is handed cannot be fully loaded.
/// </summary>
/// <remarks>
/// Scanning exists so that a rule cannot be missed by omission. An assembly whose types partly
/// fail to load reopens that hole from the other side: the rule is absent, and absence is
/// indistinguishable at runtime from a rule whose condition never fired. These tests compile two
/// assemblies and then deny one of them its dependency, which is the only way to observe the
/// behaviour — it cannot be reached from a normally-loaded test assembly.
/// </remarks>
public class WriteRuleAssemblyScanTests
{
    private const string DependencySource = """
        namespace SpikeDep;

        public abstract class GuardBase { }
        """;

    private const string RulesSource = """
        using System;
        using Iyu.Data.WriteRules;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.ChangeTracking;

        namespace SpikeRules;

        public sealed class LoadableRule : IEntityWriteRule
        {
            public Type EntityType => typeof(object);
            public void Apply(EntityEntry entry, DbContext context) { }
        }

        /// A rule that cannot load unless its base type does.
        public sealed class GuardRule : SpikeDep.GuardBase, IEntityWriteRule
        {
            public Type EntityType => typeof(object);
            public void Apply(EntityEntry entry, DbContext context) { }
        }
        """;

    [Fact]
    public void A_partially_loadable_assembly_is_refused_rather_than_registered_in_part()
    {
        var (rules, _) = CompileRuleAssemblies();

        using var context = new DenyDependencyLoadContext();
        var assembly = context.LoadFromStream(new MemoryStream(rules));

        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddIyuWriteRules(assembly));

        Assert.Contains("SpikeRules", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SpikeDep", ex.Message, StringComparison.Ordinal);
        Assert.IsType<ReflectionTypeLoadException>(ex.InnerException);

        // And nothing was registered on the way to refusing: a caller that catches this must not
        // find a half-built rule set behind it.
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IEntityWriteRule));
    }

    [Fact]
    public void The_same_assembly_registers_normally_once_its_dependency_resolves()
    {
        var (rules, dependency) = CompileRuleAssemblies();

        using var context = new ResolveDependencyLoadContext(dependency);
        var assembly = context.LoadFromStream(new MemoryStream(rules));

        var services = new ServiceCollection();
        services.AddIyuWriteRules(assembly);

        var registered = services
            .Where(d => d.ServiceType == typeof(IEntityWriteRule))
            .Select(d => d.ImplementationType!.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["GuardRule", "LoadableRule"], registered);
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>Compiles the rules assembly and the dependency it needs, as raw bytes.</summary>
    private static (byte[] Rules, byte[] Dependency) CompileRuleAssemblies()
    {
        var dependency = Emit("SpikeDep", DependencySource, []);
        var rules = Emit("SpikeRules", RulesSource, [MetadataReference.CreateFromImage(dependency)]);
        return (rules, dependency);
    }

    private static byte[] Emit(string name, string source, IEnumerable<MetadataReference> extra)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            ReferenceSet().Concat(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);

        Assert.True(
            result.Success,
            $"{name} failed to compile: " + string.Join(
                Environment.NewLine,
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        return stream.ToArray();
    }

    /// <summary>
    /// One reference per assembly name — the same assembly reachable by two paths is CS1703,
    /// which would read as a defect in these fixtures rather than in the list.
    /// </summary>
    private static IReadOnlyList<MetadataReference> ReferenceSet()
    {
        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var byName = trusted
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        var refs = new List<MetadataReference>();
        foreach (var dll in byName)
        {
            try { refs.Add(MetadataReference.CreateFromFile(dll)); }
            catch (BadImageFormatException) { /* native or resource-only; not a reference */ }
            catch (IOException) { /* listed but not present; the compile reports what it misses */ }
        }
        return refs;
    }

    /// <summary>
    /// Resolves everything the default context knows — which includes Iyu.Data, so the loaded
    /// rules implement the same <c>IEntityWriteRule</c> this test does — and nothing else. The
    /// compiled dependency is deliberately not among them.
    /// </summary>
    private sealed class DenyDependencyLoadContext()
        : AssemblyLoadContext("write-rule-scan-deny", isCollectible: true), IDisposable
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;

        public void Dispose() => Unload();
    }

    /// <summary>The same context, but able to supply the dependency from memory.</summary>
    private sealed class ResolveDependencyLoadContext(byte[] dependency)
        : AssemblyLoadContext("write-rule-scan-resolve", isCollectible: true), IDisposable
    {
        private readonly byte[] _dependency = dependency;

        protected override Assembly? Load(AssemblyName assemblyName)
            => assemblyName.Name == "SpikeDep"
                ? LoadFromStream(new MemoryStream(_dependency))
                : null;

        public void Dispose() => Unload();
    }
}
