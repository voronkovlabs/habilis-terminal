using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.SageMaker;

/// <summary>
/// Amazon SageMaker training jobs in a Habilis Terminal panel (issue #47), read-only. Opens from F11, the drive menu
/// or the command line: <c>sm:</c>, <c>sm:profile</c>, <c>sm:profile/region</c>.
/// </summary>
public sealed class SageMakerPlugin : Plugin
{
    private readonly List<WeakReference<SageMakerPanel>> _panels = [];
    private readonly Lazy<PriceTable> _builtInPrices = new(PriceTable.Load);

    internal Func<AwsSession, ISageMakerApi> Connect { get; set; } = session => new SageMakerApi(session);

    /// <summary>The price table for cost estimates; read on first use, not while the host starts.</summary>
    internal Func<PriceTable> Prices { get; set; }

    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public SageMakerPlugin() => Prices = () => _builtInPrices.Value;

    public override IEnumerable<PluginMenuItem> MenuItems => [new("SageMaker training jobs", token => OpenAsync(null, null))];

    public override IEnumerable<PluginMenuItem> DriveMenuItems => MenuItems;

    public override IEnumerable<string> Prefixes => ["sm"];

    public override bool HasConfiguration => true;

    public override Task<IPanelProvider?> OpenAddressAsync(string address, CancellationToken cancellationToken)
    {
        var (profile, region) = AwsText.ParseProfileAddress(address);
        return OpenAsync(profile, region);
    }

    public override async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        var current = SageMakerSettings.Load(Host.Settings);
        var form = SageMakerSettings.Form(current, AwsProfiles.List(current.CredentialsFile));
        if (!await Host.Dialogs.EditAsync(form).ConfigureAwait(false))
        {
            return;
        }

        var settings = SageMakerSettings.FromForm(form);
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
        var settings = SageMakerSettings.Load(Host.Settings);
        var chosenProfile = profile ?? settings.Profile;
        var chosenRegion = region ?? (profile is not null ? AwsProfiles.RegionOf(chosenProfile, settings.CredentialsFile) : null) ?? settings.Region;
        if (!AwsRegions.IsValid(chosenRegion))
        {
            throw new PluginException($"\"{chosenRegion}\" is not a valid AWS region name, e.g. eu-west-1.");
        }

        var panel = new SageMakerPanel(Host, settings, settings.Session(chosenProfile, chosenRegion), Connect, Prices, Clock);
        lock (_panels)
        {
            _panels.RemoveAll(w => !w.TryGetTarget(out _));
            _panels.Add(new WeakReference<SageMakerPanel>(panel));
        }

        return Task.FromResult<IPanelProvider?>(panel);
    }

    private List<SageMakerPanel> OpenPanels()
    {
        lock (_panels)
        {
            return _panels.Select(w => w.TryGetTarget(out var panel) ? panel : null).OfType<SageMakerPanel>().ToList();
        }
    }
}
