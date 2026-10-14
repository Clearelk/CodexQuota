using System.Windows;
using System.Windows.Input;

namespace CodexQuota.Desktop.Views;

/// <summary>
/// The details panel. It renders cached view-model state only; showing it never
/// starts an official read. Pointer enter/leave is forwarded to the shared
/// interaction controller so the pointer can travel between ball and panel.
/// </summary>
public partial class DetailsWindow : Window
{
    public DetailsWindow()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the panel asks to be hidden (title close or Escape).</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when the header gear asks for the settings window.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised when the pointer enters the panel.</summary>
    public event Action? PointerEntered;

    /// <summary>Raised when the pointer leaves the panel.</summary>
    public event Action? PointerExited;

    /// <summary>Exposed for verification: the rendered width of the panel in DIPs.</summary>
    public double PanelWidth => PanelRoot.ActualWidth > 0 ? PanelRoot.ActualWidth : Width;

    /// <summary>
    /// Focuses the neutral panel surface so no header button (gear/close) stays the
    /// window's remembered focused element and re-highlights when the panel is shown.
    /// </summary>
    public void FocusOpeningSurface() => Keyboard.Focus(PanelRoot);

    /// <summary>Measures visible content independently of the old scroll viewport.</summary>
    public double GetPreferredHeight(double width)
    {
        double border = PanelRoot.BorderThickness.Left + PanelRoot.BorderThickness.Right;
        PanelContent.Measure(new Size(Math.Max(1, width - border), double.PositiveInfinity));
        return Math.Ceiling(PanelContent.DesiredSize.Height + PanelRoot.BorderThickness.Top + PanelRoot.BorderThickness.Bottom);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        FocusOpeningSurface();
        CloseRequested?.Invoke();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        FocusOpeningSurface();
        SettingsRequested?.Invoke();
    }

    private void OnPanelMouseEnter(object sender, MouseEventArgs e) => PointerEntered?.Invoke();

    private void OnPanelMouseLeave(object sender, MouseEventArgs e) => PointerExited?.Invoke();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
