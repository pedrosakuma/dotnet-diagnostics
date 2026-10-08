const assert = require("node:assert/strict");
const test = require("node:test");
const fs = require("node:fs");
const path = require("node:path");
const Module = require("node:module");

const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    if (request === "vscode") {
        return path.join(__dirname, "stubs", "vscode-stub.js");
    }
    return originalResolveFilename.call(this, request, ...rest);
};

const { renderHtml, renderDumpAnalysisHtml } = require("../out/extension.js");
const { SingleFlightGuard, buildQueryFrame, parseTypeFilter, MAX_TYPE_FILTER_LENGTH } = require("../out/protocol.js");

const target = {
    processId: 4242,
    operatingSystem: "Linux",
    processArchitecture: "x64",
    runtimeVersion: "10.0.0",
    managedEntrypointAssemblyName: "Sample",
};

function extractInlineScript(html) {
    const match = /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html);
    assert.ok(match, "output must contain an inline <script> block");
    return match[1];
}

test("buildQueryFrame omits typeFilter unless provided", () => {
    assert.deepEqual(buildQueryFrame("r1", "h1", "timers"), { type: "query", requestId: "r1", handle: "h1", view: "timers" });
    assert.deepEqual(
        buildQueryFrame("r2", "h1", "thread-statics", "MyApp.Cache"),
        { type: "query", requestId: "r2", handle: "h1", view: "thread-statics", typeFilter: "MyApp.Cache" },
    );
});

test("parseTypeFilter accepts and trims exact type names", () => {
    assert.equal(parseTypeFilter("  MyApp.Cache  "), "MyApp.Cache");
    assert.equal(parseTypeFilter("Ns.Outer+Inner`1"), "Ns.Outer+Inner`1");
});

test("parseTypeFilter rejects empty, non-string, oversized, and control-character values", () => {
    for (const bad of [undefined, null, 42, {}, "", "   ", "a\nb", "a\u0000b", "x".repeat(MAX_TYPE_FILTER_LENGTH + 1)]) {
        assert.equal(parseTypeFilter(bad), undefined, JSON.stringify(bad));
    }
    assert.equal(parseTypeFilter("x".repeat(MAX_TYPE_FILTER_LENGTH)), "x".repeat(MAX_TYPE_FILTER_LENGTH));
});

for (const [name, html] of [
    ["live", renderHtml(target, "nonce123", 120)],
    ["dump", renderDumpAnalysisHtml("sample.dmp", "nonce123")],
]) {
    test(`${name} webview labels retained-exceptions and thread-statics`, () => {
        const script = extractInlineScript(html);
        assert.match(script, /'retained-exceptions': 'Retained exceptions'/);
        assert.match(script, /'thread-statics': 'Thread statics'/);
        assert.match(html, /id="threadStaticsType"/);
        assert.match(html, /id="threadStaticsButton"/);
    });

    test(`${name} webview thread-statics button validates input client-side`, () => {
        const script = extractInlineScript(html);
        const busyFn = /function setThreadStaticsBusy\(busy\) \{[\s\S]*?\n    \}/.exec(script);
        assert.ok(busyFn, "expected setThreadStaticsBusy");
        const match = /threadStaticsButton\.addEventListener\('click', \(\) => \{[\s\S]*?\n    \}\);/.exec(script);
        assert.ok(match, "expected the thread-statics click handler");

        function run(inputValue) {
            const posted = [];
            const input = { value: inputValue };
            const error = { textContent: "" };
            let handler;
            const button = { disabled: false, textContent: "Query thread statics", addEventListener(_event, fn) { handler = fn; } };
            new Function("threadStaticsButton", "threadStaticsInput", "threadStaticsError", "vscode", busyFn[0] + match[0])(
                button, input, error, { postMessage: message => posted.push(message) });
            handler();
            const first = { disabled: button.disabled, text: button.textContent };
            handler();
            return { posted, error: error.textContent, first, button };
        }

        const empty = run("   ");
        assert.deepEqual(empty.posted, []);
        assert.match(empty.error, /exact type name/);

        const ok = run("  MyApp.Cache ");
        assert.deepEqual(ok.posted, [{ type: "queryThreadStatics", typeFilter: "MyApp.Cache" }]);
        assert.equal(ok.error, "");
        assert.deepEqual(ok.first, { disabled: true, text: "Querying…" }, "button must disable on submit; duplicate click posts nothing");
        assert.equal(empty.first.disabled, false);
    });
}

test("SingleFlightGuard runs one task at a time and releases on success or failure", async () => {
    const guard = new SingleFlightGuard();
    let release;
    let runs = 0;
    const first = guard.run(() => { runs++; return new Promise(resolve => { release = resolve; }); });
    assert.equal(guard.inFlight, true);
    assert.deepEqual(await guard.run(async () => { runs++; }), { started: false });
    assert.equal(runs, 1);
    release("done");
    assert.deepEqual(await first, { started: true, value: "done" });
    assert.equal(guard.inFlight, false);

    await assert.rejects(guard.run(async () => { throw new Error("boom"); }), /boom/);
    assert.equal(guard.inFlight, false);
    assert.deepEqual(await guard.run(async () => 7), { started: true, value: 7 });
});

test("live and dump controllers guard thread-statics and report busy state", () => {
    const source = fs.readFileSync(path.join(__dirname, "..", "src", "extension.ts"), "utf8");
    assert.equal((source.match(/new SingleFlightGuard\(\)/g) || []).length, 2);
    assert.equal((source.match(/this\.threadStaticsGuard\.run\(/g) || []).length, 2);
    assert.equal((source.match(/type: "threadStaticsState", state: "running"/g) || []).length, 2);
    assert.equal((source.match(/type: "threadStaticsState", state: "idle"/g) || []).length, 2);
    assert.equal((source.match(/message\.type === 'threadStaticsState'/g) || []).length, 2);
});

test("extension plumbs includeRetainedExceptions into live and dump capture frames and queries", () => {
    const source = fs.readFileSync(path.join(__dirname, "..", "src", "extension.ts"), "utf8");
    assert.match(source, /label: "Retained exceptions"[^\n]*key: "includeRetainedExceptions"/);
    assert.match(source, /includeRetainedExceptions: queryOptIns\.includeRetainedExceptions/);
    assert.match(source, /includeRetainedExceptions: this\.heapQueryOptIns\.includeRetainedExceptions/);
    assert.equal((source.match(/views\.push\("retained-exceptions"\)/g) || []).length, 2);
    assert.match(source, /includeRetainedExceptions: false \}/, "gcdump source must skip the opt-in");
});

test("thread-statics is not auto-queried and is routed through sendQuery with typeFilter", () => {
    const source = fs.readFileSync(path.join(__dirname, "..", "src", "extension.ts"), "utf8");
    const views = /const THREAD_QUERY_VIEWS = \[([^\]]*)\]/.exec(source);
    assert.ok(views);
    assert.doesNotMatch(views[1], /thread-statics/);
    assert.equal((source.match(/sendQuery\((snapshot\.session|child), (snapshot\.handle|handle), view, typeFilter\)/g) || []).length, 2);
    assert.equal((source.match(/message\.type === "queryThreadStatics"|"queryThreadStatics"/g) || []).length >= 2, true);
    assert.equal((source.match(/parseTypeFilter\(message\.typeFilter\)/g) || []).length, 2);
});
