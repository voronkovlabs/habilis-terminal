using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;

namespace HabilisTerminal.App;

/// <summary>WPF's <c>SetResourceReference</c> on Avalonia: the property follows a resource, so theme changes show live.</summary>
public static class ResourceReferences
{
    /// <remarks>
    /// The Habi.* keys live in the application's resources (PanelPalette), which an element in a popup does not always
    /// reach through its tree: the application's own resources are watched instead.
    /// </remarks>
    public static void SetResourceReference(this StyledElement element, AvaloniaProperty property, object key) =>
        element.Bind(property, Application.Current is { } application ? application.GetResourceObservable(key) : element.GetResourceObservable(key), BindingPriority.LocalValue);

    /// <summary>The same for a resource that may sit outside the element's tree (a popup's child before it opens).</summary>
    public static T WithResource<T>(this T element, AvaloniaProperty property, object key)
        where T : Control
    {
        element.SetResourceReference(property, key);
        return element;
    }
}
