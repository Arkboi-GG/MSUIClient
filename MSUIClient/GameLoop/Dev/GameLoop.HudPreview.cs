using System.Numerics;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Offline HUD preview — draws the gameplay interface with no server session,
/// against a synthetic player.
///
/// WHY THIS EXISTS
///   Every world-render change could be checked in seconds with the creator
///   probe (boot offline, screenshot, look). UI changes could not: the whole
///   gameplay HUD hangs off `NetState.InWorld`, so the probe drew a world with
///   no interface on it at all. That asymmetry meant painterly's square chrome
///   shipped unverified, and the first thing it did was paint a black rect over
///   every spell icon on the action bar - a bug a single screenshot would have
///   caught. This closes that hole.
///
/// WHAT IT IS NOT
///   Not a fake network client, and not a second HUD code path. The real
///   DrawCombatHud runs, drawing the real frames from the real draw code; the
///   only thing supplied is a synthetic controlled entity and permission for
///   four guards to pass without a session. Panels that genuinely need server
///   state (loot, vendor, quests, the chat backlog) early-return on their own
///   `_net` checks and simply stay closed - which is fine, because the pieces
///   worth eyeballing are the ones that draw from unit fields: player frame,
///   minimap, action bars.
///
/// TURNING IT ON
///   MSUI_HUD_PREVIEW=1, alongside the usual offline probe:
///     $env:MSUI_HUD_PREVIEW = "1"
///     $env:MSUI_CREATOR_PROBE = "spell=Cone of Cold;slot=CLOUDS;to=SNOWFLAKE2"
///     dotnet run --project MSUIClient -- &lt;config.json&gt;
///   It is env-gated and never consulted when a real session exists, so a
///   normal launch cannot reach any of it.
/// </summary>
public sealed partial class GameLoop
{
    private static readonly bool HudPreviewRequested =
        Environment.GetEnvironmentVariable("MSUI_HUD_PREVIEW") is "1" or "true";

    private bool _hudPreviewEntityReady;

    /// <summary>
    /// True only when the preview was asked for AND no real session is in world.
    /// Testing IsInWorld rather than `_net is null` matters: an offline launch
    /// can still construct a NetworkClient that never connects, so a null check
    /// suppressed the preview in exactly the configuration it exists for. A
    /// real in-world session always wins, which is what keeps a normal launch
    /// identical even with the variable left set.
    /// </summary>
    internal bool HudPreview => HudPreviewRequested && _net is not { IsInWorld: true };

    /// <summary>
    /// The stand-in the HUD reads: a level-30 human male mage with plausible
    /// health and mana, so the bars, the level text and the portrait fallback
    /// all have something real to render instead of zeroes.
    /// </summary>
    private void EnsureHudPreviewEntity()
    {
        if (_hudPreviewEntityReady) return;
        _hudPreviewEntityReady = true;

        var fields = new ObjectFields().AsCreated();
        fields.SetU32(ObjectFields.UNIT_HEALTH, 2400);
        fields.SetU32(ObjectFields.UNIT_MAXHEALTH, 3100);
        fields.SetU32(ObjectFields.UNIT_POWER1, 1900);      // mana is power slot 0
        fields.SetU32(ObjectFields.UNIT_MAXPOWER1, 2600);
        fields.SetU32(ObjectFields.UNIT_LEVEL, 30);
        // BYTES_0 packs race | class | gender | powerType, one byte each:
        // race 1 (human), class 8 (mage), gender 0 (male), power 0 (mana).
        fields.SetU32(ObjectFields.UNIT_BYTES_0, 1u | (8u << 8) | (0u << 16) | (0u << 24));

        _entities.AddSynthetic(new WorldEntity
        {
            Guid = LocalPlayerGuid,
            Type = ObjectTypeId.Player,
            Fields = fields,
            Position = _controller?.Position ?? Vector3.Zero,
            Orientation = _controller?.Yaw ?? 0f,
        });
        SeedHudPreviewActionBar();
        Console.WriteLine($"[hud-preview] synthetic player {LocalPlayerGuid:X} - " +
                          "drawing the gameplay HUD with no session");
    }

    /// <summary>
    /// Fill the first ten action slots, because an EMPTY bar is precisely the
    /// case that cannot catch the bug this harness exists for: painterly's
    /// square slot chrome was painting over every icon, and empty slots have no
    /// icon to paint over. Real 1.12 mage spell ids, so the catalog resolves
    /// proper artwork when it is loaded and falls back to the question mark
    /// when it is not - either way there is something in the slot to obscure.
    ///
    /// Goes through the ordinary ApplyButtons wire path rather than a test-only
    /// setter, so the preview bar is populated exactly the way a real one is.
    /// </summary>
    private void SeedHudPreviewActionBar()
    {
        uint[] spells = [133, 116, 5143, 2136, 122, 1953, 118, 12051, 2139, 1459];
        var body = new byte[120 * sizeof(uint)];
        for (int i = 0; i < spells.Length; i++)
        {
            // Packed = actionId | (kind << 24); ActionSlot.Spell is 0.
            uint packed = spells[i] | ((uint)ActionSlot.Spell << 24);
            BitConverter.TryWriteBytes(body.AsSpan(i * sizeof(uint)), packed);
        }
        ActionsFor(LocalPlayerGuid).ApplyButtons(body);
    }

    /// <summary>
    /// Preview entry point, called instead of the session-gated HUD dispatch.
    /// Requires a live world so the frames sit over terrain rather than over a
    /// cleared buffer, which is also what makes the screenshots worth reading.
    /// </summary>
    private void DrawHudPreview()
    {
        if (_terrain is null || _gameplayArt is null)
        {
            // Said once, because a preview that silently draws nothing is worse
            // than no preview - it reads as "the UI is broken".
            if (!_hudPreviewWaitLogged)
            {
                _hudPreviewWaitLogged = true;
                Console.WriteLine($"[hud-preview] waiting - terrain " +
                                  $"{(_terrain is null ? "null" : "ready")}, gameplay art " +
                                  $"{(_gameplayArt is null ? "null" : "ready")}");
            }
            return;
        }
        EnsureHudPreviewEntity();
        DrawCombatHud();
    }

    private bool _hudPreviewWaitLogged;
}
