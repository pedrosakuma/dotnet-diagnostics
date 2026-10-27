const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const Module = require("node:module");
const resolve = Module._resolveFilename;
Module._resolveFilename = function(request, ...rest) {
    return request === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js") : resolve.call(this, request, ...rest);
};
const b = require("../out/baselineCompare.js");
const h = require("../out/processHistory.js");
const vscode = require("./stubs/vscode-stub.js");

const id = character => character.repeat(32);
const artifact = (artifactId, kind = "heap-snapshot", name = kind) =>
    ({ artifactId, kind, name, supportedViews: ["top-types"] });
const capture = (captureId, artifactId, kind = "heap-snapshot", name = `Capture ${captureId[0]}`) => ({
    captureId, createdUtc: "2026-10-26T00:00:00Z", name, state: "sealed",
    artifacts: [artifact(artifactId, kind)], qualityStatus: "complete",
});
const current = capture(id("a"), id("b"));

test("comparison choice pre-scope distinguishes matching, incompatible and unknown metadata without replacing CLI validation", () => {
    const same = b.classifyComparisonChoice(current.artifacts[0], artifact(id("c")));
    assert.equal(same.compatibility, "matching");
    assert.match(same.reason, /still require CLI validation/);
    const incompatible = b.classifyComparisonChoice(current.artifacts[0], artifact(id("d"), "counters"));
    assert.equal(incompatible.compatibility, "incompatible");
    assert.match(incompatible.reason, /family mismatch/);
    const unknown = b.classifyComparisonChoice(current.artifacts[0], artifact(id("e"), ""));
    assert.equal(unknown.compatibility, "unknown");
    assert.match(unknown.reason, /CLI must determine/);
});

test("empty catalog keeps the loaded current artifact as an explicit self-comparison and gives a next step", () => {
    const items = b.comparisonChoiceItems({ captures: [], truncated: false }, current, id("b"));
    const selectable = items.filter(item => item.capture);
    assert.equal(selectable.length, 1);
    assert.equal(selectable[0].capture.captureId, current.captureId);
    assert.match(selectable[0].label, /Current artifact/);
    assert.match(items.at(-1).label, /Save another heap-snapshot capture or use the CLI/);
});

test("catalog pagination includes later pages while a current capture outside the loaded window remains selectable", async () => {
    const later = capture(id("c"), id("d"));
    const calls = [];
    const catalog = await b.loadComparisonCatalog("cli", async (_cli, args) => {
        calls.push(args);
        const after = args.includes("--after-capture-id");
        return JSON.stringify({ data: {
            captures: after ? [later] : [capture(id("e"), id("f"), "counters")],
            nextAfterCaptureId: after ? null : id("9"),
        } });
    });
    assert.equal(calls.length, 2);
    assert.ok(calls[1].includes(id("9")));
    assert.deepEqual(catalog.captures.map(item => item.captureId), [id("e"), id("c")]);
    const choices = b.comparisonChoiceItems(catalog, current, id("b")).filter(item => item.capture);
    assert.equal(choices[0].capture.captureId, current.captureId);
    assert.ok(choices.some(item => item.capture.captureId === later.captureId));
    assert.ok(choices.some(item => item.compatibility === "incompatible"));
});

test("catalog stops at 500 and reports older out-of-window captures", async () => {
    let page = 0;
    const catalog = await b.loadComparisonCatalog("cli", async () => {
        const captures = Array.from({ length: 50 }, (_, index) => {
            const value = (page * 50 + index).toString(16).padStart(32, "0");
            return capture(value, id("f"));
        });
        page++;
        return JSON.stringify({ data: {
            captures, nextAfterCaptureId: page.toString(16).padStart(32, "0"),
        } });
    });
    assert.equal(catalog.captures.length, 500);
    assert.equal(catalog.truncated, true);
    assert.equal(page, 10);
});

test("direction is reversible and preserves baseline-left candidate-right argument ownership", () => {
    const other = capture(id("c"), id("d"));
    const normal = b.orientComparison("current-candidate", current, id("b"), other, id("d"));
    assert.equal(normal.baseline.captureId, other.captureId);
    assert.equal(normal.candidate.captureId, current.captureId);
    const reversed = b.orientComparison("current-baseline", current, id("b"), other, id("d"));
    assert.equal(reversed.baseline.captureId, current.captureId);
    assert.equal(reversed.candidate.captureId, other.captureId);
});

test("comparison explains the successfully loaded current artifact before catalog selection and honors reversal", async () => {
    const other = capture(id("c"), id("d"));
    const originalRun = h.runHistoryCli, originalPick = vscode.window.showQuickPick;
    const calls = [], prompts = [];
    h.runHistoryCli = async (_cli, args) => {
        calls.push(args);
        if (args[0] === "captures") return JSON.stringify({ data: { captures: [other], nextAfterCaptureId: null } });
        return JSON.stringify({
            schema: "dotnet-diagnostics/historical-comparison/v1",
            left: { reference: { captureId: current.captureId, artifactId: id("b") }, kind: "heap-snapshot" },
            right: { reference: { captureId: other.captureId, artifactId: id("d") }, kind: "heap-snapshot" },
            compatibility: { status: "qualified", reasons: [{
                code: "RetainedOnly", side: "both", field: "population", message: "Retained observations only.",
            }] },
            quality: {
                left: { capture: { isComplete: true, isIncomplete: false }, notes: [] },
                right: { capture: { isComplete: true, isIncomplete: false }, notes: [] },
            },
            metrics: [],
        });
    };
    let picker = 0;
    vscode.window.showQuickPick = async (items, options) => {
        prompts.push(options.placeHolder);
        picker++;
        if (picker === 1) return items.find(item => item.direction === "current-baseline");
        return items.find(item => item.capture?.captureId === other.captureId);
    };
    try {
        const result = await b.compareSaved("cli", current, id("b"));
        assert.equal(result.baseline.captureId, current.captureId);
        assert.equal(result.candidate.captureId, other.captureId);
        assert.match(prompts[0], /Successfully loaded current artifact/);
        assert.match(prompts[1], /Current baseline \(left\)/);
        const compare = calls.find(args => args[0] === "compare");
        assert.deepEqual(compare.slice(0, 9), ["compare", "--baseline-capture-id", current.captureId,
            "--baseline-artifact-id", id("b"), "--candidate-capture-id", other.captureId,
            "--candidate-artifact-id", id("d")]);
    } finally {
        h.runHistoryCli = originalRun;
        vscode.window.showQuickPick = originalPick;
    }
});
