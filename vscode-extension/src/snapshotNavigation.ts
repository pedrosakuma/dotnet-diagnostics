declare function updateEvidenceViewSwitcher(container: HTMLElement): void;
declare function selectThreadSnapshotView(view: string): void;

function setThreadWorkspaceView(view: string): void {
    const workspaceView = document.getElementById("workspaceView") as HTMLSelectElement | null;
    if (workspaceView) workspaceView.value = view;
    if (typeof selectThreadSnapshotView === "function") {
        selectThreadSnapshotView(view);
        return;
    }
    const summary = document.getElementById("captureThreadHeadline");
    const table = document.getElementById("captureThreadTable")?.closest("details") as HTMLElement | null;
    const statics = document.querySelector("#resultThreads .statics-form") as HTMLElement | null;
    const queries = document.getElementById("threadQueryViews");
    const isSummary = view === "snapshot";
    if (summary) summary.hidden = !isSummary;
    if (table) table.hidden = !isSummary;
    if (statics) statics.hidden = view !== "thread-statics";
    if (queries) {
        queries.hidden = isSummary;
        const panelId = view === "thread-explorer" ? "threadExplorer" : "threadQueryViews-" + view;
        const switcher = queries.querySelector<HTMLElement>(".evidence-view-switcher");
        const tab = Array.from(switcher?.querySelectorAll<HTMLButtonElement>("button") ?? [])
            .find(button => button.dataset.panel === panelId);
        tab?.click();
    }
}

function navigateThreadTo(id: number, returnPanel?: string): void {
    const explorer = document.getElementById("threadExplorer") as (HTMLElement & {
        threadState?: { selected?: unknown; mode?: string; returnPanel?: string };
    }) | null;
    if (!explorer?.threadState) return;
    explorer.threadState.selected = id;
    explorer.threadState.mode = "threads";
    explorer.threadState.returnPanel = returnPanel;
    updateThreadNavigation("selection", {});
    setThreadWorkspaceView("thread-explorer");
    explorer.querySelector<HTMLElement>(".evidence-selection")?.focus({ preventScroll: true });
}

function navigateHeapDetails(details: boolean): void {
    const workspace = document.getElementById("heapWorkspace") as (HTMLElement & {
        navigateDetails?: (details: boolean) => void;
    }) | null;
    workspace?.navigateDetails?.(details);
}

function navigateThreadFromGroup(id: number): void {
    navigateThreadTo(id, "threadQueryViews-unique-stacks");
}

function renderHeapTypeNavigation(types: Array<Record<string, unknown>>): void {
    const table = document.getElementById("captureHeapTable") as HTMLTableElement | null;
    if (!table) return;
    const workspace = document.getElementById("heapWorkspace") as (HTMLElement & {
        navigateDetails?: (details: boolean) => void;
    }) | null;
    const pane = document.getElementById("heapDetailPane");
    const back = document.getElementById("backToHeapTypes");
    const open = document.getElementById("openHeapDetails");
    let selectedButton: HTMLButtonElement | undefined;
    let overviewScroll = 0;
    const showDetails = (): void => {
        if (!workspace || !pane) return;
        if (workspace.dataset.focused !== "true") overviewScroll = window.scrollY;
        workspace.dataset.focused = "true";
        const view = document.getElementById("workspaceView") as HTMLSelectElement | null;
        if (view) view.value = "details";
        pane.focus({ preventScroll: true });
        pane.scrollIntoView({ block: "nearest" });
    };
    if (workspace) {
        workspace.dataset.focused = "false";
        workspace.navigateDetails = details => {
            if (details) showDetails();
            else {
                workspace.dataset.focused = "false";
                const view = document.getElementById("workspaceView") as HTMLSelectElement | null;
                if (view) view.value = "snapshot";
                (selectedButton ?? open ?? view)?.focus({ preventScroll: true });
                window.scrollTo({ top: overviewScroll });
            }
        };
    }
    if (open) open.onclick = showDetails;
    if (back) back.onclick = () => {
        navigateHeapDetails(false);
    };
    document.getElementById("heapTypeSelection")?.remove();
    const selection = document.createElement("section");
    selection.id = "heapTypeSelection";
    selection.className = "evidence-selection";
    const note = document.createElement("p");
    note.textContent = "Retained objects in this snapshot, not allocation traffic. Search/sort covers returned top types only. "
        + "Select a type to find matching captured retention paths. Instance enumeration is not available here.";
    selection.appendChild(note);
    const detail = document.createElement("div");
    detail.tabIndex = -1;
    selection.appendChild(detail);
    if (pane) pane.insertBefore(selection, document.getElementById("heapQueryViews"));
    else table.parentElement?.insertBefore(selection, table.nextSibling);
    const rows = Array.from(table.querySelectorAll("tbody tr"));
    rows.forEach((row, index) => {
        const type = types[index];
        const cell = row.querySelector("td");
        if (!type || !cell) return;
        const button = document.createElement("button");
        button.type = "button";
        button.className = "heap-type-link";
        const name = String(type.typeFullName ?? "Unavailable");
        button.textContent = name;
        button.addEventListener("click", () => {
            selectedButton?.removeAttribute("aria-current");
            selectedButton = button;
            button.setAttribute("aria-current", "true");
            selection.dataset.type = name;
            detail.replaceChildren();
            const title = document.createElement("h4");
            title.textContent = name;
            const counts = document.createElement("p");
            counts.textContent = String(type.instanceCount ?? "Unavailable") + " instances | "
                + String(type.totalBytes ?? "Unavailable") + " direct bytes. Not dominator-retained size.";
            const clear = document.createElement("button");
            clear.type = "button";
            clear.textContent = "Clear type focus";
            clear.addEventListener("click", () => {
                selectedButton?.removeAttribute("aria-current");
                selectedButton = undefined;
                delete selection.dataset.type;
                detail.replaceChildren();
                applyHeapTypeFocus();
            });
            detail.append(title, counts, clear);
            const views = document.getElementById("heapQueryViews");
            const switcher = views?.querySelector<HTMLElement>(".evidence-view-switcher");
            if (views && switcher && document.getElementById("heapQueryViews-retention-paths")) {
                switcher.dataset.active = "heapQueryViews-retention-paths";
                updateEvidenceViewSwitcher(views);
            }
            applyHeapTypeFocus();
            if (pane) showDetails();
            else {
                detail.focus({ preventScroll: true });
                detail.scrollIntoView({ block: "nearest" });
            }
        });
        cell.replaceChildren(button);
    });
}

function applyHeapTypeFocus(): void {
    const selection = document.getElementById("heapTypeSelection");
    if (!selection) return;
    const paths = document.getElementById("heapQueryViews-retention-paths") as HTMLDetailsElement | null;
    let status = document.getElementById("heapTypeFocusStatus");
    if (!status) {
        status = document.createElement("p");
        status.id = "heapTypeFocusStatus";
        status.setAttribute("role", "status");
        selection.appendChild(status);
    }
    const type = selection.dataset.type;
    const entries = paths ? Array.from(paths.querySelectorAll<HTMLDetailsElement>(".query-view-nested")) : [];
    let matches = 0;
    for (const entry of entries) {
        const match = !type || (entry.querySelector("summary")?.textContent ?? "").toLowerCase().includes(type.toLowerCase());
        entry.hidden = !match;
        if (match) { matches++; if (type) entry.open = true; }
    }
    if (type && paths) paths.open = true;
    status.textContent = !type ? "No type focus applied."
        : !paths ? "Retention paths are not available yet. Capture with retention paths enabled; no live read was started."
        : paths.classList.contains("query-view-error") ? "Retention paths are unavailable. See the query error; no new live read was started."
        : matches + " of " + entries.length + " returned retention paths match this type name. "
            + "Substring match, not an exhaustive instance search. Address links open the object inspector.";
}

function updateThreadNavigation(view: string, result: Record<string, unknown>): void {
    const root = document.getElementById("captureThreadResult");
    if (!root) return;
    let explorer = document.getElementById("threadExplorer");
    if (!explorer) {
        explorer = document.createElement("section");
        explorer.id = "threadExplorer";
        root.appendChild(explorer);
    }
    type ThreadState = {
        threads: Array<Record<string, unknown>>;
        chains?: Record<string, unknown>;
        deadlocks?: Array<Record<string, unknown>>;
        selected?: unknown;
        search: string;
        mode?: string;
        returnPanel?: string;
    };
    const host = explorer as HTMLElement & { threadState?: ThreadState };
    if (view === "snapshot") host.threadState = {
        threads: Array.isArray(result.threads) ? result.threads : [], search: "",
    };
    const state = host.threadState ??= { threads: [], search: "" };
    if (view === "wait-chains") state.chains = result.waitChains as Record<string, unknown> | undefined;
    if (view === "deadlocks") state.deadlocks = Array.isArray(result.deadlocks) ? result.deadlocks : [];
    const hadSearchFocus = explorer.querySelector("input") === document.activeElement;
    const focusedMode = Array.from(explorer.querySelectorAll<HTMLButtonElement>(".thread-mode-tab"))
        .find(button => button === document.activeElement)?.dataset.mode;
    explorer.replaceChildren();
    const heading = document.createElement("h4");
    heading.textContent = "Thread explorer";
    const note = document.createElement("p");
    note.textContent = "Point-in-time snapshot, not execution history. Wait reasons and cycle candidates may be inferred. "
        + "Groups may expose representative thread IDs only; missing inline stacks remain unavailable.";
    const search = document.createElement("input");
    search.type = "search";
    search.maxLength = 512;
    search.value = state.search;
    search.setAttribute("aria-label", "Find returned threads or stack methods");
    const listing = document.createElement("div");
    const mode = { value: state.mode ?? "threads" };
    const modes = document.createElement("div");
    modes.className = "thread-mode-tabs";
    modes.setAttribute("role", "tablist");
    modes.setAttribute("aria-label", "Thread explorer mode");
    listing.id = "threadModePanel";
    listing.setAttribute("role", "tabpanel");
    listing.tabIndex = 0;
    const modeButtons: HTMLButtonElement[] = [];
    const selectMode = (value: string) => {
        mode.value = value;
        state.mode = value;
        search.disabled = value !== "threads";
        for (const button of modeButtons) {
            const active = button.dataset.mode === value;
            button.setAttribute("aria-selected", String(active));
            button.tabIndex = active ? 0 : -1;
            if (active) listing.setAttribute("aria-labelledby", button.id);
        }
        drawList();
    };
    for (const [value, label] of [
        ["threads", "Returned threads"],
        ["waits", "Wait chains"], ["cycles", "Inferred cycle candidates"],
    ]) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "thread-mode-tab";
        button.id = "threadMode-" + value;
        button.dataset.mode = value;
        button.textContent = label;
        button.setAttribute("role", "tab");
        button.setAttribute("aria-controls", listing.id);
        button.addEventListener("click", () => selectMode(value));
        button.addEventListener("keydown", event => {
            const index = modeButtons.indexOf(button);
            const next = event.key === "ArrowRight" ? (index + 1) % modeButtons.length
                : event.key === "ArrowLeft" ? (index + modeButtons.length - 1) % modeButtons.length
                : event.key === "Home" ? 0 : event.key === "End" ? modeButtons.length - 1 : -1;
            if (next < 0) return;
            event.preventDefault();
            selectMode(modeButtons[next].dataset.mode!);
            modeButtons[next].focus({ preventScroll: true });
        });
        modeButtons.push(button);
        modes.appendChild(button);
    }
    const details = document.createElement("section");
    details.className = "evidence-selection";
    details.tabIndex = -1;
    details.setAttribute("aria-label", "Selected thread");
    const text = (parent: HTMLElement, value: string) => {
        const line = document.createElement("p");
        line.textContent = value;
        parent.appendChild(line);
    };
    const pick = (parent: HTMLElement, id: unknown) => {
        if (typeof id !== "number" || !Number.isSafeInteger(id) || id < 0) return;
        const button = document.createElement("button");
        button.type = "button";
        button.textContent = "Thread " + id;
        button.addEventListener("click", () => {
            state.selected = id;
            drawDetails();
            details.focus({ preventScroll: true });
            details.scrollIntoView({ block: "nearest" });
        });
        parent.appendChild(button);
    };
    function drawDetails(): void {
        details.replaceChildren();
        if (state.returnPanel) {
            const back = document.createElement("button");
            back.type = "button";
            const panelView = state.returnPanel.replace("threadQueryViews-", "");
            const returnLabels: Record<string, string> = {
                snapshot: "inline thread summary",
                deadlocks: "inferred deadlock candidates",
                "unique-stacks": "grouped stacks",
                "wait-chains": "wait chains",
                threadpool: "thread-pool query",
            };
            back.textContent = "Back to " + (returnLabels[panelView] ?? "thread evidence");
            back.addEventListener("click", () => {
                const returnView = state.returnPanel === "snapshot" ? "snapshot" : panelView;
                setThreadWorkspaceView(returnView);
                document.getElementById("tab-" + state.returnPanel)?.focus({ preventScroll: true });
            });
            details.appendChild(back);
        }
        if (state.selected === undefined) { text(details, "Select a thread, stack group member, or wait-chain participant."); return; }
        const thread = state.threads.find(item => item.managedThreadId === state.selected);
        text(details, "Managed thread " + state.selected);
        if (thread) {
            text(details, "State: " + String(thread.state ?? "Unavailable") + " | wait: "
                + String(thread.inferredWaitReason ?? "Unavailable") + " | OS thread: " + String(thread.osThreadId ?? "Unavailable"));
            const frames = Array.isArray(thread.frames) ? thread.frames : [];
            const stack = document.createElement("ol");
            for (const frame of frames) {
                const item = document.createElement("li");
                item.textContent = typeof frame === "string" ? frame : String(frame.displayName ?? "Unavailable");
                stack.appendChild(item);
            }
            details.appendChild(stack);
            if (!frames.length) text(details, "No inline stack was returned for this thread.");
        } else text(details, "This participant was not included in the returned inline thread list.");
        for (const chain of Array.isArray(state.chains?.chains) ? state.chains.chains : []) {
            for (const link of Array.isArray(chain.links) ? chain.links : []) {
                if (link.waitingThreadId !== state.selected && link.ownerThreadId !== state.selected) continue;
                const edge = document.createElement("div");
                text(edge, "Waiting thread:");
                pick(edge, link.waitingThreadId);
                text(edge, "waits for " + String(link.targetLabel ?? link.targetKind ?? "unknown resource")
                    + " | reason: " + String(link.waitReason ?? "Unavailable") + " | confidence: " + String(link.confidence ?? "Unavailable"));
                text(edge, "Owner thread:");
                pick(edge, link.ownerThreadId);
                if (link.ownerThreadId == null) text(edge, "Owner unavailable.");
                details.appendChild(edge);
            }
        }
    }
    function drawList(): void {
        listing.replaceChildren();
        const term = state.search.toLowerCase();
        const matches = state.threads.filter(thread => JSON.stringify(thread).toLowerCase().includes(term));
        if (mode.value === "threads") {
            text(listing, matches.length + " of " + state.threads.length + " returned threads match.");
            for (const thread of matches) {
                if (typeof thread.managedThreadId !== "number") continue;
                const row = document.createElement("div");
                row.className = "thread-explorer-row";
                const topFrame = Array.isArray(thread.frames) && thread.frames.length
                    ? (typeof thread.frames[0] === "string" ? thread.frames[0] : String(thread.frames[0]?.displayName ?? "Unavailable"))
                    : "Unavailable";
                const button = document.createElement("button");
                button.type = "button";
                button.textContent = "Thread " + thread.managedThreadId;
                button.setAttribute("aria-label", button.textContent + " | " + String(thread.state ?? "state unavailable")
                    + " | " + topFrame);
                button.addEventListener("click", () => {
                    state.selected = thread.managedThreadId;
                    drawDetails();
                    details.focus({ preventScroll: true });
                    details.scrollIntoView({ block: "nearest" });
                });
                row.appendChild(button);
                const context = document.createElement("span");
                context.className = "thread-explorer-row-context";
                context.textContent = String(thread.state ?? "state unavailable") + " | " + topFrame;
                row.appendChild(context);
                listing.appendChild(row);
            }
        }
        if (mode.value === "cycles") text(listing, state.deadlocks === undefined
            ? "Cycle evidence has not been returned; check Deadlock candidates for availability."
            : state.deadlocks.length + " inferred candidates returned. Absence does not establish that no deadlock exists.");
        for (const candidate of mode.value === "cycles" ? state.deadlocks ?? [] : []) {
            const row = document.createElement("div");
            text(row, "Inferred cycle candidate | confidence: " + String(candidate.confidence ?? "Unavailable"));
            for (const member of Array.isArray(candidate.cycleMembers) ? candidate.cycleMembers : []) pick(row, member.threadId);
            listing.appendChild(row);
        }
        const chains = Array.isArray(state.chains?.chains) ? state.chains.chains : [];
        if (mode.value === "waits" && !chains.length) text(listing, state.chains
            ? "No wait chains were returned." : "Wait chains have not been returned; check the Wait chains evidence view for availability.");
        for (const chain of mode.value === "waits" ? chains : []) {
            const disclosure = document.createElement("details");
            const summary = document.createElement("summary");
            summary.textContent = "Wait chain from thread " + String(chain.rootThreadId ?? "Unavailable")
                + " | " + String(chain.terminalKind ?? "Unknown terminal") + " | confidence: "
                + String(chain.inferenceConfidence ?? "Unavailable");
            disclosure.appendChild(summary);
            for (const link of Array.isArray(chain.links) ? chain.links : []) {
                const edge = document.createElement("div");
                text(edge, "Waiting thread:");
                pick(edge, link.waitingThreadId);
                text(edge, "waits for " + String(link.targetLabel ?? link.targetKind ?? "unknown resource")
                    + " | owner thread:");
                pick(edge, link.ownerThreadId);
                if (link.ownerThreadId == null) text(edge, "Owner unavailable.");
                disclosure.appendChild(edge);
            }
            listing.appendChild(disclosure);
        }
    }
    search.addEventListener("input", () => { state.search = search.value; drawList(); });
    explorer.append(modes, search, listing, details, note);
    selectMode(mode.value);
    drawDetails();
    if (hadSearchFocus) search.focus({ preventScroll: true });
    else if (focusedMode) modeButtons.find(button => button.dataset.mode === focusedMode)?.focus({ preventScroll: true });
    const queries = document.getElementById("threadQueryViews");
    if (queries) {
        if (explorer.parentElement !== queries) queries.insertBefore(explorer, queries.firstChild);
        updateEvidenceViewSwitcher(queries);
    }
}

export const SNAPSHOT_NAVIGATION_SCRIPT = navigateHeapDetails.toString() + "\n" + renderHeapTypeNavigation.toString() + "\n"
    + applyHeapTypeFocus.toString() + "\n" + setThreadWorkspaceView.toString() + "\n" + navigateThreadTo.toString()
    + "\n" + navigateThreadFromGroup.toString() + "\n" + updateThreadNavigation.toString();
