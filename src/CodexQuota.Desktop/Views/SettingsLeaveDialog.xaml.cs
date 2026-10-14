using System.Windows;
using System.Windows.Input;

namespace CodexQuota.Desktop.Views;

public enum SettingsLeaveChoice
{
    SaveAndLeave,
    DiscardAndLeave,
    Continue,
}

/// <summary>
/// One compact themed confirmation owned by the visible Settings window. Escape and the
/// native window close mean continue editing, so a stray dismissal can never silently
/// discard or save the draft.
/// </summary>
public partial class SettingsLeaveDialog : Window
{
    private const double SettingsWidthRatio = 0.8;

    public SettingsLeaveDialog()
    {
        InitializeComponent();
        Width = (double)FindResource("PanelWidth") * SettingsWidthRatio;
        Loaded += (_, _) => ContinueButton.Focus();
    }

    /// <summary>Shows the dialog modally and returns the chosen action.</summary>
    public static SettingsLeaveChoice ShowFor(Window owner)
    {
        var dialog = new SettingsLeaveDialog { Owner = owner };
        if (owner.ActualWidth > 0)
        {
            dialog.Width = owner.ActualWidth * SettingsWidthRatio;
        }

        dialog.ShowDialog();
        return dialog.Choice;
    }

    private SettingsLeaveChoice Choice { get; set; } = SettingsLeaveChoice.Continue;

    private void OnSaveAndLeave(object sender, RoutedEventArgs e)
    {
        Choice = SettingsLeaveChoice.SaveAndLeave;
        Close();
    }

    private void OnDiscardAndLeave(object sender, RoutedEventArgs e)
    {
        Choice = SettingsLeaveChoice.DiscardAndLeave;
        Close();
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        Choice = SettingsLeaveChoice.Continue;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape keeps the draft and the live preview exactly as they are.
        if (e.Key == Key.Escape)
        {
            Choice = SettingsLeaveChoice.Continue;
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
