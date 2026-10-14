const assert = require("node:assert/strict");
const test = require("node:test");
const {
    INSTALL_COMMAND,
    actionsForReport,
    checkEnvironment,
    parseDoctorOutput,
    parseHandshakeOutput,
    runEnvironmentCheckFlow,
    summarizeReport,
    UPDATE_COMMAND,
} = require("../out/environmentCheck.js");

// Shape emitted by `dotnet-diagnostics-cli doctor --json` (DiagnosticResult<PreflightReport>).
function doctorJson(checks, overall) {
    return JSON.stringify({
        summary: "Preflight",
        hints: [{ nextTool: "fix-environment", reason: "x", priority: "normal" }],
        data: { os: "linux", overall, checks, hasBlocker: overall === "Blocked" },
        cancelled: false,
    });
}

const okCheck = { id: "clrmd-attach", title: "ClrMD live attach (ptrace)", status: "Ok", reason: "ptrace_scope=0" };
const ptraceBlocked = {
    id: "clrmd-attach",
    title: "ClrMD live attach (ptrace)",
    status: "Blocked",
    reason: "kernel.yama.ptrace_scope=1 blocks peer attach.",
    remediation: "Grant CAP_SYS_PTRACE (docker: --cap-add SYS_PTRACE).",
    affectedTools: ["collect_thread_snapshot"],
};
const uidBlocked = {
    id: "socket-uid",
    title: "Diagnostic IPC socket UID match",
    status: "Blocked",
    reason: "Target runs as UID 0; this process runs as UID 1000.",
    remediation: "Run the CLI as the same UID as the target.",
};
const perfDegraded = {
    id: "offcpu-perf",
    title: "Off-CPU sampling (perf sched_switch)",
    status: "Degraded",
    reason: "perf_event_paranoid=2",
    remediation: "sudo sysctl -w kernel.perf_event_paranoid=-1",
    affectedTools: ["collect_sample(kind=\"off_cpu\")"],
};

const HELLO_OK = '{"type":"hello","protocolVersion":2}\n';
const result = (o = {}) => ({ exitCode: 0, stdout: "", stderr: "", ...o });

function runner(handshake, doctor) {
    const calls = [];
    const run = async (cliPath, args, stdin) => {
        calls.push({ cliPath, args, stdin });
        return args[0] === "stream" ? handshake : doctor;
    };
    run.calls = calls;
    return run;
}

test("parseDoctorOutput parses a realistic doctor envelope", () => {
    const report = parseDoctorOutput(doctorJson([perfDegraded, okCheck, { id: "socket-uid", title: "t", status: "NotApplicable", reason: "r" }], "Degraded"));
    assert.equal(report.os, "linux");
    assert.equal(report.overall, "Degraded");
    assert.equal(report.hasBlocker, false);
    assert.equal(report.checks.length, 3);
    assert.equal(report.checks[0].remediation, "sudo sysctl -w kernel.perf_event_paranoid=-1");
    assert.deepEqual(report.checks[1].affectedTools, []);
});

test("parseDoctorOutput rejects malformed output", () => {
    assert.throws(() => parseDoctorOutput("Unknown command 'doctor'."));
    assert.throws(() => parseDoctorOutput("{}"));
    assert.throws(() => parseDoctorOutput(JSON.stringify({ data: { os: "linux", overall: "Weird", checks: [] } })));
});

test("parseHandshakeOutput distinguishes ok, unsupported and invalid", () => {
    assert.deepEqual(parseHandshakeOutput(HELLO_OK), { kind: "ok", protocolVersion: 2 });
    assert.deepEqual(
        parseHandshakeOutput('{"type":"error","code":"protocol_version_unsupported","message":"m","supportedVersions":[1]}\n'),
        { kind: "unsupported", supportedVersions: [1] },
    );
    assert.deepEqual(parseHandshakeOutput("Unknown command 'stream'.\n"), { kind: "invalid" });
    assert.deepEqual(parseHandshakeOutput(""), { kind: "invalid" });
});

test("healthy environment is ready and sends only hello then doctor", async () => {
    const run = runner(result({ stdout: HELLO_OK }), result({ stdout: doctorJson([okCheck], "Ok") }));
    const report = await checkEnvironment("my-cli", run);
    assert.equal(report.ready, true);
    assert.equal(report.protocolVersion, 2);
    assert.deepEqual(report.issues, []);
    assert.deepEqual(run.calls.map(c => c.args), [["stream", "--protocol", "jsonl"], ["doctor", "--json"]]);
    assert.equal(run.calls[0].cliPath, "my-cli");
    assert.equal(JSON.parse(run.calls[0].stdin).protocolVersion, 2);
});

test("degraded checks stay ready but carry warnings with remediation", async () => {
    const run = runner(result({ stdout: HELLO_OK }), result({ stdout: doctorJson([perfDegraded, okCheck], "Degraded") }));
    const report = await checkEnvironment("cli", run);
    assert.equal(report.ready, true);
    assert.equal(report.issues[0].kind, "environment-degraded");
    assert.equal(report.issues[0].severity, "warning");
    assert.match(report.issues[0].guidance, /perf_event_paranoid/);
});

test("blocked ptrace and UID checks parse despite doctor exiting 1", async () => {
    const run = runner(result({ stdout: HELLO_OK }), result({ exitCode: 1, stdout: doctorJson([ptraceBlocked, uidBlocked], "Blocked") }));
    const report = await checkEnvironment("cli", run);
    assert.equal(report.ready, false);
    assert.deepEqual(report.issues.map(i => i.kind), ["environment-blocked", "environment-blocked"]);
    assert.match(report.issues[0].guidance, /SYS_PTRACE/);
    assert.match(report.issues[1].guidance, /same UID/);
});

test("missing executable gives guided install path", async () => {
    const run = runner(result({ exitCode: null, spawnError: "spawn nope ENOENT" }), result());
    const report = await checkEnvironment("nope", run);
    assert.equal(report.ready, false);
    assert.equal(report.issues[0].kind, "cli-missing");
    assert.equal(report.issues[0].command, INSTALL_COMMAND);
    assert.equal(run.calls.length, 1);
});

test("missing .NET runtime is distinguished from a missing CLI", async () => {
    const stderr = "You must install or update .NET to run this application.\nFramework: 'Microsoft.NETCore.App', version '10.0.0' not found";
    const report = await checkEnvironment("cli", runner(result({ exitCode: 150, stderr }), result()));
    assert.equal(report.issues[0].kind, "runtime-missing");
    assert.match(report.issues[0].guidance, /self-contained/);
});

test("old CLI answering protocol_version_unsupported is outdated", async () => {
    const stdout = '{"type":"error","code":"protocol_version_unsupported","message":"Protocol version 2 is not supported.","supportedVersions":[1]}\n';
    const report = await checkEnvironment("dotnet-diagnostics-cli", runner(result({ exitCode: 2, stdout }), result()));
    assert.equal(report.ready, false);
    assert.equal(report.issues[0].kind, "cli-outdated");
    assert.match(report.issues[0].message, /Protocol mismatch/);
    assert.equal(report.issues[0].command, UPDATE_COMMAND);
});

test("pre-stream CLI that prints usage is treated as outdated", async () => {
    const report = await checkEnvironment("cli", runner(result({ stdout: "Unknown command 'stream'.\n\nUsage:" }), result()));
    assert.equal(report.issues[0].kind, "cli-outdated");
});

test("CLI supporting only a newer protocol means the extension is outdated", async () => {
    const stdout = '{"type":"error","code":"protocol_version_unsupported","message":"m","supportedVersions":[3]}\n';
    const report = await checkEnvironment("cli", runner(result({ exitCode: 2, stdout }), result()));
    assert.equal(report.issues[0].kind, "extension-outdated");
});

test("handshake timeout and malformed doctor output are reported as failures", async () => {
    const timedOut = await checkEnvironment("cli", runner(result({ exitCode: null, timedOut: true }), result()));
    assert.equal(timedOut.issues[0].kind, "cli-failed");
    const badDoctor = await checkEnvironment("cli", runner(result({ stdout: HELLO_OK }), result({ exitCode: 3, stderr: "boom" })));
    assert.equal(badDoctor.issues[0].kind, "cli-failed");
    assert.match(badDoctor.issues[0].message, /boom/);
});

function host(handshakes, doctors, choices, cliPath = "cli") {
    const notifications = [];
    const copied = [];
    const calls = { walkthrough: 0, settings: 0 };
    return {
        notifications,
        copied,
        calls,
        getCliPath: () => cliPath,
        run: async (cliPath, args) => (args[0] === "stream" ? handshakes : doctors).shift() ?? result(),
        notify: async (severity, message, actions) => {
            notifications.push({ severity, message, actions });
            return choices.shift();
        },
        copyToClipboard: async text => { copied.push(text); },
        openWalkthrough: async () => { calls.walkthrough++; },
        openSettings: async () => { calls.settings++; },
        log() {},
    };
}

test("missing CLI flow copies the install command, never runs it, and rechecks to success", async () => {
    const h = host(
        [result({ exitCode: null, spawnError: "ENOENT" }), result({ stdout: HELLO_OK })],
        [result({ stdout: doctorJson([okCheck], "Ok") })],
        ["Copy Install Command", "Recheck"],
    );
    const report = await runEnvironmentCheckFlow(h);
    assert.equal(report.ready, true);
    assert.deepEqual(h.copied, [INSTALL_COMMAND]);
    assert.deepEqual(h.notifications[0].actions, ["Copy Install Command", "Open Settings", "Open Getting Started", "Recheck"]);
    assert.equal(h.notifications[0].severity, "error");
    assert.equal(h.notifications.at(-1).severity, "info");
});

test("protocol mismatch offers the walkthrough and opens it", async () => {
    const h = host([result({ stdout: "Unknown command 'stream'." })], [], ["Open Getting Started"], "dotnet-diagnostics-cli");
    const report = await runEnvironmentCheckFlow(h);
    assert.equal(report.issues[0].kind, "cli-outdated");
    assert.ok(h.notifications[0].actions.includes("Open Getting Started"));
    assert.ok(h.notifications[0].actions.includes("Copy Update Command"));
    assert.equal(h.calls.walkthrough, 1);
    assert.deepEqual(h.copied, []);
});

test("actionsForReport offers no recheck when ready", () => {
    assert.deepEqual(actionsForReport({ cliPath: "c", ready: true, issues: [] }), []);
});

test("host-only success summary says target permissions are not verified", async () => {
    const socket = { id: "socket-uid", title: "t", status: "NotApplicable", reason: "No target" };
    const report = await checkEnvironment("cli", runner(result({ stdout: HELLO_OK }), result({ stdout: doctorJson([okCheck, socket], "Ok") })));
    const summary = summarizeReport(report);
    assert.match(summary, /Host environment ready/);
    assert.match(summary, /not verified/);
    assert.doesNotMatch(summary, /all checks passed/);
});

test("target-scoped check passes --pid to doctor and drops the unverified note", async () => {
    const socket = { id: "socket-uid", title: "t", status: "Ok", reason: "UID matches" };
    const run = runner(result({ stdout: HELLO_OK }), result({ stdout: doctorJson([okCheck, socket], "Ok") }));
    const report = await checkEnvironment("cli", run, 1234);
    assert.deepEqual(run.calls[1].args, ["doctor", "--json", "--pid", "1234"]);
    assert.doesNotMatch(summarizeReport(report), /not verified/);
});

test("install command uses supported dotnet tool syntax", () => {
    assert.equal(INSTALL_COMMAND, "dotnet tool install -g dotnet-diagnostics-cli");
    assert.doesNotMatch(INSTALL_COMMAND, /--version/);
});

test("custom cliPath outdated guidance does not offer the global update", async () => {
    const run = runner(result({ stdout: "Unknown command 'stream'." }), result());
    const report = await checkEnvironment("/opt/tools/dd-cli", run);
    const issue = report.issues[0];
    assert.equal(issue.kind, "cli-outdated");
    assert.equal(issue.command, undefined);
    assert.match(issue.guidance, /\/opt\/tools\/dd-cli/);
    const actions = actionsForReport(report);
    assert.ok(!actions.includes("Copy Update Command"));
    assert.ok(actions.includes("Open Settings"));
    const defaultReport = await checkEnvironment("dotnet-diagnostics-cli", runner(result({ stdout: "Unknown command 'stream'." }), result()));
    assert.equal(defaultReport.issues[0].command, UPDATE_COMMAND);
    assert.ok(actionsForReport(defaultReport).includes("Copy Update Command"));
});
