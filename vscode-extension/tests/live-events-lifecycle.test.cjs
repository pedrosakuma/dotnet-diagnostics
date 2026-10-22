const assert = require("node:assert/strict");
const test = require("node:test");
const fs = require("node:fs");
const path = require("node:path");
const Module = require("node:module");
const { spawn } = require("node:child_process");
const { once } = require("node:events");

const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    return request === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js")
        : originalResolveFilename.call(this, request, ...rest);
};
const extensionPath = path.join(__dirname, "../out/extension.js");
const Controller = new Function("require", "exports", fs.readFileSync(extensionPath, "utf8")
    + "\nreturn CounterPanelController;")(Module.createRequire(extensionPath), {});

const cliFixture = `
const readline = require('node:readline');
const mode = process.argv[1];
const send = frame => process.stdout.write(JSON.stringify(frame) + '\\n');
readline.createInterface({input:process.stdin}).on('line', line => {
    const frame = JSON.parse(line);
    if(frame.type === 'hello') send({type:'hello',protocolVersion:2,
        ...(mode === 'old' ? {} : {supportedLiveKinds:['counters','gc','event_source']})});
    if(frame.type === 'start') {
        if(mode === 'denied' || mode === 'rejected') {
            send({type:'error',requestId:frame.requestId,
                code:mode === 'denied' ? 'permission_denied' : 'invalid_start',
                message:mode === 'denied' ? 'Diagnostic attach denied' : 'EventSource start rejected'});
        } else send({type:'started',sessionId:'owned-events',kinds:['event_source']});
    }
    if(frame.type === 'exitTarget') send({type:'terminal',sessionId:'owned-events',status:'targetExited'});
    if(frame.type === 'cancel') send({type:'terminal',sessionId:'owned-events',status:'stopped'});
    if(frame.type === 'disconnect') {
        if(mode === 'safety-stderr') process.stderr.write('SAFETY warning: bounded EventPipe capture exposes names.\\n');
        process.exit(0);
    }
}).on('close', () => process.exit(0));
`;

function fixture(mode) {
    const messages = [], logs = [];
    const controller = Object.create(Controller.prototype);
    controller.pendingExports = new Map();
    controller.eventsGeneration = 0;
    controller.output = { appendLine: line => logs.push(line) };
    controller.postEventsMessage = message => messages.push(message);
    controller.postMessage = message => messages.push(message);
    controller.resetEventsDeliveryState();
    let child, closed;
    controller.spawnStream = () => {
        child = spawn(process.execPath, ["-e", cliFixture, mode], { stdio: ["pipe", "pipe", "pipe"] });
        closed = once(child, "close");
        return child;
    };
    return {
        controller, messages, logs,
        async connect() { return controller.connectEventsChild(); },
        async assertExited() {
            await closed;
            assert.ok(child.exitCode !== null || child.signalCode !== null);
            assert.throws(() => process.kill(child.pid, 0), { code: "ESRCH" });
            assert.equal(controller.eventsChild, undefined);
            assert.equal(controller.eventsBatchTimer, undefined);
        },
        async cleanup() {
            if (controller.eventsBatchTimer) clearTimeout(controller.eventsBatchTimer);
            if (child && child.exitCode === null && child.signalCode === null) child.kill();
            if (closed) await closed;
        },
    };
}

test("a successful v2 stream with SAFETY stderr disconnects without an old-CLI diagnosis", { timeout: 10_000 }, async () => {
    const f = fixture("safety-stderr");
    try {
        const session = await f.connect();
        assert.equal(session.protocolReady, true);
        f.controller.writeFrame(session, { type: "disconnect" });
        await session.closed.promise;
        const errors = f.messages.filter(m => m.state === "error").map(m => m.message).join("\n");
        assert.match(errors, /CLI exited after a successful JSONL protocol handshake/);
        assert.match(errors, /SAFETY warning/);
        assert.doesNotMatch(errors, /older CLI|Update\/build/);
        assert.ok(f.logs.some(line => line.includes("SAFETY warning")));
        await f.assertExited();
    } finally { await f.cleanup(); }
});

test("an expected clean CLI shutdown is not surfaced as a protocol error", { timeout: 10_000 }, async () => {
    const f = fixture("normal");
    try {
        const session = await f.connect();
        assert.equal(session.protocolReady, true);
        await f.controller.closeChild(session);
        await f.assertExited();
        assert.equal(f.messages.some(message => message.state === "error"), false);
    } finally { await f.cleanup(); }
});

test("an unexpected clean CLI exit remains an explicit protocol error", { timeout: 10_000 }, async () => {
    const f = fixture("normal");
    try {
        const session = await f.connect();
        assert.equal(session.protocolReady, true);
        f.controller.writeFrame(session, { type: "disconnect" });
        await session.closed.promise;
        assert.ok(f.messages.some(message => message.state === "error"
            && /CLI exited after a successful JSONL protocol handshake/.test(message.message)));
        await f.assertExited();
    } finally { await f.cleanup(); }
});

test("Events rejects an old CLI without capability and closes its owned child", { timeout: 10_000 }, async () => {
    const f = fixture("old");
    try {
        await assert.rejects(f.connect(), /does not advertise.*event_source/);
        assert.ok(f.messages.some(message => message.type === "eventsStatus"
            && message.state === "error" && /Update the CLI/.test(message.message)));
        await f.assertExited();
    } finally { await f.cleanup(); }
});

for (const [mode, expected] of [["denied", /Diagnostic attach denied/], ["rejected", /EventSource start rejected/]]) {
    test(`Events ${mode} start fails explicitly and releases its owned child`, { timeout: 10_000 }, async () => {
        const f = fixture(mode);
        try {
            const session = await f.connect();
            const started = assert.rejects(session.started.promise, expected);
            f.controller.writeFrame(session, { type: "start", requestId: "start-1" });
            await started;
            await session.closed.promise;
            assert.ok(f.messages.some(message => message.state === "error" && expected.test(message.message)));
            await f.assertExited();
            assert.equal(session.pendingCaptures.size, 0);
            assert.equal(session.pendingQueries.size, 0);
        } finally { await f.cleanup(); }
    });
}

for (const action of ["targetExit", "unexpectedEof", "stdinEof", "dispose"]) {
    test(`Events ${action} completes lifecycle and leaves no owned child`, { timeout: 10_000 }, async () => {
        const f = fixture("normal");
        try {
            const session = await f.connect();
            f.controller.writeFrame(session, { type: "start", requestId: "start-1" });
            await session.started.promise;
            assert.equal(session.sessionId, "owned-events");
            if (action === "targetExit") {
                f.controller.writeFrame(session, { type: "exitTarget" });
                await session.terminal.promise;
                assert.ok(f.messages.some(message => message.state === "stopped" && /Target process exited/.test(message.message)));
                await f.controller.closeChild(session);
            } else if (action === "unexpectedEof") {
                f.controller.writeFrame(session, { type: "disconnect" });
                await session.closed.promise;
                assert.ok(f.messages.some(message => message.state === "error" && /CLI exited/.test(message.message)));
            } else if (action === "stdinEof") {
                session.process.stdin.end();
                await session.closed.promise;
                assert.ok(f.messages.some(message => message.state === "error" && /CLI exited/.test(message.message)));
            } else {
                await f.controller.disposeEventsChild();
                assert.equal(session.terminalReceived, true);
            }
            await f.assertExited();
            assert.equal(f.controller.eventsQueue.retainedEventCount, 0);
            assert.equal(f.controller.eventsQueue.retainedByteCount, 0);
            assert.equal(f.logs.some(line => /Forcing|forcibly/.test(line)), false);
        } finally { await f.cleanup(); }
    });
}
