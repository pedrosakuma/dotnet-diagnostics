const assert = require("node:assert/strict");
const test = require("node:test");
const {
    describeStreamCompatibilityError,
    parseProcessList,
    parseProtocolFrame,
    isGcCollection,
    isCpuSampleSummary,
    isHeapCaptureResult,
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

test("heap capture result validator accepts a successful live snapshot projection", () => {
    const result = {
        summary: "Captured a live heap snapshot for PID 123.",
        data: {
            processId: 123,
            suspendDuration: "00:00:00.1234567",
            topTypesByBytes: [
                { typeFullName: "System.String", instanceCount: 10, totalBytes: 1024, totalBytesPercent: 42.5 },
            ],
            topTypesByInstances: [
                { typeFullName: "System.String", instanceCount: 10, totalBytes: 1024, totalBytesPercent: 42.5 },
            ],
        },
    };
    assert.equal(isHeapCaptureResult(result), true);
});

test("heap capture result validator accepts a gcdump snapshot with completion status", () => {
    const result = {
        summary: "Captured a gcdump heap snapshot for PID 123.",
        data: {
            processId: 123,
            suspendDuration: "00:00:00",
            topTypesByBytes: [],
            topTypesByInstances: [],
            warnings: ["Export of the underlying trace was not requested."],
            gcDumpStatus: {
                gcStopObserved: true,
                eventStreamCompleted: true,
                timedOut: false,
                readerFailed: false,
                traceExportRequested: false,
                traceExportCompleted: false,
            },
        },
    };
    assert.equal(isHeapCaptureResult(result), true);
});

test("heap capture result validator accepts an error-only projection with no data", () => {
    assert.equal(isHeapCaptureResult({ summary: "No process found matching the requested id." }), true);
});

test("heap capture result validator rejects malformed shapes", () => {
    assert.equal(isHeapCaptureResult(null), false);
    assert.equal(isHeapCaptureResult({ summary: 42 }), false);
    assert.equal(isHeapCaptureResult({ summary: "ok", data: { processId: "123" } }), false);
    assert.equal(isHeapCaptureResult({
        summary: "ok",
        data: {
            processId: 123,
            suspendDuration: "00:00:00",
            topTypesByBytes: [{ typeFullName: "X" }],
            topTypesByInstances: [],
        },
    }), false);
});
