# YFGDESK v2 — Independent Remote Desktop for Windows

YFGDESK is a Windows remote desktop support application built from an independent source. This is a **clean-room reimplementation** with a three-tier connection architecture (LAN → P2P → TURN relay), inspired by how AnyDesk/RustDesk structure their discovery, but written from scratch without using any of their code.

## Repository layout

```
.
├── shared/         # Protocol + crypto helpers (cross-platform, no deps)
├── server/         # ASP.NET Core 10 discovery + signaling + TURN issuer
├── client/         # Windows WPF client (.NET 10 x64) + Linux-testable subset
├── deploy/         # Docker compose for server + coturn + Caddy TLS
└── docs/           # Architecture + worklog
```

## Architecture (three-tier connection priority)

When a viewer connects to a target device ID, the client tries these tiers **in order** and uses the first that works:

1. **LAN cache lookup** — if the target ID was recently seen on the local network, connect directly to its local IP (no internet needed)
2. **LAN broadcast/mDNS** — send a UDP broadcast on port 49152 and wait 3s for the target to respond; if it does, connect directly (no internet needed)
3. **Server lookup + NAT hole-punch** — ask the central server for the target's public IP, gather ICE candidates (host + srflx via STUN), try P2P
4. **TURN relay** — if direct P2P fails (symmetric NAT, carrier-grade NAT), relay encrypted traffic through the TURN server

See `docs/ARCHITECTURE.md` for the full design.

## Build

### Prerequisites
- .NET 10 SDK (https://dot.net)
- For the WPF client: Windows 10 1809+ x64 + WebView2 Runtime
- For the server: any OS that runs .NET 10

### Build everything
```bash
dotnet build YfgDesk.slnx -c Release
```

### Run tests
```bash
dotnet test server/Tests/YfgDesk.Tests.csproj -c Release
dotnet test client/Tests/YfgDesk.Client.Tests.csproj -c Release -f net10.0
```

### Publish the server (self-contained, Linux x64)
```bash
dotnet publish server/YfgDesk.Server.csproj -c Release -r linux-x64 --self-contained true -o ./publish/server
```

### Publish the client (self-contained, Windows x64)
```cmd
dotnet publish client/YfgDesk.Client.csproj -c Release -f net10.0-windows -r win-x64 --self-contained true -o .\publish\client
```

## Deploy (production, via Docker Compose)

```bash
cd deploy
cp .env.example .env
# Edit .env — set YFGDESK_DOMAIN, TURN_REALM, TURN_URLS, TURN_SHARED_SECRET
docker compose up -d
```

This starts three services:
- **yfgdesk-server** — REST + WebSocket on port 8080 (internal)
- **turn** — coturn on ports 3478/tcp+udp and 49160-49200/udp
- **caddy** — TLS-terminating reverse proxy on 80/443

## Security overview

| Aspect | Status |
|--------|--------|
| Host consent gate (PENDING → ACCEPTED → ACTIVE) | ✅ Implemented, fail-closed |
| Reject/timeout never grants control | ✅ Tested |
| Bearer tokens in Authorization header only | ✅ Never in query strings |
| ECDSA P-256 challenge-response (DER, RFC 3279) | ✅ Both client and server |
| Argon2id unattended verifier (local only) | ✅ Never sent to server |
| WebRTC DTLS-SRTP media encryption | ✅ Via WebView2 + browser stack |
| Anti-replay (512 sliding window, ±120s skew) | ✅ Server-side |
| Rate limit (60/min per IP, 60/min per device) | ✅ Server-side |
| SDP/ICE bounds (12 KiB / 4 KiB, grammar-validated) | ✅ Server-side |
| TURN short-lived credentials (HMAC-SHA1, 2h TTL) | ✅ When configured |
| SQLite metadata only — no SDP/password/file content | ✅ Enforced |
| Audit log (capped detail, no secrets) | ✅ Enforced |
| Bounded challenge store (4096 entries max) | ✅ Enforced |

## Test results

```
server tests: 25/25 passed
client tests: 33/33 passed (Linux-testable subset; WPF UI is Windows-only)
total:        58/58 passed
```

## Limitations

- WPF client runs only on Windows x64.
- WebView2 Runtime must be installed (default on Windows 11).
- Mouse/keyboard input injection uses WebView2 (limited); full native injection is a v2.1 follow-up.
- TURN is required for cross-NAT sessions.
- mDNS not implemented in v2.0 (UDP broadcast is sufficient for LAN discovery).
- No EF migrations — uses `EnsureCreated()` (schema reset on schema change).

## Documentation

- `docs/ARCHITECTURE.md` — full architecture (Persian + English mix)
- `docs/WORKLOG.md` — build log per task
- `deploy/` — production deployment artifacts

## License

Independent rewrite. No AnyDesk, RustDesk, or DorsanDesk code is used.

© 2026 YFG SYSTEMS.
