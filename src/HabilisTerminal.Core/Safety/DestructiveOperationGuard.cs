namespace HabilisTerminal.Core.Safety;

/// <summary>
/// Last line of defence for tests (SPEC §16): every destructive operation in Core calls <see cref="Check"/>
/// with the paths it is about to change. Tests install a validator that only allows their sandbox;
/// in the application no validator is installed and the check is free.
/// </summary>
public static class DestructiveOperationGuard
{
    private static Func<string, bool>? _validator;

    /// <summary>Installs a validator; returns a scope that removes it again.</summary>
    public static IDisposable Restrict(Func<string, bool> isAllowed)
    {
        _validator = isAllowed;
        return new Scope();
    }

    /// <exception cref="InvalidOperationException">The path is outside what the installed validator allows.</exception>
    public static void Check(string path)
    {
        if (_validator is { } validator && !validator(Path.GetFullPath(path)))
        {
            throw new InvalidOperationException($"Destructive operation outside the allowed area was blocked: '{path}'.");
        }
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _validator = null;
    }
}
