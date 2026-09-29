using System.Windows;
using SegmentDeck.App.ViewModels;

namespace SegmentDeck.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly SettingsViewModel _vm;
    private readonly Action _openLog;

    public SettingsWindow(AppServices services, Action openLog)
    {
        InitializeComponent();
        _services = services;
        _openLog = openLog;
        _vm = new SettingsViewModel(services);
        DataContext = _vm;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = _vm.Build();
        if (settings is null) return;
        await _services.ApplySettingsAsync(settings);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Log_Click(object sender, RoutedEventArgs e) => _openLog();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var w = new AboutWindow(_services.Connection.ProductInfo) { Owner = this };
        w.ShowDialog();
    }
}
