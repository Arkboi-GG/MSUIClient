# SPEC-22 X2 bounded transit capture — capture privilege unavailable

## Actual versus predicted

```text
PREDICTED preferred Linux capture: sudo -n tcpdump permitted
ACTUAL tcpdump: /usr/bin/tcpdump exists
ACTUAL sudo -n true: FAIL, "sudo: a password is required"
RESULT: ordered Linux capture path unavailable; no password sudo attempted

PREDICTED Windows fallback: pktmon or netsh elevated capture
ACTUAL pktmon status: FAIL, driver access denied
ACTUAL UAC preflight: no approved elevated process/output after two bounded attempts
ACTUAL netsh trace start: FAIL, operation requires Administrator elevation
ACTUAL installed fallback: no dumpcap, tshark, WinDump, Npcap/NPF service
RESULT: ordered Windows capture path unavailable

PREDICTED built-in VMaNGOS packet logger: use if it covers world opcodes
ACTUAL: Anticheat.PacketLogSize is movement-anticheat history only and writes a
        .pkt file only when the anticheat result kicks or bans
RESULT: not applicable to CMSG_ATTACKSWING; no setting changed

PREDICTED bounded raw capture then deletion after frame extraction
ACTUAL: no capture engine started, no filter changed, no ETL/PCAP/raw capture created
RESULT: no raw capture exists; temporary preflight helper removed
```

## Read-only deployed enumeration

- `/home/wowvmangos/vmangos/run/etc/mangosd.conf:1589-1592` describes
  `Anticheat.PacketLogSize` as previous *movement* packets dumped on a detection
  that results in kick or ban; deployed value is `100` at line 2120.
- `src/game/Anticheat/MovementAnticheat/MovementAnticheat.cpp:399-407` queues
  packets only through `LogMovementPacket`.
- The same file at lines 151-158 writes `movement_log_<account>_<time>.pkt`
  only when the result includes kick or ban.
- `src/game/World.cpp:1109` loads only that movement packet-log size option.

This is not a general ingress/world-opcode logger and cannot record opcode 321
without abusing anticheat behavior, which is outside scope.

## Evidence boundary

X1 still proves the exact attack write returned successfully from the socket:

```text
CMSG_ATTACKSWING bytes=14
sha256=784feef9f39b41853082ecfd8bb6dd47d801f1d3e7143986d79abb317c336420
post-encryption bytes=8263DEEECF998CA20406000030F1
plaintext body=8CA20406000030F1
```

X2 cannot honestly promote those bytes to `present on wire`, because neither
prescribed capture boundary was available. It also cannot call them `absent on
wire`; absence was not measured.

No server code, database, persistent configuration, combat behavior, error
display, or F3-F6 behavior changed. The only attempted tracing mutations were
rejected before start. Current trace status remains stopped.
