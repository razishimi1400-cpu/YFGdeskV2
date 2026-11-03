# YFGDESK v2 — Worklog

This file tracks per-task progress for the YFGDESK v2 clean-room reimplementation.
Entries are append-only and ordered newest-first.

---

## Task `client-build` — Windows WPF client (.NET 10, multi-targeted)

**Date:** 2026-10
**Status:** ✅ Complete — multi-targeted build succeeds (both `net10.0` and `net10.0-windows`), 33/33 unit tests pass on `net10.0`.

### Scope

Build the YFGDESK v2 desktop client (Task 3 of the clean-room reimplementation).
The client is a single WPF app that multi-targets two TFMs so that the pure-C#
service layer can be unit-tested on Linux CI while the full WPF UI + WebView2
+ DPAPI + Argon2 + SendInput path runs on Windows.

### Files created

```
client/
  YfgDesk.Client.csproj             ← multi-target: net10.0-windows (WPF) + net10.0 (headless)
  App.xaml                           ← WPF Application definition
  App.xaml.cs                        ← DI graph (LocalCache, SecureStorage, DeviceIdentity,
                                       DiscoveryClient, SignalingClient, Orchestrator, etc.)
  MainWindow.xaml                    ← tabbed UI: Host / Connect / Unattended / Status
  MainWindow.xaml.cs                 ← code-behind: language toggle, copy device id
  Assets/
    yfgdesk-mark.svg                 ← brand mark (dark navy disc + teal Y + signal arcs)
  MediaAssets/
    index.html                       ← WebView2 WebRTC harness (getDisplayMedia + RTCPeerConnection + DataChannel)
  Resources/
    Theme.xaml                       ← brand palette + control styles (navy #0A1220, teal #2CE2CA)
  Services/
    Options.cs                       ← ClientOptions: server URL, ports, defaults
    LocalCache.cs                    ← %APPDATA%/YFGDESK/discovery.cache or ~/.config/yfgdesk/discovery.cache
    SecureStorage.cs                 ← ISecureStorage + DPAPI (Windows) / plaintext-0600 (Linux) + InMemory test stub
    DeviceIdentity.cs                ← ECDSA P-256 keypair, EnrollAsync/RenewBearerAsync, PKCS8+SPKI persistence
    SignalingClient.cs               ← ClientWebSocket to /ws, Authorization: Bearer header only,
                                       anti-replay messageId+timestamp, heartbeat every 30s, exp backoff reconnect
    DiscoveryClient.cs               ← UDP broadcast every 5s on port 49152, listen loop, Tier-2 query
    ConnectionOrchestrator.cs        ← Tier 1 cache → Tier 2 LAN broadcast → Tier 3 server lookup+ICE → Tier 4 TURN
    HostConsentCoordinator.cs        ← state machine: Idle → AwaitingConsent → Accepted/Rejected/TimedOut → Active → Ended
    TurnClient.cs                    ← REST POST /api/turn-credentials with 5-min-before-expiry refresh cache
                                       + ServerLookupClient (POST /api/lookup)
    UnattendedStore.cs               ← Argon2id verifier (Windows only), DPAPI-protected, 5-fail/15-min lockout,
                                       Linux stub throws PlatformNotSupportedException
    PeerConnection.cs                ← net10.0-windows only — WebView2 + RTCPeerConnection bridge
    ScreenCaptureService.cs          ← net10.0-windows only — getDisplayMedia via WebView2 harness
    InputInjectionService.cs         ← net10.0-windows only — User32!SendInput for mouse/keyboard
    FileTransferService.cs           ← net10.0-windows only — chunked DataChannel transfer + SHA-256 verify
  ViewModels/
    ObservableObject.cs              ← INotifyPropertyChanged base (cross-platform)
    MainViewModel.cs                 ← top-level VM + sticky-footer state
    HostViewModel.cs                 ← host panel VM (consent, share-screen, kill switch)
    ViewerViewModel.cs               ← viewer panel VM (target id validation, permissions, quality)
    UnattendedViewModel.cs           ← opt-in unattended VM + StatusViewModel
  Views/
    HostView.xaml + .cs              ← host panel (device id, accept/reject, screen picker)
    ViewerView.xaml + .cs            ← viewer panel (target id, permissions, quality, request)
    UnattendedView.xaml + .cs        ← toggle + password + unlock (PasswordBoxes wired in code-behind)
    StatusView.xaml + .cs            ← connection state + tier + heartbeat + last error
  Tests/
    YfgDesk.Client.Tests.csproj     ← xUnit, targets net10.0, references the client net10.0 output
    DeviceIdentityTests.cs           ← 5 tests: keypair round-trip, persist, wipe, default path, missing file
    HostConsentCoordinatorTests.cs   ← 11 tests: Begin/Accept subset check/Reject/Expire/Activate/End/EmergencyStop
    DiscoveryClientTests.cs          ← 5 tests: codec round-trip, query timeout, listen loop caching, query via cache
    ConnectionOrchestratorTests.cs   ← 8 tests: three-tier fallback (Tier1 cache hit, Tier1→Tier3 fallthrough,
                                       Tier3 P2P, Tier3 offline→Tier4, Tier4 unavailable, all-fail, empty id, TierChanged)
```

The `shared/` project was used as-is — no helpers were added (the existing
`CrockfordBase32`, `Hasher`, `Validators`, `Envelope`, `JsonOptions` covered all needs).

### Build result

```
$ dotnet build shared/YfgDesk.Shared.csproj -c Release
Build succeeded.    0 Warning(s)    0 Error(s)

$ dotnet build client/YfgDesk.Client.csproj -c Release -f net10.0
Build succeeded.    0 Warning(s)    0 Error(s)

$ dotnet build client/YfgDesk.Client.csproj -c Release -f net10.0-windows
Build succeeded.    0 Warning(s)    0 Error(s)
```

Notably, both TFMs build cleanly even on Linux. The .NET 10 SDK ships enough
of the Windows reference assemblies (`Microsoft.WindowsDesktop.App.Ref`) for
`net10.0-windows` to compile against `PresentationFramework`/`System.Windows`/
`Microsoft.Web.WebView2`/`Konscious.Security.Cryptography.Argon2` from the
NuGet packages alone — no Windows SDK install required for compilation.
(Actually running the WPF app still requires Windows because the WPF
rendering stack is Windows-only; the SDK lets us compile-check from Linux CI.)

### Test result

```
$ dotnet test client/Tests/YfgDesk.Client.Tests.csproj -c Release -f net10.0
Passed!  - Failed: 0, Passed: 33, Skipped: 0, Total: 33, Duration: 5 s
```

Tests cover:
- **DeviceIdentity** (5): keypair generation, sign/verify round-trip, persist via ISecureStorage, wipe, missing-file.
- **HostConsentCoordinator** (11): full state machine — Begin, Accept subset enforcement, Reject, Expire-after-deadline, Accept-after-deadline, Activate-from-Accepted, End-from-Active, EmergencyStop-from-anywhere, Reset, Snapshot.
- **DiscoveryClient** (5): beacon codec round-trip, malformed payload rejection, query timeout, listen loop caches incoming beacons, query via pre-populated cache.
- **ConnectionOrchestrator** (8): Tier 1 cache hit + DirectLan success, Tier 1 cache hit + DirectLan fail → falls to Tier 3 P2P, Tier 3 server-lookup online + P2P success, Tier 3 target-offline → falls to Tier 4 Relay, Tier 4 TURN-unavailable → Failed, all-tiers-fail surfaces last error, empty target id, TierChanged event fires for all 4 tiers.

### Architecture highlights

1. **Multi-targeting** — single csproj builds both `net10.0-windows` (WPF, WinExe, WebView2, DPAPI, Argon2) and `net10.0` (Library, headless, Linux-testable). The `<Compile Remove="..." />` glob in `Condition="'$(TargetFramework)' != 'net10.0-windows'"` excludes WPF/WebView2/SendInput/Argon2 files from the headless build.
2. **Three-tier orchestrator** — `ConnectionOrchestrator.ConnectAsync` walks Tier 1 (cache + direct TCP) → Tier 2 (LAN broadcast query 3s + direct TCP) → Tier 3 (server lookup + ICE P2P) → Tier 4 (TURN relay fallback). Each tier is a try/catch with a `lastFailure` accumulator; failures fall through silently and only the final error surfaces to the UI. `TierChanged` event lets the UI show the current tier indicator live.
3. **Bearer in header, never in query** — `DeviceIdentity.CreateHttpClient` injects a `BearerMessageHandler` that adds `Authorization: Bearer ...` to every request. `SignalingClient` does the same for the WebSocket upgrade via `ClientWebSocket.Options.SetRequestHeader`. No bearer ever lands in a URL.
4. **DeviceIdentity persistence** — P-256 keypair stored as PKCS8 (private) + SPKI (public), DPAPI-protected on Windows, plaintext + 0600 on Linux, via `ISecureStorage`. `LoadAsync` re-imports the PKCS8 and verifies the curve OID is `1.2.840.10045.3.1.7` (P-256).
5. **SignalingClient** — auto-reconnect with exponential backoff (1s → 2s → 4s → ... → 30s cap), parallel receive + heartbeat loops under a linked `CancellationTokenSource`, send serialization via `SemaphoreSlim` (WebSocket `SendAsync` is not thread-safe), 32 KiB receive buffer with 64 KiB total envelope cap.
6. **HostConsentCoordinator** — strict subset enforcement on `Accept`: host can grant only bits that the viewer requested, never more. `EmergencyStop` is idempotent and clears all permissions, transitioning to Ended from any non-terminal state.
7. **UnattendedStore** — Argon2id (RFC 9106) with 16-byte salt, 3 iterations, 64 MiB memory cost, parallelism 2, 32-byte output, constant-time compare. Verifier persisted via DPAPI on Windows. Linux stub throws `PlatformNotSupportedException` so the rest of the client compiles. Lockout after 5 failures for 15 minutes (in-memory only — restarting resets, intentional to prevent self-DoS).
8. **PeerConnection (Windows only)** — wraps a `WebView2` instance hosting `MediaAssets/index.html` (a clean-room reimplementation of the v1 `WebView2MediaBridge.cs`). The harness does `getDisplayMedia()` for the host role, `RTCPeerConnection` with built-in DTLS-SRTP for media, and `RTCDataChannel` for input events + file transfer chunks. C# ↔ JS bridge via `PostWebMessageAsJson` / `WebMessageReceived`.
9. **InputInjectionService (Windows only)** — `User32!SendInput` with `INPUT_MOUSE` / `INPUT_KEYBOARD` structs. Mouse moves normalized to 0..65535 for `MOUSEEVENTF_ABSOLUTE`. Linux stub throws `PlatformNotSupportedException`.
10. **Brand** — dark navy `#0A1220` background + teal `#2CE2CA` accent, FA-RTL default with EN-LTR toggle in the top bar. Strings are inline in XAML (no resx for v2.0; can be promoted to `Strings.fa.resx` / `Strings.en.resx` in v2.1).
11. **`InternalsVisibleTo`** — the client exposes `DiscoveryClient.EncodeBeacon` / `TryDecodeBeacon` (internal helpers) to the tests project so the codec can be exercised without touching the network.

### Notable design choices

- **`ConnectionResult` is a single discriminated record** — `record ConnectionResult(ConnectionTier Tier, string TargetId, string? Ip = null, int? Port = null, IceServer[]? IceServers = null, string? TurnUsername = null, string? FailureReason = null, TimeSpan Elapsed = default)`. A nested-record hierarchy was tried first but positional record members can't override abstract base init-only properties — the single-record form is simpler and matches the discriminated-union intent.
- **`IUdpTransport` abstraction** — `DiscoveryClient` takes an `IUdpTransport` (production `SystemUdpTransport` uses `UdpClient`; tests use `InMemoryUdpTransport` with an unbounded channel). This keeps the broadcast/listen loop unit-testable without touching the real network.
- **`EnableWindowsTargeting=true`** — set per-TFM on the Windows target so NuGet restore succeeds on Linux (restore only downloads packages; it never invokes Windows reference assemblies). This is the standard property for cross-compiling Windows TFMs from non-Windows hosts.
- **`OperatingSystem.IsWindows()` runtime branch in `SecureStorage`** — rather than `#if NET10_0_WINDOWS` (which would compile out the Linux path entirely), the runtime check lets the same source file ship on both TFMs. The DPAPI impl is guarded by `#if NET10_0_WINDOWS` so the type name `System.Security.Cryptography.ProtectedData` doesn't need a NuGet ref on the headless target.
- **`Argon2` + `WebView2` packages gated by TFM** — `Condition="'$(TargetFramework)' == 'net10.0-windows'"` so the headless target doesn't pull in native Windows libs.
- **Tests reference the multi-targeted client** — when a multi-targeted project is consumed, NuGet picks the consumer's TFM. The tests csproj targets `net10.0`, so it automatically uses the client's `net10.0` output (which doesn't reference WebView2 / Argon2).

### Limitations / follow-ups

1. **The WPF UI was not run** — per task instructions ("Do NOT run the client — it's WPF — only runs on Windows"). The XAML compiles and code-behind wiring is in place, but visual layout has not been validated on Windows.
2. **No /api/heartbeat REST endpoint exists** — per the server worklog, heartbeats are WebSocket-only. The `SignalingClient` heartbeats correctly via `Envelope{kind:"heartbeat"}`. A REST-only "keep-alive" agent would need a separate endpoint.
3. **mDNS not implemented** — per the spec ("if too complex, just UDP broadcast is enough for v2.0"). `ClientOptions.MdnsServiceType` is reserved for `_yfgdesk._tcp.local` in v2.1.
4. **No WebView2 runtime detection** — the client assumes WebView2 Runtime is installed. A v2.1 follow-up should bundle the Evergreen Bootstrapper or surface a "download WebView2 Runtime" dialog.
5. **PeerConnection is host-process for the WebView2** — the `WebView2` control is owned by the WPF Window; `PeerConnection.Dispose` doesn't dispose it (just unhooks events). This matches the v1 design but means the harness outlives a single session — re-init on reconnect is required.
6. **`UnattendedStore` lockout is in-memory only** — restarting the client clears the counter. This is intentional (prevents a permanent self-DoS where the legitimate owner can't unlock after a forgotten password) but means a determined attacker could brute-force by crashing the client. A v2.1 follow-up could persist the failure counter to DPAPI.
7. **No device-id formatting in the UI text boxes** — the XAML `TextBox` for target ID accepts up to 11 chars (9 digits + 2 spaces). Auto-formatting `123456789` → `123 456 789` (like AnyDesk) is a v2.1 polish item.
8. **Solution file (`YfgDesk.slnx`) not updated** — the client + tests projects are not registered in the solution file. Add `<Folder Name="/client/">` and `<Project Path="client/YfgDesk.Client.csproj" />` etc. in a follow-up so `dotnet build YfgDesk.slnx` includes the client.
9. **Server tests still pass** — confirmed `25/25` after the client was added. The server project was not modified (read-only as instructed).

### What was NOT done (out of scope for this task)

- Did NOT run the client (only `dotnet build` and `dotnet test`).
- Did NOT modify the `shared/` project (read-only; existing helpers were sufficient).
- Did NOT modify the `server/` project (read-only; verified server tests still pass 25/25 after client was added).
- Did NOT write deploy artifacts (Dockerfile, MSIX packaging, code-signing) — that's a later task.
- Did NOT implement a real TURN client socket (only the REST credentials fetcher; the actual UDP/TCP relay traffic is handled by the WebView2 RTCPeerConnection stack, which uses the TURN credentials via `iceServers`).

---

## Task `server-build` — ASP.NET Core 10 signaling + discovery server

**Date:** 2026-10
**Status:** ✅ Complete — solution builds with 0 errors, 25/25 integration tests pass.

### Scope

Build the central server for YFGDESK v2 (Task 2 of the clean-room reimplementation).
The server is a single self-contained ASP.NET Core 10 binary that does:

- **REST** — `/api/health`, `/api/enroll/challenge`, `/api/enroll/register`,
  `/api/lookup`, `/api/turn-credentials`.
- **WebSocket signaling** — `/ws` carrying JSON envelopes per `YfgDesk.Shared.Protocol.Envelope`.
- **SQLite** persistence via EF Core for `devices`, `sessions`, `audit_events`.
- **Background sweep service** for expired challenges / idle sessions / stale devices.

### Files created

```
server/
  Program.cs                              ← DI, Kestrel, security headers, route mapping, SweepService
  appsettings.json                       ← default config
  appsettings.Development.json            ← dev overrides
  YfgDesk.Server.csproj                   ← (updated: added Tests\** compile-exclude glob)
  Data/
    YfgDeskDbContext.cs                   ← EF Core DbContext (DateTime-only, SQLite)
    Entities.cs                            ← DeviceEntity / SessionEntity / AuditEventEntity
    YfgDeskOptions.cs                      ← config options (DB, rate limits, TTLs, TURN)
  Crypto/
    EcdsaVerifier.cs                       ← P-256 SPKI import + DER ECDSA verification
    DeviceIdGenerator.cs                   ← 9-digit random id, DB-collision-checked
    SessionIdGenerator.cs                  ← "s_" + 22 Crockford base32 chars
    BearerTokenIssuer.cs                   ← yfg.<64 hex>, SHA-256 digest storage, constant-time compare
    TurnCredentialIssuer.cs                ← coturn HMAC-SHA1 short-lived credentials
  Api/
    EndpointBase.cs                        ← shared helpers: rate limit, audit, JSON write, error write
    EnrollChallengeEndpoint.cs             ← POST /api/enroll/challenge
    EnrollRegisterEndpoint.cs              ← POST /api/enroll/register (idempotent on digest)
    LookupEndpoint.cs                      ← POST /api/lookup (bearer-auth)
    TurnCredentialsEndpoint.cs             ← POST /api/turn-credentials (bearer-auth)
    HealthEndpoint.cs                      ← GET /api/health (public)
    RateLimiter.cs                         ← in-memory sliding-window per-IP / per-device
    AuditLog.cs                            ← writes to audit_events; caps Detail at 256 chars; never logs secrets
    ChallengeStore.cs                      ← bounded (4096) in-memory challenge store with lazy prune
  Signaling/
    SignalingHub.cs                        ← /ws accept + dispatch + lifecycle transitions
    SignalingSessionStore.cs               ← in-memory connection + session state, replay window
    SignalingValidator.cs                  ← envelope parse, timestamp skew, SDP/ICE validation, role check
    SweepService.cs                        ← BackgroundService (every 30s) for expired sessions + challenges
  Tests/
    EnrollFlowTests.cs                     ← 9 tests: challenge → register → bearer → renew → lookup → turn
    SignalingContractTests.cs              ← 8 tests: camelCase envelope, replay, timestamp skew, role check
    SessionLifecycleTests.cs               ← 3 tests: PENDING→ACCEPTED→ACTIVE→ENDED, reject, idle timeout
    RateLimitTests.cs                      ← 4 tests: per-IP enroll limit, per-device WS limit, secret-in-query
    TestServerFactory.cs                   ← WebApplicationFactory<Program> with per-test SQLite file
    CryptoTestUtils.cs                     ← P-256 keypair + nonce-sign helper for tests
```

The `shared/` project was used as-is — no helpers were added (the existing
`CrockfordBase32`, `Hasher`, `Validators`, `Envelope`, `JsonOptions` covered all needs).

### Build result

```
$ dotnet build YfgDesk.slnx -c Release
...
Build succeeded.
    8 Warning(s)    ← all are NuGet vulnerability advisories on transitive deps
    0 Error(s)
```

### Test result

```
$ dotnet test server/Tests/YfgDesk.Tests.csproj -c Release --no-build
...
Passed!  - Failed: 0, Passed: 25, Skipped: 0, Total: 25, Duration: 5 s
```

Tests cover:
- enroll challenge issuance + nonce + SPKI validation
- enroll register signature verification + bearer issuance + digest-only storage
- lookup with bearer auth (and 401 without)
- turn-credentials endpoint (disabled path when env vars unset)
- re-enroll with same key yields same deviceId but fresh bearer
- WS upgrade requires Authorization header (no query-string bearer)
- WS upgrade emits `register` envelope in camelCase
- envelope timestamp skew ±5s rejected, malformed JSON rejected
- heartbeat ack, hostReady ack
- role enforcement: host cannot send `offer`; only viewer can
- anti-replay: duplicate messageId rejected on second `offer`
- full session lifecycle: PENDING → ACCEPTED → ACTIVE → ENDED with DB consistency
- host reject path → viewer notified with reason `host_rejected`
- idle timeout (2s in tests) → sweep tears down session, notifies both peers, DB row updated
- per-IP rate limit on /api/enroll/* → 429 after 4 requests
- per-device WS rate limit → `error.rate_limited` after threshold
- query string secret rejection on every endpoint

### Security checklist (verified by tests + code review)

1. ✅ Bearer in `Authorization: Bearer ...` header only; any `?token=` / `?bearer=` / `?password=` / `?secret=` rejected globally with 400.
2. ✅ Security headers on every response: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`. HSTS only when TLS.
3. ✅ Body size cap: 32 KiB globally (Kestrel `MaxRequestBodySize`), 16 KiB per endpoint (`EndpointBase.ReadBodyAsync`).
4. ✅ No secrets in logs: `AuditLog.SanitizeDetail` strips `yfg.`, `bearer`, `nonce`, `signature`, `sdp`, `password`, `secret`, `credential`, `a=fingerprint`, `candidate:`, `hmac` fragments before writing.
5. ✅ Audit every security event: `enroll.challenge_issued`, `enroll.signature_invalid`, `enroll.issued`, `enroll.reissued`, `session.created`, `session.accept`, `session.rejected`, `session.expired`, `session.ended`, `ws.connect`, `ws.disconnect`, `ws.host_ready`, `error.malformed`, `error.forbidden`, `error.ratelimit`, `error.replay`, `lookup.hit`, `lookup.miss`, `turn.issued`, `turn.disabled`.
6. ✅ Anti-replay per session: sliding window of `ReplayWindowSize` (default 512) messageIds via `SignalingSessionState.TryRecordMessageId`; ±`TimestampSkewSeconds` (default 120s) timestamp skew check in `SignalingValidator.TryParseEnvelope`.
7. ✅ Rate limits: 60/min per IP on `/api/enroll/*`, 60/min per device on `/ws` messages. Configurable via `YfgDeskOptions`.
8. ✅ Role enforcement: `offer` only from `ViewerDeviceId`, `answer` only from `HostDeviceId`, `consentDecision` only from `HostDeviceId`. Malformed messages tear down ONLY the offending session — the shared Host WebSocket stays open.
9. ✅ Session lifecycle: `Pending` (5min consent wait) → `Accepted` → `Active` (on first answer) → `Ended`. Idle timeout 2min, max 12h, sweep every 30s.
10. ✅ Bounded challenge store: max 4096 entries, lazy prune on every access + periodic prune in `SweepService`.

### Notable design choices

- **DateTime (UTC) everywhere** — SQLite provider can't translate `DateTimeOffset` LINQ comparisons. All entity timestamps and runtime comparisons use `DateTime.UtcNow`.
- **Bearer storage** — only the SHA-256 hex digest of the bearer token is persisted. Comparisons use `Validators.ConstantTimeEquals`. Re-enroll with the same SPKI yields the same `deviceId` but a fresh bearer (single active bearer per device).
- **Anti-replay window** — implemented as a `LinkedList<string>` + `HashSet<string>` per `SignalingSessionState`, bounded LRU. `TryRecordMessageId` is `lock`-protected.
- **WebSocket send serialization** — each `SignalingConnection` owns a `SemaphoreSlim` `SendLock` to serialize `SendAsync` (which is not thread-safe).
- **Per-connection send buffer** — 16 KiB receive buffer, 32 KiB total message cap (oversized messages close the socket with `WebSocketCloseStatus.MessageTooBig`).
- **Sweep ordering** — DB row updated FIRST, then peers notified, so callers polling the DB observe the new state before the WS message arrives.
- **Test isolation** — `TestServerFactory` creates a fresh SQLite file per instance (`Path.Combine(Path.GetTempPath(), $"yfgdesk-test-{Guid}.db")`) and deletes it on `Dispose`. Tighter TTLs/limits enable fast tests.
- **TestServer WebSocket client** — uses `factory.Server.CreateWebSocketClient()` with `ConfigureRequest = req => req.Headers["Authorization"] = "Bearer ..."`. Upgrade failures throw `InvalidOperationException("Incomplete handshake, status code: 401")` (not `WebSocketException`).
- **`Tests\**` compile-exclude glob** — added to `YfgDesk.Server.csproj` so the test files inside `server/Tests/` aren't double-compiled into the server project.

### Limitations / follow-ups for the client team

1. **TURN credentials are not issued unless `YFGDESK_TURN_URLS` + `YFGDESK_TURN_SHARED_SECRET` env vars are set.** When unset, `/api/turn-credentials` returns `{ turnAvailable: false, urls: [] }` and `sessionStarted.iceServers` is empty. Client must handle this gracefully (P2P-only or surface "TURN not configured" to the user).
2. **`senderId` is set by the server on relayed envelopes** — clients should trust `envelope.senderId` from the server, not any client-supplied value (the DTO ignores it; the server overwrites).
3. **`heartbeat` ack uses a fresh `messageId`** of the form `m_server_<guid>` (not the requester's `messageId`). Clients correlating acks by `messageId` should instead correlate by `kind == "heartbeat"`. This is documented in `SignalingHub.OnHeartbeatAsync`.
4. **No persistent bearer blacklist** — when a device re-enrolls, the old bearer's digest is simply overwritten in the DB. Old bearer tokens silently stop working. If the client wants "logout + revoke" UX, file a follow-up to add a `BearerRevokedAt` column.
5. **ForwardedHeaders configured but `KnownIPNetworks`/`KnownProxies` cleared** — trusting any upstream. For production behind a known Caddy, populate these to prevent IP spoofing. (Note: `KnownNetworks` is deprecated in .NET 10 in favor of `KnownIPNetworks` — already migrated.)
6. **No migrations** — uses `db.Database.EnsureCreated()` on startup (per task spec). If the schema evolves, switch to EF Core migrations before the first production deploy.
7. **`audit_events` table grows unbounded** — no retention sweep. Follow-up: add a background job that deletes rows older than N days (default 90).
8. **WebSocket close from server** — when a device reconnects with the same `deviceId`, the old connection is closed with `WebSocketCloseStatus.PolicyViolation` and reason `"replaced"`. The client should treat this as "session moved" and surface appropriately.
9. **`DisplayName` is sanitized** server-side (control chars stripped, whitespace collapsed). Clients should not assume their submitted display name round-trips verbatim.
10. **No /api/heartbeat REST endpoint** — heartbeats are WebSocket-only (per architecture doc §3.3). REST-only clients (e.g., headless agents that just want to keep `online = true`) should open a WebSocket and send `heartbeat` envelopes.

### What was NOT done (out of scope for this task)

- Did NOT run the server (only `dotnet build` and `dotnet test`).
- Did NOT modify the `shared/` project (read-only; existing helpers were sufficient).
- Did NOT write deploy artifacts (Dockerfile, Caddyfile) — that's a later task.
- Did NOT implement the coturn TURN server itself — only the credential issuer that talks to a separately-deployed coturn.
