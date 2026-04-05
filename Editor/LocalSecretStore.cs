using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Ignoranz.CollabSync.Tools
{
    /// <summary>
    /// 承認用メールアドレスなどの秘密情報を「ローカルのみ」で暗号保存。
    /// - 保存先: UserSettings/.collab_uploader/secrets.bin（VCS共有されない）
    /// - AES-256(CBC) + PKCS7
    /// - IV(16B) を先頭に格納
    /// - 鍵は端末/プロジェクト固有情報から SHA-256 で導出（32B）
    /// </summary>
    public static class LocalSecretStore
    {
        // 保存ディレクトリとファイル
        static string Dir =>
            Path.Combine(Directory.GetCurrentDirectory(), "UserSettings/.collab_uploader");
        static string FilePath => Path.Combine(Dir, "secrets.bin");

        // 端末/プロジェクト固有のキー（32 bytes）を導出
        static byte[] DeriveKey()
        {
            var salt = $"{Application.companyName}|{Application.productName}|{Application.unityVersion}|{Application.dataPath}|{SystemInfo.deviceUniqueIdentifier}";
            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(salt)); // 32 bytes
        }

        /// <summary>メールアドレスなどのプレーン文字列を保存（暗号化）</summary>
        public static void SaveEmail(string email)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var plain = Encoding.UTF8.GetBytes(email ?? "");

                using var aes = Aes.Create();
                aes.Key = DeriveKey();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();

                using var ms = new MemoryStream();
                // 先頭に IV を書き込む
                ms.Write(aes.IV, 0, aes.IV.Length);

                using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                {
                    cs.Write(plain, 0, plain.Length);
                    cs.FlushFinalBlock();
                }

                File.WriteAllBytes(FilePath, ms.ToArray());
            }
            catch (Exception e)
            {
                Debug.LogError("[LocalSecretStore] SaveEmail failed: " + e);
            }
        }

        /// <summary>保存済み文字列を復号して取得。存在しなければ null。</summary>
        public static string LoadEmail()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var data = File.ReadAllBytes(FilePath);
                if (data.Length < 16) return null; // IV 不足

                var iv = new byte[16];
                Array.Copy(data, 0, iv, 0, 16);
                var cipher = new byte[data.Length - 16];
                Array.Copy(data, 16, cipher, 0, cipher.Length);

                using var aes = Aes.Create();
                aes.Key = DeriveKey();
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var ms = new MemoryStream();
                using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                {
                    cs.Write(cipher, 0, cipher.Length);
                    cs.FlushFinalBlock();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>保存を破棄</summary>
        public static void ForgetEmail()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch { /* ignore */ }
        }
    }
}