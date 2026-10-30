// Keep unavailable actions focusable so their prerequisite remains discoverable.
function setActionAvailable(button: HTMLElement | null, available: boolean, reason: string): void {
    if (!button) return;
    button.setAttribute("aria-disabled", String(!available));
    button.title = available ? "" : reason;
}

export const ACTION_AVAILABILITY_SCRIPT = `
${setActionAvailable.toString()}
document.addEventListener("click", event => {
  if (event.target.closest?.('[aria-disabled="true"]')) {
    event.preventDefault();
    event.stopImmediatePropagation();
  }
}, true);
`;

export function profileRetentionText(retained: boolean): string {
    return retained
        ? "Cached loaded projection. Additional evidence requires an explicit query on this displayed capture's retained CLI connection."
        : "Cached loaded projection only. This displayed capture's retained CLI evidence is unavailable; additional queries and retained-evidence exports require a new capture.";
}

function installProfileActions(): (message: {
    type?: string; kind?: string; state?: string;
    profiles?: { cpu: boolean; trace: boolean; allocation: boolean };
}) => void {
    let cpuMeasurements = false;
    let profiles = { cpu: false, trace: false, allocation: false };
    const captures = new Set<string | undefined>();
    const queries = new Set<string>();
    let exporting = false;
    return message => {
        if (message.profiles) profiles = message.profiles;
        if (message.type === "capture" && message.kind === "cpu") cpuMeasurements = true;
        if (message.type === "captureStatus") {
            if (message.state === "starting" || message.state === "running") captures.add(message.kind);
            else captures.delete(message.kind);
        }
        if (message.type === "cpuQueryState" || message.type === "allocationQueryState") {
            if (message.state === "running") queries.add(message.type);
            else queries.delete(message.type);
        }
        if (message.type === "cpuExportState") exporting = message.state === "running";
        const busy = captures.size > 0 || queries.size > 0 || exporting;
        const wait = "Wait for the current capture, query or export to finish.";
        const cpuReason = profiles.cpu ? wait : "Capture CPU with a retained CLI profile first; reconnect by capturing again.";
        const summaryReason = profiles.cpu ? wait : cpuMeasurements
            ? "This displayed CPU capture has no retained CLI evidence. Capture new CPU samples; export never starts a capture."
            : "Capture new CPU samples first. Export never starts a capture.";
        const allocationReason = profiles.allocation ? wait : "Capture allocations with a retained CLI profile first.";
        for (const id of ["exportJson", "exportCsv"])
            setActionAvailable(document.getElementById(id), cpuMeasurements,
                "Capture new CPU samples first. Redacted measurements use the displayed result and remain available offline.");
        setActionAvailable(document.getElementById("exportSummary"), profiles.cpu && !busy, summaryReason);
        setActionAvailable(document.getElementById("exportTrace"), profiles.trace && !busy,
            profiles.trace ? wait : profiles.cpu
                ? "This displayed CPU capture did not retain a raw trace. Capture new CPU samples + retain raw trace; saving never starts a capture."
                : "This displayed CPU capture has no retained CLI evidence. Capture new CPU samples + retain raw trace; saving never starts a capture.");
        for (const id of ["captureCpu", "captureCpuTrace", "captureAllocation"])
            setActionAvailable(document.getElementById(id), !busy, wait);
        for (const button of Array.from(document.querySelectorAll<HTMLElement>("[data-cpu-view], #cpuQueryViews [data-profile-query]")))
            setActionAvailable(button, profiles.cpu && !busy, cpuReason);
        for (const button of Array.from(document.querySelectorAll<HTMLElement>("[data-allocation-view], #allocationQueryViews [data-profile-query]")))
            setActionAvailable(button, profiles.allocation && !busy, allocationReason);
        for (const [id, available, reason] of [
            ["cpuQueryViews", profiles.cpu && !busy, cpuReason],
            ["allocationQueryViews", profiles.allocation && !busy, allocationReason],
        ] as const) {
            const container = document.getElementById(id);
            container?.setAttribute("data-profile-query-available", String(available));
            container?.setAttribute("data-profile-query-reason", reason);
            container?.setAttribute("data-profile-retained", String(id === "cpuQueryViews" ? profiles.cpu : profiles.allocation));
            for (const note of Array.from(container?.querySelectorAll<HTMLElement>("[data-profile-retention-note]") ?? []))
                note.textContent = profileRetentionText(id === "cpuQueryViews" ? profiles.cpu : profiles.allocation);
        }
        const explorer = document.getElementById("allocationExplorer");
        if (explorer) {
            if (!profiles.allocation && explorer.contains(document.activeElement))
                document.getElementById("captureAllocation")?.focus();
            explorer.hidden = !profiles.allocation;
        }
        const cpuStatus = document.getElementById("cpuEvidencePrerequisite");
        if (cpuStatus) cpuStatus.textContent = !cpuMeasurements
            ? "No displayed CPU capture. Collect new CPU samples to enable sharing and retained-evidence actions."
            : !profiles.cpu
                ? "Displayed CPU measurements remain shareable offline. Retained CLI evidence is unavailable, so investigation-summary and raw-trace exports cannot run. Export actions never collect again."
                : !profiles.trace
                    ? "Displayed CPU measurements are shareable. Investigation-summary export uses this capture's retained CLI profile. No raw trace was retained; saving one requires a new CPU capture with trace retention."
                    : "Displayed CPU measurements are shareable. Investigation-summary and raw-trace exports reuse this capture's retained CLI evidence and never collect again.";
        const allocationStatus = document.getElementById("allocationEvidencePrerequisite");
        if (allocationStatus) allocationStatus.textContent = profiles.allocation ? ""
            : "Allocation exploration requires a retained profile. Table-only or disconnected results remain readable; capture again with a profile-capable CLI.";
    };
}

export const PROFILE_ACTIONS_SCRIPT = `${profileRetentionText.toString()}\n${installProfileActions.toString()}`;
export const ACTION_AVAILABILITY_STYLES = `button[aria-disabled="true"], [aria-disabled="true"] button { color:var(--vscode-disabledForeground); cursor:default; }`;
