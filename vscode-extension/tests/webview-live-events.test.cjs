const assert = require("node:assert/strict");
const test = require("node:test");
const path = require("node:path");
const Module = require("node:module");

const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    if (request === "vscode") {
        return path.join(__dirname, "stubs", "vscode-stub.js");
    }
    return originalResolveFilename.call(this, request, ...rest);
};

const { renderHtml } = require("../out/extension.js");
const html = renderHtml({
    processId: 4242,
    operatingSystem: "Linux",
    processArchitecture: "x64",
    runtimeVersion: "10.0.0",
    managedEntrypointAssemblyName: "Sample",
}, "nonce123", 120);

test("Live Events panel exposes bounded controls and clear sensitive-data framing", () => {
    assert.match(html, /id="monitoringTabEvents" role="tab"[^>]*aria-controls="monitoringEvents">Live Events/);
    assert.match(html, /id="monitoringEvents" class="monitoring-view" role="tabpanel"/);
    assert.doesNotMatch(html, /id="resultTabEvents"|id="resultTabDiagnostics"/);
    assert.match(html, /id="startEvents"/);
    assert.match(html, /id="stopEvents"/);
    assert.match(html, /id="eventsSearch"[^>]*maxlength="256"/);
    assert.match(html, /bounded in-memory windows and are not automatically saved or logged/);
    assert.match(html, /Native Windows ETW is not collected/);
    assert.match(html, /const eventsMaxCount = 2000;/);
    assert.match(html, /const eventsMaxBytes = 8388608;/);
    assert.match(html, /const eventsPageSize = 100;/);
    assert.match(html, /const eventsBatchMaxBytes = 65536;/);
});

test("Monitoring subview navigation is local, keyboard accessible, and does not touch stream state", () => {
    const script = /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html)?.[1];
    assert.ok(script);
    const start = script.indexOf("const monitoringTabs =");
    const end = script.indexOf("const eventsStatusElement =", start);
    assert.ok(start >= 0 && end > start);
    const source = script.slice(start, end);
    assert.doesNotMatch(source, /postMessage|startEvents|stopEvents|ackEventBatch|receiveEventsBatch/);

    const panels = new Map([
        ["monitoringLive", { hidden: false }],
        ["monitoringEvents", { hidden: true }],
    ]);
    const tabs = ["Live", "Events"].map(name => ({
        id: "monitoringTab" + name,
        attributes: { "aria-controls": "monitoring" + name, "aria-selected": String(name === "Live") },
        listeners: {},
        setAttribute(key, value) { this.attributes[key] = String(value); },
        getAttribute(key) { return this.attributes[key]; },
        addEventListener(type, listener) { this.listeners[type] = listener; },
        focus() { this.focused = true; },
    }));
    const document = {
        querySelectorAll: selector => selector === '.monitoring-tabs [role="tab"]' ? tabs : [],
        getElementById: id => panels.get(id),
    };
    const navigation = new Function("document", `${source}; return {monitoringTabs, selectMonitoringTab};`)(document);
    assert.equal(panels.get("monitoringLive").hidden, false);
    tabs[0].listeners.keydown({ key: "ArrowRight", preventDefault() {} });
    assert.equal(panels.get("monitoringLive").hidden, true);
    assert.equal(panels.get("monitoringEvents").hidden, false);
    assert.equal(tabs[1].attributes["aria-selected"], "true");
    assert.equal(tabs[1].tabIndex, 0);
    assert.equal(tabs[1].focused, true);
    navigation.selectMonitoringTab(tabs[0]);
    assert.equal(panels.get("monitoringLive").hidden, false);
    assert.equal(panels.get("monitoringEvents").hidden, true);
});

test("managed exception specialization requires the known provider, ID, version and exact payload fields", () => {
    const script = /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html)?.[1];
    assert.ok(script);
    assert.match(script, /event\.provider === 'Microsoft-Windows-DotNETRuntime'/);
    assert.match(script, /event\.eventId === 80 && event\.version === 1/);
    assert.match(script, /'ExceptionFlags', 'ClrInstanceID'/);
    assert.match(script, /payloadNames\.length === exceptionFields\.length/);
    assert.match(script, /payloadNames\.every\(\(name, index\) => name === exceptionFields\.slice\(\)\.sort\(\)\[index\]\)/);
    assert.match(script, /eventDetails\.appendChild\(payload\)/);
    assert.match(script, /payload bytes were present, but decoded fields are unavailable/);
    assert.match(script, /Decoded payload fields unavailable; payload completeness is unknown/);
});

test("throttled quality rendering schedules a trailing update", () => {
    const script = /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html)?.[1];
    assert.ok(script);
    assert.match(script, /eventsQualityRenderTimer = setTimeout\(\(\) => \{\s*eventsQualityRenderTimer = undefined;\s*renderEventsQuality\(\);/);
});

function eventsDocument() {
    function element(tag) {
        const listeners = new Map();
        return {
            tag, children: [], attributes: {}, value: "", disabled: false, ownText: "",
            get textContent() { return this.ownText + this.children.map(child => child.textContent).join("\n"); },
            set textContent(value) { this.ownText = String(value); this.children = []; },
            appendChild(child) { child.parent = this; this.children.push(child); },
            remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); },
            replaceChildren(...children) { this.ownText = ""; this.children = children; },
            setAttribute(name, value) { this.attributes[name] = value; },
            addEventListener(type, callback) { listeners.set(type, callback); },
            dispatch(type, properties = {}) { listeners.get(type)?.({ preventDefault() {}, ...properties }); },
        };
    }
    const ids = new Map();
    const document = {
        getElementById(id) {
            if (!ids.has(id)) ids.set(id, element(id));
            return ids.get(id);
        },
        createElement: element,
    };
    const script = /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html)[1];
    const start = script.indexOf("    const eventsStatusElement =");
    const end = script.indexOf("    function activateCaptureTab(", start);
    assert.ok(start >= 0 && end > start);
    const messages = [];
    const timers = new Map();
    let nextTimer = 0;
    let now = 10_000;
    const api = new Function("document", "vscode", "TextEncoder", "setTimeout", "clearTimeout", "Date",
        `${script.slice(start, end)}
         return { receiveEventsBatch, setEventsStatus,
           snapshot: () => ({ count: events.length, bytes: eventsRetainedBytes,
             evicted: eventsUiEvicted, selected: selectedEvent?.sequence, following: eventsFollowing }) };`)(
        document, { postMessage: message => messages.push(message) }, TextEncoder,
        callback => { timers.set(++nextTimer, callback); return nextTimer; },
        id => timers.delete(id),
        { now: () => now },
    );
    let batchId = 0;
    return {
        ...api, messages, get: id => document.getElementById(id),
        flushQuality() {
            now += 1000;
            const callbacks = [...timers.values()];
            timers.clear();
            callbacks.forEach(callback => callback());
        },
        batch(events, quality, sessionId = "events-1") {
            api.receiveEventsBatch({ sessionId, batchId: ++batchId, events, quality });
        },
    };
}

function event(sequence, overrides = {}) {
    return {
        sessionId: "events-1", sequence, timestamp: "2026-01-01T00:00:00Z",
        receivedAt: "2026-01-01T00:00:00.010Z", provider: "Unknown.Provider",
        eventName: `Event${sequence}`, eventId: 7, version: 3, level: "Informational",
        threadId: null, activityId: null, relatedActivityId: null, payload: { value: `item${sequence}` },
        metadataTruncated: false, payloadTruncated: false,
        omittedPayloadFields: 0, omittedPayloadCharacters: 0, unsupportedPayloadValues: 0,
        ...overrides,
    };
}

test("Events executes pause/follow/filter/clear and local details without diagnostic requests", { timeout: 10_000 }, () => {
    const ui = eventsDocument();
    assert.equal(ui.get("eventsClear").attributes["aria-disabled"], "true");
    ui.setEventsStatus("running", "Running", "events-1");
    ui.batch([event(1), event(2)]);
    assert.equal(ui.get("eventsClear").attributes["aria-disabled"], "false");
    const body = ui.get("eventsBody");
    assert.equal(body.children.length, 2);
    body.children[0].dispatch("click");
    assert.equal(ui.snapshot().selected, 2);
    assert.match(ui.get("eventDetails").textContent, /Unknown.Provider.*Event2/);
    assert.match(ui.get("eventDetails").textContent, /value: item2/);
    assert.ok(ui.messages.every(message => message.type === "ackEventBatch"));

    ui.get("eventsFollow").dispatch("click");
    assert.equal(ui.snapshot().following, false);
    const pausedText = body.textContent;
    ui.batch([event(3)]);
    assert.equal(ui.snapshot().count, 3, "presentation pause continues bounded ingestion");
    assert.equal(body.textContent, pausedText, "presentation remains frozen");
    assert.match(ui.get("eventsPresentation").textContent, /Paused presentation: 1 new arrivals; 0 evicted/);
    assert.match(ui.get("eventsPage").textContent, /Paused page \(not refreshed\); 3 events currently retained/);
    assert.equal(ui.messages.at(-1).type, "ackEventBatch", "pause is not a missing-ACK consumer");
    ui.get("eventsFollow").dispatch("click");
    assert.match(body.textContent, /Event3/);
    assert.equal(ui.snapshot().selected, 2, "new input does not replace selection");
    ui.get("eventsSearch").value = "item1";
    ui.get("eventsSearch").dispatch("input");
    assert.equal(body.children.length, 1);
    assert.match(body.textContent, /Event1/);
    body.children[0].dispatch("keydown", { key: "Enter" });
    assert.equal(ui.snapshot().selected, 1);

    ui.get("eventsClear").dispatch("click");
    assert.equal(ui.snapshot().count, 0);
    assert.equal(ui.get("eventsClear").attributes["aria-disabled"], "true");
    assert.equal(ui.snapshot().bytes, 0);
    assert.equal(ui.snapshot().selected, undefined);
    assert.equal(body.children.length, 0);
    assert.match(ui.get("eventDetails").textContent, /Select an event/);
    assert.ok(ui.messages.every(message => message.type === "ackEventBatch"), "all details/filter/clear are local");
});

test("Events evicts a selected event while paused without replay history and preserves cumulative quality on clear", { timeout: 10_000 }, () => {
    const ui = eventsDocument();
    ui.batch([event(1)]);
    ui.get("eventsBody").children[0].dispatch("click");
    ui.get("eventsFollow").dispatch("click");
    const quality = { eventPipeEventsLost: null, eventPipeLossAvailable: false,
        coreQueueDropped: 17, cliDispatchQueueDropped: 23, hostDeliveryDropped: 31 };
    for (let first = 2; first <= 2100; first += 64) {
        ui.batch(Array.from({ length: Math.min(64, 2101 - first) }, (_, index) => event(first + index)), quality);
        assert.ok(ui.snapshot().count <= 2000);
        assert.ok(ui.snapshot().bytes <= 8 * 1024 * 1024);
        assert.ok(ui.get("eventsBody").children.length <= 100);
    }
    assert.equal(ui.snapshot().count, 2000);
    assert.equal(ui.snapshot().evicted, 100);
    assert.equal(ui.snapshot().selected, undefined);
    assert.match(ui.get("eventDetails").textContent, /selected event expired/);
    assert.match(ui.get("eventsPresentation").textContent, /2099 new arrivals; 100 evicted/);
    assert.equal(ui.get("eventsBody").children.length, 0, "expired payloads cannot linger in paused DOM rows");
    ui.get("eventsFollow").dispatch("click");
    assert.equal(ui.get("eventsBody").children.length, 100);
    assert.match(ui.get("eventsBody").textContent, /Event2100/);
    ui.get("eventsClear").dispatch("click");
    assert.equal(ui.snapshot().evicted, 100, "clear is not capture-wide accounting reset");
    // Force the real trailing render without a wall-clock sleep.
    ui.flushQuality();
    assert.match(ui.get("eventsQuality").textContent, /source loss count unknown/);
    assert.match(ui.get("eventsQuality").textContent, /17 Core queue drops/);
    assert.match(ui.get("eventsQuality").textContent, /100 UI-window evictions/);
    assert.match(ui.get("eventsQuality").textContent, /Panel lifetime \(including earlier streams and Clear\)/);
    const before = ui.messages.length;
    ui.batch([event(9999)], quality, "stale-session");
    assert.equal(ui.snapshot().count, 0);
    assert.equal(ui.messages.length, before, "stale sessions cannot update or ACK this window");
});

test("Events waits for an actual observation and shows the exact requested subscription without affecting counter state", () => {
    const ui = eventsDocument();
    const subscription = { processId: 42, providers: ["<provider>", "Other"], eventLevel: 5, keywords: "-9223372036854775808" };
    ui.setEventsStatus("starting", "Starting", undefined, subscription);
    assert.match(ui.get("eventsSubscription").textContent, /PID 42.*<provider>, Other.*level: 5.*-9223372036854775808 \(signed decimal\)/);
    ui.setEventsStatus("running", "Running", "events-1");
    assert.match(ui.get("eventsStatus").textContent, /Waiting for first event/);
    ui.batch([]);
    assert.match(ui.get("eventsStatus").textContent, /Waiting for first event/);
    ui.batch([event(1)]);
    assert.match(ui.get("eventsStatus").textContent, /events observed/);
    ui.setEventsStatus("running", "Running", "events-1");
    assert.match(ui.get("eventsStatus").textContent, /events observed/);
    ui.get("eventsClear").dispatch("click");
    assert.match(ui.get("eventsStatus").textContent, /events observed/, "Clear does not reset stream observation state");
    ui.setEventsStatus("stopped", "Stopped");
    ui.setEventsStatus("starting", "Starting", undefined, subscription);
    ui.setEventsStatus("running", "Running", "events-2");
    assert.match(ui.get("eventsStatus").textContent, /Waiting for first event/, "a restart cannot inherit first-event evidence");
    assert.deepEqual(ui.messages.map(message => message.type), ["ackEventBatch", "ackEventBatch"]);
});

test("Events executes schema-gated details and generic fallback with inert original bounded payload", { timeout: 10_000 }, () => {
    const payload = { ExceptionType: "System.Exception", ExceptionMessage: "<script>secret</script>",
        ExceptionEIP: "0", ExceptionHRESULT: "-1", ExceptionFlags: "0", ClrInstanceID: "1" };
    for (const overrides of [
        { provider: "Other.Provider", eventId: 80, version: 1, payload },
        { provider: "Microsoft-Windows-DotNETRuntime", eventId: 80, version: 2, payload },
        { provider: "Microsoft-Windows-DotNETRuntime", eventId: 80, version: 1, payload: { ExceptionType: "partial" } },
        { payload: {}, payloadFieldsUnavailable: true },
        { payload: { value: "bounded" }, payloadTruncated: true, omittedPayloadCharacters: 99 },
        { provider: "Microsoft-Windows-DotNETRuntime", eventId: 80, version: 1, payload },
    ]) {
        const ui = eventsDocument();
        ui.batch([event(1, overrides)]);
        ui.get("eventsBody").children[0].dispatch("click");
        const details = ui.get("eventDetails");
        const recognized = overrides.provider === "Microsoft-Windows-DotNETRuntime"
            && overrides.version === 1 && overrides.payload === payload;
        assert.equal(details.textContent.includes("Schema-verified managed exception"), recognized);
        const pre = details.children.find(child => child.tag === "pre");
        assert.ok(pre);
        if (overrides.payload === payload) assert.match(pre.textContent, /<script>secret<\/script>/);
        if (overrides.payloadFieldsUnavailable) assert.match(details.textContent, /completeness is unknown/);
        if (overrides.payloadTruncated) assert.match(details.textContent, /99 payload characters omitted/);
        assert.deepEqual(ui.messages.map(message => message.type), ["ackEventBatch"]);
    }
});
