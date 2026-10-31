using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HabilisTerminal.App.Widgets;

/// <summary>A row of the quick pick; details can be filled in later (e.g. drive sizes arriving asynchronously).</summary>
public sealed class QuickPickItem : INotifyPropertyChanged
{
    private string _description = string.Empty;
    private string _detail = string.Empty;
    private double? _usage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Label { get; init; }

    /// <summary>Codicon-like glyph text drawn before the label.</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>The icon as geometry for the template.</summary>
    public System.Windows.Media.Geometry? IconGeometry => Theming.Codicon.GeometryOf(Icon);

    /// <summary>A key that accepts this item at once while the filter is empty (drive letters).</summary>
    public char? Hotkey { get; init; }

    /// <summary>Group header shown above this item (e.g. "Places").</summary>
    public string? Group { get; init; }

    public object? Tag { get; init; }

    public bool IsCurrent { get; init; }

    public string Description
    {
        get => _description;
        set => Set(ref _description, value);
    }

    /// <summary>Right-aligned text.</summary>
    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    /// <summary>0..1 usage bar under the detail (drive fill); null hides it.</summary>
    public double? Usage
    {
        get => _usage;
        set
        {
            Set(ref _usage, value);
            OnPropertyChanged(nameof(HasUsage));
        }
    }

    public bool HasUsage => _usage is not null;

    public bool HasGroup => !string.IsNullOrEmpty(Group);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(name);
        }
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
