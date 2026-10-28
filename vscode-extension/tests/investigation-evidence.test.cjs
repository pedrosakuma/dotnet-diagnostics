const assert = require("node:assert/strict");
const test = require("node:test");
const {
    INVESTIGATION_EVIDENCE_KINDS,
    InvestigationEvidenceStore,
    classifyInvestigationTemporalRelation,
    parseInvestigationEvidenceDescriptor,
    supportsInvestigationEvidence,
} = require("../out/investigationEvidence.js");

const start = "2026-10-26T15:00:00.000Z";

function descriptor(kind, overrides = {}) {
    const connectionId = overrides.connectionId ?? "cli-one";
    const storeId = overrides.storeId ?? "store-one";
    const startedAt = overrides.startedAt ?? start;
    const endedAt = overrides.endedAt ?? "2026-10-26T15:00:06.000Z";
    const identityStatus = overrides.identityStatus ?? "verified";
    const handle = overrides.handle === undefined ? `${kind}-handle` : overrides.handle;
    return {
        version: 1,
        evidenceId: overrides.evidenceId ?? `${kind}-evidence`,
        kind,
        collectorKind: overrides.collectorKind ?? kind,
        target: {
            processId: overrides.processId ?? 4242,
            processStartedAt: identityStatus === "verified" ? "2026-10-26T14:58:00.000Z" : null,
            identityStatus,
        },
        window: {
            kind: overrides.windowKind ?? "reported",
            start: startedAt,
            end: endedAt,
            clock: {
                kind: "diagnostic-host-utc",
                instanceId: connectionId,
                comparison: "reported-only",
            },
            completion: "normal",
        },
        retention: {
            connection: { instanceId: connectionId, storeId },
            projection: { owner: "extension-context", mode: "response-copy" },
            drilldown: {
                owner: handle === null ? "none" : "cli-connection",
                connectionId,
                storeId,
                handle,
                status: handle === null ? "unavailable" : "available",
                unavailableReason: handle === null ? "not-retained" : null,
                expiresNoLaterThan: handle === null ? null
                    : Object.hasOwn(overrides, "expiresNoLaterThan")
                        ? overrides.expiresNoLaterThan
                        : new Date(Date.now() + 10 * 60 * 1000).toISOString(),
                invalidatedBy: handle === null ? [] : [
                    "expired", "capacity-evicted", "owner-replaced", "invalidated", "connection-closed", "process-exited",
                ],
            },
        },
        backend: { kind: kind === "off_cpu" ? "perf" : "eventpipe", source: kind },
        quality: { status: "reported", source: "collector", limitations: ["bounded"] },
        safety: { risk: kind === "off_cpu" ? "high" : "moderate", authorization: "standard-preflight" },
        mayContainSensitiveData: kind !== "gc",
        limitations: ["Independent captures are not causal."],
        availability: identityStatus === "changed" ? "unavailable" : "available",
        unavailableReason: identityStatus === "changed" ? "target-changed" : null,
    };
}

test("capability negotiation is exact and older CLIs remain compatible", () => {
    assert.equal(supportsInvestigationEvidence({
        version: 1,
        kinds: [...INVESTIGATION_EVIDENCE_KINDS],
        maxItems: 7,
        maxItemBytes: 1024 * 1024,
        maxTotalBytes: 4 * 1024 * 1024,
    }), true);
    assert.equal(supportsInvestigationEvidence(undefined), false);
    assert.equal(supportsInvestigationEvidence({ version: 2 }), false);
});

test("all seven kinds are retained once and gc-activities is excluded", () => {
    const store = new InvestigationEvidenceStore();
    store.open(11);
    for (const kind of INVESTIGATION_EVIDENCE_KINDS) {
        const result = store.admit(11, descriptor(kind), { kind });
        assert.equal(result.admitted, true);
    }
    assert.equal(store.snapshot().length, 7);
    assert.equal(parseInvestigationEvidenceDescriptor(descriptor("gc-activities")), undefined);
});

test("same-kind replacement is atomic and failed admissions preserve the prior item", () => {
    const store = new InvestigationEvidenceStore();
    store.open(3);
    assert.equal(store.admit(3, descriptor("cpu", { evidenceId: "first" }), { value: 1 }).admitted, true);
    const oversized = store.admit(3, descriptor("cpu", { evidenceId: "oversized" }), {
        text: "x".repeat(1024 * 1024),
    });
    assert.deepEqual(oversized, { admitted: false, reason: "item-too-large" });
    assert.equal(store.snapshot()[0].descriptor.evidenceId, "first");
    const replacement = store.admit(3, descriptor("cpu", { evidenceId: "second" }), { value: 2 });
    assert.equal(replacement.admitted, true);
    assert.equal(replacement.replacedEvidenceId, "first");
    assert.equal(store.snapshot().length, 1);
    assert.equal(store.snapshot()[0].projection.value, 2);
});

test("target generation and count/byte ceilings are enforced at insertion", () => {
    const store = new InvestigationEvidenceStore();
    assert.deepEqual(store.admit(1, descriptor("cpu"), {}), { admitted: false, reason: "context-closed" });
    store.open(1);
    assert.deepEqual(store.admit(2, descriptor("cpu"), {}), {
        admitted: false,
        reason: "target-generation-mismatch",
    });
    const payload = { text: "x".repeat(1_047_400) };
    assert.equal(store.admit(1, descriptor("cpu"), payload).admitted, true);
    assert.equal(store.admit(1, descriptor("allocation"), payload).admitted, true);
    assert.equal(store.admit(1, descriptor("off_cpu"), payload).admitted, true);
    assert.deepEqual(store.admit(1, descriptor("contention"), payload), {
        admitted: false,
        reason: "context-too-large",
    });
    assert.equal(store.snapshot().length, 3);
    store.open(2);
    assert.equal(store.snapshot().length, 0);
});

test("TTL is an upper bound while replacement, eviction and disconnect remain distinct", () => {
    const store = new InvestigationEvidenceStore();
    store.open(7);
    store.admit(7, descriptor("cpu", { evidenceId: "cpu", connectionId: "cli-a" }), {});
    store.admit(7, descriptor("requests", { evidenceId: "requests", connectionId: "cli-b" }), {});
    store.markConnectionClosed("cli-a");
    assert.equal(store.snapshot().find(item => item.descriptor.kind === "cpu")
        .descriptor.retention.drilldown.unavailableReason, "connection-closed");
    assert.equal(store.markUnavailable("requests", "capacity-evicted"), true);
    store.markConnectionClosed("cli-b");
    assert.equal(store.snapshot().find(item => item.descriptor.kind === "requests")
        .descriptor.retention.drilldown.unavailableReason, "capacity-evicted");

    const expiring = new InvestigationEvidenceStore();
    expiring.open(8);
    expiring.admit(8, descriptor("gc", {
        evidenceId: "gc",
        expiresNoLaterThan: "2026-10-26T15:10:06.000Z",
    }), {});
    expiring.expireUpperBounds(Date.parse("2026-10-26T15:10:07.000Z"));
    assert.equal(expiring.snapshot()[0].descriptor.retention.drilldown.unavailableReason, "expired");
    expiring.markConnectionClosed("cli-one");
    assert.equal(expiring.snapshot()[0].descriptor.retention.drilldown.unavailableReason, "expired");
});

test("TTL automatically transitions borrowed drilldown to display-only", async () => {
    const store = new InvestigationEvidenceStore();
    store.open(9);
    const expiresNoLaterThan = new Date(Date.now() + 20).toISOString();
    store.admit(9, descriptor("cpu", { expiresNoLaterThan }), {});
    await new Promise(resolve => setTimeout(resolve, 50));
    const drilldown = store.snapshot()[0].descriptor.retention.drilldown;
    assert.equal(drilldown.status, "display-only");
    assert.equal(drilldown.unavailableReason, "expired");
});

test("independent windows never produce observed-overlap", () => {
    const left = descriptor("threadpool");
    const overlapping = descriptor("requests", {
        startedAt: "2026-10-26T15:00:05.000Z",
        endedAt: "2026-10-26T15:00:10.000Z",
    });
    assert.equal(classifyInvestigationTemporalRelation(left, overlapping), "reported-intersection");
    assert.notEqual(classifyInvestigationTemporalRelation(left, overlapping), "observed-overlap");
    assert.equal(classifyInvestigationTemporalRelation(left, descriptor("requests", {
        startedAt: "2026-10-26T15:00:06.000Z",
        endedAt: "2026-10-26T15:00:12.000Z",
    })), "adjacent");
    assert.equal(classifyInvestigationTemporalRelation(left, descriptor("requests", {
        startedAt: "2026-10-26T15:00:07.000Z",
        endedAt: "2026-10-26T15:00:12.000Z",
    })), "none");
    assert.equal(classifyInvestigationTemporalRelation(left, descriptor("requests", {
        connectionId: "another-cli",
    })), "unknown");
    assert.equal(classifyInvestigationTemporalRelation(left, descriptor("requests", {
        identityStatus: "unknown",
    })), "unknown");
    assert.equal(classifyInvestigationTemporalRelation(left, descriptor("requests", {
        windowKind: "requested",
    })), "unknown");
});
