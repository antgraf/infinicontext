"""Probe what each additional flag/env var actually drops from the init event.

For each config variant, spawns claude with stream-json input/output, sends ONE
user message ("ok"), captures init + result, kills the process, and prints a
compact summary: tools / mcp / slash_commands / skills / plugins / agents counts
plus token usage for the single turn.

Usage:  python probe-flags.py
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import threading
import time
from queue import Empty, Queue


def resolve_argv() -> list[str]:
    if os.name == "nt":
        cli = os.path.join(os.environ.get("APPDATA", ""), "npm", "node_modules", "@anthropic-ai", "claude-code", "cli.js")
        node = shutil.which("node")
        if node and os.path.isfile(cli):
            return [node, cli]
    return [shutil.which("claude") or "claude"]


SYS = "Reply with exactly OK."
USER = '{"type":"user","message":{"role":"user","content":"ok"}}'

EMPTY_CWD = os.path.join(os.environ.get("TEMP", "/tmp"), "empty-claude-probe")
os.makedirs(EMPTY_CWD, exist_ok=True)


def base_args() -> list[str]:
    return [
        "--print", "--verbose",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--model", "claude-haiku-4-5",
        "--system-prompt", SYS,
        "--tools", "",
        "--strict-mcp-config",
        "--mcp-config", '{"mcpServers":{}}',
        "--no-session-persistence",
    ]


def base_env() -> dict[str, str]:
    e = os.environ.copy()
    e["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false"
    e["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1"
    return e


def reader(stream, q):
    for line in stream:
        q.put(line.rstrip("\r\n"))
    q.put("")


def run_variant(name: str, extra_args: list[str], extra_env: dict[str, str] | None = None) -> dict:
    argv = [*resolve_argv(), *base_args(), *extra_args]
    env = base_env()
    if extra_env:
        env.update(extra_env)

    t0 = time.monotonic()
    proc = subprocess.Popen(
        argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, bufsize=1, cwd=EMPTY_CWD, env=env,
    )
    q: "Queue[str]" = Queue()
    threading.Thread(target=reader, args=(proc.stdout, q), daemon=True).start()
    threading.Thread(target=reader, args=(proc.stderr, q), daemon=True).start()

    try:
        proc.stdin.write(USER + "\n")
        proc.stdin.flush()
    except Exception as ex:
        return {"name": name, "error": f"stdin write failed: {ex}"}

    init = result = None
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
        if evt.get("type") == "system" and evt.get("subtype") == "init":
            init = evt
        elif evt.get("type") == "result":
            result = evt
            break

    elapsed = (time.monotonic() - t0) * 1000
    try:
        proc.stdin.close()
    except Exception:
        pass
    try:
        proc.wait(timeout=3)
    except subprocess.TimeoutExpired:
        proc.kill()

    summary = {
        "name": name,
        "wall_ms": int(elapsed),
        "tools": len(init.get("tools", [])) if init else None,
        "mcp": len(init.get("mcp_servers", [])) if init else None,
        "slash": len(init.get("slash_commands", [])) if init else None,
        "skills": len(init.get("skills", [])) if init else None,
        "plugins": len(init.get("plugins", [])) if init else None,
        "agents": len(init.get("agents", [])) if init else None,
        "memory": init.get("memory_paths") if init else None,
    }
    if result:
        u = result.get("usage", {}) or {}
        summary.update({
            "in_tok": u.get("input_tokens"),
            "cache_create": u.get("cache_creation_input_tokens"),
            "cache_read": u.get("cache_read_input_tokens"),
            "out_tok": u.get("output_tokens"),
            "cost": result.get("total_cost_usd"),
            "api_ms": result.get("duration_api_ms"),
            "ok": not result.get("is_error"),
        })
    return summary


EXTRA_AGENT_ENV = {"CLAUDE_AGENT_SDK_DISABLE_BUILTIN_AGENTS": "1"}
EXTRA_MD_ENV = {"CLAUDE_CODE_DISABLE_CLAUDE_MDS": "1"}
EXTRA_POLICY_ENV = {"CLAUDE_CODE_DISABLE_POLICY_SKILLS": "1"}
EXTRA_HOOKS_FLAGS = ["--settings", '{"disableAllHooks":true}']

ALL_FLAGS = [
    "--disable-slash-commands",
    "--setting-sources", "project",
    *EXTRA_HOOKS_FLAGS,
]
ALL_ENV = {**EXTRA_AGENT_ENV, **EXTRA_MD_ENV, **EXTRA_POLICY_ENV}

VARIANTS = [
    ("baseline (already-known minimal)", [], None),
    ("+ --disable-slash-commands", ["--disable-slash-commands"], None),
    ("+ --setting-sources project", ["--setting-sources", "project"], None),
    ("+ disable-slash + setting-sources project", ["--disable-slash-commands", "--setting-sources", "project"], None),
    ("+ env DISABLE_BUILTIN_AGENTS", [], EXTRA_AGENT_ENV),
    ("+ env DISABLE_CLAUDE_MDS", [], EXTRA_MD_ENV),
    ("+ env DISABLE_POLICY_SKILLS", [], EXTRA_POLICY_ENV),
    ("+ --settings disableAllHooks:true", EXTRA_HOOKS_FLAGS, None),
    ("MAX (all flags + all env)", ALL_FLAGS, ALL_ENV),
]


def main() -> int:
    print(f"{'name':<55} {'tools':>6} {'mcp':>4} {'slsh':>4} {'skil':>4} {'plug':>4} {'agts':>4} {'in':>5} {'cR':>5} {'cC':>5} {'out':>4} {'cost_usd':>9} {'wall_ms':>7}")
    print("-" * 130)
    for name, args, extra_env in VARIANTS:
        try:
            s = run_variant(name, args, extra_env)
        except Exception as ex:
            print(f"{name:<55}  ERROR: {ex}")
            continue
        if "error" in s:
            print(f"{s['name']:<55}  ERROR: {s['error']}")
            continue
        print(
            f"{s['name']:<55} {s.get('tools','-'):>6} {s.get('mcp','-'):>4} "
            f"{s.get('slash','-'):>4} {s.get('skills','-'):>4} {s.get('plugins','-'):>4} "
            f"{s.get('agents','-'):>4} "
            f"{s.get('in_tok','-'):>5} {s.get('cache_read','-'):>5} {s.get('cache_create','-'):>5} "
            f"{s.get('out_tok','-'):>4} {s.get('cost', '-'):>9} {s['wall_ms']:>7}"
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
