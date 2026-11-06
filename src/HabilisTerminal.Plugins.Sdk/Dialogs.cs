namespace HabilisTerminal.Plugins;

/// <summary>Dialogs a plugin describes and the host draws. All of them can be called from any thread.</summary>
public interface IPluginDialogs
{
    Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Information);

    /// <summary>
    /// Asks for confirmation, listing the <paramref name="items"/> affected. <paramref name="destructive"/> makes the
    /// confirm button look dangerous and puts the focus on Cancel.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, IReadOnlyList<string>? items = null, string confirmText = "OK", bool destructive = false);

    /// <summary>One line of text; null when cancelled.</summary>
    Task<string?> InputAsync(string title, string prompt, string? initialValue = null);

    /// <summary>One line the screen must not show, such as a password (SDK 1.5); null when cancelled. Keep the answer in memory only.</summary>
    Task<string?> InputSecretAsync(string title, string prompt) => InputAsync(title, prompt);

    /// <summary>Picks one of <paramref name="choices"/> (filterable list); null when cancelled.</summary>
    Task<string?> ChooseAsync(string title, IReadOnlyList<string> choices, string? current = null);

    /// <summary>Edits a form; true when the user confirmed (the fields then hold the new values).</summary>
    Task<bool> EditAsync(PluginForm form);
}

public enum MessageKind
{
    Information,
    Warning,
    Error,
}

/// <summary>A form for <see cref="IPluginDialogs.EditAsync"/>, e.g. a plugin's settings.</summary>
public sealed class PluginForm(string title, IReadOnlyList<FormField> fields)
{
    public string Title { get; } = title;

    public IReadOnlyList<FormField> Fields { get; } = fields;

    /// <summary>Optional check before the form closes: an error text keeps it open.</summary>
    public Func<PluginForm, string?>? Validate { get; init; }

    public T Field<T>(string key)
        where T : FormField =>
        Fields.OfType<T>().First(f => f.Key == key);
}

/// <summary>A form field. <see cref="Key"/> identifies it; <see cref="Label"/> is shown.</summary>
public abstract class FormField(string key, string label)
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    /// <summary>A hint under the field.</summary>
    public string? Description { get; init; }
}

public sealed class TextField(string key, string label, string value = "") : FormField(key, label)
{
    public string Value { get; set; } = value;

    public string? Placeholder { get; init; }
}

public sealed class NumberField(string key, string label, int value, int minimum = int.MinValue, int maximum = int.MaxValue) : FormField(key, label)
{
    public int Value { get; set; } = value;

    public int Minimum { get; } = minimum;

    public int Maximum { get; } = maximum;
}

public sealed class ToggleField(string key, string label, bool value) : FormField(key, label)
{
    public bool Value { get; set; } = value;
}

/// <summary>One of a list of choices; <see cref="Editable"/> also allows typing a value not in the list.</summary>
public sealed class ChoiceField(string key, string label, IReadOnlyList<string> choices, string value) : FormField(key, label)
{
    public IReadOnlyList<string> Choices { get; } = choices;

    public string Value { get; set; } = value;

    public bool Editable { get; init; }
}

/// <summary>A list of strings edited one per line (e.g. wildcard patterns, regions).</summary>
public sealed class ListField(string key, string label, IReadOnlyList<string> values) : FormField(key, label)
{
    public IReadOnlyList<string> Values { get; set; } = values;
}
