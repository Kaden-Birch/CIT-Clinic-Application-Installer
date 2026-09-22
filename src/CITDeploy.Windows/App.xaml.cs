using System.Windows;
namespace CITDeploy.Windows;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            new MainWindow().Show();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "CIT Deploy cannot start", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
}
