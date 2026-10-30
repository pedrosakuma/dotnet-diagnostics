const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const fs = require("node:fs");
const Module = require("node:module");
const resolve = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    return request === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js")
        : resolve.call(this, request, ...rest);
};
const { ACTION_AVAILABILITY_SCRIPT, PROFILE_ACTIONS_SCRIPT } = require("../out/actionAvailability.js");
const { renderHistoryHtml } = require("../out/captureHistoryPanel.js");
const { SingleFlightGuard } = require("../out/protocol.js");
require("./stubs/vscode-stub.js").EventEmitter = class { event() {} fire() {} dispose() {} };
const extensionPath = path.join(__dirname, "../out/extension.js");
const { Controller, Actions, renderHtml } = new Function("require", "exports",
    fs.readFileSync(extensionPath, "utf8")
    + "\nreturn { Controller: CounterPanelController, Actions: DiagnosticsActionsProvider, renderHtml: exports.renderHtml };")(
    Module.createRequire(extensionPath), {});

function dom() {
    const ids = new Map(), documentListeners = {};
    const document = {
        activeElement: null,
        addEventListener: (type, callback) => { documentListeners[type] = callback; },
        getElementById(id) {
            if (!ids.has(id)) { const n = element("div"); n.id = id; ids.set(id, n); }
            return ids.get(id);
        },
        createElement: tag => element(tag),
        querySelectorAll: selector => [...ids.values()].filter(n =>
            selector.includes("data-cpu-view") && n.id === "cpuQuery"
            || selector.includes("data-allocation-view") && n.id === "allocationQuery"),
    };
    function element(tag) {
        return {
            tag, children: [], attributes: {}, listeners: {}, value: "", disabled: false, hidden: false,
            textContent: "", title: "",
            get options() { return this.children; },
            get selectedOptions() { return this.children.filter(n => n.value === this.value); },
            appendChild(n) { this.children.push(n); return n; },
            replaceChildren(...nodes) { this.children = nodes; if (this.id === "view") this.value = ""; },
            querySelector(selector) { return this.children.find(n => n.tag === selector) ?? null; },
            querySelectorAll(selector) {
                return this.children.filter(n => selector === "[data-profile-retention-note]"
                    && n.attributes["data-profile-retention-note"] !== undefined);
            },
            setAttribute(name, value) { this.attributes[name] = String(value); },
            getAttribute(name) { return this.attributes[name]; },
            addEventListener(type, callback) { this.listeners[type] = callback; },
            contains(n) { return this.children.includes(n); },
            focus() { document.activeElement = this; },
            closest(selector) {
                return selector === '[aria-disabled="true"]' && this.attributes["aria-disabled"] === "true" ? this : null;
            },
            click() {
                let blocked = false;
                documentListeners.click?.({ target: this, preventDefault() {}, stopImmediatePropagation() { blocked = true; } });
                if (!blocked && !this.disabled) this.listeners.click?.();
            },
        };
    }
    return { document, get: id => document.getElementById(id) };
}

function profileUi() {
    const ui = dom();
    for (const id of ["exportJson", "exportCsv", "exportSummary", "exportTrace", "captureCpu", "captureCpuTrace",
        "captureAllocation", "cpuQuery", "allocationQuery", "cpuQueryViews", "allocationQueryViews", "allocationExplorer",
        "cpuEvidencePrerequisite", "allocationEvidencePrerequisite"]) ui.get(id);
    ui.get("allocationExplorer").children.push(ui.get("allocationQuery"));
    const update = new Function("document", `${ACTION_AVAILABILITY_SCRIPT}\n${PROFILE_ACTIONS_SCRIPT}\nreturn installProfileActions();`)(ui.document);
    update({});
    return { ...ui, update, available: id => ui.get(id).getAttribute("aria-disabled") === "false" };
}
const ready = { cpu: true, trace: true, allocation: true };
test("profile action matrix preserves host-held evidence independently of connection and capture phase", () => {
    const ui = profileUi();
    assert.match(ui.get("cpuEvidencePrerequisite").textContent, /No displayed CPU capture/);
    assert.equal(ui.available("captureCpu"), true);
    assert.equal(ui.available("captureCpuTrace"), true);
    assert.equal(ui.available("exportJson"), false);
    assert.equal(ui.available("exportSummary"), false);
    assert.equal(ui.get("allocationExplorer").hidden, true);
    ui.update({ type: "captureStatus", kind: "cpu", state: "running" });
    ui.update({ type: "captureStatus", kind: "cpu", state: "error" });
    assert.equal(ui.available("exportJson"), false, "an initial failed capture creates no evidence");
    ui.update({ type: "capture", kind: "cpu", profiles: ready });
    assert.equal(ui.available("exportJson"), true);
    assert.equal(ui.available("exportTrace"), true);
    ui.update({ type: "captureStatus", kind: "cpu", state: "done" });
    ui.get("exportJson").focus();
    for (const state of ["starting", "running"]) {
        ui.update({ type: "captureStatus", kind: "allocation", state });
        assert.equal(ui.available("exportJson"), true, "measurement export has no CLI dependency");
        assert.equal(ui.available("cpuQuery"), false);
        assert.equal(ui.get("cpuQueryViews").getAttribute("data-profile-query-available"), "false");
        assert.equal(ui.get("cpuQueryViews").getAttribute("aria-disabled"), undefined, "local profile exploration remains available offline");
        assert.equal(ui.available("exportSummary"), false);
        assert.equal(ui.available("allocationQuery"), false);
        assert.equal(ui.document.activeElement, ui.get("exportJson"));
    }
    ui.update({ type: "captureStatus", kind: "allocation", state: "error" });
    assert.equal(ui.available("allocationQuery"), true, "a failed or busy/rejected attempt retains the previous handle");
    ui.update({ type: "cpuQueryState", state: "running" });
    assert.equal(ui.available("allocationQuery"), false);
    ui.update({ type: "cpuQueryState", state: "idle" });
    assert.equal(ui.available("allocationQuery"), true);
    ui.get("allocationQuery").focus();
    ui.update({ type: "profileAvailability", profiles: { cpu: false, trace: false, allocation: false } });
    assert.equal(ui.get("allocationExplorer").hidden, true);
    assert.equal(ui.document.activeElement, ui.get("captureAllocation"), "hidden explorer focus returns to its stable entry point");
    assert.equal(ui.available("cpuQuery"), false);
    assert.equal(ui.available("exportSummary"), false);
    assert.equal(ui.available("exportTrace"), false);
    assert.equal(ui.available("exportCsv"), true, "disconnect does not discard redacted measurements");
    assert.match(ui.get("cpuEvidencePrerequisite").textContent, /remain shareable offline/);
    assert.match(ui.get("cpuEvidencePrerequisite").textContent, /never collect again/);
    assert.match(ui.get("exportSummary").title, /never starts a capture/);
    ui.update({ type: "capture", kind: "cpu", profiles: { cpu: true, trace: false, allocation: false } });
    assert.equal(ui.available("exportSummary"), true);
    assert.equal(ui.available("exportTrace"), false, "ordinary CPU capture did not retain a trace");
    assert.match(ui.get("cpuEvidencePrerequisite").textContent, /No raw trace was retained/);
    assert.match(ui.get("exportTrace").title, /did not retain a raw trace/);
    ui.update({ type: "profileAvailability", profiles: ready });
    assert.match(ui.get("cpuEvidencePrerequisite").textContent, /reuse this capture's retained CLI evidence/);
    assert.match(ui.get("cpuEvidencePrerequisite").textContent, /never collect again/);
});
test("unavailable actions remain focusable but block click and keyboard-generated click invocation", () => {
    const ui = profileUi();
    let calls = 0;
    ui.get("exportJson").addEventListener("click", () => calls++);
    ui.get("exportJson").focus();
    ui.get("exportJson").click();
    assert.equal(calls, 0);
    assert.equal(ui.document.activeElement, ui.get("exportJson"));
    assert.match(ui.get("exportJson").title, /Capture new CPU samples/);
    ui.update({ type: "capture", kind: "cpu" });
    ui.get("exportJson").click();
    assert.equal(calls, 1);
});

test("disconnect updates cached projection provenance and disables raw exports without discarding measurement exports", () => {
    const ui = profileUi();
    const note = ui.document.createElement("p");
    note.setAttribute("data-profile-retention-note","");
    ui.get("cpuQueryViews").children.push(note);
    ui.update({type:"capture",kind:"cpu",profiles:ready});
    assert.match(note.textContent,/explicit query on this displayed capture's retained CLI connection/);
    ui.update({type:"profileAvailability",profiles:{cpu:false,trace:false,allocation:false}});
    assert.match(note.textContent,/Cached loaded projection only/);
    assert.match(note.textContent,/retained-evidence exports require a new capture/);
    assert.equal(ui.get("cpuQueryViews").getAttribute("data-profile-retained"),"false");
    assert.equal(ui.available("exportTrace"),false);
    assert.equal(ui.available("exportJson"),true);
});
test("CPU rendering separates new collection from displayed and retained-evidence exports", () => {
    const html = renderHtml({
        processId: 42, managedEntrypointAssemblyName: "Fixture", runtimeVersion: "10",
        operatingSystem: "Windows", processArchitecture: "x64",
    }, "nonce", 120);
    const captureGroup = html.indexOf('aria-label="New CPU capture"');
    const evidenceGroup = html.indexOf("<summary>Share or save displayed CPU evidence</summary>");
    assert.ok(captureGroup >= 0 && evidenceGroup > captureGroup);
    assert.ok(html.indexOf('id="captureCpuTrace"') < evidenceGroup,
        "trace retention belongs to new capture options, not displayed-evidence exports");
    assert.match(html, /Open redacted measurements \(JSON\)/);
    assert.match(html, /Open retained investigation measurements \(JSON\)/);
    assert.match(html, /Save retained raw trace locally…/);
    assert.match(html, /They never start a new capture/);
    assert.match(html, /cannot reopen the investigation/);
});
test("retained-evidence export failure never falls back to a new capture", async () => {
    const controller = Object.create(Controller.prototype);
    controller.exporting = false;
    controller.cpuExport = undefined;
    controller.postMessage = () => {};
    let captureCalls = 0;
    controller.captureCpuCore = async () => { captureCalls++; };
    const errors = [];
    const originalError = require("./stubs/vscode-stub.js").window.showErrorMessage;
    require("./stubs/vscode-stub.js").window.showErrorMessage = async message => errors.push(message);
    try {
        await controller.exportCpu("summary");
        assert.equal(captureCalls, 0);
        assert.match(errors[0], /CPU export failed/);
        assert.match(errors[0], /Capture CPU with an updated CLI first/);
    } finally {
        require("./stubs/vscode-stub.js").window.showErrorMessage = originalError;
    }
});
test("saved view actions follow loaded selection, reject stale responses and recover after failure", () => {
    const ui = dom(), messages = [];
    const artifactId = "b".repeat(32);
    const detail = { name: "Saved", captureId: "a".repeat(32), createdUtc: "", artifacts: [
        { artifactId, kind: "heap-snapshot", supportedViews: ["top-types"] },
        { artifactId: "unsupported", kind: "other", supportedViews: ["records"] },
    ] };
    ui.get("artifact").value = artifactId;
    const html = renderHistoryHtml("nonce", "csp", detail);
    let receive;
    new Function("document", "window", "acquireVsCodeApi", html.match(/<script nonce="nonce">([\s\S]*?)<\/script>/)[1])(
        ui.document, { addEventListener: (_type, cb) => { receive = cb; } },
        () => ({ postMessage: message => messages.push(message) }));
    const available = id => ui.get(id).getAttribute("aria-disabled") === "false";
    const respond = (request, ok) => receive({ data: { ...request, type: "result", ok, summary: "Saved types",
        message: "Saved evidence not found", data: { topTypes: [] } } });
    const first = messages.at(-1);
    assert.equal(available("exportJson"), false);
    assert.equal(available("compareBaseline"), false);
    respond({ ...first, requestId: 999 }, true);
    assert.equal(available("exportJson"), false);
    respond(first, true);
    assert.equal(available("exportJson"), true);
    assert.equal(available("compareBaseline"), true);
    receive({ data: { ...first, type: "comparisonState", state: "running" } });
    assert.equal(available("compareBaseline"), false);
    assert.equal(available("exportJson"), true);
    receive({ data: { ...first, type: "comparisonState", state: "idle" } });
    assert.equal(available("compareBaseline"), true);
    ui.get("view").listeners.change();
    const second = messages.at(-1);
    assert.equal(available("exportCsv"), false);
    respond(first, true);
    assert.equal(available("exportCsv"), false);
    respond(second, false);
    assert.equal(available("exportCsv"), false);
    assert.equal(available("compareBaseline"), false);
    ui.get("view").listeners.change();
    respond(messages.at(-1), true);
    assert.equal(available("exportCsv"), true);
    ui.get("artifact").value = "unsupported";
    ui.get("artifact").listeners.change();
    assert.equal(available("compareBaseline"), false);
    assert.equal(available("exportJson"), false);
    assert.match(ui.get("status").textContent, /No displayable/);
});
test("a stale allocation handle invalidates only that profile; rejected queries preserve valid evidence", async () => {
    const controller = Object.create(Controller.prototype);
    const session = { closing: false, process: { exitCode: null, signalCode: null } };
    const cpu = controller.cpuExport = { session, handle: "cpu" };
    const allocation = controller.allocationProfile = { session, handle: "allocation" };
    controller.cpuQueryGuard = new SingleFlightGuard();
    controller.output = { appendLine() {} };
    const messages = [];
    controller.postMessage = message => messages.push(message);
    controller.sendQuery = async () => { throw Object.assign(new Error("Busy"), { code: "invalid_query" }); };
    await controller.queryCpu("call-tree", undefined, "allocation");
    assert.equal(controller.allocationProfile, allocation);
    controller.sendQuery = async () => { throw Object.assign(new Error("Expired"), { code: "unknown_handle" }); };
    await controller.queryCpu("call-tree", undefined, "allocation");
    assert.equal(controller.allocationProfile, undefined);
    assert.equal(controller.cpuExport, cpu);
    assert.ok(messages.some(m => m.type === "queryError" && m.message === "Expired"));
});
test("profile query identity rejects stale requests and suppresses late results after owner replacement", async () => {
    const controller = Object.create(Controller.prototype);
    const session = { closing: false, process: { exitCode: null, signalCode: null } };
    controller.cpuExport = { session, handle: "first", evidenceId: "first" };
    controller.cpuQueryGuard = new SingleFlightGuard();
    controller.output = { appendLine() {} };
    const messages = [];
    controller.postMessage = message => messages.push(message);
    let sendCount = 0;
    controller.sendQuery = async () => {
        sendCount++;
        return { data: { totalSamples: 4 } };
    };

    await controller.queryCpu("call-tree", undefined, "cpu", "stale");
    assert.equal(sendCount, 0, "a stale evidence identity must not reach the retained-handle query");
    assert.ok(messages.some(message => message.type === "queryError"
        && message.evidenceId === "stale" && /replaced/.test(message.message)));

    messages.length = 0;
    let started;
    const queryStarted = new Promise(resolve => { started = resolve; });
    let finishQuery;
    controller.sendQuery = async () => {
        sendCount++;
        started();
        return await new Promise(resolve => { finishQuery = resolve; });
    };
    const pending = controller.queryCpu("call-tree", undefined, "cpu", "first");
    await queryStarted;
    controller.cpuExport = { session, handle: "second", evidenceId: "second" };
    finishQuery({ data: { totalSamples: 9 } });
    await pending;
    assert.ok(messages.some(message => message.type === "queryError"
        && message.evidenceId === "first" && /replaced or disconnected/.test(message.message)));
    assert.equal(messages.some(message => message.type === "query" && message.evidenceId === "first"), false);
    assert.equal(controller.cpuExport.evidenceId, "second");
});
test("heap preloaded views stop publishing when their retained snapshot is replaced", async () => {
    const controller = Object.create(Controller.prototype);
    const session = {};
    controller.heapSnapshot = { session, handle: "first", evidenceId: "first" };
    const messages = [];
    controller.postMessage = message => messages.push(message);
    controller.output = { appendLine() {} };
    let queries = 0;
    controller.sendQuery = async () => {
        queries++;
        controller.heapSnapshot = { session, handle: "second", evidenceId: "second" };
        return { data: {} };
    };

    await controller.runHeapQueries(session, "first", {
        includeStaticFields: false,
        includeDelegateTargets: false,
        includeRetentionPaths: false,
        includeRetainedExceptions: false,
    }, "first");

    assert.equal(queries, 1);
    assert.equal(messages.length, 0, "a late query for the replaced snapshot must not reach the webview");
});
test("sidebar Stop Live Counters is discoverable but has no command without an active session", () => {
    const vscode = require("./stubs/vscode-stub.js");
    vscode.EventEmitter = class { event() {} fire() {} dispose() {} };
    const provider = new Actions();
    const stop = () => {
        const monitoring = provider.getChildren().find(item => item.label === "Monitoring");
        assert.equal(monitoring.command, undefined);
        return provider.getChildren(monitoring).find(item => item.label === "Stop Live Counters");
    };
    assert.equal(stop().command, undefined);
    assert.match(stop().tooltip, /Start Live Counters/);
    provider.setLiveCountersActive(true);
    assert.equal(stop().command.command, "dotnetDiagnostics.stopCounters");
    provider.setLiveCountersActive(false);
    assert.equal(stop().command, undefined);
    provider.dispose();
});
test("symptom entry is optional, target-aware navigation beside the unchanged expert groups", () => {
    const provider = new Actions();
    let roots = provider.getChildren();
    const symptom = roots[0];
    assert.equal(symptom.label, "What are you investigating?");
    assert.equal(symptom.description, "Optional guide");
    assert.deepEqual(provider.getChildren(symptom).slice(0, 6).map(item => item.label), [
        "High CPU",
        "Memory growth or GC pressure",
        "Slow requests",
        "Locks, blocking or waits",
        "Exceptions or crashes",
        "General runtime health",
    ]);
    assert.equal(provider.getChildren(symptom).at(-1).label, "All diagnostic actions");
    provider.setActiveTarget({
        processId: 4242,
        managedEntrypointAssemblyName: "Example.Api",
        runtimeVersion: "10.0.0",
        operatingSystem: "Windows",
        processArchitecture: "X64",
    });
    roots = provider.getChildren();
    assert.equal(roots[0].description, "Example.Api · PID 4242");
    provider.setSelectedSymptom("slow-requests");
    roots = provider.getChildren();
    assert.equal(roots[0].description, "Slow requests");
    assert.match(roots[0].tooltip, /never starts collection/);
    assert.deepEqual(roots.slice(1, 7).map(item => item.label),
        ["Monitoring", "CPU", "Memory & GC", "Threads & waits", "Requests & networking", "Exceptions"]);
});
test("CPU startup failure retains the previous connection-bound capture as well as its measurements", async () => {
    const controller = Object.create(Controller.prototype);
    const previous = controller.cpuExport = { session: {}, handle: "previous" };
    controller.cpuQueryGuard = new SingleFlightGuard();
    controller.selectedTarget = { processId: 4242 };
    controller.acquireChild = async () => { throw new Error("Connection is busy"); };
    const messages = [];
    controller.postMessage = message => messages.push(message);
    await controller.captureCpuCore();
    assert.equal(controller.cpuExport, previous);
    assert.deepEqual(messages.map(m => m.state), ["running", "error"]);
});
