const assert = require("node:assert/strict");
const test = require("node:test");

const { buildQueryFrame } = require("../out/protocol.js");
const { TypeInstanceNavigationController } = require("../out/snapshotNavigation.js");

const identity = {
    typeFullName: "Sample.Cache",
    moduleName: "Sample.dll",
    moduleVersionId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    metadataToken: 0x02000001,
};

test("type-instance controller keeps exact identity/cursor and posts a correlated bounded page", async () => {
    const calls = [];
    const posts = [];
    const controller = new TypeInstanceNavigationController({
        currentHeapHandle: () => "heap-1",
        acknowledge: async typeName => { calls.push(["ack", typeName]); return true; },
        query: async (handle, typeIdentity, cursor, acknowledgeRisk) => {
            calls.push(["query", handle, typeIdentity, cursor, acknowledgeRisk]);
            return {
                handle,
                view: "type-instances",
                requestedTypeIdentity: typeIdentity,
                typeInstances: [{ address: "0xfedcba9876543210", size: 48 }],
                nextCursor: "v1.next",
                instanceEnumerationComplete: false,
            };
        },
        post: message => posts.push(message),
        log() {},
    });

    assert.equal(await controller.handle({
        type: "queryHeapInstances",
        selectionId: 7,
        typeIdentity: identity,
        cursor: "v1.current",
    }), true);

    assert.deepEqual(calls, [
        ["ack", "Sample.Cache"],
        ["query", "heap-1", identity, "v1.current", true],
    ]);
    assert.equal(posts.length, 1);
    assert.equal(posts[0].type, "heapInstances");
    assert.equal(posts[0].selectionId, 7);
    assert.equal(posts[0].result.typeInstances[0].address, "0xfedcba9876543210");

    assert.deepEqual(
        buildQueryFrame("q", "heap-1", "type-instances", undefined, undefined, undefined, {
            typeIdentity: identity, pageSize: 50, cursor: "v1.current", acknowledgeRisk: true,
        }),
        {
            type: "query",
            requestId: "q",
            handle: "heap-1",
            view: "type-instances",
            typeIdentity: identity,
            topN: 50,
            cursor: "v1.current",
            acknowledgeRisk: "high",
        },
    );
});

test("type-instance controller rejects stale context after acknowledgement and malformed identities", async () => {
    let handle = "heap-1";
    let queried = false;
    const posts = [];
    const controller = new TypeInstanceNavigationController({
        currentHeapHandle: () => handle,
        acknowledge: async () => { handle = "heap-2"; return true; },
        query: async () => { queried = true; throw new Error("must not run"); },
        post: message => posts.push(message),
        log() {},
    });

    await controller.handle({ type: "queryHeapInstances", selectionId: 1, typeIdentity: identity });
    assert.equal(queried, false);
    assert.equal(posts[0].type, "heapInstancesError");
    assert.match(posts[0].message, /context changed/);

    posts.length = 0;
    await controller.handle({
        type: "queryHeapInstances",
        selectionId: 2,
        typeIdentity: { typeFullName: "", metadataToken: -1 },
    });
    assert.equal(posts.length, 0);
    assert.equal(queried, false);
});
