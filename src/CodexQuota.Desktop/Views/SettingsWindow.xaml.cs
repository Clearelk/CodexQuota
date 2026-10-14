using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodexQuota.Desktop.ViewModels;
using CodexQuota.Desktop.Services;
using System.Windows.Threading;

namespace CodexQuota.Desktop.Views;

/// <summary>Compact settings panel. Closing it never exits the widget.</summary>
public partial class SettingsWindow : Window
{
    private long _opening;
    /// <summary>Raw threshold text at the last baseline capture; used for dirty detection.</summary>
    internal string BaselineThresholdText { get; set; } = string.Empty;

    public SettingsWindow()
    {
        InitializeComponent();
        ThresholdInput.TextChanged += (_, _) => RawThresholdChanged?.Invoke();
        IsVisibleChanged += (_, _) => { if (!IsVisible) { _opening++; } };
    }

    /// <summary>Raised when the user asks to save; the caller validates and persists.</summary>
    public event Action? SaveRequested;

    /// <summary>Raised when the panel should be hidden.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised whenever the raw threshold text changes, even when invalid.</summary>
    public event Action? RawThresholdChanged;

    /// <summary>Starts a new opening neutrally while retaining tab access to every control.</summary>
    internal void FocusOpeningSurface() => Keyboard.Focus(SettingsSurface);

    /// <summary>Only a hidden-to-visible opening resets focus and scroll; stale callbacks cannot affect another session.</summary>
    internal void ResetOpeningSurface()
    {
        long opening = ++_opening;
        FocusOpeningSurface();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!IsVisible || _opening != opening) { return; }
            SettingsBody.UpdateLayout();
            SettingsBody.ScrollToTop();
            SettingsBody.ScrollToHorizontalOffset(0);
        });
    }

    /// <summary>
    /// The one validation query shared by the Save button and the leave confirmation:
    /// an invalid uncommitted threshold text blocks both save paths while notifications
    /// are enabled; disabled editing keeps its established last-valid-draft behavior.
    /// </summary>
    internal bool CanSave() => !ViewModel.NotificationsEnabled || !Validation.GetHasError(ThresholdInput);

    /// <summary>Current raw threshold text; invalid text never reaches the view model.</summary>
    internal string RawThresholdText => ThresholdInput.Text;

    internal void ResetThresholdToDraft() => ThresholdInput.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();

    /// <summary>Revalidates the current proposed text without overwriting it or updating the draft.</summary>
    internal void RefreshLocalizedValidation() => ThresholdInput.GetBindingExpression(TextBox.TextProperty)?.ValidateWithoutUpdate();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (CanSave()) { SaveRequested?.Invoke(); }
    }

    private void OnRestoreDefaults(object sender, RoutedEventArgs e) => ViewModel.RestoreDefaults();

    private void OnThresholdUp(object sender, RoutedEventArgs e) => ChangeThreshold(1);

    private void OnThresholdDown(object sender, RoutedEventArgs e) => ChangeThreshold(-1);

    private void ChangeThreshold(int delta)
    {
        if (!ViewModel.NotificationsEnabled) { return; }
        ViewModel.LowQuotaThreshold += delta;
        ThresholdInput.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // The neutral opening surface is outside the body. Page keys still reach it,
        // while editable controls retain their own arrows/Home/End and popup behavior.
        bool neutralFocus = ReferenceEquals(Keyboard.FocusedElement, SettingsSurface) || ReferenceEquals(Keyboard.FocusedElement, SettingsBody);
        bool actionPaging = Keyboard.FocusedElement is Button && e.Key is Key.PageDown or Key.PageUp;
        if (!e.Handled && (neutralFocus || actionPaging))
        {
            switch (e.Key)
            {
                case Key.PageDown: SettingsBody.PageDown(); e.Handled = true; break;
                case Key.PageUp: SettingsBody.PageUp(); e.Handled = true; break;
                case Key.Home: SettingsBody.ScrollToTop(); e.Handled = true; break;
                case Key.End: SettingsBody.ScrollToBottom(); e.Handled = true; break;
                case Key.Down: SettingsBody.LineDown(); e.Handled = true; break;
                case Key.Up: SettingsBody.LineUp(); e.Handled = true; break;
            }
        }

        base.OnKeyDown(e);
    }

    /// <summary>Applies the current settings to the view model before the caller reads them.</summary>
    public SettingsViewModel ViewModel => (SettingsViewModel)DataContext;
}

public sealed class QuotaThresholdRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo) =>
        int.TryParse(value?.ToString(), NumberStyles.Integer, cultureInfo, out int threshold) && threshold is >= 1 and <= 100
            ? ValidationResult.ValidResult
            : new ValidationResult(false, LocalizationService.Text("ThresholdValidation"));
}
