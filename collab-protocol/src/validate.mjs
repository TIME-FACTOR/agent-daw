// Copyright the AGENT DAW protocol authors. Licensed under the Apache License, Version 2.0.
import Ajv2020 from "ajv/dist/2020.js";
// Static JSON imports (not readFileSync): a bundler inlines them, so a packaged single-file server still has its schemas.
import manifestSchema from "../schema/manifest.schema.json" with { type: "json" };
import changesetSchema from "../schema/changeset.schema.json" with { type: "json" };

const ajv = new Ajv2020({ allErrors: true, strict: true });
ajv.addSchema(manifestSchema);
ajv.addSchema(changesetSchema);
const vManifest = ajv.getSchema("https://agentdaw.invalid/schema/manifest/1");
const vChangeSet = ajv.getSchema("https://agentdaw.invalid/schema/changeset/1");

const fmt = (errs) => (errs ?? []).map((e) => `${e.instancePath || "/"} ${e.message}`);

/** Every node id in a manifest must be unique (a schema cannot say this). */
export function duplicateIds(manifest) {
  const seen = new Set(), dup = new Set();
  const walk = (n) => {
    if (seen.has(n.id)) dup.add(n.id); else seen.add(n.id);
    for (const list of Object.values(n.lists ?? {})) for (const c of list) walk(c);
  };
  walk(manifest.root);
  return [...dup];
}

/** @returns {{ok: boolean, errors: string[]}} */
export function validateManifest(m) {
  if (!vManifest(m)) return { ok: false, errors: fmt(vManifest.errors) };
  const dup = duplicateIds(m);
  if (dup.length) return { ok: false, errors: dup.map((d) => `duplicate node id ${d}`) };
  if (m.root.id === undefined) return { ok: false, errors: ["root has no id"] };
  return { ok: true, errors: [] };
}

/** @returns {{ok: boolean, errors: string[]}} */
export function validateChangeSet(cs) {
  return vChangeSet(cs) ? { ok: true, errors: [] } : { ok: false, errors: fmt(vChangeSet.errors) };
}
