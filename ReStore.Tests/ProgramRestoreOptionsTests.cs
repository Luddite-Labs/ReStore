using FluentAssertions;
using ReStore.Core;
using ReStore.Core.src.core;
using System.Reflection;

namespace ReStore.Tests;

/// <summary>Covers the restore command's --include glob matching and --conflict parsing.</summary>
public class ProgramRestoreOptionsTests
{
    [Theory]
    [InlineData("docs/report.txt", "docs/**", true)]
    [InlineData("docs/nested/deep/report.txt", "docs/**", true)]
    [InlineData("docs", "docs/**", true)]
    [InlineData("other/report.txt", "docs/**", false)]
    [InlineData("report.txt", "*.txt", true)]
    [InlineData("docs/report.txt", "*.txt", false)]
    [InlineData("docs/report.txt", "docs/*.txt", true)]
    [InlineData("docs/nested/report.txt", "docs/*.txt", false)]
    [InlineData("notes.md", "notes.??", true)]
    [InlineData("notes.mdx", "notes.??", false)]
    [InlineData("Docs/Report.TXT", "docs/**", true)]
    [InlineData("anything", "**", true)]
    [InlineData("docs/report.txt", "docs/**/report.txt", true)]
    [InlineData("docs/nested/report.txt", "docs/**/report.txt", true)]
    [InlineData("docs/report.md", "docs/**/report.txt", false)]
    public void MatchesGlob_ShouldHonourSeparatorSemantics(string relativePath, string pattern, bool expected)
    {
        Program.MatchesGlob(relativePath, pattern).Should().Be(expected);
    }

    [Fact]
    public void MatchesGlob_ShouldNotMatch_WhenPatternIsBlank()
    {
        Program.MatchesGlob("docs/report.txt", "  ").Should().BeFalse();
    }

    [Fact]
    public void MatchesGlob_ShouldTreatBackslashesAsSeparators()
    {
        // Manifests store '/' but a user may type a Windows-style pattern.
        Program.MatchesGlob("docs/report.txt", @"docs\*.txt").Should().BeTrue();
    }

    [Theory]
    [InlineData("skip", RestoreConflictPolicy.Skip)]
    [InlineData("overwrite", RestoreConflictPolicy.Overwrite)]
    [InlineData("keepboth", RestoreConflictPolicy.KeepBoth)]
    [InlineData("fail", RestoreConflictPolicy.Fail)]
    [InlineData("OVERWRITE", RestoreConflictPolicy.Overwrite)]
    public void TryParseConflictPolicy_ShouldAcceptKnownValues(string value, RestoreConflictPolicy expected)
    {
        var arguments = new[] { "restore", "src", "dst", "--conflict", value };

        var (parsed, policy) = InvokeTryParseConflictPolicy(arguments);

        parsed.Should().BeTrue();
        policy.Should().Be(expected);
    }

    [Fact]
    public void TryParseConflictPolicy_ShouldDefaultToSkip_WhenFlagAbsent()
    {
        var (parsed, policy) = InvokeTryParseConflictPolicy(["restore", "src", "dst"]);

        parsed.Should().BeTrue();
        policy.Should().Be(RestoreConflictPolicy.Skip,
            "the safest default must not silently replace a user's existing files");
    }

    [Fact]
    public void TryParseConflictPolicy_ShouldFail_ForUnknownValue()
    {
        var (parsed, _) = InvokeTryParseConflictPolicy(["restore", "src", "dst", "--conflict", "clobber"]);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void TryParseConflictPolicy_ShouldFail_WhenFlagHasNoValue()
    {
        var (parsed, _) = InvokeTryParseConflictPolicy(["restore", "src", "dst", "--conflict"]);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void GetRepeatedOptionValues_ShouldCollectEveryInclude()
    {
        var arguments = new[] { "restore", "src", "dst", "--include", "docs/**", "--include", "*.txt", "--dry-run" };

        var values = InvokeGetRepeatedOptionValues(arguments, "--include");

        values.Should().BeEquivalentTo(["docs/**", "*.txt"]);
    }

    [Fact]
    public void GetRepeatedOptionValues_ShouldIgnoreFlagWithoutValue()
    {
        var arguments = new[] { "restore", "src", "dst", "--include", "--dry-run" };

        InvokeGetRepeatedOptionValues(arguments, "--include").Should().BeEmpty();
    }

    private static (bool Parsed, RestoreConflictPolicy Policy) InvokeTryParseConflictPolicy(string[] arguments)
    {
        var method = typeof(Program).GetMethod(
            "TryParseConflictPolicy",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var parameters = new object?[] { arguments, null };
        var parsed = (bool)method.Invoke(null, parameters)!;

        return (parsed, (RestoreConflictPolicy)parameters[1]!);
    }

    private static List<string> InvokeGetRepeatedOptionValues(string[] arguments, string optionName)
    {
        var method = typeof(Program).GetMethod(
            "GetRepeatedOptionValues",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        return (List<string>)method.Invoke(null, [arguments, optionName])!;
    }
}
