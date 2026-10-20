const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const fs = require("node:fs");
const Module = require("node:module");
const resolve = Module._resolveFilename;
Module._resolveFilename = function(request, ...rest) {
    return request === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js") : resolve.call(this, request, ...rest);
};
const vscode = require("./stubs/vscode-stub.js");
vscode.workspace = { async openTextDocument(value) { return value; } };
const e = require("../out/resultExport.js"), b = require("../out/baselineCompare.js");
const A = "a".repeat(32), B = "b".repeat(32);
const capture = { captureId: A, state: "sealed", artifacts: [{ artifactId: B }] };

test("JSON/CSV exclude free text, dynamic keys, source paths and arbitrary numeric values", () => {
    const input = { totalSamples: 100, frame: { method: "PASSWORD_SECRET", module: "/secret/path" },
        data: { topTypes: [{ typeFullName: "PASSWORD_SECRET", totalBytes: 12 }], secret: 12345,
            retainedExceptions: [{ exceptionMessage: "PASSWORD_SECRET", value: 12345 }] },
        tables: [{ rows: [["PASSWORD_SECRET", 12345]] }],
        findings: { topHotspots: [{ symbol: { method: "PASSWORD_SECRET" }, exclusiveSamples: 7 }] } };
    for (const format of ["json", "csv"]) {
        const text = e.renderShareable(input, format);
        assert.ok(!text.includes("PASSWORD_SECRET") && !text.includes("/secret/path") && !text.includes("12345"));
        assert.match(text, /totalSamples/);
        assert.match(text, /exclusiveSamples/);
    }
    const result = e.projectShareable(input);
    assert.equal(result.measurements[0].value, 100);
    assert.ok(result.omitted > 0);
});
test("measurement projections retain unavailable and measured zero with explicit bounded omissions", () => {
    assert.equal(e.projectShareable({ Findings: { TotalSamples: 42, TopHotspots: [{ ExclusiveSamples: 7 }] } })
        .measurements[0].value, 42);
    const result = e.projectShareable({ metrics: [{ value: null }, { value: 0 }, { value: Infinity }],
        topHotspots: Array.from({ length: 220 }, () => ({ exclusiveSamples: 1 })) });
    assert.deepEqual(result.measurements.slice(0, 2).map(m => m.value), [null, 0]);
    assert.equal(result.measurements.length, 202);
    assert.ok(result.omitted >= 21);
    assert.match(result.notice, /Lossy measurement-only/);
});
test("export uses only host-held evidence and opens an editable local document", async () => {
    const exporter = new e.ResultExporter(), docs = [], errors = [];
    vscode.window.showTextDocument = async document => docs.push(document);
    vscode.window.showErrorMessage = async message => errors.push(message);
    await exporter.handle({ type: "exportResult", format: "json", data: { totalSamples: 999 } });
    assert.match(errors[0], /Collect a result/);
    exporter.set({ totalSamples: 3 });
    await exporter.handle({ type: "exportResult", format: "json", data: { totalSamples: 999 } });
    assert.equal(JSON.parse(docs[0].content).measurements[0].value, 3);
    assert.equal(docs[0].language, "json");
    exporter.clear();
    await exporter.handle({ type: "exportResult", format: "csv" });
    assert.equal(docs.length, 1);
});
test("CPU export stays bound to its displayed capture after another collector publishes a result", async () => {
    const exporter = new e.ResultExporter(), docs = [], errors = [];
    vscode.window.showTextDocument = async document => docs.push(document);
    vscode.window.showErrorMessage = async message => errors.push(message);
    exporter.observe({ type: "capture", kind: "cpu", summary: { totalSamples: 12345 } });
    exporter.observe({ type: "captureStatus", kind: "exceptions", state: "running" });
    exporter.observe({ type: "capture", kind: "exceptions", result: { totalExceptions: 678 } });

    for (const format of ["json", "csv"]) {
        await exporter.handle({ type: "exportResult", captureKind: "cpu", format });
    }

    assert.equal(errors.length, 0);
    assert.match(docs[0].content, /12345/);
    assert.doesNotMatch(docs[0].content, /678/);
    assert.match(docs[1].content, /12345/);
    assert.doesNotMatch(docs[1].content, /678/);
    assert.match(docs[1].content, /metric,value/);
    await exporter.handle({ type: "exportResult", format: "json" });
    assert.equal(JSON.parse(docs[2].content).measurements[0].value, 678);
});
test("exports reject stale webview evidence identities", async () => {
    const exporter = new e.ResultExporter(), docs = [], errors = [];
    vscode.window.showTextDocument = async document => docs.push(document);
    vscode.window.showErrorMessage = async message => errors.push(message);
    exporter.observe({ type: "capture", kind: "cpu", evidenceId: A, summary: { totalSamples: 42 } });
    await exporter.handle({ type: "exportResult", captureKind: "cpu", evidenceId: A, format: "json" });
    exporter.observe({ type: "capture", kind: "cpu", evidenceId: B, summary: { totalSamples: 99 } });
    await exporter.handle({ type: "exportResult", captureKind: "cpu", evidenceId: A, format: "json" });
    await exporter.handle({ type: "exportResult", captureKind: "cpu", evidenceId: B, format: "json" });
    assert.equal(docs.length, 2);
    assert.equal(JSON.parse(docs[0].content).measurements[0].value, 42);
    assert.equal(JSON.parse(docs[1].content).measurements[0].value, 99);
    assert.match(errors[0], /replaced before export/);
});
test("scoped exports preserve evidence through rejected, running and failed attempts until replaced", async () => {
    const exporter = new e.ResultExporter(), docs = [], errors = [];
    vscode.window.showTextDocument = async document => docs.push(document);
    vscode.window.showErrorMessage = async message => errors.push(message);
    exporter.observe({ type: "capture", kind: "cpu", summary: { totalSamples: 42 } });
    exporter.observe({ type: "captureStatus", kind: "cpu", state: "error" });
    await exporter.handle({ type: "exportResult", captureKind: "cpu", format: "json" });
    assert.equal(JSON.parse(docs[0].content).measurements[0].value, 42);

    exporter.observe({ type: "captureStatus", kind: "cpu", state: "running" });
    exporter.observe({ type: "captureStatus", kind: "cpu", state: "error" });
    exporter.observe({ type: "capture", kind: "exceptions", result: { totalExceptions: 7 } });

    await exporter.handle({ type: "exportResult", captureKind: "cpu", format: "json" });
    assert.equal(docs.length, 2);
    assert.equal(JSON.parse(docs[1].content).measurements[0].value, 42);

    exporter.observe({ type: "capture", kind: "cpu", summary: { totalSamples: 99 } });
    await exporter.handle({ type: "exportResult", captureKind: "cpu", format: "json" });
    assert.equal(JSON.parse(docs[2].content).measurements[0].value, 99);

    exporter.observe({ type: "capture", kind: "cpu" });
    await exporter.handle({ type: "exportResult", captureKind: "cpu", format: "csv" });
    assert.equal(docs.length, 3);
    assert.match(errors[0], /displayed capture is no longer available/);
});
test("generic export buttons include their containing capture kind when available", () => {
    const posted = [], listeners = new Map();
    const document = { getElementById(id) {
        return { addEventListener(_event, handler) { listeners.set(id, handler); },
            closest(selector) { return selector === "[data-export-kind]" ? { dataset: { exportKind: "cpu" } } : null; } };
    } };
    new Function("document", "vscode", e.RESULT_EXPORT_SCRIPT)(document, { postMessage: message => posted.push(message) });

    listeners.get("exportJson")();
    listeners.get("exportCsv")();
    assert.deepEqual(posted.map(message => [message.format, message.captureKind]),
        [["json", "cpu"], ["csv", "cpu"]]);
});
test("CPU export buttons carry the evidence identity with the scoped export", () => {
    const posted = [], listeners = new Map();
    const document = { getElementById(id) {
        return { addEventListener(_event, handler) { listeners.set(id, handler); },
            closest(selector) {
                if (selector === "[data-export-kind]") return { dataset: { exportKind: "cpu" } };
                if (selector === "[data-evidence-id]") return { dataset: { evidenceId: A } };
                return null;
            } };
    } };
    new Function("document", "vscode", e.RESULT_EXPORT_SCRIPT)(document, { postMessage: message => posted.push(message) });
    listeners.get("exportJson")();
    assert.deepEqual(posted[0], { type: "exportResult", format: "json", captureKind: "cpu", evidenceId: A });
});
test("address exports follow host-held navigation history and refuse pending or failed evidence", async () => {
    const exporter = new e.ResultExporter(), docs = [], errors = [];
    vscode.window.showTextDocument = async document => docs.push(document);
    vscode.window.showErrorMessage = async message => errors.push(message);
    const selected = (address, ready) => exporter.handle({ type: "navigationSelection", view: "object", address, ready,
        result: { totalBytes: 9999 } });
    const exported = () => exporter.handle({ type: "exportResult", format: "json" });
    exporter.observe({ type: "capture", result: { totalBytes: 100 } });
    await selected("0xff", false);
    await exported();
    assert.equal(docs.length, 0);
    exporter.observe({ type: "navigation", view: "object", address: "0xff", result: { totalBytes: 7 } });
    await selected("0xff", true);
    await exported();
    exporter.observe({ type: "navigation", view: "object", address: "0xaa", result: { totalBytes: 8 } });
    await selected("0xaa", true);
    await exported();
    await selected("0xff", true);
    await exported();
    assert.deepEqual(docs.map(d => JSON.parse(d.content).measurements[0].value), [7, 8, 7]);
    await selected("0xbb", false);
    await exported();
    assert.equal(docs.length, 3);
    assert.equal(errors.length, 2);
    exporter.observe({ type: "capture", result: { totalBytes: 200 } });
    await selected("0xff", true);
    await exported();
    assert.equal(docs.length, 3);
    for (let i = 1; i <= 21; i++)
        exporter.observe({ type: "navigation", view: "object", address: `0x${i.toString(16)}`, result: { totalBytes: i } });
    await selected("0x1", true);
    await exported();
    assert.equal(docs.length, 3);
});
test("branching history prunes discarded forward results, not retained breadcrumb evidence", async () => {
    const exporter = new e.ResultExporter(), docs = [];
    vscode.window.showTextDocument = async document => docs.push(document);
    const history = Array.from({ length: 20 }, (_, i) => ({ view: "object", address: `0x${(i + 1).toString(16)}` }));
    await exporter.handle({ type: "navigationSelection", ...history[0], ready: false, history });
    for (let i = 0; i < history.length; i++)
        exporter.observe({ type: "navigation", ...history[i], result: { totalBytes: i + 1 } });
    const branch = [...history.slice(0, 2), { view: "gcroot", address: "0xff" }];
    await exporter.handle({ type: "navigationSelection", ...branch[2], ready: false, history: branch });
    exporter.observe({ type: "navigation", ...branch[2], result: { totalBytes: 99 } });
    exporter.observe({ type: "navigation", ...history[19], result: { totalBytes: 999 } });
    await exporter.handle({ type: "navigationSelection", ...history[0], ready: true, history: branch });
    await exporter.handle({ type: "exportResult", format: "json" });
    assert.equal(JSON.parse(docs[0].content).measurements[0].value, 1);
    assert.equal(exporter.navigation.size, 3);
});
test("sensitive object responses are never retained by the exporter, even if the page claims they are ready", async () => {
    const exporter = new e.ResultExporter(), docs = [], errors = [];
    vscode.window.showTextDocument = async document => docs.push(document);
    vscode.window.showErrorMessage = async message => errors.push(message);
    const entry = { view: "object", address: "0xff", navigationId: 1 };
    exporter.observe({ type: "navigation", ...entry, sensitiveValues: true,
        result: { stringValue: "SECRET", totalBytes: 42 } });
    await exporter.handle({ type: "navigationSelection", ...entry, ready: true });
    await exporter.handle({ type: "exportResult", format: "json" });
    assert.equal(exporter.navigation.size, 0);
    assert.equal(docs.length, 0);
    assert.equal(errors.length, 1);
});
test("repeated reads of the same address preserve each entry's own host-held evidence", async () => {
    const exporter = new e.ResultExporter(), docs = [];
    vscode.window.showTextDocument = async document => docs.push(document);
    const history = [1, 2].map(navigationId => ({ navigationId, view: "object", address: "0xff" }));
    await exporter.handle({ type: "navigationSelection", ...history[0], ready: false, history });
    exporter.observe({ type: "navigation", ...history[0], result: { totalBytes: 1 } });
    exporter.observe({ type: "navigation", ...history[1], result: { totalBytes: 2 } });
    for (const entry of history) {
        await exporter.handle({ type: "navigationSelection", ...entry, ready: true, history });
        await exporter.handle({ type: "exportResult", format: "json" });
    }
    assert.deepEqual(docs.map(d => JSON.parse(d.content).measurements[0].value), [1, 2]);
});
test("baseline compare builds only validated catalog IDs and refuses mutable or unrelated references", () => {
    assert.deepEqual(b.compareArgs(capture, B, capture, B), ["compare", "--baseline-capture-id", A,
        "--baseline-artifact-id", B, "--candidate-capture-id", A, "--candidate-artifact-id", B, "--json"]);
    assert.throws(() => b.compareArgs({ ...capture, state: "interrupted" }, B, capture, B), /sealed/);
    assert.throws(() => b.compareArgs(capture, "c".repeat(32), capture, B), /catalog/);
});
const comparison = () => ({ schema: "dotnet-diagnostics/historical-comparison/v1",
    compatibility: { status: "qualified", reasons: [{ message: "<script>Retained only</script>" }] },
    metrics: [{ key: "=secret()", unit: "bytes", aggregation: "sum", normalization: "none", population: "retained",
        leftValue: 0, rightValue: 4, absoluteDelta: 4, relativeDelta: null, unavailableReason: "ZeroBaseline" }] });
test("comparison preserves CLI deltas, zero baseline, nulls and measurement semantics", () => {
    const result = b.parseComparison(JSON.stringify(comparison()));
    assert.equal(result.metrics[0].absoluteDelta, 4);
    assert.equal(result.metrics[0].relativeDelta, null);
    assert.equal(result.metrics[0].unit, "bytes");
    assert.equal(result.metrics[0].unavailableReason, "ZeroBaseline");
    const exported = e.renderShareable({ metrics: result.metrics }, "csv");
    assert.ok(!exported.includes("=secret()"));
    assert.match(exported, /absoluteDelta/);
    assert.throws(() => b.parseComparison("{}"), /unsupported/);
    const bad = comparison(); bad.metrics[0].rightValue = "4";
    assert.throws(() => b.parseComparison(JSON.stringify(bad)), /measurement/);
});
test("comparison accepts CLI-omitted nullable fields without inventing measured zero", () => {
    const value = comparison();
    delete value.metrics[0].relativeDelta;
    delete value.metrics[0].unit;
    value.metrics.push({ key: "retained-count", unit: "samples", aggregation: "count",
        normalization: "none", population: "retained", leftValue: 2, rightValue: 3, absoluteDelta: 1, relativeDelta: 0.5 });
    const result = b.parseComparison(JSON.stringify(value));
    assert.equal(result.metrics[0].relativeDelta, null);
    assert.equal(result.metrics[0].unit, null);
    assert.equal(result.metrics[0].unavailableReason, "ZeroBaseline");
    assert.equal(result.metrics[1].unavailableReason, null);
    assert.equal(result.metrics[1].relativeDelta, 0.5);
});
function node(tag) {
    return { tag, textContent: "", children: [], appendChild(n) { this.children.push(n); }, replaceChildren() { this.children = []; } };
}
const allText = n => [n.textContent, ...n.children.map(allText)].join("\n");
test("comparison renderer is self-contained, text-only and reports qualified evidence", () => {
    const root = node("div"), render = new Function("document", b.COMPARISON_RENDERER_SCRIPT + ";return renderComparison;")({
        getElementById: () => root, createElement: node,
    });
    render("views", b.parseComparison(JSON.stringify(comparison())));
    assert.match(allText(root), /<script>Retained only<\/script>/);
    assert.match(allText(root), /not percent/);
    assert.match(allText(root), /Unavailable/);
    assert.match(allText(root), /sum \/ none \/ retained/);
});
test("comparison summary counts objective deltas, including zero and unavailable, without consuming verdict labels", () => {
    const value = comparison();
    value.verdict = "not_assessed";
    const base = value.metrics[0];
    value.metrics = [base, { ...base, absoluteDelta: -2 }, { ...base, absoluteDelta: 0 },
        { ...base, leftValue: null, rightValue: 4, absoluteDelta: null }];
    const root = node("div"), render = new Function("document", b.COMPARISON_RENDERER_SCRIPT + ";return renderComparison;")({
        getElementById: () => root, createElement: node,
    });
    render("views", b.parseComparison(JSON.stringify(value)));
    assert.match(allText(root), /1 up, 1 down, 1 flat, 1 unavailable/);
    assert.match(allText(root), /not a performance verdict/);
    assert.ok(!allText(root).includes("not_assessed"));
});
    const extensionPath = path.join(__dirname, "../out/extension.js");
    const Controller = new Function("require", "exports", fs.readFileSync(extensionPath, "utf8")
        + "\nreturn CounterPanelController;")(Module.createRequire(extensionPath), {});
    function cpuFixture() {
        const controller = Object.create(Controller.prototype), frames = [], docs = [], errors = [];
        const session = { pendingCaptures: new Map(), pendingQueries: new Map(), startInFlight: false,
            process: { exitCode: null }, closing: false };
        controller.cpuExport = { session, handle: "cpu-handle" };
        controller.exporter = new e.ResultExporter();
        controller.pendingExports = new Map();
        controller.output = { appendLine() {} };
        controller.writeFrame = (_session, frame) => {
            frames.push(frame);
            controller.handleFrame(session, JSON.stringify({ type: "export", requestId: frame.requestId,
                result: frame.format === "trace"
                    ? { kind: "trace", outputPath: frame.outputPath, totalBytes: 512, sha256: "a".repeat(64) }
                    : { Schema: "dotnet-diagnostics-mcp/investigation-summary/v1", Provenance: {},
                        Findings: { TotalSamples: 17, TopHotspots: [] }, password: "SECRET" } }));
        };
        vscode.window.showTextDocument = async document => docs.push(document);
        vscode.window.showErrorMessage = async message => errors.push(message);
        vscode.window.showInformationMessage = async () => {};
        return { controller, frames, docs, errors, session };
    }
    test("CPU summary uses the same connection's handle and projects the response before opening", async () => {
        const { controller, frames, docs } = cpuFixture();
        await controller.exportCpu("summary");
        assert.equal(frames[0].type, "export");
        assert.equal(frames[0].handle, "cpu-handle");
        assert.equal(frames[0].format, "summary");
        assert.equal(JSON.parse(docs[0].content).measurements[0].value, 17);
        assert.ok(!docs[0].content.includes("SECRET"));
        assert.equal(controller.pendingExports.size, 0);
    });
    test("raw trace export requires a second explicit confirmation and a native local destination", async () => {
        const { controller, frames, errors } = cpuFixture();
        vscode.window.showWarningMessage = async () => "Cancel";
        await controller.exportCpu("trace");
        assert.equal(frames.length, 0);
        vscode.window.showWarningMessage = async () => "Export Locally";
        vscode.window.showSaveDialog = async () => ({ scheme: "https", fsPath: "/untrusted" });
        await controller.exportCpu("trace");
        assert.equal(frames.length, 0);
        assert.match(errors[0], /local file/);
        vscode.window.showSaveDialog = async () => ({ scheme: "file", fsPath: "/operator-choice.nettrace" });
        await controller.exportCpu("trace");
        assert.equal(frames[0].outputPath, "/operator-choice.nettrace");
        assert.equal(frames[0].acknowledgeRisk, "critical");
        assert.equal(frames[0].tracePath, undefined);
        assert.equal(errors.length, 1);
    });
    test("raw export rejects an invalid receipt instead of announcing success", async () => {
        const { controller, errors } = cpuFixture();
        let successes = 0;
        vscode.window.showWarningMessage = async () => "Export Locally";
        vscode.window.showSaveDialog = async () => ({ scheme: "file", fsPath: "/operator-choice.nettrace" });
        vscode.window.showInformationMessage = async () => { successes++; };
        controller.writeFrame = (session, frame) => controller.handleFrame(session, JSON.stringify({
            type: "export", requestId: frame.requestId, result: { kind: "trace", outputPath: "/wrong.nettrace" },
        }));
        await controller.exportCpu("trace");
        assert.match(errors[0], /verified trace export receipt/);
        assert.equal(successes, 0);
    });
    test("summary export refuses malformed responses instead of opening empty evidence", async () => {
        for (const result of [undefined, {}, { Schema: "future-schema", Findings: {} }]) {
            const { controller, errors, docs } = cpuFixture();
            controller.writeFrame = (session, frame) => controller.handleFrame(session, JSON.stringify({
                type: "export", requestId: frame.requestId, result,
            }));
            await controller.exportCpu("summary");
            assert.match(errors[0], /unsupported CPU investigation summary/);
            assert.equal(docs.length, 0);
        }
    });
    test("CPU export refuses expired or busy connections without writing protocol frames", async () => {
        const { controller, frames, errors, session } = cpuFixture();
        session.pendingCaptures.set("capture", {});
        await controller.exportCpu("summary");
        assert.match(errors[0], /Wait for/);
        session.pendingCaptures.clear();
        session.process.exitCode = 0;
        await controller.exportCpu("summary");
        assert.match(errors[1], /updated CLI/);
        assert.equal(frames.length, 0);
    });
