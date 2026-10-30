const assert = require("node:assert/strict");
const test = require("node:test");
const {
    SYMPTOM_SPECS,
    buildSymptomActionItems,
    runSymptomInvestigation,
} = require("../out/symptomInvestigation.js");

test("symptom guide maps each common journey to one bounded recommended first measurement", () => {
    assert.deepEqual(SYMPTOM_SPECS.map(spec => spec.id), [
        "high-cpu",
        "memory-growth",
        "slow-requests",
        "locks-waits",
        "exceptions-crashes",
        "runtime-health",
    ]);
    const recommendations = Object.fromEntries(SYMPTOM_SPECS.map(spec => [
        spec.id,
        spec.actions.filter(action => action.recommended).map(action => [action.command, action.arguments]),
    ]));
    assert.deepEqual(recommendations, {
        "high-cpu": [["dotnetDiagnostics.captureCpu", undefined]],
        "memory-growth": [["dotnetDiagnostics.startCounters", undefined]],
        "slow-requests": [["dotnetDiagnostics.captureDiagnostics", ["requests"]]],
        "locks-waits": [["dotnetDiagnostics.captureThreadSnapshot", undefined]],
        "exceptions-crashes": [["dotnetDiagnostics.captureExceptions", undefined]],
        "runtime-health": [["dotnetDiagnostics.startCounters", undefined]],
    });
    for (const spec of SYMPTOM_SPECS) {
        assert.equal(spec.actions.filter(action => action.recommended).length, 1, spec.id);
        assert.ok(spec.actions.some(action => action.label === "All diagnostic actions"), spec.id);
    }
    assert.ok(SYMPTOM_SPECS.find(spec => spec.id === "high-cpu").actions
        .some(action => action.label === "Capture new CPU samples + retain raw trace"));
    assert.equal(SYMPTOM_SPECS.find(spec => spec.id === "high-cpu").actions[0].label, "Capture new CPU samples");
});

test("guide discloses target, evidence mode and boundaries before dispatch", () => {
    const selected = buildSymptomActionItems("locks-waits", "Example.Api · PID 4242");
    assert.match(selected[0].detail, /Target: Example\.Api · PID 4242/);
    assert.match(selected[0].detail, /Collects new temporary evidence/);
    assert.match(selected[0].detail, /permissions and consent/);
    const withoutTarget = buildSymptomActionItems("memory-growth");
    assert.match(withoutTarget[0].detail, /none selected/);
    assert.match(withoutTarget[0].detail, /process picker/);
    assert.match(withoutTarget[0].detail, /does not create a persistent artifact/);
});

test("opening or cancelling symptom navigation never dispatches collection", async () => {
    let dispatches = 0;
    const result = await runSymptomInvestigation(
        "high-cpu",
        undefined,
        async () => undefined,
        async () => { dispatches++; },
    );
    assert.equal(result, "cancelled");
    assert.equal(dispatches, 0);
});

test("a chosen alternative delegates once to the existing canonical command", async () => {
    const calls = [];
    let options;
    const result = await runSymptomInvestigation(
        "slow-requests",
        "Example.Api · PID 4242",
        async (items, receivedOptions) => {
            options = receivedOptions;
            return items.find(item => item.label === "Requests + completed Activities");
        },
        async (...args) => { calls.push(args); },
    );
    assert.equal(result, "dispatched");
    assert.deepEqual(calls, [["dotnetDiagnostics.captureRequestActivities"]]);
    assert.equal(options.matchOnDetail, true);
    assert.match(options.placeHolder, /Nothing starts/);
});
