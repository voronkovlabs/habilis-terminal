# S3 Browser for Habilis Terminal

Shows Amazon S3 as a file panel. Buckets are listed at the top level. Inside a bucket, key prefixes appear as folders
and objects as files, with their sizes and dates. You copy, view, delete and create with the same keys as on a local
disk.

## Install

1. Copy the plugin folder (the published `aws.s3` folder, which holds `plugin.json`, `Habilis.Aws.S3.dll` and the AWS
   SDK assemblies) to `%LOCALAPPDATA%\VoronkovLabs\HabilisTerminal\plugins\aws.s3`. No administrator rights are needed.
2. Restart Habilis Terminal. The plugin appears in F9 > Options > Plugins.

The plugin reads AWS profiles from the standard `%USERPROFILE%\.aws\config` and `%USERPROFILE%\.aws\credentials`
files (or the credentials file named in the settings). Access keys, role profiles and SSO profiles all work. For SSO,
run `aws sso login --profile <name>` first. It does not need the AWS CLI otherwise.

## Opening

- **F11 > S3 buckets**, or the drive menu (Alt+F1 / Alt+F2) > S3 buckets: the bucket list.
- **Command line:** `s3:` (the bucket list), `s3://bucket/` or `s3://bucket/folder/sub/`. The short form
  `s3:bucket/folder` works too, and a missing final `/` is added. `cd s3://bucket/folder/` in the console does the same, and so does pasting the address into the drive menu. An
  address without a final `/` that names an object opens the object's folder.
- **Folder history (Alt+F12) and bookmarks** reopen the location the panel showed.

The panel title is `S3 · profile · bucket/folder/`.

## Keys

| Key | In the bucket list | Inside a bucket |
|---|---|---|
| Enter | Opens the bucket | Opens a folder; on a file, views it |
| `..`, Ctrl+PgUp | Leaves the plugin | Goes up: folder → bucket root → bucket list |
| F3 | | Views a file (downloaded to a temporary copy) |
| F5 | Downloads whole buckets | Downloads files and folders (with everything under them) to the other panel's folder |
| F5 / F6 from a local panel | Refused (open a bucket first) | Uploads files and folder trees into the current folder; F6 deletes the local originals after a clean upload |
| F6 from the S3 panel | Not supported (moving from S3 to local disk is out of scope) | |
| F7 | Refused (creating buckets is out of scope) | Makes a folder |
| F8 | Refused (deleting buckets is out of scope) | Deletes files and folders after the host's confirmation |
| Shift+F2 | Switch profile | Switch profile (the panel stays where it is if the new profile can list it, else shows the bucket list) |
| Ctrl+L | Info: profile, region, location, counts, credentials kind | |
| Ctrl+1 / 2 / 3 | Layouts: Files (size, date), Storage class, Names | |
| Ctrl+R | Re-lists | |

F9 > Commands lists the plugin commands **Switch profile** and **Settings**. Every transfer and delete shows progress
with a Cancel button.

## Settings

F9 > Options > Plugins > S3 Browser, or the panel's **Settings** command. Changes apply to open panels.

| Setting | Default | Meaning |
|---|---|---|
| AWS profile | `default` | The profile from the AWS config and credentials files. |
| Default region | `us-east-1` | The region the bucket list is requested from. Each bucket is then reached in its own region. |
| Parallel transfers | 8 (1..64) | Requests running at once, counting both files and parts of large files. |
| Part size, MB | 16 (5..512) | Larger files are uploaded and downloaded in parts of this size. The size is raised automatically when a file would need more than 10,000 parts. |
| Request timeout, seconds | 60 (5..600) | Applies to each request. One part of a large file is one request. |
| Regions offered | five common regions | Offered first in the region list. |
| AWS credentials file | blank | Blank means the standard files. |

The settings never hold secrets. Credentials stay in memory and are never written to disk, logs or settings. The
plugin connects only to AWS and sends no telemetry.

## Required IAM permissions

| Permission | Needed for |
|---|---|
| `s3:ListAllMyBuckets` | The bucket list |
| `s3:GetBucketLocation` | Finding a bucket's region when it was opened by address (the bucket list usually reports regions itself) |
| `s3:ListBucket` | Listing folders. Also needed for downloads and deletes of folders, and for the "already exists" check on upload |
| `s3:GetObject` | Downloads and F3 |
| `s3:PutObject` | Uploads and F7 |
| `s3:DeleteObject` | F8 |
| `s3:AbortMultipartUpload` | Cleaning up a failed or cancelled upload of a large file |
| `s3:ListMultipartUploadParts` | Multipart uploads (part of the standard multipart permission set) |

When a permission is missing, the message names it, for example "Permission missing: s3:ListBucket".

## Behaviour and limits

- **Large folders** are listed 1,000 keys at a time. Each page shows as soon as it arrives.
- **Buckets in any commercial region** work from one panel. Each bucket's region is found once and kept.
- **Parallel transfers:** files, and the parts of large files, run in parallel up to the configured limit. Memory use
  stays bounded: each running request streams through one 80 KB buffer.
- **No partial files:**
  - A download is written to `name.habi-part` and renamed only when it is complete. A failed or cancelled download
    deletes that file.
  - A large upload uses an S3 multipart upload. On a failure or cancel the upload is aborted, so S3 keeps no partial
    object or parts. If even the abort fails, the message says so.
- **Byte-identical:** a download checks the byte count, and fails if the object changed in S3 after it was listed
  (ETag match).
- **Existing targets:** you are asked about each one, with Overwrite / Skip / Overwrite all / Skip all / Cancel.
  Cancel stops the operation; the items not done count as skipped.
- **Move up (F6 from a local panel):** the host deletes the local originals only when every item was uploaded,
  with none failed or skipped.
- **Key names:** every character S3 allows in a key works.
  - The panel shows keys exactly.
  - On download, characters Windows does not allow in file names are percent-encoded: `<>:"|?*\`, control
    characters, trailing dots and spaces, and device names such as `CON` or `nul.txt`. For example `what?.txt`
    becomes `what%3F.txt` and `CON` becomes `CO%4E`.
  - A `%` that would read as such an escape becomes `%25`, so two keys never get the same local name.
  - An empty path segment (the folder in `a//b`) is shown and saved as `%2F`.
  - Two keys that differ only in letter case cannot both be saved in one Windows folder. The second is reported as
    failed rather than overwriting the first.
- **Uploads** keep local names as they are, with `/` between folders. An empty local folder becomes a zero-byte
  `folder/` marker, as the AWS console makes folders. Links to other folders (junctions) are not followed; they are
  listed in the report.
- **Folder markers** (zero-byte `folder/` objects) are not shown as files. A downloaded folder marker becomes an empty
  local folder.
- **Archived objects** (S3 Glacier Flexible Retrieval, Deep Archive) are dimmed. Downloading one before it is restored
  gives a message saying it must be restored first.
- **Not included:** renaming or moving inside S3; moving from S3 to local disk; creating, deleting or renaming
  buckets; object versions; changing storage classes; presigned URLs; restoring archived objects; S3-compatible
  services other than Amazon S3.
- **Other limits:**
  - An object whose key ends in `/` but holds data is treated as a folder marker, so its data is not downloaded.
  - A key segment longer than Windows allows in a file name (255 characters) fails to download with a message.
  - A download keeps the list of keys to transfer in memory. A folder with millions of keys needs proportionate
    memory, about a hundred bytes per key.

## Third-party components

| Component | Licence |
|---|---|
| AWS SDK for .NET (`AWSSDK.Core`, `AWSSDK.S3`, and `AWSSDK.SecurityToken`, `AWSSDK.SSO`, `AWSSDK.SSOOIDC` for role and SSO profiles) | Apache License 2.0 |

None of them requires disclosing the plugin's source code.

## Manual test plan (real AWS account)

Use a test account or a sandbox bucket. Never use production data. Set up as follows:

- A profile `s3test` with the permissions above, and a second profile without `s3:ListBucket` on the test bucket.
- Two test buckets: `habi-test-<you>-use1` in us-east-1 and `habi-test-<you>-apse2` in ap-southeast-2.
- A local test folder `%TEMP%\habi-s3-manual` with:
  - a few small files;
  - a 200 MB file, for example `fsutil file createnew big.bin 209715200` and then some real content copied in;
  - a nested folder tree with an empty subfolder.
- In the us-east-1 bucket, uploaded with the AWS CLI:
  - keys `odd/what?.txt`, `odd/CON`, `odd/trailing.`, `odd/a//b.txt` and `odd/tab<TAB>name`;
  - 2,500 small objects under `many/`;
  - one object in the GLACIER storage class (`aws s3 cp x s3://.../cold.bin --storage-class GLACIER`).

| # | Steps | Expected |
|---|---|---|
| 1 | Settings: profile `s3test`. F11 > S3 buckets. | Both test buckets are listed. Ctrl+L shows the profile and the credentials kind. |
| 2 | Enter each bucket. | Both list; Ctrl+L shows us-east-1 and ap-southeast-2. |
| 3 | Type `s3://habi-test-<you>-apse2/` on the command line. | Opens directly. Alt+F12 lists the location and reopens it. |
| 4 | Open `many/`. | The 2,500 objects appear page by page; Ctrl+L counts 2,500. |
| 5 | Open `odd/`, F5 it to the local test folder. | Files are named `what%3F.txt`, `CO%4E`, `trailing%2E`, `%2F\b.txt` and `tab%09name`; their contents match. |
| 6 | Enter the `%2F` folder under `odd/`, then `..`. | `b.txt` is listed; `..` returns to `odd/`. |
| 7 | Upload the local tree and `big.bin` with F5. Watch progress, then download them again into another folder. | Progress moves smoothly. `fc /b` shows the files identical, the empty folder exists in S3, and `big.bin` was sent in parts. |
| 8 | Upload `big.bin` again and press Cancel halfway. | No `big.bin` is replaced. `aws s3api list-multipart-uploads --bucket ...` shows no leftover upload. |
| 9 | Download `big.bin` and Cancel halfway. | No `big.bin` and no `big.bin.habi-part` remain locally. |
| 10 | Download a file that already exists locally; try Skip, then Overwrite, then Cancel. | Each answer is honoured; the panel reports the outcome. |
| 11 | F6 a local folder into the bucket. | The folder is uploaded and the local original is deleted. Repeat with one file locked (open in an editor with exclusive access): that file fails and the originals are kept. |
| 12 | F7 `new folder` in a bucket, then F8 on it. | It appears as a folder, then disappears. |
| 13 | F8 on `many/`. | All 2,500 objects are deleted with progress. |
| 14 | F3 on `cold.bin`. | A message says the object is archived and must be restored first. |
| 15 | In the bucket list, press F7, F8, and F5 from a local panel. | Each is refused with a clear message. |
| 16 | Shift+F2 to the profile without `s3:ListBucket` while inside the bucket. | The panel shows the bucket list with a status message. Entering the bucket says "Permission missing: s3:ListBucket". |
| 17 | Set an SSO profile whose session expired, then Ctrl+R. | The message says to run `aws sso login --profile ...`. |
| 18 | Unplug the network and press Ctrl+R. | "Cannot reach AWS" appears, and the host stays responsive. |
| 19 | Settings: parallel transfers 1, part size 5 MB; upload `big.bin`. | Slower, but it works; Ctrl+L shows the new values in open panels. |
| 20 | Clean up: delete the test buckets' contents with the AWS CLI and remove the local test folder. | |
