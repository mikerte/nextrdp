# Changelog

All notable changes to NextRDP will be documented here. The project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and semantic versioning.

## [Unreleased]

## [0.2.0-preview.1] - 2026-07-21

### Added

- Working Windows 11 x64 desktop MVP with embedded Microsoft RDP sessions, local SQLite inventory, safe import, diagnostics, accessibility support, and prompt-only credentials.
- Self-hosted shared-management preview with PostgreSQL, ASP.NET Core, React, OIDC/RBAC foundations, policy publication, desktop synchronization, automation, and encrypted backup/restore.
- Apache-2.0 licensing, public setup documentation, community files, continuous integration, and dependency/security automation.

### Security

- Passwords remain in the Windows credential prompt and are excluded from profiles, imports, exports, synchronization, logs, and support data.

This checkpoint is a source-only GitHub pre-release. No production-signed binary release has been published.

[Unreleased]: https://github.com/mikerte/nextrdp/compare/v0.2.0-preview.1...HEAD
[0.2.0-preview.1]: https://github.com/mikerte/nextrdp/releases/tag/v0.2.0-preview.1
