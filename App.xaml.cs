using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using R2PrismRuntime.Diagnostics;

namespace R2PrismRuntime;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--verbose", StringComparer.OrdinalIgnoreCase)) Log.MinLevel = LogLevel.Debug;
        Log.Init();
        Resources["Grain"] = CreateGrain();
        UI.MainViewModel.DiagnoseOnFirstScan = e.Args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase);
        UI.MainViewModel.SelfTest = e.Args.Contains("--selftest-ui", StringComparer.OrdinalIgnoreCase);
        base.OnStartup(e);

        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error("Unhandled UI exception.", a.Exception);
            MessageBox.Show(a.Exception.Message + "\n\nDetails are in the log file.", "Prismforge",
                MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            Log.Error($"Unhandled exception (terminating={a.IsTerminating}).", a.ExceptionObject as Exception ?? new Exception("unknown"));
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Log.Error("Unobserved task exception.", a.Exception);
            a.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info($"App exiting (code {e.ApplicationExitCode}).");
        Log.Shutdown();
        base.OnExit(e);
    }

    /// Tiled film-grain texture: faint warm speckles over the dark background.
    private static Brush CreateGrain()
    {
        const int size = 160;
        var px = new byte[size * size * 4];
        var rng = new Random(1337);
        for (int i = 0; i < size * size; i++)
        {
            int v = rng.Next(256);
            byte a = (byte)(v > 128 ? (v - 128) / 11 : 0);
            px[i * 4 + 0] = 0xB8; // B
            px[i * 4 + 1] = 0xC8; // G
            px[i * 4 + 2] = 0xD8; // R
            px[i * 4 + 3] = a;
        }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        bmp.Freeze();
        var brush = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }
}
