const assert = require("node:assert/strict");
const test = require("node:test");
const fs = require("node:fs");
const path = require("node:path");
const Module = require("node:module");
const { createRequire } = Module;
const resolve = Module._resolveFilename;
Module._resolveFilename = function(request, ...rest) {
    return request === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js") : resolve.call(this, request, ...rest);
};
const file = path.join(__dirname, "../out/extension.js");
const extensionRequire = createRequire(file);
const vscode = require("./stubs/vscode-stub.js");
const targets = [42, 43].map(processId => ({
    processId, managedEntrypointAssemblyName: `App${processId}`, runtimeVersion: "10.0.0",
    operatingSystem: "Windows", processArchitecture: "X64",
}));
const Controller = new Function("require", "exports", fs.readFileSync(file, "utf8")
    + "\nreturn CounterPanelController;")(name => name === "node:util"
        ? { promisify: () => async () => ({ stdout: JSON.stringify({ data: targets }) }) }
        : extensionRequire(name), {});

test("tree and quick-picker selection emit the same authoritative target callback without collecting", async () => {
    const controller = Object.create(Controller.prototype);
    const changed = [], opened = [];
    controller.targetChanged = target => changed.push(target);
    controller.stopCounters = controller.stopEvents = controller.disposeChild = async () => {};
    controller.resetEventsDeliveryState = () => {};
    controller.exporter = { clear() {} };
    controller.getCliPath = () => "synthetic-cli";
    controller.openPanel = target => { opened.push(target); controller.panel = { reveal() {}, dispose() {} }; };
    controller.waitForPanelReady = async () => {};
    controller.writeFrame = () => { throw Error("selection must not collect"); };
    await controller.selectProcess({ processId: 42, entrypoint: "App42", runtimeVersion: "10.0.0",
        platform: "Windows/X64", identity: "App42|10.0.0|Windows/X64" });
    assert.equal(changed.at(-1).processId, 42);
    controller.selectedTarget = controller.panel = undefined;
    vscode.window.showQuickPick = async items => items[1];
    await controller.ensureTargetAndPanel();
    assert.deepEqual(changed.map(target => target.processId), [42, 43]);
    assert.equal(controller.selectedTarget, opened.at(-1));
    controller.selectedTarget = controller.panel = undefined;
    vscode.window.showQuickPick = async () => undefined;
    assert.equal(await controller.ensureTargetAndPanel(), undefined);
    assert.equal(changed.length, 2, "cancelling a picker never activates a remembered target");
});
test("native tree wiring leaves an empty process view message unset so VS Code can show viewsWelcome", () => {
    const source = fs.readFileSync(file, "utf8");
    const start = source.indexOf('const processView = vscode.window.createTreeView("dotnetDiagnostics.processes"');
    const end = source.indexOf("context.subscriptions.push(", start);
    assert.ok(start >= 0 && end > start);
    const views = new Map();
    let changed;
    const processes = { onDidChangeTreeData: callback => { changed = callback; return {}; } };
    new Function("vscode", "processes", "history", source.slice(start, end))({
        window: { createTreeView: id => { const view = {}; views.set(id, view); return view; } },
    }, processes, {});
    changed();
    assert.equal(views.get("dotnetDiagnostics.processes").message, undefined);
    processes.message = "Cannot list processes: CLI missing";
    changed();
    assert.match(views.get("dotnetDiagnostics.processes").message, /CLI missing/);
    processes.message = undefined;
    changed();
    assert.equal(views.get("dotnetDiagnostics.processes").message, undefined);
    assert.match(views.get("dotnetDiagnostics.history").message, /extension host; not workspace-local/);
});
