using System.Xml.Linq;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// Every package says what it is in its own words.
/// </summary>
/// <remarks>
/// The repository-level props carry one fallback <c>Description</c>. A project that does not
/// set its own inherits it, and then every package on nuget.org reads as the same sentence — a
/// reader choosing between two of them cannot tell what either does without opening a README.
/// A new project is added without anyone looking at its description, so the rule is held here:
/// each shipped project sets its own, and no two share one.
/// </remarks>
public sealed class PackageMetadataTests
{
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IyuFramework.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null,
            "the repository root (IyuFramework.slnx) was not found above the test output; " +
            "this test asserts nothing while its subject is missing.");
        return Path.Combine(dir!.FullName, "src");
    }

    private static List<(string Project, string? Description)> Descriptions() =>
        Directory.GetDirectories(SourceRoot())
            .Select(d => (Name: Path.GetFileName(d), Dir: d))
            .Where(x => x.Name != "Iyu.Tests")
            .Select(x => Path.Combine(x.Dir, x.Name + ".csproj"))
            .Where(File.Exists)
            .Select(f => (
                Project: Path.GetFileNameWithoutExtension(f),
                Description: XDocument.Load(f).Descendants("Description").FirstOrDefault()?.Value.Trim()))
            .ToList();

    [Fact]
    public void Every_project_sets_its_own_description()
    {
        var all = Descriptions();
        Assert.NotEmpty(all);

        var missing = all.Where(x => string.IsNullOrWhiteSpace(x.Description)).Select(x => x.Project).ToList();
        Assert.True(missing.Count == 0,
            "these projects inherit the repository-level description instead of describing themselves: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void No_two_projects_share_a_description()
    {
        var shared = Descriptions()
            .Where(x => !string.IsNullOrWhiteSpace(x.Description))
            .GroupBy(x => x.Description!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" = ", g.Select(x => x.Project)))
            .ToList();

        Assert.True(shared.Count == 0, "projects with the same description: " + string.Join("; ", shared));
    }

    private static List<(string Project, string? Tags)> Tags() =>
        Directory.GetDirectories(SourceRoot())
            .Select(d => (Name: Path.GetFileName(d), Dir: d))
            .Where(x => x.Name != "Iyu.Tests")
            .Select(x => Path.Combine(x.Dir, x.Name + ".csproj"))
            .Where(File.Exists)
            .Select(f => (
                Project: Path.GetFileNameWithoutExtension(f),
                Tags: XDocument.Load(f).Descendants("PackageTags").FirstOrDefault()?.Value.Trim()))
            .ToList();

    // The repository-level props also carry one fallback set of tags (OData, GraphQL, ...): a package that does not set its
    // own is found on nuget.org under words that describe a different package.
    [Fact]
    public void Every_project_sets_its_own_tags()
    {
        var all = Tags();
        Assert.NotEmpty(all);

        var missing = all.Where(x => string.IsNullOrWhiteSpace(x.Tags)).Select(x => x.Project).ToList();
        Assert.True(missing.Count == 0,
            "these projects inherit the repository-level tags instead of naming what they are for: " + string.Join(", ", missing));
    }

    [Fact]
    public void No_two_projects_share_tags()
    {
        var shared = Tags()
            .Where(x => !string.IsNullOrWhiteSpace(x.Tags))
            .GroupBy(x => x.Tags!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" = ", g.Select(x => x.Project)))
            .ToList();

        Assert.True(shared.Count == 0, "projects with the same tags: " + string.Join("; ", shared));
    }
}
