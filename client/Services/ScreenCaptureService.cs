#if NET10_0_WINDOWS
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YfgDesk.Client.Services;

/// <summary>
/// Screen-capture service for the HOST role. Delegates to the browser via
/// <c>navigator.mediaDevices.getDisplayMedia()</c> running inside the
/// <see cref="PeerConnection"/>'s WebView2 harness.
///
/// <para>
/// No native Windows Graphics Capture interop is used in v2.0 — the WebView2
/// runtime handles DTLS-SRTP encoding, ICE candidate gathering, and frame
/// pacing for us. A v2.1 follow-up may add native capture via
/// <c>Windows.Graphics.Capture.GraphicsCaptureItem</c> for lower latency.
/// </para>
/// </summary>
public sealed class ScreenCaptureService
{
    private readonly PeerConnection _peer;
    private int _width;
    private int _height;
    private int _fps;
    private string _qualityMode;

    public ScreenCaptureService(PeerConnection peer, int width, int height, int fps, string qualityMode)
    {
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        _width = width; _height = height; _fps = fps; _qualityMode = qualityMode;
    }

    /// <summary>
    /// Start screen capture. The harness will:
    /// 1) prompt the user to pick a window/monitor,
    /// 2) add the resulting MediaStream to the RTCPeerConnection as the video sender.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new CapturePrefs
        {
            Width = _width, Height = _height, Fps = _fps, QualityMode = _qualityMode,
        });
        return _peer.StartScreenCaptureAsync(ct);
    }

    /// <summary>
    /// Update video preferences mid-session. The harness will call
    /// <c>RTCRtpSender.setParameters()</c> with the new maxBitrate.
    /// </summary>
    public Task UpdatePreferencesAsync(int width, int height, int fps, string qualityMode, CancellationToken ct = default)
    {
        _width = width; _height = height; _fps = fps; _qualityMode = qualityMode;
        var payload = JsonSerializer.SerializeToElement(new CapturePrefs
        {
            Width = width, Height = height, Fps = fps, QualityMode = qualityMode,
        });
        // The PeerConnection exposes SendInputEventAsync for arbitrary JSON;
        // for capture-pref changes we piggyback on a "capturePrefs" message.
        return _peer.SendInputEventAsync(payload, ct);
    }

    public sealed class CapturePrefs
    {
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("fps")] public int Fps { get; set; }
        [JsonPropertyName("qualityMode")] public string QualityMode { get; set; } = "Standard";
    }
}
#endif
