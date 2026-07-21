# Release checkpoint validation: v0.2.0-preview.1

## Boundary

This checkpoint freezes the working Windows desktop MVP and Phase 2 self-hosted management preview before privileged-access implementation begins. The public GitHub pre-release is source-only. Locally generated ZIP and MSIX outputs are evaluation artifacts and are not public release assets.

## Automated release gate

The release command must pass all of the following from a clean tagged source tree:

- Release metadata consistency across .NET assemblies, the administration console, the PowerShell module, the Git tag, and the numeric MSIX version.
- The complete Phase 1 and Phase 2 requirement mapping and regression suites.
- .NET and npm dependency vulnerability audits.
- First-party credential-pattern scanning and public-source-boundary validation.
- Administration-console production build and tests.
- Docker Compose configuration validation.
- Self-contained Windows x64 publication and responsive unpackaged launch.
- MSIX identity, publisher, architecture, signature, payload, sensitive-name, and SHA-256 verification.
- Windows 11 x64 clean-install and Windows App Certification Kit preflights.
- Deterministic ZIP entry ordering/timestamps, a local evaluation-artifact manifest, and SHA-256 output.

Run:

```powershell
.\scripts\build-preview-release.ps1
```

## Isolated Windows acceptance

The machine-backed gate uses either a disposable Windows 11 x64 test account or a clean `NextRDP.Client` package namespace. Both scripts launch the installed executable against a disposable test-only inventory, fingerprint any existing `%LOCALAPPDATA%\NextRDP` directory before and after the run, and prove it was unchanged. Evidence records no target, account, credential, or machine name.

1. `test-msix-install.ps1` verifies clean install, responsive launch, normal close, uninstall, package-data cleanup, and removal of temporary certificate trust.
2. `test-msix-upgrade.ps1` verifies stable identity, publisher, architecture, and signer; launch of the previous package; upgrade to `0.2.0.0`; isolated inventory-data persistence; launch after upgrade; uninstall; data cleanup; and temporary-trust cleanup.
3. The test certificate and test-signed package remain local and are never represented as production trusted.
4. Both scripts refuse an existing package installation or package-data directory and verify that temporary trust is removed when they added it.
5. `invoke-msix-acceptance.ps1` provides a self-elevating entry point and writes only ignored local transcripts; it does not change the evidence schema or publish machine details.

## Release acceptance

- The merge commit on protected `main` passes both required GitHub workflows.
- The annotated `v0.2.0-preview.1` tag points to that exact merge commit.
- The GitHub entry is marked as a pre-release and contains no uploaded binary assets.
- The generated GitHub source archives are reachable anonymously.
- Production signing, MSI, enterprise software distribution, updater channels, and release support remain outside this checkpoint.
