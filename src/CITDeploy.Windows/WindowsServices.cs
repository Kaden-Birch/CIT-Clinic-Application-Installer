using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using CITDeploy.Core;
namespace CITDeploy.Windows;
public enum TimeoutChoice
{
    Wait, Terminate
}
public sealed record InstallerCommand(string File, string Arguments, string WorkingDirectory)
{
    public override string ToString() => $"{WindowsRunner.Quote(File)} {Arguments}\nWorking directory: {WorkingDirectory}";
}
public sealed class WindowsRunner(PortableStorage storage, Func<string, Task<TimeoutChoice>> timeout) : IInstallerRunner
{
    public InstallerCommand Command(Package package, bool interactive, bool uninstall)
    {
        if (!uninstall)
            return Entrypoint(storage.Resolve(package.EntrypointRelativePath), interactive ? package.InteractiveArguments : package.SilentArguments, interactive);
        if (!string.IsNullOrWhiteSpace(package.ProductCode))
        {
            if (!Guid.TryParse(package.ProductCode, out _))
                throw new InvalidOperationException("Invalid MSI ProductCode.");
            return new("msiexec.exe", $"/x {package.ProductCode} {(interactive ? "/norestart" : "/qn /norestart")}", storage.Root);
        }
        var command = !interactive && !string.IsNullOrWhiteSpace(package.QuietUninstallCommand)
            ? package.QuietUninstallCommand : package.UninstallCommand;
        var (file, arguments) = SplitCommand(command);
        if (string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException("Configure an uninstall command or MSI ProductCode.");
        if (file.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            file = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        else if (!Path.IsPathRooted(file))
            file = storage.Resolve(file);
        return new(file, arguments + " " + (interactive ? "" : package.UninstallArguments), Path.GetDirectoryName(file)!);
    }
    public async Task<ExecutionResult> Run(Package package, bool interactive, bool uninstall)
    {
        var started = DateTime.UtcNow;
        var rebootRequired = false;
        if (!uninstall && !string.IsNullOrWhiteSpace(package.PreInstallScript))
        {
            var pre = await Launch(storage.Resolve(package.PreInstallScript), "", false, package.TimeoutSeconds);
            if (pre.ExitCode != 0 || pre.TimedOut)
                return pre;
        }
        var result = await Raw(Command(package, interactive, uninstall), interactive, package.TimeoutSeconds);
        rebootRequired |= result.ExitCode == 3010;
        if (!uninstall && !result.TimedOut && (package.SuccessCodes.Contains(result.ExitCode ?? -1) || result.ExitCode == 3010)
            && !string.IsNullOrWhiteSpace(package.PostInstallScript))
        {
            var post = await Launch(storage.Resolve(package.PostInstallScript), "", false, package.TimeoutSeconds);
            if (post.ExitCode != 0 || post.TimedOut)
                return post;
        }
        return result with
        {
            Duration = DateTime.UtcNow - started,
            RebootRequired = rebootRequired
        };
    }
    public static (string File, string Args) SplitCommand(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0)
                throw new InvalidOperationException("Unclosed executable quote.");
            return (command[1..end], command[(end + 1)..].Trim());
        }
        var split = command.IndexOf(' ');
        return split < 0 ? (command, "") : (command[..split], command[(split + 1)..]);
    }
    public Task<ExecutionResult> Launch(string path, string args, bool interactive, int seconds)
        => Raw(Entrypoint(path, args, interactive), interactive, seconds);
    static InstallerCommand Entrypoint(string path, string args, bool interactive)
    {
        var directory = Path.GetDirectoryName(path)!;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".msi" => new("msiexec.exe", $"/i {Quote(path)} {(interactive ? string.IsNullOrWhiteSpace(args) ? "/norestart" : args : string.IsNullOrWhiteSpace(args) ? "/qn /norestart" : args)}", directory),
            ".ps1" => new(PowerShell, $"-NoProfile -ExecutionPolicy Bypass -File {Quote(path)} {args}", directory),
            ".cmd" or ".bat" => new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"{Quote(path)} {args}\"", directory),
            ".exe" => new(path, args, directory),
            _ => throw new InvalidOperationException("Unsupported registered entrypoint.")
        };
    }
    public static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    public static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    async Task<ExecutionResult> Raw(InstallerCommand command, bool interactive, int seconds)
    {
        if (seconds < 1 || seconds > 86400)
            throw new InvalidOperationException("Timeout must be between 1 and 86400 seconds.");
        var start = DateTime.UtcNow;
        using var process = new Process
        {
            StartInfo = new(command.File, command.Arguments)
            {
                WorkingDirectory = command.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = !interactive,
                RedirectStandardOutput = !interactive,
                RedirectStandardError = !interactive
            }
        };
        if (!process.Start())
            throw new InvalidOperationException("Process failed to start.");
        // Drain without retaining potentially sensitive enrollment data from vendor output.
        using var drainCancellation = new CancellationTokenSource();
        async Task Drain(Stream stream)
        {
            try
            {
                await stream.CopyToAsync(Stream.Null, drainCancellation.Token);
            }
            catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested) { }
        }
        Task stdout = interactive ? Task.CompletedTask : Drain(process.StandardOutput.BaseStream);
        Task stderr = interactive ? Task.CompletedTask : Drain(process.StandardError.BaseStream);
        var timedOut = false;
        while (!process.HasExited)
        {
            var exit = process.WaitForExitAsync();
            if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(seconds))) == exit)
                break;
            if (await timeout($"{Path.GetFileName(command.File)} is still running after {seconds} seconds. Termination may corrupt the installation.") == TimeoutChoice.Terminate)
            {
                if (!process.HasExited)
                    process.Kill(true);
                await process.WaitForExitAsync();
                timedOut = true;
                break;
            }
        }
        // A child process may inherit the pipes after the entrypoint exits. Never hang on those pipes.
        drainCancellation.Cancel();
        await Task.WhenAll(stdout, stderr);
        return new(process.ExitCode, timedOut, DateTime.UtcNow - start);
    }
}
public record UninstallEntry(string Name, string Command, string Quiet, string ProductCode);
public sealed class WindowsDetector(PortableStorage storage) : IDetector
{
    public static IEnumerable<UninstallEntry> Discover()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key == null)
                    continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub?.GetValue("DisplayName") is string display)
                        yield return new(display, sub.GetValue("UninstallString") as string ?? "", sub.GetValue("QuietUninstallString") as string ?? "", Guid.TryParse(name, out _) ? name : "");
                }
            }
    }
    public async Task<bool> Installed(Package p)
    {
        var value = Environment.ExpandEnvironmentVariables(p.DetectionValue);
        switch (p.DetectionType)
        {
            case DetectionType.None:
                return false;
            case DetectionType.MsiProductCode:
                var product = string.IsNullOrWhiteSpace(p.ProductCode) ? value : p.ProductCode;
                if (!Guid.TryParse(product, out _))
                    throw new InvalidOperationException("Configure a valid MSI ProductCode.");
                return MsiQueryProductState(product) == 5;
            case DetectionType.DisplayName:
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException("Display name detection cannot be blank.");
                return Discover().Any(e => e.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
            case DetectionType.FileExists:
                if (!Path.IsPathFullyQualified(value))
                    throw new InvalidOperationException("File detection requires an absolute target path, optionally using environment variables.");
                return File.Exists(value);
            case DetectionType.FolderExists:
                if (!Path.IsPathFullyQualified(value))
                    throw new InvalidOperationException("Folder detection requires an absolute target path, optionally using environment variables.");
                return Directory.Exists(value);
            case DetectionType.Registry:
                return RegistryExists(value);
            case DetectionType.PowerShell:
                var runner = new WindowsRunner(storage, _ => Task.FromResult(TimeoutChoice.Terminate));
                var result = await runner.Launch(storage.Resolve(p.DetectionScriptPath), "", false, 60);
                if (result.TimedOut || result.ExitCode is not (0 or 1))
                    throw new InvalidOperationException("Detection script failed. Scripts must exit 0 (present) or 1 (absent).");
                return result.ExitCode == 0;
            default:
                throw new InvalidOperationException("Unsupported detection.");
        }
    }
    static bool RegistryExists(string value)
    {
        var parts = value.Split('|', 3);
        var slash = parts[0].IndexOf('\\');
        if (slash < 0)
            throw new InvalidOperationException("Registry detection: HKLM\\path|valueName|expectedValue; omit value fields to check key.");
        var hive = parts[0][..slash] switch
        {
            "HKLM" => RegistryHive.LocalMachine,
            "HKCU" => RegistryHive.CurrentUser,
            _ => throw new InvalidOperationException("Use HKLM or HKCU.")
        };
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(parts[0][(slash + 1)..]);
            if (key != null && (parts.Length == 1 || key.GetValue(parts[1]) is object v && (parts.Length == 2 || v.ToString() == parts[2])))
                return true;
        }
        return false;
    }
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern int MsiQueryProductState(string product);
}
public sealed class DomainJoinPartialException(string message) : Exception(message);
public sealed class DomainService
{
    public async Task Preflight(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return;
        await Task.Run(() => { var status = DsGetDcName(null, domain, IntPtr.Zero, null, 0x40000010, out var info); if (info != IntPtr.Zero) NetApiBufferFree(info); if (status != 0) throw new System.ComponentModel.Win32Exception((int)status, "Domain controller discovery failed. Check clinic DNS, network/VPN, and domain name."); });
    }
    public async Task Join(Clinic clinic, string computer, string user, SecureString password)
    {
        if (!Rules.HasDomain(clinic))
            return;
        Rules.ComputerName(computer);
        await Task.Run(() =>
        {
            var ptr = Marshal.SecureStringToGlobalAllocUnicode(password);
            try
            { // Credentials are passed directly to Windows, never command lines or disk.
                var result = NetJoinDomain(null, clinic.DomainFqdn, string.IsNullOrWhiteSpace(clinic.OuPath) ? null : clinic.OuPath, user, ptr, 0x1 | 0x2 | (computer.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? 0u : 0x100u));
                if (result != 0)
                    throw new System.ComponentModel.Win32Exception((int)result, "Domain join failed.");
                if (!computer.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    result = NetRenameMachineInDomain(null, computer, user, ptr, 2);
                    if (result != 0)
                        throw new DomainJoinPartialException($"Domain joined, but computer rename failed (Windows error {result}). Reboot is required; resolve the name before reuse.");
                }
            }
            finally { Marshal.ZeroFreeGlobalAllocUnicode(ptr); }
        });
    }
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] static extern uint NetJoinDomain(string? server, string domain, string? ou, string account, IntPtr password, uint options);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] static extern uint NetRenameMachineInDomain(string? server, string name, string account, IntPtr password, uint options);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] static extern uint DsGetDcName(string? computer, string domain, IntPtr guid, string? site, uint flags, out IntPtr info);
    [DllImport("Netapi32.dll")] static extern uint NetApiBufferFree(IntPtr buffer);
}
public static class InstallerAnalyzer
{
    // Only carve an uncompressed compound-file candidate. Never launch a wrapper or assume vendor switches.
    public static string? TryExtractEmbeddedMsi(string executable, string packageFolder)
    {
        if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        using var input = File.OpenRead(executable);
        if (input.Length > 512L * 1024 * 1024)
            return null;
        var prefix = new byte[Math.Min(input.Length, 16 * 1024 * 1024)];
        input.ReadExactly(prefix);
        byte[] signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        int offset = 1;
        for (var attempt = 0; attempt < 4 && offset < prefix.Length; attempt++)
        {
            var index = prefix.AsSpan(offset).IndexOf(signature);
            if (index < 0)
                return null;
            offset += index;
            var candidate = Path.Combine(packageFolder, "analyzed-" + Guid.NewGuid().ToString("N") + ".msi");
            var keep = false;
            try
            {
                input.Position = offset;
                using (var output = File.Create(candidate))
                    input.CopyTo(output);
                // A CFB signature alone could be another document type. Require a readable MSI ProductCode.
                keep = Guid.TryParse(MsiMetadata.ProductCode(candidate), out _);
                if (keep)
                    return candidate;
            }
            finally { if (!keep && File.Exists(candidate)) File.Delete(candidate); }
            offset++;
        }
        return null;
    }

    public static (string Framework, string Arguments) Analyze(string file)
    {
        if (Path.GetExtension(file).Equals(".msi", StringComparison.OrdinalIgnoreCase))
            return ("MSI", "/qn /norestart");
        using var stream = File.OpenRead(file);
        var buffer = new byte[Math.Min(stream.Length, 16 * 1024 * 1024)];
        stream.ReadExactly(buffer);
        var ascii = System.Text.Encoding.Latin1.GetString(buffer);
        var unicode = System.Text.Encoding.Unicode.GetString(buffer);
        bool Has(string s) => ascii.Contains(s, StringComparison.OrdinalIgnoreCase) || unicode.Contains(s, StringComparison.OrdinalIgnoreCase);
        return Has("Inno Setup") ? ("Inno Setup", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-") : Has("Nullsoft") || Has("NSIS") ? ("NSIS", "/S") : Has("InstallShield") ? ("InstallShield", "/s /v\"/qn /norestart\"") : Has("WixBundle") || Has("Burn") ? ("WiX / Burn", "/quiet /norestart") : ("Unknown", "");
    }
}
public static class MsiMetadata
{
    public static bool PopulateProductCode(Package package, string path)
    {
        if (!Path.GetExtension(path).Equals(".msi", StringComparison.OrdinalIgnoreCase))
            return false;
        var code = ProductCode(path);
        // Replaced media must never retain an unrelated ProductCode if the new MSI cannot be read.
        package.ProductCode = Guid.TryParse(code, out _) ? code : "";
        return package.ProductCode.Length > 0;
    }

    public static string ProductCode(string path)
    {
        uint database = 0, view = 0, record = 0;
        try
        {
            if (MsiOpenDatabase(path, IntPtr.Zero, out database) != 0)
                return "";
            if (MsiDatabaseOpenView(database, "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductCode'", out view) != 0 || MsiViewExecute(view, 0) != 0 || MsiViewFetch(view, out record) != 0)
                return "";
            var buffer = new System.Text.StringBuilder(256);
            uint length = 255;
            return MsiRecordGetString(record, 1, buffer, ref length) == 0 ? buffer.ToString() : "";
        }
        finally { if (record != 0) MsiCloseHandle(record); if (view != 0) MsiCloseHandle(view); if (database != 0) MsiCloseHandle(database); }
    }
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll")] static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiRecordGetString(uint record, uint field, System.Text.StringBuilder value, ref uint length);
    [DllImport("msi.dll")] static extern uint MsiCloseHandle(uint handle);
}
