export type CaptureViewKind =
    | "allocation" | "off_cpu" | "contention" | "threadpool" | "gc"
    | "gc-activities" | "requests" | "kestrel" | "db" | "networking";

export interface CaptureKindSpec {
    kind: CaptureViewKind;
    label: string;
    description: string;
    risk: "moderate" | "high";
    defaultTopN: number;
    supportsSymbolPath?: boolean;
    supportsSources?: boolean;
    supportsThreshold?: boolean;
}

export const CAPTURE_KIND_SPECS: readonly CaptureKindSpec[] = [
    { kind: "allocation", label: "Allocations", description: "Sampled allocation types and sites (estimates)", risk: "moderate", defaultTopN: 25 },
    { kind: "off_cpu", label: "Off-CPU blocking", description: "Blocking stacks; needs perf/CAP_PERFMON on Linux or ETW privileges on Windows", risk: "high", defaultTopN: 25, supportsSymbolPath: true },
    { kind: "contention", label: "Lock contention", description: "Managed monitor contention sites", risk: "moderate", defaultTopN: 25 },
    { kind: "threadpool", label: "Thread pool", description: "Thread counts, hill-climbing and starvation evidence", risk: "moderate", defaultTopN: 25 },
    { kind: "gc", label: "Garbage collections", description: "Collections, generations and suspension evidence", risk: "moderate", defaultTopN: 25 },
    { kind: "gc-activities", label: "GC and activities", description: "Activities overlapping GCs", risk: "moderate", defaultTopN: 20, supportsSources: true },
    { kind: "requests", label: "In-flight requests", description: "Long-running requests (paths can be sensitive)", risk: "moderate", defaultTopN: 25, supportsThreshold: true },
    { kind: "kestrel", label: "Kestrel", description: "Connections, queues and request timings", risk: "moderate", defaultTopN: 25 },
    { kind: "db", label: "Database", description: "Command groups, N+1 and pools (sanitized SQL can still be sensitive)", risk: "moderate", defaultTopN: 25 },
    { kind: "networking", label: "Networking", description: "HTTP, DNS, TLS and sockets (hosts can be sensitive)", risk: "moderate", defaultTopN: 25 },
];

export interface CaptureParams {
    durationSeconds: number;
    topN: number;
    symbolPath?: string;
    sources?: string[];
    thresholdMs?: number;
}

type Scalar = string | number | boolean | null;
export interface CaptureViewResult {
    handle?: string | null;
    kind: CaptureViewKind;
    title: string;
    summary: string;
    provenance: {
        origin: "live-stream"; durable: false; processId: number; startedAt: string | null;
        reportedDurationSeconds: number | null; requestedDurationSeconds: number;
        runtime: string | null; runtimeVersion: string | null;
    };
    metrics: Array<{ label: string; value: Scalar; note?: string | null }>;
    tables: Array<{
        id: string; title: string; columns: string[]; rows: Scalar[][];
        totalRows: number; omittedRows: number; emptyText: string;
    }>;
    notes: string[];
    evidence: {
        source: string;
        limitations: Array<{ category: string; scope: string; affectedCount?: number | null; detail: string }>;
        conclusions?: { retainedPositive: string; absenceOrExhaustive: string; healthyControl: string } | null;
    };
}

export function isCaptureViewKind(kind: string): kind is CaptureViewKind {
    return CAPTURE_KIND_SPECS.some(spec => spec.kind === kind);
}

export function validateInteger(text: string, min: number, max: number, label: string): string | undefined {
    const trimmed = text.trim();
    return /^\d+$/.test(trimmed) && Number(trimmed) >= min && Number(trimmed) <= max
        ? undefined : `${label} must be a whole number between ${min} and ${max}.`;
}

export function validateSymbolPath(text: string): string | undefined {
    if (text.length > 1024) return "Symbol path must be at most 1024 characters.";
    return /[\u0000-\u001f\u007f-\u009f]/.test(text) ? "Symbol path must not contain control characters." : undefined;
}

export function parseSources(text: string): { sources?: string[]; error?: string } {
    const sources = [...new Set(text.split(",").map(s => s.trim()).filter(Boolean))];
    if (sources.length > 20) return { error: "At most 20 source filters are allowed." };
    if (sources.some(s => s.length > 200 || /[\u0000-\u001f\u007f-\u009f]/.test(s)))
        return { error: "Each source filter must be at most 200 characters without control characters." };
    return sources.length ? { sources } : {};
}

export function buildCaptureRequest(requestId: string, spec: CaptureKindSpec, processId: number, params: CaptureParams): Record<string, unknown> {
    return {
        type: "capture", requestId, kind: spec.kind, processId,
        durationSeconds: params.durationSeconds, topN: params.topN,
        ...(spec.supportsSymbolPath && params.symbolPath ? { symbolPath: params.symbolPath } : {}),
        ...(spec.supportsSources && params.sources ? { sources: params.sources } : {}),
        ...(spec.supportsThreshold && params.thresholdMs !== undefined ? { thresholdMs: params.thresholdMs } : {}),
        ...(spec.risk === "high" ? { acknowledgeRisk: "high" } : {}),
    };
}

export function captureTimeoutMs(kind: CaptureViewKind, durationSeconds: number): number {
    return durationSeconds * 1000 + (kind === "gc-activities" ? 90_000 : 60_000);
}

function record(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}
function scalar(value: unknown): boolean {
    return value === null || typeof value === "string" || typeof value === "boolean"
        || (typeof value === "number" && Number.isFinite(value));
}
function count(value: unknown): value is number {
    return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}
function nullableString(value: unknown): boolean { return value === null || typeof value === "string"; }

export function isCaptureViewResult(value: unknown): value is CaptureViewResult {
    if (!record(value) || typeof value.kind !== "string" || !isCaptureViewKind(value.kind)
        || typeof value.title !== "string" || typeof value.summary !== "string") return false;
    if (value.handle !== undefined && value.handle !== null
        && (typeof value.handle !== "string" || value.handle.length < 1 || value.handle.length > 128
            || /[\u0000-\u0020\u007f]/.test(value.handle))) return false;
    const p = value.provenance;
    if (!record(p) || p.origin !== "live-stream" || p.durable !== false || !count(p.processId) || p.processId === 0
        || !count(p.requestedDurationSeconds) || !nullableString(p.startedAt) || !nullableString(p.runtime)
        || !nullableString(p.runtimeVersion)
        || !(p.reportedDurationSeconds === null || typeof p.reportedDurationSeconds === "number" && Number.isFinite(p.reportedDurationSeconds))) return false;
    if (!Array.isArray(value.metrics) || value.metrics.length > 100
        || !value.metrics.every(m => record(m) && typeof m.label === "string" && scalar(m.value)
            && (m.note === undefined || nullableString(m.note)))) return false;
    if (!Array.isArray(value.tables) || value.tables.length > 20) return false;
    for (const t of value.tables) {
        if (!record(t) || typeof t.id !== "string" || typeof t.title !== "string"
            || typeof t.emptyText !== "string" || !count(t.totalRows) || !count(t.omittedRows)
            || !Array.isArray(t.columns) || t.columns.length > 12 || !t.columns.every(c => typeof c === "string")
            || !Array.isArray(t.rows) || t.rows.length > 100) return false;
        const columns = t.columns.length;
        if (!t.rows.every(row => Array.isArray(row) && row.length === columns && row.every(scalar))
            || t.totalRows !== t.rows.length + t.omittedRows) return false;
    }
    if (!Array.isArray(value.notes) || value.notes.length > 50 || !value.notes.every(n => typeof n === "string")) return false;
    const e = value.evidence;
    if (!record(e) || typeof e.source !== "string" || !Array.isArray(e.limitations) || e.limitations.length > 100
        || !e.limitations.every(l => record(l) && typeof l.category === "string" && typeof l.scope === "string" && typeof l.detail === "string")) return false;
    const conclusions = e.conclusions;
    return conclusions === undefined || conclusions === null || record(conclusions)
        && ["retainedPositive", "absenceOrExhaustive", "healthyControl"].every(key => typeof conclusions[key] === "string");
}
