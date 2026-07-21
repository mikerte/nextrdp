# Contributing to NextRDP

Thanks for helping improve NextRDP. Bug reports, documentation fixes, tests, and focused code changes are welcome.

## Before opening a change

1. Search existing issues and discussions.
2. Open an issue before starting a large feature, protocol change, database migration, security-sensitive change, or new dependency.
3. Keep credentials, private hostnames, IP addresses, exported inventories, certificates, and real `.rdp` or `.rdg` files out of issues, commits, screenshots, and test data.

## Development setup

Follow [the development guide](docs/development.md). The normal validation command on Windows is:

```powershell
.\scripts\check-phase2.ps1 -Configuration Release
```

Changes to the self-hosted stack should also pass the live Compose checks described in [the self-hosting guide](docs/self-hosting.md).

## Pull requests

- Create a focused branch from `main`.
- Add or update tests for behavior changes.
- Update public documentation when commands, settings, or user-visible behavior change.
- Do not commit build output, local databases, `.env`, certificates, package artifacts, or generated frontend directories.
- Use clear commit messages and resolve all automated checks.
- Complete the security and testing sections in the pull-request template.

## Licence and Developer Certificate of Origin

Unless you explicitly state otherwise, contributions submitted for inclusion in NextRDP are licensed under the Apache License 2.0, consistent with the repository licence.

Every commit must include a `Signed-off-by` line certifying the Developer Certificate of Origin below. Create it with:

```text
git commit --signoff
```

By making a contribution, you certify that:

1. The contribution was created in whole or in part by you and you have the right to submit it under the open-source licence indicated in this repository; or
2. The contribution is based on previous work that, to the best of your knowledge, is covered under an appropriate open-source licence and you have the right under that licence to submit the work with modifications under the same open-source licence; or
3. The contribution was provided directly to you by another person who certified either of the preceding statements, and you have not modified it; and
4. You understand and agree that the project and contribution are public and that a record of the contribution, including all personal information submitted with it, is maintained indefinitely and may be redistributed consistent with this project or its open-source licence.

## Reporting security issues

Do not open a public issue for a suspected vulnerability. Follow [SECURITY.md](SECURITY.md).
