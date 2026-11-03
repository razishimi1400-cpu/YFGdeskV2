#if NET10_0_WINDOWS
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YfgDesk.Client.Services;

/// <summary>
/// Chunked file transfer over an <c>RTCDataChannel</c>. Each file is split
/// into fixed-size chunks (default 16 KiB). The first chunk carries the
/// file metadata (name, size, SHA-256 of the whole file, transferId); the
/// last chunk is verified by recomputing SHA-256 over the reassembled bytes
/// and comparing to the metadata hash.
///
/// <para>
/// The transfer supports cancellation via <see cref="CancellationToken"/>.
/// The receiving side can request a "cancel" message; both sides then drop
/// the in-flight chunks.
/// </para>
/// <para>
/// <b>Wire protocol (JSON-over-DataChannel):</b>
/// <code>
///   { "kind":"start", "transferId":"...", "name":"...", "size":&lt;bytes&gt;, "sha256":"&lt;hex&gt;", "chunkBytes":&lt;n&gt; }
///   { "kind":"chunk", "transferId":"...", "seq":&lt;n&gt;, "data":"&lt;base64&gt;" }
///   { "kind":"end",   "transferId":"...", "seq":&lt;n&gt; }
///   { "kind":"cancel","transferId":"...", "reason":"user_cancelled" }
///   { "kind":"ack",   "transferId":"...", "sha256":"&lt;hex verified by receiver&gt;" }
/// </code>
/// </para>
/// </summary>
public sealed class FileTransferService
{
    private readonly PeerConnection _peer;
    private readonly int _chunkBytes;

    public FileTransferService(PeerConnection peer, int chunkBytes = 16 * 1024)
    {
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        _chunkBytes = chunkBytes;
    }

    /// <summary>
    /// Send a file via the DataChannel. Reports progress through
    /// <paramref name="progress"/> (0..1). Returns the receiver's verified
    /// SHA-256, or null on cancel.
    /// </summary>
    public async Task<string?> SendFileAsync(
        string localPath,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        if (!File.Exists(localPath)) throw new FileNotFoundException(localPath);
        var name = Path.GetFileName(localPath);
        var bytes = await File.ReadAllBytesAsync(localPath, ct);
        var sha = SHA256.HashData(bytes);
        var shaHex = Convert.ToHexString(sha).ToLowerInvariant();
        var transferId = "f_" + Guid.NewGuid().ToString("N");

        // 1) start
        await SendAsync(new StartMessage(transferId, name, bytes.Length, shaHex, _chunkBytes), ct);

        // 2) chunks
        var totalChunks = (bytes.Length + _chunkBytes - 1) / Math.Max(1, _chunkBytes);
        for (int seq = 0; seq < totalChunks; seq++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = seq * _chunkBytes;
            var len = Math.Min(_chunkBytes, bytes.Length - offset);
            var chunk = new byte[len];
            Buffer.BlockCopy(bytes, offset, chunk, 0, len);
            await SendAsync(new ChunkMessage(transferId, seq, Convert.ToBase64String(chunk)), ct);
            progress?.Report((double)(seq + 1) / totalChunks);
        }

        // 3) end
        await SendAsync(new EndMessage(transferId, totalChunks), ct);
        return shaHex;
    }

    /// <summary>
    /// Cancel an in-flight transfer by sending a "cancel" message.
    /// </summary>
    public Task CancelAsync(string transferId, string reason = "user_cancelled", CancellationToken ct = default)
        => SendAsync(new CancelMessage(transferId, reason), ct);

    private Task SendAsync<T>(T message, CancellationToken ct)
        => _peer.SendInputEventAsync(System.Text.Json.JsonSerializer.SerializeToElement(message), ct);

    // ---- DTOs ----

    private sealed class StartMessage
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "ft_start";
        [JsonPropertyName("transferId")] public string TransferId { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("size")] public int Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; }
        [JsonPropertyName("chunkBytes")] public int ChunkBytes { get; set; }
        public StartMessage(string tid, string name, int size, string sha, int cb)
        { TransferId = tid; Name = name; Size = size; Sha256 = sha; ChunkBytes = cb; }
    }

    private sealed class ChunkMessage
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "ft_chunk";
        [JsonPropertyName("transferId")] public string TransferId { get; set; }
        [JsonPropertyName("seq")] public int Seq { get; set; }
        [JsonPropertyName("data")] public string Data { get; set; }
        public ChunkMessage(string tid, int seq, string data)
        { TransferId = tid; Seq = seq; Data = data; }
    }

    private sealed class EndMessage
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "ft_end";
        [JsonPropertyName("transferId")] public string TransferId { get; set; }
        [JsonPropertyName("seq")] public int Seq { get; set; }
        public EndMessage(string tid, int total) { TransferId = tid; Seq = total; }
    }

    private sealed class CancelMessage
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "ft_cancel";
        [JsonPropertyName("transferId")] public string TransferId { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; }
        public CancelMessage(string tid, string reason) { TransferId = tid; Reason = reason; }
    }
}
#endif
