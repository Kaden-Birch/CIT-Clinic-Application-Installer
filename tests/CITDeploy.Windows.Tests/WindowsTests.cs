using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CITDeploy.Core;
using CITDeploy.Windows;
using Xunit;
namespace CITDeploy.Windows.Tests;
public sealed class WindowsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankDomainSkipsNativePreflightAndJoin(string domain)
    {
        var service = new DomainService();
        using var password = new System.Security.SecureString();
        await service.Preflight(domain);
        // An invalid new name and empty credentials must never reach validation or native APIs.
        await service.Join(new Clinic { DomainFqdn = domain }, "invalid/name", "", password);
    }
    [Fact]
    public void UnreadableMsiClearsStaleCodeAndOtherMediaKeepsManualCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "cit-metadata-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var package = new Package { ProductCode = "existing manual code" };
            Assert.False(MsiMetadata.PopulateProductCode(package, Path.Combine(root, "setup.exe")));
            Assert.Equal("existing manual code", package.ProductCode);
            var invalidMsi = Path.Combine(root, "invalid.msi");
            File.WriteAllText(invalidMsi, "not an MSI database");
            Assert.False(MsiMetadata.PopulateProductCode(package, invalidMsi));
            Assert.Empty(package.ProductCode);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void EmbeddedMsiIsValidatedWithoutExecutingWrapper()
    {
        var root = Path.Combine(Path.GetTempPath(), "cit-msi-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var msi = Path.Combine(root, "fixture.msi");
            Assert.Equal(0u, MsiOpenDatabase(msi, new IntPtr(3), out var database));
            try
            {
                foreach (var sql in new[] { "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)", "INSERT INTO `Property` (`Property`,`Value`) VALUES ('ProductCode','{11111111-1111-1111-1111-111111111111}')" })
                {
                    Assert.Equal(0u, MsiDatabaseOpenView(database, sql, out var view));
                    try
                    {
                        Assert.Equal(0u, MsiViewExecute(view, 0));
                    }
                    finally { MsiCloseHandle(view); }
                }
                Assert.Equal(0u, MsiDatabaseCommit(database));
            }
            finally { MsiCloseHandle(database); }
            Assert.Equal("{11111111-1111-1111-1111-111111111111}", MsiMetadata.ProductCode(msi));
            var storage = new PortableStorage(Path.Combine(root, "kit"));
            var package = new Package { ProductCode = "{22222222-2222-2222-2222-222222222222}" };
            var imported = storage.Import(msi, "Software/Universal", msi);
            Assert.True(MsiMetadata.PopulateProductCode(package, storage.Resolve(imported)));
            Assert.Equal(MsiMetadata.ProductCode(msi), package.ProductCode);
            var folder = Path.Combine(root, "media");
            Directory.CreateDirectory(folder);
            var folderMsi = Path.Combine(folder, "SETUP.MSI");
            File.Copy(msi, folderMsi);
            package.ProductCode = "old code";
            imported = storage.Import(folder, "Software/Universal", folderMsi);
            Assert.True(MsiMetadata.PopulateProductCode(package, storage.Resolve(imported)));
            Assert.Equal(MsiMetadata.ProductCode(msi), package.ProductCode);
            var wrapper = Path.Combine(root, "wrapper.exe");
            using (var output = File.Create(wrapper))
            {
                output.Write(new byte[1024]);
                output.Write(File.ReadAllBytes(msi));
            }
            var candidate = InstallerAnalyzer.TryExtractEmbeddedMsi(wrapper, root);
            Assert.NotNull(candidate);
            Assert.Equal(MsiMetadata.ProductCode(msi), MsiMetadata.ProductCode(candidate));
            File.WriteAllBytes(wrapper, [0, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0]);
            Assert.Null(InstallerAnalyzer.TryExtractEmbeddedMsi(wrapper, root));
        }
        finally { Directory.Delete(root, true); }
    }
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [System.Runtime.InteropServices.DllImport("msi.dll")] static extern uint MsiViewExecute(uint view, uint record);
    [System.Runtime.InteropServices.DllImport("msi.dll")] static extern uint MsiDatabaseCommit(uint database);
    [System.Runtime.InteropServices.DllImport("msi.dll")] static extern uint MsiCloseHandle(uint handle);
    [Fact]
    public async Task CommandAndLocalDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "cit-windows-" + Guid.NewGuid());
        try
        {
            var storage = new PortableStorage(root);
            var runner = new WindowsRunner(storage, _ => Task.FromResult(TimeoutChoice.Terminate));
            var package = new Package { EntrypointRelativePath = "Software/Universal/setup.msi" };
            Assert.Contains("/qn /norestart", runner.Command(package, false, false).Arguments);
            Assert.DoesNotContain("/qn", runner.Command(package, true, false).Arguments);
            package.ProductCode = "{11111111-1111-1111-1111-111111111111}";
            Assert.StartsWith("/x", runner.Command(package, false, true).Arguments);
            var split = WindowsRunner.SplitCommand("\"C:\\Program Files\\Vendor\\remove.exe\" /quiet");
            Assert.Equal(@"C:\Program Files\Vendor\remove.exe", split.File);
            Assert.Equal("/quiet", split.Args);
            var detector = new WindowsDetector(storage);
            await Assert.ThrowsAsync<InvalidOperationException>(() => detector.Installed(new() { DetectionType = DetectionType.FileExists }));
            var marker = Path.Combine(root, "installed.txt");
            File.WriteAllText(marker, "test");
            Assert.True(await detector.Installed(new()
            {
                DetectionType = DetectionType.FileExists,
                DetectionValue = marker
            }));
            // This fixture exits immediately; it does not install or change any system configuration.
            var script = Path.Combine(root, "fixture.cmd");
            File.WriteAllText(script, "@echo off\r\nexit /b 42\r\n");
            Assert.Equal(42, (await runner.Launch(script, "", false, 10)).ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void WpfShellRenders()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cit-ui-" + Guid.NewGuid());
            try
            {
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                var session = new SessionViewModel(root);
                session.Catalog.Clinics.Add(new()
                {
                    Id = 1,
                    Name = "Test Clinic",
                    DomainFqdn = "clinic.test",
                    ComputerPrefix = "CL-WS",
                    SyncroRelativePath = "Syncro/agent.exe"
                });
                session.Catalog.Packages.Add(new()
                {
                    Id = 1,
                    Name = "Clinical Viewer",
                    GeneralKbUrl = "https://example.com/guide"
                });
                session.Catalog.Packages.Add(new()
                {
                    Id = 2,
                    Name = "Office Suite"
                });
                session.Catalog.Profiles.Add(new()
                {
                    Id = 1,
                    ClinicId = 1,
                    Name = "General workstation",
                    Software = new() { { 1, 10 }, { 2, 20 } }
                });
                session.Save("UI smoke fixture");
                var window = new MainWindow(session);
                window.Show();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.True(window.ActualWidth >= 900);
                Assert.True(window.IsVisible);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var screenshot = Environment.GetEnvironmentVariable("CIT_UI_SCREENSHOT");
                if (!string.IsNullOrEmpty(screenshot))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);
                    using var output = File.Create(screenshot);
                    encoder.Save(output);
                }
                window.Close();
                app.Dispatcher.InvokeShutdown();
            }
            catch (Exception ex) { failure = ex; }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF smoke test timed out.");
        Assert.Null(failure);
    }
}
