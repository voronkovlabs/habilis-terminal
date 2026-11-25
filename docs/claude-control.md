# Claude control

Claude (Claude Code, Claude Desktop, any MCP client) can drive a running Habilis Terminal:

- see both panels;
- move them, mark files and run any command of the command list;
- run console commands and read what they print;
- open files in the viewer or editor;
- take a screenshot of the window;
- read and change settings.

## Turning it on

Settings, **Claude**, **Claude Control**:

| Value | What Claude may do |
|---|---|
| `off` (default) | Nothing: every control call is refused. |
| `ask` | Everything, but each console command and each settings change waits for your **Allow**. |
| `trusted` | Everything without asking. |

In every mode:

- **The app's own confirmations show as for you:** delete, overwrite and the like.
- **Claude cannot change Claude Control itself.**
- **While calls come, the title bar shows ● Claude.** Every call is written to `control.log` beside `settings.json`.
- **Only your Windows user can reach the API:** a named pipe with `CurrentUserOnly`.

## Connecting Claude

`habi mcp` is an MCP server over stdio. It talks to the running app: the one in `HABI_PID`, which is set in the app's own consoles, or else the newest `habi` running.

**Claude Code**, from any folder:

```powershell
claude mcp add habilis -- "$env:LOCALAPPDATA\HabilisTerminal\current\habi.exe" mcp
```

**Claude Desktop**, in `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "habilis": { "command": "C:\\Users\\<you>\\AppData\\Local\\HabilisTerminal\\current\\habi.exe", "args": ["mcp"] }
  }
}
```

## Tools

| Tool | What it does |
|---|---|
| `get_state` | Both panels: location, cursor, marked names, first rows. Also the page over them and the console's folder and state. Start here. |
| `get_panel` | One panel in detail. |
| `navigate` | A folder, a file (its folder, the cursor on it) or a plugin address (`s3://bucket/`) in a panel. |
| `activate_panel`, `set_cursor`, `select` | Which panel is active, where its cursor is, what is marked: `set`, `add`, `remove`, `clear`. |
| `list_commands`, `run_command` | The command list (Ctrl+Shift+P) and running one by id, as if chosen. |
| `run_in_console` | Runs a command line in the console, waits for it, returns the exit code and the output. |
| `read_console` | The console's last lines. |
| `view_file`, `edit_file`, `close_page` | The viewer (F3) and editor (F4). |
| `screenshot` | The window as a picture. |
| `get_settings`, `set_setting` | Settings and their values. |

The methods behind them are in [plugin-api.md](plugin-api.md).
