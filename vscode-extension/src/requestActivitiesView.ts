import type { JointCapture, JointQuery, JointRequest, RelatedActivity } from "./requestActivities";

// Serialized into the existing webview; all dependencies stay inside this closure.
function createRequestActivitiesView(container: HTMLElement, send: (message: object) => void) {
    let owner: { evidenceId: string; result: JointCapture } | undefined;
    let loaded: { rowId: string; result: JointQuery<RelatedActivity> } | undefined;
    let inFlight: { rowId: string; token: string } | undefined;
    let expandedRowId: string | undefined;
    let retryRowId: string | undefined;
    let querySequence = 0;
    let available = false;
    let unavailableReason = "";
    let status: HTMLElement;
    let activityRows: HTMLElement[] = [];
    const requestRows = new Map<string, { request: JointRequest; row: HTMLTableRowElement; button: HTMLButtonElement; details: HTMLTableRowElement; region: HTMLElement }>();
    const el = (tag: string, text?: string): HTMLElement => {
        const node = document.createElement(tag);
        if (text !== undefined) node.textContent = text;
        return node;
    };
    const warningMessages = (query: JointQuery<unknown>): string[] => {
        const q = query.quality;
        const warnings: string[] = [];
        if (q.completion !== "normal") warnings.push(`Capture is partial: ${q.completion}.`);
        if (q.eventsLost === null || q.eventsLost > 0)
            warnings.push(`Source event loss: ${q.eventsLost === null ? "unknown" : q.eventsLost}.`);
        if (q.parseErrors > 0) warnings.push(`Parse errors: ${q.parseErrors}.`);
        if (q.droppedRequests > 0) warnings.push(`Dropped requests: ${q.droppedRequests}.`);
        if (q.droppedActivities > 0) warnings.push(`Dropped completed Activities: ${q.droppedActivities}.`);
        if (q.invalidIdentities > 0) warnings.push(`Invalid identities: ${q.invalidIdentities}.`);
        if (q.duplicateIdentities > 0) warnings.push(`Duplicate identities: ${q.duplicateIdentities}.`);
        if (q.identityCapacityReached) warnings.push("Identity capacity was reached.");
        if (q.truncatedFields > 0) warnings.push(`Truncated fields: ${q.truncatedFields}.`);
        return warnings;
    };
    const appendWarnings = (query: JointQuery<unknown>, parent: HTMLElement): void => {
        const warnings = warningMessages(query);
        if (!warnings.length) return;
        const list = el("ul");
        list.className = "joint-quality-alert";
        list.setAttribute("role", "alert");
        for (const warning of warnings) list.appendChild(el("li", warning));
        parent.appendChild(list);
    };
    const appendTechnicalDetails = (query: JointQuery<unknown>, parent: HTMLElement, capture: JointCapture): void => {
        const details = document.createElement("details");
        details.className = "joint-technical-details";
        details.appendChild(el("summary", "Capture quality, coverage and provenance"));
        details.appendChild(el("p", `PID ${query.processId} · Process start: ${query.processStartedAt ?? "unknown"} · `
            + `Window: ${query.observationStart ?? "unknown start"} – ${query.observationEnd}`));
        details.appendChild(el("p", `Sources: ${capture.sources.join(", ")} · ${capture.requestedDurationSeconds}s requested · `
            + `Capture ${capture.captureId} · Connection-local, not saved · Expires ${capture.handleExpiresAt}`));
        const q = query.quality;
        details.appendChild(el("p", `Observed requests: ${q.requestsStarted} started / ${q.requestsCompleted} matched completions; `
            + `Activities observed: ${q.activitiesObserved}; retained identities: ${q.retainedIdentities} `
            + `(${q.accountedRetainedBytes} accounted bytes).`));
        details.appendChild(el("p", `Completion: ${q.completion}; source events lost: ${q.eventsLost ?? "unknown"}; `
            + `lifetime: ${q.lifetimeStatus}; parse errors: ${q.parseErrors}; dropped requests: ${q.droppedRequests}; `
            + `dropped Activities: ${q.droppedActivities}; invalid identities: ${q.invalidIdentities}; `
            + `duplicate identities: ${q.duplicateIdentities}; identity cap reached: ${q.identityCapacityReached}; `
            + `truncated fields: ${q.truncatedFields}.`));
        for (const note of query.notes) details.appendChild(el("p", note));
        parent.appendChild(details);
    };
    const relationLabel = (activity: RelatedActivity): string => {
        const relation = activity.relationship === "direct-child" ? "Direct child (direct-child)"
            : activity.relationship === "observed-descendant" ? "Observed descendant (observed-descendant)"
                : "Same trace only (same-trace-only)";
        return `${relation} · ${activity.verified ? "Verified identifier relationship"
            : `Unverified: ${activity.verificationWithheldReason ?? "verification unavailable"}`}`;
    };
    const renderActivityRows = (query: JointQuery<RelatedActivity>, before: HTMLTableRowElement): void => {
        const append = (row: HTMLElement): void => {
            before.parentElement!.insertBefore(row, before);
            activityRows.push(row);
        };
        for (const [index, row] of query.rows.entries()) {
            const item = el("tr");
            item.className = "joint-activity-row";
            item.id = `joint-operation-${expandedRowId}-${index}`;
            const name = el("td");
            name.className = "joint-activity-name";
            const toggle = document.createElement("button");
            toggle.type = "button";
            toggle.className = "joint-request-toggle joint-activity-toggle";
            toggle.appendChild(el("span", "›"));
            toggle.setAttribute("aria-label", `Details for ${row.activity.operation}; local retained evidence only`);
            toggle.setAttribute("aria-expanded", "false");
            const detailRow = el("tr");
            detailRow.className = "joint-activity-details";
            detailRow.id = `joint-activity-${expandedRowId}-${index}`;
            detailRow.hidden = true;
            toggle.setAttribute("aria-controls", detailRow.id);
            toggle.addEventListener("click", () => {
                detailRow.hidden = !detailRow.hidden;
                toggle.setAttribute("aria-expanded", String(!detailRow.hidden));
            });
            name.appendChild(toggle);
            name.appendChild(el("span", row.activity.operation));
            if (row.relationship === "same-trace-only") {
                const weak = el("span", "Weak link");
                weak.className = "joint-link-marker";
                name.appendChild(weak);
            }
            if (!row.verified) {
                const unverified = el("span", "Unverified");
                unverified.className = "joint-link-marker";
                unverified.setAttribute("title", row.verificationWithheldReason ?? "Verification unavailable");
                name.appendChild(unverified);
            }
            item.appendChild(name);
            const duration = el("td", row.activity.durationMs === null ? "Unknown" : String(row.activity.durationMs));
            duration.setAttribute("aria-label", row.activity.durationMs === null
                ? "Finished duration unavailable" : `Finished duration: ${row.activity.durationMs} ms`);
            item.appendChild(duration);
            append(item);
            const identity = el("td");
            identity.setAttribute("colspan", "2");
            identity.appendChild(el("p", relationLabel(row)));
            identity.appendChild(el("p", `Source: ${row.activity.source}`));
            identity.appendChild(el("p", `Started: ${row.activity.startedAt} · Stopped: ${row.activity.stoppedAt}`));
            identity.appendChild(el("p", `Trace ID: ${row.activity.traceId}`));
            identity.appendChild(el("p", `Span ID: ${row.activity.spanId} · Parent span ID: ${row.activity.parentSpanId ?? "unknown"}`));
            detailRow.appendChild(identity);
            append(detailRow);
        }
        requestRows.get(expandedRowId!)?.button.setAttribute("aria-controls",
            [before.id, ...activityRows.map(row => row.id)].join(" "));
    };
    const setDisclosure = (entry: { request: JointRequest; button: HTMLButtonElement; details: HTMLTableRowElement },
        expanded: boolean): void => {
        entry.button.setAttribute("aria-expanded", String(expanded));
        if (!expanded) entry.button.setAttribute("aria-controls", entry.details.id);
        entry.button.setAttribute("aria-label", expanded
            ? `Collapse completed Activities for ${entry.request.method} ${entry.request.path}, request row ${entry.request.rowId}`
            : `Show completed Activities for ${entry.request.method} ${entry.request.path}, request row ${entry.request.rowId}. `
                + "First expansion queries the retained CLI capture; it does not start a capture or read the process.");
        entry.details.hidden = !expanded;
    };
    const renderDetails = (rowId: string): void => {
        const entry = requestRows.get(rowId);
        if (!entry || !owner || expandedRowId !== rowId) return;
        const { region, request } = entry;
        for (const row of activityRows) row.remove();
        activityRows = [];
        region.replaceChildren();
        region.setAttribute("aria-busy", String(inFlight?.rowId === rowId));
        region.setAttribute("role", "region");
        const appendRequestIdentity = (): void => {
            const identity = document.createElement("details");
            identity.appendChild(el("summary", "Request identifiers and timing"));
            identity.appendChild(el("p", `${request.method} ${request.path} · Request row ${request.rowId}`));
            identity.appendChild(el("p", `Elapsed at window close: ${request.elapsedMs} ms · Started: ${request.startedAt} · `
                + `Trace ID: ${request.traceId ?? "unknown"} · Span ID: ${request.spanId ?? "unknown"}`));
            if (loaded?.rowId === rowId) identity.appendChild(el("p",
                `${loaded.result.rows.length} shown / ${loaded.result.totalRows} associated retained Activities; ${loaded.result.omittedRows} omitted.`));
            region.appendChild(identity);
        };

        if (loaded?.rowId === rowId) {
            const result = loaded.result;
            appendWarnings(result, region);
            if (result.omittedRows > 0) {
                const omission = el("p", `${result.rows.length} of ${result.totalRows} associated Activities shown; ${result.omittedRows} omitted.`);
                omission.className = "joint-quality-alert";
                omission.setAttribute("role", "alert");
                region.appendChild(omission);
            }
            if (result.rows.length) renderActivityRows(result, entry.details);
            else region.appendChild(el("p", "No retained completed Activities matched this request in the observation window. Open operations are not retained. Instrumentation, enabled source coverage and loss may be unknown; this does not mean no work occurred."));
            appendRequestIdentity();
            appendTechnicalDetails(result, region, owner.result);
            if (!available) {
                const stale = el("p", `Retained display only; no further CLI query is available. ${unavailableReason}`);
                stale.className = "joint-stale";
                stale.setAttribute("role", "status");
                region.appendChild(stale);
            }
            return;
        }

        const pendingSameRow = inFlight?.rowId === rowId;
        const waitingForOtherRow = inFlight !== undefined && !pendingSameRow;
        const message = pendingSameRow ? "Loading completed Activities from the retained CLI capture. No new capture or process read is started."
            : waitingForOtherRow ? "A previous request lookup is still finishing. This row will not be queried automatically."
                : !available ? `Retained capture is unavailable for new queries. ${unavailableReason}`
                    : retryRowId === rowId ? "The earlier response was discarded because another row became active. Retry this exact row when ready."
                        : "Expand to query completed Activities already retained by the owning CLI connection.";
        const state = el("p", message);
        state.setAttribute("role", "status");
        state.setAttribute("aria-live", "polite");
        region.appendChild(state);
        if (retryRowId === rowId || (!available && loaded?.rowId !== rowId)) {
            const retry = document.createElement("button");
            retry.type = "button";
            retry.textContent = "Retry related Activities lookup";
            retry.disabled = !available || inFlight !== undefined;
            retry.addEventListener("click", () => {
                requestRows.get(rowId)?.button.focus();
                beginQuery(rowId);
            });
            region.appendChild(retry);
        }
        appendRequestIdentity();
    };
    const renderExpanded = (): void => {
        if (!expandedRowId) {
            for (const row of activityRows) row.remove();
            activityRows = [];
        }
        for (const [rowId, entry] of requestRows) {
            const expanded = rowId === expandedRowId;
            setDisclosure(entry, expanded);
            if (expanded) renderDetails(rowId);
        }
    };
    const beginQuery = (rowId: string): void => {
        if (!owner || !available || inFlight || loaded?.rowId === rowId) return;
        const token = `${owner.evidenceId}:${++querySequence}`;
        retryRowId = undefined;
        inFlight = { rowId, token };
        renderDetails(rowId);
        send({ type: "queryRequestActivities", evidenceId: owner.evidenceId, requestRowId: rowId, queryToken: token });
    };
    const toggleRow = (rowId: string): void => {
        if (expandedRowId === rowId) {
            expandedRowId = undefined;
            renderExpanded();
            return;
        }
        expandedRowId = rowId;
        if (loaded?.rowId !== rowId) loaded = undefined;
        retryRowId = undefined;
        renderExpanded();
        if (loaded?.rowId === rowId) return;
        if (inFlight) {
            if (inFlight.rowId !== rowId) retryRowId = rowId;
            renderDetails(rowId);
            return;
        }
        beginQuery(rowId);
    };
    const reset = (): void => {
        owner = undefined;
        loaded = undefined;
        inFlight = undefined;
        expandedRowId = undefined;
        retryRowId = undefined;
        available = false;
        requestRows.clear();
        activityRows = [];
        container.replaceChildren();
    };
    return {
        capture(evidenceId: string, result: JointCapture): void {
            reset();
            owner = { evidenceId, result };
            available = true;
            status = el("p");
            status.setAttribute("role", "status");
            container.appendChild(status);
            const main = el("section");
            main.setAttribute("aria-label", "Requests and completed Activities evidence");
            container.appendChild(main);
            const q = result.requests;
            main.appendChild(el("p", `${q.rows.length} in-flight requests · ${q.retainedActivities} completed Activities · ${result.requestedDurationSeconds} s requested`));
            appendWarnings(q, main);
            const filterLabel = el("label", "Show requests ");
            filterLabel.className = "diagnostic-request-filter";
            const filter = document.createElement("select");
            filter.setAttribute("aria-label", "Filter retained in-flight requests");
            const longCount = q.rows.filter(row => row.elapsedMs >= 1000).length;
            for (const [value, text] of [["all", `All in-flight (${q.rows.length} shown)`],
                ["long", `Long-running ≥ 1,000 ms (${longCount} of ${q.rows.length} shown)`]]) {
                const option = document.createElement("option");
                option.value = value;
                option.textContent = text;
                filter.appendChild(option);
            }
            filter.value = "all";
            filterLabel.appendChild(filter);
            main.appendChild(filterLabel);

            const wrap = el("div");
            wrap.className = "query-view-table-wrap joint-request-table-wrap";
            const table = el("table");
            table.className = "query-view-table joint-request-table";
            table.appendChild(el("caption", `Requests pending at window close (${q.rows.length} of ${q.totalRows} retained shown)`));
            const columns = el("colgroup");
            const operationColumn = el("col");
            operationColumn.className = "joint-operation-column";
            columns.appendChild(operationColumn);
            columns.appendChild(el("col"));
            table.appendChild(columns);
            const head = el("thead");
            const header = el("tr");
            for (const column of ["Request / associated operation", "Time (ms)"]) {
                const th = el("th", column);
                th.setAttribute("scope", "col");
                header.appendChild(th);
            }
            head.appendChild(header);
            table.appendChild(head);
            const body = el("tbody");
            q.rows.forEach((request, index) => {
                const row = el("tr") as HTMLTableRowElement;
                const requestCell = el("td");
                requestCell.className = "joint-request-cell";
                const toggle = document.createElement("button");
                toggle.type = "button";
                toggle.className = "joint-request-toggle";
                toggle.setAttribute("aria-expanded", "false");
                toggle.setAttribute("aria-controls", `joint-request-details-${index}`);
                toggle.appendChild(el("span", "›"));
                toggle.addEventListener("click", () => toggleRow(request.rowId));
                requestCell.appendChild(toggle);
                const identity = el("span", `${request.method} ${request.path}`);
                identity.className = "joint-request-identity";
                requestCell.appendChild(identity);
                const marker = el("span", "In flight");
                marker.className = "joint-inflight-marker";
                requestCell.appendChild(marker);
                row.appendChild(requestCell);
                row.appendChild(el("td", String(request.elapsedMs)));
                body.appendChild(row);

                const details = el("tr") as HTMLTableRowElement;
                details.id = `joint-request-details-${index}`;
                details.className = "joint-request-details-row";
                details.hidden = true;
                const cell = el("td");
                cell.setAttribute("colspan", "2");
                const region = el("section");
                region.setAttribute("aria-label", `Completed Activity details for ${request.method} ${request.path}, request row ${request.rowId}`);
                cell.appendChild(region);
                details.appendChild(cell);
                body.appendChild(details);
                setDisclosure({ request, button: toggle, details }, false);
                requestRows.set(request.rowId, { request, row, button: toggle, details, region });
            });
            table.appendChild(body);
            wrap.appendChild(table);
            main.appendChild(wrap);
            if (!q.rows.length) main.appendChild(el("p", "No identifiable request was retained pending at window close. This is not completed-request history or proof of no requests."));
            filter.addEventListener("change", () => {
                q.rows.forEach(request => {
                    const entry = requestRows.get(request.rowId);
                    if (!entry) return;
                    entry.row.hidden = filter.value === "long" && request.elapsedMs < 1000;
                    if (entry.row.hidden && expandedRowId === request.rowId) {
                        expandedRowId = undefined;
                        renderExpanded();
                    }
                });
            });
            main.appendChild(el("p", "Long-running is a local UI threshold (≥ 1,000 ms), not a collector classification. Filters apply only to returned rows."));
            main.appendChild(el("p", "In flight: request time is elapsed at window close, not a finished duration. Associated Activity times are finished durations. Indentation groups retained associations, not a call tree; Weak link means same trace only, not proven parentage. Expand an Activity locally for its relationship basis and any withheld-verification reason."));
            main.appendChild(el("p", "Only first-accepted requests still pending at close and completed Activity Stop records are retained. There is no oldest-request guarantee, open-operation inventory, full trace or historical recovery. Expand a row to query the retained CLI capture; no new capture or process read starts. New evidence requires an explicit EventPipe reproduction with exact ActivitySource names."));
            main.appendChild(el("p", "Relationship basis and verification are separate. Finished operation durations are observed wall-clock spans, not exclusive delay, causality or a critical path. Do not sum concurrent durations. Instrumentation, enabled source coverage and loss may be unknown."));
            appendTechnicalDetails(q, main, result);
        },
        reset,
        unavailable(evidenceId: string, message: string): void {
            if (owner?.evidenceId !== evidenceId) return;
            available = false;
            unavailableReason = message;
            if (inFlight) inFlight = undefined;
            status.textContent = message;
            if (expandedRowId) renderDetails(expandedRowId);
        },
        queryStarted(evidenceId: string, rowId: string, token: string): void {
            if (owner?.evidenceId === evidenceId && inFlight?.rowId === rowId && inFlight.token === token)
                renderDetails(rowId);
        },
        receive(evidenceId: string, rowId: string, result: JointQuery<RelatedActivity>, token: string): void {
            if (owner?.evidenceId !== evidenceId || !inFlight || inFlight.rowId !== rowId
                || inFlight.token !== token) return;
            inFlight = undefined;
            if (expandedRowId === rowId) {
                loaded = { rowId, result };
                retryRowId = undefined;
                renderDetails(rowId);
            } else if (expandedRowId) {
                retryRowId = expandedRowId;
                renderDetails(expandedRowId);
            }
        },
        error(evidenceId: string, rowId: string, message: string, token: string): void {
            if (owner?.evidenceId !== evidenceId || !inFlight || inFlight.rowId !== rowId
                || inFlight.token !== token) return;
            inFlight = undefined;
            if (expandedRowId === rowId) {
                retryRowId = rowId;
                const error = el("p", `${message} You can retry this retained-evidence lookup.`);
                error.className = "query-view-error";
                error.setAttribute("role", "alert");
                const entry = requestRows.get(rowId);
                if (entry) {
                    entry.region.replaceChildren(error);
                    entry.region.setAttribute("aria-busy", "false");
                    const retry = document.createElement("button");
                    retry.type = "button";
                    retry.textContent = "Retry related Activities lookup";
                    retry.disabled = !available;
                    retry.addEventListener("click", () => {
                        requestRows.get(rowId)?.button.focus();
                        beginQuery(rowId);
                    });
                    entry.region.appendChild(retry);
                }
            } else if (expandedRowId) {
                retryRowId = expandedRowId;
                renderDetails(expandedRowId);
            }
        },
    };
}

export const REQUEST_ACTIVITIES_SCRIPT = createRequestActivitiesView.toString();
