using CITDeploy.Core;
using Xunit;
namespace CITDeploy.Tests;
public sealed class CoreTests
{
    [Fact]
    public void DependencyOrderingOverridesManualOrder()
    {
        var a = new Package { Id = 1, Name = "A" };
        var b = new Package { Id = 2, Name = "B", Dependencies = [1] };
        var c = new Catalog { Packages = [a, b] };
        Assert.Equal(new[] { 1, 2 }, Rules.Plan(c, 10, [2], new Dictionary<int, int> { { 2, 0 }, { 1, 99 } }).Select(x => x.Id));
    }
    [Fact]
    public void CycleIncludesNames()
    {
        var c = new Catalog { Packages = [new() { Id = 1, Name = "A", Dependencies = [2] }, new() { Id = 2, Name = "B", Dependencies = [1] }] };
        Assert.Contains("A → B → A", Assert.Throws<InvalidOperationException>(() => Rules.Plan(c, 1, [1])).Message);
    }
    [Fact]
    public void UniversalCannotDependOnClinicPackage()
    {
        var c = new Catalog { Packages = [new() { Id = 1, Dependencies = [2] }, new() { Id = 2, ClinicId = 7 }] };
        Assert.Throws<InvalidOperationException>(() => Rules.Plan(c, 7, [1]));
    }
    [Fact]
    public void InactiveDependenciesBlockPlan()
    {
        var c = new Catalog { Packages = [new() { Id = 1, Dependencies = [2] }, new() { Id = 2, IsActive = false }] };
        Assert.Throws<InvalidOperationException>(() => Rules.Plan(c, 7, [1]));
    }
    [Fact]
    public void AvailabilityAndKbScoping()
    {
        Assert.True(Rules.Available(new(), 8));
        Assert.False(Rules.Available(new()
        {
            ClinicId = 7
        }, 8));
        Assert.False(Rules.HasLink(" "));
        Rules.Url("https://example.com/guide");
        Assert.Throws<InvalidOperationException>(() => Rules.Url("file:///secret"));
        Assert.Throws<InvalidOperationException>(() => Rules.Url("https://user:secret@example.com"));
    }
    [Theory][InlineData("123")][InlineData("too-long-computer-name")][InlineData("-name")][InlineData("")][InlineData("a/b")] public void RejectBadNames(string name) => Assert.Throws<InvalidOperationException>(() => Rules.ComputerName(name));
    [Fact]
    public void TraversalRejectedAndRootCanMove()
    {
        using var temp = new Temp();
        var a = new PortableStorage(temp.Path + "/a");
        Assert.Throws<InvalidOperationException>(() => a.Resolve("../outside"));
        File.WriteAllText(temp.Path + "/setup.exe", "test");
        var relative = a.Import(temp.Path + "/setup.exe", "Software/Universal", temp.Path + "/setup.exe");
        Directory.Move(a.Root, temp.Path + "/b");
        var b = new PortableStorage(temp.Path + "/b");
        Assert.True(File.Exists(b.Resolve(relative)));
        Assert.False(System.IO.Path.IsPathRooted(relative));
    }
    [Fact]
    public void MissingMediaListsAll()
    {
        using var temp = new Temp();
        var s = new PortableStorage(temp.Path);
        Assert.Equal(3, s.Missing([new() { EntrypointRelativePath = "a.exe" }, new() { EntrypointRelativePath = "b.exe" }], new()
        {
            SyncroRelativePath = "sync.exe"
        }).Count);
    }
    [Fact]
    public void DatabaseRoundTripPreservesInheritanceAndBackup()
    {
        using var temp = new Temp();
        var s = new PortableStorage(temp.Path);
        using var repo = new CatalogRepository(s);
        var c = new Catalog { Clinics = [new() { Id = 1, Name = "Clinic", DomainFqdn = "clinic.test", SyncroRelativePath = "Syncro/agent.exe" }], Profiles = [new() { Id = 1, ClinicId = 1, Name = "Doctor" }, new() { Id = 2, ClinicId = 1, Name = "Reception" }], Packages = [new() { Id = 1, Name = "Universal" }], SiteLinks = [new(1, 1, "https://example.com/site")] };
        repo.Save(c, "test");
        var loaded = repo.Load();
        Assert.Equal(2, loaded.Profiles.Count);
        Assert.All(loaded.Profiles, p => Assert.Equal("Syncro/agent.exe", loaded.Clinics.Single(x => x.Id == p.ClinicId).SyncroRelativePath));
        Assert.Single(loaded.SiteLinks);
        Assert.NotEmpty(Directory.GetFiles(temp.Path + "/Backups"));
    }
    [Fact]
    public async Task AutomaticFailureFallsBackAndVerifies()
    {
        var runner = new FakeRunner([1603, 0]);
        var detection = new FakeDetection([false, true]);
        var tech = new FakeTech(Decision.Interactive);
        var e = new DeploymentEngine(runner, detection, tech);
        var r = await e.Execute(new()
        {
            DetectionType = DetectionType.FileExists
        }, test: true);
        Assert.Equal(StepState.Success, r.State);
        Assert.True(r.Verified);
        Assert.Equal(new[] { false, true }, runner.Interactive);
    }
    [Fact]
    public async Task SilentOnlyDoesNotOfferInteractive()
    {
        var t = new FakeTech(Decision.Skip);
        var r = await new DeploymentEngine(new FakeRunner([1603]), new FakeDetection([false]), t).Execute(new()
        {
            InstallMode = InstallMode.SilentOnly,
            DetectionType = DetectionType.FileExists
        }, test: true);
        Assert.False(t.AllowInteractive);
        Assert.Equal(StepState.Failed, r.State);
    }
    [Fact]
    public async Task ExitZeroWithoutDetectionIsNotPassedTest()
    {
        var r = await new DeploymentEngine(new FakeRunner([0]), new FakeDetection([]), new FakeTech(Decision.Skip)).Execute(new(), test: true);
        Assert.Equal(StepState.Failed, r.State);
        Assert.False(r.Verified);
    }
    [Fact]
    public async Task UninstallRequiresAbsence()
    {
        var p = new Package { UninstallMode = InstallMode.Automatic, DetectionType = DetectionType.FileExists };
        var e = new DeploymentEngine(new FakeRunner([0, 0]), new FakeDetection([true, false]), new FakeTech(Decision.Interactive));
        var r = await e.Execute(p, uninstall: true, test: true);
        Assert.True(r.Verified);
    }
    [Fact]
    public async Task RebootCodePreserved()
    {
        var e = new DeploymentEngine(new FakeRunner([3010]), new FakeDetection([true]), new FakeTech(Decision.Abort));
        Assert.Equal(StepState.RebootRequired, (await e.Execute(new()
        {
            DetectionType = DetectionType.FileExists
        }, test: true)).State);
    }
    [Fact]
    public async Task AlreadyPresentSkipsInstaller()
    {
        var runner = new FakeRunner([]);
        var e = new DeploymentEngine(runner, new FakeDetection([true]), new FakeTech(Decision.Abort));
        Assert.Equal(StepState.Skipped, (await e.Execute(new()
        {
            DetectionType = DetectionType.FileExists
        })).State);
        Assert.Empty(runner.Interactive);
    }
    [Fact]
    public void FailedSaveLeavesPreviousDatabaseIntact()
    {
        using var temp = new Temp();
        using var repo = new CatalogRepository(new(temp.Path));
        var c = new Catalog { Clinics = [new() { Id = 1, Name = "One", DomainFqdn = "one.test" }] };
        repo.Save(c, "initial");
        c.Clinics.Add(new()
        {
            Id = 2,
            Name = "One",
            DomainFqdn = "two.test"
        });
        Assert.Throws<InvalidOperationException>(() => repo.Save(c, "invalid"));
        Assert.Single(repo.Load().Clinics);
    }
    [Fact]
    public void WholeFolderImportPreservesSupportFiles()
    {
        using var temp = new Temp();
        Directory.CreateDirectory(temp.Path + "/source/sub");
        File.WriteAllText(temp.Path + "/source/sub/setup.exe", "installer");
        File.WriteAllText(temp.Path + "/source/payload.dat", "payload");
        var storage = new PortableStorage(temp.Path + "/kit");
        var entry = storage.Import(temp.Path + "/source", "Software/Universal", temp.Path + "/source/sub/setup.exe");
        var importedRoot = Directory.GetParent(Path.GetDirectoryName(storage.Resolve(entry))!)!.FullName;
        Assert.True(File.Exists(Path.Combine(importedRoot, "payload.dat")));
    }
    [Fact]
    public void EntrypointOutsideFolderIsRejected()
    {
        using var temp = new Temp();
        Directory.CreateDirectory(temp.Path + "/source");
        File.WriteAllText(temp.Path + "/outside.exe", "");
        var storage = new PortableStorage(temp.Path + "/kit");
        Assert.Throws<InvalidOperationException>(() => storage.Import(temp.Path + "/source", "Software/Universal", temp.Path + "/outside.exe"));
        Assert.Empty(Directory.GetDirectories(temp.Path + "/kit/Software/Universal"));
    }
    [Fact]
    public async Task InteractiveOnlyDoesNotTrySilent()
    {
        var runner = new FakeRunner([0]);
        await new DeploymentEngine(runner, new FakeDetection([true]), new FakeTech(Decision.Abort)).Execute(new()
        {
            InstallMode = InstallMode.InteractiveOnly,
            DetectionType = DetectionType.FileExists
        }, test: true);
        Assert.Equal(new[] { true }, runner.Interactive);
    }
    [Fact]
    public void RelationalSelectionsAndDependenciesSurviveReload()
    {
        using var temp = new Temp();
        using var repo = new CatalogRepository(new(temp.Path));
        var c = new Catalog { Clinics = [new() { Id = 1, Name = "One", DomainFqdn = "one.test" }], Profiles = [new() { Id = 1, Name = "Doctor", ClinicId = 1, Software = new() { { 2, 45 } } }], Packages = [new() { Id = 1, Name = "Prerequisite" }, new() { Id = 2, Name = "App", Dependencies = [1], SuccessCodes = [0, 3010, 42] }] };
        repo.Save(c, "seed");
        var loaded = repo.Load();
        Assert.Equal(45, loaded.Profiles[0].Software[2]);
        Assert.Equal(new[] { 1 }, loaded.Packages.Single(x => x.Id == 2).Dependencies);
        Assert.Contains(42, loaded.Packages.Single(x => x.Id == 2).SuccessCodes);
        Assert.Equal(new[] { 1, 2 }, Rules.Plan(loaded, 1, [2]).Select(x => x.Id));
    }
    sealed class FakeRunner(IEnumerable<int> codes) : IInstallerRunner
    {
        readonly Queue<int> values = new(codes); public List<bool> Interactive = []; public Task<ExecutionResult> Run(Package p, bool interactive, bool uninstall)
        {
            Interactive.Add(interactive);
            return Task.FromResult(new ExecutionResult(values.Dequeue(), false, TimeSpan.Zero));
        }
    }
    sealed class FakeDetection(IEnumerable<bool> states) : IDetector
    {
        readonly Queue<bool> values = new(states); public Task<bool> Installed(Package p) => Task.FromResult(values.Dequeue());
    }
    sealed class FakeTech(Decision choice) : ITechnician
    {
        public bool AllowInteractive; public Task<Decision> Failure(Package p, string detail, bool allowInteractive)
        {
            AllowInteractive = allowInteractive;
            return Task.FromResult(choice);
        }
        public Task<bool> LaunchInteractive(Package p, bool uninstall) => Task.FromResult(true);
    }
    sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cit-test-" + Guid.NewGuid()); public Temp() => Directory.CreateDirectory(Path); public void Dispose() => Directory.Delete(Path, true);
    }
}
