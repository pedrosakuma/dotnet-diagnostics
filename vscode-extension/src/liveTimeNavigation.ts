interface LiveTimePoint { receivedAtMs: number; timestamp: string; value: number }
interface LiveGcRow { element: HTMLElement; receivedAtMs: number }

function installLiveTimeNavigation(
    canvas: HTMLCanvasElement,
    getPoints: () => LiveTimePoint[],
    gcRows: LiveGcRow[],
    refresh: () => void,
): { filter: (points: LiveTimePoint[]) => LiveTimePoint[]; update: () => void } {
    let range: { start: number; end: number } | undefined;
    let inspected: LiveTimePoint | undefined;
    const controls = document.createElement("section");
    controls.className = "evidence-controls";
    controls.setAttribute("aria-label", "Live time selection");
    const label = document.createElement("p");
    label.textContent = "Explore retained live data by receipt time (not target event time). Selection does not change collection or retention.";
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
    reset.textContent = "Follow live / reset zoom";
    const status = document.createElement("p");
    status.setAttribute("role", "status");
    const detail = document.createElement("p");
    detail.setAttribute("aria-label", "Selected counter observation");
    const formatReceiptTime = (milliseconds: number | undefined): string =>
        milliseconds !== undefined && Number.isFinite(milliseconds)
            ? new Date(milliseconds).toISOString() : "No retained receipt time";
    const filter = (points: LiveTimePoint[]) => range
        ? points.filter(point => point.receivedAtMs >= range!.start && point.receivedAtMs <= range!.end) : points;
    function update(): void {
        const points = getPoints();
        const visible = filter(points);
        if (inspected && !visible.includes(inspected)) {
            inspected = undefined;
            detail.textContent = "The selected observation is no longer in this metric's retained selection.";
        }
        for (const row of gcRows) row.element.hidden = !!range
            && (row.receivedAtMs < range.start || row.receivedAtMs > range.end);
        const first = points[0]?.receivedAtMs;
        const last = points[points.length - 1]?.receivedAtMs;
        const selectedStart = range?.start ?? first;
        const selectedEnd = range?.end ?? last;
        const startTime = formatReceiptTime(selectedStart);
        const endTime = formatReceiptTime(selectedEnd);
        startValue.textContent = startTime;
        endValue.textContent = endTime;
        start.setAttribute("aria-valuetext", `${start.value}% of retained receipt time; selected start ${startTime}`);
        end.setAttribute("aria-valuetext", `${end.value}% of retained receipt time; selected end ${endTime}`);
        status.textContent = !range ? "Following live: " + points.length + " retained observations in this metric."
            : "Fixed receipt-time selection: " + visible.length + " of " + points.length
                + " retained observations in this metric.";
        status.textContent += ` Selected receipt-time bounds: ${startTime} to ${endTime}.`;
        if (range) status.textContent += " Older selected data can expire; empty is not zero.";
    }
    function select(): void {
        const points = getPoints();
        if (!points.length) { status.textContent = "No retained observations to select."; return; }
        const first = points[0].receivedAtMs;
        const duration = points[points.length - 1].receivedAtMs - first;
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
        range = undefined; inspected = undefined;
        start.value = "0"; end.value = "100"; detail.textContent = ""; update(); refresh();
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
        detail.textContent = "Observed value: " + nearest.value + " | target timestamp: " + nearest.timestamp
            + ". Nearest retained observation; no interpolation.";
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
    controls.append(label, startLabel, endLabel, reset, status, detail);
    canvas.parentElement?.insertBefore(controls, canvas);
    update();
    return { filter, update };
}

export const LIVE_TIME_NAVIGATION_SCRIPT = installLiveTimeNavigation.toString();
