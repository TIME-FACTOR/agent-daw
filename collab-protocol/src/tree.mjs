// Copyright the AGENT DAW protocol authors. Licensed under the Apache License, Version 2.0.
//
// Reference semantics for manifests and change sets: canonical form, applyChangeSet, diff.
//
// Pure and persistent: inputs are never modified, and results SHARE every unchanged part with their inputs (props, blobs,
// untouched subtrees), so manifests must be treated as immutable. A failure throws ApplyError and leaves no partial result.
//
// Cost: applyChangeSet is O(nodes) once to index plus O(depth x list length) per operation; diff is O(nodes) plus the
// same per emitted operation. Nothing is re-indexed per operation.

import { isDeepStrictEqual } from "node:util";

export class ApplyError extends Error {
  constructor(code, message, opIndex = -1) {
    super(message);
    this.name = "ApplyError";
    this.code = code;
    this.opIndex = opIndex;
  }
}

/** Sets an own property even when the name is "__proto__" (a plain assignment would change the object's prototype instead). */
export function put(obj, key, value) {
  if (key === "__proto__") Object.defineProperty(obj, key, { value, enumerable: true, writable: true, configurable: true });
  else obj[key] = value;
}
const has = (o, k) => o !== undefined && Object.hasOwn(o, k);
const nonEmpty = (o) => o !== undefined && o !== null && Object.keys(o).length > 0;

/**
 * Canonical form: empty props/blobs/lists are omitted, so equal projects compare equal. Returns the SAME node object when it
 * is already canonical (no allocation), so a canonical tree keeps its structure shared and unchanged subtrees can be recognised
 * by reference. Otherwise it builds new nodes that share props and blobs with the input.
 */
export function canonical(node) {
  let ok = (node.props === undefined || nonEmpty(node.props)) && (node.blobs === undefined || nonEmpty(node.blobs));
  let lists;
  if (node.lists !== undefined) {
    const names = Object.keys(node.lists);
    let same = names.length > 0;
    const built = {};
    for (const k of names) {
      const kids = node.lists[k];
      if (kids.length === 0) { same = false; continue; }
      let arr = kids;
      for (let i = 0; i < kids.length; i++) {
        const c = canonical(kids[i]);
        if (c !== kids[i]) { if (arr === kids) arr = kids.slice(); arr[i] = c; }
      }
      if (arr !== kids) same = false;
      put(built, k, arr);
    }
    if (same) lists = node.lists;
    else { ok = false; if (Object.keys(built).length) lists = built; }
  }
  if (ok) return node;
  const out = { id: node.id, type: node.type };
  if (nonEmpty(node.props)) out.props = node.props;
  if (nonEmpty(node.blobs)) out.blobs = node.blobs;
  if (lists) out.lists = lists;
  return out;
}
export const canonicalManifest = (m) => ({ ...m, root: canonical(m.root) });

/** id -> {node, parent: id|null, list: string|null}. Parents are held by id so a copied parent never leaves a stale pointer. */
function index(root) {
  const map = new Map();
  const walk = (n, parent, list) => {
    map.set(n.id, { node: n, parent, list });
    if (n.lists) for (const name of Object.keys(n.lists)) for (const c of n.lists[name]) walk(c, n.id, name);
  };
  walk(root, null, null);
  return map;
}

function subtreeIds(n, into = new Set()) {
  into.add(n.id);
  if (n.lists) for (const kids of Object.values(n.lists)) for (const c of kids) subtreeIds(c, into);
  return into;
}

/**
 * A working copy of a canonical tree with a live index. A node is copied the first time an operation writes to it, together with
 * its ancestors (path copying); everything else stays shared with the tree it started from.
 */
class Work {
  constructor(root) {
    this.root = root;
    this.idx = index(root);
    this.copies = new Set();      // node objects this Work created
    this.listsOwned = new Set();  // nodes whose `lists` object is private
    this.arrays = new Set();      // list arrays that are private
    this.propsOwned = new Set();
    this.blobsOwned = new Set();
  }

  get(id, i) {
    const e = this.idx.get(id);
    if (!e) throw new ApplyError("missing_node", `no node ${id}`, i);
    return e;
  }

  /** Makes the entry's node (and its ancestors) private and returns it. */
  touch(e) {
    if (this.copies.has(e.node)) return e.node;
    const parentEntry = e.parent === null ? null : this.idx.get(e.parent);
    const parentNode = parentEntry ? this.touch(parentEntry) : null;
    const old = e.node;
    const copy = { ...old };
    this.copies.add(copy);
    if (parentNode) {
      const arr = this.writableList(parentNode, e.list);
      arr[arr.indexOf(old)] = copy;
    } else this.root = copy;
    e.node = copy;
    return copy;
  }

  /** The node's list array, private to this Work (the node must already be touched). Creates it if absent. */
  writableList(node, name) {
    if (!this.listsOwned.has(node)) { node.lists = { ...(node.lists ?? {}) }; this.listsOwned.add(node); }
    let arr = Object.hasOwn(node.lists, name) ? node.lists[name] : undefined;   // an own key only: lists["__proto__"] would otherwise read Object.prototype
    if (arr === undefined) { arr = []; put(node.lists, name, arr); this.arrays.add(arr); }
    else if (!this.arrays.has(arr)) { arr = [...arr]; put(node.lists, name, arr); this.arrays.add(arr); }
    return arr;
  }

  /** Removes an emptied list (and an emptied lists object) so the tree stays canonical. */
  tidyLists(node, name) {
    if (node.lists && node.lists[name] && node.lists[name].length === 0) {
      delete node.lists[name];
      if (Object.keys(node.lists).length === 0) { delete node.lists; this.listsOwned.delete(node); }
    }
  }

  writableProps(node) {
    if (!this.propsOwned.has(node)) { node.props = { ...(node.props ?? {}) }; this.propsOwned.add(node); }
    return node.props;
  }
  writableBlobs(node) {
    if (!this.blobsOwned.has(node)) { node.blobs = { ...(node.blobs ?? {}) }; this.blobsOwned.add(node); }
    return node.blobs;
  }

  /** True if `ancestor` is `id` or one of its ancestors. */
  within(ancestorId, id) {
    for (let e = this.idx.get(id); e; e = e.parent === null ? undefined : this.idx.get(e.parent)) if (e.node.id === ancestorId) return true;
    return false;
  }

  insertAt(parentNode, list, after, child, i) {
    const arr = this.writableList(parentNode, list);
    let pos = 0;
    if (after !== null) {
      const at = arr.findIndex((c) => c.id === after);
      if (at < 0) throw new ApplyError("missing_anchor", `${after} is not in ${parentNode.id}.${list}`, i);
      pos = at + 1;
    }
    arr.splice(pos, 0, child);
  }

  register(node, parentId, list) {
    this.idx.set(node.id, { node, parent: parentId, list });
    if (node.lists) for (const name of Object.keys(node.lists)) for (const c of node.lists[name]) this.register(c, node.id, name);
  }
  unregister(node) {
    this.idx.delete(node.id);
    if (node.lists) for (const kids of Object.values(node.lists)) for (const c of kids) this.unregister(c);
  }

  checkExpect(expect, present, current, what, i) {
    if (!expect) return;
    if (expect.absent) { if (present) throw new ApplyError("precondition", `${what} expected to be unset`, i); return; }
    if (!present || !isDeepStrictEqual(current, expect.value)) throw new ApplyError("precondition", `${what} is not the expected value`, i);
  }

  apply(op, i) {
    switch (op.op) {
      case "add": {
        const parent = this.get(op.parent, i);
        const fresh = canonical(op.node);
        const ids = subtreeIds(fresh);
        let count = 0; (function c(n) { count++; if (n.lists) for (const kids of Object.values(n.lists)) kids.forEach(c); })(fresh);
        if (ids.size !== count) throw new ApplyError("duplicate_id", "the added node repeats an id inside itself", i);
        for (const id of ids) if (this.idx.has(id)) throw new ApplyError("duplicate_id", `${id} already exists`, i);
        const parentNode = this.touch(parent);
        this.insertAt(parentNode, op.list, op.after, fresh, i);
        this.register(fresh, op.parent, op.list);
        return;
      }
      case "remove": {
        const e = this.get(op.id, i);
        if (e.parent === null) throw new ApplyError("root_immutable", "the root cannot be removed", i);
        const node = e.node, parentId = e.parent, list = e.list;
        const parentNode = this.touch(this.idx.get(parentId));
        const arr = this.writableList(parentNode, list);
        arr.splice(arr.indexOf(node), 1);
        this.tidyLists(parentNode, list);
        this.unregister(node);
        return;
      }
      case "move": {
        const e = this.get(op.id, i);
        if (e.parent === null) throw new ApplyError("root_immutable", "the root cannot be moved", i);
        const target = this.get(op.parent, i);
        if (this.within(op.id, target.node.id)) throw new ApplyError("cycle", `cannot move ${op.id} into its own subtree`, i);
        if (op.after === op.id) throw new ApplyError("missing_anchor", "a node cannot follow itself", i);
        const node = e.node, fromId = e.parent, fromList = e.list;
        const fromNode = this.touch(this.idx.get(fromId));
        const fromArr = this.writableList(fromNode, fromList);
        fromArr.splice(fromArr.indexOf(node), 1);
        this.tidyLists(fromNode, fromList);
        const targetNode = this.touch(this.idx.get(op.parent));
        this.insertAt(targetNode, op.list, op.after, node, i);
        e.parent = op.parent; e.list = op.list;
        return;
      }
      case "set": case "unset": {
        const e = this.get(op.id, i);
        const present = has(e.node.props, op.key);
        this.checkExpect(op.expect, present, e.node.props?.[op.key], `${op.id}.${op.key}`, i);
        if (op.op === "set") put(this.writableProps(this.touch(e)), op.key, op.value);
        else if (present) {
          const node = this.touch(e);
          delete this.writableProps(node)[op.key];
          if (Object.keys(node.props).length === 0) { delete node.props; this.propsOwned.delete(node); }
        }
        return;
      }
      case "setBlob": case "unsetBlob": {
        const e = this.get(op.id, i);
        const present = has(e.node.blobs, op.name);
        this.checkExpect(op.expect, present, e.node.blobs?.[op.name], `${op.id} blob ${op.name}`, i);
        if (op.op === "setBlob") put(this.writableBlobs(this.touch(e)), op.name, op.hash);
        else if (present) {
          const node = this.touch(e);
          delete this.writableBlobs(node)[op.name];
          if (Object.keys(node.blobs).length === 0) { delete node.blobs; this.blobsOwned.delete(node); }
        }
        return;
      }
      default:
        throw new ApplyError("unknown_op", `unknown op ${op.op}`, i);
    }
  }
}

/** Returns a new canonical manifest, or throws ApplyError. The input is not modified. */
export function applyChangeSet(manifest, cs) {
  if (cs.projectId !== manifest.projectId) throw new ApplyError("wrong_project", "change set is for another project");
  if (cs.baseVersion !== undefined && manifest.version !== undefined && cs.baseVersion !== manifest.version)
    throw new ApplyError("stale_base", `change set is based on ${cs.baseVersion}, manifest is ${manifest.version}`);
  const work = new Work(canonical(manifest.root));
  cs.ops.forEach((op, i) => work.apply(op, i));
  return { ...manifest, root: work.root };
}

/** A change set that turns `base` into `head` (same project and root). Applying it to `base` yields `head`. */
export function diff(base, head, meta = {}) {
  if (base.projectId !== head.projectId || base.root.id !== head.root.id) throw new ApplyError("wrong_project", "base and head are different projects");
  const b = canonicalManifest(base), h = canonicalManifest(head);
  const baseIdx = index(b.root), headIdx = index(h.root);
  for (const [id, e] of headIdx) {
    const be = baseIdx.get(id);
    if (be && be.node.type !== e.node.type) throw new ApplyError("type_changed", `${id} changed type`);
  }
  const w = new Work(b.root), ops = [];
  const emit = (op) => { w.apply(op, ops.length); ops.push(op); };

  // Phase 0: drop stale subtrees that hold nothing that survives.
  const stale = (id) => !headIdx.has(id);
  const dropWhole = (n) => {
    if (stale(n.id) && [...subtreeIds(n)].every(stale)) { emit({ op: "remove", id: n.id }); return; }
    if (n.lists) for (const kids of Object.values(n.lists)) for (const c of [...kids]) dropWhole(c);
  };
  for (const kids of Object.values(b.root.lists ?? {})) for (const c of [...kids]) dropWhole(c);

  // Phase 1: walk head in order: add, move, set/unset.
  const shallow = (n) => { const s = { id: n.id, type: n.type }; if (n.props) s.props = n.props; if (n.blobs) s.blobs = { ...n.blobs }; return s; };
  const fields = (n) => {
    const was = w.idx.get(n.id).node;
    if (was.props !== n.props) {
      const keys = new Set([...Object.keys(was.props ?? {}), ...Object.keys(n.props ?? {})]);
      for (const k of [...keys].sort()) {
        const inOld = has(was.props, k), inNew = has(n.props, k);
        if (inOld && inNew && isDeepStrictEqual(was.props[k], n.props[k])) continue;
        const expect = inOld ? { value: was.props[k] } : { absent: true };
        if (inNew) emit({ op: "set", id: n.id, key: k, value: n.props[k], expect });
        else emit({ op: "unset", id: n.id, key: k, expect });
      }
    }
    if (was.blobs !== n.blobs) {
      const bkeys = new Set([...Object.keys(was.blobs ?? {}), ...Object.keys(n.blobs ?? {})]);
      for (const k of [...bkeys].sort()) {
        const inOld = has(was.blobs, k), inNew = has(n.blobs, k);
        if (inOld && inNew && was.blobs[k] === n.blobs[k]) continue;
        const expect = inOld ? { value: was.blobs[k] } : { absent: true };
        if (inNew) emit({ op: "setBlob", id: n.id, name: k, hash: n.blobs[k], expect });
        else emit({ op: "unsetBlob", id: n.id, name: k, expect });
      }
    }
  };
  const walk = (n, parentId, list, after) => {
    if (parentId !== null) {
      if (!baseIdx.has(n.id)) emit({ op: "add", parent: parentId, list, after, node: shallow(n) });
      else {
        const here = w.idx.get(n.id);
        const arr = here.parent === null ? [] : w.idx.get(here.parent).node.lists[here.list];
        const at = arr.findIndex((c) => c.id === n.id);
        const pred = at > 0 ? arr[at - 1].id : null;
        if (here.parent !== parentId || here.list !== list || pred !== after) emit({ op: "move", id: n.id, parent: parentId, list, after });
        if (w.idx.get(n.id).node === n) return;       // the very same subtree on both sides: nothing inside changed
        fields(n);
      }
    } else fields(n);
    if (n.lists) for (const [name, kids] of Object.entries(n.lists)) kids.forEach((c, i) => walk(c, n.id, name, i ? kids[i - 1].id : null));
  };
  if (w.root !== h.root) walk(h.root, null, null, null);

  // Phase 2: stale nodes that still held survivors are now empty.
  for (const id of baseIdx.keys()) if (stale(id) && w.idx.has(id)) emit({ op: "remove", id });

  return { schema: "agentdaw.changeset/1", id: meta.id ?? "unassigned", projectId: base.projectId, ...(base.version !== undefined ? { baseVersion: base.version } : {}), ...(meta.label ? { label: meta.label } : {}), ops };
}
