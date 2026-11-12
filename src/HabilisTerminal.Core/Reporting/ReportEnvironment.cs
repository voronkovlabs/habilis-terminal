using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HabilisTerminal.Core.Reporting;

/// <summary>The app and Windows versions that go into every report.</summary>
public static class ReportEnvironment
{
    /// <summary>The informational version of <paramref name="assembly"/> without the commit hash.</summary>
    public static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? assembly.GetName().Version?.ToString() ?? "?";

    /// <summary>For example "Windows 11 24H2 (10.0.26100), x64"; elsewhere the runtime's description, such as "Darwin 25.0.0, arm64".</summary>
    public static string Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return $"{RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
        }

        var version = Environment.OSVersion.Version;
        var name = version.Major != 10 ? $"Windows {version.Major}.{version.Minor}" : version.Build >= 22000 ? "Windows 11" : "Windows 10";
        string? release = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            release = key?.GetValue("DisplayVersion") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return $"{name}{(string.IsNullOrEmpty(release) ? "" : " " + release)} ({version.Major}.{version.Minor}.{version.Build}), {arch}";
    }
}
