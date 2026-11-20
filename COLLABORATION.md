# AGENT DAW: collaboration support in this fork

This fork of [Nota](https://github.com/nota-daw/nota) (AGPL-3.0) adds what a host program needs to let several producers work on one project. It adds **no networking and no accounts**: the DAW stays a standalone program and works exactly as before without any host.

## What was added

- **Stable IDs.** Every track and clip has a stable 128-bit id (minted in `Engine::settleUids`, stored in the project file as `id` with no format-version bump; older builds keep it, and files without ids get ids derived from content so every opener agrees).
- **Manifest export/import/check/apply** (`ProjectManifest`, `CollaborationService`): the project as a content-addressed tree (`agentdaw.manifest/1`), applied atomically with rollback. Hostile manifests are refused (shape checks, paths confined to the project folder, size limits).
- **Local API** (`collab-protocol/contract/local-api.1.json`): four `collab_*` tools on the DAW's loopback MCP server, off unless enabled in Preferences.
- **Studios** (`StudioManager`, `ReadOnlyEngineProxy`): a private "My Studio" engine and a read-only "Shared Studio" engine; the shared one cannot be edited from the DAW (writes fail closed).
- **Audio tap** (`GET /audio/tap`, loopback only): the live master mix as a stream for a host's Listen Together.
- `collab-protocol/`: the **Apache-2.0** manifest/change-set protocol library (schema, apply, diff, three-way merge with note-level conflicts). `npm install && npm test`.

## What is not here

The shared-master service (proposals, unanimous approval, relay, presence, Listen Together) is a separate, proprietary component of a host program and is not part of this repository.

## Licensing

Everything in this repository outside `collab-protocol/` is under Nota's license (AGPL-3.0). `collab-protocol/` was written from the protocol specification, not derived from Nota's source, and is Apache-2.0 (`collab-protocol/LICENSE`).

## Status

Work in progress on the branch `agentdaw/p0-stable-ids`. Not endorsed by or affiliated with the Nota maintainers.
