using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HabilisTerminal.App.Editing;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Viewing;
using HabilisTerminal.Core.Editing;
using HabilisTerminal.Core.Settings;
using HabilisTerminal.Core.Viewing;
using HabilisTerminal.Themes;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace HabilisTerminal.App.Panels;

/// <summary>
/// Ctrl+Q (SPEC §9): the current file of the other panel as text or image, folders as a summary; for a plugin panel
/// the text its provider gives (<c>IQuickViewEntry</c>). Text files follow the F3 rule (issue #74): up to
/// <c>viewer.textModeMaxMB</c> in a read-only editor with syntax colours, larger ones in the paged view.
/// </summary>
public sealed class QuickView : Grid, IDisposable
{
    private readonly ViewerView _text = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(8) };
    private readonly TextBlock _message = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
    private readonly TextBlock _plain = new() { Margin = new Thickness(10, 8, 10, 8), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
    private readonly ScrollViewer _plainScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private readonly TextEditor _editor = new()
    {
        IsReadOnly = true,
        ShowLineNumbers = true,
        Focusable = false,
        Visibility = Visibility.Collapsed,
    };

    private ViewerDocument? _document;
    private string? _path;

    /// <summary>The settings and colour theme of the text mode, set by the window (like <see cref="ViewerView.SetFont"/>).</summary>
    public static AppSettings? Settings { get; set; }

    public static ColorTheme? Theme { get; set; }

    public QuickView()
    {
        Children.Add(_text);
        _editor.TextArea.Focusable = false; // the keyboard stays in the panels
        Children.Add(_editor);
        Children.Add(_image);
        Children.Add(_message);
        _plainScroll.Content = _plain;
        Children.Add(_plainScroll);
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        Background = PanelPalette.Background;
        _message.Foreground = PanelPalette.DescriptionForeground;
        _plain.Foreground = PanelPalette.Foreground;
        _text.InvalidateVisual();
        if (Theme is { } theme)
        {
            TextEditorTheme.Apply(_editor, theme);
        }
    }

    public void Show(string? path, bool isDirectory, long? folderSize)
    {
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) && !isDirectory)
        {
            return;
        }

        _path = path;
        _document?.Dispose();
        _document = null;
        _text.Document = null;
        _image.Source = null;
        _text.Visibility = _image.Visibility = _message.Visibility = _plainScroll.Visibility = _editor.Visibility = Visibility.Collapsed;
        _editor.Text = string.Empty;
        if (path is null)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        if (isDirectory)
        {
            _message.Text = $"{Path.GetFileName(path.TrimEnd('\\'))}\n\n" + (folderSize is { } size ? $"Size: {PanelColumns.FormatCompactSize(size, culture)}" : "Folder (F3 computes its size)");
            _message.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (ViewerPage.ImageExtensions.Contains(Path.GetExtension(path)))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                _image.Source = image;
                _image.Visibility = Visibility.Visible;
                return;
            }

            if (Settings is { } settings && ViewerPage.FitsTextMode(path, settings.ViewerTextModeMaxMB))
            {
                ShowInEditor(TextFile.Load(path), settings);
                return;
            }

            _document = ViewerDocument.Open(path);
            _text.Document = _document;
            _text.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _message.Text = ex.Message;
            _message.Visibility = Visibility.Visible;
        }
    }

    private void ShowInEditor(TextFile file, AppSettings settings)
    {
        _editor.FontFamily = new FontFamily(settings.ViewerFontFamily + ", Consolas");
        _editor.FontSize = settings.ViewerFontSize;
        _editor.WordWrap = settings.ViewerWrap;
        _editor.Options.IndentationSize = settings.EditorTabSize;
        _editor.SyntaxHighlighting = settings.EditorSyntaxHighlighting ? HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(file.Path)) : null;
        _editor.Text = file.Text;
        _editor.ScrollToHome();
        if (Theme is { } theme)
        {
            // Recolours the syntax definition just chosen.
            TextEditorTheme.Apply(_editor, theme);
        }

        _editor.Visibility = Visibility.Visible;
    }

    /// <summary>A plugin's text for the entry under the cursor (null: empty), kept scrolled where it was on a refresh.</summary>
    public void ShowText(string? text)
    {
        _path = null;
        _document?.Dispose();
        _document = null;
        _text.Document = null;
        _image.Source = null;
        _text.Visibility = _image.Visibility = _message.Visibility = _editor.Visibility = Visibility.Collapsed;
        _plain.Text = text ?? string.Empty;
        _plainScroll.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Dispose()
    {
        _document?.Dispose();
        _document = null;
        _path = null;
    }
}
