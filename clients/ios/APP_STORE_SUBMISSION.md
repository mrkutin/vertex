# Vertex iOS — App Store submission packet

Drafts and step-by-step for App Store submissions. Keep this file updated for future releases.

**Current target**: 1.0.0 (4) — resubmission after a 4.3(a) rejection on 1.0.0 (3) (May 7, 2026). The metadata below was rewritten in response to the rejection: leads with operator/topology differentiation, drops the `wireguard` keyword that triggered the "VPN template" classification, and offers a Resolution Center reply (Section 11). No mention of the underlying transport layer (publish/subscribe broker) anywhere in public copy — that stays out of App Store text and the public site.

---

## 1. App Store Connect → My Apps → New App

| Field | Value |
|---|---|
| Platform | iOS |
| Name | **Vertex** |
| Primary language | English (U.S.) |
| Bundle ID | `ru.vertices` (must already exist in Certificates, IDs & Profiles → Identifiers) |
| SKU | `vertex-ios` |
| User Access | Full Access |

If "Vertex" name is taken in App Store, fall back to **"Vertex VPN"** or **"Vertex Tunnel"**.

---

## 2. App Information

- **Subtitle** (30 chars max): `Relay-graph VPN by Vertex` (29 chars)
  - Reasoning: contains the unique term `relay-graph` (no other VPN on the store uses it) and the `by Vertex` signature positions us as operators rather than resellers — both signals against 4.3(a) reapplication.
  - Backup options if the above is rejected for trademark/clarity: `VPN through a vertex graph` (26), `Indirect-path privacy VPN` (25).
- **Category — Primary**: `Utilities`
- **Category — Secondary**: `Productivity`
- **Content Rights**: Does NOT contain third-party content
- **Age Rating**: 4+ (no questionable content; standard answers all "No")

---

## 3. Pricing & Availability

- **Price**: Free
- **Availability**: All territories **except Russia** (uncheck the single RU checkbox; everything else stays selected)

---

## 4. App Privacy

### Data Collection

Click "Get Started" or "Edit", then choose **Yes, we collect data from this app**.

Tick the following categories:

#### Identifiers
- **User ID**
  - Linked to user: **No** (it's a username we generate, not tied to Apple ID)
  - Used for tracking: **No**
  - Purpose: **App Functionality** (authentication to relay vertices)

#### Diagnostics
- **Performance Data** (if you ever surface MetricKit signposts to telemetry — currently NO; skip)
- **Other Diagnostic Data** — connection metadata kept on relay-vertex logs for ≤ 7 days
  - Linked to user: **No**
  - Used for tracking: **No**
  - Purpose: **App Functionality**

Everything else: **No**.

### Tracking

- "Does your app use data to track users?" → **No**

---

## 5. Version Information (for 1.0.0)

### Promotional Text (170 chars, optional, can update without review)

```
Vertex is a privacy VPN with our own relay-graph topology and protocol. We operate every relay node and exit; this app is not a template or a wrapper around an existing VPN.
```

### Description (4000 chars max)

```
Vertex is a privacy-focused VPN with a topology and protocol we designed and operate ourselves. It is not a wrapper around WireGuard, OpenVPN, IKEv2, or any commercial VPN service. Every relay node, every exit, and every line of the wire protocol is ours.

WHAT WE DESIGNED, NOT BORROWED

A Vertex connection does not open a direct tunnel between your phone and an exit server. Your device and the exit talk through a third point — a relay vertex — that we run ourselves on independent infrastructure. End-to-end encryption is established directly between your device and the exit, so the relay vertex only ever forwards opaque ciphertext. The protocol on the wire was written from scratch for this topology; it does not match the signature of any existing VPN protocol.

WHO BUILT AND OPERATES IT

A small independent team. We wrote the iOS, macOS, Android, Windows, and Linux clients on a shared Swift / Kotlin / .NET / Go core, not from a template. We rent and configure the relay vertices and exit servers ourselves across multiple infrastructure providers and regions. There is no parent VPN service we resell.

WHAT MAKES IT DIFFERENT FROM OTHER VPN APPS

• Relay-vertex topology. A connection passes through a vertex node that holds no cryptographic keys; even compelled disclosure from the relay operator yields only ciphertext.

• Custom wire protocol. End-to-end X25519 Diffie–Hellman handshake per connection, ChaCha20-Poly1305 AEAD per packet, HKDF-SHA256 key derivation. Apple's CryptoKit is used for all primitives.

• Multi-vertex resilience. Several independent relay vertices, each fully standalone. Client failover happens in roughly 100 ms, fast enough for streaming and SSH sessions to survive.

• Auto exit-select. The client probes reachable exits and picks the lowest-latency one under load. Manual override at any time.

• Device identity (Trust On First Use). The exit pins your device's X25519 public key on first connect; a leaked password alone cannot impersonate you.

• Split routing. An on-device CIDR table sends Russian destinations direct, the rest through the tunnel. 8,585 subnets, refreshed each release.

• No telemetry, no analytics SDK, no auto-update phone-home. We collect a username and an identity public key. That is the entire user data set.

NATIVE iOS

Built on NetworkExtension with NEPacketTunnelProvider, NWConnection, and CryptoKit. Universal binary for iPhone and iPad. Released under the same Bundle ID as our macOS app (ru.vertices) — they share a Swift Package called VertexCore that contains the protocol implementation.

REQUIREMENTS

iOS 17 or newer. iPhone or iPad.

LEARN MORE

Architecture and source-code references: https://vertices.ru/features
Privacy: https://vertices.ru/privacy
Support: https://vertices.ru/support
```

### Keywords (100 chars total, comma-separated, no spaces after commas)

```
vertex,relay,vpn,privacy,encryption,e2e,tunnel,split,topology,independent
```

Reasoning vs. the rejected build:
- **Removed `wireguard`** — was the single biggest 4.3(a) trigger; signals "WireGuard wrapper" to App Review.
- **Removed `bypass`, `censorship`, `secure`** — generic, dilute the unique signal.
- **Added `vertex`, `topology`, `independent`** — unique terms that don't appear in other VPN listings.

### Support URL: `https://vertices.ru/support/`
### Marketing URL: `https://vertices.ru/`
### Version: `1.0.0`
### Copyright: `© 2026 Vertex`

### What's New in This Version (4000 chars max, first release)

```
Initial App Store release.

Vertex is a privacy VPN with our own relay-graph topology and wire protocol. End-to-end encryption between device and exit, multi-vertex failover, automatic exit selection, device-identity pinning, RU split routing.
```

---

## 6. App Review Information

### Sign-In Required: **Yes**

| Field | Value |
|---|---|
| User name | `vtx-client-appstore-review` |
| Password | (see CREDENTIALS.md — `706f370f2a5a09ac5755f607d62d3921`) |

### Review notes

```
Vertex is a VPN client. To test:

1. Launch the app. The first launch will request Local Network access (required for VPN tunnel) and VPN configuration permission — please grant both.

2. The app auto-discovers reachable relay vertices and exit servers. You should see the connection panel with a Connect button on the main screen.

3. (Optional) Settings → Identity: confirm the username is `vtx-client-appstore-review` and the password is filled in.

4. Tap Connect. Within 2-3 seconds the status should change to "Connected" and the speed indicators start updating.

5. To verify the VPN is active, open Safari and visit https://ifconfig.me — the public IP shown should belong to one of our exit servers (Canada or Sweden), not your local network IP.

6. The connection works on cellular and Wi-Fi alike. Wi-Fi ↔ cellular handoff is seamless.

We do not require any account creation by the user and do not collect any personal data beyond the demo username above.

Privacy Policy: https://vertices.ru/privacy/
Support: https://vertices.ru/support/
```

### Contact Information

- First name / Last name / Phone number / Email — your real contact (used by App Review only if they need follow-up)

### Demo Notes

If review fails to connect, the most likely cause is that the demo identity key was already TOFU-pinned to a previous device. Fix: email support@vertices.ru and we'll reset the pin within an hour. (You can preemptively note this in Review Notes if Apple has rejected before.)

---

## 7. Export Compliance (App Information → Encryption)

`Info.plist` declares `ITSAppUsesNonExemptEncryption=false` (App/Info.plist). This is correct because the iOS client uses **only Apple's CryptoKit** for cryptographic primitives (X25519, HKDF-SHA256, ChaCha20-Poly1305) — no third-party crypto library, no custom crypto implementation. Per Apple's documentation, apps that use only the encryption shipped in iOS qualify for the standard Apple-OS exemption and do not need to file an annual self-classification report.

When uploading the build, App Store Connect will read the plist value and skip the encryption questionnaire. No further action needed.

**If a future build adds non-CryptoKit crypto** (e.g., a third-party AEAD library, post-quantum primitives Apple hasn't shipped yet, custom-implemented ciphers), flip `ITSAppUsesNonExemptEncryption` to `true` and answer the ASC questionnaire:
- "Does your app qualify for any of the exemptions provided in Category 5, Part 2 of the U.S. Export Administration Regulations?" — **Yes**, "Standard encryption algorithms instead of, or in addition to, using or accessing the encryption within Apple's operating system."
- File annual self-classification at https://www.snr.bis.doc.gov/, ECCN `5D992.c`.

---

## 8. Screenshots

### Required sizes (App Store Connect)

- **iPhone 6.9"** (iPhone 16 Pro Max): 1290 × 2796 — **REQUIRED**, minimum 3
- **iPhone 6.5"** (iPhone 14 Plus / 11 Pro Max): 1242 × 2688 or 1284 × 2778 — recommended
- **iPad 13"** (iPad Pro M4): 2064 × 2752 — required since the target is universal

### What to capture (3 minimum)

1. **Connected** — main screen with "Connected" status, picked exit server, throughput indicators visible.
2. **Exit picker** — list of available exits with latency badges.
3. **Diagnostics / Identity** — settings showing identity key, connection metrics.

### Capture method

Real iPhone 16 Pro Max is needed for screenshots that show the connected state (NEPacketTunnelProvider doesn't run in Simulator). Two paths:

- **Live device**: `xcrun simctl` is for simulator only. For real devices use the **Photos app** — open the app on the iPhone, side-button + volume-up screenshot, AirDrop to Mac, no scaling needed if device is iPhone 16 Pro Max (native 1290×2796).
- **iPhone 15 Pro Max** (current dev device): produces 1290 × 2796 too (same logical resolution as 16 Pro Max).

If you don't have a 6.9" device, App Store Connect accepts 6.5" screenshots and auto-fits, but Apple flags it during review for newer apps. Better to borrow a 16 Pro Max for an hour.

---

## 9. Build upload via Makefile

After all metadata is filled and screenshots are uploaded:

### Set up ASC API key (one-time)

1. App Store Connect → Users and Access → Keys → Generate API Key
2. Role: **App Manager** (minimum needed for upload)
3. Download the `.p8` file (only once!) — save to `~/.private_keys/AuthKey_<KEY_ID>.p8`
4. Note the **Key ID** (10 chars) and **Issuer ID** (UUID)
5. Export to your shell profile:
   ```bash
   export ASC_KEY_ID=ABCDEFG123
   export ASC_ISSUER_ID=12345678-90ab-cdef-1234-567890abcdef
   ```

### Upload

```bash
cd ~/Projects/vertex

# Validate first (catches most issues before upload)
make validate-ios-appstore

# Upload to App Store Connect
make upload-ios-appstore
```

Build appears in ASC → My Apps → Vertex → TestFlight (Internal) within 15-30 min after upload (Apple processing).

### Attach build to version 1.0.0

In App Store Connect → My Apps → Vertex → 1.0.0 (the version draft you created at step 1) → **Build** → "+" → select the just-uploaded 1.0.0 (4) build.

### Submit for Review

Top-right "Add for Review" → "Submit to App Review".

ETA: VPN apps usually 24-72 hours. Common follow-up questions: demo account doesn't connect (TOFU re-pin needed) or encryption exemption proof. Both already addressed in the packet above.

---

## 10. Post-approval checklist

When approved:

1. Replace `https://vertices.ru/download/` iOS card placeholder with the real App Store link `apps.apple.com/app/...` (update `download.iosNote` and add a real button in `pages/download.astro`).
2. Note the actual App Store URL in CREDENTIALS.md.
3. Save the **Apple ID** (numeric) of the app for deep-links and analytics later.
4. Verify regional availability — sometimes Apple flags individual countries for legal reasons.

---

## 11. Resolution Center reply (post-rejection)

Used after the 4.3(a) rejection of build 1.0.0 (3) on May 7, 2026 (submission ID `6f4fdbce-2c18-4516-a2e5-794b28ffd881`). Send through App Store Connect → Resolution Center for the rejected submission, then upload the new build (1.0.0 (4)) with the rewritten metadata in Section 5.

### Rationale

App Review's 4.3(a) rejection cited "similar binary, metadata, and/or concept as apps submitted to the App Store by other developers." For VPN apps, this label is most often triggered by metadata signals that resemble templates and resold services. The rejected metadata had three such signals:

1. The `wireguard` keyword (interpreted as "another WireGuard wrapper").
2. A generic subtitle (`End-to-end encrypted tunnel`) shared by many privacy-VPN templates.
3. A description that opened with a privacy-policy claim rather than with operator and topology differentiation.

Build 1.0.0 (4) addresses all three. The Resolution Center reply below explains the situation directly to App Review and offers concrete proof of independent design and operation.

### Reply text (English, paste into Resolution Center)

```
Hi App Review Team,

Thank you for the feedback on submission 6f4fdbce-2c18-4516-a2e5-794b28ffd881 (Vertex 1.0 build 3). We respectfully disagree with the 4.3(a) classification and would like to provide context that we believe was not visible from the metadata of build 3. We have also rewritten the metadata for the next submission to make this context unambiguous.

Vertex is not a repackaged template, not a third-party SDK build, and not a reseller of an existing VPN service. We are an independent team that designed and operates the entire stack:

1. PROTOCOL. The Vertex wire protocol was written from scratch for a relay-vertex topology. It is not WireGuard, OpenVPN, IKEv2, Shadowsocks, or any other public VPN protocol. The wire transport is a publish/subscribe relay layer rather than a point-to-point tunnel — that's the architectural choice that gives us a single logical exit reachable through several independent vertices. End-to-end encryption is X25519 Diffie-Hellman + ChaCha20-Poly1305 + HKDF-SHA256, all via Apple's CryptoKit. Architecture and source-code references are at https://vertices.ru/features.

2. INFRASTRUCTURE. We rent and configure every relay vertex and every exit server ourselves, across multiple infrastructure providers in different regions. We do not resell or rebrand a third-party VPN service.

3. CODE BASE. The iOS app is built on a shared Swift Package (VertexCore) that we wrote and that we also use in our macOS app under the same Bundle ID prefix (ru.vertices). The same protocol is implemented natively in Kotlin (Android), .NET (Windows), and Go (Linux gateway). All clients ship from one engineering team — none of them are produced from a code generator or template service.

4. APPLE FRAMEWORKS. The iOS client is built on NetworkExtension (NEPacketTunnelProvider), NWConnection for transport, and CryptoKit for cryptography. There is no third-party VPN SDK, no template scaffolding, and no third-party analytics or attribution SDK in the binary.

We are happy to provide:
- A walkthrough of the relay-vertex topology and our custom wire protocol
- Source-code access to VertexCore (the Swift Package powering both iOS and macOS)
- Logs of our independent infrastructure (relay nodes and exit nodes) on request
- A demo account that connects to our production network (already provided in App Review notes)

For the resubmission we have rewritten the App Store description, subtitle, and keywords to make the above unambiguous in metadata alone. The previous metadata leaned on generic VPN terminology and on the keyword "wireguard" (intended for discoverability, not as a description of the product), which we recognize sent the wrong signal.

Please let us know if any specific aspect of the app remains unclear and we'll address it directly.

Thank you,
Vertex team
support@vertices.ru
```

### Submission order

1. Send the reply above in Resolution Center for the rejected submission (do **not** create a new submission first — Apple usually re-reviews the existing one).
2. Wait for Apple's response. If they re-review and reject again, or ask for the rewritten metadata, then:
3. Bump build version (already done: 1.0.0 (4) in `project.yml`), upload via `make upload-ios-appstore`, attach to the same 1.0.0 version draft, replace metadata per Section 5, resubmit.

ETA for re-review after a Resolution Center reply: 24-72 hours, occasionally faster if the reply is detailed.

---

## 12. App Review Board appeal

After the second 4.3(a) rejection (a near-identical templated response to the Resolution Center reply in Section 11), the next escalation is the App Review Board. The Board is a separate pool of reviewers from the team that handled the initial submission, and is the official channel for cases that get stuck in a templated-rejection loop.

### Where to submit

https://developer.apple.com/contact/app-store/?topic=appeal — sign in with the Apple ID that owns the developer account, fill the form. The page redirects through Apple ID auth; the form itself is not publicly visible.

### Form fields (typical)

- App name: `Vertex`
- App Apple ID: (numeric ID from App Store Connect → My Apps → Vertex → App Information)
- Bundle ID: `ru.vertices`
- Latest submission ID: `6f4fdbce-2c18-4516-a2e5-794b28ffd881`
- Guideline being appealed: **4.3(a) — Design: Spam**
- Reason for appeal: (text body, see below)
- Contact info: same as App Review contact (Section 6)

### Tone

This is a **compliance message, not a disagreement**. Lead with what we changed, then with what is objectively unique, then with the specific ask. Do not use "we respectfully disagree" — Apple reads that as "developer is arguing" and routes it back to the same templated-rejection pipeline.

### Appeal text (paste into the form's reason field — 2000 character limit)

```
Vertex (Bundle ID ru.vertices, submission 6f4fdbce-2c18-4516-a2e5-794b28ffd881) received two identical templated 4.3(a) rejections. The second came after we rewrote the metadata to address the first; the templated repeat suggests the case did not get a substantive second look.

CHANGES IN BUILD 4
- Removed `wireguard` keyword (a known 4.3 trigger).
- New subtitle: "Relay-graph VPN by Vertex" (was generic).
- Description now opens with topology and operator identity.
- Added unique keywords: vertex, topology, independent.

WHY VERTEX IS NOT A TEMPLATE OR RESELLER

1. PROTOCOL. Written from scratch. Not WireGuard, OpenVPN, IKEv2, or Shadowsocks. End-to-end X25519 + ChaCha20-Poly1305 + HKDF-SHA256, via Apple CryptoKit. Architecture: https://vertices.ru/features

2. TOPOLOGY. Not a direct device-to-exit tunnel — traffic routes through a relay vertex that holds no keys. Even compelled disclosure from the relay operator yields only ciphertext.

3. INFRASTRUCTURE. We rent and operate every relay and exit ourselves across multiple providers and regions. No third-party VPN is resold.

4. CODE. Our team authored the Swift Package VertexCore, used by both iOS and macOS (same Bundle ID prefix ru.vertices). Same protocol is implemented natively in Kotlin (Android), .NET (Windows), Go (Linux).

5. FRAMEWORKS. NetworkExtension + NWConnection + CryptoKit only. No third-party VPN SDK, no template scaffolding, no analytics SDK.

REQUEST

If the Board still believes Vertex resembles another app, please name it — we will demonstrate concrete differences. We can also arrange a phone call, share VertexCore source, or provide infrastructure logs.

Demo account is in App Review notes (vtx-client-appstore-review).

Thank you,
Vertex team — support@vertices.ru
```

Length: ~1765 characters (well under 2000).

### After submission

ETA for App Review Board response: typically 3–7 business days. The Board either re-reviews the existing submission directly, or sends a senior reviewer to evaluate before responding. Both outcomes break the templated-rejection loop.

While waiting, do **not** upload a new build or change metadata — that resets the queue and the appeal gets re-routed to the standard review pool. Hold the build, wait for the Board response, then act on whatever they ask for.
