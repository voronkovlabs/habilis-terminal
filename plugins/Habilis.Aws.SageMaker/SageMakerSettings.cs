using System.Text.Json.Nodes;
using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.SageMaker;

/// <summary>The plugin's settings, kept by the host (never credentials).</summary>
internal sealed record SageMakerSettings
{
    public const int DefaultMaxJobs = 100;
    public const int MaxJobsLimit = 1000;
    private const string AllStatuses = "All";

    /// <summary>The job statuses the filter offers, as the SageMaker API names them.</summary>
    public static IReadOnlyList<string> Statuses { get; } = ["InProgress", "Completed", "Failed", "Stopping", "Stopped"];

    public string CredentialsFile { get; init; } = string.Empty;

    public string Profile { get; init; } = "default";

    public string Region { get; init; } = "us-east-1";

    public IReadOnlyList<string> Regions { get; init; } = ["us-east-1", "us-east-2", "us-west-2", "eu-west-1", "eu-central-1"];

    /// <summary>How many of the most recent jobs are listed (1 to 1000).</summary>
    public int MaxJobs { get; init; } = DefaultMaxJobs;

    /// <summary>Jobs whose names contain this; blank: all jobs.</summary>
    public string NameContains { get; init; } = string.Empty;

    /// <summary>One of <see cref="Statuses"/>; blank: every status.</summary>
    public string StatusFilter { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Colour rows by job status.</summary>
    public bool ColorRows { get; init; } = true;

    public static SageMakerSettings Load(IPluginSettings store)
    {
        var json = store.Load();
        var defaults = new SageMakerSettings();
        var status = AwsText.Text(json, "statusFilter", defaults.StatusFilter);
        return new SageMakerSettings
        {
            CredentialsFile = AwsText.Text(json, "credentialsFile", defaults.CredentialsFile),
            Profile = AwsText.Text(json, "profile", defaults.Profile),
            Region = AwsText.Text(json, "region", defaults.Region),
            Regions = AwsText.List(json, "regions", defaults.Regions),
            MaxJobs = Math.Clamp(AwsText.Number(json, "maxJobs", defaults.MaxJobs), 1, MaxJobsLimit),
            NameContains = AwsText.Text(json, "nameContains", defaults.NameContains),
            StatusFilter = Statuses.Contains(status) ? status : string.Empty,
            TimeoutSeconds = AwsText.Number(json, "timeoutSeconds", defaults.TimeoutSeconds),
            ColorRows = AwsText.Flag(json, "colorRows", defaults.ColorRows),
        };
    }

    public void Save(IPluginSettings store) => store.Save(new JsonObject
    {
        ["credentialsFile"] = CredentialsFile,
        ["profile"] = Profile,
        ["region"] = Region,
        ["regions"] = AwsText.ToJson(Regions),
        ["maxJobs"] = MaxJobs,
        ["nameContains"] = NameContains,
        ["statusFilter"] = StatusFilter,
        ["timeoutSeconds"] = TimeoutSeconds,
        ["colorRows"] = ColorRows,
    });

    public AwsSession Session(string? profile = null, string? region = null) =>
        new(profile ?? Profile, region ?? Region, CredentialsFile.Length > 0 ? CredentialsFile : null, TimeoutSeconds);

    /// <summary>The settings form (plugin configuration and the panel's Settings command).</summary>
    public static PluginForm Form(SageMakerSettings current, IReadOnlyList<string> profiles) => new("SageMaker Training Jobs settings",
    [
        new ChoiceField("profile", "AWS profile", profiles.Count > 0 ? profiles : [current.Profile], current.Profile) { Editable = true },
        new ChoiceField("region", "Region", current.Regions.Concat(AwsRegions.Commercial).Distinct().ToList(), current.Region) { Editable = true },
        new NumberField("maxJobs", "Jobs listed", current.MaxJobs, 1, MaxJobsLimit) { Description = "The most recent jobs, newest first." },
        new TextField("nameContains", "Job name contains", current.NameContains) { Description = "Blank: all jobs." },
        StatusField(current.StatusFilter),
        new ToggleField("colorRows", "Colour rows by job status", current.ColorRows),
        new ListField("regions", "Regions offered when switching", current.Regions),
        new NumberField("timeoutSeconds", "Request timeout, seconds", current.TimeoutSeconds, 5, 300),
        new TextField("credentialsFile", "AWS credentials file", current.CredentialsFile) { Description = "Blank: the standard %USERPROFILE%\\.aws\\credentials and config." },
    ])
    {
        Validate = form => AwsRegions.IsValid(form.Field<ChoiceField>("region").Value) ? null : "The region is not a valid AWS region name, e.g. eu-west-1.",
    };

    public static SageMakerSettings FromForm(PluginForm form) => new()
    {
        Profile = form.Field<ChoiceField>("profile").Value,
        Region = form.Field<ChoiceField>("region").Value,
        MaxJobs = Math.Clamp(form.Field<NumberField>("maxJobs").Value, 1, MaxJobsLimit),
        NameContains = form.Field<TextField>("nameContains").Value.Trim(),
        StatusFilter = StatusOf(form),
        ColorRows = form.Field<ToggleField>("colorRows").Value,
        Regions = form.Field<ListField>("regions").Values is { Count: > 0 } regions ? regions : new SageMakerSettings().Regions,
        TimeoutSeconds = form.Field<NumberField>("timeoutSeconds").Value,
        CredentialsFile = form.Field<TextField>("credentialsFile").Value,
    };

    /// <summary>The Filter command's form (Ctrl+I): name and status only.</summary>
    public static PluginForm FilterForm(SageMakerSettings current) => new("Filter training jobs",
    [
        new TextField("nameContains", "Job name contains", current.NameContains) { Description = "Blank: all jobs." },
        StatusField(current.StatusFilter),
    ]);

    /// <summary>These settings with the filter form's values.</summary>
    public SageMakerSettings WithFilter(PluginForm form) => this with
    {
        NameContains = form.Field<TextField>("nameContains").Value.Trim(),
        StatusFilter = StatusOf(form),
    };

    private static ChoiceField StatusField(string current) =>
        new("statusFilter", "Job status", [AllStatuses, .. Statuses], current.Length > 0 ? current : AllStatuses);

    private static string StatusOf(PluginForm form) =>
        form.Field<ChoiceField>("statusFilter").Value is var status && Statuses.Contains(status) ? status : string.Empty;
}
