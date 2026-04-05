using System.Security.Cryptography;
using System.Text;

namespace Ignoranz.CollabSync.Tools
{
    public static class HashUtil
    {
        public static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(bytes ?? new byte[0]);
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}