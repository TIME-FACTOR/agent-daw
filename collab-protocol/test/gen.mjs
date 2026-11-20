// Copyright the AGENT DAW protocol authors. Licensed under the Apache License, Version 2.0.
// Seeded random manifests and edits for property tests.
export const hex = (n) => n.toString(16).padStart(32, "0");

// ---- property test: apply(base, diff(base, head)) == head ------------------------------------
export function rng(seed) { let s = seed >>> 0; return () => ((s = (Math.imul(s, 1664525) + 1013904223) >>> 0) / 2 ** 32); }

export function randomManifest(r, nextId) {
  const mk = (type, depth) => {
    const n = { id: hex(nextId()), type };
    if (r() < 0.8) n.props = { a: Math.floor(r() * 5), ...(r() < 0.3 ? { b: [1, 2, { c: r() < 0.5 }] } : {}) };
    if (r() < 0.2) n.blobs = { audio: `sha256:${hex(Math.floor(r() * 1e9)).repeat(2)}` };
    if (depth < 3) {
      const lists = {};
      for (const name of ["x", "y"]) if (r() < 0.6) lists[name] = Array.from({ length: Math.floor(r() * 4) }, () => mk("t" + (depth + 1), depth + 1));
      if (Object.keys(lists).length) n.lists = lists;
    }
    return n;
  };
  const root = mk("project", 0); root.id = hex(1);
  return { schema: "agentdaw.manifest/1", projectId: hex(1), version: "v1", root };
}

export function mutate(m, r, nextId) {
  const h = structuredClone(m);
  const all = [];
  const walk = (n, parent, list) => { all.push({ n, parent, list }); for (const [k, kids] of Object.entries(n.lists ?? {})) for (const c of kids) walk(c, n, k); };
  walk(h.root, null, null);
  const steps = 1 + Math.floor(r() * 6);
  for (let s = 0; s < steps; s++) {
    const pick = all[Math.floor(r() * all.length)];
    const op = Math.floor(r() * 6);
    const live = (e) => e.parent === null || e.parent.lists?.[e.list]?.includes(e.n);
    if (!live(pick)) continue;
    if (op === 0) { (pick.n.props ??= {}).a = Math.floor(r() * 9); }
    else if (op === 1) { if (pick.n.props) delete pick.n.props[Object.keys(pick.n.props)[0]]; }
    else if (op === 2) { ((pick.n.lists ??= {}).x ??= []).splice(Math.floor(r() * 3), 0, { id: hex(nextId()), type: "new", props: { z: 1 } }); }
    else if (op === 3 && pick.parent) { const arr = pick.parent.lists[pick.list]; arr.splice(arr.indexOf(pick.n), 1); }
    else if (op === 4 && pick.parent) { // move to another node's list when that does not create a cycle
      const target = all[Math.floor(r() * all.length)];
      const inside = (a, b) => { let f = false; (function w(x) { if (x === b) f = true; for (const ks of Object.values(x.lists ?? {})) ks.forEach(w); })(a); return f; };
      if (live(target) && !inside(pick.n, target.n)) {
        const arr = pick.parent.lists[pick.list]; arr.splice(arr.indexOf(pick.n), 1);
        const dst = ((target.n.lists ??= {}).y ??= []); dst.splice(Math.floor(r() * (dst.length + 1)), 0, pick.n);
        all.push({ n: pick.n, parent: target.n, list: "y" });
      }
    } else if (op === 5 && pick.parent) { const arr = pick.parent.lists[pick.list]; const i = arr.indexOf(pick.n), j = Math.floor(r() * arr.length); arr.splice(i, 1); arr.splice(j, 0, pick.n); }
  }
  return h;
}

