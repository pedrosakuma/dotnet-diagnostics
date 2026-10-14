const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const fs = require("node:fs");
const Module = require("node:module");
const { createRequire } = Module;
const resolve = Module._resolveFilename;
Module._resolveFilename = function(request, ...rest) {
    return request === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js") : resolve.call(this, request, ...rest);
};
const h = require("../out/processHistory.js");
const p = require("../out/captureHistoryPanel.js");
const vscode = require("./stubs/vscode-stub.js");
vscode.EventEmitter = class { event() {} fire() {} dispose() {} };
const { CaptureHistory } = require("../out/captureHistory.js");
const A = "a".repeat(32), B = "b".repeat(32);
const detail = (kind = "heap-snapshot", views = ["top-types", "roots-by-kind"]) => ({
    captureId: A, createdUtc: "2026-10-14T00:00:00Z", name: "<script>saved</script>", state: "sealed",
    artifacts: [{ artifactId: B, kind, name: "Saved", supportedViews: views }],
});
test("gcdump, thread and GC saved captures open useful primary views", () => {
    assert.equal(p.defaultView(detail("heap-snapshot", ["top-types", "records"]), B), "top-types");
    assert.equal(p.defaultView(detail("thread-snapshot", ["threads-summary", "stack", "thread-statics"]), B), "threads-summary");
    assert.equal(p.defaultView(detail("gc-events", ["summary", "events"]), B), "summary");
    assert.equal(p.defaultView(detail("other-kind", ["summary"]), B), undefined);
});
test("offline query gates views before spawning and returns visible CLI errors", async () => {
    const result = await p.queryHistoryView("cli", detail(), B, "top-types", async (_cli, args) => {
        assert.ok(args.includes("--capture-id") && args.includes("--artifact-id"));
        return JSON.stringify({ summary: "Saved types", data: { topTypes: [] } });
    });
    assert.equal(result.ok, true);
    assert.deepEqual(result.data.topTypes, []);
    const refused = await p.queryHistoryView("cli", detail(), B, "thread-statics", () => { throw Error("must not execute"); });
    assert.equal(refused.ok, false);
    assert.match(refused.message, /not displayable/);
    const expired = await p.queryHistoryView("cli", detail(), B, "top-types", async () => { throw Error("NotFound"); });
    assert.equal(expired.message, "NotFound");
});
function node(tag) {
    return { tag, children: [], textContent: "", appendChild(child) { this.children.push(child); return child; },
        replaceChildren(...children) { this.children = children; } };
}
const text = n => [n.textContent, ...n.children.map(text)].join("\n");
test("primary history renderers are self-contained, text-only and report presentation caps", () => {
    const root = node("div");
    const render = new Function("document", `${p.SAVED_PRIMARY_RENDERER_SCRIPT}\nreturn renderSavedPrimary;`)({
        getElementById: () => root, createElement: node,
    });
    render("views", "top-types", { rankBy: "bytes", topTypes: Array.from({ length: 105 }, () =>
        ({ typeFullName: "<script>Type</script>", instanceCount: 1, totalBytes: 42, totalBytesPercent: 1 })) });
    assert.match(text(root), /<script>Type<\/script>/);
    assert.match(text(root), /5 omitted by the panel/);
    render("views", "threads-summary", { totalThreads: 10, omittedThreads: 9, threads: [{ managedThreadId: 1 }] });
    assert.match(text(root), /omitted: 9/);
    assert.match(text(root), /Unavailable/);
    render("views", "summary", { payload: { measurementStatus: "unavailable", totalCollections: 0, data: { generations: [] } } });
    assert.match(text(root), /elapsed is not suspension time/);
    assert.match(text(root), /unavailable/);
});
test("saved panel script parses with CSP, escaped names and correlated request handling", () => {
    const html = p.renderHistoryHtml("nonce", "csp", detail());
    assert.ok(!html.includes("<script>saved</script>"));
    assert.match(html, /script-src 'nonce-nonce'/);
    assert.match(html, /m.requestId !== current.requestId/);
    assert.match(html, /artifact.disabled = view.disabled = true/);
    const script = html.match(/<script nonce="nonce">([\s\S]*?)<\/script>/)[1];
    assert.doesNotThrow(() => new Function(script));
});
test("capture deletion is confirmed and performed only through the CLI", async () => {
    const original = h.runHistoryCli, calls = [];
    h.runHistoryCli = async (_cli, args) => {
        calls.push(args);
        return JSON.stringify({ data: args.includes("delete") ? { captureId: A, deleted: true } : { captures: [] } });
    };
    const history = new CaptureHistory(() => "cli", { appendLine() {} });
    try {
        vscode.window.showWarningMessage = async () => "Cancel";
        await history.delete({ capture: detail() });
        assert.equal(calls.length, 0);
        vscode.window.showWarningMessage = async () => "Delete Capture";
        await history.delete({ capture: detail() });
        assert.deepEqual(calls[0], ["captures", "delete", "--capture-id", A, "--acknowledge-risk", "high", "--json"]);
        assert.ok(calls[1].includes("list"));
    } finally { h.runHistoryCli = original; history.dispose(); }
});
test("save is a new explicit snapshot, revalidates the process and reopens the returned capture", async () => {
    const original = h.runHistoryCli, calls = [], opened = [];
    const processRow = { processId: 42, managedEntrypointAssemblyName: "App", runtimeVersion: "10",
        operatingSystem: "Linux", processArchitecture: "x64" };
    const process = h.parseProcessInventory(JSON.stringify({ data: [processRow] })).processes[0];
    h.runHistoryCli = async (_cli, args) => {
        calls.push(args);
        if (args[0] === "processes") return JSON.stringify({ data: [processRow] });
        if (args.includes("--persist")) return JSON.stringify({ capture: detail() });
        return JSON.stringify({ data: { captures: [] } });
    };
    const history = new CaptureHistory(() => "cli", { appendLine() {} });
    history.open = async row => opened.push(row.capture.captureId);
    try {
        vscode.window.showQuickPick = async () => ({ captureKind: "heap-gcdump" });
        vscode.window.showWarningMessage = async () => "Cancel";
        await history.save(process);
        assert.equal(calls.length, 0);
        vscode.window.showWarningMessage = async () => "Acknowledge and Save";
        await history.save(process);
        assert.equal(calls[0][0], "processes");
        assert.ok(calls[1].includes("--persist") && calls[1].includes("gcdump"));
        assert.deepEqual(opened, [A]);
    } finally { h.runHistoryCli = original; history.dispose(); }
});
test("switching selected processes cannot let delayed disposal clear the new panel", async () => {
    const extensionPath = path.join(__dirname, "../out/extension.js");
    const callbacks = [];
    const originalCreate = vscode.window.createWebviewPanel;
    vscode.workspace = {
        getConfiguration: () => ({ get: () => 120 }),
        onDidChangeConfiguration: () => ({ dispose() {} }),
    };
    const Controller = new Function("require", "exports", fs.readFileSync(extensionPath, "utf8")
        + "\nreturn CounterPanelController;")(createRequire(extensionPath), {});
    vscode.window.createWebviewPanel = () => ({
        webview: { onDidReceiveMessage() {}, html: "" }, reveal() {},
        dispose() {}, onDidDispose(cb) { callbacks.push(cb); },
    });
    const controller = Object.create(Controller.prototype);
    controller.output = { appendLine() {} };
    controller.stopCounters = async () => {};
    controller.disposeChild = async () => {};
    controller.waitForPanelReady = async () => {};
    try {
        await controller.selectProcess({ processId: 1, entrypoint: "A", runtimeVersion: "10", platform: "Linux/x64" });
        await controller.selectProcess({ processId: 2, entrypoint: "B", runtimeVersion: "10", platform: "Linux/x64" });
        const current = controller.panel;
        callbacks[0]();
        assert.equal(controller.selectedTarget.processId, 2);
        assert.equal(controller.panel, current);
    } finally { vscode.window.createWebviewPanel = originalCreate; }
});
