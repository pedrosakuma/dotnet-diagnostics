const assert = require("node:assert/strict");
const test = require("node:test");
const fs = require("node:fs");
const path = require("node:path");
const { createRequire } = require("node:module");

const extensionPath = path.join(__dirname, "..", "out", "extension.js");
const extensionRequire = createRequire(extensionPath);
const vscode = require("./stubs/vscode-stub.js");
const Controller = new Function("require", "exports",
    fs.readFileSync(extensionPath, "utf8") + "\nreturn CounterPanelController;")(
    name => name === "vscode" ? vscode : extensionRequire(name), {},
);

function fixture(overrides = {}) {
    const messages = [];
    const frames = [];
    const session = {
        pendingCaptures: new Map(), pendingQueries: new Map(), startInFlight: false,
        ...overrides,
    };
    const controller = Object.create(Controller.prototype);
    controller.selectedTarget = { processId: 4242 };
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
    vscode.window.showQuickPick = async items => items[0];
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
        assert.ok(!messages.some(message => /reproduce the problem now/.test(message.message)));
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
    assert.ok(messages.some(message => /reproduce the problem now/.test(message.message)));
    assert.equal(messages[0].state, "starting");
    assert.equal(messages.find(message => message.type === "capture").kind, "exceptions");
    assert.equal(messages.at(-1).state, "done");
    assert.equal(session.pendingCaptures.size, 0);
    assert.equal(controller.captureExceptionsTask, undefined);
});

test("cancelling exception picker does not acquire or write a CLI request", async () => {
    const { controller, frames, messages } = fixture();
    vscode.window.showQuickPick = async () => undefined;
    controller.acquireChild = () => { throw new Error("must not connect"); };
    await controller.captureExceptions();
    assert.deepEqual(frames, []);
    assert.deepEqual(messages, []);
});
