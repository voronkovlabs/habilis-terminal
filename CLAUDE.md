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

## How the owner works
- **The current phase.** The owner tries the app and reports deviations from Far Manager or UI problems, often with a screenshot or a short video, in Russian.
  - Each report: open an issue, fix, test (`dotnet test` plus a UI check), commit and push to main, close the issue.
  - Close every test copy you started once your checks are done (`tools/ui/Stop-TestCopies.ps1`). Start a fresh copy with `tools/ui/New-TestCopy.ps1` and leave it running only when the owner asks ("запусти"). Report its path, what changed, and what was and was not verified.
- **"Запусти" / "запусти опять"** means: start a fresh test copy of the current build for the owner to try.
- **Far is the reference.** When a behaviour is unclear, offer "as in Far" as the recommended option.
- **Misreadings happen.** For example, Tab first got implemented as "show the other panel". When a report is ambiguous, ask with concrete options before building.
- **Don't pause between steps** of an agreed plan (PLAN.md stages and the like); ask only for real design decisions or outward actions.
- **Report honestly** what could not be checked: high DPI, several monitors, installer. Those go into `verify` issues.

## Development notes
`docs/development.md` covers build and test, UI checks (`tools/ui/New-TestCopy.ps1`, `tools/ui/uitool.ps1`), keyboard routing pitfalls, releases and known oddities. Read it before UI work.

## Standing rules
- **Tests and experiments** run only in temporary directories (`%TEMP%\habi-tests\...`). Never touch the owner's real files.
- **Language.** Code, docs, UI and GitHub are in English. Chat with the owner is in Russian.
- **Never without the owner's explicit go-ahead:** create or move a release tag, change system settings, download files.
- **Commits** end with the Co-Authored-By line from the session's attribution instructions.
