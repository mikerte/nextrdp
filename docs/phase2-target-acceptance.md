# Phase 2 Target Acceptance

This checklist closes the 28 Phase 2 requirements whose implementation and automated evidence pass but whose final behavior depends on an external provider, Windows/RDP target, monitor topology, or redirected device. It supplements the local Phase 2 check and must not contain credentials.

Current checkpoint (2026-07-20): direct authenticated RDP, embedded rendering, 2560x1440 full screen, pinned connection bar, return to the same tab, quality telemetry, local managed OIDC sign-in, scoped synchronization, authentication-time thumbnail suppression, secret-free managed-profile creation/launch, and separate-window presentation have passed. Live observation confirmed that a managed administrative session connected, all-monitor mode preserved both physical display surfaces, restoring the window returned the same live session, and the hidden authentication preview changed to a live in-memory thumbnail only after authentication. A managed session also opened in a resizable separate window, maximized/restored correctly, reattached to the same tab, and allowed clean client shutdown while connected. Four checks pass, six are environment-blocked, and eighteen remain `not-run`; five redirected-device checks lack trustworthy disposable device endpoints, while simultaneous live multi-window validation is blocked by the available target/account's single-session limit. No catalog row is promoted until a complete passing record exists.

## Evidence rules

- Use only non-production targets and credentials supplied outside source control, scripts, command lines, screenshots, logs, and project memory.
- Record the date, tester, client OS/build, sanitized target type, result, and artifact path. Do not record hostnames, IP addresses, usernames, tokens, tenant identifiers, or secrets.
- A result may be `pass`, `fail`, `blocked`, or `not-applicable` only where the requirement explicitly allows unavailable hardware.
- Failed checks remain open with the exact redacted error category and reproduction steps.
- Corporate-provider configuration requires an isolated non-production tenant/application and a separate security review. Test success does not establish production readiness.

## Evidence recording workflow

1. Copy `samples/phase2-acceptance-results.template.json` to a location outside the repository.
2. Change only each controlled `result` (`pass`, `fail`, `blocked`, or `not-run`) and the two boolean acknowledgements. The recorder rejects additional properties and free-form notes.
3. Keep raw redacted evidence outside the project tree. Use one or more JSON, XML, PDF, or PNG artifacts for each group that passes.
4. Run the recorder with the applicable artifact paths:

```powershell
.\scripts\record-phase2-acceptance.ps1 `
  -ResultsPath C:\Phase2Evidence\phase2-results.json `
  -CorporateIdentityEvidencePath C:\Phase2Evidence\corporate-identity.pdf `
  -ManagedRdpEvidencePath C:\Phase2Evidence\managed-rdp.pdf `
  -DomainAccountEvidencePath C:\Phase2Evidence\domain-account.pdf `
  -WindowsSessionEvidencePath C:\Phase2Evidence\windows-session.pdf `
  -RedirectedDeviceEvidencePath C:\Phase2Evidence\redirected-device.pdf
```

The recorder requires the exact 28 catalog IDs, binds the record to the current signed MSIX and a deterministic first-party source fingerprint, validates the Windows 11 x64 client boundary, requires two monitors for multi-monitor pass claims, and stores only controlled results plus artifact hashes/format/size. Use `-AllowIncomplete` only to create an explicitly incomplete checkpoint; it never produces a pass claim.

## Corporate identity provider

Catalog IDs: NX-L0152, NX-L0167, NX-L0168, NX-L0197.

1. Create a disposable corporate OIDC test application and configure issuer, client, callback, scopes, and claim mapping outside source control.
2. Sign in through authorization code plus PKCE; verify issuer, audience, state, nonce, and redirect validation fail closed when tampered.
3. Confirm the provider performs its own MFA and NextRDP records only the validated authentication context, never an MFA secret or product password.
4. Map a test provider group to each allowed scoped Phase 2 role; verify a removed or unknown group is denied and global logout/revocation removes access.

Required evidence: redacted protocol/role test report and authorization decision IDs; no provider tokens or claim payloads.

## Broker, RemoteApp, and administrative sessions

Catalog IDs: NX-L0015, NX-L0019, NX-L0020, NX-L0021, NX-L0083.

1. Use a disposable non-production Connection Broker/RD Gateway collection with a published RemoteApp.
2. Launch a brokered desktop and confirm the target route succeeds through the managed profile without a stored or synchronized password.
3. Launch the published RemoteApp with a known harmless program, working directory, and argument; confirm invalid values fail visibly.
4. Launch an administrative session to a permitted Windows Server target and confirm clean disconnect/audit behavior.

Required evidence: redacted launch matrix covering broker, RemoteApp, command line, and administrative mode.

## Kerberos and account-state handling

Catalog IDs: NX-L0032, NX-L0043, NX-L0044, NX-L0045.

1. On a disposable domain-joined client/target, connect through the Windows credential prompt and confirm Kerberos/CredSSP behavior without password persistence.
2. Use disposable directory accounts to force expired-password, required-password-change, locked-account, and disabled-account states.
3. Confirm each state produces its distinct redacted action/error category and does not expose sensitive detail in audit or support output.

Required evidence: redacted authentication-state matrix; reset or remove all disposable test accounts afterward.

## Windows presentation and live-session behavior

Catalog IDs: NX-L0052, NX-L0054, NX-L0055, NX-L0064, NX-L0066, NX-L0067, NX-L0068, NX-L0069, NX-L0070, NX-L0930.

1. Open a session in a separate window, move and resize it, detach/reattach it, and close each window independently without creating duplicate RDP hosts.
2. On a client with at least two monitors, validate all-monitor and selected-monitor modes, DPI/resolution, full-screen exit, and cleanup.
3. Enable thumbnails for an authenticated connected desktop and verify they stop during prompts/disconnect and are absent when user or locked privacy policy disables them.
4. Validate configured shortcut routing, Windows-key combinations, and the supported Secure Attention Sequence action in embedded, full-screen, and separate-window modes.
5. Restart with a saved workspace and confirm only disconnected descriptors reopen; reconnect still requires explicit user action and a fresh Windows credential flow.

Required evidence: redacted window/monitor/keyboard/privacy matrix plus screenshots that contain no credentials or identifying target data.

## Redirected-device hardware

Catalog IDs: NX-L0073, NX-L0074, NX-L0076, NX-L0077, NX-L0078.

1. With central policy enabled, validate microphone, camera, COM/LPT, supported USB/Plug and Play, and smart-card redirection using disposable test devices/data.
2. Disable or lock each setting centrally and confirm the redirected resource is unavailable on a newly created session.
3. Confirm unsupported control/device combinations fail visibly and that diagnostics contain no device serial number, certificate material, or credential data.

Required evidence: one redacted enabled/disabled result per capability; unsupported hardware may remain `blocked`, not silently passed.

## Completion decision

After all applicable checks pass, generate a passing target-acceptance record, update the 28 catalog rows from `implemented` to `verified`, attach the redacted record path to the Phase 2 completion matrix, and rerun `scripts/check-phase2.ps1 -Configuration Release -Live`. Complete a separate security and production-readiness review before publishing a supported release.
