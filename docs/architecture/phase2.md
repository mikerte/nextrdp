# ADR: Phase 2 Shared-Management Architecture

## Status

Accepted architecture for the implemented shared-management preview. External identity-provider validation, production deployment, security review, and a supported release remain future work.

## Context

Phase 1 is a local WPF/SQLite RDP manager. Phase 2 adds shared inventory, scoped administration, OIDC, policy publication, desktop synchronization, automation, and recovery without turning the desktop into the authority or introducing stored RDP credentials. The self-hosted deployment is intentionally compact and can evolve into later privileged-access, gateway, and SaaS phases.

## Decision

Use a .NET 10 modular monolith with explicit boundaries:

- `NextRdp.Platform.Domain`: organization, workspace, membership, inventory, configuration, policy, audit, synchronization, service-account, and outbox invariants.
- `NextRdp.Platform.Application`: use cases, commands/queries, validation, authorization requirements, import/export, publication, backup, and synchronization orchestration.
- `NextRdp.Platform.Infrastructure`: EF Core/Npgsql, PostgreSQL migrations, OpenIddict stores, OpenFGA and OPA clients, outbox dispatch, encryption, OpenTelemetry, and health checks.
- `NextRdp.ControlPlane`: ASP.NET Core host, versioned REST/OpenAPI, browser BFF endpoints, OIDC federation/product-token flows, rate limiting, problem details, static administration assets, and operational endpoints.
- `NextRdp.Admin`: React 19, TypeScript, Vite, Fluent UI React v9, TanStack Query, route-level authorization, and accessible interaction states.
- `NextRdp.Cli` and `NextRdp.PowerShell`: generated/typed clients over the public API contract, not database access.
- Existing `NextRdp.Core`, `NextRdp.Infrastructure`, `NextRdp.RdpHost`, and `NextRdp.Desktop`: retain local contracts and add central references, sync state, effective policy, workspaces, and expanded RDP settings.

The host deploys as one control-plane unit in Phase 2. PostgreSQL is authoritative shared state. A transactional outbox records durable side effects. NATS JetStream, Valkey, gRPC, and service extraction stay behind interfaces and are introduced only when later phase or measured reliability evidence requires them.

## Trust And Authorization Boundaries

- Browser and desktop clients are untrusted. Resource ownership, organization/workspace scope, role, revision, and effective policy are resolved server-side.
- Corporate authentication is external OIDC. The platform brokers product sessions/tokens with OpenIddict and never offers a production local-password fallback.
- Interactive web uses authorization code plus PKCE and a same-site secure BFF cookie. Desktop uses system-browser authorization code plus PKCE with loopback callback. Automation uses scoped client credentials.
- OpenFGA is the relationship decision point for organization/workspace/folder/connection access. OPA evaluates attribute/policy rules. Missing, unavailable, or indeterminate authorization dependencies deny protected operations.
- Fixed Phase 2 roles are platform administrator, organization administrator, workspace administrator, operator, and read-only auditor. Custom roles and ABAC authoring remain Phase 3.
- RDP passwords, imported password blobs, bearer tokens, client secrets, and encryption keys are excluded from inventory, audit details, synchronization, normal export, and support contracts.
- Service-account secrets are generated with a cryptographic RNG, shown once, stored only through OpenIddict's protected representation, rotated/revoked explicitly, and never logged.

## Data And Consistency

- UUID identifiers are server generated. Mutable resources carry monotonically increasing revisions and strong ETags.
- Every shared table contains an organization identifier; workspace-owned tables also contain a workspace identifier. Repository methods require a resolved scope object, and database indexes/constraints reinforce uniqueness inside that scope.
- Configuration layers are organization, workspace, folder, then connection. Each field carries value, source layer, lock state, and conflict information in effective responses.
- Drafts are mutable. Published configuration versions are immutable. Rollback creates a new version referencing the prior version; it never edits history.
- Audit events are append-only application records with actor alias, action, scoped resource, outcome, correlation ID, and redacted metadata. Sensitive target and identity details are minimized.
- Synchronization uses an initial snapshot plus ordered change feed. A client supplies its last cursor and known revisions; the server returns additions/updates/tombstones, effective policy revision, minimum-client result, and next cursor.
- Centrally sourced offline cache is read-only. Revocation produces a tombstone and local authorization denial. Local-only Phase 1 profiles remain separate and usable during an outage.

## API Contract

- Public routes use `/api/v1`, JSON, OpenAPI 3.1, RFC 9457 problem details, UTC timestamps, opaque continuation tokens, bounded pagination, and idempotency keys for mutation retries.
- Bulk operations have fixed item/size limits and return an outcome per item; an all-or-nothing option is explicit rather than implicit.
- Conditional mutation requires `If-Match` for revisioned resources. Conflicts return the current revision and safe comparison metadata.
- Normal exports are versioned and secret-free by construction. Administrative backups are a distinct privileged operation using authenticated encryption and a versioned manifest.
- Health separates liveness from readiness. Readiness checks PostgreSQL and required authorization/policy dependencies; optional telemetry exporters do not determine readiness.

## Self-Hosted Deployment

Docker Compose defines PostgreSQL 18, OpenFGA, OPA, control plane/admin, and persistent named volumes. Secrets enter through environment/file mounts and checked-in examples contain placeholders only. Services run as non-root where supported, expose only required ports, include health checks, and use a private network for data and policy services. Development TLS and local identity fixtures are for test evidence only and cannot be enabled under a production environment name. Outside Development, the control plane also fails closed unless the persisted Data Protection key ring is encrypted with a currently valid RSA certificate. Rotation configures the new protection certificate while explicitly retaining prior decryption certificates until old keys expire.

## Failure Handling

- PostgreSQL unavailable: readiness fails and mutations stop; no in-memory authoritative fallback.
- OpenFGA or OPA unavailable/indeterminate: protected operation is denied with a correlation ID; no permissive fallback.
- OIDC discovery/key failure: new authentication fails; already issued product sessions follow their bounded lifetime and revocation rules.
- Revision conflict: no last-write-wins overwrite; client sees comparison and refresh/merge choices.
- Desktop offline: shared cache is visibly stale/read-only; mutations and unverified managed launches are blocked; local-only profiles remain separate.
- Outbox delivery failure: transaction remains committed, retries are bounded/backed off, and operational health exposes backlog without duplicating idempotent effects.
- Backup/decryption/version failure: restore aborts before mutation and reports a redacted diagnostic.

## Rejected Alternatives

- Microservices in Phase 2: too many deployment and distributed-consistency surfaces before there is scale evidence.
- Central state in SQLite: conflicts with shared multi-user authority and the selected PostgreSQL architecture.
- Desktop-trusted authorization: permits tampering and stale-access errors.
- Product-managed corporate passwords: conflicts with delegated OIDC/MFA and expands credential risk.
- A fake-only OpenFGA/OPA implementation: deterministic adapters are acceptable in unit tests, but completion requires real service integration evidence.

## Validation

- Project-native checks: release build, .NET tests, frontend lint/typecheck/tests/build, catalog/matrix checks, secret and dependency scans.
- Integration checks: PostgreSQL migrations/constraints/outbox, OpenIddict flows, OpenFGA relationships, OPA decisions, API contracts, Compose health, certificate-encrypted Data Protection persistence/restart/rotation, backup/restore, synchronization and revocation.
- Manual checks: responsive/keyboard/screen-reader admin workflow, desktop sign-in/sync/offline/conflict, live Gateway/RemoteApp/multimonitor paths, and the nine-step Phase 2 acceptance flow.
- Evidence location: `docs/validation/phase2-completion.md`, `docs/testing/phase2.md`, test results, and redacted acceptance artifacts.
