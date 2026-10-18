const assert = require("node:assert/strict");
const test = require("node:test");
const path = require("node:path");
const Module = require("node:module");

const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    if (request === "vscode") {
        return path.join(__dirname, "stubs", "vscode-stub.js");
    }
    return originalResolveFilename.call(this, request, ...rest);
};

const { renderHtml } = require("../out/extension.js");
const html = renderHtml({
    processId: 4242,
    operatingSystem: "Linux",
    processArchitecture: "x64",
    runtimeVersion: "10.0.0",
    managedEntrypointAssemblyName: "Sample",
}, "nonce123", 120);

test("Live Events panel exposes bounded controls and clear sensitive-data framing", () => {
    assert.match(html, /id="resultTabEvents"/);
    assert.match(html, /id="startEvents"/);
    assert.match(html, /id="stopEvents"/);
    assert.match(html, /id="eventsSearch"[^>]*maxlength="256"/);
    assert.match(html, /bounded in-memory windows and are not automatically saved or logged/);
    assert.match(html, /Native Windows ETW is not collected/);
    assert.match(html, /const eventsMaxCount = 2000;/);
    assert.match(html, /const eventsMaxBytes = 8388608;/);
    assert.match(html, /const eventsPageSize = 100;/);
    assert.match(html, /const eventsBatchMaxBytes = 65536;/);
});

test("managed exception specialization requires the known provider, ID, version and exact payload fields", () => {
    const script = /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html)?.[1];
    assert.ok(script);
    assert.match(script, /event\.provider === 'Microsoft-Windows-DotNETRuntime'/);
    assert.match(script, /event\.eventId === 80 && event\.version === 1/);
    assert.match(script, /'ExceptionFlags', 'ClrInstanceID'/);
    assert.match(script, /payloadNames\.length === exceptionFields\.length/);
    assert.match(script, /payloadNames\.every\(\(name, index\) => name === exceptionFields\.slice\(\)\.sort\(\)\[index\]\)/);
    assert.match(script, /eventDetails\.appendChild\(payload\)/);
});

test("throttled quality rendering schedules a trailing update", () => {
    const script = /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html)?.[1];
    assert.ok(script);
    assert.match(script, /eventsQualityRenderTimer = setTimeout\(\(\) => \{\s*eventsQualityRenderTimer = undefined;\s*renderEventsQuality\(\);/);
});
