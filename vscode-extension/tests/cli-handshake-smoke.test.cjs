const assert = require("node:assert/strict");
const test = require("node:test");
const { createCliRunner } = require("../out/cliRunner.js");
const { parseHandshakeOutput, REQUIRED_PROTOCOL_VERSION } = require("../out/environmentCheck.js");

const cliDll = process.env.DOTNET_DIAGNOSTICS_CLI_DLL;

test("built CLI completes a real JSONL handshake", { skip: cliDll ? false : "Set DOTNET_DIAGNOSTICS_CLI_DLL to run the built-CLI smoke test." }, async () => {
    const run = createCliRunner({ timeoutMs: 15_000, graceMs: 1_000, settleMs: 1_000, maxOutputChars: 4096 });
    const result = await run(
        "dotnet",
        [cliDll, "stream", "--protocol", "jsonl"],
        `${JSON.stringify({ type: "hello", protocolVersion: REQUIRED_PROTOCOL_VERSION })}\n`,
    );

    assert.equal(result.spawnError, undefined, result.spawnError);
    assert.equal(result.timedOut, false, result.stderr);
    assert.equal(result.outputLimitExceeded, undefined, "The CLI exceeded the smoke test output limit.");
    assert.equal(result.exitCode, 0, result.stderr || result.stdout);

    const handshake = parseHandshakeOutput(result.stdout);
    assert.equal(handshake.kind, "ok", result.stdout);
    assert.ok(handshake.cliVersion, "The CLI handshake did not report its version.");
});
