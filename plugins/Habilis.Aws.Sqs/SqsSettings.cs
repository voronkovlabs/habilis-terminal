using System.Text.Json.Nodes;
using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.Sqs;

/// <summary>The plugin's settings, kept by the host (never credentials).</summary>
internal sealed record SqsSettings
{
    public string CredentialsFile { get; init; } = string.Empty;

    public string Profile { get; init; } = "default";

    public string Region { get; init; } = "us-east-1";

    public IReadOnlyList<string> Regions { get; init; } = ["us-east-1", "us-east-2", "us-west-2", "eu-west-1", "eu-central-1"];

    /// <summary>Queues whose names start with this; blank: all queues.</summary>
    public string NamePrefix { get; init; } = string.Empty;

    /// <summary>Wildcard patterns of queue names to hide.</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = [];

    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>The most messages Enter on a queue receives (issue #397).</summary>
    public int ReceiveLimit { get; init; } = 100;

    /// <summary>Long polling: how long the first receive waits for messages to arrive.</summary>
    public int WaitSeconds { get; init; } = 2;

    /// <summary>How long received messages stay hidden from consumers while the queue is open; 0 hides nothing.</summary>
    public int VisibilitySeconds { get; init; } = 30;

    public const int MaxReceiveLimit = 1000;

    public const int MaxVisibilitySeconds = 43200;

    public static SqsSettings Load(IPluginSettings store)
    {
        var json = store.Load();
        var defaults = new SqsSettings();
        return new SqsSettings
        {
            CredentialsFile = AwsText.Text(json, "credentialsFile", defaults.CredentialsFile),
            Profile = AwsText.Text(json, "profile", defaults.Profile),
            Region = AwsText.Text(json, "region", defaults.Region),
            Regions = AwsText.List(json, "regions", defaults.Regions),
            NamePrefix = AwsText.Text(json, "namePrefix", defaults.NamePrefix),
            Exclusions = AwsText.List(json, "exclusions", defaults.Exclusions),
            TimeoutSeconds = AwsText.Number(json, "timeoutSeconds", defaults.TimeoutSeconds),
            ReceiveLimit = Math.Clamp(AwsText.Number(json, "receiveLimit", defaults.ReceiveLimit), 1, MaxReceiveLimit),
            WaitSeconds = Math.Clamp(AwsText.Number(json, "waitSeconds", defaults.WaitSeconds), 0, 20),
            VisibilitySeconds = Math.Clamp(AwsText.Number(json, "visibilitySeconds", defaults.VisibilitySeconds), 0, MaxVisibilitySeconds),
        };
    }

    public void Save(IPluginSettings store) => store.Save(new JsonObject
    {
        ["credentialsFile"] = CredentialsFile,
        ["profile"] = Profile,
        ["region"] = Region,
        ["regions"] = AwsText.ToJson(Regions),
        ["namePrefix"] = NamePrefix,
        ["exclusions"] = AwsText.ToJson(Exclusions),
        ["timeoutSeconds"] = TimeoutSeconds,
        ["receiveLimit"] = ReceiveLimit,
        ["waitSeconds"] = WaitSeconds,
        ["visibilitySeconds"] = VisibilitySeconds,
    });

    public AwsSession Session(string? profile = null, string? region = null) =>
        new(profile ?? Profile, region ?? Region, CredentialsFile.Length > 0 ? CredentialsFile : null, TimeoutSeconds);

    /// <summary>The settings form (plugin configuration and the panel's Settings command).</summary>
    public static PluginForm Form(SqsSettings current, IReadOnlyList<string> profiles) => new("SQS Queues settings",
    [
        new ChoiceField("profile", "AWS profile", profiles.Count > 0 ? profiles : [current.Profile], current.Profile) { Editable = true },
        new ChoiceField("region", "Region", current.Regions.Concat(AwsRegions.Commercial).Distinct().ToList(), current.Region) { Editable = true },
        new TextField("namePrefix", "Queue name prefix", current.NamePrefix) { Description = "Only queues whose names start with this are listed. Blank: all queues." },
        new ListField("exclusions", "Hide queues matching", current.Exclusions) { Description = "One wildcard pattern per line, e.g. *-dlq or test-*." },
        new ListField("regions", "Regions offered when switching", current.Regions),
        new NumberField("receiveLimit", "Messages to receive when a queue opens", current.ReceiveLimit, 1, MaxReceiveLimit) { Description = "Enter on a queue receives up to this many messages, 10 per request." },
        new NumberField("waitSeconds", "Wait for messages, seconds", current.WaitSeconds, 0, 20) { Description = "Long polling: the first request waits this long for a message to arrive (0 to 20)." },
        new NumberField("visibilitySeconds", "Hide received messages from consumers, seconds", current.VisibilitySeconds, 0, MaxVisibilitySeconds) { Description = "While the queue is open; they are released when you leave it. 0 hides nothing, but then every refresh receives the messages again and raises their receive count." },
        new NumberField("timeoutSeconds", "Request timeout, seconds", current.TimeoutSeconds, 5, 300),
        new TextField("credentialsFile", "AWS credentials file", current.CredentialsFile) { Description = "Blank: the standard %USERPROFILE%\\.aws\\credentials and config." },
    ])
    {
        Validate = form => AwsRegions.IsValid(form.Field<ChoiceField>("region").Value) ? null : "The region is not a valid AWS region name, e.g. eu-west-1.",
    };

    public static SqsSettings FromForm(PluginForm form) => new()
    {
        Profile = form.Field<ChoiceField>("profile").Value,
        Region = form.Field<ChoiceField>("region").Value,
        NamePrefix = form.Field<TextField>("namePrefix").Value,
        Exclusions = form.Field<ListField>("exclusions").Values,
        Regions = form.Field<ListField>("regions").Values is { Count: > 0 } regions ? regions : new SqsSettings().Regions,
        TimeoutSeconds = form.Field<NumberField>("timeoutSeconds").Value,
        ReceiveLimit = form.Field<NumberField>("receiveLimit").Value,
        WaitSeconds = form.Field<NumberField>("waitSeconds").Value,
        VisibilitySeconds = form.Field<NumberField>("visibilitySeconds").Value,
        CredentialsFile = form.Field<TextField>("credentialsFile").Value,
    };
}
