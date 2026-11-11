using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.App;

/// <summary>
/// The interface zoom (issue #237): Ctrl+wheel anywhere, Ctrl+Alt+= / − / 0, and a badge with the percentage and Reset
/// for a moment after each change.
/// </summary>
public partial class MainWindow
{
    private Popup? _zoomBadge;
    private TextBlock? _zoomPercent;
    private DispatcherTimer? _zoomBadgeTimer;

    private void InstallZoom(AppState state)
    {
        UiZoom.Set(state.Zoom ?? 1.0);
        UiZoom.Apply(Root);
        ApplyCaptionHeight();
        UiZoom.Changed += OnZoomChanged;
        PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Delta != 0)
            {
                e.Handled = true;
                UiZoom.Step(Math.Sign(e.Delta));
            }
        };
        Deactivated += (_, _) => HideZoomBadge();

        // A WebView2 page holds the keyboard in its own process: its Ctrl+Alt keys come back only as routed events.
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? KeyModifiers.Control : KeyModifiers.None)
                | (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? KeyModifiers.Alt : KeyModifiers.None)
                | (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? KeyModifiers.Shift : KeyModifiers.None);
            if (HandleZoomKey(key, modifiers))
            {
                e.Handled = true;
            }
        };
    }

    /// <summary>Ctrl+Alt+= / Ctrl+Alt+− / Ctrl+Alt+0, from any focus; the gray keys too.</summary>
    private static bool HandleZoomKey(Key key, KeyModifiers modifiers)
    {
        if (modifiers != (KeyModifiers.Control | KeyModifiers.Alt))
        {
            return false;
        }

        switch (key)
        {
            case Key.OemPlus or Key.Add:
                UiZoom.Step(1);
                return true;
            case Key.OemMinus or Key.Subtract:
                UiZoom.Step(-1);
                return true;
            case Key.D0 or Key.NumPad0:
                UiZoom.Reset();
                return true;
            default:
                return false;
        }
    }

    private void OnZoomChanged()
    {
        ApplyCaptionHeight();
        foreach (var tab in _tabs)
        {
            UiZoom.Unscale(tab.Terminal);
        }

        ApplyTerminalTheme(_themes.Current);
        UpdateCellSize();
        ShowZoomBadge();
    }

    /// <summary>The title bar grows with the zoom, so its drag area must too.</summary>
    private void ApplyCaptionHeight()
    {
        if (WindowChrome.GetWindowChrome(this) is { IsFrozen: false } chrome)
        {
            chrome.CaptionHeight = TitleBar.BarHeight * UiZoom.Factor;
        }
    }

    private void ShowZoomBadge()
    {
        if (!IsActive || WindowState == WindowState.Minimized)
        {
            return;
        }

        if (_zoomBadge is null)
        {
            _zoomPercent = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MinWidth = 36 };
            var reset = new TextBlock { Text = "Reset", VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, TextDecorations = TextDecorations.Underline };
            reset.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                UiZoom.Reset();
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { _zoomPercent, reset } };
            var badge = new Border
            {
                Child = row,
                Padding = new Thickness(12, 6, 12, 6),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
            };
            TextElement.SetFontFamily(badge, new FontFamily("Segoe UI"));
            TextElement.SetFontSize(badge, 12.0);
            badge.MouseEnter += (_, _) => _zoomBadgeTimer?.Stop();
            badge.MouseLeave += (_, _) => _zoomBadgeTimer?.Start();
            _zoomBadge = new Popup
            {
                Child = badge,
                PlacementTarget = Workspace,
                Placement = PlacementMode.Custom,

                // Both sizes in the same units, whatever the zoom: centred, a little below the title bar.
                CustomPopupPlacementCallback = (popup, target, _) => [new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, popup.Height / 3), PopupPrimaryAxis.None)],
                AllowsTransparency = true,
                StaysOpen = true,
            };
            UiZoom.Apply(_zoomBadge);
            _zoomBadgeTimer = new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Normal, (_, _) => HideZoomBadge(), Dispatcher);
        }

        var frame = (Border)_zoomBadge.Child;
        frame.Background = PanelPalette.WidgetBackground;
        frame.BorderBrush = PanelPalette.WidgetBorder;
        _zoomPercent!.Foreground = PanelPalette.Foreground;
        ((TextBlock)((StackPanel)frame.Child).Children[1]).Foreground = PanelPalette.FocusOutline;
        _zoomPercent.Text = ZoomSteps.Percent(UiZoom.Factor);
        _zoomBadge.IsOpen = true;

        // Moved a little so a popup already open takes the new offset.
        _zoomBadge.HorizontalOffset += 0.01;
        _zoomBadge.HorizontalOffset -= 0.01;
        _zoomBadgeTimer!.Stop();
        if (!frame.IsMouseOver)
        {
            _zoomBadgeTimer.Start();
        }
    }

    private void HideZoomBadge()
    {
        _zoomBadgeTimer?.Stop();
        if (_zoomBadge is not null)
        {
            _zoomBadge.IsOpen = false;
        }
    }
}
