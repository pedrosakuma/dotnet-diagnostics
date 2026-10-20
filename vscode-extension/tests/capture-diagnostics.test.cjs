const assert = require("node:assert/strict");
const test = require("node:test");
const fs = require("node:fs");
const path = require("node:path");
const { createRequire } = require("node:module");
const Module = require("node:module");
const kinds = require("../out/captureKinds.js");
const { SingleFlightGuard } = require("../out/protocol.js");
const { CAPTURE_VIEW_RENDERER_SCRIPT } = require("../out/captureViewRenderer.js");
const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    return request === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js")
        : originalResolveFilename.call(this, request, ...rest);
};
const extensionPath = path.join(__dirname, "..", "out", "extension.js");
const extensionRequire = createRequire(extensionPath);
const vscode = require("./stubs/vscode-stub.js");
const Controller = new Function("require", "exports",
    fs.readFileSync(extensionPath, "utf8") + "\nreturn CounterPanelController;")(extensionRequire, {});
const Actions = new Function("require", "exports",
    fs.readFileSync(extensionPath, "utf8") + "\nreturn DiagnosticsActionsProvider;")(extensionRequire, {});

function view(kind = "contention") {
    return {
        kind, title: kind, summary: "Observed sample",
        provenance: { origin: "live-stream", durable: false, processId: 4242, startedAt: null,
            reportedDurationSeconds: null, requestedDurationSeconds: 10, runtime: null, runtimeVersion: null },
        metrics: [{ label: "Count", value: 3 }, { label: "P95", value: null }],
        tables: [{ id: "sites", title: "Sites", columns: ["Site", "Count"],
            rows: [["<script>App.Lock</script>", 3]], totalRows: 5, omittedRows: 4, emptyText: "No observations" }],
        notes: ["cap-note"], evidence: { source: "not-reported", limitations: [] },
    };
}
function fixture(spec = kinds.CAPTURE_KIND_SPECS[0], overrides = {}) {
    const messages = [], frames = [];
    const session = { pendingCaptures: new Map(), pendingQueries: new Map(), startInFlight: false, ...overrides };
    const controller = Object.create(Controller.prototype);
    controller.selectedTarget = { processId: 4242 };
    controller.output = { appendLine() {} };
    controller.pendingExports = new Map();
    controller.cpuQueryGuard = new SingleFlightGuard();
    controller.acquireChild = async () => ({ session, owns: false });
    controller.postMessage = message => messages.push(message);
    controller.writeFrame = (_session, frame) => {
        frames.push(frame);
        controller.handleFrame(session, JSON.stringify({
            type: "capture", requestId: frame.requestId, kind: frame.kind, result: view(frame.kind),
        }));
    };
    vscode.window.showQuickPick = async () => ({ spec });
    vscode.window.showInputBox = async options => options.value ?? "";
    vscode.window.showWarningMessage = async () => "Acknowledge and Capture";
    return { controller, messages, frames, session };
}
test("one command exposes exactly the ten scoped kinds", () => {
    assert.deepEqual(kinds.CAPTURE_KIND_SPECS.map(s => s.kind),
        ["allocation", "off_cpu", "contention", "threadpool", "gc", "gc-activities", "requests", "kestrel", "db", "networking"]);
    assert.deepEqual(kinds.CAPTURE_KIND_SPECS.filter(s => s.risk === "high").map(s => s.kind), ["off_cpu"]);
    const manifest = require("../package.json");
    assert.equal(manifest.contributes.commands.filter(c => c.command === "dotnetDiagnostics.captureDiagnostics").length, 1);
});
test("native capture groups expose every existing flow without attaching on expansion", () => {
    const provider = new Actions();
    const roots = provider.getChildren();
    const groups = roots.filter(item => item.collapsibleState !== vscode.TreeItemCollapsibleState.None);
    assert.deepEqual(groups.map(item => item.label),
        ["Monitoring", "CPU", "Memory & GC", "Threads & waits", "Requests & networking", "Exceptions"]);
    const leaves = groups.flatMap(group => {
        assert.equal(group.command, undefined, "expansion is not a diagnostic command");
        assert.equal(typeof group.id, "string");
        return provider.getChildren(group);
    });
    const generic = leaves.filter(item => item.command.command === "dotnetDiagnostics.captureDiagnostics");
    assert.deepEqual(generic.map(item => item.command.arguments[0]).sort(),
        kinds.CAPTURE_KIND_SPECS.map(spec => spec.kind).sort());
    assert.equal(new Set(generic.map(item => item.command.arguments[0])).size, 10);
    for (const suffix of ["captureCpu", "captureCpuTrace", "captureHeap", "captureThreadSnapshot",
        "captureExceptions", "startCounters", "stopCounters", "startEvents"])
        assert.equal(leaves.filter(item => item.command.command === "dotnetDiagnostics." + suffix).length, 1, suffix);
    for (const suffix of ["captureDiagnostics", "analyzeDumpFile"])
        assert.equal(roots.filter(item => item.command?.command === "dotnetDiagnostics." + suffix).length, 1, suffix);
    assert.deepEqual(provider.getChildren(leaves[0]), []);
    assert.deepEqual(provider.getChildren().filter(item => item.id).map(item => item.id), groups.map(item => item.id));
});
test("expert diagnostic picker has matching native headings, searchable descriptions and all ten kinds", async () => {
    const { controller, frames } = fixture();
    let choices;
    vscode.window.showQuickPick = async (items, options) => {
        choices = items;
        assert.equal(options.matchOnDescription, true);
        return undefined;
    };
    await controller.captureDiagnostics();
    assert.deepEqual(choices.filter(item => item.kind === vscode.QuickPickItemKind.Separator).map(item => item.label),
        ["Memory & GC", "Threads & waits", "Requests & networking"]);
    assert.deepEqual(choices.filter(item => item.spec).map(item => item.spec.kind).sort(),
        kinds.CAPTURE_KIND_SPECS.map(spec => spec.kind).sort());
    assert.equal(frames.length, 0, "cancelling navigation never collects");
});
test("each grouped leaf skips only the kind picker and uses the canonical capture flow", async () => {
    for (const spec of kinds.CAPTURE_KIND_SPECS) {
        const { controller, frames } = fixture(spec);
        vscode.window.showQuickPick = () => { throw new Error("leaf already selected a kind"); };
        await controller.captureDiagnostics(spec.kind);
        assert.equal(frames.length, 1, spec.kind);
        assert.equal(frames[0].kind, spec.kind);
        assert.equal(frames[0].acknowledgeRisk, spec.risk === "high" ? "high" : undefined);
    }
});
test("grouped off-CPU entry still requires consent before connecting", async () => {
    const { controller, frames } = fixture(kinds.CAPTURE_KIND_SPECS[1]);
    vscode.window.showQuickPick = () => { throw new Error("leaf already selected a kind"); };
    vscode.window.showWarningMessage = async () => "Cancel";
    controller.acquireChild = () => { throw new Error("must not connect without consent"); };
    await controller.captureDiagnostics("off_cpu");
    assert.equal(frames.length, 0);
});
test("parameters reject invalid input and forward only applicable options", () => {
    for (const invalid of ["0", "301", "1.5", "-1", "abc", ""])
        assert.ok(kinds.validateInteger(invalid, 1, 300, "Duration"));
    assert.equal(kinds.validateInteger("6", 1, 300, "Duration"), undefined);
    assert.ok(kinds.validateSymbolPath("a\nb"));
    assert.ok(kinds.validateSymbolPath("x".repeat(1025)));
    assert.equal(kinds.validateSymbolPath(""), undefined);
    assert.deepEqual(kinds.parseSources("A,A,B*"), { sources: ["A", "B*"] });
    assert.ok(kinds.parseSources(Array.from({ length: 21 }, (_, i) => `s${i}`).join(",")).error);
    const params = { durationSeconds: 6, topN: 3, symbolPath: "/sym", sources: ["A"], thresholdMs: 100 };
    const off = kinds.buildCaptureRequest("r", kinds.CAPTURE_KIND_SPECS[1], 42, params);
    assert.equal(off.acknowledgeRisk, "high");
    assert.equal(off.symbolPath, "/sym");
    assert.equal(off.sources, undefined);
    assert.equal(off.thresholdMs, undefined);
    assert.equal(kinds.captureTimeoutMs("gc-activities", 10), 100000);
});
test("result guard rejects ragged rows, unknown quality, mismatched totals and invalid provenance", () => {
    assert.equal(kinds.isCaptureViewResult(view()), true);
    for (const change of [
        v => v.tables[0].rows.push(["ragged"]),
        v => v.tables[0].totalRows = 99,
        v => v.evidence.conclusions = {},
        v => v.provenance.durable = true,
        v => v.provenance.startedAt = {},
        v => v.notes = Array(51).fill("n"),
        v => v.metrics[0].value = {},
        v => v.handle = 42,
        v => v.handle = "",
        v => v.handle = "invalid\nhandle",
    ]) {
        const result = view(); change(result);
        assert.equal(kinds.isCaptureViewResult(result), false);
    }
});
test("each kind completes through the compiled controller and actual frame validator", async () => {
    for (const spec of kinds.CAPTURE_KIND_SPECS) {
        const { controller, messages, frames, session } = fixture(spec);
        await controller.captureDiagnostics();
        assert.equal(frames.length, 1, spec.kind);
        assert.equal(frames[0].kind, spec.kind);
        assert.equal(frames[0].acknowledgeRisk, spec.risk === "high" ? "high" : undefined);
        assert.equal(messages[0].state, "starting");
        assert.equal(messages.find(m => m.type === "capture").result.kind, spec.kind);
        assert.equal(messages.at(-1).state, "done");
        assert.equal(session.pendingCaptures.size, 0);
    }
});
test("malformed JSONL frames report an error in the active Events view", () => {
    const controller = Object.create(Controller.prototype);
    const messages = [], eventsMessages = [];
    let killed = false;
    controller.postMessage = message => messages.push(message);
    controller.postEventsMessage = message => eventsMessages.push(message);
    controller.failPending = () => {};
    controller.handleFrame({
        eventsConnection: true,
        startupErrorReported: false,
        process: { kill() { killed = true; } },
    }, "{malformed");
    assert.equal(eventsMessages.length, 1);
    assert.equal(eventsMessages[0].type, "eventsStatus");
    assert.equal(eventsMessages[0].state, "error");
    assert.equal(messages.length, 0);
    assert.equal(killed, true);
});
test("busy captures do not send queued reproduction guidance or discard previous evidence", async () => {
    for (const state of [
        { pendingCaptures: new Map([["cpu", {}]]) }, { pendingQueries: new Map([["q", {}]]) }, { startInFlight: true },
    ]) {
        const { controller, messages, frames } = fixture(undefined, state);
        await controller.captureDiagnostics();
        assert.equal(frames.length, 0);
        assert.equal(messages.at(-1).state, "error");
        assert.ok(!messages.some(m => m.state === "running"));
    }
});
test("a sibling capture cannot close the connection retaining a CPU export handle", async () => {
    for (const retained of ["none", "cpu", "allocation"]) {
        const { controller, session } = fixture(kinds.CAPTURE_KIND_SPECS[2]);
        let closed = 0;
        controller.acquireChild = async () => ({ session, owns: true });
        controller.closeChild = async () => { closed++; };
        if (retained === "cpu") controller.cpuExport = { session, handle: "cpu-handle" };
        if (retained === "allocation") controller.allocationProfile = { session, handle: "allocation-handle" };
        await controller.captureDiagnostics();
        assert.equal(closed, retained === "none" ? 1 : 0);
    }
});
test("allocation capture retains its exact connection and byte-weighted handle, then drills down without recapture", async () => {
    const { controller, messages, frames, session } = fixture();
    session.process = { exitCode: null, signalCode: null };
    let closed = 0;
    const queries = [];
    controller.acquireChild = async () => ({ session, owns: true });
    controller.closeChild = async () => { closed++; };
    controller.writeFrame = (_session, frame) => {
        frames.push(frame);
        controller.handleFrame(session, JSON.stringify({ type: "capture", requestId: frame.requestId,
            kind: "allocation", result: { ...view("allocation"), handle: "this-allocation" } }));
    };
    controller.sendQuery = async (connection, handle, queryView) => {
        assert.equal(connection, session);
        queries.push({ handle, view: queryView });
        return { weightUnit: "allocation-tick-estimated-bytes", data: {} };
    };
    vscode.window.showQuickPick = () => { throw new Error("dedicated action does not need a kind picker"); };
    await controller.captureAllocations();
    await controller.queryCpu("top-methods", undefined, "allocation");
    assert.equal(frames.length, 1);
    assert.equal(frames[0].kind, "allocation");
    assert.deepEqual(queries, [{ handle: "this-allocation", view: "call-tree" }, { handle: "this-allocation", view: "top-methods" }]);
    assert.equal(closed, 0);
    assert.equal(messages.filter(message => message.type === "query" && message.kind === "allocation").length, 2);
    controller.sendQuery = async () => ({ data: {} });
    await controller.queryCpu("call-tree", undefined, "allocation");
    assert.match(messages.findLast(message => message.type === "queryError").message, /did not confirm allocation-byte weighting/);
});
test("allocation capture preserves existing evidence when a profile query is busy", async () => {
    const { controller, frames } = fixture();
    controller.allocationProfile = { handle: "previous" };
    await controller.cpuQueryGuard.run(async () => {
        await controller.captureAllocations();
        assert.equal(controller.allocationProfile.handle, "previous");
        assert.equal(frames.length, 0);
    });
});
test("declining high risk does not connect or issue an acknowledgement", async () => {
    const { controller, frames, messages } = fixture(kinds.CAPTURE_KIND_SPECS[1]);
    vscode.window.showWarningMessage = async () => "Cancel";
    controller.acquireChild = () => { throw new Error("must not connect"); };
    await controller.captureDiagnostics();
    assert.deepEqual(frames, []);
    assert.deepEqual(messages, []);
});
test("CLI platform errors remain visible and are not treated as successful captures", async () => {
    const { controller, messages, session } = fixture(kinds.CAPTURE_KIND_SPECS[1]);
    controller.writeFrame = (_session, frame) => controller.handleFrame(session, JSON.stringify({
        type: "error", requestId: frame.requestId, code: "capture_failed", message: "perf is not available",
    }));
    await controller.captureDiagnostics();
    assert.equal(messages.at(-1).state, "error");
    assert.match(messages.at(-1).message, /perf is not available/);
    assert.ok(!messages.some(m => m.type === "capture"));
});
function node(tag) {
    return { tag, children: [], textContent: "", appendChild(child) { this.children.push(child); },
        replaceChildren() { this.children = []; }, querySelectorAll() { return []; },
        setAttribute() {}, addEventListener() {} };
}
const text = n => [n.textContent, ...n.children.map(text)].join("\n");
test("every kind uses structured text-only tables, quality and explicit unavailable cells", () => {
    const container = node("div");
    const render = new Function("document", `${CAPTURE_VIEW_RENDERER_SCRIPT}\nreturn renderCaptureView;`)({
        getElementById: () => container, createElement: node,
    });
    for (const spec of kinds.CAPTURE_KIND_SPECS) {
        render("views", view(spec.kind));
        assert.match(text(container), /P95\nUnavailable/);
        assert.match(text(container), /<script>App.Lock<\/script>/);
        assert.match(text(container), /4 retained row\(s\) omitted/);
        assert.match(text(container), /Evidence quality: not reported/);
        assert.match(text(container), /cap-note/);
        assert.ok(container.children.some(n => n.tag === "table"));
    }
});
