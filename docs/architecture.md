# NextRDP architecture

## Local desktop boundaries

The WPF shell calls application-facing interfaces from `NextRdp.Core`. `NextRdp.Infrastructure` owns SQLite, imports, diagnostics, local policy, and audit redaction. `NextRdp.RdpHost` isolates Windows Forms and Microsoft RDP ActiveX interoperability from the WPF UI.

Connection launch follows this path:

1. Validate the `ConnectionProfile` and effective policy.
2. Write a redacted connection-attempt audit event.
3. Create an isolated Windows Forms RDP host and probe installed controls from newest to oldest, accepting a candidate only when the required client, advanced-settings, and prompt-only credential interfaces are available.
4. Apply port, NLA, prompt-only credentials, sizing, display, redirection, audio, and bounded reconnect settings.
5. Map COM events into typed session states and redacted diagnostics through an ordered asynchronous queue.
6. Present status, environment classification, and full-screen controls above the airspace-hosted ActiveX surface.
7. Disconnect, drain queued state transitions, and dispose the host when its tab or the application closes.

SQLite persists profiles, folders, settings, tags, and metadata-only audit events at `%LOCALAPPDATA%\NextRDP\nextrdp.db`. Compiled EF Core migrations govern schema changes. Migration startup validates expected tables and columns, creates a one-time adjacent backup when baselining the earlier schema, applies pending changes, and runs `PRAGMA quick_check`. Database triggers reject audit-event updates and deletions. Partial or corrupt schemas fail closed. Passwords are absent from the domain model, database schema, and migration backup.

Imports are untrusted input. The infrastructure layer enforces extension, size, declared type, encoding, XML root, depth, element count, server count, and field-type limits before producing a review-only preview. Secret fields are discarded during parsing and never enter preview, profile, or export contracts.

## Stable contracts

The desktop establishes `ConnectionProfile`, `ConnectionFolder`, `RdpSettings`, `RdpSession`, `CredentialReference`, `PolicyDecision`, `AuditEvent`, `ImportPreview`, and `ConfigurationVersion`, together with repository, RDP-host, credential-broker, policy, audit, import, and diagnostics interfaces. Centralized implementations extend these boundaries.

`ConfigurationInheritance` defines deterministic organization → workspace → folder → connection precedence. Each layer records whether it explicitly overrides an inherited value and whether it locks that effective value against lower scopes. Local profiles use their own connection settings; centrally managed profiles consume the same tested resolver.

## Shared-management platform

The self-hosted control plane uses ASP.NET Core 10, PostgreSQL, React/TypeScript administration, standards-based identity, OpenFGA/OPA authorization, OpenTelemetry, and versioned REST/OpenAPI. Gateway, vault, event streaming, cache, cloud, SIEM, and ticketing systems remain adapters. See [the Phase 2 architecture](architecture/phase2.md) and [feature catalog](reference/feature-catalog.csv) for boundaries and delivery evidence.
