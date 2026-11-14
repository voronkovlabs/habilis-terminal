#!/bin/bash
# File operations through the UI on a Mac (issue #316): starts habi, drives it with System Events key codes in a
# temporary folder, and checks the results on disk. Screens go to the given folder; every check prints PASS or FAIL,
# and the script ends with the count. Runs on the macOS runner (macos-screens).
#   tools/mac/ui-operations.sh <habi executable> <screens folder>
set -u
habi=$1
screens=$2
mkdir -p "$screens"
root=/tmp/habi-ops
trace=/tmp/habi-ops-trace.log
rm -rf "$root" "$trace"
mkdir -p "$root/left/folder/sub" "$root/right"
for name in alpha beta gamma delta epsilon; do echo "$name" > "$root/left/$name.txt"; done
echo inner > "$root/left/folder/sub/inner.txt"
seq 1 300 > "$root/left/long.txt"

passed=0
failed=0
check() { # description, test...
  local what=$1
  shift
  if "$@"; then echo "PASS $what"; passed=$((passed + 1)); else echo "FAIL $what"; failed=$((failed + 1)); fi
}

key() { # key code, modifiers ("shift down", "control down, shift down" or ""), pause
  local using=""
  [ -n "${2:-}" ] && using=" using {$2}"
  osascript -e "tell application \"System Events\" to key code $1$using" > /dev/null
  sleep "${3:-1}"
}
typed() { osascript -e "tell application \"System Events\" to keystroke \"$1\"" > /dev/null; sleep 0.5; }
enter() { key 36 "" "${1:-2}"; }
esc() { key 53 "" "${1:-1}"; }
shot() { screencapture -x "$screens/ops-$1.png"; }
# The command line: typed while a panel has the keys, run with Enter.
run() { typed "$1"; enter "${2:-3}"; }
goto() { run "goto $1"; }

HABI_TRACE=$trace SHELL=/bin/zsh "$habi" > /tmp/habi-ops.log 2>&1 &
pid=$!
sleep 20
osascript -e 'tell application "System Events" to set frontmost of (first process whose unix id is '"$pid"') to true' > /dev/null
sleep 2
shot 00-start

# The right panel at "right", then the left one at "left" (Tab switches panels).
key 48
goto "$root/right"
key 48
goto "$root/left"
shot 01-panels

# F5: copy a file to the other panel.
goto "$root/left/alpha.txt"
key 96 "" 2
shot 02-copy-dialog
enter
check "F5 copies a file" test -f "$root/right/alpha.txt" -a -f "$root/left/alpha.txt"

# F5 on a folder: the tree is copied.
goto "$root/left/folder"
key 96 "" 2
enter 3
check "F5 copies a folder tree" test -f "$root/right/folder/sub/inner.txt"

# F6: move to the other panel.
goto "$root/left/beta.txt"
key 97 "" 2
shot 03-move-dialog
enter
check "F6 moves a file" test -f "$root/right/beta.txt" -a ! -e "$root/left/beta.txt"

# Shift+F6: rename in place (the dialog gets the new name).
goto "$root/left/gamma.txt"
key 97 "shift down" 2
shot 04-rename-dialog
key 0 "command down" 0.5 # select all (Cmd+A)
typed "gamma-renamed.txt"
enter
check "Shift+F6 renames a file" test -f "$root/left/gamma-renamed.txt" -a ! -e "$root/left/gamma.txt"

# F7: a new folder.
key 98 "" 2
shot 05-mkdir-dialog
typed "made-here"
enter
check "F7 makes a folder" test -d "$root/left/made-here"

# F8: to the Trash.
goto "$root/left/delta.txt"
key 100 "" 2
shot 06-delete-dialog
enter
check "F8 deletes a file" test ! -e "$root/left/delta.txt"
check "F8 puts it in the Trash" test -f "$HOME/.Trash/delta.txt"

# Shift+Del: delete permanently (forward delete is key code 117).
goto "$root/left/epsilon.txt"
key 117 "shift down" 2
shot 07-delete-now-dialog
enter
check "Shift+Del deletes permanently" test ! -e "$root/left/epsilon.txt" -a ! -e "$HOME/.Trash/epsilon.txt"

# F3 and F4: the viewer and the editor open and close.
goto "$root/left/long.txt"
key 99 "" 3
shot 08-viewer
esc 2
key 118 "" 3
shot 09-editor
esc 2
shot 10-after-editor

# Esc on an empty command line: panel -> command line -> panel (#290, #313).
: > "$trace"
esc 2
shot 11-esc-to-console
esc 2
shot 12-esc-to-panel
echo "--- trace for Esc"; cat "$trace"
cp "$trace" "$screens/ops-trace-esc.log"
check "Esc gives the keys to the command line" grep -q "focus terminal" "$trace"
check "Esc gives the keys back to the panel" grep -q "keys to panel" "$trace"

# A long output makes the console grow over the panels (#312).
run "seq 1 120" 4
shot 13-console-grown
esc 2
shot 14-console-collapsed

# The command list (Ctrl+Shift+P): the scroll bar beside the shortcuts, not over them (#314).
key 35 "control down, shift down" 2
shot 15-command-list
esc 2

echo "--- habi output"; tail -40 /tmp/habi-ops.log
kill "$pid" 2> /dev/null
rm -rf "$root" "$HOME/.Trash/delta.txt"
echo "operations: $passed passed, $failed failed"
