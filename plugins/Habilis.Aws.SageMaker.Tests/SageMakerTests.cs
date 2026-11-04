using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Amazon.SageMaker;
using Amazon.SageMaker.Model;
using Habilis.Aws.Common;
using Habilis.Aws.SageMaker;
using Habilis.Aws.Tests;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.SageMaker.Tests;

public sealed class SageMakerTests : IDisposable
{
    private static readonly DateTime Now = new(2025, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly PriceTable Prices = PriceTable.Parse(
        """{ "basis": "test prices as of 2025-01", "perHour": { "ml.m5.xlarge": 0.23, "ml.p3.2xlarge": 3.825 } }""", "test");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "habi-tests", "sagemaker-" + Guid.NewGuid().ToString("N"));

    public SageMakerTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Lists_jobs_with_the_console_columns_running_first_and_toned_by_status()
    {
        var host = new FakeHost();
        var panel = await OpenAsync(host, new FakeSageMaker(SampleJobs()), "sm:prod/eu-west-1");

        var entries = await Listing.AllAsync(panel);

        Assert.Equal(["run-1", "stopping-1", "done-1", "failed-1", "stopped-1"], entries.Select(e => e.Name));
        Assert.Equal([0, 0, 1, 1, 1], entries.Select(e => e.Group));
        Assert.Equal([RowTone.Accent, RowTone.Warning, RowTone.Good, RowTone.Bad, RowTone.Dim], entries.Select(e => e.Tone));
        Assert.All(entries, e => Assert.False(e.IsFolder));

        var done = entries.Single(e => e.Name == "done-1");
        Assert.Equal(Now.AddHours(-3), done.Modified);
        Assert.Equal(DateTimeKind.Utc, done.Modified!.Value.Kind);
        Assert.Equal(Now.AddHours(-3), done.Cells![SageMakerPanel.CreatedKey].SortValue);
        Assert.Equal(TimeSpan.FromMinutes(170), done.Cells[SageMakerPanel.DurationKey].SortValue);
        Assert.Equal("2 h 50 min", done.Cells[SageMakerPanel.DurationKey].Text);
        Assert.Equal("Completed", done.Cells[SageMakerPanel.StatusKey].Text);
        Assert.Equal("Completed", done.Cells[SageMakerPanel.SecondaryKey].Text);
        Assert.Equal("Available", done.Cells[SageMakerPanel.WarmPoolKey].Text);
        Assert.Equal(TimeSpan.FromMinutes(20), done.Cells[SageMakerPanel.WarmPoolLeftKey].SortValue);
        Assert.Equal(3.825m * 2 * 1.5m, done.Cells[SageMakerPanel.CostKey].SortValue);
        Assert.Equal("$11.48", done.Cells[SageMakerPanel.CostKey].Text);

        var running = entries.Single(e => e.Name == "run-1");
        Assert.Equal(TimeSpan.FromMinutes(40), running.Cells![SageMakerPanel.DurationKey].SortValue);
        Assert.False(running.Cells.ContainsKey(SageMakerPanel.WarmPoolKey));

        Assert.Equal("sm:prod/eu-west-1", panel.Location);
        Assert.Contains(panel.Info, l => l.Label == "Jobs shown" && l.Value.StartsWith('5'));
        Assert.Contains(panel.Info, l => l is { Label: "Profile", Value: "prod" });
        Assert.Contains(panel.Info, l => l is { Label: "Region", Value: "eu-west-1" });
    }

    [Fact]
    public async Task Layouts_and_sort_keys_cover_the_console_columns()
    {
        var panel = await OpenAsync(new FakeHost(), new FakeSageMaker([]), "sm:prod/eu-west-1");

        Assert.Equal(["Console", "Brief", "Status"], panel.Layouts.Select(l => l.Title));
        Assert.Equal([SageMakerPanel.CreatedKey, SageMakerPanel.DurationKey, SageMakerPanel.StatusKey, SageMakerPanel.SecondaryKey, SageMakerPanel.CostKey], panel.Layouts[0].Columns.Select(c => c.Key));
        Assert.False(panel.ShowsParent);
        Assert.Equal([SageMakerPanel.CreatedKey, SageMakerPanel.StatusKey, SageMakerPanel.CostKey], panel.Layouts[1].Columns.Select(c => c.Key));
        Assert.Equal([SageMakerPanel.StatusKey, SageMakerPanel.SecondaryKey, SageMakerPanel.WarmPoolKey, SageMakerPanel.WarmPoolLeftKey], panel.Layouts[2].Columns.Select(c => c.Key));
        Assert.Equal([SageMakerPanel.CreatedKey, SageMakerPanel.DurationKey, SageMakerPanel.StatusKey, SageMakerPanel.CostKey], panel.SortKeys.Select(k => k.Key));
    }

    [Fact]
    public async Task Rows_are_not_coloured_when_the_setting_is_off()
    {
        var host = new FakeHost();
        new SageMakerSettings { ColorRows = false }.Save(host.Settings);
        var panel = await OpenAsync(host, new FakeSageMaker(SampleJobs()), "sm:prod/eu-west-1");

        var entries = await Listing.AllAsync(panel);

        Assert.All(entries, e => Assert.Equal(RowTone.Normal, e.Tone));
    }

    [Fact]
    public async Task Name_and_status_filters_go_to_the_api()
    {
        var api = new FakeSageMaker(SampleJobs());
        var host = new FakeHost();
        new SageMakerSettings { NameContains = "done", StatusFilter = "Completed", MaxJobs = 42 }.Save(host.Settings);
        var panel = await OpenAsync(host, api, "sm:prod/eu-west-1");

        var entries = await Listing.AllAsync(panel);

        Assert.Equal(new JobQuery("done", "Completed", 42), api.LastQuery);
        Assert.Equal(["done-1"], entries.Select(e => e.Name));
        Assert.Contains(panel.Info, l => l is { Label: "Name contains", Value: "done" });
        Assert.Contains(panel.Info, l => l is { Label: "Status", Value: "Completed" });
    }

    [Fact]
    public async Task The_filter_command_saves_the_filter_and_the_next_listing_uses_it()
    {
        var api = new FakeSageMaker(SampleJobs());
        var host = new FakeHost
        {
            EditForm = form =>
            {
                form.Field<TextField>("nameContains").Value = " failed ";
                form.Field<ChoiceField>("statusFilter").Value = "Failed";
                return true;
            },
        };
        var panel = await OpenAsync(host, api, "sm:prod/eu-west-1");

        await Command(panel, "Ctrl+I").Run(Context([]));
        var entries = await Listing.AllAsync(panel);

        Assert.Equal(new JobQuery("failed", "Failed", SageMakerSettings.DefaultMaxJobs), api.LastQuery);
        Assert.Equal(["failed-1"], entries.Select(e => e.Name));
        var saved = SageMakerSettings.Load(host.Settings);
        Assert.Equal(("failed", "Failed"), (saved.NameContains, saved.StatusFilter));
    }

    [Fact]
    public async Task Choosing_all_statuses_clears_the_status_filter()
    {
        var host = new FakeHost
        {
            EditForm = form =>
            {
                form.Field<ChoiceField>("statusFilter").Value = "All";
                return true;
            },
        };
        new SageMakerSettings { StatusFilter = "Failed" }.Save(host.Settings);
        var panel = await OpenAsync(host, new FakeSageMaker([]), "sm:prod/eu-west-1");

        await Command(panel, "Ctrl+I").Run(Context([]));

        Assert.Equal(string.Empty, SageMakerSettings.Load(host.Settings).StatusFilter);
    }

    [Fact]
    public async Task The_listing_stops_at_the_configured_number_of_jobs_across_pages()
    {
        var jobs = Enumerable.Range(0, 250).Select(i => Completed($"job-{i:000}", Now.AddMinutes(-i))).ToList();
        var api = new FakeSageMaker(jobs) { PageSize = 100 };
        var host = new FakeHost();
        new SageMakerSettings { MaxJobs = 150 }.Save(host.Settings);
        var panel = await OpenAsync(host, api, "sm:prod/eu-west-1");

        var batches = new List<int>();
        await foreach (var batch in panel.ListAsync(CancellationToken.None))
        {
            batches.Add(batch.Count);
        }

        Assert.Equal([100, 50], batches);
        Assert.Equal(2, api.PagesRequested);
        Assert.Equal(150, api.Described.Count);
        Assert.Equal(150, api.LastQuery!.MaxJobs);
    }

    [Fact]
    public async Task A_failed_description_fails_the_whole_listing_with_the_job_named()
    {
        var api = new FakeSageMaker(SampleJobs())
        {
            DescribeError = ("failed-1", new AmazonSageMakerException("Rate exceeded") { ErrorCode = "ThrottlingException" }),
        };
        var panel = await OpenAsync(new FakeHost(), api, "sm:prod/eu-west-1");

        var error = await Assert.ThrowsAsync<PluginException>(() => Listing.AllAsync(panel));

        Assert.Contains("failed-1", error.Message, StringComparison.Ordinal);
        Assert.Contains("throttling", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(panel.Info, l => l is { Label: "Jobs", Value: "loading…" });
    }

    [Fact]
    public async Task Access_denied_says_which_permission_is_missing()
    {
        var api = new FakeSageMaker(SampleJobs())
        {
            ListError = new AmazonSageMakerException("User: arn:aws:iam::1:user/x is not authorized to perform: sagemaker:ListTrainingJobs on resource: *")
            {
                ErrorCode = "AccessDeniedException",
                StatusCode = HttpStatusCode.BadRequest,
            },
        };
        var panel = await OpenAsync(new FakeHost(), api, "sm:prod/eu-west-1");

        var error = await Assert.ThrowsAsync<PluginException>(() => Listing.AllAsync(panel));

        Assert.Contains("Permission missing: sagemaker:ListTrainingJobs", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("arn:aws", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_denied_on_describe_names_that_permission()
    {
        var api = new FakeSageMaker(SampleJobs())
        {
            DescribeError = ("run-1", new AmazonSageMakerException("User: arn:aws:iam::1:user/x is not authorized to perform: sagemaker:DescribeTrainingJob on resource")
            {
                ErrorCode = "AccessDeniedException",
            }),
        };
        var panel = await OpenAsync(new FakeHost(), api, "sm:prod/eu-west-1");

        var error = await Assert.ThrowsAsync<PluginException>(() => Listing.AllAsync(panel));

        Assert.Contains("Permission missing: sagemaker:DescribeTrainingJob", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cost_of_a_finished_job_is_price_times_instances_times_billable_hours_plus_warm_pool()
    {
        var job = Completed("done", Now.AddHours(-3));
        job.WarmPoolStatus = new WarmPoolStatus { Status = WarmPoolResourceStatus.Terminated, ResourceRetainedBillableTimeInSeconds = 600 };

        var cost = JobCost.Estimate(job, Prices, Now);

        Assert.Equal((3.825m * 2 * 1.5m) + (3.825m * 2 * 600 / 3600m), cost.Amount);
        Assert.Contains(cost.Lines, l => l.Contains("2 × ml.p3.2xlarge at $3.825/h × 1 h 30 min billable", StringComparison.Ordinal));
        Assert.Contains(cost.Lines, l => l.Contains("warm pool", StringComparison.Ordinal));
        Assert.Equal("Estimate from test prices as of 2025-01; managed spot, discounts and other regions differ.", cost.Statement);
    }

    [Fact]
    public void Cost_of_a_running_job_counts_the_time_since_training_started()
    {
        var job = Running("run", Now.AddMinutes(-40), Now.AddMinutes(-30));

        var cost = JobCost.Estimate(job, Prices, Now);

        Assert.Equal(0.23m * 2 * 0.5m, cost.Amount);
        Assert.Contains(cost.Lines, l => l.Contains("since training started", StringComparison.Ordinal));
        Assert.Contains(cost.Lines, l => l.Contains("still running", StringComparison.Ordinal));
    }

    [Fact]
    public void Cost_of_an_unknown_instance_type_is_a_question_mark_with_the_reason()
    {
        var job = Completed("odd", Now.AddHours(-3));
        job.ResourceConfig = new ResourceConfig { InstanceType = TrainingInstanceType.FindValue("ml.x99.huge"), InstanceCount = 1 };

        var cost = JobCost.Estimate(job, Prices, Now);
        var entry = SageMakerPanel.Entry(job, cost, Now, colorRows: true);

        Assert.Null(cost.Amount);
        Assert.Equal("?", cost.Text);
        Assert.Equal("?", entry.Cells![SageMakerPanel.CostKey].Text);
        Assert.Contains(cost.Lines, l => l.Contains("a price for ml.x99.huge", StringComparison.Ordinal));
    }

    [Fact]
    public void Cost_of_a_heterogeneous_cluster_adds_up_its_instance_groups()
    {
        var job = Completed("hetero", Now.AddHours(-3));
        job.ResourceConfig = new ResourceConfig
        {
            InstanceGroups =
            [
                new InstanceGroup { InstanceGroupName = "cpu", InstanceType = TrainingInstanceType.FindValue("ml.m5.xlarge"), InstanceCount = 4 },
                new InstanceGroup { InstanceGroupName = "gpu", InstanceType = TrainingInstanceType.FindValue("ml.p3.2xlarge"), InstanceCount = 1 },
            ],
        };

        var cost = JobCost.Estimate(job, Prices, Now);

        Assert.Equal((0.23m * 4 * 1.5m) + (3.825m * 1.5m), cost.Amount);
    }

    [Fact]
    public void The_built_in_price_table_states_its_basis_and_covers_common_types()
    {
        var table = PriceTable.Load();

        Assert.Contains("us-east-1", table.Basis, StringComparison.Ordinal);
        Assert.Null(table.Problem);
        foreach (var type in new[] { "ml.m5.large", "ml.c5.xlarge", "ml.g4dn.xlarge", "ml.g5.2xlarge", "ml.g6.12xlarge", "ml.p4d.24xlarge", "ml.trn1.32xlarge" })
        {
            Assert.True(table.TryGetHourly(type, out var price), type);
            Assert.True(price > 0, type);
        }
    }

    [Fact]
    public void A_prices_json_next_to_the_plugin_replaces_the_built_in_table()
    {
        File.WriteAllText(Path.Combine(_folder, PriceTable.FileName), """{ "basis": "my negotiated eu-west-1 prices, 2025-02", "perHour": { "ml.x99.huge": 1.5 } }""");

        var table = PriceTable.Load(_folder);

        Assert.Equal("my negotiated eu-west-1 prices, 2025-02", table.Basis);
        Assert.True(table.TryGetHourly("ml.x99.huge", out var price));
        Assert.Equal(1.5m, price);
        Assert.False(table.TryGetHourly("ml.m5.large", out _));
    }

    [Fact]
    public void An_unreadable_prices_json_falls_back_to_the_built_in_table_and_says_why()
    {
        File.WriteAllText(Path.Combine(_folder, PriceTable.FileName), "{ broken");

        var table = PriceTable.Load(_folder);
        var job = Completed("done", Now.AddHours(-3));
        job.ResourceConfig.InstanceType = "ml.g6.12xlarge"; // ml.p3 is retired: no longer in the AWS price list
        var cost = JobCost.Estimate(job, table, Now);

        Assert.True(table.TryGetHourly("ml.g6.12xlarge", out _));
        Assert.Contains("could not be read", table.Problem, StringComparison.Ordinal);
        Assert.Contains(cost.Lines, l => l.Contains("could not be read", StringComparison.Ordinal));
        Assert.NotNull(cost.Amount);
    }

    [Fact]
    public void A_price_table_without_prices_is_refused()
    {
        Assert.Throws<FormatException>(() => PriceTable.Parse("""{ "basis": "x" }""", "test"));
        Assert.Throws<FormatException>(() => PriceTable.Parse("not json", "test"));
    }

    [Fact]
    public async Task The_summary_of_a_running_job_starts_with_its_progress()
    {
        var text = await SummaryAsync(Running("run-1", Now.AddMinutes(-40), Now.AddMinutes(-30)));

        Assert.True(text.IndexOf("PROGRESS", StringComparison.Ordinal) < text.IndexOf("DETAILS", StringComparison.Ordinal));
        Assert.DoesNotContain("OUTPUTS", text, StringComparison.Ordinal);
        Assert.Contains("Starting", text, StringComparison.Ordinal);
        Assert.Contains("(current)", text, StringComparison.Ordinal);
        Assert.Contains("Training image download completed", text, StringComparison.Ordinal);
        Assert.Contains("Elapsed since creation: 40 min 00 s", text, StringComparison.Ordinal);
        Assert.Contains("ESTIMATED COST", text, StringComparison.Ordinal);
        Assert.Contains("$0.23", text, StringComparison.Ordinal);
        Assert.Contains("managed spot, discounts and other regions differ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_of_a_completed_job_starts_with_its_outputs()
    {
        var text = await SummaryAsync(Completed("done-1", Now.AddHours(-3)));

        Assert.StartsWith("OUTPUTS", text[text.IndexOf("OUTPUTS", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.True(text.IndexOf("OUTPUTS", StringComparison.Ordinal) < text.IndexOf("DETAILS", StringComparison.Ordinal));
        Assert.DoesNotContain("PROGRESS", text, StringComparison.Ordinal);
        Assert.Contains("s3://models/done-1/output/model.tar.gz", text, StringComparison.Ordinal);
        Assert.Contains("validation:accuracy", text, StringComparison.Ordinal);
        Assert.Contains("0.93", text, StringComparison.Ordinal);
        Assert.Contains("2 × ml.p3.2xlarge", text, StringComparison.Ordinal);
        Assert.Contains("learning_rate", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_of_a_failed_job_starts_with_the_failure_reason()
    {
        var text = await SummaryAsync(Failed("failed-1", Now.AddHours(-5)));

        Assert.True(text.IndexOf("FAILURE", StringComparison.Ordinal) < text.IndexOf("DETAILS", StringComparison.Ordinal));
        Assert.Contains("AlgorithmError: CUDA out of memory", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTPUTS", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_full_record_command_shows_the_description_as_json_without_sdk_metadata()
    {
        var host = new FakeHost();
        var api = new FakeSageMaker(SampleJobs());
        var panel = await OpenAsync(host, api, "sm:prod/eu-west-1");
        var entries = await Listing.AllAsync(panel);
        var done = entries.Single(e => e.Name == "done-1");

        await Command(panel, "Shift+F3").Run(Context([done], done));

        var view = Assert.Single(host.Views);
        Assert.Equal("done-1.json", view.Title);
        var json = JsonNode.Parse(view.Text)!.AsObject();
        Assert.Equal("done-1", (string?)json["TrainingJobName"]);
        Assert.Equal("Completed", (string?)json["TrainingJobStatus"]);
        Assert.Equal("ml.p3.2xlarge", (string?)json["ResourceConfig"]!["InstanceType"]);
        Assert.Equal(5400, (int?)json["BillableTimeInSeconds"]);
        Assert.Equal("2025-03-01T09:00:00.000Z", (string?)json["CreationTime"]);
        Assert.Equal("0.01", (string?)json["HyperParameters"]!["learning_rate"]);
        Assert.Equal("validation:accuracy", (string?)json["FinalMetricDataList"]![0]!["MetricName"]);
        Assert.False(json.ContainsKey("ResponseMetadata"));
        Assert.False(json.ContainsKey("HttpStatusCode"));
        Assert.False(json.ContainsKey("ContentLength"));
        Assert.False(json.ContainsKey("TuningJobArn"));
    }

    [Fact]
    public async Task F5_writes_a_folder_per_job_with_its_record_and_s3_outputs_and_code()
    {
        var data = new FakeJobData
        {
            ["models/done-1/output/model.tar.gz"] = "model",
            ["models/done-1/output/output.tar.gz"] = "output",
            ["models/done-1/debug-output/events/0.json"] = "debug",
            ["models/done-2/output/model.tar.gz"] = "another job",
            ["code/done-1/sourcedir.tar.gz"] = "code",
            ["checkpoints/done-1/step-100.pt"] = "checkpoint",
        };
        var jobs = SampleJobs();
        var done = jobs.Single(j => j.TrainingJobName == "done-1");
        done.HyperParameters["sagemaker_submit_directory"] = "\"s3://code/done-1/sourcedir.tar.gz\"";
        done.CheckpointConfig = new CheckpointConfig { S3Uri = "s3://checkpoints/done-1" };
        var panel = await OpenAsync(new FakeHost(), new FakeSageMaker(jobs), "sm:prod/eu-west-1", data);
        var entries = await Listing.AllAsync(panel);
        var progress = new FakeProgress();

        var outcome = await panel.GetFilesAsync(Request(entries.Where(e => e.Name is "done-1" or "failed-1").ToList(), _ => throw new InvalidOperationException("No target exists.")), progress, CancellationToken.None);

        Assert.Equal(new OperationOutcome(2, 0, 0), outcome);
        Assert.Equal(
            ["done-1/checkpoints/step-100.pt", "done-1/code/sourcedir.tar.gz", "done-1/done-1.json", "done-1/output/debug-output/events/0.json",
             "done-1/output/output/model.tar.gz", "done-1/output/output/output.tar.gz", "failed-1/failed-1.json"],
            Files());
        Assert.Equal("done-1", (string?)JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_folder, "done-1", "done-1.json")))!["TrainingJobName"]);
        Assert.Equal("code", await File.ReadAllTextAsync(Path.Combine(_folder, "done-1", "code", "sourcedir.tar.gz")));
        Assert.Equal((2L, (long?)null), progress.Totals);
        Assert.Equal(2, progress.Items);
        Assert.Equal(5 + 6 + 5 + 4 + 10, progress.Bytes);
        Assert.Contains("done-1/output/output/model.tar.gz", progress.Begun);
        Assert.False(panel.CanMove);
    }

    [Fact]
    public void Job_data_is_this_jobs_part_of_shared_paths_and_skips_locations_inside_others()
    {
        var job = SampleJobs().Single(j => j.TrainingJobName == "done-1");
        job.OutputDataConfig.S3OutputPath = "s3://bucket/sagemaker-output";
        job.ModelArtifacts.S3ModelArtifacts = "s3://bucket/sagemaker-output/done-1/output/model.tar.gz";
        job.ProfilerConfig = new ProfilerConfig { S3OutputPath = "s3://bucket/profiles/" };
        job.TensorBoardOutputConfig = new TensorBoardOutputConfig { S3OutputPath = "s3://bucket/tb/done-1/run" };
        job.InputDataConfig = [new Channel { ChannelName = "train", DataSource = new DataSource { S3DataSource = new S3DataSource { S3Uri = "s3://bucket/data" } } }];

        Assert.Equal(
            ["output s3://bucket/sagemaker-output/done-1/", "tensorboard s3://bucket/tb/done-1/run/", "profiler s3://bucket/profiles/done-1/"],
            JobData.Sources(job).Select(s => $"{s.Folder} {s.Uri}"));
        Assert.Null(JobData.Parse("https://bucket/key"));
        Assert.Equal(("b", ""), JobData.Parse("s3://b"));
    }

    [Fact]
    public async Task F5_asks_once_per_existing_job_folder_and_honours_skip()
    {
        var panel = await OpenAsync(new FakeHost(), new FakeSageMaker(SampleJobs()), "sm:prod/eu-west-1");
        var entries = await Listing.AllAsync(panel);
        var selected = entries.Where(e => e.Name is "done-1" or "failed-1").ToList();
        Directory.CreateDirectory(Path.Combine(_folder, "done-1"));
        await File.WriteAllTextAsync(Path.Combine(_folder, "done-1", "done-1.json"), "old");
        var asked = new List<string>();

        var outcome = await panel.GetFilesAsync(Request(selected, name => { asked.Add(name); return OverwriteChoice.Skip; }), new FakeProgress(), CancellationToken.None);

        Assert.Equal(new OperationOutcome(1, 1, 0), outcome);
        Assert.Equal(["done-1"], asked);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(_folder, "done-1", "done-1.json")));
        Assert.True(File.Exists(Path.Combine(_folder, "failed-1", "failed-1.json")));
    }

    [Fact]
    public async Task F5_overwrite_all_asks_once_and_replaces_the_records()
    {
        var panel = await OpenAsync(new FakeHost(), new FakeSageMaker(SampleJobs()), "sm:prod/eu-west-1");
        var entries = await Listing.AllAsync(panel);
        var selected = entries.Where(e => e.Name is "done-1" or "failed-1").ToList();
        foreach (var name in new[] { "done-1", "failed-1" })
        {
            Directory.CreateDirectory(Path.Combine(_folder, name));
            await File.WriteAllTextAsync(Path.Combine(_folder, name, name + ".json"), "old");
        }

        var asked = new List<string>();
        var outcome = await panel.GetFilesAsync(Request(selected, name => { asked.Add(name); return OverwriteChoice.OverwriteAll; }), new FakeProgress(), CancellationToken.None);

        Assert.Equal(new OperationOutcome(2, 0, 0), outcome);
        Assert.Single(asked);
        Assert.Equal("done-1", (string?)JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_folder, "done-1", "done-1.json")))!["TrainingJobName"]);
        Assert.Equal(["done-1/done-1.json", "failed-1/failed-1.json"], Files());
    }

    [Fact]
    public async Task F5_cancel_at_the_overwrite_question_stops_without_writing()
    {
        var panel = await OpenAsync(new FakeHost(), new FakeSageMaker(SampleJobs()), "sm:prod/eu-west-1");
        var entries = await Listing.AllAsync(panel);
        var selected = entries.Where(e => e.Name is "done-1" or "failed-1").ToList();
        Directory.CreateDirectory(Path.Combine(_folder, "done-1"));
        await File.WriteAllTextAsync(Path.Combine(_folder, "done-1", "done-1.json"), "old");

        var outcome = await panel.GetFilesAsync(Request(selected, _ => OverwriteChoice.Cancel), new FakeProgress(), CancellationToken.None);

        Assert.Equal((0, 0, 0), (outcome.Succeeded, outcome.Skipped, outcome.Failed));
        Assert.Equal(["done-1/done-1.json"], Files());
    }

    [Fact]
    public async Task F5_cancelled_while_downloading_leaves_no_partial_file()
    {
        using var cancel = new CancellationTokenSource();
        var data = new FakeJobData { ["models/done-1/output/model.tar.gz"] = "model" };
        data.OnDownload = cancel.Cancel;
        var panel = await OpenAsync(new FakeHost(), new FakeSageMaker(SampleJobs()), "sm:prod/eu-west-1", data);
        var entries = await Listing.AllAsync(panel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            panel.GetFilesAsync(Request(entries.Where(e => e.Name == "done-1").ToList(), _ => OverwriteChoice.Overwrite), new FakeProgress(), cancel.Token));

        Assert.Equal(["done-1/done-1.json"], Files());
    }

    [Fact]
    public async Task F5_reports_jobs_and_s3_data_that_cannot_be_read_and_saves_the_rest()
    {
        var api = new FakeSageMaker(SampleJobs());
        var data = new FakeJobData { ["models/failed-1/output/output.tar.gz"] = "x" };
        data.ListError = new Amazon.S3.AmazonS3Exception("Access Denied") { ErrorCode = "AccessDenied", StatusCode = HttpStatusCode.Forbidden };
        var panel = await OpenAsync(new FakeHost(), api, "sm:prod/eu-west-1", data);
        var entries = await Listing.AllAsync(panel);
        api.DescribeError = ("done-1", new AmazonSageMakerException("Requested resource not found") { ErrorCode = "ValidationException" });

        var outcome = await panel.GetFilesAsync(Request(entries.Where(e => e.Name is "done-1" or "failed-1").ToList(), _ => OverwriteChoice.Overwrite), new FakeProgress(), CancellationToken.None);

        Assert.Equal((0, 0, 2), (outcome.Succeeded, outcome.Skipped, outcome.Failed));
        Assert.Contains("done-1", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("s3://models/failed-1/", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(["failed-1/failed-1.json"], Files());
    }

    private List<string> Files() =>
        Directory.GetFiles(_folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    [Theory]
    [InlineData("sm:", "default", "us-east-1")]
    [InlineData("sm:prod/eu-west-1", "prod", "eu-west-1")]
    [InlineData("SM: prod / ap-south-1 ", "prod", "ap-south-1")]
    public async Task Addresses_name_profile_and_region(string address, string profile, string region)
    {
        var panel = await OpenAsync(new FakeHost(), new FakeSageMaker([]), address);

        Assert.Equal($"sm:{profile}/{region}", panel.Location);
        Assert.Equal($"SageMaker · {profile} · {region}", panel.Title);
    }

    [Fact]
    public async Task An_invalid_region_is_refused_before_any_request()
    {
        var plugin = new SageMakerPlugin { Connect = _ => throw new InvalidOperationException("No connection expected.") };
        plugin.Initialize(new FakeHost());

        var error = await Assert.ThrowsAsync<PluginException>(() => plugin.OpenAddressAsync("sm:prod/EU_WEST", CancellationToken.None));

        Assert.Contains("not a valid AWS region", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plugin_offers_its_prefix_menu_items_and_configuration()
    {
        var plugin = new SageMakerPlugin();

        Assert.Equal(["sm"], plugin.Prefixes);
        Assert.Equal("SageMaker training jobs", Assert.Single(plugin.MenuItems).Title);
        Assert.Equal("SageMaker training jobs", Assert.Single(plugin.DriveMenuItems).Title);
        Assert.True(plugin.HasConfiguration);
    }

    [Fact]
    public async Task Switching_profile_and_region_changes_the_location()
    {
        var host = new FakeHost();
        host.ChooseAnswers.Enqueue("staging");
        host.ChooseAnswers.Enqueue("us-west-2");
        var panel = await OpenAsync(host, new FakeSageMaker([]), "sm:prod/eu-west-1");

        await Command(panel, "Shift+F2").Run(Context([]));

        Assert.Equal("sm:staging/us-west-2", panel.Location);
    }

    [Fact]
    public async Task Configuration_is_saved_and_applied_to_open_panels()
    {
        var api = new FakeSageMaker(SampleJobs());
        var host = new FakeHost();
        var plugin = new SageMakerPlugin { Connect = _ => api, Prices = () => Prices, Pricing = null, Clock = () => Now, DataStore = _ => new FakeJobData() };
        plugin.Initialize(host);
        var panel = (await plugin.OpenAddressAsync("sm:prod/eu-west-1", CancellationToken.None))!;
        host.EditForm = form =>
        {
            form.Field<NumberField>("maxJobs").Value = 3;
            form.Field<ToggleField>("colorRows").Value = false;
            return true;
        };

        await plugin.ConfigureAsync(CancellationToken.None);
        var entries = await Listing.AllAsync(panel);

        Assert.Equal(3, SageMakerSettings.Load(host.Settings).MaxJobs);
        Assert.Same(panel, Assert.Single(host.Refreshed));
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal(RowTone.Normal, e.Tone));
    }

    [Fact]
    public void Settings_round_trip_and_clamp_the_job_count()
    {
        var host = new FakeHost();
        new SageMakerSettings { MaxJobs = 5000, NameContains = "bert", StatusFilter = "Bogus", ColorRows = false, Regions = ["eu-west-1"] }.Save(host.Settings);

        var loaded = SageMakerSettings.Load(host.Settings);

        Assert.Equal(SageMakerSettings.MaxJobsLimit, loaded.MaxJobs);
        Assert.Equal("bert", loaded.NameContains);
        Assert.Equal(string.Empty, loaded.StatusFilter);
        Assert.False(loaded.ColorRows);
        Assert.Equal(["eu-west-1"], loaded.Regions);
        Assert.Equal(SageMakerSettings.DefaultMaxJobs, SageMakerSettings.Load(new FakeHost().Settings).MaxJobs);
    }

    private static async Task<string> SummaryAsync(DescribeTrainingJobResponse job)
    {
        var panel = await OpenAsync(new FakeHost(), new FakeSageMaker([job]), "sm:prod/eu-west-1");
        var view = await panel.ViewAsync(new PanelEntry(job.TrainingJobName), CancellationToken.None);
        Assert.Equal(job.TrainingJobName, view!.Value.Title);
        return view.Value.Text;
    }

    private TransferRequest Request(IReadOnlyList<PanelEntry> entries, Func<string, OverwriteChoice> ask) =>
        new(entries, _folder, move: false, name => Task.FromResult(ask(name)));

    private static PanelCommand Command(IPanelCommands panel, string key) => panel.Commands.Single(c => c.Key == key);

    private static PanelCommandContext Context(IReadOnlyList<PanelEntry> selected, PanelEntry? current = null) =>
        new(current, selected, new FakeProgress(), CancellationToken.None);

    private static async Task<SageMakerPanel> OpenAsync(FakeHost host, FakeSageMaker api, string address, FakeJobData? data = null)
    {
        var plugin = new SageMakerPlugin { Connect = _ => api, Prices = () => Prices, Pricing = null, Clock = () => Now, DataStore = _ => data ?? new FakeJobData() };
        plugin.Initialize(host);
        return (SageMakerPanel)(await plugin.OpenAddressAsync(address, CancellationToken.None))!;
    }

    /// <summary>Newest first, as ListTrainingJobs returns them.</summary>
    private static List<DescribeTrainingJobResponse> SampleJobs() =>
    [
        Running("run-1", Now.AddMinutes(-40), Now.AddMinutes(-30)),
        Stopping("stopping-1", Now.AddHours(-1)),
        Completed("done-1", Now.AddHours(-3)),
        Failed("failed-1", Now.AddHours(-5)),
        Stopped("stopped-1", Now.AddHours(-8)),
    ];

    private static DescribeTrainingJobResponse Job(string name, TrainingJobStatus status, SecondaryStatus secondary, DateTime created) => new()
    {
        TrainingJobName = name,
        TrainingJobArn = $"arn:aws:sagemaker:eu-west-1:123456789012:training-job/{name}",
        TrainingJobStatus = status,
        SecondaryStatus = secondary,
        CreationTime = created,
        LastModifiedTime = created,
        RoleArn = "arn:aws:iam::123456789012:role/SageMakerRole",
        ResourceConfig = new ResourceConfig { InstanceType = TrainingInstanceType.MlP32xlarge, InstanceCount = 2, VolumeSizeInGB = 50 },
        AlgorithmSpecification = new AlgorithmSpecification { TrainingImage = "123456789012.dkr.ecr.eu-west-1.amazonaws.com/train:latest", TrainingInputMode = TrainingInputMode.File },
        HyperParameters = new Dictionary<string, string> { ["learning_rate"] = "0.01", ["epochs"] = "10" },
        OutputDataConfig = new OutputDataConfig { S3OutputPath = $"s3://models/{name}/" },
    };

    private static DescribeTrainingJobResponse Running(string name, DateTime created, DateTime started)
    {
        var job = Job(name, TrainingJobStatus.InProgress, SecondaryStatus.Training, created);
        job.ResourceConfig = new ResourceConfig { InstanceType = TrainingInstanceType.MlM5Xlarge, InstanceCount = 2 };
        job.TrainingStartTime = started;
        job.SecondaryStatusTransitions =
        [
            new SecondaryStatusTransition { Status = SecondaryStatus.Starting, StartTime = created, EndTime = started, StatusMessage = "Preparing the instances for training" },
            new SecondaryStatusTransition { Status = SecondaryStatus.Training, StartTime = started, StatusMessage = "Training image download completed. Training in progress." },
        ];
        return job;
    }

    private static DescribeTrainingJobResponse Stopping(string name, DateTime created)
    {
        var job = Job(name, TrainingJobStatus.Stopping, SecondaryStatus.Stopping, created);
        job.TrainingStartTime = created.AddMinutes(5);
        return job;
    }

    private static DescribeTrainingJobResponse Completed(string name, DateTime created)
    {
        var job = Job(name, TrainingJobStatus.Completed, SecondaryStatus.Completed, created);
        job.TrainingStartTime = created.AddMinutes(10);
        job.TrainingEndTime = created.AddMinutes(170);
        job.LastModifiedTime = job.TrainingEndTime;
        job.TrainingTimeInSeconds = 9600;
        job.BillableTimeInSeconds = 5400;
        job.ModelArtifacts = new ModelArtifacts { S3ModelArtifacts = $"s3://models/{name}/output/model.tar.gz" };
        job.FinalMetricDataList = [new MetricData { MetricName = "validation:accuracy", Value = 0.93f, Timestamp = job.TrainingEndTime }];
        job.ResourceConfig.KeepAlivePeriodInSeconds = 1800;
        job.WarmPoolStatus = new WarmPoolStatus { Status = WarmPoolResourceStatus.Available };
        return job;
    }

    private static DescribeTrainingJobResponse Failed(string name, DateTime created)
    {
        var job = Job(name, TrainingJobStatus.Failed, SecondaryStatus.Failed, created);
        job.TrainingStartTime = created.AddMinutes(10);
        job.TrainingEndTime = created.AddMinutes(20);
        job.BillableTimeInSeconds = 600;
        job.FailureReason = "AlgorithmError: CUDA out of memory";
        return job;
    }

    private static DescribeTrainingJobResponse Stopped(string name, DateTime created)
    {
        var job = Job(name, TrainingJobStatus.Stopped, SecondaryStatus.Stopped, created);
        job.TrainingStartTime = created.AddMinutes(10);
        job.TrainingEndTime = created.AddMinutes(30);
        job.BillableTimeInSeconds = 1200;
        return job;
    }

    /// <summary>S3 objects by "bucket/key"; listing and downloads as the real store does them.</summary>
    private sealed class FakeJobData : IJobDataStore
    {
        private readonly Dictionary<string, string> _objects = new(StringComparer.Ordinal);

        public Exception? ListError { get; set; }

        public Action? OnDownload { get; set; }

        public string this[string bucketAndKey]
        {
            set => _objects[bucketAndKey] = value;
        }

        public async IAsyncEnumerable<(string Key, long Size)> ListAsync(JobDataSource source, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (ListError is { } error)
            {
                throw error;
            }

            var prefix = $"{source.Bucket}/{source.Key}";
            var found = source.IsPrefix ? _objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList() : _objects.Keys.Where(k => k == prefix).ToList();
            if (!source.IsPrefix && found.Count == 0)
            {
                throw new Amazon.S3.AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound };
            }

            foreach (var key in found.Order(StringComparer.Ordinal))
            {
                yield return (key[(source.Bucket.Length + 1)..], _objects[key].Length);
            }
        }

        public async Task DownloadAsync(string bucket, string key, string target, Action<long> bytes, CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(target, _objects[$"{bucket}/{key}"][..1], cancellationToken);
            OnDownload?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            await File.AppendAllTextAsync(target, _objects[$"{bucket}/{key}"][1..], cancellationToken);
            bytes(_objects[$"{bucket}/{key}"].Length);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeSageMaker(IReadOnlyList<DescribeTrainingJobResponse> jobs) : ISageMakerApi
    {
        public int PageSize { get; init; } = 100;

        public Exception? ListError { get; init; }

        public (string Name, Exception Error)? DescribeError { get; set; }

        public Action<string>? OnDescribe { get; set; }

        public JobQuery? LastQuery { get; private set; }

        public int PagesRequested { get; private set; }

        public List<string> Described { get; } = [];

        /// <summary>Filters like the service, but ignores the total so the panel's own cap is tested.</summary>
        public async IAsyncEnumerable<IReadOnlyList<TrainingJobSummary>> ListTrainingJobsAsync(JobQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastQuery = query;
            await Task.Yield();
            if (ListError is not null)
            {
                throw ListError;
            }

            var matching = jobs
                .Where(j => j.TrainingJobName.Contains(query.NameContains, StringComparison.OrdinalIgnoreCase))
                .Where(j => query.Status.Length == 0 || j.TrainingJobStatus.Value == query.Status)
                .Select(j => new TrainingJobSummary { TrainingJobName = j.TrainingJobName, TrainingJobStatus = j.TrainingJobStatus, CreationTime = j.CreationTime })
                .ToList();
            for (var i = 0; i < matching.Count; i += PageSize)
            {
                PagesRequested++;
                yield return matching.Skip(i).Take(PageSize).ToList();
            }
        }

        public Task<DescribeTrainingJobResponse> DescribeTrainingJobAsync(string name, CancellationToken cancellationToken)
        {
            lock (Described)
            {
                Described.Add(name);
            }

            OnDescribe?.Invoke(name);
            if (DescribeError is { } error && error.Name == name)
            {
                return Task.FromException<DescribeTrainingJobResponse>(error.Error);
            }

            return Task.FromResult(jobs.Single(j => j.TrainingJobName == name));
        }

        public void Dispose()
        {
        }
    }
}
