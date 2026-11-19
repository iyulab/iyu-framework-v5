using System.Text.Json.Nodes;
using Xunit;

namespace Iyu.Tests.UBoard;

/// <summary>
/// The board screen ships inside the package, so every npm package it is built from ships with it: its licence must be a permissive
/// one and its notice must travel. These read the lockfile and the allow-list beside it - no network, no Node.js - and stop a change
/// that would put a package under another licence into the screen.
/// </summary>
public sealed class ScreenLicensesTests
{
    // What may never be added to the allow-list: a copyleft or source-available or non-commercial licence in the list would let a
    // change through that the screen's packaging promise rules out. Matched on the SPDX id's start.
    private static readonly string[] NeverPermissive = ["GPL", "AGPL", "LGPL", "SSPL", "BUSL", "CC-BY-NC", "CC-BY-SA", "EUPL", "MPL", "CPAL", "OSL", "Commons-Clause", "Elastic", "Proprietary", "UNLICENSED"];

    private static string WebDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var web = Path.Combine(directory.FullName, "src", "Iyu.UBoard", "web");
            if (File.Exists(Path.Combine(web, "package-lock.json"))) return web;
        }

        throw new InvalidOperationException("src/Iyu.UBoard/web/package-lock.json was not found above the test output.");
    }

    private static JsonNode Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(WebDirectory(), name)))!;

    private static string[] Allowed() => Read("licenses.json")["allowed"]!.AsArray().Select(node => (string)node!).ToArray();

    /// <summary>Every AND side allowed and one OR side allowed; no licence, or one not named, is not allowed.</summary>
    internal static bool IsAllowed(string? expression, IReadOnlyCollection<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var clean = expression.Replace("(", " ", StringComparison.Ordinal).Replace(")", " ", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Split(clean, @"\s+OR\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Any(side => System.Text.RegularExpressions.Regex.Split(side, @"\s+AND\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .All(id => allowed.Contains(id.Trim())));
    }

    // The packages that ship: the lockfile's, except the root and what only the build tools need.
    private static IEnumerable<(string Name, string? Version, string? License)> Shipped() =>
        Read("package-lock.json")["packages"]!.AsObject()
            .Where(entry => entry.Key.Length > 0 && entry.Value!["dev"]?.GetValue<bool>() != true)
            .Select(entry => (
                Name: entry.Key[(entry.Key.LastIndexOf("node_modules/", StringComparison.Ordinal) + "node_modules/".Length)..],
                Version: (string?)entry.Value!["version"],
                License: entry.Value["license"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null));

    [Fact]
    public void Every_package_the_screen_ships_is_under_a_licence_on_the_allow_list()
    {
        var allowed = Allowed();

        var outside = Shipped().Where(pkg => !IsAllowed(pkg.License, allowed)).Select(pkg => $"{pkg.Name}@{pkg.Version}: {pkg.License ?? "(none)"}").ToList();

        Assert.True(outside.Count == 0, "These packages are outside the licence allow-list (web/licenses.json):\n  " + string.Join("\n  ", outside));
    }

    [Fact]
    public void The_allow_list_holds_only_permissive_licences()
    {
        var allowed = Allowed();

        Assert.NotEmpty(allowed);
        foreach (var id in allowed)
            Assert.DoesNotContain(NeverPermissive, never => id.StartsWith(never, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_package_under_a_licence_off_the_list_or_under_none_is_not_allowed_and_an_OR_needs_one_side()
    {
        string[] allowed = ["MIT", "ISC"];

        Assert.True(IsAllowed("MIT", allowed));
        Assert.True(IsAllowed("(MIT OR GPL-3.0-only)", allowed));
        Assert.False(IsAllowed("MIT AND GPL-3.0-only", allowed));
        Assert.False(IsAllowed("GPL-3.0-only", allowed));
        Assert.False(IsAllowed(null, allowed));
        Assert.False(IsAllowed("", allowed));
        Assert.False(IsAllowed("SEE LICENSE IN LICENSE.txt", allowed));
    }

    [Fact]
    public void The_font_whose_licence_asks_for_its_notice_is_a_shipped_package_and_the_list_names_its_licence()
    {
        Assert.Contains(Shipped(), pkg => pkg.Name == "pretendard" && pkg.License == "OFL-1.1");
        Assert.Contains("OFL-1.1", Allowed());
    }

    [Fact]
    public void Every_direct_dependency_of_the_screen_is_pinned_exactly_and_the_lockfile_holds_that_version()
    {
        var direct = Read("package.json")["dependencies"]!.AsObject();
        var locked = Shipped().ToLookup(pkg => pkg.Name, pkg => pkg.Version); // a name may be installed at more than one version

        // The four the screen is built on are named, so dropping one is not a quiet way to pass.
        foreach (var name in new[] { "@iyulab/u-board", "@iyulab/chat-components", "@iyulab/components", "@iyulab/house-style" })
            Assert.True(direct.ContainsKey(name), name);
        foreach (var (name, pin) in direct)
        {
            Assert.Matches(@"^\d+\.\d+\.\d+$", (string)pin!); // an exact version, not a range
            Assert.Equal([(string)pin!], locked[name]);
        }
    }
}
