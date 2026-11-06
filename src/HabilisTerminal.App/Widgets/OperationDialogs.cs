using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HabilisTerminal.App.Panels;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.App.Widgets;

/// <summary>The dialogs of file operations (SPEC §8), built on <see cref="ThemedDialog"/>.</summary>
public static class OperationDialogs
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    /// <summary>F5 / F6 (and Shift variants): destination and options, or null when cancelled.</summary>
    public static (string Destination, CopyOptions Options)? CopyOrMove(Window owner, bool move, IReadOnlyList<string> names, string initialDestination)
    {
        var what = names.Count == 1 ? $"'{names[0]}'" : $"{names.Count} items";
        var title = move ? (names.Count == 1 ? $"Rename or move {what} to:" : $"Move {what} to:") : $"Copy {what} to:";
        var destination = new TextBox { Text = initialDestination };
        var policies = new (string Label, ExistingFilePolicy Policy)[]
        {
            ("Ask", ExistingFilePolicy.Ask), ("Overwrite", ExistingFilePolicy.Overwrite), ("Skip", ExistingFilePolicy.Skip),
            ("Rename", ExistingFilePolicy.Rename), ("Append", ExistingFilePolicy.Append), ("Only newer", ExistingFilePolicy.OnlyNewer),
        };
        var policyRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        var radios = policies.Select(p => new RadioButton { Content = p.Label, GroupName = "existing", Tag = p.Policy, IsChecked = p.Policy == ExistingFilePolicy.Ask }).ToArray();
        foreach (var radio in radios)
        {
            policyRow.Children.Add(radio);
        }

        var accessRights = new CheckBox { Content = "Copy access rights" };
        var linkContents = new CheckBox { Content = "Copy contents of symbolic links" };

        var body = new StackPanel();
        body.Children.Add(destination);
        body.Children.Add(Description("Existing files:", top: 12));
        body.Children.Add(policyRow);
        body.Children.Add(accessRights);
        body.Children.Add(linkContents);

        var accepted = ThemedDialog.Show(owner, title, body,
            [new DialogButton<bool>(move ? "Move" : "Copy", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)],
            false, destination);
        if (!accepted || string.IsNullOrWhiteSpace(destination.Text))
        {
            return null;
        }

        var policy = (ExistingFilePolicy)radios.First(r => r.IsChecked == true).Tag;
        return (destination.Text.Trim(), new CopyOptions
        {
            ExistingFiles = policy,
            CopyAccessRights = accessRights.IsChecked == true,
            CopyLinkContents = linkContents.IsChecked == true,
        });
    }

    /// <summary>F7: name(s) and the "several names" option, or null.</summary>
    public static (string Text, bool Multiple)? MakeDirectory(Window owner)
    {
        var name = new TextBox();
        var multiple = new CheckBox { Content = "Process multiple names (separated by ;)", Margin = new Thickness(0, 10, 0, 0) };
        var body = new StackPanel();
        body.Children.Add(name);
        body.Children.Add(Description(@"Nested paths such as a\b\c are created in full.", top: 6));
        body.Children.Add(multiple);
        var accepted = ThemedDialog.Show(owner, "Create folder", body,
            [new DialogButton<bool>("Create", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false, name);
        return accepted && !string.IsNullOrWhiteSpace(name.Text) ? (name.Text, multiple.IsChecked == true) : null;
    }

    /// <summary>F2: a new name, with the part before the extension selected for typing over; null when cancelled.</summary>
    public static string? Rename(Window owner, string name, bool isFolder)
    {
        var box = new TextBox { Text = name };
        box.Loaded += (_, _) => box.Select(0, Core.Operations.RenameItem.EditableLength(name, isFolder));
        var accepted = ThemedDialog.Show(owner, $"Rename '{name}'", box,
            [new DialogButton<bool>("Rename", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false, box);
        return accepted ? box.Text : null;
    }

    /// <summary>Shift+F4: the name of the file to edit; null when cancelled.</summary>
    public static string? NewFile(Window owner)
    {
        var name = new TextBox();
        var body = new StackPanel();
        body.Children.Add(name);
        body.Children.Add(Description("The file is created when you save it. An existing file opens as it is.", top: 6));
        var accepted = ThemedDialog.Show(owner, "New file", body,
            [new DialogButton<bool>("Edit", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false, name);
        return accepted && !string.IsNullOrWhiteSpace(name.Text) ? name.Text.Trim().Trim('"') : null;
    }

    /// <summary>F8 / Shift+Del confirmation.</summary>
    public static bool ConfirmDelete(Window owner, IReadOnlyList<string> names, bool permanent)
    {
        var what = names.Count == 1 ? $"'{names[0]}'" : $"{names.Count} items";
        var body = new StackPanel();
        body.Children.Add(Label(permanent ? $"Delete {what} permanently?" : $"Move {what} to the Recycle Bin?"));
        if (permanent)
        {
            body.Children.Add(Description("This cannot be undone.", top: 6));
        }

        return ThemedDialog.Show(owner, permanent ? "Delete permanently" : "Delete", body,
            [new DialogButton<bool>("_Delete", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false);
    }

    /// <summary>Overwrite / Skip / Rename / Append / Only newer / Cancel, with "apply to all".</summary>
    public static ConflictDecision Conflict(Window owner, ConflictInfo conflict)
    {
        var newer = conflict.Source.LastWriteTimeUtc > conflict.Target.LastWriteTimeUtc ? "source" : conflict.Source.LastWriteTimeUtc < conflict.Target.LastWriteTimeUtc ? "target" : null;
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(int row, string caption, FileEntry entry, string path, bool isNewer)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var label = Description(caption, top: 4);
            Grid.SetRow(label, row);
            grid.Children.Add(label);
            var details = Label($"{PanelColumns.FormatCompactSize(entry.Length, Culture)} · {entry.LastWriteTimeUtc.ToLocalTime().ToString("g", Culture)}{(isNewer ? "  (newer)" : string.Empty)}");
            details.Margin = new Thickness(12, 4, 0, 0);
            details.ToolTip = path;
            Grid.SetRow(details, row);
            Grid.SetColumn(details, 1);
            grid.Children.Add(details);
        }

        Row(0, "New:", conflict.Source, conflict.SourcePath, newer == "source");
        Row(1, "Existing:", conflict.Target, conflict.TargetPath, newer == "target");
        var applyToAll = new CheckBox { Content = "Apply to all", Margin = new Thickness(0, 12, 0, 0) };
        var body = new StackPanel();
        body.Children.Add(Label($"'{Path.GetFileName(conflict.TargetPath)}' already exists in {Path.GetDirectoryName(conflict.TargetPath)}."));
        body.Children.Add(grid);
        body.Children.Add(applyToAll);

        var action = ThemedDialog.Show(owner, "File exists", body,
        [
            new DialogButton<ConflictAction>("_Overwrite", ConflictAction.Overwrite, IsDefault: true),
            new DialogButton<ConflictAction>("_Skip", ConflictAction.Skip),
            new DialogButton<ConflictAction>("_Rename", ConflictAction.Rename),
            new DialogButton<ConflictAction>("_Append", ConflictAction.Append),
            new DialogButton<ConflictAction>("Only _newer", ConflictAction.OverwriteIfNewer),
            new DialogButton<ConflictAction>("Cancel", ConflictAction.Cancel, IsCancel: true),
        ], ConflictAction.Cancel);
        return new ConflictDecision(action, applyToAll.IsChecked == true);
    }

    /// <summary>Retry / Skip / Skip all / Cancel.</summary>
    public static ErrorAction Error(Window owner, OperationError failure)
    {
        var body = new StackPanel();
        body.Children.Add(Label($"Cannot {failure.Operation} '{failure.Path}'."));
        var message = Description(failure.Message, top: 8);
        message.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Error");
        body.Children.Add(message);
        return ThemedDialog.Show(owner, "Error", body,
        [
            new DialogButton<ErrorAction>("_Retry", ErrorAction.Retry, IsDefault: true),
            new DialogButton<ErrorAction>("_Skip", ErrorAction.Skip),
            new DialogButton<ErrorAction>("Skip _all", ErrorAction.SkipAll),
            new DialogButton<ErrorAction>("Cancel", ErrorAction.Cancel, IsCancel: true),
        ], ErrorAction.Cancel);
    }

    /// <summary>Yes / Yes to all / No / Cancel questions raised during an operation.</summary>
    public static ConfirmationAnswer Confirm(Window owner, Confirmation confirmation)
    {
        var name = Path.GetFileName(confirmation.Path.TrimEnd('\\'));
        var (title, question) = confirmation.Kind switch
        {
            ConfirmationKind.OverwriteProtected => ("Protected file", $"'{name}' is {confirmation.Detail}. Overwrite it?"),
            ConfirmationKind.DeleteProtected => ("Protected file", $"'{name}' is read-only, hidden or system. Delete it?"),
            ConfirmationKind.DeleteNonEmptyDirectory => ("Folder is not empty", $"'{name}' is not empty. Delete it with everything inside?"),
            ConfirmationKind.DeletePermanently => ("Cannot use the Recycle Bin", $"'{name}' cannot be moved to the Recycle Bin: {confirmation.Detail}. Delete it permanently?"),
            ConfirmationKind.CopyLinkContents => ("Symbolic link", $"'{name}' cannot be recreated as a link: {confirmation.Detail}. Copy what it points to instead?"),
            ConfirmationKind.InsufficientSpace => ("Not enough space", $"There may not be enough free space in {confirmation.Path} ({confirmation.Detail}). Continue?"),
            _ => ("Confirm", confirmation.Path),
        };

        var body = new StackPanel();
        body.Children.Add(Label(question));
        return ThemedDialog.Show(owner, title, body,
        [
            new DialogButton<ConfirmationAnswer>("_Yes", ConfirmationAnswer.Yes, IsDefault: true),
            new DialogButton<ConfirmationAnswer>("Yes to _all", ConfirmationAnswer.YesToAll),
            new DialogButton<ConfirmationAnswer>("_No", ConfirmationAnswer.No),
            new DialogButton<ConfirmationAnswer>("Cancel", ConfirmationAnswer.Cancel, IsCancel: true),
        ], ConfirmationAnswer.Cancel);
    }

    /// <summary>Ctrl+A: tri-state attribute boxes, dates, recursion; null when cancelled.</summary>
    public static AttributeChange? Attributes(Window owner, IReadOnlyList<string> paths)
    {
        var infos = paths.Select(p => new FileInfo(p)).ToArray();
        var attributes = infos.Select(i => i.Attributes).ToArray();
        bool? State(FileAttributes flag) =>
            attributes.All(a => (a & flag) != 0) ? true : attributes.All(a => (a & flag) == 0) ? false : null;

        var flags = new (string Label, FileAttributes Flag)[]
        {
            ("_Read-only", FileAttributes.ReadOnly), ("A_rchive", FileAttributes.Archive), ("_Hidden", FileAttributes.Hidden),
            ("_System", FileAttributes.System), ("_Not content indexed", FileAttributes.NotContentIndexed), ("_Temporary", FileAttributes.Temporary),
        };
        var boxes = flags.Select(f => new CheckBox { Content = f.Label, IsThreeState = paths.Count > 1, IsChecked = State(f.Flag), Tag = f.Flag }).ToArray();
        var compressed = new CheckBox { Content = "_Compressed", IsThreeState = paths.Count > 1, IsChecked = State(FileAttributes.Compressed) };
        var encrypted = new CheckBox { Content = "_Encrypted", IsThreeState = paths.Count > 1, IsChecked = State(FileAttributes.Encrypted) };
        compressed.Checked += (_, _) => encrypted.IsChecked = false;
        encrypted.Checked += (_, _) => compressed.IsChecked = false;

        var single = paths.Count == 1 ? infos[0] : null;
        var created = DateBox(single?.CreationTime);
        var modified = DateBox(single?.LastWriteTime);
        var accessed = DateBox(single?.LastAccessTime);
        var recursive = new CheckBox { Content = "Apply to subfolders and files", Visibility = infos.Any(i => (i.Attributes & FileAttributes.Directory) != 0) ? Visibility.Visible : Visibility.Collapsed };

        var columns = new UniformGrid2(2);
        foreach (var box in boxes)
        {
            columns.Add(box);
        }

        columns.Add(compressed);
        columns.Add(encrypted);

        var dates = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void DateRow(int row, string caption, TextBox box, DateTime? original)
        {
            dates.RowDefinitions.Add(new RowDefinition());
            var label = Description(caption, top: 6);
            label.Margin = new Thickness(0, 6, 12, 0);
            Grid.SetRow(label, row);
            dates.Children.Add(label);
            box.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            dates.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 0, 0) };
            var now = new Button { Content = "Current", MinWidth = 64 };
            now.Click += (_, _) => box.Text = DateTime.Now.ToString("G", Culture);
            var reset = new Button { Content = "Original", MinWidth = 64 };
            reset.Click += (_, _) => box.Text = original?.ToString("G", Culture) ?? string.Empty;
            buttons.Children.Add(now);
            buttons.Children.Add(reset);
            Grid.SetRow(buttons, row);
            Grid.SetColumn(buttons, 2);
            dates.Children.Add(buttons);
        }

        DateRow(0, "Created", created, single?.CreationTime);
        DateRow(1, "Modified", modified, single?.LastWriteTime);
        DateRow(2, "Accessed", accessed, single?.LastAccessTime);

        var body = new StackPanel();
        body.Children.Add(Description(paths.Count == 1 ? paths[0] : $"{paths.Count} items", top: 0));
        body.Children.Add(columns.Panel);
        body.Children.Add(dates);
        body.Children.Add(Description(paths.Count > 1 ? "Empty dates are left unchanged." : string.Empty, top: 4));
        body.Children.Add(recursive);

        var accepted = ThemedDialog.Show(owner, "Attributes", body,
            [new DialogButton<bool>("Set", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false);
        if (!accepted)
        {
            return null;
        }

        FileAttributes set = 0, clear = 0;
        foreach (var box in boxes)
        {
            var flag = (FileAttributes)box.Tag;
            if (box.IsChecked == true)
            {
                set |= flag;
            }
            else if (box.IsChecked == false)
            {
                clear |= flag;
            }
        }

        DateTime? Parse(TextBox box, DateTime? original) =>
            DateTime.TryParse(box.Text, Culture, DateTimeStyles.AssumeLocal, out var value) && (original is null || Math.Abs((value - original.Value).TotalSeconds) >= 1)
                ? value.ToUniversalTime()
                : null;

        return new AttributeChange
        {
            Set = set,
            Clear = clear,
            Compressed = compressed.IsChecked,
            Encrypted = encrypted.IsChecked,
            CreationTimeUtc = Parse(created, single?.CreationTime),
            LastWriteTimeUtc = Parse(modified, single?.LastWriteTime),
            LastAccessTimeUtc = Parse(accessed, single?.LastAccessTime),
            Recursive = recursive.IsChecked == true,
        };
    }

    /// <summary>Simple message with one OK button (refusals, validation errors).</summary>
    public static void Message(Window owner, string title, string text)
    {
        var body = new StackPanel();
        body.Children.Add(Label(text));
        ThemedDialog.Show(owner, title, body, [new DialogButton<bool>("OK", true, IsDefault: true, IsCancel: true)], false);
    }

    private static TextBox DateBox(DateTime? value) => new() { Text = value?.ToString("G", Culture) ?? string.Empty };

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(FrameworkElement.StyleProperty, "Habi.LabelStyle");
        return block;
    }

    private static TextBlock Description(string text, double top)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 0) };
        block.SetResourceReference(FrameworkElement.StyleProperty, "Habi.DescriptionStyle");
        return block;
    }

    /// <summary>Lays out controls in a fixed number of columns, row by row.</summary>
    private sealed class UniformGrid2(int columns)
    {
        private int _count;

        public Grid Panel { get; } = CreateGrid(columns);

        public void Add(UIElement element)
        {
            var row = _count / columns;
            if (row >= Panel.RowDefinitions.Count)
            {
                Panel.RowDefinitions.Add(new RowDefinition());
            }

            Grid.SetRow(element, row);
            Grid.SetColumn(element, _count % columns);
            Panel.Children.Add(element);
            _count++;
        }

        private static Grid CreateGrid(int columns)
        {
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            for (var i = 0; i < columns; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
            }

            return grid;
        }
    }
}
