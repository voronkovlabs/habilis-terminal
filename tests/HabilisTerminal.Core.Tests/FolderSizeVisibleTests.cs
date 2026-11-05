using System.Reflection;
using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Tests;

public class FolderSizeVisibleTests
{
    /// <summary>
    /// A panel calls SetVisible while it draws. The worker holds its lock at the lowest priority, so waiting for that lock
    /// could freeze the window (issue #136). That visible folders still go first is covered in FolderSizeResumeTests.
    /// </summary>
    [Fact]
    public void Telling_what_is_visible_does_not_wait_for_the_workers_lock()
    {
        using var worker = new FolderSizeWorker(new FolderSizeCache(), backgroundMode: false);
        var gate = typeof(FolderSizeWorker).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        lock (gate)
        {
            var call = new Thread(() => worker.SetVisible(new object(), [@"C:\a"])) { IsBackground = true };
            call.Start();
            Assert.True(call.Join(TimeSpan.FromSeconds(10)), "SetVisible waited for the lock");
        }
    }
}
