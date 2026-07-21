# NextRDP roadmap

This roadmap describes intended product direction, not a delivery guarantee. Priorities may change as real-world testing, security review, and contributor feedback reveal better sequencing.

## Current: desktop MVP and shared-management preview

The current source includes:

- A working Windows 11 x64 desktop manager with local SQLite inventory and embedded Microsoft RDP sessions.
- Prompt-only credentials, safe `.rdp`/`.rdg` import, redacted audit/support data, diagnostics, tabs, full screen, display/redirection controls, themes, accessibility support, and dense inventory navigation.
- A self-hosted ASP.NET Core/PostgreSQL/React management preview with organization/workspace scoping, shared inventory, policy publication, desktop synchronization, administration, REST/OpenAPI, CLI/PowerShell automation, and encrypted backup/restore.

The [Phase 2 validation matrix](validation/phase2-completion.md) records which capabilities have local evidence and which still need real identity, domain, gateway, RemoteApp, monitor, or device infrastructure.

## Next priorities

- Complete external corporate OIDC validation with disposable non-production configuration.
- Complete real Connection Broker, RD Gateway, RemoteApp, Kerberos/domain-account, selected-monitor, redirected-device, and failure-recovery checks.
- Finish human screen-reader, High Contrast, DPI, clean-install, and Windows certification evidence.
- Harden deployment guidance, certificate custody/rotation, upgrade/rollback, observability, and supported-version policy.
- Establish production signing and a reproducible source-to-package release process before publishing binaries.

## Privileged-access security

- Multiple identity providers, SAML, SCIM, lifecycle reconciliation, custom roles, attribute-based policy, explicit deny, separation of duties, access simulation, and reviews.
- Isolated credential broker with envelope encryption and adapters for Windows Credential Manager/DPAPI, Azure Key Vault, CyberArk, and Windows LAPS.
- Just-in-time requests, approvals, expiry, emergency access, step-up authentication, credential leases, and complete audit coverage.
- Remote Credential Guard capability detection, Restricted Admin, smart cards, certificate authentication, and detailed RDP security policy.
- Active-session visibility, revocation, termination, notification, concurrency controls, and metadata-only monitoring.

## Gateway, integrations, and operations

- Outbound-only on-premises gateway/relay with mutual TLS, clustering, site routing, health, failover, trust zones, proxies, cached policy, and safe offline behavior.
- SIEM/webhook/syslog outputs, OpenTelemetry, alerting, incident integrations, and audit-integrity verification.
- Adapters for directory, cloud, virtualization, endpoint-management, CMDB, ticketing, collaboration, and security systems.
- High-availability services, retries, circuit breakers, load tests, automated backups, restore drills, and synthetic RDP testing.

## First supported enterprise release

- Production-signed MSIX and MSI, silent deployment, enterprise policy, update rings, rollback, offline packages, hashes, and deployment guidance.
- Self-hosted production readiness plus an optional multi-tenant operating model with strict isolation, regional deployment, retention, deletion, residency, and backup controls.
- Immutable audit storage, high availability, encrypted recovery, migration, SIEM export, and automation readiness.
- Secure development lifecycle evidence: threat models, SAST/DAST, dependency and secret scanning, fuzzing, SBOM, provenance, controlled builds, vulnerability disclosure, and patch policy.
- External assurance, support, contractual, training, insurance, and escrow work tracked as non-code deliverables rather than source features.

## Advanced capabilities

- Encrypted session recording with retention, legal hold, playback, watermarking, customer storage, and customer-managed keys.
- Azure Virtual Desktop and Windows 365 feeds, advanced transport support, air-gapped deployment, and complete PAM rotation.
- SSH, VNC, and WinRM protocol adapters that preserve protocol-specific security boundaries.
- Separately reviewed assisted search and troubleshooting with explicit data controls, evaluations, and cost/security limits.
- User-behavior analytics, Terraform, advanced ticket approvals, SDKs, and a signed/sandboxed plugin model.

See the [enterprise feature baseline](reference/enterprise-feature-baseline.md) and [traceable feature catalog](reference/feature-catalog.csv) for the complete requirement inventory.
