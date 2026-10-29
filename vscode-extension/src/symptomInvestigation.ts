export const SYMPTOM_IDS = [
    "high-cpu",
    "memory-growth",
    "slow-requests",
    "locks-waits",
    "exceptions-crashes",
    "runtime-health",
] as const;

export type SymptomId = typeof SYMPTOM_IDS[number];

export interface SymptomSpec {
    id: SymptomId;
    label: string;
    description: string;
    actions: readonly SymptomAction[];
}

export interface SymptomAction {
    label: string;
    reason: string;
    mode: string;
    boundaries: string;
    command: string;
    arguments?: readonly unknown[];
    recommended?: boolean;
}

export interface SymptomActionItem {
    label: string;
    description: string;
    detail: string;
    command: string;
    arguments?: readonly unknown[];
    recommended: boolean;
}

const allActions: SymptomAction = {
    label: "All diagnostic actions",
    reason: "Browse the complete expert catalog without narrowing by symptom.",
    mode: "Navigation only until you choose a capture.",
    boundaries: "The existing target, capability, consent and risk checks remain authoritative.",
    command: "dotnetDiagnostics.captureDiagnostics",
};

export const SYMPTOM_SPECS: readonly SymptomSpec[] = [
    {
        id: "high-cpu",
        label: "High CPU",
        description: "Find sampled on-CPU hotspots",
        actions: [
            {
                label: "Capture CPU samples",
                reason: "A bounded CPU sample is the lowest-surprise first evidence for sustained processor use.",
                mode: "Collects new temporary evidence in Workspace → CPU.",
                boundaries: "Moderate target impact; no persistent artifact; validates the active target before collection.",
                command: "dotnetDiagnostics.captureCpu",
                recommended: true,
            },
            {
                label: "Start live counters",
                reason: "Observe processor, GC and thread-pool trends before deciding whether to capture.",
                mode: "Monitors the selected target in Monitoring; it does not create a persistent artifact.",
                boundaries: "Low impact; starts only after process selection.",
                command: "dotnetDiagnostics.startCounters",
            },
            {
                label: "Capture CPU + raw trace",
                reason: "Keep the raw trace when later offline tooling is required.",
                mode: "Collects temporary CPU evidence and creates a local raw artifact.",
                boundaries: "The trace is unredacted; explicit confirmation is required before collection.",
                command: "dotnetDiagnostics.captureCpuTrace",
            },
            allActions,
        ],
    },
    {
        id: "memory-growth",
        label: "Memory growth or GC pressure",
        description: "Observe trends before inspecting retained objects",
        actions: [
            {
                label: "Start live counters",
                reason: "Managed heap, allocation rate and GC observations establish whether pressure is visible over time.",
                mode: "Monitors the selected target in Monitoring; it does not create a persistent artifact.",
                boundaries: "Low impact; an empty window or stable counter is not a leak verdict.",
                command: "dotnetDiagnostics.startCounters",
                recommended: true,
            },
            {
                label: "Capture allocations",
                reason: "Sample allocation types and call sites when allocation rate is the immediate question.",
                mode: "Collects new temporary evidence in Workspace → Memory & GC.",
                boundaries: "Sampling is not exact lifetime or retained-size evidence; capability is checked before dispatch.",
                command: "dotnetDiagnostics.captureDiagnostics",
                arguments: ["allocation"],
            },
            {
                label: "Capture heap snapshot",
                reason: "Inspect retained types, roots and bounded heap drilldowns at a point in time.",
                mode: "Collects new temporary evidence in Workspace → Memory & GC.",
                boundaries: "May pause the target or trigger GC; source, permissions and consent are chosen before collection.",
                command: "dotnetDiagnostics.captureHeap",
            },
            {
                label: "Capture garbage collections",
                reason: "Inspect collection activity when pauses or collection frequency are the primary symptom.",
                mode: "Collects new temporary evidence in Workspace → Memory & GC.",
                boundaries: "EventPipe collection is temporary and does not establish a memory leak.",
                command: "dotnetDiagnostics.captureDiagnostics",
                arguments: ["gc"],
            },
            allActions,
        ],
    },
    {
        id: "slow-requests",
        label: "Slow requests",
        description: "Measure in-flight request latency and related runtime work",
        actions: [
            {
                label: "Capture in-flight requests",
                reason: "A bounded request window is the direct first measurement for currently slow server work.",
                mode: "Collects new temporary evidence in Workspace → Requests & networking.",
                boundaries: "May contain application identifiers; empty evidence is inconclusive and capability is validated.",
                command: "dotnetDiagnostics.captureDiagnostics",
                arguments: ["requests"],
                recommended: true,
            },
            {
                label: "Requests + completed Activities",
                reason: "Correlate selected in-flight requests with completed instrumented Activities during a reproduction.",
                mode: "Collects new temporary evidence in Workspace → Requests & networking.",
                boundaries: "Requires exact ActivitySource names and advertised CLI support; privacy notice precedes collection.",
                command: "dotnetDiagnostics.captureRequestActivities",
            },
            {
                label: "Capture CPU samples",
                reason: "Check whether request latency overlaps sampled on-CPU work.",
                mode: "Collects separate temporary evidence in Workspace → CPU.",
                boundaries: "CPU samples do not prove request causality or workload equivalence.",
                command: "dotnetDiagnostics.captureCpu",
            },
            allActions,
        ],
    },
    {
        id: "locks-waits",
        label: "Locks, blocking or waits",
        description: "Inspect thread state before deeper wait sampling",
        actions: [
            {
                label: "Capture thread snapshot",
                reason: "A point-in-time thread and lock snapshot is the most direct first look at blocked work.",
                mode: "Collects new temporary evidence in Workspace → Threads & waits.",
                boundaries: "Live inspection needs platform permissions and consent; deadlock candidates remain inferred.",
                command: "dotnetDiagnostics.captureThreadSnapshot",
                recommended: true,
            },
            {
                label: "Capture lock contention",
                reason: "Observe contention events over a bounded interval when a snapshot is insufficient.",
                mode: "Collects new temporary evidence in Workspace → Threads & waits.",
                boundaries: "EventPipe collection adds runtime overhead; an empty window is inconclusive.",
                command: "dotnetDiagnostics.captureDiagnostics",
                arguments: ["contention"],
            },
            {
                label: "Capture off-CPU blocking",
                reason: "Sample scheduling waits when platform support and permissions are available.",
                mode: "Collects new temporary evidence in Workspace → Threads & waits.",
                boundaries: "High-impact/platform-dependent; the existing prerequisite and acknowledgement flow runs first.",
                command: "dotnetDiagnostics.captureDiagnostics",
                arguments: ["off_cpu"],
            },
            {
                label: "Capture thread-pool events",
                reason: "Inspect worker adjustment and starvation signals without assuming their cause.",
                mode: "Collects new temporary evidence in Workspace → Threads & waits.",
                boundaries: "Temporary EventPipe evidence; unsupported runtimes abstain through the existing capability gate.",
                command: "dotnetDiagnostics.captureDiagnostics",
                arguments: ["threadpool"],
            },
            allActions,
        ],
    },
    {
        id: "exceptions-crashes",
        label: "Exceptions or crashes",
        description: "Observe exceptions live or inspect an existing dump offline",
        actions: [
            {
                label: "Capture exceptions",
                reason: "Observe managed exception events or configure crash guard for a live reproduction.",
                mode: "Collects new temporary evidence in Workspace → Exceptions.",
                boundaries: "Messages and stacks may contain sensitive data; mode and consent are chosen before collection.",
                command: "dotnetDiagnostics.captureExceptions",
                recommended: true,
            },
            {
                label: "Analyze an existing dump",
                reason: "Inspect heap and thread evidence when a crash dump already exists.",
                mode: "Opens an offline dump-analysis surface; it does not contact a running process.",
                boundaries: "The file picker and optional sensitive enrichments remain explicit.",
                command: "dotnetDiagnostics.analyzeDumpFile",
            },
            {
                label: "Start live events",
                reason: "Observe an explicitly configured runtime or application EventSource.",
                mode: "Monitors the selected target in Monitoring.",
                boundaries: "Provider payloads may contain sensitive data; explicit providers and acknowledgement are required.",
                command: "dotnetDiagnostics.startEvents",
            },
            allActions,
        ],
    },
    {
        id: "runtime-health",
        label: "General runtime health",
        description: "Start with low-impact runtime trends",
        actions: [
            {
                label: "Start live counters",
                reason: "Counters provide a bounded, low-impact orientation across CPU, memory, GC and thread-pool signals.",
                mode: "Monitors the selected target in Monitoring; it does not create a persistent artifact.",
                boundaries: "Counters describe observed trends, not a healthy/unhealthy verdict.",
                command: "dotnetDiagnostics.startCounters",
                recommended: true,
            },
            {
                label: "Start live events",
                reason: "Observe a known EventSource when counters are insufficient.",
                mode: "Monitors the selected target in Monitoring.",
                boundaries: "Explicit provider configuration and sensitive-data acknowledgement precede collection.",
                command: "dotnetDiagnostics.startEvents",
            },
            allActions,
        ],
    },
];

export function isSymptomId(value: unknown): value is SymptomId {
    return typeof value === "string" && SYMPTOM_IDS.includes(value as SymptomId);
}

export function symptomSpec(id: SymptomId): SymptomSpec {
    return SYMPTOM_SPECS.find(spec => spec.id === id)!;
}

export function buildSymptomActionItems(id: SymptomId, targetLabel?: string): SymptomActionItem[] {
    const target = targetLabel
        ? `Target: ${targetLabel}.`
        : "Target: none selected; dispatch opens the existing process picker and validates the active target.";
    return symptomSpec(id).actions.map(action => ({
        label: action.recommended ? `$(star-full) ${action.label}` : action.label,
        description: action.recommended ? "Recommended first measurement" : "Alternative",
        detail: `${target} ${action.reason} ${action.mode} ${action.boundaries}`,
        command: action.command,
        arguments: action.arguments,
        recommended: action.recommended === true,
    }));
}

export async function runSymptomInvestigation(
    id: SymptomId,
    targetLabel: string | undefined,
    pick: (items: readonly SymptomActionItem[], options: {
        title: string;
        placeHolder: string;
        matchOnDescription: boolean;
        matchOnDetail: boolean;
    }) => Promise<SymptomActionItem | undefined>,
    dispatch: (command: string, ...args: unknown[]) => Promise<unknown>,
): Promise<"cancelled" | "dispatched"> {
    const spec = symptomSpec(id);
    const selected = await pick(buildSymptomActionItems(id, targetLabel), {
        title: `What are you investigating? ${spec.label}`,
        placeHolder: "Choose evidence to collect or browse. Nothing starts until you choose an action.",
        matchOnDescription: true,
        matchOnDetail: true,
    });
    if (!selected) return "cancelled";
    await dispatch(selected.command, ...(selected.arguments ?? []));
    return "dispatched";
}
