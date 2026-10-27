const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const {
    CPU_COMPARISON_SCRIPT,
    CPU_COMPARISON_STYLES,
    isCpuComparisonQueryResult,
    supportsCpuHandleComparison,
} = require("../out/cpuComparison.js");
const { buildCpuComparisonFrame } = require("../out/protocol.js");

function capture(handle, startedAt) {
    return {
        handle,
        processId: 42,
        startedAt,
        durationSeconds: 10,
        totalSamples: 100,
        backend: "EventPipeSampleProfiler",
        evidenceKind: "StackFrequencyWithHeuristicWaits",
        origin: "live",
        producingTool: "collect_sample",
        expiresAt: "2026-10-26T14:00:00Z",
        unresolvedExclusiveSamples: 4,
        unresolvedExclusivePercent: 4,
        quality: {
            schema: "dotnet-diagnostics/evidence-quality/v1",
            limitations: [{ category: "Sampling", scope: "capture", message: "Sampled evidence." }],
            conclusions: {},
        },
    };
}

function comparison() {
    return {
        handle: "current",
        baselineHandle: "baseline",
        view: "diff",
        summary: "Objective comparison.",
        data: {
            schema: "dotnet-diagnostics/cpu-sample-handle-comparison/v1",
            baseline: capture("baseline", "2026-10-26T12:00:00Z"),
            current: capture("current", "2026-10-26T12:01:00Z"),
            compatibility: { status: "qualified", reasons: [] },
            metrics: [{
                name: "samplePopulation",
                population: "retained-samples",
                aggregation: "total",
                normalization: "none",
                delta: {
                    baselineValue: 100,
                    currentValue: 120,
                    unit: "samples",
                    deltaAbs: 20,
                    relativeDelta: 0.2,
                    betterDirection: "neutral",
                    outcome: "up",
                    unavailableReason: null,
                    baselineDenominator: 100,
                    currentDenominator: 120,
                    deltaUnit: "samples",
                },
            }],
            topMovers: [{
                displayName: "Sample.Work()",
                baselineExclusiveSamples: 20,
                currentExclusiveSamples: 30,
                baselineTotalSamples: 100,
                currentTotalSamples: 120,
                exclusiveShare: {
                    baselineValue: 20,
                    currentValue: 25,
                    unit: "%",
                    deltaAbs: 5,
                    relativeDelta: 0.25,
                    betterDirection: "neutral",
                    outcome: "up",
                    unavailableReason: null,
                    baselineDenominator: 100,
                    currentDenominator: 120,
                    deltaUnit: "percentage-points",
                },
                unresolved: false,
            }],
            notes: ["Workload equivalence is not established."],
        },
    };
}

test("CPU comparison capability and frame preserve the bounded CLI contract", () => {
    assert.equal(supportsCpuHandleComparison({
        version: 1,
        queryView: "diff",
        baselineHandleField: "baselineHandle",
        retainedHandleLimit: 2,
        maxTopMovers: 50,
    }), true);
    assert.equal(supportsCpuHandleComparison({ version: 1, retainedHandleLimit: 1 }), false);
    assert.deepEqual(buildCpuComparisonFrame("r1", "current", "baseline", 25), {
        type: "query",
        requestId: "r1",
        handle: "current",
        baselineHandle: "baseline",
        view: "diff",
        topN: 25,
    });
});

test("CPU comparison validator accepts objective bounded data and rejects unsafe shapes", () => {
    assert.equal(isCpuComparisonQueryResult(comparison()), true);
    const tooMany = comparison();
    tooMany.data.topMovers = Array(51).fill(tooMany.data.topMovers[0]);
    assert.equal(isCpuComparisonQueryResult(tooMany), false);
    const unsafe = comparison();
    unsafe.data.current.totalSamples = Number.MAX_SAFE_INTEGER + 1;
    assert.equal(isCpuComparisonQueryResult(unsafe), false);
    const verdict = comparison();
    verdict.data.metrics[0].delta.betterDirection = 42;
    assert.equal(isCpuComparisonQueryResult(verdict), false);
});

class Element {
    constructor(tagName) {
        this.tagName = tagName;
        this.children = [];
        this.attributes = {};
        this.listeners = {};
        this.className = "";
    }
    set textContent(value) { this._text = String(value); this.children = []; }
    get textContent() { return (this._text ?? "") + this.children.map(child => child.textContent).join(""); }
    set tabIndex(value) { this.attributes.tabindex = String(value); }
    appendChild(child) { child.parentElement = this; this.children.push(child); return child; }
    append(...children) { for (const child of children) this.appendChild(child); }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    addEventListener(name, listener) { this.listeners[name] = listener; }
    querySelectorAll(tagName) {
        const descendants = this.children.flatMap(child => [child, ...child.querySelectorAll("*")]);
        return tagName === "*" ? descendants : descendants.filter(child => child.tagName === tagName);
    }
}

function renderComparison(value = comparison()) {
    const container = new Element("div");
    const document = { createElement: tagName => new Element(tagName) };
    const calls = [];
    const render = new Function("document", `${CPU_COMPARISON_SCRIPT}\nreturn renderCpuComparison;`)(document);
    render(container, value, {
        openProfile: (...args) => calls.push(["open", ...args]),
        useCurrentAsNextBaseline: () => calls.push(["baseline"]),
    });
    return { container, calls };
}

test("responsive comparison projection labels every evidence field without changing table semantics", () => {
    const { container } = renderComparison();
    const tables = container.querySelectorAll("table");
    assert.deepEqual(tables.map(table => table.attributes["aria-label"]),
        ["Capture-level objective deltas", "Top method movers"]);
    for (const table of tables) {
        assert.ok(table.querySelectorAll("thead").length === 1, "column headers remain available to assistive technology");
        for (const cell of [...table.querySelectorAll("th"), ...table.querySelectorAll("td")]) {
            if (cell.parentElement?.parentElement?.tagName === "thead") continue;
            assert.ok(cell.attributes["data-label"], `${cell.tagName} ${cell.textContent} requires a narrow-layout label`);
        }
    }
    const metricCells = [...tables[0].querySelectorAll("th"), ...tables[0].querySelectorAll("td")];
    assert.equal(metricCells.find(cell => cell.attributes["data-label"] === "Baseline").textContent,
        "100 samples · denominator 100 samples");
    assert.equal(metricCells.find(cell => cell.attributes["data-label"] === "Current").textContent,
        "120 samples · denominator 120 samples");
    assert.match(tables[1].textContent, /20 samples \/ 20 % of 100/);
    assert.match(tables[1].textContent, /30 samples \/ 25 % of 120/);
    assert.match(tables[1].textContent, /Baseline callers \/ callees/);
    assert.match(tables[1].textContent, /Current callers \/ callees/);
});

test("narrow comparison CSS removes the fixed table width and preserves wrapping, focus targets and forced colors", () => {
    assert.match(CPU_COMPARISON_STYLES, /\.cpu-comparison-view \{ container-type:inline-size; \}/);
    assert.match(CPU_COMPARISON_STYLES, /@container \(max-width:650px\)[\s\S]*?\.cpu-comparison-table \{[\s\S]*?min-width:0/);
    assert.match(CPU_COMPARISON_STYLES, /content:attr\(data-label\)/);
    assert.match(CPU_COMPARISON_STYLES, /@container \(max-width:430px\)/);
    assert.match(CPU_COMPARISON_STYLES, /flex-basis:100%/);
    assert.match(CPU_COMPARISON_STYLES, /overflow-wrap:anywhere/);
    assert.match(CPU_COMPARISON_STYLES, /min-height:2\.25rem/);
    assert.match(CPU_COMPARISON_STYLES, /@media \(forced-colors: active\)[\s\S]*?border-color:CanvasText/);
});

test("unavailable method presence stays unavailable and exposes only valid evidence actions", () => {
    const value = comparison();
    value.data.topMovers[0].baselineExclusiveSamples = null;
    value.data.topMovers[0].exclusiveShare.baselineValue = null;
    value.data.topMovers[0].exclusiveShare.deltaAbs = null;
    value.data.topMovers[0].exclusiveShare.relativeDelta = null;
    value.data.topMovers[0].exclusiveShare.unavailableReason = "method-not-observed-in-baseline";
    const { container, calls } = renderComparison(value);
    const movers = container.querySelectorAll("table")[1];
    assert.match(movers.textContent, /Added to current retained projection/);
    assert.match(movers.textContent, /Unavailable \(denominator 100 samples\)/);
    assert.match(movers.textContent, /Unavailable \(method-not-observed-in-baseline\)/);
    assert.doesNotMatch(movers.textContent, /Baseline callers \/ callees/);
    const current = movers.querySelectorAll("button").find(button => button.textContent === "Current callers / callees");
    current.listeners.click();
    assert.deepEqual(calls, [["open", "current", "caller-callee", "Sample.Work()"]]);
});

test("narrow visual fixtures use the production webview with deterministic locale and timezone", () => {
    const renderer = fs.readFileSync(path.join(__dirname, "..", "scripts", "render-media.cjs"), "utf8");
    assert.match(renderer, /function comparisonHarness\(scenario\) \{\s+const html = renderHtml\(/);
    assert.match(renderer, /timezoneId: "UTC"/);
    assert.match(renderer, /locale: "en-US"/);
    assert.match(renderer, /viewport: scenario\.width|width: scenario\.width/);
    assert.doesNotMatch(renderer, /CPU_COMPARISON_SCRIPT|CPU_COMPARISON_STYLES/);
    assert.match(renderer, /readySelector[\s\S]*?#cpuQueryViews h3/);
});
