using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace CuepointDeck.App;

/// <summary>Small attached behaviours the theme and views share.</summary>
public static class Ui
{
    // ------------------------------------------------------------------ IsSelected: segmented-control state on a plain Button

    /// <summary>Marks a segment of a segmented control as the current one. A Button (not a RadioButton) carries it, so a
    /// click that is refused (for example, unsaved changes when leaving Edit mode) never leaves the control out of step.</summary>
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.RegisterAttached("IsSelected", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));
    public static bool GetIsSelected(DependencyObject d) => (bool)d.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject d, bool v) => d.SetValue(IsSelectedProperty, v);

    // ------------------------------------------------------------------ Placeholder: grey hint text inside an empty field

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string v) => d.SetValue(PlaceholderProperty, v);

    // ------------------------------------------------------------------ Icon: a Segoe Fluent glyph shown by icon buttons

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetIcon(DependencyObject d) => (string)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, string v) => d.SetValue(IconProperty, v);

    // ------------------------------------------------------------------ CommitOnEnter: Return commits a LostFocus binding

    /// <summary>Settings apply as you make them. Text fields commit on Return or when focus leaves, never per keystroke,
    /// so typing an address does not reconnect on every character.</summary>
    public static readonly DependencyProperty CommitOnEnterProperty =
        DependencyProperty.RegisterAttached("CommitOnEnter", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false, OnCommitOnEnterChanged));
    public static bool GetCommitOnEnter(DependencyObject d) => (bool)d.GetValue(CommitOnEnterProperty);
    public static void SetCommitOnEnter(DependencyObject d, bool v) => d.SetValue(CommitOnEnterProperty, v);

    private static void OnCommitOnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.KeyDown -= CommitKeyDown;
        if (e.NewValue is true) box.KeyDown += CommitKeyDown;
    }

    private static void CommitKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { AcceptsReturn: false } box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
        e.Handled = true;
    }

    // ------------------------------------------------------------------ DropDownMenu: a button that opens its ContextMenu

    /// <summary>Turns a button into a pull-down button: a left click opens its ContextMenu under it, the way a toolbar
    /// "+" or "⋯" button does. The menu shares the button's DataContext so its commands bind normally.</summary>
    public static readonly DependencyProperty DropDownMenuProperty =
        DependencyProperty.RegisterAttached("DropDownMenu", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false, OnDropDownMenuChanged));
    public static bool GetDropDownMenu(DependencyObject d) => (bool)d.GetValue(DropDownMenuProperty);
    public static void SetDropDownMenu(DependencyObject d, bool v) => d.SetValue(DropDownMenuProperty, v);

    private static void OnDropDownMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= OpenMenu;
        if (e.NewValue is true) button.Click += OpenMenu;
    }

    private static void OpenMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } owner) return;
        menu.PlacementTarget = owner;
        menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 4;
        menu.DataContext = owner.DataContext;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
