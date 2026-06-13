// SupervisorHostProbe — minimal real host for the POSIX host-lifecycle smoke (task 0023, C2/C3).
// Driven by tests/InfiniContext.Tests/Supervision/PosixSignalSupervisorTests.cs.
//
// It wires the production IProcessSupervisor exactly as the CLI host does at startup
// (ProcessSupervisor.CreateForCurrentPlatform -> PosixSignalSupervisor on macOS/Linux), attaches a
// long-running child, and mirrors MultiSessionHost's ownership of Ctrl+C (Console.CancelKeyPress
// with e.Cancel = true — "interrupt the turn", not "shut down"). It then prints the child PID and a
// READY marker and blocks forever, so only a signal can end it. The smoke then drives:
//
//   * SIGINT  -> the probe AND its child must stay alive (C2: the supervisor must not reap on the
//                interrupt gesture the host owns),
//   * SIGTERM -> the probe AND its child must exit (C3: reap children, then actually terminate the
//                host — no zombie).
//
// Only meaningful on POSIX; the smoke is [MacOnlyFact]. /bin/sh is present on macOS/Linux by default.

using System.Diagnostics;
using InfiniContext.Core.Supervision;

using var supervisor = ProcessSupervisor.CreateForCurrentPlatform();

// Mirror MultiSessionHost: the interactive host owns Ctrl+C as "interrupt the current turn", so it
// must stay alive on SIGINT. On .NET/Unix this installs a SIGINT handler; e.Cancel = true suppresses
// termination. The supervisor (post-fix) does NOT register SIGINT, so nothing reaps the child here.
Console.CancelKeyPress += (_, e) => e.Cancel = true;

var psi = new ProcessStartInfo
{
    FileName = "/bin/sh",
    Arguments = "-c \"sleep 600\"",
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};
using var child = Process.Start(psi)!;
supervisor.Attach(child);

Console.WriteLine($"CHILD_PID={child.Id}");
Console.WriteLine("READY");
Console.Out.Flush();

// Block until a host-terminating signal ends us through the supervisor's handler (which reaps the
// child, then Environment.Exit's). Timeout.Infinite means we never self-exit.
await Task.Delay(Timeout.Infinite);
return 0;
