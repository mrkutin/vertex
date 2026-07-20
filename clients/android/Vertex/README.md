# Vertex Android

Native Kotlin Android client for the Vertex VPN.

See [`../PLAN.md`](../PLAN.md) for the full design and roadmap.

## Requirements

- **JDK 17** (bundled with Android Studio Ladybug+)
- **Android SDK** with platform 35 + build-tools 35
- Physical Android device, **API 26+ (Android 8.0)**
- `ANDROID_HOME` (or `ANDROID_SDK_ROOT`) pointing to the SDK install

The simplest path: install [Android Studio](https://developer.android.com/studio) — it brings JDK 17, SDK Manager, and the emulator in one package.

## Build

From the repository root:

```bash
make build-android            # Debug APK  -> dist/android/Vertex-android-debug.apk
make build-android-release VERSION=1.0.0   # Signed Release APK
```

Or, from inside this directory:

```bash
./gradlew :app:assembleDebug
./gradlew :core:test
```

## Project layout

```
Vertex/
├── app/      :app    — UI (Compose), ViewModels, Application, MainActivity
├── core/     :core   — Pure Kotlin: crypto, MQTT 5.0, wire-protocol, IPC types
├── vpn/      :vpn    — VpnService, packet pipeline, foreground notification, identity store
├── gradle/   Version catalog (libs.versions.toml) and Gradle wrapper jar
└── ...
```

## Wire protocol

Byte-exact compatible with the iOS Swift `VertexCore` reference and the Go reference. See `../PLAN.md` § "Wire-format byte-exact invariants" for the full list.

Critical constants:
- HKDF info: `"broker-tunnel-v1"`
- Identity HMAC label: `"vtx-identity-v1"`
- Encrypted packet: `[12B nonce][ciphertext][16B tag]`

## Status

**Phase 1 (MVP) — under construction.** See `../PLAN.md` for the four-phase roadmap.
