# Task Manager plugin

A live, read-only Windows process list modelled on Task Manager's **Details** page (issue #182).

Open **Task Manager · Details** from F11 or a drive menu, or type `tasks:` on the command line.

- **Details:** PID, status, user, CPU, memory and architecture.
- **Performance (Ctrl+2):** CPU time, working set, private memory, threads and handles.
- **Identity (Ctrl+3):** user, session, start time and command line.
- Click a header or use Ctrl+F12 to sort. Quick search and selection work like any Habilis panel.
- F3 shows every available field; Ctrl+Q shows it in the other panel.
- Shift+F4 pauses and resumes the one-second refresh.

Some fields are blank for protected processes or processes owned by another user. The plugin does not request elevation
and cannot end or change a process.

## Test plan

1. Open `tasks:` and compare names, PIDs, users, CPU and memory with Task Manager > Details.
2. Start and close a test program: it appears and disappears while the cursor stays on the same process.
3. Sort by CPU and memory; switch layouts with Ctrl+1, Ctrl+2 and Ctrl+3.
4. Press Shift+F4, start a program and confirm the list stays still; resume and see it appear.
5. Use F3 and Ctrl+Q on an ordinary process and on a protected system process.
