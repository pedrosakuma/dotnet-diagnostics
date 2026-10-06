const assert = require("node:assert/strict");
const test = require("node:test");
const {
    describeStreamCompatibilityError,
    parseProcessList,
    parseProtocolFrame,
    isGcCollection,
    isCpuSampleSummary,
} = require("../out/protocol.js");

test("process list parser retains valid targets without exposing command lines", () => {
    const processes = parseProcessList(JSON.stringify({
        data: [
            {
                processId: 123,
                commandLine: "secret argument",
                operatingSystem: "Windows",
                processArchitecture: "x64",
                runtimeVersion: "10.0.1",
                managedEntrypointAssemblyName: "Sample",
            },
            { processId: "invalid" },
            { processId: 0 },
        ],
    }));

    assert.deepEqual(processes, [{
        processId: 123,
        operatingSystem: "Windows",
        processArchitecture: "x64",
        runtimeVersion: "10.0.1",
        managedEntrypointAssemblyName: "Sample",
    }]);
    assert.equal(JSON.stringify(processes).includes("secret argument"), false);
});

test("process list parser rejects malformed CLI envelopes", () => {
    assert.throws(() => parseProcessList("{}"), /data array/);
    assert.throws(() => parseProcessList("not-json"), SyntaxError);
});

test("protocol frame parser requires a typed JSON object", () => {
    assert.deepEqual(parseProtocolFrame('{"type":"hello","protocolVersion":1}'), {
        type: "hello",
        protocolVersion: 1,
    });
    assert.throws(() => parseProtocolFrame("[]"), /string type/);
});

test("non-JSON stream output produces concise CLI compatibility guidance", () => {
    const message = describeStreamCompatibilityError(
        "Usage: dotnet-diagnostics-cli\n" + "collect options\n".repeat(30),
    );

    assert.match(message, /older CLI build/);
    assert.match(message, /dotnetDiagnostics\.cliPath/);
    assert.equal(message.includes("collect options"), false);
});

test("GC collection validator accepts a well-formed pause observation", () => {
    assert.equal(isGcCollection({
        timestamp: "2026-01-01T00:00:00Z",
        generation: 2,
        reason: "Induced",
        type: "Blocking",
        pauseDuration: "00:00:00.0123456",
    }), true);
    assert.equal(isGcCollection({ timestamp: "2026-01-01T00:00:00Z", generation: "2" }), false);
    assert.equal(isGcCollection(null), false);
});

test("CPU sample summary validator accepts a bounded hotspot list", () => {
    const summary = {
        processId: 123,
        startedAt: "2026-01-01T00:00:00Z",
        duration: "00:00:10",
        totalSamples: 42,
        topHotspots: [
            { frame: { module: "MyApp", method: "DoWork" }, inclusiveSamples: 10, exclusiveSamples: 5 },
        ],
    };
    assert.equal(isCpuSampleSummary(summary), true);
    assert.equal(isCpuSampleSummary({ ...summary, topHotspots: [{ frame: {} }] }), false);
    assert.equal(isCpuSampleSummary({ ...summary, totalSamples: "42" }), false);
});
