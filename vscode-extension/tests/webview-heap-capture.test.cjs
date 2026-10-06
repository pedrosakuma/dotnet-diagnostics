const assert = require("node:assert/strict");
const test = require("node:test");
const path = require("node:path");
const Module = require("node:module");

// Same vscode-stub redirect as webview-gc-pause.test.cjs: out/extension.js requires "vscode" only
// because it imports the namespace type, so this stub lets the test load the real compiled
// renderHtml() without a VS Code host process.
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

// Evaluates the real, extracted renderHeapCapture implementation (plus its parseTimeSpanToMs
// dependency and a minimal DOM stub) so this test exercises exactly what ships to the browser,
// not a hand-copied stand-in. Mirrors the extraction technique in webview-gc-pause.test.cjs.
function loadRenderHeapCapture(script) {
    const parseTimeSpanToMsMatch = /function parseTimeSpanToMs\(value\) \{[\s\S]*?\n    \}/.exec(script);
    assert.ok(parseTimeSpanToMsMatch, "expected to find parseTimeSpanToMs in the webview script");
    const renderHeapCaptureMatch = /function renderHeapCapture\(source, result\) \{[\s\S]*?\n    \}/.exec(script);
    assert.ok(renderHeapCaptureMatch, "expected to find renderHeapCapture in the webview script");

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

    const captureHeapResultElement = makeElementStub();
    const captureHeapHeadline = makeElementStub();
    const captureHeapBody = makeElementStub();

    const documentStub = {
        createElement(tagName) {
            if (tagName === "tr") {
                const cells = [];
                return { tagName: "tr", appendChild(cell) { cells.push(cell); }, get _cells() { return cells; } };
            }
            return { tagName, set textContent(value) { this._textContent = value; }, get textContent() { return this._textContent; } };
        },
    };

    // eslint-disable-next-line no-new-func -- intentionally evaluating the real, extracted webview
    // source rather than a hand-copied stand-in; see the matching comment in
    // webview-gc-pause.test.cjs.
    const renderHeapCapture = new Function(
        "document", "captureHeapResultElement", "captureHeapHeadline", "captureHeapBody",
        `${parseTimeSpanToMsMatch[0]}
         ${renderHeapCaptureMatch[0]}
         return renderHeapCapture;`,
    )(documentStub, captureHeapResultElement, captureHeapHeadline, captureHeapBody);

    return { renderHeapCapture, captureHeapResultElement, captureHeapHeadline, captureHeapBody };
}

test("renderHeapCapture formats a live snapshot's suspend duration and top-types table", () => {
    const script = extractInlineScript(renderHtml(target, "nonce123", 120));
    const { renderHeapCapture, captureHeapResultElement, captureHeapHeadline, captureHeapBody } =
        loadRenderHeapCapture(script);

    renderHeapCapture("live", {
        summary: "Captured a live heap snapshot for PID 4242.",
        data: {
            processId: 4242,
            suspendDuration: "00:00:00.0123456",
            topTypesByBytes: [
                { typeFullName: "System.String", instanceCount: 10, totalBytes: 1024, totalBytesPercent: 42.5 },
                { typeFullName: "System.Byte[]", instanceCount: 3, totalBytes: 512, totalBytesPercent: 21.3 },
            ],
            topTypesByInstances: [],
        },
    });

    assert.equal(captureHeapResultElement.classList !== undefined, true);
    assert.match(captureHeapHeadline.textContent, /PID 4242/);
    assert.match(captureHeapHeadline.textContent, /suspended 12\.3 ms/);
    assert.equal(captureHeapBody._rows.length, 2);
    assert.deepEqual(
        captureHeapBody._rows[0]._cells.map(cell => cell.textContent),
        ["System.String", "10", "1024"],
    );
});

test("renderHeapCapture surfaces concerning gcdump completion flags", () => {
    const script = extractInlineScript(renderHtml(target, "nonce123", 120));
    const { renderHeapCapture, captureHeapHeadline } = loadRenderHeapCapture(script);

    renderHeapCapture("gcdump", {
        summary: "Captured a gcdump heap snapshot for PID 4242.",
        data: {
            processId: 4242,
            suspendDuration: "00:00:00",
            topTypesByBytes: [],
            topTypesByInstances: [],
            warnings: ["Reader failed before the trace fully drained."],
            gcDumpStatus: {
                gcStopObserved: true,
                eventStreamCompleted: false,
                timedOut: false,
                readerFailed: true,
                traceExportRequested: false,
                traceExportCompleted: false,
            },
        },
    });

    assert.match(captureHeapHeadline.textContent, /reader failed/);
    assert.match(captureHeapHeadline.textContent, /1 warning/);
});
