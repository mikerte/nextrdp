# Install and run the Windows desktop

NextRDP currently publishes source code rather than production-signed binaries. The supported evaluation target is Windows 11 x64.

## Requirements

- Windows 11 x64 with the built-in Remote Desktop client components.
- .NET SDK 10.0.302 or a compatible later patch accepted by `global.json`.
- Visual Studio Community 2026 with:
  - .NET desktop development.
  - Windows 11 SDK and MSIX packaging tools.
  - .NET Framework SDK tools containing `AxImp.exe`.
- PowerShell 5.1 or later.

The first build generates managed RDP ActiveX interop assemblies under ignored `artifacts/interop/`. Those assemblies are generated from the Windows `mstscax.dll` installed on the build machine and are not stored in Git.

If `AxImp.exe` is not in the normal SDK directory, supply its full path:

```powershell
dotnet build .\NextRdp.slnx -c Release -p:AxImpPath="D:\SdkTools\AxImp.exe"
```

## Run from source

```powershell
git clone https://github.com/mikerte/nextrdp.git
Set-Location .\nextrdp
.\scripts\build.ps1 -Configuration Release
dotnet run --project .\src\NextRdp.Desktop\NextRdp.Desktop.csproj -c Release
```

Windows displays its own credential prompt when a connection opens. NextRDP does not accept a password on the command line and does not save it in the profile database.

## Create a self-contained build

```powershell
.\scripts\publish-windows.ps1 -Configuration Release
.\scripts\smoke-unpackaged.ps1
```

The executable is written to `artifacts\publish\win-x64\NextRdp.Desktop.exe`. The folder also contains a SHA-256 manifest. Keep the full folder together because the executable depends on the adjacent self-contained runtime and application files.

## Create a test-signed MSIX

```powershell
.\scripts\package-msix.ps1
```

The script:

1. Produces the self-contained x64 build if necessary.
2. Reads the authoritative version from `eng\Release.props` and creates `artifacts\msix\NextRDP-0.2.0.0-x64.msix` with identity `NextRDP.Client`.
3. Creates a local development signing certificate under ignored `artifacts\msix\`.
4. Signs and verifies the package and writes hashes.

Packaging does not install the application and does not modify certificate trust. On Windows 11 x64, run:

```powershell
.\scripts\test-msix-install.ps1 -PreflightOnly
.\scripts\invoke-msix-acceptance.ps1 -Mode Install
```

The full test temporarily places only the generated certificate in Local Machine **Trusted People**, installs and launches the package, uninstalls it, and removes the trust entry it created. A disposable test account is preferred. The lifecycle script also supports an account that has used the unpackaged application, provided the `NextRDP.Client` package namespace itself is clean.

The runner requests elevation and records its local transcript under ignored `artifacts\acceptance\`. It launches the installed executable against a disposable test-only inventory, hashes the complete `%LOCALAPPDATA%\NextRDP` directory before and after the test, and fails if any existing file or directory changes. It refuses an existing `NextRDP.Client` installation or package-data directory. AppX deployment requires the generated certificate in Local Machine **Trusted People**; the script adds it temporarily and removes it afterward if the test added it.

Never distribute the generated signing key or represent this package as production trusted.

## Build the complete preview checkpoint

From a clean checkout, run:

```powershell
.\scripts\build-preview-release.ps1
```

This runs the Phase 1/2 Release gate, builds the self-contained desktop, produces and verifies the local test MSIX, and writes a release ZIP, SHA-256 manifest, and machine-readable release manifest to `artifacts\release\v0.2.0-preview.1\`. The GitHub preview remains source-only; these locally signed artifacts are not published.

## Upgrade from older development builds

Pre-public test packages used identity `NextRDP.Hackathon`. Windows treats `NextRDP.Hackathon` and `NextRDP.Client` as different applications. Uninstall the old package before installing the new one:

```powershell
Get-AppxPackage -Name NextRDP.Hackathon | Remove-AppxPackage
```

Back up any local inventory you need through NextRDP's secret-free inventory export before removing old test data.

Packages that retain the `NextRDP.Client` identity and publisher use the normal MSIX upgrade path. The isolated upgrade test verifies identity and signer continuity, launch before and after upgrade, isolated inventory-data persistence, uninstall, data cleanup, and temporary-trust cleanup:

```powershell
.\scripts\test-msix-upgrade.ps1 `
  -PreviousPackagePath 'C:\path\to\previous\NextRDP-x64.msix' `
  -PreflightOnly

# Run the machine-backed lifecycle; this requests elevation:
.\scripts\invoke-msix-acceptance.ps1 -Mode Upgrade `
  -PreviousPackagePath 'C:\path\to\previous\NextRDP-x64.msix'
```

The previous and current packages must have the same identity, publisher, architecture, and test signer. The script always refuses existing packaged installations and package data. It uses one disposable inventory for both launches to verify application-data continuity across the package upgrade, then removes that inventory and proves any pre-existing unpackaged inventory was unchanged.

## Data and uninstall

Both source-built and MSIX-installed clients store the user's inventory in:

```text
%LOCALAPPDATA%\NextRDP\
```

This shared location preserves the inventory when moving between unpackaged and installed builds. Uninstalling the application does not delete it. Uninstall an evaluation package through **Settings → Apps → Installed apps** or:

```powershell
Get-AppxPackage -Name NextRDP.Client | Remove-AppxPackage
```

Removing an unpackaged build does not remove `%LOCALAPPDATA%\NextRDP`. Delete that folder only after exporting anything you need and confirming that no running NextRDP instance is using it.

## Troubleshooting

### AxImp.exe was not found

Install the Visual Studio .NET desktop workload and .NET Framework SDK tools, or provide `-p:AxImpPath` as shown above.

### No compatible RDP control is available

Confirm that the Windows Remote Desktop client components are installed and that `mstsc.exe` starts normally. NextRDP probes available RDP ActiveX controls from newest to oldest and reports an actionable error when none can activate.

### Test MSIX cannot be installed

Use the isolated install script from an elevated, clean Windows 11 x64 account. Do not import the development certificate into Trusted Root. Review the script's exact platform, signature, existing-package, and existing-data diagnostic.

### Inventory does not appear where expected

Confirm that the application is running under the expected Windows account. Use the in-app export rather than copying a live SQLite database.
