import type { Readable } from "node:stream";

export const LIVE_EVENT_FRAME_MAX_BYTES = 16 * 1024;
export const LIVE_EVENT_CONTROL_LINE_MAX_BYTES = 64 * 1024;
export const LIVE_EVENT_HOST_MAX_COUNT = 256;
export const LIVE_EVENT_HOST_MAX_RETAINED_BYTES = 1024 * 1024;
export const LIVE_EVENT_BATCH_MAX_COUNT = 64;
export const LIVE_EVENT_BATCH_MAX_BYTES = 64 * 1024;
export const LIVE_EVENT_MAX_UPDATES_PER_SECOND = 10;
export const LIVE_EVENT_UI_MAX_COUNT = 2000;
export const LIVE_EVENT_UI_MAX_RETAINED_BYTES = 8 * 1024 * 1024;
export const LIVE_EVENT_UI_MAX_MOUNTED_ROWS = 100;
export const LIVE_EVENT_SEARCH_MAX_CHARS = 256;

export interface LiveEventObservation {
    sessionId: string;
    sequence: number;
    timestamp: string;
    receivedAt: string;
    provider: string;
    eventName: string;
    eventId: number;
    version: number;
    level: string;
    threadId: number | null;
    activityId: string | null;
    relatedActivityId: string | null;
    payload: Record<string, string>;
    metadataTruncated: boolean;
    payloadTruncated: boolean;
    omittedPayloadFields: number;
    omittedPayloadCharacters: number;
    unsupportedPayloadValues: number;
}

interface QueuedEvent {
    event: LiveEventObservation;
    serializedBytes: number;
    retainedBytes: number;
}

export interface LiveEventBatch {
    batchId: number;
    events: LiveEventObservation[];
}

/** A bounded, single-in-flight bridge from CLI output to one webview. */
export class LiveEventDeliveryQueue {
    private readonly pending: QueuedEvent[] = [];
    private inFlight?: { batchId: number; count: number; retainedBytes: number };
    private retainedCount = 0;
    private retainedBytes = 0;
    private nextBatchId = 1;
    private droppedEvents = 0;

    public get dropped(): number {
        return this.droppedEvents;
    }

    public get retainedEventCount(): number {
        return this.retainedCount;
    }

    public get retainedByteCount(): number {
        return this.retainedBytes;
    }

    public get hasInFlightBatch(): boolean {
        return this.inFlight !== undefined;
    }

    public enqueue(event: LiveEventObservation, serializedBytes?: number): boolean {
        const accountedSerializedBytes = serializedBytes ?? Buffer.byteLength(JSON.stringify(event), "utf8");
        if (!Number.isSafeInteger(accountedSerializedBytes) || accountedSerializedBytes < 0) {
            throw new Error("Serialized event byte size must be a non-negative safe integer.");
        }
        if (accountedSerializedBytes > LIVE_EVENT_FRAME_MAX_BYTES) {
            this.droppedEvents++;
            return false;
        }

        const retainedBytes = (accountedSerializedBytes * 2) + 1024;
        if (this.retainedCount >= LIVE_EVENT_HOST_MAX_COUNT
            || this.retainedBytes + retainedBytes > LIVE_EVENT_HOST_MAX_RETAINED_BYTES) {
            this.droppedEvents++;
            return false;
        }

        this.pending.push({ event, serializedBytes: accountedSerializedBytes, retainedBytes });
        this.retainedCount++;
        this.retainedBytes += retainedBytes;
        return true;
    }

    public takeBatch(includeEmpty = false): LiveEventBatch | undefined {
        if (this.inFlight || (this.pending.length === 0 && !includeEmpty)) {
            return undefined;
        }

        const events: LiveEventObservation[] = [];
        let serializedBytes = 1024;
        let retainedBytes = 0;
        while (this.pending.length > 0 && events.length < LIVE_EVENT_BATCH_MAX_COUNT) {
            const next = this.pending[0];
            if (serializedBytes + next.serializedBytes > LIVE_EVENT_BATCH_MAX_BYTES) {
                break;
            }
            this.pending.shift();
            events.push(next.event);
            serializedBytes += next.serializedBytes;
            retainedBytes += next.retainedBytes;
        }

        const batchId = this.nextBatchId++;
        this.inFlight = { batchId, count: events.length, retainedBytes };
        return { batchId, events };
    }

    public acknowledge(batchId: number): boolean {
        if (!this.inFlight || this.inFlight.batchId !== batchId) {
            return false;
        }

        this.retainedCount -= this.inFlight.count;
        this.retainedBytes -= this.inFlight.retainedBytes;
        this.inFlight = undefined;
        return true;
    }
}

export function isLiveEventObservation(value: unknown): value is LiveEventObservation {
    if (!isRecord(value)
        || typeof value.sessionId !== "string" || value.sessionId.length > 128
        || !Number.isSafeInteger(value.sequence) || Number(value.sequence) < 1
        || !boundedString(value.timestamp, 64)
        || !boundedString(value.receivedAt, 64)
        || !boundedString(value.provider, 256)
        || !boundedString(value.eventName, 256)
        || !Number.isSafeInteger(value.eventId)
        || !Number.isSafeInteger(value.version)
        || !boundedString(value.level, 32)
        || !(value.threadId === null || Number.isSafeInteger(value.threadId))
        || !(value.activityId === null || boundedString(value.activityId, 64))
        || !(value.relatedActivityId === null || boundedString(value.relatedActivityId, 64))
        || !isRecord(value.payload)
        || typeof value.metadataTruncated !== "boolean"
        || typeof value.payloadTruncated !== "boolean"
        || !nonNegativeInteger(value.omittedPayloadFields)
        || !nonNegativeInteger(value.omittedPayloadCharacters)
        || !nonNegativeInteger(value.unsupportedPayloadValues)) {
        return false;
    }

    let payloadCharacters = 0;
    let payloadFields = 0;
    for (const name in value.payload) {
        if (!Object.prototype.hasOwnProperty.call(value.payload, name)) continue;
        payloadFields++;
        if (payloadFields > 32) return false;
        const fieldValue = value.payload[name];
        if (name.length > 128 || typeof fieldValue !== "string" || fieldValue.length > 1024) return false;
        payloadCharacters += name.length + fieldValue.length;
    }

    return payloadCharacters <= 2048
        && value.payloadTruncated === (
            value.omittedPayloadFields > 0
            || value.omittedPayloadCharacters > 0
            || value.unsupportedPayloadValues > 0
        );
}

function boundedString(value: unknown, maxLength: number): value is string {
    return typeof value === "string" && value.length > 0 && value.length <= maxLength;
}

function nonNegativeInteger(value: unknown): value is number {
    return Number.isSafeInteger(value) && Number(value) >= 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

export interface ProtocolLineReader {
    on(event: "line", listener: (line: string, byteLength: number) => void): this;
    on(event: "oversized", listener: () => void): this;
    close(): void;
}

/** Reads JSONL without retaining more than the configured UTF-8 frame budget. */
export class BoundedProtocolLineReader implements ProtocolLineReader {
    private pending = "";
    private pendingBytes = 0;
    private oversized = false;
    private readonly lineListeners: Array<(line: string, byteLength: number) => void> = [];
    private readonly oversizedListeners: Array<() => void> = [];

    public constructor(private readonly input: Readable, private readonly maxLineBytes: number) {
        input.setEncoding("utf8");
        input.on("data", this.onData);
        input.once("end", this.onEnd);
    }

    public on(event: "line", listener: (line: string) => void): this;
    public on(event: "line", listener: (line: string, byteLength: number) => void): this;
    public on(event: "oversized", listener: () => void): this;
    public on(event: "line" | "oversized", listener: ((line: string, byteLength: number) => void) | (() => void)): this {
        if (event === "line") this.lineListeners.push(listener as (line: string, byteLength: number) => void);
        else this.oversizedListeners.push(listener as () => void);
        return this;
    }

    public close(): void {
        this.input.removeListener("data", this.onData);
        this.input.removeListener("end", this.onEnd);
        this.input.resume();
        this.lineListeners.length = 0;
        this.oversizedListeners.length = 0;
    }

    private readonly onData = (chunk: string | Buffer): void => {
        const text = chunk.toString();
        let offset = 0;
        while (offset < text.length) {
            const newline = text.indexOf("\n", offset);
            const end = newline < 0 ? text.length : newline;
            const segment = text.slice(offset, end);
            const segmentBytes = Buffer.byteLength(segment, "utf8");
            if (!this.oversized && this.pendingBytes + segmentBytes <= this.maxLineBytes) {
                this.pending += segment;
                this.pendingBytes += segmentBytes;
            } else {
                this.oversized = true;
                this.pending = "";
                this.pendingBytes = 0;
            }

            if (newline >= 0) {
                this.finishLine();
                offset = newline + 1;
            } else {
                offset = text.length;
            }
        }
    };

    private readonly onEnd = (): void => {
        if (this.pending.length > 0 || this.oversized) this.finishLine();
    };

    private finishLine(): void {
        if (this.oversized) {
            for (const listener of this.oversizedListeners) listener();
        } else {
            const hasTrailingCarriageReturn = this.pending.endsWith("\r");
            const line = hasTrailingCarriageReturn ? this.pending.slice(0, -1) : this.pending;
            const byteLength = this.pendingBytes - (hasTrailingCarriageReturn ? 1 : 0);
            for (const listener of this.lineListeners) listener(line, byteLength);
        }
        this.pending = "";
        this.pendingBytes = 0;
        this.oversized = false;
    }
}
