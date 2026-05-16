"""Sanity-check that api_ms in the safety bench is dominated by the real
safety prompt + structured output, not by some baseline API overhead. Same
MAX-stripped config and resolution path as bench-singleshot.py, but with:
  - a trivial 1-line system prompt
  - a trivial user prompt ("Reply 'ok'")
  - no --json-schema (no thinking, no structured output)

If api_ms here is ~2-3s while the safety bench was ~6-15s, that proves the
delta is prompt complexity (and output token count), not startup or API
floor. Then the "api_ms dominates wall_ms" analysis stands.
"""
from __future__ import annotations
import json, os, shutil, statistics, subprocess, sys, threading, time
from queue import Empty, Queue


def resolve_argv():
    if os.name == "nt":
        cli = os.path.join(os.environ.get("APPDATA", ""), "npm", "node_modules", "@anthropic-ai", "claude-code", "cli.js")
        node = shutil.which("node")
        if node and os.path.isfile(cli):
            return [node, cli]
    return [shutil.which("claude") or "claude"]


SYS = "Reply with exactly the word 'ok' and nothing else."
PROMPT = "ping"

EMPTY_CWD = os.path.join(os.environ.get("TEMP", "/tmp"), "empty-claude-trivial")
os.makedirs(EMPTY_CWD, exist_ok=True)


def reader(stream, q):
    for line in stream:
        q.put(line.rstrip("\r\n"))
    q.put("")


def one_call() -> dict:
    argv = [*resolve_argv(),
            "--print", "--verbose",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--model", "claude-haiku-4-5",
            "--system-prompt", SYS,
            "--tools", "",
            "--strict-mcp-config", "--mcp-config", '{"mcpServers":{}}',
            "--setting-sources", "project",
            "--disable-slash-commands",
            "--no-session-persistence"]
    env = os.environ.copy()
    env["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false"
    env["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1"
    env["CLAUDE_AGENT_SDK_DISABLE_BUILTIN_AGENTS"] = "1"
    env["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1"
    env["CLAUDE_CODE_DISABLE_POLICY_SKILLS"] = "1"

    t0 = time.monotonic()
    proc = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            text=True, bufsize=1, cwd=EMPTY_CWD, env=env)
    q: Queue = Queue()
    threading.Thread(target=reader, args=(proc.stdout, q), daemon=True).start()
    proc.stdin.write(json.dumps({"type": "user", "message": {"role": "user", "content": PROMPT}}) + "\n")
    proc.stdin.flush()

    result = None
    deadline = time.monotonic() + 60.0
    while time.monotonic() < deadline:
        try:
            line = q.get(timeout=2.0)
        except Empty:
            continue
        if not line:
            break
        try:
            evt = json.loads(line)
        except json.JSONDecodeError:
            continue
        if evt.get("type") == "result":
            result = evt
            break
    elapsed = (time.monotonic() - t0) * 1000

    try: proc.stdin.close()
    except Exception: pass
    try: proc.wait(timeout=3)
    except subprocess.TimeoutExpired: proc.kill()

    out = {"ms": int(elapsed), "ok": False}
    if result and not result.get("is_error"):
        u = result.get("usage", {}) or {}
        out.update({
            "ok": True,
            "in_tok": u.get("input_tokens"),
            "cache_read": u.get("cache_read_input_tokens"),
            "cache_create": u.get("cache_creation_input_tokens"),
            "out_tok": u.get("output_tokens"),
            "cost": result.get("total_cost_usd"),
            "api_ms": result.get("duration_api_ms"),
        })
    return out


def main():
    n = int(os.environ.get("N", "5"))
    print(f"idx,ms,api_ms,startup_ms,in_tok,cache_create,cache_read,out_tok,cost,ok")
    api_ms_list = []
    wall_ms_list = []
    for i in range(1, n + 1):
        s = one_call()
        startup = s["ms"] - (s.get("api_ms") or 0) if s["ok"] else ""
        print(f"{i},{s['ms']},{s.get('api_ms','')},{startup},{s.get('in_tok','')},{s.get('cache_create','')},{s.get('cache_read','')},{s.get('out_tok','')},{s.get('cost','')},{1 if s['ok'] else 0}")
        if s["ok"] and s.get("api_ms"):
            api_ms_list.append(s["api_ms"])
            wall_ms_list.append(s["ms"])
    if api_ms_list:
        print(f"\n--- summary (n={len(api_ms_list)}) ---", file=sys.stderr)
        print(f"api_ms: mean={statistics.mean(api_ms_list):.0f}  min={min(api_ms_list)}  max={max(api_ms_list)}", file=sys.stderr)
        print(f"wall:   mean={statistics.mean(wall_ms_list):.0f}  min={min(wall_ms_list)}  max={max(wall_ms_list)}", file=sys.stderr)
        startups = [w - a for w, a in zip(wall_ms_list, api_ms_list)]
        print(f"startup (wall - api): mean={statistics.mean(startups):.0f}  min={min(startups)}  max={max(startups)}", file=sys.stderr)


if __name__ == "__main__":
    main()
