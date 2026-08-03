using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// A consuming project's code names these types simply — <c>IyuEntity</c>, not the
/// namespace path to it — and declares the namespaces once, as the README's
/// "Namespaces a consumer needs" section describes. That guidance is the whole
/// contract: get it wrong and every consumer file fails to compile.
/// </summary>
/// <remarks>
/// Nothing checked this. The packages are built and tested here, and the guidance is
/// published here, but the two were only ever joined in someone else's build — where
/// a gap surfaces as <c>CS0246</c> on their side, after release. So this compiles a
/// consumer-shaped file against the declarations the README actually publishes.
/// <para>
/// The declarations are read from the README, not repeated here. Repeating them is the
/// failure this test exists to catch, moved one level up: the copy and the published
/// text drift, the suite still passes, and the consumer still finds out first.
/// </para>
/// <para>
/// The source below is a fixture, not a sample to copy. It exists to name each type the
/// guidance covers, in the position a consumer would name it — a base class, an
/// attribute, a context, a controller, an options type.
/// </para>
/// </remarks>
public sealed class ConsumerNamespaceGuidanceTests
{
    /// <summary>
    /// Types the guidance is responsible for, each named the way a consumer names it.
    /// Compiling this is the assertion; it has no runtime behaviour.
    /// </summary>
    /// <remarks>
    /// Adding a type here is a decision: it says the guidance must resolve that name,
    /// which means the README has to carry the namespace for it.
    /// </remarks>
    private const string ConsumerShapedSource = """
        using System;
        using Microsoft.EntityFrameworkCore;

        namespace Consumer.Generated;

        public partial class Customer : IyuEntity
        {
            public string Name { get; set; } = string.Empty;
        }

        public partial class Order : IyuEntity
        {
            [Reference("Customer")]
            public Guid CustomerId { get; set; }
        }

        public class ConsumerDbContext(DbContextOptions options) : IyuDbContext(options)
        {
            public DbSet<Customer> Customers => Set<Customer>();
            public DbSet<Order> Orders => Set<Order>();
        }

        public class CustomersController(IyuDbContext context)
            : IyuODataController<Customer, Customer>(context);

        public static class Registration
        {
            public static void Register(IyuMainServerOptions options)
                => options.ODataModel.AddEntityPair<Customer, Customer>("Customers");
        }
        """;

    private static string ReadmeText()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "guidance", "README.md");
        Assert.True(File.Exists(path),
            $"the published guidance was not found at '{path}'. It is copied there from the " +
            "repository README by Iyu.Tests.csproj; if the README moved, update that copy rule. " +
            "This test asserts nothing while its subject is missing.");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The <c>global using</c> lines the README's guidance section publishes, in order.
    /// </summary>
    private static IReadOnlyList<string> PublishedGlobalUsings()
    {
        var text = ReadmeText();
        var start = text.IndexOf("## Namespaces a consumer needs", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "the namespace guidance section was not found in the README. If it was retitled, " +
            "update this test — but check first that the guidance still exists at all, because " +
            "consumers are told to look for it.");

        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? text[start..] : text[start..end];

        return [.. Regex.Matches(section, @"^\s*(?<line>global using [^;]+;)", RegexOptions.Multiline)
            .Select(m => m.Groups["line"].Value)];
    }

    /// <summary>
    /// What a project referencing these packages could compile against: the assemblies
    /// this process trusts, plus this project's own output. The shared framework is not
    /// copied next to the binaries, so the output directory alone is short of
    /// <c>System.Runtime</c> and the ASP.NET Core assemblies the controller needs.
    /// </summary>
    private static IReadOnlyList<MetadataReference> ReferenceSet()
    {
        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        // One reference per assembly name: the same assembly reachable by two paths is
        // CS1703, which would read as a defect in the guidance rather than in this list.
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

    private static IReadOnlyList<Diagnostic> CompileWith(IEnumerable<string> globalUsings)
    {
        var source = string.Join(Environment.NewLine, globalUsings)
                     + Environment.NewLine + Environment.NewLine + ConsumerShapedSource;

        var compilation = CSharpCompilation.Create(
            "ConsumerShaped",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            ReferenceSet(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    /// <summary>
    /// The guidance, as published, resolves every name a consumer's code carries.
    /// </summary>
    [Fact]
    public void Consumer_shaped_code_compiles_with_the_published_global_usings()
    {
        var errors = CompileWith(PublishedGlobalUsings());

        Assert.True(errors.Count == 0,
            "consumer-shaped code does not compile with the `global using` lines the README " +
            "publishes:\n" +
            string.Join("\n", errors.Select(e => $"  {e.Id}: {e.GetMessage()}")) +
            "\n\nThose lines are what a consuming project is told to declare, so a name they do " +
            "not resolve is a name no consumer can use. Either the guidance is missing a " +
            "namespace, or a type moved without it being updated.");
    }

    /// <summary>
    /// Guards the guard. If the section were emptied, renamed, or reformatted so no lines
    /// parsed out, the compile above would still pass on any type that needs no using —
    /// and would report the guidance as sound while publishing nothing.
    /// </summary>
    [Fact]
    public void The_guidance_actually_carries_the_declarations()
    {
        var published = PublishedGlobalUsings();

        Assert.True(published.Count >= 5,
            $"the guidance section parsed out {published.Count} `global using` line(s). Fewer than " +
            "a real consumer needs, so it was probably emptied or reformatted — and this test " +
            "would then be compiling against almost nothing and calling it a pass.");

        Assert.All(published, line =>
            Assert.StartsWith("global using Iyu.", line, StringComparison.Ordinal));
    }

    /// <summary>
    /// The fixture is load-bearing: it has to fail without the guidance, or its passing
    /// says nothing about the guidance. Compiling it bare is how that is shown rather
    /// than assumed.
    /// </summary>
    [Fact]
    public void Without_the_guidance_the_same_code_does_not_compile()
    {
        var errors = CompileWith([]);

        Assert.True(errors.Any(e => e.Id == "CS0246"),
            "consumer-shaped code compiled without any `global using`, so this suite would pass " +
            "even if the guidance published nothing. The fixture has stopped naming types that " +
            "need declaring — it must carry at least one simple name the consumer resolves.");
    }
}
