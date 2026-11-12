using System.Runtime.InteropServices;
using HabilisTerminal.Core.Diagnostics;

namespace HabilisTerminal.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class DeepDiagnosticsTests
{
    private static readonly DateTime At = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    private static PerfPeriod Period(params PerfStall[] stalls) =>
        new(At, At.AddMinutes(1), [], stalls, 0, 0);

    [WindowsTheory]
    [InlineData(2, 0, "running")]
    [InlineData(1, 0, "ready")]
    [InlineData(5, 0, "wait: Executive")]
    [InlineData(5, 37, "wait: WrAlertByThreadId")]
    [InlineData(5, 99, "wait: reason 99")]
    public void Raw_wait_reasons_get_their_names(int state, int reason, string expected) =>
        Assert.Equal(expected, NativeThreads.Describe(state, reason));

    [WindowsFact]
    public void A_sleeping_thread_is_seen_waiting()
    {
        using var thread = new BlockedThread();

        var state = NativeThreads.StateOf(Environment.ProcessId, thread.NativeId);

        Assert.Equal("wait: DelayExecution", state);
    }

    [WindowsFact]
    public void A_thread_that_is_not_there_has_no_state() =>
        Assert.Null(NativeThreads.StateOf(Environment.ProcessId, 1));

    [WindowsFact]
    public void States_are_summed_up_with_shares()
    {
        var seen = new Dictionary<string, int> { ["ready"] = 1, ["wait: Executive"] = 3 };

        Assert.Equal("wait: Executive 75%, ready 25%", NativeThreads.Summarize(seen));
        Assert.Null(NativeThreads.Summarize(new Dictionary<string, int>()));
    }

    [WindowsFact]
    public void The_stack_of_a_waiting_thread_names_modules_and_functions_but_no_paths()
    {
        using var thread = new BlockedThread();
        using var stacks = new ThreadStacks(Environment.ProcessId);

        var stack = stacks.Walk(thread.NativeId);

        Assert.NotNull(stack);
        Assert.Contains("ntdll!", stack, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", stack, StringComparison.Ordinal);
        Assert.True(thread.IsRunningAfterTheWalk(), "the thread was left suspended");
    }

    [WindowsFact]
    public void A_thread_that_is_not_there_has_no_stack()
    {
        using var stacks = new ThreadStacks(Environment.ProcessId);

        Assert.Null(stacks.Walk(1));
    }

    [WindowsFact]
    public void The_helper_answers_each_line_and_ends_with_its_input()
    {
        using var thread = new BlockedThread();
        var output = new StringWriter();

        ThreadStacks.RunHelper(Environment.ProcessId, new StringReader($"{thread.NativeId}\nnot a number\n"), output);

        var lines = output.ToString().Split(Environment.NewLine);
        Assert.Contains("ntdll!", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, lines[1]);
    }

    [WindowsFact]
    public void A_wait_chain_names_a_critical_section_and_its_holder()
    {
        var section = Marshal.AllocHGlobal(64);
        InitializeCriticalSection(section);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            EnterCriticalSection(section);
            entered.Set();
            release.Wait();
            LeaveCriticalSection(section);
        }) { IsBackground = true, Name = "Holder" };
        holder.Start();
        entered.Wait();
        var waiterId = 0;
        var waiter = new Thread(() =>
        {
            Volatile.Write(ref waiterId, (int)GetCurrentThreadId());
            EnterCriticalSection(section);
            LeaveCriticalSection(section);
        }) { IsBackground = true };
        waiter.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref waiterId) != 0, 5000);

        // The waiter needs a moment to get into the wait.
        string? chain = null;
        SpinWait.SpinUntil(() => (chain = WaitChain.Describe(Volatile.Read(ref waiterId), _ => "Holder")) is not null, 5000);

        release.Set();
        holder.Join();
        waiter.Join();

        // On GitHub's Windows Server runners the traversal answers with nothing; the report then only lacks this figure.
        if (chain is null && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is not null)
        {
            Assert.Skip("Wait chains are not available here.");
        }

        Assert.Equal("critical section < thread \"Holder\"", chain);
    }

    [WindowsFact]
    public void Busy_percent_compares_two_moments()
    {
        Assert.Equal(75, MachineLoad.BusyPercent((1000, 500), (2000, 750)));
        Assert.Null(MachineLoad.BusyPercent(null, (2000, 750)));
        Assert.Null(MachineLoad.BusyPercent((2000, 750), (2000, 750)));
        Assert.NotNull(MachineLoad.ProcessorTimes());
    }

    [WindowsFact]
    public void The_report_says_how_busy_the_machine_was_and_warns_about_wall_clock_times()
    {
        var body = PerfReport.Build([Period() with { MachineCpu = 90 }, Period() with { MachineCpu = 100 }], "1", "W", 1).Body;

        Assert.Contains("processors 95% busy on average, 100% in the busiest period", body, StringComparison.Ordinal);
        Assert.Contains("wall-clock times", body, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void Stalls_are_grouped_by_stack_with_the_new_figures_in_the_longest_table()
    {
        var stack = "ntdll!NtWaitForSingleObject < dwrite!Something";
        var stall = new PerfStall(At, 3000, 0, 0, "panel.render.first-text", null, "wait: Executive", 5, 50)
        {
            Stack = stack,
            ThreadStates = "wait: Executive 100%",
            WaitChain = "ALPC < process MsMpEng",
            MachineCpu = 99,
            DiskQueue = 12,
            DiskTransferMs = 45.5,
        };
        var body = PerfReport.Build([Period(stall, stall with { WallMs = 1000 })], "1", "W", 1).Body;

        Assert.Contains($"- 2 stalls, 4000 ms, longest 3000 ms: `{stack}`", body, StringComparison.Ordinal);
        Assert.Contains("| 99% | 12 | 45.5 | wait: Executive 100% | ALPC < process MsMpEng |", body, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void Busy_stacks_add_up_by_thread_and_stack()
    {
        var period = Period() with { BusyStacks = [new PerfBusyThread("unnamed, ntdll.dll", "a!b", 1000), new PerfBusyThread("unnamed, ntdll.dll", "a!b", 500)] };
        var body = PerfReport.Build([period], "1", "W", 1).Body;

        Assert.Contains("- unnamed, ntdll.dll, 1.5 s: `a!b`", body, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void Data_from_an_older_version_still_makes_a_report()
    {
        var body = PerfReport.Build([Period(new PerfStall(At, 200, 100, 0, "key", null))], "1", "W", 1).Body;

        Assert.DoesNotContain("Stacks of the stalled", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Where busy threads ran", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Machine:", body, StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern void InitializeCriticalSection(IntPtr section);

    [DllImport("kernel32.dll")]
    private static extern void EnterCriticalSection(IntPtr section);

    [DllImport("kernel32.dll")]
    private static extern void LeaveCriticalSection(IntPtr section);

    /// <summary>A thread parked in a native wait, with its Windows id.</summary>
    private sealed class BlockedThread : IDisposable
    {
        private readonly Thread _thread;
        private int _nativeId;

        /// <summary>
        /// Asleep until released (issue #243): a fixed sleep of 1.5 s ran out under load before the symbols were loaded,
        /// and the walk then found no thread.
        /// </summary>
        public BlockedThread()
        {
            _thread = new Thread(() =>
            {
                Volatile.Write(ref _nativeId, (int)GetCurrentThreadId());
                try
                {
                    Thread.Sleep(Timeout.Infinite);
                }
                catch (ThreadInterruptedException)
                {
                    // Released.
                }
            }) { IsBackground = true };
            _thread.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref _nativeId) != 0, 5000);
            Thread.Sleep(200);
        }

        public int NativeId => Volatile.Read(ref _nativeId);

        /// <summary>A thread left suspended would not wake to be released.</summary>
        public bool IsRunningAfterTheWalk()
        {
            _thread.Interrupt();
            return _thread.Join(5000);
        }

        public void Dispose()
        {
            _thread.Interrupt();
            _thread.Join();
        }
    }
}
