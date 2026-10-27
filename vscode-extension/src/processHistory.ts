import { createCliRunner } from "./cliRunner";

export interface ProcessEntry {
    processId: number;
    entrypoint: string | null;
    runtimeVersion: string;
    platform: string;
    identity: string;
}
export interface ProcessInventory { processes: ProcessEntry[]; total: number; truncated: boolean }
export interface CaptureArtifact {
    artifactId: string; kind: string; name: string; supportedViews: string[];
    provenance?: { processId?: number; runtimeName?: string; runtimeVersion?: string; startedAt?: string };
}
export interface CaptureSummary {
    captureId: string; name: string; createdUtc: string;
    state: "recording" | "sealed" | "interrupted";
    artifacts: CaptureArtifact[];
    qualityStatus?: "complete" | "incomplete" | "unknown";
}
export interface CaptureListPage { captures: CaptureSummary[]; nextAfterCaptureId?: string }

const runBounded = createCliRunner({ timeoutMs: 60_000, graceMs: 2_000, settleMs: 2_000, maxOutputChars: 8 * 1024 * 1024 });

export async function runHistoryCli(cliPath: string, args: string[]): Promise<string> {
    const result = await runBounded(cliPath, args);
    if (result.outputLimitExceeded) throw new Error("CLI output exceeded the 8388608-character limit; narrow the capture or query.");
    if (result.timedOut) throw new Error("The CLI operation timed out.");
    if (result.spawnError) throw new Error(`Cannot start the CLI: ${result.spawnError}`);
    if (result.exitCode !== 0) {
        parseEnvelope(result.stdout);
        throw new Error(result.stderr.trim() || `The CLI exited with code ${result.exitCode}.`);
    }
    return result.stdout;
}

function record(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function parseEnvelope(stdout: string): Record<string, unknown> {
    let value: unknown;
    try { value = JSON.parse(stdout); }
    catch { throw new Error("The CLI did not return a JSON envelope. Check the configured CLI version and Output log."); }
    if (!record(value)) throw new Error("The CLI returned an unexpected JSON shape.");
    if (record(value.error))
        throw new Error([value.error.kind, value.error.message, value.error.detail].filter(p => typeof p === "string").join(": "));
    if (value.cancelled === true) throw new Error("The CLI operation was cancelled; the result may be incomplete.");
    return value;
}

export function parseProcessInventory(stdout: string): ProcessInventory {
    const data = parseEnvelope(stdout).data;
    if (!Array.isArray(data)) throw new Error("The CLI process list did not contain a data array.");
    const processes: ProcessEntry[] = [];
    for (const row of data) {
        if (!record(row) || !Number.isSafeInteger(row.processId) || Number(row.processId) <= 0)
            throw new Error("The CLI returned an invalid process row.");
        if (processes.length >= 500) continue;
        const field = (value: unknown): string => typeof value === "string" && value ? value : "unknown";
        const entrypoint = typeof row.managedEntrypointAssemblyName === "string" ? row.managedEntrypointAssemblyName : null;
        const runtimeVersion = field(row.runtimeVersion);
        const platform = `${field(row.operatingSystem)}/${field(row.processArchitecture)}`;
        processes.push({ processId: Number(row.processId), entrypoint, runtimeVersion, platform,
            identity: [entrypoint ?? "", runtimeVersion, platform].join("|") });
    }
    return { processes, total: data.length, truncated: data.length > 500 };
}

export function filterProcesses(processes: readonly ProcessEntry[], text: string): ProcessEntry[] {
    const needle = text.trim().toLowerCase();
    return processes.filter(p => `${p.processId} ${p.entrypoint ?? ""} ${p.runtimeVersion} ${p.platform}`.toLowerCase().includes(needle));
}

// This detects known PID reuse only; the CLI does not expose process start time.
export function validateTarget(saved: ProcessEntry, live: readonly ProcessEntry[]): ProcessEntry {
    const current = live.find(p => p.processId === saved.processId);
    if (!current) throw new Error("The selected process is no longer available. Refresh the process tree.");
    if (current.identity !== saved.identity) throw new Error("The selected PID now identifies a different process. Select it again.");
    return current;
}

const validId = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{32}$/.test(value);
function requireId(value: string): string {
    if (!validId(value)) throw new Error("Expected a 32-character lower-case hex ID from the CLI capture catalog.");
    return value;
}

export function captureListArgs(after?: string): string[] {
    return ["captures", "list", "--json", "--page-size", "50", ...(after ? ["--after-capture-id", requireId(after)] : [])];
}
export const captureShowArgs = (id: string): string[] => ["captures", "show", "--capture-id", requireId(id), "--json"];

function capture(value: unknown): CaptureSummary {
    if (!record(value) || !validId(value.captureId) || !Array.isArray(value.artifacts))
        throw new Error("The CLI capture description had an unexpected shape.");
    const states = ["recording", "sealed", "interrupted"] as const;
    const state = typeof value.state === "number" ? states[value.state]
        : states.find(s => typeof value.state === "string" && s === value.state.toLowerCase());
    if (!state) throw new Error("The CLI returned an unknown capture state.");
    const artifacts = value.artifacts.map((a: unknown): CaptureArtifact => {
        if (!record(a) || !validId(a.artifactId) || typeof a.kind !== "string")
            throw new Error("The CLI returned an invalid capture artifact.");
        const views = a.supportedViews;
        if (views !== undefined && (!Array.isArray(views) || !views.every(v => typeof v === "string")))
            throw new Error("The CLI returned invalid supported capture views.");
        let provenance: CaptureArtifact["provenance"];
        if (a.provenance != null) {
            if (!record(a.provenance)) throw new Error("The CLI returned invalid artifact provenance.");
            const p = a.provenance;
            if (p.processId != null && (!Number.isSafeInteger(p.processId) || Number(p.processId) <= 0))
                throw new Error("The CLI returned an invalid provenance process ID.");
            for (const key of ["runtimeName", "runtimeVersion", "startedAt"])
                if (p[key] != null && typeof p[key] !== "string")
                    throw new Error(`The CLI returned invalid provenance ${key}.`);
            provenance = {
                processId: p.processId == null ? undefined : Number(p.processId),
                runtimeName: typeof p.runtimeName === "string" ? p.runtimeName : undefined,
                runtimeVersion: typeof p.runtimeVersion === "string" ? p.runtimeVersion : undefined,
                startedAt: typeof p.startedAt === "string" ? p.startedAt : undefined,
            };
        }
        return { artifactId: a.artifactId, kind: a.kind, name: typeof a.name === "string" ? a.name : a.kind,
            supportedViews: views ?? [], provenance };
    });
    let qualityStatus: CaptureSummary["qualityStatus"] = "unknown";
    if (value.quality != null) {
        if (!record(value.quality)) throw new Error("The CLI returned invalid capture quality.");
        if (value.quality.isComplete != null && typeof value.quality.isComplete !== "boolean"
            || value.quality.isIncomplete != null && typeof value.quality.isIncomplete !== "boolean")
            throw new Error("The CLI returned invalid capture quality status.");
        qualityStatus = value.quality.isIncomplete === true ? "incomplete"
            : value.quality.isComplete === true ? "complete" : "unknown";
    }
    return { captureId: value.captureId, name: typeof value.name === "string" ? value.name : "Capture",
        createdUtc: typeof value.createdUtc === "string" ? value.createdUtc : "", state, artifacts, qualityStatus };
}

export function parseCaptureList(stdout: string): CaptureListPage {
    const data = parseEnvelope(stdout).data;
    if (!record(data) || !Array.isArray(data.captures)) throw new Error("The CLI capture list had an unexpected shape.");
    if (data.captures.length > 50) throw new Error("The CLI exceeded the requested capture page size.");
    if (data.nextAfterCaptureId != null && !validId(data.nextAfterCaptureId)) throw new Error("The CLI returned an invalid history cursor.");
    return { captures: data.captures.map(capture), nextAfterCaptureId: data.nextAfterCaptureId ?? undefined };
}
export const parseCaptureShow = (stdout: string): CaptureSummary => capture(parseEnvelope(stdout).capture);

export function captureQueryArgs(detail: CaptureSummary, artifactId: string, view: string): string[] {
    if (detail.state !== "sealed") throw new Error("Only sealed captures can be queried. Recover interrupted evidence with the CLI first.");
    const artifact = detail.artifacts.find(a => a.artifactId === artifactId);
    if (!artifact || !artifact.supportedViews.includes(view)) throw new Error("This is not an advertised offline view of the selected capture.");
    return ["query", "--capture-id", requireId(detail.captureId), "--artifact-id", requireId(artifactId), "--view", view, "--json"];
}
export function captureDeleteArgs(id: string, confirmed: boolean): string[] {
    if (!confirmed) throw new Error("Deleting a capture requires explicit confirmation.");
    return ["captures", "delete", "--capture-id", requireId(id), "--acknowledge-risk", "high", "--json"];
}

export function persistCaptureArgs(kind: "heap-live" | "heap-gcdump" | "thread-snapshot", pid: number, confirmed: boolean): string[] {
    if (!confirmed) throw new Error("A saved snapshot requires explicit high-risk acknowledgement.");
    if (!Number.isSafeInteger(pid) || pid <= 0) throw new Error("A positive process ID is required.");
    const command = kind === "thread-snapshot" ? ["collect", "--kind", kind]
        : ["inspect-heap", "--source", kind === "heap-live" ? "live" : "gcdump"];
    return [...command, "--pid", String(pid), "--persist", "--acknowledge-risk", "high", "--json"];
}
