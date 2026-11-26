using HabilisTerminal.Core.Drives;

namespace HabilisTerminal.Core.Tests;

/// <summary>Issue #407: which drives eject, what stands on them, and waiting until Windows has taken one away.</summary>
public class DriveEjectTests
{
    [Theory]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.CDRom, true)]
    [InlineData(DriveType.Fixed, false)]
    [InlineData(DriveType.Network, false)]
    [InlineData(DriveType.Ram, false)]
    public void Removable_drives_and_discs_eject(DriveType type, bool ejects) => Assert.Equal(ejects, DriveEject.CanEject(type));

    [Fact]
    public void A_path_is_on_its_drive_whatever_the_case_or_depth()
    {
        Assert.True(DriveEject.IsOn(@"E:\Music\Album", @"E:\"));
        Assert.True(DriveEject.IsOn(@"e:\", "E:"));
        Assert.True(DriveEject.IsOn("E:", @"e:\"));
        Assert.False(DriveEject.IsOn(@"C:\Users", @"E:\"));
        Assert.False(DriveEject.IsOn(null, @"E:\"));
        Assert.False(DriveEject.IsOn("relative", @"E:\"));
        Assert.Equal(@"E:\", DriveEject.RootOf(@"E:\a\b"));
        Assert.Null(DriveEject.RootOf(""));
    }

    [Fact]
    public async Task Waiting_ends_when_the_drive_goes_or_the_time_is_up()
    {
        var checks = 0;
        Assert.True(await DriveEject.WaitGoneAsync(() => ++checks >= 3, TimeSpan.FromSeconds(10)));
        Assert.Equal(3, checks);

        var started = DateTime.UtcNow;
        Assert.False(await DriveEject.WaitGoneAsync(() => false, TimeSpan.FromMilliseconds(600)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_system_drive_is_not_gone()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.False(DriveEject.IsGone(system));
        Assert.True(DriveEject.IsGone(@"Q:\", () => []));
    }
}
