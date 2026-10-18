using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace EnergyTracker.Application;

/// <summary>
/// The one place that generates and hashes an invite token, so create and lookup cannot disagree (AD-26, AC #5).
/// A plain SHA-256 is enough: the token is 256 bits of CSPRNG output, not a human-chosen secret.
/// </summary>
public static class InviteToken
{
    /// <summary>Longest presented token that is hashed or compared; anything longer is answered like an unknown token.</summary>
    public const int MaxLength = 128;

    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool IsPlausible(string token) => token.Length is > 0 and <= MaxLength;
}
