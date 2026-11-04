using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Client.Services;

/// <summary>
/// WebSocket signaling client. Connects to <c>wss://server/ws</c> (or
/// <c>ws://</c>) with the <c>Authorization: Bearer ...</c> header — the
/// bearer is NEVER in the query string.
///
/// <para>Responsibilities:</para>
/// <list type="bullet">
///   <item>Auto-reconnect with exponential backoff (1s → 2s → 4s → ... → 30s cap).</item>
///   <item>Heartbeat every 30s.</item>
///   <item>Wrap outgoing envelopes with messageId + timestamp (anti-replay).</item>
///   <item>Dispatch incoming envelopes via <see cref="OnEnvelopeReceived"/>.</item>
/// </list>
/// </summary>
public sealed class SignalingClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = JsonOptions.Default;

    private readonly ClientOptions _opts;
    private readonly Func<string?> _bearerProvider;
    private readonly Func<long> _clock;
    private readonly Func<string> _messageIdFactory;

    private readonly CancellationTokenSource _cts = new();
    private Task? _connectTask;
    private int _started;

    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Raised when a complete envelope arrives. Subscribers are
    /// invoked sequentially on a background thread.</summary>
    public event Action<Envelope>? OnEnvelopeReceived;

    /// <summary>Raised when the connection state changes.</summary>
    public event Action<SignalingState>? OnStateChanged;

    public SignalingState State { get; private set; } = SignalingState.Disconnected;

    public enum SignalingState { Disconnected, Connecting, Online, Backoff, Closed }

    public SignalingClient(
        ClientOptions opts,
        Func<string?> bearerProvider,
        Func<long>? clock = null,
        Func<string>? messageIdFactory = null)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _bearerProvider = bearerProvider ?? throw new ArgumentNullException(nameof(bearerProvider));
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _messageIdFactory = messageIdFactory ?? (() => "m_" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>Start the connect / reconnect / heartbeat loop.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _connectTask = RunAsync(_cts.Token);
    }

    /// <summary>Send an envelope to the server. Sets messageId/timestamp if unset.</summary>
    public async Task SendAsync(Envelope envelope, CancellationToken ct = default)
    {
        var full = new Envelope
        {
            Version = ProtocolVersion.Current,
            MessageId = string.IsNullOrEmpty(envelope.MessageId) ? _messageIdFactory() : envelope.MessageId,
            SessionId = envelope.SessionId,
            SenderId = envelope.SenderId,
            Timestamp = envelope.Timestamp == 0 ? _clock() : envelope.Timestamp,
            Kind = envelope.Kind,
            Payload = envelope.Payload,
        };

        var json = JsonSerializer.Serialize(full, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws is null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("signaling socket is not connected");
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Send a heartbeat envelope. Server replies with a heartbeat ack.</summary>
    public Task SendHeartbeatAsync(CancellationToken ct = default)
        => SendAsync(new Envelope { Kind = MessageKinds.Heartbeat, Payload = default }, ct);

    /// <summary>Send an endSession envelope. Then close the socket.</summary>
    public async Task CloseAsync(string reason, CancellationToken ct = default)
    {
        try { await SendAsync(new Envelope { Kind = MessageKinds.EndSession, Payload = JsonSerializer.SerializeToElement(new { reason }) }, ct); }
        catch { /* best-effort */ }
        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                if (_ws is not null && _ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client_close", ct);
            }
            finally { _sendLock.Release(); }
        }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* best-effort */ }
        try { _connectTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _cts.Dispose();
        _sendLock.Dispose();
        try { _ws?.Dispose(); } catch { /* best-effort */ }
    }

    // ---- background loop ----

    private async Task RunAsync(CancellationToken ct)
    {
        var backoffSec = _opts.ReconnectInitialBackoffSec;
        var attemptsOnCurrentServer = 0;
        while (!ct.IsCancellationRequested)
        {
            SetState(SignalingState.Connecting);
            ClientWebSocket? ws = null;
            try
            {
                ws = new ClientWebSocket();
                var bearer = _bearerProvider();
                if (!string.IsNullOrEmpty(bearer))
                    ws.Options.SetRequestHeader("Authorization", $"Bearer {bearer}");
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(_opts.HeartbeatIntervalSec);
                ws.Options.SetRequestHeader("User-Agent", "YFGDESK/2.0");
                await ws.ConnectAsync(new Uri(_opts.ResolveWebSocketUrl()), ct);
                _ws = ws;
                SetState(SignalingState.Online);
                backoffSec = _opts.ReconnectInitialBackoffSec;
                attemptsOnCurrentServer = 0;

                // Run receive loop + heartbeat loop in parallel until either dies.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var receiveTask = ReceiveLoopAsync(ws, linked.Token);
                var heartbeatTask = HeartbeatLoopAsync(ws, linked.Token);
                await Task.WhenAny(receiveTask, heartbeatTask);
                linked.Cancel();
                try { await Task.WhenAll(receiveTask, heartbeatTask); } catch { /* expected on cancel */ }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Swallow and retry with backoff.
                _ = ex;
                attemptsOnCurrentServer++;
                // After N failures on the current server, advance to the next
                // server in the failover list.
                if (attemptsOnCurrentServer >= _opts.ReconnectAttemptsPerServer)
                {
                    _opts.AdvanceToNextServer();
                    attemptsOnCurrentServer = 0;
                    backoffSec = _opts.ReconnectInitialBackoffSec;
                }
            }
            finally
            {
                ws?.Dispose();
                _ws = null;
            }

            if (ct.IsCancellationRequested) break;
            SetState(SignalingState.Backoff);
            try { await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct); }
            catch (OperationCanceledException) { break; }
            backoffSec = Math.Min(backoffSec * 2, _opts.ReconnectMaxBackoffSec);
        }
        SetState(SignalingState.Closed);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (WebSocketException) { return; }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client_ack_close", ct); } catch { /* best-effort */ }
                return;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                var json = sb.ToString();
                sb.Clear();
                if (json.Length > 0 && json.Length <= 64 * 1024)
                {
                    try
                    {
                        var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonOpts);
                        if (envelope is not null)
                            OnEnvelopeReceived?.Invoke(envelope);
                    }
                    catch
                    {
                        // Malformed envelope — drop silently (server should reject upstream).
                    }
                }
            }
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(_opts.HeartbeatIntervalSec), ct); }
            catch (OperationCanceledException) { return; }
            try { await SendHeartbeatAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Send failure → receive loop will exit too.
                return;
            }
        }
    }

    private void SetState(SignalingState state)
    {
        State = state;
        try { OnStateChanged?.Invoke(state); } catch { /* best-effort */ }
    }
}
