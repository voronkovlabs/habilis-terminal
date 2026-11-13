using HabilisTerminal.Core.Drives;
using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Tests;

/// <summary>Files as the Finder shows them on a Mac (issue #274).</summary>
public class MacFinderTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/Volumes/Backup", true)]
    [InlineData("/Volumes/My Disk/", true)]
    [InlineData("/Volumes/", false)]
    [InlineData("/dev", false)]
    [InlineData("/System/Volumes/Data", false)]
    [InlineData("/System/Library/AssetsV2/com_apple_MobileAsset_UAF", false)]
    public void Only_the_start_disk_and_the_volumes_count_as_drives(string root, bool shown) =>
        Assert.Equal(shown, DriveCatalog.IsMacVolume(root));

    [Theory]
    [InlineData("Safari.app", true)]
    [InlineData("Report.pages", true)]
    [InlineData("Photos Library.photoslibrary", true)]
    [InlineData("Project.xcodeproj", true)]
    [InlineData("Documents", false)]
    [InlineData("archive.zip", false)]
    public void Packages_are_the_finders(string name, bool package) => Assert.Equal(package, MacPackages.IsPackage(name));
}
