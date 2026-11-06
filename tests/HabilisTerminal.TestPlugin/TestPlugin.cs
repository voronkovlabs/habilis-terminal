using HabilisTerminal.Plugins;

namespace HabilisTerminal.TestPlugin;

/// <summary>A plugin with no dependencies, loaded and unloaded by the loader's tests. A test assembly cannot be: its test framework keeps it alive.</summary>
public sealed class TestPlugin : Plugin
{
    public override IEnumerable<string> Prefixes => ["sample"];
}
