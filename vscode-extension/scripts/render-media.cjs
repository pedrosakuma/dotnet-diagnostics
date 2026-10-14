// Regenerates media/*.png (README screenshots and the Marketplace icon) with headless Chromium.
//
// The screenshots render the extension's REAL webview HTML (renderHtml from out/extension.js) and
// feed it the same message shapes the extension host posts, but the data is a hand-written
// ILLUSTRATIVE fixture. They are not captures of a live .NET process. Requires `npm run compile`
// first (`npm run media` does both) and Chrome for Testing EXPECTED_CHROME exactly, so output is
// reproducible: set CHROME_PATH to that binary or use a Playwright `chromium-1243` cache.
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const Module = require("node:module");
const { execFileSync } = require("node:child_process");

const root = path.join(__dirname, "..");
const mediaDir = path.join(root, "media");
const workDir = path.join(root, ".media-work");

const originalResolveFilename = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    return request === "vscode"
        ? path.join(root, "tests", "stubs", "vscode-stub.js")
        : originalResolveFilename.call(this, request, ...rest);
};
const { renderHtml } = require("../out/extension.js");

const EXPECTED_CHROME = "153.0.8010.12";

function findChrome() {
    if (process.env.CHROME_PATH) return process.env.CHROME_PATH;
    const cache = path.join(os.homedir(), ".cache", "ms-playwright");
    if (fs.existsSync(cache)) {
        for (const dir of fs.readdirSync(cache).filter(d => d.startsWith("chromium-")).sort().reverse()) {
            const candidate = path.join(cache, dir, "chrome-linux64", "chrome");
            if (fs.existsSync(candidate)) return candidate;
        }
    }
    throw new Error("Chromium not found; set CHROME_PATH.");
}

const theme = `:root{--vscode-foreground:#cccccc;--vscode-editor-background:#1f1f1f;--vscode-font-family:'Segoe UI',system-ui,sans-serif;
--vscode-button-background:#0e639c;--vscode-button-foreground:#fff;--vscode-button-hoverBackground:#1177bb;
--vscode-panel-border:#3c3c3c;--vscode-descriptionForeground:#9d9d9d;--vscode-editorWidget-background:#252526;
--vscode-textLink-foreground:#4daafc;--vscode-errorForeground:#f48771;--vscode-input-background:#3c3c3c;
--vscode-input-foreground:#ccc;--vscode-font-size:13px;--vscode-editor-font-family:Consolas,monospace;}
body{font-size:13px}`;

const target = { processId: 4242, operatingSystem: "Linux", processArchitecture: "x64", runtimeVersion: "10.0.0", managedEntrypointAssemblyName: "OrdersApi" };
// Fixed so rendered timestamps (and therefore the PNGs) are reproducible.
const FIXTURE_EPOCH_MS = Date.UTC(2026, 0, 15, 12, 0, 0);
const ms = n => `00:00:00.${String(n).padStart(3, "0")}0000`;

function counterMessages() {
    const out = [{ type: "status", state: "running", message: "Streaming live counters and GC pauses (illustrative fixture)" }];
    const base = FIXTURE_EPOCH_MS;
    let seq = 0;
    const series = [
        ["cpu-usage", "CPU Usage", "%", i => 18 + 14 * Math.sin(i / 4) + (i % 7)],
        ["gc-heap-size", "GC Heap Size", "MB", i => 120 + i * 1.8],
    ];
    for (let i = 0; i < 40; i++) {
        for (const [name, displayName, unit, fn] of series) {
            out.push({ type: "observation", kind: "counters", sequence: seq++, timestamp: new Date(base + i * 1000).toISOString(),
                counter: { provider: "System.Runtime", name, displayName, value: fn(i), kind: "Metric", unit } });
        }
    }
    [[2, "AllocSmall", "NonConcurrentGC", 4], [1, "AllocSmall", "NonConcurrentGC", 2], [0, "AllocSmall", "NonConcurrentGC", 1], [2, "Induced", "NonConcurrentGC", 9]]
        .forEach(([generation, reason, type, pause], i) => out.push({ type: "observation", kind: "gc", sequence: seq++,
            timestamp: new Date(base + (30 + i) * 1000).toISOString(),
            collection: { timestamp: new Date(base + (30 + i) * 1000).toISOString(), generation, reason, type, pauseDuration: ms(pause) } }));
    return out;
}

function heapMessages() {
    const types = [["System.String", 184220, 9211040], ["System.Byte[]", 4112, 6819840], ["OrdersApi.Models.Order", 52010, 3640700],
        ["System.Collections.Generic.Dictionary<System.String,System.Object>", 9120, 1458200], ["System.Object[]", 8801, 902144]];
    return [
        { type: "captureStatus", kind: "heap", state: "idle", message: "Heap snapshot complete (illustrative fixture)" },
        { type: "capture", kind: "heap", source: "live", result: { data: { processId: 4242, suspendDuration: "00:00:00.0421000", warnings: [],
            topTypesByBytes: types.map(([typeFullName, instanceCount, totalBytes]) => ({ typeFullName, instanceCount, totalBytes })) } } },
        { type: "query", kind: "heap", view: "roots-by-kind", result: { origin: "live", processId: 4242, rootsByKind: [
            { rootKind: "Stack", rootCount: 214, distinctTargetObjects: 198, directlyReferencedBytes: 52480, pinnedRootCount: 6, interiorRootCount: 2 },
            { rootKind: "StrongHandle", rootCount: 1380, distinctTargetObjects: 1301, directlyReferencedBytes: 2411520, pinnedRootCount: 0, interiorRootCount: 0 },
            { rootKind: "Pinning", rootCount: 41, distinctTargetObjects: 41, directlyReferencedBytes: 1048576, pinnedRootCount: 41, interiorRootCount: 0 }] } },
    ];
}

function threadMessages() {
    const frames = f => f;
    const threads = [
        [1, "Wait", true, "Monitor", "OrdersApi.Cache.Refresh()"],
        [11, "Wait", true, "Monitor", "OrdersApi.Locks.AcquireB()"],
        [12, "Wait", true, "Monitor", "OrdersApi.Locks.AcquireA()"],
        [14, "Running", false, "", "System.Net.Sockets.SocketAsyncEngine.EventLoop()"],
        [15, "Wait", false, "ThreadPool", "System.Threading.PortableThreadPool.WorkerThread.WorkerThreadStart()"],
    ].map(([managedThreadId, state, isLikelyBlocked, inferredWaitReason, f]) => ({ managedThreadId, state, isLikelyBlocked, inferredWaitReason, frames: frames([f]) }));
    return [
        { type: "captureStatus", kind: "thread-snapshot", state: "idle", message: "Thread snapshot complete (illustrative fixture)" },
        { type: "capture", kind: "thread-snapshot", result: { data: { processId: 4242, totalThreads: 5, totalLocks: 2, threads } } },
        { type: "query", kind: "thread-snapshot", view: "deadlocks", result: { deadlocks: [{
            classification: "inferred-deadlock-cycle-candidate", confidence: "medium",
            cycleMembers: [{ threadId: 11, osThreadId: 1101, state: "Wait", inferredWaitReason: "Monitor" }, { threadId: 12, osThreadId: 1102, state: "Wait", inferredWaitReason: "Monitor" }],
            lockChain: [{ waitingThreadId: 11, ownerThreadId: 12, lockObjectAddress: "0x7f3a1c0012a8" }, { waitingThreadId: 12, ownerThreadId: 11, lockObjectAddress: "0x7f3a1c001340" }],
            recommendedCommands: [{ command: "query_snapshot", purpose: "Inspect the captured graph" }] }] } },
    ];
}

const scenarios = [
    { file: "live-counters.png", show: ["chart", "gcTable"], messages: counterMessages(), height: 760, delayMs: 60 },
    { file: "heap-drilldown.png", show: ["captureHeapResult"], messages: heapMessages(), height: 560, delayMs: 0 },
    { file: "thread-snapshot.png", show: ["captureThreadResult"], messages: threadMessages(), height: 760, delayMs: 0 },
];

function harness(scenario) {
    const html = renderHtml(target, "mediaNonce", 120);
    // Keep the header and only the sections that contain the requested element ids.
    const hideTop = scenario.show.includes("chart") ? "" : "#chart,#value,#status,#quality,#error,.controls,#metric,header>div+div{display:none !important}";
    const hide = `${hideTop} section.panel{display:none !important} ${scenario.show.map(id => `section.panel:has(#${id}){display:block !important}`).join(" ")}`;
    const script = `<script nonce="mediaNonce">
      window.acquireVsCodeApi = () => ({ postMessage() {}, getState() { return undefined; }, setState() {} });
      window.addEventListener('load', () => {
        const messages = ${JSON.stringify(scenario.messages)};
        messages.forEach((m, i) => setTimeout(() => window.postMessage(m, '*'), 50 + i * ${scenario.delayMs}));
        setTimeout(() => document.querySelectorAll('details').forEach(d => { d.open = true; }), 2000);
      });
    </script>`;
    return html
        .replace("<style nonce=\"mediaNonce\">", `<style nonce="mediaNonce">${theme} ${hide}`)
        .replace(/<script nonce="mediaNonce">/, m => script.replace(/<\/script>$/, "") + "</script>" + m);
}

fs.mkdirSync(workDir, { recursive: true });
fs.mkdirSync(mediaDir, { recursive: true });
const chrome = findChrome();
const chromeVersion = execFileSync(chrome, ["--version"], { encoding: "utf8" });
if (!chromeVersion.includes(EXPECTED_CHROME)) {
    throw new Error(`Expected Chrome for Testing ${EXPECTED_CHROME}, found: ${chromeVersion.trim()}`);
}
process.env.TZ = "UTC";
const common = ["--headless=new", "--lang=en-US", "--no-sandbox", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1"];

for (const scenario of scenarios) {
    const page = path.join(workDir, scenario.file.replace(".png", ".html"));
    fs.writeFileSync(page, harness(scenario));
    execFileSync(chrome, [...common, `--window-size=1100,${scenario.height}`, "--virtual-time-budget=6000",
        `--screenshot=${path.join(mediaDir, scenario.file)}`, `file://${page}`], { stdio: "inherit" });
}

const iconSvg = `<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128"><rect width="128" height="128" rx="24" fill="#512bd4"/>
<path fill="none" stroke="#fff" stroke-width="9" stroke-linecap="round" stroke-linejoin="round" d="M14 68h24l13-34 26 62 13-28h24"/></svg>`;
const iconPage = path.join(workDir, "icon.html");
fs.writeFileSync(iconPage, `<!DOCTYPE html><style>html,body{margin:0;background:transparent}</style>${iconSvg}`);
execFileSync(chrome, [...common, "--default-background-color=00000000", "--window-size=128,128",
    `--screenshot=${path.join(mediaDir, "icon.png")}`, `file://${iconPage}`], { stdio: "inherit" });

fs.rmSync(workDir, { recursive: true, force: true });
