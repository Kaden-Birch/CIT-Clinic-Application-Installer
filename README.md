# CIT Deploy

Portable Windows workstation provisioning for clinic technicians. The kit contains its own .NET runtime, SQLite configuration, local installer media, logs and backups. It runs from USB and requests administrator elevation.

## Use the Windows release

1. Extract **the entire** `CITDeploy-win-x64.zip` to a writable USB drive. Keep the DLLs and runtime files beside `CITDeploy.exe`.
2. On Windows 11 Pro/Enterprise x64, launch `CITDeploy.exe` and accept elevation.
3. In **Manage clinics**, create a clinic, optionally enter its AD domain, OU and naming prefix, and optionally import its clinic-specific Syncro installer. Clinics without one can be saved and deployed; Syncro is skipped.
4. In **Software library**, import a file or a complete media folder and select its entrypoint. Configure installation mode, arguments, detection, prerequisites and optional documentation. Analyze and test packages on a controlled Windows test machine.
5. In **Manage profiles**, select a clinic and choose its software/order. Syncro and domain settings are inherited automatically.
6. In **Deploy**, choose the clinic/profile, review the name and optional software overrides, then start. Prerequisites are added automatically. Provide authorized domain credentials only at the domain stage. Review the final summary and choose when to reboot.

No installers are downloaded. Do not put passwords or tokens in package arguments. Local vendor installers may have their own network or disk requirements.

## Package maintenance

- Supported entrypoints: EXE, MSI, PS1, CMD and BAT. Folder imports preserve supporting files. Replacing media retains the software ID and profile assignments.
- Automatic mode tries unattended execution and offers retry, interactive fallback, explicit override or abort. Silent Only does not offer interactive fallback. Interactive Only waits for technician confirmation.
- Detection supports MSI ProductCode, uninstall display-name substring, target file/folder, registry key/value and bundled PowerShell scripts. Detection scripts exit `0` for present and `1` for absent; other exit codes are errors. Registry syntax: `HKLM\Software\Vendor|ValueName|ExpectedValue`; omit the latter parts to check a key or value’s existence.
- Install/uninstall tests cannot pass without detection. Production packages without detection run every time and are explicitly reported as unverified. MSI defaults are `/qn /norestart`; code `3010` retains the reboot-required result.
- Pre/post-install and detection scripts must be `.ps1` files inside the imported package folder. Their configuration uses USB-relative paths.
- Analyze Installer reads local signatures for MSI, Inno Setup, NSIS, InstallShield and WiX/Burn. Suggestions require testing. Existing MSI files in imported media can be explicitly chosen as alternative entrypoints. Uncompressed embedded MSI candidates are copied locally only when their MSI ProductCode can be read. No wrapper is executed merely to analyze it and no vendor-specific extraction is assumed.
- MSI ProductCodes can be read directly. Registry uninstall discovery offers registered normal/quiet commands for review; commands with spaces in the executable path must quote that path. MSI ProductCode takes precedence when uninstalling.
- An installer timeout offers continued waiting or explicitly confirmed process-tree termination. Termination can damage an installation; the app does not silently terminate installers.
- Failed/overridden prerequisites block dependent packages. Failures remain visible in the final result. Syncro is optional: an unconfigured installer is shown and logged as skipped. A configured installer must still exist and install successfully before domain join. Use Remove Syncro mapping in Manage clinics to stop deploying an existing mapping.
- General and per-clinic HTTPS documentation links are independent and hidden when absent. Site links are edited in Manage clinics.

## Storage and safety

`AppContext.BaseDirectory` is the kit root. `CITDeploy.db`, `Software/Universal`, `Software/Clinics`, `Syncro`, `Logs`, `Backups` and `Support` remain on the USB. Media paths are relative, and traversal/reparse points are rejected. Opaque media directory IDs avoid name collisions and make clinic/software renames safe. Deleting definitions retains media to avoid accidentally destroying proprietary installer files.

SQLite has normalized clinics, profiles, software, profile selections, dependencies, clinic KB links and versioned settings. Management saves are transactional and create a consistent SQLite backup first. Unknown schema versions are rejected. Do not run concurrent copies against one kit. Domain passwords are held in `SecureString`, passed directly to Windows APIs through a temporary unmanaged buffer, and zeroed afterward. They are never supplied in process arguments or persisted. Installer output is drained but not retained because it may disclose vendor enrollment data.

The domain is optional. When blank, domain preflight, credential prompts, joining and renaming are skipped; the existing computer name is kept, and reboot is only offered if an installer requires it. When a domain is configured, domain controller discovery runs before installation and again before asking for credentials. Windows joins the configured domain/OU and renames the account. A rename failure after a successful join is reported as a partial failure requiring reboot. Domain membership, rename and real installer behavior must be tested in your environment.

## Build and test

Install the .NET 8 SDK on the development machine, then run:

```powershell
dotnet restore CITDeploy.sln
dotnet build CITDeploy.sln -c Release
dotnet test tests/CITDeploy.Tests/CITDeploy.Tests.csproj -c Release
./publish.ps1
```

`publish.ps1` creates `artifacts/CITDeploy-win-x64.zip`. The target PC does not require the SDK or Desktop Runtime. A folder-based self-contained release avoids extracting application runtime files into the target user's temporary directory. GitHub Actions builds/tests on Windows and uploads the portable kit for each PR.

## Verification status

The implementation builds and the portable core tests run on the development host. Tests cover dependency ordering/cycles/scope, USB relocation/traversal, missing media, folder imports, SQLite transactions and relationship persistence, clinic inheritance, KB validation, name validation, skip detection, fallback modes, verified uninstall and reboot codes.

**Windows acceptance testing remains required before production deployment.** This development host is macOS and cannot run the WPF UI, Windows registry/MSI integration, vendor installers, elevation, or real AD operations. See [the Windows acceptance checklist](docs/WINDOWS-ACCEPTANCE.md). The release is an initial implementation for controlled validation, not a claim that the 28 environment-dependent acceptance cases have already passed.

## Source layout

- `src/CITDeploy.Core`: models, rules/planning, portable storage, normalized SQLite repository and testable installer state machine.
- `src/CITDeploy.Windows`: WPF shell/editor views, session/progress view models, Windows installer/detection/domain services.
- `tests/CITDeploy.Tests`: automated core tests using fake installers/detectors; no real software is installed by these tests.

Native domain behavior follows Microsoft's [NetJoinDomain](https://learn.microsoft.com/en-us/windows/win32/api/lmjoin/nf-lmjoin-netjoindomain) and [NetRenameMachineInDomain](https://learn.microsoft.com/en-us/windows/win32/api/lmjoin/nf-lmjoin-netrenamemachineindomain) documentation.
