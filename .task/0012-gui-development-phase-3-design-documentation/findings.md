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
