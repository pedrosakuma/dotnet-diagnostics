const assert = require("node:assert/strict");
const test = require("node:test");
const fs = require("node:fs");
const path = require("node:path");
const { createRequire } = require("node:module");
const Module = require("node:module");

const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    return request === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js")
        : originalResolveFilename.call(this, request, ...rest);
};
const extensionPath = path.join(__dirname, "../out/extension.js");
const extensionRequire = createRequire(extensionPath);
const vscode = require("./stubs/vscode-stub.js");
const { renderHtml } = extensionRequire(extensionPath);
const Controller = new Function("require", "exports",
    fs.readFileSync(extensionPath, "utf8") + "\nreturn CounterPanelController;")(extensionRequire, {});
const target = {
    processId: 4242,
    operatingSystem: "Linux",
    processArchitecture: "x64",
    runtimeVersion: "10.0.0",
    managedEntrypointAssemblyName: "Sample",
};

function inlineScript() {
    return /<script nonce="[^"]*">([\s\S]*?)<\/script>/.exec(renderHtml(target, "nonce", 120))[1];
}

test("result tabs navigate in rendered order and preserve focus and selection state", () => {
    const html = renderHtml(target, "nonce", 120);
    const tabs = [...html.matchAll(/<button id="(resultTab[^"]+)" role="tab" aria-selected="(true|false)" aria-controls="([^"]+)">([^<]+)<\/button>/g)]
        .map(([, id, selected, panelId, label]) => ({
            id, label, panelId, attributes: { "aria-selected": selected === "true" ? "true" : "false",
                "aria-controls": panelId }, listeners: {}, tabIndex: -1,
            setAttribute(name, value) { this.attributes[name] = String(value); },
            getAttribute(name) { return this.attributes[name]; },
            addEventListener(name, callback) { this.listeners[name] = callback; },
            focus() { this.focused = true; },
        }));
    const panels = new Map(tabs.map(tab => [tab.panelId, { hidden: tab.attributes["aria-selected"] !== "true" }]));
    const document = {
        querySelectorAll: selector => selector === '.result-tabs [role="tab"]' ? tabs : [],
        getElementById: id => panels.get(id),
    };
    const start = inlineScript().indexOf("function selectResultTab(selected)");
    const end = inlineScript().indexOf("const eventsStatusElement =", start);
    assert.ok(start >= 0 && end > start);
    const navigation = new Function("document",
        `const resultTabs = Array.from(document.querySelectorAll('.result-tabs [role="tab"]'));`
        + `${inlineScript().slice(start, end)}; return { resultTabs, selectResultTab };`)(document);
    const resultTabs = navigation.resultTabs;
    assert.deepEqual(resultTabs.map(tab => tab.label),
        ["Live", "Workspace", "Exceptions", "Diagnostics", "Threads", "Events"]);

    function press(index, key) {
        let prevented = false;
        tabs[index].listeners.keydown({ key, preventDefault() { prevented = true; } });
        return prevented;
    }
    function assertSelected(index) {
        for (const [i, tab] of tabs.entries()) {
            const active = i === index;
            assert.equal(tab.attributes["aria-selected"], String(active));
            assert.equal(tab.tabIndex, active ? 0 : -1);
            assert.equal(panels.get(tab.panelId).hidden, !active);
        }
    }
    for (let i = 0; i < tabs.length; i++) {
        assert.equal(press(i, "ArrowRight"), true);
        assertSelected((i + 1) % tabs.length);
        assert.equal(tabs[(i + 1) % tabs.length].focused, true);
    }
    for (let i = 0; i < tabs.length; i++) {
        assert.equal(press(i, "ArrowLeft"), true);
        assertSelected((i + tabs.length - 1) % tabs.length);
    }
    assert.equal(press(4, "Home"), true);
    assertSelected(0);
    assert.equal(tabs[0].focused, true);
    assert.equal(press(2, "End"), true);
    assertSelected(tabs.length - 1);
    assert.equal(tabs.at(-1).focused, true);
    navigation.selectResultTab("exceptions");
    assertSelected(2);
    assert.equal(press(0, "Escape"), false);
});

test("initial status describes a ready selected process and live status remains counter-scoped", () => {
    const html = renderHtml(target, "nonce", 120);
    assert.match(html, /id="status" role="status">Live counters are ready\./);
    assert.doesNotMatch(html, /Connecting to CLI/);

    const script = inlineScript();
    const match = /} else if \(message\.type === 'status'\) \{([\s\S]*?)\n      \} else if \(message\.type === 'observation'\)/.exec(script);
    assert.ok(match);
    const statusElement = { textContent: "" }, errorElement = { textContent: "" };
    const updateStates = [];
    const dispatch = new Function("message", "statusElement", "errorElement", "updateButtons", match[1]);
    dispatch({ state: "stopped", message: "No live capture is running." }, statusElement, errorElement, state => updateStates.push(state));
    assert.equal(statusElement.textContent, "Live counters: No live capture is running.");
    dispatch({ state: "running", message: "Live counters and GC pauses are streaming." }, statusElement, errorElement, state => updateStates.push(state));
    assert.equal(statusElement.textContent, "Live counters and GC pauses are streaming.");
    assert.deepEqual(updateStates, [false, true]);
    assert.match(script, /Live counters stopped: target process exited\./);
    assert.match(script, /message\.status === 'failed' \? 'Live counters failed\.' : 'Live counters stopped\.'/);
    assert.doesNotMatch(script, /Capture stopped\./);

    const exceptionBranch = /message\.type === 'captureStatus' && message\.kind === 'exceptions'\) \{([\s\S]*?)\n      \} else/.exec(script);
    assert.ok(exceptionBranch);
    const captureExceptionsStatus = { textContent: "" }, captureExceptionsButton = { disabled: false };
    const liveBefore = statusElement.textContent;
    new Function("message", "captureExceptionsStatus", "captureExceptionsButton", "activateCaptureTab", "document",
        exceptionBranch[1])(
        { state: "done", message: "Exception capture complete." }, captureExceptionsStatus,
        captureExceptionsButton, () => {}, { getElementById: () => ({ replaceChildren() {} }) });
    assert.equal(captureExceptionsStatus.textContent, "Exception capture complete.");
    assert.equal(statusElement.textContent, liveBefore, "a completed exception capture must not overwrite Live status");
});

test("failure notices retain the CLI reason and offer only contextual recovery actions", async () => {
    const notifications = [], commands = [], outputShows = [], posted = [];
    let selectedAction = "Check Environment";
    const originalShowError = vscode.window.showErrorMessage;
    const originalExecuteCommand = vscode.commands.executeCommand;
    try {
        vscode.window.showErrorMessage = async (...args) => {
            notifications.push(args);
            return selectedAction;
        };
        vscode.commands.executeCommand = async command => { commands.push(command); };
        const output = { show: preserveFocus => outputShows.push(preserveFocus) };
        const controller = Object.create(Controller.prototype);
        controller.exporter = { observe() {} };
        controller.panel = { webview: { postMessage: async message => { posted.push(message); return true; } } };
        controller.output = output;

        controller.postMessage({ type: "captureStatus", kind: "heap", state: "error",
            message: "Diagnostic attach denied by ptrace_scope." });
        await new Promise(resolve => setImmediate(resolve));
        assert.deepEqual(notifications[0], [
            "Diagnostic attach denied by ptrace_scope.", "Check Environment", "Show Output",
        ]);
        assert.deepEqual(commands, ["dotnetDiagnostics.checkEnvironment"]);

        selectedAction = "Show Output";
        controller.postMessage({ type: "captureStatus", kind: "cpu", state: "error", message: "perf is unavailable." });
        await new Promise(resolve => setImmediate(resolve));
        assert.deepEqual(notifications[1], ["perf is unavailable.", "Show Output"]);
        assert.deepEqual(outputShows, [true]);

        selectedAction = "Check Environment";
        controller.postMessage({ type: "captureStatus", kind: "cpu", state: "error",
            message: "Failed to launch dotnet-diagnostics-cli: spawn ENOENT." });
        await new Promise(resolve => setImmediate(resolve));
        assert.deepEqual(notifications[2], [
            "Failed to launch dotnet-diagnostics-cli: spawn ENOENT.", "Check Environment", "Show Output",
        ]);
        assert.deepEqual(commands, ["dotnetDiagnostics.checkEnvironment", "dotnetDiagnostics.checkEnvironment"]);

        selectedAction = "Show Output";
        for (const message of ["Capture cancelled.", "The operation was canceled."]) {
            controller.postMessage({ type: "captureStatus", kind: "cpu", state: "error", message });
            await new Promise(resolve => setImmediate(resolve));
            assert.equal(notifications.length, 3, "cancellation is not presented as a recoverable failure");
            assert.equal(posted.at(-1).state, "stopped");
            assert.equal(posted.at(-1).message, message, "the CLI's original cancellation reason stays visible");
        }
    } finally {
        vscode.window.showErrorMessage = originalShowError;
        vscode.commands.executeCommand = originalExecuteCommand;
    }
});
