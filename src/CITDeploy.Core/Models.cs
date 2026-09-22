namespace CITDeploy.Core;
public enum InstallMode
{
    Automatic, SilentOnly, InteractiveOnly, NotConfigured
}
public enum DetectionType
{
    None, MsiProductCode, DisplayName, FileExists, FolderExists, Registry, PowerShell
}
public enum StepState
{
    Pending, Running, Success, Skipped, RebootRequired, Failed
}
public enum Decision
{
    Retry, Interactive, Skip, Abort
}
public sealed class Clinic
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow; public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow; public int Id
    {
        get; set;
    }
    public string Name { get; set; } = ""; public string DomainFqdn { get; set; } = ""; public string OuPath { get; set; } = ""; public string ComputerPrefix { get; set; } = ""; public string SyncroRelativePath { get; set; } = ""; public string SyncroArguments { get; set; } = ""; public bool IsActive { get; set; } = true;
    public override string ToString() => Name;
}
public sealed class Profile
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow; public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow; public int Id
    {
        get; set;
    }
    public int ClinicId
    {
        get; set;
    }
    public string Name { get; set; } = ""; public bool IsActive { get; set; } = true; public Dictionary<int, int> Software { get; set; } = [];
    public override string ToString() => Name;
}
public sealed class Package
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow; public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow; public int Id
    {
        get; set;
    }
    public string Name { get; set; } = ""; public int? ClinicId
    {
        get; set;
    }
    public string RelativeFolder { get; set; } = ""; public string EntrypointRelativePath { get; set; } = ""; public InstallMode InstallMode { get; set; } = InstallMode.Automatic; public string SilentArguments { get; set; } = ""; public string InteractiveArguments { get; set; } = ""; public int TimeoutSeconds { get; set; } = 1800; public DetectionType DetectionType
    {
        get; set;
    }
    public string DetectionValue { get; set; } = ""; public string DetectionScriptPath { get; set; } = ""; public string PreInstallScript { get; set; } = ""; public string PostInstallScript { get; set; } = ""; public string InstallerFramework { get; set; } = "Unknown"; public string SuggestedSilentArgs { get; set; } = ""; public string GeneralKbUrl { get; set; } = ""; public InstallMode UninstallMode { get; set; } = InstallMode.NotConfigured; public string UninstallCommand { get; set; } = ""; public string UninstallArguments { get; set; } = ""; public string QuietUninstallCommand { get; set; } = ""; public string ProductCode { get; set; } = ""; public int DefaultInstallOrder { get; set; } = 100; public bool IsCritical { get; set; } = true; public bool IsActive { get; set; } = true; public List<int> Dependencies { get; set; } = []; public List<int> SuccessCodes { get; set; } = [0, 3010];
    public override string ToString() => Name;
}
public record SiteLink(int ClinicId, int SoftwareId, string Url);
public sealed class Catalog
{
    public List<Clinic> Clinics { get; set; } = []; public List<Profile> Profiles { get; set; } = []; public List<Package> Packages { get; set; } = []; public List<SiteLink> SiteLinks { get; set; } = [];
}
public record ExecutionResult(int? ExitCode, bool TimedOut, TimeSpan Duration, bool RebootRequired = false);
public record StepResult(Package Package, StepState State, int? ExitCode, string Detail, bool Verified = false, bool RebootRequired = false);
public static class Rules
{
    public static bool Available(Package p, int clinic) => p.IsActive && (p.ClinicId is null || p.ClinicId == clinic);
    public static bool HasLink(string? url) => !string.IsNullOrWhiteSpace(url);
    public static void Url(string url)
    {
        if (HasLink(url) && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !string.IsNullOrEmpty(u.UserInfo)))
            throw new InvalidOperationException("Knowledge-base links must be HTTPS URLs without credentials.");
    }
    public static void ComputerName(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^(?![0-9]+$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,13}[A-Za-z0-9])?$"))
            throw new InvalidOperationException("Computer name must be 1–15 letters, digits or hyphens, not all digits, and cannot begin/end with a hyphen.");
    }
    public static IReadOnlyList<Package> Plan(Catalog data, int clinic, IEnumerable<int> selected, IReadOnlyDictionary<int, int>? order = null)
    {
        var result = new List<Package>();
        var visiting = new List<int>();
        var done = new HashSet<int>();
        void Visit(int id)
        {
            var p = data.Packages.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException($"Missing package {id}.");
            if (!Available(p, clinic))
                throw new InvalidOperationException($"{p.Name} is inactive or belongs to another clinic.");
            if (visiting.Contains(id))
                throw new InvalidOperationException("Dependency cycle: " + string.Join(" → ", visiting.Append(id).Select(i => data.Packages.Single(p => p.Id == i).Name)));
            if (done.Contains(id))
                return;
            visiting.Add(id);
            foreach (var d in p.Dependencies)
            {
                var dep = data.Packages.SingleOrDefault(x => x.Id == d) ?? throw new InvalidOperationException("Missing dependency.");
                if (dep.ClinicId != null && dep.ClinicId != p.ClinicId)
                    throw new InvalidOperationException($"Invalid dependency scope for {p.Name}.");
                Visit(d);
            }
            visiting.RemoveAt(visiting.Count - 1);
            done.Add(id);
            result.Add(p);
        }
        foreach (var id in selected.Distinct().OrderBy(id => order != null && order.TryGetValue(id, out var n) ? n : data.Packages.Single(p => p.Id == id).DefaultInstallOrder).ThenBy(id => data.Packages.Single(p => p.Id == id).Name))
            Visit(id);
        return result;
    }
    public static void Validate(Catalog c)
    {
        if (c.Clinics.GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Clinic names must be unique.");
        foreach (var x in c.Clinics)
            if (string.IsNullOrWhiteSpace(x.Name) || Uri.CheckHostName(x.DomainFqdn) != UriHostNameType.Dns || !x.DomainFqdn.Contains('.'))
                throw new InvalidOperationException("Clinic name and domain FQDN are required.");
        if (c.Profiles.GroupBy(x => (x.ClinicId, x.Name.Trim().ToUpperInvariant())).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Profile names must be unique within a clinic.");
        foreach (var p in c.Packages)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || p.TimeoutSeconds < 1)
                throw new InvalidOperationException("Package name and positive timeout required.");
            Url(p.GeneralKbUrl);
            if (p.IsActive)
                Plan(c, p.ClinicId ?? -1, [p.Id]);
        }
        foreach (var p in c.Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || !c.Clinics.Any(x => x.Id == p.ClinicId))
                throw new InvalidOperationException("Profile clinic/name required.");
            foreach (var id in p.Software.Keys)
                if (!c.Packages.Any(x => x.Id == id && (x.ClinicId == null || x.ClinicId == p.ClinicId)))
                    throw new InvalidOperationException("Invalid profile software scope.");
        }
        foreach (var l in c.SiteLinks)
        {
            Url(l.Url);
            if (!c.Packages.Any(p => p.Id == l.SoftwareId && (p.ClinicId == null || p.ClinicId == l.ClinicId)))
                throw new InvalidOperationException("Invalid site link scope.");
        }
    }
}
