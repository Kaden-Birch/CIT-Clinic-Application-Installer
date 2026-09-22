namespace CITDeploy.Core;
public interface IInstallerRunner
{
    Task<ExecutionResult> Run(Package package, bool interactive, bool uninstall);
}
public interface IDetector
{
    Task<bool> Installed(Package package);
}
public interface ITechnician
{
    Task<Decision> Failure(Package package, string detail, bool allowInteractive); Task<bool> LaunchInteractive(Package package, bool uninstall);
}
public sealed class DeploymentEngine(IInstallerRunner runner, IDetector detector, ITechnician technician)
{
    public async Task<StepResult> Execute(Package p, bool uninstall = false, bool test = false, Action<string>? log = null)
    {
        var mode = uninstall ? p.UninstallMode : p.InstallMode;
        if (mode == InstallMode.NotConfigured)
            throw new InvalidOperationException("Uninstall is not configured.");
        if (!test && !uninstall && p.DetectionType != DetectionType.None && await detector.Installed(p))
            return new(p, StepState.Skipped, null, "Already detected", true);
        var interactive = mode == InstallMode.InteractiveOnly;
        while (true)
        {
            if (interactive && !await technician.LaunchInteractive(p, uninstall))
                throw new OperationCanceledException("Interactive installation cancelled.");
            ExecutionResult? result = null;
            var verified = false;
            string detail;
            try
            {
                log?.Invoke($"{p.Name}: {(uninstall ? "uninstall" : "install")}, {(interactive ? "interactive" : "silent")}, started {DateTime.UtcNow:O}");
                result = await runner.Run(p, interactive, uninstall);
                var hasDetection = p.DetectionType != DetectionType.None;
                var installed = hasDetection && await detector.Installed(p);
                verified = hasDetection && (uninstall ? !installed : installed);
                var processOk = !result.TimedOut && result.ExitCode.HasValue && (p.SuccessCodes.Contains(result.ExitCode.Value) || result.ExitCode == 3010);
                var ok = processOk && (hasDetection ? verified : !test && !uninstall);
                detail = $"Exit: {result.ExitCode?.ToString() ?? "none"}; duration: {result.Duration.TotalSeconds:F1}s; detection: {(hasDetection ? (verified ? "passed" : "failed") : "not configured (unverified)")}";
                log?.Invoke($"{p.Name}: {detail}; ended {DateTime.UtcNow:O}");
                if (ok)
                    return new(p, result.ExitCode == 3010 ? StepState.RebootRequired : StepState.Success, result.ExitCode, detail, verified);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { detail = ex.Message; log?.Invoke($"{p.Name}: {detail}"); }
            var choice = await technician.Failure(p, detail, mode == InstallMode.Automatic);
            log?.Invoke($"{p.Name}: technician selected {choice}");
            switch (choice)
            {
                case Decision.Retry:
                    interactive = mode == InstallMode.InteractiveOnly;
                    continue;
                case Decision.Interactive:
                    if (mode != InstallMode.Automatic)
                        throw new InvalidOperationException("Interactive fallback not permitted.");
                    interactive = true;
                    continue;
                case Decision.Skip:
                    return new(p, StepState.Failed, result?.ExitCode, "Explicit technician override: " + detail);
                default:
                    throw new OperationCanceledException($"Aborted at {p.Name}.");
            }
        }
    }
}
