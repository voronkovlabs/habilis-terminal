using Velopack;

namespace HabilisTerminal.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Install / update / uninstall hooks run here and may exit the process (SPEC §15).
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
