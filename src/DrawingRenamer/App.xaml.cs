using Microsoft.UI.Xaml;

namespace DrawingRenamer;

public partial class App : Application
{
    private Window? window;
    internal static DiagnosticLog Log { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrawingRenamer", "Logs"));

    public App()
    {
        Log.Info("Application", $"Starting. Version={typeof(App).Assembly.GetName().Version}; Runtime={Environment.Version}; OS={Environment.OSVersion}; Architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        UnhandledException += (_, args) => Log.Error("Application", "Unhandled UI exception.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                Log.Error("Application", $"Unhandled exception. Terminating={args.IsTerminating}", exception);
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
