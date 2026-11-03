using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using YfgDesk.Server.Data;
using YfgDesk.Shared.Crypto;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Signaling;

/// <summary>
/// Validates incoming WebSocket envelopes: structural parse, version check,
/// timestamp skew, anti-replay (per session), SDP/ICE validation, role enforcement.
/// Stateless; the replay-window bookkeeping lives in <see cref="SignalingSessionStore"/>.
/// </summary>
public sealed class SignalingValidator
{
    private readonly YfgDeskOptions _opts;

    public SignalingValidator(YfgDeskOptions opts)
    {
        _opts = opts;
    }

    /// <summary>
    /// Try to parse and structurally validate an envelope.
    /// </summary>
    public bool TryParseEnvelope(string text, [NotNullWhen(true)] out Envelope? env, out string? error)
    {
        env = null;
        error = null;
        if (string.IsNullOrEmpty(text)) { error = "empty message"; return false; }
        if (text.Length > 16 * 1024) { error = "message too large"; return false; }
        try
        {
            env = JsonSerializer.Deserialize<Envelope>(text, JsonOptions.Default);
            if (env is null) { error = "invalid JSON"; return false; }
        }
        catch (Exception ex) { error = $"invalid JSON: {ex.Message}"; return false; }

        if (env.Version != ProtocolVersion.Current)
        {
            error = $"unsupported protocol version {env.Version}";
            return false;
        }
        if (string.IsNullOrWhiteSpace(env.Kind))
        {
            error = "missing kind";
            return false;
        }
        if (string.IsNullOrWhiteSpace(env.MessageId))
        {
            error = "missing messageId";
            return false;
        }
        if (env.MessageId.Length > 64)
        {
            error = "messageId too long";
            return false;
        }
        // Timestamp skew check (ms since epoch).
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(nowMs - env.Timestamp) > _opts.TimestampSkewSeconds * 1000L)
        {
            error = "timestamp skew exceeded";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Validate an SDP offered in an offer/answer payload. Calls <see cref="Validators.IsValidSdp"/>.
    /// </summary>
    public bool TryGetSdp(Envelope env, [NotNullWhen(true)] out string? sdp, out string? error)
    {
        sdp = null;
        error = null;
        try
        {
            var dto = env.Payload.Deserialize<Payloads.OfferPayload>(JsonOptions.Default);
            if (dto is null || string.IsNullOrEmpty(dto.Sdp)) { error = "missing sdp"; return false; }
            if (!Validators.IsValidSdp(dto.Sdp)) { error = "invalid sdp"; return false; }
            sdp = dto.Sdp;
            return true;
        }
        catch (Exception ex) { error = $"invalid payload: {ex.Message}"; return false; }
    }

    /// <summary>
    /// Validate an ICE candidate payload.
    /// </summary>
    public bool TryGetIceCandidate(Envelope env, [NotNullWhen(true)] out Payloads.IceCandidatePayload? candidate, out string? error)
    {
        candidate = null;
        error = null;
        try
        {
            var dto = env.Payload.Deserialize<Payloads.IceCandidatePayload>(JsonOptions.Default);
            if (dto is null || string.IsNullOrEmpty(dto.Candidate)) { error = "missing candidate"; return false; }
            if (!Validators.IsValidIceCandidate(dto.Candidate)) { error = "invalid candidate"; return false; }
            candidate = dto;
            return true;
        }
        catch (Exception ex) { error = $"invalid payload: {ex.Message}"; return false; }
    }

    /// <summary>
    /// Validate a createSession payload.
    /// </summary>
    public bool TryGetCreateSession(Envelope env, [NotNullWhen(true)] out CreateSessionPayload? payload, out string? error)
    {
        payload = null;
        error = null;
        try
        {
            var dto = env.Payload.Deserialize<CreateSessionPayload>(JsonOptions.Default);
            if (dto is null) { error = "invalid payload"; return false; }
            if (!Validators.IsValidDeviceId(dto.HostId)) { error = "invalid hostId"; return false; }
            if (!Validators.IsValidDisplayName(dto.DisplayName)) { error = "invalid displayName"; return false; }
            if (!Validators.IsValidVideoPreference(dto.VideoWidth, dto.VideoHeight, dto.VideoFps, dto.VideoQualityMode))
            { error = "invalid video preference"; return false; }
            payload = dto;
            return true;
        }
        catch (Exception ex) { error = $"invalid payload: {ex.Message}"; return false; }
    }

    /// <summary>
    /// Validate a consentDecision payload.
    /// </summary>
    public bool TryGetConsentDecision(Envelope env, [NotNullWhen(true)] out Payloads.ConsentDecisionPayload? payload, out string? error)
    {
        payload = null;
        error = null;
        try
        {
            var dto = env.Payload.Deserialize<Payloads.ConsentDecisionPayload>(JsonOptions.Default);
            if (dto is null) { error = "invalid payload"; return false; }
            payload = dto;
            return true;
        }
        catch (Exception ex) { error = $"invalid payload: {ex.Message}"; return false; }
    }

    /// <summary>
    /// Validate an endSession payload.
    /// </summary>
    public bool TryGetEndSession(Envelope env, [NotNullWhen(true)] out Payloads.EndSessionPayload? payload, out string? error)
    {
        payload = null;
        error = null;
        try
        {
            var dto = env.Payload.Deserialize<Payloads.EndSessionPayload>(JsonOptions.Default);
            if (dto is null) { error = "invalid payload"; return false; }
            payload = dto;
            return true;
        }
        catch (Exception ex) { error = $"invalid payload: {ex.Message}"; return false; }
    }

    /// <summary>
    /// Validate a hostReady payload (should be empty but allow extras).
    /// </summary>
    public bool TryGetHostReady(Envelope env, out string? error)
    {
        error = null;
        // hostReady payload is empty {}. Just accept.
        return true;
    }
}

/// <summary>
/// createSession payload DTO. Carries the viewer's request to start a session.
/// </summary>
public sealed record CreateSessionPayload(
    string HostId,
    int Permissions,
    string DisplayName,
    int VideoWidth,
    int VideoHeight,
    int VideoFps,
    string VideoQualityMode)
{
    public VideoPreference ToVideoPreference() => new(VideoWidth, VideoHeight, VideoFps, VideoQualityMode);
}
