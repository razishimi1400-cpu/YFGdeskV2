#if NET10_0_WINDOWS
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Client.Services;

/// <summary>
/// <para>
/// WebRTC peer-connection wrapper built on a WebView2 instance that runs a
/// small HTML/JS harness (<c>MediaAssets/index.html</c>). The browser
/// provides:
/// </para>
/// <list type="bullet">
///   <item><c>navigator.mediaDevices.getDisplayMedia()</c> for screen capture (host role).</item>
///   <item><c>RTCPeerConnection</c> with built-in DTLS-SRTP (no native lib needed).</item>
///   <item><c>RTCDataChannel</c> for input events and file-transfer chunks.</item>
/// </list>
/// <para>
/// C# ↔ JS bridge:
///  - C# → JS: <c>CoreWebView2.PostWebMessageAsJson(...)</c> carrying envelopes
///    and SDP/ICE payloads in a tagged JSON shape.
///  - JS → C#: <c>window.chrome.webview.postMessage(...)</c> →
///    <see cref="CoreWebView2.WebMessageReceived"/>.
/// </para>
/// <para>
/// This is a clean-room reimplementation of the v1 <c>WebView2MediaBridge.cs</c>
/// adapted to the v2 <see cref="Envelope"/> shape and the three-tier
/// connection orchestrator.
/// </para>
/// </summary>
public sealed class PeerConnection : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = JsonOptions.Default;

    private readonly WebView2 _wv;
    private readonly string _mediaAssetsPath;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Raised when the JS harness sends a JSON message back to C#.</summary>
    public event Action<JsonElement>? OnJsMessage;

    /// <summary>Raised when an ICE candidate is gathered by the JS harness.</summary>
    public event Action<string, string?, int?>? OnIceCandidate;

    /// <summary>Raised when the local SDP offer/answer is ready.</summary>
    public event Action<string, string>? OnLocalSdp;

    public PeerConnection(WebView2 wv, string mediaAssetsPath)
    {
        _wv = wv ?? throw new ArgumentNullException(nameof(wv));
        _mediaAssetsPath = mediaAssetsPath ?? throw new ArgumentNullException(nameof(mediaAssetsPath));
    }

    /// <summary>
    /// Initialize the WebView2 environment and load the harness HTML. Must be
    /// called before any other method.
    /// </summary>
    public async Task InitializeAsync(string role, CancellationToken ct = default)
    {
        if (_initialized) return;
        await _wv.EnsureCoreWebView2Async();
        _wv.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _wv.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        var htmlPath = Path.GetFullPath(Path.Combine(_mediaAssetsPath, "index.html"));
        _wv.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "yfgdesk.media",
            Path.GetDirectoryName(htmlPath) ?? Directory.GetCurrentDirectory(),
            CoreWebView2HostResourceAccessKind.Allow);
        _wv.CoreWebView2.Navigate($"https://yfgdesk.media/index.html?role={role}");
        _initialized = true;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // Tell the harness which role we're playing.
        _ = PostToJsAsync(new JsMessage("init", JsonSerializer.SerializeToElement(new { role = _role })));
    }

    private string _role = "viewer";

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsJson();
            if (string.IsNullOrEmpty(json)) return;
            var msg = JsonSerializer.Deserialize<JsMessage>(json, JsonOpts);
            if (msg is null) return;

            switch (msg.Type)
            {
                case "iceCandidate":
                    var ice = JsonSerializer.Deserialize<IcePayload>(msg.Payload.GetRawText(), JsonOpts);
                    if (ice is not null)
                        OnIceCandidate?.Invoke(ice.Candidate, ice.SdpMid, ice.SdpMLineIndex);
                    break;
                case "offer":
                case "answer":
                    var sdp = JsonSerializer.Deserialize<SdpPayload>(msg.Payload.GetRawText(), JsonOpts);
                    if (sdp is not null)
                        OnLocalSdp?.Invoke(msg.Type, sdp.Sdp);
                    break;
                default:
                    OnJsMessage?.Invoke(msg.Payload);
                    break;
            }
        }
        catch { /* best-effort: malformed JS messages are dropped */ }
    }

    /// <summary>
    /// Send an SDP offer (viewer → host) to the harness for
    /// <c>RTCPeerConnection.setRemoteDescription</c>.
    /// </summary>
    public Task SendOfferAsync(string sdp, CancellationToken ct = default)
        => PostToJsAsync(new JsMessage("offer", JsonSerializer.SerializeToElement(new SdpPayload(sdp))));

    /// <summary>
    /// Send an SDP answer (host → viewer) to the harness.
    /// </summary>
    public Task SendAnswerAsync(string sdp, CancellationToken ct = default)
        => PostToJsAsync(new JsMessage("answer", JsonSerializer.SerializeToElement(new SdpPayload(sdp))));

    /// <summary>
    /// Send a remote ICE candidate to the harness for
    /// <c>RTCPeerConnection.addIceCandidate</c>.
    /// </summary>
    public Task SendIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct = default)
        => PostToJsAsync(new JsMessage("iceCandidate",
            JsonSerializer.SerializeToElement(new IcePayload(candidate, sdpMid, sdpMLineIndex))));

    /// <summary>
    /// Start screen capture (host role). The harness calls
    /// <c>navigator.mediaDevices.getDisplayMedia()</c> and adds the track
    /// to the RTCPeerConnection.
    /// </summary>
    public Task StartScreenCaptureAsync(CancellationToken ct = default)
        => PostToJsAsync(new JsMessage("startCapture", JsonSerializer.SerializeToElement(new { })));

    /// <summary>
    /// Send an input event (mouse/keyboard) over the input DataChannel.
    /// </summary>
    public Task SendInputEventAsync(JsonElement evt, CancellationToken ct = default)
        => PostToJsAsync(new JsMessage("input", evt));

    /// <summary>
    /// Request the JS harness to gracefully close the peer connection.
    /// </summary>
    public Task CloseAsync(CancellationToken ct = default)
        => PostToJsAsync(new JsMessage("close", JsonSerializer.SerializeToElement(new { })));

    private async Task PostToJsAsync(JsMessage message)
    {
        if (!_initialized || _disposed) return;
        var json = JsonSerializer.Serialize(message, JsonOpts);
        _wv.CoreWebView2.PostWebMessageAsJson(json);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_initialized)
            {
                _wv.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _wv.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
        }
        catch { /* best-effort */ }
        // Note: we do NOT dispose the WebView2 here — its lifetime is owned by the WPF Window.
    }

    // ---- DTOs ----

    private sealed class JsMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("payload")] public JsonElement Payload { get; set; }
        public JsMessage() { Type = ""; }
        public JsMessage(string type, JsonElement payload) { Type = type; Payload = payload; }
    }

    private sealed class SdpPayload
    {
        [JsonPropertyName("sdp")] public string Sdp { get; set; }
        public SdpPayload(string sdp) { Sdp = sdp; }
        public SdpPayload() { Sdp = ""; }
    }

    private sealed class IcePayload
    {
        [JsonPropertyName("candidate")] public string Candidate { get; set; }
        [JsonPropertyName("sdpMid")] public string? SdpMid { get; set; }
        [JsonPropertyName("sdpMLineIndex")] public int? SdpMLineIndex { get; set; }
        public IcePayload(string c, string? mid, int? idx) { Candidate = c; SdpMid = mid; SdpMLineIndex = idx; }
        public IcePayload() { Candidate = ""; }
    }
}
#endif
