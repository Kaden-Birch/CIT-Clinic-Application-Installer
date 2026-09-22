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
