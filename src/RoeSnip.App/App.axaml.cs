using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RoeSnip.App.AppShell;

namespace RoeSnip.App;

/// <summary>The Avalonia Application. Only ever instantiated on the resident-tray path
/// (TrayApp.RunResident → Program.BuildAvaloniaApp) — the headless CLI verbs never start Avalonia.
/// The app is tray-only: ShutdownMode is explicit (no main window), and startup continues in
/// <see cref="TrayApp.OnFrameworkReady"/> once the framework is up.</summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            TrayApp.OnFrameworkReady(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
