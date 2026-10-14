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
    const extensionPath = path.join(__dirname, "../out/extension.js");
    const Controller = new Function("require", "exports", fs.readFileSync(extensionPath, "utf8")
        + "\nreturn CounterPanelController;")(Module.createRequire(extensionPath), {});
    function cpuFixture() {
        const controller = Object.create(Controller.prototype), frames = [], docs = [], errors = [];
        const session = { pendingCaptures: new Map(), pendingQueries: new Map(), startInFlight: false,
            process: { exitCode: null }, closing: false };
        controller.cpuExport = { session, handle: "cpu-handle" };
        controller.pendingExports = new Map();
        controller.output = { appendLine() {} };
        controller.writeFrame = (_session, frame) => {
            frames.push(frame);
            controller.handleFrame(session, JSON.stringify({ type: "export", requestId: frame.requestId,
                result: frame.format === "trace"
                    ? { kind: "trace", outputPath: frame.outputPath, totalBytes: 512, sha256: "a".repeat(64) }
                    : { findings: { totalSamples: 17 }, password: "SECRET" } }));
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
