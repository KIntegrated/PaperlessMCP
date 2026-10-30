using System.Text.RegularExpressions;
using FluentAssertions;
using PaperlessMCP.Utils;
using Xunit;

namespace PaperlessMCP.Tests.Utils;

public class WildcardPatternTests
{
    private static (bool Ok, Regex? Regex, string? Error) Compile(
        string pattern, bool regexMode = false, bool ignoreCase = true)
    {
        var ok = WildcardPattern.TryCompile(pattern, regexMode, ignoreCase, out var regex, out var error);
        return (ok, regex, error);
    }

    [Fact]
    public void Wildcard_StarMatchesAnyRun()
    {
        var regex = Compile("*Insurance*").Regex;
        regex!.IsMatch("Vodafone Insurance").Should().BeTrue();
        regex.IsMatch("insurance").Should().BeTrue();
        regex.IsMatch("Tomorrow").Should().BeFalse();
    }

    [Fact]
    public void Wildcard_QuestionMatchesExactlyOneCharacter()
    {
        var regex = Compile("acme?").Regex;
        regex!.IsMatch("acmes").Should().BeTrue();
        regex.IsMatch("acme").Should().BeFalse();
        regex.IsMatch("acmexyz").Should().BeFalse();
    }

    [Fact]
    public void Wildcard_IsAnchoredAtBothEnds()
    {
        var regex = Compile("acme").Regex;
        regex!.IsMatch("acme corp").Should().BeFalse();
        regex.IsMatch("the acme").Should().BeFalse();
        regex.IsMatch("Acme").Should().BeTrue();
    }

    [Fact]
    public void Wildcard_EscapesRegexMetacharacters()
    {
        var regex = Compile("file.pdf").Regex;
        regex!.IsMatch("file.pdf").Should().BeTrue();
        regex.IsMatch("fileXpdf").Should().BeFalse();

        // '?' is one arbitrary character in glob mode — so it happens to match a
        // literal question mark too, and "Acmes" just as happily. This is the
        // tutorial-hazard difference from regex, where 'Acme?' means an optional
        // trailing 'e': the glob reading is unambiguous and never silently
        // optional.
        var literal = Compile("Acme?").Regex;
        literal!.IsMatch("Acme?").Should().BeTrue();
        literal.IsMatch("Acmes").Should().BeTrue();
        literal.IsMatch("Acme").Should().BeFalse();
        literal.IsMatch("Acmexyz").Should().BeFalse();

        // The regex hazard for contrast: '^Acme?$' under regexMode optionally
        // drops the 'e', matching neither real-world "Acmes" nor spelling intent.
        var regexHazard = Compile("^Acme?$", regexMode: true, ignoreCase: false).Regex;
        regexHazard!.IsMatch("Acm").Should().BeTrue();
    
    }

    [Fact]
    public void Wildcard_StarMatchesEmptyRun()
    {
        var regex = Compile("acme*").Regex;
        regex!.IsMatch("acme").Should().BeTrue();
        regex.IsMatch("acme corp").Should().BeTrue();
    }

    [Fact]
    public void Compile_HonoursIgnoreCaseFlag()
    {
        var insensitive = Compile("acme ignore_case_holder", regexMode: false, ignoreCase: true).Regex;
        insensitive!.Options.HasFlag(RegexOptions.IgnoreCase).Should().BeTrue();

        var sensitive = Compile("Acme", ignoreCase: false).Regex;
        sensitive!.IsMatch("acme").Should().BeFalse();
        sensitive.IsMatch("Acme").Should().BeTrue();
    }

    [Fact]
    public void RegexMode_CompilesRawPattern()
    {
        var result = Compile("^Tomorrow-2026-[0-9]+$", regexMode: true);
        result.Ok.Should().BeTrue();
        result.Regex!.IsMatch("Tomorrow-2026-05").Should().BeTrue();
        result.Regex.IsMatch("Tomorrow-2026-05-may").Should().BeFalse();
    }

    [Fact]
    public void RegexMode_RejectsInvalidPatternWithActionableError()
    {
        var (ok, _, error) = Compile("([invalid", regexMode: true);
        ok.Should().BeFalse();
        error.Should().NotBeNull().And.Contain("Invalid regex pattern");
    }

    [Fact]
    public void Compile_RejectsEmptyPattern()
    {
        Compile("   ").Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("*insurance*", "insurance")]
    [InlineData("acme*", "acme")]
    [InlineData("tom?orrow-2026-*", "orrow-2026-")]
    [InlineData("a*b*cdef", "cdef")]
    public void GetLiteralPrefilter_ReturnsLongestChunk(string pattern, string expected)
    {
        WildcardPattern.GetLiteralPrefilter(pattern).Should().Be(expected);
    }

    [Theory]
    [InlineData("ac*")] // chunks shorter than 3 characters gain nothing
    [InlineData("*")]
    [InlineData("")]
    public void GetLiteralPrefilter_ReturnsNullWhenNotWorthIt(string pattern)
    {
        WildcardPattern.GetLiteralPrefilter(pattern).Should().BeNull();
    }
}
