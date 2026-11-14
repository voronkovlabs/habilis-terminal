namespace Habilis.GitHub.Tests;

/// <summary>The token in the macOS Keychain (issue #280).</summary>
public sealed class KeychainTests
{
    [Theory]
    [InlineData("gho_16C7e42F292c6912E7710c838347Ae178B4a", true)]
    [InlineData("github_pat_11ABC_def-1.2", true)]
    [InlineData("octo-cat", true)]
    [InlineData("", false)]
    [InlineData("a\"b", false)]
    [InlineData("a b", false)]
    [InlineData("a\nb", false)]
    [InlineData("a\\b", false)]
    public void Only_plain_logins_and_tokens_go_to_the_keychain_tool(string text, bool plain) =>
        Assert.Equal(plain, KeychainStore.IsPlain(text));

    [Fact]
    public void Each_system_has_its_store()
    {
        var store = TokenStores.ForThisSystem();
        Assert.IsType(OperatingSystem.IsWindows() ? typeof(CredentialManagerStore) : OperatingSystem.IsMacOS() ? typeof(KeychainStore) : typeof(MemoryStore), store);
    }

    [Fact]
    public void A_token_is_kept_replaced_and_forgotten_in_the_keychain()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the macOS Keychain");

        // An item of the test's own, never the app's "HabilisTerminal:github".
        var store = new KeychainStore($"HabilisTerminal:test-{Guid.NewGuid():N}");
        try
        {
            Assert.Null(store.Read());
            store.Write("octocat", "gho_first");
            Assert.Equal("gho_first", store.Read());
            store.Write("octocat", "gho_second");
            Assert.Equal("gho_second", store.Read());
        }
        finally
        {
            store.Delete();
        }

        Assert.Null(store.Read());
    }
}
