using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// Compiles code the README publishes against these packages — the README read from the test output, where
/// <c>Iyu.Tests.csproj</c> copies it, never from a copy kept in test code.
/// </summary>
internal static class ReadmeCompilation
{
    public static string ReadmeText()
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
    public static IReadOnlyList<string> PublishedGlobalUsings()
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
    /// <c>System.Runtime</c> and the ASP.NET Core assemblies the code needs.
    /// </summary>
    public static IReadOnlyList<MetadataReference> ReferenceSet()
    {
        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        // One reference per assembly name: the same assembly reachable by two paths is
        // CS1703, which would read as a defect in the README rather than in this list.
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

    /// <summary>The errors compiling <paramref name="source"/> as a library produces.</summary>
    public static IReadOnlyList<Diagnostic> Errors(string assemblyName, string source)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            ReferenceSet(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }
}
