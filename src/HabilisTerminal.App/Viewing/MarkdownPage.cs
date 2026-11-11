using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Core.Editing;
using HabilisTerminal.Core.Viewing;
using HabilisTerminal.Themes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// Alt+F3 on a Markdown file (issue #57): the rendered file in WebView2, coloured by the colour theme. The file's
/// folder is served as <c>https://md.habi.local/</c>, so relative images load; links to other Markdown files open
/// here, web links in the browser. F3 the plain viewer, F4 / F6 the editor, Esc / F10 / Alt+F3 close.
/// </summary>
public sealed class MarkdownPage : DockPanel, IPage, IDisposable
{
    private const string Host = "md.habi.local";
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd" };

    private readonly WebView2 _web = new();
    private readonly Action _unzoom;
    private readonly TextBlock _title = new();
    private readonly TextBlock _info = new();
    private readonly ColorTheme _theme;
    private string _folder = string.Empty;

    public MarkdownPage(string path, ColorTheme theme)
    {
        FilePath = path;
        _theme = theme;
        Focusable = true;
        FocusVisualStyle = null;
        Background = PanelPalette.Background;
        LastChildFill = true;

        var hint = new TextBlock { Text = "F3 view as text · F4 edit · Esc close" };
        foreach (var text in new[] { _title, _info, hint })
        {
            text.FontFamily = new FontFamily("Segoe UI");
            text.FontSize = 12;
            text.Foreground = text == _title ? PanelPalette.Foreground : PanelPalette.DescriptionForeground;
            text.Margin = new Thickness(0, 0, 16, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
        }

        _title.FontWeight = FontWeights.SemiBold;
        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(_title);
        header.Children.Add(_info);
        var headerBorder = new Border { Child = header, BorderBrush = PanelPalette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Background = PanelPalette.HeaderBackground };
        DockPanel.SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        _web.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(theme.GetOpaque("editor.background").ToString());
        Children.Add(_web);
        _title.Text = path;
        _unzoom = Widgets.UiZoom.Follow(_web);
        _ = InitializeAsync();
    }

    /// <summary>Esc / F10 / Alt+F3.</summary>
    public event Action? CloseRequested;

    /// <summary>F3: the same file in the plain viewer.</summary>
    public event Action<string>? ViewRequested;

    /// <summary>F4 / F6: the same file in the editor.</summary>
    public event Action<string>? EditRequested;

    /// <summary>The file shown now (a followed link changes it).</summary>
    public string FilePath { get; private set; }

    public static bool IsMarkdown(string path) => Extensions.Contains(Path.GetExtension(path));

    public void FocusContent() => _web.Focus();

    public void Dispose()
    {
        _unzoom();
        _web.Dispose();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        e.Handled = true;
        switch (key)
        {
            case Key.Escape or Key.F10 when modifiers == ModifierKeys.None:
            case Key.F3 when modifiers == ModifierKeys.Alt:
                CloseRequested?.Invoke();
                break;
            case Key.F3 when modifiers == ModifierKeys.None:
                ViewRequested?.Invoke(FilePath);
                break;
            case Key.F4 or Key.F6 when modifiers == ModifierKeys.None:
                EditRequested?.Invoke(FilePath);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            // Not next to habi.exe (read-only under Program Files): the app's state folder, or next to it when portable.
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(App.Paths.StateDirectory, "WebView2"));
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException)
        {
            _info.Text = $"Markdown view needs the Microsoft Edge WebView2 Runtime: {ex.Message}";
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OnLink(new Uri(e.Uri));
        };
        Show(FilePath);
    }

    private void Show(string path)
    {
        string text;
        try
        {
            text = TextFile.Load(path).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _info.Text = ex.Message;
            return;
        }

        FilePath = path;
        _title.Text = path;
        _info.Text = string.Empty;
        var folder = Path.GetDirectoryName(path)!;
        if (!string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            if (_folder.Length > 0)
            {
                _web.CoreWebView2.ClearVirtualHostNameToFolderMapping(Host);
            }

            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.Allow);
            _folder = folder;
        }

        _web.NavigateToString(MarkdownHtml.Render(text, Colors(_theme), $"https://{Host}/", Path.GetFileName(path)));
    }

    private static MarkdownColors Colors(ColorTheme theme)
    {
        string Css(string key) => theme.GetOpaque(key).ToString();
        return new MarkdownColors(
            Css("editor.background"),
            Css("editor.foreground"),
            Css("descriptionForeground"),
            Css("textLink.foreground"),
            Css("editorWidget.border"),
            Css("textCodeBlock.background"),
            theme.IsLight);
    }

    /// <summary>The page itself loads; every link click is handled here instead of navigating the view.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            OnLink(uri);
        }
    }

    private void OnLink(Uri uri)
    {
        if (!string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase))
        {
            if (uri.Scheme is "http" or "https" or "mailto")
            {
                ShellOpen(uri.AbsoluteUri);
            }

            return;
        }

        var relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (relative.Length == 0)
        {
            // "#section": an anchor in this page (the base makes it a navigation to the folder).
            if (uri.Fragment.Length > 1)
            {
                var id = System.Text.Json.JsonSerializer.Serialize(Uri.UnescapeDataString(uri.Fragment[1..]));
                _ = _web.CoreWebView2.ExecuteScriptAsync($"document.getElementById({id})?.scrollIntoView()");
            }

            return;
        }

        var target = Path.GetFullPath(Path.Combine(_folder, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (IsMarkdown(target) && File.Exists(target))
        {
            Show(target);
        }
        else if (File.Exists(target) || Directory.Exists(target))
        {
            ShellOpen(target);
        }
        else
        {
            _info.Text = $"Not found: {relative}";
        }
    }

    private void ShellOpen(string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _info.Text = ex.Message;
        }
    }
}
