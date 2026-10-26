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
const { deriveMemoryOverview, MEMORY_OVERVIEW_SCRIPT } = require("../out/memoryOverview.js");

function point(receivedAtMs, value, segment = 1) {
    return { receivedAtMs, value, session: 1, segment };
}

function gen2(receivedAtMs, segment = 1) {
    return { receivedAtMs, generation: 2, session: 1, segment };
}

function input(overrides = {}) {
    return {
        heapPoints: [point(110, 100), point(120, 110), point(210, 102), point(220, 111)],
        allocationPoints: [],
        gen2Collections: [gen2(100), gen2(200)],
        sequenceGaps: 0,
        retainedSessionCount: 1,
        committedAvailable: true,
        allocationAvailable: true,
        seriesLimitReached: false,
        heapEvidenceAvailable: false,
        ...overrides,
    };
}

test("memory overview classifies a stable post-Gen2 floor from retained observations", () => {
    const result = deriveMemoryOverview(input());
    assert.equal(result.state, "stable");
    assert.deepEqual(result.floorValues, [100, 102]);
    assert.match(result.headline, /stayed stable or lower/);
});

test("memory overview classifies a rising post-Gen2 floor without diagnosing cause", () => {
    const result = deriveMemoryOverview(input({
        heapPoints: [point(110, 100), point(120, 108), point(210, 121), point(220, 130)],
    }));
    assert.equal(result.state, "rising");
    assert.deepEqual(result.floorValues, [100, 121]);
    assert.match(result.headline, /rose from 100 to 121/);
    assert.doesNotMatch(result.headline, /leak|root cause|retained object/i);
    assert.equal(result.action, "capture-heap");
});

test("high allocation with a stable floor remains stable and routes to allocation evidence", () => {
    const result = deriveMemoryOverview(input({
        allocationPoints: [point(120, 80_000_000), point(220, 95_000_000)],
    }));
    assert.equal(result.state, "stable");
    assert.equal(result.action, "open-allocation");
    assert.doesNotMatch(result.headline, /allocation.*caused|leak/i);
});

test("insufficient or discontinuous evidence abstains instead of treating gaps as zero", () => {
    const insufficient = deriveMemoryOverview(input({
        gen2Collections: [gen2(100)],
        heapPoints: [point(110, 100)],
        committedAvailable: false,
        allocationAvailable: false,
    }));
    assert.equal(insufficient.state, "unavailable");
    assert.match(insufficient.headline, /Cannot assess/);
    assert.ok(insufficient.quality.some(item => /two observed Gen2/.test(item)));
    assert.ok(insufficient.quality.some(item => /unavailable is not zero/.test(item)));

    const gap = deriveMemoryOverview(input({ sequenceGaps: 2 }));
    assert.equal(gap.state, "unavailable");
    assert.ok(gap.quality.some(item => /sequence gap/.test(item)));
});

function createOverviewUi({ rising = false, heapEvidence = false, includeRss = false } = {}) {
    const elements = new Map();
    const makeElement = tag => ({
        tag, textContent: "", hidden: false, children: [], dataset: {}, style: {}, attributes: {}, listeners: {},
        appendChild(child) { this.children.push(child); },
        replaceChildren(...children) { this.children = children; },
        setAttribute(name, value) { this.attributes[name] = String(value); },
        addEventListener(name, listener) { this.listeners[name] = listener; },
        focus() {},
    });
    const context = new Proxy({}, {
        get: (_target, property) => property === "measureText" ? text => ({ width: text.length * 6 }) : () => {},
        set: () => true,
    });
    const canvas = {
        ...makeElement("canvas"),
        clientWidth: 700,
        getContext: () => context,
    };
    for (const id of ["headline", "quality", "summary", "limits", "action"]) elements.set(id, makeElement("div"));
    elements.set("canvas", canvas);
    global.document = {
        body: makeElement("body"),
        createElement: makeElement,
    };
    global.window = { devicePixelRatio: 1 };
    global.getComputedStyle = () => ({
        fontFamily: "system-ui",
        getPropertyValue: () => "",
    });
    const install = new Function(`${MEMORY_OVERVIEW_SCRIPT}; return installMemoryOverview;`)();
    const counters = new Map([
        ["System.Runtime/gc-heap-size", {
            unit: "MB", points: rising
                ? [point(110, 100), point(210, 120)]
                : [point(110, 100), point(210, 102)],
        }],
        ["System.Runtime/gc-committed", { unit: "MB", points: [point(110, 180), point(210, 182)] }],
        ["System.Runtime/alloc-rate", { unit: "B/s", points: [point(110, 80_000_000), point(210, 90_000_000)] }],
    ]);
    if (includeRss) counters.set("System.Runtime/working-set", { unit: "MB", points: [point(110, 250)] });
    const captures = [];
    const navigations = [];
    const controller = install({
        canvas,
        headline: elements.get("headline"),
        quality: elements.get("quality"),
        trackSummary: elements.get("summary"),
        limitations: elements.get("limits"),
        action: elements.get("action"),
        counters,
        gcRows: [
            { ...gen2(100), element: { hidden: false } },
            { ...gen2(200), element: { hidden: false } },
        ],
        currentSession: () => 1,
        retainedSessionCount: () => 1,
        sequenceGaps: () => 0,
        seriesLimitReached: () => false,
        additionalQuality: () => [],
        historyDurationMs: () => 120_000,
        filterPoints: points => points,
        heapEvidenceAvailable: () => heapEvidence,
        captureHeap: () => captures.push("heap"),
        openEvidence: kind => navigations.push(kind),
    }, deriveMemoryOverview);
    return { controller, elements, captures, navigations };
}

test("rendering and navigation do not start diagnostic work; only the explicit capture action does", () => {
    const stable = createOverviewUi();
    stable.controller.render();
    assert.deepEqual(stable.captures, []);
    assert.deepEqual(stable.navigations, []);
    assert.equal(stable.elements.get("action").textContent, "Open allocation evidence");
    stable.elements.get("action").listeners.click();
    assert.deepEqual(stable.navigations, ["allocation"]);
    assert.deepEqual(stable.captures, []);

    const rising = createOverviewUi({ rising: true });
    rising.controller.render();
    assert.equal(rising.elements.get("action").textContent, "Capture heap snapshot…");
    rising.elements.get("action").listeners.click();
    assert.deepEqual(rising.captures, ["heap"]);

    const retainedHeap = createOverviewUi({ rising: true, heapEvidence: true, includeRss: true });
    retainedHeap.controller.render();
    assert.equal(retainedHeap.elements.get("action").textContent, "Inspect heap evidence");
    assert.match(retainedHeap.elements.get("summary").textContent, /RSS: 250 MB/);
    retainedHeap.elements.get("action").listeners.click();
    assert.deepEqual(retainedHeap.navigations, ["heap"]);
    assert.deepEqual(retainedHeap.captures, []);
});

test("normal, narrow and high-contrast layouts retain headline, temporal visualization and next action", () => {
    const html = renderHtml({
        processId: 42,
        operatingSystem: "Windows",
        processArchitecture: "x64",
        runtimeVersion: "10.0.0",
        managedEntrypointAssemblyName: "Fixture",
    }, "nonce", 120);
    const headline = html.indexOf('id="memoryOverviewHeadline"');
    const quality = html.indexOf('id="memoryOverviewQuality"');
    const chart = html.indexOf('id="memoryOverviewChart"');
    const action = html.indexOf('id="memoryOverviewAction"');
    const details = html.indexOf('class="memory-overview-details"');
    assert.ok(headline > 0 && headline < quality && quality < chart && chart < action && action < details);
    assert.match(html, /<details id="memoryOverview" class="memory-overview" open>/);
    assert.match(html, /\.memory-overview canvas \{ display:block; width:100%;/);
    assert.match(html, /@media \(max-width:480px\)[\s\S]*?\.memory-overview \.panel-actions button \{ width:100%; \}/);
    assert.match(html, /@media \(forced-colors:active\)[\s\S]*?border-color:CanvasText/);
    assert.match(html, /Timeline positions use webview receipt time/);
    assert.match(html, /does not establish object retention, a leak, or causality/);
});
