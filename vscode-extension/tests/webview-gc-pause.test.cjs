const assert = require("node:assert/strict");
const test = require("node:test");
const path = require("node:path");
const Module = require("node:module");

// out/extension.js does `require("vscode")` at module scope purely because the file imports the
// vscode namespace; none of that surface actually runs just from loading the module. Redirect that
// single require to a tiny local stub so this test can import the real compiled renderHtml()
// without needing a VS Code host process.
const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    if (request === "vscode") {
        return path.join(__dirname, "stubs", "vscode-stub.js");
    }
    return originalResolveFilename.call(this, request, ...rest);
};

const { renderHtml } = require("../out/extension.js");

const target = {
    processId: 4242,
    operatingSystem: "Linux",
    processArchitecture: "x64",
    runtimeVersion: "10.0.0",
    managedEntrypointAssemblyName: "Sample",
};

function extractInlineScript(html) {
    const match = /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html);
    assert.ok(match, "renderHtml output must contain an inline <script> block");
    return match[1];
}

// Regression test for the bug where the entire webview script is embedded inside an *outer*
// TypeScript template literal in renderHtml(). A single backslash (e.g. "\d") inside that literal
// is not a recognized JS string escape, so it was silently dropped before ever reaching the
// browser - turning the GC pause-duration regex into a pattern that could never match, and making
// every "Pause" cell render as "unknown" (see the manual VS Code extension test that found this).
test("renderHtml's embedded webview script keeps literal backslashes in its regex patterns", () => {
    const script = extractInlineScript(renderHtml(target, "nonce123", 120));

    // The known-good source text, byte for byte, as it must appear in the generated HTML so the
    // browser's regex engine receives real "\d"/"\." escapes instead of bare "d"/".".
    assert.ok(
        script.includes("/^(?:(\\d+)\\.)?(\\d{2}):(\\d{2}):(\\d{2})(?:\\.(\\d+))?$/"),
        "expected the TimeSpan-parsing regex to retain single backslashes after template-literal evaluation",
    );
});

test("the extracted parseTimeSpanToMs implementation parses real .NET TimeSpan 'c' format strings", () => {
    const script = extractInlineScript(renderHtml(target, "nonce123", 120));
    const functionMatch = /function parseTimeSpanToMs\(value\) \{[\s\S]*?\n    \}/.exec(script);
    assert.ok(functionMatch, "expected to find the parseTimeSpanToMs function body in the webview script");

    // eslint-disable-next-line no-new-func -- intentionally evaluating the real, extracted webview
    // source so this test exercises exactly what ships to the browser, not a hand-copied stand-in.
    const parseTimeSpanToMs = new Function(`${functionMatch[0]}; return parseTimeSpanToMs;`)();

    assert.equal(parseTimeSpanToMs("00:00:00.0112272"), 11.2272);
    assert.equal(parseTimeSpanToMs("00:00:00.0027723"), 2.7723);
    assert.equal(parseTimeSpanToMs("00:00:03"), 3000);
    assert.equal(parseTimeSpanToMs("1.00:00:00"), 86_400_000);
    assert.ok(Number.isNaN(parseTimeSpanToMs("not-a-timespan")));
    assert.ok(Number.isNaN(parseTimeSpanToMs(undefined)));
});
