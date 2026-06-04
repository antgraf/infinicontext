# Model upgrade and context nudge bug fixes

Fix following issues:
- CLI and GUI: Upgrade main model to Opus 4.8. However, currently the model name is scattered accross all code. Centralize this with constants. So, in future it would require a single edit.
- CLI: when context nudge is detected app said "[task] hard-nudge flush detected - auto-rotating to task...". However nothing happened after this. It stuck forever.