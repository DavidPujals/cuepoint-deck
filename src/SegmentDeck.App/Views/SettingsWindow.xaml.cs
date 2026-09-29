using System.Windows;
using SegmentDeck.App.ViewModels;

namespace SegmentDeck.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly SettingsViewModel _vm;

    public SettingsWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
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
}
