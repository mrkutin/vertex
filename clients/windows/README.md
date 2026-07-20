# Vertex Windows Client

Native Windows client for the Vertex VPN. Wire-protocol parity with iOS / macOS
(Swift) and Android (Kotlin) — same MQTT 5.0 + X25519 ECDH +
ChaCha20-Poly1305 + HKDF + identity TOFU.

UI parity with the macOS app: dark canvas, V-asterisk wordmark, animated
VertexHero, single Connect button.

See [`PLAN.md`](PLAN.md) for the multi-phase roadmap. This README covers
**how to build** — which is `Phase 0`.

---

## Repository layout (Phase 0)

```
clients/windows/
├── Vertex.sln
├── Directory.Build.props        # Nullable, deterministic, version
├── global.json                  # .NET 8 SDK pin
├── src/
│   ├── Vertex.Shared/           # IPC types shared by Service + App
│   ├── Vertex.Core/             # MQTT / crypto / discovery (Phase 1)
│   ├── Vertex.Core.Tests/       # xUnit + FluentAssertions
│   ├── Vertex.Service/          # Windows Service (LocalSystem) — owns TUN
│   └── Vertex.App/              # WinUI 3 desktop UI
├── tools/
│   └── make.ps1                 # build / test / publish helpers
└── packaging/                   # WiX 4 MSI (Phase 5)
```

## Dev workflow — Mac edits, VM builds

Code is edited on the Mac in `~/Projects/vertex/clients/windows`. The
Parallels VM `vertex-win` (10.211.55.3, Win 11 ARM64) sees the same
checkout via `\\Mac\vertex` (Parallels Shared Folder).

> **Use UNC, not `X:`.** Drive mappings live in the GUI logon session and
> are not visible from SSH. Always say `\\Mac\vertex\…`.

Build / test on the VM (default shell is Windows PowerShell 5.1, the
script targets that — `pwsh` would also work if installed):

```bash
ssh vertex-win 'cd \\Mac\vertex\clients\windows; .\tools\make.ps1 restore'
ssh vertex-win 'cd \\Mac\vertex\clients\windows; .\tools\make.ps1 build'
ssh vertex-win 'cd \\Mac\vertex\clients\windows; .\tools\make.ps1 test'
```

Publish a self-contained AMD64 build:

```bash
ssh vertex-win 'cd \\Mac\vertex\clients\windows; .\tools\make.ps1 publish-amd64'
```

## Prereqs in the Windows VM

Already installed (per `feedback_windows_via_unc.md`): .NET 8 SDK 8.0.420
ARM64, Git, OpenSSH Server.

One-time relax of PowerShell ExecutionPolicy so `make.ps1` runs over SSH:

```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned -Force
```

To install before Phase 1:

```powershell
# Windows App SDK runtime/MSIX tools
winget install --id Microsoft.WindowsAppRuntime.1.6 --silent

# WiX Toolset 4 (for Phase 5 MSI)
dotnet tool install --global wix

# (Optional) Visual Studio Build Tools 2022 with the
# "Desktop development with C++" + "Windows App SDK C# Templates" workloads
# only if you want to open the .sln in VS. CLI builds work without it.
```

## SemVer rule

Bump `<VersionPrefix>` in `Directory.Build.props` *before* every
`make build-windows-release` / `xcodebuild`-equivalent action. Project rule;
duplicate versions break our deploy story (and Apple/MSIX will reject
duplicate `CFBundleVersion` / `Version`).

```powershell
.\tools\make.ps1 build -VersionPrefix 0.2.0
```

## Phase 0 sanity check

```bash
ssh vertex-win 'cd \\Mac\vertex\clients\windows; .\tools\make.ps1 build'
ssh vertex-win 'cd \\Mac\vertex\clients\windows; .\tools\make.ps1 test'
```

The IPC contract round-trip tests (`Vertex.Core.Tests.IpcContractTests`)
must pass — they pin the wire-format strings so future drift away from
Swift / Kotlin field names becomes a compile-time / test-time failure.
