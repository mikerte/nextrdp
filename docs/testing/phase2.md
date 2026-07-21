# QA Plan: NextRDP Phase 2

## Status

- State: Local automation and deployment evidence complete; external and target-specific validation remains in progress

## Quality Goals

- No cross-organization/workspace data or action leakage.
- No RDP password, token, client secret, or sensitive imported blob in shared data, logs, exports, support packages, or test artifacts.
- Deterministic configuration provenance, publication, rollback, synchronization, and revocation.
- Accessible, responsive, keyboard-first administration and desktop workflows.
- Recoverable self-hosted deployment with directly validated backup/restore.

## Test Scope

- Functional: all 105 catalog requirements plus eight platform deliverables and the nine-step acceptance flow.
- Accessibility: WCAG 2.2 AA automated rules, keyboard/focus/zoom/contrast, screen-reader labels/live regions, WPF UI Automation, DPI and High Contrast.
- Performance: bounded paging/bulk/import, search, first/sync delta, policy resolution, admin route budgets, outbox backlog, and multiple-session resource behavior.
- Security-adjacent: authentication protocol validation, production MFA claim enforcement, rejection of raw IdP role claims, mapped-role authorization, cross-scope denial, CSRF/CORS, rate limits, SSRF-safe links, import bounds, redaction, secret scan, dependency review, key-ring encryption/rotation, and backup tamper/wrong-key tests.
- Regression: all Phase 1 build, 102 tests, package checks, prompt-only credentials, local inventory, RDP interop, import/export, themes, and accessibility behavior.

## Automation Layers

- Domain unit tests for aggregate invariants, effective configuration, revisions, fixed roles, sync cursors, idempotency, metadata validation, semantic versions, workspace restore, and redaction.
- Application tests for commands/queries, per-item bulk outcomes, import preview/commit, publication/rollback, audit requirements, backup manifest, and failure mapping.
- PostgreSQL integration tests for migrations, constraints, transaction/outbox atomicity, concurrent revisions, append-only audit, scoped queries, backup/restore, and indexes.
- Auth/policy integration tests for OpenIddict code/PKCE/client-credentials/revocation, OpenFGA relationships, OPA decisions, dependency unavailability, and group mapping.
- API contract tests for OpenAPI, problem details, pagination, ETags/If-Match, idempotency, rate limits, content limits, authorization, and secret-free schemas.
- Admin tests with Vitest/Testing Library and Playwright for every route, role, state, responsive layout, keyboard path, accessibility tree, and error recovery.
- Desktop tests for sync snapshots/deltas/tombstones/conflicts, read-only offline mode, minimum-version behavior, central/local separation, no secret transfer, and workspace restoration.
- RDP COM tests and live acceptance for Gateway, RemoteApp, admin mode, windows/monitors, presets, quality, redirection, shortcuts, thumbnails/privacy, and diagnostics.
- Compose/end-to-end tests for startup order, health/readiness, OIDC fixture, full acceptance flow, restart persistence, restore, and dependency failure/recovery.
- Fail-closed target-evidence tests for exact catalog IDs, controlled result vocabulary, duplicate/missing/unknown properties, signed-package/source/platform binding, monitor capability, evidence hashing, privacy acknowledgement, and prompt-only credentials.

## Test Data

- Synthetic organizations, workspaces, users, groups, roles, folders, profiles, policies, and revisions using reserved documentation domains and RFC test addresses.
- No real hostname, account, token, client secret, RDP credential, tenant identifier, or identity-provider metadata in source or retained evidence.
- Generated malformed, boundary, duplicate, stale-revision, cross-scope, revoked, tampered-backup, and outage cases.

## Acceptance Criteria

- Catalog and matrix validators prove exact Phase 2 scope.
- Release builds have zero warnings/errors; all Phase 1 and Phase 2 automated checks pass.
- No known high/critical dependency finding or credential scan match remains unexplained.
- Cross-boundary and unavailable-policy-service tests fail closed.
- Compose, PostgreSQL migration, real OpenFGA/OPA, backup/restore, admin browser, desktop synchronization, and Windows RDP evidence are retained.
- Every completion row contains direct evidence and no planned placeholder.
- A production-readiness and security review must complete before any production claim.

## Known Risks

- A real corporate OIDC provider, RD Gateway, RemoteApp source, selected-monitor setup, device hardware, and clean deployment environment require suitable disposable external/environment access.
- Automated accessibility does not replace human screen-reader and keyboard validation.
- Local Compose stores development data-protection keys in a persistent Docker volume; production requires a protected key store and defined key lifecycle.

## Remaining validation

Execute `docs/phase2-target-acceptance.md`, retain only redacted evidence, complete security review, and finish production-readiness checks before publishing a supported release.
