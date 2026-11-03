using System.Text.Json.Nodes;
using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Gpu;

/// <summary>
/// A GPU monitor like nvtop (issue #70): this computer's NVIDIA GPUs (<c>gpu:</c>) and SageMaker training jobs in
/// progress (<c>gpu:sm</c>, <c>gpu:sm:profile/region</c>). Opens from F11, the drive menu or the command line.
/// </summary>
public sealed class GpuPlugin : Plugin
{
    internal Func<IGpuSource> Local { get; set; }

    internal Func<ISystemSource> System { get; set; } = () => new WindowsSystem();

    internal Func<AwsSession, ICloudGpuApi> Cloud { get; set; } = session => new CloudGpuApi(session);

    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public GpuPlugin() => Local = () => new NvidiaSmi(Clock);

    public override IEnumerable<PluginMenuItem> MenuItems =>
    [
        new("GPU monitor", _ => OpenLocal()),
        new("GPU monitor · SageMaker", _ => OpenCloud(null, null)),
    ];

    public override IEnumerable<PluginMenuItem> DriveMenuItems => MenuItems;

    public override IEnumerable<string> Prefixes => ["gpu"];

    public override bool HasConfiguration => true;

    public override Task<IPanelProvider?> OpenAddressAsync(string address, CancellationToken cancellationToken)
    {
        var rest = address.Trim();
        rest = rest[(rest.IndexOf(':') + 1)..].Trim();
        if (rest.StartsWith("sm", StringComparison.OrdinalIgnoreCase))
        {
            var (profile, region) = AwsText.ParseProfileAddress(rest);
            return OpenCloud(profile, region);
        }

        return OpenLocal();
    }

    public override async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        var current = GpuSettings.Load(Host.Settings);
        var form = GpuSettings.Form(current, AwsProfiles.List(current.CredentialsFile));
        if (await Host.Dialogs.EditAsync(form).ConfigureAwait(false))
        {
            GpuSettings.FromForm(form).Save(Host.Settings);
        }
    }

    private Task<IPanelProvider?> OpenLocal()
    {
        var settings = GpuSettings.Load(Host.Settings);
        return Task.FromResult<IPanelProvider?>(new LocalGpuPanel(Host, Local(), System(), TimeSpan.FromSeconds(settings.LocalIntervalSeconds)));
    }

    private Task<IPanelProvider?> OpenCloud(string? profile, string? region)
    {
        var settings = GpuSettings.Load(Host.Settings);
        var chosenProfile = profile ?? settings.Profile;
        var chosenRegion = region ?? (profile is not null ? AwsProfiles.RegionOf(chosenProfile, settings.CredentialsFile) : null) ?? settings.Region;
        if (!AwsRegions.IsValid(chosenRegion))
        {
            throw new PluginException($"\"{chosenRegion}\" is not a valid AWS region name, e.g. eu-west-1.");
        }

        var session = new AwsSession(chosenProfile, chosenRegion, settings.CredentialsFile.Length > 0 ? settings.CredentialsFile : null, settings.TimeoutSeconds);
        return Task.FromResult<IPanelProvider?>(new CloudGpuPanel(Host, session, Cloud, Clock, TimeSpan.FromSeconds(settings.CloudIntervalSeconds)));
    }
}

/// <summary>The plugin's settings, kept by the host (never credentials).</summary>
internal sealed record GpuSettings
{
    public int LocalIntervalSeconds { get; init; } = 2;

    public int CloudIntervalSeconds { get; init; } = 60;

    public string Profile { get; init; } = "default";

    public string Region { get; init; } = "us-east-1";

    public string CredentialsFile { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; } = 30;

    public static GpuSettings Load(IPluginSettings store)
    {
        var json = store.Load();
        var defaults = new GpuSettings();
        return new GpuSettings
        {
            LocalIntervalSeconds = Math.Clamp(AwsText.Number(json, "localIntervalSeconds", defaults.LocalIntervalSeconds), 1, 60),
            CloudIntervalSeconds = Math.Clamp(AwsText.Number(json, "cloudIntervalSeconds", defaults.CloudIntervalSeconds), 30, 600),
            Profile = AwsText.Text(json, "profile", defaults.Profile),
            Region = AwsText.Text(json, "region", defaults.Region),
            CredentialsFile = AwsText.Text(json, "credentialsFile", defaults.CredentialsFile),
            TimeoutSeconds = AwsText.Number(json, "timeoutSeconds", defaults.TimeoutSeconds),
        };
    }

    public void Save(IPluginSettings store) => store.Save(new JsonObject
    {
        ["localIntervalSeconds"] = LocalIntervalSeconds,
        ["cloudIntervalSeconds"] = CloudIntervalSeconds,
        ["profile"] = Profile,
        ["region"] = Region,
        ["credentialsFile"] = CredentialsFile,
        ["timeoutSeconds"] = TimeoutSeconds,
    });

    public static PluginForm Form(GpuSettings current, IReadOnlyList<string> profiles) => new("GPU Monitor settings",
    [
        new NumberField("localIntervalSeconds", "Refresh this computer's GPUs every, seconds", current.LocalIntervalSeconds, 1, 60),
        new NumberField("cloudIntervalSeconds", "Refresh SageMaker every, seconds", current.CloudIntervalSeconds, 30, 600) { Description = "CloudWatch has one value per minute." },
        new ChoiceField("profile", "AWS profile (SageMaker)", profiles.Count > 0 ? profiles : [current.Profile], current.Profile) { Editable = true },
        new ChoiceField("region", "Region (SageMaker)", AwsRegions.Commercial.Prepend(current.Region).Distinct().ToList(), current.Region) { Editable = true },
        new NumberField("timeoutSeconds", "Request timeout, seconds", current.TimeoutSeconds, 5, 300),
        new TextField("credentialsFile", "AWS credentials file", current.CredentialsFile) { Description = "Blank: the standard %USERPROFILE%\\.aws\\credentials and config." },
    ])
    {
        Validate = form => AwsRegions.IsValid(form.Field<ChoiceField>("region").Value) ? null : "The region is not a valid AWS region name, e.g. eu-west-1.",
    };

    public static GpuSettings FromForm(PluginForm form) => new()
    {
        LocalIntervalSeconds = form.Field<NumberField>("localIntervalSeconds").Value,
        CloudIntervalSeconds = form.Field<NumberField>("cloudIntervalSeconds").Value,
        Profile = form.Field<ChoiceField>("profile").Value,
        Region = form.Field<ChoiceField>("region").Value,
        TimeoutSeconds = form.Field<NumberField>("timeoutSeconds").Value,
        CredentialsFile = form.Field<TextField>("credentialsFile").Value,
    };
}
