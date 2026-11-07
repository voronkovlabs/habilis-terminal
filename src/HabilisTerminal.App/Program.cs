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

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
