const assert = require("node:assert/strict");
const test = require("node:test");
const { Readable } = require("node:stream");
const {
    BoundedProtocolLineReader,
    LIVE_EVENT_BATCH_MAX_BYTES,
    LIVE_EVENT_FRAME_MAX_BYTES,
    LIVE_EVENT_HOST_MAX_COUNT,
    LIVE_EVENT_HOST_MAX_RETAINED_BYTES,
    LiveEventDeliveryQueue,
    isLiveEventObservation,
} = require("../out/liveEvents.js");

function observation(sequence, overrides = {}) {
    return {
        sessionId: "session-1",
        sequence,
        timestamp: "2026-01-01T00:00:00.0000000Z",
        receivedAt: "2026-01-01T00:00:00.0000000Z",
        provider: "System.Runtime",
        eventName: "EventCounters",
        eventId: 1,
        version: 0,
        level: "Informational",
        threadId: 1,
        activityId: null,
        relatedActivityId: null,
        payload: { Name: "cpu-usage", Value: "12.5" },
        metadataTruncated: false,
        payloadTruncated: false,
        omittedPayloadFields: 0,
        omittedPayloadCharacters: 0,
        unsupportedPayloadValues: 0,
        ...overrides,
    };
}

test("host event queue stays count/byte bounded under 100,000 observations", () => {
    const queue = new LiveEventDeliveryQueue();
    for (let index = 1; index <= 100_000; index++) queue.enqueue(observation(index));

    assert.equal(queue.retainedEventCount, LIVE_EVENT_HOST_MAX_COUNT);
    assert.ok(queue.retainedByteCount <= LIVE_EVENT_HOST_MAX_RETAINED_BYTES);
    assert.equal(queue.dropped, 100_000 - LIVE_EVENT_HOST_MAX_COUNT);

    const first = queue.takeBatch();
    assert.ok(first);
    assert.ok(first.events.length <= 64);
    assert.ok(Buffer.byteLength(JSON.stringify(first)) < LIVE_EVENT_BATCH_MAX_BYTES);
    assert.equal(queue.takeBatch(), undefined, "a second batch must wait for acknowledgement");
    assert.equal(queue.acknowledge(first.batchId + 1), false);
    assert.equal(queue.acknowledge(first.batchId), true);
    assert.equal(queue.acknowledge(first.batchId), false);
    assert.equal(queue.retainedEventCount, LIVE_EVENT_HOST_MAX_COUNT - first.events.length);
});

test("host event queue enforces its byte limit before the count limit", () => {
    const queue = new LiveEventDeliveryQueue();
    const large = observation(1, {
        payload: { first: "a".repeat(1000), second: "b".repeat(1000) },
    });
    for (let index = 1; index <= 256; index++) queue.enqueue({ ...large, sequence: index });

    assert.ok(queue.retainedEventCount < LIVE_EVENT_HOST_MAX_COUNT);
    assert.ok(queue.retainedByteCount <= LIVE_EVENT_HOST_MAX_RETAINED_BYTES);
    assert.ok(queue.retainedByteCount > LIVE_EVENT_HOST_MAX_RETAINED_BYTES - (2 * 8_000));
    assert.equal(queue.dropped, 256 - queue.retainedEventCount);
});

test("host event queue reuses the validated UTF-8 frame byte count", () => {
    const queue = new LiveEventDeliveryQueue();
    const event = observation(1);
    const frameBytes = Buffer.byteLength(JSON.stringify(event), "utf8") + 128;

    assert.equal(queue.enqueue(event, frameBytes), true);
    assert.equal(queue.retainedByteCount, (frameBytes * 2) + 1024);
});

test("host queue reports when a limit has been reached", () => {
    const queue = new LiveEventDeliveryQueue();
    assert.equal(queue.isAtCapacity, false);
    for (let index = 1; index <= LIVE_EVENT_HOST_MAX_COUNT; index++) queue.enqueue(observation(index));
    assert.equal(queue.isAtCapacity, true);
});

test("event validation enforces payload schema bounds and truncation accounting", () => {
    assert.equal(isLiveEventObservation(observation(1)), true);
    assert.equal(isLiveEventObservation(observation(1, { payloadTruncated: true })), false);
    assert.equal(isLiveEventObservation(observation(1, {
        payload: Object.fromEntries(Array.from({ length: 33 }, (_, index) => [`field${index}`, "x"])),
    })), false);
    assert.equal(isLiveEventObservation(observation(1, {
        payload: { field: "x".repeat(1025) },
    })), false);
});

test("queue rejects a frame larger than the event frame contract", () => {
    const queue = new LiveEventDeliveryQueue();
    assert.equal(queue.enqueue(observation(1, { provider: "x".repeat(LIVE_EVENT_FRAME_MAX_BYTES) })), false);
    assert.equal(queue.dropped, 1);
    assert.equal(queue.retainedEventCount, 0);
});

test("bounded JSONL reader handles multibyte UTF-8 and flags oversized lines", async () => {
    const input = Readable.from([
        Buffer.from('{"message":"'),
        Buffer.from("😀"),
        Buffer.from('"}\n'),
        Buffer.from("x".repeat(65_537) + "\n"),
        Buffer.from('{"ok":true}\n'),
    ]);
    const reader = new BoundedProtocolLineReader(input, 65_536);
    const lines = [];
    let oversized = 0;
    reader.on("line", (line, byteLength) => lines.push({ line, byteLength }));
    reader.on("oversized", () => { oversized++; });
    await new Promise(resolve => input.once("end", resolve));

    assert.deepEqual(lines, [
        { line: '{"message":"😀"}', byteLength: Buffer.byteLength('{"message":"😀"}', "utf8") },
        { line: '{"ok":true}', byteLength: Buffer.byteLength('{"ok":true}', "utf8") },
    ]);
    assert.equal(oversized, 1);
    reader.close();
});

test("bounded JSONL reader pauses at a frame boundary and resumes buffered text", async () => {
    const input = Readable.from([
        Buffer.from('{"one":1}\n{"two":2}\n'),
        Buffer.from('{"three":3}\n'),
    ]);
    const ended = new Promise(resolve => input.once("end", resolve));
    const reader = new BoundedProtocolLineReader(input, 65_536);
    const lines = [];
    reader.on("line", line => {
        lines.push(line);
        if (lines.length === 1) reader.pause();
    });

    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(lines, ['{"one":1}']);
    reader.resume();
    await ended;

    assert.deepEqual(lines, ['{"one":1}', '{"two":2}', '{"three":3}']);
    reader.close();
});
