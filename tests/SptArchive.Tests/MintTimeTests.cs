using SptArchive.Core.Canonical;

namespace SptArchive.Tests;

/// <summary>
/// An id that was minted while the run ran earns a warning (ADR-0034, decision 9). A hand-written id
/// that only looks like a MongoId does not.
/// </summary>
public sealed class MintTimeTests
{
    private static readonly DateTimeOffset RunStarted = new(2026, 10, 20, 19, 12, 0, TimeSpan.Zero);

    [Fact]
    public void A_mongo_id_holds_its_creation_time_in_the_first_four_bytes()
    {
        // 0x6a7f1538 = 1786000696 seconds after the epoch.
        var time = MintTime.CreatedAt(IdMinted(RunStarted.AddMinutes(1)));

        Assert.Equal(RunStarted.AddMinutes(1), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5448bd6b4bdc2dfc2f8b456")]    // 23 characters
    [InlineData("5448BD6B4BDC2DFC2F8B4569")]    // upper case is not how the server writes an id
    [InlineData("hello-world-hello-world")]
    [InlineData("0000000000000000000000ab")]    // time 0 is 1970
    public void An_id_that_is_not_a_mongo_id_holds_no_time(string rawId)
    {
        Assert.Null(MintTime.CreatedAt(rawId));
    }

    [Fact]
    public void An_id_minted_after_the_run_started_warns()
    {
        var id = IdMinted(RunStarted.AddMinutes(1));

        var warnings = MintTime.Warnings([Entry(id, pinned: false)], RunStarted);

        var warning = Assert.Single(warnings);
        Assert.Contains(id, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_minted_before_the_run_started_does_not_warn()
    {
        var warnings = MintTime.Warnings([Entry(IdMinted(RunStarted.AddHours(-3)), pinned: false)], RunStarted);

        Assert.Empty(warnings);
    }

    [Fact]
    public void A_pinned_id_does_not_warn_because_the_baseline_already_held_it()
    {
        var warnings = MintTime.Warnings([Entry(IdMinted(RunStarted.AddMinutes(1)), pinned: true)], RunStarted);

        Assert.Empty(warnings);
    }

    [Fact]
    public void A_hand_written_id_that_decodes_to_a_far_future_date_does_not_warn()
    {
        // WTT-Armory writes 53 ids by hand. Some decode to a date years away, and no run lasts that long.
        var warnings = MintTime.Warnings([Entry(IdMinted(RunStarted.AddYears(3)), pinned: false)], RunStarted);

        Assert.Empty(warnings);
    }

    internal static string IdMinted(DateTimeOffset time) =>
        time.ToUnixTimeSeconds().ToString("x8") + "0123456789abcdef";

    private static Addressing.AddressEntry Entry(string rawId, bool pinned) =>
        new("item", rawId, pinned ? rawId : "0000000000TESTADDRESS000000", pinned);
}
