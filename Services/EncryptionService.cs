using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GitDeployPro.Services
{
    public static class EncryptionService
    {
        // In a real production app, use a more secure key management strategy (e.g., DPAPI for current user)
        // For this local tool, we'll use a static key derived from a hardcoded string + machine specific info if possible, 
        // or just DPAPI (ProtectedData) which is best for local Windows apps.

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plainText);
                byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            catch
            {
                return plainText; // Fallback if encryption fails (shouldn't happen on Windows)
            }
        }

        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return cipherText;
            try
            {
                byte[] data = Convert.FromBase64String(cipherText);
                byte[] decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                return ""; // Fail safe
            }
        }
    }
}

