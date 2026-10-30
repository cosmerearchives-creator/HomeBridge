# HomeBridge — native Windows rebuild

HomeBridge makes useful Windows and local-network tools easier to operate.

**Current status: .NET 10 migration preview (not yet feature-parity with Python v0.5.1).**

## Native implementation status

| Module | Status |
| --- | --- |
| Local bulk rename | Functional preview, apply, in-session undo; files only |
| Local application launcher | Functional while app is open; registrations are not persisted |
| Device discovery and pairing | Migration pending |
| File sharing and transfers | Migration pending |
| Screen mirroring and remote control | Migration pending |
| Roku DLNA server | Migration pending |
| Android companion | Existing Kotlin prototype remains separate; integration pending |

No network listener, file server, remote execution endpoint, or Windows background service is installed by this early native build.

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
