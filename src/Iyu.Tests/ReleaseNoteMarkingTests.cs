using Xunit;

namespace Iyu.Tests;

/// <summary>
/// The changelog distinguishes a change the compiler refuses from one it does not, and
/// keeps doing so.
/// </summary>
/// <remarks>
/// <para>
/// Every release here bumps all ten packages, so a reader upgrades across entries they did
/// not go looking for. Among those entries, one kind is self-announcing — a signature or a
/// record's shape stops their build — and one kind is not: what a request answers changes,
/// nothing refuses to compile, and the difference surfaces in production. The changelog used
/// to set both in the same weight, and one release went out asserting all three of its
/// breaking changes were silent when two of them stopped the build.
/// </para>
/// <para>
/// A convention that lives only in prose is removed by the next person who tidies the file,
/// and nothing says so. These two tests are the whole gate: the rule has to be published,
/// and it has to be in use. Neither checks that a given entry is marked correctly — that is a
/// judgment, and pretending to machine-check it would put a green tick on an unread decision.
/// </para>
/// </remarks>
public sealed class ReleaseNoteMarkingTests
{
    /// <summary>The mark itself, exactly as an entry carries it.</summary>
    private const string Mark = "🔇 no build-time signal";

    /// <summary>The sentence that says what the mark means and how it is decided.</summary>
    private const string RuleSentence = "does the compiler refuse the old code?";

    private static string ChangelogText()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "guidance", "CHANGELOG.md");
        Assert.True(File.Exists(path),
            $"the published changelog was not found at '{path}'. It is copied there by " +
            "Iyu.Tests.csproj; if the file moved, update that copy rule. This test asserts " +
            "nothing while its subject is missing.");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The reader has to be told what the mark means, or it is decoration.
    /// </summary>
    [Fact]
    public void The_changelog_explains_the_mark_and_the_test_that_decides_it()
    {
        var text = ChangelogText();

        Assert.Contains(Mark, text, StringComparison.Ordinal);
        Assert.Contains(RuleSentence, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ...and it has to be applied. A legend nobody uses reads as a rule that was dropped,
    /// which is worse than never having stated one.
    /// </summary>
    [Fact]
    public void At_least_one_entry_carries_the_mark()
    {
        var text = ChangelogText();

        var uses = 0;
        var at = text.IndexOf(Mark, StringComparison.Ordinal);
        while (at >= 0)
        {
            uses++;
            at = text.IndexOf(Mark, at + Mark.Length, StringComparison.Ordinal);
        }

        Assert.True(uses >= 2,
            $"the mark appears {uses} time(s): once for the explanation and at least once on " +
            "an entry. Fewer means the convention is stated and unused, or gone.");
    }
}
