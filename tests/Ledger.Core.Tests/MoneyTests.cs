namespace Ledger.Core.Tests;

public class MoneyTests
{
    [Fact]
    public void Adding_cents_is_exact() => Assert.Equal(4990, Money.Sum([3790, 1200]));

    [Fact]
    public void Summing_many_small_amounts_does_not_drift()
    {
        // 0.1 + 0.2 style errors cannot occur with integer cents.
        Assert.Equal(100_000, Money.Sum(Enumerable.Repeat(1L, 100_000)));
    }

    [Fact]
    public void Sum_throws_on_overflow_instead_of_wrapping() =>
        Assert.Throws<OverflowException>(() => Money.Sum([long.MaxValue, 1]));

    [Theory]
    [InlineData(3790, "$37.90")]
    [InlineData(100, "$1.00")]
    [InlineData(1, "$0.01")]
    [InlineData(0, "$0.00")]
    [InlineData(-12000, "-$120.00")]
    [InlineData(123456789, "$1,234,567.89")]
    public void Formats_cents(long cents, string expected) => Assert.Equal(expected, Money.Format(cents));

    [Theory]
    [InlineData(18472, "+$184.72")]
    [InlineData(-3790, "-$37.90")]
    [InlineData(0, "$0.00")]
    public void Formats_with_explicit_sign(long cents, string expected) =>
        Assert.Equal(expected, Money.Format(cents, MoneySign.Always));

    [Fact]
    public void Formats_extreme_values_without_overflow()
    {
        Assert.StartsWith("-$", Money.Format(long.MinValue));
        Assert.StartsWith("$", Money.Format(long.MaxValue));
    }

    [Theory]
    [InlineData(120000, "$1,200")]
    [InlineData(8700, "$87")]
    [InlineData(3790, "$37.90")]
    public void Compact_format_drops_zero_cents(long cents, string expected) =>
        Assert.Equal(expected, Money.FormatCompact(cents));

    [Theory]
    [InlineData(124000, "1240.00")]
    [InlineData(-525, "-5.25")]
    [InlineData(0, "0.00")]
    public void Formats_for_editing_without_symbol_or_grouping(long cents, string expected)
    {
        Assert.Equal(expected, Money.FormatForEditing(cents));
        Assert.True(Money.TryParse(Money.FormatForEditing(cents), out var parsed));
        Assert.Equal(cents, parsed);
    }

    [Theory]
    [InlineData("37.90", 3790)]
    [InlineData("37.9", 3790)]
    [InlineData("$1,234.56", 123456)]
    [InlineData(" 5 ", 500)]
    [InlineData("-5.25", -525)]
    [InlineData("0.01", 1)]
    public void Parses_user_input(string text, long expected)
    {
        Assert.True(Money.TryParse(text, out var cents));
        Assert.Equal(expected, cents);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1.234")] // sub-cent precision is rejected, never rounded
    [InlineData("99999999999999999999")]
    public void Rejects_invalid_input(string? text) => Assert.False(Money.TryParse(text, out _));
}
