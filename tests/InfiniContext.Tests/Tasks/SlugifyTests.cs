using InfiniContext.Core.Tasks;
using FluentAssertions;

namespace InfiniContext.Tests.Tasks;

public class SlugifyTests
{
    [Theory]
    [InlineData("Implement Phase 2", "implement-phase-2")]
    [InlineData("Foo: bar (baz)?", "foo-bar-baz")]
    [InlineData("foo   --  bar", "foo-bar")]
    [InlineData("___leading_underscore", "leading-underscore")]
    [InlineData("MixedCase Title", "mixedcase-title")]
    [InlineData("trailing-hyphens---", "trailing-hyphens")]
    [InlineData("...just punctuation? !!", "just-punctuation")]
    [InlineData("ASCII-already-kebab", "ascii-already-kebab")]
    [InlineData("digits 123 in title", "digits-123-in-title")]
    public void ASCII_inputs_produce_expected_slugs(string input, string expected)
    {
        Slugify.For(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Café au lait", "cafe-au-lait")]      // diacritics stripped via NFD
    [InlineData("naïve résumé", "naive-resume")]
    public void Diacritics_are_stripped_to_ASCII(string input, string expected)
    {
        Slugify.For(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("你好 hello world", "hello-world")]   // non-ASCII letters drop
    [InlineData("こんにちは", "task")]                  // all non-ASCII → fallback
    public void NonAscii_letters_collapse_to_hyphens(string input, string expected)
    {
        Slugify.For(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("???")]
    [InlineData("---")]
    public void Empty_or_no_alnum_input_returns_fallback(string input)
    {
        Slugify.For(input).Should().Be(Slugify.Fallback);
    }

    [Fact]
    public void Long_titles_are_truncated_at_word_boundary()
    {
        // 10 segments of 10 chars + 9 hyphens = 109 chars. Default cap is 60.
        var input = string.Join("-", Enumerable.Range(0, 10).Select(i => $"segment{i:D2}"));

        var slug = Slugify.For(input);

        slug.Length.Should().BeLessOrEqualTo(60);
        slug.Should().NotEndWith("-");
        slug.Should().StartWith("segment00");
    }

    [Fact]
    public void Custom_max_length_is_honoured()
    {
        Slugify.For("alpha bravo charlie delta", maxLength: 11).Should().Be("alpha-bravo");
    }

    [Fact]
    public void MaxLength_below_4_throws()
    {
        var act = () => Slugify.For("anything", maxLength: 3);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
