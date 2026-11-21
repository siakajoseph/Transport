using System.Threading;
using System.Windows;
namespace Transport.Guichet;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        string account = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        instance = new Mutex(false, @"Global\Transport.Guichet." + account, out bool first);
        if (!first)
        {
            MessageBox.Show("Un guichet est déjà ouvert avec ce compte Windows.", "Transport");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
