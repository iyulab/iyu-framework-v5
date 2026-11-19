using System.Text.RegularExpressions;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// Every <c>Iyu.*</c> package the README's «What comes with the stack» section names is a project in this repository.
/// </summary>
/// <remarks>
/// The section tells a reader which package provides what. A package that is renamed or removed leaves
/// the sentence pointing at something a reader cannot add, and nothing else would notice: the section is prose.
/// Only whole code spans that start with <c>Iyu.</c> are read, so a namespace or a type name written there is not
/// mistaken for a package. The README is read from the copy <c>Iyu.Tests.csproj</c> places in the output folder, for the
/// reason <see cref="PublishedStatusTests"/> gives.
/// </remarks>
public class ReadmeStackContentsTests
{
    private const string SectionHeading = "## What comes with the stack";

    private static string Section()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "guidance", "README.md");
        Assert.True(File.Exists(path), $"'{path}' was not found; it is copied there by Iyu.Tests.csproj.");
        var text = File.ReadAllText(path);

        var start = text.IndexOf(SectionHeading, StringComparison.Ordinal);
        Assert.True(start >= 0,
            $"the README has no '{SectionHeading}' section. If it was retitled, update this test; " +
            "if it was removed, the list of what the stack provides is gone.");

        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    [Fact]
    public void Every_package_the_section_names_is_a_project_in_the_repository()
    {
        var named = Regex.Matches(Section(), @"`(?<name>Iyu\.[A-Za-z0-9.]+)`")
            .Select(m => m.Groups["name"].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(named);

        var src = PackageMetadataTests.SourceRoot();
        var missing = named
            .Where(name => !File.Exists(Path.Combine(src, name, name + ".csproj")))
            .ToList();

        Assert.True(missing.Count == 0,
            "the section names packages that are not projects under src: " + string.Join(", ", missing));
    }
}
