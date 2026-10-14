const assert = require("node:assert/strict");
const test = require("node:test");
const path = require("node:path");
const Module = require("node:module");
const resolve = Module._resolveFilename;
Module._resolveFilename = function(request, ...rest) {
    return request === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js") : resolve.call(this, request, ...rest);
};
const h = require("../out/processHistory.js");
const { createCliRunner } = require("../out/cliRunner.js");
const vscode = require("./stubs/vscode-stub.js");
vscode.EventEmitter = class { event() {} fire() {} dispose() {} };
const { ProcessTree } = require("../out/processTree.js");
const envelope = data => JSON.stringify({ summary: "sample", hints: [], data });
const A = "a".repeat(32), B = "b".repeat(32);
const artifact = { artifactId: B, kind: "heap", supportedViews: ["summary", "types"] };
const capture = { captureId: A, name: "Saved heap", createdUtc: "2026-10-14T00:00:00Z", state: 1, artifacts: [artifact] };
const row = (id, name) => ({ processId: id, commandLine: "SECRET-TOKEN", managedEntrypointAssemblyName: name,
    operatingSystem: "Linux", processArchitecture: "x64", runtimeVersion: "10" });
test("processes drop sensitive command lines and explicitly bound retained inventory", () => {
    const inventory = h.parseProcessInventory(envelope(Array.from({ length: 501 }, (_, i) => row(i + 1, `App${i}`))));
    assert.equal(inventory.processes.length, 500);
    assert.equal(inventory.total, 501);
    assert.equal(inventory.truncated, true);
    assert.ok(!JSON.stringify(inventory).includes("SECRET"));
    assert.equal(h.filterProcesses(inventory.processes, "App490").length, 1);
    assert.throws(() => h.parseProcessInventory(envelope([row(-1, "invalid")])), /invalid process/);
});
test("exited and detectably reused PIDs fail instead of attaching", () => {
    const saved = h.parseProcessInventory(envelope([row(42, "App")])).processes[0];
    assert.equal(h.validateTarget(saved, [saved]), saved);
    assert.throws(() => h.validateTarget(saved, []), /no longer available/);
    assert.throws(() => h.validateTarget(saved, [{ ...saved, identity: "Other" }]), /different process/);
});
test("history parses advertised views and validates query/delete intent", () => {
    assert.equal(h.parseCaptureList(envelope({ captures: [capture], nextAfterCaptureId: B })).nextAfterCaptureId, B);
    const detail = h.parseCaptureShow(JSON.stringify({ capture }));
    assert.deepEqual(h.captureQueryArgs(detail, B, "types"),
        ["query", "--capture-id", A, "--artifact-id", B, "--view", "types", "--json"]);
    assert.throws(() => h.captureQueryArgs(detail, B, "thread-statics"), /advertised offline/);
    assert.throws(() => h.captureQueryArgs({ ...detail, state: "interrupted" }, B, "types"), /sealed/);
    assert.throws(() => h.captureDeleteArgs(A, false), /confirmation/);
    assert.ok(h.captureDeleteArgs(A, true).includes("high"));
    assert.throws(() => h.captureShowArgs("../capture"), /lower-case hex/);
    assert.throws(() => h.parseEnvelope(JSON.stringify({ error: { kind: "NotFound", message: "gone" } })), /NotFound: gone/);
    assert.throws(() => h.parseCaptureList(envelope({ captures: [{ ...capture, state: 99 }] })), /unknown capture state/);
});
test("saved snapshots require explicit risk acknowledgement and use existing persistence", () => {
    assert.throws(() => h.persistCaptureArgs("heap-live", 42, false), /acknowledgement/);
    assert.deepEqual(h.persistCaptureArgs("heap-gcdump", 42, true),
        ["inspect-heap", "--source", "gcdump", "--pid", "42", "--persist", "--acknowledge-risk", "high", "--json"]);
    assert.deepEqual(h.persistCaptureArgs("thread-snapshot", 42, true).slice(0, 3), ["collect", "--kind", "thread-snapshot"]);
});
test("bounded subprocess runner returns explicit failure, never partial JSON", async () => {
    const run = createCliRunner({ timeoutMs: 5000, graceMs: 500, settleMs: 500, maxOutputChars: 1000 });
    const result = await run(process.execPath, ["-e", "process.stdout.write('x'.repeat(100000))"]);
    assert.equal(result.outputLimitExceeded, true);
    assert.equal(result.stdout, "");
    assert.equal(result.stderr, "");
});
test("process tree revalidates before context actions without a quick-pick and remembers selection", async () => {
    const original = h.runHistoryCli;
    const calls = [], stored = new Map();
    let data = [row(42, "App")];
    h.runHistoryCli = async () => envelope(data);
    const state = { get: key => stored.get(key), update: async (key, value) => stored.set(key, value) };
    const tree = new ProcessTree(state, () => "cli", async (process, action) => calls.push({ process, action }), { appendLine() {} });
    try {
        const items = await tree.getChildren();
        await tree.invoke(items[0], "cpu");
        assert.equal(calls[0].process.processId, 42);
        assert.equal(calls[0].action, "cpu");
        assert.equal(stored.get("dotnetDiagnostics.lastProcess").processId, 42);
        data = [row(42, "Other")];
        await assert.rejects(tree.invoke(items[0], "threads"), /different process/);
        assert.equal(calls.length, 1);
        data = [];
        await assert.rejects(tree.invoke(items[0], "heap"), /no longer available/);
    } finally { h.runHistoryCli = original; tree.dispose(); }
});
