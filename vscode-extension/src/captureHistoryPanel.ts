import { captureQueryArgs, parseEnvelope, runHistoryCli, type CaptureSummary } from "./processHistory";
import { QUERY_VIEW_RENDERER_SCRIPT, QUERY_VIEW_STYLES } from "./queryViewRenderer";
import { RESULT_EXPORT_BUTTONS, RESULT_EXPORT_SCRIPT } from "./resultExport";
import { COMPARISON_RENDERER_SCRIPT } from "./baselineCompare";
import { ACTION_AVAILABILITY_SCRIPT, ACTION_AVAILABILITY_STYLES } from "./actionAvailability";
import { WEBVIEW_FIELD_STYLES } from "./webviewFields";

const SHARED_VIEWS = ["roots-by-kind", "finalizer-queue", "fragmentation", "gchandles", "async", "timers", "alc",
    "deadlocks", "unique-stacks", "wait-chains", "threadpool"];
const GC_VIEWS = ["summary", "events", "pauseHistogram", "timeline", "longestPauses", "byGeneration", "heap-stats"];

export function viewChoices(detail: CaptureSummary, artifactId: string): Array<{ view: string; renderable: boolean }> {
    const artifact = detail.artifacts.find(a => a.artifactId === artifactId);
    if (!artifact) return [];
    return artifact.supportedViews.filter(v => v !== "records").map(view => ({
        view, renderable: SHARED_VIEWS.includes(view) || view === "top-types" || view === "threads-summary"
            || artifact.kind === "gc-events" && GC_VIEWS.includes(view),
    }));
}

export function defaultView(detail: CaptureSummary, artifactId: string): string | undefined {
    return viewChoices(detail, artifactId).find(v => v.renderable)?.view;
}
export type HistoryQueryResult =
    | { ok: true; view: string; summary: string; data: Record<string, unknown> }
    | { ok: false; view: string; message: string };

export async function queryHistoryView(
    cli: string, detail: CaptureSummary, artifactId: string, view: string,
    run: (cli: string, args: string[]) => Promise<string> = runHistoryCli,
): Promise<HistoryQueryResult> {
    try {
        if (!viewChoices(detail, artifactId).some(v => v.view === view && v.renderable))
            throw new Error("This advertised view is not displayable here; use the CLI for its parameters and output.");
        const envelope = parseEnvelope(await run(cli, captureQueryArgs(detail, artifactId, view)));
        const data = envelope.data;
        if (!data || typeof data !== "object" || Array.isArray(data)) throw new Error("The CLI returned no structured query data.");
        return { ok: true, view, summary: typeof envelope.summary === "string" ? envelope.summary : "",
            data: data as Record<string, unknown> };
    } catch (error) {
        return { ok: false, view, message: error instanceof Error ? error.message : String(error) };
    }
}

// Injected with Function.toString(); shared-renderer helpers are not in this function's scope.
function renderSavedPrimary(containerId: string, view: string, data: Record<string, unknown>): void {
    const root = document.getElementById(containerId);
    if (!root) return;
    root.replaceChildren();
    const rec = (v: unknown): Record<string, unknown> => v && typeof v === "object" && !Array.isArray(v) ? v as Record<string, unknown> : {};
    const text = (v: unknown): string => v == null ? "Unavailable"
        : typeof v === "object" ? "See detailed CLI view" : String(v);
    const el = (tag: string, value?: unknown): HTMLElement => {
        const n = document.createElement(tag);
        if (value !== undefined) n.textContent = text(value);
        return n;
    };
    const message = (value: unknown): void => { root.appendChild(el("p", value)); };
    const table = (title: string, columns: Array<[string, string]>, rows: unknown): void => {
        root.appendChild(el("h4", title));
        if (!Array.isArray(rows)) { message("This table is unavailable in the captured evidence."); return; }
        if (!rows.length) { message("No matching rows were retained; absence does not establish a healthy process."); return; }
        const t = el("table"), header = el("tr");
        for (const [label] of columns) header.appendChild(el("th", label));
        t.appendChild(header);
        for (const row of rows.slice(0, 100)) {
            const tr = el("tr");
            for (const [, key] of columns) tr.appendChild(el("td", text(rec(row)[key])));
            t.appendChild(tr);
        }
        root.appendChild(t);
        if (rows.length > 100) message(`Showing 100 of ${rows.length} retained rows; ${rows.length - 100} omitted by the panel.`);
    };
    const scalarTable = (title: string, value: unknown): void => {
        const rows = Object.entries(rec(value)).filter(([, v]) => v === null || typeof v !== "object")
            .map(([key, val]) => ({ key, val }));
        table(title, [["Measurement", "key"], ["Value", "val"]], rows);
    };
    const quality = (value: unknown): void => {
        const q = rec(value);
        if (Array.isArray(q.limitations)) table("Evidence limitations", [["Category", "category"], ["Scope", "scope"], ["Detail", "detail"]], q.limitations);
        else if (q.limitations) scalarTable("Evidence limitations", q.limitations);
        if (q.conclusions) scalarTable("Conclusion support", q.conclusions);
        scalarTable("Evidence status", q);
    };
    if (view === "top-types") {
        message(`Heap types ranked by ${text(data.rankBy)}. These are saved observations, not the current heap.`);
        table("Top types", [["Type", "typeFullName"], ["Instances", "instanceCount"], ["Bytes", "totalBytes"], ["Heap %", "totalBytesPercent"]], data.topTypes);
        if (data.quality) quality(data.quality);
    } else if (view === "threads-summary") {
        message(`Total threads: ${text(data.totalThreads)}; omitted: ${text(data.omittedThreads)}; first retained page only.`);
        table("Threads", [["Thread", "managedThreadId"], ["OS thread", "osThreadId"], ["State", "state"],
            ["Alive", "isAlive"], ["Likely blocked", "isLikelyBlocked"], ["Top frame", "topFrameMethod"]], data.threads);
    } else {
        const p = rec(data.payload);
        message("GC collection elapsed is not suspension time. Missing suspension evidence is unavailable, not zero pause.");
        scalarTable("GC measurements", p);
        if (p.evidence) quality(p.evidence);
        if (view === "summary") {
            table("Generations", [["Generation", "generation"], ["Count", "count"]], rec(p.data).generations);
        } else {
            const rows = view === "heap-stats" ? p.samples : p.data;
            const first = Array.isArray(rows) ? rec(rows[0]) : {};
            const columns = Object.keys(first).filter(key => first[key] == null || typeof first[key] !== "object").map(key => [key, key] as [string, string]);
            table(view, columns, rows);
        }
    }
    if (Array.isArray(data.notes)) for (const note of data.notes.slice(0, 50)) message(note);
}
export const SAVED_PRIMARY_RENDERER_SCRIPT = renderSavedPrimary.toString();

const escapeHtml = (s: string): string => s.replace(/[&<>"']/g, c => `&#${c.charCodeAt(0)};`);
export function renderHistoryHtml(nonce: string, cspSource: string, detail: CaptureSummary): string {
    const options = detail.artifacts.map(a => `<option value="${escapeHtml(a.artifactId)}">${escapeHtml(a.name || a.kind)}</option>`).join("");
    const choices = JSON.stringify(Object.fromEntries(detail.artifacts.map(a => [a.artifactId, viewChoices(detail, a.artifactId)]))).replace(/</g, "\\u003c");
    return `<!DOCTYPE html><html><head><meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${cspSource} 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
<style nonce="${nonce}">body{font-family:var(--vscode-font-family);padding:1rem;color:var(--vscode-foreground)}
table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:.25rem;border-bottom:1px solid var(--vscode-panel-border)}${QUERY_VIEW_STYLES}${WEBVIEW_FIELD_STYLES}${ACTION_AVAILABILITY_STYLES}</style></head><body>
<h2>${escapeHtml(detail.name)}</h2><p>Saved capture ${escapeHtml(detail.captureId)} - ${escapeHtml(detail.createdUtc)}. Offline, read-only evidence; the original process is not contacted.</p>
<label>Artifact <select id="artifact">${options}</select></label><label>View <select id="view"></select></label>
${RESULT_EXPORT_BUTTONS}<button id="compareBaseline">Compare with saved baseline</button>
<p id="status" role="status"></p><div id="views" class="query-views"></div>
<script nonce="${nonce}">
const vscode = acquireVsCodeApi(), choices = ${choices};
${ACTION_AVAILABILITY_SCRIPT}
${RESULT_EXPORT_SCRIPT}
${COMPARISON_RENDERER_SCRIPT}
document.getElementById("compareBaseline").addEventListener("click", () =>
  vscode.postMessage({ type: "compareBaseline", artifactId: artifact.value, requestId: current?.requestId }));
${QUERY_VIEW_RENDERER_SCRIPT}
${SAVED_PRIMARY_RENDERER_SCRIPT}
const artifact = document.getElementById("artifact"), view = document.getElementById("view"), status = document.getElementById("status");
let sequence = 0, current, loaded = false, comparing = false;
function updateActions() {
  for (const id of ["exportJson", "exportCsv"])
    setActionAvailable(document.getElementById(id), loaded, "Select a successfully loaded saved view first.");
  setActionAvailable(document.getElementById("compareBaseline"), loaded && !comparing,
    comparing ? "Wait for the current comparison to finish." : "Select a successfully loaded saved view first.");
}
function ask() {
  current = undefined;
  loaded = false;
  comparing = false;
  updateActions();
  vscode.postMessage({ type: "selectionChanged" });
  document.getElementById("views").replaceChildren();
  if (!view.value || view.selectedOptions[0]?.disabled) { status.textContent = "No displayable offline view. Use the CLI for the remaining advertised views."; return; }
  current = { requestId: ++sequence, artifactId: artifact.value, view: view.value };
  artifact.disabled = view.disabled = true;
  status.textContent = "Querying saved " + view.value + "...";
  vscode.postMessage({ type: "query", ...current });
}
function fill() {
  view.replaceChildren();
  for (const c of choices[artifact.value] || []) {
    const o = document.createElement("option"); o.value = c.view; o.disabled = !c.renderable;
    o.textContent = c.view + (c.renderable ? "" : " (use CLI)"); view.appendChild(o);
  }
  const first = Array.from(view.options).find(o => !o.disabled);
  if (first) view.value = first.value;
  ask();
}
artifact.addEventListener("change", fill); view.addEventListener("change", ask);
window.addEventListener("message", e => {
  const m = e.data;
  if (m?.type === "comparisonState") {
    if (current && m.requestId === current.requestId && m.artifactId === artifact.value) {
      comparing = m.state === "running";
      updateActions();
    }
    return;
  }
  if (m?.type === "comparison") {
    if (current && m.requestId === current.requestId && m.artifactId === artifact.value) {
      renderComparison("views", m.result); status.textContent = "Saved baseline comparison.";
    }
    return;
  }
  if (!m || m.type !== "result" || !current || m.requestId !== current.requestId || m.view !== current.view) return;
  artifact.disabled = view.disabled = false;
  status.textContent = m.ok ? m.summary : m.message;
  loaded = m.ok === true;
  updateActions();
  if (!m.ok) { renderQueryView("views", m.view, m.message, true); return; }
  if (["top-types","threads-summary","summary","events","pauseHistogram","timeline","longestPauses","byGeneration","heap-stats"].includes(m.view))
    renderSavedPrimary("views", m.view, m.data);
  else renderQueryView("views", m.view, m.data, false);
});
fill();
</script></body></html>`;
}
