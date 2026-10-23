const assert = require("node:assert/strict");
const test = require("node:test");
const fs = require("node:fs");
const path = require("node:path");
const { createRequire } = require("node:module");
const Module = require("node:module");

const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    if (request === "vscode") return path.join(__dirname, "stubs", "vscode-stub.js");
    return originalResolveFilename.call(this, request, ...rest);
};

const extensionPath = path.join(__dirname, "..", "out", "extension.js");
const extensionRequire = createRequire(extensionPath);
const vscode = require("./stubs/vscode-stub.js");
const Controller = new Function("require", "exports",
    fs.readFileSync(extensionPath, "utf8") + "\nreturn CounterPanelController;")(
    name => name === "vscode" ? vscode : extensionRequire(name), {},
);

function fixture(overrides = {}, captureKind = "exceptions") {
    const messages = [];
    const frames = [];
    const session = {
        pendingCaptures: new Map(), pendingQueries: new Map(), startInFlight: false,
        ...overrides,
    };
    const controller = Object.create(Controller.prototype);
    controller.selectedTarget = { processId: 4242 };
    controller.panel = {};
    controller.panelGeneration = 1;
    controller.output = { appendLine() {} };
    controller.acquireChild = async () => ({ session, owns: false });
    controller.postMessage = message => messages.push(message);
    controller.writeFrame = (_session, frame) => {
        frames.push(frame);
        session.pendingCaptures.get(frame.requestId).deferred.resolve({
            summary: "No exceptions", notes: [], data: {
                processId: 4242, startedAt: "2026-10-14T00:00:00Z", duration: "00:00:10",
                totalExceptions: 0, recentCap: 100, recent: [], byType: [],
            },
        });
    };
    vscode.window.showQuickPick = async items => items.find(item => item.captureKind === captureKind);
    return { controller, messages, frames, session };
}

test("exception capture never queues reproduction guidance behind a busy CLI operation", async () => {
    for (const overrides of [
        { pendingCaptures: new Map([["cpu", {}]]) },
        { pendingQueries: new Map([["query", {}]]) },
        { startInFlight: true },
    ]) {
        const { controller, messages, frames } = fixture(overrides);
        await controller.captureExceptions();
        assert.deepEqual(frames, []);
        assert.equal(messages.at(-1).state, "error");
        assert.match(messages.at(-1).message, /connection is busy/);
        assert.ok(!messages.some(message => message.state === "running"),
            "busy rejection must not send the running status that clears previous evidence");
        assert.ok(!messages.some(message => /readiness is not reported/.test(message.message)));
        const requestId = messages[0].evidenceId;
        assert.ok(requestId);
        assert.ok(messages.every(message => message.evidenceId === requestId));
        assert.ok(messages.every(message => message.captureKind === "exceptions"));
    }
});

test("idle exception capture sends bounded JSONL request and renders independently of live counters", async () => {
    const { controller, messages, frames, session } = fixture();
    await controller.captureExceptions();
    assert.equal(frames.length, 1);
    assert.deepEqual({ ...frames[0], requestId: "request" }, {
        type: "capture", requestId: "request", kind: "exceptions", processId: 4242,
        durationSeconds: 10, maxRecent: 100, topN: 25,
    });
    assert.ok(messages.some(message => /readiness is not reported/.test(message.message)));
    assert.ok(messages.some(message => /repeat the problem throughout the capture/.test(message.message)));
    assert.equal(messages[0].state, "starting");
    const requestId = frames[0].requestId;
    assert.ok(requestId);
    assert.ok(messages.every(message => message.evidenceId === requestId));
    const capture = messages.find(message => message.type === "capture");
    assert.equal(capture.kind, "exceptions");
    assert.equal(capture.captureKind, "exceptions");
    assert.equal(capture.evidenceId, requestId);
    assert.equal(messages.at(-1).state, "done");
    assert.equal(session.pendingCaptures.size, 0);
    assert.equal(controller.captureExceptionsTask, undefined);
});

test("crash-guard mode stays explicit on the bounded request and every correlated response", async () => {
    const { controller, messages, frames } = fixture({}, "crash-guard");
    await controller.captureExceptions();
    const requestId = frames[0].requestId;
    assert.equal(frames[0].kind, "crash-guard");
    assert.ok(messages.every(message => message.evidenceId === requestId));
    assert.ok(messages.every(message => message.captureKind === "crash-guard"));
    assert.equal(messages.find(message => message.type === "capture").kind, "exceptions");
});

test("cancelling exception picker does not acquire or write a CLI request", async () => {
    const { controller, frames, messages } = fixture();
    vscode.window.showQuickPick = async () => undefined;
    controller.acquireChild = () => { throw new Error("must not connect"); };
    await controller.captureExceptions();
    assert.deepEqual(frames, []);
    assert.deepEqual(messages, []);
});

test("a process switch while the collector picker is open aborts before acquiring the CLI", async () => {
    const { controller, frames, messages } = fixture();
    vscode.window.showQuickPick = async items => {
        controller.selectedTarget = { processId: 2222 };
        controller.panel = {};
        controller.panelGeneration++;
        return items[0];
    };
    controller.acquireChild = async () => { assert.fail("a stale preflight must not acquire the CLI"); };

    await controller.captureExceptions();

    assert.deepEqual(frames, []);
    assert.equal(messages.at(-1).state, "error");
    assert.equal(messages.at(-1).preflight, true);
    assert.match(messages.at(-1).message, /selected process or diagnostics panel changed.*No exception capture was sent/i);
});

test("a process switch during CLI acquisition aborts before dispatching to the old PID", async () => {
    const { controller, frames, messages, session } = fixture();
    let resolveAcquire;
    let notifyAcquireStarted;
    const acquireStarted = new Promise(resolve => { notifyAcquireStarted = resolve; });
    controller.acquireChild = () => new Promise(resolve => {
        resolveAcquire = () => resolve({ session, owns: false });
        notifyAcquireStarted();
    });

    const capture = controller.captureExceptions();
    await acquireStarted;
    controller.selectedTarget = { processId: 2222 };
    controller.panel = {};
    controller.panelGeneration++;
    resolveAcquire();
    await capture;

    assert.deepEqual(frames, [], "the old PID must never receive a capture frame");
    assert.equal(messages.at(-1).state, "error");
    assert.equal(messages.at(-1).preflight, true);
    assert.match(messages.at(-1).message, /selected process or diagnostics panel changed.*No exception capture was sent/i);
    assert.equal(session.pendingCaptures.size, 0);
});

test("replacing the panel during CLI acquisition aborts even when the selected process object is unchanged", async () => {
    const { controller, frames, messages, session } = fixture();
    let resolveAcquire;
    let notifyAcquireStarted;
    const acquireStarted = new Promise(resolve => { notifyAcquireStarted = resolve; });
    controller.acquireChild = () => new Promise(resolve => {
        resolveAcquire = () => resolve({ session, owns: false });
        notifyAcquireStarted();
    });

    const selectedTarget = controller.selectedTarget;
    const capture = controller.captureExceptions();
    await acquireStarted;
    controller.panel = {};
    controller.panelGeneration++;
    resolveAcquire();
    await capture;

    assert.equal(controller.selectedTarget, selectedTarget);
    assert.deepEqual(frames, [], "a stale panel generation must not dispatch a capture");
    assert.equal(messages.at(-1).state, "error");
    assert.equal(messages.at(-1).preflight, true);
    assert.match(messages.at(-1).message, /selected process or diagnostics panel changed.*No exception capture was sent/i);
});
