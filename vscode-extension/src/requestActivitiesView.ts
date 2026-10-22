import type { JointCapture, JointQuery, JointRequest, RelatedActivity } from "./requestActivities";

// Serialized into the existing webview; all dependencies stay inside this closure.
function createRequestActivitiesView(container: HTMLElement, send: (message: object) => void) {
    let owner: { evidenceId: string; result: JointCapture } | undefined;
    let selected: JointRequest | undefined;
    let loaded: { rowId: string; result: JointQuery<RelatedActivity> } | undefined;
    let pendingRow: string | undefined;
    let available = false;
    let unavailableReason = "";
    let status: HTMLElement;
    let selection: HTMLElement;
    let related: HTMLElement;
    let main: HTMLElement;
    let load: HTMLButtonElement;
    const el = (tag: string, text?: string): HTMLElement => {
        const node = document.createElement(tag);
        if (text !== undefined) node.textContent = text;
        return node;
    };
    const table = (caption: string, columns: string[], rows: unknown[][]): HTMLElement => {
        const wrap = el("div");
        wrap.className = "query-view-table-wrap";
        const node = el("table");
        node.className = "query-view-table " + (columns.length === 9 ? "joint-activity-table" : "joint-request-table");
        node.appendChild(el("caption", caption));
        const head = el("thead");
        const tr = el("tr");
        for (const column of columns) {
            const th = el("th", column);
            th.setAttribute("scope", "col");
            tr.appendChild(th);
        }
        head.appendChild(tr);
        node.appendChild(head);
        const body = el("tbody");
        for (const values of rows) {
            const row = el("tr");
            for (const value of values) row.appendChild(el("td", value == null ? "Unknown" : String(value)));
            body.appendChild(row);
        }
        node.appendChild(body);
        wrap.appendChild(node);
        return wrap;
    };
    const quality = (query: JointQuery<unknown>, parent: HTMLElement): void => {
        const q = query.quality;
        parent.appendChild(el("p", `PID ${query.processId} · Process start: ${query.processStartedAt ?? "unknown"} · `
            + `Window: ${query.observationStart ?? "unknown start"} – ${query.observationEnd}`));
        const text = `Quality: ${q.completion}; source events lost: ${q.eventsLost ?? "unknown"}; lifetime: ${q.lifetimeStatus}. `
            + `Parse errors: ${q.parseErrors}; dropped requests: ${q.droppedRequests}; dropped Activities: ${q.droppedActivities}; `
            + `invalid identities: ${q.invalidIdentities}; duplicate identities: ${q.duplicateIdentities}; `
            + `identity cap reached: ${q.identityCapacityReached}; truncated fields: ${q.truncatedFields}.`;
        const note = el("p", text);
        note.className = "diagnostic-capture-notes";
        parent.appendChild(note);
    };
    const showSelection = (): void => {
        selection.replaceChildren();
        selection.appendChild(el("p", selected
            ? `Selected: ${selected.method} ${selected.path} · Row ${selected.rowId} · Trace ${selected.traceId ?? "unknown"} · Span ${selected.spanId ?? "unknown"}`
            : "Select an exact retained request to inspect completed Activities."));
        load = document.createElement("button");
        load.textContent = loaded?.rowId === selected?.rowId && selected
            ? "Show loaded completed Activities" : "Load related completed Activities";
        load.disabled = !selected || !!pendingRow || (!available && loaded?.rowId !== selected.rowId);
        load.addEventListener("click", () => {
            if (!owner || !selected || pendingRow) return;
            if (loaded?.rowId === selected.rowId) { showRelated(); return; }
            if (!available) return;
            // Exactly one selected-request response, never a cache per request.
            loaded = undefined;
            related.replaceChildren();
            pendingRow = selected.rowId;
            showSelection();
            status.textContent = "Loading immutable completed Activity evidence from the owning CLI connection…";
            send({ type: "queryRequestActivities", evidenceId: owner.evidenceId, requestRowId: selected.rowId });
        });
        selection.appendChild(load);
    };
    const showRelated = (): void => {
        if (!owner || !selected || loaded?.rowId !== selected.rowId) return;
        main.hidden = true;
        related.hidden = false;
        related.replaceChildren();
        const back = document.createElement("button");
        back.textContent = "Back to selected request";
        back.addEventListener("click", () => {
            main.hidden = false;
            related.hidden = true;
            load.focus();
        });
        related.appendChild(back);
        related.appendChild(el("h4", `Completed Activities for ${selected.method} ${selected.path}`));
        related.appendChild(el("p", `Exact request row ${selected.rowId} · Capture ${owner.result.captureId} · Sources: ${owner.result.sources.join(", ")}`));
        quality(loaded.result, related);
        related.appendChild(el("p", "Relationship basis and verification are separate. Finished operation durations are observed wall-clock spans, "
            + "not exclusive delay, causality or a critical path. Do not sum concurrent durations."));
        related.appendChild(el("p",
            `${loaded.result.rows.length} shown / ${loaded.result.totalRows} associated retained Activities; ${loaded.result.omittedRows} omitted.`));
        related.appendChild(table(
            "Completed Activity evidence",
            ["Source", "Operation", "Relationship basis", "Verification", "Finished duration (ms)", "Start", "Stop", "Span", "Parent span"],
            loaded.result.rows.map(row => [row.activity.source, row.activity.operation, row.relationship,
                row.verified ? "Verified identifier relationship" : `Unverified: ${row.verificationWithheldReason}`,
                row.activity.durationMs, row.activity.startedAt, row.activity.stoppedAt, row.activity.spanId, row.activity.parentSpanId])));
        if (!loaded.result.rows.length) related.appendChild(el("p",
            "No retained completed Activities matched this request's trace in this window. Open operations are not retained. "
            + "Instrumentation, enabled source coverage and loss may be unknown; this does not mean no work occurred."));
        if (!available) related.appendChild(el("p", unavailableReason));
        back.focus();
    };
    const reset = (): void => {
        owner = undefined; selected = undefined; loaded = undefined; pendingRow = undefined; available = false;
        container.replaceChildren();
    };
    const capture = (evidenceId: string, result: JointCapture): void => {
        reset();
        owner = { evidenceId, result };
        available = true;
        status = el("p");
        status.setAttribute("role", "status");
        container.appendChild(status);
        main = el("section");
        related = el("section");
        related.hidden = true;
        container.appendChild(main);
        container.appendChild(related);
        main.appendChild(el("h4", "Requests + completed Activities"));
        const q = result.requests;
        main.appendChild(el("p", `Window: ${q.quality.requestsStarted} observed starts · ${q.quality.requestsCompleted} matched tracked completions. `
            + `At close: ${q.retainedRequests} retained in-flight requests · ${q.retainedActivities} retained completed Activities.`));
        main.appendChild(el("p", `Sources: ${result.sources.join(", ")} · ${result.requestedDurationSeconds}s requested · `
            + `Capture ${result.captureId} · Connection-local, not saved · Expires ${result.handleExpiresAt}`));
        quality(q, main);
        const label = el("label", "Show requests ");
        const filter = document.createElement("select");
        filter.setAttribute("aria-label", "Filter retained in-flight requests");
        const longCount = q.rows.filter(row => row.elapsedMs >= 1000).length;
        for (const [value, text] of [["all", `All in-flight (${q.rows.length} shown)`],
            ["long", `Long-running ≥ 1,000 ms (${longCount} of ${q.rows.length} shown)`]]) {
            const option = document.createElement("option");
            option.value = value; option.textContent = text; filter.appendChild(option);
        }
        filter.value = "all";
        label.appendChild(filter); main.appendChild(label);
        main.appendChild(el("p", "Long-running is a local UI threshold (≥ 1,000 ms), not a collector classification. Filters apply only to returned rows."));
        const wrapper = table(`Requests pending at window close (${q.rows.length} of ${q.totalRows} retained shown)`,
            ["Select", "Method", "Path", "Elapsed (ms)", "Started (UTC)", "Trace ID", "Span ID"],
            q.rows.map(row => ["", row.method, row.path, row.elapsedMs, row.startedAt, row.traceId, row.spanId]));
        const bodyRows = wrapper.querySelectorAll("tbody tr");
        q.rows.forEach((row, index) => {
            const radio = document.createElement("input");
            radio.type = "radio";
            radio.name = "joint-request";
            radio.setAttribute("aria-label", `Select ${row.method} ${row.path}, request row ${row.rowId}`);
            bodyRows[index].children[0].replaceChildren(radio);
            radio.addEventListener("change", () => {
                selected = row;
                showSelection();
            });
        });
        filter.addEventListener("change", () => q.rows.forEach((row, index) => {
            (bodyRows[index] as HTMLElement).hidden = filter.value === "long" && row.elapsedMs < 1000;
        }));
        main.appendChild(wrapper);
        if (!q.rows.length) main.appendChild(el("p", "No identifiable request was retained pending at window close. This is not completed-request history or proof of no requests."));
        selection = el("section");
        selection.setAttribute("aria-label", "Selected request actions");
        main.appendChild(selection);
        showSelection();
        const notes = el("ul");
        notes.className = "diagnostic-capture-notes";
        for (const note of q.notes) notes.appendChild(el("li", note));
        main.appendChild(notes);
        main.appendChild(el("p", "Only first-accepted requests still pending at close and Activity Stop records are retained. "
            + "No oldest-request guarantee, open-operation inventory, full trace, historical recovery or automatic new collection. "
            + "Another request load replaces the one loaded response. Back, filters and showing that response are local. "
            + "A separate explicit reproduction is required for new evidence."));
    };
    return {
        capture, reset,
        unavailable(evidenceId: string, message: string): void {
            if (owner?.evidenceId !== evidenceId) return;
            available = false; pendingRow = undefined; unavailableReason = message;
            status.textContent = message;
            showSelection();
            if (!related.hidden) showRelated();
        },
        queryStarted(evidenceId: string, rowId: string): void {
            if (owner?.evidenceId === evidenceId && selected?.rowId === rowId) {
                pendingRow = rowId; showSelection();
            }
        },
        receive(evidenceId: string, rowId: string, result: JointQuery<RelatedActivity>): void {
            if (owner?.evidenceId !== evidenceId || pendingRow !== rowId || !available) return;
            pendingRow = undefined;
            loaded = { rowId, result };
            status.textContent = "One selected-request response loaded; further navigation to this response is local.";
            showSelection();
            if (selected?.rowId === rowId) showRelated();
        },
        error(evidenceId: string, rowId: string, message: string): void {
            if (owner?.evidenceId !== evidenceId || pendingRow !== rowId) return;
            pendingRow = undefined; status.textContent = message; showSelection();
        },
    };
}

export const REQUEST_ACTIVITIES_SCRIPT = createRequestActivitiesView.toString();
