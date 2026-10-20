using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// The release key is the axis of the whole archive (ADR-0014, ADR-0019), so its parsing is a unit
/// and not a detail of the importer.
/// </summary>
public sealed class SptVersionTests
{
    [Theory]
    [InlineData("4.0.13", new[] { 4, 0, 13, 0 })]
    [InlineData("4.1", new[] { 4, 1, 0, 0 })]
    [InlineData("4", new[] { 4, 0, 0, 0 })]
    public void A_release_key_becomes_four_numbers(string version, int[] expected)
    {
        Assert.Equal(expected, SptVersion.ParseParts(version));
    }

    [Theory]
    [InlineData("^9.9.9")]
    [InlineData(">=4.0.0 <4.1.0")]
    [InlineData("~4.0.13")]
    [InlineData("latest")]
    [InlineData("4.0.13-beta")]
    [InlineData("4.0.13.0.1")]
    [InlineData("")]
    [InlineData("  ")]
    public void A_range_or_a_label_is_not_a_release_key(string value)
    {
        Assert.Throws<FormatException>(() => SptVersion.ParseParts(value));
    }

    [Fact]
    public void The_number_form_sorts_semantically_and_the_text_does_not()
    {
        // "4.0.9" sorts after "4.0.13" as text, which is why spt_versions keeps version_parts.
        Assert.True(string.CompareOrdinal("4.0.9", "4.0.13") > 0);

        var older = SptVersion.ParseParts("4.0.9");
        var newer = SptVersion.ParseParts("4.0.13");

        Assert.True(Compare(older, newer) < 0);
    }

    [Fact]
    public void Two_keys_that_state_the_same_release_have_the_same_numbers()
    {
        Assert.Equal(SptVersion.ParseParts("4.0"), SptVersion.ParseParts("4.0.0"));
    }

    private static int Compare(int[] left, int[] right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            var order = left[index].CompareTo(right[index]);

            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }
}
