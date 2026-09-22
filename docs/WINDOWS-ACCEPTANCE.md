# Windows release acceptance

Run in disposable Windows 11 Pro/Enterprise x64 VMs with snapshots, a test Active Directory domain and representative licensed vendor media. Record the result, Windows version, installer version and deployment log for each case. None of the Windows/manual cases below is claimed passed by the macOS development checks.

| SRS case | Check |
|---|---|
| AC-001 | Configure a kit at D:, remount at E:, verify every profile and registered path works. |
| AC-002 | Launch the complete self-contained kit on a PC without the .NET Desktop Runtime. Confirm UAC elevation and usable WPF forms. |
| AC-003–004 | Import clinic Syncro; create two profiles; confirm both inherit the same relative mapping. Replace it and confirm both use the replacement. |
| AC-005–006 | Universal software appears in all clinic editors; clinic software only in its owner. New packages are available but not selected. |
| AC-007 | Import nested media with payload files and a nested entrypoint; verify the complete package is copied and working directory is correct. |
| AC-008 | Override software checkboxes for one deployment; confirm saved profile is unchanged and required Syncro remains. |
| AC-009 | Re-run a detected package; confirm skipped state and no installer invocation. |
| AC-010 | Break DNS/DC connectivity; preflight must block installation and must not request credentials. |
| AC-011–012 | Join test domain and configured OU under a runtime account, rename, reboot and verify membership. Inspect DB/logs/process command lines for credentials. Test a rejected join and a rename failure independently. |
| AC-013–015 | Exercise nonzero and 3010 exits, critical override/abort, and multiple missing media files. Missing files must be listed before any install starts. |
| AC-016–018 | Exercise correct/incorrect silent arguments, failed detection after exit zero, interactive fallback and Interactive Only with visible documentation. |
| AC-019 | Read ProductCode from a known MSI, test install, detect by ProductCode and test default MSI uninstall. |
| AC-020–021 | Discover quiet/normal uninstall registrations, confirm selected executable/arguments and verify silent and interactive uninstall both require absence. |
| AC-022–023 | Select only a dependent package, test detected/successful/failed prerequisites and a cycle. Failed dependencies must block dependents. |
| AC-024–025 | Analyze representative Inno/NSIS/InstallShield/Burn/MSI and unknown wrappers; verify suggestions remain editable and optional. Test explicitly selecting an existing MSI from imported media. |
| AC-026–028 | Test all combinations of blank/general/site HTTPS KB links, two clinics and browser launching. No KB credentials are captured. |

Additional checks: cancel credential dialog; continue waiting after timeout; explicitly terminate a harmless test installer; invalid fields; duplicate profile/clinic names; clone/delete; inactive packages; missing scripts; USB read-only/full/unplug errors; failed database save; restore a backup by closing the app and replacing CITDeploy.db; non-ASCII paths; spaces in paths; batch metacharacters in paths; high-DPI layout; keyboard-only navigation; UAC and default-browser behavior.

Known boundaries: there is no reboot resume, vendor installer download, automated extraction of proprietary EXE wrappers, or post-reboot Syncro registration validation. Analyze Installer scans at most the first 16 MiB for signatures and enumerates already imported MSI candidates; it never runs an unknown wrapper to extract payloads. Installer stdout/stderr is intentionally not persisted. The app retains imported media when a definition is deleted. External installers and Windows necessarily write their own application/system data to the target PC; the portability guarantee covers CIT Deploy's configuration, media, logs, backups and runtime.
