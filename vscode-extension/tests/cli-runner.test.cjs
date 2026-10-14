const assert = require("node:assert/strict");
const test = require("node:test");
const { createCliRunner } = require("../out/cliRunner.js");

test("runner captures output and stdin", async () => {
    const run = createCliRunner({ timeoutMs: 5000, graceMs: 500, settleMs: 500 });
    const result = await run(process.execPath, ["-e", "process.stdin.pipe(process.stdout)"], "hello");
    assert.equal(result.exitCode, 0);
    assert.equal(result.stdout, "hello");
});

test("runner reports spawn errors for a missing executable", async () => {
    const run = createCliRunner({ timeoutMs: 5000, graceMs: 500, settleMs: 500 });
    const result = await run("definitely-not-a-real-cli-1165", []);
    assert.match(result.spawnError, /ENOENT|EACCES/);
});

test("runner force-kills a child that ignores SIGTERM and settles", { skip: process.platform === "win32" }, async () => {
    const run = createCliRunner({ timeoutMs: 300, graceMs: 300, settleMs: 2000 });
    const started = Date.now();
    const result = await run(process.execPath, ["-e", "process.on('SIGTERM',()=>{});setInterval(()=>{},1000)"]);
    assert.equal(result.timedOut, true);
    assert.ok(Date.now() - started < 4000);
});

test("runner settles even when the child never closes after SIGKILL", async () => {
    const run = createCliRunner({ timeoutMs: 200, graceMs: 100, settleMs: 200 });
    const started = Date.now();
    const result = await run(process.execPath, ["-e", "setInterval(()=>{},1000)"]);
    assert.equal(result.timedOut, true);
    assert.ok(Date.now() - started < 3000);
});
