function enhanceEvidenceTable(table: HTMLTableElement | null): void {
    if (!table) return;
    if (table.dataset.navigation === "ready") return;
    const body = table.querySelector("tbody");
    const headers = Array.from(table.querySelectorAll("th"));
    if (!body || !headers.length) return;
    table.dataset.navigation = "ready";
    const rows = Array.from(body.children) as HTMLTableRowElement[];
    const stateFilter = table.dataset.rowFilterSelectId
        ? document.getElementById(table.dataset.rowFilterSelectId) as HTMLSelectElement | null
        : null;
    const controls = document.createElement("div");
    controls.className = "evidence-controls";
    const label = document.createElement("label");
    label.textContent = "Filter returned rows ";
    const input = document.createElement("input");
    input.type = "search";
    input.value = "";
    input.maxLength = 512;
    label.appendChild(input);
    const status = document.createElement("span");
    status.setAttribute("role", "status");
    controls.append(label, status);
    table.parentElement?.insertBefore(controls, table);
    const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: "base" });
    let sortIndex = -1;
    let descending = false;
    function draw(): void {
        const term = input.value.toLowerCase();
        const stateRows = stateFilter && stateFilter.value !== "all"
            ? rows.filter(row => row.dataset.rowFilterState === stateFilter.value)
            : rows;
        const visible = stateRows.filter(row => ((row.textContent ?? "") + " " + (row.dataset.searchText ?? "")).toLowerCase().includes(term));
        if (sortIndex >= 0) visible.sort((a, b) => {
            const leftCell = a.children[sortIndex];
            const rightCell = b.children[sortIndex];
            const left = leftCell?.getAttribute("data-sort-value") ?? leftCell?.textContent ?? "";
            const right = rightCell?.getAttribute("data-sort-value") ?? rightCell?.textContent ?? "";
            const timestampPrefix = "timestamp:";
            if (left.startsWith(timestampPrefix) && right.startsWith(timestampPrefix)) {
                const leftTimestamp = BigInt(left.slice(timestampPrefix.length));
                const rightTimestamp = BigInt(right.slice(timestampPrefix.length));
                return descending
                    ? Number(rightTimestamp > leftTimestamp) - Number(rightTimestamp < leftTimestamp)
                    : Number(leftTimestamp > rightTimestamp) - Number(leftTimestamp < rightTimestamp);
            }
            const decimal = /^-?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i;
            const result = decimal.test(left) && decimal.test(right) && Number.isFinite(Number(left)) && Number.isFinite(Number(right))
                ? Number(left) - Number(right) : collator.compare(left, right);
            return descending ? -result : result;
        });
        body!.replaceChildren(...visible);
        status.textContent = visible.length + " of " + stateRows.length + " returned rows. Not a full-capture filter.";
    }
    headers.forEach((header, index) => {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "evidence-sort";
        button.textContent = header.textContent;
        button.title = "Sort by " + header.textContent + " (ascending / descending)";
        button.setAttribute("aria-label", "Sort by " + header.textContent);
        button.addEventListener("click", () => {
            descending = sortIndex === index ? !descending : false;
            sortIndex = index;
            headers.forEach(item => item.removeAttribute("aria-sort"));
            header.setAttribute("aria-sort", descending ? "descending" : "ascending");
            draw();
        });
        header.replaceChildren(button);
    });
    input.addEventListener("input", draw);
    stateFilter?.addEventListener("change", draw);
    draw();
}

function resetEvidenceTable(table: HTMLTableElement | null): void {
    if (!table) return;
    if (table.previousElementSibling?.classList.contains("evidence-controls")) table.previousElementSibling.remove();
    delete table.dataset.navigation;
}

function enhanceEvidenceTables(parent: HTMLElement): void {
    for (const table of Array.from(parent.querySelectorAll<HTMLTableElement>("table"))) enhanceEvidenceTable(table);
}

function renderExceptionNavigation(parent: HTMLElement, events: Array<Record<string, unknown>>): void {
    const section = document.createElement("section");
    section.className = "exception-explorer";
    const title = document.createElement("h4");
    title.textContent = "Explore retained occurrences";
    const help = document.createElement("p");
    help.textContent = "Filters below apply only to retained events. All-observed type counts above do not change.";
    const type = document.createElement("select");
    type.setAttribute("aria-label", "Exception type");
    const message = document.createElement("select");
    message.setAttribute("aria-label", "Exception message");
    const occurrences = document.createElement("div");
    const detail = document.createElement("section");
    detail.className = "evidence-selection";
    detail.tabIndex = -1;
    detail.setAttribute("aria-label", "Selected exception occurrence");
    const status = document.createElement("p");
    status.setAttribute("role", "status");
    const reset = document.createElement("button");
    reset.type = "button";
    reset.textContent = "Reset exception filters";
    const value = (event: Record<string, unknown>, field: string) =>
        typeof event[field] === "string" ? String(event[field]) : "(unavailable)";
    function options(select: HTMLSelectElement, values: string[], all: string): void {
        select.replaceChildren();
        const option = document.createElement("option");
        option.value = "";
        option.textContent = all;
        select.appendChild(option);
        for (const entry of [...new Set(values)]) {
            const option = document.createElement("option");
            option.value = entry;
            option.textContent = entry === "" ? "(empty message)" : entry;
            // Empty messages need an unambiguous value distinct from the All sentinel.
            option.value = JSON.stringify(entry);
            select.appendChild(option);
        }
        select.value = "";
    }
    const selectedValue = (select: HTMLSelectElement, entry: string) =>
        select.value === "" || select.value === JSON.stringify(entry);
    function draw(): void {
        occurrences.replaceChildren();
        detail.replaceChildren();
        const filtered = events.filter(event => selectedValue(type, value(event, "exceptionType"))
            && selectedValue(message, value(event, "exceptionMessage")));
        status.textContent = filtered.length + " of " + events.length + " retained occurrences.";
        for (const event of filtered) {
            const button = document.createElement("button");
            button.type = "button";
            button.textContent = value(event, "timestamp") + " | " + value(event, "exceptionType")
                + " | thread " + String(event.threadId ?? "Unavailable");
            button.addEventListener("click", () => {
                detail.replaceChildren();
                const description = document.createElement("p");
                description.textContent = button.textContent + "\n" + value(event, "exceptionMessage");
                const stack = document.createElement("pre");
                stack.textContent = Array.isArray(event.managedStack) && event.managedStack.length
                    ? event.managedStack.map(String).join("\n") : "No managed stack was captured for this event.";
                detail.append(description, stack);
                detail.focus({ preventScroll: true });
                detail.scrollIntoView({ block: "nearest" });
            });
            occurrences.appendChild(button);
        }
    }
    function messages(): void {
        options(message, events.filter(event => selectedValue(type, value(event, "exceptionType")))
            .map(event => value(event, "exceptionMessage")), "All retained messages");
    }
    options(type, events.map(event => value(event, "exceptionType")), "All retained types");
    messages();
    type.addEventListener("change", () => { messages(); draw(); });
    message.addEventListener("change", draw);
    reset.addEventListener("click", () => { type.value = ""; messages(); draw(); });
    section.append(title, help, type, message, reset, status, occurrences, detail);
    parent.appendChild(section);
    draw();
}

export const EVIDENCE_NAVIGATION_SCRIPT = enhanceEvidenceTable.toString() + "\n"
    + resetEvidenceTable.toString() + "\n" + enhanceEvidenceTables.toString() + "\n" + renderExceptionNavigation.toString();

export const EVIDENCE_NAVIGATION_STYLES = `
    th > button.evidence-sort {
        display:inline-flex; align-items:center; gap:.4rem; width:100%; text-align:left;
        color:inherit; background:transparent; border:0; border-radius:0; padding:.15rem 0;
        font:inherit; cursor:pointer;
    }
    th > button.evidence-sort::after { content:"\\2195"; opacity:.5; font-size:.85em; }
    th[aria-sort="ascending"] > button.evidence-sort::after { content:"\\2191"; opacity:1; }
    th[aria-sort="descending"] > button.evidence-sort::after { content:"\\2193"; opacity:1; }
    th[aria-sort] { color:var(--vscode-foreground); }
    th > button.evidence-sort:hover { background:transparent; color:var(--vscode-textLink-foreground); }
    th > button.evidence-sort:focus-visible { outline:1px solid var(--vscode-focusBorder); outline-offset:3px; }
    .evidence-controls { display:flex; flex-wrap:wrap; gap:.6rem; margin:.5rem 0; align-items:center; }
    .evidence-controls label { display:flex; flex-wrap:wrap; gap:.35rem; min-width:0; max-width:100%; }
    .evidence-controls input { box-sizing:border-box; min-width:0; max-width:100%; }
    .evidence-controls span { color:var(--vscode-descriptionForeground); font-size:.85rem; }
    .evidence-selection { border-left:3px solid var(--vscode-focusBorder); padding:.5rem; overflow-wrap:anywhere; max-height:45vh; overflow:auto; }
    button.heap-type-link {
        margin-left:.5rem; padding:0; border:0; border-radius:0;
        background:transparent; color:var(--vscode-textLink-foreground);
        font:inherit; font-size:.85em; cursor:pointer; text-decoration:underline;
        text-underline-offset:2px;
    }
    button.heap-type-link { margin:0; font-size:inherit; text-align:left; overflow-wrap:anywhere; }
    button.heap-type-link:hover { background:transparent; color:var(--vscode-textLink-activeForeground, var(--vscode-textLink-foreground)); }
    button.heap-type-link:focus-visible { outline:1px solid var(--vscode-focusBorder); outline-offset:3px; }
    .exception-explorer button, .exception-explorer select { margin:.25rem; }
    .exception-explorer pre { white-space:pre-wrap; overflow-wrap:anywhere; }
`;
