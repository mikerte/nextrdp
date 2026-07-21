# Self-hosting the management preview

The self-hosted stack is a functional preview for evaluation and development. It is not a production deployment template and does not replace the open security, identity-integration, target-infrastructure, availability, and release work recorded in the validation documents.

## Components

Docker Compose starts:

- PostgreSQL for authoritative shared state.
- OpenFGA for relationship-based authorization.
- Open Policy Agent for policy decisions.
- The ASP.NET Core control plane.
- The React administration console.

The administration console is bound to `127.0.0.1:5090`; the API is bound to `127.0.0.1:5080`. Internal services are not published to the host.

## Requirements

- Docker Desktop with Compose v2.
- At least 4 GB of available memory for the stack.
- PowerShell for repository smoke and restore scripts.

## Start a local evaluation

```powershell
Copy-Item .env.example .env
```

Edit `.env` and replace `POSTGRES_PASSWORD` with a long random value used only for this local deployment. Keep `.env` outside source control.

```powershell
docker compose up -d --build --wait
docker compose ps
```

Open `http://127.0.0.1:5090`. The Compose configuration sets `ASPNETCORE_ENVIRONMENT=Development` and enables the local development identity. This identity is intentionally unavailable outside Development.

Stop containers while retaining volumes:

```powershell
docker compose down
```

Remove the local evaluation deployment and all of its data only when you have confirmed that nothing needs to be retained:

```powershell
docker compose down --volumes --remove-orphans
```

## Health and diagnostics

```powershell
docker compose ps
docker compose logs --tail 200 control-plane
docker compose logs --tail 200 admin
```

Useful endpoints:

- API liveness: `http://127.0.0.1:5080/health/live`
- API readiness: `http://127.0.0.1:5080/health/ready`
- Administration console: `http://127.0.0.1:5090`

Logs can contain operational identifiers. Redact them before sharing and never post tokens, cookies, connection strings, target data, or user information.

## Live integration and restore drill

Run the complete static and live checks with local-only disposable values:

```powershell
.\scripts\check-phase2.ps1 -Configuration Release -Live `
  -PostgresPassword '<same local database password>' `
  -BackupPassphrase '<new disposable backup passphrase>'
```

The workflow exercises scoped administration, OIDC protocol behavior, authorization denials, synchronization, automation, encrypted backup creation, and an isolated clean restore. It does not prove behavior against a real corporate identity provider, Connection Broker, RD Gateway, RemoteApp deployment, domain account state, or unavailable devices.

Do not reuse the database password as the backup passphrase. Do not commit or upload either value or the generated backup.

## Production-shaped configuration

The Development Compose file is deliberately local-only. A production-shaped deployment must, at minimum:

- Use a real HTTPS endpoint and reverse proxy.
- Disable the development identity and configure a reviewed corporate OIDC provider.
- Provide issuer, audience, signature, lifetime, state, nonce, PKCE, MFA-claim, and group-mapping configuration.
- Use protected secrets rather than `.env` files.
- Encrypt persisted ASP.NET Core Data Protection keys with a protected RSA certificate.
- Define certificate issuance, custody, rotation, backup, and recovery ownership.
- Use TLS for PostgreSQL and internal authorization services where traffic crosses a trust boundary.
- Establish monitoring, rate limits, backups, restore testing, incident response, and an upgrade/rollback process.
- Complete the outstanding target/security validation and an explicit production release review.

Data Protection settings use:

```text
Security__DataProtectionCertificatePath
Security__DataProtectionCertificatePassword
Security__DataProtectionUnprotectingCertificates__0__Path
Security__DataProtectionUnprotectingCertificates__0__Password
```

The control plane fails closed outside Development when valid key-protection configuration is absent. Never commit certificate files or passwords.

## Backup and recovery

The live check invokes the authenticated encrypted backup and isolated restore scripts. For operational use:

- Store backup files separately from the database host.
- Store the backup passphrase in an approved secret manager.
- Retain the certificates needed to decrypt historical Data Protection keys during the approved rotation window.
- Test recovery into an isolated environment before relying on a backup.
- Verify restored organization/workspace scope and audit integrity before reconnecting clients.

## Updating

Before updating:

1. Review `CHANGELOG.md` and schema changes.
2. Create and verify an encrypted backup.
3. Pull the selected commit or release source.
4. Rebuild with `docker compose build --pull`.
5. Start with `docker compose up -d --wait` and verify health plus a scoped sign-in/synchronization smoke.

There is no stable production upgrade guarantee until the first supported release is published.
