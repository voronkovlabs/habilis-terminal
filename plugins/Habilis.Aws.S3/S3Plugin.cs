using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3;

/// <summary>
/// Amazon S3 in a Habilis Terminal panel (issue #46). Opens from F11, the drive menu or the command line:
/// <c>s3:</c> (the bucket list), <c>s3://bucket/</c>, <c>s3://bucket/prefix/</c>, also after <c>cd</c>.
/// </summary>
public sealed class S3Plugin : Plugin
{
    private readonly List<WeakReference<S3Panel>> _panels = [];

    internal Func<AwsSession, IS3Api> Connect { get; set; } = session => new S3Api(session);

    public override IEnumerable<PluginMenuItem> MenuItems => [new("S3 buckets", token => OpenAsync(S3Location.Buckets))];

    public override IEnumerable<PluginMenuItem> DriveMenuItems => MenuItems;

    public override IEnumerable<string> Prefixes => ["s3"];

    public override IEnumerable<string> AddressCommands => ["cd"];

    public override bool HasConfiguration => true;

    public override Task<IPanelProvider?> OpenAddressAsync(string address, CancellationToken cancellationToken) =>
        OpenAsync(S3Location.Parse(address));

    public override async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        var current = S3Settings.Load(Host.Settings);
        var form = S3Settings.Form(current, AwsProfiles.List(current.CredentialsFile));
        if (!await Host.Dialogs.EditAsync(form).ConfigureAwait(false))
        {
            return;
        }

        var settings = S3Settings.FromForm(form);
        settings.Save(Host.Settings);

        // Changes apply to the open panels too.
        foreach (var panel in OpenPanels())
        {
            panel.Apply(settings);
            Host.Refresh(panel);
        }
    }

    private Task<IPanelProvider?> OpenAsync(S3Location location)
    {
        var settings = S3Settings.Load(Host.Settings);
        if (!AwsRegions.IsValid(settings.Region))
        {
            throw new PluginException($"\"{settings.Region}\" is not a valid AWS region name, e.g. eu-west-1. Fix the default region in the S3 Browser settings.");
        }

        var panel = new S3Panel(Host, settings, settings.Session(), location, Connect);
        lock (_panels)
        {
            _panels.RemoveAll(w => !w.TryGetTarget(out _));
            _panels.Add(new WeakReference<S3Panel>(panel));
        }

        return Task.FromResult<IPanelProvider?>(panel);
    }

    private List<S3Panel> OpenPanels()
    {
        lock (_panels)
        {
            return _panels.Select(w => w.TryGetTarget(out var panel) ? panel : null).OfType<S3Panel>().ToList();
        }
    }
}
