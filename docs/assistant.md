# The assistant (experiment)

Ctrl+Shift+A (or F9, Commands, Assistant) opens the assistant: Claude Code working in this window. Ask it in plain words, such as "keep only the music in this folder". It looks at the panels and the console, says what it is going to do, and asks before every step that changes something.

Branch `experiment/assistant`, issue #197.

## How it works

- **The engine is Claude Code itself,** started headless:
  - the command is `claude -p --input-format stream-json --output-format stream-json --permission-prompts host`;
  - it runs in the active panel's folder;
  - your own Claude Code setup applies: sign-in, settings, CLAUDE.md, skills.
- **Its hands are `habi mcp`,** the Claude Control server (claude-control.md). It sees and drives this window: panels, marks, the command list, the console.
  - The session proves itself with a secret of its own, so the Claude Control setting may stay off.
  - Its console commands and settings changes do not ask a second time.
- **Permissions:** whatever Claude Code would ask in a terminal, the page asks with three buttons:
  - **Allow;**
  - **Allow** the tool for this conversation;
  - **Deny.**

  Reading files and looking at the panels never ask.
- **Deleting:** the assistant is told to move files to the Recycle Bin.

## Keys

| Key | Does |
|---|---|
| Enter | Send |
| Shift+Enter | New line |
| Ctrl+C (nothing selected), Stop | Stop the turn |
| Ctrl+N | New conversation |
| Esc | Hide the page. The session goes on, and a question brings the page back. |

## Settings (Claude)

| Setting | Default | Meaning |
|---|---|---|
| `assistant.provider` | `claude` | `claude`: Claude Code as set up on this computer (your subscription or API key). `bedrock`: Amazon Bedrock (`CLAUDE_CODE_USE_BEDROCK=1`). |
| `assistant.model` | empty | `opus`, `sonnet`, `haiku` or a full id. Empty means Claude Code's default. |
| `assistant.awsProfile`, `assistant.awsRegion` | empty | For Bedrock: the profile and the region with Claude models enabled. |
| `assistant.claudePath` | empty | The claude executable. Empty means it is looked for on PATH and in `~/.local/bin`. |

## Needs

- **Claude Code installed and signed in:** run `claude` once in a terminal, then `/login`.
- **For Bedrock:** an AWS profile allowed `bedrock:InvokeModel` on the Claude models.

## Not yet

- Markdown in replies shows as plain text.
- The page covers the panels; Esc shows them.
- There is no history of past conversations.
