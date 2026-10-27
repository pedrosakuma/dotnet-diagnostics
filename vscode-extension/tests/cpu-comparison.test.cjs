const assert = require("node:assert/strict");
const test = require("node:test");

const {
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
