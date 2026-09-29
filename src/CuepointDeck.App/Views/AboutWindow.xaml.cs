using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using CuepointDeck.App.Services;
using CuepointDeck.Core.Logging;

namespace CuepointDeck.App.Views;

public partial class AboutWindow : Window
{
    private bool _installed;

    public AboutWindow(string? resolumeProduct)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        VersionText.Text = "Version " + UpdateService.Format(UpdateService.CurrentVersion);
        RepoLink.NavigateUri = new Uri(UpdateService.RepoUrl);
        RepoLinkText.Text = UpdateService.RepoUrl.Replace("https://", "");
        if (resolumeProduct is not null) ResolumeText.Text = $"Connected to {resolumeProduct}";
        if (!UpdateService.CanSelfUpdate)
        {
            UpdateStatus.Text = "This is a development build (folder layout), so it cannot update itself. Releases are single-file exes.";
            UpdateButton.IsEnabled = false;
        }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* no browser association — the URL is shown as text anyway */ }
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_installed)
        {
            // Swap already happened on disk — start the new exe and bow out.
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
            Application.Current.Shutdown();
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                UpdateStatus.Text = "You're up to date — this is the latest version.";
            }
            else
            {
                var ver = "v" + UpdateService.Format(info.Version);
                UpdateStatus.Text = $"Downloading {ver}…";
                var progress = new Progress<double>(p => UpdateStatus.Text = $"Downloading {ver}… {p:P0}");
                await UpdateService.DownloadAndInstallAsync(info, progress);
                _installed = true;
                UpdateStatus.Text = $"{ver} installed — restart to finish. Resolume keeps playing while Cuepoint Deck restarts.";
                UpdateButton.Content = "Restart now";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Update", ex);
            UpdateStatus.Text = "Update failed: " + ex.Message;
        }
        UpdateButton.IsEnabled = true;
    }
}
