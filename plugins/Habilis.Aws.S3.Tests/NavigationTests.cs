using System.Text;
using Habilis.Aws.Tests;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3.Tests;

public class NavigationTests
{
    [Theory]
    [InlineData("s3:", null, "")]
    [InlineData("s3://", null, "")]
    [InlineData("S3: ", null, "")]
    [InlineData("s3://bucket", "bucket", "")]
    [InlineData("s3://bucket/", "bucket", "")]
    [InlineData("s3://bucket/a/b/", "bucket", "a/b/")]
    [InlineData("s3://bucket/a/b", "bucket", "a/b/")]
    [InlineData("s3:bucket/a/b", "bucket", "a/b/")]
    [InlineData(" s3://bucket/a//b c/ ", "bucket", "a//b c/")]
    [InlineData("s3://bucket//", "bucket", "/")]
    public void Addresses_name_bucket_and_prefix(string address, string? bucket, string prefix) =>
        Assert.Equal(new S3Location(bucket, prefix), S3Location.Parse(address));

    [Theory]
    [InlineData("bucket", "")]
    [InlineData("bucket", "a/")]
    [InlineData("bucket", "a//b?/ c%41/")]
    [InlineData("bucket", "/")]
    public void Locations_reopen_where_they_were(string bucket, string prefix)
    {
        var location = new S3Location(bucket, prefix);
        Assert.Equal(location, S3Location.Parse(location.Address));
    }

    [Theory]
    [InlineData("", "us-east-1")]
    [InlineData(null, "us-east-1")]
    [InlineData("EU", "eu-west-1")]
    [InlineData("ap-southeast-2", "ap-southeast-2")]
    public void Bucket_locations_become_regions(string? location, string region) =>
        Assert.Equal(region, BucketRegions.FromLocation(location));

    [Fact]
    public async Task Bucket_list_shows_buckets_as_folders()
    {
        var api = new FakeS3().AddBucket("logs", "EU").AddBucket("photos", "ap-southeast-2").Add("logs", "one.txt", "12345");
        var panel = await S3Test.OpenAsync(api);

        var entries = await Listing.AllAsync(panel);

        Assert.Equal(["logs", "photos"], entries.Select(e => e.Name));
        Assert.All(entries, e => Assert.True(e.IsFolder));
        Assert.All(entries, e => Assert.Equal(PanelIcons.Bucket, e.Icon)); // issue #69
        Assert.Equal(5, entries[0].Size);
        Assert.Equal("5 B", entries[0].Cells![PanelColumn.SizeKey].Text);
        Assert.Equal(1L, entries[0].Cells![S3Panel.ObjectCountKey].SortValue);
        Assert.Equal(0, entries[1].Size);
        Assert.Equal(1, api.MetricsCalls);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), entries[0].Modified);
        Assert.Equal("s3:", panel.Location);
        Assert.Equal("S3 · default", panel.Title);
        Assert.Contains(panel.Info, l => l is { Label: "Buckets shown", Value: "2" });
    }

    [Fact]
    public async Task Bucket_metrics_use_a_normal_single_name_column_and_the_bucket_list_has_no_parent_row()
    {
        var panel = await S3Test.OpenAsync(new FakeS3().AddBucket("logs", "EU").Add("logs", "a.txt", "x"));
        Assert.False(panel.ShowsParent);
        Assert.Equal(1, panel.NameColumns);

        Assert.True(await panel.EnterAsync(await S3Test.EntryAsync(panel, "logs"), CancellationToken.None));
        Assert.True(panel.ShowsParent);
        Assert.Equal(1, panel.NameColumns);
    }

    [Fact]
    public async Task Missing_cloudwatch_permission_does_not_hide_the_bucket_list()
    {
        var api = new FakeS3
        {
            MetricsError = new Amazon.CloudWatch.AmazonCloudWatchException("Access denied")
            {
                ErrorCode = "AccessDenied",
                StatusCode = System.Net.HttpStatusCode.Forbidden,
            },
        }.AddBucket("logs", "EU");
        var panel = await S3Test.OpenAsync(api);

        var entries = await Listing.AllAsync(panel);

        Assert.Equal("logs", Assert.Single(entries).Name);
        Assert.Null(entries[0].Size);
        Assert.Contains(panel.Info, line => line.Label == "Bucket metrics" && line.Value.Contains("cloudwatch:GetMetricData", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Listing_shows_prefixes_as_folders_and_objects_as_files_without_the_folder_marker()
    {
        var api = new FakeS3()
            .Add("b", "docs/", string.Empty)
            .Add("b", "docs/readme.txt", "hello")
            .Add("b", "docs/old/a.txt", "a")
            .Add("b", "docs/old/b.txt", "b")
            .Add("b", "docs/new/", string.Empty)
            .Add("b", "top.txt", "t");
        var panel = await S3Test.OpenAsync(api, "s3://b/docs/");

        var entries = await Listing.AllAsync(panel);

        Assert.Equal(["new", "old", "readme.txt"], entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        var file = entries.Single(e => e.Name == "readme.txt");
        Assert.False(file.IsFolder);
        Assert.Equal(5L, file.Size);
        Assert.Equal(DateTimeKind.Utc, file.Modified!.Value.Kind);
        Assert.Equal("STANDARD", file.Cells![S3Panel.StorageClassKey].Text);
        Assert.True(entries.Single(e => e.Name == "old").IsFolder);
        Assert.Equal("S3 · default · b/docs/", panel.Title);
        Assert.Contains(panel.Info, l => l is { Label: "Folders shown", Value: "2" });
        Assert.Contains(panel.Info, l => l is { Label: "Objects shown", Value: "1" });
        Assert.Contains(panel.Info, l => l is { Label: "Region", Value: "us-east-1" });
    }

    [Fact]
    public async Task Large_folders_arrive_page_by_page()
    {
        var api = new FakeS3 { PageSize = 2 };
        for (var i = 0; i < 5; i++)
        {
            api.Add("b", $"f{i}.txt", "x");
        }

        var panel = await S3Test.OpenAsync(api, "s3://b/");
        var batches = new List<int>();
        await foreach (var batch in panel.ListAsync(CancellationToken.None))
        {
            batches.Add(batch.Count);
        }

        Assert.Equal([2, 2, 1], batches);
    }

    [Fact]
    public async Task Enter_and_up_walk_down_to_a_folder_and_back_out_of_the_plugin()
    {
        var api = new FakeS3().AddBucket("b", "EU", listRegion: true).Add("b", "a/c/file.txt", "x");
        var panel = await S3Test.OpenAsync(api);

        Assert.True(await panel.EnterAsync(await S3Test.EntryAsync(panel, "b"), CancellationToken.None));
        Assert.Equal("s3://b/", panel.Location);
        Assert.True(await panel.EnterAsync(await S3Test.EntryAsync(panel, "a"), CancellationToken.None));
        Assert.True(await panel.EnterAsync(await S3Test.EntryAsync(panel, "c"), CancellationToken.None));
        Assert.Equal("s3://b/a/c/", panel.Location);
        Assert.False(await panel.EnterAsync(await S3Test.EntryAsync(panel, "file.txt"), CancellationToken.None));

        Assert.True(await panel.GoUpAsync(CancellationToken.None));
        Assert.Equal("s3://b/a/", panel.Location);
        Assert.True(await panel.GoUpAsync(CancellationToken.None));
        Assert.Equal("s3://b/", panel.Location);
        Assert.True(await panel.GoUpAsync(CancellationToken.None));
        Assert.Equal("s3:", panel.Location);
        Assert.False(await panel.GoUpAsync(CancellationToken.None));

        // The region came with the bucket list (and every call went to eu-west-1, or the fake would refuse it).
        Assert.Equal(0, api.LocationCalls);
    }

    [Fact]
    public async Task A_typed_address_finds_the_bucket_region_once()
    {
        var api = new FakeS3().AddBucket("b", "EU").Add("b", "x.txt", "x");
        var panel = await S3Test.OpenAsync(api, "s3://b/");

        await Listing.AllAsync(panel);
        await Listing.AllAsync(panel);

        Assert.Equal(1, api.LocationCalls);
        Assert.Contains(panel.Info, l => l is { Label: "Region", Value: "eu-west-1" });
    }

    [Fact]
    public async Task Empty_path_segments_are_folders_that_can_be_entered()
    {
        var api = new FakeS3().Add("b", "a//b.txt", "x").Add("b", "a/c.txt", "y").Add("b", "/root.txt", "z");
        var panel = await S3Test.OpenAsync(api, "s3://b/");

        Assert.Equal([KeyNames.EmptySegment, "a"], (await Listing.AllAsync(panel)).Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.True(await panel.EnterAsync(await S3Test.EntryAsync(panel, "a"), CancellationToken.None));
        Assert.Equal([KeyNames.EmptySegment, "c.txt"], (await Listing.AllAsync(panel)).Select(e => e.Name).Order(StringComparer.Ordinal));

        Assert.True(await panel.EnterAsync(await S3Test.EntryAsync(panel, KeyNames.EmptySegment), CancellationToken.None));
        Assert.Equal("s3://b/a//", panel.Location);
        Assert.Equal(["b.txt"], (await Listing.AllAsync(panel)).Select(e => e.Name));

        Assert.True(await panel.GoUpAsync(CancellationToken.None));
        Assert.Equal("s3://b/a/", panel.Location);
    }

    [Fact]
    public async Task Keys_with_characters_windows_forbids_are_shown_exactly()
    {
        var api = new FakeS3().Add("b", "what?.txt", "x").Add("b", "a:b|c*\"<>\\", "y").Add("b", "tab\there", "z");
        var panel = await S3Test.OpenAsync(api, "s3://b/");

        var names = (await Listing.AllAsync(panel)).Select(e => e.Name).ToList();

        Assert.Contains("what?.txt", names);
        Assert.Contains("a:b|c*\"<>\\", names);
        Assert.Contains("tab\there", names);
    }

    [Fact]
    public async Task Archived_objects_are_dimmed()
    {
        var api = new FakeS3().Add("b", "cold.bin", Encoding.UTF8.GetBytes("x"), "DEEP_ARCHIVE").Add("b", "warm.bin", "x");
        var panel = await S3Test.OpenAsync(api, "s3://b/");

        var entries = await Listing.AllAsync(panel);

        Assert.Equal(RowTone.Dim, entries.Single(e => e.Name == "cold.bin").Tone);
        Assert.Equal(RowTone.Normal, entries.Single(e => e.Name == "warm.bin").Tone);
    }

    [Fact]
    public async Task Switching_profile_keeps_the_location_when_the_profile_can_list_it()
    {
        var api = new FakeS3().Add("b", "a/x.txt", "x");
        var host = new FakeHost();
        host.ChooseAnswers.Enqueue("staging");
        var plugin = new S3Plugin { Connect = _ => api };
        plugin.Initialize(host);
        var panel = (S3Panel)(await plugin.OpenAddressAsync("s3://b/a/", CancellationToken.None))!;

        await panel.Commands.Single(c => c.Key == "Shift+F2").Run(new PanelCommandContext(null, [], new FakeProgress(), CancellationToken.None));

        Assert.Equal("s3://b/a/", panel.Location);
        Assert.Equal("S3 · staging · b/a/", panel.Title);
    }

    [Theory]
    [InlineData("s3://b/a/x.txt", "s3://b/a/")] // an object: its folder
    [InlineData("s3://b/x.txt", "s3://b/")]
    [InlineData("s3://b/a", "s3://b/a/")] // a folder without its final "/"
    [InlineData("s3://b/a/", "s3://b/a/")]
    [InlineData("s3://b", "s3://b/")]
    [InlineData("s3:", "s3:")]
    public async Task An_address_of_a_file_opens_its_folder(string address, string location)
    {
        var plugin = new S3Plugin { Connect = _ => new FakeS3().Add("b", "a/x.txt", "x").Add("b", "x.txt", "y") };
        plugin.Initialize(new FakeHost());
        var panel = (S3Panel)(await plugin.OpenAddressAsync(address, CancellationToken.None))!;
        Assert.Equal(location, panel.Location);
    }

    [Fact]
    public async Task Cd_in_the_console_is_caught_and_its_address_opens_the_place()
    {
        var plugin = new S3Plugin { Connect = _ => new FakeS3().Add("b", "a/x.txt", "x") };
        plugin.Initialize(new FakeHost());
        Assert.Contains("cd", plugin.AddressCommands); // issue #140; the host passes the address alone
        var panel = (S3Panel)(await plugin.OpenAddressAsync("s3://b/a/", CancellationToken.None))!;
        Assert.Equal("s3://b/a/", panel.Location);
    }

    [Fact]
    public async Task Switching_to_a_profile_that_cannot_list_the_bucket_shows_the_bucket_list()
    {
        var api = new FakeS3().Add("b", "a/x.txt", "x");
        var denied = new FakeS3 { ListError = Denied() }.Add("b", "a/x.txt", "x");
        var host = new FakeHost();
        host.ChooseAnswers.Enqueue("other");
        var plugin = new S3Plugin { Connect = session => session.Profile == "other" ? denied : api };
        plugin.Initialize(host);
        var panel = (S3Panel)(await plugin.OpenAddressAsync("s3://b/a/", CancellationToken.None))!;

        await panel.Commands.Single(c => c.Key == "Shift+F2").Run(new PanelCommandContext(null, [], new FakeProgress(), CancellationToken.None));

        Assert.Equal("s3:", panel.Location);
        Assert.Contains(host.Messages, m => m.Title == "status" && m.Message.Contains("other", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_changes_apply_to_open_panels()
    {
        var api = new FakeS3().AddBucket("b");
        var host = new FakeHost();
        var plugin = new S3Plugin { Connect = _ => api };
        plugin.Initialize(host);
        var panel = (S3Panel)(await plugin.OpenAddressAsync("s3:", CancellationToken.None))!;
        host.EditForm = form =>
        {
            form.Field<NumberField>("maxParallel").Value = 3;
            form.Field<NumberField>("partSizeMb").Value = 64;
            form.Field<ChoiceField>("region").Value = "eu-central-1";
            return true;
        };

        await plugin.ConfigureAsync(CancellationToken.None);

        Assert.Equal(3, S3Settings.Load(host.Settings).MaxParallel);
        Assert.Contains(panel.Info, l => l is { Label: "Parallel transfers", Value: "3" });
        Assert.Contains(panel.Info, l => l is { Label: "Part size", Value: "64 MB" });
        Assert.Contains(panel.Info, l => l is { Label: "Region", Value: "eu-central-1 (for the bucket list)" });
        Assert.Contains(panel, host.Refreshed);
    }

    [Fact]
    public void Settings_out_of_range_are_clamped_when_loaded()
    {
        var host = new FakeHost();
        host.Settings.Save(new System.Text.Json.Nodes.JsonObject { ["maxParallel"] = 1000, ["partSizeMb"] = 1 });

        var settings = S3Settings.Load(host.Settings);

        Assert.Equal(S3Settings.MaximumParallel, settings.MaxParallel);
        Assert.Equal(S3Settings.MinimumPartSizeMb, settings.PartSizeMb);
    }

    internal static Amazon.S3.AmazonS3Exception Denied() =>
        new("Access Denied") { ErrorCode = "AccessDenied", StatusCode = System.Net.HttpStatusCode.Forbidden };
}
