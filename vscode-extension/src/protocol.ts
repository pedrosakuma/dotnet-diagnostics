export interface TargetProcess {
    processId: number;
    operatingSystem: string;
    processArchitecture: string;
    runtimeVersion: string;
    managedEntrypointAssemblyName?: string | null;
}

export interface CounterValue {
    provider: string;
    name: string;
    displayName: string;
    value: number;
    kind: string;
    unit?: string | null;
}

/** A single GC pause observation forwarded from the CLI's `gc` live kind (#1099/#1100). */
export interface GcCollection {
    timestamp: string;
    generation: number;
    reason: string;
    type: string;
    pauseDuration: string;
    clrInstanceId?: number | null;
    collectionCount?: number | null;
}

/** One hotspot frame from a point-in-time CPU capture's bounded summary. */
export interface CpuHotspot {
    frame: { module: string; method: string };
    inclusiveSamples: number;
    exclusiveSamples: number;
}

/** The bounded `CpuSample` summary returned by the CLI's one-shot `capture` request. */
export interface CpuSampleSummary {
    processId: number;
    startedAt: string;
    duration: string;
    totalSamples: number;
    topHotspots: CpuHotspot[];
}

/** One type's aggregate footprint from a heap snapshot's bounded top-N summary. */
export interface HeapTypeStat {
    typeFullName: string;
    moduleName?: string | null;
    instanceCount: number;
    totalBytes: number;
    totalBytesPercent: number;
}

/** Observed completion state for an EventPipe gcdump capture (`source: "gcdump"` only). */
export interface GcDumpCaptureStatus {
    gcStopObserved: boolean;
    eventStreamCompleted: boolean;
    timedOut: boolean;
    readerFailed: boolean;
    traceExportRequested: boolean;
    traceExportCompleted: boolean;
}

/** The trimmed `DiagnosticResult<LiveHeapInspection>` projection returned by the CLI's one-shot `capture` request for `kind: "heap"`. */
export interface HeapCaptureResult {
    summary: string;
    data?: {
        processId: number;
        suspendDuration: string;
        topTypesByBytes: HeapTypeStat[];
        topTypesByInstances: HeapTypeStat[];
        warnings?: string[] | null;
        gcDumpStatus?: GcDumpCaptureStatus | null;
        /** The `IDiagnosticHandleStore` handle registered for this snapshot; lets the extension issue follow-up `query` requests on the same connection (#1116). */
        handle?: string | null;
    } | null;
}

/**
 * The trimmed `DiagnosticResult<DumpInspection>` projection returned by the CLI's one-shot
 * `capture` request for `kind: "heap"`, `source: "dump"`. Unlike `HeapCaptureResult` there is no
 * live attach, so `data` identifies the offline dump file instead of a PID/suspend-duration/
 * GC-dump-status (see `DumpInspection` in `IDumpInspector.cs` vs `LiveHeapInspection`).
 */
export interface DumpHeapCaptureResult {
    summary: string;
    data?: {
        filePath: string;
        fileSizeBytes: number;
        topTypesByBytes: HeapTypeStat[];
        topTypesByInstances: HeapTypeStat[];
        warnings?: string[] | null;
        /** See `HeapCaptureResult.data.handle` (#1116). */
        handle?: string | null;
    } | null;
}

/** One managed thread observed in a point-in-time thread snapshot capture, trimmed from `ManagedThread`. */
export interface ThreadSnapshotThread {
    managedThreadId: number;
    osThreadId: number;
    state: string;
    isAlive: boolean;
    isBackground: boolean;
    isGc: boolean;
    isThreadpoolWorker: boolean;
    lockCount: number;
    currentExceptionType?: string | null;
    isLikelyBlocked: boolean;
    inferredWaitReason?: string | null;
    frames: string[];
}

/** One contended monitor lock observed in a point-in-time thread snapshot capture, trimmed from `MonitorLockState`. */
export interface ThreadSnapshotLock {
    objectTypeFullName?: string | null;
    ownerManagedThreadId: number;
    waitingThreadCount: number;
    isContended: boolean;
}

/** The trimmed `ThreadSnapshotQueryResult` projection returned by the CLI's one-shot `capture` request for `kind: "thread-snapshot"`. */
export interface ThreadCaptureResult {
    summary: string;
    data?: {
        processId: number;
        origin: string;
        capturedAt: string;
        walkDuration: string;
        totalThreads?: number | null;
        omittedThreads?: number | null;
        totalLocks?: number | null;
        omittedLocks?: number | null;
        threads?: ThreadSnapshotThread[] | null;
        locks?: ThreadSnapshotLock[] | null;
        /** See `HeapCaptureResult.data.handle` (#1116). */
        handle?: string | null;
    } | null;
}

/**
 * The trimmed `query` response payload for a follow-up heap/thread-snapshot drilldown view
 * (#1116). `view`-specific fields (e.g. `rootsByKind`, `deadlocks`) vary by which of the 10
 * heap/4 thread views was requested — see `CliStreamingProtocol.TrimHeapQueryResult`/
 * `TrimThreadQueryResult` for the exact per-view field lists — so this only pins down the
 * envelope fields every view shares and leaves the rest to the per-view renderer.
 */
export interface QueryResult {
    handle: string;
    view: string;
    origin?: string | null;
    processId?: number | null;
    capturedAt?: string | null;
    [key: string]: unknown;
}

export interface ProtocolFrame {
    type: string;
    [key: string]: unknown;
}

export function parseProcessList(json: string): TargetProcess[] {
    const envelope: unknown = JSON.parse(json);
    if (!isRecord(envelope) || !Array.isArray(envelope.data)) {
        throw new Error("The CLI process-list response did not contain a data array.");
    }

    return envelope.data.flatMap((value: unknown) => {
        if (!isRecord(value)
            || typeof value.processId !== "number"
            || !Number.isSafeInteger(value.processId)
            || value.processId <= 0) {
            return [];
        }

        return [{
            processId: value.processId,
            operatingSystem: optionalString(value.operatingSystem) ?? "unknown OS",
            processArchitecture: optionalString(value.processArchitecture) ?? "unknown architecture",
            runtimeVersion: optionalString(value.runtimeVersion) ?? "unknown runtime",
            managedEntrypointAssemblyName: optionalString(value.managedEntrypointAssemblyName),
        }];
    });
}

export function parseProtocolFrame(line: string): ProtocolFrame {
    const value: unknown = JSON.parse(line);
    if (!isRecord(value) || typeof value.type !== "string") {
        throw new Error("The CLI emitted a protocol frame without a string type.");
    }

    return value as ProtocolFrame;
}

export function describeStreamCompatibilityError(output: string): string {
    const trimmed = output.trim();
    if (!trimmed.startsWith("{") || trimmed.split(/\r?\n/).length > 8 || trimmed.length > 600) {
        return "The configured dotnet-diagnostics-cli did not start the JSONL stream protocol. It may be an older CLI build without live-stream support. Update/build the CLI with `stream --protocol jsonl`, then set the machine-scoped `dotnetDiagnostics.cliPath` to that executable.";
    }

    return "The configured dotnet-diagnostics-cli returned output that is not a valid JSONL protocol frame. Check the CLI executable and its version.";
}

export function isGcCollection(value: unknown): value is GcCollection {
    return isRecord(value)
        && typeof value.timestamp === "string"
        && typeof value.generation === "number"
        && typeof value.reason === "string"
        && typeof value.type === "string"
        && typeof value.pauseDuration === "string";
}

export function isCpuSampleSummary(value: unknown): value is CpuSampleSummary {
    return isRecord(value)
        && typeof value.processId === "number"
        && typeof value.startedAt === "string"
        && typeof value.duration === "string"
        && typeof value.totalSamples === "number"
        && Array.isArray(value.topHotspots)
        && value.topHotspots.every(isCpuHotspot);
}

function isCpuHotspot(value: unknown): value is CpuHotspot {
    return isRecord(value)
        && isRecord(value.frame)
        && typeof value.frame.module === "string"
        && typeof value.frame.method === "string"
        && typeof value.inclusiveSamples === "number"
        && typeof value.exclusiveSamples === "number";
}

export function isHeapCaptureResult(value: unknown): value is HeapCaptureResult {
    if (!isRecord(value) || typeof value.summary !== "string") {
        return false;
    }
    if (value.data === undefined || value.data === null) {
        return true;
    }
    const data = value.data;
    return isRecord(data)
        && typeof data.processId === "number"
        && typeof data.suspendDuration === "string"
        && Array.isArray(data.topTypesByBytes)
        && data.topTypesByBytes.every(isHeapTypeStat)
        && Array.isArray(data.topTypesByInstances)
        && data.topTypesByInstances.every(isHeapTypeStat);
}

export function isDumpHeapCaptureResult(value: unknown): value is DumpHeapCaptureResult {
    if (!isRecord(value) || typeof value.summary !== "string") {
        return false;
    }
    if (value.data === undefined || value.data === null) {
        return true;
    }
    const data = value.data;
    return isRecord(data)
        && typeof data.filePath === "string"
        && typeof data.fileSizeBytes === "number"
        && Array.isArray(data.topTypesByBytes)
        && data.topTypesByBytes.every(isHeapTypeStat)
        && Array.isArray(data.topTypesByInstances)
        && data.topTypesByInstances.every(isHeapTypeStat);
}

function isHeapTypeStat(value: unknown): value is HeapTypeStat {
    return isRecord(value)
        && typeof value.typeFullName === "string"
        && typeof value.instanceCount === "number"
        && typeof value.totalBytes === "number"
        && typeof value.totalBytesPercent === "number";
}

export function isThreadCaptureResult(value: unknown): value is ThreadCaptureResult {
    if (!isRecord(value) || typeof value.summary !== "string") {
        return false;
    }
    if (value.data === undefined || value.data === null) {
        return true;
    }
    const data = value.data;
    return isRecord(data)
        && typeof data.processId === "number"
        && typeof data.origin === "string"
        && typeof data.capturedAt === "string"
        && typeof data.walkDuration === "string"
        && (data.threads === undefined || data.threads === null
            || (Array.isArray(data.threads) && data.threads.every(isThreadSnapshotThread)))
        && (data.locks === undefined || data.locks === null
            || (Array.isArray(data.locks) && data.locks.every(isThreadSnapshotLock)));
}

function isThreadSnapshotThread(value: unknown): value is ThreadSnapshotThread {
    return isRecord(value)
        && typeof value.managedThreadId === "number"
        && typeof value.osThreadId === "number"
        && typeof value.state === "string"
        && typeof value.isAlive === "boolean"
        && typeof value.isBackground === "boolean"
        && typeof value.isGc === "boolean"
        && typeof value.isThreadpoolWorker === "boolean"
        && typeof value.lockCount === "number"
        && typeof value.isLikelyBlocked === "boolean"
        && Array.isArray(value.frames)
        && value.frames.every(frame => typeof frame === "string");
}

/** Validates only the shared envelope (`handle`/`view`); the rest of the shape is view-specific and left to the caller's renderer (#1116). */
export function isQueryResult(value: unknown): value is QueryResult {
    return isRecord(value)
        && typeof value.handle === "string"
        && typeof value.view === "string";
}

function isThreadSnapshotLock(value: unknown): value is ThreadSnapshotLock {
    return isRecord(value)
        && typeof value.ownerManagedThreadId === "number"
        && typeof value.waitingThreadCount === "number"
        && typeof value.isContended === "boolean";
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

function optionalString(value: unknown): string | null {
    return typeof value === "string" && value.length > 0 ? value : null;
}
