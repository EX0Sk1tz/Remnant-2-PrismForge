using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using R2PrismRuntime.Diagnostics;

namespace R2PrismRuntime.UI;

/// <summary>About panel and the first-start notice.</summary>
public partial class MainViewModel
{
    private readonly AppInfo.Settings _settings = AppInfo.LoadSettings();

    [ObservableProperty] private bool _isAboutOpen;
    [ObservableProperty] private bool _isNoticeOpen;

    public string AppVersion => AppInfo.Version;
    public string AppName => AppInfo.Name;
    public AppInfo.Credit[] CreditList => AppInfo.Credits;
    public bool HasNexusUrl => AppInfo.NexusUrl.Length > 0;
    public bool HasSourceUrl => AppInfo.SourceUrl.Length > 0;

    /// Shown until the user has accepted it for this version.
    private void InitShell() => IsNoticeOpen = !SelfTest && _settings.AcceptedNoticeVersion != AppInfo.Version;

    [RelayCommand]
    private void AcceptNotice()
    {
        _settings.AcceptedNoticeVersion = AppInfo.Version;
        AppInfo.SaveSettings(_settings);
        IsNoticeOpen = false;
    }

    [RelayCommand]
    private void ToggleAbout() => IsAboutOpen = !IsAboutOpen;

    [RelayCommand]
    private void OpenNexus() => OpenUrl(AppInfo.NexusUrl);

    [RelayCommand]
    private void OpenSource() => OpenUrl(AppInfo.SourceUrl);

    private static void OpenUrl(string url)
    {
        if (url.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Could not open link.", ex); }
    }
}
