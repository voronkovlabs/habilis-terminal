using System.Text.Json.Nodes;
using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3;

/// <summary>The plugin's settings, kept by the host (never credentials).</summary>
internal sealed record S3Settings
{
    public const int MinimumPartSizeMb = 5;
    public const int MaximumPartSizeMb = 512;
    public const int MaximumParallel = 64;

    public string CredentialsFile { get; init; } = string.Empty;

    public string Profile { get; init; } = "default";

    /// <summary>The region the bucket list is requested from; each bucket is then reached in its own region.</summary>
    public string Region { get; init; } = "us-east-1";

    public IReadOnlyList<string> Regions { get; init; } = ["us-east-1", "us-east-2", "us-west-2", "eu-west-1", "eu-central-1"];

    /// <summary>Requests running at once: files, and parts of large files.</summary>
    public int MaxParallel { get; init; } = 8;

    /// <summary>Files larger than this are transferred in parts of this size.</summary>
    public int PartSizeMb { get; init; } = 16;

    /// <summary>Per request; one part of a large file is one request.</summary>
    public int TimeoutSeconds { get; init; } = 60;

    public long PartSize => PartSizeMb * 1024L * 1024L;

    public static S3Settings Load(IPluginSettings store)
    {
        var json = store.Load();
        var defaults = new S3Settings();
        return new S3Settings
        {
            CredentialsFile = AwsText.Text(json, "credentialsFile", defaults.CredentialsFile),
            Profile = AwsText.Text(json, "profile", defaults.Profile),
            Region = AwsText.Text(json, "region", defaults.Region),
            Regions = AwsText.List(json, "regions", defaults.Regions),
            MaxParallel = Math.Clamp(AwsText.Number(json, "maxParallel", defaults.MaxParallel), 1, MaximumParallel),
            PartSizeMb = Math.Clamp(AwsText.Number(json, "partSizeMb", defaults.PartSizeMb), MinimumPartSizeMb, MaximumPartSizeMb),
            TimeoutSeconds = AwsText.Number(json, "timeoutSeconds", defaults.TimeoutSeconds),
        };
    }

    public void Save(IPluginSettings store) => store.Save(new JsonObject
    {
        ["credentialsFile"] = CredentialsFile,
        ["profile"] = Profile,
        ["region"] = Region,
        ["regions"] = AwsText.ToJson(Regions),
        ["maxParallel"] = MaxParallel,
        ["partSizeMb"] = PartSizeMb,
        ["timeoutSeconds"] = TimeoutSeconds,
    });

    public AwsSession Session(string? profile = null) =>
        new(profile ?? Profile, Region, CredentialsFile.Length > 0 ? CredentialsFile : null, TimeoutSeconds);

    /// <summary>The settings form (plugin configuration and the panel's Settings command).</summary>
    public static PluginForm Form(S3Settings current, IReadOnlyList<string> profiles) => new("S3 Browser settings",
    [
        new ChoiceField("profile", "AWS profile", profiles.Count > 0 ? profiles : [current.Profile], current.Profile) { Editable = true },
        new ChoiceField("region", "Default region", current.Regions.Concat(AwsRegions.Commercial).Distinct().ToList(), current.Region) { Editable = true, Description = "Used for the bucket list; each bucket is reached in its own region." },
        new NumberField("maxParallel", "Parallel transfers", current.MaxParallel, 1, MaximumParallel) { Description = "Files, and parts of large files, transferred at once." },
        new NumberField("partSizeMb", "Part size, MB", current.PartSizeMb, MinimumPartSizeMb, MaximumPartSizeMb) { Description = "Larger files are uploaded and downloaded in parts of this size." },
        new NumberField("timeoutSeconds", "Request timeout, seconds", current.TimeoutSeconds, 5, 600) { Description = "Per request; one part of a large file is one request." },
        new ListField("regions", "Regions offered", current.Regions),
        new TextField("credentialsFile", "AWS credentials file", current.CredentialsFile) { Description = "Blank: the standard %USERPROFILE%\\.aws\\credentials and config." },
    ])
    {
        Validate = form => AwsRegions.IsValid(form.Field<ChoiceField>("region").Value) ? null : "The region is not a valid AWS region name, e.g. eu-west-1.",
    };

    public static S3Settings FromForm(PluginForm form) => new()
    {
        Profile = form.Field<ChoiceField>("profile").Value,
        Region = form.Field<ChoiceField>("region").Value,
        MaxParallel = form.Field<NumberField>("maxParallel").Value,
        PartSizeMb = form.Field<NumberField>("partSizeMb").Value,
        TimeoutSeconds = form.Field<NumberField>("timeoutSeconds").Value,
        Regions = form.Field<ListField>("regions").Values is { Count: > 0 } regions ? regions : new S3Settings().Regions,
        CredentialsFile = form.Field<TextField>("credentialsFile").Value,
    };
}
