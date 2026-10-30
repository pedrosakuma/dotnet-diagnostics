import { profileRetentionText } from "./actionAvailability";

const retentionText = profileRetentionText;

interface CpuTreeNode {
    frame: { method: string; module: string };
    inclusiveSamples: number;
    exclusiveSamples: number;
    children: CpuTreeNode[];
    selfSamples?: { runningSamples: number; waitingSamples: number; unknownSamples: number };
}

interface CpuTreeData {
    root?: CpuTreeNode;
    totalSamples?: number;
    truncated?: boolean;
    traversalLimitReached?: boolean;
}

interface CpuProfileState {
    mode: string;
    search: string;
    focus: number[];
    selected?: number[];
    expanded?: string[];
}

function renderCpuProfile(
    container: HTMLElement,
    data: CpuTreeData,
    query: (view: string, method?: string) => void,
    state: CpuProfileState,
    weightUnit: "samples" | "allocation-tick-estimated-bytes" = "samples",
): void {
    const allocation = weightUnit === "allocation-tick-estimated-bytes";
    const unit = allocation ? "estimated bytes" : "samples";
    const entries: Array<{ node: CpuTreeNode; path: number[]; parent?: number }> = [];
    let limited = false;
    let invalid = false;
    const validCount = (value: unknown): value is number =>
        typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
    function visit(node: CpuTreeNode | undefined, path: number[], parent?: number): void {
        if (path.length >= 8 || entries.length >= 64) { limited = true; return; }
        if (!node || !validCount(node.inclusiveSamples) || !validCount(node.exclusiveSamples)
            || node.exclusiveSamples > node.inclusiveSamples || !Array.isArray(node.children)
            || typeof node.frame?.method !== "string") { invalid = true; return; }
        const index = entries.length;
        entries.push({ node, path, parent });
        const children = node.children;
        if (path.length === 7 && children.length > 0) { limited = true; return; }
        for (let i = 0; i < children.length; i++) {
            if (entries.length >= 64) { limited = true; break; }
            visit(children[i], [...path, i], index);
        }
    }
    if (data.root) visit(data.root, []);
    for (let i = 0; i < entries.length; i++) {
        const entry = entries[i];
        const represented = entries.filter(child => child.parent === i)
            .reduce((sum, child) => sum + child.node.inclusiveSamples, entry.node.exclusiveSamples);
        if (represented > entry.node.inclusiveSamples) invalid = true;
    }
    if (invalid
        || (data.totalSamples !== undefined && !validCount(data.totalSamples))
        || (allocation && data.totalSamples === undefined)
        || (allocation && data.root !== undefined && validCount(data.totalSamples)
            && data.root.inclusiveSamples > data.totalSamples)) {
        const message = document.createElement("p");
        message.setAttribute("role", "alert");
        message.textContent = "Call tree unavailable: invalid or missing " + unit + " / frame data. No values were replaced with zero.";
        container.appendChild(message);
        return;
    }
    const samePath = (left: number[], right: number[]) =>
        left.length === right.length && left.every((part, i) => part === right[i]);
    const atPath = (path: number[]) => entries.find(entry => samePath(entry.path, path));
    let focus = atPath(state.focus) ?? entries[0];
    let selected = state.selected ? atPath(state.selected) : undefined;
    if (!focus) {
        container.textContent = allocation && data.totalSamples === 0
            ? "No allocation ticks were captured in this window. Capture completion confirms protocol execution, not representative allocation activity or exact EventPipe arm timing."
            : allocation && validCount(data.totalSamples) && data.totalSamples > 0
                ? `${data.totalSamples} estimated sampled bytes were captured, but no usable stack attribution was returned. The profile is intentionally empty; the bytes were not replaced with zero.`
                : allocation
                    ? "No call tree was returned for this allocation profile. The response does not identify why attribution is unavailable."
                    : "No call tree was returned for this sample. The response does not identify why the profile is unavailable.";
        return;
    }
    const method = (node: CpuTreeNode) => node.frame?.method || "Unknown method";
    const moduleName = (node: CpuTreeNode) => node.frame?.module || "Unknown module";
    const percent = (value: number, total: number) =>
        total > 0 ? (100 * value / total).toFixed(1) + "%" : "Unavailable";
    const captureTotal = data.totalSamples ?? 0;
    const pathKey = (path: number[]) => path.join(".");
    const expanded = new Set(state.expanded ?? [pathKey(focus.path)]);
    const loadedHotpath = new Set<string>();
    const button = (text: string, action: () => void) => {
        const element = document.createElement("button");
        element.type = "button";
        element.textContent = text;
        element.addEventListener("click", action);
        return element;
    };
    const queryButton = (text: string, view: string, methodFilter?: string) => {
        const element = button(text, () => {
            if (element.getAttribute("aria-disabled") !== "true") query(view, methodFilter);
        });
        element.setAttribute("data-profile-query", "");
        const availability = container.closest("[data-profile-query-available]");
        const available = availability?.getAttribute("data-profile-query-available") !== "false";
        element.setAttribute("aria-disabled", String(!available));
        element.title = available ? "" : availability?.getAttribute("data-profile-query-reason") ?? "Capture a retained profile first.";
        return element;
    };
    const text = (parent: HTMLElement, value: string, className?: string) => {
        const element = document.createElement("p");
        element.textContent = value;
        if (className) element.className = className;
        parent.appendChild(element);
        return element;
    };
    const controls = document.createElement("div");
    controls.className = "cpu-profile-controls";
    const treeButton = button("Call tree", () => { state.mode = "tree"; redraw(); });
    const flameButton = button("Flame graph (icicle)", () => { state.mode = "flame"; redraw(); });
    const reset = button("Reset focus", () => {
        state.focus = []; state.selected = undefined; redraw();
    });
    controls.append(treeButton, flameButton, reset);
    const expandHotpath = button("Expand hotpath", () => {
        for (const key of loadedHotpath) expanded.add(key);
        state.expanded = [...expanded];
        state.mode = "tree";
        redraw();
    });
    expandHotpath.title = "Expand only the dominant known loaded branch; never queries or loads omitted descendants.";
    container.appendChild(controls);

    const label = document.createElement("label");
    label.textContent = "Highlight loaded method or module ";
    const search = document.createElement("input");
    search.type = "search";
    search.maxLength = 512;
    search.value = state.search;
    search.setAttribute("aria-label", "Highlight loaded method or module nodes by case-insensitive substring. "
        + "Asterisks and question marks are literal characters.");
    label.appendChild(search);
    container.appendChild(label);
    text(container, "Case-insensitive substring; * and ? are literal. Highlights loaded nodes only and "
        + "does not filter or renormalize totals.", "query-view-provenance");
    const searchStatus = text(container, "", "query-view-provenance");
    searchStatus.setAttribute("role", "status");
    const profileGuidance = document.createElement("section");
    profileGuidance.className = "cpu-profile-guidance";
    profileGuidance.setAttribute("aria-label", "Profile measurement and retention details");
    text(profileGuidance, "Hotpath marks the largest known loaded branch, not a global diagnosis. "
        + "Expansion is local; omitted descendants require an explicit retained-capture query.", "query-view-provenance");
    const retention = text(profileGuidance, retentionText(
        container.closest("[data-profile-retained]")?.getAttribute("data-profile-retained") !== "false"), "query-view-provenance");
    retention.setAttribute("data-profile-retention-note", "");
    text(profileGuidance, (allocation
        ? "Widths show estimated allocation bytes attributed to sampled stacks, not object counts, retained bytes or CPU time. "
        : "Widths show inclusive sample counts, not elapsed time or measured CPU utilization. ")
        + "Search highlights loaded nodes only; it never filters or renormalizes the " + unit + " totals. "
        + "Gray blocks represent self " + unit + " or explicitly labelled omitted / unattributed descendants.", "query-view-provenance");
    if (allocation) {
        const stacked = data.root?.inclusiveSamples ?? 0;
        const coverage = captureTotal === 0
            ? "No allocation ticks were captured in this window. Capture completion confirms protocol execution, not representative allocation activity or exact EventPipe arm timing."
            : stacked === 0
                ? captureTotal + " estimated sampled bytes were captured, but none were attributable to usable stacks. The profile is intentionally empty; captured bytes were not replaced with zero."
                : "Displayed root: " + stacked + " of " + captureTotal + " estimated bytes ("
                    + percent(stacked, captureTotal) + "). Remaining bytes are outside this root or have no captured stack.";
        text(profileGuidance, coverage + " Types and sites are independent aggregates, not a type-to-stack correlation.",
            "query-view-provenance");
    } else {
        const comparisonNote = text(profileGuidance,
            "This CPU profile is temporary. A live baseline/current comparison is available only while "
            + "the owning CLI retains the two most recent compatible CPU handles; durable saved-capture comparison remains separate. See ",
            "query-view-provenance");
        const comparisonLink = document.createElement("a");
        comparisonLink.textContent = "CPU comparison limits";
        comparisonLink.setAttribute("href", "https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/historical-comparisons.md#supported-matrix");
        comparisonLink.setAttribute("target", "_blank");
        comparisonLink.setAttribute("rel", "noopener noreferrer");
        comparisonNote.appendChild(comparisonLink);
    }
    if (data.truncated || data.traversalLimitReached || limited) {
        const warning = text(container, "Partial projection (up to 64 nodes / depth 8). Omitted descendants remain counted in their parent. "
            + "Use Query subtree by method to retrieve another bounded view from the same capture.", "cpu-profile-warning");
        warning.setAttribute("role", "alert");
    }
    const breadcrumb = document.createElement("nav");
    breadcrumb.setAttribute("aria-label", allocation ? "Allocation focus path" : "CPU focus path");
    breadcrumb.className = "cpu-profile-controls";
    const queries = document.createElement("details");
    queries.className = "cpu-profile-queries";
    const querySummary = document.createElement("summary");
    querySummary.textContent = "Retained-capture queries";
    queries.appendChild(querySummary);
    queries.appendChild(queryButton("Full capture tree", "call-tree"));
    const methodQueries = document.createElement("div");
    queries.appendChild(methodQueries);
    text(queries, "Queries match the highest-ranked occurrence containing the method name, not necessarily "
        + "the selected call path. They read the retained capture, without sampling again.", "query-view-provenance");
    const selection = document.createElement("section");
    selection.setAttribute("aria-label", "Local branch actions");
    selection.className = "cpu-profile-selection";
    const selectedName = text(selection, "", "cpu-profile-selected-name");
    selectedName.setAttribute("role", "status");
    const selectedInfo = text(selection, "", "query-view-provenance");
    const selectedSelf = text(selection, "", "query-view-provenance");
    const localActions = document.createElement("div");
    localActions.className = "cpu-profile-controls";
    const focusBranch = button("Focus branch", () => { if (selected) setFocus(selected); });
    localActions.append(focusBranch, expandHotpath);
    selection.appendChild(localActions);
    const visualization = document.createElement("div");
    visualization.className = "cpu-profile-visualization";
    container.appendChild(visualization);
    container.append(breadcrumb, selection, queries, profileGuidance);
    const info = (node: CpuTreeNode) =>
        method(node) + " | " + moduleName(node) + " | inclusive " + node.inclusiveSamples
        + " (" + percent(node.inclusiveSamples, captureTotal) + " of capture; "
        + percent(node.inclusiveSamples, focus.node.inclusiveSamples) + " of focus)"
        + " | self " + node.exclusiveSamples + " " + unit;
    const matches = (node: CpuTreeNode) => state.search.length > 0
        && (method(node) + " " + moduleName(node)).toLowerCase().includes(state.search.toLowerCase());
    const underFocus = (entry: typeof focus) =>
        focus.path.every((part, i) => entry.path[i] === part);
    function select(entry: typeof focus): void {
        selected = entry; state.selected = entry.path; drawSelection(); highlightSelection();
    }
    function setFocus(entry: typeof focus): void {
        expanded.add(pathKey(entry.path));
        state.expanded = [...expanded];
        state.focus = entry.path; state.selected = entry.path; redraw();
        const methodButton = Array.from(visualization.querySelectorAll<HTMLElement>("[data-node-path]"))
            .find(row => row.tagName === "TR" && row.getAttribute("data-node-path") === pathKey(entry.path))
            ?.querySelector<HTMLButtonElement>(".cpu-profile-method");
        (methodButton ?? visualization.querySelector<HTMLElement>("button, summary, [tabindex]"))?.focus();
    }
    function drawSelection(): void {
        selectedName.textContent = selected ? "Selected branch: " + method(selected.node)
            : "Select a row for branch actions.";
        selectedName.title = selected ? method(selected.node) : "";
        selectedInfo.textContent = selected ? info(selected.node) : "";
        const self = selected?.node.selfSamples;
        selectedSelf.textContent = !selected || allocation ? "" : self && validCount(self.runningSamples) && validCount(self.waitingSamples) && validCount(self.unknownSamples)
            ? "Self evidence: " + self.runningSamples + " on-CPU / " + self.waitingSamples
                + " heuristic-wait / " + self.unknownSamples + " unknown."
            : "Self scheduler-state evidence: Unavailable. Sampled stacks alone do not establish on-CPU time.";
        focusBranch.disabled = !selected;
        methodQueries.replaceChildren();
        if (selected) methodQueries.append(
            queryButton("Callers / callees", "caller-callee", method(selected.node)),
            queryButton("Query subtree by method", "call-tree", method(selected.node)));
        else text(methodQueries, "Select a row to query a method.");
    }
    function highlightSelection(): void {
        for (const row of Array.from(visualization.querySelectorAll<HTMLElement>("[data-node-path]"))) {
            const active = !!selected && row.getAttribute("data-node-path") === pathKey(selected.path);
            row.setAttribute("aria-selected", String(active));
        }
    }
    function drawTree(): void {
        const table = document.createElement("table");
        table.className = "cpu-call-tree";
        const caption = document.createElement("caption");
        caption.textContent = "Total includes descendants; Self excludes them. Both are shares of "
            + captureTotal + " captured " + (allocation ? "estimated sampled bytes." : "stack samples.");
        table.appendChild(caption);
        const head = document.createElement("thead");
        const header = document.createElement("tr");
        for (const label of ["Method / call path", "Total %", "Self %"]) {
            const cell = document.createElement("th");
            cell.scope = "col";
            cell.textContent = label;
            header.appendChild(cell);
        }
        head.appendChild(header);
        table.appendChild(head);
        const body = document.createElement("tbody");
        const rows: Array<{ entry: typeof focus; row: HTMLTableRowElement; toggle?: HTMLButtonElement }> = [];
        const update = () => {
            for (const { entry, row, toggle } of rows) {
                row.hidden = false;
                for (let length = focus.path.length; length < entry.path.length; length++) {
                    if (!expanded.has(pathKey(entry.path.slice(0, length))) && state.search.length === 0) row.hidden = true;
                }
                if (toggle) toggle.setAttribute("aria-expanded", String(expanded.has(pathKey(entry.path)) || state.search.length > 0));
            }
        };
        for (const entry of entries.filter(underFocus)) {
            const children = entries.filter(candidate => candidate.parent === entries.indexOf(entry));
            const row = document.createElement("tr");
            row.setAttribute("data-node-path", pathKey(entry.path));
            row.className = (matches(entry.node) ? "cpu-profile-match " : "")
                + (loadedHotpath.has(pathKey(entry.path)) ? "cpu-profile-hotpath" : "");
            const cell = document.createElement("td");
            const name = document.createElement("div");
            name.className = "cpu-profile-row";
            name.style.paddingInlineStart = (entry.path.length - focus.path.length) + "rem";
            let toggle: HTMLButtonElement | undefined;
            if (children.length) {
                toggle = button("", () => {
                    const key = pathKey(entry.path);
                    if (expanded.has(key)) expanded.delete(key); else expanded.add(key);
                    state.expanded = [...expanded];
                    update();
                });
                toggle.className = "cpu-profile-disclosure";
                toggle.setAttribute("aria-label", "Expand or collapse " + method(entry.node));
                name.appendChild(toggle);
            } else {
                const spacer = document.createElement("span");
                spacer.className = "cpu-profile-disclosure-space";
                name.appendChild(spacer);
            }
            const pick = button(method(entry.node), () => select(entry));
            pick.title = info(entry.node);
            pick.className = "cpu-profile-method";
            name.appendChild(pick);
            cell.appendChild(name);
            row.appendChild(cell);
            for (const count of [entry.node.inclusiveSamples, entry.node.exclusiveSamples]) {
                const metric = document.createElement("td");
                metric.className = "cpu-profile-number";
                metric.textContent = percent(count, captureTotal);
                metric.title = count + " " + unit + " / " + captureTotal + " captured " + unit;
                row.appendChild(metric);
            }
            rows.push({ entry, row, toggle });
            body.appendChild(row);
        }
        update();
        table.appendChild(body);
        visualization.appendChild(table);
    }
    function drawFlame(): void {
        function compactMethod(node: CpuTreeNode): string {
            const signature = method(node);
            const name = signature.split("(")[0].trim();
            const separators: number[] = [];
            let genericDepth = 0;
            for (let i = 0; i < name.length; i++) {
                if (name[i] === "<") genericDepth++;
                else if (name[i] === ">") genericDepth--;
                else if (name[i] === "." && genericDepth === 0) separators.push(i);
            }
            return separators.length > 1 ? name.slice(separators[separators.length - 2] + 1) : name;
        }
        function colorIndex(node: CpuTreeNode): number {
            const key = moduleName(node) + "!" + method(node);
            let hash = 0;
            for (let i = 0; i < key.length; i++) hash = (hash * 31 + key.charCodeAt(i)) | 0;
            return (hash >>> 0) % 6;
        }
        const svgNs = "http://www.w3.org/2000/svg";
        const svg = document.createElementNS(svgNs, "svg");
        const visible = entries.filter(underFocus);
        const maxDepth = Math.max(...visible.map(entry => entry.path.length - focus.path.length));
        const height = (maxDepth + 2) * 32;
        svg.setAttribute("viewBox", "0 0 1000 " + height);
        svg.setAttribute("width", "100%");
        svg.setAttribute("height", String(height));
        svg.setAttribute("role", "group");
        svg.setAttribute("aria-label", "Sampled call stacks; click or press Enter to focus a branch");
        svg.classList.add("cpu-flame");
        text(visualization, "Blocks show short method names and share of focus. Hover for the full signature and metrics; "
            + "focus a block for details. Colors distinguish methods, not severity. Yellow marks search matches.",
        "query-view-provenance");
        const total = focus.node.inclusiveSamples;
        if (total === 0) {
            text(visualization, "No inclusive " + unit + " are available for this focus.");
            return;
        }
        function rectangle(x: number, depth: number, width: number, caption: string, entry?: typeof focus): void {
            if (width <= 0) return;
            const group = document.createElementNS(svgNs, "g");
            const rect = document.createElementNS(svgNs, "rect");
            rect.setAttribute("x", String(x));
            rect.setAttribute("y", String(depth * 32));
            rect.setAttribute("width", String(width));
            rect.setAttribute("height", "30");
            rect.setAttribute("class", entry ? (matches(entry.node) ? "cpu-flame-match" : "cpu-flame-node") : "cpu-flame-gap");
            if (entry) rect.setAttribute("data-color", String(colorIndex(entry.node)));
            group.appendChild(rect);
            const title = document.createElementNS(svgNs, "title");
            title.textContent = caption;
            group.appendChild(title);
            if (width >= 45) {
                const clip = document.createElementNS(svgNs, "svg");
                clip.setAttribute("x", String(x));
                clip.setAttribute("y", String(depth * 32));
                clip.setAttribute("width", String(width));
                clip.setAttribute("height", "30");
                clip.setAttribute("overflow", "hidden");
                const name = document.createElementNS(svgNs, "text");
                name.setAttribute("x", "6");
                name.setAttribute("y", "20");
                const label = entry ? compactMethod(entry.node) + "  " + percent(entry.node.inclusiveSamples, total)
                    : caption.startsWith("Self ") ? "Self" : "Omitted / unattributed";
                const chars = Math.max(1, Math.floor((width - 12) / 8));
                name.textContent = label.length > chars ? label.slice(0, chars - 1) + "\u2026" : label;
                clip.appendChild(name);
                group.appendChild(clip);
            }
            if (entry) {
                group.setAttribute("tabindex", "0");
                group.setAttribute("role", "button");
                group.setAttribute("aria-label", caption);
                group.addEventListener("click", () => setFocus(entry));
                group.addEventListener("keydown", event => {
                    if (event.key === "Enter" || event.key === " ") {
                        event.preventDefault(); setFocus(entry);
                    } else if (event.key === "Escape") {
                        event.preventDefault();
                        setFocus(entries[focus.parent ?? 0]);
                    }
                });
            }
            svg.appendChild(group);
        }
        function draw(entry: typeof focus, x: number, depth: number, width: number): void {
            rectangle(x, depth, width, info(entry.node), entry);
            const children = entries.filter(candidate => candidate.parent === entries.indexOf(entry));
            let remaining = entry.node.inclusiveSamples;
            const scale = remaining > 0 ? width / remaining : 0;
            let offset = x;
            for (const child of children) {
                const count = Math.min(remaining, child.node.inclusiveSamples);
                const childWidth = scale * count;
                draw(child, offset, depth + 1, childWidth);
                offset += childWidth;
                remaining -= count;
            }
            const self = Math.min(remaining, entry.node.exclusiveSamples);
            if (self > 0) {
                const selfWidth = scale * self;
                rectangle(offset, depth + 1, selfWidth, allocation ? "Self estimated bytes: " + self : "Self samples: " + self);
                offset += selfWidth; remaining -= self;
            }
            if (remaining > 0) rectangle(offset, depth + 1, scale * remaining,
                "Omitted / unattributed descendants: " + remaining + " " + unit);
        }
        draw(focus, 0, 0, 1000);
        visualization.appendChild(svg);
    }
    function redraw(): void {
        focus = atPath(state.focus) ?? entries[0];
        selected = state.selected ? atPath(state.selected) : undefined;
        loadedHotpath.clear();
        let branch: typeof focus | undefined = focus;
        while (branch) {
            loadedHotpath.add(pathKey(branch.path));
            const index = entries.indexOf(branch);
            const children = entries.filter(entry => entry.parent === index);
            const largest: (typeof entries)[number] | undefined = children.reduce<(typeof entries)[number] | undefined>((best, child) =>
                !best || child.node.inclusiveSamples > best.node.inclusiveSamples ? child : best, undefined);
            const omitted: number = branch.node.inclusiveSamples - branch.node.exclusiveSamples
                - children.reduce((sum, child) => sum + child.node.inclusiveSamples, 0);
            // An omitted branch may dominate; do not descend past unknown evidence.
            branch = largest && largest.node.inclusiveSamples > 0 && largest.node.inclusiveSamples > omitted ? largest : undefined;
        }
        treeButton.setAttribute("aria-pressed", String(state.mode !== "flame"));
        flameButton.setAttribute("aria-pressed", String(state.mode === "flame"));
        reset.disabled = focus.path.length === 0;
        breadcrumb.replaceChildren();
        for (let length = 0; length <= focus.path.length; length++) {
            const ancestor = atPath(focus.path.slice(0, length));
            if (ancestor) breadcrumb.appendChild(button(method(ancestor.node), () => setFocus(ancestor)));
        }
        searchStatus.textContent = state.search.length
            ? entries.filter(entry => underFocus(entry) && matches(entry.node)).length
                + " matching loaded node(s) in this focus. No nodes hidden."
                + (entries.some(entry => underFocus(entry) && matches(entry.node))
                    ? "" : " No match in this loaded projection; omitted nodes may still contain matches.")
            : "Showing " + entries.filter(underFocus).length + " loaded node(s).";
        drawSelection();
        visualization.replaceChildren();
        if (state.mode === "flame") drawFlame(); else drawTree();
        highlightSelection();
    }
    search.addEventListener("input", () => { state.search = search.value.slice(0, 512); redraw(); });
    redraw();
}

export const CPU_PROFILE_SCRIPT = `const retentionText = ${profileRetentionText.toString()};\n${renderCpuProfile.toString()}`;

export const CPU_PROFILE_STYLES = `
    .cpu-profile-controls { display:flex; flex-wrap:wrap; gap:.4rem; margin:.6rem 0; }
    .cpu-profile-controls button[aria-pressed="true"] { outline:2px solid var(--vscode-focusBorder); }
    .cpu-profile-selection { border:1px solid var(--vscode-panel-border); padding:.5rem; margin:.6rem 0 0; overflow-wrap:anywhere; }
    .cpu-profile-selected-name { white-space:nowrap; overflow:hidden; text-overflow:ellipsis; font-weight:600; }
    .cpu-profile-queries { margin:.6rem 0; }
    .cpu-profile-selection button { margin:.2rem; }
    .cpu-profile-row { display:flex; gap:.35rem; align-items:center; min-width:0; }
    .cpu-profile-method { flex:1; min-width:0; text-align:left; background:transparent; color:var(--vscode-textLink-foreground); white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
    .cpu-profile-match { outline:2px solid var(--vscode-editor-findMatchHighlightBorder, var(--vscode-focusBorder)); }
    .cpu-profile-warning { border:1px solid var(--vscode-editorWarning-foreground); padding:.5rem; font-weight:600; }
    .cpu-call-tree { width:100%; min-width:34rem; table-layout:fixed; border-collapse:collapse; }
    .cpu-call-tree caption { text-align:left; margin:.5rem 0; }
    .cpu-call-tree th:first-child { width:auto; text-align:left; }
    .cpu-call-tree th:not(:first-child), .cpu-profile-number { width:4.5rem; text-align:right; white-space:nowrap; font-variant-numeric:tabular-nums; }
    .cpu-call-tree td { padding:.25rem; border-bottom:1px solid var(--vscode-panel-border); }
    .cpu-profile-disclosure, .cpu-profile-disclosure-space { flex:none; width:1.6rem; }
    .cpu-profile-disclosure { padding:.2rem; background:transparent; color:var(--vscode-foreground); }
    .cpu-profile-disclosure::before { content:'\\25B8'; }
    .cpu-profile-disclosure[aria-expanded="true"]::before { content:'\\25BE'; }
    .cpu-profile-hotpath .cpu-profile-method { text-decoration:underline; text-decoration-color:var(--vscode-focusBorder); }
    .cpu-call-tree tr[aria-selected="true"] { background:var(--vscode-list-activeSelectionBackground); color:var(--vscode-list-activeSelectionForeground); outline:1px solid var(--vscode-contrastActiveBorder, transparent); }
    .cpu-call-tree tr[aria-selected="true"] button { color:inherit; }
    .cpu-call-tree tr:focus-visible { outline:2px solid var(--vscode-focusBorder); outline-offset:-2px; }
    .cpu-flame { display:block; width:100%; height:auto; min-width:1000px; }
    .cpu-profile-visualization { overflow:auto; max-height:65vh; }
    .cpu-flame-node { fill:#a8ccef; stroke:#18212b; }
    .cpu-flame-node[data-color="1"] { fill:#a8d8bd; }
    .cpu-flame-node[data-color="2"] { fill:#e8c49c; }
    .cpu-flame-node[data-color="3"] { fill:#cbbce8; }
    .cpu-flame-node[data-color="4"] { fill:#e7b5bf; }
    .cpu-flame-node[data-color="5"] { fill:#a8d8dc; }
    .cpu-flame-match { fill:#ffe083; stroke:#18212b; stroke-width:2; }
    .cpu-flame-gap { fill:#c6cbd2; stroke:#18212b; }
    .cpu-flame text { fill:#18212b; font:13px var(--vscode-editor-font-family, monospace); pointer-events:none; }
    .cpu-flame g[role="button"] { cursor:pointer; }
    .cpu-flame g[role="button"]:hover rect { stroke:#18212b; stroke-width:3; }
    .cpu-flame g:focus rect { stroke:var(--vscode-focusBorder); stroke-width:3; }
    body.vscode-high-contrast .cpu-flame rect, body.vscode-high-contrast-light .cpu-flame rect {
        fill:var(--vscode-editor-background); stroke:var(--vscode-editor-foreground);
    }
    body.vscode-high-contrast .cpu-flame text, body.vscode-high-contrast-light .cpu-flame text { fill:var(--vscode-editor-foreground); }
    body.vscode-high-contrast .cpu-flame-match, body.vscode-high-contrast-light .cpu-flame-match { stroke:var(--vscode-focusBorder); stroke-width:3; }
    @media (forced-colors: active) {
        .cpu-flame rect { fill:Canvas; stroke:CanvasText; }
        .cpu-flame text { fill:CanvasText; }
        .cpu-flame g:focus rect, .cpu-flame .cpu-flame-match { stroke:Highlight; stroke-width:3; }
    }
`;
