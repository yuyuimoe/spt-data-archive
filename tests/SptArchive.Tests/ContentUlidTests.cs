using SptArchive.Core.Canonical;

namespace SptArchive.Tests;

/// <summary>
/// The name of a derived object (ADR-0034, decision 2): 26 Crockford characters, no time, and 80 bits
/// of the SHA-256 of a seed. The vectors come from an independent implementation of the same rule, so
/// a change to the encoding fails here.
/// </summary>
public sealed class ContentUlidTests
{
    [Theory]
    [InlineData("item", "Item D", "0000000000FWCJN7GG06XCRADM")]
    [InlineData("item", "anpas29b_coti_clip_on_thermal_imager", "0000000000B8B42DN01WHR46V6")]
    [InlineData("quest", "Gunsmith - Abakan", "00000000008P3AA05KY7YZB2SQ")]
    public void A_name_is_the_hash_of_its_seed_with_no_time(string kind, string identity, string expected)
    {
        Assert.Equal("sptarchive:v1|" + kind + "|" + identity, ContentUlid.Seed(kind, [identity]));
        Assert.Equal(expected, ContentUlid.Create(kind, identity));
        Assert.True(ContentUlid.IsContentUlid(expected));
    }

    [Fact]
    public void One_seed_gives_one_name_and_one_name_only()
    {
        var first = ContentUlid.Create("item", "Item D");

        Assert.Equal(first, ContentUlid.Create("item", "Item D"));
        Assert.NotEqual(first, ContentUlid.Create("item", "item d"));
        Assert.NotEqual(first, ContentUlid.Create("quest", "Item D"));
        Assert.Equal(ContentUlid.TextLength, first.Length);
    }

    [Fact]
    public void A_field_that_holds_the_separator_cannot_fake_two_fields()
    {
        Assert.NotEqual(
            ContentUlid.Seed("item", ["a|b"]),
            ContentUlid.Seed("item", ["a", "b"]));

        Assert.NotEqual(
            ContentUlid.Create("item", "a\\|b"),
            ContentUlid.Create("item", "a|b"));
    }

    [Fact]
    public void A_name_survives_the_uuid_column()
    {
        var name = ContentUlid.Create("item", "Item D");
        var stored = ContentUlid.ToGuid(name);

        Assert.Equal("00000000-0000-7f19-2a9e-1001bacc29b4", stored.ToString());
        Assert.Equal(name, ContentUlid.FromGuid(stored));
    }

    [Fact]
    public void A_uuid_that_holds_a_time_is_refused()
    {
        // A ULID of a real clock holds a time in its first six bytes. The archive never states a time.
        var timed = Guid.Parse("0192f2a0-0000-7000-8000-000000000000");

        Assert.Throws<ArgumentException>(() => ContentUlid.FromGuid(timed));
        Assert.False(ContentUlid.IsContentUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV"));
        Assert.Throws<ArgumentException>(() => ContentUlid.ToGuid("01ARZ3NDEKTSV4RRFFQ69G5FAV"));
    }

    [Fact]
    public void Text_that_is_not_a_name_is_refused()
    {
        Assert.False(ContentUlid.IsContentUlid(null));
        Assert.False(ContentUlid.IsContentUlid("0000000000FWCJN7GG06XCRAD"));
        Assert.False(ContentUlid.IsContentUlid("0000000000FWCJN7GG06XCRADU"));
        Assert.Throws<ArgumentException>(() => ContentUlid.ToGuid("not a ulid"));
    }
}
