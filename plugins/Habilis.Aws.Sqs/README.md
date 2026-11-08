# SQS Queues plugin

A Habilis Terminal panel with the Amazon SQS queues of one AWS account and region: each queue's approximate number of
messages available and in flight. From the panel you can purge (remove all messages from) the queues you choose.
Implements issue #48.

## Install

Build with `tools/plugins/Build-Plugins.ps1`, then copy `artifacts/plugins/aws.sqs` into
`%LOCALAPPDATA%\VoronkovLabs\HabilisTerminal\plugins\aws.sqs` (or the `plugins` folder next to a portable `habi.exe`) and restart
Habilis Terminal. No administrator rights are needed.

## Open

- F11 > **SQS queues**, or the drive menu (Alt+F1 / Alt+F2) > Plugins > **SQS queues**.
- The command line: `sqs:` (the profile and region from the settings), `sqs:prod` (profile `prod`, its configured
  region), `sqs:prod/eu-west-1`.
- `..` at the top returns to the folder the panel showed before.

## Keys

| Key | Action |
|---|---|
| F8 | Purge the selected queues (or the one under the cursor) after a confirmation that names them; the outcome is reported per queue |
| Shift+F2 | Switch profile and region |
| F3 / Enter | Queue details: name, URL, counts |
| Ctrl+R | Refresh |
| Ctrl+F3 / Ctrl+F12 | Sort by name / sort menu (messages available, in flight) |
| Ctrl+1 / Ctrl+2 | Layouts: with counts / names only |
| Ctrl+L | Summary in the other panel: profile, region, filter, queues shown and hidden, load time, credentials |
| F9 > Commands > Plugin panel commands | All commands, including Settings |

Queues with no messages available are dimmed.

## Settings

F9 > Options > Plugins > SQS Queues (Alt+Shift+F9), or the panel's Settings command. Kept in Habilis Terminal's
plugin settings (`plugin-settings\aws.sqs.json`); never credentials.

- **AWS profile** and **Region**: where the panel opens.
- **Queue name prefix**: only queues whose names start with it; blank lists all queues.
- **Hide queues matching**: wildcard patterns (`*`, `?`), one per line, e.g. `*-dlq`.
- **Regions offered when switching**.
- **Request timeout**.
- **AWS credentials file**: blank uses the standard `%USERPROFILE%\.aws\credentials` and `config`.

Changes apply to open panels at once.

## Credentials and permissions

The plugin uses the standard AWS shared config and credentials files, including profiles that assume a role and SSO
profiles (run `aws sso login --profile <name>` first). Credentials stay in memory; nothing is written to disk or logs.
The plugin connects only to Amazon SQS (and STS / SSO when a profile needs them) and sends nothing anywhere else.

Required IAM permissions:

- `sqs:ListQueues`
- `sqs:GetQueueAttributes`
- `sqs:PurgeQueue` (only for purging)

A missing permission is reported by name, e.g. "Permission missing: sqs:PurgeQueue".

## Limits

- Counts are approximate, as Amazon SQS reports them, and may lag by up to a minute.
- Amazon SQS allows one purge per queue every 60 seconds; a second purge in that time is reported as not purged.
- Out of scope: reading, sending or deleting individual messages; creating, deleting or configuring queues.

## Third-party components

AWS SDK for .NET (`AWSSDK.Core`, `AWSSDK.SQS`, `AWSSDK.SecurityToken`, `AWSSDK.SSO`, `AWSSDK.SSOOIDC`), Apache License
2.0.

## Manual test plan (real account)

Use a test account or a prefix only test queues match.

1. Create queues `habi-test-a`, `habi-test-b`, `habi-test-a-dlq`; send 3 messages to `habi-test-a`.
2. Settings: prefix `habi-test-`, hide `*-dlq`. Open `sqs:<profile>/<region>`.
   Expect `habi-test-a` (3 available) and `habi-test-b` (0, dimmed); Ctrl+L shows 2 shown, 1 hidden.
3. Receive one message from `habi-test-a` elsewhere without deleting it; Ctrl+R: 2 available, 1 in flight.
4. Sort by messages available (Ctrl+F12) both ways.
5. Mark both queues, F8: the confirmation names exactly those two; Cancel purges nothing.
6. F8 again, Purge: report lists both as purged; Ctrl+R after a minute shows 0.
7. F8 on `habi-test-a` again within 60 s: reported as not purged.
8. Shift+F2 to another region: the title and location change; Alt+F12 lists both locations.
9. Use a profile without `sqs:PurgeQueue`: purge reports the missing permission; listing still works.
10. Expire the credentials (or use a wrong key): a plain message suggests refreshing them; Habilis Terminal stays responsive.
11. Delete the test queues.
