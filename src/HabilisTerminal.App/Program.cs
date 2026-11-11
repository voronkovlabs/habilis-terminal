using Avalonia;
using Velopack;

namespace HabilisTerminal.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The helper of performance diagnostics (issue #164): no window, no install hooks, and it ends with its parent.
        if (args is ["--perf-stack-sampler", var parent] && int.TryParse(parent, out var parentId))
        {
            Core.Diagnostics.ThreadStacks.RunHelper(parentId, Console.In, Console.Out);
            return;
        }

        // Install / update / uninstall hooks run here and may exit the process (SPEC §15).
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The Avalonia application (issue #253); also what the previewer would use.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
