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
const { pathToFileURL } = require("node:url");
const { chromium } = require("playwright-core");

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
const requestedFiles = process.env.MEDIA_SCENARIOS
    ? new Set(process.env.MEDIA_SCENARIOS.split(",").map(value => value.trim()).filter(Boolean))
    : undefined;

function findChrome() {
    if (process.env.CHROME_PATH) return process.env.CHROME_PATH;
    for (const cache of [path.join(os.homedir(), ".cache", "ms-playwright"),
        path.join(os.homedir(), "AppData", "Local", "ms-playwright")]) {
        if (fs.existsSync(cache)) {
            for (const dir of fs.readdirSync(cache).filter(d => d.startsWith("chromium-")).sort().reverse()) {
                for (const relative of [["chrome-linux64", "chrome"], ["chrome-win", "chrome.exe"]]) {
                    const candidate = path.join(cache, dir, ...relative);
                    if (fs.existsSync(candidate)) return candidate;
                }
            }
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

function comparisonCapture(handle, startedAt, totalSamples, backend = "EventPipeSampleProfiler") {
    return {
        handle,
        processId: 4242,
        startedAt,
        durationSeconds: 10,
        totalSamples,
        backend,
        evidenceKind: "StackFrequencyWithHeuristicWaits",
        origin: "live",
        producingTool: "collect_sample",
        expiresAt: "2026-01-15T12:05:00Z",
        unresolvedExclusiveSamples: 4,
        unresolvedExclusivePercent: 4,
        quality: {
            schema: "dotnet-diagnostics/evidence-quality/v1",
            limitations: [{ category: "Sampling", scope: "capture", message: "Sampling does not establish workload equivalence." }],
        },
    };
}

function qualifiedComparison() {
    const delta = (baselineValue, currentValue, deltaAbs, relativeDelta, unit = "%") => ({
        baselineValue, currentValue, unit, deltaAbs, relativeDelta,
        betterDirection: "neutral", outcome: deltaAbs > 0 ? "up" : "down",
        unavailableReason: null, baselineDenominator: 100, currentDenominator: 120,
        deltaUnit: unit === "%" ? "percentage-points" : unit,
        relativeDeltaUnavailableReason: null,
    });
    return {
        handle: "cpu-current", baselineHandle: "cpu-baseline", view: "diff",
        summary: "Objective comparison of retained CPU sample handles.",
        data: {
            schema: "dotnet-diagnostics/cpu-sample-handle-comparison/v1",
            baseline: comparisonCapture("cpu-baseline", "2026-01-15T12:00:00Z", 100),
            current: comparisonCapture("cpu-current", "2026-01-15T12:01:00Z", 120),
            compatibility: { status: "qualified", reasons: [] },
            metrics: [{
                name: "samplePopulation", population: "retained-samples", aggregation: "total", normalization: "none",
                delta: {
                    baselineValue: 100, currentValue: 120, unit: "samples", deltaAbs: 20, relativeDelta: .2,
                    betterDirection: "neutral", outcome: "up", unavailableReason: null,
                    baselineDenominator: 100, currentDenominator: 120, deltaUnit: "samples",
                    relativeDeltaUnavailableReason: null,
                },
            }],
            topMovers: [
                {
                    displayName: "OrdersApi.Services.PricingService.CalculateDiscountAsync(Order, CancellationToken)",
                    baselineExclusiveSamples: 20, currentExclusiveSamples: 36,
                    baselineTotalSamples: 100, currentTotalSamples: 120,
                    exclusiveShare: delta(20, 30, 10, .5), unresolved: false,
                },
                {
                    displayName: "System.Text.Json.Serialization.Metadata.JsonTypeInfo`1.Serialize(Utf8JsonWriter, T&)",
                    baselineExclusiveSamples: null, currentExclusiveSamples: 12,
                    baselineTotalSamples: 100, currentTotalSamples: 120,
                    exclusiveShare: {
                        baselineValue: null, currentValue: 10, unit: "%", deltaAbs: null, relativeDelta: null,
                        betterDirection: "neutral", outcome: "unavailable",
                        unavailableReason: "method-not-observed-in-baseline", baselineDenominator: 100,
                        currentDenominator: 120, deltaUnit: "percentage-points",
                        relativeDeltaUnavailableReason: "baseline-share-unavailable",
                    },
                    unresolved: false,
                },
            ],
            notes: ["Workload equivalence is not established.", "The mover list is bounded to retained rows."],
        },
    };
}

function mismatchComparison() {
    const result = qualifiedComparison();
    result.data.current = comparisonCapture("cpu-current", "2026-01-15T12:01:00Z", 120, "perf");
    result.data.compatibility = {
        status: "incompatible",
        reasons: [{
            code: "backend-mismatch",
            field: "backend",
            message: "Baseline uses EventPipeSampleProfiler while current uses perf; objective deltas are unavailable.",
        }],
    };
    result.data.metrics[0].delta = {
        baselineValue: 100, currentValue: 120, unit: "samples", deltaAbs: null, relativeDelta: null,
        betterDirection: "neutral", outcome: "unavailable", unavailableReason: "backend-mismatch",
        baselineDenominator: 100, currentDenominator: 120, deltaUnit: "samples",
        relativeDeltaUnavailableReason: "backend-mismatch",
    };
    result.data.topMovers = [{
        displayName: "OrdersApi.Controllers.CheckoutController.SubmitAsync()",
        baselineExclusiveSamples: 18, currentExclusiveSamples: null,
        baselineTotalSamples: 100, currentTotalSamples: 120,
        exclusiveShare: {
            baselineValue: 18, currentValue: null, unit: "%", deltaAbs: null, relativeDelta: null,
            betterDirection: "neutral", outcome: "unavailable", unavailableReason: "backend-mismatch",
            baselineDenominator: 100, currentDenominator: 120, deltaUnit: "percentage-points",
            relativeDeltaUnavailableReason: "backend-mismatch",
        },
        unresolved: false,
    }];
    return result;
}

const comparisonScenarios = [
    { file: "cpu-comparison-qualified-540.png", width: 540, height: 2600, result: qualifiedComparison() },
    { file: "cpu-comparison-mismatch-390.png", width: 390, height: 3000, result: mismatchComparison() },
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

function comparisonHarness(scenario) {
    const html = renderHtml(target, "mediaNonce", 120);
    const currentSummary = {
        processId: 4242,
        startedAt: "2026-01-15T12:01:00Z",
        totalSamples: scenario.result.data.current.totalSamples,
        topHotspots: [],
    };
    const messages = [
        { type: "captureStatus", kind: "cpu", state: "starting", evidenceId: "current-evidence" },
        {
            type: "capture",
            kind: "cpu",
            evidenceId: "current-evidence",
            summary: currentSummary,
            comparisonBaseline: {
                evidenceId: "baseline-evidence",
                summary: {
                    processId: 4242,
                    startedAt: "2026-01-15T12:00:00Z",
                    totalSamples: scenario.result.data.baseline.totalSamples,
                    topHotspots: [],
                },
            },
        },
        {
            type: "cpuComparison",
            evidenceId: "current-evidence",
            baselineEvidenceId: "baseline-evidence",
            result: scenario.result,
        },
    ];
    const hide = `
      body > header, body > nav, body > section:not(#resultWorkspace),
      #resultWorkspace > :not(#workspaceCanvas), #workspaceCanvas > :not(#resultCpu),
      #resultCpu > .panel-actions, #captureStatus, #captureHeadline, #cpuQueryStatus,
      #cpuHotspotSummary, #cpuSamplingDisclaimer, .cpu-export-actions { display:none !important; }
      #resultWorkspace, #workspaceCanvas, #resultCpu { display:block !important; }
    `;
    const script = `<script nonce="mediaNonce">
      window.acquireVsCodeApi = () => ({ postMessage() {}, getState() { return undefined; }, setState() {} });
      window.addEventListener('load', () => {
        const messages = ${JSON.stringify(messages)};
        messages.forEach((message, index) => setTimeout(() => window.postMessage(message, '*'), 25 + index * 25));
        setTimeout(() => document.querySelectorAll('#cpuQueryViews details').forEach(detail => { detail.open = true; }), 150);
      });
    </script>`;
    return html
        .replace("<style nonce=\"mediaNonce\">", `<style nonce="mediaNonce">${theme} ${hide}`)
        .replace(/<script nonce="mediaNonce">/, match => script.replace(/<\/script>$/, "") + "</script>" + match);
}

async function screenshot(browser, file, html, viewport, waitMs, omitBackground = false, readySelector) {
    const pagePath = path.join(workDir, file.replace(".png", ".html"));
    fs.writeFileSync(pagePath, html);
    const context = await browser.newContext({
        viewport,
        deviceScaleFactor: 1,
        locale: "en-US",
        timezoneId: "UTC",
        colorScheme: "dark",
    });
    const page = await context.newPage();
    const pageErrors = [];
    page.on("pageerror", error => pageErrors.push(error));
    await page.goto(pathToFileURL(pagePath).href, { waitUntil: "load" });
    await page.waitForTimeout(waitMs);
    if (pageErrors.length) throw pageErrors[0];
    if (readySelector) {
        const ready = page.locator(readySelector);
        await ready.waitFor({ state: "attached", timeout: 5000 });
        if (!(await ready.isVisible())) {
            const ancestry = await ready.evaluate(element => {
                const result = [];
                for (let current = element; current; current = current.parentElement) {
                    result.push({
                        tag: current.tagName,
                        id: current.id,
                        hidden: current.hidden,
                        display: getComputedStyle(current).display,
                    });
                }
                return result;
            });
            throw new Error(`Screenshot target is hidden: ${JSON.stringify(ancestry)}`);
        }
    }
    await page.screenshot({
        path: path.join(mediaDir, file),
        animations: "disabled",
        omitBackground,
    });
    await context.close();
}

async function main() {
    fs.mkdirSync(workDir, { recursive: true });
    fs.mkdirSync(mediaDir, { recursive: true });
    const browser = await chromium.launch({
        executablePath: findChrome(),
        headless: true,
        args: ["--lang=en-US", "--no-sandbox", "--disable-gpu", "--hide-scrollbars"],
    });
    try {
        const chromeVersion = browser.version();
        if (!chromeVersion.includes(EXPECTED_CHROME)) {
            throw new Error(`Expected Chrome for Testing ${EXPECTED_CHROME}, found: ${chromeVersion}`);
        }
        for (const scenario of scenarios.filter(item => !requestedFiles || requestedFiles.has(item.file))) {
            await screenshot(browser, scenario.file, harness(scenario), { width: 1100, height: scenario.height }, 6000);
        }
        for (const scenario of comparisonScenarios.filter(item => !requestedFiles || requestedFiles.has(item.file))) {
            await screenshot(browser, scenario.file, comparisonHarness(scenario),
                { width: scenario.width, height: scenario.height }, 500, false, "#cpuQueryViews h3");
        }
        if (!requestedFiles || requestedFiles.has("icon.png")) {
            const iconSvg = `<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128"><rect width="128" height="128" rx="24" fill="#512bd4"/>
<path fill="none" stroke="#fff" stroke-width="9" stroke-linecap="round" stroke-linejoin="round" d="M14 68h24l13-34 26 62 13-28h24"/></svg>`;
            await screenshot(browser, "icon.png",
                `<!DOCTYPE html><style>html,body{margin:0;background:transparent}</style>${iconSvg}`,
                { width: 128, height: 128 }, 0, true);
        }
    } finally {
        await browser.close();
        fs.rmSync(workDir, { recursive: true, force: true });
    }
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
