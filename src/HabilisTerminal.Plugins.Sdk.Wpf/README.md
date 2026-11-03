# HabilisTerminal.Plugins.Sdk.Wpf

For plugins that draw their panel themselves (SDK 1.2). Target `net10.0-windows` with `<UseWPF>true</UseWPF>`,
reference this package next to `HabilisTerminal.Plugins.Sdk` (both with `PrivateAssets="all" ExcludeAssets="runtime"`:
the host provides them), and implement `IPanelView` on your `IPanelProvider`:

```csharp
public FrameworkElement CreateView(IPanelViewHost host)
{
    var text = new TextBlock { Text = "Hello" };
    text.SetResourceReference(TextBlock.ForegroundProperty, HostBrushes.Foreground);
    return text;
}
```

The manifest asks for `"sdk": "1.2"`. The panel keys stay the host's; keep your elements non-focusable.
