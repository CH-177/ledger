using System.Security.Cryptography;
using System.Text;
using Ledger.App.Infrastructure;

namespace Ledger.App.Services;

/// <summary>
/// The localhost Grokbot API bearer token, encrypted with Windows DPAPI for the current user,
/// outside the database so backups and exports never contain it.
/// </summary>
public sealed class GrokbotTokenStore
{
    private static readonly byte[] Entropy = "Ledger.Grokbot.v1"u8.ToArray();

    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "grokbot.token");

    public bool HasToken => File.Exists(FilePath);

    public string GenerateAndSave()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Save(token);
        return token;
    }

    public void Save(string token)
    {
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllBytes(FilePath, protectedBytes);
    }

    /// <summary>The token, or null when none is saved or it can't be read.</summary>
    public string? Load()
    {
        try
        {
            return HasToken ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser)) : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Clear()
    {
        if (HasToken)
        {
            File.Delete(FilePath);
        }
    }
}
