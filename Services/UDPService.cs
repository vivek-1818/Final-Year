using DeezFiles.Utilities;
using DNStore.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DNStore.Services
{
    /// <summary>
    /// REVISED UDPService v4
    /// Implements guaranteed ordered reassembly of chunks for failproof file transfers.
    /// Handles binary payloads for SAVESHARD and TAKESHARD correctly and includes the READY?/READY_ACK handshake protocol.
    /// </summary>
    public class UDPService : IDisposable
    {
        private readonly UdpClient _udpClient;
        private readonly IPEndPoint _serverEndPoint;
        private readonly string _clientId;
        private readonly int _intervalSeconds;
        private CancellationTokenSource _cts;
        private bool _isDisposed;

        private readonly ConcurrentDictionary<string, ChunkedMessage> _incompleteMessages = new ConcurrentDictionary<string, ChunkedMessage>();
        private readonly Timer _cleanupTimer;
        private const int ChunkTimeoutSeconds = 300;

        public event Action<string> LogMessage;

        public UDPService(string serverIp, int serverPort, string clientId, int intervalSeconds = 20)
        {
            _udpClient = new UdpClient(0);
            _serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIp), serverPort);
            _clientId = clientId;
            _intervalSeconds = intervalSeconds;
            _cleanupTimer = new Timer(CleanupIncompleteMessages, null, TimeSpan.FromSeconds(ChunkTimeoutSeconds), TimeSpan.FromSeconds(ChunkTimeoutSeconds));
        }

        public IPEndPoint LocalEndPoint => (IPEndPoint)_udpClient.Client.LocalEndPoint;

        #region Service Lifecycle
        public void StartHolePunching()
        {
            if (_cts != null) throw new InvalidOperationException("Service is already running");
            _cts = new CancellationTokenSource();
            LogMessage?.Invoke($"Starting UDP keep-alive to {_serverEndPoint} every {_intervalSeconds}s");
            _ = Task.Run(ReceiveLoopAsync, _cts.Token);
            _ = Task.Run(PingLoopAsync, _cts.Token);
        }
        public void Stop() { _cts?.Cancel(); }
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _cleanupTimer?.Dispose();
            _cts?.Cancel();
            _udpClient?.Dispose();
            _cts?.Dispose();
            GC.SuppressFinalize(this);
        }
        ~UDPService() => Dispose();
        #endregion

        #region Listener Management
        private readonly ConcurrentDictionary<string, Action<int>> _ackListeners = new ConcurrentDictionary<string, Action<int>>();
        private readonly ConcurrentDictionary<string, Action> _readinessListeners = new ConcurrentDictionary<string, Action>();

        public void RegisterAckListener(string messageId, Action<int> ackHandler) => _ackListeners[messageId] = ackHandler;
        public void UnregisterAckListener(string messageId) => _ackListeners.TryRemove(messageId, out _);

        public void RegisterReadinessListener(string readinessId, Action ackHandler) => _readinessListeners[readinessId] = ackHandler;
        public void UnregisterReadinessListener(string readinessId) => _readinessListeners.TryRemove(readinessId, out _);
        #endregion

        #region Sending and Receiving
        private async Task PingLoopAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var message = Encoding.UTF8.GetBytes($"KEEPALIVE|{_clientId}|{DateTime.UtcNow:o}");
                    await _udpClient.SendAsync(message, _serverEndPoint);
                }
                catch (Exception ex) { LogMessage?.Invoke($"Ping failed: {ex.Message}"); }
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), _cts.Token);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(_cts.Token);
                    // First, try to process as a chunked message.
                    if (TryProcessChunk(result.Buffer, result.RemoteEndPoint)) continue;
                    // If not a chunk, process as a simple message.
                    ProcessSimpleMessage(result.Buffer, result.RemoteEndPoint);
                }
                catch (OperationCanceledException) { LogMessage?.Invoke("Receive loop stopped."); break; }
                catch (Exception ex) { LogMessage?.Invoke($"Receive error: {ex.Message}"); }
            }
        }
        public async Task SendAsync(byte[] data, IPEndPoint endpoint) => await _udpClient.SendAsync(data, endpoint);
        #endregion

        #region Message Processing

        private void ProcessSimpleMessage(byte[] buffer, IPEndPoint remoteEndPoint)
        {
            string dataStr = Encoding.UTF8.GetString(buffer);
            var parts = dataStr.Split(new[] { '|' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            var prefix = parts[0];

            // --- Special handling for binary payloads BEFORE general text processing ---
            if (prefix == "TAKESHARD")
            {
                int prefixBytesLen = Encoding.UTF8.GetByteCount("TAKESHARD|");
                if (buffer.Length > prefixBytesLen)
                {
                    byte[] shardData = new byte[buffer.Length - prefixBytesLen];
                    Buffer.BlockCopy(buffer, prefixBytesLen, shardData, 0, shardData.Length);
                    // Asynchronously save the shard without blocking the receive loop.
                    _ = LocalFileHelper.SaveDownloadedShardAsync(shardData);
                }
                return; // Message handled
            }

            if (prefix == "SAVESHARD")
            {
                HandleSaveShard(buffer, "SAVESHARD|");
                return; // Message handled
            }
            // --- End of binary payload handling ---

            // --- Existing Readiness and ACK Handlers ---
            if (prefix == "READY?")
            {
                if (parts.Length > 1)
                {
                    string readinessId = parts[1];
                    LogMessage?.Invoke($"Received READY? from {remoteEndPoint}. Responding...");
                    var response = Encoding.UTF8.GetBytes($"READY_ACK|{readinessId}");
                    _ = SendAsync(response, remoteEndPoint); // Fire and forget response
                }
                return;
            }
            if (prefix == "READY_ACK")
            {
                if (parts.Length > 1 && _readinessListeners.TryGetValue(parts[1], out var handler))
                {
                    handler?.Invoke();
                }
                return;
            }
            if (prefix == "ACK")
            {
                var ackParts = dataStr.Split('|');
                if (ackParts.Length >= 3 && _ackListeners.TryGetValue(ackParts[1], out var handler))
                {
                    if (int.TryParse(ackParts[2], out int index)) handler?.Invoke(index);
                }
                return;
            }
            // --- End of existing handlers ---

            // Process all other messages as text.
            HandleIncomingTextMessage(dataStr, remoteEndPoint);
        }

        private bool TryProcessChunk(byte[] data, IPEndPoint sender)
        {
            string headerStr;
            int headerEndIndex = -1;
            int pipeCount = 0;
            // A chunked header is CHUNKED|messageId|chunkIndex|totalChunks|originalPrefix|
            // So we need to find the 5th pipe character.
            for (int i = 0; i < data.Length && i < 200; i++) // Limit search to prevent long scans
            {
                if (data[i] == (byte)'|')
                {
                    pipeCount++;
                    if (pipeCount == 5) { headerEndIndex = i; break; }
                }
            }

            if (headerEndIndex == -1) return false; // Not a valid chunk header

            try
            {
                headerStr = Encoding.UTF8.GetString(data, 0, headerEndIndex);
                string[] headerParts = headerStr.Split('|');

                if (headerParts.Length < 5 || headerParts[0] != "CHUNKED") return false;

                string messageId = headerParts[1];
                if (!int.TryParse(headerParts[2], out int chunkIndex) || !int.TryParse(headerParts[3], out int totalChunks)) return false;
                string originalPrefix = headerParts[4];

                int headerLength = Encoding.UTF8.GetByteCount(headerStr + "|");
                var message = _incompleteMessages.GetOrAdd(messageId, _ => new ChunkedMessage(totalChunks, originalPrefix));

                byte[] chunkData = new byte[data.Length - headerLength];
                Buffer.BlockCopy(data, headerLength, chunkData, 0, chunkData.Length);

                message.AddChunk(chunkIndex, chunkData);

                // Send acknowledgment for this chunk
                byte[] ack = Encoding.UTF8.GetBytes($"ACK|{messageId}|{chunkIndex}");
                _ = SendAsync(ack, sender);

                if (message.IsComplete)
                {
                    if (_incompleteMessages.TryRemove(messageId, out var completedMessage))
                    {
                        byte[] fullData = completedMessage.GetFullData();
                        ProcessReassembledMessage(fullData, sender, originalPrefix);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Chunk processing error: {ex.Message}");
                return false;
            }
        }

        private void ProcessReassembledMessage(byte[] fullData, IPEndPoint sender, string prefix)
        {
            LogMessage?.Invoke($"Reassembled a {prefix} message of {fullData.Length} bytes from {sender}.");

            // Correctly route reassembled messages based on their original prefix.
            if (prefix == "SAVESHARD")
            {
                LocalFileHelper.SaveAndCreateShardTransaction(fullData);
            }
            else if (prefix == "TAKESHARD")
            {
                // The payload is binary shard data, handle it directly.
                _ = LocalFileHelper.SaveDownloadedShardAsync(fullData);
            }
            else
            {
                // This is for all other text-based messages.
                string dataStr = Encoding.UTF8.GetString(fullData);
                HandleIncomingTextMessage($"{prefix}|{dataStr}", sender);
            }
        }

        private void HandleSaveShard(byte[] buffer, string prefixWithPipe)
        {
            int prefixBytesLen = Encoding.UTF8.GetByteCount(prefixWithPipe);
            if (buffer.Length > prefixBytesLen)
            {
                byte[] shardData = new byte[buffer.Length - prefixBytesLen];
                Buffer.BlockCopy(buffer, prefixBytesLen, shardData, 0, shardData.Length);
                LocalFileHelper.SaveAndCreateShardTransaction(shardData);
            }
        }

        private async void HandleIncomingTextMessage(string recData, IPEndPoint remoteEndPoint)
        {
            try
            {
                var parts = recData.Split(new[] { '|' }, 2);
                var prefix = parts[0];
                var payload = parts.Length > 1 ? parts[1] : string.Empty;

                switch (prefix)
                {
                    case "PUNCHPEER":
                        if (parts.Length > 1)
                        {
                            var peerParts = payload.Split('|');
                            if (peerParts.Length == 2)
                            {
                                IPEndPoint nodeEndpoint = new IPEndPoint(IPAddress.Parse(peerParts[0]), int.Parse(peerParts[1]));
                                byte[] msg = Encoding.UTF8.GetBytes("PUNCH");
                                for (int i = 0; i < 3; i++) await SendAsync(msg, nodeEndpoint);
                            }
                        }
                        break;
                    case "PUNCH":
                        LogMessage?.Invoke($"Received PUNCH from {remoteEndPoint}");
                        break;
                    case "DOWNLOADBC":
                        string uploadBC = JsonConvert.SerializeObject(await Blockchain.GetBlockchain());
                        await Blockchain.p2pService.SendMessageAsync(remoteEndPoint.Address.ToString(), remoteEndPoint.Port, "TAKEBC", uploadBC);
                        break;
                    case "TAKEBC":
                        List<Block> deserializedBC = JsonConvert.DeserializeObject<List<Block>>(payload);
                        await Blockchain.UpdateBlockchain(deserializedBC);
                        break;
                    case "ADDTRANSACTION":
                        StorageCommitmentTransaction transaction = JsonConvert.DeserializeObject<StorageCommitmentTransaction>(payload);
                        await Blockchain.AddTransaction(transaction);
                        break;
                    case "NEWBLOCK":
                        Block newBlock = JsonConvert.DeserializeObject<Block>(payload);
                        await Blockchain.ProcessNewBlock(newBlock, remoteEndPoint.ToString());
                        break;
                    case "DOWNLOADSHARD":
                        byte[] shardData = LocalFileHelper.RetrieveShards(payload);
                        if (shardData != null)
                        {
                            await Blockchain.p2pService.SendDataAsync(remoteEndPoint.Address.ToString(), remoteEndPoint.Port, "TAKESHARD", shardData);
                        }
                        break;
                    default:
                        LogMessage?.Invoke($"Received unknown message prefix '{prefix}' from {remoteEndPoint}");
                        break;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Error in HandleIncomingTextMessage: {ex.Message} for data: {recData}");
            }
        }
        #endregion

        #region Helper Class and Methods
        private void CleanupIncompleteMessages(object state)
        {
            var keysToRemove = _incompleteMessages.Where(kvp => (DateTime.UtcNow - kvp.Value.LastUpdated).TotalSeconds > ChunkTimeoutSeconds)
                                                  .Select(kvp => kvp.Key)
                                                  .ToList();
            foreach (var key in keysToRemove)
            {
                if (_incompleteMessages.TryRemove(key, out _))
                {
                    LogMessage?.Invoke($"Cleaned up stale incomplete message: {key}");
                }
            }
        }

        private class ChunkedMessage
        {
            public byte[][] Chunks { get; }
            public string Prefix { get; }
            public DateTime LastUpdated { get; private set; }
            private int _receivedCount;
            public bool IsComplete => _receivedCount == Chunks.Length;

            public ChunkedMessage(int totalChunks, string prefix)
            {
                Chunks = new byte[totalChunks][];
                Prefix = prefix;
                LastUpdated = DateTime.UtcNow;
            }

            public void AddChunk(int index, byte[] data)
            {
                if (index >= 0 && index < Chunks.Length && Chunks[index] == null)
                {
                    Chunks[index] = data;
                    Interlocked.Increment(ref _receivedCount);
                    LastUpdated = DateTime.UtcNow;
                }
            }

            /// <summary>
            /// **RELIABILITY FIX:** This method now assembles chunks in their correct order,
            /// preventing data corruption from out-of-order packet arrival.
            /// </summary>
            /// <returns>The fully reassembled message data.</returns>
            public byte[] GetFullData()
            {
                using (var ms = new MemoryStream())
                {
                    for (int i = 0; i < Chunks.Length; i++)
                    {
                        // If any chunk is missing, the data is incomplete and cannot be reassembled.
                        if (Chunks[i] == null)
                        {
                            // This case should ideally not be hit if IsComplete is checked first,
                            // but it's a safeguard.
                            return null;
                        }
                        ms.Write(Chunks[i], 0, Chunks[i].Length);
                    }
                    return ms.ToArray();
                }
            }
        }
        #endregion
    }
}
