using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SptArchive.Core.Canonical;

/// <summary>
/// The name of an object that a run made (ADR-0034, decision 2). An id that a run gives an object is
/// not a fact about the object: a mod can ask the server for a new one on every boot. The archive
/// derives a name from the identity of the object instead, so one object keeps one name in every run.
/// </summary>
/// <remarks>
/// A ULID holds 128 bits: 48 bits of time and 80 bits of value, written as 26 Crockford characters.
/// This one keeps the time half at zero, because the archive does not know when an object was born and
/// must not state a time it did not measure. The 80 bits are the first 10 bytes of the SHA-256 of the
/// seed, so one seed gives one name in every run, in every process, on every machine.
/// </remarks>
public static class ContentUlid
{
    /// <summary>The first part of every seed. A later version of the rule writes a different one.</summary>
    public const string SeedVersion = "sptarchive:v1";

    /// <summary>Crockford base32, the alphabet of a ULID. It leaves out I, L, O and U.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>26 characters hold 130 bits; the first 2 are padding and stay zero.</summary>
    public const int TextLength = 26;

    /// <summary>The 48 bits of time and the 2 bits of padding, as ten zero characters.</summary>
    private const int TimeCharacters = 10;

    /// <summary>The 48 bits of time, as six bytes of the <see cref="Guid"/>.</summary>
    private const int TimeBytes = 6;

    /// <summary>80 bits of value, the width of the random half of a ULID.</summary>
    private const int ValueBytes = 10;

    /// <summary>The name of a derived object, from its kind and the fields that identify it.</summary>
    public static string Create(string kind, params string[] identityFields) => Create(kind, (IEnumerable<string>)identityFields);

    /// <summary>The name of a derived object, from its kind and the fields that identify it.</summary>
    public static string Create(string kind, IEnumerable<string> identityFields) =>
        FromSeed(Seed(kind, identityFields));

    /// <summary>
    /// The name from a whole seed. The address of a tied object extends the seed with content and with
    /// order (ADR-0034, decision 4), so the seed is built in parts and hashed once, at the end.
    /// </summary>
    public static string FromSeed(string seed) =>
        Encode(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, ValueBytes));

    /// <summary>
    /// The text a name is derived from: the version, the kind and the identity fields (ADR-0034,
    /// decision 3). A field is escaped, so two lists of fields cannot produce one seed.
    /// </summary>
    public static string Seed(string kind, IEnumerable<string> identityFields)
    {
        var builder = new StringBuilder(SeedVersion);
        builder.Append('|').Append(Escape(kind));

        foreach (var field in identityFields)
        {
            builder.Append('|').Append(Escape(field));
        }

        return builder.ToString();
    }

    /// <summary>The 128 bits as a <see cref="Guid"/>, for a <c>uuid</c> column. The time half is the six zero bytes.</summary>
    public static Guid ToGuid(string ulid)
    {
        var bytes = ToBytes(ulid);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>The text of a name, from the <see cref="Guid"/> the database holds.</summary>
    public static string FromGuid(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);

        // Six bytes of time, then the value: the value starts at the seventh byte.
        for (var index = 0; index < TimeBytes; index++)
        {
            if (bytes[index] != 0)
            {
                throw new ArgumentException(
                    $"{value} holds a time. A content ULID of {SeedVersion} keeps the time half at zero.",
                    nameof(value));
            }
        }

        return Encode(bytes.AsSpan(TimeBytes, ValueBytes));
    }

    /// <summary>True when the text is a content ULID of this version: 26 characters, and no time.</summary>
    public static bool IsContentUlid(string? text)
    {
        if (text is null || text.Length != TextLength)
        {
            return false;
        }

        for (var index = 0; index < TimeCharacters; index++)
        {
            if (!TryRank(text[index], out var rank) || rank != 0)
            {
                return false;
            }
        }

        for (var index = TimeCharacters; index < TextLength; index++)
        {
            if (!TryRank(text[index], out _))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The 16 bytes of the name, most significant first, for a <c>uuid</c> column.</summary>
    internal static byte[] ToBytes(string ulid)
    {
        ArgumentNullException.ThrowIfNull(ulid);

        if (ulid.Length != TextLength)
        {
            throw new ArgumentException($"A ULID holds {TextLength} characters, not {ulid.Length}: '{ulid}'.", nameof(ulid));
        }

        var bytes = new byte[16];
        var bit = 48;

        for (var index = 0; index < TimeCharacters; index++)
        {
            if (Rank(ulid[index]) != 0)
            {
                throw new ArgumentException(
                    $"'{ulid}' holds a time. A content ULID of {SeedVersion} keeps the time half at zero.",
                    nameof(ulid));
            }
        }

        for (var index = TimeCharacters; index < TextLength; index++)
        {
            var value = Rank(ulid[index]);

            for (var position = 4; position >= 0; position--)
            {
                if (((value >> position) & 1) == 1)
                {
                    bytes[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
                }

                bit++;
            }
        }

        return bytes;
    }

    /// <summary>Ten bytes become the sixteen characters of the value half.</summary>
    private static string Encode(ReadOnlySpan<byte> value)
    {
        var text = new char[TextLength];

        for (var index = 0; index < TimeCharacters; index++)
        {
            text[index] = '0';
        }

        var bit = 0;

        for (var index = TimeCharacters; index < TextLength; index++)
        {
            var rank = 0;

            for (var position = 0; position < 5; position++)
            {
                rank = (rank << 1) | ((value[bit >> 3] >> (7 - (bit & 7))) & 1);
                bit++;
            }

            text[index] = Alphabet[rank];
        }

        return new string(text);
    }

    /// <summary>The value of one character. Lower case and the look-alikes of Crockford are read too.</summary>
    private static int Rank(char character) => TryRank(character, out var rank)
        ? rank
        : throw new ArgumentException($"'{character}' is not a Crockford base32 character.", nameof(character));

    /// <summary>The value of one character, or false when the alphabet holds no such character.</summary>
    private static bool TryRank(char character, out int rank)
    {
        if (character is >= '0' and <= '9')
        {
            rank = character - '0';
            return true;
        }

        var offset = character switch
        {
            >= 'A' and <= 'Z' => character - 'A',
            >= 'a' and <= 'z' => character - 'a',
            _ => -1,
        };

        if (offset < 0)
        {
            rank = -1;
            return false;
        }

        rank = offset switch
        {
            <= 7 => offset + 10, // A to H
            8 => 1, // I reads as 1
            9 or 10 => offset + 9, // J and K
            11 => 1, // L reads as 1
            12 or 13 => offset + 8, // M and N
            14 => 0, // O reads as 0
            15 or 16 or 17 or 18 or 19 => offset + 7, // P to T
            20 => -1, // U is not in the alphabet
            _ => offset + 6, // V to Z
        };

        return rank >= 0;
    }

    /// <summary>A field is escaped, so a value that holds the separator cannot fake the field of another.</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>The decimal text of the index of a tie, which is the last part of a seed.</summary>
    internal static string Ordinal(int index) => index.ToString(CultureInfo.InvariantCulture);
}
