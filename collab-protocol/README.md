# AGENT DAW collaboration protocol, v1 (draft)

Licensed under the Apache License, Version 2.0 (see `LICENSE`). Authored from the protocol specification below, **not derived from any DAW's source code**. This directory is meant to become its own package so that Agent One can depend on it without importing any AGPL code (see `../phase0/LICENSING_BOUNDARY.md` section 3).

## Model

A project is a **tree of objects** keyed by stable 32-hex ids. Each node has a `type`, independent `props` (any JSON), named ordered child `lists`, and named content-addressed `blobs` (`sha256:...`, opaque: audio, plugin state). The merge engine needs no knowledge of what a "track" or "clip" is; DAWs map their own model onto node types.

- `schema/manifest.schema.json`: a snapshot. `version` is the opaque master version assigned by the service.
- `schema/changeset.schema.json`: ordered ops against `baseVersion`: `add`, `remove` (subtree), `move`, `set`, `unset`, `setBlob`, `unsetBlob`. `set`/`unset`/blob ops may carry an `expect` precondition (`{value}` or `{absent:true}`) so a change can be rejected, not silently applied, when what it edited has changed.

## Reference semantics (`src/`)

- `validateManifest` / `validateChangeSet`: JSON Schema plus unique node ids.
- `applyChangeSet(manifest, cs)`: pure and **atomic**; throws `ApplyError` with a code (`missing_node`, `duplicate_id`, `missing_anchor`, `cycle`, `root_immutable`, `precondition`, `stale_base`, `wrong_project`, `unknown_op`) and the failing op index. Output is in canonical form (empty props/blobs/lists omitted).
- `diff(base, head)`: a change set with `apply(base, diff(base, head)) == head`. Not minimal, always correct. A node cannot change `type`.

## How a DAW maps onto it (Nota, as implemented)

Root = project (props: every top-level field but the tracks; `blobs`: sha256 of every bundle file). Lists: `tracks` of `track` nodes; each has `midiClips`, `audioClips`, `sessionSlots` of clip nodes. Track references that the DAW stores as indices are written as `{"$track": "<id>"}` so they survive reordering. See `examples/from-daw-rich.json` (produced by the DAW's own export) and `examples/proposal-kick.changeset.json`.

## Three-way merge (`src/merge.mjs`)

`merge(base, ours, theirs, {resolutions})` returns `{status, merged, conflicts}`. Props, blobs, a node's location and each list's order merge independently. Conflict kinds: `prop`, `blob`, `move`, `order`, `add_add`, `delete_modify` (a delete against any change inside the node, or a move), and `resurrected` (a surviving node whose parent did not survive; its parent is brought back). `status` is `"conflicted"` until every conflict has an entry in `resolutions` (`"ours"`/`"theirs"`, `{value}` for a prop, `{hash}` for a blob, `"accept"` for `resurrected`); a conflicted `merged` is provisional (ours wins) and must never be committed. When the deleter wins a `delete_modify`, everything the other side still has inside that node is deleted with it. The result is symmetric: swapping ours and theirs gives the same tree, including where two sides insert at the same spot. A merge result carries no `version`; the committing service assigns it.

## Not in v1 (decided later)

Node types registry per DAW, signatures. (Note-level merge is done: a clip's `notes` are a keyed collection, pitch + start, so different notes merge and the same note edited two ways conflicts on that note.)

## Run the tests

`npm install && npm test` (Node 24; the only dependency is `ajv`).
Tests: `test/protocol.test.mjs` (schema, apply, diff) and `test/merge.test.mjs` (scenarios per conflict kind and randomized properties: identity, fast-forward, symmetry, valid output, every conflict resolvable). Includes a property test: 1500 seeded random edits (adds, removes, moves, reorders, prop changes) must round-trip through `diff` and `apply`; mutating `diff` makes it fail.
