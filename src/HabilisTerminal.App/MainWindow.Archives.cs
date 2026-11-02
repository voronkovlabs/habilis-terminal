using System.IO;
using System.Windows;
using System.Windows.Controls;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Archives;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Core.Plugins;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.App;

/// <summary>
/// Archives as folders (issue #51): Enter on a file with an archive extension or Ctrl+PgDn on any file opens it with
/// 7z.dll in a plugin panel (<see cref="ArchiveProvider"/>); ".." at its top returns to the file.
/// </summary>
public partial class MainWindow
{
    /// <summary>The built-in archive support, in the shape plugin panels expect.</summary>
    private static readonly LoadedPlugin Archives = new(
        AppContext.BaseDirectory,
        PluginManifest.Parse("""{ "id": "habi.archives", "name": "Archives", "version": "1.0.0", "assembly": "habi.dll", "sdk": "1.0" }"""),
        new BuiltInArchives(),
        Error: null);

    private async Task OpenArchiveAsync(FilePanel panel, PanelItem item, bool anyFormat)
    {
        var path = Path.Combine(panel.CurrentPath, item.Name);
        ArchiveProvider? provider;
        try
        {
            provider = await Task.Run(() => ArchiveProvider.TryOpen(path, anyFormat, AskArchivePassword));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            panel.Message = ex.Message;
            return;
        }

        if (provider is null)
        {
            if (anyFormat)
            {
                panel.Message = SevenZipLibrary.Default is null ? $"{SevenZipLibrary.FileName} is missing: archives cannot be opened." : $"'{item.Name}' is not an archive.";
            }
            else
            {
                // An archive extension but no archive after all: what Enter does with any other file.
                OnPanelExecuteRequested(panel, item);
            }

            return;
        }

        await panel.OpenPluginAsync(Archives, provider, panel.CurrentPath, item.Name);
    }

    /// <summary>Folder history: a location inside an archive (<c>C:\data\a.zip\docs</c>).</summary>
    private async Task<bool> OpenArchiveLocationAsync(FilePanel panel, string location)
    {
        ArchiveProvider? provider;
        try
        {
            provider = await Task.Run(() => ArchiveProvider.TryOpenLocation(location, AskArchivePassword));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            panel.Message = ex.Message;
            return false;
        }

        if (provider is null)
        {
            return false;
        }

        var archive = provider.Location.Length > 0 ? ArchiveFileOf(location) : null;
        return await panel.OpenPluginAsync(Archives, provider, Path.GetDirectoryName(archive), Path.GetFileName(archive));
    }

    private static string? ArchiveFileOf(string location)
    {
        var file = location.TrimEnd('\\');
        while (file.Length > 3 && !File.Exists(file))
        {
            file = Path.GetDirectoryName(file) ?? string.Empty;
        }

        return File.Exists(file) ? file : null;
    }

    /// <summary>The password of an encrypted archive; asked from 7-Zip's thread, shown on the UI thread.</summary>
    private string? AskArchivePassword(string archiveName) => Dispatcher.Invoke(() =>
    {
        var box = new PasswordBox { MinWidth = 280 };
        var body = new StackPanel();
        var label = new TextBlock { Text = $"'{archiveName}' is encrypted. Password:", Margin = new Thickness(0, 0, 0, 6) };
        label.SetResourceReference(StyleProperty, "Habi.LabelStyle");
        body.Children.Add(label);
        body.Children.Add(box);
        var accepted = ThemedDialog.Show(this, "Password", body,
            [new DialogButton<bool>("OK", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false, box);
        return accepted ? box.Password : null;
    });

    /// <summary>Stands in for a plugin assembly: archives open from files, never from an address prefix.</summary>
    private sealed class BuiltInArchives : Plugin;
}
