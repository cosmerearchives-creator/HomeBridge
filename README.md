# HomeBridge — native Windows rebuild

.NET 10 / WPF preview, **1.0.0-alpha.5**. Windows 11 x64 is the target.

| Module | Status |
| --- | --- |
| Bulk Rename | Native preview, apply, rollback on failure, in-session undo; files only |
| Application Launcher | Native local EXE launching; saved registrations and removal |
| Persistent settings | Atomic per-user JSON; DPAPI-protected identity |
| Devices | Opt-in IPv4 LAN discovery, invitation pairing, mutually authenticated status checks |
| Windows service | Optional LocalService worker; local read-only status pipe |
| Updates | Automatic startup checks; verified in-app installer download and user-approved upgrade |
| Screen | Paired view-only primary-display sharing with local approval and a Stop sharing indicator |
| Files, Roku DLNA, remote launch, Android integration | Pending; no endpoints provided |

Networking is off on every desktop launch. The optional service has no TCP/UDP listener and never runs desktop tools.

## Pair two Windows PCs

1. Install alpha.5 on both PCs, open **Devices**, and use the same trusted private network.
2. On the receiving PC, click **Allow pairing for 2 minutes**. This also turns on nearby devices.
3. On the other PC, click **Search nearby PCs**, select the receiving PC, then **Pair selected PC**.
4. Compare the eight-digit code shown on BOTH screens. Only choose **Codes match — pair** on each PC if the numbers match exactly and you intended to connect. Cancel mismatches or unexpected requests. Trust is saved only after both approvals; paired devices refresh automatically.
5. Open **Screen** to share. Newly paired PCs remember their last LAN address and fill it in automatically when selected; you can correct it after network/DHCP changes. The address is a convenience hint, never an authentication credential.

Nearby pairing is closed by default, closes after success, accepts one incoming request at a time, and permits at most five attempts per two-minute window. **Stop accepting pairing**, closing the app, disabling networking or timeout cancels pending confirmation. Codes are not passwords and are not typed or transmitted as authorization tokens. Fresh random nonces are committed before either is revealed; the displayed comparison binds both nonces and the server/client certificate fingerprints. Both humans must compare the actual two screens. This preview protocol has not had an independent security audit; it is not a Bluetooth/ZRTP implementation.

**Advanced: pair with an invitation instead** retains the older invitation workflow for manual connections or older native builds. Create an invitation, send it privately, paste it on the other PC, and verify the full fingerprint before confirming. The nearby code flow requires alpha.5 on both PCs. **Forget selected device** removes local trust immediately; forget on both PCs for mutual removal, then pair again to restore it.

Invitations contain a random 256-bit, single-use secret, valid for five minutes and at most five attempts. Creating a new invitation replaces the previous one. Never post invitations publicly. The receiver of an invitation is authorized to pair: possession replaces a second confirmation on the inviting PC.

If discovery is blocked, pairing still works using an invitation containing the correct private IPv4 address. Windows Firewall may require a manually approved Private-profile rule for HomeBridge TCP/UDP 45871. The installer creates no firewall rules. Do not allow Public-profile access or forward the port from the Internet.

## Settings and recovery

`%LOCALAPPDATA%\HomeBridge\settings.json` stores launcher entries, device name and trusted certificate fingerprints. `identity.dpapi` stores the PFX encrypted using DPAPI CurrentUser. Directory permissions grant the current Windows user and SYSTEM. Writes use a flushed temporary file and atomic replacement. A per-user mutex prevents concurrent app instances across sessions. Shortcuts load on restart; EXEs launch only on an explicit local click.

Malformed settings, unsupported versions and unreadable identities stop startup without silently resetting trust. Back up state before repair. To intentionally reset, close HomeBridge, move the directory aside, restart, and re-pair all peers. DPAPI state is tied to the Windows account/machine. Uninstall preserves user settings. Identity rotation is manual in this preview (two-year certificate lifetime).

## Service architecture

The installer offers an unchecked **Install local status service** task. It registers `HomeBridge` for automatic start under **NT AUTHORITY\LocalService**, with a quoted executable in the protected default Program Files directory. Upgrade stops/waits before replacing files and restarts an existing service; uninstall stops/waits and deletes it. Desktop tools work without the service.

The worker only reports health on `HomeBridge.Status.v1`. Its pipe denies the Network SID and grants interactive users read-only access; LocalService, SYSTEM and administrators control the pipe. A first-instance flag avoids attaching to an existing pipe owner. The desktop uses anonymous impersonation level and treats status as informational, never as an authorization signal. The worker has no client commands, user-supplied paths, plugins, credentials, launch operations or impersonation.

Discovery, identity and pairing run in the interactive per-user agent, which stops when the desktop closes. Future always-on transfers need a separately reviewed command protocol and permission model; do not add generic execution to this worker.

## Screen sharing (alpha.4)

Install alpha.4 on both PCs and pair them in **Devices**. Enable networking on the sharing PC. In **Screen**, refresh paired devices, select the viewing PC, and click **Approve sharing to selected PC**. Confirm its name/fingerprint and close private windows and notifications. The red, topmost sharing window must be shown before access is granted; closing it or pressing **STOP SHARING** cancels approval and the active stream.

On the viewing PC, select the sharing PC in **Screen**, enter its private LAN IPv4 address, and click **View screen**. This is view-only: no keyboard/mouse input, audio, recording, file access or remote application launching is provided. **Stop viewing** disconnects and clears the displayed image. Both applications must remain running in interactive Windows sessions.

Each local approval is in-memory, names one exact paired certificate, expires after five minutes without a connection, and permits one connection for at most 30 minutes. Disconnecting consumes it; reconnecting requires fresh approval. Both peers authenticate with their existing pinned certificates over TLS 1.2/1.3 on TCP 45871. Unpaired or unapproved clients cannot trigger capture. Trust is checked before each capture and before sending each frame. Stop sharing, disabling networking, forgetting a device, closing the app, session lock/logoff or console/remote disconnect cancels sharing. Stopping cannot recall a frame already in flight; viewers can independently save or photograph received images.

The preview uses GDI capture and baseline JPEG frames (quality 65), proportionally downscaled within 1280×720, at a maximum of five frames/second. It captures the primary display, including visible notifications and the sharing indicator; no region/window selection, secondary-monitor selection, cursor overlay, hardware video encoding, adaptive bitrate, HDR support or secure/UAC desktop capture is implemented. Protected/accelerated content may appear black; secure/locked desktop and real multi-monitor/DPI behavior require manual validation. This is intended for basic desktop viewing rather than smooth video/gaming.

Frames have a 2 MiB byte limit and bounded JPEG dimensions checked before WPF decoding. One frame is requested at a time; there is no accumulating frame queue. Five-second I/O deadlines stop stalled peers. The local Windows service does not capture screens or expose any new commands. No recording, stored screenshots, automatic approval, persistent sharing permission, or Internet relay is added.

Tests cover consent expiry/single-use/concurrent access, real WPF JPEG encoding and frame validation, mutual-TLS streaming, no capture before approval, reconnect refusal, local stop and trust revocation. Live primary-screen capture, the persistent stop window, lock handling, high-DPI/multiple-monitor behavior and a real two-PC session still need manual checks before broad distribution.

## Authentication and limits

TLS 1.2/1.3 protects all pairing/status traffic. The inviter presents a certificate pinned by the invitation **before** the client sends its secret. Every connection requires a client certificate and proof of its private key. Unknown client certificates may reach the pairing operation only; they need a live invitation to gain trust. Status requests require the saved fingerprint. Sessions are not resumed, so revocation is checked on every connection. Certificates are self-signed: explicit pins replace public CA validation, with validity dates checked.

Discovery advertisements are unauthenticated hints, contain no tokens, use a fresh query nonce, are bounded, and are rate limited. They can be spoofed or suppressed. TLS uses eight concurrent connection slots, eight-second deadlines, one request per connection, and 4 KiB frames. Failed pairing attempts never install trust. If one side loses the final reply or cannot save, trust may be asymmetric; forget both sides and re-pair.

Limitations:

- Windows-to-Windows preview protocol only; no legacy Python/Android/Roku interoperability.
- IPv4 private/link-local networks and loopback only. No IPv6 LAN, WAN, relays, NAT traversal or DNS discovery. Broadcast can miss additional adapters, VLANs, VPNs or Wi-Fi client isolation; use direct invitations.
- One listening desktop per machine (port 45871); multiple Windows users cannot listen simultaneously. Networking must be enabled after each restart. No headless pairing or transfer support.
- Binding is on all IPv4 interfaces; peer addresses are filtered. A private source address does not prove a trusted network. Restrict firewall access to Private profiles and trusted interfaces.
- Same-user malware and administrators are outside the threat model. DPAPI protects stored key material, not a compromised logged-in account. Schannel uses a temporary per-user key container while the certificate is loaded; it is not hardware-backed.
- Invitations are bearer secrets. Anyone who obtains a live invitation can pair. No six-digit codes, remote execution, file sharing, remote input, password authentication or automatic firewall rules exist.
- No independent security audit, automated certificate rotation, signed installer, automatic rollback or Android companion implementation in this phase.
- Automated tests cover loopback TLS and disposable-runner service lifecycle. Real two-PC firewall, adapter and interactive UI behavior still require manual checks.

## Build and installer

Install the .NET 10 SDK and Windows Desktop workload, then open `HomeBridge.slnx` or run:

```powershell
dotnet build HomeBridge.slnx -c Release
dotnet test tests/HomeBridge.Tests/HomeBridge.Tests.csproj -c Release
dotnet run --project src/HomeBridge.Desktop/HomeBridge.Desktop.csproj
```

GitHub Actions **Windows installer** builds/tests on Windows, publishes self-contained Win-x64 desktop/service binaries, packages with Inno Setup, and checks service installation, account, quoted path, status, upgrade and uninstall on its disposable runner. Download `HomeBridge-Windows-Installer` from a successful run; `HomeBridge-Test-Results` contains test results. Do not run `tests/Service-Smoke.ps1` on a PC with an existing HomeBridge installation: it installs, upgrades and removes the program.

Before a release, manually verify two-PC discovery/pair/authentication/revocation, firewall refusal, restart persistence, local EXE launch, rename preview/apply/undo, optional-service installation and upgrade/uninstall on Windows 11. Only launch EXEs you trust. Never commit tokens, private keys or user state.

## Automatic updates

Install alpha.3 once manually to obtain the updater; older builds cannot update themselves. In **Settings**, automatic checks on startup and preview releases are enabled by default and can be disabled separately. **Check for updates** works on demand. Checks contact the fixed HomeBridge GitHub Releases API over HTTPS, independently of LAN networking; settings, device credentials and files are never uploaded. Offline/rate-limit failures leave the installed app alone and appear in Settings.

When a newer release is available, the app notifies you. **Download and install update** downloads the installer, verifies its exact size and GitHub-provided SHA-256 digest, and starts the normal installer. Installation requires your confirmation and the Windows administrator approval prompt. The app closes, so in-session rename undo is lost; saved settings, launcher entries and paired identities remain. Reopen HomeBridge from the installer or Start menu after upgrading. Downloads and installations do not happen silently in the background.

Only the exact `HomeBridge-Setup.exe` asset from `cosmerearchives-creator/HomeBridge` releases is accepted. Redirects are restricted to GitHub and its release-asset host over HTTPS. Missing digests, altered/incomplete/oversized downloads, untrusted redirects, same versions and older versions are rejected. Candidate selection uses semantic versions across the latest 20 release records, with drafts excluded and preview filtering respected. Update checks have a 15-second deadline; downloads are limited to 300 MiB and ten minutes. Verified files remain locked against modification through installer launch. Failed downloads are deleted; successful installers are retained under `%LOCALAPPDATA%\HomeBridge\Updates` and may be removed when no installation is running.

The update trust root is GitHub HTTPS plus control of this repository and its release assets. SHA-256 protects against download corruption/substitution relative to the API digest; it does not provide an independent publisher signature if the GitHub account/repository is compromised. Installers are currently unsigned, so Windows may display an unknown-publisher warning. There is no automatic rollback, silent elevation, certificate-based publisher signing, or privilege-bearing update endpoint in the service. Manual older-installer installation is outside the updater's downgrade checks.

### Publishing updates

After merging and reviewing a version, keep the desktop project Version and installer MyAppVersion equal, commit the changes, then push a matching `v` tag (for example `v1.0.0-alpha.3`). The Windows workflow validates the tag/version, runs all tests and service lifecycle checks, creates the installer, and only then publishes it through a release job with repository write permission. Tags containing a hyphen produce prereleases. Normal branch and PR builds only upload build artifacts and do not publish updates. Existing releases are not overwritten by reruns; resolve a failed or incomplete release explicitly.

No release existed when this updater was added. Until a matching tagged release is published, checks correctly report no newer release. The updater's network policy and verification are tested with controlled HTTP responses; a real published-release download and interactive UAC cancellation/upgrade should also be tested before broad distribution.

Upgrading the initial discovery preview migrates its saved DisplayName. A recognized HomeBridgeDiscovery service at the protected application path is stopped and removed, and replaced with the LocalService status worker. Its old UDP 45679 protocol is retired; use the new opt-in discovery and invitations. Remove obsolete firewall rules for that old port manually if you created them.
