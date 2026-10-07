const assert = require("node:assert/strict");
const test = require("node:test");
const path = require("node:path");
const Module = require("node:module");

// Same vscode-stub redirect used by the other webview-*.test.cjs files: out/extension.js requires
// "vscode" only for its namespace type, so this stub lets the test load the real compiled
// renderHtml()/renderDumpAnalysisHtml() without a VS Code host process.
const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    if (request === "vscode") {
        return path.join(__dirname, "stubs", "vscode-stub.js");
    }
    return originalResolveFilename.call(this, request, ...rest);
};

const { renderHtml, renderDumpAnalysisHtml } = require("../out/extension.js");

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

// Evaluates the real, extracted renderQueryView implementation (issue #1116) plus its
// QUERY_VIEW_LABELS dependency and a minimal DOM stub, so this test exercises exactly what ships
// to the browser. Mirrors the extraction technique used by the other webview-*.test.cjs files.
function loadRenderQueryView(script) {
    const labelsMatch = /const QUERY_VIEW_LABELS = \{[\s\S]*?\n    \};/.exec(script);
    assert.ok(labelsMatch, "expected to find QUERY_VIEW_LABELS in the webview script");
    const renderQueryViewMatch = /function renderQueryView\(containerId, view, resultOrMessage, isError\) \{[\s\S]*?\n    \}/.exec(script);
    assert.ok(renderQueryViewMatch, "expected to find renderQueryView in the webview script");

    function makeDetailsStub(id) {
        const classes = new Set();
        const children = [];
        return {
            id,
            className: "",
            open: false,
            classList: {
                add(name) { classes.add(name); },
                toggle(name, on) { if (on) classes.add(name); else classes.delete(name); },
                has(name) { return classes.has(name); },
            },
            appendChild(child) { children.push(child); },
            querySelector(selector) {
                const tag = selector.replace(/^\s+|\s+$/g, "");
                return children.find(child => child.tagName === tag);
            },
            get _classes() { return classes; },
        };
    }

    const elementsById = new Map();
    const containers = new Map();

    const documentStub = {
        getElementById(id) {
            if (containers.has(id)) {
                return containers.get(id);
            }
            return elementsById.get(id);
        },
        createElement(tagName) {
            if (tagName === "details") {
                return makeDetailsStub(undefined);
            }
            const element = {
                tagName,
                set textContent(value) { this._textContent = value; },
                get textContent() { return this._textContent; },
            };
            return element;
        },
    };

    function makeContainer(id) {
        const children = [];
        const container = {
            id,
            appendChild(child) {
                children.push(child);
                elementsById.set(child.id, child);
            },
            get _children() { return children; },
        };
        containers.set(id, container);
        return container;
    }

    // eslint-disable-next-line no-new-func -- intentionally evaluating the real, extracted webview
    // source rather than a hand-copied stand-in; see the matching comment in
    // webview-heap-capture.test.cjs.
    const renderQueryView = new Function(
        "document",
        `${labelsMatch[0]}
         ${renderQueryViewMatch[0]}
         return renderQueryView;`,
    )(documentStub);

    return { renderQueryView, makeContainer, elementsById };
}

test("renderHtml's renderQueryView renders a successful view result as a collapsible section", () => {
    const script = extractInlineScript(renderHtml(target, "nonce123", 120));
    const { renderQueryView, makeContainer } = loadRenderQueryView(script);
    const container = makeContainer("heapQueryViews");

    renderQueryView("heapQueryViews", "roots-by-kind", { roots: [{ kind: "stack", count: 3 }] }, false);

    assert.equal(container._children.length, 1);
    const details = container._children[0];
    assert.equal(details.id, "heapQueryViews-roots-by-kind");
    assert.ok(!details.classList.has("query-view-error"));
    const summary = details.querySelector("summary");
    const pre = details.querySelector("pre");
    assert.equal(summary.textContent, "GC roots by kind");
    assert.match(pre.textContent, /"kind": "stack"/);
});

test("renderHtml's renderQueryView surfaces a per-view error without throwing", () => {
    const script = extractInlineScript(renderHtml(target, "nonce123", 120));
    const { renderQueryView, makeContainer } = loadRenderQueryView(script);
    const container = makeContainer("heapQueryViews");

    renderQueryView("heapQueryViews", "static-fields", "View not captured: static fields were not requested.", true);

    const details = container._children[0];
    assert.ok(details.classList.has("query-view-error"));
    assert.match(details.querySelector("summary").textContent, /Static fields \(unavailable\)/);
    assert.match(details.querySelector("pre").textContent, /not requested/);
});

test("renderHtml's renderQueryView gives the deadlocks view distinct open-by-default prominence", () => {
    const script = extractInlineScript(renderHtml(target, "nonce123", 120));
    const { renderQueryView, makeContainer } = loadRenderQueryView(script);
    const container = makeContainer("threadQueryViews");

    renderQueryView("threadQueryViews", "deadlocks", { cycles: [] }, false);

    const details = container._children[0];
    assert.equal(details.open, true);
    assert.ok(details.classList.has("query-view-deadlocks"));
    assert.equal(details.querySelector("summary").textContent, "Deadlocks");
});

test("renderHtml's renderQueryView updates an existing section in place rather than duplicating it", () => {
    const script = extractInlineScript(renderHtml(target, "nonce123", 120));
    const { renderQueryView, makeContainer } = loadRenderQueryView(script);
    const container = makeContainer("heapQueryViews");

    renderQueryView("heapQueryViews", "timers", { timers: [] }, false);
    renderQueryView("heapQueryViews", "timers", { timers: [{ dueTime: "00:00:05" }] }, false);

    assert.equal(container._children.length, 1);
    assert.match(container._children[0].querySelector("pre").textContent, /dueTime/);
});

test("renderDumpAnalysisHtml also defines renderQueryView with the same labels and deadlock prominence", () => {
    const script = extractInlineScript(renderDumpAnalysisHtml("sample.dmp", "nonce123"));
    const { renderQueryView, makeContainer } = loadRenderQueryView(script);
    const container = makeContainer("threadQueryViews");

    renderQueryView("threadQueryViews", "deadlocks", { cycles: [{ threads: [1, 2] }] }, false);

    const details = container._children[0];
    assert.equal(details.open, true);
    assert.ok(details.classList.has("query-view-deadlocks"));
    assert.match(details.querySelector("pre").textContent, /"threads"/);
});
