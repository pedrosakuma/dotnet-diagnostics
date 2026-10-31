const assert = require("node:assert/strict");
const test = require("node:test");

const growth = require("../out/retainedMemoryGrowth.js");

const capability = {
    version: 1,
    available: true,
    storeCapacity: 64,
    minimumStoreCapacity: 2,
    queryView: "growth",
    handleKind: "heap-snapshot",
    baselineField: "baselineHandle",
    objectiveTargetField: "typeFilter",
    comparisonPairsField: "comparisonPairs",
    objectiveSchema: "dotnet-diagnostics/retained-growth-evidence/v1",
    maxAttempts: 8,
    minimumRobustPairs: 3,
    rankBy: ["bytes", "instances"],
    maxTopN: 500,
    storage: "in-memory",
    boundedLifetime: true,
};

function descriptor(id, capturedAt, source = "live") {
    return {
        evidenceId: id,
        source,
        processId: 42,
        capturedAt,
        quality: "no structured limitation reported",
        available: true,
        detailsAvailable: source === "live",
        identity: "ephemeral",
    };
}

function rankedResult() {
    return {
        handle: "current",
        baselineHandle: "baseline",
        view: "growth",
        baselineOrigin: "live",
        currentOrigin: "live",
        processId: 42,
        baselineCapturedAt: "2026-10-31T10:00:00Z",
        currentCapturedAt: "2026-10-31T10:01:00Z",
        elapsed: "00:01:00",
        rankBy: "bytes",
        baselineHeapBytes: 1000,
        currentHeapBytes: 1800,
        totalHeapGrowthBytes: 800,
        totalGrowers: 1,
        growers: [{
            typeFullName: "Sample.Cache",
            moduleName: "Sample",
            baselineBytes: 100,
            currentBytes: 700,
            bytesDelta: 600,
            baselineInstances: 2,
            currentInstances: 14,
            instancesDelta: 12,
            identity: { typeFullName: "Sample.Cache", moduleName: "Sample" },
            retentionPaths: [{ targetObjectAddress: "0x1234" }],
            totalRetentionPaths: 2,
            omittedRetentionPaths: 1,
        }],
        notes: ["Bounded response."],
        baselineQuality: null,
        currentQuality: null,
    };
}

function objectiveResult() {
    return {
        handle: "current",
        baselineHandle: "baseline",
        view: "growth",
        objectiveRetainedGrowth: {
            schema: "dotnet-diagnostics/retained-growth-evidence/v1",
            targetType: { typeFullName: "Sample.Cache", moduleName: "Sample" },
            pairMethod: { id: "paired-delta", version: 1 },
            counts: {
                attempts: 3, successfulAttempts: 3, failedAttempts: 0, abortedAttempts: 0,
                captures: 6, completePairs: 3, acceptedPairs: 3, rejectedPairs: 0,
                processRuns: 3, allocationObservations: 2,
            },
            pairs: [{
                attemptId: "primary",
                availability: "available",
                baselineRetainedBytes: 100,
                currentRetainedBytes: 700,
                retainedBytesDelta: 600,
                baselineRetainedInstances: 2,
                currentRetainedInstances: 14,
                retainedInstancesDelta: 12,
                compatibilityIdentity: { backend: "ClrMD", independenceUnit: "process_run" },
                compatibilityMismatches: [],
                unavailableFields: [],
                assumptions: ["Same managed type."],
                limitations: [],
            }],
            repeated: {
                availability: "available",
                acceptedPairs: 3,
                medianRetainedBytesDelta: 640,
                retainedBytesDeltaMad: 20,
                medianRetainedInstancesDelta: 12,
                retainedInstancesDeltaMad: 1,
                retainedBytesDeltaInterval: { lower: 600, upper: 680 },
                retainedInstancesDeltaInterval: { lower: 11, upper: 13 },
                unavailableFields: [],
                assumptions: ["Independent process runs."],
                limitations: ["Bootstrap does not remove systematic bias."],
            },
            allocationEvidence: {
                availability: "available",
                medianSampledBytes: 50000,
                sampleEvents: 20,
                lostOrDropped: 0,
                assumptions: [],
                limitations: ["Sampled allocation traffic."],
            },
            rootPathEvidence: {
                availability: "available",
                capturesWithPaths: 1,
                totalCaptures: 3,
                matchingPathCount: 2,
                unavailableReason: null,
            },
            observerEffects: {
                gcDumpCaptures: 1,
                observedGen2Collections: 2,
                provenance: "One gcdump pair induced blocking Gen2 collection.",
                limitations: ["Observed count is caller supplied."],
            },
            assumptions: ["Shallow retained bytes."],
            limitations: ["No leak or health conclusion."],
        },
    };
}

test("heap-growth capability, explicit frame and pair compatibility preserve the CLI contract", () => {
    assert.equal(growth.supportsHeapGrowth(capability), true);
    assert.equal(growth.supportsHeapGrowth({ ...capability, storeCapacity: 1 }), true);
    assert.equal(growth.supportsHeapGrowth({ ...capability, maxAttempts: 9 }), false);
    assert.deepEqual(growth.buildHeapGrowthFrame("r1", "current", "baseline", "instances", "Sample.Cache"), {
        type: "query",
        requestId: "r1",
        handle: "current",
        baselineHandle: "baseline",
        view: "growth",
        rankBy: "instances",
        minDeltaPct: 0,
        topN: 25,
        typeFilter: "Sample.Cache",
    });

    const baseline = descriptor("b", "2026-10-31T10:00:00Z");
    const current = descriptor("c", "2026-10-31T10:01:00Z");
    assert.equal(growth.heapEvidenceCompatibility(baseline, current).compatible, true);
    assert.match(growth.heapEvidenceCompatibility(current, baseline).reason, /captured after/);
    assert.match(growth.heapEvidenceCompatibility(baseline, { ...current, source: "gcdump" }).reason, /different heap sources/);
    assert.match(growth.heapEvidenceCompatibility({ ...baseline, available: false }, current).reason, /unavailable/);
});

test("growth validators accept bounded ranked and objective evidence and reject unsafe rows", () => {
    assert.equal(growth.isHeapGrowthQueryResult(rankedResult()), true);
    assert.equal(growth.isRetainedGrowthObjectiveResult(objectiveResult()), true);
    const tooMany = rankedResult();
    tooMany.growers = Array(26).fill(tooMany.growers[0]);
    assert.equal(growth.isHeapGrowthQueryResult(tooMany), false);
    const unsafe = rankedResult();
    unsafe.growers[0].bytesDelta = Number.MAX_SAFE_INTEGER + 1;
    assert.equal(growth.isHeapGrowthQueryResult(unsafe), false);
    const attempts = objectiveResult();
    attempts.objectiveRetainedGrowth.pairs = Array(9).fill(attempts.objectiveRetainedGrowth.pairs[0]);
    assert.equal(growth.isRetainedGrowthObjectiveResult(attempts), false);
});

test("ranked growth is hidden when structured quality does not support the conclusion", () => {
    const result = rankedResult();
    assert.equal(growth.growthRankingSupported(result), true);
    result.currentQuality = { conclusions: { regressionOrHealthyControl: "inconclusive" } };
    assert.equal(growth.growthRankingSupported(result), false);
    result.currentQuality = {};
    assert.equal(growth.growthRankingSupported(result), false);
});

class Element {
    constructor(tagName) {
        this.tagName = tagName;
        this.children = [];
        this.listeners = {};
        this.className = "";
    }
    set textContent(value) { this._text = String(value); this.children = []; }
    get textContent() { return (this._text ?? "") + this.children.map(child => child.textContent).join(""); }
    appendChild(child) { child.parentElement = this; this.children.push(child); return child; }
    append(...children) { for (const child of children) this.appendChild(child); }
    replaceChildren(...children) { this.children = []; this._text = ""; this.append(...children); }
    addEventListener(name, listener) { this.listeners[name] = listener; }
    setAttribute(name, value) { this[name] = String(value); }
    querySelectorAll(tagName) {
        const descendants = this.children.flatMap(child => [child, ...child.querySelectorAll("*")]);
        return tagName === "*" ? descendants : descendants.filter(child => child.tagName === tagName);
    }
}

function render(result) {
    const document = { createElement: tagName => new Element(tagName) };
    const renderer = new Function("document", `${growth.RETAINED_MEMORY_GROWTH_SCRIPT}\nreturn renderRetainedGrowth;`)(document);
    const container = new Element("div");
    const selected = [];
    renderer(container, result, identity => selected.push(identity));
    return { container, selected };
}

test("renderer separates ranking, objective statistics, allocation, roots and observer effects", () => {
    const ranked = render(rankedResult());
    assert.match(ranked.container.textContent, /ranked by absolute retained bytes/);
    assert.match(ranked.container.textContent, /bounded and is not an exhaustive heap inventory/);
    assert.match(ranked.container.textContent, /Representative path captured/);
    const type = ranked.container.querySelectorAll("button").find(button => button.textContent === "Sample.Cache");
    type.listeners.click();
    assert.equal(ranked.selected[0].typeFullName, "Sample.Cache");

    const objective = render(objectiveResult()).container.textContent;
    assert.match(objective, /Retained bytes100 → 700 · delta 600/);
    assert.match(objective, /Median bytes delta 640; MAD 20/);
    assert.match(objective, /Allocation traffic \(separate\): available/);
    assert.match(objective, /Root\/path evidence: available/);
    assert.match(objective, /Observer effects: 1 GC dump capture/);
    assert.match(objective, /Counts, method, identity and unavailable fields/);
    assert.match(objective, /Assumptions and limitations/);
});

test("unsupported ranking remains explicit and narrow-pane styles preserve accessible controls", () => {
    const result = rankedResult();
    result.currentQuality = { conclusions: { regressionOrHealthyControl: "inconclusive" } };
    const { container } = render(result);
    assert.match(container.textContent, /Growing-type ranking is unavailable/);
    assert.equal(container.querySelectorAll("button").length, 0);
    assert.match(growth.RETAINED_MEMORY_GROWTH_STYLES, /@media\(max-width:760px\)/);
    assert.match(growth.RETAINED_MEMORY_GROWTH_STYLES, /grid-template-columns:1fr/);
    assert.match(growth.RETAINED_MEMORY_GROWTH_STYLES, /@media\(forced-colors:active\)/);
    assert.match(growth.RETAINED_MEMORY_GROWTH_HTML, /role="status"/);
    assert.match(growth.RETAINED_MEMORY_GROWTH_HTML, /does not collect EventPipe data or calculate retained-growth statistics in TypeScript/);
});
