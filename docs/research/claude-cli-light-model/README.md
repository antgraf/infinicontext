# Artifacts — `claude` CLI light-model recipe

Raw scripts and result CSVs supporting [`../claude-cli-light-model.md`](../claude-cli-light-model.md). Read the paper first; this folder is the data appendix.

## Scripts

| File | Used for |
|---|---|
| `bench-singleshot.py` | §4 baseline single-shot bench (un-tightened, MAX config). Fresh `claude` process per call, N from `$env:N` (default 10). |
| `bench-singleshot-tight.py` | §5–§6 tightened single-shot bench. **The primary deliverable script.** Adds `CLAUDE_CODE_DISABLE_THINKING=1`, `CLAUDE_CODE_MAX_OUTPUT_TOKENS=128`, and a tightened system prompt. |
| `bench-claude-longlived.py` | §7.1 long-lived stream-json bench with `/clear` between turns. CLI flags: `--n`, `--csv`, `--model`, `--cwd`, `--verbose`, etc. |
| `probe-flags.py` | §3.1 — enumerates which flags drop which fields from the `system/init` event. Used to derive the MAX configuration. |
| `probe-hooks.py` | Verifies `--setting-sources project` already suppresses user-level hooks (zero `hook_response` events emitted). |
| `probe-api-ms-trivial.py` | §5 — trivial-prompt probe that isolates the API-time and startup-time floors on the same MAX config. |

## Result CSVs

| File | Configuration | Headline |
|---|---|---|
| `results-singleshot-max-n10.csv` | npm 2.1.112, MAX, no thinking suppression | p50 **11 614 ms**, $0.0083/call |
| `results-singleshot-native-n10.csv` | native 2.1.133, MAX, no thinking suppression | p50 **9 203 ms**, $0.0074/call |
| `results-singleshot-tight-n10.csv` | native 2.1.133, MAX + tightened prompt only | p50 **5 847 ms**, $0.0030/call |
| `results-singleshot-tight-nothink-n10.csv` | native 2.1.133, MAX + tightened prompt + DISABLE_THINKING + MAX_OUTPUT_TOKENS=128 | **p50 2 325 ms, $0.00071/call** (the headline) |
| `results-trivial-n5.csv` | native 2.1.133, MAX, "ping → ok" | api_ms ~1 500 ms, startup ~1 100 ms (the floors) |
| `results-longlived-native-n10.csv` | native 2.1.133, MAX, long-lived + `/clear` | shows `in_tok` growth → sudden reset (negative result) |
| `results-longlived-max-n10.csv` | npm 2.1.112, MAX, long-lived + `/clear` | same negative result, cross-version |
| `results-longlived-minimal-n10.csv` | npm 2.1.112, OLD-minimal, long-lived | earlier minimal config — not load-bearing, kept for completeness |
| `results-longlived-n10.csv` | npm 2.1.112, full context, long-lived | rejected baseline (~$0.057 for "hi") |

## Reproducing

Each script is self-contained, no shared imports beyond stdlib. Requires:

- Python 3.11+
- `claude` 2.1.133+ on `PATH` (or at `~/.local/bin/claude.exe` on Windows)
- `claude login` completed (OAuth credentials in keychain or `%USERPROFILE%\.claude\.credentials.json`)
- Ideally **no** `ANTHROPIC_API_KEY` in the environment, so the test exercises the OAuth-only path

To reproduce the headline number:

```powershell
$env:N = "10"
python docs/research/claude-cli-light-model/bench-singleshot-tight.py
```

Expected stderr summary: `wall: mean ~2 300 ms, p50 ~2 300 ms, p95 ~2 900 ms; cost ~$0.0007/call`.

## Versioning note

These CSVs are a snapshot from 2026-05-07 against `claude` 2.1.133 (and 2.1.112 for the cross-check rows). Anthropic ships `claude` updates frequently. The flag and env-var contract is *not* guaranteed stable; before relying on these recipes in production, re-run the bench against the target version. The most likely failure mode is `CLAUDE_CODE_DISABLE_THINKING=1` being renamed or removed — see §8 of the paper for the limitation discussion.
