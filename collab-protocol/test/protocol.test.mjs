// Copyright the AGENT DAW protocol authors. Licensed under the Apache License, Version 2.0.
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { validateManifest, validateChangeSet } from "../src/validate.mjs";
import { applyChangeSet, diff, canonicalManifest, ApplyError } from "../src/tree.mjs";
import { merge } from "../src/merge.mjs";

const ex = (f) => JSON.parse(readFileSync(new URL(`../examples/${f}`, import.meta.url), "utf8"));
const master = ex("master-v1.json");
const drums = ex("proposal-drums.changeset.json");
import { hex, rng, randomManifest, mutate } from "./gen.mjs";

test("the example manifest and change set validate", () => {
  assert.deepEqual(validateManifest(master), { ok: true, errors: [] });
  assert.deepEqual(validateChangeSet(drums), { ok: true, errors: [] });
});

test("schema rejects malformed input", () => {
  const bad = structuredClone(master); bad.root.id = "NOT-HEX";
  assert.equal(validateManifest(bad).ok, false);
  const dup = structuredClone(master); dup.root.lists.tracks.push(structuredClone(dup.root.lists.tracks[0]));
  assert.match(validateManifest(dup).errors[0], /duplicate node id/);
  const noType = structuredClone(master); delete noType.root.type;
  assert.equal(validateManifest(noType).ok, false);
  const badBlob = structuredClone(master); badBlob.root.lists.tracks[0].blobs.instrumentState = "md5:abc";
  assert.equal(validateManifest(badBlob).ok, false);
  assert.equal(validateChangeSet({ ...drums, ops: [{ op: "explode", id: hex(1) }] }).ok, false);
  assert.equal(validateChangeSet({ ...drums, ops: [{ op: "add", parent: hex(1), list: "x", node: { id: hex(2), type: "t" } }] }).ok, false, "add requires `after`");
});

test("the drum proposal applies to the master and the input is untouched", () => {
  const before = structuredClone(master);
  const next = applyChangeSet(master, drums);
  assert.deepEqual(master, before);
  assert.deepEqual(next.root.lists.tracks.map((t) => t.props.name), ["Bass", "Drums"]);
  assert.equal(next.root.lists.tracks[1].lists.clips[0].id, "cccccccccccccccccccccccccccccc02");
  assert.equal(validateManifest(next).ok, true);
});

test("apply is atomic: a failing op leaves no partial result", () => {
  const cs = { ...drums, ops: [...drums.ops, { op: "remove", id: hex(999) }] };
  assert.throws(() => applyChangeSet(master, cs), (e) => e instanceof ApplyError && e.code === "missing_node" && e.opIndex === 2);
});

test("apply refuses wrong project, stale base, duplicates, cycles, root edits, bad anchors, failed preconditions", () => {
  const code = (cs) => { try { applyChangeSet(master, cs); } catch (e) { return e.code; } return "no error"; };
  assert.equal(code({ ...drums, projectId: hex(7) }), "wrong_project");
  assert.equal(code({ ...drums, baseVersion: "v0" }), "stale_base");
  assert.equal(code({ ...drums, ops: [drums.ops[0], drums.ops[0]] }), "duplicate_id");
  const t1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", c1 = "cccccccccccccccccccccccccccccc01", root = master.root.id;
  assert.equal(code({ ...drums, ops: [{ op: "move", id: t1, parent: c1, list: "x", after: null }] }), "cycle");
  assert.equal(code({ ...drums, ops: [{ op: "remove", id: root }] }), "root_immutable");
  assert.equal(code({ ...drums, ops: [{ op: "move", id: root, parent: t1, list: "x", after: null }] }), "root_immutable");
  assert.equal(code({ ...drums, ops: [{ op: "add", parent: root, list: "tracks", after: hex(5), node: { id: hex(6), type: "track" } }] }), "missing_anchor");
  assert.equal(code({ ...drums, ops: [{ op: "set", id: c1, key: "length", value: 8, expect: { value: 99 } }] }), "precondition");
  assert.equal(code({ ...drums, ops: [{ op: "set", id: c1, key: "length", value: 8, expect: { value: 16 } }] }), "no error");
  assert.equal(code({ ...drums, ops: [{ op: "set", id: c1, key: "newkey", value: 1, expect: { absent: true } }] }), "no error");
  assert.equal(code({ ...drums, ops: [{ op: "set", id: c1, key: "length", value: 1, expect: { absent: true } }] }), "precondition");
});

test("diff of equal manifests is empty; diff of the drum result reproduces the proposal's effect", () => {
  assert.deepEqual(diff(master, master).ops, []);
  const next = applyChangeSet(master, drums);
  const d = diff(master, next);
  assert.deepEqual(applyChangeSet(master, d), canonicalManifest(next));
  assert.equal(validateChangeSet(d).ok, true);
});

test("property: apply(base, diff(base, head)) equals head over 1500 random edits (incl. moves, reorders, removals)", () => {
  let checked = 0, withMoves = 0, withRemoves = 0, withAdds = 0;
  for (let seed = 1; seed <= 1500; seed++) {
    const r = rng(seed); let id = 1000;
    const nextId = () => ++id;
    const base = randomManifest(r, nextId);
    const head = mutate(base, r, nextId);
    if (validateManifest(head).ok === false) continue; // generator produced an invalid tree (e.g. shared node); skip, counted below
    const d = diff(base, head);
    const got = applyChangeSet(base, d);
    assert.deepEqual(got, canonicalManifest(head), `seed ${seed}`);
    assert.equal(validateChangeSet(d).ok, true, `seed ${seed} change set validates`);
    checked++;
    if (d.ops.some((o) => o.op === "move")) withMoves++;
    if (d.ops.some((o) => o.op === "remove")) withRemoves++;
    if (d.ops.some((o) => o.op === "add")) withAdds++;
  }
  assert.ok(checked > 1000, `only ${checked} valid cases`);
  assert.ok(withMoves > 50 && withRemoves > 50 && withAdds > 50, `coverage: moves ${withMoves}, removes ${withRemoves}, adds ${withAdds}`);
  console.log(`# property cases: ${checked}, with moves ${withMoves}, removes ${withRemoves}, adds ${withAdds}`);
});

test("manifests exported by the DAW validate and survive a diff/apply round trip", () => {
  const files = readdirSync(new URL("../examples/", import.meta.url)).filter((f) => f.startsWith("from-daw-") && !f.includes(".after-"));
  assert.ok(files.length > 0, "no DAW-exported examples");
  for (const f of files) {
    const m = ex(f);
    assert.deepEqual(validateManifest(m), { ok: true, errors: [] }, f);
    // A simple edit on a real DAW manifest: add a track, then rename another; apply(diff) must reproduce it.
    const head = structuredClone(m);
    head.root.lists.tracks.push({ id: hex(77), type: "track", props: { name: "New" } });
    head.root.lists.tracks[0].props.name = "Renamed";
    const d = diff(m, head);
    assert.deepEqual(applyChangeSet(m, d), canonicalManifest(head), f);
    assert.equal(validateChangeSet(d).ok, true, f);
  }
});

test("the committed kick-proposal examples are not stale (regenerate with scripts/gen-examples.mjs)", () => {
  const base = ex("from-daw-rich.json"), cs = ex("proposal-kick.changeset.json"), after = ex("from-daw-rich.after-kick.json");
  assert.deepEqual(validateChangeSet(cs), { ok: true, errors: [] });
  assert.deepEqual(applyChangeSet(base, cs), canonicalManifest(after));
});

test("the local API contract file is well formed: unique tool names, every parameter typed, capabilities listed", () => {
  const c = JSON.parse(readFileSync(new URL("../contract/local-api.1.json", import.meta.url), "utf8"));
  assert.equal(c.api, "agentdaw.local/1");
  assert.equal(c.manifestSchema, "agentdaw.manifest/1");
  const names = c.tools.map((t) => t.name);
  assert.equal(new Set(names).size, names.length);
  assert.ok(names.every((n) => /^collab_[a-z_]+$/.test(n)));
  for (const t of c.tools) for (const p of t.params) {
    assert.ok(["string", "boolean"].includes(p.type), `${t.name}.${p.name} type`);
    assert.equal(typeof p.required, "boolean");
  }
  assert.ok(c.capabilities.length >= 1 && c.reserved_not_implemented.length >= 1);
  assert.ok(!c.capabilities.some((cap) => c.reserved_not_implemented.join(" ").includes(cap)), "a capability cannot also be reserved");
});

// ---- persistence and sharing guarantees ------------------------------------------------------------------------
import { canonical } from "../src/tree.mjs";
const deepFreeze = (v) => { if (v && typeof v === "object" && !Object.isFrozen(v)) { Object.freeze(v); for (const k of Object.keys(v)) deepFreeze(v[k]); } return v; };

test("apply and diff never modify their inputs: 1500 random edits over deep-frozen manifests and change sets", () => {
  let n = 0;
  for (let seed = 1; seed <= 1500; seed++) {
    const r = rng(seed * 13); let id = 1000; const nextId = () => ++id;
    const base = randomManifest(r, nextId), head = mutate(base, r, nextId);
    if (!validateManifest(head).ok) continue;
    const snapshotBase = structuredClone(base), snapshotHead = structuredClone(head);
    deepFreeze(base); deepFreeze(head);                        // any write to either now throws (ES modules are strict)
    const d = diff(base, head);
    deepFreeze(d);
    const out = applyChangeSet(base, d);
    assert.deepEqual(out, canonicalManifest(head), `seed ${seed}`);
    assert.deepEqual(base, snapshotBase, `seed ${seed}: base changed`);
    assert.deepEqual(head, snapshotHead, `seed ${seed}: head changed`);
    n++;
  }
  assert.ok(n > 1000);
});

test("results share untouched subtrees with their input by reference, and diff recognises them", () => {
  const base = canonicalManifest(master);
  const cs = { schema: "agentdaw.changeset/1", id: "c", projectId: base.projectId, ops: [{ op: "set", id: "cccccccccccccccccccccccccccccc01", key: "length", value: 99 }] };
  const next = applyChangeSet(base, cs);
  assert.notEqual(next.root, base.root);                      // the path to the change was copied
  const track = (m) => m.root.lists.tracks[0];
  assert.notEqual(track(next), track(base));
  assert.equal(next.root.props, base.root.props);            // untouched props are the same object
  const bigger = structuredClone(master);
  bigger.root.lists.tracks.push({ id: hex(500), type: "track", props: { name: "Other" }, lists: { clips: [{ id: hex(501), type: "clip", props: { start: 1 } }] } });
  const b2 = canonicalManifest(bigger);
  const n2 = applyChangeSet(b2, cs);
  assert.equal(n2.root.lists.tracks[1], b2.root.lists.tracks[1], "the untouched second track is the very same object");
  assert.deepEqual(diff(b2, n2).ops.map((o) => [o.op, o.id, o.key]), [["set", "cccccccccccccccccccccccccccccc01", "length"]]);
  assert.deepEqual(diff(b2, b2).ops, []);
});

test("canonical returns the same object for an already canonical tree, and is idempotent", () => {
  const c = canonicalManifest(master).root;
  assert.equal(canonical(c), c);
  const messy = { id: hex(1), type: "t", props: {}, blobs: {}, lists: { a: [], b: [{ id: hex(2), type: "u", lists: { z: [] } }] } };
  const once = canonical(messy);
  assert.deepEqual(once, { id: hex(1), type: "t", lists: { b: [{ id: hex(2), type: "u" }] } });
  assert.equal(canonical(once), once);
});

test("an added node that repeats an id inside itself is refused", () => {
  const base = canonicalManifest(master);
  const dup = { op: "add", parent: base.root.id, list: "extra", after: null, node: { id: hex(600), type: "t", lists: { k: [{ id: hex(601), type: "u" }, { id: hex(601), type: "u" }] } } };
  assert.throws(() => applyChangeSet(base, { schema: "agentdaw.changeset/1", id: "c", projectId: base.projectId, ops: [dup] }), (e) => e instanceof ApplyError && e.code === "duplicate_id");
});

test("a large project applies and diffs in a time that does not grow with the number of operations (no per-operation re-index)", () => {
  const mk = (tracks) => { let i = 10_000; return { schema: "agentdaw.manifest/1", projectId: hex(1), root: { id: hex(1), type: "project", lists: { tracks: Array.from({ length: tracks }, () => ({ id: hex(++i), type: "track", props: { v: 0 }, lists: { clips: Array.from({ length: 20 }, () => ({ id: hex(++i), type: "clip", props: { s: 1 } })) } })) } } }; };
  const base = canonicalManifest(mk(100));              // 2100 nodes
  const ops = (k) => base.root.lists.tracks.slice(0, k).map((t, i) => ({ op: "set", id: t.id, key: "v", value: i + 1 }));
  // Counting index builds is the deterministic way to say "no re-index per op" without a flaky stopwatch.
  const orig = Map.prototype.set; let sets = 0;
  Map.prototype.set = function (...a) { sets++; return orig.apply(this, a); };
  try {
    sets = 0; applyChangeSet(base, { schema: "agentdaw.changeset/1", id: "a", projectId: base.projectId, ops: ops(1) });
    const one = sets;
    sets = 0; applyChangeSet(base, { schema: "agentdaw.changeset/1", id: "b", projectId: base.projectId, ops: ops(100) });
    const hundred = sets;
    assert.ok(hundred <= one + 10, `Map writes grew with the number of ops: ${one} for 1 op, ${hundred} for 100 ops`);
  } finally { Map.prototype.set = orig; }
});

test("diff does work proportional to what changed, not to the size of the project (identical shared subtrees are skipped)", () => {
  let i = 20_000;
  const base = canonicalManifest({ schema: "agentdaw.manifest/1", projectId: hex(1), root: { id: hex(1), type: "project", lists: { tracks: Array.from({ length: 100 }, () => ({ id: hex(++i), type: "track", props: { v: 0 }, lists: { clips: Array.from({ length: 20 }, () => ({ id: hex(++i), type: "clip", props: { s: 1 } })) } })) } } });
  const clip = base.root.lists.tracks[50].lists.clips[7].id;
  const next = applyChangeSet(base, { schema: "agentdaw.changeset/1", id: "c", projectId: base.projectId, ops: [{ op: "set", id: clip, key: "s", value: 2 }] });
  const orig = Array.prototype.findIndex; let calls = 0;
  Array.prototype.findIndex = function (...a) { calls++; return orig.apply(this, a); };
  let d;
  try { d = diff(base, next); } finally { Array.prototype.findIndex = orig; }
  assert.deepEqual(d.ops.map((o) => [o.op, o.id, o.key]), [["set", clip, "s"]]);
  assert.ok(calls < 400, `diff visited ${calls} list positions for a one-property change in a 2100-node project`);
});

// ---- names that would corrupt an object -------------------------------------------------------------------------
test("the names __proto__, constructor and prototype are refused by the schema, in manifests and in change sets", () => {
  for (const evil of ["__proto__", "constructor", "prototype"]) {
    const props = JSON.parse(`{ "id": "${hex(1)}", "type": "project", "props": { "${evil}": 1 } }`);
    assert.equal(validateManifest({ schema: "agentdaw.manifest/1", projectId: hex(1), root: props }).ok, false, `prop ${evil}`);
    const blobs = JSON.parse(`{ "id": "${hex(1)}", "type": "project", "blobs": { "${evil}": "sha256:${"a".repeat(64)}" } }`);
    assert.equal(validateManifest({ schema: "agentdaw.manifest/1", projectId: hex(1), root: blobs }).ok, false, `blob ${evil}`);
    const lists = JSON.parse(`{ "id": "${hex(1)}", "type": "project", "lists": { "${evil}": [] } }`);
    assert.equal(validateManifest({ schema: "agentdaw.manifest/1", projectId: hex(1), root: lists }).ok, false, `list ${evil}`);
    assert.equal(validateChangeSet({ schema: "agentdaw.changeset/1", id: "c", projectId: hex(1), ops: [{ op: "set", id: hex(1), key: evil, value: 1 }] }).ok, false, `set ${evil}`);
    assert.equal(validateChangeSet({ schema: "agentdaw.changeset/1", id: "c", projectId: hex(1), ops: [{ op: "add", parent: hex(1), list: evil, after: null, node: { id: hex(2), type: "t" } }] }).ok, false, `add list ${evil}`);
  }
});

test("even if a hostile host skips validation, applying and merging never changes a prototype: __proto__ stays an own, inert key", () => {
  const base = { schema: "agentdaw.manifest/1", projectId: hex(1), root: { id: hex(1), type: "project", props: { a: 1 } } };
  const evilSet = JSON.parse(`{ "schema": "agentdaw.changeset/1", "id": "c", "projectId": "${hex(1)}", "ops": [ { "op": "set", "id": "${hex(1)}", "key": "__proto__", "value": { "polluted": true } } ] }`);
  const out = applyChangeSet(base, evilSet);
  assert.equal({}.polluted, undefined, "Object.prototype was not touched");
  assert.equal(Object.getPrototypeOf(out.root.props), Object.prototype, "props kept its prototype");
  assert.ok(Object.hasOwn(out.root.props, "__proto__"), "the key is an ordinary own property");
  assert.equal(out.root.props.polluted, undefined);
  const evilAdd = JSON.parse(`{ "schema": "agentdaw.changeset/1", "id": "c", "projectId": "${hex(1)}", "ops": [ { "op": "add", "parent": "${hex(1)}", "list": "__proto__", "after": null, "node": { "id": "${hex(2)}", "type": "t" } } ] }`);
  const out2 = applyChangeSet(base, evilAdd);
  assert.equal(Object.getPrototypeOf(out2.root.lists), Object.prototype);
  assert.ok(Object.hasOwn(out2.root.lists, "__proto__"));
  // merging two sides that both carry the key
  const side = (v) => applyChangeSet(base, JSON.parse(`{ "schema": "agentdaw.changeset/1", "id": "c", "projectId": "${hex(1)}", "ops": [ { "op": "set", "id": "${hex(1)}", "key": "__proto__", "value": ${v} } ] }`));
  const m = merge(base, side(1), side(2));
  assert.equal(m.status, "conflicted");
  assert.equal({}.polluted, undefined);
  assert.equal(Object.getPrototypeOf(m.merged.root.props), Object.prototype);
  const clean = merge(base, side(1), base);
  assert.ok(Object.hasOwn(clean.merged.root.props, "__proto__"));
  assert.equal(clean.merged.root.props.a, 1, "the other keys survive");
});
