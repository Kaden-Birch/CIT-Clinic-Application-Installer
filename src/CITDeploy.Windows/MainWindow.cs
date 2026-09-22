using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using CITDeploy.Core;
namespace CITDeploy.Windows;
public sealed class MainWindow : Window
{
    readonly SessionViewModel session;
    readonly TabControl tabs = new(); readonly ComboBox clinics = new(), profiles = new(); readonly TextBox computer = new(); readonly StackPanel software = new(), inherited = new(); readonly Button start = new() { Content = "Start deployment", Background = new SolidColorBrush(Color.FromRgb(22, 125, 146)), Foreground = Brushes.White }; readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    readonly ObservableCollection<StepViewModel> steps = []; readonly ListBox clinicList = new(), profileList = new(), packageList = new(); readonly Dictionary<int, CheckBox> checks = []; bool busy; string? logFile;
    Catalog Data => session.Catalog;
    public MainWindow(SessionViewModel? model = null)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        session = model ?? new();
        Title = "CIT Deploy · Portable workstation provisioning";
        Width = 1180;
        Height = 850;
        MinWidth = 900;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var shell = new DockPanel { Margin = new Thickness(24) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        heading.Children.Add(new TextBlock { Text = "CIT Deploy", FontSize = 30, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "WORKSTATION PROVISIONING  /  USB WORKSPACE", Foreground = Brushes.SlateGray, Margin = new Thickness(0, 6, 0, 0) });
        DockPanel.SetDock(heading, Dock.Top);
        shell.Children.Add(heading);
        shell.Children.Add(tabs);
        Content = shell;
        BuildDeploy();
        BuildManage("Manage clinics", clinicList, () => EditClinic(null), () => EditClinic(clinicList.SelectedItem as Clinic), () => DeleteClinic());
        BuildManage("Manage profiles", profileList, () => EditProfile(null), () => EditProfile(profileList.SelectedItem as Profile), DeleteProfile, CloneProfile);
        BuildManage("Software library", packageList, () => EditPackage(null), () => EditPackage(packageList.SelectedItem as Package), DeletePackage);
        var settings = new StackPanel { Margin = new Thickness(24) };
        settings.Children.Add(new TextBlock { Text = "Portable workspace", FontSize = 22 });
        settings.Children.Add(new TextBlock { Text = session.Storage.Root, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        settings.Children.Add(new TextBlock { Text = "CIT Deploy 1.0 • .NET 8 • SQLite schema 1\nAll configuration, media, backups and logs stay in this folder.\nCopy the complete release folder to USB. No runtime installation is required.", TextWrapping = TextWrapping.Wrap });
        settings.Children.Add(Button("Back up database now", () => { session.Repository.Backup(); Info("Database backup created on the USB."); }));
        settings.Children.Add(Button("Open logs", () => Process.Start(new ProcessStartInfo("explorer.exe", WindowsRunner.Quote(Path.Combine(session.Storage.Root, "Logs"))))));
        tabs.Items.Add(new TabItem { Header = "Settings / About", Content = settings });
        clinics.SelectionChanged += (_, _) => { profiles.ItemsSource = Data.Profiles.Where(p => p.IsActive && p.ClinicId == (clinics.SelectedItem as Clinic)?.Id).ToList(); profiles.SelectedIndex = 0; computer.Text = (clinics.SelectedItem as Clinic)?.ComputerPrefix ?? ""; ShowSelection(); };
        profiles.SelectionChanged += (_, _) => ShowSelection();
        start.Click += async (_, _) => await GuardAsync(Deploy);
        Closing += (_, e) => { if (busy) { e.Cancel = true; Info("Finish or abort the current operation before closing CIT Deploy."); } };
        Closed += (_, _) => session.Dispose();
        Refresh();
    }
    static T Copy<T>(T model) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(model))!;
    static int Next(IEnumerable<int> ids) => ids.DefaultIfEmpty(0).Max() + 1;
    void Info(string text) => MessageBox.Show(this, text, "CIT Deploy");
    bool Confirm(string text) => MessageBox.Show(this, text, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    Button Button(string text, Action action)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => Guard(action);
        return b;
    }
    void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) { Info(ex.Message); Refresh(); }
    }
    async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) { Info(ex.Message); }
    }
    void Refresh()
    {
        var id = (clinics.SelectedItem as Clinic)?.Id;
        clinics.ItemsSource = Data.Clinics.Where(c => c.IsActive).ToList();
        clinics.SelectedItem = Data.Clinics.FirstOrDefault(c => c.Id == id);
        if (clinics.SelectedItem == null)
            clinics.SelectedIndex = 0;
        clinicList.ItemsSource = Data.Clinics.ToList();
        profileList.ItemsSource = Data.Profiles.Select(p => p).ToList();
        packageList.ItemsSource = Data.Packages.ToList();
        ShowSelection();
    }
    void BuildDeploy()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(370)
        });
        root.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(24)
        });
        root.ColumnDefinitions.Add(new());
        var left = new StackPanel();
        foreach (var (label, control) in new (string, UIElement)[] { ("Clinic", clinics), ("Workstation profile", profiles), ("Computer name", computer) })
        {
            left.Children.Add(new TextBlock { Text = label });
            left.Children.Add(control);
        }
        left.Children.Add(new TextBlock { Text = "Applications", FontSize = 20, Margin = new Thickness(0, 12, 0, 8) });
        left.Children.Add(software);
        left.Children.Add(new TextBlock { Text = "Automatic / inherited", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 8) });
        left.Children.Add(inherited);
        left.Children.Add(start);
        root.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var right = new DockPanel();
        Grid.SetColumn(right, 2);
        right.Children.Add(new TextBlock { Text = "Deployment activity", FontSize = 22, Margin = new Thickness(0, 0, 0, 12) });
        DockPanel.SetDock(right.Children[0], Dock.Top);
        DockPanel.SetDock(summary, Dock.Bottom);
        right.Children.Add(summary);
        var wrappedText = new Style(typeof(TextBlock));
        wrappedText.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        var grid = new DataGrid { ItemsSource = steps, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, MinRowHeight = 50 };
        grid.Columns.Add(new DataGridTextColumn { Header = "Step", Binding = new System.Windows.Data.Binding("Name"), Width = 160, MinWidth = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new System.Windows.Data.Binding("State"), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Result", MinWidth = 200, ElementStyle = wrappedText, Binding = new System.Windows.Data.Binding("Detail"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        right.Children.Add(grid);
        root.Children.Add(right);
        tabs.Items.Add(new TabItem { Header = "Deploy", Content = root });
    }
    void ShowSelection()
    {
        software.Children.Clear();
        inherited.Children.Clear();
        checks.Clear();
        var clinic = clinics.SelectedItem as Clinic;
        var profile = profiles.SelectedItem as Profile;
        if (clinic == null)
        {
            software.Children.Add(new TextBlock { Text = "Create a clinic and profile to begin.", TextWrapping = TextWrapping.Wrap });
            return;
        }
        computer.IsEnabled = !busy && Rules.HasDomain(clinic);
        if (!Rules.HasDomain(clinic))
            computer.Text = Environment.MachineName;
        if (profile != null)
            foreach (var p in Data.Packages.Where(p => Rules.Available(p, clinic.Id)).OrderBy(p => p.Name))
            {
                var row = new StackPanel();
                var cb = new CheckBox { Content = p.Name, IsChecked = profile.Software.ContainsKey(p.Id) };
                checks[p.Id] = cb;
                row.Children.Add(cb);
                AddLinks(row, p, clinic.Id);
                software.Children.Add(row);
            }
        inherited.Children.Add(new TextBlock { Text = $"Syncro: {(!Rules.HasSyncro(clinic) ? "Not configured — will be skipped" : Path.GetFileName(clinic.SyncroRelativePath))}\nDomain: {(Rules.HasDomain(clinic) ? clinic.DomainFqdn : "Not configured — join will be skipped")}\nOU: {(string.IsNullOrEmpty(clinic.OuPath) ? "Default computer container" : clinic.OuPath)}", TextWrapping = TextWrapping.Wrap });
    }
    string Site(Package p, int clinic) => Data.SiteLinks.FirstOrDefault(l => l.ClinicId == clinic && l.SoftwareId == p.Id)?.Url ?? "";
    void AddLinks(Panel panel, Package p, int clinic)
    {
        var links = new WrapPanel();
        if (Rules.HasLink(p.GeneralKbUrl))
            links.Children.Add(Button("Installation Guide", () => TechnicianDialogs.Open(p.GeneralKbUrl)));
        var site = Site(p, clinic);
        if (Rules.HasLink(site))
            links.Children.Add(Button("Site Instructions", () => TechnicianDialogs.Open(site)));
        panel.Children.Add(links);
    }
    void BuildManage(string title, ListBox list, Action add, Action edit, Action delete, Action? clone = null)
    {
        var dock = new DockPanel { Margin = new Thickness(20) };
        var actions = new WrapPanel();
        actions.Children.Add(Button("Add", add));
        actions.Children.Add(Button("Edit selected", edit));
        if (clone != null)
            actions.Children.Add(Button("Clone selected", clone));
        actions.Children.Add(Button("Delete selected", delete));
        DockPanel.SetDock(actions, Dock.Top);
        dock.Children.Add(actions);
        list.FontSize = 17;
        list.Padding = new Thickness(12);
        list.MouseDoubleClick += (_, _) => Guard(edit);
        dock.Children.Add(list);
        tabs.Items.Add(new TabItem { Header = title, Content = dock });
    }
    void EditClinic(Clinic? source)
    {
        var c = source == null ? new Clinic { Id = Next(Data.Clinics.Select(x => x.Id)) } : Copy(source);
        var e = new Editor(source == null ? "Add clinic" : "Edit clinic", c) { Owner = this };
        e.Field("Friendly name", nameof(c.Name));
        e.Field("Active Directory domain FQDN (optional)", nameof(c.DomainFqdn));
        e.Field("Target OU distinguished name (optional)", nameof(c.OuPath));
        e.Field("Computer naming prefix", nameof(c.ComputerPrefix));
        e.Field("Active", nameof(c.IsActive));
        var media = e.Text("Optional Syncro installer (USB-relative)", c.SyncroRelativePath);
        media.IsReadOnly = true;
        e.Action("Import / replace Syncro installer", () => { var pick = PickFile(); if (pick != null) media.Text = session.Storage.Import(pick, "Syncro", pick); });
        e.Action("Remove Syncro mapping", () => { media.Clear(); c.SyncroArguments = ""; e.RefreshFields(); });
        e.Field("Syncro silent arguments", nameof(c.SyncroArguments));
        e.Note("Every profile inherits this clinic’s domain and naming prefix. Syncro is installed only when an installer is configured. Leave the domain blank to skip domain checks, credentials, joining and renaming.");
        var links = new Dictionary<int, TextBox>();
        foreach (var p in Data.Packages.Where(p => Rules.Available(p, c.Id)))
        {
            var url = e.Text(p.Name + " — Site Knowledge Base HTTPS URL", Site(p, c.Id));
            links[p.Id] = url;
            e.Action("Test " + p.Name + " site link", () => TechnicianDialogs.Open(url.Text));
        }
        e.Save(() => { c.DomainFqdn = c.DomainFqdn.Trim(); c.SyncroRelativePath = media.Text.Trim(); if (source != null) Data.Clinics.RemoveAll(x => x.Id == source.Id); Data.Clinics.Add(c); Data.SiteLinks.RemoveAll(l => l.ClinicId == c.Id); foreach (var pair in links.Where(x => Rules.HasLink(x.Value.Text))) Data.SiteLinks.Add(new(c.Id, pair.Key, pair.Value.Text)); c.UpdatedUtc = DateTime.UtcNow; session.Save("Saved clinic " + c.Name); });
        e.ShowDialog();
        Refresh();
    }
    void EditProfile(Profile? source)
    {
        if (Data.Clinics.Count == 0)
        {
            Info("Create a clinic first.");
            return;
        }
        var p = source == null ? new Profile { Id = Next(Data.Profiles.Select(x => x.Id)), ClinicId = Data.Clinics[0].Id } : Copy(source);
        var e = new Editor(source == null ? "Add profile" : "Edit profile", p) { Owner = this };
        e.Field("Profile name", nameof(p.Name));
        e.Field("Active", nameof(p.IsActive));
        var clinic = e.Choice("Clinic", Data.Clinics, Data.Clinics.Single(c => c.Id == p.ClinicId));
        clinic.IsEnabled = source == null;
        var rows = new StackPanel();
        var options = new Dictionary<int, (CheckBox, TextBox)>();
        void Update()
        {
            rows.Children.Clear();
            options.Clear();
            var c = (Clinic)clinic.SelectedItem;
            p.ClinicId = c.Id;
            rows.Children.Add(new TextBlock { Text = $"Inherited Syncro: {(Rules.HasSyncro(c) ? c.SyncroRelativePath : "Not configured — will be skipped")}\nDomain: {(Rules.HasDomain(c) ? c.DomainFqdn : "Not configured — join will be skipped")}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
            foreach (var package in Data.Packages.Where(x => Rules.Available(x, c.Id)))
            {
                var cb = new CheckBox { Content = package.Name, IsChecked = p.Software.ContainsKey(package.Id) };
                var order = new TextBox { Text = p.Software.GetValueOrDefault(package.Id, package.DefaultInstallOrder).ToString(), Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
                rows.Children.Add(cb);
                rows.Children.Add(new TextBlock { Text = "Install order (dependencies take priority)" });
                rows.Children.Add(order);
                AddLinks(rows, package, c.Id);
                options[package.Id] = (cb, order);
            }
        }
        clinic.SelectionChanged += (_, _) => Update();
        Update();
        e.Add(rows);
        e.Save(() => { p.Software = options.Where(x => x.Value.Item1.IsChecked == true).ToDictionary(x => x.Key, x => int.Parse(x.Value.Item2.Text)); if (source != null) Data.Profiles.RemoveAll(x => x.Id == source.Id); Data.Profiles.Add(p); p.UpdatedUtc = DateTime.UtcNow; session.Save("Saved profile " + p.Name); });
        e.ShowDialog();
        Refresh();
    }
    void CloneProfile()
    {
        if (profileList.SelectedItem is not Profile p)
            return;
        var copy = Copy(p);
        copy.Id = Next(Data.Profiles.Select(x => x.Id));
        copy.Name += " copy";
        Data.Profiles.Add(copy);
        try
        {
            EditProfile(copy);
        }
        finally { if (!session.Repository.Load().Profiles.Any(x => x.Id == copy.Id)) Data.Profiles.Remove(copy); Refresh(); }
    }
    void DeleteProfile()
    {
        if (profileList.SelectedItem is Profile p && Confirm("Delete profile " + p.Name + "?"))
        {
            Data.Profiles.Remove(p);
            session.Save("Deleted profile " + p.Name);
            Refresh();
        }
    }
    void DeleteClinic()
    {
        if (clinicList.SelectedItem is not Clinic c)
            return;
        if (Data.Profiles.Any(p => p.ClinicId == c.Id) || Data.Packages.Any(p => p.ClinicId == c.Id))
            throw new InvalidOperationException("Delete this clinic’s profiles and clinic-specific software first.");
        if (!Confirm("Delete clinic " + c.Name + "?"))
            return;
        Data.SiteLinks.RemoveAll(l => l.ClinicId == c.Id);
        Data.Clinics.Remove(c);
        session.Save("Deleted clinic " + c.Name);
        Refresh();
    }
    void DeletePackage()
    {
        if (packageList.SelectedItem is not Package p)
            return;
        if (Data.Packages.Any(x => x.Dependencies.Contains(p.Id)))
            throw new InvalidOperationException("Remove this package from its dependents before deleting it.");
        if (!Confirm($"Delete {p.Name}? This removes it from {Data.Profiles.Count(x => x.Software.ContainsKey(p.Id))} profiles. A database backup will be created. Media will be retained."))
            return;
        foreach (var profile in Data.Profiles)
            profile.Software.Remove(p.Id);
        Data.SiteLinks.RemoveAll(l => l.SoftwareId == p.Id);
        Data.Packages.Remove(p);
        session.Save("Deleted software " + p.Name);
        Refresh();
    }
    static string? PickFile()
    {
        var d = new OpenFileDialog { Filter = "Installer media|*.exe;*.msi;*.ps1;*.cmd;*.bat|All files|*.*" };
        return d.ShowDialog() == true ? d.FileName : null;
    }
    void EditPackage(Package? source)
    {
        var p = source == null ? new Package { Id = Next(Data.Packages.Select(x => x.Id)) } : Copy(source);
        var e = new Editor(source == null ? "Add software" : "Edit software", p) { Owner = this };
        e.Field("Friendly name", nameof(p.Name));
        var scopes = new List<object> { "Universal" };
        scopes.AddRange(Data.Clinics);
        var scope = e.Choice("Available to", scopes, p.ClinicId.HasValue ? Data.Clinics.Single(c => c.Id == p.ClinicId) : scopes[0]);
        scope.IsEnabled = source == null;
        var media = e.Text("Registered entrypoint (USB-relative)", p.EntrypointRelativePath);
        media.IsReadOnly = true;
        void Import(bool folder)
        {
            string? sourcePath, entry;
            if (folder)
            {
                var dialog = new OpenFolderDialog { Title = "Select complete installer folder" };
                if (dialog.ShowDialog() != true)
                    return;
                sourcePath = dialog.FolderName;
                var fd = new OpenFileDialog { InitialDirectory = sourcePath, Title = "Select entrypoint inside the installer folder", Filter = "Entrypoints|*.exe;*.msi;*.ps1;*.cmd;*.bat" };
                if (fd.ShowDialog() != true)
                    return;
                entry = fd.FileName;
            }
            else
            {
                sourcePath = PickFile();
                entry = sourcePath;
            }
            if (sourcePath == null || entry == null)
                return;
            p.ClinicId = (scope.SelectedItem as Clinic)?.Id;
            media.Text = session.Storage.Import(sourcePath, p.ClinicId == null ? "Software/Universal" : "Software/Clinics/" + p.ClinicId, entry);
            p.EntrypointRelativePath = media.Text;
            var category = p.ClinicId == null ? "Software/Universal" : "Software/Clinics/" + p.ClinicId;
            var remainder = Path.GetRelativePath(category, media.Text);
            p.RelativeFolder = Path.Combine(category, remainder.Split(Path.DirectorySeparatorChar)[0]);
            scope.IsEnabled = false;
        }
        e.Action("Import installer file", () => Import(false));
        e.Action("Import complete folder + select entrypoint", () => Import(true));
        foreach (var (label, property) in new[] { ("Installation mode", nameof(p.InstallMode)), ("Silent arguments (MSI defaults to /qn /norestart)", nameof(p.SilentArguments)), ("Interactive arguments", nameof(p.InteractiveArguments)), ("Timeout in seconds", nameof(p.TimeoutSeconds)), ("Default install order", nameof(p.DefaultInstallOrder)), ("Critical package", nameof(p.IsCritical)), ("Active", nameof(p.IsActive)), ("Detection rule", nameof(p.DetectionType)), ("Detection value", nameof(p.DetectionValue)), ("Detection script USB-relative path", nameof(p.DetectionScriptPath)), ("Pre-install script USB-relative path", nameof(p.PreInstallScript)), ("Post-install script USB-relative path", nameof(p.PostInstallScript)), ("MSI ProductCode", nameof(p.ProductCode)), ("General Knowledge Base HTTPS URL", nameof(p.GeneralKbUrl)) })
            e.Field(label, property);
        var successCodes = e.Text("Success exit codes (comma-separated; 3010 always requires reboot)", string.Join(",", p.SuccessCodes));
        e.Note("Detection: MSI ProductCode, uninstall display-name substring, file/folder absolute target path, HKLM\\key|valueName|expectedValue, or a bundled PowerShell script (exit 0 present / 1 absent). Script paths must be inside this package’s imported folder. No detection means production installs run each time; tests cannot pass without verification.");
        e.Action("Test Installation Guide link", () => TechnicianDialogs.Open(p.GeneralKbUrl));
        e.Action("Analyze installer", () => { p.EntrypointRelativePath = media.Text; var (framework, args) = InstallerAnalyzer.Analyze(session.Storage.Resolve(media.Text)); p.InstallerFramework = framework; p.SuggestedSilentArgs = args; e.RefreshFields(); Info($"Framework: {framework}\nSuggested arguments: {args}\nSuggestions are unverified. Enter or adjust arguments before testing."); var embedded = InstallerAnalyzer.TryExtractEmbeddedMsi(session.Storage.Resolve(media.Text), session.Storage.Resolve(p.RelativeFolder)); if (embedded != null) Info("A locally embedded MSI was found and validated as an MSI database. You may choose it in the next dialog after reviewing wrapper prerequisites."); var msi = Directory.GetFiles(session.Storage.Resolve(p.RelativeFolder), "*.msi", SearchOption.AllDirectories); if (msi.Length > 0) { var candidates = new Editor("Optional MSI entrypoint", new object()) { Owner = e, Height = 450 }; candidates.Note("These MSI files are already inside the imported media. Selecting one registers it as the entrypoint; it may bypass wrapper prerequisites, so test it before deployment."); foreach (var candidate in msi) candidates.Action(Path.GetRelativePath(session.Storage.Root, candidate), () => { media.Text = Path.GetRelativePath(session.Storage.Root, candidate); p.EntrypointRelativePath = media.Text; p.ProductCode = MsiMetadata.ProductCode(candidate); e.RefreshFields(); candidates.Close(); }); candidates.ShowDialog(); } if (Path.GetExtension(media.Text).Equals(".msi", StringComparison.OrdinalIgnoreCase)) { p.ProductCode = MsiMetadata.ProductCode(session.Storage.Resolve(media.Text)); e.RefreshFields(); } });
        foreach (var (label, property) in new[] { ("Framework (informational)", nameof(p.InstallerFramework)), ("Suggested silent arguments (unverified)", nameof(p.SuggestedSilentArgs)), ("Uninstall mode", nameof(p.UninstallMode)), ("Uninstall executable/registered command", nameof(p.UninstallCommand)), ("Silent uninstall arguments", nameof(p.UninstallArguments)), ("Discovered quiet uninstall command", nameof(p.QuietUninstallCommand)) })
            e.Field(label, property);
        e.Note("Do not enter credentials, tokens or other secrets in installer arguments. Use vendor-provided local enrollment media.\nAn uninstall command with spaces in its executable path must quote that path. MSI ProductCode takes priority over executable commands. Uninstall uses the package timeout.");
        var deps = new Dictionary<int, CheckBox>();
        e.Note("Prerequisites are inserted before this package. Cycles and cross-clinic dependencies are rejected on save.");
        foreach (var dep in Data.Packages.Where(x => x.Id != p.Id && x.IsActive))
        {
            var cb = new CheckBox { Content = dep.Name, IsChecked = p.Dependencies.Contains(dep.Id) };
            deps[dep.Id] = cb;
            e.Add(cb);
        }
        void Capture()
        {
            p.SuccessCodes = successCodes.Text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x.Trim())).Distinct().ToList();
            p.ClinicId = (scope.SelectedItem as Clinic)?.Id;
            p.EntrypointRelativePath = media.Text;
            p.Dependencies = deps.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToList();
            ValidatePackagePaths(p);
        }
        var testInstall = e.Action("Test install on this workstation", () => { });
        testInstall.Click += async (_, _) => await GuardAsync(async () => { Capture(); await TestPackage(p, false, e); e.RefreshFields(); });
        var testUninstall = e.Action("Test uninstall on this workstation", () => { });
        testUninstall.Click += async (_, _) => await GuardAsync(async () => { Capture(); await TestPackage(p, true, e); e.RefreshFields(); });
        e.Action("Discover Windows uninstall metadata", () => { var matches = WindowsDetector.Discover().Where(x => x.Name.Contains(string.IsNullOrWhiteSpace(p.DetectionValue) ? p.Name : p.DetectionValue, StringComparison.OrdinalIgnoreCase)).ToList(); var pick = new Editor("Select uninstall metadata", new object()) { Owner = e, Height = 500 }; if (matches.Count == 0) pick.Note("No matching uninstall registration was found."); foreach (var match in matches) pick.Action(match.Name, () => { if (MessageBox.Show(pick, $"Command: {match.Command}\nQuiet: {match.Quiet}\nProductCode: {match.ProductCode}\nUse this registration?", "Review uninstall metadata", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; p.UninstallCommand = match.Command; p.QuietUninstallCommand = match.Quiet; p.ProductCode = match.ProductCode; e.RefreshFields(); pick.Close(); }); pick.ShowDialog(); });
        e.Save(() => { Capture(); if (p.InstallMode == InstallMode.NotConfigured) throw new InvalidOperationException("Choose an installation mode."); if (source != null) Data.Packages.RemoveAll(x => x.Id == source.Id); Data.Packages.Add(p); p.UpdatedUtc = DateTime.UtcNow; session.Save("Saved software " + p.Name); });
        e.ShowDialog();
        Refresh();
    }
    void ValidatePackagePaths(Package p)
    {
        if (string.IsNullOrWhiteSpace(p.EntrypointRelativePath) || !File.Exists(session.Storage.Resolve(p.EntrypointRelativePath)))
            throw new InvalidOperationException("Import installer media first.");
        foreach (var script in new[] { p.DetectionScriptPath, p.PreInstallScript, p.PostInstallScript }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var full = session.Storage.Resolve(script);
            var folder = session.Storage.Resolve(p.RelativeFolder) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || !Path.GetExtension(full).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Scripts must be imported .ps1 files inside this package folder.");
        }
        if (p.DetectionType == DetectionType.PowerShell && string.IsNullOrWhiteSpace(p.DetectionScriptPath))
            throw new InvalidOperationException("Choose a bundled detection script.");
        Rules.Url(p.GeneralKbUrl);
    }
    WindowsRunner Runner(Window owner) => new(session.Storage, detail => Task.FromResult(MessageBox.Show(owner, detail + "\n\nYes: continue waiting. No: terminate the process tree (may corrupt installation).", "Installer timeout", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) == MessageBoxResult.Yes ? TimeoutChoice.Wait : TimeoutChoice.Terminate));
    async Task TestPackage(Package p, bool uninstall, Window owner)
    {
        if (busy)
            return;
        if (p.DetectionType == DetectionType.None)
            throw new InvalidOperationException("Configure detection before running a verification test.");
        if (MessageBox.Show(owner, $"This changes the current workstation. Use a controlled test PC/VM.\n\nCommand: {Runner(this).Command(p, (uninstall ? p.UninstallMode : p.InstallMode) == InstallMode.InteractiveOnly, uninstall)}\nMode: {(uninstall ? p.UninstallMode : p.InstallMode)}\n\nContinue?", "Test " + (uninstall ? "uninstall" : "install"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        busy = true;
        owner.IsEnabled = false;
        var path = Path.Combine(session.Storage.Root, "Logs", "test-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff") + ".log");
        void Log(string text) => File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + text + Environment.NewLine);
        try
        {
            var engine = new DeploymentEngine(Runner(this), new WindowsDetector(session.Storage), new TechnicianDialogs(this, _ => ""));
            var result = await engine.Execute(p, uninstall, true, Log);
            Log($"Final: {result.State}; verified: {result.Verified}");
            Info($"{p.Name}: {result.State}\n{result.Detail}\nLog: {path}");
            if (!uninstall && result.Verified)
            {
                var match = WindowsDetector.Discover().FirstOrDefault(x => x.Name.Contains(p.Name, StringComparison.OrdinalIgnoreCase));
                if (match != null && Confirm($"Discovered uninstall metadata for {match.Name}.\n{match.Command}\n{match.Quiet}\nUse this configuration?"))
                {
                    p.UninstallCommand = match.Command;
                    p.QuietUninstallCommand = match.Quiet;
                    p.ProductCode = match.ProductCode;
                }
            }
        }
        catch (Exception ex) { Log("Test incomplete: " + ex.Message); throw; }
        finally { busy = false; owner.IsEnabled = true; }
    }
    void Log(string text)
    {
        if (logFile != null)
            File.AppendAllText(logFile, $"{DateTime.UtcNow:O} {text}\n");
    }
    async Task Deploy()
    {
        if (busy)
            return;
        var clinic = clinics.SelectedItem as Clinic ?? throw new InvalidOperationException("Select a clinic.");
        var profile = profiles.SelectedItem as Profile ?? throw new InvalidOperationException("Select a profile.");
        var name = Rules.HasDomain(clinic) ? computer.Text.Trim() : Environment.MachineName;
        if (Rules.HasDomain(clinic))
            Rules.ComputerName(name);
        using (var identity = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Restart CIT Deploy as administrator.");
        var plan = Rules.Plan(Data, clinic.Id, checks.Where(x => x.Value.IsChecked == true).Select(x => x.Key), profile.Software);
        var missing = session.Storage.Missing(plan, clinic);
        if (missing.Count > 0)
            throw new InvalidOperationException("Missing required media:\n" + string.Join("\n", missing));
        foreach (var p in plan)
            ValidatePackagePaths(p);
        if (!Confirm($"Deploy {plan.Count} application(s), {(Rules.HasSyncro(clinic) ? "install the configured Syncro agent" : "skip Syncro (not configured)")}, and {(Rules.HasDomain(clinic) ? $"join {clinic.DomainFqdn} as {name}" : "skip domain join and keep the current computer name")}?"))
            return;
        var results = new Dictionary<int, StepResult>();
        var directory = Path.Combine(session.Storage.Root, "Logs", name);
        Directory.CreateDirectory(directory);
        logFile = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff") + ".log");
        Log($"Deployment started; clinic={clinic.Name}; profile={profile.Name}; computer={name}; applications={string.Join(", ", plan.Select(p => p.Name))}");
        var domain = new DomainService();
        var domainStep = new StepViewModel("Domain join + rename");
        bool reboot = false, joined = false;
        busy = true;
        start.IsEnabled = false;
        clinics.IsEnabled = profiles.IsEnabled = computer.IsEnabled = software.IsEnabled = false;
        foreach (TabItem tab in tabs.Items)
            if (tab != tabs.Items[0])
                tab.IsEnabled = false;
        steps.Clear();
        summary.Text = Rules.HasDomain(clinic) ? "Checking domain connectivity…" : "Preparing application deployment…";
        try
        {
            if (Rules.HasDomain(clinic))
            {
                await domain.Preflight(clinic.DomainFqdn);
                Log("Domain preflight passed");
            }
            var rows = plan.ToDictionary(p => p.Id, p => new StepViewModel(p.Name));
            foreach (var row in rows.Values)
                steps.Add(row);
            var syncRow = new StepViewModel("Syncro · " + clinic.Name);
            steps.Add(syncRow);
            steps.Add(domainStep);
            var engine = new DeploymentEngine(Runner(this), new WindowsDetector(session.Storage), new TechnicianDialogs(this, p => Site(p, clinic.Id)));
            foreach (var p in plan)
            {
                var row = rows[p.Id];
                if (p.Dependencies.Any(id => !results.TryGetValue(id, out var r) || r.State == StepState.Failed))
                {
                    row.State = StepState.Failed;
                    row.Detail = "Blocked by failed prerequisite";
                    results[p.Id] = new(p, StepState.Failed, null, row.Detail);
                    Log(p.Name + ": " + row.Detail);
                    if (p.IsCritical && !Confirm(p.Name + " is critical and blocked by a failed dependency. Explicitly override and continue?"))
                        throw new OperationCanceledException("Critical dependency blocked deployment.");
                    continue;
                }
                row.State = StepState.Running;
                summary.Text = "Installing " + p.Name;
                Log($"{p.Name}: entrypoint={p.EntrypointRelativePath}; general KB={Rules.HasLink(p.GeneralKbUrl)}; site KB={Rules.HasLink(Site(p, clinic.Id))}");
                var result = await engine.Execute(p, log: Log);
                results[p.Id] = result;
                row.State = result.State;
                row.Detail = result.Detail;
                reboot |= result.RebootRequired;
            }
            syncRow.State = StepState.Running;
            var syncResult = await engine.ExecuteSyncro(clinic, log: Log);
            syncRow.State = syncResult.State;
            syncRow.Detail = syncResult.Detail;
            reboot |= syncResult.RebootRequired;
            if (syncResult.State == StepState.Failed)
                throw new InvalidOperationException("Configured Syncro installation failed; domain join was not attempted.");
            if (Rules.HasDomain(clinic))
            {
                domainStep.State = StepState.Running;
                summary.Text = "Preparing domain join…";
                await domain.Preflight(clinic.DomainFqdn);
                var credentials = new Editor("Domain join credentials", new object()) { Owner = this, Height = 430 };
                credentials.Note($"Join {clinic.DomainFqdn} and rename to {name}. Credentials exist only in memory for this operation.");
                var user = credentials.Text("Authorized account (DOMAIN\\user or user@domain)");
                var password = new PasswordBox { Margin = new Thickness(0, 6, 0, 16), Padding = new Thickness(8) };
                credentials.Add(new TextBlock { Text = "Password" });
                credentials.Add(password);
                credentials.Action("Join domain", () => { if (string.IsNullOrWhiteSpace(user.Text) || password.SecurePassword.Length == 0) throw new InvalidOperationException("Enter account and password."); credentials.DialogResult = true; });
                if (credentials.ShowDialog() != true)
                {
                    password.Clear();
                    user.Clear();
                    throw new OperationCanceledException("Domain join cancelled.");
                }
                try
                {
                    using var secure = password.SecurePassword;
                    password.Clear();
                    await domain.Join(clinic, name, user.Text, secure);
                }
                finally { user.Clear(); password.Clear(); }
                joined = true;
                reboot = true;
                domainStep.State = StepState.Success;
                domainStep.Detail = "Joined and renamed; reboot required";
                Log("Domain join and rename succeeded");
            }
            else
            {
                domainStep.State = StepState.Skipped;
                domainStep.Detail = "No domain configured; join and rename skipped.";
                Log(domainStep.Detail);
            }
            var failures = results.Values.Count(r => r.State == StepState.Failed);
            summary.Text = $"{(failures == 0 ? "Deployment completed" : "Completed with explicitly overridden failures")}. {results.Values.Count(r => r.State is StepState.Success or StepState.RebootRequired)} installed, {results.Values.Count(r => r.State == StepState.Skipped)} skipped, {failures} failed. {(joined ? "Domain joined." : "Domain join skipped.")} {(reboot ? "Reboot required." : "No reboot required.")}";
        }
        catch (Exception ex)
        {
            if (ex is DeploymentAbortedException aborted)
                reboot |= aborted.RebootRequired;
            if (ex is DomainJoinPartialException)
            {
                reboot = true;
                joined = true;
            }
            foreach (var row in steps.Where(r => r.State == StepState.Running))
            {
                row.State = StepState.Failed;
                row.Detail = ex.Message;
            }
            summary.Text = "Deployment incomplete: " + ex.Message;
        }
        finally
        {
            busy = false;
            start.IsEnabled = true;
            clinics.IsEnabled = profiles.IsEnabled = software.IsEnabled = true;
            computer.IsEnabled = Rules.HasDomain(clinic);
            foreach (TabItem tab in tabs.Items)
                tab.IsEnabled = true;
            try
            {
                Log($"Final summary: {summary.Text}; domain joined={joined}; reboot required={reboot}; ended {DateTime.UtcNow:O}");
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
            {
                summary.Text += "\nCould not persist the final log: " + logError.Message;
            }
        }
        if (reboot && Confirm(summary.Text + "\n\nReboot this workstation now?"))
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0") { UseShellExecute = false });
    }
}
