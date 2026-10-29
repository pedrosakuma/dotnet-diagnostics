function updateEvidenceViewSwitcher(container: HTMLElement): void {
    const panels = Array.from(container.children).filter((child): child is HTMLElement =>
        child.tagName !== undefined && (child.classList.contains("query-view") || child.id === "threadExplorer"));
    if (!panels.length) return;
    let controls = Array.from(container.children).find(child => child.classList.contains("evidence-view-switcher")) as HTMLElement | undefined;
    if (!controls) {
        controls = document.createElement("nav");
        controls.className = "evidence-view-switcher";
        controls.setAttribute("aria-label", "Evidence views");
        container.appendChild(controls);
    }
    const bar = controls;
    const tabs = container.id === "threadQueryViews";
    const compact = container.id === "heapQueryViews";
    const compactSelectorFocused = compact && bar.querySelector("select") === document.activeElement;
    const active = bar.dataset.active;
    const focusedPanel = Array.from(bar.querySelectorAll<HTMLButtonElement>("button"))
        .find(button => button === document.activeElement)?.dataset.panel;
    const selected = panels.find(panel => panel.id === active) ?? panels[0];
    if (!active && selected.tagName.toLowerCase() === "details" && !selected.id.endsWith("-deadlocks"))
        (selected as HTMLDetailsElement).open = true;
    bar.dataset.active = selected.id;
    const select = (panel: HTMLElement) => {
        bar.dataset.active = panel.id;
        for (const candidate of panels) {
            candidate.hidden = candidate !== panel;
            if (candidate === panel && candidate.tagName.toLowerCase() === "details")
                (candidate as HTMLDetailsElement).open = true;
        }
        for (const button of Array.from(bar.querySelectorAll<HTMLButtonElement>("button"))) {
            const active = button.dataset.panel === panel.id;
            button.setAttribute(tabs ? "aria-selected" : "aria-pressed", String(active));
            if (tabs) button.tabIndex = active ? 0 : -1;
        }
        const selector = bar.querySelector<HTMLSelectElement>("select");
        if (selector) selector.value = panel.id;
    };
    bar.replaceChildren();
    const tablist = document.createElement("div");
    if (tabs) {
        tablist.className = "evidence-tabs";
        tablist.setAttribute("role", "tablist");
        tablist.setAttribute("aria-label", "Thread evidence views");
        bar.appendChild(tablist);
    }
    const buttons: HTMLButtonElement[] = [];
    const status = document.createElement("p");
    status.setAttribute("role", "status");
    const errors = panels.filter(panel => panel.classList.contains("query-view-error"));
    const candidates = panels.filter(panel => panel.classList.contains("query-view-deadlocks"));
    status.id = container.id + "ViewStatus";
    status.textContent = panels.length + " available views. "
        + (errors.length ? errors.length + " unavailable view(s); select the labelled view for its error. " : "")
        + (candidates.length ? "Inferred deadlock candidates reported; inspect Deadlock candidates. " : "")
        + "Switching views preserves filters and selection; no new read.";
    if (compact) {
        const label = document.createElement("label");
        const selector = document.createElement("select");
        selector.id = container.id + "Selector";
        selector.setAttribute("aria-describedby", status.id);
        label.htmlFor = selector.id;
        label.textContent = "Evidence view";
        for (const panel of panels) {
            const option = document.createElement("option");
            option.value = panel.id;
            option.textContent = panel.querySelector("summary")?.textContent ?? "Evidence";
            selector.appendChild(option);
        }
        selector.value = selected.id;
        selector.addEventListener("change", () => {
            const panel = panels.find(candidate => candidate.id === selector.value);
            if (panel) select(panel);
        });
        bar.append(label, selector, status);
        if (compactSelectorFocused) selector.focus({ preventScroll: true });
        for (const panel of panels) panel.hidden = panel !== selected;
        return;
    }
    for (const panel of panels) {
        const button = document.createElement("button");
        button.type = "button";
        button.dataset.panel = panel.id;
        const label = panel.id === "threadExplorer" ? "Thread explorer" : panel.querySelector("summary")?.textContent ?? "Evidence";
        button.textContent = label + (panel.classList.contains("query-view-deadlocks") ? " (warning)" : "");
        button.setAttribute("aria-controls", panel.id);
        if (tabs) {
            button.id = "tab-" + panel.id;
            button.setAttribute("role", "tab");
            panel.setAttribute("role", "tabpanel");
            panel.setAttribute("aria-labelledby", button.id);
            button.addEventListener("keydown", event => {
                const index = buttons.indexOf(button);
                const next = event.key === "ArrowRight" ? (index + 1) % buttons.length
                    : event.key === "ArrowLeft" ? (index + buttons.length - 1) % buttons.length
                    : event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1 : -1;
                if (next < 0) return;
                event.preventDefault();
                select(panels[next]);
                buttons[next].focus({ preventScroll: true });
            });
        }
        button.setAttribute(tabs ? "aria-selected" : "aria-pressed", String(panel === selected));
        if (tabs) button.tabIndex = panel === selected ? 0 : -1;
        button.addEventListener("click", () => select(panel));
        buttons.push(button);
        (tabs ? tablist : bar).appendChild(button);
        if (focusedPanel === panel.id) button.focus({ preventScroll: true });
    }
    bar.appendChild(status);
    // Do not auto-open a newly arrived result or steal the current selection.
    for (const panel of panels) panel.hidden = panel !== selected;
}

export const EVIDENCE_VIEW_SWITCHER_SCRIPT = updateEvidenceViewSwitcher.toString();
export const EVIDENCE_VIEW_SWITCHER_STYLES = `
    #heapQueryViews, #threadQueryViews { display:flex; flex-direction:column; }
    .evidence-view-switcher { order:-1; display:flex; flex-wrap:wrap; gap:.3rem; margin:.5rem 0; }
    .evidence-view-switcher p { flex-basis:100%; color:var(--vscode-descriptionForeground); font-size:.85rem; }
    #heapQueryViews .evidence-view-switcher { display:grid; grid-template-columns:max-content minmax(0, 28rem); align-items:center; gap:.35rem .6rem; }
    #heapQueryViews .evidence-view-switcher label { font-weight:600; }
    #heapQueryViews .evidence-view-switcher select { width:100%; min-width:0; }
    #heapQueryViews .evidence-view-switcher p { grid-column:1 / -1; margin:.15rem 0 0; }
    .evidence-view-switcher button[aria-pressed="true"] { outline:2px solid var(--vscode-focusBorder); }
    .evidence-tabs, .thread-mode-tabs { display:flex; gap:.2rem; overflow-x:auto; max-width:100%; border-bottom:1px solid var(--vscode-panel-border); }
    .evidence-tabs { flex:1 1 100%; min-width:0; }
    .evidence-tabs button, .thread-mode-tabs button {
        flex:0 0 auto; background:transparent; color:var(--vscode-foreground); border:0;
        border-radius:0; border-bottom:2px solid transparent; padding:.5rem .65rem; font:inherit;
    }
    .evidence-tabs button[aria-selected="true"], .thread-mode-tabs button[aria-selected="true"] {
        color:var(--vscode-textLink-foreground); border-bottom-color:var(--vscode-focusBorder);
    }
    .evidence-tabs button:hover, .thread-mode-tabs button:hover { background:var(--vscode-toolbar-hoverBackground); }
    .evidence-tabs button:focus-visible, .thread-mode-tabs button:focus-visible { outline:1px solid var(--vscode-focusBorder); outline-offset:-2px; }
    .thread-mode-tabs { margin:.6rem 0; }
    #heapQueryViews > [hidden], #threadQueryViews > [hidden], .diagnostic-table-view[hidden] { display:none; }
    @media(max-width:600px) {
        #heapQueryViews .evidence-view-switcher { grid-template-columns:minmax(0, 1fr); }
    }
`;
