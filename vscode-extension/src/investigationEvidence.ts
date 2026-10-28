export const INVESTIGATION_EVIDENCE_VERSION = 1;
export const INVESTIGATION_EVIDENCE_MAX_ITEMS = 7;
export const INVESTIGATION_EVIDENCE_MAX_ITEM_BYTES = 1024 * 1024;
export const INVESTIGATION_EVIDENCE_MAX_TOTAL_BYTES = 4 * 1024 * 1024;

export const INVESTIGATION_EVIDENCE_KINDS = [
    "cpu", "allocation", "off_cpu", "contention", "threadpool", "gc", "requests",
] as const;

export type InvestigationEvidenceKind = typeof INVESTIGATION_EVIDENCE_KINDS[number];
export type InvestigationIdentityStatus = "verified" | "unknown" | "changed";
export type InvestigationWindowKind = "observed" | "reported" | "requested";
export type InvestigationCompletion = "normal" | "early" | "cancelled" | "failed" | "unknown";
export type InvestigationAvailabilityReason =
    | "expired" | "capacity-evicted" | "owner-replaced" | "invalidated"
    | "connection-closed" | "process-exited" | "not-retained" | "unknown";
export type InvestigationTemporalRelation = "reported-intersection" | "adjacent" | "none" | "unknown";

export interface InvestigationEvidenceCapability {
    version: 1;
    kinds: InvestigationEvidenceKind[];
    maxItems: 7;
    maxItemBytes: number;
    maxTotalBytes: number;
}

export interface InvestigationEvidenceDescriptor {
    version: 1;
    evidenceId: string;
    kind: InvestigationEvidenceKind;
    collectorKind: string;
    target: {
        processId: number;
        processStartedAt: string | null;
        identityStatus: InvestigationIdentityStatus;
    };
    window: {
        kind: InvestigationWindowKind;
        start: string | null;
        end: string | null;
        clock: {
            kind: "diagnostic-host-utc";
            instanceId: string;
            comparison: "reported-only";
        };
        completion: InvestigationCompletion;
    };
    retention: {
        connection: { instanceId: string; storeId: string };
        projection: { owner: "extension-context"; mode: "response-copy" };
        drilldown: {
            owner: "cli-connection" | "none";
            connectionId: string;
            storeId: string;
            handle: string | null;
            status: "available" | "unavailable" | "display-only";
            unavailableReason: InvestigationAvailabilityReason | null;
            expiresNoLaterThan: string | null;
            invalidatedBy: InvestigationAvailabilityReason[];
        };
    };
    backend: { kind: string; source: string };
    quality: { status: "reported" | "unknown"; source: string; limitations: string[] };
    safety: { risk: "moderate" | "high"; authorization: string };
    mayContainSensitiveData: boolean;
    limitations: string[];
    availability: "available" | "unavailable";
    unavailableReason: "target-changed" | null;
}

export interface InvestigationEvidenceItem {
    descriptor: InvestigationEvidenceDescriptor;
    projection: unknown;
    retainedBytes: number;
}

export type InvestigationAdmission =
    | { admitted: true; item: InvestigationEvidenceItem; replacedEvidenceId?: string }
    | { admitted: false; reason: "context-closed" | "target-generation-mismatch" | "invalid-descriptor"
        | "target-changed" | "item-too-large" | "context-too-large" | "item-count-exceeded" };

const record = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);
const text = (value: unknown, max = 2048): value is string =>
    typeof value === "string" && value.length > 0 && value.length <= max;
const nullableDate = (value: unknown): value is string | null =>
    value === null || typeof value === "string" && value.length <= 64 && Number.isFinite(Date.parse(value));
const stringArray = (value: unknown, maxItems: number, maxText = 2048): value is string[] =>
    Array.isArray(value) && value.length <= maxItems && value.every(item => text(item, maxText));
const kind = (value: unknown): value is InvestigationEvidenceKind =>
    typeof value === "string" && INVESTIGATION_EVIDENCE_KINDS.includes(value as InvestigationEvidenceKind);
const oneOf = <T extends string>(value: unknown, values: readonly T[]): value is T =>
    typeof value === "string" && values.includes(value as T);

export function supportsInvestigationEvidence(value: unknown): value is InvestigationEvidenceCapability {
    if (!record(value) || !Array.isArray(value.kinds)) return false;
    const kinds = value.kinds;
    return value.version === INVESTIGATION_EVIDENCE_VERSION
        && value.maxItems === INVESTIGATION_EVIDENCE_MAX_ITEMS
        && value.maxItemBytes === INVESTIGATION_EVIDENCE_MAX_ITEM_BYTES
        && value.maxTotalBytes === INVESTIGATION_EVIDENCE_MAX_TOTAL_BYTES
        && kinds.length === INVESTIGATION_EVIDENCE_KINDS.length
        && INVESTIGATION_EVIDENCE_KINDS.every(expected => kinds.includes(expected));
}

export function parseInvestigationEvidenceDescriptor(value: unknown): InvestigationEvidenceDescriptor | undefined {
    if (!record(value) || value.version !== INVESTIGATION_EVIDENCE_VERSION
        || !text(value.evidenceId, 128) || !kind(value.kind) || !text(value.collectorKind, 64)
        || !record(value.target) || !record(value.window) || !record(value.retention)
        || !record(value.backend) || !record(value.quality) || !record(value.safety)) return undefined;
    const target = value.target;
    const window = value.window;
    const retention = value.retention;
    if (!Number.isSafeInteger(target.processId) || Number(target.processId) <= 0
        || !nullableDate(target.processStartedAt)
        || !oneOf(target.identityStatus, ["verified", "unknown", "changed"] as const)
        || !oneOf(window.kind, ["observed", "reported", "requested"] as const)
        || !nullableDate(window.start) || !nullableDate(window.end)
        || !record(window.clock)
        || window.clock.kind !== "diagnostic-host-utc"
        || !text(window.clock.instanceId, 128) || window.clock.comparison !== "reported-only"
        || !oneOf(window.completion, ["normal", "early", "cancelled", "failed", "unknown"] as const)
        || !record(retention.connection) || !text(retention.connection.instanceId, 128)
        || !text(retention.connection.storeId, 128)
        || !record(retention.projection) || retention.projection.owner !== "extension-context"
        || retention.projection.mode !== "response-copy"
        || !record(retention.drilldown)
        || !oneOf(retention.drilldown.owner, ["cli-connection", "none"] as const)
        || !text(retention.drilldown.connectionId, 128) || !text(retention.drilldown.storeId, 128)
        || !(retention.drilldown.handle === null || text(retention.drilldown.handle, 128))
        || !oneOf(retention.drilldown.status, ["available", "unavailable", "display-only"] as const)
        || !(retention.drilldown.unavailableReason === null
            || oneOf(retention.drilldown.unavailableReason, [
                "expired", "capacity-evicted", "owner-replaced", "invalidated",
                "connection-closed", "process-exited", "not-retained", "unknown",
            ] as const))
        || !nullableDate(retention.drilldown.expiresNoLaterThan)
        || !Array.isArray(retention.drilldown.invalidatedBy)
        || !retention.drilldown.invalidatedBy.every(reason => oneOf(reason, [
            "expired", "capacity-evicted", "owner-replaced", "invalidated",
            "connection-closed", "process-exited", "not-retained", "unknown",
        ] as const))
        || !text(value.backend.kind, 64) || !text(value.backend.source, 64)
        || !oneOf(value.quality.status, ["reported", "unknown"] as const)
        || !text(value.quality.source, 64) || !stringArray(value.quality.limitations, 100)
        || !oneOf(value.safety.risk, ["moderate", "high"] as const)
        || !text(value.safety.authorization, 64)
        || typeof value.mayContainSensitiveData !== "boolean"
        || !stringArray(value.limitations, 20)
        || !oneOf(value.availability, ["available", "unavailable"] as const)
        || !(value.unavailableReason === null || value.unavailableReason === "target-changed")) return undefined;

    if (retention.connection.instanceId !== window.clock.instanceId
        || retention.drilldown.connectionId !== retention.connection.instanceId
        || retention.drilldown.storeId !== retention.connection.storeId
        || (retention.drilldown.owner === "cli-connection") !== (retention.drilldown.handle !== null)
        || (retention.drilldown.status === "available") !== (retention.drilldown.handle !== null
            && retention.drilldown.unavailableReason === null)
        || target.identityStatus === "verified" && target.processStartedAt === null
        || target.identityStatus === "changed" && (value.availability !== "unavailable"
            || value.unavailableReason !== "target-changed")
        || window.kind !== "requested" && (window.start === null || window.end === null)
        || window.start !== null && window.end !== null && Date.parse(window.end) < Date.parse(window.start)) return undefined;

    return value as unknown as InvestigationEvidenceDescriptor;
}

export class InvestigationEvidenceStore {
    private generation?: number;
    private retainedBytes = 0;
    private readonly itemsByKind = new Map<InvestigationEvidenceKind, InvestigationEvidenceItem>();
    private readonly expiryTimersByKind = new Map<InvestigationEvidenceKind, NodeJS.Timeout>();

    public open(targetGeneration: number): void {
        if (this.generation === targetGeneration) return;
        this.clear();
        this.generation = targetGeneration;
    }

    public clear(): void {
        for (const timer of this.expiryTimersByKind.values()) clearTimeout(timer);
        this.expiryTimersByKind.clear();
        this.itemsByKind.clear();
        this.retainedBytes = 0;
        this.generation = undefined;
    }

    public snapshot(): readonly InvestigationEvidenceItem[] {
        return [...this.itemsByKind.values()];
    }

    public admit(targetGeneration: number, descriptorValue: unknown, projection: unknown): InvestigationAdmission {
        if (this.generation === undefined) return { admitted: false, reason: "context-closed" };
        if (this.generation !== targetGeneration) return { admitted: false, reason: "target-generation-mismatch" };
        const descriptor = parseInvestigationEvidenceDescriptor(descriptorValue);
        if (!descriptor) return { admitted: false, reason: "invalid-descriptor" };
        if (descriptor.target.identityStatus === "changed" || descriptor.availability !== "available")
            return { admitted: false, reason: "target-changed" };

        const projectionJson = JSON.stringify(projection);
        if (projectionJson === undefined) return { admitted: false, reason: "invalid-descriptor" };
        const projectionBytes = Buffer.byteLength(projectionJson, "utf8");
        if (projectionBytes > INVESTIGATION_EVIDENCE_MAX_ITEM_BYTES)
            return { admitted: false, reason: "item-too-large" };
        const copy = JSON.parse(projectionJson) as unknown;
        const descriptorCopy = JSON.parse(JSON.stringify(descriptor)) as InvestigationEvidenceDescriptor;
        const retainedBytes = retainedItemBudget(descriptorCopy, copy);
        const previous = this.itemsByKind.get(descriptor.kind);
        const nextTotal = this.retainedBytes - (previous?.retainedBytes ?? 0) + retainedBytes;
        if (!previous && this.itemsByKind.size >= INVESTIGATION_EVIDENCE_MAX_ITEMS)
            return { admitted: false, reason: "item-count-exceeded" };
        if (nextTotal > INVESTIGATION_EVIDENCE_MAX_TOTAL_BYTES)
            return { admitted: false, reason: "context-too-large" };

        const item = { descriptor: descriptorCopy, projection: copy, retainedBytes };
        const previousTimer = this.expiryTimersByKind.get(descriptor.kind);
        if (previousTimer) clearTimeout(previousTimer);
        this.itemsByKind.set(descriptor.kind, item);
        this.retainedBytes = nextTotal;
        this.scheduleExpiry(descriptor.kind, descriptor.evidenceId,
            descriptor.retention.drilldown.expiresNoLaterThan);
        return {
            admitted: true,
            item,
            ...(previous ? { replacedEvidenceId: previous.descriptor.evidenceId } : {}),
        };
    }

    public markConnectionClosed(connectionId: string): void {
        for (const item of this.itemsByKind.values()) {
            if (item.descriptor.retention.drilldown.owner === "cli-connection"
                && item.descriptor.retention.drilldown.connectionId === connectionId)
                this.markUnavailable(item.descriptor.evidenceId, "connection-closed");
        }
    }

    public markUnavailable(evidenceId: string, reason: InvestigationAvailabilityReason): boolean {
        for (const [itemKind, item] of this.itemsByKind) {
            if (item.descriptor.evidenceId !== evidenceId) continue;
            if (item.descriptor.retention.drilldown.status !== "available") return false;
            const updated = structuredClone(item);
            updated.descriptor.retention.drilldown.status = "display-only";
            updated.descriptor.retention.drilldown.unavailableReason = reason;
            this.itemsByKind.set(itemKind, { ...updated, retainedBytes: item.retainedBytes });
            const timer = this.expiryTimersByKind.get(itemKind);
            if (timer) clearTimeout(timer);
            this.expiryTimersByKind.delete(itemKind);
            return true;
        }
        return false;
    }

    public expireUpperBounds(now = Date.now()): void {
        for (const item of this.itemsByKind.values()) {
            const upperBound = item.descriptor.retention.drilldown.expiresNoLaterThan;
            if (upperBound !== null && Date.parse(upperBound) <= now)
                this.markUnavailable(item.descriptor.evidenceId, "expired");
        }
    }

    private scheduleExpiry(
        itemKind: InvestigationEvidenceKind,
        evidenceId: string,
        expiresNoLaterThan: string | null,
    ): void {
        if (expiresNoLaterThan === null) return;
        const expiresAt = Date.parse(expiresNoLaterThan);
        if (!Number.isFinite(expiresAt)) return;
        const schedule = (): void => {
            const remaining = expiresAt - Date.now();
            if (remaining <= 0) {
                this.expiryTimersByKind.delete(itemKind);
                this.markUnavailable(evidenceId, "expired");
                return;
            }
            const timer = setTimeout(schedule, Math.min(remaining, 2_147_483_647));
            timer.unref();
            this.expiryTimersByKind.set(itemKind, timer);
        };
        schedule();
    }
}

function retainedItemBudget(descriptor: InvestigationEvidenceDescriptor, projection: unknown): number {
    const actual = Buffer.byteLength(JSON.stringify({ descriptor, projection }), "utf8");
    if (descriptor.retention.drilldown.owner !== "cli-connection") return actual;
    const unavailable = structuredClone(descriptor);
    unavailable.retention.drilldown.status = "display-only";
    unavailable.retention.drilldown.unavailableReason = "connection-closed";
    return Math.max(actual, Buffer.byteLength(JSON.stringify({ descriptor: unavailable, projection }), "utf8"));
}

export function classifyInvestigationTemporalRelation(
    left: InvestigationEvidenceDescriptor,
    right: InvestigationEvidenceDescriptor,
): InvestigationTemporalRelation {
    if (left.target.identityStatus !== "verified" || right.target.identityStatus !== "verified"
        || left.target.processId !== right.target.processId
        || left.target.processStartedAt !== right.target.processStartedAt
        || left.window.kind === "requested" || right.window.kind === "requested"
        || left.window.start === null || left.window.end === null
        || right.window.start === null || right.window.end === null
        || left.window.clock.instanceId !== right.window.clock.instanceId
        || left.window.clock.comparison !== "reported-only"
        || right.window.clock.comparison !== "reported-only") return "unknown";
    const leftStart = Date.parse(left.window.start);
    const leftEnd = Date.parse(left.window.end);
    const rightStart = Date.parse(right.window.start);
    const rightEnd = Date.parse(right.window.end);
    if (![leftStart, leftEnd, rightStart, rightEnd].every(Number.isFinite)) return "unknown";
    if (leftEnd === rightStart || rightEnd === leftStart) return "adjacent";
    if (leftStart < rightEnd && rightStart < leftEnd) return "reported-intersection";
    return "none";
}
