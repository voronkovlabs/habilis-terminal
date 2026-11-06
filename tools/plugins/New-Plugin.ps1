<#
.SYNOPSIS
    Makes a new Habilis Terminal plugin project that builds and loads as it is. See docs/plugins.md.
.EXAMPLE
    New-Plugin.ps1 -Name MyFormat -Id me.myformat -Path C:\work -Kind FileFormat -Extension myf
.EXAMPLE
    New-Plugin.ps1 -Name MyPanel -Id me.mypanel -Path C:\work -Kind Panel -Prefix my
#>
param(
    # The project and namespace, e.g. MyFormat.
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z][A-Za-z0-9_.]*$')][string]$Name,
    # The plugin's id: letters, digits, '.', '-', '_'. Choose your own prefix.
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]{1,64}$')][string]$Id,
    # The folder to make the project's folder in.
    [Parameter(Mandatory)][string]$Path,
    # Panel: a list opened from F11, the drive menu and an address. FileFormat: a file opened as a folder.
    [ValidateSet('Panel', 'FileFormat')][string]$Kind = 'Panel',
    # For Panel, required: the address prefix, without the colon.
    [string]$Prefix,
    # For FileFormat, required: the file extension, without the dot.
    [string]$Extension,
    # The folder of habi.exe, whose HabilisTerminal.Plugins.Sdk.dll the plugin compiles against. Found by default.
    [string]$AppFolder
)
$ErrorActionPreference = 'Stop'
if ($Kind -eq 'FileFormat' -and -not $Extension) { throw 'A FileFormat plugin needs -Extension.' }
if ($Kind -eq 'Panel' -and -not $Prefix) { throw 'A Panel plugin needs -Prefix.' }

function Find-Sdk {
    $name = 'HabilisTerminal.Plugins.Sdk.dll'
    $candidates = @()
    if ($AppFolder) { $candidates += $AppFolder }
    $candidates += $PSScriptRoot
    $repo = Split-Path (Split-Path $PSScriptRoot)
    $candidates += Join-Path $repo 'src\HabilisTerminal.App\bin\Release\net10.0-windows10.0.19041.0\win-x64'
    $candidates += Get-Process habi -ErrorAction SilentlyContinue | ForEach-Object { Split-Path $_.Path }
    $candidates += Join-Path $env:LOCALAPPDATA 'HabilisTerminal\current'
    foreach ($folder in $candidates) {
        if ($folder -and (Test-Path (Join-Path $folder $name))) { return (Resolve-Path (Join-Path $folder $name)).Path }
    }
    throw "$name was not found. Pass the folder of habi.exe with -AppFolder."
}

$sdk = Find-Sdk
$sdkVersion = [Reflection.AssemblyName]::GetAssemblyName($sdk).Version
$project = Join-Path $Path $Name
if (Test-Path $project) { throw "$project already exists." }
New-Item $project -ItemType Directory -Force | Out-Null

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Dependencies are copied next to the plugin; the host loads them with its .deps.json. -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <!-- The folder of habi.exe. If it moves, change this, or build with -p:HabiDir=<folder>. -->
    <HabiDir Condition="'`$(HabiDir)' == ''">$(Split-Path $sdk)</HabiDir>
  </PropertyGroup>
  <ItemGroup>
    <!-- The host provides the SDK at run time: Private=false keeps a copy out of the plugin folder. -->
    <Reference Include="HabilisTerminal.Plugins.Sdk">
      <HintPath>`$(HabiDir)\HabilisTerminal.Plugins.Sdk.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $project "$Name.csproj") -Encoding utf8

@"
{
  "id": "$Id",
  "name": "$Name",
  "version": "1.0.0",
  "assembly": "$Name.dll",
  "sdk": "$($sdkVersion.Major).$($sdkVersion.Minor)"
}
"@ | Set-Content (Join-Path $project 'plugin.json') -Encoding utf8

$entryPoints = if ($Kind -eq 'FileFormat') {
@"
    public override IEnumerable<string> FileExtensions => ["$Extension"];

    public override Task<IPanelProvider?> OpenFileAsync(FileOpenRequest request, CancellationToken cancellationToken)
    {
        // A start. Ctrl+PgDn asks about any file, however large: look at its size and first bytes, and return null when
        // the content is not yours. Go to request.InnerPath if it still exists. See the recipe in docs/plugins.md, and
        // https://github.com/voronkovlabs/habilis-terminal/tree/main/plugins/Habilis.Samples.FileFormat for a whole format.
        return Task.FromResult<IPanelProvider?>(new Panel(request.Path));
    }
"@
}
else {
@"
    public override IEnumerable<PluginMenuItem> MenuItems => [new("$Name", _ => Task.FromResult<IPanelProvider?>(new Panel("${Prefix}:")))];

    public override IEnumerable<PluginMenuItem> DriveMenuItems => MenuItems;

    public override IEnumerable<string> Prefixes => ["$Prefix"];

    public override Task<IPanelProvider?> OpenAddressAsync(string address, CancellationToken cancellationToken) =>
        Task.FromResult<IPanelProvider?>(new Panel(address));
"@
}

@"
using System.Runtime.CompilerServices;
using HabilisTerminal.Plugins;

namespace $Name;

public sealed class ${Name}Plugin : Plugin
{
$entryPoints
}

internal sealed class Panel(string location) : IPanelProvider
{
    public string Title => "$Name";

    /// <summary>An address or path that opens this place again: folder history and a reload use it.</summary>
    public string Location => location;

    public IReadOnlyList<PanelLayout> Layouts { get; } = [new PanelLayout("Default", [PanelColumn.Size])];

    public IReadOnlyList<PanelSortKey> SortKeys { get; } = [];

    public IReadOnlyList<InfoLine> Info => [new InfoLine("Location", location)];

    public async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return [new PanelEntry("Hello from $Name.txt", Size: 42)];
    }

    public Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> GoUpAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
"@ | Set-Content (Join-Path $project "${Name}Plugin.cs") -Encoding utf8

$client = if (Test-Path (Join-Path $PSScriptRoot 'habi-plugin.ps1')) { Join-Path $PSScriptRoot 'habi-plugin.ps1' } else { 'habi-plugin.ps1' }
Write-Output "Made $project (SDK $($sdkVersion.Major).$($sdkVersion.Minor), from $sdk)."
Write-Output 'Next:'
Write-Output "  dotnet build `"$project`" -c Release"
Write-Output "  & `"$client`" load `"$project\bin\Release\net10.0`""
Write-Output "Then after each change: dotnet build, and & `"$client`" reload $Id"
