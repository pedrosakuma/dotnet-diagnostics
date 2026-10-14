import type { CaptureViewResult } from "./captureKinds";

// Injected with Function.toString(): keep this function self-contained.
function renderCaptureView(containerId: string, result: unknown): void {
    const container = document.getElementById(containerId);
    if (!container) return;
    container.replaceChildren();
    const el = (tag: string, text?: string): HTMLElement => {
        const node = document.createElement(tag);
        if (text !== undefined) node.textContent = text;
        return node;
    };
    const cell = (value: unknown): string => value === null || value === undefined ? "Unavailable"
        : typeof value === "number" && !Number.isInteger(value) ? value.toFixed(2) : String(value);
    const view = result as CaptureViewResult;
    container.appendChild(el("h4", view.title));
    container.appendChild(el("p", view.summary));
    const p = view.provenance;
    container.appendChild(el("p", `Live capture, not saved - pid ${p.processId} - requested ${p.requestedDurationSeconds}s`));
    const table = (title: string, columns: string[], rows: unknown[][]): void => {
        const node = el("table");
        node.appendChild(el("caption", title));
        const head = el("tr");
        for (const column of columns) head.appendChild(el("th", column));
        node.appendChild(head);
        for (const values of rows) {
            const row = el("tr");
            for (const value of values) row.appendChild(el("td", cell(value)));
            node.appendChild(row);
        }
        container.appendChild(node);
    };
    table("Metrics", ["Metric", "Value", "Note"], view.metrics.map(m => [m.label, m.value, m.note ?? ""]));
    for (const t of view.tables) {
        table(`${t.title} (${t.rows.length} of ${t.totalRows} retained rows shown)`, t.columns, t.rows);
        if (t.rows.length === 0) container.appendChild(el("p", t.emptyText));
        if (t.omittedRows > 0) container.appendChild(el("p", `${t.omittedRows} retained row(s) omitted.`));
    }
    const e = view.evidence;
    container.appendChild(el("p", e.source === "not-reported"
        ? "Evidence quality: not reported by this collector." : `Evidence quality (${e.source})`));
    if (e.conclusions) {
        const c = e.conclusions;
        container.appendChild(el("p", `Positive evidence: ${c.retainedPositive}; absence/exhaustive counts: ${c.absenceOrExhaustive}; healthy control: ${c.healthyControl}`));
    }
    for (const l of e.limitations)
        container.appendChild(el("p", `${l.category} (${l.scope}${l.affectedCount != null ? `, ${l.affectedCount}` : ""}): ${l.detail}`));
    const notes = el("ul");
    for (const note of view.notes) notes.appendChild(el("li", note));
    container.appendChild(notes);
}

export const CAPTURE_VIEW_RENDERER_SCRIPT = renderCaptureView.toString();
