using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DNStore.Models
{
    internal class StorageCommitmentTransaction
    {
        public string TransactionType { get; set; }
        public string ChunkHash { get; set; }
        public string NodeId { get; set; }
        public DateTime Timestamp { get; set; }

        public string CalculateHash()
        {
            string rawData = $"{TransactionType}-{ChunkHash}-{NodeId}-{Timestamp}";
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                return Convert.ToHexString(bytes);
            }
        }
    }
}
