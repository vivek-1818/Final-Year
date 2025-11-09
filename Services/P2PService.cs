using DNStore.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DNStore.Services
{
    /// <summary>
    /// REVISED P2PService v4
    /// Implements a "CheckReadinessAsync" handshake method to confirm two-way
    /// communication before initiating a large data transfer.
    /// </summary>
    public class P2PService
    {
        private readonly UDPService _udpService;
        private const int MaxPayloadSize = 1200;

        public P2PService(UDPService udpService)
        {
            _udpService = udpService;
        }

        #region Public Send Methods

        public Task<bool> SendMessageAsync(string ip, int port, string prefix, string payload = "")
        {
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
            return SendMessageInternalAsync(ip, port, prefix, payloadBytes);
        }

        public Task<bool> SendDataAsync(string ip, int port, string prefix, byte[] payload)
        {
            return SendMessageInternalAsync(ip, port, prefix, payload);
        }

        public async Task PunchPeers(List<OnlineNode> nodes)
        {
            if (!nodes.Any()) return;
            string nodesJson = JsonConvert.SerializeObject(nodes);
            await SendMessageAsync("74.225.135.66", 12345, "PUNCHPEERS", nodesJson);
            // This delay is important to allow the master server to send commands
            // and for the remote peer to potentially act on them.
            await Task.Delay(1500);
        }

        /// <summary>
        /// Performs a handshake to confirm a bidirectional communication channel is open.
        /// </summary>
        /// <returns>True if the peer acknowledges readiness, false otherwise.</returns>
        public async Task<bool> CheckReadinessAsync(OnlineNode peer)
        {
            var tcs = new TaskCompletionSource<bool>();
            string readinessId = Guid.NewGuid().ToString("N");

            // The handler will be called by UDPService when a READY_ACK comes back.
            Action readinessAckHandler = () => {
                tcs.TrySetResult(true);
            };

            _udpService.RegisterReadinessListener(readinessId, readinessAckHandler);

            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(peer.ipAddress), peer.port);
                string message = $"READY?|{readinessId}";
                var messageBytes = Encoding.UTF8.GetBytes(message);

                // Send the readiness check message multiple times to increase chance of getting through firewall
                for (int i = 0; i < 3; i++)
                {
                    await _udpService.SendAsync(messageBytes, endpoint);
                    await Task.Delay(50);
                }

                // Wait for the ACK for a maximum of 2 seconds.
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(2000));

                if (completedTask == tcs.Task && tcs.Task.Result)
                {
                    return true; // Success!
                }
                else
                {
                    // Timeout occurred
                    return false;
                }
            }
            finally
            {
                // Clean up the listener to prevent memory leaks.
                _udpService.UnregisterReadinessListener(readinessId);
            }
        }

        #endregion

        #region Core Logic

        private async Task<bool> SendMessageInternalAsync(string ip, int port, string prefix, byte[] payloadBytes)
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(ip), port);
            byte[] prefixBytes = Encoding.UTF8.GetBytes(prefix + "|");

            if (prefixBytes.Length + payloadBytes.Length <= MaxPayloadSize)
            {
                byte[] singlePacket = new byte[prefixBytes.Length + payloadBytes.Length];
                Buffer.BlockCopy(prefixBytes, 0, singlePacket, 0, prefixBytes.Length);
                Buffer.BlockCopy(payloadBytes, 0, singlePacket, prefixBytes.Length, payloadBytes.Length);
                await _udpService.SendAsync(singlePacket, endpoint);
                return true;
            }

            return await SendReliableChunkedMessageAsync(endpoint, payloadBytes, prefix);
        }

        private async Task<bool> SendReliableChunkedMessageAsync(IPEndPoint endpoint, byte[] data, string prefix)
        {
            const int WindowSize = 32;
            const int TotalTimeoutMilliseconds = 30000;
            const int RetryIntervalMilliseconds = 1000;

            string messageId = Guid.NewGuid().ToString("N");
            int totalChunks = (int)Math.Ceiling(data.Length / (double)MaxPayloadSize);
            var acksReceived = new ConcurrentDictionary<int, bool>();
            var inflightPacketSendTimes = new ConcurrentDictionary<int, DateTime>();
            var allAcksReceivedCompletion = new TaskCompletionSource<bool>();
            int nextChunkToSend = 0;

            Action<int> ackHandler = (chunkIndex) =>
            {
                if (acksReceived.TryAdd(chunkIndex, true))
                {
                    inflightPacketSendTimes.TryRemove(chunkIndex, out _);
                    if (acksReceived.Count == totalChunks)
                    {
                        allAcksReceivedCompletion.TrySetResult(true);
                    }
                }
            };

            _udpService.RegisterAckListener(messageId, ackHandler);

            try
            {
                using (var cts = new CancellationTokenSource(TotalTimeoutMilliseconds))
                {
                    cts.Token.Register(() => allAcksReceivedCompletion.TrySetCanceled());

                    while (acksReceived.Count < totalChunks && !cts.Token.IsCancellationRequested)
                    {
                        while (inflightPacketSendTimes.Count < WindowSize && nextChunkToSend < totalChunks)
                        {
                            if (!acksReceived.ContainsKey(nextChunkToSend))
                            {
                                SendChunk(endpoint, data, messageId, nextChunkToSend, totalChunks, prefix);
                                inflightPacketSendTimes[nextChunkToSend] = DateTime.UtcNow;
                            }
                            nextChunkToSend++;
                        }

                        DateTime timeoutThreshold = DateTime.UtcNow.AddMilliseconds(-RetryIntervalMilliseconds);
                        foreach (var inflightPacket in inflightPacketSendTimes)
                        {
                            if (inflightPacket.Value < timeoutThreshold)
                            {
                                int chunkToResend = inflightPacket.Key;
                                Console.WriteLine($"Resending timed-out chunk {chunkToResend} for message {messageId}");
                                SendChunk(endpoint, data, messageId, chunkToResend, totalChunks, prefix);
                                inflightPacketSendTimes[chunkToResend] = DateTime.UtcNow;
                            }
                        }

                        await Task.WhenAny(allAcksReceivedCompletion.Task, Task.Delay(100, cts.Token));
                    }
                }

                return allAcksReceivedCompletion.Task.IsCompletedSuccessfully;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"Transfer for message {messageId} timed out after {TotalTimeoutMilliseconds / 1000} seconds.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred during reliable send: {ex.Message}");
                return false;
            }
            finally
            {
                _udpService.UnregisterAckListener(messageId);
            }
        }

        private void SendChunk(IPEndPoint endpoint, byte[] data, string messageId, int chunkIndex, int totalChunks, string prefix)
        {
            int offset = chunkIndex * MaxPayloadSize;
            int chunkSize = Math.Min(MaxPayloadSize, data.Length - offset);
            byte[] chunkPayload = new byte[chunkSize];
            Buffer.BlockCopy(data, offset, chunkPayload, 0, chunkSize);

            string header = $"CHUNKED|{messageId}|{chunkIndex}|{totalChunks}|{prefix}|";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);

            byte[] packet = new byte[headerBytes.Length + chunkPayload.Length];
            Buffer.BlockCopy(headerBytes, 0, packet, 0, headerBytes.Length);
            Buffer.BlockCopy(chunkPayload, 0, packet, headerBytes.Length, chunkPayload.Length);

            _ = _udpService.SendAsync(packet, endpoint);
        }

        #endregion
    }
}
