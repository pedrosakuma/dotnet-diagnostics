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
const { CPU_PROFILE_SCRIPT } = require("../out/cpuProfileViewer.js");

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
        this.style = {};
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

    setAttribute(name, value) {
        this.attributes[name] = String(value);
    }

    getAttribute(name) {
        return this.attributes[name] ?? null;
    }

    addEventListener(type, listener) {
        (this.listeners ??= {})[type] = listener;
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
        const matches = element => selector.split(",").some(part => {
            const match = part.trim();
            return match.startsWith(".") ? element.classes.has(match.slice(1))
                : match === "[tabindex]" ? element.attributes.tabindex !== undefined
                : element.tagName === match;
        });
        for (const child of this.children) {
            if (matches(child)) return child;
            const match = child.querySelector(selector);
            if (match) return match;
        }
        return null;
    }

    focus() {
        this.focused = true;
    }
}

require("./stubs/navigation-dom.cjs").extendDom(ElementStub);

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
        createElementNS(_namespace, tagName) {
            return new ElementStub(tagName);
        },
        querySelectorAll(selector) {
            const attribute = /^\[([^\]]+)\]$/.exec(selector)?.[1];
            if (!attribute) return [];
            const all = element => [element, ...element.children.flatMap(all)];
            return [...containers.values()].flatMap(all)
                .filter(element => Object.prototype.hasOwnProperty.call(element.attributes, attribute));
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
    const api = new Function(
        "document",
        `${QUERY_VIEW_RENDERER_SCRIPT}\nreturn { renderQueryView, enhanceEvidenceTable, resetEvidenceTable,
            renderHeapTypeNavigation, updateThreadNavigation, renderExceptionNavigation };`,
    )(document);
    return { ...api, makeContainer, document };
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
        assert.doesNotThrow(() => new Function(script), "the complete emitted webview script must parse");
        assert.match(script, /function renderQueryView/);
        assert.match(html, /query-view-metrics/);
        assert.doesNotMatch(script, /JSON\.stringify\(resultOrMessage/);
    }
});

test("live panel exposes the compact evidence workspace and keeps other result routes", () => {
    const html = renderHtml(target, "nonce123", 120);
    const script = extractInlineScript(html);
    for (const [label, id] of [
        ["Live", "Live"], ["Workspace", "Workspace"], ["Exceptions", "Exceptions"],
        ["Diagnostics", "Diagnostics"], ["Threads", "Threads"],
    ]) {
        assert.match(html, new RegExp(`role="tab"[^>]*>${label}<`));
        assert.match(html, new RegExp(`role="tabpanel" aria-labelledby="resultTab${id}"`));
    }
    assert.match(html, /<select id="workspaceDomain">[\s\S]*?Memory &amp; GC/);
    assert.match(html, /id="workspaceEvidenceKind"/);
    assert.match(html, /id="workspaceView"/);
    assert.match(html, /id="workspaceCanvas"/);
    assert.match(html, /id="captureGc" type="button">Capture GC evidence…/);
    assert.match(html, /id="cpuSamplingDisclaimer"/);
    assert.match(html, /<section id="resultCpu"[^>]*data-export-kind="cpu"[\s\S]*?<button id="captureCpu" type="button">/);
    assert.match(html, /<section id="resultHeap"[\s\S]*?<button id="captureHeap" type="button">/);
    assert.match(script, /function selectResultTab\(selected\)/);
    assert.match(script, /event\.key === 'ArrowRight'/);
    assert.match(script, /function selectProfileView\(kind, view, rootMethodFilter\)/);
    assert.match(script, /Selecting it did not query or read the target/);
    assert.match(script, /function loadSelectedProfileView\(\)/);
    assert.match(script, /workspaceGcStatus\.textContent = 'Replaced: shared Diagnostics slot now contains '/);
    assert.match(script, /evidenceId,\s*view,/);
    assert.doesNotMatch(script, /vscode\.postMessage\(\{ type: 'queryCpu', view \}\)/);
    assert.match(script, /evidenceId: allocation \? allocationEvidenceId : cpuEvidenceId/);
    assert.match(html, /\.cpu-query-table \.cpu-query-number \{ width:6\.5rem; \}/);
    assert.match(html, /\.cpu-query-table \.cpu-query-action \{ width:9rem; \}/);
    assert.match(html, /\.query-view-table\.cpu-query-table \{ min-width:48rem; table-layout:fixed; \}/);
    assert.match(html, /\.cpu-query-table \.cpu-query-symbol span, \.cpu-query-table \.cpu-query-module span \{ display:inline-block; white-space:nowrap; overflow-wrap:normal; \}/);
    assert.match(html, /\.cpu-query-summary-table \.cpu-query-category \{ width:56%; overflow-wrap:anywhere; word-break:normal; \}/);
    assert.match(script, /message\.type === 'query' && message\.kind === 'cpu'/);
    assert.match(script, /renderCpuQuery\(message\.view, message\.result, false\)/);
});

test("domain return restores CPU view and filter without a request; new captures discard that selection and cache", () => {
    const script = extractInlineScript(renderHtml(target, "nonce", 120));
    const navigation = script.slice(script.indexOf("let selectedWorkspaceEvidence ="),
        script.indexOf("function selectResultTab(selected)"));
    const profile = script.slice(script.indexOf("const profileAvailability ="),
        script.indexOf("const counters = new Map()"));
    const captureStart = script.indexOf("cpuEvidenceId = typeof message.evidenceId");
    const capture = script.slice(captureStart, script.indexOf("setWorkspaceEvidence('cpu');", captureStart)
        + "setWorkspaceEvidence('cpu');".length);
    const { document, makeContainer } = createDocumentStub();
    const names = ["workspaceDomain", "workspaceEvidenceKind", "workspaceEvidenceStatus", "workspaceView",
        "workspaceLoadView", "workspaceGcCapture", "cpuQueryViews", "cpuQueryStatus",
        "openHeapDetails", "backToHeapTypes", "captureGc", "resultCpu", "cpuSamplingDisclaimer"];
    const nodes = Object.fromEntries(names.map(id => [id, makeContainer(id)]));
    const requests = [], renders = [];
    const api = new Function("document", "vscode", "renders", `
      const {${names.join(",")}} = Object.fromEntries(${JSON.stringify(names)}.map(id=>[id,document.getElementById(id)]));
      const workspacePanels = {};
      const cpuQueryLabels = {'caller-callee':'Callers / callees'};
      const cpuProfileState = {}, allocationProfileState = {};
      let cpuProfileResult;
      const renderCapture = () => {};
      const renderCpuQuery = (...args) => renders.push(args);
      ${navigation}
      ${profile}
      return {setWorkspaceEvidence,selectProfileView,cacheProfileQuery,loadSelectedProfileView,
        setAvailable:()=>{profileAvailability.cpu=true},
        capture:message=>{${capture}}};
    `)(document, {postMessage:m=>requests.push(m)}, renders);
    api.capture({evidenceId:"cpu-1",summary:{totalSamples:100}});
    api.setAvailable();
    api.cacheProfileQuery("cpu","call-tree","Repeat",{data:{root:"cached exact projection"}});
    api.selectProfileView("cpu","call-tree","Repeat");
    api.setWorkspaceEvidence("heap");
    api.setWorkspaceEvidence("cpu");
    assert.equal(nodes.workspaceView.value,"call-tree");
    assert.equal(nodes.workspaceLoadView.hidden,true);
    assert.equal(renders.at(-1)[1].data.root,"cached exact projection");
    assert.deepEqual(requests,[]);
    api.selectProfileView("cpu","top-methods");
    api.setWorkspaceEvidence("heap"); api.setWorkspaceEvidence("cpu");
    assert.equal(nodes.workspaceView.value,"top-methods");
    assert.equal(nodes.workspaceLoadView.hidden,false);
    assert.deepEqual(requests,[],"even restored unloaded navigation must remain inert");
    api.loadSelectedProfileView();
    assert.deepEqual(requests,[{type:"queryCpu",evidenceId:"cpu-1",view:"top-methods"}]);
    api.capture({evidenceId:"cpu-2",summary:{totalSamples:2}});
    assert.equal(nodes.workspaceView.value,"triage");
    api.selectProfileView("cpu","call-tree","Repeat");
    assert.equal(nodes.workspaceLoadView.hidden,false,"old projection cannot become current under new evidence");
});

function cpuRenderer() {
    const html = renderHtml(target, "nonce123", 120);
    const script = extractInlineScript(html);
    const start = script.indexOf("const cpuQueryLabels = {");
    const end = script.indexOf("const profileAvailability = {", start);
    assert.ok(start >= 0 && end > start, "CPU renderer source should be present");
    const source = script.slice(start, end);
    const { document, makeContainer } = createDocumentStub();
    const cpuQueryViews = makeContainer("cpuQueryViews");
    const allocationContainer = makeContainer("allocationQueryViews");
    const requests = [];
    // eslint-disable-next-line no-new-func -- tests the renderer emitted into the webview.
    const { renderCpuQuery, state } = new Function("document", "cpuQueryViews", "vscode",
        `${CPU_PROFILE_SCRIPT}
        let cpuProfileResult;
        const cpuProfileState = { mode: 'tree', search: '', focus: [], selected: [] };
        let allocationProfileResult;
        const allocationProfileState = { mode: 'tree', search: '', focus: [], selected: [] };
        let cpuEvidenceId;
        let allocationEvidenceId;
        const activeProfileViews = { cpu: 'triage', allocation: 'call-tree' };
        const activeProfileFilters = { cpu: undefined, allocation: undefined };
        const profileResultFilters = { cpu: undefined, allocation: undefined };
        ${source}
        return { renderCpuQuery, state: cpuProfileState };`)(
        document, cpuQueryViews, { postMessage: request => requests.push(request) });
    return {
        renderCpuQuery, state, container: cpuQueryViews, allocationContainer,
        requests,
    };
}

test("allocation explorer uses estimated bytes, separate profile state and allocation query messages", () => {
    const { renderCpuQuery, allocationContainer, container, requests } = cpuRenderer();
    const result = { weightUnit: "allocation-tick-estimated-bytes", data: { totalSamples: 1000,
        root: cpuNode("Root", 800, 200, [cpuNode("Allocate", 600, 600)]) } };
    renderCpuQuery("call-tree", result, false, "allocation");
    assert.match(allocationContainer.textContent, /estimated allocation bytes/);
    assert.match(allocationContainer.textContent, /800 of 1000 estimated bytes \(80.0%\)/);
    assert.doesNotMatch(allocationContainer.textContent, /heuristic-wait|on-CPU \//);
    click(allocationContainer, "Callers / callees");
    assert.equal(requests.at(-1).type, "queryAllocation");
    click(allocationContainer, "Flame graph (icicle)");
    assert.match(allocationContainer.textContent, /Self estimated bytes/);
    renderCpuQuery("top-methods", { data: { methods: [{ method: "Allocate", exclusiveSamples: 600 }] } }, false, "allocation");
    assert.match(allocationContainer.textContent, /ranked by estimated allocation bytes/);
    assert.doesNotMatch(allocationContainer.textContent, /sampled time|Self samples/);
    click(allocationContainer, "Back to allocation profile");
    assert.match(allocationContainer.textContent, /800 of 1000/);
    assert.equal(container.children.length, 0);
    renderCpuQuery("call-tree", { data: { totalSamples: 2, root: cpuNode("Cpu", 2, 2) } }, false);
    assert.match(container.textContent, /inclusive sample counts/);
    assert.match(allocationContainer.textContent, /800 of 1000/);
});

test("CPU and allocation profile tables preserve method symbols independently", () => {
    const { renderCpuQuery, container, allocationContainer } = cpuRenderer();
    renderCpuQuery("triage", { data: { topWaitCategories: [{
        waitReason: "ThreadPool worker idle wait", exclusiveSamples: 12, exclusivePercent: 42.9,
    }] } }, false);
    const waitTable = elements(container, element => element.tagName === "table"
        && element.className.includes("cpu-query-summary-table"))[0];
    assert.ok(waitTable);
    assert.equal(elements(waitTable, element => element.tagName === "td"
        && element.className === "cpu-query-category")[0].textContent, "ThreadPool worker idle wait");
    renderCpuQuery("top-methods", { data: { methods: [{
        method: "Example.Collections.Cache<System.String>.Get(System.Int32)",
        module: "Example.Application.dll",
        exclusiveSamples: 12,
        inclusiveSamples: 18,
        exclusivePercent: 66.7,
    }] } }, false);
    const table = elements(container, element => element.tagName === "table"
        && element.className.includes("cpu-query-table"))[0];
    assert.ok(table);
    const methodCell = elements(table, element => element.tagName === "td"
        && element.className === "cpu-query-symbol")[0];
    assert.equal(methodCell.textContent, "Example.Collections.Cache<System.String>.Get(System.Int32)");
    assert.ok(elements(methodCell, element => element.tagName === "wbr").length >= 5,
        "long method signatures should wrap at punctuation and identifier boundaries");
    assert.ok(elements(table, element => element.tagName === "td"
        && element.className === "cpu-query-number").every(cell => cell.textContent.length > 0));
    assert.ok(elements(table, element => element.tagName === "td"
        && element.className === "cpu-query-action")[0].querySelector("button"));
    renderCpuQuery("by-module", { data: {} }, false, "allocation");
    assert.match(allocationContainer.textContent, /Modules/);
    assert.match(container.textContent, /Top methods/);
});

function elements(root, predicate) {
    return root.children.flatMap(child => [
        ...(predicate(child) ? [child] : []), ...elements(child, predicate),
    ]);
}

function click(root, caption, index = 0) {
    const button = elements(root, child => child.tagName === "button" && child.textContent === caption)[index];
    assert.ok(button, `button ${caption} (${index}) must exist`);
    button.listeners.click({});
}

function cpuNode(method, inclusiveSamples, exclusiveSamples, children = []) {
    return { frame: { method, module: "App" }, inclusiveSamples, exclusiveSamples, children };
}

test("CPU call tree renders a semantic table with disclosures and selected-method queries", () => {
    const { renderCpuQuery, container, requests } = cpuRenderer();
    renderCpuQuery("call-tree", { data: { totalSamples: 20,
        root: cpuNode("App.Main()", 20, 2, [cpuNode("App.Work()", 18, 18)]),
    } }, false);

    const tree = container.querySelector(".cpu-call-tree");
    assert.ok(tree);
    assert.equal(tree.attributes.role, undefined, "semantic table does not pretend to implement an ARIA treegrid");
    assert.equal(tree.querySelector(".cpu-profile-disclosure").getAttribute("aria-expanded"),"true");
    assert.match(tree.textContent, /App\.Main\(\)/);
    assert.match(tree.textContent, /100.0%10.0%/);
    assert.match(tree.textContent, /App\.Work\(\)/);
    click(container, "Callers / callees");
    assert.deepEqual(requests, [{
        type: "queryCpu",
        evidenceId: undefined,
        view: "caller-callee",
        rootMethodFilter: "App.Main()",
    }]);
});

test("CPU flame widths preserve self and omitted samples instead of renormalizing children", () => {
    const { renderCpuQuery, container, requests } = cpuRenderer();
    renderCpuQuery("call-tree", { data: { totalSamples: 100, truncated: true,
        root: cpuNode("Root", 100, 10, [cpuNode("A", 60, 60), cpuNode("B", 20, 20)]),
    } }, false);
    click(container, "Flame graph (icicle)");
    const svg = container.querySelector("svg");
    assert.ok(svg);
    const rects = elements(svg, child => child.tagName === "rect");
    assert.equal(Number(rects[0].attributes.width), 1000);
    const atRow = rects.filter(rect => rect.attributes.y === "32");
    assert.deepEqual(atRow.map(rect => Number(rect.attributes.width)), [600, 200, 100, 100]);
    assert.deepEqual(atRow.map(rect => Number(rect.attributes.x)), [0, 600, 800, 900]);
    assert.match(svg.textContent, /Omitted \/ unattributed descendants: 10 samples/);
    assert.match(container.textContent, /Partial projection/);
    assert.match(container.textContent, /not elapsed time or measured CPU utilization/);
    assert.deepEqual(requests, []);
});

test("flame labels prioritize type/method names while tooltips preserve full signatures and metrics", () => {
    const { renderCpuQuery, container } = cpuRenderer();
    const signature = "System.Diagnostics.Tracing.EventPipeEventProvider.Callback(unsigned int8*,int32,class System.Object)";
    const generic = "Example.Collections.Cache<System.String>.Get(System.Int32)";
    renderCpuQuery("call-tree", { data: { totalSamples: 100,
        root: cpuNode(signature, 100, 0, [cpuNode(generic, 100, 100)]) } }, false);
    click(container, "Flame graph (icicle)");
    const svg = container.querySelector("svg");
    const labels = elements(svg, element => element.tagName === "text").map(element => element.textContent);
    assert.equal(labels[0], "EventPipeEventProvider.Callback  100.0%");
    assert.equal(labels[1], "Cache<System.String>.Get  100.0%");
    assert.equal(labels[2], "Self");
    const titles = elements(svg, element => element.tagName === "title").map(element => element.textContent);
    assert.ok(titles[0].includes(signature));
    assert.match(titles[0], /inclusive 100.*self 0 samples/);
    const rects = elements(svg, element => element.tagName === "rect");
    const originalColors = rects.map(rect => rect.attributes["data-color"]);
    click(container, "Call tree");
    click(container, "Flame graph (icicle)");
    assert.deepEqual(elements(container.querySelector("svg"), element => element.tagName === "rect")
        .map(rect => rect.attributes["data-color"]), originalColors);
    assert.match(container.textContent, /Colors distinguish methods, not severity/);
});

test("flame palette maintains readable text contrast independently of editor theme colors", () => {
    const { CPU_PROFILE_STYLES } = require("../out/cpuProfileViewer.js");
    const luminance = hex => {
        const rgb = hex.match(/[0-9a-f]{2}/gi).map(part => parseInt(part, 16) / 255)
            .map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
        return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
    };
    const text = luminance("18212b");
    const backgrounds = [...CPU_PROFILE_STYLES.matchAll(/fill:#([0-9a-f]{6})/g)]
        .map(match => match[1]).filter(color => color !== "18212b");
    assert.equal(backgrounds.length, 8);
    for (const color of backgrounds)
        assert.ok((luminance(color) + 0.05) / (text + 0.05) >= 7, color + " must meet 7:1 contrast");
    assert.match(CPU_PROFILE_STYLES, /min-width:1000px/);
    assert.match(CPU_PROFILE_STYLES, /vscode-high-contrast/);
    assert.match(CPU_PROFILE_STYLES, /forced-colors: active/);
});

test("CPU focus uses exact local paths across modes, preserves capture denominator, and supports breadcrumbs", () => {
    const { renderCpuQuery, container, state, requests } = cpuRenderer();
    renderCpuQuery("call-tree", { data: { totalSamples: 100,
        root: cpuNode("Root", 100, 0, [
            cpuNode("Repeat", 60, 10, [cpuNode("First", 50, 50)]),
            cpuNode("Repeat", 40, 0, [cpuNode("Second", 40, 40)]),
        ]),
    } }, false);
    click(container, "Focus", 3);
    assert.deepEqual(state.focus, [1]);
    assert.equal(elements(container, e => e.attributes["data-node-path"] === "1.0")[0].hidden, false,
        "focusing a collapsed branch opens its loaded children");
    assert.match(container.querySelector(".cpu-profile-selection").textContent, /40.0% of capture; 100.0% of focus/);
    click(container, "Flame graph (icicle)");
    assert.match(container.querySelector("svg").textContent, /Second/);
    assert.doesNotMatch(container.querySelector("svg").textContent, /First/);
    click(container, "Call tree");
    assert.match(container.querySelector(".cpu-call-tree").textContent, /Second/);
    assert.doesNotMatch(container.querySelector(".cpu-call-tree").textContent, /First/);
    click(container.querySelector("nav"), "Root");
    assert.deepEqual(state.focus, []);
    assert.match(container.querySelector(".cpu-call-tree").textContent, /First/);
    assert.deepEqual(requests, [], "all local focus and mode changes must avoid backend requests");
});

test("CPU search highlights without filtering counts, expands matches, and persists across modes", () => {
    const { renderCpuQuery, container, requests } = cpuRenderer();
    renderCpuQuery("call-tree", { data: { totalSamples: 20,
        root: cpuNode("Root", 20, 0, [cpuNode("A.Work", 15, 15), cpuNode("B.Wait", 5, 5)]),
    } }, false);
    const input = container.querySelector("input");
    input.value = "b.wAIT";
    input.listeners.input();
    assert.match(container.textContent, /1 matching loaded node/);
    assert.match(container.querySelector(".cpu-profile-match").textContent, /B.Wait/);
    assert.match(container.querySelector(".cpu-call-tree").textContent, /A.Work/);
    click(container, "Flame graph (icicle)");
    const matched = elements(container, element => element.attributes.class === "cpu-flame-match");
    assert.equal(matched.length, 1);
    assert.equal(Number(matched[0].attributes.width), 250);
    input.value = "App";
    input.listeners.input();
    assert.match(container.textContent, /3 matching loaded node/);
    assert.deepEqual(requests, []);
});

test("CPU flame keyboard focus, Escape, reset, and method queries are explicitly distinct", () => {
    const { renderCpuQuery, container, state, requests } = cpuRenderer();
    renderCpuQuery("call-tree", { data: { totalSamples: 10,
        root: cpuNode("Root", 10, 0, [cpuNode("Work", 10, 10)]),
    } }, false);
    click(container, "Flame graph (icicle)");
    const branch = elements(container, element => element.tagName === "g" && element.attributes.role === "button")[1];
    let prevented = false;
    branch.listeners.keydown({ key: "Enter", preventDefault() { prevented = true; } });
    assert.equal(prevented, true);
    assert.deepEqual(state.focus, [0]);
    assert.match(container.querySelector(".cpu-profile-selection").textContent, /Work/);
    assert.ok(elements(container, element => element.focused).length);
    click(container, "Query subtree by method");
    assert.deepEqual(requests.pop(), {
        type: "queryCpu", evidenceId: undefined, view: "call-tree", rootMethodFilter: "Work",
    });
    assert.match(container.textContent, /highest-ranked occurrence containing the method name/);
    const focused = elements(container, element => element.tagName === "g" && element.attributes.role === "button")[0];
    focused.listeners.keydown({ key: "Escape", preventDefault() {} });
    assert.deepEqual(state.focus, []);
    const branchAgain = elements(container, element => element.tagName === "g" && element.attributes.role === "button")[1];
    branchAgain.listeners.keydown({ key: " ", preventDefault() {} });
    click(container, "Reset focus");
    assert.deepEqual(state.focus, []);
    click(container, "Full capture tree");
    assert.deepEqual(requests, [{ type: "queryCpu", evidenceId: undefined, view: "call-tree" }]);
});

test("CPU caller/callee and query errors return to cached mode, search and exact local focus", () => {
    const { renderCpuQuery, container, state, requests } = cpuRenderer();
    const result = { data: { totalSamples: 10,
        root: cpuNode("Root", 10, 0, [cpuNode("Work", 10, 10)]),
    } };
    renderCpuQuery("call-tree", result, false);
    click(container, "Focus", 1);
    click(container, "Flame graph (icicle)");
    const input = container.querySelector("input");
    input.value = "Work";
    input.listeners.input();
    click(container, "Callers / callees");
    renderCpuQuery("caller-callee", { data: { callers: [], callees: [] } }, false);
    click(container, "Back to CPU profile");
    assert.deepEqual(state.focus, [0]);
    assert.equal(state.mode, "flame");
    assert.equal(container.querySelector("input").value, "Work");
    assert.ok(container.querySelector("svg"));
    renderCpuQuery("call-tree", "Query failed", true);
    click(container, "Back to CPU profile");
    assert.deepEqual(state.focus, [0]);
    assert.equal(requests.length, 1, "returning from other views or errors must not query again");
    renderCpuQuery("call-tree", { data: { totalSamples: 3, root: cpuNode("New", 3, 3) } }, false);
    assert.deepEqual(state.focus, [], "a different projection must not reuse old path indices");
});

test("disconnected CPU projections keep local exploration while redrawn query controls stay unavailable", () => {
    const { renderCpuQuery, container, requests } = cpuRenderer();
    renderCpuQuery("call-tree", { data: { totalSamples: 10,
        root: cpuNode("Root", 10, 0, [cpuNode("Work", 10, 10)]),
    } }, false);
    container.setAttribute("data-profile-query-available", "false");
    container.setAttribute("data-profile-query-reason", "Capture again for CLI queries.");
    click(container, "Flame graph (icicle)");
    assert.ok(container.querySelector("svg"), "switching cached presentation is not a CLI operation");
    click(container, "Focus selected branch");
    const queryButtons = elements(container, element =>
        element.textContent === "Callers / callees" || element.textContent === "Query subtree by method");
    assert.equal(queryButtons.length, 2);
    assert.ok(queryButtons.every(button => button.getAttribute("aria-disabled") === "true"));
    assert.ok(queryButtons.every(button => button.title === "Capture again for CLI queries."));
    click(container, "Query subtree by method");
    assert.deepEqual(requests, []);
    container.setAttribute("data-profile-query-available", "true");
    click(container, "Reset focus");
    click(container, "Query subtree by method");
    assert.equal(requests.length, 1, "reconnection restores newly rendered query controls");
});

test("selecting a cached tree after disconnect derives retention copy from its attached owner", () => {
    const {renderCpuQuery,container,requests}=cpuRenderer();
    container.setAttribute("data-profile-retained","false");
    container.setAttribute("data-profile-query-available","false");
    renderCpuQuery("call-tree",{summary:"The handle retains the full tree.",
        data:{totalSamples:10,root:cpuNode("Root",10,10)}},false);
    assert.match(container.textContent,/Cached loaded projection only/);
    assert.doesNotMatch(container.textContent,/handle retains the full tree/);
    click(container,"Query subtree by method");
    assert.deepEqual(requests,[]);
});

test("CPU tree-table aligns fixed capture shares and expands only the known loaded hotpath without losing manual paths", () => {
    const { renderCpuQuery, container, state, requests } = cpuRenderer();
    const result = {summary:"Partial tree. The handle retains the full tree.",data:{totalSamples:100,truncated:true,
        root:cpuNode("Root",100,0,[
            cpuNode("Repeat",60,10,[cpuNode("Loaded",30,30)]),
            cpuNode("Repeat",40,0,[cpuNode("Other",40,40)]),
        ])}};
    renderCpuQuery("call-tree",result,false);
    const table = container.querySelector(".cpu-call-tree");
    assert.deepEqual(elements(table,e=>e.tagName==="th").map(e=>e.textContent),["Method / call path","Total %","Self %"]);
    const rows = elements(table,e=>e.attributes["data-node-path"]!==undefined);
    const metrics = row=>row.children.slice(1).map(e=>e.textContent);
    assert.deepEqual(metrics(rows[1]),["60.0%","10.0%"]);
    assert.deepEqual(metrics(rows[3]),["40.0%","0.0%"]);
    assert.equal(rows[2].hidden,true);
    const toggle = rows[3].querySelector(".cpu-profile-disclosure");
    toggle.listeners.click();
    assert.equal(rows[4].hidden,false);
    assert.deepEqual(state.expanded,["","1"]);
    click(container,"Expand hotpath");
    const expandedRows = elements(container,e=>e.attributes["data-node-path"]!==undefined);
    assert.equal(expandedRows[2].hidden,false);
    assert.equal(expandedRows[4].hidden,false,"off-path manual expansion survives");
    assert.deepEqual(state.focus,[]);
    assert.deepEqual(state.selected,[]);
    assert.deepEqual(requests,[]);
    assert.match(container.textContent,/not a global diagnosis/);
    assert.doesNotMatch(container.textContent,/handle retains the full tree/);
    assert.match(container.textContent,/100 captured stack samples/);
    renderCpuQuery("call-tree",result,false);
    assert.ok(state.expanded.includes("1"),"cached redraw retains path-keyed expansion");
    renderCpuQuery("call-tree",{data:{totalSamples:100,truncated:true,
        root:cpuNode("Root",100,0,[cpuNode("Known minority",20,20)])}},false);
    click(container,"Expand hotpath");
    assert.deepEqual(state.expanded,[""],"a dominating omitted branch cannot be manufactured or expanded");
});

test("CPU profile enforces client node/depth bounds and reports omissions", () => {
    const { renderCpuQuery, container } = cpuRenderer();
    renderCpuQuery("call-tree", { data: { totalSamples: 100,
        root: cpuNode("Root", 100, 0, Array.from({ length: 100 }, (_, i) => cpuNode("Branch" + i, 1, 1))),
    } }, false);
    assert.equal(elements(container, child => child.className === "cpu-profile-method").length, 64);
    assert.match(container.textContent, /Partial projection/);
    let chain = cpuNode("Leaf", 1, 1);
    for (let i = 0; i < 20; i++) chain = cpuNode("Depth" + i, 1, 0, [chain]);
    renderCpuQuery("call-tree", { data: { totalSamples: 1, root: chain } }, false);
    assert.equal(elements(container, child => child.className === "cpu-profile-method").length, 8);
    assert.match(container.textContent, /Partial projection/);
});

test("CPU profile renders untrusted names as text and preserves unknown scheduler-state evidence", () => {
    const { renderCpuQuery, container } = cpuRenderer();
    const name = "<img src=x onerror=alert(1)>";
    const root = cpuNode(name, 10, 10);
    root.selfSamples = { runningSamples: 0, waitingSamples: 2, unknownSamples: 8 };
    renderCpuQuery("call-tree", { data: { totalSamples: 10, root } }, false);
    assert.match(container.textContent, /0 on-CPU \/ 2 heuristic-wait \/ 8 unknown/);
    assert.ok(container.textContent.includes(name));
    click(container, "Flame graph (icicle)");
    assert.ok(container.querySelector("svg").textContent.includes(name));
    assert.equal(container.querySelector("img"), null);
    assert.equal(container.querySelector("script"), null);
});

test("CPU profile handles empty/zero data and rejects missing, negative or inconsistent counts explicitly", () => {
    const { renderCpuQuery, container } = cpuRenderer();
    renderCpuQuery("call-tree", { data: {} }, false);
    assert.match(container.textContent, /No call tree/);
    renderCpuQuery("call-tree", { data: { totalSamples: 0, root: cpuNode("Root", 0, 0) } }, false);
    click(container, "Flame graph (icicle)");
    assert.match(container.textContent, /No inclusive samples/);
    assert.equal(container.querySelector("svg"), null);
    for (const root of [
        cpuNode("Missing", undefined, 0), cpuNode("Negative", -1, 0), cpuNode("NaN", NaN, 0),
        cpuNode("Inconsistent", 1, 2), cpuNode("Children exceed parent", 1, 0, [cpuNode("Child", 2, 2)]),
    ]) {
        renderCpuQuery("call-tree", { data: { totalSamples: 10, root } }, false);
        assert.match(container.textContent, /Call tree unavailable: invalid or missing/);
        assert.equal(container.querySelector("svg"), null);
    }
    renderCpuQuery("call-tree", { data: { root: cpuNode("Unknown total", 1, 1) } }, false);
    assert.match(container.textContent, /Unavailable of capture/);
    assert.match(container.textContent, /Self scheduler-state evidence: Unavailable/);
});

test("exception captures render counts, retained messages and stacks as text with cap notes", () => {
    const { renderQueryView, makeContainer } = loadRenderer();
    const container = makeContainer("exceptionCaptureViews");
    const message = "<img src=x onerror=alert('sensitive')>";
    renderQueryView("exceptionCaptureViews", "exceptions", {
        summary: "7 exceptions captured.",
        data: {
            totalExceptions: 7, recentCap: 2,
            startedAt: "2026-10-14T00:00:00Z", duration: "00:00:10",
            processExited: true, unhandledExceptionObserved: false,
            byType: [{ exceptionType: "System.Exception", count: 7 }],
            recent: [
                { timestamp: "2026-10-14T00:00:01Z", exceptionType: "System.Exception",
                    exceptionMessage: message, threadId: 1, managedStack: ["Sample.Throw()", "<script>frame</script>"] },
                { timestamp: "2026-10-14T00:00:02Z", exceptionType: "System.Exception",
                    exceptionMessage: message, threadId: 1 },
            ],
        },
        notes: ["maxRecent=2; 5 event(s) omitted", "collector-note"],
    }, false);
    assert.match(container.textContent, /Observed exceptions7/);
    assert.match(container.textContent, /Top messages \(retained events only\)/);
    assert.match(container.textContent, /2026-10-14T00:00:01Z/);
    assert.match(container.textContent, /2026-10-14T00:00:02Z/);
    assert.ok(container.textContent.includes(message));
    click(container, "2026-10-14T00:00:01Z | System.Exception | thread 1");
    assert.match(container.textContent, /Sample.Throw\(\)/);
    click(container, "2026-10-14T00:00:02Z | System.Exception | thread 1");
    assert.match(container.textContent, /No managed stack was captured/);
    assert.match(container.textContent, /5 event\(s\) omitted/);
    assert.match(container.textContent, /collector-note/);
    assert.match(container.textContent, /Unhandled exception observedNo/);
    const startedMetric = elements(container, element => element.className === "query-view-metric")
        .find(element => element.textContent.startsWith("Started"));
    assert.ok(startedMetric);
    assert.doesNotMatch(startedMetric.textContent, /2026-10-14T00:00:00Z/);
    const exactTimestamp = container.querySelector(".query-view-timestamp");
    assert.equal(exactTimestamp.querySelector("summary").textContent, "Exact start time");
    assert.equal(exactTimestamp.querySelector("code").textContent, "2026-10-14T00:00:00Z");
    assert.equal(container.querySelector("img"), null);
    assert.equal(container.querySelector("script"), null);
    renderQueryView("exceptionCaptureViews", "exceptions", {
        summary: "No exceptions", data: { totalExceptions: 0, byType: [], recent: [] }, notes: [],
    }, false);
    assert.match(container.textContent, /Start capture before reproducing/);
    assert.ok(!container.textContent.includes(message), "new results replace old sensitive messages");
});

test("live webview wires exception requests and result/status handling separately from CPU", () => {
    const html = renderHtml(target, "nonce123", 120);
    const script = extractInlineScript(html);
    assert.match(html, /id="captureExceptions"/);
    assert.match(html, /id="captureExceptionsStatus" role="status"/);
    assert.match(script, /captureExceptionsButton\.addEventListener\('click', \(\) => vscode\.postMessage\(\{ type: 'captureExceptions' \}\)\)/);
    assert.match(script, /message\.type === 'captureStatus' && message\.kind === 'exceptions'/);
    assert.match(script, /renderQueryView\('exceptionCaptureViews', 'exceptions', message\.result, false\)/);
    const statusBranch = /message\.type === 'captureStatus' && message\.kind === 'exceptions'\) \{([\s\S]*?)\n      \} else/.exec(script);
    assert.ok(statusBranch);
    const elements = new Map([
        ["captureExceptionsStatus", { textContent: "" }],
        ["captureExceptionsButton", { disabled: false }],
        ["exceptionCaptureViews", { clears: 0, replaceChildren() { this.clears++; } }],
    ]);
    const dispatchStatus = new Function("message", "captureExceptionsStatus", "captureExceptionsButton", "activateCaptureTab", "document", statusBranch[1]);
    for (const state of ["starting", "error", "running"]) {
        dispatchStatus({ state }, elements.get("captureExceptionsStatus"), elements.get("captureExceptionsButton"),
            () => {}, { getElementById: id => elements.get(id) });
        assert.equal(elements.get("exceptionCaptureViews").clears, state === "running" ? 1 : 0,
            "only an issued capture clears prior evidence");
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
    assert.equal(container.children.filter(child => child.classList.contains("query-view")).length, 1);
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

test("grouped stacks explicitly identify unavailable stacks without a duplicate explorer mode", () => {
    for (const frames of [[], undefined, null]) {
        const { renderQueryView, makeContainer, document, updateThreadNavigation } = loadRenderer();
        const root = makeContainer("captureThreadResult");
        const queries = makeContainer("threadQueryViews");
        root.appendChild(queries);
        updateThreadNavigation("snapshot", { threads: [] });
        renderQueryView("threadQueryViews", "unique-stacks", { uniqueStacks: [{
            threadCount: 7, threadPercentage: .25, sampleThreads: [{ managedThreadId: 2 }],
            canonicalFrames: frames,
        }] }, false);
        const evidence = document.getElementById("threadQueryViews-unique-stacks");
        assert.match(evidence.querySelector(".query-view-nested").querySelector("summary").textContent, /Stack unavailable/);
        assert.match(evidence.textContent, /No managed stack frames were returned/);
        assert.match(evidence.textContent, /does not specify why/);
        const explorer = document.getElementById("threadExplorer");
        assert.equal(document.getElementById("threadMode-groups"), null);
        assert.match(evidence.textContent, /share the same execution path/);
        assert.ok(!explorer.textContent.includes("Stack unavailable"));
    }
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
    assert.match(container.textContent, /Unavailable: numeric address \(imprecise JavaScript integer\)/);
});

function makeTable(document, parent, columns, values, id) {
    const table = document.createElement("table");
    if (id) table.id = id;
    const head = document.createElement("thead");
    const heading = document.createElement("tr");
    for (const column of columns) {
        const th = document.createElement("th");
        th.textContent = column;
        heading.appendChild(th);
    }
    head.appendChild(heading);
    const body = document.createElement("tbody");
    for (const cells of values) {
        const row = document.createElement("tr");
        for (const value of cells) {
            const cell = document.createElement("td");
            cell.textContent = String(value);
            row.appendChild(cell);
        }
        body.appendChild(row);
    }
    table.append(head, body);
    parent.appendChild(table);
    return table;
}

test("returned-row navigation filters and numerically sorts without redundant disclosures", () => {
    const { makeContainer, document, enhanceEvidenceTable } = loadRenderer();
    const root = makeContainer("tables");
    const table = makeTable(document, root, ["Name", "Bytes"], [["A", 100], ["B", 2], ["C", 30]]);
    enhanceEvidenceTable(table);
    const rows = () => table.querySelector("tbody").children;
    click(table.querySelector("thead"), "Bytes");
    assert.deepEqual(rows().map(row => row.children[1].textContent), ["2", "30", "100"]);
    const heading = table.querySelectorAll("th")[1];
    assert.equal(heading.attributes["aria-sort"], "ascending");
    assert.equal(heading.querySelector("button").className, "evidence-sort");
    click(table.querySelector("thead"), "Bytes");
    assert.deepEqual(rows().map(row => row.children[1].textContent), ["100", "30", "2"]);
    assert.equal(heading.attributes["aria-sort"], "descending");
    assert.equal(root.querySelector(".evidence-pick"), null);
    assert.equal(root.querySelector(".evidence-selection"), null);
    const input = root.querySelector("input");
    input.value = "B";
    input.listeners.input();
    assert.equal(rows().length, 1);
    assert.match(root.textContent, /1 of 3 returned rows/);
    input.value = "not found";
    input.listeners.input();
    assert.equal(rows().length, 0);
    assert.match(root.textContent, /0 of 3/);
});

test("table enhancement preserves meaningful links and nested disclosures without duplicating cells", () => {
    const { makeContainer, document, enhanceEvidenceTable } = loadRenderer();
    const root = makeContainer("inline");
    const table = makeTable(document, root, ["Name", "Count"], [["A", 10], ["B", 2]]);
    enhanceEvidenceTable(table);
    const body = table.querySelector("tbody");
    const first = body.children[0];
    const second = body.children[1];
    const link = document.createElement("button");
    link.textContent = "Inspect object";
    let opened = 0;
    link.addEventListener("click", () => { opened++; });
    first.children[0].appendChild(link);
    const disclosure = document.createElement("details");
    const summary = document.createElement("summary");
    summary.textContent = "Struct fields";
    const field = document.createElement("p");
    field.textContent = "Key: 42";
    disclosure.append(summary, field);
    first.children[0].appendChild(disclosure);
    click(table.querySelector("thead"), "Count");
    assert.equal(body.children[0], second);
    click(first, "Inspect object");
    assert.equal(opened, 1);
    assert.equal(first.querySelector("details"), disclosure);
    assert.match(disclosure.textContent, /Key: 42/);
    assert.equal(table.querySelector(".evidence-pick"), null);
    assert.equal(table.querySelector(".evidence-detail-row"), null);
});

test("table enhancement is idempotent and a new capture does not retain controls or old rows", () => {
    const { makeContainer, document, enhanceEvidenceTable, resetEvidenceTable } = loadRenderer();
    const root = makeContainer("tables");
    const table = makeTable(document, root, ["Name"], [["old"]]);
    enhanceEvidenceTable(table);
    enhanceEvidenceTable(table);
    assert.equal(root.querySelectorAll("input").length, 1);
    resetEvidenceTable(table);
    const row = document.createElement("tr");
    const cell = document.createElement("td");
    cell.textContent = "new";
    row.appendChild(cell);
    table.querySelector("tbody").replaceChildren(row);
    enhanceEvidenceTable(table);
    assert.equal(root.querySelectorAll("input").length, 1);
    assert.doesNotMatch(root.textContent, /old/);
    assert.match(root.textContent, /new/);
});

test("live and dump heap layouts keep types and details in separate responsive panes", () => {
    for (const html of [renderHtml(target, "nonce", 120), renderDumpAnalysisHtml("fixture.dmp", "nonce")]) {
        assert.match(html, /id="heapWorkspace" class="heap-workspace" data-focused="false"/);
        assert.match(html, /id="heapTypeList"[\s\S]*?id="captureHeapTable"[\s\S]*?<\/table>\s*<\/div>\s*<section id="heapDetailPane"/);
        assert.match(html, /id="backToHeapTypes" type="button">Back to types/);
        assert.match(html, /id="heapDetailPane"[\s\S]*?id="heapQueryViews"[\s\S]*?id="addressNavigation"/);
        assert.match(html, /@container \(max-width: 850px\)/);
        assert.match(html, /workspace\.dataset\.focused = "true"/);
        assert.match(html, /window\.scrollTo\(\{ top: overviewScroll \}\)/);
    }
});

test("heap type focus navigates captured paths, handles late paths and clears without a live read", () => {
    const { makeContainer, document, renderHeapTypeNavigation, renderQueryView } = loadRenderer();
    const root = makeContainer("captureHeapResult");
    const table = makeTable(document, root, ["Type", "Instances", "Bytes"], [["App.Cache", 2, 100]], "captureHeapTable");
    const views = document.createElement("div");
    views.id = "heapQueryViews";
    root.appendChild(views);
    renderHeapTypeNavigation([{ typeFullName: "App.Cache", instanceCount: 2, totalBytes: 100 }]);
    click(table, "App.Cache");
    assert.match(root.textContent, /Retention paths are not available yet/);
    renderQueryView("heapQueryViews", "retention-paths", { retentionPaths: [
        { targetTypeFullName: "App.Cache", targetObjectAddress: "0x10", chain: [] },
        { targetTypeFullName: "App.Other", targetObjectAddress: "0x20", chain: [] },
    ] }, false);
    const paths = views.querySelectorAll(".query-view-nested");
    assert.equal(paths[0].hidden, false);
    assert.equal(paths[0].open, true);
    assert.equal(paths[1].hidden, true);
    assert.match(root.textContent, /1 of 2 returned retention paths/);
    assert.match(root.textContent, /Not dominator-retained size/);
    click(root, "Clear type focus");
    assert.ok(paths.every(path => !path.hidden));
});

test("empty deadlocks remain neutral and stale candidate warnings are removed after empty/error results", () => {
    const { renderQueryView, makeContainer } = loadRenderer();
    const root = makeContainer("threadQueryViews");
    renderQueryView("threadQueryViews", "deadlocks", { deadlocks: [] }, false);
    const view = root.children[0];
    assert.equal(view.classList.contains("query-view-deadlocks"), false);
    assert.equal(view.open, false);
    renderQueryView("threadQueryViews", "deadlocks", { deadlocks: [{ cycleMembers: [] }] }, false);
    assert.equal(view.classList.contains("query-view-deadlocks"), true);
    renderQueryView("threadQueryViews", "deadlocks", { deadlocks: [] }, false);
    assert.equal(view.classList.contains("query-view-deadlocks"), false);
    renderQueryView("threadQueryViews", "deadlocks", "Permission denied", true);
    assert.equal(view.classList.contains("query-view-deadlocks"), false);
    assert.match(view.textContent, /Permission denied/);
});

test("thread tabs support roving keyboard navigation and retain filters, selection and late evidence", () => {
    const { makeContainer, document, updateThreadNavigation, renderQueryView } = loadRenderer();
    const root = makeContainer("captureThreadResult");
    const queries = makeContainer("threadQueryViews");
    root.appendChild(queries);
    updateThreadNavigation("snapshot", { threads: [{ managedThreadId: 1, frames: ["App.Work"] }] });
    const explorer = document.getElementById("threadExplorer");
    click(explorer, "Thread 1");
    const input = explorer.querySelector("input");
    input.value = "Work"; input.listeners.input();
    const modes = explorer.querySelectorAll(".thread-mode-tab");
    assert.equal(explorer.querySelector("select"), null);
    assert.deepEqual(modes.map(tab => tab.tabIndex), [0, -1, -1]);
    const key = (tab, value) => tab.listeners.keydown({ key: value, preventDefault() {} });
    key(modes[0], "ArrowRight");
    assert.equal(modes[1].attributes["aria-selected"], "true");
    assert.equal(modes[1].focused, true);
    assert.equal(input.disabled, true);
    key(modes[1], "End");
    assert.equal(modes[2].attributes["aria-selected"], "true");
    assert.equal(input.disabled, true);
    key(modes[2], "ArrowRight");
    assert.equal(modes[0].attributes["aria-selected"], "true");
    assert.equal(input.value, "Work");
    assert.match(explorer.querySelector(".evidence-selection").textContent, /Managed thread 1/);
    key(modes[0], "ArrowLeft");
    key(modes[2], "Home");
    assert.equal(document.getElementById("threadModePanel").attributes["aria-labelledby"], modes[0].id);
    renderQueryView("threadQueryViews", "unique-stacks", { uniqueStacks: [] }, false);
    const controls = queries.querySelector(".evidence-view-switcher");
    const tabs = controls.querySelectorAll("button");
    assert.equal(controls.querySelector(".evidence-tabs").attributes.role, "tablist");
    assert.equal(tabs[0].attributes.role, "tab");
    key(tabs[0], "ArrowRight");
    const evidence = document.getElementById("threadQueryViews-unique-stacks");
    assert.equal(explorer.hidden, true);
    assert.equal(evidence.hidden, false);
    assert.equal(evidence.attributes.role, "tabpanel");
    assert.equal(evidence.attributes["aria-labelledby"], tabs[1].id);
    assert.deepEqual(tabs.map(tab => tab.tabIndex), [-1, 0]);
    renderQueryView("threadQueryViews", "wait-chains", { waitChains: { chains: [] } }, false);
    assert.equal(evidence.hidden, false, "new evidence must not steal the active tab");
    const refreshed = controls.querySelectorAll("button");
    key(refreshed[1], "Home");
    assert.equal(explorer.hidden, false);
    assert.equal(explorer.querySelector("input").value, "Work");
    assert.match(explorer.querySelector(".evidence-selection").textContent, /Managed thread 1/);
});

test("single grouped-stacks tab links representative threads and returns without rebuilding groups", () => {
    const { makeContainer, document, updateThreadNavigation, renderQueryView } = loadRenderer();
    const root = makeContainer("captureThreadResult");
    const queries = makeContainer("threadQueryViews");
    root.appendChild(queries);
    updateThreadNavigation("snapshot", { threads: [{ managedThreadId: 1, frames: ["App.Work"] }] });
    renderQueryView("threadQueryViews", "unique-stacks", { uniqueStacks: [{
        threadCount: 7, threadPercentage: .25,
        sampleThreads: [{ managedThreadId: 1, osThreadId: 42 }, { managedThreadId: 99, osThreadId: 0 }],
        canonicalFrames: [{ displayName: "App.Work" }],
    }] }, false);
    const controls = queries.querySelector(".evidence-view-switcher");
    assert.equal(controls.querySelectorAll("button").filter(button => button.textContent === "Grouped stacks").length, 1);
    assert.equal(document.getElementById("threadMode-groups"), null);
    click(controls, "Grouped stacks");
    const groups = document.getElementById("threadQueryViews-unique-stacks");
    const disclosure = groups.querySelector(".query-view-nested");
    disclosure.open = true;
    const input = groups.querySelector("input");
    input.value = "Thread 1"; input.listeners.input();
    assert.match(groups.textContent, /representative thread IDs/);
    click(groups, "Thread 1");
    const explorer = document.getElementById("threadExplorer");
    assert.equal(explorer.hidden, false);
    assert.equal(groups.hidden, true);
    assert.match(explorer.querySelector(".evidence-selection").textContent, /App.Work/);
    click(explorer, "Back to grouped stacks");
    assert.equal(groups.hidden, false);
    assert.equal(input.value, "Thread 1");
    assert.equal(disclosure.open, true);
    input.value = ""; input.listeners.input();
    click(groups, "Thread 99");
    assert.match(explorer.querySelector(".evidence-selection").textContent, /not included in the returned inline thread list/);
    click(explorer, "Back to grouped stacks");
    assert.equal(groups.hidden, false);
});

test("thread explorer connects groups, full inline stacks and wait-chain owners, including omitted participants", () => {
    const { makeContainer, updateThreadNavigation } = loadRenderer();
    const root = makeContainer("captureThreadResult");
    updateThreadNavigation("snapshot", { threads: [
        { managedThreadId: 1, state: "Wait", frames: ["App.Entry", "<script>App.Block</script>"] },
        { managedThreadId: 2, state: "Running", frames: ["App.Owner"] },
    ] });
    updateThreadNavigation("unique-stacks", { uniqueStacks: [{
        threadCount: 5, inferredWaitReason: "Monitor",
        canonicalFrames: [{ displayName: "App.Block" }], sampleThreads: [{ managedThreadId: 1 }],
    }] });
    updateThreadNavigation("wait-chains", { waitChains: { chains: [{
        rootThreadId: 1, terminalKind: "Owner", links: [
            { waitingThreadId: 1, ownerThreadId: 2, targetLabel: "Monitor", confidence: "medium" },
            { waitingThreadId: 2, ownerThreadId: 99, targetLabel: "Other" },
        ],
    }] } });
    click(root, "Thread 1");
    const details = root.querySelector(".evidence-selection");
    assert.match(details.textContent, /App.Entry/);
    assert.match(details.textContent, /<script>App.Block<\/script>/);
    assert.equal(details.querySelector("script"), null);
    assert.match(details.textContent, /confidence: medium/);
    click(details, "Thread 2");
    assert.match(details.textContent, /App.Owner/);
    click(details, "Thread 99");
    assert.match(details.textContent, /not included in the returned inline thread list/);
    const search = root.querySelector("input");
    search.value = "App.Owner";
    search.listeners.input();
    assert.match(root.textContent, /1 of 2 returned threads match/);
    updateThreadNavigation("snapshot", { threads: [] });
    assert.doesNotMatch(root.textContent, /App.Owner|Thread 99|App.Block/);
});

test("exception type and message filters select retained occurrences without changing observed totals", () => {
    const { makeContainer, renderExceptionNavigation } = loadRenderer();
    const root = makeContainer("exceptions");
    renderExceptionNavigation(root, [
        { exceptionType: "A", exceptionMessage: "", timestamp: "t1", threadId: 1 },
        { exceptionType: "A", exceptionMessage: "<img>message", timestamp: "t2", threadId: 2, managedStack: ["Throw()"] },
        { exceptionType: "B", exceptionMessage: "other", timestamp: "t3", threadId: 3 },
    ]);
    const [type, message] = root.querySelectorAll("select");
    type.value = JSON.stringify("A");
    type.listeners.change();
    assert.match(root.textContent, /2 of 3 retained occurrences/);
    message.value = JSON.stringify("");
    message.listeners.change();
    assert.match(root.textContent, /1 of 3 retained occurrences/);
    click(root, "t1 | A | thread 1");
    assert.match(root.textContent, /No managed stack was captured/);
    message.value = JSON.stringify("<img>message");
    message.listeners.change();
    click(root, "t2 | A | thread 2");
    assert.match(root.textContent, /Throw\(\)/);
    assert.match(root.textContent, /<img>message/);
    assert.equal(root.querySelector("img"), null);
    type.value = JSON.stringify("B");
    type.listeners.change();
    assert.equal(message.value, "");
    assert.doesNotMatch(root.querySelector("section").textContent, /Throw\(\)/);
    click(root, "Reset exception filters");
    assert.match(root.textContent, /3 of 3 retained occurrences/);
});

test("live time selection filters only retained observations and GC receipt times and supports reset/inspection", () => {
    const { document, makeContainer } = createDocumentStub();
    const root = makeContainer("live");
    const canvas = document.createElement("canvas");
    canvas.getBoundingClientRect = () => ({ width: 1056 });
    root.appendChild(canvas);
    const { LIVE_TIME_NAVIGATION_SCRIPT } = require("../out/liveTimeNavigation.js");
    const install = new Function("document", LIVE_TIME_NAVIGATION_SCRIPT + ";return installLiveTimeNavigation;")(document);
    let points = [0, 100, 200, 300].map(receivedAtMs => ({ receivedAtMs, timestamp: "target-" + receivedAtMs, value: receivedAtMs / 10 }));
    const gcRows = [50, 250].map(receivedAtMs => ({ receivedAtMs, element: document.createElement("tr") }));
    let redraws = 0;
    const nav = install(canvas, () => points, gcRows, () => redraws++);
    const [start, end] = root.querySelectorAll("input");
    assert.match(root.textContent, /Start of selected receipt time/);
    assert.match(root.textContent, /End of selected receipt time/);
    assert.match(start.attributes["aria-valuetext"], /0% of retained receipt time/);
    assert.match(end.attributes["aria-valuetext"], /100% of retained receipt time/);
    start.value = "25"; end.value = "75"; start.listeners.input();
    assert.deepEqual(nav.filter(points).map(point => point.receivedAtMs), [100, 200]);
    assert.ok(gcRows.every(row => row.element.hidden));
    assert.match(root.textContent, /Selected receipt-time bounds: 1970-01-01T00:00:00\.075Z to 1970-01-01T00:00:00\.225Z/);
    assert.match(start.attributes["aria-valuetext"], /selected start 1970-01-01T00:00:00\.075Z/);
    canvas.listeners.keydown({ key: "Home", preventDefault() {} });
    assert.match(root.textContent, /Observed value: 10 \| target timestamp: target-100/);
    points = [{ receivedAtMs: 1000, timestamp: "later", value: 123 }];
    nav.update();
    assert.match(root.textContent, /0 of 1 retained observations/);
    assert.equal(nav.filter(points).length, 0, "fixed selections must not silently follow incoming data");
    click(root, "Follow live / reset zoom");
    assert.equal(nav.filter(points).length, 1);
    assert.match(root.textContent, /Following live.*Selected receipt-time bounds: 1970-01-01T00:00:01\.000Z to 1970-01-01T00:00:01\.000Z/);
    assert.ok(gcRows.every(row => !row.element.hidden));
    assert.equal(redraws, 2);
    const reset = root.querySelector("button");
    points = [];
    gcRows.length = 0;
    nav.update();
    assert.equal(reset.getAttribute("aria-disabled"), "true");
    assert.match(reset.title, /Wait for retained/);
    gcRows.push({ receivedAtMs: 2000, element: document.createElement("tr") });
    nav.update();
    assert.equal(reset.getAttribute("aria-disabled"), "false", "retained GC rows are sufficient to reset their time selection");
});

test("all diagnostic kinds provide sortable selectable returned rows without fabricating a timeline", () => {
    const { document, makeContainer } = createDocumentStub();
    const root = makeContainer("diagnostics");
    const { CAPTURE_VIEW_RENDERER_SCRIPT } = require("../out/captureViewRenderer.js");
    const render = new Function("document", CAPTURE_VIEW_RENDERER_SCRIPT + ";return renderCaptureView;")(document);
    const { CAPTURE_KIND_SPECS } = require("../out/captureKinds.js");
    for (const spec of CAPTURE_KIND_SPECS) {
        render("diagnostics", {
            kind: spec.kind, title: spec.label, summary: "Fixture",
            provenance: { processId: 1, requestedDurationSeconds: 10 },
            metrics: [{ label: "Unavailable metric", value: null }],
            tables: [{ title: "Returned evidence", columns: ["Name", "Count"],
                rows: [["<script>operation</script>", 0], ["Other", 10]], totalRows: 3, omittedRows: 1 }],
            notes: ["bounded"], evidence: { source: "not-reported", limitations: [] },
        });

        assert.equal(root.querySelectorAll("input").length, 2);
        assert.match(root.textContent, /do not establish a complete event timeline/);
        assert.match(root.textContent, /1 retained row\(s\) omitted/);
        const tables = root.querySelectorAll("table");
        assert.equal(tables[1].querySelector(".evidence-pick"), null);
        assert.equal(tables[1].querySelector("tbody").children[0].children[1].textContent, "0");
        assert.match(tables[1].textContent, /<script>operation<\/script>/);
        assert.equal(root.querySelector("script"), null);
    }
});

test("heap view switching retains table filters and selection, surfaces hidden failures, and does not auto-switch on arrival", () => {
            const { renderQueryView, makeContainer, document } = loadRenderer();
            const root = makeContainer("heapQueryViews");
            renderQueryView("heapQueryViews", "static-fields", { staticFields: [{ fieldName: "Cache" }, { fieldName: "Other" }] }, false);
            const statics = document.getElementById("heapQueryViews-static-fields");
            const input = statics.querySelector("input");
            input.value = "Cache";
            input.listeners.input();
            renderQueryView("heapQueryViews", "roots-by-kind", { rootsByKind: [] }, false);
            const roots = document.getElementById("heapQueryViews-roots-by-kind");
            assert.equal(statics.hidden, false);
            assert.equal(roots.hidden, true);
            const controls = root.querySelector(".evidence-view-switcher");
            click(controls, "GC roots by kind");
            assert.equal(statics.hidden, true);
            assert.equal(roots.hidden, false);
            assert.equal(roots.open, true);
            click(controls, "Static fields");
            assert.equal(input.value, "Cache");
            assert.equal(statics.querySelector(".evidence-pick"), null);
            renderQueryView("heapQueryViews", "roots-by-kind", "Permission denied", true);
            assert.equal(statics.hidden, false, "a late failing query must not steal selection");
            assert.match(controls.textContent, /1 unavailable view/);
            click(controls, "GC roots by kind (unavailable)");
            assert.match(roots.textContent, /Permission denied/);
            assert.equal(roots.hidden, false);
        });

        test("thread explorer modes preserve the selected thread across grouped and wait evidence updates", () => {
            const { makeContainer, document, updateThreadNavigation, renderQueryView } = loadRenderer();
            const root = makeContainer("captureThreadResult");
            const queries = document.createElement("div");
            queries.id = "threadQueryViews";
            root.appendChild(queries);
            updateThreadNavigation("snapshot", { threads: [{ managedThreadId: 1, frames: ["App.Wait"] }] });
            const explorer = document.getElementById("threadExplorer");
            click(explorer, "Thread 1");
            renderQueryView("threadQueryViews", "unique-stacks", { uniqueStacks: [{
                threadCount: 4, sampleThreads: [{ managedThreadId: 1 }], canonicalFrames: [{ displayName: "App.Wait" }],
            }] }, false);
            assert.equal(document.getElementById("threadMode-threads").attributes["aria-selected"], "true");
            assert.match(explorer.querySelector(".evidence-selection").textContent, /Managed thread 1/);
            assert.equal(explorer.hidden, false);
            assert.equal(document.getElementById("threadQueryViews-unique-stacks").hidden, true);
            assert.match(document.getElementById("threadQueryViews-unique-stacks").textContent, /4 thread/);
            click(explorer, "Inferred cycle candidates");
            renderQueryView("threadQueryViews", "deadlocks", { deadlocks: [{ cycleMembers: [{ threadId: 1 }], confidence: "medium" }] }, false);
            assert.equal(document.getElementById("threadMode-cycles").attributes["aria-selected"], "true");
            assert.match(queries.querySelector(".evidence-view-switcher").textContent, /Deadlock candidates \(warning\)/);
            assert.equal(explorer.hidden, false);
            assert.match(explorer.textContent, /1 inferred candidates/);
        });

        test("diagnostic selector shows one table at a time, preserving row filters and exposing omission counts in options", () => {
            const { document, makeContainer } = createDocumentStub();
            const root = makeContainer("diagnostics");
            const { CAPTURE_VIEW_RENDERER_SCRIPT } = require("../out/captureViewRenderer.js");
            const render = new Function("document", CAPTURE_VIEW_RENDERER_SCRIPT + ";return renderCaptureView;")(document);
            render("diagnostics", { kind: "gc", title: "GC", summary: "Fixture", provenance: { processId: 1, requestedDurationSeconds: 1 },
                metrics: [], tables: [
                    { title: "Collections", columns: ["Generation"], rows: [[2], [1]], totalRows: 3, omittedRows: 1 },
                    { title: "Suspensions", columns: ["Duration"], rows: [[10]], totalRows: 1, omittedRows: 0 },
                ], evidence: { source: "not-reported", limitations: [] }, notes: ["Important limitation"] });
            const panels = root.querySelectorAll(".diagnostic-table-view");
            assert.deepEqual(panels.map(panel => panel.hidden), [false, true]);
            const input = panels[0].querySelector("input");
            input.value = "2";
            input.listeners.input();
            const selector = root.querySelector("select");
            assert.match(selector.textContent, /Collections \(2 returned; 1 omitted\)/);
            selector.value = "1"; selector.listeners.change();
            assert.deepEqual(panels.map(panel => panel.hidden), [true, false]);
            assert.match(root.textContent, /Important limitation/);
            selector.value = "0"; selector.listeners.change();
            assert.equal(input.value, "2");
            assert.equal(panels[0].querySelector("tbody").children.length, 1);
        });

test("live-read permissions display scoped grants, bounded history and a revoke action without sending grants to the host", () => {
    const { document, makeContainer } = createDocumentStub();
    const root = makeContainer("liveReadConsents");
    const summary = document.createElement("summary");
    root.appendChild(summary);
    const revoke = makeContainer("revokeLiveReads");
    makeContainer("liveReadConsentStatus");
    const history = makeContainer("liveReadConsentHistory");
    const messages = [];
    const { LIVE_READ_CONSENT_SCRIPT } = require("../out/liveReadConsent.js");
    const render = new Function("document", "vscode", LIVE_READ_CONSENT_SCRIPT + ";return renderLiveReadConsent;")(
        document, { postMessage: message => messages.push(message) });
    render({ grants: ["object"], history: Array.from({ length: 40 }, (_, i) => ({
        operation: "object", decision: "allowed once", timestamp: "t" + i,
    })) });
    assert.match(summary.textContent, /Object remembered/);
    assert.equal(revoke.getAttribute("aria-disabled"), "false");
    assert.equal(history.children.length, 30);
    revoke.listeners.click();
    assert.deepEqual(messages, [{ type: "revokeLiveReads" }]);
    render({ grants: [], history: [] });
    assert.match(summary.textContent, /none remembered/);
    assert.equal(revoke.getAttribute("aria-disabled"), "true");
    assert.equal(history.children.length, 0);
    const live = renderHtml(target, "nonce", 120);
    const dump = renderDumpAnalysisHtml("fixture.dmp", "nonce");
    assert.match(live, /id="liveReadConsents"/);
    assert.doesNotMatch(dump, /id="liveReadConsents"/);
});
