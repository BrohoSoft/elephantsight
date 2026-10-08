using System.Security.Cryptography;
using System.Text;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;

namespace Flarelytics.Api.Auth;

/// <summary>
/// La 2FA dell'utente: seme cifrato, verifica dei codici, codici di recupero.
/// </summary>
public class TwoFactorService(FieldProtector protector)
{
    public const string Issuer = "Flarelytics";
    private const int RecoveryCodeCount = 10;

    /// <summary>Genera un seme nuovo, lo salva cifrato (non ancora attivo) e restituisce quello che serve all'app.</summary>
    public (string Secret, string Uri) BeginSetup(User user)
    {
        var secret = Totp.NewSecret();
        try
        {
            user.BeginTwoFactorSetup(protector.Protect(secret, Context(user)));
            return (Totp.Base32(secret), Totp.Uri(Issuer, user.Email, secret));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Verifica un codice dell'app. Se è buono lo segna come usato: non vale una seconda volta.</summary>
    public bool VerifyCode(User user, string code, DateTime nowUtc)
    {
        if (user.TotpSecretProtected is null) return false;

        var secret = protector.Unprotect(user.TotpSecretProtected, Context(user));
        try
        {
            var step = Totp.Verify(secret, code, nowUtc, user.TotpLastUsedStep);
            if (step is null) return false;

            user.MarkTotpStepUsed(step.Value);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Un codice dell'app oppure, in alternativa, un codice di recupero.</summary>
    public bool VerifyCodeOrRecovery(User user, string code, DateTime nowUtc) =>
        VerifyCode(user, code, nowUtc) || user.UseRecoveryCode(HashRecoveryCode(code));

    /// <returns>I codici in chiaro, da mostrare una volta sola; sull'utente restano gli hash.</returns>
    public static (List<string> Codes, List<string> Hashes) NewRecoveryCodes()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var codes = Enumerable.Range(0, RecoveryCodeCount)
            .Select(_ => RandomNumberGenerator.GetString(alphabet, 5) + "-" + RandomNumberGenerator.GetString(alphabet, 5))
            .ToList();

        return (codes, codes.Select(HashRecoveryCode).ToList());
    }

    /// <summary>
    /// SHA-256 e non BCrypt: 50 bit casuali per codice, non si indovinano, e
    /// un hash lento su dieci codici renderebbe lento ogni tentativo.
    /// </summary>
    public static string HashRecoveryCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().Replace("-", "").ToLowerInvariant())));

    private static string Context(User user) => $"totp|{user.Id:N}";
}
