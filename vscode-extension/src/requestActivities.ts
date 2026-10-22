export interface JointRequest {
    rowId: string;
    traceId: string | null;
    spanId: string | null;
    method: string;
    path: string;
    startedAt: string;
    elapsedMs: number;
}

export interface JointActivity {
    rowId: string;
    source: string;
    operation: string;
    traceId: string | null;
    spanId: string | null;
    parentSpanId: string | null;
    startedAt: string;
    stoppedAt: string;
    durationMs: number | null;
}

export interface RelatedActivity {
    activity: JointActivity;
    relationship: "direct-child" | "observed-descendant" | "same-trace-only";
    verified: boolean;
    verificationWithheldReason: string | null;
}

export interface JointQuality {
    completion: string;
    eventsLost: number | null;
    lifetimeStatus: string;
    parseErrors: number;
    requestsStarted: number;
    requestsCompleted: number;
    droppedRequests: number;
    activitiesObserved: number;
    droppedActivities: number;
    invalidIdentities: number;
    duplicateIdentities: number;
    identityCapacityReached: boolean;
    truncatedFields: number;
    retainedIdentities: number;
    accountedRetainedBytes: number;
}

export interface JointQuery<T> {
    captureId: string;
    processId: number;
    processStartedAt: string | null;
    observationStart: string | null;
    observationEnd: string;
    quality: JointQuality;
    retainedRequests: number;
    retainedActivities: number;
    totalRows: number;
    omittedRows: number;
    rows: T[];
    notes: string[];
}

export interface JointCapture {
    handle: string;
    handleExpiresAt: string;
    captureId: string;
    requestedStart: string;
    requestedDurationSeconds: number;
    sources: string[];
    requests: JointQuery<JointRequest>;
}

const record = (v: unknown): v is Record<string, unknown> => !!v && typeof v === "object" && !Array.isArray(v);
const text = (v: unknown, max = 256): v is string => typeof v === "string" && v.length <= max;
const finite = (v: unknown): v is number => typeof v === "number" && Number.isFinite(v) && v >= 0;
const count = (v: unknown, max = Number.MAX_SAFE_INTEGER): v is number => finite(v) && Number.isSafeInteger(v) && v <= max;
const date = (v: unknown): v is string => text(v, 64) && Number.isFinite(Date.parse(v));
const nullableDate = (v: unknown): boolean => v === null || date(v);
const id = (v: unknown, size: number): boolean => v === null || typeof v === "string"
    && v.length === size && /^[0-9a-f]+$/.test(v) && /[1-9a-f]/.test(v);
const rowId = (v: unknown): v is string => text(v, 128) && v.length > 0 && !/[\u0000-\u0020]/.test(v);

export function supportsRequestActivities(v: unknown): boolean {
    if (!record(v) || !Array.isArray(v.queryViews)) return false;
    const views = v.queryViews;
    return record(v) && v.version === 1 && v.captureKind === "request-activities"
        && ["requests", "related-activities"].every(view => views.includes(view))
        && v.maxRequests === 100 && v.maxActivities === 200 && v.maxQueryRows === 100;
}

export function parseJointSources(value: string): string[] | undefined {
    const sources = value.split(",").map(source => source.trim());
    return sources.length >= 1 && sources.length <= 20
        && sources.every(source => /^[A-Za-z0-9._-]{1,200}$/.test(source))
        ? [...new Set(sources)] : undefined;
}

function isQuality(v: unknown): v is JointQuality {
    return record(v) && text(v.completion, 64) && text(v.lifetimeStatus, 64)
        && (v.eventsLost === null || count(v.eventsLost))
        && ["parseErrors", "requestsStarted", "requestsCompleted", "droppedRequests", "activitiesObserved",
            "droppedActivities", "invalidIdentities", "duplicateIdentities", "truncatedFields"].every(k => count(v[k]))
        && typeof v.identityCapacityReached === "boolean" && count(v.retainedIdentities, 4096)
        && count(v.accountedRetainedBytes, 4 * 1024 * 1024);
}

function isRequest(v: unknown): v is JointRequest {
    return record(v) && rowId(v.rowId) && id(v.traceId, 32) && id(v.spanId, 16)
        && text(v.method) && text(v.path, 1024) && date(v.startedAt) && finite(v.elapsedMs);
}

function isRelated(v: unknown): v is RelatedActivity {
    if (!record(v) || !record(v.activity)) return false;
    const a = v.activity;
    return ["direct-child", "observed-descendant", "same-trace-only"].includes(String(v.relationship))
        && typeof v.verified === "boolean"
        && (v.verified ? v.relationship !== "same-trace-only" && v.verificationWithheldReason === null
            : text(v.verificationWithheldReason, 1024) && v.verificationWithheldReason.length > 0)
        && rowId(a.rowId) && text(a.source) && text(a.operation) && id(a.traceId, 32)
        && id(a.spanId, 16) && id(a.parentSpanId, 16) && date(a.startedAt) && date(a.stoppedAt)
        && (a.durationMs === null || finite(a.durationMs));
}

function isQuery<T>(v: unknown, row: (v: unknown) => v is T, max: number): v is JointQuery<T> {
    return record(v) && rowId(v.captureId) && count(v.processId) && v.processId > 0
        && nullableDate(v.processStartedAt) && nullableDate(v.observationStart) && date(v.observationEnd)
        && isQuality(v.quality) && count(v.retainedRequests, 100) && count(v.retainedActivities, 200)
        && count(v.totalRows, max) && count(v.omittedRows, max)
        && Array.isArray(v.rows) && v.rows.length <= 100 && v.rows.every(row)
        && v.totalRows === v.rows.length + v.omittedRows
        && new Set(v.rows.map(r => record(r) && record(r.activity) ? r.activity.rowId : record(r) ? r.rowId : null)).size === v.rows.length
        && Array.isArray(v.notes) && v.notes.length <= 20 && v.notes.every(n => text(n, 2048));
}

export function isJointRelatedQuery(v: unknown): v is JointQuery<RelatedActivity> {
    return isQuery(v, isRelated, 200);
}

export function isJointCapture(v: unknown): v is JointCapture {
    return record(v) && rowId(v.handle) && v.handle === "request-activities-" + v.captureId
        && date(v.handleExpiresAt) && rowId(v.captureId) && date(v.requestedStart)
        && count(v.requestedDurationSeconds, 300) && v.requestedDurationSeconds >= 1
        && Array.isArray(v.sources) && v.sources.length >= 1 && v.sources.length <= 20
        && v.sources.every(s => typeof s === "string" && /^[A-Za-z0-9._-]{1,200}$/.test(s))
        && isQuery(v.requests, isRequest, 100) && v.requests.captureId === v.captureId
        && v.requests.omittedRows === 0 && v.requests.totalRows === v.requests.retainedRequests;
}
