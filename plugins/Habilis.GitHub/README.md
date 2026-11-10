# GitHub

Browses GitHub in a panel, read-only: the owners you see (you first, then your organizations), their repositories, a
repository's branches, and a branch's folders and files.

- Opens from **F11**, the drive menu (**Alt+F1 / Alt+F2**) or the command line: `gh:`, `gh:owner`, `gh:owner/repo`,
  `gh:owner/repo/branch/path`, also after `cd`. A `/` in a branch name is written `%2F`.
- **Repositories:** when each was created, the date of the last commit on its default branch, and whether it is
  private, a fork or archived (archived ones are dimmed).
- **Branches:** the last commit's date and author at once. **Created** is the date of the branch's first commit that
  is not on the default branch, as GitHub keeps no creation date for branches; **Ahead** and **Behind** compare it with
  the default branch. These come in the background, a request per branch.
- **Files:** sizes, and the date of each entry's last commit, filled in the background for the folder you are in.
  **F3** views a file, **F5** copies files and folders out.
- **F9 > Panel:** Open in browser, Clone address, Refresh from GitHub, Sign out of GitHub.

## Signing in
The first time the panel opens it shows a short code and opens `github.com/login/device` in your browser. Enter the
code there and approve; the panel then opens. The program never sees your password. The token is kept in Windows
Credential Manager as `HabilisTerminal:github`; "Sign out of GitHub" removes it.

It asks GitHub for `repo` (private repositories; GitHub has no read-only scope for them, though the plugin only reads)
and `read:org`. An organization that restricts OAuth apps shows its repositories only after an owner of it approves
the Habilis Terminal app.

The OAuth App's client ID is built in; F9 > Options > Plugins > GitHub can replace it.

## Requests
Everything goes straight to `api.github.com` over HTTPS. A listing is one request per 100 repositories or branches, a
branch's whole tree is one request, and what was read is kept until "Refresh from GitHub" or Ctrl+R. Ctrl+L shows how
many requests of the hourly limit are left.
