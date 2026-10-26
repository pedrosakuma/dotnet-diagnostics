const assert = require("node:assert/strict");
const test = require("node:test");
const fs = require("node:fs");
const path = require("node:path");
const {
    buildInvestigationLensModel,
    INVESTIGATION_LENS_HTML,
    INVESTIGATION_LENS_SCRIPT,
    INVESTIGATION_LENS_STYLES,
} = require("../out/investigationLens.js");

function descriptor(kind, overrides = {}) {
    const connectionId = overrides.connectionId ?? "cli-one";
    const identityStatus = overrides.identityStatus ?? "verified";
    return {
        version: 1,
        evidenceId: overrides.evidenceId ?? `${kind}-evidence`,
        kind,
        collectorKind: kind,
        target: {
            processId: 4242,
            processStartedAt: identityStatus === "verified" ? "2026-10-26T14:58:00.000Z" : null,
            identityStatus,
        },
        window: {
            kind: overrides.windowKind ?? "reported",
            start: overrides.start ?? "2026-10-26T15:00:00.000Z",
            end: overrides.end ?? "2026-10-26T15:00:06.000Z",
            clock: { kind: "diagnostic-host-utc", instanceId: connectionId, comparison: "reported-only" },
            completion: "normal",
        },
        retention: {
            connection: { instanceId: connectionId, storeId: "store-one" },
            projection: { owner: "extension-context", mode: "response-copy" },
            drilldown: {
                owner: "cli-connection", connectionId, storeId: "store-one", handle: `${kind}-handle`,
                status: overrides.status ?? "available",
                unavailableReason: overrides.unavailableReason ?? null,
                expiresNoLaterThan: "2026-10-26T15:20:00.000Z",
                invalidatedBy: ["expired", "capacity-evicted", "owner-replaced", "connection-closed"],
            },
        },
        backend: overrides.backend ?? { kind: "eventpipe", source: kind },
        quality: { status: "reported", source: "collector", limitations: [] },
        safety: { risk: kind === "off_cpu" ? "high" : "moderate", authorization: "standard-preflight" },
        mayContainSensitiveData: kind !== "gc",
        limitations: ["Independent captures are not causal."],
        availability: "available",
        unavailableReason: null,
    };
}

function item(kind, projection, overrides = {}) {
    return { descriptor: descriptor(kind, overrides), projection, retainedBytes: 1024 };
}

function metrics(...entries) {
    return { metrics: entries.map(([label, value]) => ({ label, value })) };
}

test("sync-over-async separate windows corroborate symptoms without a verdict", () => {
    const model = buildInvestigationLensModel([
        item("threadpool", metrics(["Confirmed starvation adjustments", 3])),
        item("requests", metrics(["Long-running", 5]), {
            start: "2026-10-26T15:00:07.000Z",
            end: "2026-10-26T15:00:17.000Z",
        }),
    ]);

    assert.match(model.headline, /controlled, better-aligned repeat/);
    assert.match(model.inferred[0], /separate windows of the same process/);
    assert.match(model.inferred[0], /corroborates a recurring symptom/);
    assert.doesNotMatch(model.inferred.join(" "), /root cause|caused|verdict/i);
    assert.equal(model.visualization.relation, "none");
    assert.match(model.visualization.nextDecision, /do not assign causality/);
    assert.deepEqual(model.actions.map(action => action.kind), ["threadpool", "requests"]);
});

test("reported intersection stays reported-only and changes the next action", () => {
    const model = buildInvestigationLensModel([
        item("threadpool", metrics(["Confirmed starvation adjustments", 1])),
        item("requests", metrics(["Long-running", 2]), {
            start: "2026-10-26T15:00:05.000Z",
            end: "2026-10-26T15:00:10.000Z",
        }),
    ]);
    assert.equal(model.visualization.relation, "reported-intersection");
    assert.match(model.inferred[0], /do not establish observed overlap/);
    assert.ok(model.limitations.some(text => /never presented as observed overlap or causal/.test(text)));
});

test("the lens abstains when positive evidence or comparable identity is missing", () => {
    const missingSignal = buildInvestigationLensModel([
        item("threadpool", metrics(["Confirmed starvation adjustments", 0])),
        item("requests", metrics(["Long-running", 4])),
    ]);
    assert.equal(missingSignal.visualization, undefined);
    assert.match(missingSignal.inferred[0], /^Abstained:/);

    const unknownIdentity = buildInvestigationLensModel([
        item("threadpool", metrics(["Confirmed starvation adjustments", 2]), { identityStatus: "unknown" }),
        item("requests", metrics(["Long-running", 4]), { identityStatus: "unknown" }),
    ]);
    assert.equal(unknownIdentity.visualization, undefined);
    assert.match(unknownIdentity.inferred[0], /target identity or comparable reported windows/);
    assert.equal(unknownIdentity.relations[0].relation, "unknown");
});

test("every subset has structured observed, inferred and unavailable output", () => {
    const empty = buildInvestigationLensModel([]);
    assert.equal(empty.evidence.length, 7);
    assert.equal(empty.observed.length, 0);
    assert.equal(empty.unavailable.length, 7);
    assert.match(empty.inferred[0], /never starts captures automatically/);

    const one = buildInvestigationLensModel([item("gc", { metrics: [] }, {
        status: "display-only", unavailableReason: "connection-closed",
    })]);
    assert.equal(one.observed.length, 1);
    assert.equal(one.unavailable.length, 6);
    assert.match(one.evidence.find(evidence => evidence.kind === "gc").retention,
        /display-only \(connection-closed\)/);
    assert.equal(one.visualization, undefined);
});

test("UI contract preserves accessibility, responsive high contrast and specialist-only navigation", () => {
    assert.match(INVESTIGATION_LENS_HTML, /role="tabpanel"/);
    assert.match(INVESTIGATION_LENS_HTML, /aria-labelledby="investigationEvidenceHeading"/);
    assert.match(INVESTIGATION_LENS_HTML, /Observed, inferred and unavailable/);
    assert.match(INVESTIGATION_LENS_STYLES, /@media \(max-width:600px\)/);
    assert.match(INVESTIGATION_LENS_STYLES, /@media \(forced-colors:active\)/);
    const actionRoute = /button\.addEventListener\('click', \(\) => \{([\s\S]*?)\n        \}\);/.exec(INVESTIGATION_LENS_SCRIPT);
    assert.ok(actionRoute);
    assert.match(actionRoute[1], /selectResultTab\('Workspace'\)/);
    assert.match(actionRoute[1], /setWorkspaceEvidence\(action\.kind\)/);
    assert.doesNotMatch(actionRoute[1], /postMessage|capture/i);
});

test("compiled panel exposes the optional tab and clear route without history/export wiring", () => {
    const source = fs.readFileSync(path.join(__dirname, "..", "src", "extension.ts"), "utf8");
    assert.match(source, /id="resultTabInvestigation"[^>]* hidden/);
    assert.match(INVESTIGATION_LENS_SCRIPT, /type:'clearInvestigationEvidence'/);
    assert.match(INVESTIGATION_LENS_HTML, /Memory-only bounded projection; not saved, exported or restored/);
    assert.match(source, /message\.type === "clearInvestigationEvidence"/);
    assert.match(source, /investigationTab\.hidden && investigationTab\.getAttribute\('aria-selected'\) === 'true'/);
    assert.doesNotMatch(INVESTIGATION_LENS_SCRIPT, /save|history|export/i);
});
