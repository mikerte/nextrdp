# NextRDP Phase 1 and Phase 2 Threat Model

## Phase 1 local client

## Assets and trust boundaries

- Assets: connection inventory, username hints, environment labels, local audit metadata, RDP session availability, and imported files.
- Outside the product boundary: passwords entered into the Windows RDP prompt, test-host credentials, operating-system certificate decisions, and remote-host security.
- Trust boundaries: untrusted import file to parser; WPF process to RDP COM control; local process to SQLite; local network diagnostics to DNS/TCP targets.

## Primary threats and controls

| Threat | Control | Verification |
|---|---|---|
| Credential persistence | No password field in profile contracts; credential saving disabled; secret import fields discarded | Reflection/domain tests and import tests |
| Malicious `.rdp`/`.rdg` input | 2 MB limit, extension and declared-type checks, UTF-8/UTF-16 handling, strict RDP field types, required RDCMan root, strict bracketed IPv6 parsing, DTD/XML-resolver prohibition, depth/element/server bounds, validation, and review-only preview | Boundary and semantic tests, generated malformed RDG corpus, and secret-discard regression coverage |
| Sensitive logs | Metadata-only audit events omit targets, addresses, filenames, exception text, usernames, and credentials; marker-based redaction protects generated text | Audit/support-package and redactor tests |
| Sensitive, stale, or fabricated acceptance evidence | Fixed result IDs and enumerated outcomes only; recorder has no target, account, credential, machine-name, screenshot, or free-form-note field; failed reruns invalidate prior fixed evidence; installed-workflow timestamps/duration are cross-checked; clean install/uninstall and WACK pass claims require version-2 machine artifacts bound to Windows 11 client-workstation identity, the current package hash, and signer | Acceptance-schema and platform-predicate validation, timestamp/evidence hash/signature checks, and credential scan |
| Wrong production target | Environment marker and explicit production warning | UI/manual test |
| RDP control downgrade/unavailability | Probe installed controls newest-to-oldest and accept only candidates that expose the required client, advanced-settings, and non-scriptable prompt-only credential interfaces; otherwise fail with an actionable diagnostic | STA COM activation and capability tests |
| Network information leakage | Explicit user-invoked DNS/TCP diagnostics only; no credential data | Code review/manual test |
| Local database corruption, audit tampering, or unsafe schema upgrade | SQLite transactions, compiled migrations, pre-migration backup, expected-schema validation, integrity check, database triggers rejecting audit updates/deletes, ordered session-state writes, and fail-closed partial-schema behavior; prior live sessions are not claimed as reattached | Fresh/legacy/partial migration, append-only audit, ordered-state, persistence, and restart tests |
| Package tampering or disclosure | SHA-256 manifests, test signing, clean publish directory, no debug symbols, and payload scan for local data/secret-like filenames; production signing remains future work | Signature/hash and package-content verification |

## Residual risks

- The Windows RDP client and remote host remain security dependencies.
- Local users with access to the Windows account can read non-secret inventory metadata.
- A real non-production RDP host is still required to validate authentication, redirection, reconnect, certificate warnings, and clean disconnect end to end.
- The test certificate is not a production trust anchor. A supported release requires Authenticode signing and external security/release review.

## Phase 2 shared-management platform

### Assets and trust boundaries

- Assets: organization/workspace membership, shared and personal connection metadata, published RDP policy, configuration history, group-role mappings, product sessions/tokens, service-account credentials, synchronization cursors/cache, audit metadata, encrypted administrative backups, and Data Protection/OpenID signing keys.
- Outside the product boundary: corporate identity-provider credentials and MFA factors, RDP passwords entered in Windows, remote-host credentials and certificates, customer TLS termination, certificate/private-key custody, and raw target-acceptance artifacts.
- Trust boundaries: browser/desktop/automation client to control plane; corporate IdP to OIDC middleware; control plane to PostgreSQL; control plane to OpenFGA and OPA; control plane to persistent key storage; administrator to import/export/backup; control plane to the Windows RDP client; and external evidence to the redacted acceptance recorder.

### Primary threats and controls

| Threat | Control | Verification |
|---|---|---|
| Cross-organization or cross-workspace access | Server-resolved scope, scoped repository inputs, OpenFGA relationships, OPA attributes, fixed roles, and deny when either decision service is unavailable or indeterminate | Authorization suites and live unauthenticated, cross-workspace, cross-organization, scope-denial, and dependency-failure checks |
| Corporate identity spoofing or protocol downgrade | Production requires an absolute HTTPS authority and client credentials; code flow with PKCE; issuer, audience, signature, lifetime, nonce, and state validation; no product password fallback | Control-plane registration tests plus real-provider acceptance |
| MFA bypass | Production cannot disable required provider MFA evidence; only configured claim type/value pairs satisfy the policy; a missing claim rejects sign-in before user provisioning | Corporate MFA policy and provisioner tests plus provider claim-shape acceptance |
| Role escalation through IdP claims | Raw corporate role claims are ignored; only validated static or persisted group mappings create fixed Phase 2 roles; unmapped identities receive no default role | Unmapped-role and mapped-group tests, mapping lifecycle tests, OpenFGA tuple checks, and provider acceptance |
| Session or token theft and stale access | HttpOnly/Secure production cookies, SameSite/antiforgery protection, bounded idle/absolute/token lifetimes, no saved federation tokens, session-version revocation, global logout, and refresh-token revalidation | Session-boundary tests and live global logout/old-token rejection |
| Service-account credential disclosure or overreach | Cryptographic one-time secrets, protected OpenIddict storage, expiry, scope checks, rotation/revocation, stable redacted errors, and no secret echo in CLI/PowerShell output | Unit/live rotation, old-secret rejection, scope-denial, automation-output, and credential-scan checks |
| Stale or resurrected synchronized access | Server-authoritative revisions and cursors, bounded multi-page drain, read-only offline shared cache, tombstones, same-batch stale-upsert suppression, and managed-launch denial while authority is unavailable | Managed-client paging/outage/restart/revocation tests and live sync |
| Persisted cryptographic key disclosure | Development key volume is local-only; every non-Development start requires a currently valid RSA certificate to encrypt the Data Protection key ring; prior decryption certificates are explicit for rotation | Missing/malformed/non-RSA fail-closed tests, encrypted XML inspection, restart decryption, and rotation test |
| Import, export, or backup data leakage | Bounded preview/commit, secret-field stripping, structurally secret-free normal export, separately privileged AES-GCM/PBKDF2 administrative backup, MFA requirement, and tamper/wrong-key rejection | Import boundary tests, export contract scan, backup cryptographic tests, and clean restore drill |
| Audit or acceptance-evidence fabrication | Redacted append-oriented audit events; fixed acceptance IDs/results; exact 28-ID/five-group validation; package/source/platform/monitor binding; raw evidence kept outside the project and represented only by hashes | Audit tests, target-recorder canonical/negative/partial-record tests, and project credential scan |
| Dependency outage becomes permissive | Readiness includes PostgreSQL, OpenFGA, and OPA; authoritative mutations stop when PostgreSQL is unavailable; protected operations deny when policy dependencies fail | Compose health plus deterministic/live failure-path checks |
| Network or gateway diagnostics expose credentials | Diagnostics are explicit, bounded, credential-free, return redacted categories, and never receive an RDP password | Gateway diagnostic tests and live bounded failure path |

### Phase 2 residual risks

- Real corporate-provider issuer/claim/logout behavior remains unverified until the target recorder passes against a disposable provider application.
- Broker, RD Gateway, RemoteApp, domain account states, multi-monitor/window behavior, thumbnails/keyboard routing, and redirected-device behavior still require suitable non-production infrastructure or hardware in `docs/phase2-target-acceptance.md`.
- Local host administrators and certificate custodians remain trusted. Production certificate issuance, storage, rotation ownership, recovery, and revocation require a documented operational procedure.
- The Development Compose deployment uses local HTTP and a development identity; it is deliberately prevented from representing a production configuration.
- Phase 2 audit records are application-enforced and redacted but are not the immutable external audit storage promised for Phase 5.
- Shared connection and operational metadata can still be sensitive even without passwords. Deployment access, database backups, support procedures, and retention require customer controls.
- Passing tests does not by itself close residual security risk or establish production readiness.
