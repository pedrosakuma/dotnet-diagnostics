const assert = require("node:assert/strict");
const test = require("node:test");
const path = require("node:path");
const Module = require("node:module");

// Same vscode-stub redirect as webview-heap-capture.test.cjs: out/extension.js requires "vscode"
// only because it imports the namespace type, so this stub lets the test load the real compiled
// renderDumpAnalysisHtml() without a VS Code host process.
const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    if (request === "vscode") {
        return path.join(__dirname, "stubs", "vscode-stub.js");
    }
    return originalResolveFilename.call(this, request, ...rest);
};

const { renderDumpAnalysisHtml } = require("../out/extension.js");

function extractInlineScript(html) {
    const match = /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html);
    assert.ok(match, "renderDumpAnalysisHtml output must contain an inline <script> block");
    return match[1];
}

function makeElementStub() {
    const rows = [];
    return {
        classList: { remove() {}, add() {} },
        set textContent(value) { this._textContent = value; },
        get textContent() { return this._textContent; },
        set innerHTML(_value) { rows.length = 0; },
        appendChild(row) { rows.push(row); },
        get _rows() { return rows; },
    };
}

function makeDocumentStub() {
    return {
        createElement(tagName) {
            if (tagName === "tr") {
                const cells = [];
                return { tagName: "tr", appendChild(cell) { cells.push(cell); }, get _cells() { return cells; } };
            }
            return { tagName, set textContent(value) { this._textContent = value; }, get textContent() { return this._textContent; } };
        },
    };
}

// Evaluates the real, extracted renderDumpHeapCapture implementation (plus a minimal DOM stub) so
// this test exercises exactly what ships to the browser, not a hand-copied stand-in. Mirrors the
// extraction technique in webview-heap-capture.test.cjs.
function loadRenderDumpHeapCapture(script) {
    const renderDumpHeapCaptureMatch = /function renderDumpHeapCapture\(result\) \{[\s\S]*?\n    \}/.exec(script);
    assert.ok(renderDumpHeapCaptureMatch, "expected to find renderDumpHeapCapture in the webview script");

    const captureHeapResultElement = makeElementStub();
    const captureHeapHeadline = makeElementStub();
    const captureHeapBody = makeElementStub();
    const documentStub = makeDocumentStub();

    // eslint-disable-next-line no-new-func -- intentionally evaluating the real, extracted webview
    // source rather than a hand-copied stand-in; see the matching comment in
    // webview-heap-capture.test.cjs.
    const renderDumpHeapCapture = new Function(
        "document", "captureHeapResultElement", "captureHeapHeadline", "captureHeapBody",
        `${renderDumpHeapCaptureMatch[0]}
         return renderDumpHeapCapture;`,
    )(documentStub, captureHeapResultElement, captureHeapHeadline, captureHeapBody);

    return { renderDumpHeapCapture, captureHeapResultElement, captureHeapHeadline, captureHeapBody };
}

// Same extraction technique for renderDumpThreadCapture, mirroring webview-thread-capture.test.cjs.
function loadRenderDumpThreadCapture(script) {
    const maxThreadRowsMatch = /const MAX_THREAD_ROWS = \d+;/.exec(script);
    assert.ok(maxThreadRowsMatch, "expected to find MAX_THREAD_ROWS in the webview script");
    const renderDumpThreadCaptureMatch = /function renderDumpThreadCapture\(result\) \{[\s\S]*?\n    \}/.exec(script);
    assert.ok(renderDumpThreadCaptureMatch, "expected to find renderDumpThreadCapture in the webview script");

    const captureThreadResultElement = makeElementStub();
    const captureThreadHeadline = makeElementStub();
    const captureThreadBody = makeElementStub();
    const documentStub = makeDocumentStub();

    // eslint-disable-next-line no-new-func -- intentionally evaluating the real, extracted webview
    // source rather than a hand-copied stand-in; see the matching comment in
    // webview-heap-capture.test.cjs.
    const renderDumpThreadCapture = new Function(
        "document", "captureThreadResultElement", "captureThreadHeadline", "captureThreadBody",
        `${maxThreadRowsMatch[0]}
         ${renderDumpThreadCaptureMatch[0]}
         return renderDumpThreadCapture;`,
    )(documentStub, captureThreadResultElement, captureThreadHeadline, captureThreadBody);

    return { renderDumpThreadCapture, captureThreadResultElement, captureThreadHeadline, captureThreadBody };
}

test("renderDumpAnalysisHtml titles the page after the dump file name without a counters/start-stop chrome", () => {
    const html = renderDumpAnalysisHtml("crash-20240101.dmp", "nonce123");
    assert.match(html, /crash-20240101\.dmp/);
    assert.doesNotMatch(html, /id="start"/);
    assert.doesNotMatch(html, /id="metric"/);
});

test("renderDumpHeapCapture formats a dump-sourced snapshot's file size and top-types table", () => {
    const script = extractInlineScript(renderDumpAnalysisHtml("sample.dmp", "nonce123"));
    const { renderDumpHeapCapture, captureHeapHeadline, captureHeapBody } = loadRenderDumpHeapCapture(script);

    renderDumpHeapCapture({
        summary: "Captured a heap snapshot from dump 'sample.dmp'.",
        data: {
            filePath: "/tmp/sample.dmp",
            fileSizeBytes: 209_715_200,
            topTypesByBytes: [
                { typeFullName: "System.String", instanceCount: 10, totalBytes: 1024, totalBytesPercent: 42.5 },
                { typeFullName: "System.Byte[]", instanceCount: 3, totalBytes: 512, totalBytesPercent: 21.3 },
            ],
            topTypesByInstances: [],
        },
    });

    assert.match(captureHeapHeadline.textContent, /200\.0 MB/);
    assert.equal(captureHeapBody._rows.length, 2);
    assert.deepEqual(
        captureHeapBody._rows[0]._cells.map(cell => cell.textContent),
        ["System.String", "10", "1024"],
    );
});

test("renderDumpHeapCapture surfaces warning counts and tolerates a missing data payload", () => {
    const script = extractInlineScript(renderDumpAnalysisHtml("sample.dmp", "nonce123"));
    const { renderDumpHeapCapture, captureHeapHeadline, captureHeapBody } = loadRenderDumpHeapCapture(script);

    renderDumpHeapCapture({
        summary: "Captured a heap snapshot from dump 'sample.dmp'.",
        data: {
            filePath: "/tmp/sample.dmp",
            fileSizeBytes: 1024,
            topTypesByBytes: [],
            topTypesByInstances: [],
            warnings: ["Symbol resolution unavailable for module 'native.so'."],
        },
    });
    assert.match(captureHeapHeadline.textContent, /1 warning/);

    renderDumpHeapCapture({ summary: "Dump file not found." });
    assert.equal(captureHeapBody._rows.length, 0);
});

test("renderDumpThreadCapture formats a dump-origin thread snapshot's headline and per-thread table", () => {
    const script = extractInlineScript(renderDumpAnalysisHtml("sample.dmp", "nonce123"));
    const { renderDumpThreadCapture, captureThreadHeadline, captureThreadBody } = loadRenderDumpThreadCapture(script);

    renderDumpThreadCapture({
        summary: "Captured a thread snapshot from dump 'sample.dmp'.",
        data: {
            origin: "dump",
            totalThreads: 2,
            omittedThreads: 0,
            totalLocks: 1,
            omittedLocks: 0,
            threads: [
                {
                    managedThreadId: 1,
                    state: "Blocked",
                    isLikelyBlocked: true,
                    inferredWaitReason: "Monitor",
                    frames: ["System.Threading.Monitor.Enter", "Sample.Program.Main"],
                },
                {
                    managedThreadId: 2,
                    state: "Running",
                    isLikelyBlocked: false,
                    inferredWaitReason: null,
                    frames: [],
                },
            ],
        },
    });

    assert.match(captureThreadHeadline.textContent, /2 thread\(s\)/);
    assert.match(captureThreadHeadline.textContent, /1 lock\(s\)/);
    assert.equal(captureThreadBody._rows.length, 2);
    assert.deepEqual(
        captureThreadBody._rows[0]._cells.map(cell => cell.textContent),
        ["1", "Blocked (blocked)", "Monitor", "System.Threading.Monitor.Enter"],
    );
});
