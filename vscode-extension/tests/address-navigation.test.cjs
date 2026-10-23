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
const { buildQueryFrame } = require("../out/protocol.js");
const {
    AddressNavigationController, MAX_NAVIGATION_HISTORY, NAVIGATION_VIEWS, isNavigationView, parseHexAddress,
} = require("../out/addressNavigation.js");

const target = {
    processId: 4242, operatingSystem: "Linux", processArchitecture: "x64",
    runtimeVersion: "10.0.0", managedEntrypointAssemblyName: "Sample",
};

// Beyond 2^53: a JavaScript number cannot represent this address exactly.
const TARGET = "0xfedcba9876543211";
const FIELD = "0x7f0000001000";
const ROOT = "0x7f0000002000";

class ElementStub {
    constructor(tagName) {
        this.tagName = tagName;
        this.children = [];
        this.attributes = {};
        this.listeners = {};
        this.classes = new Set();
        this.classList = {
            add: name => this.classes.add(name),
            toggle: (name, enabled) => enabled ? this.classes.add(name) : this.classes.delete(name),
            contains: name => this.classes.has(name),
        };
        this._text = "";
        this.open = false;
        this.hidden = false;
        this.disabled = false;
    }
    set className(value) { this._className = value; this.classes = new Set(value.split(/\s+/).filter(Boolean)); }
    get className() { return this._className || ""; }
    set textContent(value) { this._text = String(value); this.children = []; }
    get textContent() { return this._text + this.children.map(child => child.textContent).join(""); }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    hasAttribute(name) { return Object.hasOwn(this.attributes, name); }
    addEventListener(type, listener) { this.listeners[type] = listener; }
    click() { assert.ok(!this.disabled, "disabled controls cannot be clicked"); this.listeners.click(); }
    focus() { this.focused = true; }
    append(...children) { for (const child of children) this.appendChild(child); }
    appendChild(child) { this.children.push(child); return child; }
    replaceChildren(...children) { this.children = []; this._text = ""; this.append(...children); }
    findAll(predicate, found = []) {
        for (const child of this.children) {
            if (predicate(child)) found.push(child);
            child.findAll(predicate, found);
        }
        return found;
    }
    querySelector(selector) {
        const matches = element => selector.startsWith(".") ? element.classes.has(selector.slice(1)) : element.tagName === selector;
        return this.findAll(matches)[0] ?? null;
    }
}

require("./stubs/navigation-dom.cjs").extendDom(ElementStub);

/** A tiny page: the heap container plus the real navigation bar ids, wired to the real emitted script. */
function loadPage() {
    const roots = [];
    const posted = [];
    const element = (tag, id, parent) => {
        const node = new ElementStub(tag);
        node.id = id;
        (parent ?? { appendChild: n => roots.push(n) }).appendChild(node);
        return node;
    };
    const document = {
        getElementById(id) {
            for (const root of roots) {
                if (root.id === id) return root;
                const found = root.findAll(node => node.id === id)[0];
                if (found) return found;
            }
            return null;
        },
        createElement: tag => new ElementStub(tag),
    };
    const heap = element("div", "heapQueryViews");
    const threads = element("div", "threadQueryViews");
    const nav = element("section", "addressNavigation");
    nav.hidden = true;
    const back = element("button", "addressNavBack", nav);
    const close = element("button", "addressNavClose", nav);
    const forward = element("button", "addressNavForward", nav);
    back.disabled = forward.disabled = true;
    element("ol", "addressBreadcrumb", nav);
    element("div", "addressNavActions", nav);
    element("div", "addressNavStatus", nav);
    element("div", "addressQueryViews", nav);
    const vscode = { postMessage: message => posted.push(JSON.parse(JSON.stringify(message))) };
    // eslint-disable-next-line no-new-func -- evaluates the exact script emitted into the webview.
    const api = new Function("document", "vscode", `${QUERY_VIEW_RENDERER_SCRIPT}
        return { renderQueryView, navigateToAddress, handleAddressNavigationMessage, resetAddressNavigation,
                 moveAddressHistory, setAddressLiveQueriesAvailable, addressNavigationState, addressLinkContainers };`)(document, vscode);
    return { ...api, document, heap, threads, nav, back, forward, close, posted };
}

const retentionPaths = address => ({
    handle: "h1", view: "retention-paths", origin: "Dump", processId: null,
    totalRetentionPaths: 1, omittedRetentionPaths: 0,
    retentionPaths: [{
        targetTypeFullName: "Sample.Cache", targetObjectAddress: address,
        chain: [
            { typeFullName: "Sample.Holder", objectAddress: ROOT, rootKind: "Strong" },
            { typeFullName: "Sample.Cache", objectAddress: address },
        ],
    }],
});

const objectResult = address => ({
    handle: "h1", view: "object", origin: "Dump", address,
    objectDetails: {
        address, typeFullName: "Sample.Cache", size: 48, generation: "Gen2", segmentKind: "Gen2",
        isArray: false, isString: false,
        fields: [{ name: "_inner", typeFullName: "Sample.Inner", value: "[redacted]", objectAddress: FIELD, referencedTypeFullName: "Sample.Inner" }],
        warnings: [],
    },
});

const staticsResult = address => ({
    handle: "h1", view: "static-fields", origin: "Dump",
    staticFields: [{ containingTypeFullName: "Sample.Holder", fieldName: "Instance", valueTypeFullName: "Sample.Cache", directlyReferencedBytes: 48, valueAddress: address }],
});

/** Host double: records calls and answers with CLI-shaped results, building the real wire frame. */
function makeHost(page, options = {}) {
    const calls = { acknowledge: [], sensitive: [], query: [], copy: [], frames: [], log: [] };
    const host = {
        currentHeapHandle: () => options.noHandle ? undefined : "h1",
        acknowledge: async (view, address) => { calls.acknowledge.push({ view, address }); return options.decline !== true; },
        acknowledgeSensitiveValues: async address => {
            calls.sensitive.push(address);
            return options.declineSensitive !== true;
        },
        query: async (handle, view, address, acknowledgeRisk, includeSensitiveValues, arrayStartIndex, arrayCount) => {
            calls.query.push({ handle, view, address, acknowledgeRisk });
            calls.frames.push(buildQueryFrame("r1", handle, view, undefined, { address, acknowledgeRisk, includeSensitiveValues, arrayStartIndex, arrayCount }));
            if (options.fail) throw new Error("CLI said no");
            if (options.array && address === TARGET && view === "object") {
                const start = options.oldRangeCli ? 0 : arrayStartIndex ?? 0;
                const count = arrayCount ?? 8;
                return { ...objectResult(address), sensitiveValuesIncluded: includeSensitiveValues === true,
                    objectDetails: {
                        address, typeFullName: "Sample.Entry[]", isArray: true, arrayLength: 11,
                        arrayStartIndex: start, arrayRequestedCount: count,
                        arraySample: Array.from({ length: Math.min(count, 11 - start) }, (_, i) => ({
                            index: start + i, typeFullName: "Sample.Entry", value: "<value-type size=40>",
                            fields: [{ name: "Nested.Value", typeFullName: "System.String",
                                value: includeSensitiveValues ? "<script>array-secret</script>" : "<redacted:metadata-only>",
                                objectAddress: FIELD }],
                        })),
                    },
                };
            }
            if (includeSensitiveValues) return {
                ...objectResult(address), sensitiveValuesIncluded: options.oldCli !== true,
                objectDetails: { ...objectResult(address).objectDetails, isString: true,
                    stringValue: "<script>secret-preview</script>", stringValueTruncated: true },
            };
            return view === "object" ? objectResult(address) : view === "static-fields" ? staticsResult(address)
                : { handle, view, origin: "Dump", address, gcRoot: { address, typeFullName: "Sample.Cache", chain: [{ typeFullName: "Sample.Holder", objectAddress: ROOT, rootKind: "Strong" }], truncated: false, warnings: [] } };
        },
        post: message => page.handleAddressNavigationMessage(JSON.parse(JSON.stringify(message))),
        copy: async address => { calls.copy.push(address); },
        log: text => calls.log.push(text),
    };
    return { controller: new AddressNavigationController(host), calls };
}

/** Delivers every webview → host message produced since the last call. */
async function pump(page, controller, seen = { count: 0 }) {
    while (seen.count < page.posted.length) {
        await controller.handle(page.posted[seen.count++]);
    }
}

const links = container => container.findAll(node => node.classes.has("address-link"));
const copies = container => container.findAll(node => node.classes.has("address-copy"));
const crumbs = page => page.document.getElementById("addressBreadcrumb").findAll(node => node.tagName === "button");

test("lost live heap connection disables every follow-up while retaining local address actions", async () => {
    const page = loadPage();
    const { controller } = makeHost(page, { array: true });
    const seen = { count: 0 };
    page.navigateToAddress(TARGET, "object");
    await pump(page, controller, seen);

    const results = page.document.getElementById("addressQueryViews");
    assert.equal(results.findAll(node => node.textContent === "Next array range")[0].disabled, false);
    assert.equal(links(results).length > 0, true);
    page.setAddressLiveQueriesAvailable(false);

    for (const container of [page.heap, results, page.nav]) {
        assert.ok(container.findAll(node => node.classes.has("address-link")).every(node => node.disabled));
        assert.ok(container.findAll(node => node.hasAttribute("data-live-follow-up")).every(node => node.disabled));
    }
    assert.ok(results.findAll(node => node.hasAttribute("data-live-follow-up")).every(node => node.disabled));
    assert.ok(copies(results).every(node => !node.disabled), "copying a displayed address is local");
    assert.match(page.document.getElementById("addressNavStatus").textContent, /Display-only: the owning CLI connection is closed/);
    const postedCount = page.posted.length;
    page.navigateToAddress(FIELD, "object");
    assert.equal(page.posted.length, postedCount, "a display-only snapshot cannot start another live read");
});

test("struct array fields expand in place, follow actual references and page without carrying sensitive consent", async () => {
    const page = loadPage();
    const { controller, calls } = makeHost(page, { array: true });
    const seen = { count: 0 };
    const results = page.document.getElementById("addressQueryViews");
    const button = text => {
        const found = results.findAll(node => node.tagName === "button").find(node => node.textContent === text);
        assert.ok(found, `missing ${text}`);
        return found;
    };
    const reveal = () => page.document.getElementById("addressQueryViews").findAll(node => node.tagName === "button")
        .find(node => node.textContent === "Reveal values of this object").click();
    page.navigateToAddress(TARGET, "object");
    await pump(page, controller, seen);
    assert.match(results.textContent, /Array range \[0, 8\) of 11/);
    assert.equal(button("Previous array range").disabled, true);
    const structs = results.findAll(node => node.classes.has("query-view-nested"));
    assert.equal(structs.length, 8);
    assert.match(structs[0].textContent, /Nested.Value/);
    assert.ok(links(structs[0]).some(node => node.textContent === FIELD));
    assert.ok(!links(structs[0]).some(node => node.textContent === TARGET), "no synthetic struct-interior link");
    reveal();
    await pump(page, controller, seen);
    assert.match(results.textContent, /array-secret/);
    button("Next array range").click();
    await pump(page, controller, seen);
    assert.match(results.textContent, /Array range \[8, 11\) of 11/);
    assert.equal(calls.frames.at(-1).arrayStartIndex, 8);
    assert.equal(calls.frames.at(-1).arrayCount, 8);
    assert.ok(!("includeSensitiveValues" in calls.frames.at(-1)));
    assert.ok(!results.textContent.includes("array-secret"));
    assert.equal(button("Next array range").disabled, true);
    assert.equal(results.findAll(node => node.classes.has("query-view-nested")).length, 3);
    reveal();
    await pump(page, controller, seen);
    assert.equal(calls.frames.at(-1).arrayStartIndex, 8, "revealing keeps the selected array range");
    assert.equal(calls.frames.at(-1).includeSensitiveValues, true);
    assert.equal(calls.sensitive.length, 2);
    button("Previous array range").click();
    await pump(page, controller, seen);
    assert.equal(calls.frames.at(-1).arrayStartIndex, 0);
    assert.ok(!("includeSensitiveValues" in calls.frames.at(-1)));
    links(results).find(node => node.textContent === FIELD).click();
    await pump(page, controller, seen);
    assert.equal(calls.frames.at(-1).address, FIELD);
    assert.ok(!("arrayStartIndex" in calls.frames.at(-1)), "referenced object does not inherit the array range");
    assert.ok(!("includeSensitiveValues" in calls.frames.at(-1)));
    page.back.click();
    assert.match(results.textContent, /Array range \[0, 8\) of 11/);
});

test("invalid range requests never dispatch and old CLI range fallback is reported explicitly", async () => {
    const page = loadPage();
    const { controller, calls } = makeHost(page, { array: true, oldRangeCli: true });
    for (const arrayStartIndex of [-1, "8", 1.5, 2147483648, null]) {
        await controller.handle({ type: "navigateAddress", kind: "heap", view: "object", address: TARGET, arrayStartIndex });
    }
    for (const arrayCount of [0, 9, "8", 1.5, null]) {
        await controller.handle({ type: "navigateAddress", kind: "heap", view: "object", address: TARGET, arrayCount });
    }
    assert.equal(calls.query.length, 0);
    page.navigateToAddress(TARGET, "object", false, 8, 8);
    await pump(page, controller);
    assert.match(page.document.getElementById("addressQueryViews").textContent, /did not confirm the requested array range/);
});

test("revealing object values requires fresh separate consent and never caches the raw result in history", async () => {
    const page = loadPage();
    const { controller, calls } = makeHost(page);
    const seen = { count: 0 };
    const reveal = () => page.document.getElementById("addressQueryViews")
        .findAll(node => node.tagName === "button").find(node => node.textContent === "Reveal values of this object").click();
    page.navigateToAddress(TARGET, "object");
    await pump(page, controller, seen);
    assert.equal(calls.sensitive.length, 0);
    assert.ok(!page.document.getElementById("addressNavActions").textContent.includes("Reveal values"));
    assert.ok(page.document.getElementById("addressQueryViews").querySelector(".object-value-actions")
        .textContent.includes("Reveal values of this object"), "non-string objects expose reveal near their fields");
    assert.ok(!("includeSensitiveValues" in calls.frames[0]));
    reveal();
    await pump(page, controller, seen);
    assert.deepEqual(calls.sensitive, [TARGET]);
    assert.equal(calls.frames[1].includeSensitiveValues, true);
    assert.equal(calls.frames[1].acknowledgeSensitiveValues, true);
    assert.equal(calls.frames[1].acknowledgeRisk, "high", "operational acknowledgement is still required");
    const results = page.document.getElementById("addressQueryViews");
    const previewActions = results.querySelector(".object-value-actions");
    assert.ok(previewActions.parentElement.classList.contains("query-view-metric"));
    assert.match(previewActions.parentElement.textContent, /String preview/);
    assert.match(previewActions.textContent, /Hide values/);
    assert.match(results.textContent, /<script>secret-preview<\/script>/);
    assert.match(results.textContent, /256 characters/);
    assert.ok(!results.findAll(node => node.tagName === "script").length, "values are text, never markup");
    assert.ok(!JSON.stringify(page.addressNavigationState.entries).includes("secret-preview"));
    page.document.getElementById("addressQueryViews").findAll(node => node.tagName === "button")
        .find(node => node.textContent === "Hide values").click();
    assert.ok(!results.textContent.includes("secret-preview"));
    reveal();
    await pump(page, controller, seen);
    assert.equal(calls.sensitive.length, 2);
    page.back.click();
    assert.ok(!results.textContent.includes("secret-preview"));
    page.forward.click();
    assert.ok(!results.textContent.includes("secret-preview"), "forward cannot restore sensitive values");
    assert.match(results.textContent, /not cached/);
    assert.equal(calls.query.length, 3, "history navigation never re-reads");
    reveal();
    await pump(page, controller, seen);
    assert.equal(calls.sensitive.length, 3, "no remembered sensitive grant");
    assert.match(results.textContent, /secret-preview/);
    page.close.click();
    assert.ok(!results.textContent.includes("secret-preview"));
    page.navigateToAddress(TARGET, "object", true);
    page.close.click();
    await pump(page, controller, seen);
    assert.equal(page.nav.hidden, true, "a late sensitive response cannot reopen the inspector");
    assert.ok(!results.textContent.includes("secret-preview"));
    assert.ok(!JSON.stringify(page.addressNavigationState).includes("secret-preview"));
});

test("declined sensitive consent and an older CLI cannot masquerade as successful disclosure", async () => {
    for (const options of [{ declineSensitive: true }, { oldCli: true }]) {
        const page = loadPage();
        const { controller, calls } = makeHost(page, options);
        page.navigateToAddress(TARGET, "object", true);
        await pump(page, controller);
        assert.ok(!page.document.getElementById("addressQueryViews").textContent.includes("secret-preview"));
        assert.match(page.document.getElementById("addressQueryViews").textContent,
            options.oldCli ? /CLI did not confirm/ : /revealing sensitive values was declined/);
        assert.equal(calls.query.length, options.oldCli ? 1 : 0);
    }
});

test("heap context changes during sensitive consent prevent dispatch and non-object reveal requests are rejected", async () => {
    const posted = [];
    let handle = "h1";
    let reads = 0;
    const controller = new AddressNavigationController({
        currentHeapHandle: () => handle,
        acknowledgeSensitiveValues: async () => { handle = "h2"; return true; },
        acknowledge: async () => true,
        query: async () => { reads++; return objectResult(TARGET); },
        post: message => posted.push(message), log() {},
    });
    await controller.handle({ type: "navigateAddress", kind: "heap", view: "object", address: TARGET, includeSensitiveValues: true });
    assert.equal(reads, 0);
    assert.match(posted[0].message, /context changed/);
    await controller.handle({ type: "navigateAddress", kind: "heap", view: "gcroot", address: TARGET, includeSensitiveValues: true });
    assert.equal(reads, 0);
});

test("retention path → object → referencing statics, through the real webview script and host controller", async () => {
    const page = loadPage();
    const { controller, calls } = makeHost(page);
    const seen = { count: 0 };

    page.renderQueryView("heapQueryViews", "retention-paths", retentionPaths(TARGET), false);
    const targetLink = links(page.heap).find(node => node.textContent === TARGET);
    assert.ok(targetLink, "the 64-bit retention target address is an exact clickable link");
    assert.ok(copies(page.heap).length >= 3, "every valid address has a copy action");

    targetLink.click();
    assert.deepEqual(page.posted, [
        { type: "navigateAddress", kind: "heap", view: "object", address: TARGET, navigationId: 1 },
        { type: "navigationSelection", view: "object", address: TARGET, ready: false, navigationId: 1,
            history: [{ view: "object", address: TARGET, navigationId: 1 }] },
    ]);
    assert.equal(page.nav.hidden, false);
    assert.match(page.document.getElementById("addressNavStatus").textContent, /Looking up/);
    await pump(page, controller, seen);

    assert.deepEqual(calls.acknowledge, [{ view: "object", address: TARGET }]);
    assert.deepEqual(calls.query, [{ handle: "h1", view: "object", address: TARGET, acknowledgeRisk: true }]);
    assert.deepEqual(calls.frames[0], { type: "query", requestId: "r1", handle: "h1", view: "object", address: TARGET, acknowledgeRisk: "high" });
    const results = page.document.getElementById("addressQueryViews");
    assert.match(results.textContent, /Sample\.Cache/);
    assert.ok(links(results).some(node => node.textContent === FIELD), "object field references continue the navigation");
    assert.match(results.textContent, /Read from the dump file/);

    const actions = page.document.getElementById("addressNavActions").findAll(node => node.tagName === "button");
    const staticsButton = actions.find(node => node.textContent === "Referencing statics");
    assert.ok(staticsButton);
    staticsButton.click();
    await pump(page, controller, seen);

    assert.equal(calls.acknowledge.length, 1, "the captured static-field filter needs no acknowledgement");
    assert.deepEqual(calls.query[1], { handle: "h1", view: "static-fields", address: TARGET, acknowledgeRisk: false });
    assert.ok(!("acknowledgeRisk" in calls.frames[1]));
    assert.equal(calls.frames[1].address, TARGET);
    assert.match(results.textContent, /Sample\.Holder/);
    assert.match(results.textContent, /Instance/);
    assert.ok(links(results).some(node => node.textContent === TARGET));
    assert.deepEqual(crumbs(page).map(node => node.textContent), [`Object ${TARGET}`, `Referencing statics ${TARGET}`]);
    assert.equal(crumbs(page)[1].attributes["aria-current"], "step");

    page.back.click();
    assert.equal(page.posted.filter(m => m.type === "navigateAddress").length, 2, "back/forward re-render cached results without a new query");
    assert.deepEqual(page.posted.at(-1), { type: "navigationSelection", view: "object", address: TARGET, ready: true, navigationId: 1,
        history: [{ view: "object", address: TARGET, navigationId: 1 }, { view: "static-fields", address: TARGET, navigationId: 2 }] });
    assert.match(results.textContent, /_inner/);
    assert.equal(page.back.disabled, true);
    assert.equal(page.forward.disabled, false);
    page.forward.click();
    assert.match(results.textContent, /Instance/);
    crumbs(page)[0].click();
    assert.match(results.textContent, /_inner/);
    assert.equal(page.posted.filter(m => m.type === "navigateAddress").length, 2);
});

test("copy requests carry the exact address and the host validates them", async () => {
    const page = loadPage();
    const { controller, calls } = makeHost(page);
    page.renderQueryView("heapQueryViews", "retention-paths", retentionPaths(TARGET), false);
    copies(page.heap)[0].click();
    await pump(page, controller);
    assert.deepEqual(calls.copy, [TARGET]);
    for (const address of [4096, "4096", "0x0", "0x", " 0x10", "0x10\n", "0x1ffffffffffffffff", 0xfedcba9876543211, undefined]) {
        await controller.handle({ type: "copyAddress", address });
    }
    assert.deepEqual(calls.copy, [TARGET]);
});

test("unsafe numeric JavaScript addresses stay flagged, unlinked and uncopyable", () => {
    const page = loadPage();
    page.renderQueryView("heapQueryViews", "retention-paths", retentionPaths(0xfedcba9876543211), false);
    assert.match(page.heap.textContent, /Unavailable: numeric address/);
    assert.ok(!links(page.heap).some(node => /^0xfedcba/.test(node.textContent)));
    const safePage = loadPage();
    safePage.renderQueryView("heapQueryViews", "static-fields", staticsResult(4096), false);
    assert.equal(links(safePage.heap).length + copies(safePage.heap).length, 0);
    assert.match(safePage.heap.textContent, /Unavailable: numeric address/);
    assert.ok(!/0x1000\b/.test(safePage.heap.textContent));
    page.renderQueryView("heapQueryViews", "static-fields", staticsResult(4096), false);
    assert.match(page.heap.textContent, /Unavailable: numeric address/);
    assert.deepEqual(links(page.heap).map(node => node.textContent), [ROOT], "only string addresses are links");
    page.navigateToAddress("4096", "object");
    page.navigateToAddress(0xfedcba9876543211, "object");
    assert.equal(page.posted.length, 0);
});

test("the host rejects malformed, unauthorised and unknown-handle navigation requests", async () => {
    const page = loadPage();
    const { controller, calls } = makeHost(page);
    const valid = { type: "navigateAddress", kind: "heap", view: "object", address: TARGET };
    for (const bad of [
        { ...valid, address: 4096 }, { ...valid, address: "4096" }, { ...valid, address: "0x0" },
        { ...valid, address: "0x1ffffffffffffffff" }, { ...valid, address: " 0x10" },
        { ...valid, view: "objsize" }, { ...valid, view: "thread-statics" }, { ...valid, view: undefined },
        { ...valid, kind: "thread-snapshot" }, { ...valid, handle: "other" },
    ].filter(message => message.handle === undefined)) {
        assert.equal(await controller.handle(bad), true);
    }
    assert.equal(calls.query.length, 0);
    assert.equal(calls.acknowledge.length, 0);

    // An unrelated message type is not consumed.
    assert.equal(await controller.handle({ type: "start" }), false);

    const noHandle = makeHost(page, { noHandle: true });
    page.navigateToAddress(TARGET, "object");
    await noHandle.controller.handle(page.posted[0]);
    assert.equal(noHandle.calls.query.length, 0);
    assert.match(page.document.getElementById("addressQueryViews").textContent, /No heap snapshot with a retained handle/);

    const declined = makeHost(page, { decline: true });
    page.navigateToAddress(FIELD, "object");
    await declined.controller.handle(page.posted.filter(m => m.type === "navigateAddress").at(-1));
    assert.equal(declined.calls.query.length, 0, "a declined acknowledgement never reaches the CLI");
    assert.match(page.document.getElementById("addressQueryViews").textContent, /acknowledgement was declined/);

    const failing = makeHost(page, { fail: true });
    page.navigateToAddress(ROOT, "gcroot");
    await failing.controller.handle(page.posted.filter(m => m.type === "navigateAddress").at(-1));
    assert.match(page.document.getElementById("addressQueryViews").textContent, /CLI said no/);
});

test("history is bounded, drops forward entries and keeps a single result in the DOM", async () => {
    const page = loadPage();
    const { controller } = makeHost(page);
    const seen = { count: 0 };
    for (let i = 1; i <= MAX_NAVIGATION_HISTORY + 5; i++) {
        page.navigateToAddress("0x" + (0x1000 + i).toString(16), "object");
        await pump(page, controller, seen);
    }
    assert.equal(page.addressNavigationState.entries.length, MAX_NAVIGATION_HISTORY);
    assert.equal(crumbs(page).length, MAX_NAVIGATION_HISTORY);
    assert.equal(page.document.getElementById("addressQueryViews").children.length, 1);

    page.back.click();
    page.back.click();
    page.navigateToAddress(FIELD, "gcroot");
    assert.equal(page.addressNavigationState.entries.length, MAX_NAVIGATION_HISTORY - 1);
    assert.equal(page.forward.disabled, true);

    page.resetAddressNavigation();
    assert.equal(page.nav.hidden, true);
    assert.equal(page.document.getElementById("addressQueryViews").children.length, 0);
});

test("a late response for a superseded request does not overwrite the current view", async () => {
    const page = loadPage();
    page.navigateToAddress(TARGET, "object");
    page.navigateToAddress(FIELD, "object");
    page.handleAddressNavigationMessage({ type: "navigation", view: "object", address: TARGET, navigationId: 1, result: objectResult(TARGET) });
    const results = page.document.getElementById("addressQueryViews");
    assert.ok(!/Sample\.Cache/.test(results.textContent), "only the current entry is rendered");
    page.handleAddressNavigationMessage({ type: "navigation", view: "object", address: FIELD, navigationId: 2, result: objectResult(FIELD) });
    assert.match(results.textContent, /Sample\.Cache/);
});

test("live and dump panels emit the same navigation surface; thread addresses link only in the dump panel", () => {
    const script = html => /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(html)[1];
    const live = renderHtml(target, "nonce123", 120);
    const dump = renderDumpAnalysisHtml("sample.dmp", "nonce123");
    for (const html of [live, dump]) {
        for (const id of ["addressNavigation", "addressNavBack", "addressNavForward", "addressBreadcrumb", "addressQueryViews"]) {
            assert.match(html, new RegExp(`id="${id}"`));
        }
        assert.doesNotThrow(() => new Function(script(html)));
        assert.match(script(html), /if \(handleAddressNavigationMessage\(message\)\) return;/);
        assert.match(script(html), /resetAddressNavigation\(\);/);
        assert.match(script(html), /function appendAddressValue/);
    }
    assert.match(script(dump), /addressLinkContainers\.add\('threadQueryViews'\)/);
    assert.doesNotMatch(script(live), /addressLinkContainers\.add\('threadQueryViews'\)/);
});

test("address helpers never accept numbers and the navigable views are exactly the CLI address views", () => {
    assert.equal(parseHexAddress("0xFEDCBA9876543211"), TARGET);
    for (const bad of [4096, 0xfedcba9876543211, "4096", "0x", "0x0", "0x0000", "0X10", "0x10 ", "0x" + "f".repeat(17), null, {}]) {
        assert.equal(parseHexAddress(bad), undefined, String(bad));
    }
    assert.deepEqual([...NAVIGATION_VIEWS], ["object", "gcroot", "static-fields"]);
    assert.equal(isNavigationView("objsize"), false);
    assert.deepEqual(buildQueryFrame("r", "h", "timers"), { type: "query", requestId: "r", handle: "h", view: "timers" });
});

test("contextual inspector opens in place, restores focus on close and does not reopen for a late response", () => {
    const page = loadPage();
    let restored = false;
    page.document.activeElement = { focus() { restored = true; } };
    page.navigateToAddress(TARGET, "object");
    assert.equal(page.nav.hidden, false);
    assert.equal(page.close.focused, true);
    page.close.click();
    assert.equal(page.nav.hidden, true);
    assert.equal(restored, true);
    page.handleAddressNavigationMessage({ type: "navigation", view: "object", address: TARGET,
        navigationId: 1, result: objectResult(TARGET) });
    assert.equal(page.nav.hidden, true);
    page.navigateToAddress(FIELD, "object");
    assert.equal(page.nav.hidden, false);
    page.nav.listeners.keydown({ key: "Escape", preventDefault() {} });
    assert.equal(page.nav.hidden, true);
});
