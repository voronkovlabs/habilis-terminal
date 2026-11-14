using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.Core.Tests;

/// <summary>"Skip this version" is remembered across starts (issue #298).</summary>
public class SkippedUpdateTests
{
    [Fact]
    public void A_skipped_update_survives_a_restart()
    {
        using var sandbox = new TestSandbox();
        var store = new StateStore(Path.Combine(sandbox.Path, "state.json"));
        Assert.Null(store.Load().SkippedUpdate);

        store.Save(new AppState { SkippedUpdate = "0.1.77" });

        Assert.Equal("0.1.77", store.Load().SkippedUpdate);
    }
}
