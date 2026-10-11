using Microsoft.CodeAnalysis;
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

    private static IReadOnlyList<Diagnostic> CompileWith(IEnumerable<string> globalUsings) =>
        ReadmeCompilation.Errors("ConsumerShaped",
            string.Join(Environment.NewLine, globalUsings) + Environment.NewLine + Environment.NewLine + ConsumerShapedSource);

    /// <summary>
    /// The guidance, as published, resolves every name a consumer's code carries.
    /// </summary>
    [Fact]
    public void Consumer_shaped_code_compiles_with_the_published_global_usings()
    {
        var errors = CompileWith(ReadmeCompilation.PublishedGlobalUsings());

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
        var published = ReadmeCompilation.PublishedGlobalUsings();

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
