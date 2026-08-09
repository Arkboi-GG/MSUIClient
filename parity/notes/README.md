# Parity notes (written by ParityDeck)

One JSON file per packet: `notes/<pairId>/<packetId>.json` (pair-level notes: `_pair.json`).
Schema: `{ schemaVersion, pairId, packetId, notes: [ { id, utc, author, kind, text, tags[], resolved } ] }`.

`kind` is one of:
- `note` — plain observation.
- `directive` — an instruction for the next agent working this packet. Act on it, then set `resolved: true`.
- `question` — needs an answer before or during the next pass; answer in a follow-up note and resolve.
- `decision` — a ruling that constrains future work (e.g. preserve MSUI behavior X). Never delete; do not resolve.
- `blocker` — the packet cannot progress until this is cleared.

Agents: before working any packet, read its note file. Unresolved `directive`/`blocker`/`question`
notes take precedence over the default packet workflow. When you act on a note, append a `note`
recording what you did and set the original's `resolved` flag to `true` (keep the original text).