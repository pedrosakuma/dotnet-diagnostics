const assert = require("node:assert/strict");
const test = require("node:test");
const fs = require("node:fs");
const path = require("node:path");
const Module = require("node:module");
const { createRequire } = Module;
const { spawn } = require("node:child_process");
const { createInterface } = require("node:readline");
const joint = require("../out/requestActivities.js");
const { REQUEST_ACTIVITIES_SCRIPT } = require("../out/requestActivitiesView.js");
const resolve = Module._resolveFilename;
Module._resolveFilename = function (name, ...rest) {
    return name === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js") : resolve.call(this, name, ...rest);
};
const extensionPath = path.join(__dirname, "..", "out", "extension.js");
const source = fs.readFileSync(extensionPath, "utf8");
const Controller = new Function("require", "exports", source + "\nreturn CounterPanelController;")(createRequire(extensionPath), {});
const vscode = require("./stubs/vscode-stub.js");
const capability = { version: 1, captureKind: "request-activities", queryViews: ["requests", "related-activities"],
    maxRequests: 100, maxActivities: 200, maxQueryRows: 100 };
const now = "2026-10-22T12:00:00Z", end = "2026-10-22T12:00:10Z";
const trace = "a".repeat(32), span = "b".repeat(16);
function query(rows = []) {
    return { captureId: "capture-a", processId: 4242, processStartedAt: now, observationStart: now, observationEnd: end,
        quality: { completion: "normal", eventsLost: 0, lifetimeStatus: "known", parseErrors: 0,
            requestsStarted: 2, requestsCompleted: 0, droppedRequests: 0, activitiesObserved: 2, droppedActivities: 0,
            invalidIdentities: 0, duplicateIdentities: 0, identityCapacityReached: false, truncatedFields: 0,
            retainedIdentities: 4, accountedRetainedBytes: 2000 },
        retainedRequests: 2, retainedActivities: 2, totalRows: rows.length, omittedRows: 0, rows, notes: ["Stop-only evidence."] };
}
function capture() {
    const requests = query([
        { rowId: "r1", traceId: trace, spanId: span, method: "GET", path: "/same", startedAt: now, elapsedMs: 2000 },
        { rowId: "r2", traceId: trace, spanId: "c".repeat(16), method: "GET", path: "/same", startedAt: now, elapsedMs: 500 },
    ]);
    return { handle: "request-activities-capture-a", captureId: "capture-a", handleExpiresAt: new Date(Date.now() + 60_000).toISOString(),
        requestedStart: now, requestedDurationSeconds: 10, sources: ["MyApp"], requests };
}
function related() {
    return query([{ activity: { rowId: "a1", source: "MyApp", operation: "<script>DB</script>",
        traceId: trace, spanId: "d".repeat(16), parentSpanId: span, startedAt: now, stoppedAt: end, durationMs: 10 },
        relationship: "direct-child", verified: true, verificationWithheldReason: null }]);
}
function fixture() {
    const controller = Object.create(Controller.prototype);
    const frames = [], messages = [];
    const session = { pendingCaptures: new Map(), pendingQueries: new Map(), requestActivities: true,
        protocolReady: true, startInFlight: false, closing: false,
        process: { exitCode: null, signalCode: null, stdin: { writable: true, write: line => frames.push(JSON.parse(line)) } } };
    controller.selectedTarget = { processId: 4242 };
    controller.output = { appendLine() {} };
    controller.pendingExports = new Map();
    controller.acquireChild = async () => ({ session, owns: false });
    controller.postMessage = message => messages.push(message);
    controller.activeChild = session;
    controller.jointGeneration = 0;
    vscode.window.showInputBox = async options => options.value ?? "MyApp";
    vscode.window.showWarningMessage = async () => "Capture reproduction";
    vscode.window.showErrorMessage = async () => undefined;
    return { controller, session, frames, messages };
}
const tick = () => new Promise(resolve => setImmediate(resolve));
function retained(f) {
    const result = capture();
    f.controller.joint = { session: f.session, evidenceId: "e1", result, timer: setTimeout(() => {}, 60_000).unref() };
    return result;
}

test("joint capability requires actual compatible hello contract, not a CLI version", () => {
    assert.equal(joint.supportsRequestActivities(capability), true);
    for (const invalid of [undefined, null, {}, { cliVersion: "0.31.0" }, { ...capability, version: 2 },
        { ...capability, queryViews: ["requests"] }, { ...capability, maxActivities: 201 }])
        assert.equal(joint.supportsRequestActivities(invalid), false);
    const f = fixture();
    f.session.handshake = { resolve() {} };
    f.controller.handleFrame(f.session, JSON.stringify({ type: "hello", protocolVersion: 2, cliVersion: "0.31.0" }));
    assert.equal(f.session.requestActivities, false);
    f.controller.handleFrame(f.session, JSON.stringify({ type: "hello", protocolVersion: 2, requestActivities: capability }));
    assert.equal(f.session.requestActivities, true);
});
test("joint exact sources reject discovery, wildcard, Unicode and bounds", () => {
    assert.deepEqual(joint.parseJointSources("MyApp, System.Net.Http,MyApp"), ["MyApp", "System.Net.Http"]);
    for (const invalid of ["", "*", "MyApp.*", "A B", "é", "a\nb", "a".repeat(201), "A,", Array(21).fill("A").join(",")])
        assert.equal(joint.parseJointSources(invalid), undefined, invalid);
});
test("typed joint guards preserve structural/unverified distinction and enforce bounded shapes", () => {
    assert.equal(joint.isJointCapture(capture()), true);
    const r = related();
    assert.equal(joint.isJointRelatedQuery(r), true);
    r.rows[0].verified = false;
    r.rows[0].verificationWithheldReason = "Stream incomplete.";
    assert.equal(joint.isJointRelatedQuery(r), true);
    for (const mutate of [
        r => r.rows[0].verified = "true", r => r.rows[0].verificationWithheldReason = null,
        r => r.rows[0].activity.durationMs = -1, r => r.rows.push(r.rows[0]),
        r => r.rows[0].relationship = "caused-delay", r => r.quality.eventsLost = -1,
        r => r.totalRows = 201, r => r.notes = Array(21).fill("n"), r => r.observationStart = "bad",
        r => r.rows[0].activity.spanId = "0".repeat(16),
    ]) {
        const value = structuredClone(r); mutate(value);
        assert.equal(joint.isJointRelatedQuery(value), false);
    }
    const c = capture(); c.handle = "request-activities-other";
    assert.equal(joint.isJointCapture(c), false);
});
test("invalid configuration, cancelled notice and legacy CLI never dispatch or replace prior evidence", async () => {
    for (const mode of ["sources", "duration", "notice", "legacy", "busy", "live"]) {
        const f = fixture(); const old = retained(f);
        if (mode === "sources") vscode.window.showInputBox = async () => "*";
        if (mode === "duration") vscode.window.showInputBox = async options => options.value ? "301" : "MyApp";
        if (mode === "notice") vscode.window.showWarningMessage = async () => "Cancel";
        if (mode === "legacy") f.session.requestActivities = false;
        if (mode === "busy") f.session.pendingQueries.set("busy", {});
        if (mode === "live") f.session.sessionId = "live";
        await f.controller.captureRequestActivities();
        assert.equal(f.frames.length, 0, mode);
        assert.equal(f.controller.joint.result, old, mode);
        clearTimeout(f.controller.joint.timer);
    }
});
test("dispatch ACK replaces joint owner; terminal displays without eager queries or MCP", async () => {
    const f = fixture(); retained(f);
    const task = f.controller.captureRequestActivities();
    await tick();
    const frame = f.frames[0];
    assert.deepEqual(Object.keys(frame).sort(), ["durationSeconds", "kind", "processId", "requestId", "sources", "type"].sort());
    assert.equal(frame.kind, "request-activities");
    assert.ok(f.controller.joint, "write is not readiness or validation acknowledgement");
    f.controller.handleFrame(f.session, JSON.stringify({ type: "capture-started", requestId: frame.requestId,
        processId: 4242, kind: frame.kind }));
    assert.equal(f.controller.joint, undefined);
    f.controller.handleFrame(f.session, JSON.stringify({ type: "capture", requestId: frame.requestId,
        kind: frame.kind, result: capture() }));
    await task;
    assert.equal(f.frames.length, 1);
    assert.equal(f.messages.filter(m => m.type === "jointCapture").length, 1);
    assert.ok(f.controller.hasRetainedProfileFor(f.session));
    f.controller.invalidateJoint("test cleanup");
});
test("invalid CLI joint rejection leaves old artifact queryable and does not send spurious cancel", async () => {
    const f = fixture(); retained(f); const old = f.controller.joint;
    const task = f.controller.captureRequestActivities(); await tick();
    f.controller.handleFrame(f.session, JSON.stringify({ type: "error", code: "invalid_capture",
        requestId: f.frames[0].requestId, message: "Invalid source" }));
    await task;
    assert.equal(f.controller.joint, old);
    assert.equal(f.frames.length, 1);
    f.controller.invalidateJoint("cleanup");
});
test("capture terminal must match the exact PID, approved sources and duration", async () => {
    for (const mode of ["pid", "sources", "duration"]) {
        const f = fixture();
        const task = f.controller.captureRequestActivities(); await tick();
        const c = capture();
        if (mode === "pid") c.requests.processId++;
        if (mode === "sources") c.sources = ["MyApp", "UnapprovedSource"];
        if (mode === "duration") c.requestedDurationSeconds = 300;
        f.controller.handleFrame(f.session, JSON.stringify({ type: "capture-started", kind: "request-activities",
            requestId: f.frames[0].requestId, processId: 4242 }));
        f.controller.handleFrame(f.session, JSON.stringify({ type: "capture", requestId: f.frames[0].requestId,
            kind: "request-activities", result: c }));
        await task;
        assert.equal(f.controller.joint, undefined);
        assert.equal(f.messages.some(message => message.type === "jointCapture"), false, mode);
        assert.equal(f.messages.at(-1).state, "error");
    }
});
test("changing target during setup cannot dispatch against a stale selection", async () => {
    const f = fixture();
    vscode.window.showWarningMessage = async () => {
        f.controller.selectedTarget = { processId: 5000 };
        return "Capture reproduction";
    };
    await f.controller.captureRequestActivities();
    assert.deepEqual(f.frames, []);
    const second = fixture();
    second.controller.acquireChild = async () => {
        second.controller.selectedTarget = { processId: 5000 };
        return { session: second.session, owns: false };
    };
    await second.controller.captureRequestActivities();
    assert.deepEqual(second.frames, []);
    assert.match(second.messages.at(-1).message, /target changed/);
    const third = fixture();
    const running = third.controller.captureRequestActivities(); await tick();
    third.controller.selectedTarget = { processId: 5000 };
    third.controller.handleFrame(third.session, JSON.stringify({ type: "capture", requestId: third.frames[0].requestId,
        kind: "request-activities", result: capture() }));
    await running;
    assert.equal(third.controller.joint, undefined);
    assert.equal(third.messages.some(message => message.type === "jointCapture"), false);
});
test("cancelled terminal retains partial quality and expiry disables subsequent reads", async () => {
    const f = fixture();
    const task = f.controller.captureRequestActivities(); await tick();
    const c = capture();
    c.requests.quality.completion = "cancelled";
    c.requests.quality.eventsLost = null;
    c.handleExpiresAt = new Date(Date.now() + 80).toISOString();
    f.controller.handleFrame(f.session, JSON.stringify({ type: "capture-started", kind: "request-activities",
        requestId: f.frames[0].requestId, processId: 4242 }));
    f.controller.writeFrame(f.session, { type: "cancel", requestId: f.frames[0].requestId });
    f.controller.handleFrame(f.session, JSON.stringify({ type: "capture", requestId: f.frames[0].requestId,
        kind: "request-activities", result: c }));
    await task;
    assert.equal(f.messages.find(m => m.type === "jointCapture").result.requests.quality.completion, "cancelled");
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(f.controller.joint, undefined);
    assert.match(f.messages.at(-1).message, /Expired: retained display only/);
    await f.controller.queryRequestActivities(f.frames[0].requestId, "r1");
    assert.equal(f.frames.filter(frame => frame.type === "query").length, 0);
});
test("oversized joint frame fails explicitly before parsing and closes its owner", async () => {
    const f = fixture(); retained(f);
    const task = f.controller.queryRequestActivities("e1", "r1"); await tick();
    let killed = false;
    f.session.process.kill = () => { killed = true; };
    f.controller.handleFrame(f.session, "x".repeat(1024 * 1024 + 1));
    await task;
    assert.equal(killed, true);
    assert.equal(f.controller.joint, undefined);
    assert.match(f.messages.find(m => m.type === "jointUnavailable").message, /1 MiB/);
});
test("explicit related query binds exact row, capture and handle; never method/path/trace", async () => {
    const f = fixture(); const c = retained(f);
    const task = f.controller.queryRequestActivities("e1", "r2"); await tick();
    const frame = f.frames[0];
    assert.equal(frame.captureId, c.captureId);
    assert.equal(frame.handle, c.handle);
    assert.equal(frame.requestRowId, "r2");
    assert.equal(frame.view, "related-activities");
    assert.equal(frame.traceId, undefined);
    f.controller.handleFrame(f.session, JSON.stringify({ type: "query", requestId: frame.requestId,
        handle: c.handle, view: frame.view, result: related() }));
    await task;
    assert.equal(f.messages.at(-1).type, "jointRelated");
    assert.equal(f.messages.at(-1).requestRowId, "r2");
    f.controller.invalidateJoint("cleanup");
});
test("related query rejects mismatch, malformed data and unavailable selectors explicitly", async () => {
    for (const mode of ["pid", "window", "quality", "capture", "shape", "handle", "expired", "disconnected", "row"]) {
        const f = fixture(); const c = retained(f);
        if (mode === "expired") c.handleExpiresAt = now;
        if (mode === "disconnected") f.session.closing = true;
        const task = f.controller.queryRequestActivities("e1", mode === "row" ? "unknown" : "r1"); await tick();
        if (f.frames.length) {
            const response = related();
            if (mode === "pid") response.processId++;
            if (mode === "window") response.observationEnd = now;
            if (mode === "quality") response.quality.completion = "cancelled";
            if (mode === "capture") response.captureId = "other";
            if (mode === "shape") response.rows = ["invalid"];
            f.controller.handleFrame(f.session, JSON.stringify({ type: "query", requestId: f.frames[0].requestId,
                handle: mode === "handle" ? "wrong" : c.handle, view: "related-activities", result: response }));
        }
        await task;
        assert.equal(f.messages.some(m => m.type === "jointRelated"), false, mode);
        assert.ok(f.messages.some(m => m.type === "jointQueryError"), mode);
        f.controller.invalidateJoint("cleanup");
    }
});
test("stale response after replacement cannot publish into newer joint owner", async () => {
    const f = fixture(); const c = retained(f);
    const task = f.controller.queryRequestActivities("e1", "r1"); await tick();
    f.controller.invalidateJoint("replaced");
    f.controller.handleFrame(f.session, JSON.stringify({ type: "query", requestId: f.frames[0].requestId,
        handle: c.handle, view: "related-activities", result: related() }));
    await task;
    assert.equal(f.messages.some(m => m.type === "jointRelated"), false);
});
test("every ordinary capture path invalidates joint reads at write; active query blocks conflicting capture", () => {
    for (const kind of ["cpu", "heap", "thread-snapshot", "exceptions", "crash-guard", "allocation",
        "off_cpu", "contention", "threadpool", "gc", "gc-activities", "requests", "kestrel", "db", "networking"]) {
        const f = fixture(); retained(f);
        f.controller.writeFrame(f.session, { type: "capture", kind, requestId: "new" });
        assert.equal(f.controller.joint, undefined, kind);
        assert.match(f.messages.at(-1).message, /Replaced/);
    }
    const f = fixture(); retained(f); f.session.pendingJointQueries = new Map([["q", {}]]);
    assert.throws(() => f.controller.writeFrame(f.session, { type: "capture", kind: "cpu" }), /Wait/);
    assert.ok(f.controller.joint);
    f.controller.invalidateJoint("cleanup");
});
test("running joint capture rejects competing capture and same-CLI live start but preserves independent Events", () => {
    const f = fixture();
    f.session.pendingCaptures.set("running", { kind: "request-activities" });
    for (const kind of ["cpu", "heap", "thread-snapshot", "exceptions", "allocation", "requests"])
        assert.throws(() => f.controller.writeFrame(f.session, { type: "capture", kind }), /reproduction capture to finish/);
    assert.throws(() => f.controller.writeFrame(f.session, { type: "start" }), /reproduction capture to finish/);
    assert.deepEqual(f.frames, []);
    f.controller.writeFrame({ ...f.session, eventsConnection: true, pendingCaptures: new Map() }, { type: "start" });
    assert.deepEqual(f.frames, [{ type: "start" }]);
});
test("disconnect/drain generation rejects a late joint terminal; explicit cancel still permits labelled partial evidence", async () => {
    const f = fixture();
    const task = f.controller.captureRequestActivities(); await tick();
    f.session.closed = { promise: Promise.resolve() };
    f.session.lines = { close() {} };
    f.session.process.stdin.end = () => f.controller.handleFrame(f.session, JSON.stringify({
        type: "capture", requestId: f.frames[0].requestId, kind: "request-activities", result: capture(),
    }));
    await f.controller.closeChild(f.session);
    await task;
    assert.equal(f.controller.joint, undefined);
    assert.equal(f.messages.some(message => message.type === "jointCapture"), false);
    assert.match(f.messages.find(message => message.type === "jointStatus" && message.state === "error").message, /owner was replaced|disconnected/);
});

class Element {
    constructor(tagName) { this.tagName = tagName; this.children = []; this.attributes = {}; this.listeners = {}; }
    set textContent(value) { this._text = String(value); this.children = []; }
    get textContent() { return (this._text ?? "") + this.children.map(c => c.textContent).join(""); }
    appendChild(child) { this.children.push(child); return child; }
    replaceChildren(...children) { this.children = children; this._text = ""; }
    setAttribute(key, value) { this.attributes[key] = value; }
    addEventListener(key, fn) { this.listeners[key] = fn; }
    focus() { this.focused = true; }
    querySelectorAll(selector) {
        const parts = selector.split(" ");
        const all = this.children.flatMap(c => [c, ...c.querySelectorAll("*")]);
        if (selector === "*") return all;
        if (parts.length === 1) return all.filter(c => c.tagName === selector);
        return all.filter(c => c.tagName === parts[0]).flatMap(c => c.querySelectorAll(parts[1]));
    }
}
function dom() {
    const container = new Element("div"), messages = [];
    const document = { createElement: tag => new Element(tag) };
    const view = new Function("document", REQUEST_ACTIVITIES_SCRIPT + "\nreturn createRequestActivitiesView;")(document)(
        container, message => messages.push(message));
    const click = text => {
        const button = container.querySelectorAll("button").find(b => b.textContent === text);
        assert.ok(button, text); assert.ok(!button.disabled, text); button.listeners.click(); return button;
    };
    return { container, view, messages, click };
}
test("actual renderer selects exact duplicate-path row inertly, loads explicitly, back/filter are local with one response", () => {
    const d = dom(); d.view.capture("e1", capture());
    assert.equal(d.messages.length, 0);
    const radios = d.container.querySelectorAll("input");
    radios[1].listeners.change();
    assert.equal(d.messages.length, 0);
    d.click("Load related completed Activities");
    assert.deepEqual(d.messages, [{ type: "queryRequestActivities", evidenceId: "e1", requestRowId: "r2" }]);
    d.view.receive("e1", "r2", related());
    assert.match(d.container.textContent, /Exact request row r2/);
    assert.match(d.container.textContent, /<script>DB<\/script>/);
    d.click("Back to selected request");
    d.click("Show loaded completed Activities");
    assert.equal(d.messages.length, 1);
    d.click("Back to selected request");
    const filter = d.container.querySelectorAll("select")[0];
    filter.value = "long"; filter.listeners.change();
    assert.equal(d.messages.length, 1);
    assert.match(d.container.textContent, /local UI threshold/);
    radios[0].listeners.change(); d.click("Load related completed Activities");
    assert.equal(d.messages.at(-1).requestRowId, "r1");
    d.view.receive("e1", "r1", related());
    d.click("Back to selected request"); radios[1].listeners.change();
    assert.equal(d.container.querySelectorAll("button").some(b => b.textContent === "Show loaded completed Activities"), false);
});
test("renderer preserves incomplete/lost structural evidence, empty meaning and stale retained-only states", () => {
    const d = dom(); const c = capture();
    c.requests.quality.completion = "cancelled"; c.requests.quality.eventsLost = 5;
    c.requests.quality.droppedActivities = 3; d.view.capture("e1", c);
    assert.match(d.container.textContent, /cancelled; source events lost: 5/);
    d.container.querySelectorAll("input")[0].listeners.change(); d.click("Load related completed Activities");
    const r = related(); r.rows[0].verified = false; r.rows[0].verificationWithheldReason = "Incomplete stream";
    d.view.receive("e1", "r1", r);
    assert.match(d.container.textContent, /direct-childUnverified: Incomplete stream/);
    d.view.unavailable("e1", "Expired: retained display only");
    assert.match(d.container.textContent, /Expired: retained display only/);
    d.click("Back to selected request"); d.click("Show loaded completed Activities");
    assert.equal(d.messages.length, 1);
    d.view.capture("e2", capture());
    d.container.querySelectorAll("input")[0].listeners.change(); d.click("Load related completed Activities");
    d.view.receive("e1", "r1", related());
    assert.doesNotMatch(d.container.textContent, /Verified identifier relationship/);
    d.view.receive("e2", "r1", query());
    assert.match(d.container.textContent, /No retained completed Activities/);
    assert.match(d.container.textContent, /does not mean no work/);
    d.view.reset();
    assert.equal(d.container.textContent, "");
});

test("built CLI advertises actual joint capability and preserves legacy hello protocol", {
    skip: process.env.DOTNET_DIAGNOSTICS_CLI_DLL ? false : "Set DOTNET_DIAGNOSTICS_CLI_DLL for actual CLI contract."
}, async () => {
    const child = spawn("dotnet", [process.env.DOTNET_DIAGNOSTICS_CLI_DLL, "stream", "--protocol", "jsonl"]);
    const lines = createInterface({ input: child.stdout });
    const frames = [];
    const closed = new Promise((resolve, reject) => { child.on("close", resolve); child.on("error", reject); });
    lines.on("line", line => frames.push(JSON.parse(line)));
    child.stdin.end(JSON.stringify({ type: "hello", protocolVersion: 2 }) + "\n");
    assert.equal(await closed, 0);
    assert.equal(joint.supportsRequestActivities(frames[0].requestActivities), true);
    assert.equal(frames[0].protocolVersion, 2);
    assert.doesNotMatch(source, /require\(["'][^"']*mcp/i);
});
