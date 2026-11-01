# Habilis Terminal: notes for Claude

## Work tracking: GitHub issues with the `claude` label
The to-do and done list lives in the GitHub issues of voronkovlabs/habilis-terminal that carry the `claude` label. Keep it current on your own, without being asked.

- **At the start of every session:**
  - Run `gh issue list --label claude --state open`.
  - Read the issues that relate to the request.
- **Each new remark or request from the owner becomes an issue.** Open it before or as work starts. Skip one-off questions that need no change.
- **While working**, comment on the issue with findings and decisions worth keeping.
- **When done**, close the issue with a comment naming the commits, and add it to the open release issue (for example "Release v0.1.1").
- **Built but not checked in a real setup?** Add the `verify` label, or open a follow-up issue.
- **New follow-ups you notice** become issues too.
- **Writing an issue:**
  - Everything on GitHub is in English.
  - The body has **Summary**: plain words for the owner, what and why.
  - It also has **Context for Claude**: files, causes, mechanisms, test names, commits. Enough to pick the work up in a new session without the chat history.
  - Labels: `claude`, plus `bug` / `enhancement` / `verify` as fits.
- `docs/v0.1.1-findings.md` (and later versions' findings) stays the detailed release log.

## Standing rules
- **Tests and experiments** run only in temporary directories (`%TEMP%\habi-tests\...`). Never touch the owner's real files.
- **Language.** Code, docs, UI and GitHub are in English. Chat with the owner is in Russian.
- **Never without the owner's explicit go-ahead:** create or move a release tag, change system settings, download files.
- **Commits** end with the Co-Authored-By line from the session's attribution instructions.
