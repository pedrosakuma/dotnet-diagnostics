const assert = require("node:assert/strict");
const test = require("node:test");
const path = require("node:path");
const Module = require("node:module");

// Same vscode-stub redirect as webview-heap-capture.test.cjs: out/extension.js requires "vscode"
// only because it imports the namespace type, so this stub lets the test load the real compiled
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

// Evaluates the real, extracted renderThreadCapture implementation (plus its MAX_THREAD_ROWS
// constant and a minimal DOM stub) so this test exercises exactly what ships to the browser, not
// a hand-copied stand-in. Mirrors the extraction technique in webview-heap-capture.test.cjs.
function loadRenderThreadCapture(script) {
    const maxThreadRowsMatch = /const MAX_THREAD_ROWS = \d+;/.exec(script);
    assert.ok(maxThreadRowsMatch, "expected to find MAX_THREAD_ROWS in the webview script");
    const renderThreadCaptureMatch = /function renderThreadCapture\(result\) \{[\s\S]*?\n    \}/.exec(script);
    assert.ok(renderThreadCaptureMatch, "expected to find renderThreadCapture in the webview script");

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

    const captureThreadResultElement = makeElementStub();
    const captureThreadHeadline = makeElementStub();
    const captureThreadBody = makeElementStub();

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
    // webview-heap-capture.test.cjs.
    const renderThreadCapture = new Function(
        "document", "captureThreadResultElement", "captureThreadHeadline", "captureThreadBody",
        `${maxThreadRowsMatch[0]}
         ${renderThreadCaptureMatch[0]}
         return renderThreadCapture;`,
    )(documentStub, captureThreadResultElement, captureThreadHeadline, captureThreadBody);

    return { renderThreadCapture, captureThreadResultElement, captureThreadHeadline, captureThreadBody };
}

test("renderThreadCapture formats a thread snapshot's headline and per-thread table", () => {
    const script = extractInlineScript(renderHtml(target, "nonce123", 120));
    const { renderThreadCapture, captureThreadResultElement, captureThreadHeadline, captureThreadBody } =
        loadRenderThreadCapture(script);

    renderThreadCapture({
        summary: "Captured a thread snapshot for PID 4242.",
        data: {
            processId: 4242,
            origin: "live",
            totalThreads: 2,
            omittedThreads: 0,
            totalLocks: 1,
            omittedLocks: 0,
            threads: [
                {
                    managedThreadId: 1,
                    osThreadId: 1001,
                    state: "Running",
                    isAlive: true,
                    isBackground: false,
                    isGc: false,
                    isThreadpoolWorker: false,
                    lockCount: 1,
                    currentExceptionType: null,
                    isLikelyBlocked: false,
                    inferredWaitReason: null,
                    frames: ["Sample.Program.Main()", "Sample.Program.DoWork()"],
                },
                {
                    managedThreadId: 2,
                    osThreadId: 1002,
                    state: "Wait",
                    isAlive: true,
                    isBackground: true,
                    isGc: false,
                    isThreadpoolWorker: true,
                    lockCount: 0,
                    currentExceptionType: null,
                    isLikelyBlocked: true,
                    inferredWaitReason: "Monitor.Enter",
                    frames: ["System.Threading.Monitor.Enter()"],
                },
            ],
            locks: [
                { objectTypeFullName: "System.Object", ownerManagedThreadId: 1, waitingThreadCount: 1, isContended: true },
            ],
        },
    });

    assert.equal(captureThreadResultElement.classList !== undefined, true);
    assert.match(captureThreadHeadline.textContent, /PID 4242/);
    assert.match(captureThreadHeadline.textContent, /2 thread\(s\)/);
    assert.match(captureThreadHeadline.textContent, /1 lock\(s\)/);
    assert.equal(captureThreadBody._rows.length, 2);
    assert.deepEqual(
        captureThreadBody._rows[0]._cells.map(cell => cell.textContent),
        ["1", "Running", "", "Sample.Program.Main()"],
    );
    assert.deepEqual(
        captureThreadBody._rows[1]._cells.map(cell => cell.textContent),
        ["2", "Wait (blocked)", "Monitor.Enter", "System.Threading.Monitor.Enter()"],
    );
});

test("renderThreadCapture flags omitted threads in the headline", () => {
    const script = extractInlineScript(renderHtml(target, "nonce123", 120));
    const { renderThreadCapture, captureThreadHeadline } = loadRenderThreadCapture(script);

    renderThreadCapture({
        summary: "Captured a thread snapshot for PID 4242.",
        data: {
            processId: 4242,
            origin: "live",
            totalThreads: 12,
            omittedThreads: 4,
            totalLocks: 0,
            omittedLocks: 0,
            threads: [],
            locks: [],
        },
    });

    assert.match(captureThreadHeadline.textContent, /12 thread\(s\)/);
    assert.match(captureThreadHeadline.textContent, /4 thread\(s\) omitted inline/);
});
