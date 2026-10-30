# HomeBridge — native Windows rebuild

.NET 10 / WPF preview, **1.0.0-alpha.2**. Windows 11 x64 is the target.

| Module | Status |
| --- | --- |
| Bulk Rename | Native preview, apply, rollback on failure, in-session undo; files only |
| Application Launcher | Native local EXE launching; saved registrations and removal |
| Persistent settings | Atomic per-user JSON; DPAPI-protected identity |
| Devices | Opt-in IPv4 LAN discovery, invitation pairing, mutually authenticated status checks |
| Windows service | Optional LocalService worker; local read-only status pipe |
| Files, Screen, Roku DLNA, remote launch, Android integration | Pending; no endpoints provided |

Networking is off on every desktop launch. The optional service has no TCP/UDP listener and never runs desktop tools.

## Pair two Windows PCs

1. Open **Devices** on both PCs. Use a trusted private LAN. Enable networking on the inviting PC; enable on both to discover both.
2. On the inviting PC, choose the correct LAN address and **Create invitation**. Send the invitation privately to the other PC.
3. Paste it into the other PC's invitation box and choose **Pair using invitation**. Compare the full SHA-256 fingerprint with the inviting PC over a trusted channel, then confirm.
4. **Scan LAN**, select the paired device, and **Authenticate selected discovery**. Discovery alone grants no trust. On the inviting PC, **Refresh trusted devices** displays new peers.
5. **Forget selected device** removes local authorization immediately. Forget on both PCs for mutual removal. A new invitation is required to restore trust.

Invitations contain a random 256-bit, single-use secret, valid for five minutes and at most five attempts. Creating a new invitation replaces the previous one. Never post invitations publicly. The receiver of an invitation is authorized to pair: possession replaces a second confirmation on the inviting PC.

If discovery is blocked, pairing still works using an invitation containing the correct private IPv4 address. Windows Firewall may require a manually approved Private-profile rule for HomeBridge TCP/UDP 45871. The installer creates no firewall rules. Do not allow Public-profile access or forward the port from the Internet.

## Settings and recovery

`%LOCALAPPDATA%\HomeBridge\settings.json` stores launcher entries, device name and trusted certificate fingerprints. `identity.dpapi` stores the PFX encrypted using DPAPI CurrentUser. Directory permissions grant the current Windows user and SYSTEM. Writes use a flushed temporary file and atomic replacement. A per-user mutex prevents concurrent app instances across sessions. Shortcuts load on restart; EXEs launch only on an explicit local click.

Malformed settings, unsupported versions and unreadable identities stop startup without silently resetting trust. Back up state before repair. To intentionally reset, close HomeBridge, move the directory aside, restart, and re-pair all peers. DPAPI state is tied to the Windows account/machine. Uninstall preserves user settings. Identity rotation is manual in this preview (two-year certificate lifetime).

## Service architecture

The installer offers an unchecked **Install local status service** task. It registers `HomeBridge` for automatic start under **NT AUTHORITY\LocalService**, with a quoted executable in the protected default Program Files directory. Upgrade stops/waits before replacing files and restarts an existing service; uninstall stops/waits and deletes it. Desktop tools work without the service.

The worker only reports health on `HomeBridge.Status.v1`. Its pipe denies the Network SID and grants interactive users read-only access; LocalService, SYSTEM and administrators control the pipe. A first-instance flag avoids attaching to an existing pipe owner. The desktop uses anonymous impersonation level and treats status as informational, never as an authorization signal. The worker has no client commands, user-supplied paths, plugins, credentials, launch operations or impersonation.

Discovery, identity and pairing run in the interactive per-user agent, which stops when the desktop closes. Future always-on transfers need a separately reviewed command protocol and permission model; do not add generic execution to this worker.

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
- No independent security audit, automated certificate rotation, signed installer, automatic updates or Android companion implementation in this phase.
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
