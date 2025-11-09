using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DNStore.Models
{
    internal class Block
    {
        private string selfAddy;
        public int Index { get; set; }
        public DateTime Timestamp { get; set; }
        public string PreviousHash { get; set; }
        public List<StorageCommitmentTransaction> Transactions { get; set; }
        public string MerkleRoot { get; set; }
        public string BlockHash { get; set; }

        // Reference to the previous block (for linking)
        public Block PreviousBlock { get; set; }

        public Block()
        {
            Transactions = new List<StorageCommitmentTransaction>();
        }

        public string CalculateMerkleRoot()
        {
            List<string> hashes = Transactions.Select(tx => tx.CalculateHash()).ToList();

            while (hashes.Count > 1)
            {
                List<string> newHashes = new List<string>();

                for (int i = 0; i < hashes.Count; i += 2)
                {
                    if (i + 1 < hashes.Count)
                    {
                        string combined = hashes[i] + hashes[i + 1];
                        using (SHA256 sha256 = SHA256.Create())
                        {
                            byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
                            newHashes.Add(Convert.ToHexString(bytes));
                        }
                    }
                    else
                    {
                        // Odd one out, hash it alone
                        newHashes.Add(hashes[i]);
                    }
                }

                hashes = newHashes;
            }

            MerkleRoot = hashes.Count > 0 ? hashes[0] : string.Empty;
            return MerkleRoot;
        }

        public string CalculateBlockHash()
        {
            string rawData = $"{Index}-{Timestamp}-{PreviousHash}-{MerkleRoot}";
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                BlockHash = Convert.ToHexString(bytes);
                return BlockHash;
            }
        }
    }

}
