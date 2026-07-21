# Security policy

## Supported versions

NextRDP is currently an early-access project. Security fixes are applied to the latest commit on `main`; there is no production-supported binary release or long-term support branch yet.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability and do not include credentials, connection inventories, private targets, certificates, tokens, or exploit details in public discussions.

Use GitHub's **Report a vulnerability** button in the repository Security tab to open a private vulnerability report. Include:

- The affected component and version or commit.
- Reproduction steps using disposable, non-production data.
- The security impact and any known prerequisites.
- Suggested mitigation, if available.

The maintainer will acknowledge a complete report within seven calendar days, coordinate remediation privately, and publish an advisory when a fix is ready. Timelines may vary with severity and reproducibility because the project does not yet have a commercial support commitment.

## Security boundary

- RDP passwords are entered in the Windows credential prompt and are not stored by NextRDP.
- Imported secret fields and encrypted password blobs are discarded.
- The self-hosted Development configuration uses a local development identity and must not be exposed as a production deployment.
- Test-signed MSIX packages are for local evaluation only.

See [the threat model](docs/threat-model.md) for the current trust boundaries and open risks.
