using System.Text.Json;
using System.Text.Json.Serialization;

namespace YfgDesk.Shared.Protocol;

/// <summary>
/// Signaling protocol version. Bumped on incompatible changes.
/// </summary>
public static class ProtocolVersion
{
    public const int Current = 1;
}

/// <summary>
/// A signaling envelope. All WebSocket messages are wrapped in this shape.
/// Field naming is camelCase (JsonSerializerDefaults.Web).
/// </summary>
public sealed class Envelope
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("messageId")]
    public string MessageId { get; init; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>
    /// Set by the SERVER from the bearer token. Client-supplied values are ignored.
    /// </summary>
    [JsonPropertyName("senderId")]
    public string? SenderId { get; init; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

/// <summary>
/// All message kinds used in protocol v1.
/// </summary>
public static class MessageKinds
{
    public const string Register = "register";
    public const string HostReady = "hostReady";
    public const string CreateSession = "createSession";
    public const string IncomingRequest = "incomingRequest";
    public const string ConsentDecision = "consentDecision";
    public const string SessionStarted = "sessionStarted";
    public const string Offer = "offer";
    public const string Answer = "answer";
    public const string IceCandidate = "iceCandidate";
    public const string Heartbeat = "heartbeat";
    public const string EndSession = "endSession";
    public const string TurnConfig = "turnConfig";
    public const string Error = "error";
}

/// <summary>
/// Session lifecycle: PENDING (awaiting consent) → ACCEPTED → ACTIVE → ENDED/TIMED_OUT.
/// </summary>
public enum SessionState
{
    Pending,
    Accepted,
    Active,
    Ended,
    Rejected,
    TimedOut,
}

/// <summary>
/// Session permissions requested by the viewer and granted by the host.
/// </summary>
[Flags]
public enum Permissions
{
    None = 0,
    ViewScreen = 1 << 0,
    RemoteControl = 1 << 1,
    FileTransfer = 1 << 2,
}

/// <summary>
/// Video quality preference sent by the viewer in createSession.
/// </summary>
public sealed record VideoPreference(
    int Width,
    int Height,
    int FramesPerSecond,
    string QualityMode);

/// <summary>
/// Convenience payload DTOs (used as <c>Envelope.Payload</c> after deserialization).
/// </summary>
public static class Payloads
{
    public sealed record IncomingRequestPayload(
        string SessionId,
        string ViewerId,
        string ViewerDisplayName,
        int Permissions,
        VideoPreference VideoPreference,
        long ExpiresAt);

    public sealed record ConsentDecisionPayload(
        bool Accept,
        int GrantedPermissions);

    public sealed record SessionStartedPayload(
        string SessionId,
        string Role,
        int GrantedPermissions,
        VideoPreference VideoPreference,
        IceServer[] IceServers,
        long ExpiresAt);

    public sealed record OfferPayload(string Sdp);
    public sealed record AnswerPayload(string Sdp);
    public sealed record IceCandidatePayload(
        string Candidate,
        string? SdpMid,
        int? SdpMLineIndex);

    public sealed record EndSessionPayload(string Reason);

    public sealed record ErrorPayload(string Code, string Detail, string? InReplyTo);

    public sealed record HeartbeatPayload(long ServerTime);
}

/// <summary>
/// ICE server descriptor (STUN/TURN) used in sessionStarted and TURN credentials endpoint.
/// </summary>
public sealed record IceServer(
    string[] Urls,
    string? Username,
    string? Credential);
