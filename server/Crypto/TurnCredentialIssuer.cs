using System.Security.Cryptography;
using System.Text;
using YfgDesk.Shared.Protocol;
using YfgDesk.Server.Data;

namespace YfgDesk.Server.Crypto;

/// <summary>
/// Issues coturn-style short-lived TURN credentials.
/// username = "{expiryEpoch}:{deviceId}"
/// credential = base64(HMAC-SHA1(secret, username))
/// </summary>
public sealed class TurnCredentialIssuer
{
    private readonly YfgDeskOptions _opts;

    public TurnCredentialIssuer(YfgDeskOptions opts)
    {
        _opts = opts;
    }

    /// <summary>
    /// Issue a credential bundle valid for the configured TTL (default 2 hours).
    /// Returns null if TURN is not configured.
    /// </summary>
    public TurnCredentialBundle? Issue(string deviceId)
    {
        if (!_opts.Turn.IsConfigured) return null;
        if (string.IsNullOrEmpty(deviceId)) return null;

        var expiry = DateTimeOffset.UtcNow.AddSeconds(_opts.Turn.TtlSeconds).ToUnixTimeSeconds();
        var username = $"{expiry}:{deviceId}";
        var credential = ComputeCredential(username, _opts.Turn.SharedSecret);
        var expiresAt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddSeconds(expiry);
        return new TurnCredentialBundle(
            username,
            credential,
            (string[])_opts.Turn.Urls.Clone(),
            expiresAt);
    }

    /// <summary>
    /// Build an <see cref="IceServer"/> for use in sessionStarted messages.
    /// </summary>
    public IceServer? ToIceServer(TurnCredentialBundle bundle)
    {
        if (bundle is null) return null;
        return new IceServer(bundle.Urls, bundle.Username, bundle.Credential);
    }

    /// <summary>
    /// Compute the coturn-style credential: base64(HMAC-SHA1(secret, username)).
    /// </summary>
    public static string ComputeCredential(string username, string secret)
    {
        if (string.IsNullOrEmpty(username)) throw new ArgumentException("username required", nameof(username));
        if (string.IsNullOrEmpty(secret)) throw new ArgumentException("secret required", nameof(secret));
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var usernameBytes = Encoding.UTF8.GetBytes(username);
        var hmac = HMACSHA1.HashData(keyBytes, usernameBytes);
        return Convert.ToBase64String(hmac);
    }
}

/// <summary>
/// A TURN credential bundle: username, credential, urls, expiry.
/// </summary>
public sealed record TurnCredentialBundle(
    string Username,
    string Credential,
    string[] Urls,
    DateTime ExpiresAt);
