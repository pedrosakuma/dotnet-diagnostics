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
const {
    CPU_INVESTIGATION_OVERVIEW_SCRIPT,
    CPU_INVESTIGATION_OVERVIEW_STYLES,
} = require("../out/cpuInvestigationOverview.js");
const { CPU_COMPARISON_SCRIPT } = require("../out/cpuComparison.js");
const { CAPTURE_GROUPS, CAPTURE_KIND_SPECS } = require("../out/captureKinds.js");

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
            remove: name => this.classes.delete(name),
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

    click() {
        this.listeners?.click?.({});
    }

    append(...children) {
        for (const child of children) this.appendChild(child);
    }

    appendChild(child) {
        child._parent = this;
        this.children.push(child);
        return child;
    }

    replaceChildren(...children) {
        for (const child of this.children) child._parent = null;
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
        querySelector() { return null; },
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

function loadRenderer(selectThreadSnapshotView) {
    const { document, makeContainer } = createDocumentStub();
    // eslint-disable-next-line no-new-func -- exercises the exact script emitted into the webview.
    const api = new Function(
        "document", "window", "selectThreadSnapshotView",
        `${QUERY_VIEW_RENDERER_SCRIPT}\nreturn { renderQueryView, enhanceEvidenceTable, resetEvidenceTable,
            renderHeapTypeNavigation, navigateHeapDetails, updateThreadNavigation, renderExceptionNavigation,
            navigateThreadTo };`,
    )(document, {scrollY:13, scrollTo(){}}, selectThreadSnapshotView);
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

test("captured data precedes static scope and quantitative summaries stay secondary", () => {
    const html = renderHtml(target, "nonce123", 120);
    assert.ok(html.indexOf('id="chart"') < html.indexOf('id="counterIdentity"'));
    assert.ok(html.indexOf('id="chart"') < html.indexOf('id="chartNote"'));
    assert.ok(html.indexOf('id="quality"') < html.indexOf('id="chart"'), "active chart loss stays visible before data");
    assert.ok(html.indexOf('id="cpuQueryViews"') < html.indexOf('id="cpuSamplingDisclaimer"'));
    assert.ok(html.indexOf('id="cpuQueryViews"') < html.indexOf('id="cpuEvidencePrerequisite"'));
    assert.ok(html.indexOf('id="captureHeapTable"') < html.indexOf("Remembered permissions apply"));
    assert.ok(html.indexOf('id="eventsTable"') < html.indexOf('id="eventsSubscription"'));
    assert.match(html, /\.capture-summary, \.exception-capture-summary[\s\S]*?font-size:\.85rem/);
    for (const id of ["captureCpu", "captureAllocation", "captureHeap", "captureThreadSnapshot", "captureExceptions"])
        assert.match(html, new RegExp(`id="${id}"[^>]*aria-label="[^"]+">Capture</button>`));

    const { renderQueryView, makeContainer, document, updateThreadNavigation } = loadRenderer();
    const exceptions = makeContainer("exceptionCaptureViews");
    renderQueryView("exceptionCaptureViews", "exceptions", {
        data: {totalExceptions: 5, recentCap: 1, recent: [
            {exceptionType: "Fixture.Exception", exceptionMessage: "Captured message", threadId: 1},
        ], byType: []}, notes: ["maxRecent=1; 4 events omitted"],
    }, false);
    const explorer = exceptions.querySelector(".exception-explorer");
    const scope = exceptions.querySelector(".exception-capture-scope");
    assert.equal(scope._parent, explorer._parent);
    assert.ok(scope._parent.children.indexOf(scope) > scope._parent.children.indexOf(explorer));
    assert.match(exceptions.querySelector(".exception-capture-notes").textContent, /4 events omitted/);
    assert.match(exceptions.querySelector(".exception-capture-notes").children[0].textContent, /4 observations not retained/);
    assert.equal(exceptions.querySelector(".exception-capture-summary").textContent, "5 observed · 1 retained");

    const threads = makeContainer("threadQueryViews");
    makeContainer("captureThreadResult").appendChild(threads);
    updateThreadNavigation("snapshot", {threads: [{managedThreadId: 1, state: "Running", frames: ["Fixture.Work()"]}]});
    const list = document.getElementById("threadModePanel");
    assert.ok(threads.textContent.includes("Fixture.Work()"));
    const threadExplorer = document.getElementById("threadExplorer");
    assert.ok(threadExplorer.children.indexOf(list) < threadExplorer.children.findIndex(node =>
        node.textContent.startsWith("Point-in-time snapshot")));
});

test("thread Workspace summary exposes deadlock and query availability without opening legacy tabs", () => {
    const html = renderHtml(target, "nonce", 120);
    const script = extractInlineScript(html);
    assert.match(html, /id="workspaceThreadViewStatus" role="status"/);
    assert.match(script, /'thread-explorer': 'Thread explorer'/);
    assert.match(script, /threadQueryStatusSummary\(\)/);
    assert.match(script, /state === 'unavailable' \? base \+ ' \(unavailable\)'/);

    const state = /    const threadQueryStates = \{[\s\S]*?\n    \};/.exec(script);
    const summary = /    function threadQueryStatusSummary\(\) \{[\s\S]*?\n    \}/.exec(script);
    const presentation = /    function refreshThreadQueryPresentation\(\) \{[\s\S]*?\n    \}/.exec(script);
    assert.ok(state && summary && presentation, "thread status projection should be emitted in the Workspace script");
    const source = [state[0], summary[0], presentation[0]].join("\n");
    const labels = {
        deadlocks: "Inferred deadlock candidates",
        "unique-stacks": "Grouped snapshot stacks",
        "wait-chains": "Snapshot wait chains",
        threadpool: "Snapshot thread-pool query",
    };
    const options = Object.keys(labels).map(value => ({ value, textContent: "" }));
    // eslint-disable-next-line no-new-func -- executes the actual emitted status summary.
    const api = new Function("threadSnapshotViewLabels", "workspaceView", `
        let threadSnapshotQueryAvailable = true;
        let threadSnapshotEvidenceId = "thread-evidence";
        ${source}
        return {threadQueryStates, threadQueryStatusSummary, refreshThreadQueryPresentation};
    `)(labels, { options });
    api.threadQueryStates.deadlocks.state = "returned";
    api.threadQueryStates.deadlocks.count = 2;
    api.threadQueryStates["unique-stacks"].state = "unavailable";
    api.threadQueryStates["wait-chains"].state = "pending";
    api.refreshThreadQueryPresentation();
    assert.match(api.threadQueryStatusSummary(), /2 inferred deadlock candidate\(s\)/);
    assert.match(api.threadQueryStatusSummary(), /1 views pending/);
    assert.match(api.threadQueryStatusSummary(), /1 views unavailable/);
    assert.equal(options.find(option => option.value === "deadlocks").textContent,
        "Inferred deadlock candidates - 2 found (inferred)");
    assert.equal(options.find(option => option.value === "unique-stacks").textContent,
        "Grouped snapshot stacks (unavailable)");
});

test("every generic capture kind has one canonical Workspace domain and no Diagnostics fallback", () => {
    const script = extractInlineScript(renderHtml(target, "nonce", 120));
    const match = /const genericCaptureDomains = (\{[^;]+\});/.exec(script);
    assert.ok(match, "the route table is emitted from the capture-group catalog");
    const routes = JSON.parse(match[1]);
    const expected = Object.fromEntries(CAPTURE_GROUPS.flatMap(group =>
        group.kinds.filter(kind => kind !== "allocation").map(kind => [kind, group.id])));
    expected["request-activities"] = "requests";
    assert.deepEqual(routes, expected);
    assert.deepEqual(Object.keys(routes).sort(),
        [...CAPTURE_KIND_SPECS.filter(spec => spec.kind !== "allocation").map(spec => spec.kind), "request-activities"].sort());
    assert.doesNotMatch(script, /resultDiagnostics|resultTabDiagnostics|selectResultTab\('diagnostics'\)/);
    assert.match(script, /Unsupported generic capture kind/);
    assert.match(script, /function setGenericCaptureOwner\(kind, evidenceId\)/);
});

test("live panel exposes the compact evidence workspace and keeps other result routes", () => {
    const html = renderHtml(target, "nonce123", 120);
    const script = extractInlineScript(html);
    const dumpHtml = renderDumpAnalysisHtml("sample.dmp", "nonce123");
    for (const [label, id] of [
        ["Monitoring", "Monitoring"], ["Workspace", "Workspace"],
    ]) {
        assert.match(html, new RegExp(`role="tab"[^>]*>${label}<`));
        assert.match(html, new RegExp(`role="tabpanel" aria-labelledby="resultTab${id}"`));
    }
    assert.match(html, /id="monitoringTabLive" role="tab"[^>]*aria-controls="monitoringLive">Live counters &amp; GC pauses/);
    assert.match(html, /id="monitoringTabEvents" role="tab"[^>]*aria-controls="monitoringEvents">Live Events/);
    assert.match(html, /id="monitoringLive" class="monitoring-view" role="tabpanel"/);
    assert.match(html, /id="monitoringEvents" class="monitoring-view" role="tabpanel"[^>]*hidden/);
    assert.match(html, /id="resultLive"/);
    assert.match(html, /id="resultEvents"/);
    assert.doesNotMatch(html, /id="resultTabDiagnostics"|id="resultDiagnostics"/);
    assert.doesNotMatch(html, /id="resultTabEvents"|id="resultTabLive"/);
    assert.doesNotMatch(html, /id="resultTabExceptions"/);
    assert.doesNotMatch(html, /id="resultTabThreads"/);
    assert.match(html, /<select id="workspaceDomain">[\s\S]*?Memory &amp; GC[\s\S]*?Threads &amp; waits[\s\S]*?Requests &amp; networking[\s\S]*?Exceptions/);
    assert.match(html, /id="workspaceEvidenceKind"/);
    assert.match(html, /id="workspaceView"/);
    assert.match(html, /id="workspaceCanvas"/);
    assert.match(html, /id="resultExceptions" hidden/);
    assert.match(html, /exceptionCaptureViews[\s\S]*?Managed exceptions retain bounded messages without stack frames/);
    assert.match(script, /exceptions: document\.getElementById\('resultExceptions'\)/);
    assert.match(script, /workspaceCanvas\.appendChild\(panel\)/);
    assert.match(html, /<section id="resultThreads" hidden>/);
    assert.match(html, /#resultThreads\.workspace-thread-snapshot \.evidence-view-switcher/);
    assert.match(script, /workspacePanels\['thread-snapshot'\]\.classList\.add\('workspace-thread-snapshot'\)/);
    assert.doesNotMatch(dumpHtml, /workspace-thread-snapshot/);
    assert.match(html, /id="captureGc" type="button">Capture GC evidence…/);
    assert.match(html, /id="cpuSamplingDisclaimer"/);
    assert.match(html, /<section id="resultCpu"[^>]*data-export-kind="cpu"[\s\S]*?<button id="captureCpu" type="button" aria-label="Capture CPU sample">Capture/);
    assert.match(html, /<section id="resultHeap"[\s\S]*?<button id="captureHeap" type="button" aria-label="Capture heap snapshot">Capture/);
    assert.match(script, /function selectResultTab\(selected\)/);
    assert.match(script, /event\.key === 'ArrowRight'/);
    assert.match(script, /function selectProfileView\(kind, view, rootMethodFilter\)/);
    assert.match(script, /Selecting it did not query or read the target/);
    assert.match(script, /function loadSelectedProfileView\(\)/);
    assert.match(script, /function selectThreadSnapshotView\(view\)/);
    assert.match(script, /setWorkspaceStatus\(genericCaptureKind, 'Not current: the shared generic capture result was replaced by '/);
    assert.match(script, /message\.evidenceId !== threadSnapshotEvidenceId/);
    assert.match(script, /message\.retainedEvidenceInvalidated === true/);
    assert.doesNotMatch(script, /workspaceGcResults/);
    assert.match(script, /evidenceId,\s*view,/);
    assert.doesNotMatch(script, /vscode\.postMessage\(\{ type: 'queryCpu', view \}\)/);
    assert.match(script, /profileRole === 'baseline' \? cpuBaselineEvidenceId : cpuEvidenceId/);
    assert.match(script, /type: 'queryCpuComparison', evidenceId/);
    assert.match(html, /\.cpu-query-table \.cpu-query-number \{ width:6\.5rem; \}/);
    assert.match(html, /\.cpu-query-table \.cpu-query-action \{ width:9rem; \}/);
    assert.match(html, /\.query-view-table\.cpu-query-table \{ min-width:48rem; table-layout:fixed; \}/);
    assert.match(html, /\.diagnostic-capture-notes li \{ overflow-wrap:anywhere; \}/);
    assert.match(html, /@media \(max-width:480px\) \{\s*#genericCaptureViews > \.diagnostic-evidence-table-label \{ display:flex; flex-direction:column; align-items:flex-start; gap:\.35rem; max-width:100%; \}/);
    assert.match(html, /#genericCaptureViews > \.diagnostic-evidence-table-label > select \{ box-sizing:border-box; min-width:0; width:100%; max-width:100%; \}/);
    assert.match(html, /\.evidence-controls label \{ display:flex; flex-wrap:wrap; gap:\.35rem; min-width:0; max-width:100%; \}/);
    assert.match(html, /\.evidence-controls input \{ box-sizing:border-box; min-width:0; max-width:100%; \}/);
    assert.match(html, /\.cpu-query-table \.cpu-query-symbol span, \.cpu-query-table \.cpu-query-module span \{ display:inline-block; white-space:nowrap; overflow-wrap:normal; \}/);
    assert.match(html, /\.cpu-query-summary-table \.cpu-query-category \{ width:56%; overflow-wrap:anywhere; word-break:normal; \}/);
    assert.match(script, /message\.type === 'query' && message\.kind === 'cpu'/);
    assert.match(script, /renderCpuQuery\(message\.view, message\.result, false, 'cpu', role\)/);
});

test("terminal thread capture status resolves pending queries without overwriting another workspace", () => {
    const script = extractInlineScript(renderHtml(target, "nonce", 120));
    const marker = "} else if (message.type === 'captureStatus' && message.kind === 'thread-snapshot') {";
    const start = script.indexOf(marker) + marker.length;
    const handler = script.slice(start, script.indexOf("      } else if (message.type === 'captureStatus')", start));
    const selectStart = script.indexOf("    function selectThreadSnapshotView(view) {");
    const select = script.slice(selectStart, script.indexOf("    workspaceDomain.addEventListener", selectStart));
    const create = new Function("selectedWorkspaceEvidence", `
        const workspaceView = {value:'call-tree'};
        const workspaceThreadViewStatus = {textContent:'unchanged'};
        const captureThreadStatusElement = {}, captureThreadSnapshotButton = {};
        let pendingThreadSnapshotEvidenceId='thread-current', threadSnapshotEvidenceId='thread-prior';
        let threadSnapshotQueryAvailable=true, threadSnapshotView='thread-explorer';
        const threadQueryStates=Object.fromEntries(['deadlocks','unique-stacks','wait-chains','threadpool']
            .map(view=>[view,{state:'pending',count:0}]));
        const setThreadStaticsBusy=()=>{}, setWorkspaceStatus=()=>{}, refreshThreadQueryPresentation=()=>{};
        const document={getElementById:()=>({innerHTML:'',remove(){}})};
        ${select}
        return {receive(message){${handler}},workspaceView,workspaceThreadViewStatus,threadQueryStates,
            selectedView:()=>threadSnapshotView,selectThreadSnapshotView};
    `);
    for (const domain of ["cpu", "allocation"]) {
        for (const state of ["error", "done"]) {
            const api = create(domain);
            api.receive({evidenceId:"thread-current",state,retainedEvidenceInvalidated:state==="error"});
            assert.ok(Object.values(api.threadQueryStates).every(item=>item.state==="unavailable"));
            assert.equal(api.workspaceView.value,"call-tree");
            assert.equal(api.workspaceThreadViewStatus.textContent,"unchanged");
        }
        const api = create(domain);
        api.selectThreadSnapshotView("thread-explorer");
        assert.equal(api.selectedView(),"thread-explorer","retained thread navigation state still updates");
        assert.equal(api.workspaceView.value,"call-tree");
    }
});

test("domain return restores CPU view and filter without a request; new captures discard that selection and cache", () => {
    const script = extractInlineScript(renderHtml(target, "nonce", 120));
    const navigation = script.slice(script.indexOf("let selectedWorkspaceEvidence ="),
        script.indexOf("function selectResultTab(selected)"));
    const activationStart = script.indexOf("function activateCaptureTab(name) {");
    const activationEnd = script.indexOf("    function setGenericCaptureOwner", activationStart);
    const activation = script.slice(activationStart, activationEnd);
    const profile = script.slice(script.indexOf("const profileAvailability ="),
        script.indexOf("const counters = new Map()"));
    const captureStart = script.indexOf("cpuEvidenceId = typeof message.evidenceId");
    const capture = script.slice(captureStart, script.indexOf("setWorkspaceEvidence('cpu');", captureStart)
        + "setWorkspaceEvidence('cpu');".length);
    const { document, makeContainer } = createDocumentStub();
    const names = ["workspaceDomain", "workspaceEvidenceKind", "workspaceEvidenceStatus", "workspaceView",
        "workspaceLoadView", "workspaceGcCapture", "workspaceGcStatus", "cpuQueryViews", "cpuQueryStatus",
        "openHeapDetails", "backToHeapTypes", "captureGc", "resultCpu", "resultAllocation", "resultHeap",
        "resultThreads", "cpuSamplingDisclaimer", "workspaceCanvas", "workspaceCaptureStatus",
        "workspaceThreadViewStatus", "genericCaptureViews", "captureThreadHeadline", "captureThreadTable",
        "threadQueryViews", "threadDetails", "threadStaticsForm", "jointReproduction",
        "workspaceRequestCapture", "captureRequestEvidence"];
    const nodes = Object.fromEntries(names.map(id => [id, makeContainer(id)]));
    nodes.captureThreadTable.closest = () => nodes.threadDetails;
    document.querySelector = selector => selector === "#resultThreads .statics-form" ? nodes.threadStaticsForm : null;
    const requests = [], renders = [];
    const api = new Function("document", "vscode", "renders", `
      const {${names.join(",")}} = Object.fromEntries(${JSON.stringify(names)}.map(id=>[id,document.getElementById(id)]));
      const workspacePanels = {cpu:resultCpu,allocation:resultAllocation,heap:resultHeap,'thread-snapshot':resultThreads};
      const isThreadCapture = kind => ['off_cpu','contention','threadpool'].includes(kind);
      const isRequestCapture = kind => ['requests','kestrel','db','networking'].includes(kind);
      const usesWorkspaceForGenericCapture = kind => ['gc','gc-activities'].includes(kind)
        || isThreadCapture(kind) || isRequestCapture(kind);
      const genericCaptureLabels = {requests:'In-flight requests',kestrel:'Kestrel',db:'Database',networking:'Networking'};
      let genericCaptureKind;
      let activeResultTab;
      const selectResultTab = name => { activeResultTab = name; };
      const threadSnapshotViewLabels = {snapshot:'Inline thread summary','thread-explorer':'Thread explorer',
        deadlocks:'Inferred deadlock candidates','unique-stacks':'Grouped snapshot stacks',
        'wait-chains':'Wait chains',threadpool:'Snapshot thread-pool query','thread-statics':'Thread statics'};
      const threadQueryStates = {
        deadlocks:{state:'not-requested',count:0},'unique-stacks':{state:'not-requested'},
        'wait-chains':{state:'not-requested'},threadpool:{state:'not-requested'}};
      let threadSnapshotEvidenceId = "thread-1";
      let threadSnapshotQueryAvailable = true;
      let threadSnapshotView = 'snapshot';
      const cpuQueryLabels = {'caller-callee':'Callers / callees'};
      const cpuProfileState = {}, cpuBaselineProfileState = {}, allocationProfileState = {};
      let cpuProfileResult, cpuBaselineProfileResult, cpuBaselineCaptureSummary, cpuComparisonResult;
      const renderCapture = () => {};
      const renderCpuQuery = (...args) => renders.push(args);
      ${navigation}
      ${activation}
      ${profile}
      return {setWorkspaceEvidence,activateCaptureTab,getActiveResultTab:()=>activeResultTab,
        selectThreadSnapshotView,selectProfileView,cacheProfileQuery,loadSelectedProfileView,
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
    api.setWorkspaceEvidence("thread-snapshot");
    assert.equal(nodes.workspaceDomain.value, "threads");
    assert.deepEqual(nodes.workspaceEvidenceKind.children.map(option => option.value),
        ["thread-snapshot", "off_cpu", "contention", "threadpool"]);
    assert.equal(nodes.workspaceView.value, "snapshot");
    api.selectThreadSnapshotView("thread-statics");
    assert.match(nodes.workspaceThreadViewStatus.textContent, /explicitly query this snapshot/);
    assert.deepEqual(requests, [{type:"queryCpu",evidenceId:"cpu-1",view:"top-methods"}],
        "switching thread views remains local and never issues an implicit query");
    for (const kind of ["requests", "kestrel", "db", "networking"]) {
        api.activateCaptureTab(kind);
        assert.equal(api.getActiveResultTab(), "Workspace");
        assert.equal(nodes.workspaceDomain.value, "requests");
        assert.deepEqual(nodes.workspaceEvidenceKind.children.map(option => option.value),
            ["requests", "request-activities", "kestrel", "db", "networking"]);
        assert.equal(nodes.workspaceEvidenceKind.value, kind);
        assert.equal(nodes.workspaceView.value, "capture-result");
        assert.equal(nodes.workspaceView.disabled, true, "generic results have no retained query view");
        assert.equal(nodes.workspaceLoadView.hidden, true, "navigation has no synthetic load action");
        assert.match(nodes.workspaceEvidenceStatus.textContent, /No completed evidence/);
        assert.equal(nodes.workspaceRequestCapture.hidden, false);
        assert.equal(nodes.jointReproduction.hidden, true);
        assert.match(nodes.captureRequestEvidence.attributes["aria-label"], /^Capture /);
    }
    api.setWorkspaceEvidence("request-activities");
    assert.equal(nodes.workspaceRequestCapture.hidden, true);
    assert.equal(nodes.jointReproduction.hidden, false, "the existing joint reproduction remains available in its evidence context");
    assert.deepEqual(requests, [{type:"queryCpu",evidenceId:"cpu-1",view:"top-methods"}],
        "request-family navigation performs no CLI work");
});

test("all nine generic capture kinds keep one truthful replaceable result slot in Workspace", () => {
    const script = extractInlineScript(renderHtml(target, "nonce", 120));
    const start = script.indexOf("function setGenericCaptureOwner(kind, evidenceId) {");
    const end = script.indexOf("    const cpuQueryLabels = {", start);
    assert.ok(start >= 0 && end > start);
    const source = script.slice(start, end);
    const statuses = new Map();
    const makeNode = () => ({
        dataset: {}, hidden: false, parentElement: null,
        appendChild(child) { child.parentElement = this; return child; },
    });
    const workspaceCanvas = makeNode(), genericCaptureViews = makeNode();
    const workspaceCaptureStatus = { textContent: "" }, workspaceGcStatus = { textContent: "" };
    const replaceOwner = new Function("workspaceCanvas", "genericCaptureViews", "workspaceCaptureStatus",
        "workspaceGcStatus", "setWorkspaceStatus", "usesWorkspaceForGenericCapture", `
          let genericCaptureKind;
          let selectedWorkspaceEvidence = "gc";
          const genericCaptureLabels = {gc:"Garbage collections","gc-activities":"GC and activities",
            off_cpu:"Off-CPU blocking",contention:"Lock contention",threadpool:"Thread-pool capture",
            networking:"Networking",requests:"In-flight requests",kestrel:"Kestrel",db:"Database"};
          const isRequestCapture = kind => ["requests", "kestrel", "db", "networking"].includes(kind);
          const selectEvidence = kind => { selectedWorkspaceEvidence = kind; };
          ${source}
          return {setGenericCaptureOwner,getOwner:()=>genericCaptureKind,selectEvidence};
        `)(workspaceCanvas, genericCaptureViews, workspaceCaptureStatus, workspaceGcStatus,
        (kind, status) => statuses.set(kind, status),
        kind => ["gc", "gc-activities", "off_cpu", "contention", "threadpool",
            "requests", "kestrel", "db", "networking"].includes(kind));

    replaceOwner.setGenericCaptureOwner("gc", "gc-1");
    assert.equal(genericCaptureViews.parentElement, workspaceCanvas);
    assert.equal(genericCaptureViews.dataset.evidenceId, "gc-1");
    assert.equal(genericCaptureViews.hidden, false);
    replaceOwner.setGenericCaptureOwner("contention", "contention-1");
    assert.equal(replaceOwner.getOwner(), "contention");
    assert.equal(genericCaptureViews.parentElement, workspaceCanvas);
    assert.equal(genericCaptureViews.hidden, true, "late results do not appear under a different selected evidence kind");
    assert.match(statuses.get("gc"), /no longer current|replaced/i);
    replaceOwner.selectEvidence("contention");
    replaceOwner.setGenericCaptureOwner("networking", "networking-1");
    assert.equal(genericCaptureViews.parentElement, workspaceCanvas);
    assert.equal(genericCaptureViews.dataset.evidenceId, "networking-1");
    assert.match(statuses.get("contention"), /no longer current|replaced/i);
    assert.equal(genericCaptureViews.hidden, true);
    replaceOwner.setGenericCaptureOwner("requests", "requests-1");
    assert.equal(genericCaptureViews.parentElement, workspaceCanvas);
    assert.equal(genericCaptureViews.dataset.evidenceId, "requests-1");
    assert.match(statuses.get("networking"), /replaced by In-flight requests/i);
    replaceOwner.setGenericCaptureOwner("kestrel", "kestrel-1");
    assert.equal(genericCaptureViews.dataset.evidenceId, "kestrel-1");
    assert.match(statuses.get("requests"), /replaced by Kestrel/i);
    replaceOwner.setGenericCaptureOwner("db", "db-1");
    assert.equal(genericCaptureViews.dataset.evidenceId, "db-1");
    assert.match(statuses.get("kestrel"), /replaced by Database/i);
    assert.equal(replaceOwner.setGenericCaptureOwner("unknown", "unknown-1"), false);
    assert.match(workspaceCaptureStatus.textContent, /Unsupported generic capture kind/);
});

test("request-family failures preserve the current result and late completions cannot replace it", () => {
    const script = extractInlineScript(renderHtml(target, "nonce", 120));
    const routeStart = script.indexOf("} else if (message.type === 'captureStatus' && message.kind === 'diagnostics') {");
    const routeEnd = script.indexOf("      } else if (message.type === 'captureStatus' && message.kind === 'heap')", routeStart);
    const route = script.slice(routeStart, routeEnd).replace(/^\s*}\s*else if/, "if") + " }";
    const ownerStart = script.indexOf("function setGenericCaptureOwner(kind, evidenceId) {");
    const ownerEnd = script.indexOf("    const cpuQueryLabels = {", ownerStart);
    const owner = script.slice(ownerStart, ownerEnd);
    assert.ok(routeStart >= 0 && routeEnd > routeStart && ownerStart >= 0 && ownerEnd > ownerStart);

    const makeNode = () => ({
        dataset: {}, hidden: false, disabled: false, parentElement: null, textContent: "",
        appendChild(child) { child.parentElement = this; return child; },
        replaceChildren() {},
    });
    const workspaceCanvas = makeNode(), genericCaptureViews = makeNode();
    const workspaceCaptureStatus = makeNode(), workspaceGcStatus = makeNode();
    const statuses = new Map();
    const document = { getElementById: () => makeNode() };
    const api = new Function("document", "workspaceCanvas", "genericCaptureViews",
        "workspaceCaptureStatus", "workspaceGcStatus", "setWorkspaceStatus", `
          let diagnosticsRequestId;
          let genericCaptureKind;
          let activeCapture;
          let selectedWorkspaceEvidence = "cpu";
          const genericCaptureLabels = {gc:"Garbage collections","gc-activities":"GC and activities",
            off_cpu:"Off-CPU blocking",contention:"Lock contention",threadpool:"Thread-pool capture",
            networking:"Networking",requests:"In-flight requests",kestrel:"Kestrel",db:"Database"};
          const isRequestCapture = kind => ["requests","kestrel","db","networking"].includes(kind);
          const usesWorkspaceForGenericCapture = kind => ["gc","gc-activities","off_cpu","contention",
            "threadpool","requests","kestrel","db","networking"].includes(kind);
          const activateCaptureTab = kind => { activeCapture = kind; };
          const renderCaptureView = () => {};
          const jointView = {reset() {}};
          ${owner}
          function handleMessage(message) { ${route} }
          return {handleMessage, getOwner:()=>genericCaptureKind, getActiveCapture:()=>activeCapture,
            selectEvidence:kind=>{selectedWorkspaceEvidence=kind;},selectActiveCapture:kind=>{activeCapture=kind;}};
        `)(document, workspaceCanvas, genericCaptureViews,
        workspaceCaptureStatus, workspaceGcStatus, (kind, message) => statuses.set(kind, message));

    const status = (evidenceId, captureKind, state) => api.handleMessage({
        type: "captureStatus", kind: "diagnostics", evidenceId, captureKind, state, message: state,
    });
    const result = (evidenceId, captureKind) => api.handleMessage({
        type: "capture", kind: "diagnostics", evidenceId, captureKind, result: { kind: captureKind },
    });
    status("networking-1", "networking", "starting");
    assert.equal(api.getActiveCapture(), "networking");
    status("networking-1", "networking", "running");
    api.selectEvidence("cpu");
    api.selectActiveCapture("user-selected-cpu");
    result("networking-1", "networking");
    assert.equal(api.getOwner(), "networking");
    assert.equal(genericCaptureViews.dataset.evidenceId, "networking-1");
    assert.equal(api.getActiveCapture(), "user-selected-cpu", "completion does not steal the selected view");
    assert.equal(genericCaptureViews.hidden, true, "background completion does not display under CPU");
    status("requests-failed", "requests", "running");
    status("requests-failed", "requests", "error");
    assert.equal(api.getOwner(), "networking", "a failed request capture does not replace valid evidence");
    assert.equal(genericCaptureViews.dataset.evidenceId, "networking-1");
    assert.match(statuses.get("requests"), /running|error/i);
    result("networking-1", "networking");
    assert.equal(api.getOwner(), "networking", "a completion from the previous request is ignored");
    assert.equal(genericCaptureViews.dataset.evidenceId, "networking-1");
    status("contention-2", "contention", "starting");
    status("contention-2", "contention", "running");
    api.selectActiveCapture("Monitoring");
    api.selectEvidence("cpu");
    result("contention-2", "contention");
    assert.equal(api.getOwner(), "contention");
    assert.equal(genericCaptureViews.dataset.evidenceId, "contention-2");
    assert.equal(api.getActiveCapture(), "Monitoring");
    assert.equal(genericCaptureViews.hidden, true);
    result("requests-failed", "requests");
    assert.equal(api.getOwner(), "contention", "a late failed-request completion cannot overwrite newer evidence");
    assert.equal(genericCaptureViews.dataset.evidenceId, "contention-2");
    status("unknown-1", "not-a-capture", "error");
    assert.match(workspaceCaptureStatus.textContent, /Unsupported generic capture kind/);
    result("unknown-1", "not-a-capture");
    assert.equal(api.getOwner(), "contention", "unsupported kinds cannot replace the current result");
    status(undefined, "networking", "error");
    assert.match(workspaceCaptureStatus.textContent, /omitted its evidence ID/);
    result(undefined, "networking");
    assert.equal(api.getOwner(), "contention", "uncorrelated results cannot replace the current result");
});

function cpuRenderer() {
    const html = renderHtml(target, "nonce123", 120);
    const script = extractInlineScript(html);
    const start = script.indexOf("const cpuQueryLabels = {");
    const end = script.indexOf("const profileAvailability = {", start);
    assert.ok(start >= 0 && end > start, "CPU renderer source should be present");
    const source = script.slice(start, end);
    const { document, makeContainer } = createDocumentStub();
    const createElement = document.createElement;
    document.createElement = tagName => {
        const element = createElement(tagName);
        if (tagName === "tr") element.tagName = "TR";
        return element;
    };
    const cpuQueryViews = makeContainer("cpuQueryViews");
    const allocationContainer = makeContainer("allocationQueryViews");
    const requests = [];
    // eslint-disable-next-line no-new-func -- tests the renderer emitted into the webview.
    const { renderCpuQuery, state } = new Function("document", "cpuQueryViews", "vscode",
        `${CPU_PROFILE_SCRIPT}
        ${CPU_INVESTIGATION_OVERVIEW_SCRIPT}
        ${CPU_COMPARISON_SCRIPT}
        let cpuProfileResult;
        let cpuBaselineProfileResult;
        let cpuComparisonResult;
        const cpuProfileState = { mode: 'tree', search: '', focus: [], selected: undefined };
        const cpuBaselineProfileState = { mode: 'tree', search: '', focus: [], selected: undefined };
        let cpuCaptureSummary;
        let cpuBaselineCaptureSummary;
        let allocationProfileResult;
        const allocationProfileState = { mode: 'tree', search: '', focus: [], selected: undefined };
        let cpuEvidenceId;
        let cpuBaselineEvidenceId;
        let activeCpuProfileRole = 'current';
        let allocationEvidenceId;
        const openCpuProfile = () => {};
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

function cpuOverviewRenderer(result, capture) {
    const { document, makeContainer } = createDocumentStub();
    const container = makeContainer("cpuOverview");
    const profileViews = [];
    const evidence = [];
    // eslint-disable-next-line no-new-func -- exercises the exact script emitted into the webview.
    const render = new Function("document",
        `${CPU_INVESTIGATION_OVERVIEW_SCRIPT}; return renderCpuInvestigationOverview;`)(document);
    render(container, result, capture, {
        openProfileView: (view, rootMethodFilter) => profileViews.push({ view, rootMethodFilter }),
        openEvidence: kind => evidence.push(kind),
    });
    return { container, profileViews, evidence };
}

function cpuComparisonRenderer(result) {
    const { document, makeContainer } = createDocumentStub();
    const container = makeContainer("cpuComparison");
    const profiles = [];
    let reset = 0;
    // eslint-disable-next-line no-new-func -- exercises the exact script emitted into the webview.
    const render = new Function("document",
        `${CPU_COMPARISON_SCRIPT}; return renderCpuComparison;`)(document);
    render(container, result, {
        openProfile: (role, view, method) => profiles.push({ role, view, method }),
        useCurrentAsNextBaseline: () => { reset++; },
    });
    return { container, profiles, reset: () => reset };
}

test("CPU comparison exposes roles, evidence limits, bounded movers and specialist return actions", () => {
    const capture = (handle, startedAt) => ({
        handle, processId: 42, startedAt, durationSeconds: 10, totalSamples: 100,
        backend: "EventPipeSampleProfiler", evidenceKind: "StackFrequencyWithHeuristicWaits",
        origin: "live", producingTool: "collect_sample", expiresAt: "2026-10-26T14:00:00Z",
        unresolvedExclusiveSamples: 5, unresolvedExclusivePercent: 5,
        quality: { schema: "dotnet-diagnostics/evidence-quality/v1",
            limitations: [{ category: "Sampling", scope: "capture", message: "Sampled stacks only." }] },
    });
    const result = {
        handle: "current", baselineHandle: "baseline", view: "diff",
        data: {
            schema: "dotnet-diagnostics/cpu-sample-handle-comparison/v1",
            baseline: capture("baseline", "2026-10-26T12:00:00Z"),
            current: capture("current", "2026-10-26T12:01:00Z"),
            compatibility: { status: "qualified", reasons: [] },
            metrics: [{ name: "samplePopulation", population: "retained-samples", aggregation: "total",
                normalization: "none", delta: { baselineValue: 100, currentValue: 120, unit: "samples",
                    deltaAbs: 20, relativeDelta: .2, betterDirection: "neutral", outcome: "up" } }],
            topMovers: [
                { displayName: "Orders.Price()", baselineExclusiveSamples: 20, currentExclusiveSamples: 30,
                    baselineTotalSamples: 100, currentTotalSamples: 120, unresolved: false,
                    exclusiveShare: { baselineValue: 20, currentValue: 25, unit: "%", deltaAbs: 5,
                        relativeDelta: .25, betterDirection: "neutral", outcome: "up",
                        baselineDenominator: 100, currentDenominator: 120, deltaUnit: "percentage-points" } },
                { displayName: "Orders.NewPath()", baselineExclusiveSamples: null, currentExclusiveSamples: 10,
                    baselineTotalSamples: 100, currentTotalSamples: 120, unresolved: false,
                    exclusiveShare: { baselineValue: null, currentValue: 8.3333, unit: "%", deltaAbs: null,
                        relativeDelta: null, betterDirection: "neutral", outcome: "unavailable",
                        unavailableReason: "MissingBaselineObservation", baselineDenominator: 100,
                        currentDenominator: 120, deltaUnit: "percentage-points" } },
            ],
            notes: ["Workload equivalence and causation are not established."],
        },
    };
    const rendered = cpuComparisonRenderer(result);
    const heading = elements(rendered.container, element => element.tagName === "h3")[0];
    assert.equal(heading.tabIndex, -1);
    assert.match(rendered.container.textContent, /Baseline \(earlier\).*Current \(later\)/);
    assert.match(rendered.container.textContent, /BackendEventPipeSampleProfiler/);
    assert.match(rendered.container.textContent, /EvidenceStackFrequencyWithHeuristicWaits/);
    assert.match(rendered.container.textContent, /20 samples \/ 20 % of 100/);
    assert.match(rendered.container.textContent, /Added to current retained projection/);
    assert.match(rendered.container.textContent, /MissingBaselineObservation/);
    assert.match(rendered.container.textContent, /distinct from durable saved-capture comparison/);
    assert.doesNotMatch(rendered.container.textContent, /improved|regressed|application time|runtime time/i);

    click(rendered.container, "Baseline callers / callees");
    click(rendered.container, "Open current call tree");
    click(rendered.container, "Use current as baseline for the next capture");
    assert.deepEqual(rendered.profiles, [
        { role: "baseline", view: "caller-callee", method: "Orders.Price()" },
        { role: "current", view: "call-tree", method: undefined },
    ]);
    assert.equal(rendered.reset(), 1);
});

test("CPU overview distinguishes OS on-CPU evidence and keeps profile actions navigational", () => {
    const { container, profileViews, evidence } = cpuOverviewRenderer({
        summary: "Backend summary.",
        data: {
            totalSamples: 100,
            evidenceBackend: "LinuxPerf",
            evidenceKind: "OsOnCpuSamples",
            selfSamples: { runningSamples: 90, waitingSamples: 0, unknownSamples: 10 },
            topBusyMethods: [{
                method: "OrdersApi.Price()", module: "OrdersApi.dll",
                exclusiveSamples: 60, inclusiveSamples: 70,
                selfSamples: { runningSamples: 60, waitingSamples: 0, unknownSamples: 0 },
            }],
            hotPathLeaf: {
                method: "OrdersApi.Price()", module: "OrdersApi.dll",
                inclusiveSamples: 70, exclusiveSamples: 60,
            },
            hotPathDepth: 3,
            topWaitCategories: [],
        },
    }, {
        processId: 42, startedAt: "2026-10-26T12:00:00Z", duration: "00:00:10", totalSamples: 100,
    });

    assert.match(container.textContent, /OrdersApi\.Price\(\) is the top observed on-CPU method/);
    assert.match(container.textContent, /Evidence status: observed on-CPU samples/);
    assert.match(container.textContent, /Observed on-CPU: 90/);
    assert.match(container.textContent, /Application\/framework\/runtime\/GC ownership composition/);
    assert.doesNotMatch(container.textContent, /measured CPU utilization/);
    const bar = elements(container, element => element.classes.has("cpu-overview-bar"))[0];
    assert.match(bar.getAttribute("aria-label"), /90 on-CPU, 0 heuristic-wait, 10 unknown/);

    click(container, "Inspect callers and callees");
    click(container, "Open call tree");
    click(container, "Allocations");
    assert.deepEqual(profileViews, [
        { view: "caller-callee", rootMethodFilter: "OrdersApi.Price()" },
        { view: "call-tree", rootMethodFilter: undefined },
    ]);
    assert.deepEqual(evidence, ["allocation"]);
    assert.doesNotMatch(CPU_INVESTIGATION_OVERVIEW_SCRIPT, /postMessage|captureCpu|captureDiagnostics/);
    assert.match(CPU_INVESTIGATION_OVERVIEW_STYLES, /@media \(max-width:650px\)/);
    assert.match(CPU_INVESTIGATION_OVERVIEW_STYLES, /\.cpu-overview-actions button \{ flex:1 1 12rem/);
});

test("CPU overview labels EventPipe waits as inferred and abstains when unknown evidence dominates", () => {
    const { container } = cpuOverviewRenderer({
        data: {
            totalSamples: 100,
            evidenceBackend: "EventPipeSampleProfiler",
            evidenceKind: "StackFrequencyWithHeuristicWaits",
            selfSamples: { runningSamples: 0, waitingSamples: 20, unknownSamples: 80 },
            topBusyMethods: [{
                method: "Worker.Run()", module: "Worker.dll",
                exclusiveSamples: 30, inclusiveSamples: 50,
            }],
            topWaitCategories: [{
                waitReason: "ThreadPool worker idle wait", exclusiveSamples: 20, exclusivePercent: 20,
            }],
        },
    }, { totalSamples: 100, duration: "00:00:10" });

    assert.match(container.textContent, /Scheduler state is unknown for 80 of 100 captured samples/);
    assert.match(container.textContent, /Evidence status: limited by unknown scheduler state/);
    assert.match(container.textContent, /Wait labels are name-based heuristics/);
    assert.match(container.textContent, /On-CPU state: not established by this backend/);
    assert.doesNotMatch(container.textContent, /Observed on-CPU: 0/);
    assert.match(container.textContent, /Top name-based heuristic wait: ThreadPool worker idle wait/);
    assert.match(container.textContent, /does not distinguish whether unknown scheduler state came from unmatched, unresolved, or native leaves/);
    assert.doesNotMatch(container.textContent, /application time|framework time|managed\/native/);
    const bar = elements(container, element => element.classes.has("cpu-overview-bar"))[0];
    assert.match(bar.getAttribute("aria-label"), /on-CPU state not established/);
    assert.doesNotMatch(bar.getAttribute("aria-label"), /0 on-CPU/);
});

test("CPU overview presents empty and unattributable captures without inventing zero evidence", () => {
    const empty = cpuOverviewRenderer({
        data: {
            totalSamples: 0,
            evidenceBackend: "EventPipeSampleProfiler",
            evidenceKind: "StackFrequencyWithHeuristicWaits",
            selfSamples: { runningSamples: 0, waitingSamples: 0, unknownSamples: 0 },
        },
    }, { totalSamples: 0 }).container;
    assert.match(empty.textContent, /No stack samples were captured/);
    assert.doesNotMatch(empty.textContent, /most frequent attributable leaf/);

    const degraded = cpuOverviewRenderer({
        data: {
            totalSamples: 25,
            evidenceBackend: "LegacyUnknown",
            evidenceKind: "LegacyUnknown",
        },
    }, { totalSamples: 25 }).container;
    assert.match(degraded.textContent, /25 samples were captured, but no attributable method or loaded hot path was returned/);
    assert.match(degraded.textContent, /No heuristic wait category was returned; this is not evidence that waiting was zero/);
});

test("allocation explorer uses estimated bytes, separate profile state and allocation query messages", () => {
    const { renderCpuQuery, allocationContainer, container, requests } = cpuRenderer();
    const result = { weightUnit: "allocation-tick-estimated-bytes", data: { totalSamples: 1000,
        root: cpuNode("Root", 800, 200, [cpuNode("Allocate", 600, 600)]) } };
    renderCpuQuery("call-tree", result, false, "allocation");
    assert.match(allocationContainer.textContent, /estimated allocation bytes/);
    assert.match(allocationContainer.textContent, /800 of 1000 estimated bytes \(80.0%\)/);
    assert.doesNotMatch(allocationContainer.textContent, /heuristic-wait|on-CPU \//);
    selectCpuPath(allocationContainer, "");
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
    assert.match(container.textContent, /Methods ranked by sampled time/);
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

function clickByLabel(root, label) {
    const button = elements(root, child => child.tagName === "button" && child.getAttribute("aria-label") === label)[0];
    assert.ok(button, `button with accessible name ${label} must exist`);
    button.listeners.click({});
}

function cpuNode(method, inclusiveSamples, exclusiveSamples, children = []) {
    return { frame: { method, module: "App" }, inclusiveSamples, exclusiveSamples, children };
}

function selectCpuPath(container, path) {
    const row = elements(container, e => e.attributes["data-node-path"] === path)[0];
    row.querySelector(".cpu-profile-method").listeners.click();
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
    selectCpuPath(container, "");
    click(container, "Callers / callees");
    assert.deepEqual(requests, [{
        type: "queryCpu",
        evidenceId: undefined,
        view: "caller-callee",
        rootMethodFilter: "App.Main()",
    }]);
});

test("CPU visualization follows essential controls and keeps active partial-data warnings prominent", () => {
    const { renderCpuQuery, container } = cpuRenderer();
    renderCpuQuery("call-tree", { data: { totalSamples: 100, truncated: true,
        root: cpuNode("Root", 100, 10, [cpuNode("Work", 90, 90)]),
    } }, false);

    const all = elements(container, () => true);
    const indexOf = predicate => all.findIndex(predicate);
    const visualization = indexOf(element => element.className === "cpu-profile-visualization");
    const search = indexOf(element => element.tagName === "input");
    const warning = indexOf(element => element.className === "cpu-profile-warning");
    const selection = indexOf(element => element.className === "cpu-profile-selection");
    const guidance = indexOf(element => element.className === "cpu-profile-guidance");
    assert.ok(search < warning && warning < visualization,
        "search and any active partial-projection warning precede the data");
    assert.equal(all[warning].getAttribute("role"), "alert");
    assert.ok(visualization < selection && visualization < guidance,
        "the call tree precedes secondary actions and visible technical guidance");
    assert.match(all[guidance].textContent, /inclusive sample counts, not elapsed time or measured CPU utilization/);
    assert.match(all[guidance].textContent, /live baseline\/current comparison is available only while/);
    assert.match(all[guidance].textContent, /durable saved-capture comparison remains separate/);
    const comparisonLink = elements(all[guidance], element => element.tagName === "a")[0];
    assert.equal(comparisonLink.getAttribute("href"),
        "https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/historical-comparisons.md#supported-matrix");
    assert.equal(comparisonLink.getAttribute("target"), "_blank");
    assert.equal(comparisonLink.getAttribute("rel"), "noopener noreferrer");
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
    selectCpuPath(container, "1");
    click(container, "Focus branch");
    assert.deepEqual(state.focus, [1]);
    assert.equal(elements(container, e => e.attributes["data-node-path"] === "1.0")[0].hidden, false,
        "focusing a collapsed branch opens its loaded children");
    const focusedMethod = elements(container, e => e.className === "cpu-profile-method" && e.focused)[0];
    assert.equal(focusedMethod, elements(container, e => e.attributes["data-node-path"] === "1")[0]
        .querySelector(".cpu-profile-method"),
        "focus returns to the focused branch's method button rather than a row or disclosure control");
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
    selectCpuPath(container, "0");
    click(container, "Focus branch");
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
    click(container, "Call tree");
    selectCpuPath(container, "0");
    click(container, "Focus branch");
    const queryButtons = elements(container, element =>
        element.textContent === "Callers / callees" || element.textContent === "Query subtree by method");
    assert.equal(queryButtons.length, 2);
    assert.ok(queryButtons.every(button => button.getAttribute("aria-disabled") === "true"));
    assert.ok(queryButtons.every(button => button.title === "Capture again for CLI queries."));
    click(container, "Query subtree by method");
    assert.deepEqual(requests, []);
    container.setAttribute("data-profile-query-available", "true");
    click(container, "Reset focus");
    selectCpuPath(container, "");
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
    selectCpuPath(container, "");
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
    assert.equal(state.selected,undefined);
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

test("CPU contextual actions select exact occurrences by mouse and keyboard without focus, expansion or requests", () => {
    const {renderCpuQuery,container,state,requests}=cpuRenderer();
    const result={data:{totalSamples:100,root:cpuNode("Root",100,0,[
        cpuNode("Same.Name",60,10,[cpuNode("Leaf",50,50)]),
        cpuNode("Same.Name",40,40),
    ])}};
    renderCpuQuery("call-tree",result,false);
    const context=container.querySelector(".cpu-profile-selection");
    assert.match(context.textContent,/Select a row for branch actions/);
    assert.equal(context.querySelector("button").disabled,true);
    assert.equal(elements(container,e=>e.textContent==="Callers / callees").length,0);
    const rows=elements(container,e=>e.attributes["data-node-path"]!==undefined);
    assert.ok(rows.every(row => row.attributes.tabindex === undefined),
        "semantic table rows do not add a redundant keyboard tab stop");
    assert.equal(elements(container.querySelector(".cpu-call-tree"),e=>e.textContent==="Focus"||e.textContent==="Hotpath").length,0);
    const expanded=[...state.expanded ?? [""]];
    const firstMethod = rows[1].querySelector(".cpu-profile-method");
    assert.equal(firstMethod.tagName, "button", "the method action remains natively keyboard-activatable");
    firstMethod.listeners.click();
    assert.deepEqual(state.selected,[0]);
    assert.equal(rows[1].getAttribute("aria-selected"),"true");
    assert.match(context.textContent,/Selected branch: Same.Name/);
    assert.equal(context.querySelector(".cpu-profile-selected-name").title,"Same.Name");
    const second=rows[3];
    second.querySelector(".cpu-profile-method").listeners.click();
    assert.deepEqual(state.selected,[1],"same-name occurrence has its own path identity");
    assert.equal(rows[1].getAttribute("aria-selected"),"false");
    assert.equal(second.getAttribute("aria-selected"),"true");
    assert.deepEqual(state.focus,[]);
    assert.deepEqual(state.expanded ?? [""],expanded);
    assert.deepEqual(requests,[]);
    const expand=elements(context,e=>e.tagName==="button"&&e.textContent==="Expand hotpath")[0];
    expand.listeners.click();
    assert.equal(container.querySelector(".cpu-profile-selection"),context,"context is stable while tree redraws");
    assert.equal(elements(context,e=>e.textContent==="Expand hotpath")[0],expand,"local control keeps its identity/focus");
    assert.deepEqual(state.selected,[1]);
    renderCpuQuery("call-tree",result,false);
    assert.deepEqual(state.selected,[1]);
    assert.match(container.querySelector(".cpu-profile-selection").textContent,/40.0% of capture/);
    click(container,"Focus branch");
    assert.deepEqual(state.focus,[1]);
    assert.deepEqual(requests,[]);
    click(container,"Reset focus");
    assert.equal(state.selected,undefined);
    assert.equal(container.querySelector(".cpu-profile-selection").querySelector("button").disabled,true);
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
    selectCpuPath(container, "");
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
    click(container, "Call tree");
    selectCpuPath(container, "");
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
    clickByLabel(container, "2026-10-14T00:00:01Z | System.Exception | thread 1");
    assert.match(container.textContent, /Sample.Throw\(\)/);
    clickByLabel(container, "2026-10-14T00:00:02Z | System.Exception | thread 1");
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
    assert.match(container.textContent, /EventPipe readiness is not reported/);
    assert.match(container.textContent, /repeat the problem throughout a new capture/);
    assert.ok(!container.textContent.includes(message), "new results replace old sensitive messages");
});

test("live webview wires exception evidence into the workspace without a duplicate result tab", () => {
    const html = renderHtml(target, "nonce123", 120);
    const script = extractInlineScript(html);
    assert.match(html, /id="captureExceptions"/);
    assert.match(html, /Sending a capture clears the current result; if collection then fails, it cannot be restored/);
    assert.match(html, /Exception results are temporary and are not saved/);
    assert.match(html, /id="captureExceptionsStatus" role="status"/);
    assert.match(html, /id="exceptionEvidenceIdentity" class="workspace-evidence-status" role="status"/);
    assert.match(script, /captureExceptionsButton\.addEventListener\('click', \(\) => vscode\.postMessage\(\{ type: 'captureExceptions' \}\)\)/);
    assert.match(script, /message\.type === 'captureStatus' && message\.kind === 'exceptions'/);
    assert.match(script, /message\.evidenceId !== pendingExceptionEvidenceId/);
    assert.match(script, /renderQueryView\('exceptionCaptureViews', 'exceptions', message\.result, false\)/);
    const navigationHandler = /workspaceDomain\.addEventListener\('change', \(\) => ([\s\S]*?)\)\);/.exec(script);
    assert.ok(navigationHandler);
    assert.doesNotMatch(navigationHandler[1], /postMessage|capture/);
    const statusBranch = /message\.type === 'captureStatus' && message\.kind === 'exceptions'\) \{([\s\S]*?)\n      \} else/.exec(script);
    assert.ok(statusBranch);
    function statusHarness(initial = {}) {
        const state = {
            pendingExceptionEvidenceId: undefined,
            exceptionEvidenceId: "previous-evidence",
            exceptionEvidenceMode: "crash-guard",
            ...initial,
        };
        const activated = [];
        const statuses = [];
        const views = { clears: 0, replaceChildren() { this.clears++; } };
        const panel = { dataset: {} };
        const identity = { textContent: "" };
        const elements = new Map([
            ["captureExceptionsStatus", { textContent: "" }],
            ["captureExceptionsButton", { disabled: false }],
            ["exceptionCaptureViews", views],
            ["resultExceptions", panel],
            ["exceptionEvidenceIdentity", identity],
        ]);
        const dispatch = new Function("message", "state", "deps", `
            let pendingExceptionEvidenceId = state.pendingExceptionEvidenceId;
            let exceptionEvidenceId = state.exceptionEvidenceId;
            let exceptionEvidenceMode = state.exceptionEvidenceMode;
            const { exceptionCaptureModes, captureExceptionsStatus, captureExceptionsButton,
                activateCaptureTab, document, setWorkspaceStatus, selectedWorkspaceEvidence,
                setWorkspaceEvidence } = deps;
            ${statusBranch[1]}
            Object.assign(state, { pendingExceptionEvidenceId, exceptionEvidenceId, exceptionEvidenceMode });
        `);
        const deps = {
            exceptionCaptureModes: { exceptions: "Managed exceptions", "crash-guard": "Crash guard" },
            captureExceptionsStatus: elements.get("captureExceptionsStatus"),
            captureExceptionsButton: elements.get("captureExceptionsButton"),
            activateCaptureTab: name => activated.push(name),
            document: { getElementById: id => elements.get(id) },
            setWorkspaceStatus: (...args) => statuses.push(args),
            selectedWorkspaceEvidence: "cpu",
            setWorkspaceEvidence() { assert.fail("status must not change the selected domain"); },
        };
        return { state, activated, statuses, views, panel, identity, elements, dispatch, deps };
    }

    const harness = statusHarness();
    harness.dispatch({
        state: "starting", evidenceId: "new-request", captureKind: "crash-guard", message: "Preparing",
    }, harness.state, harness.deps);
    assert.deepEqual(harness.activated, ["exceptions"]);
    assert.equal(harness.views.clears, 0);
    assert.equal(harness.state.exceptionEvidenceId, "previous-evidence");
    harness.dispatch({
        state: "running", evidenceId: "old-request", captureKind: "exceptions", message: "Stale",
    }, harness.state, harness.deps);
    assert.equal(harness.views.clears, 0, "late status from an older request cannot clear current evidence");
    harness.dispatch({
        state: "error", evidenceId: "new-request", captureKind: "crash-guard", message: "Busy",
    }, harness.state, harness.deps);
    assert.equal(harness.views.clears, 0, "busy/failing before collection preserves the current result");
    assert.equal(harness.state.exceptionEvidenceId, "previous-evidence");
    assert.match(harness.statuses.at(-1)[1], /Crash guard evidence remains current/);
    assert.deepEqual(harness.activated, ["exceptions"], "completion/error status does not steal focus");

    const running = statusHarness({ pendingExceptionEvidenceId: "running-request" });
    running.dispatch({
        state: "running", evidenceId: "running-request", captureKind: "exceptions", message: "Capturing",
    }, running.state, running.deps);
    assert.equal(running.views.clears, 1);
    assert.equal(running.state.exceptionEvidenceId, undefined);
    assert.equal(running.state.exceptionEvidenceMode, undefined);
    assert.equal(running.panel.dataset.evidenceId, "");
    assert.match(running.identity.textContent, /Managed exceptions · Live EventPipe/);
    assert.deepEqual(running.activated, [], "running status after navigation does not reclaim focus");

    const changedTarget = statusHarness({ pendingExceptionEvidenceId: "old-request" });
    changedTarget.dispatch({
        state: "error", preflight: true, evidenceId: "new-request",
        captureKind: "exceptions", message: "The selected process changed during setup.",
    }, changedTarget.state, changedTarget.deps);
    assert.equal(changedTarget.state.pendingExceptionEvidenceId, "new-request");
    assert.equal(changedTarget.views.clears, 0, "preflight abort must preserve current evidence");
    assert.match(changedTarget.elements.get("captureExceptionsStatus").textContent, /selected process changed/);
});

test("exception result ownership stays dedicated, mode-specific and request-correlated", () => {
    const html = renderHtml(target, "nonce123", 120);
    const script = extractInlineScript(html);
    const resultBranch = /message\.type === 'capture' && message\.kind === 'exceptions'\) \{([\s\S]*?)\n      \} else if \(message\.type === 'capture' && message\.kind === 'heap'\)/.exec(script);
    assert.ok(resultBranch);
    const state = { pendingExceptionEvidenceId: "request-2" };
    const panel = { dataset: {} };
    const identity = { textContent: "" };
    const details = { open: false };
    const rendered = [];
    const statuses = [];
    const document = {
        getElementById(id) {
            return id === "resultExceptions" ? panel
                : id === "exceptionEvidenceIdentity" ? identity
                : id === "exceptionCaptureViews-exceptions" ? details
                : null;
        },
    };
    const dispatch = new Function("message", "state", "deps", `
        let pendingExceptionEvidenceId = state.pendingExceptionEvidenceId;
        let exceptionEvidenceId = state.exceptionEvidenceId;
        let exceptionEvidenceMode = state.exceptionEvidenceMode;
        const { exceptionCaptureModes, document, setWorkspaceStatus, selectedWorkspaceEvidence,
            setWorkspaceEvidence, renderQueryView } = deps;
        ${resultBranch[1]}
        Object.assign(state, { pendingExceptionEvidenceId, exceptionEvidenceId, exceptionEvidenceMode });
    `);
    const deps = {
        exceptionCaptureModes: { exceptions: "Managed exceptions", "crash-guard": "Crash guard" },
        document,
        setWorkspaceStatus: (...args) => statuses.push(args),
        selectedWorkspaceEvidence: "cpu",
        setWorkspaceEvidence() { assert.fail("late result must not change the selected domain"); },
        renderQueryView: (...args) => rendered.push(args),
    };

    dispatch({
        evidenceId: "old-request", captureKind: "exceptions", result: { data: {} },
    }, state, deps);
    assert.equal(state.exceptionEvidenceId, undefined);
    assert.deepEqual(rendered, [], "late completion cannot overwrite the current exception owner");
    dispatch({
        evidenceId: "request-2", captureKind: "crash-guard",
        result: { data: { processId: 4242, duration: "00:00:10" } },
    }, state, deps);
    assert.equal(state.exceptionEvidenceId, "request-2");
    assert.equal(state.exceptionEvidenceMode, "crash-guard");
    assert.deepEqual(panel.dataset, { evidenceId: "request-2", captureKind: "crash-guard" });
    assert.equal(identity.textContent, "", "selectors and target header already identify this evidence");
    assert.equal(rendered[0][0], "exceptionCaptureViews", "exceptions never render into the generic capture slot");
    assert.deepEqual(statuses, [["exceptions", "Current evidence: Crash guard · Live EventPipe · PID 4242."]]);
    assert.equal(details.open, true);
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

    assert.match(container.textContent, /Top of stack first/);
    assert.match(container.textContent, /Sample\.Run\(\).*Sample\.Main\(\)/);
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
        assert.doesNotMatch(html, /id="openHeapDetails"|Explore heap details/);
        assert.doesNotMatch(html, /\.heap-workspace\[data-focused="(?:true|false)"\][^{]+\{ display:none; \}/);
        assert.match(html, /workspace\.dataset\.focused = "true"/);
        assert.match(html, /window\.scrollTo\(\{ top: overviewScroll \}\)/);
    }
});

test("heap navigation needs no Explore shortcut and restores keyboard focus in live and dump panels", () => {
    const live=renderHtml(target,"nonce",120),dump=renderDumpAnalysisHtml("fixture.dmp","nonce");
    assert.doesNotMatch(live, /id="openHeapDetails"/);
    assert.doesNotMatch(dump,/id="openHeapDetails"/);
    assert.match(extractInlineScript(live),/navigateHeapDetails\(workspaceView.value === 'details'\)/);
    const {makeContainer,document,renderHeapTypeNavigation,navigateHeapDetails}=loadRenderer();
    const workspace=makeContainer("heapWorkspace");
    const types=document.createElement("div");types.id="heapTypeList";workspace.appendChild(types);
    const table=makeTable(document,types,["Type"],[["App.Cache"]],"captureHeapTable");
    const pane=document.createElement("section");pane.id="heapDetailPane";workspace.appendChild(pane);
    const back=document.createElement("button");back.id="backToHeapTypes";pane.appendChild(back);
    const views=document.createElement("div");views.id="heapQueryViews";pane.appendChild(views);
    const view=makeContainer("workspaceView");view.value="snapshot";
    renderHeapTypeNavigation([{typeFullName:"App.Cache",instanceCount:2,totalBytes:100}]);
    navigateHeapDetails(true);
    assert.equal(workspace.dataset.focused,"true");
    assert.equal(view.value,"details");
    assert.equal(pane.focused,true);
    assert.equal(pane.scrolledIntoView,true);
    navigateHeapDetails(false);
    assert.equal(workspace.dataset.focused,"false");
    assert.equal(view.value,"snapshot");
    assert.equal(view.focused,true,"no legacy shortcut is needed to restore keyboard focus");
    click(table,"App.Cache");
    assert.equal(workspace.dataset.focused,"true");
    assert.equal(view.value,"details","type selection keeps workspace destination truthful");
    back.onclick();
    assert.equal(workspace.dataset.focused,"false");
    assert.equal(view.value,"snapshot");
    const getElementById=document.getElementById.bind(document);
    document.getElementById=id=>id==="workspaceView"?null:getElementById(id);
    renderHeapTypeNavigation([{typeFullName:"App.Cache",instanceCount:2,totalBytes:100}]);
    navigateHeapDetails(true);
    assert.equal(workspace.dataset.focused,"true");
    back.onclick();
    assert.equal(workspace.dataset.focused,"false");
    assert.equal(table.focused,true,"dump without a workspace selector restores focus to its type table");
    assert.equal(table.tabIndex,-1);
    assert.equal(views.children.length,0,"all navigation is local; no evidence was manufactured");
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
    assert.equal(document.getElementById("thread-inline-1").parentElement.parentElement.hidden, false);
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
    assert.equal(document.getElementById("thread-inline-1").parentElement.parentElement.hidden, false);
});

test("thread explorer uses aligned inline disclosures without guessing numeric runtime state", () => {
    const {makeContainer, document, updateThreadNavigation} = loadRenderer();
    const root = makeContainer("captureThreadResult");
    const longFrame = "App.Generic.Work<" + "LongNamespace.".repeat(30) + "Type>()";
    updateThreadNavigation("snapshot", {threads: [
        {managedThreadId: 4, osThreadId: 24192, state: "16953888", frames: ["App.Poll()", longFrame]},
        {managedThreadId: 5, osThreadId: 200, state: 131616, frames: []},
        {managedThreadId: 2, state: "Background, WaitSleepJoin", frames: [{displayName: "<script>App.Wait()</script>"}]},
    ]});
    const table = root.querySelector(".thread-explorer-table");
    assert.deepEqual(table.querySelectorAll("th").map(x => x.textContent), ["Thread", "State", "Top frame"]);
    const rows = table.querySelectorAll(".thread-explorer-row");
    assert.deepEqual(rows.map(x => x.children[1].textContent), ["Not decoded", "Not decoded", "Background, WaitSleepJoin"]);
    assert.equal(rows[1].children[2].textContent, "Unavailable");
    assert.equal(rows[0].querySelector(".thread-top-frame-preview").textContent, "App.Poll()");
    assert.equal(root.querySelector(".evidence-selection"), null, "no separate selected stack panel");
    assert.equal(table.querySelectorAll(".thread-inline-row").every(x => x.hidden), true);
    assert.equal(root.querySelector("label").htmlFor, "threadExplorerSearch");
    assert.equal(root.querySelector("input").placeholder, "Thread ID, state or stack method");

    const buttons = table.querySelectorAll(".thread-disclosure");
    const first = buttons[0];
    assert.equal(first.tagName.toLowerCase(), "button", "native Enter/Space activation");
    assert.equal(first.attributes["aria-controls"], "thread-inline-4");
    first.listeners.keydown({key: "ArrowRight", preventDefault() {}});
    assert.equal(first.attributes["aria-expanded"], "true");
    const details = document.getElementById("thread-inline-4");
    assert.equal(details.parentElement.parentElement, rows[0].nextElementSibling);
    assert.equal(rows[0].nextElementSibling.hidden, false);
    assert.deepEqual(details.querySelectorAll("li").map(x => x.textContent), ["App.Poll()", longFrame]);
    assert.match(details.querySelector(".thread-technical").textContent, /Reported state: 16953888.*OS thread: 24192/);
    assert.equal(details.querySelector(".thread-technical").open, false, "raw flags stay in a closed native disclosure");
    click(root, "Thread 5");
    assert.equal(buttons[1].attributes["aria-expanded"], "true");
    assert.match(document.getElementById("thread-inline-5").textContent, /Unavailable: no inline stack/);
    assert.equal(first.attributes["aria-expanded"], "true", "more than one stack may be inspected");
    details.listeners.keydown({key:"Escape",preventDefault(){},stopPropagation(){}});
    assert.equal(first.attributes["aria-expanded"], "false");
    assert.equal(first.focused, true);
    assert.equal(buttons[1].attributes["aria-expanded"], "true");

    const input=root.querySelector("input");
    input.value="App.Wait"; input.listeners.input();
    assert.equal(root.querySelectorAll(".thread-explorer-row").length, 1);
    click(root, "Thread 2");
    assert.equal(root.querySelector("script"), null);
    input.value=""; input.listeners.input();
    assert.equal(root.querySelectorAll(".thread-explorer-row").length, 3);
    const restored=root.querySelectorAll(".thread-disclosure").find(x=>x.dataset.threadId==="5");
    assert.equal(restored.attributes["aria-expanded"], "true", "filtering keeps expansion by stable thread ID");
    updateThreadNavigation("wait-chains", {waitChains:{chains:[]}});
    assert.equal(root.querySelectorAll(".thread-disclosure").find(x=>x.dataset.threadId==="5").attributes["aria-expanded"], "true");
    updateThreadNavigation("snapshot", {threads:[]});
    assert.match(root.textContent,/No threads were returned/);
    assert.equal(root.querySelectorAll(".thread-disclosure").length,0);
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
    assert.match(document.getElementById("thread-inline-1").textContent, /App.Work/);
    const sourceLink = groups.querySelectorAll("button").find(button => button.textContent === "Thread 1");
    assert.equal(document.getElementById("thread-inline-1").parentElement.parentElement.hidden, false);
    click(explorer, "Back to grouped stacks");
    assert.equal(groups.hidden, false);
    assert.equal(input.value, "Thread 1");
    assert.equal(disclosure.open, true);
    assert.equal(sourceLink.focused, true, "return restores focus to the originating grouped thread link");
    input.value = ""; input.listeners.input();
    click(groups, "Thread 99");
    assert.match(document.getElementById("thread-inline-99").textContent, /not included in the returned inline thread list/);
    click(explorer, "Back to grouped stacks");
    assert.equal(groups.hidden, false);
});

test("thread explorer connects groups, full inline stacks and wait-chain owners, including omitted participants", () => {
    const { makeContainer, document, updateThreadNavigation } = loadRenderer();
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
    let details = document.getElementById("thread-inline-1");
    assert.match(details.textContent, /App.Entry/);
    assert.match(details.textContent, /<script>App.Block<\/script>/);
    assert.equal(details.querySelector("script"), null);
    assert.match(details.textContent, /confidence: medium/);
    click(details, "Thread 2");
    details = document.getElementById("thread-inline-2");
    assert.match(details.textContent, /App.Owner/);
    click(details, "Thread 99");
    details = document.getElementById("thread-inline-99");
    assert.match(details.textContent, /not included in the returned inline thread list/);
    const search = root.querySelector("input");
    search.value = "App.Owner";
    search.listeners.input();
    assert.match(root.textContent, /1 of 2 returned threads match/);
    updateThreadNavigation("snapshot", { threads: [] });
    assert.doesNotMatch(root.textContent, /App.Owner|Thread 99|App.Block/);
});

test("direct wait-chain thread links synchronize Workspace navigation and return to their source view", () => {
    let selectedView;
    const { makeContainer, document, updateThreadNavigation, renderQueryView } = loadRenderer(view => { selectedView = view; });
    const root = makeContainer("captureThreadResult");
    const workspaceView = makeContainer("workspaceView");
    updateThreadNavigation("snapshot", { threads: [
        { managedThreadId: 1, state: "Wait", frames: ["App.Waiter"] },
        { managedThreadId: 2, state: "Running", frames: ["App.Owner"] },
    ] });
    const queries = makeContainer("threadQueryViews");
    root.appendChild(queries);
    renderQueryView("threadQueryViews", "wait-chains", { waitChains: { chains: [{
        rootThreadId: 1, terminalKind: "Owner", links: [
            { waitingThreadId: 1, ownerThreadId: 2, targetLabel: "Monitor", confidence: "medium" },
        ],
    }] } }, false);

    click(queries, "Thread 2");
    assert.equal(selectedView, "thread-explorer");
    assert.equal(workspaceView.value, "thread-explorer");
    const explorer = document.getElementById("threadExplorer");
    assert.match(document.getElementById("thread-inline-2").textContent, /App.Owner/);
    assert.equal(document.getElementById("thread-inline-2").parentElement.parentElement.hidden, false);
    click(document.getElementById("thread-inline-2"), "Back to wait chains");
    assert.equal(selectedView, "wait-chains");
    assert.equal(workspaceView.value, "wait-chains");
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

test("exception occurrence detail keeps selection, keyboard return and bounded navigation in wide and narrow fixtures", () => {
    const { renderHtml } = require("../out/extension.js");
    const html = renderHtml(target, "nonce123", 120);
    assert.match(html, /\.exception-navigation-layout \{ display:grid; grid-template-columns:minmax\(12rem, 1fr\) minmax\(0, 1\.5fr\)/);
    assert.match(html, /@media \(max-width:640px\) \{\s*\.exception-navigation-layout \{ grid-template-columns:minmax\(0, 1fr\); \}/);

    for (const viewportWidth of [1280, 360]) {
        const { makeContainer, renderExceptionNavigation } = loadRenderer();
        const root = makeContainer("exceptions-" + viewportWidth);
        root.clientWidth = viewportWidth;
        const events = Array.from({ length: 100 }, (_, index) => ({
            timestamp: new Date(Date.UTC(2026, 9, 14, 0, 0, index)).toISOString(),
            exceptionType: index === 0 ? "Very.Long." + "GenericType".repeat(12) : "System.Exception",
            exceptionMessage: index === 0 ? "<script>long message</script>" : "Failure " + index,
            threadId: index + 1,
            managedStack: index === 0 ? ["Sample.Throw()"] : [],
        }));
        renderExceptionNavigation(root, events);

        const list = root.querySelector(".exception-occurrences");
        assert.equal(list.tagName, "ol", "occurrences use a native ordered-list structure");
        assert.equal(list.getAttribute("aria-label"), "Retained exception occurrences");
        assert.equal(list.children.length, 100, "all bounded retained events remain available");
        const first = list.children[0].querySelector("button");
        assert.match(first.textContent, /Very\.Long\./);
        assert.match(first.getAttribute("aria-label"), /2026-10-14T00:00:00\.000Z/,
            "the exact timestamp remains available to assistive technology");
        first.listeners.click({});
        assert.equal(first.getAttribute("aria-pressed"), "true");
        assert.match(root.textContent, /Occurrence 1 of 100 selected/);
        const detail = root.querySelector(".evidence-selection");
        const actions = detail.querySelectorAll("button");
        assert.equal(actions[0].disabled, true);
        assert.equal(actions[1].textContent, "Next occurrence");
        actions[1].listeners.click({});
        assert.equal(first.getAttribute("aria-pressed"), "false");
        const second = list.children[1].querySelector("button");
        assert.equal(second.getAttribute("aria-pressed"), "true");
        assert.match(root.textContent, /Occurrence 2 of 100 selected/);
        actions[2].listeners.click({});
        assert.equal(second.focused, true, "Back returns focus to the selected occurrence");
        assert.equal(second.scrolledIntoView, true);
        assert.equal(root.querySelector("script"), null, "exception text is rendered without markup");
    }
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
    const nav = install(canvas, () => points, gcRows, () => redraws++, value => value + " MB");
    const [start, end] = root.querySelectorAll("input");
    assert.match(root.textContent, /Start of selected receipt time/);
    assert.match(root.textContent, /End of selected receipt time/);
    assert.match(start.attributes["aria-valuetext"], /0% of retained receipt time/);
    assert.match(end.attributes["aria-valuetext"], /100% of retained receipt time/);
    start.value = "25"; end.value = "75"; start.listeners.input();
    assert.deepEqual(nav.filter(points).map(point => point.receivedAtMs), [100, 200]);
    assert.ok(gcRows.every(row => row.element.hidden));
    assert.match(root.querySelectorAll("p").find(node => node.attributes.role === "status").title,
        /Selected receipt-time bounds: 1970-01-01T00:00:00\.075Z to 1970-01-01T00:00:00\.225Z/);
    assert.match(start.attributes["aria-valuetext"], /selected start 1970-01-01T00:00:00\.075Z/);
    canvas.listeners.keydown({ key: "Home", preventDefault() {} });
    assert.match(root.textContent, /Observed value: 10 MB \| target timestamp: target-100/);
    assert.equal(nav.selected(), points[1], "inspection exposes the exact point for the canvas marker");
    const detail = root.querySelectorAll("p").find(node => node.attributes["aria-label"] === "Selected counter observation");
    assert.equal(detail.getAttribute("role"), "status", "inspection and expiry are announced without moving chart focus");
    assert.equal(detail.getAttribute("aria-atomic"), "true");
    points = [{ receivedAtMs: 1000, timestamp: "later", value: 123 }];
    gcRows.forEach(row => { row.receivedAtMs += 1000; });
    nav.update();
    assert.match(root.textContent, /0 of 1 observations/);
    assert.equal(nav.selected(), undefined);
    assert.match(root.textContent, /Selected data has expired/);
    assert.equal(start.value, "0", "thumbs reflect clipped current retention, not stale percentages");
    assert.equal(nav.filter(points).length, 0, "fixed selections must not silently follow incoming data");
    click(root, "Follow latest");
    assert.equal(nav.filter(points).length, 1);
    assert.equal(root.querySelector("button").textContent, "Pause following");
    assert.ok(gcRows.every(row => !row.element.hidden));
    assert.equal(redraws, 3);
    const reset = root.querySelector("button");
    points = [];
    gcRows.length = 0;
    nav.update();
    assert.equal(reset.getAttribute("aria-disabled"), "true");
    assert.equal(reset.disabled, true);
    assert.match(reset.title, /Wait for retained/);
    gcRows.push({ receivedAtMs: 2000, element: document.createElement("tr") });
    nav.update();
    assert.equal(reset.getAttribute("aria-disabled"), "false", "retained GC rows are sufficient to reset their time selection");
});

test("monitoring window choices remain local and paused absolute bounds survive new arrivals and retention expiry", () => {
    const { document, makeContainer } = createDocumentStub();
    const root = makeContainer("live-window");
    const canvas = document.createElement("canvas");
    root.appendChild(canvas);
    const { LIVE_TIME_NAVIGATION_SCRIPT } = require("../out/liveTimeNavigation.js");
    const install = new Function("document", LIVE_TIME_NAVIGATION_SCRIPT + ";return installLiveTimeNavigation;")(document);
    let points = [1000, 32000, 62000].map(receivedAtMs => ({ receivedAtMs, timestamp: "target", value: 1 }));
    const nav = install(canvas, () => points, [], () => {});
    const window = root.querySelector("select");
    window.value = "30";
    window.listeners.change();
    assert.deepEqual(nav.filter(points).map(point => point.receivedAtMs), [32000, 62000]);
    click(root, "Pause following");
    points.push({ receivedAtMs: 92000, timestamp: "later", value: 2 });
    nav.update();
    assert.deepEqual(nav.filter(points).map(point => point.receivedAtMs), [32000, 62000], "pause freezes absolute bounds");
    const [start, end] = root.querySelectorAll("input");
    assert.equal(start.value, "34");
    assert.equal(end.value, "67", "slider thumbs track the new retained extent rather than stale input percentages");
    points = points.slice(-1);
    nav.update();
    assert.equal(nav.filter(points).length, 0);
    assert.match(root.textContent, /Selected data has expired.*empty is not zero/);
    points = [];
    nav.update();
    assert.match(root.textContent, /Selected data has expired/);
    assert.equal(root.querySelector("button").disabled, false, "Follow latest stays available to leave an expired selection");
    click(root, "Follow latest");
    assert.equal(root.querySelector("button").disabled, true, "empty following state is truly disabled");
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
        assert.equal(tables[0].querySelector(".evidence-pick"), null);
        assert.equal(tables[0].querySelector("tbody").children[0].children[1].textContent, "0");
        assert.match(tables[0].textContent, /<script>operation<\/script>/);
        assert.equal(tables[1]._parent._parent, root.querySelector(".diagnostic-quality-details"));
        assert.ok(root.querySelector(".diagnostic-evidence-table-label"));
        assert.ok(root.querySelector(".diagnostic-capture-notes"));
        assert.equal(tables.every(table => table._parent.className === "query-view-table-wrap"), true,
            "each captured table keeps horizontal scrolling inside its own bounded wrapper");
        assert.equal(root.querySelector("script"), null);
    }
});

test("unreported capture quality remains visible before rows without notes or limitations", () => {
    const { document, makeContainer } = createDocumentStub();
    const root = makeContainer("diagnostics");
    const { CAPTURE_VIEW_RENDERER_SCRIPT } = require("../out/captureViewRenderer.js");
    const render = new Function("document", CAPTURE_VIEW_RENDERER_SCRIPT + ";return renderCaptureView;")(document);
    render("diagnostics", {
        kind: "db", summary: "Fixture", provenance: {processId: 1, requestedDurationSeconds: 10},
        metrics: [], tables: [{title: "Commands", columns: ["Name"], rows: [["Query"]]}],
        notes: [], evidence: {source: "not-reported", limitations: []},
    });
    const alert = root.querySelector(".diagnostic-active-quality");
    assert.equal(alert.children[0].textContent, "quality not reported");
    assert.ok(root.children.indexOf(alert) < root.children.indexOf(root.querySelector(".diagnostic-table-view")));
});

test("actual long networking summary and normal zero-count scope do not precede captured rows", () => {
        const { document, makeContainer } = createDocumentStub();
        const root = makeContainer("networking");
        const { CAPTURE_VIEW_RENDERER_SCRIPT } = require("../out/captureViewRenderer.js");
        const render = new Function("document", CAPTURE_VIEW_RENDERER_SCRIPT + ";return renderCaptureView;")(document);
        const summary = "Captured 63 HTTP request(s) (0 failure events, including cancellation/timeouts), requested 10s. DNS: 0 lookup(s), 0 failure events. TLS: 0 handshake(s), 0 failure events. Sockets: 0 connect(s), 0 failed. Request p95=3.1ms, time-in-queue p95=unavailable (not-observed), DNS p95=unavailable (not-observed), TLS p95=unavailable (not-observed). Percentile samples retained/accepted: HTTP 63/63, queue 0/0, DNS 0/0, TLS 0/0. Fewer retained samples means reservoir approximation, independent of pairing/capture gaps; max uses all accepted samples. Capture completion=normal; transport events lost=0; parse errors=0. Duration is requested time; stream-read elapsed is not proof of target coverage. Latencies cover accepted observed pairs only: HTTP 63/63, DNS 0/0, TLS 0/0. See correlation accounting for exclusions; this does not establish acquisition completeness. Latency population v2 includes failed completions: HTTP 0 failed/63 without observed failure, DNS 0/0, TLS 0/0. HTTP status-error responses=0; these are not transport failure events. Zero accepted pairs means latency unavailable; no observed failure is not proof of success.";
        const result = {
            kind: "networking", summary, provenance: { processId: 40416, requestedDurationSeconds: 10 },
            metrics: [{label: "Transport events lost", value: 0}, {label: "Capture completion", value: "normal"}],
            tables: [{id: "operations",title: "HTTP operations",columns: ["Host", "Path", "Count", "P95 (ms)", "Latency availability"],
                rows: [["http://127.0.0.1:53225", "/weatherforecast", 63, 3.13, "measured"]],
                totalRows: 1, omittedRows: 0}],
            notes: ["Capture completion=normal; transport events lost=0; parse errors=0. Duration is requested time; stream-read elapsed is not proof of target coverage.",
                "dns latency is unavailable (not-observed); see Correlation counts and CaptureQuality. Scalar zero is not a measurement without accepted samples.",
                "Missing latency is unavailable, not zero. Hosts and paths can contain sensitive application data."],
            evidence: {source: "networking", limitations: [{category: "captureWindow", scope: "capture", affectedCount: 0,
                detail: "Completion=normal; transport events lost=0; parse errors=0. Stream read duration is not proof of target coverage."}]},
        };
        render("networking", result);
        assert.equal(root.querySelector(".capture-summary"), null);
        assert.equal(root.querySelector(".diagnostic-active-quality"), null, "known normal acquisition and contextual notes are not warning conditions");
        const details = root.querySelector(".diagnostic-quality-details");
        assert.ok(root.children.indexOf(details) > root.children.indexOf(root.querySelector(".diagnostic-table-view")));
        assert.match(details.textContent, /Captured 63 HTTP request/);
        assert.match(details.textContent, /not proof of target coverage/);
        assert.match(details.textContent, /dns latency is unavailable/);
        assert.equal(details.open, false, "full raw summary is not automatically expanded");
        for (const limitation of [
            {category: "captureWindow", scope: "capture", affectedCount: 0, detail: "Completion=early; transport events lost=0; parse errors=0. Stream read duration is not proof of target coverage."},
            {category: "captureWindow", scope: "capture", affectedCount: null, detail: "Completion=normal; transport events lost=unknown; parse errors=0. Stream read duration is not proof of target coverage."},
            {category: "captureWindow", scope: "capture", affectedCount: 0, detail: "Completion=normal; transport events lost=0; parse errors=3. Stream read duration is not proof of target coverage."},
            {category: "outputProjection", scope: "rows", affectedCount: 0, detail: "Coverage of this projection is unknown."},
        ]) {
            result.evidence.limitations = [limitation];
            result.notes = ["Queue latency rejected 3 RequestLeftQueue payload(s); only valid nonnegative durations are sampled."];
            render("networking", result);
            const alert = root.querySelector(".diagnostic-active-quality");
            assert.ok(alert, "zero is never a universal suppression rule");
            assert.match(alert.textContent, /Queue latency rejected 3/);
            assert.ok(alert.textContent.includes(limitation.detail));
        }
        result.evidence = {source: "not-reported", limitations: []};
        result.notes = [];
        render("networking", result);
        assert.match(root.querySelector(".diagnostic-active-quality").textContent, /quality not reported/);
});

test("in-flight request rows lead the result and state filters compose with returned-row search", () => {
    const { document, makeContainer } = createDocumentStub();
    const root = makeContainer("diagnostics");
    const { CAPTURE_VIEW_RENDERER_SCRIPT } = require("../out/captureViewRenderer.js");
    const render = new Function("document", CAPTURE_VIEW_RENDERER_SCRIPT + ";return renderCaptureView;")(document);
    render("diagnostics", {
        kind: "requests", title: "requests", summary: "An unparsed fallback summary.",
        provenance: { processId: 42, requestedDurationSeconds: 10 },
        metrics: [
            { label: "Started", value: 12 },
            { label: "Completed", value: 7 },
            { label: "In flight at end", value: 5 },
            { label: "Long-running", value: 3 },
            { label: "Long-running threshold (ms)", value: 750 },
        ],
        tables: [{
            id: "requests", title: "In-flight requests",
            columns: ["Method", "Path", "Elapsed (ms)", "Long-running", "Started", "Trace ID"],
            rows: [
                ["GET", "/alpha", 2400, true, "t1", "trace-a"],
                ["POST", "/beta", 300, false, "t2", "trace-b"],
                ["GET", "/gamma", 1700, true, "t3", "trace-c"],
                ["GET", "/delta", 100, false, "t4", "trace-d"],
            ],
            totalRows: 5, omittedRows: 1, emptyText: "No in-flight requests.",
        }],
        notes: ["In-flight requests: 1 retained row(s) omitted from the view at topN=4.",
            "Dropped 2 newer starts at maxRequests=100; counts are lower bounds.",
            "Correlation uses tracked starts; counts do not prove unique operations."],
        evidence: { source: "collector", limitations: [
            { category: "Capture window", scope: "requests", detail: "The snapshot is partial." },
        ] },
    });

    assert.match(root.textContent, /In-flight requests/);
    assert.match(root.textContent, /Window: 12 observed starts · 7 matched completions/);
    assert.match(root.textContent, /At close: 5 tracked in flight · 3 tracked long-running \(≥ 750 ms\)/);
    assert.match(root.textContent, /PID 42 · 10s window · Live capture, not saved/);
    assert.doesNotMatch(root.textContent, /An unparsed fallback summary|requests\n/);
    assert.doesNotMatch(root.textContent, /Metrics/);
    assert.doesNotMatch(root.textContent, /In flight at window close · Filters apply to shown rows/);
    assert.equal(root.children.filter(child => child.tagName === "p"
        && child.textContent.startsWith("Window:")).length, 1, "one capture summary precedes data");
    assert.match(root.textContent, /All in-flight \(4 shown\)/);
    assert.match(root.textContent, /At or above 750 ms \(2 shown\)/,
        "filter count comes from returned rows, not the capture-wide long-running metric");
    assert.match(root.textContent, /Dropped 2 newer starts/);
    assert.match(root.textContent, /Correlation uses tracked starts/);
    assert.match(root.textContent, /1 retained row\(s\) omitted/);
    assert.match(root.textContent, /Capture window \(requests\): The snapshot is partial/);
    const selector = document.getElementById("inFlightRequestsStateFilter");
    assert.equal(selector.getAttribute("aria-label"), "Show requests");
    const table = document.getElementById("inFlightRequestsTable");
    const disclaimer = root.querySelector(".diagnostic-request-disclaimer");
    assert.ok(disclaimer, "full capture limitations appear as a readable disclaimer");
    assert.match(disclaimer.textContent, /Search, sorting, and filtering apply only to returned rows/);
    assert.match(disclaimer.textContent, /Start\/stop pairing uses an activity ID or trace\/span ID pair/);
    assert.match(disclaimer.textContent, /Events without correlatable IDs are skipped/);
    assert.match(disclaimer.textContent, /repeated starts replace the tracked entry/);
    assert.ok(root.children.indexOf(disclaimer) > root.children.indexOf(table._parent),
        "the disclaimer follows the request table");
    assert.ok(root.querySelector(".diagnostic-active-quality"), "active quality has a compact visible summary");
    const notes = root.querySelector(".diagnostic-capture-notes");
    assert.equal(notes._parent.className, "diagnostic-active-quality", "full reasons remain at the active alert");
    assert.match(notes.textContent, /Dropped 2 newer starts/);
    assert.ok(root.children.some(child => child.textContent.includes("Capture window (requests): The snapshot is partial")),
        "evidence limitations remain visible before the request table");
    const body = table.querySelector("tbody");
    assert.equal(body.children.length, 4);
    const search = root.querySelector("input");
    search.value = "beta";
    search.listeners.input();
    assert.equal(body.children.length, 1);
    assert.match(root.textContent, /1 of 4 returned rows/);
    selector.value = "long-running";
    selector.listeners.change();
    assert.equal(body.children.length, 0, "search and state filters intersect");
    assert.match(root.textContent, /0 of 2 returned rows/);
    search.value = "gamma";
    search.listeners.input();
    assert.deepEqual(body.children.map(row => row.children[1].textContent), ["/gamma"]);
    assert.match(root.textContent, /1 of 2 returned rows/);
    selector.value = "all";
    selector.listeners.change();
    assert.deepEqual(body.children.map(row => row.children[1].textContent), ["/gamma"]);
    assert.match(root.textContent, /1 of 4 returned rows/);
});

test("in-flight request state filter omits a threshold claim when threshold metadata is absent", () => {
    const { document, makeContainer } = createDocumentStub();
    const root = makeContainer("diagnostics");
    const { CAPTURE_VIEW_RENDERER_SCRIPT } = require("../out/captureViewRenderer.js");
    const render = new Function("document", CAPTURE_VIEW_RENDERER_SCRIPT + ";return renderCaptureView;")(document);
    render("diagnostics", {
        kind: "requests", title: "Requests", summary: "Request capture.",
        provenance: { processId: 42, requestedDurationSeconds: 10 },
        metrics: [{ label: "Long-running", value: 1 }],
        tables: [{
            id: "requests", title: "In-flight requests",
            columns: ["Method", "Path", "Elapsed (ms)", "Long-running", "Started", "Trace ID"],
            rows: [["GET", "/slow", 1200, true, "t1", "trace-a"]],
            totalRows: 1, omittedRows: 0, emptyText: "No in-flight requests.",
        }],
        notes: [],
        evidence: { source: "collector", limitations: [] },
    });

    const selector = document.getElementById("inFlightRequestsStateFilter");
    assert.match(selector.children[1].textContent, /^Long-running \(1 shown\)$/);
    assert.doesNotMatch(selector.children[1].textContent, /(?:above|at or above|ms)/i);
});

test("in-flight request Started timestamps display in UTC while preserving precise ISO values and sort order", () => {
    const { document, makeContainer } = createDocumentStub();
    const root = makeContainer("diagnostics");
    const { CAPTURE_VIEW_RENDERER_SCRIPT } = require("../out/captureViewRenderer.js");
    const render = new Function("document", CAPTURE_VIEW_RENDERER_SCRIPT + ";return renderCaptureView;")(document);
    const later = "2026-10-21T22:45:00.0000000Z";
    const offsetEarlier = "2026-10-21T23:30:00.7395271+02:00";
    const result = {
        kind: "requests", title: "Requests", summary: "Request capture.",
        provenance: { processId: 42, requestedDurationSeconds: 10 },
        metrics: [{ label: "Long-running threshold (ms)", value: 1000 }],
        tables: [{
            id: "requests", title: "In-flight requests",
            columns: ["Method", "Path", "Elapsed (ms)", "Long-running", "Started", "Trace ID"],
            rows: [
                ["GET", "/later", 1200, true, later, "trace-a"],
                ["GET", "/offset", 1300, true, offsetEarlier, "trace-b"],
                ["GET", "/invalid", 1400, false, "not-an-ISO-timestamp", "trace-c"],
                ["GET", "/missing", 1500, false, null, "trace-d"],
            ],
            totalRows: 4, omittedRows: 0, emptyText: "No in-flight requests.",
        }],
        notes: [],
        evidence: { source: "collector", limitations: [] },
    };
    render("diagnostics", result);

    const table = document.getElementById("inFlightRequestsTable");
    const body = table.children[2];
    const laterCell = body.children[0].children[4];
    const offsetCell = body.children[1].children[4];
    const invalidCell = body.children[2].children[4];
    const missingCell = body.children[3].children[4];
    assert.equal(laterCell.children[0].textContent, "21 Oct 2026, 22:45:00.000 UTC");
    assert.equal(laterCell.children[0].getAttribute("datetime"), later);
    assert.equal(laterCell.children[0].getAttribute("title"), `Original ISO timestamp: ${later}`);
    assert.match(laterCell.children[0].getAttribute("aria-label"), new RegExp(later.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
    assert.equal(offsetCell.children[0].textContent, "21 Oct 2026, 21:30:00.739 UTC");
    assert.equal(offsetCell.children[0].getAttribute("datetime"), offsetEarlier);
    assert.equal(invalidCell.textContent, "not-an-ISO-timestamp");
    assert.equal(invalidCell.children.length, 0);
    assert.equal(missingCell.textContent, "Unavailable");
    assert.equal(missingCell.children.length, 0);
    assert.equal(result.tables[0].rows[0][4], later, "rendering does not mutate the source timestamp");

    const startedSort = table.children[1].children[0].children[4].children[0];
    startedSort.listeners.click();
    assert.deepEqual(body.children.slice(0, 2).map(row => row.children[1].textContent), ["/offset", "/later"],
        "sort order follows the UTC instant, not the displayed date or source offset");
    const search = root.querySelector("input");
    search.value = offsetEarlier;
    search.listeners.input();
    assert.deepEqual(body.children.map(row => row.children[1].textContent), ["/offset"],
        "search still matches the original ISO timestamp");
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
            assert.equal(document.getElementById("thread-inline-1").parentElement.parentElement.hidden, false);
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
