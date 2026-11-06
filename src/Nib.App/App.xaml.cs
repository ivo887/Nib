using System;
using System.Runtime;
using Microsoft.UI.Xaml;
using Nib.App.Diagnostics;
using Nib.App.Services;
using Nib.Core.Diagnostics;

namespace Nib.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        // Ink and render loops are allocation-light; keep gen2 collections from ever blocking a stroke.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        InitializeComponent();
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupClock.Mark("OnLaunched");
        var options = LaunchOptions.Parse(Environment.GetCommandLineArgs());
        // A restart after changing the notebook location: wait for the old process to release its database, then apply the change.
        if (options.WaitForPid > 0) DataLocation.WaitForPredecessor(options.WaitForPid);
        if (options.DataFolder is null) DataLocation.ApplyPending();
        _window = new MainWindow(options);
        _window.Activate();
        StartupClock.Mark("WindowActivated");
    }
}
