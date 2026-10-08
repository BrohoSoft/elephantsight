using System.Security.Cryptography;

namespace Flarelytics.Api.Common;

public static class SecureToken
{
    /// <summary>256 bit casuali in base64url: per refresh token e link delle email.</summary>
    public static string Create() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
