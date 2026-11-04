using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Themes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// Ctrl+F in the PDF page view (issues #110, #114): the Edge PDF viewer in WebView2, with its text selection and zoom.
/// Read-only: the toolbar's save buttons are hidden. Web links open in the browser. Esc / F10 / F3 close. It opens with a
/// search row of our own, Esc closes that first: the viewer's search bar takes no input when hosted in WebView2, while
/// its find API works on the document.
/// </summary>
public sealed class PdfViewerPage : DockPanel, IPage, IDisposable
{
    private readonly WebView2 _web = new();
    private readonly TextBlock _info = new();
    private readonly TextBox _search = new();
    private readonly TextBlock _matches = new();
    private readonly Border _searchRow;
    private readonly string _path;
    private readonly Uri _uri;
    private CoreWebView2Environment? _environment;
    private DateTime _loadedAt;
    private int _findRequest;
    private bool _findPending;

    /// <param name="page">The zero-based page to open at.</param>
    public PdfViewerPage(string path, ColorTheme theme, int page)
    {
        _path = path;
        _uri = new Uri($"{new Uri(path).AbsoluteUri}#page={page + 1}");
        Focusable = true;
        FocusVisualStyle = null;
        Background = PanelPalette.Background;
        LastChildFill = true;

        var title = new TextBlock { Text = path, FontWeight = FontWeights.SemiBold };
        var hint = new TextBlock { Text = "Ctrl+F find · Esc close" };
        var findLabel = new TextBlock { Text = "Find:" };
        foreach (var text in new[] { title, _info, hint, findLabel, _matches })
        {
            text.FontFamily = new FontFamily("Segoe UI");
            text.FontSize = 12;
            text.Foreground = text == title ? PanelPalette.Foreground : PanelPalette.DescriptionForeground;
            text.Margin = new Thickness(0, 0, 16, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
        }

        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(title);
        header.Children.Add(_info);
        var headerBorder = new Border { Child = header, BorderBrush = PanelPalette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Background = PanelPalette.HeaderBackground };
        DockPanel.SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        findLabel.Margin = new Thickness(0, 0, 8, 0);
        _search.Width = 280;
        _search.Margin = new Thickness(0, 0, 12, 0);
        _search.FontFamily = new FontFamily("Segoe UI");
        _search.FontSize = 12;
        _search.Foreground = PanelPalette.Foreground;
        _search.CaretBrush = PanelPalette.Foreground;
        _search.Background = PanelPalette.Background;
        _search.BorderBrush = PanelPalette.Border;
        _search.TextChanged += (_, _) => _ = FindAsync();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 4, 10, 4) };
        row.Children.Add(findLabel);
        row.Children.Add(_search);
        row.Children.Add(_matches);
        _searchRow = new Border { Child = row, BorderBrush = PanelPalette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Background = PanelPalette.HeaderBackground };
        DockPanel.SetDock(_searchRow, Dock.Top);
        Children.Add(_searchRow);

        _web.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(theme.GetOpaque("editor.background").ToString());
        Children.Add(_web);
        _ = InitializeAsync();
    }

    /// <summary>Esc / F10 / F3.</summary>
    public event Action? CloseRequested;

    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public void FocusContent()
    {
        if (_searchRow.Visibility == Visibility.Visible)
        {
            _search.Focus();
        }
        else
        {
            _web.Focus();
        }
    }

    public void Dispose()
    {
        _findRequest++;
        _web.Dispose();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var searching = _searchRow.Visibility == Visibility.Visible;
        e.Handled = true;
        switch (key)
        {
            case Key.F when modifiers == ModifierKeys.Control:
                _searchRow.Visibility = Visibility.Visible;
                _search.Focus();
                _search.SelectAll();
                break;
            case Key.Escape when modifiers == ModifierKeys.None && searching:
                CloseSearch();
                break;
            case Key.Enter when searching && _search.IsKeyboardFocused && modifiers == ModifierKeys.None:
                if (_web.CoreWebView2?.Find is { MatchCount: > 0 } find)
                {
                    find.FindNext();
                }
                else if (!_findPending)
                {
                    // The file may have taken longer to read than expected.
                    _ = FindAsync();
                }

                break;
            case Key.Enter when searching && _search.IsKeyboardFocused && modifiers == ModifierKeys.Shift:
                _web.CoreWebView2?.Find.FindPrevious();
                break;
            case Key.Escape or Key.F10 or Key.F3 when modifiers == ModifierKeys.None:
                CloseRequested?.Invoke();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private void CloseSearch()
    {
        _searchRow.Visibility = Visibility.Collapsed;
        _findRequest++;
        _findPending = false;
        _web.CoreWebView2?.Find.Stop();
        _web.Focus();
    }

    private async Task FindAsync()
    {
        if (_web.CoreWebView2 is not { } core || _environment is null)
        {
            return;
        }

        var request = ++_findRequest;
        var term = _search.Text;
        if (term.Length == 0)
        {
            _findPending = false;
            core.Find.Stop();
            _matches.Text = string.Empty;
            return;
        }

        var options = _environment.CreateFindOptions();
        options.FindTerm = term;
        options.SuppressDefaultFindDialog = true;
        options.ShouldHighlightAllMatches = true;

        _findPending = true;
        try
        {
            // The viewer gives no sign that it has read the file, and a search started before that finds nothing or never
            // ends. So the search waits for the time reading takes (about 60 ms per MB, issue #110) and starts once.
            var wait = _loadedAt - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                _matches.Text = "Loading…";
                await Task.Delay(wait).ConfigureAwait(true);
                if (request != _findRequest)
                {
                    return;
                }
            }

            _matches.Text = "Searching…";
            core.Find.Stop();
            await core.Find.StartAsync(options).ConfigureAwait(true);
            if (request != _findRequest)
            {
                return;
            }

            _findPending = false;
            ShowMatches(core.Find);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            if (request == _findRequest)
            {
                _findPending = false;
                _matches.Text = ex.Message;
            }
        }
    }

    private void ShowMatches(CoreWebView2Find find) =>
        _matches.Text = _search.Text.Length == 0 ? string.Empty
            : find.MatchCount > 0 ? $"{find.ActiveMatchIndex} of {find.MatchCount}"
            : _findPending ? _matches.Text
            : "Not found";

    private async Task InitializeAsync()
    {
        try
        {
            // Not next to habi.exe (read-only under Program Files): the app's state folder, or next to it when portable.
            _environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(App.Paths.StateDirectory, "WebView2"));
            await _web.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException)
        {
            _info.Text = $"PDF view needs the Microsoft Edge WebView2 Runtime: {ex.Message}";
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.Save | CoreWebView2PdfToolbarItems.SaveAs | CoreWebView2PdfToolbarItems.FullScreen | CoreWebView2PdfToolbarItems.MoreSettings | CoreWebView2PdfToolbarItems.Search;
        core.Find.MatchCountChanged += (_, _) => ShowMatches(core.Find);
        core.Find.ActiveMatchIndexChanged += (_, _) => ShowMatches(core.Find);
        core.NavigationStarting += OnNavigationStarting;

        // Text typed before the viewer was created has not been searched for yet.
        core.NavigationCompleted += (_, _) =>
        {
            if (!_findPending)
            {
                _ = FindAsync();
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenLink(e.Uri);
        };
        _loadedAt = DateTime.UtcNow.AddSeconds(1 + (new FileInfo(_path).Length / (1024.0 * 1024.0) * 0.1));
        core.Navigate(_uri.AbsoluteUri);
    }

    /// <summary>The file itself loads; a link in it opens outside, it never replaces the document.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.IsFile && string.Equals(uri.LocalPath, _path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        OpenLink(e.Uri);
    }

    private void OpenLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _info.Text = ex.Message;
        }
    }
}
