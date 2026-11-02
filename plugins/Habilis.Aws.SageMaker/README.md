# SageMaker Training Jobs plugin

A read-only Habilis Terminal panel with the Amazon SageMaker training jobs of one AWS account and region, much like
the Training jobs page of the SageMaker console: the most recent jobs first, running jobs on top, with status,
secondary status, warm pool, duration and an estimated cost. Open a job for a readable summary or its full record,
and save the full records of selected jobs as JSON files. Implements issue #47.

## Install

Build with `tools/plugins/Build-Plugins.ps1`, then copy `artifacts/plugins/aws.sagemaker` into
`%LOCALAPPDATA%\HabilisTerminal\plugins\aws.sagemaker` (or the `plugins` folder next to a portable `habi.exe`) and
restart Habilis Terminal. No administrator rights are needed.

## Open

- F11 > **SageMaker training jobs**, or the drive menu (Alt+F1 / Alt+F2) > Plugins > **SageMaker training jobs**.
- The command line: `sm:` (the profile and region from the settings), `sm:prod` (profile `prod`, its configured
  region), `sm:prod/eu-west-1`.
- `..` at the top returns to the folder the panel showed before.

## Keys

| Key | Action |
|---|---|
| F3 / Enter | Summary of the job under the cursor (see below) |
| Shift+F3 | Full record: the complete DescribeTrainingJob response as indented JSON |
| F5 | Save the full records of the selected jobs as `<job name>.json` into the other panel's folder |
| Ctrl+I | Filter: part of the job name and the job status (saved in the settings) |
| Shift+F2 | Switch profile and region |
| Ctrl+R | Refresh |
| Ctrl+F3 / Ctrl+F5 / Ctrl+F12 | Sort by name / creation time / sort menu (creation time, duration, status, estimated cost) |
| Ctrl+1 / Ctrl+2 / Ctrl+3 | Layouts: Console (all columns) / Brief (created, status, cost) / Status (status, secondary status, warm pool, pool time left) |
| Ctrl+L | Summary in the other panel: profile, region, filters, jobs shown, load time, credentials |
| F9 > Commands > Plugin panel commands | All commands, including Settings |

Running jobs (InProgress, Stopping) stay above finished ones whatever the sort. With row colouring on, completed jobs
are green, failed red, stopped dimmed, in-progress highlighted and stopping in the warning colour.

### The summary (F3)

What comes first depends on the job's state:

- **Running** (InProgress, Stopping): progress, i.e. the secondary status steps with their start times, how long each
  took (the current one marked), the status messages and the time elapsed since creation.
- **Completed** (and Stopped): outputs, i.e. the S3 location of the model artifacts and the final metric values.
- **Failed**: the failure reason and the last step.

Then the details (times, instances, spot, image, channels, output path, role, warm pool, hyperparameters) and the
cost estimate with its basis. The summary, the full record and F5 always describe the job afresh, so a running job
shows its current state.

### Saving records (F5)

Each selected job is written as `<job name>.json`, the same JSON as the full record. Every file is written to a
temporary name in the target folder and then moved into place, so a failure or a cancel never leaves a partial file.
An existing file is offered for overwrite or skip (with "all" variants and Cancel). F6 (move) is not offered: the
plugin never changes anything in AWS.

## Settings

F9 > Options > Plugins > SageMaker Training Jobs (Alt+Shift+F9), or the panel's Settings command. Kept in Habilis
Terminal's plugin settings (`plugin-settings\aws.sagemaker.json`); never credentials.

- **AWS profile** and **Region**: where the panel opens.
- **Jobs listed**: how many of the most recent jobs are listed, 1 to 1000 (default 100).
- **Job name contains** and **Job status**: the filter, also set with Ctrl+I.
- **Colour rows by job status** (default on).
- **Regions offered when switching**.
- **Request timeout**.
- **AWS credentials file**: blank uses the standard `%USERPROFILE%\.aws\credentials` and `config`.

Changes apply to open panels at once.

## Credentials and permissions

The plugin uses the standard AWS shared config and credentials files, including profiles that assume a role and SSO
profiles (run `aws sso login --profile <name>` first). Credentials stay in memory; nothing is written to disk or logs.
The plugin connects only to Amazon SageMaker (and STS / SSO when a profile needs them) and sends nothing anywhere
else: no telemetry, no update checks.

It only reads. Required IAM permissions:

- `sagemaker:ListTrainingJobs`
- `sagemaker:DescribeTrainingJob`

A missing permission is reported by name, e.g. "Permission missing: sagemaker:DescribeTrainingJob". The AWS managed
policy `AmazonSageMakerReadOnly` includes both.

## How the cost is estimated

For each job:

    on-demand price per instance-hour × instance count × billable hours
    + on-demand price per instance-hour × instance count × warm pool retained billable hours

- **Billable hours** are SageMaker's `BillableTimeInSeconds` when reported. For a running job, the time since
  `TrainingStartTime` (zero before training starts). Otherwise `TrainingTimeInSeconds`, or training end minus start.
- **Instances**: the instance type and count; for a heterogeneous cluster, every instance group is priced separately;
  when SageMaker chose among several instance preferences, the selected type and count.
- **Warm pool**: `WarmPoolStatus.ResourceRetainedBillableTimeInSeconds` when SageMaker reports it. A warm pool that
  is still available keeps costing until it ends or is reused; the summary says so.
- **Prices** come from a small table of on-demand SageMaker training prices in USD for us-east-1, built into the
  plugin (`prices.json`, embedded). Its basis is stated in every summary: "Estimate from on-demand us-east-1 prices
  as of 2025-01; managed spot, discounts and other regions differ."
- An instance type missing from the table shows **?** as the cost; the summary names the type.

**The built-in prices were compiled from general knowledge and must be checked against the AWS pricing page
(https://aws.amazon.com/sagemaker/pricing/, Training, On-Demand) before relying on them.** To correct or extend them,
or to use another region's prices, put your own `prices.json` next to `Habilis.Aws.SageMaker.dll` in the plugin
folder; it replaces the built-in table. The format:

```json
{
  "basis": "on-demand eu-west-1 prices as of 2025-06",
  "perHour": {
    "ml.m5.xlarge": 0.26,
    "ml.g5.2xlarge": 1.69
  }
}
```

The `basis` text is shown in every estimate. If the file cannot be read, the built-in table is used and the summary
says why.

Limits of the estimate:

- It is not billing. Managed spot training (billed at spot prices, usually far lower), Savings Plans, discounts,
  credits, free tier, other regions, storage, data transfer and taxes are not taken into account.
- The duration column is the time from creation to the end (or to now while running), as in the console; the cost
  uses the billable time, which is usually shorter.

## Limits

- At most 1000 jobs are listed; the most recent come first. Filters are applied by SageMaker, so a narrow filter in a
  large account may need several requests.
- The list is a snapshot: press Ctrl+R to refresh. If any request fails, the previous list stays and the error is
  shown; a partial list is never presented as complete.
- Out of scope: creating, stopping, deleting, tagging or editing jobs; resources other than training jobs; training
  logs and metrics dashboards; exact billing.

## Third-party components

AWS SDK for .NET (`AWSSDK.Core`, `AWSSDK.SageMaker`, `AWSSDK.SecurityToken`, `AWSSDK.SSO`, `AWSSDK.SSOOIDC`), Apache
License 2.0.

## Manual test plan (real account)

Use an account where you may run small training jobs, with a profile that has only the two read permissions above
for the plugin (a second, more privileged profile creates the jobs).

1. With the privileged profile, start three short jobs on `ml.m5.large` (e.g. a built-in algorithm on a tiny data
   set): one that completes, one that fails (point it at a missing S3 object), and one with a long max runtime that
   you stop by hand. Start a fourth with `KeepAlivePeriodInSeconds` set (warm pool), and keep a fifth running while
   you test.
2. Open `sm:<read-only profile>/<region>`. Expect the jobs newest first, the running one on top and highlighted,
   completed green, failed red, stopped dimmed. Compare status, secondary status, duration, warm pool status and
   time left with the console.
3. Ctrl+1 / Ctrl+2 / Ctrl+3: the three layouts. Ctrl+F12 > Estimated cost and Duration; the running job stays on top.
4. F3 on the running job: progress first; F3 again a minute later shows the elapsed time grown. F3 on the completed
   job: the model artifacts S3 URI and the final metrics, matching the console. F3 on the failed job: the failure
   reason as in the console.
5. Check one cost by hand: instance price from the AWS pricing page × instance count × billable seconds (console,
   job details) / 3600. It matches the estimate if the price table is current.
6. Shift+F3: the full record; compare with `aws sagemaker describe-training-job --training-job-name <name>`.
7. Select three jobs, F5 into a local folder: three `.json` files. F5 again: answer Skip, then Overwrite all. Start a
   large F5 and cancel it: no `.tmp` or partial `.json` file remains.
8. Ctrl+I: name part of one job, status Failed; only matching jobs are listed and Ctrl+L shows the filter. Clear it
   (blank name, All).
9. Settings: Jobs listed 2. Only the two newest jobs are listed. Turn row colouring off: rows lose their colours.
10. Shift+F2: switch to another region with no jobs: an empty list. Switch to a profile without
    `sagemaker:DescribeTrainingJob`: the error names that permission and the previous list stays.
11. With an SSO profile, let the session expire and press Ctrl+R: the message says to run `aws sso login`.
12. Disconnect the network and press Ctrl+R: a plain-language network message; the previous list stays.
13. Delete the test jobs' S3 outputs when done.
