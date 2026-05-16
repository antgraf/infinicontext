"""Verify whether `--settings '{"disableAllHooks":true}'` suppresses the
SessionStart:startup hook_response error event."""

from __future__ import annotations
import json, os, shutil, subprocess, sys, threading, time
from queue import Empty, Queue


def resolve_argv():
    if os.name == "nt":
        cli = os.path.join(os.environ.get("APPDATA", ""), "npm", "node_modules", "@anthropic-ai", "claude-code", "cli.js")
        node = shutil.which("node")
        if node and os.path.isfile(cli):
            return [node, cli]
    return [shutil.which("claude") or "claude"]


EMPTY_CWD = os.path.join(os.environ.get("TEMP", "/tmp"), "empty-claude-probe-hooks")
os.makedirs(EMPTY_CWD, exist_ok=True)


def reader(stream, q):
    for line in stream:
        q.put(line.rstrip("\r\n"))
    q.put("")


def run(name, extra_flags):
    argv = [*resolve_argv(), "--print", "--verbose",
            "--input-format", "stream-json", "--output-format", "stream-json",
            "--model", "claude-haiku-4-5",
            "--system-prompt", "Reply with exactly OK.",
            "--tools", "",
            "--strict-mcp-config", "--mcp-config", '{"mcpServers":{}}',
            "--setting-sources", "project",
            "--disable-slash-commands",
            "--no-session-persistence",
            *extra_flags]
    env = os.environ.copy()
    env["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false"
    env["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1"
    env["CLAUDE_AGENT_SDK_DISABLE_BUILTIN_AGENTS"] = "1"
    env["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1"
    env["CLAUDE_CODE_DISABLE_POLICY_SKILLS"] = "1"
    proc = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            text=True, bufsize=1, cwd=EMPTY_CWD, env=env)
    q = Queue()
    threading.Thread(target=reader, args=(proc.stdout, q), daemon=True).start()
    proc.stdin.write('{"type":"user","message":{"role":"user","content":"ok"}}\n')
    proc.stdin.flush()
    hook_responses = 0
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        try:
            line = q.get(timeout=2)
        except Empty:
            continue
        if not line:
            break
        try:
            evt = json.loads(line)
        except json.JSONDecodeError:
            continue
        if evt.get("type") == "system" and evt.get("subtype") == "hook_response":
            hook_responses += 1
            print(f"  [{name}] hook fired: {evt.get('hook_name')} outcome={evt.get('outcome')} exit_code={evt.get('exit_code')}")
        if evt.get("type") == "result":
            break
    try: proc.stdin.close()
    except Exception: pass
    try: proc.wait(timeout=3)
    except subprocess.TimeoutExpired: proc.kill()
    print(f"  [{name}] total hook_response events: {hook_responses}")


print("=== WITHOUT --settings disableAllHooks ===")
run("no-hooks-flag", [])
print()
print("=== WITH --settings '{\"disableAllHooks\":true}' ===")
run("with-hooks-flag", ["--settings", '{"disableAllHooks":true}'])
