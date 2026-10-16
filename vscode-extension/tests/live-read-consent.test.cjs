const assert = require("node:assert/strict");
const test = require("node:test");
const { LiveReadConsent } = require("../out/liveReadConsent.js");

test("session grants are separate per operation and never persist to a different snapshot", async () => {
    const states = [];
    const consent = new LiveReadConsent(state => states.push(state));
    let scope = {};
    let prompts = 0;
    const prompt = async () => { prompts++; return "session"; };
    assert.equal(await consent.acknowledge("object", () => scope, prompt), true);
    assert.equal(await consent.acknowledge("object", () => scope, prompt), true);
    assert.equal(prompts, 1);
    assert.equal(await consent.acknowledge("gcroot", () => scope, prompt), true);
    assert.equal(prompts, 2);
    assert.deepEqual(states.at(-1).grants, ["object", "gcroot"]);
    scope = {};
    await consent.acknowledge("object", () => scope, prompt);
    assert.equal(prompts, 3);
    assert.deepEqual(states.at(-1).grants, ["object"]);
    assert.equal(states.at(-1).history.length, 1);
});

test("one-shot decisions, cancellation and missing context do not create remembered permission", async () => {
    const states = [];
    const consent = new LiveReadConsent(state => states.push(state));
    const scope = {};
    assert.equal(await consent.acknowledge("object", () => scope, async () => "once"), true);
    assert.equal(await consent.acknowledge("object", () => scope, async () => "cancel"), false);
    assert.deepEqual(states.at(-1).grants, []);
    assert.equal(await consent.acknowledge("object", () => undefined, async () => { throw Error("must not prompt"); }), false);
});

test("revocation or a changed context while the native prompt is open cancels the read", async () => {
    const states = [];
    const consent = new LiveReadConsent(state => states.push(state));
    let scope = {};
    let resolve;
    let pending = consent.acknowledge("object", () => scope, () => new Promise(done => { resolve = done; }));
    consent.revoke();
    resolve("session");
    assert.equal(await pending, false);
    assert.deepEqual(states.at(-1).grants, []);
    pending = consent.acknowledge("object", () => scope, () => new Promise(done => { resolve = done; }));
    scope = {};
    resolve("once");
    assert.equal(await pending, false);
});

test("revocation affects future reads and bounded history never records target values or addresses", async () => {
    const states = [];
    const consent = new LiveReadConsent(state => states.push(state));
    const scope = {};
    await consent.acknowledge("object", () => scope, async () => "session");
    for (let i = 0; i < 40; i++) await consent.acknowledge("object", () => scope, async () => { throw Error("unexpected prompt"); });
    assert.equal(states.at(-1).history.length, 30);
    consent.revoke();
    assert.deepEqual(states.at(-1).grants, []);
    assert.equal(await consent.acknowledge("object", () => scope, async () => "cancel"), false);
    consent.clear();
    assert.deepEqual(states.at(-1).history, []);
    assert.deepEqual(states.at(-1).grants, []);
});
