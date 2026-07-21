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
.\scripts\package-msix.ps1 -Version 0.1.0.0
```

The script:

1. Produces the self-contained x64 build if necessary.
2. Creates `artifacts\msix\NextRDP-0.1.0.0-x64.msix` with identity `NextRDP.Client`.
3. Creates a local development signing certificate under ignored `artifacts\msix\`.
4. Signs and verifies the package and writes hashes.

Packaging does not install the application and does not modify certificate trust. On an elevated, disposable Windows 11 x64 test account, run:

```powershell
.\scripts\test-msix-install.ps1 -PreflightOnly
.\scripts\test-msix-install.ps1
```

The full test temporarily places only the generated certificate in Local Machine **Trusted People**, installs and launches the package, uninstalls it, and removes the trust entry it created. It intentionally refuses to run against an account that already contains NextRDP application data or an installed NextRDP package.

Never distribute the generated signing key or represent this package as production trusted.

## Upgrade from older development builds

Pre-public test packages used identity `NextRDP.Hackathon`. Windows treats `NextRDP.Hackathon` and `NextRDP.Client` as different applications. Uninstall the old package before installing the new one:

```powershell
Get-AppxPackage -Name NextRDP.Hackathon | Remove-AppxPackage
```

Back up any local inventory you need through NextRDP's secret-free inventory export before removing old test data.

Future packages that retain the `NextRDP.Client` identity and publisher can use the normal MSIX upgrade path when properly signed.

## Data and uninstall

Unpackaged builds store local data in:

```text
%LOCALAPPDATA%\NextRDP\
```

MSIX builds use the Windows package data directory for `NextRDP.Client`. Uninstall an evaluation package through **Settings → Apps → Installed apps** or:

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

Packaged and unpackaged applications use different Windows data locations. Confirm which build is running and use the in-app export rather than copying a live SQLite database.
