# YFGDESK v2 — سند معماری

**نسخه:** 2.0  
**تاریخ:** 2026-10  
**وضعیت:** مبنای اجرا  

---

## ۱. هدف محصول

یک نرم‌افزار پشتیبانی از راه دور (Remote Desktop Support) برای ویندوز با ویژگی‌های:

- **شناسهٔ پایدار** — هر دستگاه یک شناسهٔ کوتاه ثابت دارد (مثل AnyDesk `123 456 789`)
- **اتصال سه‌سطحی** با اولویت خودکار:
  1. **سطح ۱ — LAN Discovery**: اگر دو دستگاه روی یک شبکهٔ محلی باشند، بدون سرور مرکزی و بدون اینترنت وصل می‌شوند
  2. **سطح ۲ — مستقیم P2P**: با NAT hole-punching (STUN) اگر دو دستگاه روی اینترنت باشند
  3. **سطح ۳ — TURN Relay**: اگر NAT سختگیرانه مانع اتصال مستقیم شود، ترافیک از سرور relay عبور می‌کند
- **رضایت میزبان** — هیچ رسانه/کنترلی قبل از Accept شروع نمی‌شود
- **حالت بدون نظارت** — opt-in، با رمز Argon2id (هرگز به سرور نمی‌رود)
- **رابط دوزبانه** فارسی RTL + انگلیسی LTR
- **مستقل** — بدون وابستگی به RustDesk/DorsanDesk/AnyDesk

---

## ۲. معماری کلی

### ۲.۱ نقش‌ها

```
┌─────────────────┐         ┌─────────────────┐         ┌─────────────────┐
│   کلاینت A      │         │   سرور مرکزی    │         │   کلاینت B      │
│  (Host/Viewer)  │         │  (Discovery +   │         │  (Host/Viewer)  │
│                 │ ◄─────► │   TURN Relay)   │ ◄─────► │                 │
│  LAN Discovery  │         │                 │         │  LAN Discovery  │
│  P2P (ICE)      │         │  SQLite         │         │  P2P (ICE)      │
│  TURN fallback  │         │  WebSocket Hub  │         │  TURN fallback  │
└─────────────────┘         └─────────────────┘         └─────────────────┘
        │                                                    │
        └──────────────► LAN / اینترنت ◄────────────────────┘
                          (P2P یا Relay)
```

### ۲.۲ نقش سرور

سرور مرکزی **سبک** است و چهار وظیفه دارد:

| وظیفه | پروتکل | توضیح |
|------|--------|-------|
| **Discovery** | REST + WebSocket | ترجمهٔ `شناسه → IP عمومی + پورت` (مثل DNS) |
| **Signaling** | WebSocket | تبادل SDP و ICE candidates بین همتایان (هیچ رسانه از سرور عبور نمی‌کند) |
| **TURN Relay** | UDP/TCP | فقط وقتی مستقیم ناموفق است، ترافیک رمزنگاری‌شده را relay می‌کند (نمی‌تواند رمزگشایی کند) |
| **Authentication** | REST | احراز هویت دستگاه با ECDSA P-256 challenge-response |

**سرور هرگز ذخیره نمی‌کند:**
- ❌ SDP یا ICE candidates (بعد از relay حذف می‌شوند)
- ❌ رمز عبور یا verifier
- ❌ محتوای فایل یا صفحهٔ نمایش
- ❌ Bearer token به‌صورت plaintext (فقط SHA-256 hash)

### ۲.۳ نقش کلاینت

کلاینت ویندوزی (WPF، .NET 10، x64) دو نقش دارد:

- **Host**: شناسهٔ خود را ثبت می‌کند، درخواست‌های ورودی را نمایش می‌دهد، صفحه را ضبط و ارسال می‌کند
- **Viewer**: با شناسهٔ میزبان وصل می‌شود، تصویر را نمایش می‌دهد، کنترل می‌فرستد

**اولویت اتصال خودکار در کلاینت:**

```
connect(target_id)
  │
  ├─ ۱. LAN cache lookup (آیا این شناسه در کش محلی است؟)
  │     └─ اگر بله → اتصال مستقیم به IP محلی
  │
  ├─ ۲. LAN broadcast/mDNS discovery (۳ ثانیه timeout)
  │     └─ اگر پاسخ داد → اتصال مستقیم به IP محلی
  │
  ├─ ۳. سرور discovery lookup (اینترنت لازم است)
  │     └─ اگر سرور در دسترس بود → IP عمومی مقصد را بگیر
  │        └─ مستقیم امتحان کن + NAT hole-punch (ICE)
  │           └─ اگر برقرار شد → P2P
  │
  └─ ۴. TURN relay fallback
        └─ از سرور TURN credentials بگیر
           └─ ترافیک را از طریق relay بفرست
```

---

## ۳. شناسه‌دهی

### ۳.۱ فرمت شناسه

```
شناسه: 9 رقم، مثل 123 456 789
```

**چرا ۹ رقم؟**
- فضای کافی: ۱ میلیارد شناسهٔ یکتا
- به یاد سپردنی: کاربر می‌تواند تلفنی منتقل کند
- سازگار با AnyDesk (تا کاربران موجود راحت مهاجرت کنند)

### ۳.۲ تولید شناسه

سرور شناسه‌ها را تولید می‌کند (نه کلاینت) تا جلوگیری از تضاد:

1. کلاینت یک کلید ECDSA P-256 تولید می‌کند
2. `POST /api/enroll/challenge` با `publicKey` → سرور nonce می‌دهد
3. کلاینت nonce را امضا می‌کند
4. `POST /api/enroll/register` با `signature` → سرور:
   - کلید عمومی را verify می‌کند
   - یک شناسهٔ ۹ رقمی **تصادفی** تولید می‌کند (تکراری نباشد)
   - در SQLite ذخیره می‌کند: `id, publicKey, lastSeenIp, lastSeenPort, online`
   - bearer token برمی‌گرداند

### ۳.۳ heartbeat

هر ۳۰ ثانیه، کلاینت یک heartbeat به سرور می‌فرستد:
- IP عمومی فعلی خود را (از دید سرور) آپدیت می‌کند
- پورت‌های listening محلی را گزارش می‌دهد
- `lastSeen` آپدیت می‌شود
- اگر ۹۰ ثانیه heartbeat نباشد، `online = false` می‌شود

---

## ۴. پروتکل Discovery

### ۴.۱ REST API (نسخهٔ ۱)

```
GET  /api/health                              → { status, version, time }
POST /api/enroll/challenge                    → { challenge, expiresAt }
POST /api/enroll/register                     → { deviceId, bearerToken, expiresAt }
POST /api/lookup                              → { ip, port, online, lastSeen }
POST /api/turn-credentials                    → { username, credential, urls, expiresAt }
```

**هیچ bearer/token در query string نباید باشد.** همیشه `Authorization: Bearer ...` header.

### ۴.۲ WebSocket Signaling

```
ws://server/v1/ws?bearer=...    ❌ ممنوع (query string)
ws://server/v1/ws                ✅ با Authorization: Bearer ... header
```

#### پیام‌ها (envelopes JSON، camelCase)

```typescript
{
  version: 1,
  messageId: "m_...",        // برای anti-replay
  sessionId: "s_...",         // شناسهٔ نشست
  timestamp: 1234567890,      // epoch ms
  kind: "offer",              // نوع پیام
  payload: { ... }
}
```

#### انواع پیام‌ها

| kind | جهت | payload | توضیح |
|------|------|---------|-------|
| `register` | سرور ← کلاینت | `{ deviceId, heartbeatIntervalMs }` | تأیید اتصال |
| `hostReady` | کلاینت → سرور | `{}` | اعلام آمادگی میزبان |
| `createSession` | Viewer → سرور | `{ hostId, permissions, videoPreference }` | درخواست نشست |
| `incomingRequest` | سرور → Host | `{ sessionId, viewerId, permissions, expiresAt }` | درخواست ورودی |
| `consentDecision` | Host → سرور | `{ accept, grantedPermissions }` | پاسخ رضایت |
| `sessionStarted` | سرور → هر دو | `{ sessionId, role, iceServers, expiresAt }` | شروع نشست |
| `offer` | Viewer → Host | `{ sdp }` | SDP offer |
| `answer` | Host → Viewer | `{ sdp }` | SDP answer |
| `iceCandidate` | هر دو | `{ candidate, sdpMid, sdpMLineIndex }` | کاندیدای ICE |
| `heartbeat` | هر دو → سرور | `{}` | keepalive |
| `endSession` | هر دو → سرور | `{ reason }` | پایان نشست |
| `error` | سرور → کلاینت | `{ code, detail }` | خطا |

### ۴.۳ امنیت signaling

- **Anti-replay**: پنجرهٔ لغزان ۵۱۲ messageId
- **Timestamp skew**: ±۱۲۰ ثانیه
- **Rate limit**: ۶۰ پیام/دقیقه برای هر دستگاه
- **Role enforcement**: `offer` فقط از Viewer، `answer` فقط از Host
- **SDP validation**: حداکثر ۱۲KB، باید `a=fingerprint:sha-256` داشته باشد
- **ICE validation**: حداکثر ۴KB، گرامر `candidate:...` لازم است

---

## ۵. Discovery محلی (LAN)

وقتی کلاینت باز است، در پس‌زمینه روی شبکهٔ محلی شناسه می‌دهد:

### ۵.۱ Broadcast UDP

هر ۵ ثانیه، کلاینت یک بستهٔ UDP به `255.255.255.255:49152` می‌فرستد:

```json
{
  "protocol": "yfgdesk",
  "version": 1,
  "deviceId": "123456789",
  "displayName": "Office-PC",
  "listenPort": 49153,
  "timestamp": 1234567890
}
```

هر کلاینت دیگری این بسته را می‌شنود و در **کش discovery محلی** ذخیره می‌کند:

```
deviceId: 123456789 → 192.168.1.10:49153 (آخرین دیده‌شده: حالا)
deviceId: 987654321 → 192.168.1.20:49153 (آخرین دیده‌شده: ۵ ثانیه پیش)
```

### ۵.۲ mDNS (Multicast DNS)

اگر broadcast کار نکرد (مثلاً WiFi guest)، از mDNS استفاده می‌کنیم:

```
نوع سرویس: _yfgdesk._tcp.local
 TXT record: device_id=123456789, name=Office-PC
 A record: 192.168.1.10
```

### ۵.۳ کش محلی

فایل `%APPDATA%\YFGDESK\discovery.cache`:
- شناسه‌های اخیراً دیده‌شده
- آخرین IP محلی شناخته‌شده
- timestamp آخرین مشاهده

### ۵.۴ ترفند اتصال مستقیم با IP

اگر discovery کار نکرد، کاربر می‌تواند مستقیماً `192.168.1.10` را وارد کند. کلاینت مستقیماً به آن IP وصل می‌شود.

---

## ۶. اتصال مستقیم P2P (سطح ۲)

وقتی discovery محلی ناموفق بود و اینترنت در دسترس بود:

### ۶.۱ lookup از سرور

```
POST /api/lookup
{ targetId: "123456789" }
← 200 { ip: "5.5.5.5", port: 49153, online: true, lastSeen: "..." }
```

### ۶.۲ ICE Candidate Gathering

هر دو کلاینت لیست IP/پورت‌های ممکن را جمع می‌کنند:

```
Host candidates (مستقیم، IP محلی):
  192.168.1.10:49153 (host)
  10.0.0.5:49153 (host)

Server-reflexive candidates (STUN، IP عمومی):
  5.5.5.5:61234 (srflx)  ← از STUN سرور

Relay candidates (TURN):
  turn.yfgdesk.com:3478 → 1.2.3.4:50000 (relay)
```

### ۶.۳ Connectivity Checks

هر دو کلاینت همهٔ ترکیب‌های ممکن را امتحان می‌کنند. اولین ترکیب که کار کرد، استفاده می‌شود.

اولویت ICE:
1. `host` (مستقیم، IP محلی) — اگر هر دو روی LAN باشند
2. `srflx` (STUN) — اگر NAT سبک باشد
3. `relay` (TURN) — fallback نهایی

---

## ۷. TURN Relay (سطح ۳)

### ۷.۱ معماری

```
Viewer ───► TURN Server ◄─── Host
            (UDP/TCP)
```

سرور TURN:
- ترافیک بین دو peer را relay می‌کند
- **نمی‌تواند رمزگشایی کند** (داده‌ها end-to-end رمزنگاری شده‌اند)
- credentials کوتاه‌مدت دارد (۲ ساعت TTL)
- پروتکل RFC 8656

### ۷.۲ احراز هویت TURN

```
POST /api/turn-credentials
← 200 {
  username: "1234567890:device_id",
  credential: "base64_hmac_sha1",
  urls: ["turn:turn.yfgdesk.com:3478?transport=udp", ...],
  expiresAt: "..."
}
```

`username = {expiryEpoch}:{deviceId}`  
`credential = HMAC-SHA1(secret, username)`

### ۷.۳ خود-میزبانی TURN

سرور TURN می‌تواند:
- **روی همان سرور مرکزی** اجرا شود (تک باینری، با embedded coturn یا libturn)
- **روی سرور جداگانه** با Docker compose (coturn رسمی)

برای v2 از coturn رسمی + Docker compose استفاده می‌کنیم.

---

## ۸. امنیت

### ۸.۱ احراز هویت دستگاه

```
کلاینت                            سرور
  │                                │
  │ ──── publicKey ──────────────► │
  │ ◄─── nonce (challenge) ─────── │
  │                                │
  │    sign(nonce, privateKey)     │
  │ ──── signature ──────────────► │
  │                                │
  │    verify(signature, publicKey)│
  │    اگر OK → شماره دستگاه + bearer
  │ ◄─── { deviceId, bearer } ────│
```

- الگوریتم: ECDSA P-256
- فرمت امضا: DER (RFC 3279 Sequence)
- Hash: SHA-256

### ۸.۲ رمزنگاری رسانه

- **WebRTC DTLS-SRTP** (RFC 8827)
- گواهی نامعتبر رد می‌شود
- کلیدها در حافظه، هرگز ذخیره نمی‌شوند

### ۸.۳ رضایت میزبان

```
PENDING (درخواست نشست، انتظار رضایت، حداکثر ۵ دقیقه)
  ├── accept → ACTIVE
  └── reject/timeout → ENDED (هیچ رسانه‌ای شروع نمی‌شود)
```

### ۸.۴ حالت بدون نظارت

- پیش‌فرض: خاموش
- فعال‌سازی: کاربر رمز تعیین می‌کند
- **Argon2id** (RFC 9106) برای verifier
- Verifier فقط محلی ذخیره می‌شود (Windows DPAPI)
- **هرگز به سرور نمی‌رود**
- Lockout بعد از ۵ تلاش ناموفق، ۱۵ دقیقه

### ۸.۵ بستار حملات

| تهدید | کنترل |
|------|-------|
| Replay attack | پنجرهٔ ۵۱۲ messageId + timestamp skew ±120s |
| Brute force token | Rate limit 60/min + SHA-256 hash مقایسه با constant-time |
| SDP injection | Validation: max 12KB + فنگرپرینت SHA-256 + media video |
| ICE injection | Validation: max 4KB + گرامر candidate |
| NAT traversal abuse | TURN credentials کوتاه‌مدت (۲h) |
| DDoS challenge | Bounded store (4096) + rate limit per IP |
| Cross-session forgery | sessionId checked + role enforcement |

---

## ۹. ساختار پروژه

```
yfgdesk-v2/
├── server/                     ← سرور ASP.NET Core 10
│   ├── YfgDesk.Server.csproj
│   ├── Program.cs              ← DI + Kestrel + WebSocket + TURN
│   ├── Api/                    ← REST endpoints
│   │   ├── EnrollEndpoint.cs
│   │   ├── LookupEndpoint.cs
│   │   ├── TurnCredentialsEndpoint.cs
│   │   └── HealthEndpoint.cs
│   ├── Signaling/              ← WebSocket hub
│   │   ├── SignalingHub.cs
│   │   ├── SignalingSessionStore.cs
│   │   └── SignalingValidator.cs
│   ├── Data/                   ← EF Core SQLite
│   │   ├── YfgDeskDbContext.cs
│   │   └── Entities.cs
│   ├── Crypto/                 ← ECDSA verifier, ID generator, tokens
│   │   ├── EcdsaVerifier.cs
│   │   ├── DeviceIdGenerator.cs
│   │   ├── BearerTokenIssuer.cs
│   │   └── TurnCredentialIssuer.cs
│   └── Tests/                  ← xUnit
│
├── client/                     ← کلاینت WPF .NET 10
│   ├── YfgDesk.Client.csproj
│   ├── App.xaml / App.xaml.cs
│   ├── MainWindow.xaml / .cs
│   ├── Services/
│   │   ├── DeviceIdentity.cs   ← ECDSA keypair + IndexedDB/DPAPI storage
│   │   ├── SignalingClient.cs  ← WebSocket client
│   │   ├── DiscoveryClient.cs  ← LAN broadcast + mDNS
│   │   ├── PeerConnection.cs   ← WebRTC RTCPeerConnection wrapper
│   │   ├── ConnectionOrchestrator.cs ← سه‌سطحی coordinator
│   │   ├── ScreenCapture.cs    ← getDisplayMedia (WebView2)
│   │   ├── InputInjection.cs   ← mouse/keyboard injection
│   │   ├── FileTransfer.cs     ← DataChannel chunked transfer
│   │   └── UnattendedStore.cs  ← Argon2id verifier
│   ├── ViewModels/
│   ├── Views/
│   └── Tests/
│
├── shared/                    ← کد مشترک (protocol + crypto helpers)
│   ├── YfgDesk.Shared.csproj
│   ├── Protocol/
│   │   ├── Envelope.cs         ← envelope DTO
│   │   ├── MessageKinds.cs     ← enum
│   │   ├── SessionState.cs     ← enum
│   │   └── Permissions.cs      ← enum
│   └── Crypto/
│       ├── CrockfordBase32.cs
│       └── Validators.cs        ← isValidDeviceId, isValidSdp, ...
│
├── deploy/
│   ├── Dockerfile.server
│   ├── docker-compose.yml      ← server + coturn + caddy
│   ├── Caddyfile
│   └── .env.example
│
├── docs/
│   ├── ARCHITECTURE.md         ← این فایل
│   ├── PROTOCOL.md             ← جزئیات پروتکل v1
│   ├── SECURITY.md             ← مدل تهدید
│   └── DEPLOYMENT.md
│
└── README.md
```

### چرا `shared/` جدا است؟

- کلاینت و سرور هر دو به envelope DTO، enums، validators نیاز دارند
- اگر کد در هر کدام تکرار شود، drift پیدا می‌کند
- پروژهٔ `shared` **بدون وابستگی خارجی** است (فقط .NET BCL)
- هم به `net10.0` (سرور + تست‌ها) و هم `net10.0-windows` (کلاینت) target می‌شود

---

## ۱۰. تکنولوژی

### ۱۰.۱ سرور

| بخش | تکنولوژی | چرا |
|------|----------|-----|
| فریم‌ورک | ASP.NET Core 10 | مدرن، سریع، cross-platform |
| WebSocket | raw `HttpContext.WebSockets` | سبک، کنترل کامل |
| دیتابیس | SQLite via EF Core | سبک، تک‌فایل، بدون نیاز به سرویس خارجی |
| TURN | coturn (Docker) | استاندارد RFC 8656، پایدار |
| Reverse proxy | Caddy | TLS خودکار، ساده |
| Tests | xUnit + TestServer | integration tests |

### ۱۰.۲ کلاینت

| بخش | تکنولوژی | چرا |
|------|----------|-----|
| UI | WPF .NET 10 x64 | دسترسی به Windows Graphics Capture، SendInput |
| WebRTC | WebView2 + browser API | بدون نیاز به WebRTC library بومی، DTLS-SRTP داخلی |
| LAN Discovery | UDP broadcast + mDNS (Bonjour) | cross-platform در shared |
| Argon2 | Konscious.Security.Cryptography.Argon2 | استاندارد RFC 9106 |
| DPAPI | System.Security.Cryptography.ProtectedData | محافظت محلی |
| Tests | xUnit | - |

### ۱۰.۳ لایهٔ رسانه

از WebView2 برای:
- `navigator.mediaDevices.getDisplayMedia()` — ضبط صفحه
- `RTCPeerConnection` — WebRTC + DTLS-SRTP داخلی
- `RTCDataChannel` — انتقال فایل

این به ما WebRTC production-grade می‌دهد بدون نیاز به wrapper بومی.

---

## ۱۱. معیار پذیرش

### ۱۱.۱ MVP (نسخهٔ ۲.۰)

- [ ] سرور discovery + signaling + TURN روی Docker
- [ ] کلاینت ویندوز با UI دوزبانه
- [ ] احراز هویت دستگاه (ECDSA P-256)
- [ ] رضایت میزبان (Accept/Reject/Timeout)
- [ ] اتصال LAN (سطح ۱) — broadcast + cache
- [ ] اتصال P2P (سطح ۲) — STUN + ICE
- [ ] اتصال TURN (سطح ۳) — relay fallback
- [ ] اشتراک صفحه با WebView2
- [ ] حالت بدون نظارت opt-in
- [ ] Rate limit + anti-replay + audit log
- [ ] تست واحد ≥ ۸۰٪ coverage

### ۱۱.۲ v2.۱ (آینده)

- کنترل ماوس/صفحه‌کلید بومی (Windows SendInput)
- انتقال فایل دوطرفه با hash
- کیفیت تصویر Small/Standard/Custom
- ضبط نشست
- multi-monitor
- WebSocket reconnect خودکار

---

## ۱۲. محدودیت‌ها

- **WPF فقط روی ویندوز** — کلاینت روی لینوکس/مک اجرا نمی‌شود
- **WebView2 Runtime** — باید روی ویندوز نصب باشد
- **کنترل ماوس/صفحه‌کلید محدود** — از WebView2 به‌جای injection بومی (در v2.۱ بهتر می‌شود)
- **TURN سرور برای NAT سختگیرانه** — carrier-grade NAT و symmetric NAT بدون TURN کار نمی‌کند
- **شناسهٔ ۹ رقمی** — فضای محدود (۱ میلیارد)، برای规模化 به ۱۲ رقم ارتقا پیدا می‌کند

---

## ۱۳. مراحل اجرا

### مرحلهٔ ۱ — پروژهٔ shared + protocol
- تعریف envelope DTO، message kinds، session states، permissions
- Validators مشترک (deviceId، SDP، ICE، hostId)

### مرحلهٔ ۲ — سرور
- EF Core + SQLite schema
- ECDSA verifier + bearer token issuer + deviceId generator
- REST endpoints (enroll، lookup، turn-credentials، health)
- WebSocket signaling hub با anti-replay، rate limit، role enforcement
- TURN credential issuer (HMAC-SHA1)

### مرحلهٔ ۳ — کلاینت
- WPF UI دوزبانه
- DeviceIdentity (ECDSA + DPAPI)
- SignalingClient (WebSocket)
- DiscoveryClient (UDP broadcast + mDNS)
- ConnectionOrchestrator (سه‌سطحی)
- PeerConnection (WebView2 + RTCPeerConnection)
- ScreenCapture (getDisplayMedia)
- UnattendedStore (Argon2id)

### مرحلهٔ ۴ — deploy
- Dockerfile.server
- docker-compose.yml (server + coturn + caddy)
- Caddyfile با TLS خودکار
- مستندات فارسی

### مرحلهٔ ۵ — تست
- unit tests (crypto، validators، state machine)
- integration tests (signaling، session lifecycle)
- build پک ویندوز self-contained

---

## ۱۴. تفاوت با YFGDESK v1

| جنبه | v1 | v2 |
|------|----|----|
| معماری | سرور signaling + کلاینت WebView2 | سرور discovery + signaling + TURN + کلاینت سه‌سطحی |
| LAN discovery | ❌ | ✅ Broadcast + mDNS + cache |
| اتصال P2P | فقط TURN یا LAN | سه سطح با fallback خودکار |
| شناسه | `h_` + base32 (طولانی) | ۹ رقم (مثل AnyDesk) |
| پروتکل | پیچیده با version، messageId، timestamp | ساده‌تر ولی همان امنیت |
| کد مشترک | تکرار در کلاینت و سرور | پروژهٔ `shared/` جدا |
| سرور | сложный | سبک، تک‌باینری |
| مستندات | محدود | جامع فارسی |

---

© 2026 YFG SYSTEMS — معماری مستقل، بدون کد RustDesk/DorsanDesk/AnyDesk.
