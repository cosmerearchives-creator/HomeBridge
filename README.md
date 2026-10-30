# HomeBridge — native Windows rebuild

HomeBridge makes useful Windows and local-network tools easier to operate.

**Current status: .NET 10 migration preview (not yet feature-parity with Python v0.5.1).**

## Native implementation status

| Module | Status |
| --- | --- |
| Local bulk rename | Functional preview, apply, in-session undo; files only |
| Local application launcher | Functional while app is open; registrations are not persisted |
| Device discovery | Implemented (local broadcast, unauthenticated device names only)
| Device pairing | Migration pending |
| File sharing and transfers | Migration pending |
| Screen mirroring and remote control | Migration pending |
| Roku DLNA server | Migration pending |
| Android companion | Existing Kotlin prototype remains separate; integration pending |

The installer now registers HomeBridgeDiscovery, a minimal Windows service that responds to local discovery requests. It does not expose files, remote execution, pairing, or any management API. Discovery is informational only and must not be treated as authenticated identity. The GUI saves user settings in LocalAppData.

## Build

Open `HomeBridge.slnx` in Visual Studio with the .NET 10 SDK and Windows Desktop workload, or run:

```powershell
dotnet build HomeBridge.slnx -c Release
dotnet run --project src/HomeBridge.Desktop/HomeBridge.Desktop.csproj
```

## Installer

GitHub Actions > **Windows installer** builds a self-contained Win-x64 .NET 10 application and packages it using Inno Setup. Download the `HomeBridge-Windows-Installer` build artifact from a successful run. The installer creates Start Menu shortcuts, optional desktop shortcut, and a normal Add/Remove Programs entry.

Automatic self-updating is **not yet implemented**. Future work should add signed release artifacts, verification and an updater that safely handles running Windows services.

## Security

Do not publish previous HomeBridge access tokens, certificate private keys, pairing secrets, or user configurations in this public repository. Old prototype code is intentionally not imported until credentials and sensitive local state are audited. Only launch EXEs you trust.

## Roadmap

1. Stable native application shell, persistent settings, diagnostics, module contracts.
2. Secure device discovery, per-device keys and mutual authentication.
3. Authenticated transfers, local-network permissions, Explorer integration.
4. Background Windows service and per-user interactive agent.
5. Remote application launching and Roku DLNA media service.
6. Android pairing/transfer interoperability and hardware-accelerated screen streaming.
7. Signed installers, automatic update checks, package verification and rollback.

Developed for Windows 11; Roku and Android support will be migrated in stages.

## Discovery service

The installer registers `HomeBridgeDiscovery` using Windows Service Control Manager. It responds to UDP broadcast requests on port 45679 with the computer name; it has no remote action endpoint. Windows Firewall may block incoming discovery traffic; allow UDP 45679 only on trusted Private profiles as needed. Do not forward this port on your router. You can check the service in `services.msc`.

**Security milestone still pending:** authenticated pairing with unique device credentials and verification codes. Never authorize file or command execution merely because a device appeared in discovery.

## Download a native Windows installer

**Latest experimental native preview:** https://github.com/cosmerearchives-creator/HomeBridge/releases/tag/native-preview

**Direct installer:** https://github.com/cosmerearchives-creator/HomeBridge/releases/download/native-preview/HomeBridge-Setup.exe

The Windows installer GitHub Actions workflow uploads its normal artifact and, on successful pushes to `main`, publishes or replaces the downloadable installer asset in the rolling `native-preview` prerelease. Pull requests do not publish releases. For now this is distribution only; automatic in-app updates and installer signing have not been implemented. Browser security prompts may appear for an unsigned installer. Install only if you trust this repository and the build. The native application does not yet include all Python-era modules.
