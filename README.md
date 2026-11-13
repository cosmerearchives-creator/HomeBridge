# HomeBridge — native Windows rebuild

## Download and run HomeBridge

**[Download HomeBridge for Windows (.exe)](https://github.com/cosmerearchives-creator/HomeBridge/releases/download/native-preview/HomeBridge-Setup.exe)**

1. Download and run **HomeBridge-Setup.exe**. No ZIP extraction or separate .NET installation is needed.
2. Complete Windows administrator approval and installation, then launch **HomeBridge** from the Start menu or the installer's launch option.
3. HomeBridge checks GitHub Releases for updates when it opens. Use **Settings → Check for updates → Download and install update** to upgrade, or change update preferences there.

[Download page and installation notes](https://github.com/cosmerearchives-creator/HomeBridge/releases/tag/native-preview) · [Version history](https://github.com/cosmerearchives-creator/HomeBridge/releases)

Windows x64 preview; the installer is currently unsigned, so Windows may show an unknown-publisher prompt. Older builds without the updater need this one-time manual installation.

HomeBridge makes useful Windows and local-network tools easier to operate.

**Current status: .NET 10 migration preview (not yet feature-parity with Python v0.5.1).**

## Native implementation status

| Module | Status |
| --- | --- |
| Local bulk rename | Functional preview, apply, in-session undo; files only |
| Local application launcher | Functional while app is open; registrations are not persisted |
| Device discovery | Implemented (local broadcast, unauthenticated device names only)
| Device pairing | Mutual TLS and matching code approval on both PCs |
| File sharing and transfers | Explicit shares, browsing, downloads and approved uploads |
| Screen mirroring and remote control | Migration pending |
| Roku DLNA server | Migration pending |
| Android and Meta Quest | SMB network file explorer and Quest panel layout |

The installer now registers HomeBridgeDiscovery, a minimal Windows service that responds to local discovery requests. It does not expose files, remote execution, pairing, or any management API. Discovery is informational only and must not be treated as authenticated identity. The GUI saves user settings in LocalAppData.

## Build

Open `HomeBridge.slnx` in Visual Studio with the .NET 10 SDK and Windows Desktop workload, or run:

```powershell
dotnet build HomeBridge.slnx -c Release
dotnet run --project src/HomeBridge.Desktop/HomeBridge.Desktop.csproj
```

## Installer

GitHub Actions > **Windows installer** builds a self-contained Win-x64 .NET 10 application and packages it using Inno Setup. Use the **Download HomeBridge for Windows** link above. Build artifacts are retained for development and diagnostics. The installer creates Start Menu shortcuts, optional desktop shortcut, and a normal Add/Remove Programs entry.

Native update checking, verified downloads and confirmed installer handoff are implemented; see Automatic updates below. Publisher signing and rollback remain future work.

## Security

Do not publish previous HomeBridge access tokens, certificate private keys, pairing secrets, or user configurations in this public repository. Old prototype code is intentionally not imported until credentials and sensitive local state are audited. Only launch EXEs you trust.

## Roadmap

1. Stable native application shell, persistent settings, diagnostics, module contracts.
2. Secure device discovery, per-device keys and mutual authentication.
3. Authenticated transfers, local-network permissions, Explorer integration.
4. Background Windows service and per-user interactive agent.
5. Remote application launching and Roku DLNA media service.
6. Android pairing/transfer interoperability and hardware-accelerated screen streaming.
7. Signed installers and automatic rollback.

Developed for Windows 11; Roku and Android support will be migrated in stages.

## Discovery service

The installer registers `HomeBridgeDiscovery` using Windows Service Control Manager. It responds to UDP broadcast requests on port 45679 with the computer name; it has no remote action endpoint. Windows Firewall may block incoming discovery traffic; allow UDP 45679 only on trusted Private profiles as needed. Do not forward this port on your router. You can check the service in `services.msc`.


## Download a native Windows installer

Get the newest versioned preview from [GitHub Releases](https://github.com/cosmerearchives-creator/HomeBridge/releases). Download its HomeBridge-Setup.exe asset. Unsigned installers may trigger browser and Windows prompts. Branch/PR builds are also available as Actions artifacts. This app does not yet include all Python-era modules.

## Discovery installer repair

The installer includes the Windows discovery service and its PowerShell registration script. It adds a Windows Firewall inbound rule for UDP 45679 restricted to the Private profile and local subnet. Reinstall HomeBridge on *both* PCs, ensure their networks are Private, and check `HomeBridgeDiscovery` in `services.msc`. The rule is removed on uninstall. Avoid disabling the firewall globally.


## Native automatic updates

Install a new installer once to obtain this updater; older discovery previews cannot update themselves. Settings shows the installed version, **Check for updates**, **Download and install update**, and preferences for startup checks and preview releases (both enabled by default). Checks work independently of LAN discovery. Failures are shown in Settings and leave the current installation intact.

Checks use the fixed HomeBridge GitHub Releases API over HTTPS. Semantic version comparison rejects equal or older versions; drafts and optionally prereleases are excluded. The newest of the latest 20 release records is selected. Only this repository's exact HomeBridge-Setup.exe download URL is accepted. The installer must have GitHub's SHA-256 digest and a valid size. Redirects allow only HTTPS GitHub/release-assets hosts; downloads have size and time limits. The downloaded bytes are checked against both size and digest, then held with a read-only file lock through installer launch. Failed downloads are deleted.

You confirm installation before download. The normal installer requests Windows administrator approval, and HomeBridge closes only after successful handoff. Declining elevation leaves the current app running. Save work first: in-session rename undo is lost on exit. Settings and discovery service configuration remain in place. Successful installer files remain under LocalAppData/HomeBridge/Updates; remove them when no installer is running.

GitHub HTTPS and repository ownership are the trust root. The digest protects against corrupted downloads, not a compromised repository. Installers remain unsigned (Windows may show unknown publisher). No silent installation or automatic rollback is provided. Interactive UAC cancellation, installer upgrade and a real two-PC discovery regression should be manually checked before broad distribution.

### Publishing versions

Every successful main build now publishes a separate `v1.0.0-preview.RUN_NUMBER` prerelease instead of replacing the same rolling asset. The embedded application version, installer version and `update.json` metadata agree. The metadata records version, commit, installer name, size and SHA-256; the updater trusts GitHub's independently returned asset digest. Releases stay draft until both assets are uploaded. Branch/PR builds run update tests and upload artifacts without publishing. Only the release job has repository write permission.

For a stable or manually versioned release, set the desktop project Version and installer default MyAppVersion to the same semantic version and push a matching `vVERSION` tag. Tags must match the project version. Existing published versions are never clobbered; publish a new version after a failure or correction. The permanent native-preview download is refreshed only after a complete versioned release is published. The semantic-version updater uses versioned releases, independently of this download alias.

## Pair and share files

Install this version on both Windows PCs and open HomeBridge. On a trusted Private network, open **Devices**, select **Allow pairing** on the receiving PC, and select the other PC under **Find nearby PCs** on the sending PC. Select **Pair selected PC**, compare the code, and approve on both screens. Pairing and transfers stay on the LAN. Allow pairing stays open until you stop it, successfully pair, or exit HomeBridge. After five attempts, select Allow pairing again. **Forget selected PC** revokes that PC's access from the current computer.

Open **Files** to share specific files or folders. A share grants all currently paired PCs read access. To receive uploads, explicitly choose **Allow uploads to folder**. Select a paired PC and **Open shared files** to browse, download, or upload to an approved destination. Multi-file and folder transfers keep existing files. If a duplicate or network error stops a transfer, completed files remain and unfinished files are discarded where possible. Shared files stay in place when you add or remove a share. Transfers support local fixed drives and skip links and junctions. The file size limit is 10 GiB.

Windows 11 Explorer commands appear under **Show more options**. Send to Computer stages an item in HomeBridge for you to choose the paired PC and upload folder. Use the HomeBridge file picker for selecting multiple files at once.

**Devices → Diagnose** checks the network profile, discovery service, active firewall rules, UDP replies and paired connections. **Diagnose & repair** requests confirmation and Windows administrator approval before restoring HomeBridge's service and Private-network rules. It then repeats the checks. Use the standard Program Files installation for repair.

The Windows discovery service continues operating after HomeBridge exits. Pairing and file sharing run under the signed-in account. Enable the notification-area option in **Settings** to keep sharing after closing the window. Use **Exit HomeBridge** from the notification area to stop sharing. Signing out ends file sharing.


## Android and Meta Quest

Download HomeBridge-Android.apk from the newest Android preview on the [Releases page](https://github.com/cosmerearchives-creator/HomeBridge/releases). Android previews use CI debug signing. If a later APK reports a signing mismatch, uninstall the previous preview before sideloading. For Quest development installs, use adb install with the APK and open HomeBridge from Unknown Sources.

Find drives discovers servers advertising SMB through DNS-SD. Some Windows PCs and NAS devices do not advertise this service. Use Connect with a hostname or IPv4 address and, if needed, the share name. Browse folders and save files through the device's system file picker. The server account controls access. HomeBridge keeps credentials in memory for the current session. The explorer uses signed SMB 2/3 sessions. For folders shared through HomeBridge itself, tap HomeBridge PCs, select the Windows PC, and approve the matching code on both devices. Browse and save paired files, or upload selected files to a folder approved for uploads on the PC.
