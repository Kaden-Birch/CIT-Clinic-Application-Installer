using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CITDeploy.Core;
namespace CITDeploy.Windows;
public sealed class Editor : Window
{
    readonly StackPanel fields = new() { Margin = new Thickness(24) };
    readonly object model;
    public Editor(string title, object model)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        this.model = model;
        Title = title;
        Width = 680;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    public void Note(string text) => fields.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12), Foreground = System.Windows.Media.Brushes.SlateGray });
    public void Field(string label, string property)
    {
        fields.Children.Add(new TextBlock { Text = label });
        var prop = model.GetType().GetProperty(property)!;
        FrameworkElement input;
        if (prop.PropertyType == typeof(bool))
            input = new CheckBox();
        else if (prop.PropertyType.IsEnum)
            input = new ComboBox { ItemsSource = Enum.GetValues(prop.PropertyType) };
        else
            input = new TextBox();
        var target = input is CheckBox ? System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty : input is ComboBox ? System.Windows.Controls.Primitives.Selector.SelectedItemProperty : TextBox.TextProperty;
        input.SetBinding(target, new Binding(property) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
        fields.Children.Add(input);
    }
    public void RefreshFields()
    {
        void Walk(DependencyObject node)
        {
            if (node is TextBox text)
                text.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
        Walk(fields);
    }
    public TextBox Text(string label, string value = "")
    {
        fields.Children.Add(new TextBlock { Text = label });
        var box = new TextBox { Text = value };
        fields.Children.Add(box);
        return box;
    }
    public ComboBox Choice(string label, System.Collections.IEnumerable values, object? selected = null)
    {
        fields.Children.Add(new TextBlock { Text = label });
        var box = new ComboBox { ItemsSource = values, SelectedItem = selected };
        fields.Children.Add(box);
        return box;
    }
    public void Add(UIElement element) => fields.Children.Add(element);
    public Button Action(string text, Action action)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "CIT Deploy", MessageBoxButton.OK, MessageBoxImage.Warning); } };
        fields.Children.Add(b);
        return b;
    }
    public void Save(Action save) => Action("Save changes", () => { if (HasErrors(fields)) throw new InvalidOperationException("Correct the highlighted invalid values."); save(); DialogResult = true; });
    static bool HasErrors(DependencyObject root)
    {
        if (Validation.GetHasError(root))
            return true;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (HasErrors(System.Windows.Media.VisualTreeHelper.GetChild(root, i)))
                return true;
        return false;
    }
}
public sealed class TechnicianDialogs(Window owner, Func<Package, string> siteUrl) : ITechnician
{
    public Task<bool> LaunchInteractive(Package p, bool uninstall)
    {
        var dialog = Dialog(p, (uninstall ? "Uninstall" : "Install") + " requires technician action. Complete the installer, then CIT Deploy will verify the result.");
        var choice = false;
        dialog.Action("Launch installer", () => { choice = true; dialog.Close(); });
        dialog.Action("Cancel / abort", dialog.Close);
        dialog.ShowDialog();
        return Task.FromResult(choice);
    }
    public Task<Decision> Failure(Package p, string detail, bool allowInteractive)
    {
        var choice = Decision.Abort;
        var dialog = Dialog(p, detail);
        dialog.Action("Retry automatic", () => { choice = Decision.Retry; dialog.Close(); });
        if (allowInteractive)
            dialog.Action("Run interactively", () => { choice = Decision.Interactive; dialog.Close(); });
        dialog.Action(p.IsCritical ? "Explicitly override critical failure" : "Skip and record failure", () => { if (MessageBox.Show(dialog, "The failure will remain in the summary. Dependent packages will be blocked. Continue?", "Confirm override", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; choice = Decision.Skip; dialog.Close(); });
        dialog.Action("Abort deployment", dialog.Close);
        dialog.ShowDialog();
        return Task.FromResult(choice);
    }
    Editor Dialog(Package p, string text)
    {
        var e = new Editor(p.Name, new object()) { Owner = owner, Height = 450 };
        e.Note(text);
        Links(e, p.GeneralKbUrl, siteUrl(p));
        return e;
    }
    public static void Links(Editor e, string general, string site)
    {
        if (Rules.HasLink(general))
            e.Action("Installation Guide", () => Open(general));
        if (Rules.HasLink(site))
            e.Action("Site Instructions", () => Open(site));
    }
    public static void Open(string url)
    {
        Rules.Url(url);
        if (!Rules.HasLink(url))
            return; // Explorer delegates navigation to the desktop shell where available.
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { url }, UseShellExecute = false });
    }
}
