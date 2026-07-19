using System;
using System.Security.Cryptography;
using System.Text;

namespace Diffusion.Toolkit.Common;

/// <summary>
/// Encrypts/decrypts short secrets (API keys) with Windows DPAPI, scoped to the
/// current Windows user, so they are never stored in plaintext in settings.json.
/// </summary>
public static class ProtectedString
{
    /// <summary>
    /// Encrypts a value and returns it as base64 ciphertext. Returns null for
    /// null/empty input.
    /// </summary>
    public static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Decrypts base64 ciphertext produced by <see cref="Protect"/>. Returns
    /// null when input is empty or cannot be decrypted (corrupt data, or the
    /// settings file was copied from a different Windows user).
    /// </summary>
    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
