// Copyright the AGENT DAW protocol authors. Licensed under the Apache License, Version 2.0.
//
// Three-way merge of manifests: merge(base, ours, theirs, { resolutions }).
//
// What merges independently: each prop key, each blob name, a node's location (parent+list), and the
// order of each list. A node deleted on one side and modified (anywhere in its subtree, or moved) on the
// other is a conflict. Opaque values (blobs, and a prop's whole JSON value) are never merged inside.
//
// The result always carries a provisional `merged` tree in which ours wins every unresolved conflict but `status` is "conflicted"
// until every conflict has an entry in `resolutions`; a conflicted tree must never be committed.
// Choosing "the deleter wins" removes the node together with everything the other side still has inside it.
// Resolution values: "ours" | "theirs" for every kind; for delete_modify "ours"/"theirs" means whose
// side wins (the deleter deletes, the other keeps); {value} for a prop or {hash} for a blob conflict;
// "accept" for kind "resurrected". Pure: inputs are not modified.

import { isDeepStrictEqual } from "node:util";
import { canonical, put } from "./tree.mjs";

const SEP = "\u0000";
const END = Symbol("end");
const eq = isDeepStrictEqual;

export class MergeError extends Error {
  constructor(code, message) { super(message); this.name = "MergeError"; this.code = code; }
}

function indexTree(root) {
  const map = new Map(); // id -> {node, parent, list}
  const walk = (n, parent, list) => {
    map.set(n.id, { node: n, parent, list });
    for (const [name, kids] of Object.entries(n.lists ?? {})) for (const c of kids) walk(c, n.id, name);
  };
  walk(root, null, null);
  return map;
}
const seqOf = (idx, root, parent, list) => (idx.get(parent)?.node.lists?.[list] ?? []).map((c) => c.id);
const subtreeEq = (a, b) => eq(canonical(a), canonical(b));

/** @returns {{status: "clean"|"conflicted", merged: object, conflicts: object[]}} */
/**
 * Props whose value is an array of objects with a natural identity merge element by element instead of as one value.
 * `notes`: a MIDI note is identified by its pitch and start, so two people editing different notes of one clip do not
 * conflict, and two edits to the same note do. Pass `identities: {}` to turn this off.
 */
export const DEFAULT_IDENTITIES = {
  notes: (n) => (n && typeof n === "object" && Number.isFinite(n.pitch) && Number.isFinite(n.start) ? `${n.pitch}:${n.start}` : undefined),
};

export function merge(base, ours, theirs, { resolutions = {}, identities = DEFAULT_IDENTITIES } = {}) {
  for (const m of [ours, theirs]) {
    if (m.projectId !== base.projectId || m.root.id !== base.root.id) throw new MergeError("wrong_project", "manifests are of different projects");
  }
  const rootId = base.root.id;
  const B = indexTree(canonical(base.root)), O = indexTree(canonical(ours.root)), T = indexTree(canonical(theirs.root));
  const conflicts = [];
  const used = new Set();

  const decide = (kind, id, key, info, allowed) => {
    const ck = `${kind}|${id}|${key ?? ""}`;
    const res = resolutions[ck];
    used.add(ck);
    if (res !== undefined && !allowed(res)) throw new MergeError("bad_resolution", `resolution for ${ck} is not allowed`);
    conflicts.push({ kind, id, ...(key !== undefined ? { key } : {}), conflictKey: ck, ...info, resolved: res !== undefined, ...(res !== undefined ? { resolution: res } : {}) });
    return res;
  };
  const side = (v) => v === "ours" || v === "theirs";

  // ---- existence -------------------------------------------------------------------------------
  const ids = new Set([...B.keys(), ...O.keys(), ...T.keys()]);
  const present = new Set([rootId]);
  const explicitDeletes = new Set(); // deletions a resolution chose: their remaining contents go too
  const addAddChoice = new Map(); // id -> "ours" | "theirs": whose content an add/add uses
  for (const id of ids) {
    if (id === rootId) continue;
    const inB = B.has(id), inO = O.has(id), inT = T.has(id);
    if (!inB) {
      if (inO || inT) present.add(id);
      if (inO && inT) {
        const a = O.get(id).node, b = T.get(id).node;
        const here = { type: a.type, props: a.props, blobs: a.blobs }, there = { type: b.type, props: b.props, blobs: b.blobs };
        if (!eq(here, there)) {
          const r = decide("add_add", id, undefined, { ours: here, theirs: there }, side);
          addAddChoice.set(id, r === "theirs" ? "theirs" : "ours");
        }
      }
    } else if (inO && inT) present.add(id);
    else if (inO || inT) {
      const keeperIsOurs = inO;
      const kept = (keeperIsOurs ? O : T).get(id), was = B.get(id);
      const modified = !subtreeEq(kept.node, was.node) || kept.parent !== was.parent || kept.list !== was.list;
      if (!modified) continue; // clean delete
      const deleter = keeperIsOurs ? "theirs" : "ours";
      const r = decide("delete_modify", id, undefined, { deleter }, side);
      if ((r ?? "ours") === deleter) { explicitDeletes.add(id); continue; } // unresolved: ours wins, like every other default
      present.add(id);
    }
  }

  // ---- location ---------------------------------------------------------------------------------
  const locKey = (e) => (e ? `${e.parent}${SEP}${e.list}` : undefined);
  const placed = new Map(); // id -> {parent, list}
  const place = (id) => {
    const eb = B.get(id), eo = O.get(id), et = T.get(id);
    const bl = locKey(eb), ol = eo ? locKey(eo) : bl, tl = et ? locKey(et) : bl;
    let chosen;
    if (ol === tl) chosen = ol;
    else if (ol === bl) chosen = tl;
    else if (tl === bl) chosen = ol;
    else {
      const [op, olst] = ol.split(SEP), [tp, tlst] = tl.split(SEP);
      const r = decide("move", id, undefined, { ours: { parent: op, list: olst }, theirs: { parent: tp, list: tlst } }, side);
      chosen = r === "theirs" ? tl : ol;
    }
    const [parent, list] = chosen.split(SEP);
    return { parent, list };
  };
  const loc = new Map();
  for (const id of present) if (id !== rootId) loc.set(id, place(id));
  // Safety net: a surviving node whose parent did not survive brings its parent back.
  for (let changed = true; changed;) {
    changed = false;
    for (const [id, l] of [...loc]) {
      if (present.has(l.parent)) continue;
      if (explicitDeletes.has(l.parent)) { // the parent's deletion was chosen: what is left inside it is deleted too
        present.delete(id); loc.delete(id); explicitDeletes.add(id);
        changed = true;
        continue;
      }
      present.add(l.parent);
      loc.set(l.parent, place(l.parent));
      decide("resurrected", l.parent, undefined, { because: id }, (v) => v === "accept");
      changed = true;
    }
  }

  // Element-wise three-way merge of an array of keyed objects. Null means "cannot": fall back to a whole-value conflict.
  const mergeKeyed = (nodeId, key, bArr, oArr, tArr, identity) => {
    const asMap = (arr) => {
      if (arr === undefined) return new Map();
      if (!Array.isArray(arr)) return null;
      const m = new Map();
      for (const el of arr) { const k = identity(el); if (k === undefined || m.has(k)) return null; m.set(k, el); }
      return m;
    };
    const [bm, om, tm] = [asMap(bArr), asMap(oArr), asMap(tArr)];
    if (!bm || !om || !tm) return null;
    const out = new Map();
    for (const eid of new Set([...bm.keys(), ...om.keys(), ...tm.keys()])) {
      const [b, o, t] = [bm.get(eid), om.get(eid), tm.get(eid)];
      const eq3 = (x, y) => (x === undefined || y === undefined ? x === y : eq(x, y));
      let chosen;
      if (eq3(o, t)) chosen = o;
      else if (eq3(o, b)) chosen = t;
      else if (eq3(t, b)) chosen = o;
      else {
        const r = decide("prop", nodeId, `${key}[${eid}]`, { base: b, ours: o, theirs: t }, (v) => side(v) || (v !== null && typeof v === "object" && "value" in v));
        chosen = r === "theirs" ? t : r && typeof r === "object" ? r.value : o;
      }
      if (chosen !== undefined) out.set(eid, chosen);
    }
    // Notes are unordered; a fixed, side-independent order keeps the merge symmetric.
    return [...out.values()].sort((x, y) => (x.start - y.start) || (x.pitch - y.pitch));
  };

  // ---- content ----------------------------------------------------------------------------------
  const mergeMap = (id, field, kind, resolveWith) => {
    const bn = B.get(id)?.node, on = O.get(id)?.node ?? bn, tn = T.get(id)?.node ?? bn;
    const bm = bn?.[field] ?? {}, om = on?.[field] ?? {}, tm = tn?.[field] ?? {};
    const out = {};
    for (const k of new Set([...Object.keys(bm), ...Object.keys(om), ...Object.keys(tm)])) {
      const [hb, ho, ht] = [k in bm, k in om, k in tm];
      const same = (ha, a, hb2, b) => ha === hb2 && (!ha || eq(a, b));
      let pick, keyed;
      if (same(ho, om[k], ht, tm[k])) pick = ho ? { v: om[k] } : null;
      else if (same(ho, om[k], hb, bm[k])) pick = ht ? { v: tm[k] } : null;
      else if (same(ht, tm[k], hb, bm[k])) pick = ho ? { v: om[k] } : null;
      else if (kind === "prop" && identities[k] && (keyed = mergeKeyed(id, k, bm[k], om[k], tm[k], identities[k])) !== null) pick = { v: keyed };
      else {
        const allowed = (r) => side(r) || (kind === "prop" ? r !== null && typeof r === "object" && "value" in r : r !== null && typeof r === "object" && "hash" in r);
        const r = decide(kind, id, k, { base: hb ? bm[k] : undefined, ours: ho ? om[k] : undefined, theirs: ht ? tm[k] : undefined }, allowed);
        if (r === "theirs") pick = ht ? { v: tm[k] } : null;
        else if (r && typeof r === "object") pick = { v: kind === "prop" ? r.value : r.hash };
        else pick = ho ? { v: om[k] } : null;
      }
      if (pick) put(out, k, pick.v);
    }
    return out;
  };

  // ---- order -------------------------------------------------------------------------------------
  const groups = new Map(); // "parent\0list" -> Set(ids)
  for (const [id, l] of loc) {
    const k = `${l.parent}${SEP}${l.list}`;
    if (!groups.has(k)) groups.set(k, new Set());
    groups.get(k).add(id);
  }
  const orderOf = (parent, list, members) => {
    const sb = seqOf(B, null, parent, list), so = seqOf(O, null, parent, list), st = seqOf(T, null, parent, list);
    const inSeq = (s) => new Set(s);
    const stable = sb.filter((x) => members.has(x));
    const restrictTo = (s, set) => s.filter((x) => set.has(x));
    const sO = inSeq(so), sT = inSeq(st);
    const stSet = new Set(stable);
    const oR = restrictTo(so, stSet), tR = restrictTo(st, stSet);
    const bForO = stable.filter((x) => sO.has(x)), bForT = stable.filter((x) => sT.has(x));
    const changedO = !eq(oR, bForO), changedT = !eq(tR, bForT);
    let result;
    if (!changedO && !changedT) result = stable.slice();
    else if (changedO && !changedT) result = oR.slice();
    else if (!changedO && changedT) result = tR.slice();
    else if (eq(oR, tR)) result = oR.slice();
    else {
      const r = decide("order", parent, list, { ours: oR, theirs: tR }, side);
      result = (r === "theirs" ? tR : oR).slice();
    }
    // Everything else (added, moved in, or missing from the chosen side) goes in runs next to the nearest
    // predecessor that is already in `result`. Runs keep their side's internal order. Runs at the same
    // anchor are ordered by their first id, and an id found in several runs is placed once, so the
    // outcome does not depend on which side is "ours".
    const base0 = new Set(result);
    const runs = []; // {anchor, items}; anchor null = the start, END = the end
    for (const seq of [so, st, sb]) {
      let anchor = null, cur = null;
      for (const x of seq) {
        if (!members.has(x)) continue;
        if (base0.has(x)) { anchor = x; cur = null; continue; }
        if (!cur) { cur = { anchor, items: [] }; runs.push(cur); }
        cur.items.push(x);
      }
      if (cur && cur.anchor !== null) cur.anchor = END; // appended at the end of its side's list: stays at the end
    }
    const rank = (a) => (a === null ? -1 : a === END ? Infinity : result.indexOf(a));
    runs.sort((a, b) => (rank(a.anchor) === rank(b.anchor) ? 0 : rank(a.anchor) < rank(b.anchor) ? -1 : 1) || (a.items[0] < b.items[0] ? -1 : a.items[0] > b.items[0] ? 1 : 0));
    const placedExtra = new Set();
    const after = new Map(); // anchor -> ids to put after it, in order
    for (const r of runs) {
      const list = after.get(r.anchor) ?? [];
      for (const x of r.items) if (!placedExtra.has(x)) { placedExtra.add(x); list.push(x); }
      after.set(r.anchor, list);
    }
    const out = [...(after.get(null) ?? [])];
    for (const x of result) { out.push(x); out.push(...(after.get(x) ?? [])); }
    out.push(...(after.get(END) ?? []));
    result = out;
    const inResult = new Set(result);
    for (const x of [...members].sort()) if (!inResult.has(x)) { result.push(x); inResult.add(x); } // cannot happen; never lose a node
    return result;
  };

  const build = (id) => {
    const src = O.get(id) ?? T.get(id) ?? B.get(id);
    const type = (O.get(id) ?? T.get(id) ?? B.get(id)).node.type;
    if (O.get(id) && T.get(id) && O.get(id).node.type !== T.get(id).node.type) throw new MergeError("type_changed", `${id} has different types on the two sides`);
    const node = { id, type };
    const pickSide = addAddChoice.get(id);
    let props, blobs;
    if (pickSide) { const n = (pickSide === "theirs" ? T : O).get(id).node; props = n.props ?? {}; blobs = n.blobs ?? {}; }
    else { props = mergeMap(id, "props", "prop"); blobs = mergeMap(id, "blobs", "blob"); }
    if (Object.keys(props).length) node.props = props;
    if (Object.keys(blobs).length) node.blobs = blobs;
    const lists = {};
    for (const [k, members] of groups) {
      const [parent, list] = k.split(SEP);
      if (parent !== id) continue;
      const ord = orderOf(parent, list, members);
      if (ord.length) lists[list] = ord.map(build);
    }
    if (Object.keys(lists).length) node.lists = lists;
    void src;
    return node;
  };
  const root = build(rootId);

  for (const k of Object.keys(resolutions)) if (!used.has(k)) throw new MergeError("unknown_resolution", `no conflict ${k}`);
  const merged = { schema: base.schema, projectId: base.projectId, root };
  return { status: conflicts.every((c) => c.resolved) ? "clean" : "conflicted", merged, conflicts };
}
