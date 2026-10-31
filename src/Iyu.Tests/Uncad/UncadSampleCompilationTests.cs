using System.Text.RegularExpressions;
using Xunit;

namespace Iyu.Tests.Uncad;

/// <summary>
/// The README's example for <c>Iyu.Uncad</c> is the first thing a new user copies, so it is compiled here against the packages as
/// they are — read from the test output where <c>Iyu.Tests.csproj</c> copies the README, never from a copy kept in test code.
/// </summary>
public class UncadSampleCompilationTests
{
    private const string Marker = "<!-- uncad-sample -->";

    // The sample is written for a project with implicit usings, which new projects have; a plain compilation does not.
    private const string ImplicitUsings =
        "global using System;\nglobal using System.Collections.Generic;\nglobal using System.IO;\nglobal using System.Linq;\n"
        + "global using System.Threading;\nglobal using System.Threading.Tasks;\n";

    private static string Sample()
    {
        var match = Regex.Match(
            ReadmeCompilation.ReadmeText(),
            Regex.Escape(Marker) + @"\r?\n```csharp\r?\n(?<code>.*?)\r?\n```",
            RegexOptions.Singleline);
        Assert.True(match.Success,
            "the Iyu.Uncad sample was not found in the README (the code block that follows " + Marker + "). If the section "
            + "was reworded, keep the marker: this test asserts nothing while its subject is missing.");
        return match.Groups["code"].Value;
    }

    [Fact]
    public void The_example_in_the_README_compiles_against_these_packages()
    {
        var errors = ReadmeCompilation.Errors("UncadSample", ImplicitUsings + Sample())
            .Select(e => $"{e.Id}: {e.GetMessage()} ({e.Location.GetLineSpan().StartLinePosition.Line + 1})");

        Assert.Empty(errors);
    }

    [Fact]
    public void The_example_shows_the_opt_in_and_a_read_through_the_service()
    {
        var sample = Sample();

        Assert.Contains("AddIyuUncad(", sample);
        Assert.Contains("ExePath", sample);
        Assert.Contains("DrawingProbeService", sample);
        Assert.Contains("ProbeAsync(", sample);
    }
}
