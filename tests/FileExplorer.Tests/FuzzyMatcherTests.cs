using FileExplorer.Helpers;

namespace FileExplorer.Tests;

public class FuzzyMatcherTests
{
    [Fact]
    public void TryScore_EmptyQuery_AlwaysMatchesWithZeroScore()
    {
        var matched = FuzzyMatcher.TryScore("anything.txt", "", out var score);

        Assert.True(matched);
        Assert.Equal(0, score);
    }

    [Fact]
    public void TryScore_EmptyText_NonEmptyQuery_DoesNotMatch()
    {
        Assert.False(FuzzyMatcher.TryScore("", "abc", out _));
    }

    [Fact]
    public void TryScore_ExactSubstring_Matches()
    {
        Assert.True(FuzzyMatcher.TryScore("readme.txt", "read", out var score));
        Assert.True(score > 0);
    }

    [Fact]
    public void TryScore_ExactSubstring_IsCaseInsensitive()
    {
        Assert.True(FuzzyMatcher.TryScore("README.txt", "read", out _));
    }

    [Fact]
    public void TryScore_SubstringAtStart_ScoresHigherThanSubstringLaterInString()
    {
        FuzzyMatcher.TryScore("read-report.txt", "read", out var earlyScore);
        FuzzyMatcher.TryScore("my-read-notes.txt", "read", out var lateScore);

        Assert.True(earlyScore > lateScore);
    }

    [Fact]
    public void TryScore_SubsequenceMatch_MatchesOutOfOrderTypos()
    {
        // "rdme" is an in-order subsequence of "readme.txt" (r-e-a-d-m-e), just missing letters -
        // this is the typo-tolerant fallback path, not an exact substring.
        Assert.True(FuzzyMatcher.TryScore("readme.txt", "rdme", out var score));
        Assert.True(score > 0);
    }

    [Fact]
    public void TryScore_SubsequenceOutOfOrder_DoesNotMatch()
    {
        // "emdr" is not an in-order subsequence of "readme.txt".
        Assert.False(FuzzyMatcher.TryScore("readme.txt", "emdr", out _));
    }

    [Fact]
    public void TryScore_QueryLongerThanText_DoesNotMatch()
    {
        Assert.False(FuzzyMatcher.TryScore("abc", "abcdef", out _));
    }

    [Fact]
    public void TryScore_SubsequenceWithConsecutiveRuns_ScoresHigherThanFullyScattered()
    {
        // Neither is an exact substring match for "abcd", so both go through the subsequence
        // fallback - but "abXcd" contains two adjacent matched pairs (a-b, c-d), earning a
        // consecutive-run bonus that a fully scattered match (one char match per gap) never gets.
        FuzzyMatcher.TryScore("abXcd", "abcd", out var runScore);
        FuzzyMatcher.TryScore("aXbXcXdX", "abcd", out var scatteredScore);

        Assert.True(runScore > scatteredScore);
    }
}
