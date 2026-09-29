using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SegmentDeck.App.ViewModels;
using SegmentDeck.Core.Logging;

namespace SegmentDeck.App.Views;

/// <summary>Code-behind owns the media element: WPF's MediaElement has no bindable position, so the view keeps the
/// player and the view model's scrub position in step both ways.</summary>
public partial class EditView : UserControl
{
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private EditViewModel? _vm;
    private bool _mediaReady;

    public EditView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.OldValue as EditViewModel, e.NewValue as EditViewModel);
        _playTimer.Tick += (_, _) => PullPositionFromPlayer();
        Unloaded += (_, _) => { _playTimer.Stop(); try { Player.Stop(); Player.Source = null; } catch { } };
    }

    private void Attach(EditViewModel? old, EditViewModel? vm)
    {
        if (old is not null) old.PropertyChanged -= Vm_PropertyChanged;
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += Vm_PropertyChanged;
        LoadProxy(vm.ProxyPath);
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(EditViewModel.ProxyPath):
                LoadProxy(_vm.ProxyPath);
                break;
            case nameof(EditViewModel.IsPlaying):
                if (!_mediaReady) break;
                if (_vm.IsPlaying) { Player.Play(); _playTimer.Start(); }
                else { _playTimer.Stop(); Player.Pause(); PullPositionFromPlayer(); }
                break;
            case nameof(EditViewModel.ScrubMs):
                // The player drives ScrubMs while playing; otherwise the scrubber drives the player.
                if (_mediaReady && !_vm.IsPlaying && !_vm.PositionFromPlayer)
                    Player.Position = TimeSpan.FromMilliseconds(_vm.ScrubMs);
                break;
        }
    }

    private void LoadProxy(string? path)
    {
        _mediaReady = false;
        _playTimer.Stop();
        try
        {
            if (path is null) { Player.Stop(); Player.Source = null; return; }
            Player.Source = new Uri(path);
            Player.Volume = 0.8;
            // Play then pause so the element decodes and shows the first frame while paused (scrubbing needs that).
            Player.Play();
            Player.Pause();
        }
        catch (Exception ex) { Log.Warn($"Preview player: {ex.Message}"); }
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        Log.Info($"Preview video opened: {(Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds.ToString("0.0") + " s" : "?")}, {Player.NaturalVideoWidth}x{Player.NaturalVideoHeight}");
        if (_vm is null) return;
        if (Player.NaturalVideoWidth > 0 && Player.NaturalVideoHeight > 0)
            _vm.PreviewAspect = Player.NaturalVideoWidth / (double)Player.NaturalVideoHeight;
        Player.Position = TimeSpan.FromMilliseconds(_vm.ScrubMs);
        if (_vm.IsPlaying) { Player.Play(); _playTimer.Start(); } else Player.Pause();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.IsPlaying = false;
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Log.Warn($"Preview video could not be played: {e.ErrorException.Message}");
        _mediaReady = false;
        if (_vm is not null) { _vm.IsPlaying = false; _vm.ProxyPath = null; }
    }

    private void PullPositionFromPlayer()
    {
        if (_vm is null || !_mediaReady) return;
        _vm.PositionFromPlayer = true;
        try { _vm.ScrubMs = Player.Position.TotalMilliseconds; }
        finally { _vm.PositionFromPlayer = false; }
    }

    private void Player_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _vm?.PlayPauseCommand.Execute(null);

    private void ScrubSlider_DragStarted(object sender, DragStartedEventArgs e) { if (_vm is not null) _vm.IsPlaying = false; }

    private void ScrubSlider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Left: _vm.ScrubStep(-1, shift); e.Handled = true; break;
            case Key.Right: _vm.ScrubStep(1, shift); e.Handled = true; break;
            case Key.M: _vm.MarkAtScrubberCommand.Execute(null); e.Handled = true; break;
            case Key.Space: _vm.PlayPauseCommand.Execute(null); e.Handled = true; break;
        }
    }

    private void StepBack_Click(object sender, RoutedEventArgs e) => _vm?.ScrubStep(-1, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    private void StepForward_Click(object sender, RoutedEventArgs e) => _vm?.ScrubStep(1, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { DataContext: FilmstripFrame frame }) return;
        _vm.ScrubTo(frame);
        ScrubSlider.Focus();
    }
}
