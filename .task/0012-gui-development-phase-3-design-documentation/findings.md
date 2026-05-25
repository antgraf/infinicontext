# Findings and Ideas

## 1. Why & What Happened
It's a long term goal. Make sure that operator can easily understand what and why happened in the task. Was some event issues that triggered an operation? Did agent decide to do a task? For now it's abstract but it will need more details and refinment later.

## 2. Reduce context thresholds
For 1M models spft and hard context size thresholds should be lowered to 45% anf 65% accordingly. Leave current thresholds for smaller context models.

## 3. Allow rm om project files
Make sure that any files and folders operations are allowed inside of home cwd folder. It may be fixed already.

## 4. Support dark theme for permission banner
Currently permission banner is always light-yellow collor. However in dark theme the font is white which is mostly invisible on light-yellow background. It may be fixed already.

## 5. New task button
Make "+ New task" button functional.

## 6. Store main window size in settings
Store  main window state (windowed / maximized) in gui settings json.

## 7. Improve message enqueing
Currently if session is processing (sending / receiving data to / from agent) operator's messages are queued. However there is no any indicator of such messages. So operator send the same message several times confused. Also, need to check that such messages are sent automatically when user input is requested. I have an impression that they are not sent until any new user message is sent. Also, if several messages are enqued they are processed separately (at least this is how they are represented in UI). They must be just glued together and sent and processed as a single message.

## 8. Task archiving
Need to introduce a way to archive or "complete" tasks. Tree view should have two root nodes "Active" - uncollapsed by default, "Archived" - collapsed by default. Also, such an operation need to perform some task completion operations. For istance, check if there any deffered or posrponed tasks, reported issues. And propose actions on them. Ususally saving them in todo list or issue-tracker.

## 9. Task tree view improvements
Currrently there is no way to collapse / uncollapse tree nodes. Need this ability.

## 10. Incorrect symbols in CLI auto-mode output [SHIPPED 2026-05-24 - 62 mojibake sequences across 28 files swept; see progress.md]
CLI auto-mode verdict lines render mojibake. Example: `[approval] Edit ╨▓тФ╝' ALLOW (2440ms)`. The three chars between `Edit` and `ALLOW` (`╨▓`, `тФ╝`, `'`) are a CP1251 misread of UTF-8 bytes `E2 86 92` = U+2192 RIGHTWARDS ARROW (`тЖТ`). Same root cause as the GUI fix in slice 4c: C# source files without a UTF-8 BOM are parsed by the C# compiler under the system's ANSI locale (win-1251), so any non-ASCII literal in source gets corrupted at compile time. Fix shape: scan `src/InfiniContext.Cli/` for non-ASCII literals (arrow symbols, em-dashes, etc.), replace with `char.ConvertFromUtf32(0xNNNN)` runtime construction OR add a UTF-8 BOM to the affected source files. The arrow used in `ConsoleEchoingApprovalSink`'s verdict formatter is the obvious starting point.
