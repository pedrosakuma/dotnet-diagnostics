const assert = require("node:assert/strict");
const test = require("node:test");
const { isExceptionCaptureResult } = require("../out/protocol.js");
const manifest = require("../package.json");

function fixture() {
    return {
        summary: "7 exceptions observed.",
        notes: ["Retained 2 event(s) with maxRecent=2; 5 event(s) omitted."],
        data: {
            processId: 4242, startedAt: "2026-10-14T00:00:00Z", duration: "00:00:10",
            totalExceptions: 7, recentCap: 2,
            byType: [{ exceptionType: "System.Exception", count: 7 }],
            recent: [{ timestamp: "2026-10-14T00:00:01Z", exceptionType: "System.Exception",
                exceptionMessage: "<img src=x onerror=alert(1)>", threadId: 1 }],
        },
    };
}

test("exception captures accept CLI summaries and optional crash-guard stack evidence", () => {
    const result = fixture();
    assert.equal(isExceptionCaptureResult(result), true);
    result.data.processExited = true;
    result.data.unhandledExceptionObserved = false;
    result.data.recent[0].managedStack = ["Sample.Throw()"];
    assert.equal(isExceptionCaptureResult(result), true);
    assert.ok(manifest.contributes.commands.some(command => command.command === "dotnetDiagnostics.captureExceptions"));
});

test("exception capture validation rejects malformed or unbounded results", () => {
    for (const mutate of [
        result => { result.data.totalExceptions = -1; },
        result => { result.data.recentCap = 501; },
        result => { result.data.recentCap = 0; },
        result => { result.data.recent = Array(3).fill(result.data.recent[0]); },
        result => { result.data.byType[0].count = "7"; },
        result => { result.notes = ["note", {}]; },
        result => { result.data.recent[0].managedStack = [1]; },
        result => { result.data.processExited = "true"; },
        result => { delete result.data; },
    ]) {
        const result = fixture();
        mutate(result);
        assert.equal(isExceptionCaptureResult(result), false);
    }
});
