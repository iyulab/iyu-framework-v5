using System.Text.RegularExpressions;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// Every runtime version the README's generator-to-runtime table names is a release this
/// repository's changelog records.
/// </summary>
/// <remarks>
/// The table says which <c>Iyu.*</c> release a generator release needs. A mistyped or invented
/// number there sends a reader to a runtime that does not exist, and nothing else would notice:
/// the table is prose. The floors are bold in the table's third column; each one has to be a
/// heading in the changelog that ships beside the README. Both are read from the copies
/// <c>Iyu.Tests.csproj</c> places in the output folder, for the reason
/// <see cref="PublishedStatusTests"/> gives.
/// </remarks>
public class ReadmeVersionPairingTests
{
    private const string SectionHeading = "### Which `mdd` release needs which `Iyu.*` release";

    private static string Guidance(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "guidance", file);
        Assert.True(File.Exists(path), $"'{path}' was not found; it is copied there by Iyu.Tests.csproj.");
        return File.ReadAllText(path);
    }

    private static string PairingSection()
    {
        var text = Guidance("README.md");
        var start = text.IndexOf(SectionHeading, StringComparison.Ordinal);
        Assert.True(start >= 0,
            $"the README has no '{SectionHeading}' section. If it was retitled, update this test; " +
            "if it was removed, the pairing guidance is gone.");

        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    [Fact]
    public void Every_runtime_version_in_the_pairing_table_is_a_changelog_release()
    {
        var floors = Regex.Matches(PairingSection(), @"\*\*(?<version>[0-9]+\.[0-9]+\.[0-9]+)\*\*")
            .Select(m => m.Groups["version"].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(floors);

        var changelog = Guidance("CHANGELOG.md");
        var missing = floors
            .Where(v => !Regex.IsMatch(changelog, $@"^## \[{Regex.Escape(v)}\]", RegexOptions.Multiline))
            .ToList();

        Assert.True(missing.Count == 0,
            "the pairing table names runtime versions the changelog has no release heading for: " +
            string.Join(", ", missing));
    }
}
