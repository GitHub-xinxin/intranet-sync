using System;
using System.Security.Cryptography;
using System.Text;

namespace IntranetSync.Core.Config
{
    /// <summary>
    /// Protects secrets at rest. Prefers DPAPI (LocalMachine) on Windows;
    /// falls back to AES with a machine-derived key when DPAPI is unavailable.
    /// </summary>
    public static class SecretProtector
    {
        private const string AesPrefix = "AES1:";
        private const string DpapiPrefix = "DPAPI:";

        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return plainText;

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plainText);
                byte[] protectedBytes = ProtectedData.Protect(data, GetEntropy(), DataProtectionScope.LocalMachine);
                return DpapiPrefix + Convert.ToBase64String(protectedBytes);
            }
            catch (Exception)
            {
                return AesPrefix + AesProtect(plainText);
            }
        }

        public static string Unprotect(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText))
                return cipherText;

            if (cipherText.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                byte[] protectedBytes = Convert.FromBase64String(cipherText.Substring(DpapiPrefix.Length));
                byte[] data = ProtectedData.Unprotect(protectedBytes, GetEntropy(), DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(data);
            }

            if (cipherText.StartsWith(AesPrefix, StringComparison.Ordinal))
            {
                return AesUnprotect(cipherText.Substring(AesPrefix.Length));
            }

            // legacy / plaintext migration
            return cipherText;
        }

        private static byte[] GetEntropy()
        {
            return Encoding.UTF8.GetBytes("IntranetSyncAgent.v1." + Environment.MachineName);
        }

        private static byte[] DeriveKey()
        {
            // Machine-scope key material — not portable across hosts (by design).
            string material = "IntranetSync.AES." + Environment.MachineName + "|" + Environment.UserDomainName;
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(Encoding.UTF8.GetBytes(material));
            }
        }

        private static string AesProtect(string plainText)
        {
            byte[] key = DeriveKey();
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV();
                using (var enc = aes.CreateEncryptor())
                {
                    byte[] plain = Encoding.UTF8.GetBytes(plainText);
                    byte[] cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
                    byte[] payload = new byte[aes.IV.Length + cipher.Length];
                    Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
                    Buffer.BlockCopy(cipher, 0, payload, aes.IV.Length, cipher.Length);
                    return Convert.ToBase64String(payload);
                }
            }
        }

        private static string AesUnprotect(string b64)
        {
            byte[] payload = Convert.FromBase64String(b64);
            byte[] key = DeriveKey();
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                byte[] iv = new byte[16];
                Buffer.BlockCopy(payload, 0, iv, 0, 16);
                aes.IV = iv;
                byte[] cipher = new byte[payload.Length - 16];
                Buffer.BlockCopy(payload, 16, cipher, 0, cipher.Length);
                using (var dec = aes.CreateDecryptor())
                {
                    byte[] plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                    return Encoding.UTF8.GetString(plain);
                }
            }
        }
    }
}
