using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DeezFiles.Utilities
{
    /// <summary>
    /// Manages the state of file download operations. It tracks received shards
    /// and triggers file reconstruction when all shards for a file are present.
    /// </summary>
    public static class FileDownloader
    {
        private static readonly ConcurrentDictionary<string, DownloadOperation> _downloads =
            new ConcurrentDictionary<string, DownloadOperation>();

        /// <summary>
        /// Starts a new download process for a given file.
        /// </summary>
        public static void InitiateDownload(string filename, IEnumerable<string> shardHashes)
        {
            if (string.IsNullOrEmpty(filename) || !shardHashes.Any())
            {
                Console.WriteLine($"[FileDownloader] Invalid request to initiate download for {filename}.");
                return;
            }

            var operation = new DownloadOperation(filename, shardHashes);
            if (_downloads.TryAdd(filename, operation))
            {
                Console.WriteLine($"[FileDownloader] Initiating download for '{filename}'. Required shards: {shardHashes.Count()}");

                // **RELIABILITY FIX:** Kick off the download process in a background task
                // to request shards sequentially, preventing network flooding.
                Task.Run(async () =>
                {
                    foreach (var shardHash in operation.GetExpectedShardHashes())
                    {
                        Console.WriteLine($"[FileDownloader] Requesting shard: {shardHash}");

                        // Await the now-awaitable method to ensure the request is sent.
                        await LocalFileHelper.FindAndDownloadShardAsync(shardHash);

                        // Add a small delay between requests to be courteous to the network and peer.
                        await Task.Delay(5000); // 250 milliseconds
                    }
                });
            }
            else
            {
                Console.WriteLine($"[FileDownloader] A download for '{filename}' is already in progress.");
            }
        }

        /// <summary>
        /// Callback method to be invoked when a shard has been successfully downloaded and saved.
        /// </summary>
        public static void OnShardDownloaded(string shardHash, string shardPath)
        {
            var operation = _downloads.Values.FirstOrDefault(op => op.HasShard(shardHash));

            if (operation != null)
            {
                operation.AddDownloadedShard(shardHash, shardPath);
                Console.WriteLine($"[FileDownloader] Shard '{shardHash}' for file '{operation.OriginalFilename}' received. " +
                                  $"Progress: {operation.GetProgress()}%");

                if (operation.IsComplete())
                {
                    Console.WriteLine($"[FileDownloader] All shards for '{operation.OriginalFilename}' received. Reconstructing file...");

                    var orderedShardsData = operation.GetOrderedShardsData();
                    LocalFileHelper.ReconstructFile(orderedShardsData, operation.OriginalFilename);

                    _downloads.TryRemove(operation.OriginalFilename, out _);
                    operation.DeleteDownloadedShards();
                }
            }
        }

        private class DownloadOperation
        {
            public string OriginalFilename { get; }
            private readonly List<string> _expectedShardHashes;
            private readonly ConcurrentDictionary<string, string> _downloadedShardPaths;

            public DownloadOperation(string filename, IEnumerable<string> shardHashes)
            {
                OriginalFilename = filename;
                _expectedShardHashes = shardHashes.Where(h => !string.IsNullOrEmpty(h)).ToList();
                _downloadedShardPaths = new ConcurrentDictionary<string, string>();
            }

            public bool HasShard(string shardHash) => _expectedShardHashes.Contains(shardHash);
            public IEnumerable<string> GetExpectedShardHashes() => _expectedShardHashes;
            public void AddDownloadedShard(string shardHash, string shardPath) => _downloadedShardPaths.TryAdd(shardHash, shardPath);
            public bool IsComplete() => _downloadedShardPaths.Count == _expectedShardHashes.Count;
            public int GetProgress() => (_downloadedShardPaths.Count * 100) / _expectedShardHashes.Count;

            public List<byte[]> GetOrderedShardsData()
            {
                var orderedData = new List<byte[]>();
                foreach (var hash in _expectedShardHashes)
                {
                    if (_downloadedShardPaths.TryGetValue(hash, out var path) && File.Exists(path))
                    {
                        orderedData.Add(File.ReadAllBytes(path));
                    }
                    else
                    {
                        Console.WriteLine($"[Error] Missing shard data for hash {hash} during reconstruction!");
                    }
                }
                return orderedData;
            }

            public void DeleteDownloadedShards()
            {
                foreach (var path in _downloadedShardPaths.Values)
                {
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[FileDownloader] Failed to delete temp shard {path}: {ex.Message}");
                    }
                }
            }
        }
    }
}
