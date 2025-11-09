using DeezFiles.Models;
using DeezFiles.Services;
using DNStore.Models;
using DNStore.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeezFiles.Utilities
{
    internal class LocalFileHelper
    {
        public static event EventHandler FileListUpdated;

        public static string statePath;
        public static string userFolderPath;
        public static string documentsPath;
        public static string dnStorePath;
        public static string mainstoragePath;
        public static string uploadqueuePath;
        public static string configPath;
        public static string downloadPath;

        public LocalFileHelper(string username)
        {
            documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            dnStorePath = Path.Combine(documentsPath, "DNStore");
            userFolderPath = Path.Combine(dnStorePath, username);
            mainstoragePath = Path.Combine(userFolderPath, "storage");
            uploadqueuePath = Path.Combine(userFolderPath, "uploadData");
            configPath = Path.Combine(userFolderPath, "config");
            downloadPath = Path.Combine(userFolderPath, "downloads");
            statePath = Path.Combine(userFolderPath, "state");
        }

        public void SetupRegistrationFolders()
        {
            if (!Directory.Exists(dnStorePath)) Directory.CreateDirectory(dnStorePath);
            if (!Directory.Exists(userFolderPath)) Directory.CreateDirectory(userFolderPath);
            if (!Directory.Exists(configPath)) Directory.CreateDirectory(configPath);
            if (!Directory.Exists(downloadPath)) Directory.CreateDirectory(downloadPath);
            if (!Directory.Exists(mainstoragePath)) Directory.CreateDirectory(mainstoragePath);
            if (!Directory.Exists(uploadqueuePath)) Directory.CreateDirectory(uploadqueuePath);
            if (!Directory.Exists(statePath)) Directory.CreateDirectory(statePath);
        }

        public void SaveMasterKey(string mKey)
        {
            string secretfile = Path.Combine(configPath, "secret.txt");
            File.WriteAllText(secretfile, mKey);
        }

        public static void SaveDNETaddress(string username, string address)
        {
            string data = username + ":" + address;
            string addressfile = Path.Combine(configPath, "add.txt");
            File.WriteAllText(addressfile, data);
        }

        public static string GetDNETaddress()
        {
            return File.ReadAllText(Path.Combine(configPath, "add.txt"));
        }

        public static async Task SaveUPFileDetails(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            DateTime uploadTime = DateTime.Now;
            var fileInfo = new FileInfo(filePath);
            ulong fileSize = (ulong)fileInfo.Length;
            string sha256Hash = await CalculateSHA256(filePath);
            var fileState = new { UploadTime = uploadTime, Size = fileSize, SHA256 = sha256Hash };
            string jsonFilePath = Path.Combine(statePath, "filestate.json");
            string jsonData = string.Empty;
            if (File.Exists(jsonFilePath)) { jsonData = await File.ReadAllTextAsync(jsonFilePath); }
            var fileStateDict = string.IsNullOrEmpty(jsonData) ? new Dictionary<string, object>() : JsonSerializer.Deserialize<Dictionary<string, object>>(jsonData);
            fileStateDict[fileName] = fileState;
            string updatedJson = JsonSerializer.Serialize(fileStateDict, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(jsonFilePath, updatedJson);
            FileListUpdated?.Invoke(null, EventArgs.Empty);
        }

        private static async Task<string> CalculateSHA256(string filePath)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = await sha256.ComputeHashAsync(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        public static async Task<string> RetrieveHash(string filename)
        {
            string jsonFilePath = Path.Combine(statePath, "filestate.json");
            if (!File.Exists(jsonFilePath)) { return null; }
            string jsonData = await File.ReadAllTextAsync(jsonFilePath);
            var fileStateDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData);
            if (fileStateDict != null && fileStateDict.TryGetValue(filename, out var fileStateElement))
            {
                if (fileStateElement.TryGetProperty("SHA256", out var sha256Element))
                {
                    return sha256Element.GetString();
                }
            }
            return null;
        }

        static (byte[] key, byte[] iv) LoadAESKeyIV(string filePath)
        {
            string content = File.ReadAllText(filePath);
            string[] parts = content.Split(';');
            if (parts.Length != 2) throw new Exception("Invalid key file format!");
            byte[] key = Convert.FromBase64String(parts[0]);
            byte[] iv = Convert.FromBase64String(parts[1]);
            return (key, iv);
        }

        public async static void CreateChunks(string filePath)
        {
            const int chunkSize = 256 * 1024;
            string hashFilePath;
            string originalFileHash;
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (var sha256 = SHA256.Create()) { originalFileHash = BitConverter.ToString(sha256.ComputeHash(fs)).Replace("-", "").ToLowerInvariant(); }
                fs.Position = 0;
                byte[] buffer = new byte[chunkSize];
                int bytesRead;
                int partNumber = 1;
                while ((bytesRead = await fs.ReadAsync(buffer, 0, chunkSize)) > 0)
                {
                    string tempFileName = $"temp{partNumber}";
                    string tempFilePath = Path.Combine(uploadqueuePath, tempFileName);
                    byte[] actualData = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, actualData, 0, bytesRead);
                    await File.WriteAllBytesAsync(tempFilePath, actualData);
                    partNumber++;
                }
                hashFilePath = Path.Combine(uploadqueuePath, $"{originalFileHash}.dn");
            }
            List<byte[]> chunkHashList = await StoreChunks();
            if (chunkHashList != null && chunkHashList.Any()) { StoreRecreationInfo(chunkHashList, hashFilePath); }
        }

        private static async Task<List<byte[]>> StoreChunks()
        {
            List<byte[]> chunksHash = new List<byte[]>();
            var tempFiles = Directory.GetFiles(uploadqueuePath).Where(f => Path.GetFileName(f).StartsWith("temp")).OrderBy(f => f).ToList();
            foreach (var file in tempFiles)
            {
                bool shardSentSuccessfully = false;
                try
                {
                    OnlineNode selectedNode = await Blockchain.SelectBestNode();
                    if (selectedNode == null) { Console.WriteLine("No online nodes available. Aborting."); return null; }
                    Console.WriteLine($"Attempting to establish connection with {selectedNode.dnAddress}...");
                    await Blockchain.p2pService.PunchPeers(new List<OnlineNode> { selectedNode });
                    bool isReady = await Blockchain.p2pService.CheckReadinessAsync(selectedNode);
                    if (!isReady) { Console.WriteLine($"Peer {selectedNode.dnAddress} did not confirm readiness. Aborting."); return null; }
                    Console.WriteLine($"Connection established with {selectedNode.dnAddress}. Encrypting and sending shard...");
                    string secretPath = Path.Combine(configPath, "secret.txt");
                    var (key, iv) = LoadAESKeyIV(secretPath);
                    byte[] data = await File.ReadAllBytesAsync(file);
                    byte[] encryptedData = CryptHelper.EncryptData(data, key, iv);
                    using (SHA256 sha256 = SHA256.Create()) { chunksHash.Add(sha256.ComputeHash(encryptedData)); }
                    bool success = await Blockchain.p2pService.SendDataAsync(selectedNode.ipAddress, selectedNode.port, "SAVESHARD", encryptedData);
                    if (success) { Console.WriteLine($"Shard sent successfully."); shardSentSuccessfully = true; await Task.Delay(1000); }
                    else { Console.WriteLine($"Failed to send shard to {selectedNode.dnAddress}. Aborting upload."); return null; }
                }
                catch (Exception ex) { Console.WriteLine($"An error occurred during shard processing: {ex.Message}"); return null; }
                finally { if (shardSentSuccessfully) { File.Delete(file); } }
            }
            return chunksHash;
        }

        private static void StoreRecreationInfo(List<byte[]> hashes, string hashFilepath)
        {
            try
            {
                string hashesJoined = string.Join(";", hashes.Select(h => BitConverter.ToString(h).Replace("-", "").ToLowerInvariant()));
                File.WriteAllText(hashFilepath, hashesJoined);
                Console.WriteLine($"Recreation info for {Path.GetFileName(hashFilepath)} written successfully.");
            }
            catch (Exception ex) { Console.WriteLine($"Error writing recreation info: {ex.Message}"); }
        }

        public static async void SaveAndCreateShardTransaction(byte[] shardData)
        {
            try
            {
                string shardName;
                using (SHA256 sha256 = SHA256.Create()) { shardName = BitConverter.ToString(sha256.ComputeHash(shardData)).Replace("-", "").ToLower(); }
                string shardPath = Path.Combine(mainstoragePath, shardName + ".shard");
                await File.WriteAllBytesAsync(shardPath, shardData);
                Console.WriteLine($"Shard {shardName} saved to local storage.");
                var transaction = new StorageCommitmentTransaction { NodeId = AuthorizationService.nodeAddress, Timestamp = DateTime.UtcNow, ChunkHash = shardName, TransactionType = "STORAGE" };
                await Blockchain.AddTransaction(transaction);
                string transitTransaction = Newtonsoft.Json.JsonConvert.SerializeObject(transaction);
                List<OnlineNode> onlineNodes = await Blockchain.GetOnlineNodes();
                if (onlineNodes.Any())
                {
                    await Blockchain.p2pService.PunchPeers(onlineNodes);
                    foreach (OnlineNode onlineNode in onlineNodes) { await Blockchain.p2pService.SendMessageAsync(onlineNode.ipAddress, onlineNode.port, "ADDTRANSACTION", transitTransaction); }
                }
            }
            catch (Exception ex) { Console.WriteLine($"Error saving shard and creating transaction: {ex.Message}"); }
        }

        public static byte[] RetrieveShards(string hash)
        {
            string shardPath = Path.Combine(mainstoragePath, hash + ".shard");
            if (File.Exists(shardPath)) { return File.ReadAllBytes(shardPath); }
            return null;
        }

        public static async Task SaveDownloadedShardAsync(byte[] shardData)
        {
            if (shardData == null || shardData.Length == 0) { Console.WriteLine("[LocalFileHelper] Received an empty shard. Skipping save."); return; }
            try
            {
                string shardName;
                using (SHA256 sha256 = SHA256.Create()) { shardName = BitConverter.ToString(sha256.ComputeHash(shardData)).Replace("-", "").ToLowerInvariant(); }
                string shardPath = Path.Combine(downloadPath, shardName + ".shard-dl");
                await File.WriteAllBytesAsync(shardPath, shardData);
                Console.WriteLine($"[LocalFileHelper] Downloaded shard saved: {shardName}");
                FileDownloader.OnShardDownloaded(shardName, shardPath);
            }
            catch (Exception ex) { Console.WriteLine($"[LocalFileHelper] Error saving downloaded shard: {ex.Message}"); }
        }

        public static async Task FindAndDownloadShardAsync(string shardHash)
        {
            var blockchain = await Blockchain.GetBlockchain();
            var transaction = blockchain.SelectMany(b => b.Transactions).FirstOrDefault(t => t.ChunkHash.Equals(shardHash, StringComparison.OrdinalIgnoreCase));
            if (transaction == null) { Console.WriteLine($"Shard hash {shardHash} not found on the blockchain."); return; }
            var onlineNodes = await Blockchain.GetOnlineNodes();
            var holderNode = onlineNodes.FirstOrDefault(n => n.dnAddress == transaction.NodeId);
            if (holderNode == null) { Console.WriteLine($"Node {transaction.NodeId} is not online. Cannot download shard {shardHash}."); return; }
            await Blockchain.p2pService.PunchPeers(new List<OnlineNode> { holderNode });
            await Blockchain.p2pService.SendMessageAsync(holderNode.ipAddress, holderNode.port, "DOWNLOADSHARD", shardHash);
        }

        /// <summary>
        /// DECRYPTION & RECONSTRUCTION LOGIC
        /// Reconstructs the original file from its decrypted shards.
        /// </summary>
        public static void ReconstructFile(List<byte[]> shards, string originalFilename)
        {
            string finalFilePath = Path.Combine(downloadPath, originalFilename);
            Console.WriteLine($"[LocalFileHelper] Reconstructing '{originalFilename}' from {shards.Count} shards.");

            try
            {
                string secretPath = Path.Combine(configPath, "secret.txt");
                if (!File.Exists(secretPath))
                {
                    Console.WriteLine("[LocalFileHelper] ERROR: Decryption key 'secret.txt' not found.");
                    return;
                }
                var (key, iv) = LoadAESKeyIV(secretPath);

                using (var fs = new FileStream(finalFilePath, FileMode.Create, FileAccess.Write))
                {
                    for (int i = 0; i < shards.Count; i++)
                    {
                        var shardData = shards[i];
                        Console.WriteLine($"[LocalFileHelper] Decrypting shard {i + 1}/{shards.Count}...");

                        byte[] decryptedShard = CryptHelper.DecryptData(shardData, key, iv);

                        if (decryptedShard != null)
                        {
                            fs.Write(decryptedShard, 0, decryptedShard.Length);
                        }
                        else
                        {
                            Console.WriteLine($"[LocalFileHelper] ERROR: Failed to decrypt shard {i + 1}. Aborting file reconstruction.");
                            fs.Close();
                            File.Delete(finalFilePath);
                            return;
                        }
                    }
                }
                Console.WriteLine($"[SUCCESS] File '{originalFilename}' successfully reconstructed and decrypted at {finalFilePath}!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LocalFileHelper] An error occurred during file reconstruction: {ex.Message}");
            }
        }
    }
}
