const assert = require("node:assert/strict");
const test = require("node:test");
const {
    describeStreamCompatibilityError,
    parseProcessList,
    parseProtocolFrame,
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
