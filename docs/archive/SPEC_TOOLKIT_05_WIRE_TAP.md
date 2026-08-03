# SPEC TOOLKIT 05 — Wire tap (recorder stage A)

Instrument I7, stage A only, of `GAMEPLAY_FOUNDATION_PLAN.md`: make the packet
stream observable. Replay (stages B/C) is explicitly NOT in this spec — do not
build any replay scaffolding. SPEC 00's orders remain binding.

Build this before SPEC 06 (the gameplay dump consumes this ring).

---

## 1. The wire ring (always on, in-memory)

New file `MSUIClient/Engine/WireRing.cs`:

```csharp
public readonly record struct WirePacket(
    double Time, bool Outgoing, ushort Opcode, string OpcodeName, int Size);
public sealed class WireRing   // capacity 512, single-threaded consumer model:
{                              // see threading note below
    public void Add(WirePacket p);
    public IReadOnlyList<WirePacket> Snapshot();
}
```

Capture sites — find the single choke points; do not scatter:

- **Incoming:** wherever `WorldSession`/`Program.Net.cs` dispatches a decoded SMSG
  by opcode (there is one dispatch switch/site — grep the opcode dispatch). Capture
  after decryption/framing, before per-opcode handling.
- **Outgoing:** wherever `NetworkClient`/`WorldSession` frames and sends a CMSG
  (again one choke point; senders funnel through it).

`OpcodeName` from the existing `Opcodes` enum (`Enum.GetName`, cache the lookup —
this runs per packet). Unknown opcodes: `0x{op:X4}`.

**Threading note (verify, don't assume):** determine which thread the receive path
decodes on. If it is not the main thread, either lock the ring's Add/Snapshot with
a simple `lock` (contention is trivial at this rate) or marshal via the existing
mechanism the net layer already uses to hand packets to the game loop — whichever
the code already does for handler dispatch, mirror it. State the answer in the
report with a file:line cite.

No console output from the ring. Zero cost when nobody looks at it beyond the
struct write.

## 2. The binary log (opt-in, DevTools toggle)

DevTools checkbox **"Record wire log"** (default off, lives beside the Verdicts
panel header controls). While on, every captured packet is appended to
`dumps/wire-<yyyyMMdd-HHmmss>.wlog` (timestamp fixed at toggle-on):

```
u8  direction        0=SMSG 1=CMSG
f64 time             NowSeconds()
u16 opcode
u32 fullSize         the real payload size
u16 storedSize       min(fullSize, 256)
u8[storedSize]       payload prefix, post-decryption
```

- Payloads capped at 256 bytes; `fullSize` preserves the truth.
- Buffered `FileStream`, flushed on toggle-off and on client exit; also flush every
  5 s so a crash loses little.
- Toggling on prints one line: `[wire] recording to dumps/wire-….wlog` and copies
  that path to the clipboard (standing rule: shown ⇒ copyable).
- A companion `.txt` with one human-readable line per packet
  (`t=…s CMSG_CAST_SPELL(0x12E) 12B  1A 02 00 00 …`) is written alongside —
  this is the file Nico pastes from, the `.wlog` is for future tooling. If writing
  both feels heavy, the `.txt` alone is the acceptable fallback (record it).

## 3. Wire section in the Verdicts panel

Add a `wire` pseudo-channel to the existing Verdicts panel filters: when enabled it
interleaves the wire ring's tail (formatted like the `.txt` lines, without payload
hex beyond the first 16 bytes) with the verdict rows, same clipboard affordances.
Implementation may simply merge-by-time at draw; do not restructure the panel.

## 4. Boundaries

- Read-only observation. No change to framing, encryption, send timing, or handler
  order. The tap must be physically incapable of altering the stream (capture =
  copy out of the already-decoded buffer).
- Do not log account/session credentials: skip payload storage (store size only)
  for the auth/login opcode family — identify it from `Opcodes.cs` (the
  auth-session / login-proof opcodes) and list the skipped opcodes in the report.

## Test protocol / definition of done

1. Log in, kill and loot one mob with recording on: the `.txt` shows the documented
   loot sequence (`CMSG_LOOT` → `SMSG_LOOT_RESPONSE` → `CMSG_AUTOSTORE_LOOT_ITEM` →
   `SMSG_LOOT_REMOVED` …) with sizes matching `Net/LootState.cs`'s documented wire
   shapes (PORT_SESSION_2026-07-30 §4).
2. The wire pseudo-channel shows the same packets in-client, copyable.
3. Toggle off → file flushed and closed; toggle on again → new file.
4. `devTools:false` → ring still fills (harmless), no toggle UI, no files ever
   written.

## Live checks for Nico (copy into report verbatim)

1. Record a session: enter world, cast once, loot once, toggle off. Send the
   assistant the `.txt` — this replaces "the loot window misbehaved" prose
   forever.
