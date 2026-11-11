# HabilisTerminal.Plugins.Sdk.Avalonia

For plugins that draw their panel themselves (SDK 1.2). Reference this package and `Avalonia` (the host's version)
next to `HabilisTerminal.Plugins.Sdk`, all with `PrivateAssets="all" ExcludeAssets="runtime"`: the host provides them.
Then implement `IPanelView` on your `IPanelProvider`:

```csharp
public Control CreateView(IPanelViewHost host)
{
    var text = new TextBlock { Text = "Hello" };
    text.Bind(TextBlock.ForegroundProperty, Application.Current!.GetResourceObservable(HostBrushes.Foreground));
    return text;
}
```

The manifest asks for `"sdk": "1.2"`. The panel keys stay the host's; keep your controls non-focusable.
