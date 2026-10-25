using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LoanOnboard.Core.Common;

/// <summary>Opaque, sortable 26-character ids (ULID-style). Safe to expose to dealers.</summary>
public static class AckIdGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New(DateTimeOffset now)
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)now.ToUnixTimeMilliseconds();
        for (var i = 0; i < 6; i++) bytes[i] = (byte)(ms >> (40 - 8 * i));
        RandomNumberGenerator.Fill(bytes[6..]);

        var value = BinaryPrimitives.ReadUInt128BigEndian(bytes);
        Span<char> chars = stackalloc char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31u)];
            value >>= 5;
        }
        return new string(chars);
    }
}

public static class LoanNumber
{
    /// <summary>LN + 9-digit sequence + Luhn check digit, e.g. LN0000001239.</summary>
    public static string Format(long sequence)
    {
        var body = sequence.ToString("D9");
        return $"LN{body}{LuhnCheckDigit(body)}";
    }

    public static bool IsValid(string loanNumber)
    {
        if (loanNumber.Length != 12 || !loanNumber.StartsWith("LN", StringComparison.Ordinal)) return false;
        var body = loanNumber.Substring(2, 9);
        return body.All(char.IsDigit) && loanNumber[11] - '0' == LuhnCheckDigit(body);
    }

    private static int LuhnCheckDigit(string digits)
    {
        var sum = 0;
        var doubleIt = true;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        return (10 - sum % 10) % 10;
    }
}

public static class IdentifierHasher
{
    public static string Hash(string value, string key)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Last4(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Length >= 4 ? normalized[^4..] : normalized.PadLeft(4, '*');
    }
}
