using System.Runtime.CompilerServices;
using HabilisTerminal.Core.Safety;

namespace HabilisTerminal.Core.Tests;

internal static class TestSetup
{
    /// <summary>
    /// For the whole test assembly: any destructive Core operation outside %TEMP%\habi-tests throws, so a broken
    /// test can never touch real files (SPEC §16).
    /// </summary>
    [ModuleInitializer]
    internal static void RestrictToSandboxes() =>
        DestructiveOperationGuard.Restrict(path =>
            path.StartsWith(TestSandbox.RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
}
