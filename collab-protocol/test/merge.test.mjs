// Copyright the AGENT DAW protocol authors. Licensed under the Apache License, Version 2.0.
import test from "node:test";
import assert from "node:assert/strict";
import { merge, MergeError } from "../src/merge.mjs";
import { canonicalManifest } from "../src/tree.mjs";
import { validateManifest } from "../src/validate.mjs";
import { hex, rng, randomManifest, mutate } from "./gen.mjs";

const ROOT = hex(1), T1 = hex(11), T2 = hex(12), C1 = hex(21), C2 = hex(22), C3 = hex(23);
const base = () => ({
  schema: "agentdaw.manifest/1", projectId: ROOT, version: "v1",
  root: { id: ROOT, type: "project", props: { bpm: 120 }, lists: { tracks: [
    { id: T1, type: "track", props: { name: "Bass", volume: 0.8 }, blobs: { state: "sha256:" + "1".repeat(64) },
      lists: { clips: [{ id: C1, type: "clip", props: { start: 0, notes: [1] } }, { id: C2, type: "clip", props: { start: 8 } }] } },
    { id: T2, type: "track", props: { name: "Lead" } },
  ] } },
});
const edit = (fn) => { const m = base(); fn(m.root, m); return m; };
const track = (m, id) => m.root.lists.tracks.find((t) => t.id === id);
// `version` is assigned by the server on commit; a merge result carries none, so compare the trees.
const same = (a, b) => assert.deepEqual(canonicalManifest(a).root, canonicalManifest(b).root);
const kinds = (r) => r.conflicts.map((c) => c.kind).sort();

test("identity and fast-forward", () => {
  const b = base(), o = edit((r) => { track({ root: r }, T1).props.volume = 0.5; });
  for (const [x, y, want] of [[b, b, b], [o, b, o], [b, o, o], [o, o, o]]) {
    const r = merge(b, x, y);
    assert.equal(r.status, "clean"); same(r.merged, want);
  }
});

test("edits to different nodes and different keys of one node merge cleanly", () => {
  const o = edit((r) => { track({ root: r }, T1).props.volume = 0.5; track({ root: r }, T2).props.name = "Keys"; });
  const t = edit((r) => { track({ root: r }, T1).props.name = "Sub"; r.props.bpm = 128; });
  const r = merge(base(), o, t);
  assert.equal(r.status, "clean");
  assert.deepEqual(track(r.merged, T1).props, { name: "Sub", volume: 0.5 });
  assert.equal(track(r.merged, T2).props.name, "Keys");
  assert.equal(r.merged.root.props.bpm, 128);
});

test("same key: equal edits agree, different edits conflict, and resolutions settle it", () => {
  const mk = (v) => edit((r) => { track({ root: r }, T1).props.volume = v; });
  assert.equal(merge(base(), mk(0.5), mk(0.5)).status, "clean");
  const r = merge(base(), mk(0.5), mk(0.9));
  assert.equal(r.status, "conflicted");
  assert.deepEqual(r.conflicts.map((c) => [c.kind, c.id, c.key, c.base, c.ours, c.theirs]), [["prop", T1, "volume", 0.8, 0.5, 0.9]]);
  assert.equal(track(r.merged, T1).props.volume, 0.5, "best effort: ours");
  assert.equal(validateManifest(r.merged).ok, true, "even a conflicted result is a valid tree");
  const ck = r.conflicts[0].conflictKey;
  assert.equal(track(merge(base(), mk(0.5), mk(0.9), { resolutions: { [ck]: "theirs" } }).merged, T1).props.volume, 0.9);
  const custom = merge(base(), mk(0.5), mk(0.9), { resolutions: { [ck]: { value: 0.7 } } });
  assert.equal(custom.status, "clean"); assert.equal(track(custom.merged, T1).props.volume, 0.7);
});

test("a prop removed on one side and changed on the other conflicts", () => {
  const o = edit((r) => { delete track({ root: r }, T1).props.volume; });
  const t = edit((r) => { track({ root: r }, T1).props.volume = 0.1; });
  assert.deepEqual(kinds(merge(base(), o, t)), ["prop"]);
});

test("blobs: same rules, opaque values", () => {
  const h = (c) => "sha256:" + c.repeat(64);
  const o = edit((r) => { track({ root: r }, T1).blobs.state = h("2"); });
  const t = edit((r) => { track({ root: r }, T1).blobs.state = h("3"); });
  const r = merge(base(), o, t);
  assert.deepEqual(kinds(r), ["blob"]);
  assert.equal(merge(base(), o, base()).merged.root.lists.tracks[0].blobs.state, h("2"));
  const ck = r.conflicts[0].conflictKey;
  assert.equal(track(merge(base(), o, t, { resolutions: { [ck]: { hash: h("4") } } }).merged, T1).blobs.state, h("4"));
});

test("delete vs unchanged is clean; delete vs modified conflicts; both deleting is clean", () => {
  const del = (id) => edit((r) => { const tr = r.lists.tracks; tr.splice(tr.findIndex((x) => x.id === id), 1); });
  let r = merge(base(), del(T2), base());
  assert.equal(r.status, "clean"); assert.equal(r.merged.root.lists.tracks.length, 1);
  assert.equal(merge(base(), del(T2), del(T2)).status, "clean");
  const mod = edit((x) => { track({ root: x }, T2).props.name = "Pad"; });
  r = merge(base(), del(T2), mod);
  assert.deepEqual(kinds(r), ["delete_modify"]);
  assert.equal(r.conflicts[0].deleter, "ours");
  assert.equal(r.merged.root.lists.tracks.length, 1, "provisional tree: ours (the deleter) wins");
  const ck = r.conflicts[0].conflictKey;
  assert.equal(merge(base(), del(T2), mod, { resolutions: { [ck]: "ours" } }).merged.root.lists.tracks.length, 1, "deleter wins");
  assert.equal(merge(base(), del(T2), mod, { resolutions: { [ck]: "theirs" } }).merged.root.lists.tracks.length, 2, "keeper wins");
});

test("deleting a track while the other side edits or adds inside it conflicts", () => {
  const del = edit((r) => { r.lists.tracks.splice(0, 1); });
  const inner = edit((r) => { track({ root: r }, T1).lists.clips.push({ id: C3, type: "clip", props: { start: 16 } }); });
  assert.deepEqual(kinds(merge(base(), del, inner)), ["delete_modify"]);
  const editClip = edit((r) => { track({ root: r }, T1).lists.clips[0].props.start = 4; });
  // Deleting the track while editing a clip in it is two real conflicts: the track and the edited clip.
  assert.deepEqual(kinds(merge(base(), editClip, del)), ["delete_modify", "delete_modify"]);
  const both = merge(base(), editClip, del);
  const all = (choice) => merge(base(), editClip, del, { resolutions: Object.fromEntries(both.conflicts.map((c) => [c.conflictKey, choice])) });
  assert.equal(all("theirs").status, "clean");
  assert.equal(all("ours").status, "clean");
});

test("moves: one side moving wins; both moving to different places conflicts", () => {
  const mv = (to) => edit((r) => { const t1 = track({ root: r }, T1); const c = t1.lists.clips.shift(); const dst = track({ root: r }, to); (dst.lists ??= {}).clips = [c]; });
  const r = merge(base(), mv(T2), base());
  assert.equal(r.status, "clean"); assert.equal(track(r.merged, T2).lists.clips[0].id, C1);
  const toRoot = edit((r2) => { const c = track({ root: r2 }, T1).lists.clips.shift(); (r2.lists.extra ??= []).push(c); });
  const conflict = merge(base(), mv(T2), toRoot);
  assert.deepEqual(kinds(conflict), ["move"]);
  assert.equal(conflict.conflicts[0].ours.parent, T2);
  const ck = conflict.conflicts[0].conflictKey;
  assert.equal(merge(base(), mv(T2), toRoot, { resolutions: { [ck]: "theirs" } }).merged.root.lists.extra[0].id, C1);
});

test("order: a reorder and an add merge; two different reorders conflict", () => {
  const reorder = edit((r) => { track({ root: r }, T1).lists.clips.reverse(); });
  const add = edit((r) => { track({ root: r }, T1).lists.clips.push({ id: C3, type: "clip", props: { start: 16 } }); });
  const r = merge(base(), reorder, add);
  assert.equal(r.status, "clean");
  assert.deepEqual(track(r.merged, T1).lists.clips.map((c) => c.id), [C2, C1, C3]);
  const base3 = edit((x) => { track({ root: x }, T1).lists.clips.push({ id: C3, type: "clip" }); });
  const a = (() => { const m = structuredClone(base3); track(m, T1).lists.clips = [track(m, T1).lists.clips[2], track(m, T1).lists.clips[0], track(m, T1).lists.clips[1]]; return m; })();
  const b = (() => { const m = structuredClone(base3); track(m, T1).lists.clips.reverse(); return m; })();
  const oc = merge(base3, a, b);
  assert.deepEqual(kinds(oc), ["order"]);
  const ids3 = (m) => track(m, T1).lists.clips.map((c) => c.id);
  assert.deepEqual(ids3(oc.merged), ids3(a), "provisional tree: ours");
  const ock = oc.conflicts[0].conflictKey;
  assert.deepEqual(ids3(merge(base3, a, b, { resolutions: { [ock]: "theirs" } }).merged), ids3(b));
  assert.deepEqual(ids3(merge(base3, a, b, { resolutions: { [ock]: "ours" } }).merged), ids3(a));
});

test("two adds at the same place both land, in an order that does not depend on the argument order", () => {
  const add = (id) => edit((r) => { track({ root: r }, T1).lists.clips.push({ id, type: "clip", props: { start: 99 } }); });
  const r1 = merge(base(), add(C3), add(hex(24))), r2 = merge(base(), add(hex(24)), add(C3));
  assert.equal(r1.status, "clean");
  assert.deepEqual(track(r1.merged, T1).lists.clips.map((c) => c.id), [C1, C2, C3, hex(24)]);
  same(r1.merged, r2.merged);
});

test("add/add of one id: identical content agrees, different content conflicts", () => {
  const add = (name) => edit((r) => { r.lists.tracks.push({ id: hex(50), type: "track", props: { name } }); });
  assert.equal(merge(base(), add("X"), add("X")).status, "clean");
  const r = merge(base(), add("X"), add("Y"));
  assert.deepEqual(kinds(r), ["add_add"]);
  assert.equal(r.merged.root.lists.tracks.at(-1).props.name, "X");
  assert.equal(merge(base(), add("X"), add("Y"), { resolutions: { [r.conflicts[0].conflictKey]: "theirs" } }).merged.root.lists.tracks.at(-1).props.name, "Y");
});

test("bad inputs and resolutions are rejected", () => {
  const o = edit((r) => { track({ root: r }, T1).props.volume = 0.5; }), t = edit((r) => { track({ root: r }, T1).props.volume = 0.9; });
  assert.throws(() => merge(base(), { ...o, projectId: hex(9) }, t), (e) => e instanceof MergeError && e.code === "wrong_project");
  const ck = merge(base(), o, t).conflicts[0].conflictKey;
  assert.throws(() => merge(base(), o, t, { resolutions: { [ck]: "maybe" } }), (e) => e.code === "bad_resolution");
  assert.throws(() => merge(base(), o, t, { resolutions: { "prop|nope|x": "ours" } }), (e) => e.code === "unknown_resolution");
  const before = JSON.stringify([base(), o, t]);
  merge(base(), o, t); assert.equal(JSON.stringify([base(), o, t]), before);
});

test("the proof-of-concept flow: three producers' contributions land on one master, in any order", () => {
  const b = base();
  const a = edit((r) => { r.lists.tracks.push({ id: hex(61), type: "track", props: { name: "Drums" } }); });
  const p2 = edit((r) => { track({ root: r }, T1).lists.clips.push({ id: C3, type: "clip", props: { start: 16 } }); });
  const p3 = edit((r) => { track({ root: r }, T2).props.name = "Synth"; });
  const heads = [];
  for (const order of [[a, p2, p3], [p3, a, p2], [p2, p3, a]]) {
    let head = b;
    for (const c of order) { const r = merge(b, head, c); assert.equal(r.status, "clean"); head = r.merged; }
    heads.push(head);
  }
  same(heads[0], heads[1]); same(heads[1], heads[2]);
  assert.deepEqual(heads[0].root.lists.tracks.map((t) => t.props.name), ["Bass", "Lead", "Drums"].map((n) => (n === "Lead" ? "Synth" : n)));
});

// ---- properties ---------------------------------------------------------------------------------
const ids = (n, out = []) => { out.push(n.id); for (const k of Object.values(n.lists ?? {})) for (const c of k) ids(c, out); return out; };
const nodeMap = (m) => { const mp = new Map(); const w = (n) => { mp.set(n.id, n); for (const k of Object.values(n.lists ?? {})) k.forEach(w); }; w(m.root); return mp; };

test("property: identity and fast-forward hold, a merged tree is always valid, and a clean merge only holds values one side had", () => {
  let clean = 0, conflicted = 0;
  for (let seed = 1; seed <= 1200; seed++) {
    const r = rng(seed); let id = 1000; const nextId = () => ++id;
    const b = randomManifest(r, nextId), o = mutate(b, r, nextId), t = mutate(b, r, nextId);
    same(merge(b, b, b).merged, b);
    same(merge(b, o, b).merged, o);
    same(merge(b, b, o).merged, o);
    same(merge(b, o, o).merged, o);
    const m = merge(b, o, t);
    const v = validateManifest(m.merged);
    assert.equal(v.ok, true, `seed ${seed}: ${v.errors}`);
    if (m.status === "clean") {
      clean++;
      const [bm, om, tm, mm] = [nodeMap(b), nodeMap(o), nodeMap(t), nodeMap(m.merged)];
      for (const [nid, n] of mm) {
        assert.ok(om.has(nid) || tm.has(nid) || bm.has(nid), `seed ${seed}: node ${nid} appeared from nowhere`);
        for (const [k, val] of Object.entries(n.props ?? {})) {
          const cands = [bm, om, tm].map((x) => x.get(nid)?.props?.[k]);
          assert.ok(cands.some((c) => JSON.stringify(c) === JSON.stringify(val)), `seed ${seed}: ${nid}.${k} is a value no side had`);
        }
      }
    } else conflicted++;
  }
  console.log(`# clean ${clean}, conflicted ${conflicted}`);
  assert.ok(clean > 150 && conflicted > 150, `coverage: clean ${clean}, conflicted ${conflicted}`);
});

test("property: merge is symmetric (swapping ours/theirs gives the same tree) when there is no conflict", () => {
  let checked = 0;
  for (let seed = 1; seed <= 1500; seed++) {
    const r = rng(seed * 7919); let id = 1000; const nextId = () => ++id;
    const b = randomManifest(r, nextId), o = mutate(b, r, nextId), t = mutate(b, r, nextId);
    const x = merge(b, o, t), y = merge(b, t, o);
    assert.equal(x.status, y.status, `seed ${seed}: status differs`);
    assert.deepEqual(kinds(x), kinds(y), `seed ${seed}: conflict kinds differ`);
    if (x.status === "clean") { same(x.merged, y.merged); checked++; }
  }
  assert.ok(checked > 200, `only ${checked} clean cases`);
});

test("property: every conflict can be resolved either way, and then the merge is clean", () => {
  let resolvedCases = 0;
  const pick = (c, choice) => (c.kind === "resurrected" ? "accept" : choice);
  for (let seed = 1; seed <= 1200; seed++) {
    const r = rng(seed * 104729); let id = 1000; const nextId = () => ++id;
    const b = randomManifest(r, nextId), o = mutate(b, r, nextId), t = mutate(b, r, nextId);
    const m = merge(b, o, t);
    if (m.status !== "conflicted") continue;
    for (const choice of ["ours", "theirs"]) {
      const res = Object.fromEntries(m.conflicts.map((c) => [c.conflictKey, pick(c, choice)]));
      const done = merge(b, o, t, { resolutions: res });
      assert.equal(done.status, "clean", `seed ${seed} ${choice}`);
      assert.equal(validateManifest(done.merged).ok, true, `seed ${seed} ${choice} valid`);
    }
    same(merge(b, o, t, { resolutions: Object.fromEntries(m.conflicts.map((c) => [c.conflictKey, pick(c, "ours")])) }).merged, m.merged);
    resolvedCases++;
  }
  assert.ok(resolvedCases > 150, `only ${resolvedCases} conflicted cases`);
});

test("merging on a manifest the DAW exported: the kick proposal plus a concurrent rename, track references intact", async () => {
  const { readFileSync } = await import("node:fs");
  const ex = (f) => JSON.parse(readFileSync(new URL(`../examples/${f}`, import.meta.url), "utf8"));
  const b = ex("from-daw-rich.json"), kick = ex("from-daw-rich.after-kick.json");
  const renamed = structuredClone(b);
  const vox = renamed.root.lists.tracks.find((t) => t.props.name === "Vox");
  vox.props.name = "Lead Vox";
  const r = merge(b, kick, renamed);
  assert.equal(r.status, "clean");
  const names = r.merged.root.lists.tracks.map((t) => t.props.name);
  assert.deepEqual(names.slice().sort(), ["Bass", "Drums", "Kick", "Lead Vox", "Rhythm"]);
  const drumsId = r.merged.root.lists.tracks.find((t) => t.props.name === "Drums").id;
  const mv = r.merged.root.lists.tracks.find((t) => t.props.name === "Lead Vox");
  assert.deepEqual(mv.props.devices[0].sidechainSource, { $track: drumsId }, "the sidechain still names the Drums track by id");
  // Editing the very device that other person's rename did not touch, versus editing the same track's devices from both sides:
  const dev1 = structuredClone(b), dev2 = structuredClone(b);
  dev1.root.lists.tracks.find((t) => t.props.name === "Vox").props.devices[0].bypassed = true;
  dev2.root.lists.tracks.find((t) => t.props.name === "Vox").props.devices[0].folded = true;
  assert.deepEqual(kinds(merge(b, dev1, dev2)), ["prop"], "v1 limit: two edits to one track's devices conflict as a whole value");
});

// ---- note-level merge (Q1) ---------------------------------------------------------------------------
describe_notes();
function describe_notes() {
  const note = (pitch, start, velocity = 1) => ({ pitch, start, length: 1, velocity });
  const withNotes = (notes) => edit((r) => { track({ root: r }, T1).lists.clips[0].props.notes = notes; });
  const clipOf = (m) => track(m, T1).lists.clips[0];
  const NB = [note(36, 0), note(38, 1)];

  test("notes: edits to different notes of one clip, and additions, merge cleanly (this used to be a conflict)", () => {
    const base0 = withNotes(NB);
    const ours = withNotes([note(36, 0, 0.5), note(38, 1)]);            // velocity of the first
    const theirs = withNotes([note(36, 0), note(38, 1), note(42, 2)]);  // a new note
    const r = merge(base0, ours, theirs);
    assert.equal(r.status, "clean");
    assert.deepEqual(clipOf(r.merged).props.notes, [note(36, 0, 0.5), note(38, 1), note(42, 2)]);
    const rev = merge(base0, theirs, ours);
    assert.deepEqual(clipOf(rev.merged).props.notes, clipOf(r.merged).props.notes, "symmetric");
  });

  test("notes: both removing the same note agrees; removing and editing the same note conflicts", () => {
    const base0 = withNotes(NB);
    assert.equal(merge(base0, withNotes([note(38, 1)]), withNotes([note(38, 1)])).status, "clean");
    const r = merge(base0, withNotes([note(38, 1)]), withNotes([note(36, 0, 0.2), note(38, 1)]));
    assert.equal(r.status, "conflicted");
    assert.equal(r.conflicts[0].key, "notes[36:0]");
    const ck = r.conflicts[0].conflictKey;
    assert.equal(clipOf(merge(base0, withNotes([note(38, 1)]), withNotes([note(36, 0, 0.2), note(38, 1)]), { resolutions: { [ck]: "theirs" } }).merged).props.notes.length, 2);
    assert.equal(clipOf(merge(base0, withNotes([note(38, 1)]), withNotes([note(36, 0, 0.2), note(38, 1)]), { resolutions: { [ck]: "ours" } }).merged).props.notes.length, 1);
  });

  test("notes: the same note edited two ways conflicts on that note only; adding the same position two ways conflicts", () => {
    const base0 = withNotes(NB);
    const r = merge(base0, withNotes([note(36, 0, 0.3), note(38, 1)]), withNotes([note(36, 0, 0.9), note(38, 1, 0.5)]));
    assert.deepEqual(r.conflicts.map((c) => c.key), ["notes[36:0]"]);          // 38:1 changed only by theirs: no conflict
    const add = merge(base0, withNotes([...NB, note(40, 2, 0.1)]), withNotes([...NB, note(40, 2, 0.9)]));
    assert.deepEqual(add.conflicts.map((c) => c.key), ["notes[40:2]"]);
    assert.equal(merge(base0, withNotes([...NB, note(40, 2)]), withNotes([...NB, note(40, 2)])).status, "clean");
  });

  test("notes: arrays without a usable identity (duplicates) still merge as one value", () => {
    const dup = [note(36, 0), note(36, 0)];
    const r = merge(withNotes(dup), withNotes([note(36, 0, 0.1), note(36, 0)]), withNotes([note(36, 0, 0.9), note(36, 0)]));
    assert.equal(r.status, "conflicted");
    assert.equal(r.conflicts[0].key, "notes");
  });

  test("notes: a value that can be resolved with a custom array", () => {
    const base0 = withNotes(NB);
    const r = merge(base0, withNotes([note(36, 0, 0.3), note(38, 1)]), withNotes([note(36, 0, 0.9), note(38, 1)]));
    const done = merge(base0, withNotes([note(36, 0, 0.3), note(38, 1)]), withNotes([note(36, 0, 0.9), note(38, 1)]), { resolutions: { [r.conflicts[0].conflictKey]: { value: note(36, 0, 0.6) } } });
    assert.equal(done.status, "clean");
    assert.equal(clipOf(done.merged).props.notes[0].velocity, 0.6);
  });

  test("property: random note edits from two producers are symmetric, never duplicate a note position, and resolve either way", () => {
    let clean = 0, conflicted = 0;
    for (let seed = 1; seed <= 800; seed++) {
      const r = rng(seed * 31); const pick = (xs) => xs[Math.floor(r() * xs.length)];
      const baseNotes = Array.from({ length: 1 + Math.floor(r() * 5) }, (_, i) => note(40 + i, i));
      const edit1 = () => {
        const out = baseNotes.map((n) => ({ ...n }));
        for (let k = 0; k < 1 + Math.floor(r() * 3); k++) {
          const roll = r();
          if (roll < 0.4 && out.length) out[Math.floor(r() * out.length)].velocity = Math.round(r() * 100) / 100;
          else if (roll < 0.7 && out.length) out.splice(Math.floor(r() * out.length), 1);
          else { const n = note(40 + Math.floor(r() * 8), Math.floor(r() * 8), Math.round(r() * 100) / 100); if (!out.some((x) => x.pitch === n.pitch && x.start === n.start)) out.push(n); }
        }
        return out;
      };
      const b = withNotes(baseNotes), o = withNotes(edit1()), t = withNotes(edit1());
      const x = merge(b, o, t), y = merge(b, t, o);
      assert.equal(x.status, y.status, `seed ${seed}`);
      const pos = clipOf(x.merged).props.notes.map((n) => `${n.pitch}:${n.start}`);
      assert.equal(new Set(pos).size, pos.length, `seed ${seed}: duplicate note position`);
      if (x.status === "clean") { clean++; assert.deepEqual(clipOf(x.merged).props.notes, clipOf(y.merged).props.notes, `seed ${seed}: symmetric`); }
      else {
        conflicted++;
        for (const choice of ["ours", "theirs"]) {
          const res = Object.fromEntries(x.conflicts.map((c) => [c.conflictKey, choice]));
          assert.equal(merge(b, o, t, { resolutions: res }).status, "clean", `seed ${seed} ${choice}`);
        }
      }
    }
    assert.ok(clean > 100 && conflicted > 100, `coverage: clean ${clean}, conflicted ${conflicted}`);
  });
}
