using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// The first code a new user copies out of the README — the minimum consumer's <c>Program.cs</c> and the write-rule example —
/// is compiled here against the packages as they are, read from the test output where <c>Iyu.Tests.csproj</c> copies the
/// README, never from a copy kept in test code.
/// </summary>
/// <remarks>
/// A sample that compiled only because the test supplied a <c>using</c> the README leaves out would pass and still fail the
/// reader, so each sample brings its own <c>using</c> lines and the harness declares only what a host brings itself: the
/// <c>global using</c> lines the README's namespace guidance publishes, the implicit usings of a web project, and the
/// entities and context of the host's own model.
/// </remarks>
public sealed class ReadmeConsumerSnippetTests
{
    private const string MinimumConsumerMarker = "<!-- minimum-consumer-sample -->";
    private const string WriteRuleMarker = "<!-- write-rule-sample -->";
    private const string WriteRuleRegistrationMarker = "<!-- write-rule-registration -->";

    /// <summary>What a web project (<c>Microsoft.NET.Sdk.Web</c>) declares by itself.</summary>
    private const string WebImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Hosting;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.Extensions.Configuration;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.Extensions.Hosting;
        global using Microsoft.Extensions.Logging;
        """;

    /// <summary>The entities and the context of the host's own model, at their smallest.</summary>
    private const string HostSide = """
        using Microsoft.EntityFrameworkCore;

        public partial class Order : IyuEntity
        {
            public string? VatType { get; set; }
            public DateTimeOffset? BilledDate { get; set; }
        }

        public partial class OrderExt : IyuEntity
        {
            public string? VatType { get; set; }
            public DateTimeOffset? BilledDate { get; set; }
        }

        public class AppDbContext(DbContextOptions options) : IyuDbContext(options)
        {
            public DbSet<Order> Orders => Set<Order>();
        }
        """;

    /// <summary>
    /// The sample calls <c>UseSqlServer</c> from the SQL Server provider package, which this test project does not reference
    /// (referencing it would be a new dependency for one call). A stand-in with the same call shape lives in the namespace the
    /// real one does, so the sample still has to say <c>using Microsoft.EntityFrameworkCore;</c> to reach it.
    /// </summary>
    private const string SqlServerProviderStandIn = """
        namespace Microsoft.EntityFrameworkCore;

        public static class SqlServerProviderStandIn
        {
            public static DbContextOptionsBuilder UseSqlServer(this DbContextOptionsBuilder options, string? connectionString) => options;
        }
        """;

    private static string Block(string marker, string what)
    {
        var match = Regex.Match(
            ReadmeCompilation.ReadmeText(),
            Regex.Escape(marker) + @"\r?\n```csharp\r?\n(?<code>.*?)\r?\n```",
            RegexOptions.Singleline);
        Assert.True(match.Success,
            $"the {what} was not found in the README (the code block that follows {marker}). If the section was reworded, "
            + "keep the marker: this test asserts nothing while its subject is missing.");
        return match.Groups["code"].Value;
    }

    private static string GlobalUsings() =>
        WebImplicitUsings + Environment.NewLine + string.Join(Environment.NewLine, ReadmeCompilation.PublishedGlobalUsings());

    private static string Format(IEnumerable<Diagnostic> errors) =>
        string.Join(Environment.NewLine, errors.Select(e => $"{e.Id}: {e.GetMessage()} (line {e.Location.GetLineSpan().StartLinePosition.Line + 1})"));

    [Fact]
    public void The_minimum_consumer_in_the_README_compiles_against_these_packages()
    {
        var sample = Block(MinimumConsumerMarker, "minimum consumer sample");

        var errors = ReadmeCompilation.Errors(
            "MinimumConsumerSample",
            [GlobalUsings(), HostSide, SqlServerProviderStandIn, sample],
            OutputKind.ConsoleApplication);

        Assert.True(errors.Count == 0, Format(errors));
    }

    /// <summary>
    /// The sample's package lines are the other half of «show what to reference»: a line of the sample that needs a package
    /// the lines above it do not name sends the reader to a compile error that the text could have prevented.
    /// </summary>
    [Fact]
    public void The_minimum_consumer_names_the_packages_its_code_needs()
    {
        var readme = ReadmeCompilation.ReadmeText();
        var start = readme.IndexOf("## Minimum consumer", StringComparison.Ordinal);
        var marker = readme.IndexOf(MinimumConsumerMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && marker > start, "the Minimum consumer section or its sample marker was not found in the README.");
        var lead = readme[start..marker];
        var sample = Block(MinimumConsumerMarker, "minimum consumer sample");

        Assert.Contains("dotnet add package Iyu.MainServer", lead);
        if (sample.Contains("options.GraphQL", StringComparison.Ordinal))
            Assert.Contains("dotnet add package Iyu.MainServer.GraphQL", lead);
        if (sample.Contains("UseSqlServer", StringComparison.Ordinal))
            Assert.Contains("dotnet add package Microsoft.EntityFrameworkCore.SqlServer", lead);
    }

    [Fact]
    public void The_write_rule_example_in_the_README_compiles_against_these_packages()
    {
        var rule = Block(WriteRuleMarker, "write-rule example");
        var registration = Block(WriteRuleRegistrationMarker, "write-rule registration line");
        var (registrationUsings, registrationBody) = SplitUsings(registration);

        var registrationHost = registrationUsings + """

            public sealed class Program { }

            public static class RuleRegistration
            {
                public static void Register(WebApplicationBuilder builder)
                {
            """ + registrationBody + """

                }
            }
            """;

        var errors = ReadmeCompilation.Errors(
            "WriteRuleSample",
            [GlobalUsings(), HostSide, rule, registrationHost],
            OutputKind.DynamicallyLinkedLibrary);

        Assert.True(errors.Count == 0, Format(errors));
    }

    /// <summary>Leading <c>using</c> lines go to the top of a file; the rest is what the sample does.</summary>
    private static (string Usings, string Body) SplitUsings(string code)
    {
        var lines = code.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var usings = lines.TakeWhile(l => l.StartsWith("using ", StringComparison.Ordinal) || l.Length == 0).ToList();
        return (string.Join(Environment.NewLine, usings), string.Join(Environment.NewLine, lines.Skip(usings.Count)));
    }
}
