param(
    [Parameter(Mandatory)][ValidateSet('shot', 'screen', 'keys', 'text', 'rect', 'click', 'dblclick', 'rclick', 'drag', 'wheel', 'move')] [string]$Action,
    [string]$Process = 'habi',
    [string]$Out,
    [string]$Arg
)
# Window capture and keyboard / mouse input for a single target window (never the whole desktop).
# See docs/development.md, "UI checks". Examples:
#   uitool.ps1 shot  -Process pid:1234 -Out shot.png      # PrintWindow of the window (no popups)
#   uitool.ps1 screen -Process pid:1234 -Out menu.png     # real screen pixels inside the window (popups included)
#   uitool.ps1 keys  -Process pid:1234 -Arg 'f9 left ctrl+p esc'   # names: see $vk below, f1..f12, single letters
#   uitool.ps1 text  -Process pid:1234 -Arg "cd 'C:\some dir'"
#   uitool.ps1 click -Process pid:1234 -Arg '150,70'       # window-relative physical pixels; also dblclick, move
# For frames milliseconds apart, dot-source it once (". uitool.ps1 rect -Process pid:N") and call
# [UiTool]::Chord / [UiTool]::Shot($h, path) directly: each script run compiles the type and costs ~1 s.
# Focus() refuses to send keys when the target is not in the foreground, so keys never land in the owner's windows.
Add-Type -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives, System.Threading.Thread, System.Runtime.InteropServices, System.Runtime, System.Threading, System.Private.Windows.GdiPlus, System.Private.Windows.Core -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

public static class UiTool
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    public static RECT Bounds(IntPtr h)
    {
        RECT r;
        if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out r);
        return r;
    }

    public static void Shot(IntPtr h, string path)
    {
        RECT w; GetWindowRect(h, out w);
        RECT f = Bounds(h);
        using (var full = new Bitmap(w.Right - w.Left, w.Bottom - w.Top))
        {
            using (var g = Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                PrintWindow(h, hdc, 2); // PW_RENDERFULLCONTENT
                g.ReleaseHdc(hdc);
            }
            var crop = new Rectangle(f.Left - w.Left, f.Top - w.Top, f.Right - f.Left, f.Bottom - f.Top);
            using (var cut = full.Clone(crop, full.PixelFormat)) cut.Save(path, ImageFormat.Png);
        }
    }

    // Screen pixels inside the window rectangle: includes popups drawn over the window.
    public static void ScreenShot(IntPtr h, string path)
    {
        RECT f = Bounds(h);
        using (var bmp = new Bitmap(f.Right - f.Left, f.Bottom - f.Top))
        {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Left, f.Top, 0, 0, bmp.Size);
            bmp.Save(path, ImageFormat.Png);
        }
    }

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public static void Focus(IntPtr h)
    {
        if (GetForegroundWindow() == h) return;
        uint targetPid, foregroundPid;
        GetWindowThreadProcessId(h, out targetPid);
        GetWindowThreadProcessId(GetForegroundWindow(), out foregroundPid);
        if (targetPid == foregroundPid) return; // a dialog of the same app is active: keys go to it
        keybd_event(0x12, 0, 0, IntPtr.Zero);      // Alt down unlocks SetForegroundWindow
        SetForegroundWindow(h);
        keybd_event(0x12, 0, 2, IntPtr.Zero);
        Thread.Sleep(150);
        uint p1, p2; GetWindowThreadProcessId(h, out p1); GetWindowThreadProcessId(GetForegroundWindow(), out p2);
        if (p1 != p2) throw new InvalidOperationException("Target app is not in the foreground; keys not sent.");
    }

    static INPUT Key(ushort vk, ushort scan, uint flags)
    {
        var i = new INPUT(); i.type = 1; i.ki.wVk = vk; i.ki.wScan = scan; i.ki.dwFlags = flags; return i;
    }

    // Chord like "ctrl+o", "ctrl+f1", "alt+f4", "tab", "enter".
    public static void Chord(ushort[] modifiers, ushort vk)
    {
        foreach (var m in modifiers) SendInput(1, new[] { Key(m, 0, 0) }, Marshal.SizeOf(typeof(INPUT)));
        SendInput(1, new[] { Key(vk, 0, 0) }, Marshal.SizeOf(typeof(INPUT)));
        SendInput(1, new[] { Key(vk, 0, 2) }, Marshal.SizeOf(typeof(INPUT)));
        for (int k = modifiers.Length - 1; k >= 0; k--) SendInput(1, new[] { Key(modifiers[k], 0, 2) }, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(60);
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    // Mouse at window-relative physical pixels.
    public static void Mouse(IntPtr h, int x, int y, int clicks)
    {
        var r = Bounds(h);
        SetCursorPos(r.Left + x, r.Top + y);
        Thread.Sleep(120);
        for (int i = 0; i < clicks; i++) { mouse_event(2, 0, 0, 0, IntPtr.Zero); mouse_event(4, 0, 0, 0, IntPtr.Zero); Thread.Sleep(60); }
        Thread.Sleep(150);
    }

    // Right click at window-relative physical pixels.
    public static void RightClick(IntPtr h, int x, int y)
    {
        var r = Bounds(h);
        SetCursorPos(r.Left + x, r.Top + y);
        Thread.Sleep(120);
        mouse_event(8, 0, 0, 0, IntPtr.Zero); mouse_event(16, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(150);
    }

    // Left button down at one point, moved in steps, up at another.
    public static void Drag(IntPtr h, int x1, int y1, int x2, int y2)
    {
        var r = Bounds(h);
        SetCursorPos(r.Left + x1, r.Top + y1);
        Thread.Sleep(120);
        mouse_event(2, 0, 0, 0, IntPtr.Zero);
        for (int i = 1; i <= 10; i++) { SetCursorPos(r.Left + x1 + (x2 - x1) * i / 10, r.Top + y1 + (y2 - y1) * i / 10); Thread.Sleep(30); }
        mouse_event(4, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(150);
    }

    // Wheel notches (positive: away from the user) at window-relative physical pixels.
    public static void Wheel(IntPtr h, int x, int y, int notches)
    {
        var r = Bounds(h);
        SetCursorPos(r.Left + x, r.Top + y);
        Thread.Sleep(120);
        mouse_event(0x0800, 0, 0, (uint)(notches * 120), IntPtr.Zero);
        Thread.Sleep(150);
    }

    public static void Text(string s)
    {
        foreach (char c in s)
        {
            SendInput(1, new[] { Key(0, c, 4) }, Marshal.SizeOf(typeof(INPUT)));      // KEYEVENTF_UNICODE
            SendInput(1, new[] { Key(0, c, 4 | 2) }, Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(15);
        }
    }
}
'@

if ($Process.StartsWith("title:")) {
    Add-Type -Name F -Namespace U2 -MemberDefinition '[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);'
    $h = [U2.F]::FindWindow($null, $Process.Substring(6))
    if ($h -eq [IntPtr]::Zero) { throw "No window titled $Process" }
} elseif ($Process.StartsWith("pid:")) {
    $h = (Get-Process -Id ([int]$Process.Substring(4)) -ErrorAction Stop).MainWindowHandle
} else {
    $p = Get-Process -Name $Process -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { throw "No window for process $Process" }
    $h = $p.MainWindowHandle
}

$vk = @{ ctrl = 0x11; alt = 0x12; shift = 0x10; tab = 0x09; enter = 0x0D; esc = 0x1B; space = 0x20; up = 0x26; down = 0x28; left = 0x25; right = 0x27; back = 0x08; pageup = 0x21; pagedown = 0x22; home = 0x24; end = 0x23; backslash = 0xDC; ins = 0x2D; del = 0x2E; add = 0x6B; subtract = 0x6D; multiply = 0x6A }
1..12 | ForEach-Object { $vk["f$_"] = 0x6F + $_ }

switch ($Action) {
    'shot' { [UiTool]::Shot($h, $Out); $Out }
    'screen' { [UiTool]::Focus($h); Start-Sleep -Milliseconds 200; [UiTool]::ScreenShot($h, $Out); $Out }
    'rect' { $r = [UiTool]::Bounds($h); "$($r.Left),$($r.Top),$($r.Right),$($r.Bottom)" }
    'keys' {
        [UiTool]::Focus($h)
        foreach ($chord in $Arg -split ' ') {
            $parts = $chord.ToLowerInvariant() -split '\+'
            $main = $parts[-1]
            $code = if ($vk.ContainsKey($main)) { $vk[$main] } else { [int][char]$main.ToUpperInvariant() }
            $mods = [uint16[]]@($parts[0..($parts.Count - 2)] | Where-Object { $parts.Count -gt 1 } | ForEach-Object { $vk[$_] })
            [UiTool]::Chord($mods, [uint16]$code)
        }
    }
    'text' { [UiTool]::Focus($h); [UiTool]::Text($Arg) }
    'click' { [UiTool]::Focus($h); $xy = $Arg -split ','; [UiTool]::Mouse($h, [int]$xy[0], [int]$xy[1], 1) }
    'dblclick' { [UiTool]::Focus($h); $xy = $Arg -split ','; [UiTool]::Mouse($h, [int]$xy[0], [int]$xy[1], 2) }
    'move' { [UiTool]::Focus($h); $xy = $Arg -split ','; [UiTool]::Mouse($h, [int]$xy[0], [int]$xy[1], 0) }
    'rclick' { [UiTool]::Focus($h); $xy = $Arg -split ','; [UiTool]::RightClick($h, [int]$xy[0], [int]$xy[1]) }
    # -Arg x1,y1,x2,y2
    'drag' { [UiTool]::Focus($h); $xy = $Arg -split ','; [UiTool]::Drag($h, [int]$xy[0], [int]$xy[1], [int]$xy[2], [int]$xy[3]) }
    # -Arg x,y,notches (negative: towards the user, scrolling down)
    'wheel' { [UiTool]::Focus($h); $xy = $Arg -split ','; [UiTool]::Wheel($h, [int]$xy[0], [int]$xy[1], [int]$xy[2]) }
}
