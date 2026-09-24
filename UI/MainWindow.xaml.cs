using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace R2PrismRuntime.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // Keep the journal scrolled to the newest line.
        _vm.LogLines.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && _vm.IsJournalOpen && JournalList.Items.Count > 0)
                JournalList.ScrollIntoView(JournalList.Items[^1]);
        };

        Loaded += (_, _) => _vm.Start();

        if (MainViewModel.SelfTest)
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -30000;
            Top = -30000;
            Loaded += async (_, _) => await RunPickerSelfTest();
        }
        Closed += (_, _) => _vm.Stop();
        StateChanged += (_, _) =>
        {
            // A chrome-less maximized window overhangs the screen by the resize border.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
    }

    /// Opens every stat picker of every prism, logs how many items it has, rescans, repeats.
    private async Task RunPickerSelfTest()
    {
        for (int i = 0; i < 30 && _vm.Prisms.Count == 0; i++) await Task.Delay(500);
        for (int round = 0; round < 2; round++)
        {
            foreach (var prism in _vm.Prisms.ToList())
            {
                _vm.SelectedPrism = prism;
                await Task.Delay(400);
                var combos = FindChildren<ComboBox>(this).Where(c => c.IsVisible).ToList();
                foreach (var (cb, idx) in combos.Select((c, i) => (c, i)))
                {
                    cb.IsDropDownOpen = true;
                    await Task.Delay(150);
                    var src = cb.ItemsSource as System.Collections.IEnumerable;
                    int srcCount = src?.Cast<object>().Count() ?? -1;
                    Diagnostics.Log.Info($"SELFTEST round {round} '{prism.Name}' picker {idx}: Items={cb.Items.Count} " +
                                         $"source={srcCount} sourceType={cb.ItemsSource?.GetType().Name ?? "null"} " +
                                         $"selected='{(cb.SelectedItem as Game.SegmentDef)?.Name}'");
                    cb.IsDropDownOpen = false;
                }
            }
            await _vm.ForceRescanAsync();
            await Task.Delay(3000);
        }
        float? Caps(string n) => n switch { "MoveSpeedCap" => 2f, "HealthMaxCap" => 1000f, _ => null };
        foreach (var (n, v) in new (string, float)[] {
                     ("MoveSpeed", 3), ("MoveSpeed", 1.5f), ("CritChance", 150), ("DamageReductionMod", 100),
                     ("FireResistance", 90), ("SkillCooldownMod", -120), ("FireSpeed", 0), ("ReloadSpeed", 8),
                     ("PowerLevel", 999), ("CritDamageMod", 500), ("HealthMax", 5000), ("MoveSpeedCap", 9) })
            Diagnostics.Log.Info($"SELFTEST advice {n}={v}: {Game.StatAdvice.Check(n, v, Caps) ?? "(ok)"}");
        // Render the pages and overlays to PNGs (no screen capture needed).
        await Task.Delay(300);
        RenderTo("selftest_prisms.png");
        _vm.IsNoticeOpen = true;
        await Task.Delay(300);
        RenderTo("selftest_notice.png");
        _vm.IsNoticeOpen = false;
        _vm.IsAboutOpen = true;
        await Task.Delay(300);
        RenderTo("selftest_about.png");
        _vm.IsAboutOpen = false;
        _vm.Page = Page.Stats;
        await Task.Delay(300);
        RenderTo("selftest_stats.png");

        // Render one attribute tooltip standalone.
        _vm.StatSearch = "FogOfWar";
        await Task.Delay(500);
        var owner = FindChildren<FrameworkElement>(this).FirstOrDefault(e => e.ToolTip is ToolTip && e.DataContext is Models.StatEntry);
        if (owner?.ToolTip is ToolTip tip)
        {
            tip.PlacementTarget = owner;
            tip.IsOpen = true;
            await Task.Delay(400);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)tip.ActualWidth + 1,
                (int)tip.ActualHeight + 1, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(tip);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(System.IO.Path.Combine(Diagnostics.Log.LogDirectory, "selftest_tooltip.png")))
                enc.Save(fs);
            tip.IsOpen = false;
        }
        else Diagnostics.Log.Warn("SELFTEST: no attribute tooltip found.");
        Diagnostics.Log.Info("SELFTEST done.");
        Close();
    }

    private void RenderTo(string file)
    {
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(this);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(System.IO.Path.Combine(Diagnostics.Log.LogDirectory, file));
        enc.Save(fs);
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindChildren<T>(child)) yield return d;
        }
    }

    private void OnBackdropClick(object sender, MouseButtonEventArgs e) => _vm.IsAboutOpen = false;

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// Enter commits a text field, Escape restores the staged value.
    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var binding = tb.GetBindingExpression(TextBox.TextProperty);
        if (e.Key == Key.Enter) { binding?.UpdateSource(); tb.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape) { binding?.UpdateTarget(); e.Handled = true; }
    }
}
