import { SingleFlightGuard, isQueryResult, type HeapTypeIdentity, type QueryResult } from "./protocol";

declare function updateEvidenceViewSwitcher(container: HTMLElement): void;
declare function selectThreadSnapshotView(view: string): void;
declare function appendAddressValue(parent: HTMLElement, value: unknown, containerId: string): void;
declare const vscode: { postMessage(message: Record<string, unknown>): void };

export const TYPE_INSTANCE_PAGE_SIZE = 50;

export interface TypeInstanceNavigationHost {
    currentHeapHandle(): string | undefined;
    acknowledge(typeName: string): Promise<boolean>;
    query(handle: string, typeIdentity: HeapTypeIdentity, cursor: string | undefined, acknowledgeRisk: boolean): Promise<QueryResult>;
    post(message: Record<string, unknown>): void;
    log(text: string): void;
}

export class TypeInstanceNavigationController {
    private readonly guard = new SingleFlightGuard();

    public constructor(private readonly host: TypeInstanceNavigationHost) {}

    public async handle(message: Record<string, unknown>): Promise<boolean> {
        if (message.type !== "queryHeapInstances") return false;
        const identity = parseHeapTypeIdentity(message.typeIdentity);
        const cursor = message.cursor === undefined ? undefined
            : typeof message.cursor === "string" && message.cursor.length > 0 && message.cursor.length <= 256
                ? message.cursor : null;
        const selectionId = typeof message.selectionId === "number" && Number.isSafeInteger(message.selectionId)
            && message.selectionId > 0 ? message.selectionId : undefined;
        if (identity === undefined || cursor === null || selectionId === undefined) {
            this.host.log("Ignored an invalid heap type-instance request from the webview.");
            return true;
        }
        const outcome = await this.guard.run(() => this.query(identity, cursor, selectionId));
        if (!outcome.started) {
            this.fail(identity.typeFullName, selectionId, "Another type-instance page is still running.");
        }
        return true;
    }

    private async query(identity: HeapTypeIdentity, cursor: string | undefined, selectionId: number): Promise<void> {
        const handle = this.host.currentHeapHandle();
        if (handle === undefined) {
            this.fail(identity.typeFullName, selectionId, "No current heap snapshot handle is available.");
            return;
        }
        if (!await this.host.acknowledge(identity.typeFullName)) {
            this.fail(identity.typeFullName, selectionId, "Cancelled: the live-read acknowledgement was declined.");
            return;
        }
        if (this.host.currentHeapHandle() !== handle) {
            this.fail(identity.typeFullName, selectionId, "The heap context changed during acknowledgement; select the type again.");
            return;
        }
        try {
            const result = await this.host.query(handle, identity, cursor, true);
            if (!isQueryResult(result) || result.view !== "type-instances"
                || !sameHeapTypeIdentity(identity, result.requestedTypeIdentity)) {
                this.fail(identity.typeFullName, selectionId, "The CLI returned an unexpected type-instance response.");
                return;
            }
            this.host.post({ type: "heapInstances", selectionId, typeIdentity: identity, result });
        } catch (error) {
            this.fail(identity.typeFullName, selectionId, error instanceof Error ? error.message : String(error));
        }
    }

    private fail(typeFullName: string, selectionId: number, message: string): void {
        this.host.post({ type: "heapInstancesError", selectionId, typeFullName, message });
        this.host.log(`Heap type-instance query for '${typeFullName}' failed: ${message}`);
    }
}

function parseHeapTypeIdentity(value: unknown): HeapTypeIdentity | undefined {
    if (typeof value !== "object" || value === null) return undefined;
    const candidate = value as Record<string, unknown>;
    if (typeof candidate.typeFullName !== "string" || candidate.typeFullName.trim().length === 0
        || candidate.typeFullName.length > 1024) return undefined;
    for (const [key, limit] of [["moduleName", 1024], ["modulePath", 4096]] as const) {
        const field = candidate[key];
        if (field !== undefined && field !== null && (typeof field !== "string" || field.length > limit)) return undefined;
    }
    if (candidate.moduleVersionId !== undefined && candidate.moduleVersionId !== null
        && (typeof candidate.moduleVersionId !== "string"
            || !/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(candidate.moduleVersionId)))
        return undefined;
    if (candidate.metadataToken !== undefined && candidate.metadataToken !== null
        && (typeof candidate.metadataToken !== "number" || !Number.isSafeInteger(candidate.metadataToken)
            || candidate.metadataToken <= 0 || candidate.metadataToken > 2147483647))
        return undefined;
    return candidate as unknown as HeapTypeIdentity;
}

function sameHeapTypeIdentity(expected: HeapTypeIdentity, actual: unknown): boolean {
    const parsed = parseHeapTypeIdentity(actual);
    return parsed !== undefined
        && parsed.typeFullName === expected.typeFullName
        && (parsed.moduleName ?? null) === (expected.moduleName ?? null)
        && (parsed.modulePath ?? null) === (expected.modulePath ?? null)
        && (parsed.moduleVersionId ?? null) === (expected.moduleVersionId ?? null)
        && (parsed.metadataToken ?? null) === (expected.metadataToken ?? null);
}

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

function navigateThreadTo(id: number, returnPanel?: string, returnFocus?: HTMLElement): void {
    const explorer = document.getElementById("threadExplorer") as (HTMLElement & {
        threadState?: {
            selected?: unknown; mode?: string; returnPanel?: string; returnFocus?: HTMLElement; returnMode?: string;
            expanded?: number[]; revealSelected?: boolean;
        };
    }) | null;
    if (!explorer?.threadState) return;
    explorer.threadState.selected = id;
    explorer.threadState.mode = "threads";
    explorer.threadState.returnPanel = returnPanel;
    explorer.threadState.returnFocus = returnFocus;
    explorer.threadState.returnMode = undefined;
    explorer.threadState.revealSelected = true;
    explorer.threadState.expanded = [...new Set([...(explorer.threadState.expanded ?? []), id])];
    updateThreadNavigation("selection", {});
    setThreadWorkspaceView("thread-explorer");
    const button = Array.from(explorer.querySelectorAll<HTMLElement>(".thread-disclosure"))
        .find(item => item.dataset.threadId === String(id));
    button?.focus({ preventScroll: true });
    button?.scrollIntoView({ block: "nearest" });
}

function navigateHeapDetails(details: boolean): void {
    const workspace = document.getElementById("heapWorkspace") as (HTMLElement & {
        navigateDetails?: (details: boolean) => void;
    }) | null;
    workspace?.navigateDetails?.(details);
}

function resetHeapDetailsNavigation(): void {
    navigateHeapDetails(false);
    const selection = document.getElementById("heapTypeSelection") as (HTMLElement & {
        instanceState?: unknown;
    }) | null;
    if (!selection) return;
    delete selection.dataset.type;
    delete selection.instanceState;
    selection.remove();
}

function navigateThreadFromGroup(id: number, returnFocus?: HTMLElement): void {
    navigateThreadTo(id, "threadQueryViews-unique-stacks", returnFocus);
}

function renderHeapTypeNavigation(types: Array<Record<string, unknown>>): void {
    const table = document.getElementById("captureHeapTable") as HTMLTableElement | null;
    if (!table) return;
    const workspace = document.getElementById("heapWorkspace") as (HTMLElement & {
        navigateDetails?: (details: boolean) => void;
    }) | null;
    const pane = document.getElementById("heapDetailPane");
    const back = document.getElementById("backToHeapTypes");
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
                (selectedButton ?? view ?? table)?.focus({ preventScroll: true });
                window.scrollTo({ top: overviewScroll });
            }
        };
    }
    table.tabIndex = -1;
    if (back) back.onclick = () => {
        navigateHeapDetails(false);
    };
    document.getElementById("heapTypeSelection")?.remove();
    const selection = document.createElement("section");
    selection.id = "heapTypeSelection";
    selection.className = "evidence-selection";
    const note = document.createElement("p");
    note.textContent = "Retained objects in this snapshot, not allocation traffic. Search/sort covers returned top types only. "
        + "Select a type to enumerate a bounded page of current instances, then inspect an address.";
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
            const identity = typeof type.identity === "object" && type.identity !== null
                ? type.identity as Record<string, unknown> : { typeFullName: name, moduleName: type.moduleName ?? null };
            const status = document.createElement("p");
            status.setAttribute("role", "status");
            status.textContent = "Requesting the first bounded instance page…";
            const instances = document.createElement("div");
            instances.className = "heap-type-instances";
            const more = document.createElement("button");
            more.type = "button";
            more.textContent = "Load next instance page";
            more.hidden = true;
            const selectionId = ((window as unknown as { heapTypeSelectionSequence?: number }).heapTypeSelectionSequence ?? 0) + 1;
            (window as unknown as { heapTypeSelectionSequence?: number }).heapTypeSelectionSequence = selectionId;
            const state = { selectionId, typeIdentity: identity, status, instances, more, returned: 0, nextCursor: undefined as string | undefined };
            (selection as HTMLElement & { instanceState?: typeof state }).instanceState = state;
            more.addEventListener("click", () => {
                if (!state.nextCursor) return;
                more.disabled = true;
                status.textContent = "Requesting the next bounded instance page…";
                vscode.postMessage({ type: "queryHeapInstances", selectionId, typeIdentity: identity, cursor: state.nextCursor });
            });
            const clear = document.createElement("button");
            clear.type = "button";
            clear.textContent = "Clear type focus";
            clear.addEventListener("click", () => {
                selectedButton?.removeAttribute("aria-current");
                selectedButton = undefined;
                delete selection.dataset.type;
                delete (selection as HTMLElement & { instanceState?: unknown }).instanceState;
                detail.replaceChildren();
                applyHeapTypeFocus();
            });
            detail.append(title, counts, status, instances, more, clear);
            const views = document.getElementById("heapQueryViews");
            const switcher = views?.querySelector<HTMLElement>(".evidence-view-switcher");
            if (views && switcher && document.getElementById("heapQueryViews-retention-paths")) {
                switcher.dataset.active = "heapQueryViews-retention-paths";
                updateEvidenceViewSwitcher(views);
            }
            applyHeapTypeFocus();
            vscode.postMessage({ type: "queryHeapInstances", selectionId, typeIdentity: identity });
            if (pane) showDetails();
            else {
                detail.focus({ preventScroll: true });
                detail.scrollIntoView({ block: "nearest" });
            }
        });
        cell.replaceChildren(button);
    });
}

function handleHeapInstancesMessage(message: Record<string, unknown>): boolean {
    if (message.type !== "heapInstances" && message.type !== "heapInstancesError") return false;
    const selection = document.getElementById("heapTypeSelection") as (HTMLElement & {
        instanceState?: {
            selectionId: number;
            typeIdentity: Record<string, unknown>;
            status: HTMLElement;
            instances: HTMLElement;
            more: HTMLButtonElement;
            returned: number;
            nextCursor?: string;
        };
    }) | null;
    const state = selection?.instanceState;
    if (!state || message.selectionId !== state.selectionId) return true;
    state.more.disabled = false;
    if (message.type === "heapInstancesError") {
        state.status.textContent = "Instance enumeration unavailable: " + String(message.message ?? "The query failed.");
        state.more.hidden = true;
        return true;
    }

    const result = message.result as Record<string, unknown> | undefined;
    const rows = Array.isArray(result?.typeInstances) ? result.typeInstances : [];
    for (const value of rows) {
        if (typeof value !== "object" || value === null) continue;
        const row = value as Record<string, unknown>;
        const card = document.createElement("div");
        card.className = "query-view-metric";
        const address = document.createElement("strong");
        appendAddressValue(address, row.address, "heapQueryViews");
        const metadata = document.createElement("span");
        metadata.textContent = String(row.size ?? "Unavailable") + " bytes | "
            + String(row.generation ?? "Unknown generation") + " | " + String(row.segmentKind ?? "Unknown segment");
        card.append(address, metadata);
        state.instances.appendChild(card);
    }
    state.returned += rows.length;
    const complete = result?.instanceEnumerationComplete === true;
    state.nextCursor = typeof result?.nextCursor === "string" ? result.nextCursor : undefined;
    state.more.hidden = complete || state.nextCursor === undefined;
    const scanned = typeof result?.scannedObjects === "number" ? result.scannedObjects : "unknown";
    const consistency = result?.instanceConsistency === "stable-offline-dump"
        ? "Stable offline dump paging."
        : "Live best-effort paging: GC or allocation between pages can move, remove, add, skip, or repeat logical instances.";
    const origin = String(result?.origin ?? "Unknown origin");
    const capturedAt = typeof result?.capturedAt === "string" ? result.capturedAt : "unknown capture time";
    const enumeratedAt = typeof result?.enumeratedAt === "string" ? result.enumeratedAt : "unknown enumeration time";
    const quality = typeof result?.quality === "object" && result.quality !== null
        ? result.quality as Record<string, unknown> : undefined;
    const limitationCount = Array.isArray(quality?.limitations) ? quality.limitations.length : 0;
    state.status.textContent = state.returned + " instance address(es) returned across bounded page(s); "
        + scanned + " heap object(s) scanned in the latest page. "
        + (complete ? "Enumeration reached the end of the heap. " : "The inventory is incomplete; more pages may exist. ")
        + consistency + " Source: " + origin + ", snapshot captured " + capturedAt + ", page enumerated " + enumeratedAt + ". "
        + (limitationCount ? "Quality reports " + limitationCount + " limitation(s)." : "No additional quality limitation was reported.");
    const warnings = Array.isArray(result?.instanceWarnings) ? result.instanceWarnings : [];
    for (const warning of warnings) {
        const item = document.createElement("p");
        item.className = "thread-secondary";
        item.textContent = String(warning);
        state.instances.appendChild(item);
    }
    return true;
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
        returnFocus?: HTMLElement;
        returnMode?: string;
        expanded?: number[];
        revealSelected?: boolean;
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
    const focusedThread = Array.from(explorer.querySelectorAll<HTMLButtonElement>(".thread-disclosure"))
        .find(button => button === document.activeElement)?.dataset.threadId;
    explorer.replaceChildren();
    const note = document.createElement("p");
    note.textContent = "Point-in-time snapshot, not execution history. Wait reasons and cycle candidates may be inferred. "
        + "Groups may expose representative thread IDs only; missing inline stacks remain unavailable. "
        + "Numeric runtime flags are not decoded here; raw values remain in Technical details.";
    const search = document.createElement("input");
    search.type = "search";
    search.maxLength = 512;
    search.value = state.search;
    search.id = "threadExplorerSearch";
    search.placeholder = "Thread ID, state or stack method";
    search.setAttribute("aria-label", "Find returned threads or stack methods");
    const searchLabel = document.createElement("label");
    searchLabel.htmlFor = search.id;
    searchLabel.className = "thread-search";
    searchLabel.textContent = "Filter returned threads";
    searchLabel.appendChild(search);
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
    const text = (parent: HTMLElement, value: string) => {
        const line = document.createElement("p");
        line.textContent = value;
        parent.appendChild(line);
    };
    const pick = (parent: HTMLElement, id: unknown) => {
        if (typeof id !== "number" || !Number.isSafeInteger(id) || id < 0) return;
        const button = document.createElement("button");
        button.type = "button";
        button.className = "heap-type-link";
        button.textContent = "Thread " + id;
        button.addEventListener("click", () => {
            state.selected = id;
            if (mode.value !== "threads") state.returnMode = mode.value;
            state.revealSelected = true;
            state.expanded = [...new Set([...(state.expanded ?? []), id])];
            selectMode("threads");
            const disclosure = Array.from(listing.querySelectorAll<HTMLButtonElement>(".thread-disclosure"))
                .find(item => item.dataset.threadId === String(id));
            disclosure?.focus({ preventScroll: true });
            disclosure?.scrollIntoView({ block: "nearest" });
        });
        parent.appendChild(button);
    };
    function drawDetails(details: HTMLElement, thread: Record<string, unknown> | undefined, id: number): void {
        if (state.selected === id && (state.returnPanel || state.returnMode)) {
            const back = document.createElement("button");
            back.type = "button";
            const panelView = state.returnPanel?.replace("threadQueryViews-", "");
            const returnLabels: Record<string, string> = {
                snapshot: "inline thread summary",
                deadlocks: "inferred deadlock candidates",
                "unique-stacks": "grouped stacks",
                "wait-chains": "wait chains",
                threadpool: "thread-pool query",
            };
            back.textContent = "Back to " + (panelView ? returnLabels[panelView] ?? "thread evidence"
                : state.returnMode === "cycles" ? "inferred cycle candidates" : "wait chains");
            back.addEventListener("click", () => {
                if (panelView) {
                    const returnView = state.returnPanel === "snapshot" ? "snapshot" : panelView;
                    setThreadWorkspaceView(returnView);
                    (state.returnFocus ?? document.getElementById("tab-" + state.returnPanel))
                        ?.focus({ preventScroll: true });
                } else {
                    selectMode(state.returnMode ?? "threads");
                    modeButtons.find(button => button.dataset.mode === mode.value)?.focus({ preventScroll: true });
                }
            });
            details.appendChild(back);
        }
        const technical = document.createElement("details");
        technical.className = "thread-technical";
        const technicalLabel = document.createElement("summary");
        technicalLabel.textContent = "Technical details";
        technical.appendChild(technicalLabel);
        if (thread) {
            text(technical, "Reported state: " + String(thread.state ?? "Unavailable") + " | wait: "
                + String(thread.inferredWaitReason ?? "Unavailable") + " | OS thread: " + String(thread.osThreadId ?? "Unavailable"));
            const frames = Array.isArray(thread.frames) ? thread.frames : [];
            const count = document.createElement("p");
            count.className = "thread-secondary";
            count.textContent = frames.length + " returned frame(s)";
            details.appendChild(count);
            const stack = document.createElement("ol");
            stack.className = "thread-stack";
            stack.setAttribute("aria-label", "Returned stack for managed thread " + id);
            for (const frame of frames) {
                const item = document.createElement("li");
                item.textContent = typeof frame === "string" ? frame : String(frame.displayName ?? "Unavailable");
                stack.appendChild(item);
            }
            details.appendChild(stack);
            if (!frames.length) text(details, "Unavailable: no inline stack was returned for this thread.");
        } else text(details, "This participant was not included in the returned inline thread list.");
        details.appendChild(technical);
        for (const chain of Array.isArray(state.chains?.chains) ? state.chains.chains : []) {
            for (const link of Array.isArray(chain.links) ? chain.links : []) {
                if (link.waitingThreadId !== id && link.ownerThreadId !== id) continue;
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
            const count = document.createElement("p");
            count.className = "thread-secondary";
            count.textContent = matches.length + " of " + state.threads.length + " returned threads match.";
            listing.appendChild(count);
            const table = document.createElement("table");
            table.className = "thread-explorer-table";
            table.setAttribute("aria-label", "Returned threads and inline stacks");
            const head = document.createElement("thead");
            const headers = document.createElement("tr");
            for (const name of ["Thread", "State", "Top frame"]) {
                const cell = document.createElement("th");
                cell.scope = "col";
                cell.textContent = name;
                headers.appendChild(cell);
            }
            head.appendChild(headers);
            table.appendChild(head);
            const body = document.createElement("tbody");
            table.appendChild(body);
            const selected = state.threads.find(thread => thread.managedThreadId === state.selected);
            const shown = [...matches];
            if (state.revealSelected && selected && !shown.includes(selected)) {
                shown.push(selected);
                text(listing, "Linked thread shown outside the current filter.");
            }
            const appendThread = (thread: Record<string, unknown> | undefined, id: number): void => {
                const row = document.createElement("tr");
                row.className = "thread-explorer-row";
                const topFrame = thread && Array.isArray(thread.frames) && thread.frames.length
                    ? (typeof thread.frames[0] === "string" ? thread.frames[0] : String(thread.frames[0]?.displayName ?? "Unavailable"))
                    : "Unavailable";
                const rawState = thread?.state;
                const reportedState = typeof rawState === "string" && rawState.trim() ? rawState.trim() : undefined;
                const numericState = typeof rawState === "number" || !!reportedState && /^-?\d+$/.test(reportedState);
                const stateLabel = numericState ? "Not decoded" : reportedState ?? "Unavailable";
                const disclosureRow = document.createElement("tr");
                disclosureRow.className = "thread-inline-row";
                const detailCell = document.createElement("td");
                detailCell.colSpan = 3;
                const details = document.createElement("section");
                details.className = "thread-inline-stack";
                details.id = "thread-inline-" + id;
                details.setAttribute("aria-label", "Managed thread " + id + " stack and details");
                detailCell.appendChild(details);
                disclosureRow.appendChild(detailCell);
                const button = document.createElement("button");
                button.type = "button";
                button.className = "thread-disclosure";
                button.dataset.threadId = String(id);
                button.textContent = "Thread " + id;
                button.setAttribute("aria-controls", details.id);
                const setExpanded = (expanded: boolean): void => {
                    const ids = new Set(state.expanded ?? []);
                    if (expanded) ids.add(id); else ids.delete(id);
                    state.expanded = [...ids];
                    button.setAttribute("aria-expanded", String(expanded));
                    disclosureRow.hidden = !expanded;
                    row.classList.toggle("thread-expanded", expanded);
                    if (expanded) { details.replaceChildren(); drawDetails(details, thread, id); }
                };
                button.addEventListener("click", () => {
                    state.selected = id;
                    setExpanded(button.getAttribute("aria-expanded") !== "true");
                });
                button.addEventListener("keydown", event => {
                    if (event.key !== "ArrowRight" && event.key !== "ArrowLeft" && event.key !== "Escape") return;
                    event.preventDefault();
                    state.selected = id;
                    setExpanded(event.key === "ArrowRight");
                });
                details.addEventListener("keydown", event => {
                    if (event.key !== "Escape") return;
                    event.preventDefault();
                    event.stopPropagation();
                    setExpanded(false);
                    button.focus({ preventScroll: true });
                });
                const identity = document.createElement("td");
                identity.appendChild(button);
                const stateCell = document.createElement("td");
                stateCell.textContent = stateLabel;
                const frameCell = document.createElement("td");
                frameCell.className = "thread-top-frame";
                const preview = document.createElement("span");
                preview.className = "thread-top-frame-preview";
                preview.textContent = topFrame;
                frameCell.appendChild(preview);
                row.append(identity, stateCell, frameCell);
                body.append(row, disclosureRow);
                setExpanded((state.expanded ?? []).includes(id));
            };
            for (const thread of shown) {
                const id = thread.managedThreadId;
                if (typeof id === "number" && Number.isSafeInteger(id) && id >= 0) appendThread(thread, id);
            }
            if (state.revealSelected && !selected && typeof state.selected === "number")
                appendThread(undefined, state.selected);
            if (!body.children.length) text(listing, state.threads.length ? "No returned threads match this filter." : "No threads were returned.");
            listing.appendChild(table);
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
    search.addEventListener("input", () => { state.search = search.value; state.revealSelected = false; drawList(); });
    explorer.append(modes, searchLabel, listing, note);
    selectMode(mode.value);
    if (hadSearchFocus) search.focus({ preventScroll: true });
    else if (focusedMode) modeButtons.find(button => button.dataset.mode === focusedMode)?.focus({ preventScroll: true });
    else if (focusedThread) Array.from(explorer.querySelectorAll<HTMLButtonElement>(".thread-disclosure"))
        .find(button => button.dataset.threadId === focusedThread)?.focus({ preventScroll: true });
    const queries = document.getElementById("threadQueryViews");
    if (queries) {
        if (explorer.parentElement !== queries) queries.insertBefore(explorer, queries.firstChild);
        updateEvidenceViewSwitcher(queries);
    }
}

export const SNAPSHOT_NAVIGATION_SCRIPT = navigateHeapDetails.toString() + "\n" + resetHeapDetailsNavigation.toString() + "\n"
    + renderHeapTypeNavigation.toString() + "\n"
    + handleHeapInstancesMessage.toString() + "\n"
    + applyHeapTypeFocus.toString() + "\n" + setThreadWorkspaceView.toString() + "\n" + navigateThreadTo.toString()
    + "\n" + navigateThreadFromGroup.toString() + "\n" + updateThreadNavigation.toString();
