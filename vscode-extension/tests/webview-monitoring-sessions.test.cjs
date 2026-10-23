const assert = require("node:assert/strict");
const test = require("node:test");
const path = require("node:path");
const Module = require("node:module");

const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    return request === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js")
        : originalResolveFilename.call(this, request, ...rest);
};
const { renderHtml } = require("../out/extension.js");
const html = renderHtml({ processId: 42, operatingSystem: "Windows", processArchitecture: "x64",
    runtimeVersion: "10.0.0", managedEntrypointAssemblyName: "Fixture" }, "nonce", 120);
const script = /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html)[1];

function counterUi() {
    const nodes = new Map();
    const make = () => ({
        textContent: "", children: [], value: "", title: "",
        appendChild(child) { this.children.push(child); },
        insertBefore(child) { this.children.unshift(child); },
        setAttribute() {}, addEventListener() {}, remove() {},
    });
    const document = { getElementById(id) {
        if (!nodes.has(id)) nodes.set(id, make());
        return nodes.get(id);
    }, createElement: make };
    const drawing = [];
    const context = new Proxy({}, { get: (_, name) => name === "measureText"
        ? text => ({ width: text.length * 6 }) : (...args) => drawing.push([name, ...args]), set: () => true });
    const canvas = { clientWidth: 1000, clientHeight: 280 };
    let now = 10_000;
    const start = script.indexOf("    const counters = new Map();");
    const end = script.indexOf("    // A capture is a single point-in-time snapshot", start);
    assert.ok(start > 0 && end > start);
    const api = new Function("document", "canvas", "context", "getComputedStyle", "installLiveTimeNavigation", "Date",
        `const metricSelect = document.getElementById('metric');
         const valueElement = document.getElementById('value');
         const qualityElement = document.getElementById('quality');
         const gcBody = document.getElementById('gcBody');
         const gcHeadline = document.getElementById('gcHeadline');
         ${script.slice(start, end)}
         return { beginCounterSession, trackSequence, addCounter, addGcPause, draw, evictOldPoints,
           state: () => ({ lastSequence, sequenceGaps, counterSessionNumber, counterSegment,
             gcPauseCount, counters, gcRows }) };`)(
        document, canvas, context, () => ({ getPropertyValue: () => "blue", color: "white", fontFamily: "system-ui" }),
        () => ({ update() {}, filter: points => points, selected: () => undefined }),
        class extends Date { constructor(...args) { super(...(args.length ? args : [now])); } static now() { return now; } },
    );
    return { ...api, drawing, get: id => document.getElementById(id), tick: ms => { now += ms; } };
}

const sample = sequence => ({ sequence, timestamp: "2026-10-23T12:00:00Z",
    counter: { provider: "System.Runtime", name: "cpu-usage", displayName: "CPU usage", value: sequence, kind: "Metric", unit: "%" } });

test("counter restart A -> B tracks B gaps from sequence 1 and retains bounded history without joining sessions", () => {
    const ui = counterUi();
    ui.beginCounterSession("A");
    for (let sequence = 1; sequence <= 200; sequence++) {
        ui.trackSequence(sequence); ui.addCounter(sample(sequence)); ui.tick(10);
    }
    assert.equal(ui.state().sequenceGaps, 0);
    const originalSegment = ui.state().counterSegment;
    ui.beginCounterSession("B");
    assert.equal(ui.state().lastSequence, 0);
    assert.equal(ui.state().sequenceGaps, 0);
    for (const sequence of [1, 2, 5, 6, 9]) {
        ui.trackSequence(sequence); ui.addCounter(sample(sequence)); ui.tick(1000);
    }
    assert.equal(ui.state().sequenceGaps, 4);
    assert.equal(ui.state().lastSequence, 9);
    assert.match(ui.get("quality").textContent, /This counter session: 4 observations missing/);
    assert.equal(ui.state().counters.get("System.Runtime/cpu-usage").points.length, 205);
    assert.ok(ui.state().counterSegment > originalSegment);
    assert.match(ui.get("counterSession").textContent, /session 2.*PID 42.*not continuous/);
    assert.match(ui.get("counterIdentity").textContent, /System.Runtime\/cpu-usage.*Metric.*%/);
    ui.drawing.length = 0;
    ui.draw();
    assert.equal(ui.drawing.filter(([command]) => command === "moveTo").length, 7,
        "four separate paths plus three boundary ticks");
    assert.ok(ui.drawing.some(([command, text]) => command === "fillText" && text === "Session 2"));
    assert.ok(ui.drawing.some(([command, text]) => command === "fillText" && text === "Sequence gap"));
    ui.beginCounterSession("B");
    assert.equal(ui.state().sequenceGaps, 4, "duplicate session notification cannot erase loss");
    ui.beginCounterSession("C");
    assert.equal(ui.state().sequenceGaps, 0, "previous loss is not attributed to a new session");
    assert.equal(ui.get("quality").textContent, "");
    ui.tick(121_000);
    ui.state().counters.forEach(ui.evictOldPoints);
    assert.equal(ui.state().counters.get("System.Runtime/cpu-usage").points.length, 0);
});

test("GC and counters consume a shared sequence and Events/Clear cannot reset their accounting", () => {
    const ui = counterUi();
    ui.beginCounterSession("counter-session");
    ui.trackSequence(1); ui.addCounter(sample(1));
    ui.trackSequence(2); ui.addGcPause({ collection: { timestamp: "2026-10-23T12:00:00Z",
        generation: 0, reason: "AllocSmall", type: "NonConcurrentGC", pauseDuration: "00:00:00.0050000" } });
    ui.trackSequence(3); ui.addCounter(sample(3));
    assert.equal(ui.state().sequenceGaps, 0, "GC is not a missing counter observation");
    ui.trackSequence(6);
    assert.equal(ui.state().sequenceGaps, 2);
    const eventsStart = script.indexOf("    const eventsStatusElement =");
    const eventsEnd = script.indexOf("    function activateCaptureTab(", eventsStart);
    assert.doesNotMatch(script.slice(eventsStart, eventsEnd), /beginCounterSession|lastSequence|sequenceGaps|counters\.clear/);
    assert.match(script, /message\.type === 'session'\) \{\s*beginCounterSession\(message\.sessionId\)/);
    assert.match(script, /'This counter session: ' \+ \[/, "terminal accounting is explicitly per counter session");
    ui.beginCounterSession("next-counter-session");
    assert.equal(ui.state().gcPauseCount, 1, "restart keeps explicitly panel-wide GC totals and retained rows");
    assert.equal(ui.state().gcRows.length, 1);
});
