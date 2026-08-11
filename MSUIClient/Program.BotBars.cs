using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // ── Layered bot skillbars ─────────────────────────────────────────────────────────────────
    // Bots run on rotations server-side and own no action buttons, so the client builds their
    // bars from three layers (most specific slot wins):
    //   1. generated baseline — the bot's known ACTIVE spells, highest rank per name, laid onto
    //      the main bar (wire 0-11) then the bottom-left bar (wire 48-59);
    //   2. class customization — a persisted sparse slot map applied to every bot of the class;
    //   3. per-bot override — a persisted sparse slot map for one named bot.
    // Persisted in botbars.json at the repo root, together with a per-bot spell cache so the
    // free view can show bars for bots that are not currently possessed. An explicit 0 in a
    // layer masks the layer below (a deliberately emptied slot stays empty).

    private sealed class BotBarsDocument
    {
        public Dictionary<string, Dictionary<string, uint>> ClassSlots { get; set; } = [];
        public Dictionary<string, Dictionary<string, uint>> BotSlots { get; set; } = [];
        public Dictionary<string, List<uint>> BotSpells { get; set; } = [];
        public Dictionary<string, string> BotClasses { get; set; } = [];
        public Dictionary<string, string> BotRoles { get; set; } = [];   // Tank / Healer / DPS
        public Dictionary<string, bool> BotLinks { get; set; } = [];     // chain membership (absent = linked)
    }

    private BotBarsDocument? _botBars;
    private bool _botBarSaveToClass;   // banner toggle: edits land on the class layer instead

    private string BotBarsPath => Path.Combine(_config.RepoRoot, "botbars.json");

    private BotBarsDocument LoadBotBars()
    {
        if (_botBars is not null) return _botBars;
        try
        {
            if (File.Exists(BotBarsPath))
                _botBars = JsonSerializer.Deserialize<BotBarsDocument>(File.ReadAllText(BotBarsPath));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[botbars] load failed ({ex.Message}); starting fresh");
        }
        return _botBars ??= new BotBarsDocument();
    }

    private void SaveBotBars()
    {
        try
        {
            File.WriteAllText(BotBarsPath, JsonSerializer.Serialize(LoadBotBars(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[botbars] save failed: {ex.Message}");
        }
    }

    private static string ClassIdName(byte classId) => classId switch
    {
        1 => "Warrior", 2 => "Paladin", 3 => "Hunter", 4 => "Rogue", 5 => "Priest",
        7 => "Shaman", 8 => "Mage", 9 => "Warlock", 11 => "Druid", _ => "",
    };

    private string BotClassName(ulong guid, string name)
    {
        if (_entities.TryGet(guid, out WorldEntity entity) && entity.IsPlayer)
        {
            (_, byte classId, _, _) = entity.Fields.Bytes0;
            string resolved = ClassIdName(classId);
            if (resolved.Length != 0) return resolved;
        }
        return name.Length != 0 ? LoadBotBars().BotClasses.GetValueOrDefault(name, "") : "";
    }

    private static int ParseSpellRank(string rank)
    {
        int space = rank.LastIndexOf(' ');
        return space >= 0 && int.TryParse(rank[(space + 1)..], out int value) ? value : 0;
    }

    /// <summary>Resolve the three layers into a full 120-slot bar.</summary>
    private uint[] ResolveBotBar(string className, string botName, IReadOnlySet<uint> knownSpells)
    {
        var bar = new uint[120];

        // Layer 1: generated baseline — highest rank per spell name, active spells only.
        if (_spellCatalog is not null)
        {
            var bestByName = new Dictionary<string, (uint Id, int Rank)>();
            foreach (uint id in knownSpells.OrderBy(v => v))
            {
                if (!_spellCatalog.TryGet(id, out SpellInfo info)) continue;
                if (info.Passive || !info.InSpellbook || info.Name.Length == 0) continue;
                int rank = ParseSpellRank(info.Rank);
                if (!bestByName.TryGetValue(info.Name, out (uint Id, int Rank) seen) ||
                    rank > seen.Rank || (rank == seen.Rank && id > seen.Id))
                    bestByName[info.Name] = (id, rank);
            }
            List<uint> picks = [.. bestByName.Values.Select(v => v.Id).OrderBy(v => v)];
            for (int i = 0; i < picks.Count && i < 24; i++)
                bar[i < 12 ? i : 48 + (i - 12)] = picks[i];
        }

        // Layer 2 + 3: persisted sparse overrides (an explicit 0 empties the slot).
        BotBarsDocument doc = LoadBotBars();
        void ApplyLayer(Dictionary<string, uint>? layer)
        {
            if (layer is null) return;
            foreach ((string key, uint spell) in layer)
                if (int.TryParse(key, out int slot) && slot is >= 0 and < 120)
                    bar[slot] = spell;
        }
        if (className.Length != 0) ApplyLayer(doc.ClassSlots.GetValueOrDefault(className));
        if (botName.Length != 0) ApplyLayer(doc.BotSlots.GetValueOrDefault(botName));
        return bar;
    }

    /// <summary>
    /// Fill a bot's action store from the layered bars and cache its spellbook + class for
    /// later free-view resolution. Called when the possession proxy delivers the spellbook
    /// (and after SMSG_ACTION_BUTTONS clears a bar the server has no content for).
    /// </summary>
    private void PopulateBotBar(ulong guid)
    {
        if (guid == 0 || guid == LocalPlayerGuid) return;
        PlayerActions store = ActionsFor(guid);
        string name = ResolveUnitName(guid);
        string className = BotClassName(guid, name);

        if (name.Length != 0 && store.KnownSpells.Count > 0)
        {
            BotBarsDocument doc = LoadBotBars();
            doc.BotSpells[name] = [.. store.KnownSpells.OrderBy(v => v)];
            if (className.Length != 0) doc.BotClasses[name] = className;
            SaveBotBars();
        }

        uint[] bar = ResolveBotBar(className, name, store.KnownSpells);
        for (int slot = 0; slot < bar.Length; slot++)
            store.Set(slot, bar[slot] != 0
                ? new ActionSlot((byte)(bar[slot] >> 24), bar[slot] & 0x00ff_ffffu)
                : null);
    }

    /// <summary>
    /// Free-view selection wants this bot's bars without possessing it: seed the store from
    /// the persisted spell cache (recorded during any earlier possession) and resolve.
    /// </summary>
    private void EnsureBotBarForViewing(ulong guid)
    {
        if (guid == 0 || guid == LocalPlayerGuid) return;
        PlayerActions store = ActionsFor(guid);
        if (store.KnownSpells.Count == 0)
        {
            string name = ResolveUnitName(guid);
            if (name.Length != 0 &&
                LoadBotBars().BotSpells.TryGetValue(name, out List<uint>? cached))
                store.SeedSpells(cached);
        }
        if (store.OccupiedCount == 0 && store.KnownSpells.Count > 0)
            PopulateBotBar(guid);
    }

    // ── RTS/CRPG command strips beside the party portraits ────────────────────────────────────
    // Toggled by Settings.Controls.RtsCommands. Role feeds the rotation work later; Hold and
    // Patrol ride the existing CMSG_SUI_ORDER codes (2 stop / 4 patrol-loop).

    private static string RoleGlyph(string role) => role switch
    {
        "Tank" => "T", "Healer" => "H", _ => "D",
    };

    /// <summary>Is this member part of the chain? Absent from the map = linked (the default).</summary>
    private bool PartyMemberLinked(string name) =>
        LoadBotBars().BotLinks.GetValueOrDefault(name, true);

    /// <summary>
    /// Divinity-style chain toggle: linked members follow whoever is being driven
    /// (ORDER_LINK, server-side formation gate); an unlinked member stands its ground.
    /// </summary>
    private void SetPartyLink(PartyMember member, bool linked)
    {
        _net?.SuiOrder(6, [member.Guid], 0, linked ? 1f : 0f, 0, 0);
        BotBarsDocument doc = LoadBotBars();
        doc.BotLinks[member.Name] = linked;
        SaveBotBars();
        AddChatMessage(linked
            ? $"{member.Name} chained back into the party."
            : $"{member.Name} unchained — standing ground until re-linked.");
    }

    /// <summary>Pixels between the pressed portrait's centre and the cursor.</summary>
    private float PartyDragDistance(int index)
    {
        float scale = GameplayUiScale();
        Vector2 from = new Vector2(PartyFrameUiLaw.FirstX + 47,
            PartyFrameUiLaw.MemberY(index) + 15) * scale;
        return (ImGui.GetIO().MousePos - from).Length();
    }

    private bool MouseOverPlayerFrame()
    {
        float scale = GameplayUiScale();
        Vector2 mouse = ImGui.GetIO().MousePos;
        Vector2 min = new Vector2(-19, 4) * scale;      // the player frame's authored origin
        Vector2 size = new Vector2(232, 100) * scale;
        return mouse.X >= min.X && mouse.X <= min.X + size.X &&
               mouse.Y >= min.Y && mouse.Y <= min.Y + size.Y;
    }

    private void DrawRtsCommandStrips(PartyMember[] members)
    {
        if (!Settings.Controls.RtsCommands || _net is null) return;
        float scale = GameplayUiScale();
        BotBarsDocument doc = LoadBotBars();
        for (int i = 0; i < members.Length; i++)
        {
            PartyMember member = members[i];
            Vector2 pos = new Vector2(
                PartyFrameUiLaw.FirstX + PartyFrameUiLaw.FrameWidth + 6,
                PartyFrameUiLaw.MemberY(i) + 12) * scale;
            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.35f);
            ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;
            if (!ImGui.Begin($"##rts-strip-{i}", flags)) { ImGui.End(); continue; }

            string role = doc.BotRoles.GetValueOrDefault(member.Name, "DPS");
            if (ImGui.SmallButton($"{RoleGlyph(role)}##role{i}"))
            {
                doc.BotRoles[member.Name] = role switch
                {
                    "DPS" => "Tank",
                    "Tank" => "Healer",
                    _ => "DPS",
                };
                SaveBotBars();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Role: {role} — click to cycle (feeds rotations later)");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Hold##rts{i}"))
            {
                _net.SuiOrder(2, [member.Guid], 0, 0, 0, 0);
                AddChatMessage($"{member.Name}: stand your ground.");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Stand your ground: stop and hold this spot");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Patrol##rts{i}") &&
                _entities.TryGet(member.Guid, out WorldEntity unit))
            {
                _net.SuiOrder(4, [member.Guid], 0,
                    unit.Position.X, unit.Position.Y, unit.Position.Z);
                AddChatMessage($"{member.Name}: patrol the current route.");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Loop the waypoint chain (Ctrl+RightClick spots in free view\n" +
                    "with the bot selected first); its current spot closes the loop.\n" +
                    "Move or Hold cancels the patrol.");
            bool linked = PartyMemberLinked(member.Name);
            ImGui.SameLine();
            if (ImGui.SmallButton(linked ? $"⛓##rtsl{i}" : $"✕##rtsl{i}"))
                SetPartyLink(member, !linked);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(linked
                    ? "Chained: follows whoever you drive. Click (or drag the portrait away) to unchain."
                    : "Unchained: stands its ground. Click (or drop the portrait on another) to re-chain.");
            ImGui.End();
        }
    }

    /// <summary>
    /// The permanent Divinity-style chain: link beads run from the player frame down
    /// through every chained portrait; an unchained member shows a broken stub instead.
    /// Always visible with the party frames — this is party state, not an RTS-strip extra.
    /// </summary>
    private void DrawPartyChainLinks(PartyMember[] members)
    {
        float scale = GameplayUiScale();
        var draw = ImGui.GetForegroundDrawList();
        // Anchor rail: just left of the portraits, starting under the player frame.
        float railX = (PartyFrameUiLaw.FirstX + 2) * scale;
        Vector2 previous = new(railX, 96f * scale);   // player frame's lower edge
        const uint beadColor = 0xFFC8DCF0;            // ABGR pale steel
        const uint beadShadow = 0x88000000;
        for (int i = 0; i < members.Length; i++)
        {
            Vector2 at = new(railX, (PartyFrameUiLaw.MemberY(i) + 15) * scale);
            if (PartyMemberLinked(members[i].Name))
            {
                // Beads along the segment read as chain links without heavy art.
                Vector2 delta = at - previous;
                float length = delta.Length();
                int beads = Math.Max(2, (int)(length / (7f * scale)));
                for (int b = 0; b <= beads; b++)
                {
                    Vector2 p = previous + delta * (b / (float)beads);
                    draw.AddCircleFilled(p + new Vector2(1, 1), 2.4f * scale * 0.5f + 1.2f, beadShadow);
                    draw.AddCircle(p, 2.4f * scale * 0.5f + 1.2f, beadColor, 10, 1.4f);
                }
                previous = at;   // the chain continues from this member
            }
            else
            {
                // Broken stub: two offset dashes where the link would meet the portrait.
                Vector2 stub = at + new Vector2(-4f * scale * 0.5f, 0);
                draw.AddLine(stub + new Vector2(-6, -4), stub + new Vector2(-1, -1), 0xFF5060E0, 2f);
                draw.AddLine(stub + new Vector2(1, 1), stub + new Vector2(6, 4), 0xFF5060E0, 2f);
            }
        }
    }

    /// <summary>Dashed hint from the dragged portrait to the cursor while chaining a follow.</summary>
    private void DrawPartyDragFeedback(PartyMember[] members)
    {
        if (_partyPressIndex < 0 || _partyPressIndex >= members.Length ||
            _partyPressButton != PartyPointerButton.Left) return;
        float scale = GameplayUiScale();
        Vector2 from = new Vector2(PartyFrameUiLaw.FirstX + 47,
            PartyFrameUiLaw.MemberY(_partyPressIndex) + 15) * scale;
        Vector2 mouse = ImGui.GetIO().MousePos;
        if ((mouse - from).Length() < 20f * scale) return;
        var draw = ImGui.GetForegroundDrawList();
        DrawDashedLine(draw, from, mouse, 0xCC55D8F0, 8f, 6f);
        draw.AddCircleFilled(mouse, 4f, 0xFF55D8F0);
    }

    /// <summary>
    /// A bar edit while driving a bot: persist to the chosen layer (this bot, or its whole
    /// class) and keep the in-memory store as the resolved truth. Never touches the wire —
    /// CMSG_SET_ACTION_BUTTON would edit the SESSION character's server bar.
    /// </summary>
    private void SaveBotBarSlot(int slot, uint packed)
    {
        if (ControlledGuid == LocalPlayerGuid) return;
        string name = ResolveUnitName(ControlledGuid);
        if (name.Length == 0) return;
        string className = BotClassName(ControlledGuid, name);
        BotBarsDocument doc = LoadBotBars();
        string key = slot.ToString();
        if (_botBarSaveToClass && className.Length != 0)
        {
            doc.ClassSlots.TryGetValue(className, out Dictionary<string, uint>? classMap);
            (classMap ??= doc.ClassSlots[className] = []) [key] = packed;
            // A per-bot override on the same slot would mask the class edit just made.
            if (doc.BotSlots.TryGetValue(name, out Dictionary<string, uint>? botMap))
                botMap.Remove(key);
        }
        else
        {
            doc.BotSlots.TryGetValue(name, out Dictionary<string, uint>? botMap);
            (botMap ??= doc.BotSlots[name] = []) [key] = packed;
        }
        SaveBotBars();
    }
}
