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
vscode.env = { clipboard: { async writeText() {} } };
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
    assert.match(html, /type: "selectionChanged"/);
    assert.match(html, /artifactId: artifact.value, requestId: current\?\.requestId/);
    const script = html.match(/<script nonce="nonce">([\s\S]*?)<\/script>/)[1];
    assert.doesNotThrow(() => new Function(script));
});
test("baseline comparison follows only the currently loaded artifact and ignores changed selections", async () => {
    const baseline = require("../out/baselineCompare.js");
    const originalRun = h.runHistoryCli, originalCreate = vscode.window.createWebviewPanel;
    const originalCompare = baseline.compareSaved, errors = [], messages = [];
    let receive, finish, entered;
    const started = new Promise(resolve => { entered = resolve; });
    h.runHistoryCli = async (_cli, args) => JSON.stringify(args[0] === "query"
        ? { summary: "Loaded", data: { topTypes: [] } } : { capture: detail() });
    vscode.window.showErrorMessage = async message => errors.push(message);
    vscode.window.createWebviewPanel = () => ({
        webview: {
            onDidReceiveMessage(callback) { receive = callback; return { dispose() {} }; },
            postMessage: async message => messages.push(message), cspSource: "csp",
        },
        onDidDispose() {}, dispose() {},
    });
    baseline.compareSaved = async () => {
        entered();
        return new Promise(resolve => { finish = resolve; });
    };
    const history = new CaptureHistory(() => "cli", { appendLine() {} });
    try {
        await history.open({ capture: detail() });
        await receive({ type: "query", artifactId: B, view: "top-types", requestId: 1 });
        await receive({ type: "compareBaseline", artifactId: "c".repeat(32), requestId: 1 });
        await receive({ type: "selectionChanged" });
        await receive({ type: "compareBaseline", artifactId: B, requestId: 1 });
        assert.equal(errors.length, 2);
        await receive({ type: "query", artifactId: B, view: "top-types", requestId: 2 });
        const pending = receive({ type: "compareBaseline", artifactId: B, requestId: 2 });
        await started;
        await receive({ type: "selectionChanged" });
        finish({ status: "qualified", metrics: [], reasons: [] });
        await pending;
        assert.ok(!messages.some(message => message.type === "comparison"));
        baseline.compareSaved = async () => ({ status: "qualified", metrics: [], reasons: [] });
        await receive({ type: "query", artifactId: B, view: "top-types", requestId: 3 });
        await receive({ type: "compareBaseline", artifactId: B, requestId: 3 });
        const comparison = messages.find(message => message.type === "comparison" && message.requestId === 3);
        assert.equal(comparison.artifactId, B);
        assert.equal(messages.at(-1).type, "comparisonState");
        assert.equal(messages.at(-1).state, "idle");
    } finally {
        h.runHistoryCli = originalRun;
        vscode.window.createWebviewPanel = originalCreate;
        baseline.compareSaved = originalCompare;
        history.dispose();
    }
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
test("saved address controls copy exact strings and report invalid or failed copies", async () => {
    const originalRun = h.runHistoryCli, originalCreate = vscode.window.createWebviewPanel;
    const originalWrite = vscode.env.clipboard.writeText, copies = [], errors = [];
    let receive;
    h.runHistoryCli = async () => JSON.stringify({ capture: detail() });
    vscode.window.createWebviewPanel = () => ({
        webview: { onDidReceiveMessage(callback) { receive = callback; return { dispose() {} }; }, cspSource: "csp" },
        onDidDispose() {}, dispose() {},
    });
    vscode.env.clipboard.writeText = async address => copies.push(address);
    vscode.window.showErrorMessage = async message => errors.push(message);
    const history = new CaptureHistory(() => "cli", { appendLine() {} });
    try {
        await history.open({ capture: detail() });
        await receive({ type: "copyAddress", address: "0xFEDCBA9876543210" });
        assert.deepEqual(copies, ["0xfedcba9876543210"]);
        for (const address of [9007199254740992, "123", "0x0", "0x12345678901234567"])
            await receive({ type: "copyAddress", address });
        assert.equal(copies.length, 1);
        assert.equal(errors.length, 4);
        vscode.env.clipboard.writeText = async () => { throw new Error("clipboard unavailable"); };
        await receive({ type: "copyAddress", address: "0xff" });
        assert.match(errors.at(-1), /clipboard unavailable/);
    } finally {
        h.runHistoryCli = originalRun;
        vscode.window.createWebviewPanel = originalCreate;
        vscode.env.clipboard.writeText = originalWrite;
        history.dispose();
    }
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
        let choices, pickerOptions, warning;
        vscode.window.showQuickPick = async (items, options) => {
            choices = items;
            pickerOptions = options;
            return { captureKind: "heap-gcdump" };
        };
        vscode.window.showWarningMessage = async () => "Cancel";
        await history.save(process);
        assert.equal(calls.length, 0);
        assert.deepEqual(choices.map(choice => choice.label), [
            "New persistent live heap snapshot",
            "New persistent GC dump snapshot",
            "New persistent thread snapshot",
        ]);
        assert.match(pickerOptions.placeHolder, /never converts the displayed result or a sharing export/);
        vscode.window.showWarningMessage = async message => {
            warning = message;
            return "Acknowledge and Capture";
        };
        await history.save(process);
        assert.match(warning, /collects again/);
        assert.match(warning, /does not convert any displayed result/);
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
        webview: { onDidReceiveMessage() {}, postMessage: async () => true, html: "" }, reveal() {},
        dispose() {}, onDidDispose(cb) { callbacks.push(cb); },
    });
    const controller = Object.create(Controller.prototype);
    controller.output = { appendLine() {} };
    controller.exporter = { clear() {} };
    controller.stopCounters = async () => {};
    controller.disposeChild = async () => {};
    controller.waitForPanelReady = async () => {};
    try {
        await controller.selectProcess({ processId: 1, entrypoint: "A", runtimeVersion: "10", platform: "Linux/x64", identity: "A|10|Linux/x64" });
        await controller.selectProcess({ processId: 2, entrypoint: "B", runtimeVersion: "10", platform: "Linux/x64", identity: "B|10|Linux/x64" });
        const current = controller.panel;
        callbacks[0]();
        assert.equal(controller.selectedTarget.processId, 2);
        assert.equal(controller.panel, current);
        const reused = { processId: 2, entrypoint: "C", runtimeVersion: "10", platform: "Linux/x64", identity: "C|10|Linux/x64" };
        controller.activeChild = { pendingCaptures: new Map([["old", {}]]), pendingQueries: new Map() };
        await assert.rejects(controller.selectProcess(reused), /Wait for the current capture/);
        assert.equal(controller.selectedTarget.managedEntrypointAssemblyName, "B");
        controller.activeChild = undefined;
        await controller.selectProcess(reused);
        assert.notEqual(controller.panel, current);
        assert.equal(controller.selectedTarget.managedEntrypointAssemblyName, "C");
    } finally { vscode.window.createWebviewPanel = originalCreate; }
});
test("Live Events startup is cancelled when its target changes during provider setup", async () => {
    const extensionPath = path.join(__dirname, "../out/extension.js");
    const Controller = new Function("require", "exports", fs.readFileSync(extensionPath, "utf8")
        + "\nreturn CounterPanelController;")(createRequire(extensionPath), {});
    const originalInput = vscode.window.showInputBox;
    const originalWarning = vscode.window.showWarningMessage;
    const controller = Object.create(Controller.prototype);
    const originalTarget = { processId: 1 };
    controller.disposed = false;
    controller.eventsGeneration = 0;
    controller.selectedTarget = originalTarget;
    const messages = [];
    controller.postEventsMessage = message => messages.push(message);
    let connectCalls = 0;
    controller.connectEventsChild = async () => { connectCalls++; throw new Error("must not connect"); };
    let promptCount = 0;
    vscode.window.showInputBox = async () => {
        promptCount++;
        if (promptCount === 2) {
            controller.selectedTarget = { processId: 2 };
            await controller.stopEvents();
        }
        return ["System.Runtime", "4", "0"][promptCount - 1];
    };
    vscode.window.showWarningMessage = async () => "Acknowledge and Start";
    try {
        await controller.startEventsCore();
        assert.equal(promptCount, 2);
        assert.equal(connectCalls, 0);
        assert.equal(controller.eventsGeneration, 1);
        assert.ok(messages.some(message => message.state === "stopped"));
    } finally {
        vscode.window.showInputBox = originalInput;
        vscode.window.showWarningMessage = originalWarning;
    }
});
test("disposing the Events panel clears batches that can no longer be acknowledged", () => {
    const extensionPath = path.join(__dirname, "../out/extension.js");
    const Controller = new Function("require", "exports", fs.readFileSync(extensionPath, "utf8")
        + "\nreturn CounterPanelController;")(createRequire(extensionPath), {});
    const { LiveEventDeliveryQueue } = require("../out/liveEvents.js");
    const controller = Object.create(Controller.prototype);
    const queue = new LiveEventDeliveryQueue();
    queue.enqueue({
        sessionId: "session-1", sequence: 1, timestamp: "2026-01-01T00:00:00Z",
        receivedAt: "2026-01-01T00:00:00Z", provider: "System.Runtime", eventName: "Event",
        eventId: 1, version: 0, level: "Informational", threadId: null, activityId: null,
        relatedActivityId: null, payload: {}, metadataTruncated: false, payloadTruncated: false,
        omittedPayloadFields: 0, omittedPayloadCharacters: 0, unsupportedPayloadValues: 0,
    });
    queue.takeBatch();
    controller.eventsBatchTimer = setTimeout(() => {}, 60_000);
    controller.eventsQueue = queue;
    controller.eventsStreamSessionId = "session-1";
    controller.eventsQuality = { coreQueueDropped: 0 };
    controller.resetEventsDeliveryState();

    assert.equal(controller.eventsQueue.retainedEventCount, 0);
    assert.equal(controller.eventsQueue.hasInFlightBatch, false);
    assert.equal(controller.eventsStreamSessionId, undefined);
    assert.equal(controller.eventsQuality, undefined);
});
test("blocked Events webview keeps stdout draining, rejects newest offers and coalesces quality until ACK", async () => {
    const extensionPath = path.join(__dirname, "../out/extension.js");
    const Controller = new Function("require", "exports", fs.readFileSync(extensionPath, "utf8")
        + "\nreturn CounterPanelController;")(createRequire(extensionPath), {});
    const { BoundedProtocolLineReader, LiveEventDeliveryQueue } = require("../out/liveEvents.js");
    const { PassThrough } = require("node:stream");
    for (const capacity of [32, 64]) {
        const controller = Object.create(Controller.prototype);
        const input = new PassThrough();
        const lines = new BoundedProtocolLineReader(input, 65_536);
        let resolveTerminal;
        const terminal = { promise: new Promise(resolve => { resolveTerminal = resolve; }), resolve: () => resolveTerminal() };
        const child = { sessionId: "current", eventsConnection: true, lines, terminal };
        controller.eventsQueue = new LiveEventDeliveryQueue(capacity);
        controller.eventsChild = child;
        controller.eventsStreamSessionId = "current";
        controller.eventsQualityRevision = 0;
        controller.eventsQualityAcknowledgedRevision = 0;
        controller.eventsLastQualityAt = 0;
        controller.eventsGeneration = 0;
        const posted = [];
        controller.panel = { webview: { postMessage: message => { posted.push(message); return Promise.resolve(true); } } };
        controller.scheduleEventsBatch = () => {};
        controller.postEventsMessage = message => posted.push(message);
        lines.on("line", (line, bytes) => controller.handleFrame(child, line, bytes));
        const frame = sequence => JSON.stringify({
            type: "observation", kind: "event_source", sessionId: "current", sequence,
            timestamp: "2026-01-01T00:00:00Z", receivedAt: "2026-01-01T00:00:00Z",
            provider: "System.Runtime", eventName: "Event", eventId: 1, version: 0,
            level: "Informational", threadId: null, activityId: null, relatedActivityId: null,
            payload: {}, metadataTruncated: false, payloadTruncated: false,
            omittedPayloadFields: 0, omittedPayloadCharacters: 0, unsupportedPayloadValues: 0,
        }) + "\n";
        const quality = count => JSON.stringify({
            type: "quality", kind: "event_source", sessionId: "current",
            eventPipeEventsLost: null, eventPipeLossAvailable: false,
            coreQueueDropped: count, cliDispatchQueueDropped: count * 2,
        }) + "\n";
        const assertDrained = () => {
            assert.equal(input.isPaused(), false);
            assert.equal(input.readableLength, 0);
            assert.equal(lines.deferredText, "");
            assert.equal(lines.pendingBytes, 0);
            assert.equal(lines.pending, "");
        };
        try {
            for (let sequence = 1; sequence <= capacity; sequence++) input.write(frame(sequence));
            controller.sendEventsBatch();
            const batch = posted.find(message => message.type === "eventsBatch");
            assert.equal(batch.events.length, capacity);
            const heldBytes = controller.eventsQueue.retainedByteCount;
            controller.sendEventsBatch();
            assert.equal(posted.filter(message => message.type === "eventsBatch").length, 1, "one batch held in flight");
            for (let start = capacity + 1; start <= 10_000; start += 100) {
                let chunk = "";
                for (let sequence = start; sequence < Math.min(start + 100, 10_001); sequence++) {
                    chunk += frame(sequence);
                    if (sequence % 10 === 0) chunk += quality(sequence);
                }
                input.write(chunk);
                assertDrained();
                assert.equal(controller.eventsQueue.retainedEventCount, capacity);
                assert.equal(controller.eventsQueue.retainedByteCount, heldBytes);
            }
            assert.equal(controller.eventsQueue.retainedEventCount, capacity);
            assert.equal(controller.eventsQueue.dropped, 10_000 - capacity, "actual parsed reader offers rejected at insertion");
            assert.equal(controller.eventsQuality.coreQueueDropped, 10_000);
            assert.equal(controller.eventsQuality.cliDispatchQueueDropped, 20_000);
            assert.deepEqual(batch.events.map(event => event.sequence), Array.from({ length: capacity }, (_, i) => i + 1));
            controller.acknowledgeEventBatch("stale-session", batch.batchId);
            controller.acknowledgeEventBatch("current", batch.batchId + 1);
            assert.equal(controller.eventsQueue.retainedEventCount, capacity);
            controller.acknowledgeEventBatch("current", batch.batchId);
            assert.equal(controller.eventsQueue.retainedEventCount, 0);
            const drops = controller.eventsQueue.dropped;
            input.write(frame(10_001));
            assert.equal(controller.eventsQueue.retainedEventCount, 1);
            controller.sendEventsBatch();
            const recovered = posted.filter(message => message.type === "eventsBatch").at(-1);
            assert.equal(recovered.events[0].sequence, 10_001, "recovery admits new input, not a deferred old chunk");
            assert.deepEqual(recovered.quality, {
                eventPipeEventsLost: null, eventPipeLossAvailable: false,
                coreQueueDropped: 10_000, cliDispatchQueueDropped: 20_000, hostDeliveryDropped: drops,
            });
            const qualityRevision = controller.eventsInFlightQualityRevision;
            input.write(quality(10_001));
            controller.acknowledgeEventBatch("current", batch.batchId);
            assert.equal(controller.eventsQueue.hasInFlightBatch, true, "old ACK cannot release recovery batch");
            controller.acknowledgeEventBatch("current", recovered.batchId);
            assert.equal(controller.eventsQualityAcknowledgedRevision, qualityRevision);
            assert.ok(controller.eventsQualityRevision > qualityRevision, "newer quality remains dirty after ACK of older snapshot");
            controller.eventsLastQualityAt = 0;
            controller.sendEventsBatch();
            const qualityOnly = posted.filter(message => message.type === "eventsBatch").at(-1);
            assert.equal(qualityOnly.events.length, 0);
            assert.equal(qualityOnly.quality.coreQueueDropped, 10_001);
            controller.acknowledgeEventBatch("current", qualityOnly.batchId);
            assert.equal(controller.eventsQualityAcknowledgedRevision, controller.eventsQualityRevision);

            // Stop must consume the terminal frame even while a new full batch has no ACK.
            for (let sequence = 10_002; sequence < 10_002 + capacity; sequence++) input.write(frame(sequence));
            controller.sendEventsBatch();
            const held = posted.filter(message => message.type === "eventsBatch").at(-1);
            assert.equal(controller.eventsQueue.retainedEventCount, capacity);
            controller.writeFrame = (session, request) => {
                assert.equal(session, child);
                assert.equal(request.type, "cancel");
                input.write(quality(10_002));
                input.write(JSON.stringify({ type: "terminal", sessionId: "current", status: "stopped" }) + "\n");
            };
            let closed = false;
            controller.closeChild = async session => { assert.equal(session, child); closed = true; };
            await controller.stopEvents();
            assert.equal(closed, true);
            assert.equal(child.terminalReceived, true);
            assert.equal(controller.eventsQualityFinal, true);
            assert.equal(controller.eventsChild, undefined);
            assert.equal(controller.eventsQueue.hasInFlightBatch, true, "terminal does not depend on a webview ACK");
            assertDrained();
            controller.acknowledgeEventBatch("current", held.batchId);
            controller.eventsLastQualityAt = 0;
            controller.sendEventsBatch();
            const finalQuality = posted.filter(message => message.type === "eventsBatch").at(-1);
            assert.equal(finalQuality.events.length, 0);
            assert.equal(finalQuality.quality.coreQueueDropped, 10_002);
            assert.equal(finalQuality.quality.hostDeliveryDropped, drops);
            controller.acknowledgeEventBatch("current", finalQuality.batchId);
            assert.equal(controller.eventsQueue.retainedEventCount, 0);
            assert.equal(controller.eventsQueue.retainedByteCount, 0);
        } finally {
            lines.close();
            input.destroy();
        }
    }
});

test("Events still validates rejected offers and bounded lines while the webview is blocked", () => {
    const extensionPath = path.join(__dirname, "../out/extension.js");
    const Controller = new Function("require", "exports", fs.readFileSync(extensionPath, "utf8")
        + "\nreturn CounterPanelController;")(createRequire(extensionPath), {});
    const { BoundedProtocolLineReader, LiveEventDeliveryQueue } = require("../out/liveEvents.js");
    const { PassThrough } = require("node:stream");
    const controller = Object.create(Controller.prototype);
    const input = new PassThrough();
    const lines = new BoundedProtocolLineReader(input, 65_536);
    const child = { sessionId: "current", eventsConnection: true, lines };
    controller.eventsQueue = new LiveEventDeliveryQueue(1);
    controller.eventsQueue.enqueue({ sequence: 1 }, 100);
    controller.eventsQueue.takeBatch();
    const errors = [];
    controller.failEventsProtocol = (_child, message) => errors.push(message);
    lines.on("line", (line, bytes) => controller.handleFrame(child, line, bytes));
    let oversized = 0;
    lines.on("oversized", () => oversized++);
    try {
        input.write(JSON.stringify({
            type: "observation", kind: "event_source", sessionId: "current", sequence: 2,
            timestamp: "2026-01-01T00:00:00Z",
        }) + "\n");
        assert.match(errors[0], /unsupported or unbounded schema/);
        assert.equal(controller.eventsQueue.dropped, 0, "invalid frames are protocol errors, not host offer drops");
        for (let i = 0; i < 100; i++) input.write("x".repeat(4096));
        assert.equal(lines.pendingBytes, 0);
        assert.equal(lines.pending, "");
        input.write("\n");
        assert.equal(oversized, 1);
        assert.equal(lines.deferredText, "");
        assert.equal(input.readableLength, 0);
        assert.equal(input.isPaused(), false);
        assert.equal(controller.eventsQueue.retainedEventCount, 1);
    } finally { lines.close(); input.destroy(); }
});

test("Events request uses32 while independent counters and GC retain256 defaults", () => {
    const source = fs.readFileSync(path.join(__dirname, "../src/extension.ts"), "utf8");
    assert.match(source, /observationCapacity: LIVE_EVENT_OBSERVATION_CAPACITY/);
    assert.match(source, /kind: "counters", providers: \["System.Runtime"\], intervalSeconds: 1, observationCapacity: 256/);
    assert.match(source, /kind: "gc", observationCapacity: 256/);
});
test("continuation pages preserve earlier captures and ordinary refresh replaces them", async () => {
    const original = h.runHistoryCli;
    h.runHistoryCli = async (_cli, args) => JSON.stringify({ data: args.includes("--after-capture-id")
        ? { captures: [{ ...detail(), captureId: "c".repeat(32) }], nextAfterCaptureId: B }
        : { captures: [detail()], nextAfterCaptureId: A } });
    const history = new CaptureHistory(() => "cli", { appendLine() {} });
    try {
        await history.refresh();
        await history.refresh(A);
        let rows = await history.getChildren();
        assert.deepEqual(rows.filter(r => r.capture).map(r => r.capture.captureId), [A, "c".repeat(32)]);
        await history.refresh();
        assert.equal((await history.getChildren()).filter(r => r.capture).length, 1);
        history.rows = Array.from({ length: 500 }, () => ({ capture: detail() }));
        await history.refresh(B);
        rows = await history.getChildren();
        assert.equal(rows.filter(r => r.capture).length, 500);
        assert.ok(rows.some(r => /first 500/.test(r.message)));
        assert.ok(!rows.some(r => r.more));
    } finally { h.runHistoryCli = original; history.dispose(); }
});
