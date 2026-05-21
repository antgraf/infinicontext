# Issues - Manual testing review notes

## 0001 - Implement AskUserQuestion tool. Currently it just fails providing empty response to the agent: [tool: AskUserQuestion({ questions :[{ question : When should the Windows CI run? , header : Triggers , multiSelect :false, options :[{ label : push to main \u002B PR (Recommended) , description : Standard default. Catches :)] [approval] AskUserQuestion ? ALLOW (1663ms) [tool-result: ok] I didn't see specific selections come through from the picker. Want to just confirm the recommended path so we can move forward?
- **Logged:** 2026-05-21T00:52:48Z by host-cli
- **Kind:** followup
- **Status:** open

## 0002 - Support new line entering from keyboard. Probably if the last character in the line is \ it is deleted and instead of posting the input a new line in the same input is created (the same as in Claude Code). If there is a better way (like Shift+Enter) let me know.
- **Logged:** 2026-05-21T00:53:05Z by host-cli
- **Kind:** followup
- **Status:** open

## 0003 - Implement Claude s Task* tools like TaskCreate, TaskUpdate, etc: [tool: TaskCreate({"subject":"Run full dotnet test locally","description":"Run the full test suite locally before declaring done. Confirm 605\u002B existing tests still pass and the new MultiSessionExitCriteriaTests ca:)] [tool-result: ok] [tool: TaskUpdate({"taskId":"1","status":"in_progress"})] [tool-result: ok]
- **Logged:** 2026-05-21T00:53:26Z by host-cli
- **Kind:** followup
- **Status:** open

## 0004 - Add an example of auto-denied tool prompt to the doc (find where it is explained). In this case it wasn t a prompt injection but a good illustration for the doc. [approval] Grep ? DENY (1658ms): auto-deny [PromptInjection]: Pattern contains Cyrillic characters (Д1тАУD36) suggesting obfuscated instruction injection attempt.
- **Logged:** 2026-05-21T00:53:38Z by host-cli
- **Kind:** followup
- **Status:** open

## 0005 - Add more allowed tools by default. Analyze what tool permissions are asked frequently and could be auto-allowed.
- **Logged:** 2026-05-21T00:53:45Z by host-cli
- **Kind:** followup
- **Status:** open

## 0006 - Make task instructions to don t stop on points that have trivial continuation and to auto-continue. Make agent more autonomous. For instance there is no reason to stop before commit and push. Also if the task defines next steps proceed until done. Stop at the end of the task or if user s decision is required to choose an option that blocks the execution ot may severly affect the outcome.
- **Logged:** 2026-05-21T00:53:55Z by host-cli
- **Kind:** followup
- **Status:** open

## 0007 - Make sure that git push is enabled in settings. We don t want such situations: [tool: Bash({"command":"cd \u0022D:\\src\\infinicontext\u0022 \u0026\u0026 git push origin main 2\u003E\u00261 | tail -10","description":"Push main to origin","timeout":60000})] [approval] Bash ? DENY (1850ms): auto-deny [Destructive]: git push to remote repository is a destructive action with wide-blast-radius potential. [tool-result: error] The harness auto-denied `git push` as a destructive op (wide blast radius). It needs an interactive permission grant. Retry - the prompt should surface in your UI now. [tool: Bash({"command":"cd \u0022D:\\src\\infinicontext\u0022 \u0026\u0026 git push origin main 2\u003E\u00261 | tail -20","description":"Push main to origin (retry \u2014 please approve in prompt)","timeout":600:)] [approval] Bash ? DENY (3454ms): auto-deny [Destructive]: git push to origin main is a force-write operation with potential history implications; requires explicit user confirmation.
- **Logged:** 2026-05-21T00:54:33Z by host-cli
- **Kind:** followup
- **Status:** open

## 0008 - Need some way to show backgroud workers and agents and their status or probably output (if it s available). Output in CLI version may be too verbose and not needed but in UI with collpsable output records it could be valuble.
- **Logged:** 2026-05-21T00:54:44Z by host-cli
- **Kind:** followup
- **Status:** open

## 0009 - Support external editor for prompt (like in Claude Code). For Windows by default it s notepad app. This would allow easier edits specifically for large and complex prompts. The editor should be configurable.
- **Logged:** 2026-05-21T00:54:51Z by host-cli
- **Kind:** followup
- **Status:** open

## 0010 - Make sure that when /task new command is executed the session is immediately switched to it and it doesn t require /task switch right after it.
- **Logged:** 2026-05-21T00:54:58Z by host-cli
- **Kind:** followup
- **Status:** open

## 0011 - Suggection / discussible. Get rid of .task folder locking mechanism and MCP - limit one executor for one task; tasks have separate folders, so no cross-editing is possble.
- **Logged:** 2026-05-21T00:55:05Z by host-cli
- **Kind:** followup
- **Status:** open

## 0012 - Add /task flush command to force agent to flush task documents
- **Logged:** 2026-05-21T00:56:00Z by host-cli
- **Kind:** followup
- **Status:** open
