# Phase 1 Live Acceptance

Use this procedure only with a disposable non-production RDP host and a clean Windows 11 x64 test user. Keep credentials in the Windows credential prompt. Do not place a hostname, IP address, username, password, machine name, screenshot containing sensitive data, or free-form note in the results file.

## Prepare

1. Run `scripts/check-phase1.ps1 -Package` on the development machine.
2. Copy `samples/phase1-acceptance-results.template.json` to a working file outside source control.
3. On the clean elevated Windows 11 x64 user, run `scripts/test-msix-install.ps1 -ManualAcceptance`. The script installs and opens the packaged app, then waits while the human completes the walkthrough. Close NextRDP normally when the walkthrough finishes; the script then uninstalls the package and removes only the trust/data state it created.
4. Run `scripts/test-windows-certification.ps1` to create the WACK XML and package-bound evidence sidecar.
5. Change only each result value to `pass`, `fail`, `not-run`, or, for `touch-and-pen` only, `not-applicable`.

The authenticated workflow must exercise creation or safe import, search, the prompt-only Windows credential boundary, multiple embedded tabs, initial sizing and live resize, full-screen entry/exit, configured clipboard/drive/printer/audio redirection, bounded reconnect, clean disconnect, restart persistence, Recent history, password absence, and Production safety labels. Complete the core workflow with a Windows screen reader. Use `not-applicable` for touch/pen only when suitable hardware is unavailable.

## Produce redacted evidence

```powershell
.\scripts\record-phase1-acceptance.ps1 -ResultsPath C:\path\to\phase1-results.json
```

The recorder validates the fixed check set, a Windows 11 x64 client-workstation SKU, and the signed MSIX. Windows Server is rejected even when its build number is newer. A `pass` for clean install/uninstall or Windows App Certification is rejected unless the version-2 machine-generated install JSON and WACK JSON/XML artifacts identify a supported Windows 11 x64 workstation, report success and cleanup, and match the current package SHA-256 and signer. If the installed workflow is marked complete, the install evidence must also show a timestamp-consistent manual-acceptance window of at least 30 seconds, followed by a normal zero-exit close and cleanup. Starting a new install or certification run invalidates the prior fixed evidence before mutation, so a failed rerun cannot be mistaken for new success. The recorder writes a timestamped JSON record under `artifacts\acceptance\` containing the OS version, package hash/signature, hashes of the machine-generated evidence, fixed check labels, results, and overall outcome. It intentionally has no field for targets, accounts, credentials, machine names, or notes.

The command fails when any required check is not `pass`. To retain an explicitly incomplete checkpoint without presenting it as acceptance, add `-AllowIncomplete`. `-ValidateOnly` validates a result file without creating evidence or requiring the package.
