# Development guide

## Repository layout

- `src/NextRdp.Desktop`: WPF shell, inventory UI, synchronization client integration, and session workspace.
- `src/NextRdp.RdpHost`: isolated Windows Forms/ActiveX RDP host and COM event mapping.
- `src/NextRdp.Core` and `src/NextRdp.Infrastructure`: local domain, import, diagnostics, persistence, and audit behavior.
- `src/NextRdp.Platform.*`: shared-management domain, application, client, and PostgreSQL infrastructure.
- `src/NextRdp.ControlPlane`: ASP.NET Core API, identity, authorization, health, and administration backend.
- `src/NextRdp.Admin`: React/TypeScript administration console.
- `src/NextRdp.Cli`: command-line automation client.
- `tests/`: .NET test projects; frontend tests live beside the administration console.
- `scripts/`: build, validation, packaging, acceptance, backup, restore, and smoke workflows.

The solution uses C# 14, .NET 10, WPF, Windows Forms interoperability, SQLite/EF Core, ASP.NET Core, PostgreSQL/Npgsql, React, TypeScript, OpenIddict, OpenFGA, OPA, OpenTelemetry, and Docker Compose.

## Toolchain

- Windows 11 x64.
- .NET SDK selected by `global.json`.
- Visual Studio Community 2026 with .NET desktop, Windows SDK/MSIX, and .NET Framework SDK tools.
- Node.js 24 and npm 11 for the administration console.
- Docker Desktop for Compose validation and integration testing.

Restore frontend dependencies after cloning:

```powershell
Set-Location .\src\NextRdp.Admin
npm ci
Set-Location ..\..
```

NuGet restoration occurs automatically during the first solution build.

## RDP interop generation

The repository does not commit `MSTSCLib.dll` or `AxInterop.MSTSCLib.dll`. The `NextRdp.RdpHost` project generates them from `%WINDIR%\System32\mstscax.dll` into `artifacts\interop\` before resolving references.

The default `AxImpPath` uses the 32-bit Program Files path reported by MSBuild. Override it on a custom installation:

```powershell
dotnet build .\NextRdp.slnx -p:AxImpPath="D:\VisualStudioTools\AxImp.exe"
```

Do not copy generated interop assemblies between machines as a substitute for installing the supported Windows client components and SDK tools.

## Common commands

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release
.\scripts\check-phase1.ps1 -Configuration Release
.\scripts\check-phase2.ps1 -Configuration Release
```

Administration-console development:

```powershell
Set-Location .\src\NextRdp.Admin
npm run dev
npm run check
npm audit --audit-level=low
```

Run the Windows application:

```powershell
dotnet run --project .\src\NextRdp.Desktop\NextRdp.Desktop.csproj -c Debug
```

## Validation expectations

- New behavior needs deterministic tests at the narrowest applicable layer.
- RDP-host changes need both typed COM mapping tests and real STA activation coverage where possible.
- Import changes must retain file-size, schema, depth/count, deduplication, and secret-field rejection tests.
- Authorization changes must include cross-organization and cross-workspace denial cases.
- Synchronization changes must cover paging, conflicts, revocation, restart, and offline behavior.
- User-interface changes must preserve keyboard operation, automation names, focus, contrast, high-DPI layout, and both themes.
- Public text and support artifacts must remain free of credentials, private target data, and unredacted exception content.

The feature baseline and delivery evidence are validated through `docs/reference/feature-catalog.csv`, `docs/validation/phase1-completion.md`, and `docs/validation/phase2-completion.md`.

## Local secrets and test data

- Copy `.env.example` to ignored `.env`; never edit the example with real values.
- Use disposable non-production targets and enter RDP credentials only in the Windows prompt.
- Do not pass passwords or tokens in command history when an interactive or environment-local alternative exists.
- Keep raw acceptance evidence outside the repository and record only redacted hashes/status where required.
- Generated packages, databases, certificate material, logs, and acceptance output belong under ignored paths.

## Contribution workflow

Read [CONTRIBUTING.md](../CONTRIBUTING.md). Pull requests should be focused, signed off under the Developer Certificate of Origin, and green under the Windows and self-hosted CI workflows.
