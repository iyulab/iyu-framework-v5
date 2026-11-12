using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// Each package opens on a README that is about it.
/// </summary>
/// <remarks>
/// The repository README is long (it is the documentation of every package), and the package page on nuget.org shows the
/// README that is packed. Before a package had one of its own, every package showed the same repository page, headed
/// with the repository's name. <c>package-readmes/&lt;project&gt;.md</c> is packed as the package's README instead, and
/// this holds the three things that go wrong silently: a new package added without one, a README that says something its
/// project's description does not, and a link into the repository README whose heading was renamed.
/// </remarks>
public sealed class PackageReadmeTests
{
    private static string Root() => Path.GetDirectoryName(PackageMetadataTests.SourceRoot())!;

    private static string ReadmeDir() => Path.Combine(Root(), "package-readmes");

    private static bool IsPackable(XDocument project) =>
        !string.Equals(project.Descendants("IsPackable").FirstOrDefault()?.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>The projects that ship as packages, with the description each sets.</summary>
    private static List<(string Name, string Description)> Packages() =>
        Directory.GetDirectories(PackageMetadataTests.SourceRoot())
            .Select(d => Path.GetFileName(d))
            .Where(n => n != "Iyu.Tests")
            .Select(n => (Name: n, File: Path.Combine(PackageMetadataTests.SourceRoot(), n, n + ".csproj")))
            .Where(x => File.Exists(x.File))
            .Select(x => (x.Name, Project: XDocument.Load(x.File)))
            .Where(x => IsPackable(x.Project))
            .Select(x => (x.Name, Description: x.Project.Descendants("Description").First().Value.Trim()))
            .ToList();

    [Fact]
    public void Every_shipped_package_has_a_readme_of_its_own_and_nothing_else_is_there()
    {
        var packages = Packages();
        Assert.NotEmpty(packages);

        var missing = packages.Where(p => !File.Exists(Path.Combine(ReadmeDir(), p.Name + ".md"))).Select(p => p.Name).ToList();
        Assert.True(missing.Count == 0,
            "these packages would show the repository README instead of their own — add package-readmes/<project>.md: " + string.Join(", ", missing));

        var known = packages.Select(p => p.Name + ".md").ToHashSet(StringComparer.Ordinal);
        var stray = Directory.GetFiles(ReadmeDir()).Select(Path.GetFileName).Where(f => !known.Contains(f!)).ToList();
        Assert.True(stray.Count == 0,
            "these files in package-readmes/ belong to no shipped package (renamed, removed, or not packable): " + string.Join(", ", stray));
    }

    [Fact]
    public void A_package_readme_names_the_package_and_says_what_its_project_says()
    {
        foreach (var (name, description) in Packages())
        {
            var text = File.ReadAllText(Path.Combine(ReadmeDir(), name + ".md")).Replace("\r\n", "\n");
            Assert.StartsWith("# " + name + "\n", text);
            Assert.Contains(description, text);                       // the same words as the project, so the two cannot drift apart
            Assert.Contains("dotnet add package " + name, text);
            Assert.True(text.Split('\n').Length <= 40, name + ": a package README is a front page, not the documentation — link to the repository README instead");
        }
    }

    // A link into the repository README names a heading by its GitHub anchor; a renamed heading leaves the link on the page top.
    [Fact]
    public void Links_into_the_repository_README_name_a_heading_that_exists()
    {
        var readme = File.ReadAllText(Path.Combine(Root(), "README.md"));
        var anchors = Regex.Matches(readme, @"^#{1,6} (?<h>.+?)\s*$", RegexOptions.Multiline)
            .Select(m => Slug(m.Groups["h"].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(anchors);

        var broken = new List<string>();
        foreach (var (name, _) in Packages())
            foreach (Match link in Regex.Matches(File.ReadAllText(Path.Combine(ReadmeDir(), name + ".md")), @"iyu-framework-v5#(?<a>[^)\s]+)"))
                if (!anchors.Contains(link.Groups["a"].Value))
                    broken.Add(name + " → #" + link.Groups["a"].Value);

        Assert.True(broken.Count == 0, "package README links to a heading the repository README does not have: " + string.Join("; ", broken));
    }

    // GitHub's anchor for a heading: lower-case, markup and punctuation dropped, spaces become hyphens.
    private static string Slug(string heading)
    {
        var kept = new string(heading.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray());
        return kept.Replace(' ', '-');
    }
}
