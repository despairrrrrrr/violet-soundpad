using System;
using System.Threading;
using System.Windows;

namespace VioletSoundpad;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance;
    internal const string InstanceId = "VioletSoundpad-47D9E29A-3E13-415F-AACC-DBFFFC32CC9D";

    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, InstanceId, out bool created);
        ownsInstance = created;
        if (!created)
        {
            MessageBox.Show("Violet Soundpad уже открыт.", "Violet Soundpad", MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance)
            instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
