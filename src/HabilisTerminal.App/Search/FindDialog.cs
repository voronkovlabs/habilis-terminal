using System.Windows;
using System.Windows.Controls;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Search;

namespace HabilisTerminal.App.Search;

/// <summary>What the Alt+F7 dialog asks, kept for the next time as Far does.</summary>
internal sealed record FindOptions(string Masks, string Text, bool MatchCase, bool WholeWords, bool Folders, SearchScope Scope)
{
    public static FindOptions Default { get; } = new("*", string.Empty, false, false, false, SearchScope.FromCurrentFolder);
}

/// <summary>The Alt+F7 dialog (issue #198), laid out as Far's "Find file".</summary>
internal static class FindDialog
{
    private static readonly (SearchScope Scope, string Label)[] Scopes =
    [
        (SearchScope.FromCurrentFolder, "From the current _folder"),
        (SearchScope.CurrentFolderOnly, "The current folder _only"),
        (SearchScope.SelectedFolders, "Selected fold_ers"),
        (SearchScope.FromDriveRoot, "From the _root of the drive"),
        (SearchScope.AllLocalDrives, "_All local drives"),
    ];

    /// <summary>Null when cancelled. A mask that does not parse is told and asked again.</summary>
    public static FindOptions? Ask(Window owner, FindOptions last, bool hasSelectedFolders)
    {
        while (true)
        {
            var masks = new TextBox { Text = last.Masks };
            var text = new TextBox { Text = last.Text };
            var matchCase = new CheckBox { Content = "Case _sensitive", IsChecked = last.MatchCase, Margin = new Thickness(0, 8, 16, 0) };
            var wholeWords = new CheckBox { Content = "_Whole words", IsChecked = last.WholeWords, Margin = new Thickness(0, 8, 0, 0) };
            var folders = new CheckBox { Content = "Search for fol_ders too", IsChecked = last.Folders, Margin = new Thickness(0, 10, 0, 0) };
            var radios = Scopes.Select(s => new RadioButton
            {
                Content = s.Label,
                GroupName = "scope",
                Tag = s.Scope,
                IsChecked = s.Scope == (last.Scope == SearchScope.SelectedFolders && !hasSelectedFolders ? SearchScope.FromCurrentFolder : last.Scope),
                IsEnabled = s.Scope != SearchScope.SelectedFolders || hasSelectedFolders,
                Margin = new Thickness(0, 4, 0, 0),
            }).ToArray();

            var body = new StackPanel();
            body.Children.Add(Label("File masks, separated by ; or , (exclusions after |):"));
            body.Children.Add(masks);
            body.Children.Add(Label("Containing text (empty: names only):", top: 10));
            body.Children.Add(text);
            var options = new WrapPanel();
            options.Children.Add(matchCase);
            options.Children.Add(wholeWords);
            body.Children.Add(options);
            body.Children.Add(folders);
            body.Children.Add(Label("Search in:", top: 12));
            foreach (var radio in radios)
            {
                body.Children.Add(radio);
            }

            var accepted = ThemedDialog.Show(owner, "Find file", body,
                [new DialogButton<bool>("Find", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false, masks);
            if (!accepted)
            {
                return null;
            }

            var chosen = new FindOptions(
                string.IsNullOrWhiteSpace(masks.Text) ? "*" : masks.Text.Trim(),
                text.Text,
                matchCase.IsChecked == true,
                wholeWords.IsChecked == true,
                folders.IsChecked == true,
                (SearchScope)radios.First(r => r.IsChecked == true).Tag);
            if (FileMask.TryParse(chosen.Masks, out _))
            {
                return chosen;
            }

            OperationDialogs.Message(owner, "Find file", $"'{chosen.Masks}' is not a valid mask.");
            last = chosen;
        }
    }

    private static TextBlock Label(string text, double top = 0)
    {
        var label = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 4) };
        label.SetResourceReference(FrameworkElement.StyleProperty, "Habi.DescriptionStyle");
        return label;
    }
}
