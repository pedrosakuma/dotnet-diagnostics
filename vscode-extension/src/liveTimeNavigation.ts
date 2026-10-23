interface LiveTimePoint { receivedAtMs: number; timestamp: string; value: number }
interface LiveGcRow { element: HTMLElement; receivedAtMs: number }

function installLiveTimeNavigation(
    canvas: HTMLCanvasElement,
    getPoints: () => LiveTimePoint[],
    gcRows: LiveGcRow[],
    refresh: () => void,
    formatValue: (value: number) => string = String,
): { filter: (points: LiveTimePoint[]) => LiveTimePoint[]; update: () => void; selected: () => LiveTimePoint | undefined } {
    let range: { start: number; end: number } | undefined;
    let inspected: LiveTimePoint | undefined;
    const controls = document.createElement("section");
    controls.className = "monitoring-time-controls";
    controls.setAttribute("aria-label", "Live time selection");
    const explanation = document.createElement("details");
    const explanationTitle = document.createElement("summary");
    explanationTitle.textContent = "Receipt-time selection";
    const label = document.createElement("p");
    label.textContent = "Explore retained live data by receipt time (not target event time). Selection does not change collection or retention.";
    const windowLabel = document.createElement("label");
    windowLabel.textContent = "Window ";
    const windowSelect = document.createElement("select");
    windowSelect.setAttribute("aria-label", "Visible receipt-time window");
    for (const [value, caption] of [["all", "All retained"], ["30", "30 seconds"], ["60", "1 minute"], ["300", "5 minutes"]]) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = caption;
        windowSelect.appendChild(option);
    }
    windowSelect.value = "all";
    windowLabel.appendChild(windowSelect);
    const start = document.createElement("input");
    const end = document.createElement("input");
    const startValue = document.createElement("output");
    const endValue = document.createElement("output");
    const startLabel = document.createElement("label");
    const endLabel = document.createElement("label");
    const startCaption = document.createElement("span");
    const endCaption = document.createElement("span");
    startCaption.textContent = "Start of selected receipt time";
    endCaption.textContent = "End of selected receipt time";
    startLabel.append(startCaption, start, startValue);
    endLabel.append(endCaption, end, endValue);
    for (const input of [start, end]) {
        input.type = "range";
        input.min = "0";
        input.max = "100";
        input.step = "1";
    }
    start.value = "0";
    end.value = "100";
    start.setAttribute("aria-label", "Selection start percent of retained receipt time");
    end.setAttribute("aria-label", "Selection end percent of retained receipt time");
    const reset = document.createElement("button");
    reset.type = "button";
    reset.textContent = "Pause following";
    const status = document.createElement("p");
    status.setAttribute("role", "status");
    const detail = document.createElement("p");
    detail.setAttribute("aria-label", "Selected counter observation");
    detail.setAttribute("role", "status");
    detail.setAttribute("aria-atomic", "true");
    const formatReceiptTime = (milliseconds: number | undefined): string =>
        milliseconds !== undefined && Number.isFinite(milliseconds)
            ? new Date(milliseconds).toISOString() : "No retained receipt time";
    const bounds = (): { start: number; end: number } | undefined => {
        const times = [...getPoints().map(point => point.receivedAtMs), ...gcRows.map(row => row.receivedAtMs)];
        if (!times.length) return undefined;
        return { start: Math.min(...times), end: Math.max(...times) };
    };
    const selection = () => {
        if (range) return range;
        const retained = bounds();
        return retained && { start: windowSelect.value === "all" ? retained.start
            : Math.max(retained.start, retained.end - Number(windowSelect.value) * 1000), end: retained.end };
    };
    const filter = (points: LiveTimePoint[]) => {
        const selected = selection();
        return selected ? points.filter(point => point.receivedAtMs >= selected.start && point.receivedAtMs <= selected.end) : points;
    };
    function update(): void {
        const points = getPoints();
        const empty = !points.length && !gcRows.length;
        reset.disabled = empty && !range;
        reset.setAttribute("aria-disabled", String(reset.disabled));
        start.disabled = end.disabled = empty;
        reset.textContent = range ? "Follow latest" : "Pause following";
        reset.title = empty ? "Wait for retained counter observations or GC pauses." : "";
        const visible = filter(points);
        if (inspected && !visible.includes(inspected)) {
            inspected = undefined;
            detail.textContent = "The selected observation is no longer in this metric's retained selection.";
        }
        const selected = selection();
        for (const row of gcRows) row.element.hidden = !!selected
            && (row.receivedAtMs < selected.start || row.receivedAtMs > selected.end);
        const retained = bounds();
        const selectedStart = selected?.start;
        const selectedEnd = selected?.end;
        const startTime = formatReceiptTime(selectedStart);
        const endTime = formatReceiptTime(selectedEnd);
        startValue.textContent = startTime;
        endValue.textContent = endTime;
        const duration = retained ? retained.end - retained.start : 0;
        const percent = (time: number | undefined, fallback: number) => duration > 0 && time !== undefined && retained
            ? String(Math.round(Math.max(0, Math.min(100, (time - retained.start) / duration * 100)))) : String(fallback);
        start.value = percent(selectedStart, 0);
        end.value = percent(selectedEnd, 100);
        start.setAttribute("aria-valuetext", `${start.value}% of retained receipt time; selected start ${startTime}`);
        end.setAttribute("aria-valuetext", `${end.value}% of retained receipt time; selected end ${endTime}`);
        status.textContent = !range ? "" : "Paused · " + visible.length + " of " + points.length + " observations";
        status.title = `Selected receipt-time bounds: ${startTime} to ${endTime}.`;
        if (range) status.textContent += !retained || range.start < retained.start
            ? " Selected data has expired from retention; empty is not zero."
            : "";
    }
    function select(): void {
        const retained = bounds();
        if (!retained) { status.textContent = "No retained observations to select."; return; }
        const first = retained.start;
        const duration = retained.end - first;
        const low = Math.min(Number(start.value), Number(end.value));
        const high = Math.max(Number(start.value), Number(end.value));
        range = { start: first + duration * low / 100, end: first + duration * high / 100 };
        detail.textContent = "";
        inspected = undefined;
        update();
        refresh();
    }
    start.addEventListener("input", select);
    end.addEventListener("input", select);
    reset.addEventListener("click", () => {
        range = range ? undefined : selection();
        inspected = undefined;
        detail.textContent = ""; update(); refresh();
    });
    windowSelect.addEventListener("change", () => {
        // A paused window stays fixed at its selected end, not at the latest arrival.
        if (range) {
            const retained = bounds();
            range.start = windowSelect.value === "all" ? (retained?.start ?? range.start)
                : range.end - Number(windowSelect.value) * 1000;
        }
        inspected = undefined; detail.textContent = ""; update(); refresh();
    });
    const inspect = (fraction: number) => {
        const points = filter(getPoints());
        if (!points.length) { detail.textContent = "No retained observation at this selection."; return; }
        const first = points[0].receivedAtMs;
        const last = points[points.length - 1].receivedAtMs;
        const target = first + Math.max(0, Math.min(1, fraction)) * (last - first);
        const nearest = points.reduce((best, point) =>
            Math.abs(point.receivedAtMs - target) < Math.abs(best.receivedAtMs - target) ? point : best);
        inspected = nearest;
        detail.textContent = "Observed value: " + formatValue(nearest.value) + " | target timestamp: " + nearest.timestamp
            + ". Nearest retained observation; no interpolation.";
        refresh();
    };
    let position = 1;
    canvas.tabIndex = 0;
    canvas.setAttribute("aria-label", "Counter observations. Click to inspect; Left/Right to move, Home/End to jump.");
    canvas.addEventListener("click", event => {
        const width = canvas.getBoundingClientRect().width;
        position = Math.max(0, Math.min(1, (event.offsetX - 44) / Math.max(1, width - 56)));
        inspect(position);
    });
    canvas.addEventListener("keydown", event => {
        if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
        event.preventDefault();
        position = event.key === "Home" ? 0 : event.key === "End" ? 1
            : Math.max(0, Math.min(1, position + (event.key === "ArrowLeft" ? -0.05 : 0.05)));
        inspect(position);
    });
    explanation.append(explanationTitle, label, startLabel, endLabel);
    controls.append(windowLabel, reset, status);
    canvas.parentElement?.insertBefore(controls, canvas);
    const details = document.createElement("section");
    details.className = "monitoring-time-details";
    details.append(detail, explanation);
    canvas.parentElement?.insertBefore(details, canvas.nextSibling);
    update();
    return { filter, update, selected: () => inspected };
}

export const LIVE_TIME_NAVIGATION_SCRIPT = installLiveTimeNavigation.toString();
