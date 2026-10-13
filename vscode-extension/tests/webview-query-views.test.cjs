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

const { renderHtml, renderDumpAnalysisHtml } = require("../out/extension.js");
const { QUERY_VIEW_RENDERER_SCRIPT } = require("../out/queryViewRenderer.js");

const target = {
    processId: 4242,
    operatingSystem: "Linux",
    processArchitecture: "x64",
    runtimeVersion: "10.0.0",
    managedEntrypointAssemblyName: "Sample",
};

class ElementStub {
    constructor(tagName) {
        this.tagName = tagName;
        this.children = [];
        this.attributes = {};
        this.classes = new Set();
        this.classList = {
            add: name => this.classes.add(name),
            toggle: (name, enabled) => enabled ? this.classes.add(name) : this.classes.delete(name),
            contains: name => this.classes.has(name),
        };
        this._textContent = "";
        this.open = false;
    }

    set className(value) {
        this._className = value;
        this.classes = new Set(value.split(/\s+/).filter(Boolean));
    }

    get className() {
        return this._className || "";
    }

    set textContent(value) {
        this._textContent = String(value);
        this.children = [];
    }

    get textContent() {
        return this._textContent + this.children.map(child => child.textContent).join("");
    }

    append(...children) {
        for (const child of children) this.appendChild(child);
    }

    appendChild(child) {
        this.children.push(child);
        return child;
    }

    replaceChildren(...children) {
        this.children = [];
        this._textContent = "";
        this.append(...children);
    }

    querySelector(selector) {
        const matches = element => selector.startsWith(".")
            ? element.classes.has(selector.slice(1))
            : element.tagName === selector;
        for (const child of this.children) {
            if (matches(child)) return child;
            const match = child.querySelector(selector);
            if (match) return match;
        }
        return null;
    }
}

function createDocumentStub() {
    const containers = new Map();
    const document = {
        getElementById(id) {
            if (containers.has(id)) return containers.get(id);
            for (const container of containers.values()) {
                const element = findById(container, id);
                if (element) return element;
            }
            return null;
        },
        createElement(tagName) {
            return new ElementStub(tagName);
        },
    };

    function findById(element, id) {
        if (element.id === id) return element;
        for (const child of element.children) {
            const found = findById(child, id);
            if (found) return found;
        }
        return null;
    }

    function makeContainer(id) {
        const element = new ElementStub("div");
        element.id = id;
        containers.set(id, element);
        return element;
    }

    return { document, makeContainer };
}

function loadRenderer() {
    const { document, makeContainer } = createDocumentStub();
    // eslint-disable-next-line no-new-func -- exercises the exact script emitted into the webview.
    const renderQueryView = new Function(
        "document",
        `${QUERY_VIEW_RENDERER_SCRIPT}\nreturn renderQueryView;`,
    )(document);
    return { renderQueryView, makeContainer };
}

function extractInlineScript(html) {
    const match = /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html);
    assert.ok(match, "output must contain an inline <script> block");
    return match[1];
}

test("live and dump webviews include the specialized renderer and styles", () => {
    for (const html of [
        renderHtml(target, "nonce123", 120),
        renderDumpAnalysisHtml("sample.dmp", "nonce123"),
    ]) {
        const script = extractInlineScript(html);
        assert.match(script, /function renderQueryView/);
        assert.match(html, /query-view-metrics/);
        assert.doesNotMatch(script, /JSON\.stringify\(resultOrMessage/);
    }
});

test("roots-by-kind renders human-readable metrics and a category table", () => {
    const { renderQueryView, makeContainer } = loadRenderer();
    const container = makeContainer("heapQueryViews");

    renderQueryView("heapQueryViews", "roots-by-kind", {
        origin: "live",
        processId: 4242,
        rootsByKind: [{
            rootKind: "Stack",
            rootCount: 3,
            distinctTargetObjects: 2,
            directlyReferencedBytes: 2048,
            pinnedRootCount: 1,
            interiorRootCount: 0,
        }],
    }, false);

    const details = container.children[0];
    assert.equal(details.id, "heapQueryViews-roots-by-kind");
    assert.equal(details.querySelector("summary").textContent, "GC roots by kind");
    assert.match(details.textContent, /Root records3/);
    assert.match(details.textContent, /Stack/);
    assert.match(details.textContent, /2 KB/);
    assert.match(details.textContent, /PID 4242/);
});

test("per-view errors are accessible and do not retain stale results", () => {
    const { renderQueryView, makeContainer } = loadRenderer();
    const container = makeContainer("heapQueryViews");

    renderQueryView("heapQueryViews", "static-fields", {
        staticFields: [{ fieldName: "Cache" }],
    }, false);
    renderQueryView("heapQueryViews", "static-fields", "View was not captured.", true);

    const details = container.children[0];
    assert.equal(container.children.length, 1);
    assert.ok(details.classList.contains("query-view-error"));
    assert.equal(details.querySelector("summary").textContent, "Static fields (unavailable)");
    assert.match(details.textContent, /View was not captured/);
    assert.doesNotMatch(details.textContent, /Cache/);
});

test("deadlock candidates are expanded and retain caveats about snapshot inference", () => {
    const { renderQueryView, makeContainer } = loadRenderer();
    const container = makeContainer("threadQueryViews");

    renderQueryView("threadQueryViews", "deadlocks", {
        deadlocks: [{
            classification: "inferred-deadlock-cycle-candidate",
            confidence: "medium",
            cycleMembers: [{ threadId: 11, osThreadId: 1101, state: "Wait", inferredWaitReason: "Monitor" }],
            lockChain: [{ waitingThreadId: 11, ownerThreadId: 12, lockObjectAddress: "0x1234" }],
            recommendedCommands: [{ command: "query_snapshot", purpose: "Inspect the captured graph" }],
        }],
    }, false);

    const details = container.children[0];
    assert.equal(details.open, true);
    assert.ok(details.classList.contains("query-view-deadlocks"));
    assert.match(details.textContent, /11/);
    assert.match(details.textContent, /validate the wait-for edges/);
});

test("unique stacks show frame display names and fractional shares as percentages", () => {
    const { renderQueryView, makeContainer } = loadRenderer();
    const container = makeContainer("threadQueryViews");

    renderQueryView("threadQueryViews", "unique-stacks", {
        uniqueStacks: [{
            threadCount: 2,
            threadPercentage: 0.5,
            sampleThreads: [],
            canonicalFrames: [{ displayName: "Sample.Run()" }, { displayName: "Sample.Main()" }],
        }],
    }, false);

    assert.match(container.textContent, /Sample\.Run\(\)\s+→\s+Sample\.Main\(\)/);
    assert.match(container.textContent, /50%/);
});

test("retained-exception samples are rendered without interpreting application text as markup", () => {
    const { renderQueryView, makeContainer } = loadRenderer();
    const container = makeContainer("heapQueryViews");
    const message = "<img src=x onerror=alert(1)>";

    renderQueryView("heapQueryViews", "retained-exceptions", {
        retainedExceptions: [{
            typeFullName: "System.InvalidOperationException",
            instanceCount: 1,
            totalBytes: 128,
            samples: [{ message, hResult: -2146233079, messageTruncated: true }],
        }],
    }, false);

    const details = container.children[0];
    assert.match(details.textContent, new RegExp(message.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
    assert.equal(details.querySelector("img"), null);
    assert.match(details.textContent, /truncated/);
    assert.match(details.textContent, /0x80131509/i);
});

test("unknown views use a compact key/value fallback and omit opaque handles", () => {
    const { renderQueryView, makeContainer } = loadRenderer();
    const container = makeContainer("heapQueryViews");

    renderQueryView("heapQueryViews", "future-view", { handle: "opaque-secret", count: 4 }, false);

    assert.equal(container.children[0].querySelector("summary").textContent, "future-view");
    assert.match(container.textContent, /count4/);
    assert.doesNotMatch(container.textContent, /opaque-secret/);
});

test("address values remain exact hex strings and unsafe JavaScript numbers are not reformatted", () => {
    const { renderQueryView, makeContainer } = loadRenderer();
    const container = makeContainer("heapQueryViews");
    const exactAddress = "0xfedcba9876543210";

    renderQueryView("heapQueryViews", "static-fields", {
        staticFields: [{ containingTypeFullName: "Sample.Cache", fieldName: "Current", valueAddress: exactAddress }],
    }, false);
    assert.match(container.textContent, new RegExp(exactAddress));

    renderQueryView("heapQueryViews", "static-fields", {
        staticFields: [{ containingTypeFullName: "Sample.Cache", fieldName: "Current", valueAddress: 0xfedcba9876543210 }],
    }, false);
    assert.match(container.textContent, /Unavailable: imprecise JavaScript integer/);
});
