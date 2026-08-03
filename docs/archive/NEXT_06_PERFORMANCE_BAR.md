# NEXT 06 — Missing performance / ping meter

**Screenshot symptom:** real 1.12 has the small latency meter at the bar's bottom-right
(the vertical green/yellow/red column). MSUI omits it.

---

## benilla spec (PROOF)

Source: `benilla/crates/benilla/assets/ui/ActionBar.xml`.

**Frame** `BenillaPerformanceBarFrame` (`:867-892`): `16x64`, `frameStrata="LOW"`,
`<Anchor point="BOTTOMRIGHT" relativeTo="BenillaActionBar" relativePoint="BOTTOMRIGHT"><Offset
x=-227 y=-10>` (`:870`). Its bottom-right sits `(227, 10)` in from the bar's bottom-right; it
hangs 10 px below the bar so the column shows in the bar's transparent recess.

**Texture** `BenillaPerformanceBar` (`:874`): `Interface\MainMenuBar\UI-MainMenuBar-PerformanceBar`,
`20x66`, `<Anchor point="TOPRIGHT"/>` (overspills 4 px left, 2 px below the frame). This is the
tinted element. A HIGH-strata `setAllPoints` hover button sits over it for the tooltip.

**Tint law** (`:520-521, 560-565`, verbatim):
```
PERFORMANCEBAR_LOW_LATENCY    = 300
PERFORMANCEBAR_MEDIUM_LATENCY = 600
latency > 600 → SetVertexColor(1,0,0)  red
latency > 300 → SetVertexColor(1,1,0)  yellow
else          → SetVertexColor(0,1,0)  green
```
Latency only — 1.12 never scales the bar height, only its color.

**Poll cadence** `PERFORMANCEBAR_UPDATE_INTERVAL = 10` s (`:522`), seeded to 0 so the first
`OnUpdate` polls immediately (`:534, 553-558`). Hover tooltip (`:547-551, 573-581`): "Latency:
{ms}ms" + a wrapped newbie tip, refreshed on the same 10 s beat.

> Correction to prior notes: `src/perf.rs` is the dev **FPS HUD** (Ctrl+Cmd+P), unrelated to
> this meter — no latency, no red>600 law. Its only reusable piece is
> `FrameStats::fps() = 1000/mean(frame_ms)` (`perf.rs:108-118`). The 1.12 meter's latency comes
> from `GetNetStats()` (the averaged ping RTT), `main.rs:540-542`.

---

## MSUI implementation (+ the latency gap)

MSUI has **no RTT measurement**: `NetworkClient.StartPing` fires `CMSG_PING` every 30 s with
`lastRttMs` hardcoded `0` (`Net/NetworkClient.cs:417-423`, `Net/WorldSession.cs:268-273`), and
`SMSG_PONG` (`Net/Opcodes.cs:17`) is enqueued but never handled to derive latency. Two options:

1. **Stub** `_latencyMs` to a fixed low value (always-green) until RTT lands — clears the
   defect visually.
2. **Measure RTT:** stamp `MovementInfo.ClientUptimeMs()` when `Ping` is sent; on `SMSG_PONG`
   receipt compute `rtt = now - sentAt`, roll into `_latencyMs` (a moving average = benilla's
   "averaged ping RTT"). Drop the 30 s ping cadence if you want a live number sooner.

Draw sketch (call from `DrawActionBars`, background list). Bar bottom-right =
`(barMin.X + 1024*scale, display.Y)`:

```csharp
private void DrawPerformanceMeter(ImDrawListPtr bg, Vector2 barMin, float scale, Vector2 display)
{
    uint tex = _gameplayArt!.Handle(@"Interface\MainMenuBar\UI-MainMenuBar-PerformanceBar.blp");
    if (tex == 0) return;
    Vector2 frameBR = new(barMin.X + (1024f - 227f) * scale, display.Y - 10f * scale);
    Vector2 frameTL = frameBR - new Vector2(16f, 64f) * scale;
    Vector2 texTR = new(frameBR.X, frameTL.Y);                    // 20x66 anchored TOPRIGHT of frame
    Vector2 texTL = texTR - new Vector2(20f, 0f) * scale;
    Vector2 texBR = texTL + new Vector2(20f, 66f) * scale;

    int latency = _latencyMs;                                    // stub or measured
    Vector4 tint = latency > 600 ? new(1, 0, 0, 1)
                 : latency > 300 ? new(1, 1, 0, 1)
                 :                 new(0, 1, 0, 1);
    bg.AddImage((nint)tex, texTL, texBR, Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(tint));

    ImGui.SetCursorScreenPos(frameTL);
    ImGui.InvisibleButton("##perf", new Vector2(16f, 64f) * scale);
    if (ImGui.IsItemHovered())
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"Latency: {latency}ms");
        ImGui.EndTooltip();
    }
}
```

Poll `_latencyMs` on a 10 s cadence to match `PERFORMANCEBAR_UPDATE_INTERVAL`.

**Verification:** the green column appears bottom-right of the bar; hover shows "Latency:
Nms". With option 2, pulling the network cable / high ping turns it yellow then red at the
300/600 ms thresholds.
