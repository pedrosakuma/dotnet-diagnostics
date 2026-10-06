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
    } | null;
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

function isHeapTypeStat(value: unknown): value is HeapTypeStat {
    return isRecord(value)
        && typeof value.typeFullName === "string"
        && typeof value.instanceCount === "number"
        && typeof value.totalBytes === "number"
        && typeof value.totalBytesPercent === "number";
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

function optionalString(value: unknown): string | null {
    return typeof value === "string" && value.length > 0 ? value : null;
}
