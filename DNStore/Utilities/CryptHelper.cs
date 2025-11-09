using System;
using System.IO;
using System.Security.Cryptography;

namespace DeezFiles.Utilities
{
    /// <summary>
    /// Provides helper methods for AES encryption and decryption.
    /// </summary>
    public static class CryptHelper
    {

        public static string GenerateMasterKey(int byteLength)
        {
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256; // AES-256
                aes.GenerateKey();
                aes.GenerateIV();

                // Convert key and IV to Base64 strings
                string keyBase64 = Convert.ToBase64String(aes.Key);
                string ivBase64 = Convert.ToBase64String(aes.IV);

                // Save to file as: key;iv
                string content = $"{keyBase64};{ivBase64}";
                return content;
            }
        }

        /// <summary>
        /// Encrypts byte data using AES.
        /// </summary>
        public static byte[] EncryptData(byte[] data, byte[] key, byte[] iv)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                using (var memoryStream = new MemoryStream())
                {
                    using (var cryptoStream = new CryptoStream(memoryStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        cryptoStream.Write(data, 0, data.Length);
                        cryptoStream.FlushFinalBlock();
                        return memoryStream.ToArray();
                    }
                }
            }
        }

        /// <summary>
        /// Decrypts byte data using AES.
        /// </summary>
        /// <returns>The decrypted data, or null if decryption fails.</returns>
        public static byte[] DecryptData(byte[] data, byte[] key, byte[] iv)
        {
            try
            {
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;
                    using (var memoryStream = new MemoryStream())
                    {
                        using (var cryptoStream = new CryptoStream(memoryStream, aes.CreateDecryptor(), CryptoStreamMode.Write))
                        {
                            cryptoStream.Write(data, 0, data.Length);
                            cryptoStream.FlushFinalBlock();
                            return memoryStream.ToArray();
                        }
                    }
                }
            }
            catch (CryptographicException ex)
            {
                // This typically happens if the key is wrong or the data is corrupt.
                Console.WriteLine($"[CryptHelper] Decryption failed: {ex.Message}.");
                return null;
            }
        }
    }
}
