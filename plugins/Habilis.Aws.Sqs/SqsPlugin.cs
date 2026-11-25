using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.Sqs;

/// <summary>
/// Amazon SQS queues in a Habilis Terminal panel (issue #48). Opens from F11, the drive menu or the command line:
/// <c>sqs:</c>, <c>sqs:profile</c>, <c>sqs:profile/region</c>.
/// </summary>
public sealed class SqsPlugin : Plugin
{
    private readonly List<WeakReference<SqsPanel>> _panels = [];

    internal Func<AwsSession, ISqsApi> Connect { get; set; } = session => new SqsApi(session);

    public override IEnumerable<PluginMenuItem> MenuItems => [new("SQS queues", token => OpenAsync(null, null))];

    public override IEnumerable<PluginMenuItem> DriveMenuItems => MenuItems;

    public override IEnumerable<string> Prefixes => ["sqs"];

    public override bool HasConfiguration => true;

    /// <summary>A queue's URL, "https://sqs.us-east-1.amazonaws.com/123456789012/name" (#382).</summary>
    public override bool HandlesAddress(string address) => QueueUrl.Parse(address) is not null;

    public override async Task<IPanelProvider?> OpenAddressAsync(string address, CancellationToken cancellationToken)
    {
        // "cd <queue URL>": the queue's region in the default profile, the cursor on the queue.
        if (QueueUrl.Parse(address) is { } queue)
        {
            var provider = await OpenAsync(null, queue.Region).ConfigureAwait(false);
            if (provider is SqsPanel panel)
            {
                panel.InitialCursor = queue.Name;
            }

            return provider;
        }

        var (profile, region) = AwsText.ParseProfileAddress(address);
        return await OpenAsync(profile, region).ConfigureAwait(false);
    }

    public override async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        var current = SqsSettings.Load(Host.Settings);
        var form = SqsSettings.Form(current, AwsProfiles.List(current.CredentialsFile));
        if (!await Host.Dialogs.EditAsync(form).ConfigureAwait(false))
        {
            return;
        }

        var settings = SqsSettings.FromForm(form);
        settings.Save(Host.Settings);

        // Changes apply to the open panels too.
        foreach (var panel in OpenPanels())
        {
            panel.Apply(settings);
            Host.Refresh(panel);
        }
    }

    private Task<IPanelProvider?> OpenAsync(string? profile, string? region)
    {
        var settings = SqsSettings.Load(Host.Settings);
        var chosenProfile = profile ?? settings.Profile;
        var chosenRegion = region ?? (profile is not null ? AwsProfiles.RegionOf(chosenProfile, settings.CredentialsFile) : null) ?? settings.Region;
        if (!AwsRegions.IsValid(chosenRegion))
        {
            throw new PluginException($"\"{chosenRegion}\" is not a valid AWS region name, e.g. eu-west-1.");
        }

        var panel = new SqsPanel(Host, settings, settings.Session(chosenProfile, chosenRegion), Connect);
        lock (_panels)
        {
            _panels.RemoveAll(w => !w.TryGetTarget(out _));
            _panels.Add(new WeakReference<SqsPanel>(panel));
        }

        return Task.FromResult<IPanelProvider?>(panel);
    }

    private List<SqsPanel> OpenPanels()
    {
        lock (_panels)
        {
            return _panels.Select(w => w.TryGetTarget(out var panel) ? panel : null).OfType<SqsPanel>().ToList();
        }
    }
}

/// <summary>The parts of a queue's URL: https://sqs.REGION.amazonaws.com/ACCOUNT/NAME (#382).</summary>
internal sealed partial record QueueUrl(string Region, string Account, string Name)
{
    public static QueueUrl? Parse(string address)
    {
        var match = Pattern().Match(address.Trim());
        return match.Success ? new QueueUrl(match.Groups["region"].Value, match.Groups["account"].Value, match.Groups["name"].Value) : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^https://sqs\.(?<region>[a-z0-9-]+)\.amazonaws\.com(?:\.cn)?/(?<account>\d+)/(?<name>[A-Za-z0-9_-]{1,80}(?:\.fifo)?)/?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}
