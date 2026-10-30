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
    controller.targetGeneration = 1;
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
    assert.equal(manifest.contributes.commands.find(c => c.command === "dotnetDiagnostics.saveSnapshot").title,
        ".NET Diagnostics: Capture New Persistent Snapshot");
});
test("native capture groups expose every existing flow without attaching on expansion", () => {
    const provider = new Actions();
    const roots = provider.getChildren();
    const groups = roots.filter(item => item.collapsibleState !== vscode.TreeItemCollapsibleState.None);
    assert.deepEqual(groups.map(item => item.label),
        ["What are you investigating?", "Monitoring", "CPU", "Memory & GC", "Threads & waits", "Requests & networking", "Exceptions"]);
    const leaves = groups.flatMap(group => {
        assert.equal(group.command, undefined, "expansion is not a diagnostic command");
        assert.equal(typeof group.id, "string");
        return provider.getChildren(group);
    });
    const generic = leaves.filter(item => item.command?.command === "dotnetDiagnostics.captureDiagnostics"
        && item.command.arguments?.length);
    assert.deepEqual(generic.map(item => item.command.arguments[0]).sort(),
        kinds.CAPTURE_KIND_SPECS.map(spec => spec.kind).sort());
    assert.equal(new Set(generic.map(item => item.command.arguments[0])).size, 10);
    for (const suffix of ["captureCpu", "captureCpuTrace", "captureHeap", "captureThreadSnapshot",
        "captureExceptions", "startCounters", "startEvents"])
        assert.equal(leaves.filter(item => item.command?.command === "dotnetDiagnostics." + suffix).length, 1, suffix);
    assert.equal(leaves.filter(item => item.command?.command === "dotnetDiagnostics.investigateSymptom").length, 6);
    assert.equal(leaves.find(item => item.label === "Stop Live Counters").command, undefined);
    assert.ok(leaves.some(item => item.label === "Capture new CPU samples"));
    assert.ok(leaves.some(item => item.label === "Capture new CPU samples + retain raw trace"));
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
test("ordinary capture prompts disclose shared replacement before dispatch and preserve cancellation", async () => {
    for (const kind of ["requests", "kestrel", "db", "networking"]) {
        const { controller, frames } = fixture();
        let prompted = false;
        vscode.window.showInputBox = async options => {
            assert.equal(frames.length, 0);
            assert.match(options.prompt, /Success replaces the shared generic result; not saved/);
            prompted = true;
            return undefined;
        };
        await controller.captureDiagnostics(kind);
        assert.equal(prompted, true);
        assert.equal(frames.length, 0);
    }
});
test("Workspace Capture dispatches only the selected ordinary kind through the existing host route", () => {
    const html = extensionRequire(extensionPath).renderHtml({
        processId: 42, managedEntrypointAssemblyName: "Synthetic", runtimeVersion: "10",
        operatingSystem: "Windows", processArchitecture: "X64",
    }, "nonce", 120);
    const click = /document.getElementById\('captureRequestEvidence'\).addEventListener\('click', \(\) => \{([\s\S]*?)\n    \}\);/.exec(html);
    assert.ok(click);
    assert.match(html, /Replaces the current result · May contain sensitive data/);
    const messages = [];
    const run = new Function("selectedWorkspaceEvidence", "vscode", click[1]);
    for (const kind of ["requests", "kestrel", "db", "networking", "cpu", "request-activities"])
        run(kind, { postMessage: message => messages.push(message) });
    assert.deepEqual(messages, ["requests", "kestrel", "db", "networking"].map(kind => ({ type: "captureDiagnostics", kind })));
    const source = fs.readFileSync(extensionPath, "utf8");
    const start = source.indexOf('else if (message.type === "captureDiagnostics")');
    const end = source.indexOf('else if (message.type === "captureRequestActivities")', start);
    assert.ok(start >= 0 && end > start);
    const route = source.slice(start + "else ".length, end).trim();
    const calls = [], errors = [];
    const host = { captureDiagnostics: kind => calls.push(kind), postMessage: message => errors.push(message) };
    const handle = new Function("message", "captureKinds_1", route);
    for (const kind of ["requests", "kestrel", "db", "networking", undefined, "unsupported", null])
        handle.call(host, { type: "captureDiagnostics", kind }, kinds);
    assert.deepEqual(calls, ["requests", "kestrel", "db", "networking", undefined]);
    assert.equal(errors.length, 2);
});
test("grouped off-CPU entry still requires consent before connecting", async () => {
    const { controller, frames } = fixture(kinds.CAPTURE_KIND_SPECS[1]);
    vscode.window.showQuickPick = () => { throw new Error("leaf already selected a kind"); };
    vscode.window.showWarningMessage = async () => "Cancel";
    controller.acquireChild = () => { throw new Error("must not connect without consent"); };
    await controller.captureDiagnostics("off_cpu");
    assert.equal(frames.length, 0);
});
test("diagnostic setup rejects target changes at picker, parameters, consent and connection boundaries", async () => {
    for (const boundary of ["picker", "parameters", "consent", "connection"]) {
        const spec = kinds.CAPTURE_KIND_SPECS.find(item => item.kind === "off_cpu");
        const { controller, frames, messages, session } = fixture(spec);
        const changeTarget = () => {
            controller.selectedTarget = { processId: 5252 };
            controller.targetGeneration++;
        };
        let acquired = false, closed = 0;
        vscode.window.showQuickPick = async () => {
            if (boundary === "picker") changeTarget();
            return { spec };
        };
        vscode.window.showInputBox = async options => {
            if (boundary === "parameters") changeTarget();
            return options.value ?? "";
        };
        vscode.window.showWarningMessage = async (_message, options) => {
            if (options?.modal && boundary === "consent") changeTarget();
            return "Acknowledge and Capture";
        };
        controller.acquireChild = async () => {
            acquired = true;
            if (boundary === "connection") changeTarget();
            return { session, owns: true };
        };
        controller.closeChild = async () => { closed++; };
        await controller.captureDiagnostics();
        assert.deepEqual(frames, [], boundary + " must not dispatch to the old PID");
        assert.equal(acquired, boundary === "connection");
        assert.equal(closed, boundary === "connection" ? 1 : 0);
        if (boundary === "connection") {
            assert.equal(messages.at(-1).state, "error", "starting status must settle to an error");
            assert.equal(session.expectedShutdown, true);
        }
    }
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

test("successful CPU captures retain an explicit bounded baseline/current pair only when advertised", () => {
    const makeSession = capability => ({
        pendingCaptures: new Map(),
        pendingQueries: new Map(),
        startInFlight: false,
        closing: false,
        cpuHandleComparison: capability,
        process: { exitCode: null, signalCode: null },
    });
    const summary = (startedAt, samples) => ({
        processId: 4242,
        startedAt,
        duration: "00:00:10",
        totalSamples: samples,
        topHotspots: [],
    });
    const complete = (controller, session, evidenceId, handle, result) => {
        let resolved;
        session.pendingCaptures.set(evidenceId, {
            kind: "cpu",
            evidenceId,
            deferred: { resolve(value) { resolved = value; }, reject() {} },
        });
        controller.handleFrame(session, JSON.stringify({
            type: "capture",
            requestId: evidenceId,
            kind: "cpu",
            exportHandle: handle,
            result,
        }));
        return resolved;
    };
    const capability = {
        version: 1,
        queryView: "diff",
        baselineHandleField: "baselineHandle",
        retainedHandleLimit: 2,
        maxTopMovers: 50,
    };
    const controller = Object.create(Controller.prototype);
    controller.output = { appendLine() {} };
    const session = makeSession(capability);
    complete(controller, session, "cpu-1", "handle-1", summary("2026-10-26T12:00:00Z", 100));
    assert.equal(controller.cpuBaseline, undefined);
    complete(controller, session, "cpu-2", "handle-2", summary("2026-10-26T12:01:00Z", 120));
    assert.equal(controller.cpuBaseline.handle, "handle-1");
    assert.equal(controller.cpuExport.handle, "handle-2");
    complete(controller, session, "cpu-3", "handle-3", summary("2026-10-26T12:02:00Z", 110));
    assert.equal(controller.cpuBaseline.handle, "handle-2", "the prior current becomes the only retained baseline");
    assert.equal(controller.cpuExport.handle, "handle-3");

    const legacy = Object.create(Controller.prototype);
    legacy.output = { appendLine() {} };
    const legacySession = makeSession(undefined);
    complete(legacy, legacySession, "legacy-1", "legacy-h1", summary("2026-10-26T12:00:00Z", 100));
    complete(legacy, legacySession, "legacy-2", "legacy-h2", summary("2026-10-26T12:01:00Z", 100));
    assert.equal(legacy.cpuBaseline, undefined);
    assert.equal(legacy.cpuExport.handle, "legacy-h2");
});
test("live thread snapshot capture and eager queries share one evidence identity", async () => {
    const controller = Object.create(Controller.prototype);
    const messages = [], frames = [];
    const target = { processId: 4242, managedEntrypointAssemblyName: "Sample" };
    const session = {
        pendingCaptures: new Map(), pendingQueries: new Map(), startInFlight: false, closing: false,
        process: { exitCode: null, signalCode: null },
    };
    controller.selectedTarget = target;
    controller.targetGeneration = 1;
    controller.disposed = false;
    controller.output = { appendLine() {} };
    controller.postMessage = message => messages.push(message);
    controller.acquireChild = async () => ({ session, owns: false });
    controller.sendQuery = async (_session, handle, viewName) => ({ handle, view: viewName });
    controller.writeFrame = (_session, frame) => {
        frames.push(frame);
        controller.handleFrame(session, JSON.stringify({
            type: "capture", requestId: frame.requestId, kind: frame.kind,
            result: { summary: "snapshot", data: {
                processId: 4242, origin: "live", capturedAt: "now", walkDuration: "00:00:01",
                threads: [], locks: [], handle: "thread-handle",
            } },
        }));
    };
    let confirmation;
    vscode.window.showWarningMessage = async message => {
        confirmation = message;
        return "Acknowledge and Capture";
    };

    await controller.captureThreadSnapshotCore();

    const capture = messages.find(message => message.type === "capture" && message.kind === "thread-snapshot");
    assert.match(confirmation, /Sample \(PID 4242\)/);
    assert.match(confirmation, /platform-specific privileged ClrMD attach/);
    assert.match(confirmation, /cannot be stopped from this extension/);
    assert.match(confirmation, /frontend limit/);
    assert.doesNotMatch(confirmation, /ptrace/);
    assert.ok(capture.evidenceId);
    assert.equal(capture.retainedHandleAvailable, true);
    assert.equal(frames[0].requestId, capture.evidenceId);
    assert.deepEqual(messages.filter(message => message.type === "captureStatus").map(message => message.evidenceId),
        [capture.evidenceId, capture.evidenceId, capture.evidenceId]);
    assert.equal(messages.find(message => message.state === "running").retainedEvidenceInvalidated, true);
    const queries = messages.filter(message => message.type === "query" && message.kind === "thread-snapshot");
    assert.deepEqual(queries.map(message => message.view), ["deadlocks", "unique-stacks", "wait-chains", "threadpool"]);
    assert.ok(queries.every(message => message.evidenceId === capture.evidenceId));
    assert.equal(controller.threadSnapshot.evidenceId, capture.evidenceId);
    const lastQueryIndex = messages.findLastIndex(message => message.type === "query" && message.kind === "thread-snapshot");
    const doneStatusIndex = messages.findIndex(message => message.type === "captureStatus" && message.state === "done");
    assert.ok(doneStatusIndex > lastQueryIndex, "capture status remains active until eager snapshot views finish");
    assert.equal(session.pendingCaptures.size, 0);
});

test("heap confirmation names its target, source and selected enrichments without dispatch on cancellation", async () => {
    const controller = Object.create(Controller.prototype);
    const target = { processId: 4242, managedEntrypointAssemblyName: "Sample" };
    const frames = [];
    controller.selectedTarget = target;
    controller.targetGeneration = 1;
    controller.disposed = false;
    controller.output = { appendLine() {} };
    controller.postMessage = () => {};
    controller.acquireChild = () => { throw new Error("cancellation must precede connection acquisition"); };
    controller.writeFrame = (_session, frame) => frames.push(frame);
    let picker = 0;
    vscode.window.showQuickPick = async items => {
        picker++;
        return picker === 1 ? items[0] : [items[0], items[2]];
    };
    let confirmation;
    vscode.window.showWarningMessage = async message => {
        confirmation = message;
        return "Cancel";
    };

    await controller.captureHeapCore();

    assert.match(confirmation, /Sample \(PID 4242\)/);
    assert.match(confirmation, /Source: Live \(ClrMD\)/);
    assert.match(confirmation, /Static fields, Retention paths/);
    assert.match(confirmation, /platform-specific privileged ClrMD attach/);
    assert.match(confirmation, /cannot be stopped from this extension/);
    assert.match(confirmation, /frontend limit/);
    assert.doesNotMatch(confirmation, /ptrace/);
    assert.deepEqual(frames, []);
});

test("heap and thread confirmation do not dispatch when the selected target changes", async () => {
    const target = { processId: 4242, managedEntrypointAssemblyName: "Sample" };
    for (const kind of ["heap", "thread-snapshot"]) {
        const controller = Object.create(Controller.prototype);
        const frames = [], messages = [];
        controller.selectedTarget = target;
        controller.targetGeneration = 1;
        controller.disposed = false;
        controller.output = { appendLine() {} };
        controller.postMessage = message => messages.push(message);
        controller.acquireChild = () => { throw new Error("stale confirmation must not acquire a CLI"); };
        controller.writeFrame = (_session, frame) => frames.push(frame);
        if (kind === "heap") {
            let picker = 0;
            vscode.window.showQuickPick = async items => {
                picker++;
                return picker === 1 ? items[0] : [];
            };
        }
        vscode.window.showWarningMessage = async () => {
            controller.selectedTarget = { processId: 5252, managedEntrypointAssemblyName: "Other" };
            return "Acknowledge and Capture";
        };

        if (kind === "heap") await controller.captureHeapCore();
        else await controller.captureThreadSnapshotCore();

        assert.deepEqual(frames, [], kind + " must not dispatch against the previously confirmed target");
        assert.equal(messages.some(message => message.type === "capture"), false);
    }
});

test("heap and thread captures revalidate target ownership after asynchronous CLI acquisition", async () => {
    const target = { processId: 4242, managedEntrypointAssemblyName: "Sample" };
    for (const kind of ["heap", "thread-snapshot"]) {
        const controller = Object.create(Controller.prototype);
        const frames = [], messages = [];
        const session = { pendingCaptures: new Map(), pendingQueries: new Map(), startInFlight: false };
        let closed = 0;
        controller.selectedTarget = target;
        controller.targetGeneration = 1;
        controller.disposed = false;
        controller.output = { appendLine() {} };
        controller.postMessage = message => messages.push(message);
        controller.acquireChild = async () => {
            controller.selectedTarget = { processId: 5252, managedEntrypointAssemblyName: "Other" };
            return { session, owns: true };
        };
        controller.closeChild = async () => { closed++; };
        controller.writeFrame = (_session, frame) => frames.push(frame);
        if (kind === "heap") {
            let picker = 0;
            vscode.window.showQuickPick = async items => {
                picker++;
                return picker === 1 ? items[0] : [];
            };
        }
        vscode.window.showWarningMessage = async () => "Acknowledge and Capture";

        if (kind === "heap") await controller.captureHeapCore();
        else await controller.captureThreadSnapshotCore();

        assert.deepEqual(frames, [], kind + " must revalidate ownership before writing a capture frame");
        assert.equal(closed, 1, kind + " must close its newly acquired idle CLI after the target changes");
        assert.ok(messages.some(message => message.state === "error"));
    }
});
test("late thread snapshot query results cannot cross evidence replacement", async () => {
    const controller = Object.create(Controller.prototype);
    const messages = [];
    const session = { closing: false, process: { exitCode: null, signalCode: null } };
    controller.output = { appendLine() {} };
    controller.postMessage = message => messages.push(message);
    controller.threadSnapshot = { session, handle: "thread-a", evidenceId: "thread-a" };
    let release;
    controller.sendQuery = () => new Promise(resolve => { release = resolve; });

    const preload = controller.runThreadQueries(session, "thread-a", "thread-a");
    await Promise.resolve();
    controller.threadSnapshot = { session, handle: "thread-b", evidenceId: "thread-b" };
    release({ view: "deadlocks" });
    await preload;
    assert.deepEqual(messages, [], "a late automatic query must not update the replacement snapshot");

    controller.threadSnapshot = { session, handle: "thread-a", evidenceId: "thread-a" };
    const statics = controller.queryThreadStaticsCore("Example.Cache", "thread-a");
    await Promise.resolve();
    controller.threadSnapshot = { session, handle: "thread-b", evidenceId: "thread-b" };
    release({ view: "thread-statics" });
    await statics;
    assert.deepEqual(messages, [], "a late explicit statics query must not update another snapshot");
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
        const { controller, messages, frames, session } = fixture(undefined, state);
        const previous = controller.allocationProfile = { session, handle: "previous" };
        await controller.captureDiagnostics();
        assert.equal(frames.length, 0);
        assert.equal(messages.at(-1).state, "error");
        assert.ok(!messages.some(m => m.state === "running"));
        assert.equal(controller.allocationProfile, previous);
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
    assert.equal(controller.allocationProfile, undefined);
});
test("failed allocation capture preserves the previous profile; successful table-only replacement invalidates it", async () => {
    const { controller, session, messages } = fixture();
    const previous = controller.allocationProfile = { session, handle: "previous" };
    controller.writeFrame = (_session, frame) => controller.handleFrame(session, JSON.stringify({
        type: "error", requestId: frame.requestId, code: "capture_safety_rejected", message: "Capture rejected",
    }));
    await controller.captureAllocations();
    assert.equal(controller.allocationProfile, previous);
    assert.equal(messages.at(-1).state, "error");
    controller.writeFrame = (_session, frame) => controller.handleFrame(session, JSON.stringify({
        type: "capture", requestId: frame.requestId, kind: "allocation", result: view("allocation"),
    }));
    await controller.captureAllocations();
    assert.equal(controller.allocationProfile, undefined);
    assert.ok(messages.some(m => m.type === "queryError" && /only allocation tables/.test(m.message)));
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
    test("ordinary quality has one compact active alert before data and full scope after data", () => {
        const container = node("div");
        const render = new Function("document", `${CAPTURE_VIEW_RENDERER_SCRIPT}\nreturn renderCaptureView;`)({
            getElementById: () => container, createElement: node,
        });
        for (const kind of ["requests", "kestrel", "db", "networking"]) {
            const result = view(kind);
            result.evidence = { source: "collector", conclusions: {
                retainedPositive: "available", absenceOrExhaustive: "withheld", healthyControl: "withheld",
            }, limitations: [{ category: "loss", scope: "window", affectedCount: 7, detail: "Seven events lost" }] };
            render("views", result);
            const output = text(container);
            assert.ok(output.indexOf("Seven events lost") < output.indexOf("Metrics"));
            assert.ok(output.indexOf("cap-note") < output.indexOf("Metrics"));
            assert.ok(output.indexOf("loss: 7 · exhaustive conclusions withheld") < output.indexOf("Metrics"));
            assert.ok(output.indexOf("Absence/exhaustive counts: withheld") > output.indexOf("Sites"));
            assert.equal(container.children.filter(n => n.className === "diagnostic-active-quality").length, 1);
            assert.ok(output.indexOf("Search, sorting and row details") > output.indexOf("Sites"));
        }
    });
    for (const spec of kinds.CAPTURE_KIND_SPECS) {
        render("views", view(spec.kind));
        assert.match(text(container), /P95\nUnavailable/);
        assert.match(text(container), /<script>App.Lock<\/script>/);
        assert.match(text(container), /4 retained row\(s\) omitted/);
        assert.match(text(container), /Evidence quality: not reported/);
        assert.match(text(container), /cap-note/);
        assert.ok(container.children.some(n => n.className === "diagnostic-table-view"
            && n.children.some(child => child.className === "query-view-table-wrap")));
    }
});
