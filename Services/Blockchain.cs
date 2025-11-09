using DeezFiles.Services;
using DeezFiles.Utilities;
using DNStore.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DNStore.Services
{
    /// <summary>
    /// REVISED Blockchain
    /// The changes here are to update the calls to P2PService to use the new
    /// SendMessageAsync method.
    /// </summary>
    internal class Blockchain
    {
        private static readonly string _blockchainPath = Path.Combine(LocalFileHelper.statePath, "blockchain.json");
        public static UDPService udpService;
        public static P2PService p2pService;
        private static readonly List<StorageCommitmentTransaction> _pendingTransactions = new List<StorageCommitmentTransaction>();
        private static readonly object _lock = new object();

        public static async Task InitializeBlockchainAsync()
        {
            // Initialize network services
            // Ensure AuthorizationService.nodeAddress is set before this is called
            udpService = new UDPService("74.225.135.66", 12345, AuthorizationService.nodeAddress);
            p2pService = new P2PService(udpService);
            udpService.StartHolePunching();

            // Create or validate blockchain file
            if (!File.Exists(_blockchainPath))
            {
                await CreateNewBlockchain();
            }
            else
            {
                var blockchain = await LoadBlockchain();
                if (!ValidateBlockchain(blockchain))
                {
                    Console.WriteLine("Invalid blockchain detected, creating new one.");
                    await CreateNewBlockchain();
                }
            }

            await SyncWithNetwork();
        }

        private static async Task CreateNewBlockchain()
        {
            var genesisBlock = new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                PreviousHash = "0",
                Transactions = new List<StorageCommitmentTransaction>(),
                BlockHash = CalculateHash(0, DateTime.UtcNow, "0", string.Empty)
            };

            await SaveBlockchain(new List<Block> { genesisBlock });
        }

        public static async Task<List<Block>> GetBlockchain()
        {
            return await LoadBlockchain();
        }

        public static async Task AddTransaction(StorageCommitmentTransaction transaction)
        {
            bool isDuplicate = await IsDuplicateTransaction(transaction.ChunkHash);
            if (isDuplicate)
            {
                Console.WriteLine($"Duplicate transaction detected and skipped: {transaction.ChunkHash}");
                return;
            }

            lock (_lock)
            {
                if (!_pendingTransactions.Any(t => t.ChunkHash == transaction.ChunkHash))
                {
                    _pendingTransactions.Add(transaction);
                }
            }

            if (_pendingTransactions.Count >= 3)
            {
                await CreateBlockFromPendingTransactions();
            }
        }

        private static async Task CreateBlockFromPendingTransactions()
        {
            List<StorageCommitmentTransaction> transactionsToMine;
            lock (_lock)
            {
                if (_pendingTransactions.Count < 3) return;
                transactionsToMine = new List<StorageCommitmentTransaction>(_pendingTransactions);
                _pendingTransactions.Clear();
            }

            var blockchain = await GetBlockchain();
            var latestBlock = blockchain.LastOrDefault();
            if (latestBlock == null) return; // Should not happen after initialization

            var newBlock = new Block
            {
                Index = latestBlock.Index + 1,
                Timestamp = DateTime.UtcNow,
                PreviousHash = latestBlock.BlockHash,
                Transactions = transactionsToMine,
                BlockHash = string.Empty
            };

            newBlock.MerkleRoot = CalculateMerkleRoot(newBlock.Transactions);
            newBlock.BlockHash = CalculateBlockHash(newBlock);

            blockchain.Add(newBlock);
            await SaveBlockchain(blockchain);
            Console.WriteLine($"Created and added new block #{newBlock.Index}");

            await BroadcastNewBlock(newBlock);
        }

        private static async Task<bool> IsDuplicateTransaction(string chunkHash)
        {
            lock (_lock)
            {
                if (_pendingTransactions.Any(t => t.ChunkHash == chunkHash))
                    return true;
            }

            var blockchain = await GetBlockchain();
            return blockchain.SelectMany(b => b.Transactions).Any(t => t.ChunkHash == chunkHash);
        }

        private static async Task BroadcastNewBlock(Block newBlock)
        {
            var onlineNodes = await GetOnlineNodes();
            if (onlineNodes.Any())
            {
                var blockJson = JsonConvert.SerializeObject(newBlock);
                foreach (var node in onlineNodes)
                {
                    // *** UPDATED METHOD CALL ***
                    await p2pService.SendMessageAsync(node.ipAddress, node.port, "NEWBLOCK", blockJson);
                }
            }
        }

        public static async Task ProcessNewBlock(Block newBlock, string sender)
        {
            var blockchain = await GetBlockchain();
            var lastBlock = blockchain.LastOrDefault();

            if (lastBlock == null || newBlock.Index <= lastBlock.Index)
            {
                Log($"Received old or competing block #{newBlock.Index} from {sender}. Ignoring.");
                return;
            }

            if (newBlock.PreviousHash == lastBlock.BlockHash && newBlock.BlockHash == CalculateBlockHash(newBlock))
            {
                blockchain.Add(newBlock);
                await SaveBlockchain(blockchain);
                Log($"Added new valid block #{newBlock.Index} from {sender}.");
            }
            else
            {
                Log($"Received invalid block #{newBlock.Index} from {sender}.");
            }
        }

        public static async Task UpdateBlockchain(List<Block> newBlockchain)
        {
            if (newBlockchain == null || !newBlockchain.Any()) return;

            var currentBlockchain = await GetBlockchain();
            if (newBlockchain.Count > currentBlockchain.Count && ValidateBlockchain(newBlockchain))
            {
                await SaveBlockchain(newBlockchain);
                Log("Blockchain updated from network.");
            }
        }

        public static async Task<List<Block>> LoadBlockchain()
        {
            try
            {
                if (!File.Exists(_blockchainPath)) return new List<Block>();

                var json = await File.ReadAllTextAsync(_blockchainPath);
                return JsonConvert.DeserializeObject<List<Block>>(json) ?? new List<Block>();
            }
            catch (Exception ex)
            {
                Log($"Error loading blockchain: {ex.Message}");
                return new List<Block>();
            }
        }

        public static async Task SaveBlockchain(List<Block> blockchain)
        {
            try
            {
                var json = JsonConvert.SerializeObject(blockchain, Formatting.Indented);
                await File.WriteAllTextAsync(_blockchainPath, json);
            }
            catch (Exception ex)
            {
                Log($"Error saving blockchain: {ex.Message}");
            }
        }

        private static bool ValidateBlockchain(List<Block> blockchain)
        {
            if (blockchain == null || !blockchain.Any()) return false;
            if (blockchain[0].Index != 0 || blockchain[0].PreviousHash != "0") return false;

            for (int i = 1; i < blockchain.Count; i++)
            {
                var currentBlock = blockchain[i];
                var previousBlock = blockchain[i - 1];

                if (currentBlock.PreviousHash != previousBlock.BlockHash) return false;
                if (currentBlock.BlockHash != CalculateBlockHash(currentBlock)) return false;
            }
            return true;
        }

        public static string CalculateBlockHash(Block block)
        {
            return CalculateHash(block.Index, block.Timestamp, block.PreviousHash, block.MerkleRoot);
        }

        private static string CalculateHash(int index, DateTime timestamp, string previousHash, string data)
        {
            using (var sha256 = SHA256.Create())
            {
                var input = $"{index}{timestamp:O}{previousHash}{data}";
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                return BitConverter.ToString(bytes).Replace("-", "").ToLower();
            }
        }

        public static async Task<OnlineNode> SelectBestNode()
        {
            List<OnlineNode> onlineNodes = await GetOnlineNodes();
            if (!onlineNodes.Any()) return null;

            var random = new Random();
            int index = random.Next(onlineNodes.Count);
            return onlineNodes[index];
        }

        private static string CalculateMerkleRoot(List<StorageCommitmentTransaction> transactions)
        {
            if (transactions == null || !transactions.Any()) return string.Empty;

            var hashes = transactions.Select(t => t.ChunkHash).ToList();
            while (hashes.Count > 1)
            {
                if (hashes.Count % 2 != 0)
                {
                    hashes.Add(hashes.Last()); // Duplicate last hash if odd number
                }
                var nextLevelHashes = new List<string>();
                for (int i = 0; i < hashes.Count; i += 2)
                {
                    var combinedHashData = hashes[i] + hashes[i + 1];
                    nextLevelHashes.Add(CalculateHash(0, DateTime.UtcNow, "", combinedHashData));
                }
                hashes = nextLevelHashes;
            }
            return hashes.FirstOrDefault();
        }

        private static async Task SyncWithNetwork()
        {
            var onlineNodes = await GetOnlineNodes();
            if (onlineNodes.Any())
            {
                await p2pService.PunchPeers(onlineNodes);
                foreach (var node in onlineNodes)
                {
                    // *** UPDATED METHOD CALL ***
                    await p2pService.SendMessageAsync(node.ipAddress, node.port, "DOWNLOADBC");
                }
            }
        }

        public static async Task<List<OnlineNode>> GetOnlineNodes()
        {
            try
            {
                HttpResponseMessage response = await NetworkService.SendGetRequest("OnlineNodes");
                response.EnsureSuccessStatusCode();
                string jsonResponse = await response.Content.ReadAsStringAsync();
                List<OnlineNode> nodes = JsonConvert.DeserializeObject<List<OnlineNode>>(jsonResponse);
                nodes.RemoveAll(node => node.dnAddress == AuthorizationService.nodeAddress);
                return nodes;
            }
            catch (Exception ex)
            {
                Log($"Could not get online nodes: {ex.Message}");
                return new List<OnlineNode>();
            }
        }

        private static void Log(string message)
        {
            Console.WriteLine($"[Blockchain] {message}");
        }
    }
}
