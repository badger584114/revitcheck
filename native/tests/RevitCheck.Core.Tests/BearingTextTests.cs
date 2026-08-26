using RevitCheck.Core.Checks;
using Xunit;

namespace RevitCheck.Core.Tests;

public class BearingTextTests
{
    [Fact]
    public void Parses_the_real_confirmed_format()
    {
        // Real confirmed format, PLANNING.md §14, InspectPileSetout.pushbutton's
        // real run: literal degree/minute/second symbols, no label.
        var degrees = BearingText.TryParseDegrees("165° 07' 01\"");

        Assert.NotNull(degrees);
        Assert.Equal(165 + (7.0 / 60) + (1.0 / 3600), degrees!.Value, 9);
    }

    [Fact]
    public void Trailing_carriage_return_does_not_block_the_match()
    {
        // Real TextNote.Text on this project ends with \r - confirmed
        // 2026-08-26.
        var degrees = BearingText.TryParseDegrees("165° 13' 26\"\r");

        Assert.NotNull(degrees);
        Assert.Equal(165 + (13.0 / 60) + (26.0 / 3600), degrees!.Value, 9);
    }

    [Fact]
    public void Curly_prime_and_quote_characters_are_accepted()
    {
        var degrees = BearingText.TryParseDegrees("161° 22′ 41″");

        Assert.NotNull(degrees);
        Assert.Equal(161 + (22.0 / 60) + (41.0 / 3600), degrees!.Value, 9);
    }

    [Fact]
    public void Fractional_seconds_are_accepted()
    {
        var degrees = BearingText.TryParseDegrees("90° 00' 30.5\"");

        Assert.NotNull(degrees);
        Assert.Equal(90 + (30.5 / 3600), degrees!.Value, 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("EVERARD AVENUE EASTBOUND TRAFFIC LANE VARIES")]
    [InlineData("300 MIN.")]
    public void Non_bearing_text_returns_null(string? text)
    {
        Assert.Null(BearingText.TryParseDegrees(text));
    }
}
