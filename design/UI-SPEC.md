# Vertex iOS — Design Specification

**Version:** 1.0
**Date:** 2026-04-25
**Audience:** iOS engineer (SwiftUI, iOS 17+, Swift 6)
**Source of truth icon:** `~/Projects/vertex/design/icon/B2-11-x-bold.svg`
**Source of truth brand:** `~/Projects/vertex/BRAND.md`

---

## 0. Design intent (one paragraph)

Vertex is a premium network-infrastructure product. Public surface must never read as "VPN", "shield", "tunnel", "secure connection". The visual language is **deep navy + luminous V geometry** — the same world as the app icon. Glow is the primary mechanism of emphasis: connected things shine; idle things rest in deep navy. The V-asterisk motif (two endpoint nodes converging into a vertex) is the only iconographic constant — every screen must touch it at least once. Light-mode is supported but is never the marquee experience.

---

## 1. Color tokens

All tokens land in `Assets.xcassets` under a single asset catalog (extend the existing `Assets.xcassets`). Each color is defined for **Any (Light)** and **Dark**. SwiftUI consumes them through a `Color+Vertex.swift` extension (e.g. `Color.bgSurface`).

### 1.1 Surfaces & background

| Token | Dark (default) | Light | Usage |
|---|---|---|---|
| `bg.canvas` | `#080F26` | `#F4F6FB` | Root background; tinted by canvas gradient |
| `bg.canvasGradientTop` | `#162456` | `#FFFFFF` | Top of radial canvas gradient (matches icon) |
| `bg.canvasGradientBottom` | `#080F26` | `#E7EBF4` | Bottom of radial canvas gradient |
| `bg.surface` | `#101A3D` | `#FFFFFF` | Card backgrounds (ServerCard, StatRow) |
| `bg.surfaceElevated` | `#162456` | `#FFFFFF` | Sheets, modals, popovers |
| `bg.surfaceMuted` | `#0C1430` | `#EEF1F8` | List rows on canvas, secondary surfaces |
| `border.subtle` | `#FFFFFF` @ 8% | `#0B1638` @ 8% | Hairline card borders (replaces `.separator`) |
| `border.strong` | `#7DB3FF` @ 28% | `#3D6FB7` @ 28% | Selected/active card border |

### 1.2 Accent & glow

| Token | Dark | Light | Usage |
|---|---|---|---|
| `accent.primary` | `#7DB3FF` | `#2E63C9` | Primary tint, links, selected glyphs (replaces `AccentColor` green) |
| `accent.primaryHover` | `#9CC6FF` | `#1D4FAF` | Pressed/highlighted accent |
| `accent.primaryMuted` | `#7DB3FF` @ 18% | `#2E63C9` @ 12% | Accent fills on dark surfaces |
| `glow.primary` | `#7DB3FF` @ 60% → 0% | `#3D6FB7` @ 35% → 0% | Hero glow, button glow (radial) |
| `glow.coreHot` | `#FFFFFF` @ 60% | `#FFFFFF` @ 70% | Innermost hot point of the glow |
| `glow.warm` | `#FAF4E6` @ 55% | `#FAF4E6` @ 65% | Optional premium accent (e.g. paid tier badge — future) |

### 1.3 Text & glyphs

| Token | Dark | Light | Usage |
|---|---|---|---|
| `text.primary` | `#FFFFFF` | `#0B1638` | Titles, hero status text |
| `text.secondary` | `#C4D2EE` (alpha 78%) | `#3D4A6B` | Subheads, captions |
| `text.tertiary` | `#8497C2` (alpha 60%) | `#6B7895` | Footnotes, metadata |
| `text.onAccent` | `#0B1638` | `#FFFFFF` | Text on bright accent backgrounds |
| `glyph.primary` | `#FFFFFF` | `#0B1638` | Custom V/asterisk glyphs at full intensity |
| `glyph.dim` | `#C4D2EE` @ 50% | `#3D4A6B` @ 50% | Dormant V glyph (disconnected hero) |

### 1.4 State colors

Critically, **no greens** — green is now a removed brand color (it betrayed the VPN nature). Connected state uses the brand's sky-blue glow.

| Token | Dark | Light | Usage |
|---|---|---|---|
| `state.connected` | `#7DB3FF` | `#2E63C9` | "Connected" — same as accent (intentional: the brand IS "connected") |
| `state.connectedGlow` | `#FFFFFF` @ 75% | `#7DB3FF` @ 60% | Connected hero core glow |
| `state.transitioning` | `#FAD27A` | `#C78A1F` | Connecting / reasserting / disconnecting |
| `state.transitioningGlow` | `#FAD27A` @ 50% | `#FAD27A` @ 55% | Pulsing amber glow during handshake |
| `state.dormant` | `#8497C2` | `#6B7895` | Disconnected (neutral, low energy) |
| `state.error` | `#FF6E78` | `#C73645` | Invalid / error |
| `state.errorGlow` | `#FF6E78` @ 50% | `#FF6E78` @ 45% | Red-shifted hero glow on error |

### 1.5 Required `Assets.xcassets` entries

Create one Color Set per row above (24 entries). Naming in the catalog uses the dotted path verbatim (e.g. `bg/canvas`, `accent/primary`, `state/connected`). Provide **Any Appearance** (Light) and **Dark Appearance** swatches.

### 1.6 Color-Swift mapping helper

```swift
// File: clients/ios/Vertex/App/Theme/Color+Vertex.swift
extension Color {
    static let bgCanvas         = Color("bg/canvas")
    static let bgCanvasTop      = Color("bg/canvasGradientTop")
    static let bgCanvasBottom   = Color("bg/canvasGradientBottom")
    static let bgSurface        = Color("bg/surface")
    static let bgSurfaceElev    = Color("bg/surfaceElevated")
    static let bgSurfaceMuted   = Color("bg/surfaceMuted")
    static let borderSubtle     = Color("border/subtle")
    static let borderStrong     = Color("border/strong")
    static let accentPrimary    = Color("accent/primary")
    static let accentPrimaryHover = Color("accent/primaryHover")
    static let accentPrimaryMuted = Color("accent/primaryMuted")
    static let glowPrimary      = Color("glow/primary")
    static let glowCoreHot      = Color("glow/coreHot")
    static let glowWarm         = Color("glow/warm")
    static let textPrimary      = Color("text/primary")
    static let textSecondary    = Color("text/secondary")
    static let textTertiary     = Color("text/tertiary")
    static let textOnAccent     = Color("text/onAccent")
    static let glyphPrimary     = Color("glyph/primary")
    static let glyphDim         = Color("glyph/dim")
    static let stateConnected   = Color("state/connected")
    static let stateConnectedGlow = Color("state/connectedGlow")
    static let stateTransitioning = Color("state/transitioning")
    static let stateTransitioningGlow = Color("state/transitioningGlow")
    static let stateDormant     = Color("state/dormant")
    static let stateError       = Color("state/error")
    static let stateErrorGlow   = Color("state/errorGlow")
}
```

The existing `AccentColor` asset must be **repointed**: open the asset, set Any/Dark to the same hex as `accent/primary` (`#2E63C9` / `#7DB3FF`). Do not delete `AccentColor` — Apple's built-in tinting for navigation chrome relies on it.

---

## 2. Typography scale

Vertex uses three SF Pro families:

- **SF Pro Rounded** — for the Vertex wordmark and the hero status word (premium, branded)
- **SF Pro Text / Display** — body, titles, labels (system default at given sizes)
- **SF Mono** — IPs, byte counts, hex pubkeys, timer monospaced digits

Token table:

| Token | Family | Size | Weight | Line height | Tracking | Usage |
|---|---|---|---|---|---|---|
| `font.heroStatus` | SF Pro Rounded | 28 | Semibold (.semibold) | 32 | -0.4 | "Connected" / "Connecting…" under hero |
| `font.brandWordmark` | SF Pro Rounded | 17 | Bold (.bold) | 22 | 1.5 | "VERTEX" in nav bar (uppercased, tracked) |
| `font.titleLarge` | SF Pro Display | 28 | Bold | 34 | -0.4 | Sheet/screen large titles (when used) |
| `font.title` | SF Pro Display | 22 | Semibold | 28 | -0.2 | Section headers in main content |
| `font.headline` | SF Pro Text | 17 | Semibold | 22 | -0.2 | Card titles, primary list rows |
| `font.body` | SF Pro Text | 17 | Regular | 22 | 0 | Default body |
| `font.bodyEmphasized` | SF Pro Text | 17 | Medium | 22 | 0 | Selected list values |
| `font.callout` | SF Pro Text | 16 | Regular | 21 | 0 | StatusPill body |
| `font.subheadline` | SF Pro Text | 15 | Regular | 20 | 0 | Sub-labels in cards ("Broker", "Exit") |
| `font.footnote` | SF Pro Text | 13 | Regular | 18 | 0 | Footers, helper text |
| `font.caption` | SF Pro Text | 12 | Regular | 16 | 0 | Accessory chips ("MQTTS · 8883") |
| `font.captionMono` | SF Mono | 12 | Medium | 16 | 0 | Schemes, codes, brief mono accents |
| `font.statValue` | SF Mono | 17 | Medium | 22 | 0 | "12.4 MB" / uptime / IP |
| `font.statValueLarge` | SF Mono | 28 | Semibold | 34 | -0.2 | Hero-scale mono (StatsSheet) |
| `font.identityHex` | SF Mono | 13 | Regular | 18 | 0 | Pubkey hex blocks |

---

## 3. Spacing & radius scale

### 3.1 Spacing (base unit = 4pt)

| Token | Value | Usage |
|---|---|---|
| `space.0` | 0 | Reset |
| `space.1` | 4 | Hairline gaps inside compound glyphs |
| `space.2` | 8 | Icon-to-text inside row |
| `space.3` | 12 | Internal vertical card padding (small) |
| `space.4` | 16 | Default content padding, card inset |
| `space.5` | 20 | Screen horizontal padding (current uses 20 — keep) |
| `space.6` | 24 | Between major content blocks |
| `space.7` | 28 | Hero ↔ ServerCard ↔ Connect button gaps |
| `space.8` | 32 | Major section gap, screen bottom padding |
| `space.10` | 40 | Hero top breathing room |
| `space.12` | 48 | Hero footprint padding (vertical) |

### 3.2 Corner radius

| Token | Value | Usage |
|---|---|---|
| `radius.sm` | 8 | Small accessory chips, inline tags |
| `radius.md` | 12 | Inline buttons, small cards |
| `radius.lg` | 16 | Primary cards (ServerCard, StatRowView), error banners |
| `radius.xl` | 22 | Sheet content cards, picker rows on dark canvas |
| `radius.capsule` | 999 | StatusPill, BigConnectButton, identity pubkey copy button |
| `radius.heroOuter` | 96 | Notional bounding for hero glow blur (used internally for sizing) |

### 3.3 Shadow / elevation (dark mode emphasizes glow over shadow)

| Token | Spec | Usage |
|---|---|---|
| `shadow.card` | 0 / 12 / 32 / `#000` @ 30% (dark) ; 0 / 4 / 12 / `#0B1638` @ 8% (light) | Cards on canvas |
| `shadow.sheet` | 0 / 24 / 48 / `#000` @ 40% (dark) ; 0 / 12 / 32 / `#0B1638` @ 12% (light) | Modal sheets |
| `glow.heroOuter` | radial blur 96pt, color `glow.primary` 60% | Hero state ring (animated) |
| `glow.heroCore` | radial blur 32pt, color `glow.coreHot` | Hero core hotspot |
| `glow.button` | radial blur 24pt, color `accent.primary` @ 35% | BigConnectButton when in "Connect" state |

---

## 4. Motion & easing

Reduce-motion fallbacks are mandatory — see §9.

| Use case | Duration | Curve | Notes |
|---|---|---|---|
| Hero state replace (status → status) | 280 ms | `.easeInOut` | Cross-fade old V to new V state |
| Hero glow pulse (connected idle "breathing") | 2400 ms loop | `.easeInOut` reversed | Alpha 70% ↔ 100%, scale 1.00 ↔ 1.04 |
| Hero pulse (connecting handshake) | 900 ms loop | `.easeInOut` reversed | Alpha 35% ↔ 90%, scale 0.92 ↔ 1.06 |
| Hero error shake | 360 ms one-shot | `.spring(response: 0.32, dampingFraction: 0.55)` | x-translate ±6pt, 3 cycles |
| Connect button press | 120 ms | `.spring(response: 0.18, dampingFraction: 0.7)` | scale 0.97 |
| Connect button glow pulse | 1800 ms loop | `.easeInOut` reversed | Only when `isConnected == false && !isTransitioning` |
| Sheet present | 360 ms | system default `.presentationDetents` | Add subtle `presentationCornerRadius(28)` |
| Sheet dismiss | 280 ms | system | — |
| Navigation push (broker/exit list) | 320 ms | system | Use default; complement with hero ghost-fade |
| StatsCard appear | 280 ms | `.spring(response: 0.36, dampingFraction: 0.78)` | Slide+fade from bottom |
| Status pill text change | 180 ms | `.easeOut` + `contentTransition(.opacity)` | Already wired |
| Number transitions (bytes) | 220 ms | `contentTransition(.numericText())` | Keep |
| Particle/edge flow (connected) | 5000 ms loop | `.linear` | Particle travels endpoint→vertex |

---

## 5. Hero component — `VertexHero`

Replaces `ShieldHeroView` entirely. **Delete `ShieldHeroView.swift`.**

### 5.1 Concept (chosen, justified)

**Direction 1 — the icon's V-shape itself, animated.** Justification:

- **Brand fidelity**: it IS the icon. The user's first emotional anchor (App icon → first launch hero) is identical, which collapses brand learning to zero.
- **Lower jetsam pressure** than particle systems: the host app, not the extension, animates this — but even still, two `Path` shapes + radial gradients are dramatically cheaper than CAEmitterLayer particles.
- **State legibility**: glow intensity, glow color, and node activation map cleanly onto the 5 NEVPNStatus states without inventing new visual language.
- **Iconic recognizability**: any glance at the screen reads as "Vertex". A constellation (Direction 2) is too abstract; particle flow (Direction 3) is busy and competes with the status word for attention.

We **borrow** Direction 2's "node activation sequence" idea for the *connecting* state only, where it adds narrative tension to the handshake without dominating the steady states.

### 5.2 Geometry

The icon's normalized geometry (1024×1024 box) converts to a 220×220 hero canvas:

| Element | Icon space (1024) | Hero space (220) |
|---|---|---|
| Endpoint A (top-left node) | (280, 304) | (60.2, 65.3) |
| Endpoint B (top-right node) | (744, 304) | (159.8, 65.3) |
| Vertex (bottom node) | (512, 720) | (110.0, 154.7) |
| Edge stroke width | 56 | 12.0 |
| Endpoint dot radius | 48 | 10.3 |
| Vertex dot radius | 72 | 15.5 |
| Endpoint glow radius | 140 | 30.1 |
| Vertex glow radius | 240 | 51.6 |

Hero canvas: **220×220 pt**, with extra invisible padding of 24pt for blur to spill (so the actual `frame` is `268×268`). Center horizontally in `ConnectScreen`. Use a `ZStack` of layers from back to front:

1. **Ambient halo** (`Canvas` or `RadialGradient` rect) — soft gradient from `glow.primary` at center to clear, radius ~140pt. Drives global state mood.
2. **Endpoint A glow** — radial gradient circle (matches `eGlow11`).
3. **Endpoint B glow** — radial gradient circle.
4. **Vertex glow** — radial gradient circle (matches `vGlow11`, larger and brighter).
5. **Edge A** — `Path` from endpoint A through vertex, extended past vertex (matches the asymmetric 825-y extension in the SVG).
6. **Edge B** — `Path` from endpoint B through vertex, extended past vertex.
7. **Endpoint A core** — solid white circle.
8. **Endpoint B core** — solid white circle.
9. **Vertex core** — solid white circle (largest).

Each layer's color and opacity is keyed off the **current state**.

### 5.3 States

| State | Endpoint A core | Endpoint B core | Vertex core | Edges | Halo | Motion |
|---|---|---|---|---|---|---|
| **.disconnected** | `glyph.dim` 60% | `glyph.dim` 60% | `glyph.dim` 70% | `glyph.dim` 50%, stroke 8 (thinner) | none | static, no animation |
| **.connecting** | flicks active in sequence: A → B → Vertex (per ~300ms each, looping) | same | same, brightest when active | white 70% pulsing in sync with active node | `state.transitioningGlow` 40%, pulse 0.9s | endpoint pulse + edge "fill" sweep from endpoint to vertex |
| **.reasserting** | Same as connecting but cycle is 1.4s (slower, less anxious) | | | | tinted `state.transitioningGlow` 30% | slower pulse |
| **.connected** | white 100% | white 100% | white 100% (slightly brighter via glow) | white 100% stroke 12 | `glow.primary` 60% with breathing 2.4s loop | gentle breath: scale 1.00↔1.04, opacity 70%↔100% on halo only (cores stay solid) |
| **.disconnecting** | white 80% fading out | same | same | white 80% fading | dimming halo | one-shot 0.6s fade-down to disconnected |
| **.invalid (error)** | `state.error` 100% | `state.error` 100% | `state.error` 100% | `state.error` 90% | `state.errorGlow` 55% | one-shot 360ms shake (±6pt x), then static red |

### 5.4 SwiftUI implementation hints

```swift
// File: clients/ios/Vertex/App/Views/Components/VertexHero.swift

import NetworkExtension
import SwiftUI

struct VertexHero: View {
    let status: NEVPNStatus
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0/30.0, paused: shouldPause)) { ctx in
            Canvas { gc, size in
                draw(into: gc, size: size, time: ctx.date)
            }
            .frame(width: 220, height: 220)
            .padding(24) // halo spill
            .accessibilityElement()
            .accessibilityLabel(a11yLabel)
            .accessibilityValue(a11yValue)
        }
    }

    private var shouldPause: Bool {
        reduceMotion || status == .disconnected || status == .invalid
    }
    // draw(into:size:time:) — implements layering described in §5.2 + §5.3
}
```

Key implementation guidance:

- **Use `Canvas` (SwiftUI)**, not `Image(systemName:)`. Canvas gives us per-frame control with `TimelineView(.animation)` and pauses cleanly when reduce-motion is on.
- **Don't use Metal or `CALayer` directly** — Canvas is sufficient; the spec is intentionally cheap (5 circles + 2 paths + 3 radial gradients per frame on the host app, not the extension).
- **Edge sweep effect** for connecting: draw the edge as a path, then over it draw a moving `LinearGradient`-clipped stroke from endpoint to vertex using `time`-modulated `t ∈ [0,1]` and `path.trimmedPath(from:0, to:t)`.
- **Halo breath** uses `let phase = sin(time * (2π / 2.4))` mapped to alpha and scale.
- **State changes** cross-fade with `.contentTransition(.opacity)` — when switching states, render two heroes in a `ZStack` and crossfade — or use one Canvas with smoothly interpolated state-target floats (cleaner; recommended).
- **No `symbolEffect`** — those are for SF Symbols and we're done with shields.
- The hero itself **does not own** the status text "Connected"; that lives outside in `ConnectScreen.hero` (kept as today, but restyled to `font.heroStatus`).

### 5.5 Layout in ConnectScreen

```
   space.10 (40pt top)
┌─────────── canvas ───────────┐
│                              │
│        [VertexHero 268]      │   ← centered, 268×268 (220 + 24 halo padding both sides)
│                              │
│   space.4 (16pt)             │
│      "Connected"             │   ← font.heroStatus, color `state.connected`
│   space.3 (12pt)             │
│   ┌──────────────────┐       │
│   │ • 10.9.0.42 · 7:23 │     │   ← StatusPillView (capsule)
│   └──────────────────┘       │
└──────────────────────────────┘
```

---

## 6. Component specs

### 6.1 `BigConnectButton` (refresh)

**Visual** — a wide capsule that mirrors the hero's energy.

- Disconnected (idle): fill = `accent.primary`, text = `text.onAccent`, soft `glow.button` halo behind (24pt blur, opacity pulses 60%↔100% via `Motion.buttonGlow`). Subtle outward "breathing" suggesting "tap me".
- Pressed: scale 0.97 + glow snaps to 100% for one frame.
- Transitioning: fill = `accent.primaryMuted`, text = `text.primary`, **no glow**, label "Cancel", inline `ProgressView()` tinted `accent.primary`.
- Connected: fill = `bg.surfaceMuted`, **no glow**, text "Disconnect" in `state.error`. (Disconnect is a destructive action — always available, never glowing — visual quietness is the cue.)
- Disabled (rare; e.g. permission flow): opacity 0.4, no animations.

**Spec**: min height 56pt, padding horizontal 24pt, capsule corner, font SF Pro Rounded 18 Semibold, glow only when idle, press scale 0.97.

### 6.2 `ServerCard` (refresh)

Replace cliché icons with custom Vertex glyphs (see §8). Glyphs use `accent.primary`. Cell-divider stays but uses `border.subtle`.

**Subheads change** to reflect Vertex semantics (no "VPN", no "tunnel"):

- Row 1: title `"Vertex"` (was "Broker"), subhead = host (e.g. `mqtt-yc.vertices.ru`), accessory monospaced uppercase scheme (`MQTTS`).
- Row 2: title `"Edge"` (was "Exit"), subhead = friendly name (e.g. `Stockholm, Sweden`), accessory = code chip (`E₁ · STO`) using `font.captionMono` in `text.tertiary`.

The terminology Vertex/Edge is on-brand and continues to hide the VPN nature.

**Layout**:

```
┌────────────────────────────────────────────┐
│  ✱   Vertex                                │
│      mqtt-yc.vertices.ru       MQTTS  ›    │
│  ─── (border.subtle, inset 56) ───         │
│  ↗   Edge                                  │
│      Stockholm, Sweden         E₁·STO ›    │
└────────────────────────────────────────────┘
```

The leading glyphs are 28pt-wide custom shapes (see §8): a tiny V-asterisk for Vertex rows, a single ascending edge stroke for Edge rows.

### 6.3 `StatusPillView` (refresh)

- Background: `.thinMaterial` over `bg.canvas` — for proper contrast on dark navy, set `colorScheme: .dark` on the pill.
- Dot color: `state.connected` for connected (was green), `state.transitioning` for connecting/reasserting/disconnecting, `state.dormant` for disconnected, `state.error` for invalid.
- Dot has a subtle `glow.primary` 8pt halo when connected (small; only visible on dark mode).
- Text: `font.callout.monospacedDigit()` (keep), color `text.primary`.
- Padding 14h × 7v (keep).

### 6.4 `StatRowView` (refresh)

- Background: `bg.surface` solid (drop `.regularMaterial`).
- Border: `border.subtle` 0.5pt.
- Icons: `arrow.up` and `arrow.down` SF Symbols, color `accent.primary`. Add a tiny `accent.primaryMuted` 4pt circle behind each icon — turns the icon into a small "node" (subtle Vertex motif).
- Values: `font.statValue` in `text.primary`. Labels in `text.secondary`.
- Divider in middle: `border.subtle`, height 28 (keep).

### 6.5 Picker rows (`BrokerListView` / `ExitListView`)

- List background: `bg.canvas`. Use `.scrollContentBackground(.hidden)` and apply background gradient.
- Row background: `bg.surface` with `radius.lg` insets, hairline `border.subtle`.
- Selected row: `border.strong` 1pt, leading glyph and host text gain `text.primary`. Trailing checkmark **replaced** with the **vertex selection glyph** — a small filled V-asterisk in `accent.primary` with a 6pt `glow.primary` halo.
- Refresh chevron in toolbar: SF Symbol `arrow.clockwise` (keep) tinted `accent.primary`.
- Section header: uppercased, tracked, `font.caption`, `text.tertiary`.
- Footer: `font.footnote`, `text.secondary`.
- Wording: header "Available Vertices" / "Available Edges" (replaces "Brokers" / "Exits").

### 6.6 Loading states

- **Discovery loading**: single full-row skeleton (rounded rect 56pt tall, `bg.surfaceMuted`, shimmer using `accent.primaryMuted` traveling left→right at 1.8s loop).
- **Empty state**: inline empty card with the V glyph dim, title "No vertices discovered yet", body "Pull to retry. Defaults will be used in the meantime."

---

## 7. Screen specs

### 7.1 Canvas & background gradient (every screen)

Reusable `VertexCanvas` view modifier:

```swift
struct VertexCanvas: ViewModifier {
    func body(content: Content) -> some View {
        content
            .background {
                RadialGradient(
                    colors: [.bgCanvasTop, .bgCanvasBottom],
                    center: .init(x: 0.5, y: 0.55),
                    startRadius: 0,
                    endRadius: 600
                )
                .ignoresSafeArea()
            }
            .preferredColorScheme(.dark) // override system in this app
    }
}
```

Apply globally at `RootView`. Light mode is opt-in via Settings → "Match system appearance" toggle (future); for v1, lock to dark.

### 7.2 `RootView`

- NavigationStack background: clear; canvas underneath.
- Nav bar: large title disabled; inline title shows the brand wordmark "VERTEX" in `font.brandWordmark`, color `text.primary`. Replace `.navigationTitle("Vertex")` with `.toolbar { ToolbarItem(.principal) { Text("VERTEX").font(.brandWordmark).tracking(1.5) } }`.
- Trailing toolbar: `gear` SF Symbol, color `accent.primary`.
- Background: `VertexCanvas` modifier applied at this level.

### 7.3 `ConnectScreen` (the marquee)

```
┌────────── safe area ───────────┐
│   VERTEX            ⚙           │  ← nav bar, inline
├────────────────────────────────┤
│                                │
│         [VertexHero]           │  ← 268×268, centered
│                                │
│         "Connected"            │  ← font.heroStatus, state color
│                                │
│      • 10.9.0.42 · 7:23        │  ← StatusPill capsule
│                                │
│  ┌──────────────────────────┐  │
│  │ ✱  Vertex                │  │
│  │    mqtt-yc.vertices.ru MQTTS│  ← ServerCard
│  │ ↗  Edge                  │  │
│  │    Reykjavík, Iceland    │  │
│  └──────────────────────────┘  │
│                                │
│  ┌──────────────────────────┐  │
│  │      Disconnect          │  │  ← BigConnectButton
│  └──────────────────────────┘  │
│                                │
│  ┌──────────────────────────┐  │
│  │ ↑ Sent  12.4 MB          │  │  ← StatRow (only when connected)
│  │ ↓ Recv  84.2 MB          │  │
│  └──────────────────────────┘  │
│                                │
└────────────────────────────────┘
```

Vertical rhythm: hero → 16 → status text → 12 → pill → 28 → ServerCard → 28 → connect button → 28 → stats → 32 (bottom).

Background: canvas gradient. **Remove** the `.background(Color(uiColor: .systemBackground))` line in current `ConnectScreen.body`.

### 7.4 `SettingsScreen`

- Form background: `.scrollContentBackground(.hidden)` + canvas.
- Section background: `bg.surface` via `.listRowBackground(Color.bgSurface)`.
- Section headers: uppercased, `font.caption`, `text.tertiary`.
- Section footers: `font.footnote`, `text.secondary`.
- All `LabeledContent` value text: `text.primary`. Labels: `text.secondary`.
- Identity section icon `key.fill` → use as-is, tinted `accent.primary`.
- About icon `info.circle` → tinted `accent.primary`.

### 7.5 `IdentityKeyView`

- Same canvas + form treatment.
- Pubkey hex block: `font.identityHex`, color `text.primary`, background `bg.surfaceMuted` with `radius.md`, padding 12, scrollable horizontally if needed.
- Copy button: capsule, `accent.primaryMuted` background, `accent.primary` text/icon, height 44 (was 36 — bumped for accessibility). On copy, swap to `checkmark` and tint background `state.connected` for 2s.

### 7.6 `AboutView`

- "How it works" copy: must avoid "VPN", "tunnel", "encrypted traffic". Replace with brand-aligned copy:

> "Vertex routes your device through a trusted network vertex — a meeting point where edges converge. Every connection is end-to-end protected with modern cryptography (X25519 + ChaCha20-Poly1305); no relay along the path can read or alter your data."

- Source-code link icon: keep `chevron.left.forwardslash.chevron.right` (neutral).

### 7.7 `BrokerListView` & `ExitListView`

Already covered in §6.5. Rename navigation titles: "Vertex" (was "Broker"), "Edge" (was "Exit"). Section headers: "Available Vertices" / "Available Edges". Footers:

> Vertices are tried in SRV-priority order. The selected vertex leads; the rest serve as failover.

> The edge is the network shoulder where your traffic exits to the public internet.

### 7.8 `StatsSheet`

- `presentationBackground(.regularMaterial)` → keep, but set `.preferredColorScheme(.dark)` and `presentationCornerRadius(28)`.
- Section "Connection" → "Vertex" (label only). "Exit" row → "Edge".
- Add a **mini hero** at the top of the sheet: a 96×96 inline `VertexHero` mirroring current state, no halo padding. Beneath it: status text. Above the lists.

### 7.9 `PermissionDeniedView`

**Replace `lock.shield`** with the **VertexHero in a special "locked" state**:

- Render `VertexHero` at 180×180, status `.disconnected`, but tint glyphs `state.transitioning` (amber) and add a **small lock glyph** (`lock.fill`, 20pt) overlaid on the vertex node — implies "the vertex is locked, not the user". This avoids the security-shield trope while signaling permission gating.
- Background: canvas gradient.
- Title: "Permission required" (drop "VPN").
- Body: "Vertex needs permission to add a network configuration on this device. Tap **Try again** and approve the system prompt."
- Buttons: "Try again" → primary capsule `accent.primary`. "Open Settings" → outline capsule, border `border.strong`, text `text.primary`. "Cancel" → text-only, `text.secondary`.

---

## 8. SF Symbol replacements & custom glyphs

| Where | Current | New | Reason |
|---|---|---|---|
| `ShieldHeroView` | `shield.*` family | **`VertexHero` Canvas-drawn shape** | Shield = VPN cliché |
| `ServerCard` row 1 (Broker) | `antenna.radiowaves.left.and.right` | **Custom `VxAsteriskGlyph`** (mini V-asterisk Shape) | Antenna = infrastructure tell |
| `ServerCard` row 2 (Exit) | `globe` | **Custom `VxEdgeGlyph`** (single ascending stroke with terminal dot) | Globe = "I'm connecting to the world" cliché |
| `BrokerListView` row | `antenna.radiowaves.left.and.right` | `VxAsteriskGlyph` | Same |
| `ExitListView` row | `globe` | `VxEdgeGlyph` | Same |
| Picker selection check | `checkmark` | **`VxSelectionGlyph`** — tiny filled V-asterisk + 6pt accent halo | Brand-coherent "current selection" mark |
| `PermissionDeniedView` | `lock.shield` | `VertexHero` in disconnected/amber state + small `lock.fill` overlay | Decouples lock from shield |
| Errors banner | `exclamationmark.triangle.fill` (orange) | keep, retint to `state.error` | Error is error |
| Stats `arrow.up` / `arrow.down` | keep | keep | Universal |

### 8.1 Custom glyph specs

#### `VxAsteriskGlyph` (mini-V)

- Canvas 24×24.
- Two strokes from (6, 8) → (16.5, 22) and (18, 8) → (7.5, 22), stroke width 2, `lineCap: .round`, color `accent.primary`.
- Three small filled circles at the endpoints (radius 1.6) and the meeting vertex (radius 2.0) in `accent.primary`.
- Used at 18–20pt for ServerCard/list rows.

#### `VxEdgeGlyph` (single edge)

- Canvas 24×24.
- One stroke from (4, 18) → (20, 6), stroke width 2, `lineCap: .round`, color `accent.primary`.
- One filled circle at (20, 6) radius 2.4 (the destination "node"), `accent.primary`.
- Used at 18–20pt.

#### `VxSelectionGlyph` (selected)

- Same as `VxAsteriskGlyph`, but all fills/strokes are solid `accent.primary` (no thin strokes), and the parent is wrapped in a `.background(Circle().fill(accentPrimary).blur(radius: 6).opacity(0.35))` halo of 28pt frame. Used as the picker checkmark replacement.

#### `VxLockedHero` (composition)

- Z-stacks `VertexHero` (status `.disconnected`, amber-tinted) with `Image(systemName: "lock.fill").font(.system(size: 20)).foregroundStyle(.stateTransitioning)` centered on the vertex node. Used only in `PermissionDeniedView`.

---

## 9. Accessibility

### 9.1 Contrast (WCAG 2.1 AA)

| Combo (dark) | Ratio | Passes |
|---|---|---|
| `text.primary` (#FFFFFF) on `bg.canvas` (#101A3D mid) | ~17.2 : 1 | AAA |
| `text.secondary` (#C4D2EE 78%) on `bg.canvas` | ~10.4 : 1 | AAA |
| `text.tertiary` (#8497C2 60%) on `bg.canvas` | ~5.1 : 1 | AA |
| `accent.primary` (#7DB3FF) on `bg.canvas` | ~7.6 : 1 | AAA (large text & UI) |
| `text.onAccent` (#0B1638) on `accent.primary` (#7DB3FF) | ~9.0 : 1 | AAA |
| `state.error` (#FF6E78) on `bg.canvas` | ~5.4 : 1 | AA |
| `state.transitioning` (#FAD27A) on `bg.canvas` | ~10.7 : 1 | AAA |

If any combo falls under 4.5:1 in production tests, **darken `text.tertiary` light-mode value to `#5A6884`**.

### 9.2 VoiceOver labels for the hero

`VertexHero` is a single accessibility element.

| State | `accessibilityLabel` | `accessibilityValue` |
|---|---|---|
| .connected | "Vertex status" | "Connected. Network is active." |
| .connecting | "Vertex status" | "Connecting." |
| .reasserting | "Vertex status" | "Reconnecting." |
| .disconnecting | "Vertex status" | "Disconnecting." |
| .disconnected | "Vertex status" | "Not connected." |
| .invalid | "Vertex status" | "Configuration error." |

Add `.accessibilityAddTraits(.updatesFrequently)` only during connecting/reasserting.

### 9.3 Dynamic Type

- Hero status text: clamp to `.accessibility2`.
- All other text: support up to **AX5**.
- Custom glyphs: scale with `@ScaledMetric` driven by `.body` or `.title3`.
- Hero canvas (220pt) is **fixed**: brand element, doesn't scale with Dynamic Type.

### 9.4 Reduce Motion fallbacks

When `accessibilityReduceMotion` is true:

- Hero halo breathing: **off** (hold at midpoint opacity).
- Hero connecting node sweep: **off** (show all three nodes at 60% brightness, no animation).
- Connect button glow pulse: **off** (static glow at 80% strength).
- Hero state replace: **instant** (or 120ms `.easeOut` fade max).
- Particle / edge sweep effects: **never run**.

`shouldPause` in §5.4 captures this.

### 9.5 Touch targets

- All interactive rows ≥ 44pt tall.
- BigConnectButton 56pt — fine.
- Pubkey copy button: bump from 36pt to **44pt** minimum.

---

## 10. Implementation roadmap

### Phase A — Tokens & primitives

1. Create `clients/ios/Vertex/App/Theme/` directory.
2. Create `Assets.xcassets` color sets per §1.5 (24 entries). Repoint existing `AccentColor` to the new accent hexes.
3. Create `Theme/Color+Vertex.swift`.
4. Create `Theme/Font+Vertex.swift`.
5. Create `Theme/Spacing.swift` and `Theme/Radius.swift`.
6. Create `Theme/Motion.swift`.
7. Create `Theme/VertexCanvas.swift` (view modifier).

### Phase B — Glyphs

8. Create `App/Views/Components/Glyphs/VxAsteriskGlyph.swift`.
9. Create `App/Views/Components/Glyphs/VxEdgeGlyph.swift`.
10. Create `App/Views/Components/Glyphs/VxSelectionGlyph.swift`.

### Phase C — Hero

11. Create `App/Views/Components/VertexHero.swift`.
12. **Delete** `App/Views/Components/ShieldHeroView.swift`.

### Phase D — Refresh existing components

13. Modify `StatusPillView.swift`.
14. Modify `StatRowView.swift`.
15. Modify `ServerCard.swift`.
16. Modify `BigConnectButton.swift`.

### Phase E — Screens

17. Modify `RootView.swift` — apply `VertexCanvas`, brand wordmark, dark scheme.
18. Modify `ConnectScreen.swift` — drop `systemBackground`, use canvas, swap hero.
19. Modify `BrokerListView.swift` — Vertex copy, custom glyph.
20. Modify `ExitListView.swift` — Edge copy, glyph, E₀/E₁/E₂ chips.
21. Modify `SettingsScreen.swift` — token recolor, hidden list bg.
22. Modify `IdentityKeyView.swift` — restyled hex block, copy button capsule.
23. Modify `AboutView.swift` — rewrite copy to drop "VPN" language.
24. Modify `StatsSheet.swift` — mini hero at top, Vertex/Edge labels.
25. Modify `PermissionDeniedView.swift` — replace `lock.shield` with `VxLockedHero`.

### Phase F — ViewModel adjustments

26. Modify `TunnelViewModel.swift`:
    - `statusColor` → return `Color.stateConnected` / `.stateTransitioning` / `.stateDormant` / `.stateError`.

### Phase G — Polish & QA

27. Verify contrast ratios with Accessibility Inspector.
28. Verify Reduce Motion.
29. Verify Dynamic Type from xSmall to AX5.
30. Run on physical device only.
31. Verify host app stays under jetsam baseline.

### Files to create (new)

```
clients/ios/Vertex/App/Theme/Color+Vertex.swift
clients/ios/Vertex/App/Theme/Font+Vertex.swift
clients/ios/Vertex/App/Theme/Spacing.swift
clients/ios/Vertex/App/Theme/Radius.swift
clients/ios/Vertex/App/Theme/Motion.swift
clients/ios/Vertex/App/Theme/VertexCanvas.swift
clients/ios/Vertex/App/Views/Components/VertexHero.swift
clients/ios/Vertex/App/Views/Components/Glyphs/VxAsteriskGlyph.swift
clients/ios/Vertex/App/Views/Components/Glyphs/VxEdgeGlyph.swift
clients/ios/Vertex/App/Views/Components/Glyphs/VxSelectionGlyph.swift
```

### Files to delete

```
clients/ios/Vertex/App/Views/Components/ShieldHeroView.swift
```

---

## 11. Brand-language copy crib

| Old (forbidden) | New |
|---|---|
| VPN | (omit) or "private network" only in legal/Apple-mandated contexts |
| Tunnel | "connection" |
| Broker | **Vertex** (in user UI), `V₀ / V₁ / V₂` (in settings/stats detail) |
| Exit / Exit node | **Edge** (in user UI), `E₀ / E₁ / E₂` (codes) |
| Shield / Protected | "Active" / "Connected" |
| Encrypted traffic | "End-to-end protected" |
| Server | "Vertex" or "Edge" depending on role |
| Tunnel ready | "Vertex active" |

The tagline **"Where paths meet"** is suitable as a footer in `AboutView` under the version row.

---

## 12. Visual system summary

- **Canvas is the void**: deep navy radial gradient. Nothing competes with it for area.
- **Glow is the message**: every important live element radiates softly. Idle elements absorb light.
- **The V is sacred**: hero, row glyphs, selection mark. Three sizes (220, 24, 20). One color family (white core → accent halo → fade).
- **Type is restrained**: rounded for brand voice, mono for facts, default text for everything else. Two weights: regular and semibold.
- **Motion is ambient**: slow breaths (2.4s), purposeful pulses during handshake (0.9s), one-shot springs on state changes. Never disco.
- **Words hide intent**: Vertex, Edge, "active", "connected" — never "VPN", never "tunnel", never "shield".

If a future component needs a decision and this spec doesn't cover it, the rule is: *would it look at home in the icon's universe?* If not, it doesn't ship.

---

**End of specification.**
