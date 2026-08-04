using System.Reflection;
using System.Text.RegularExpressions;
using Iyu.Core.Entities;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// The version the README announces is the version the packages carry.
/// </summary>
/// <remarks>
/// <para>
/// The Status section is where a reader decides how recent everything below it is — the known-gaps
/// list under it is only as trustworthy as the version above it. It was wrong at two releases in a
/// row, and correcting it by hand is what kept being forgotten, because nothing failed when it was
/// skipped.
/// </para>
/// <para>
/// The version is read from the shipped assembly rather than from the props file, so this checks
/// what a consumer would actually resolve, not what the build was told. It reads the README from
/// the same copied file <see cref="ConsumerNamespaceGuidanceTests"/> uses, for the same reason:
/// a copy kept in test code is the failure being guarded against, one level up.
/// </para>
/// </remarks>
public class PublishedStatusTests
{
    private static string ReadmeText()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "guidance", "README.md");
        Assert.True(File.Exists(path),
            $"the published README was not found at '{path}'. It is copied there by " +
            "Iyu.Tests.csproj; if the README moved, update that copy rule. This test asserts " +
            "nothing while its subject is missing.");
        return File.ReadAllText(path);
    }

    private static string StatusSection()
    {
        var text = ReadmeText();
        var start = text.IndexOf("## Status", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "the Status section was not found in the README. If it was retitled, update this " +
            "test — but check first that it still exists, because it is where the version and " +
            "the known-gaps list are published.");

        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    /// <summary>The version the packages actually carry, without the source-link build metadata.</summary>
    private static string ShippedVersion()
    {
        var informational = typeof(IyuEntity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(informational),
            "the assembly carries no informational version, so there is nothing to compare the " +
            "README against.");

        var plus = informational!.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }

    [Fact]
    public void The_readme_announces_the_version_the_packages_carry()
    {
        var declared = Regex.Match(StatusSection(), @"Version \*\*(?<version>[0-9]+\.[0-9]+\.[0-9]+)\*\*");

        Assert.True(declared.Success,
            "the Status section does not announce a version in the expected `Version **X.Y.Z**` " +
            $"form. Section read:\n{StatusSection()}");

        Assert.Equal(ShippedVersion(), declared.Groups["version"].Value);
    }

    /// <summary>
    /// Nothing in the Status section may be a hand-kept count.
    /// </summary>
    /// <remarks>
    /// A test total has to be re-typed on every commit that adds one, which is a maintenance cost
    /// paid forever for a number that tells a reader very little — and the same neglect that let
    /// the version rot applies to it with far more opportunities. The version is guarded because it
    /// is worth publishing and has a single source of truth; a running total has neither property,
    /// so it is not published at all rather than published stale.
    /// </remarks>
    [Fact]
    public void The_status_section_publishes_no_hand_kept_test_count()
    {
        var offending = Regex.Match(StatusSection(), @"\b\d+\s+(unit|integration|tests?\b)",
            RegexOptions.IgnoreCase);

        Assert.False(offending.Success,
            $"the Status section quotes a test count ('{offending.Value}'). It goes stale on the " +
            "next commit that adds a test, and no reader acts on the number. Describe the suite " +
            "instead of counting it.");
    }
}
