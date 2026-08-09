using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Solo corpse looting: the CMSG_LOOT / SMSG_LOOT_RESPONSE flow and the authored 1.12
/// LootFrame. Modeled on benilla ui_loot.rs + net/apply/loot.rs and LootFrame.xml.
/// </summary>
public sealed partial class GameLoop
{
    private readonly LootState _loot = new();
    private ulong _lootPendingGuid;
    private Vector3 _lootOpenedAt;
    private int _lootPage = 1;
    private readonly List<(uint Entry, uint Count, int TriesLeft)> _pendingReceives = new();
    private uint _lootMoneyBefore;
    private uint _lootMoneyExpected;
    private bool _lootMoneyPending;

    private const int LootRowsPerFrame = 4;

    private void ResetLoot()
    {
        _loot.Clear();
        _lootPendingGuid = 0;
        _lootPage = 1;
        _pendingReceives.Clear();
        _lootMoneyPending = false;
    }

    /// <summary>Right-click on a dead, lootable corpse. The kneel plays optimistically at the
    /// send (the reference arms its loot latch at the CMSG_LOOT send site, 0x5df253).</summary>
    private bool RequestLoot(ulong guid)
    {
        bool eligible = _entities.TryGet(guid, out WorldEntity source) && source.IsCreature &&
                        source.IsDead && source.Fields.Lootable;
        if (_net is null || !eligible)
        {
            EmitInterface("loot", "request", "REFUSED", guid,
                $"inWorld={_net?.IsInWorld == true};entity={source is not null};dead={source?.IsDead == true};lootable={source?.Fields.Lootable == true}");
            return false;
        }
        _lootPendingGuid = guid;
        bool sent = _net.Loot(guid);
        EmitInterface("loot", "request", sent ? "SENT" : "SEND_FAILED", guid,
            $"dead={source.IsDead};lootable={source.Fields.Lootable};body={Convert.ToHexString(WorldSession.BuildLootGuidBody(guid))}");
        if (!sent) { _lootPendingGuid = 0; return false; }
        _character?.TriggerOneShot(50); // EmoteLoot: the kneel dip
        return true;
    }

    private void ApplyLootResponse(byte[] body)
    {
        var (guid, lootType, error, gold, items) = LootPackets.ParseResponse(body);
        if (_gameObjectGuid == guid || (_entities.TryGet(guid, out WorldEntity gameObject) && gameObject.IsGameObject))
            EmitInterface("gameobject", "response", lootType == 0 ? "REFUSED" : "LOOT_OPEN", guid,
                $"lootType={lootType};error={error};money={gold};items={items.Count}");
        if (lootType == 0)
        {
            // The error shape. Refusals surface as the red error text the 1.12 client shows.
            ShowUiError(LootPackets.ErrorText(error));
            if (_lootPendingGuid == guid) _lootPendingGuid = 0;
            EmitInterface("loot", "response", "REFUSED", guid, $"error={error};text={SanitizeEvidence(LootPackets.ErrorText(error))}");
            return;
        }
        _loot.Open(guid, lootType, gold, items);
        _lootPage = 1;
        _lootPendingGuid = 0;
        if (_controller is not null) _lootOpenedAt = _controller.Position;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            _lootMoneyBefore = player.Fields.Coinage;
        EmitInterface("loot", "response", items.Count == 0 && gold == 0 ? "EMPTY" : "OPEN", guid,
            $"type={lootType};money={gold};items={items.Count};slots={string.Join(',', items.Select(i => i.Slot))}");
        // Ask for every row's template once so names/quality colors resolve; icons come
        // straight off the wire displayInfoId and never wait.
        if (_items is not null && _net is not null)
            foreach (LootItem item in items)
                _items.Require(item.ItemId, guid, _net);
    }

    private void ApplyLootRemoved(byte[] body)
    {
        if (body.Length < 1) return;
        _loot.RemoveSlot(body[0]);
        EmitInterface("loot", "item", "REMOVED", _loot.Source, $"slot={body[0]};remaining={_loot.Items.Count}");
    }

    private void ApplyLootClearMoney()
    {
        uint cleared = _loot.Gold;
        _loot.ClearMoney();
        EmitInterface("loot", "money", "CLEARED", _loot.Source, $"amount={cleared};remainingItems={_loot.Items.Count}");
    }

    private void ApplyLootReleaseResponse(byte[] body)
    {
        // Idempotent, guid-matched: a stale release for corpse A must not clear a
        // fresh session on corpse B (benilla ui_loot.rs:209-214).
        if (body.Length < 8) return;
        ulong guid = BitConverter.ToUInt64(body, 0);
        if (_loot.Source == guid) _loot.Clear();
        if (_lootPendingGuid == guid) _lootPendingGuid = 0;
        EmitInterface("loot", "release", "RELEASED", guid, "guidMatched=true");
    }

    /// <summary>SMSG_ITEM_PUSH_RESULT — the one reliable "it landed" signal for solo loot.
    /// Drives the green "You receive loot: [Name] xN." line once the template resolves.</summary>
    private void ApplyItemPushResult(byte[] body)
    {
        if (_net is null || body.Length < 41) return;
        var r = new PacketReader(body);
        ulong player = r.ReadU64();
        if (player != _net.PlayerGuid) return;
        uint received = r.ReadU32();
        uint created = r.ReadU32();
        uint showInChat = r.ReadU32();
        byte bagSlot = r.ReadU8();
        uint itemSlot = r.ReadU32(); // 0xFFFFFFFF = stacked onto an existing slot
        uint entry = r.ReadU32();
        uint suffixFactor = r.ReadU32();
        uint randomProperty = r.ReadU32();
        uint count = r.ReadU32();
        if (entry == 0) return;
        uint actualCount = Math.Max(1, count);
        if (_items is not null) _items.Require(entry, 0, _net);
        TriggerItemPushAnimation(bagSlot, itemSlot, entry);
        _pendingReceives.Add((entry, actualCount, 120));
        EmitInterface("loot", "item", "RECEIVED", player,
            $"item={entry};count={actualCount};received={received};created={created};show={showInChat};" +
            $"bag={bagSlot};slot={itemSlot};suffix={suffixFactor};random={randomProperty};bytes={body.Length}");
        if (created != 0)
            ObserveProfessionCreatedItemPush(entry, actualCount);
    }

    /// <summary>Escape closes the loot window before it reaches the game menu.</summary>
    private bool TryCloseLootOnEscape()
    {
        if (!_loot.IsOpen) return false;
        ReleaseLoot();
        return true;
    }

    private void ReleaseLoot()
    {
        if (!_loot.IsOpen) return;
        ulong source = _loot.Source;
        bool sent = _net?.LootRelease(source) == true;
        EmitInterface("loot", "release", sent ? "SENT" : "SEND_FAILED", source,
            $"body={Convert.ToHexString(WorldSession.BuildLootGuidBody(source))}");
        _loot.Clear(); // optimistic; SMSG_LOOT_RELEASE_RESPONSE clears again idempotently
    }

    private bool TakeLootMoney()
    {
        if (_net is null || !_loot.IsOpen || _loot.Gold == 0) return false;
        if (_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) _lootMoneyBefore = player.Fields.Coinage;
        _lootMoneyExpected = _loot.Gold;
        _lootMoneyPending = _net.LootMoney();
        EmitInterface("loot", "money", _lootMoneyPending ? "SENT" : "SEND_FAILED", _loot.Source,
            $"amount={_lootMoneyExpected};moneyBefore={_lootMoneyBefore};body=EMPTY");
        return _lootMoneyPending;
    }

    private bool TakeFirstLootItem()
    {
        if (_net is null || !_loot.IsOpen || _loot.Items.Count == 0) return false;
        byte slot = _loot.Items[0].Slot;
        bool sent = _net.AutostoreLootItem(slot);
        EmitInterface("loot", "item", sent ? "SENT" : "SEND_FAILED", _loot.Source,
            $"slot={slot};body={Convert.ToHexString(WorldSession.BuildAutostoreLootBody(slot))}");
        return sent;
    }

    private bool TakeAllLoot()
    {
        if (_net is null || !_loot.IsOpen) return false;
        bool sent = true;
        if (_loot.Gold > 0) sent &= TakeLootMoney();
        foreach (LootItem item in _loot.Items.ToArray())
        {
            bool itemSent = _net.AutostoreLootItem(item.Slot);
            sent &= itemSent;
            EmitInterface("loot", "item", itemSent ? "SENT" : "SEND_FAILED", _loot.Source,
                $"slot={item.Slot};body={Convert.ToHexString(WorldSession.BuildAutostoreLootBody(item.Slot))};batch=all");
        }
        EmitInterface("loot", "take-all", sent ? "SENT" : "SEND_FAILED", _loot.Source,
            $"money={_loot.Gold};items={_loot.Items.Count}");
        return sent;
    }

    private void SimulateLootFlow(bool empty = false)
    {
        ulong guid = _net?.PlayerGuid ?? 0xF130000006000001ul;
        var w = new PacketWriter(); w.WriteU64(guid); w.WriteU8(1); w.WriteU32(empty ? 0u : 37u);
        w.WriteU8(empty ? (byte)0 : (byte)2);
        if (!empty)
            foreach ((byte slot, uint item, uint count, uint display) in new[] { ((byte)0, 117u, 2u, 789u), ((byte)3, 159u, 1u, 790u) })
            { w.WriteU8(slot); w.WriteU32(item); w.WriteU32(count); w.WriteU32(display); w.WriteU32(0); w.WriteU32(0); w.WriteU8(0); }
        ApplyLootResponse(w.ToArray());
        EmitInterface("loot", empty ? "simulate-empty" : "simulate", empty ? "EMPTY" : "OPEN", guid,
            empty ? "money=0;items=0;autoRelease=false" : "money=37;items=2;wireSlots=0,3");
    }

    /// <summary>Per-frame loot upkeep, called from the HUD pass.</summary>
    private void UpdateLoot()
    {
        // Resolve queued "You receive loot" lines as their templates land (~2 s budget).
        for (int i = _pendingReceives.Count - 1; i >= 0; i--)
        {
            var (entry, count, tries) = _pendingReceives[i];
            if (_items?.TryGet(entry, out ItemTemplate? template) == true && template is not null)
            {
                string suffix = count > 1 ? $" x{count}" : "";
                PushCenterText($"You receive loot: [{template.Name}]{suffix}.",
                    CenterCombatTextStyle.Heal);
                _pendingReceives.RemoveAt(i);
            }
            else if (tries <= 0) _pendingReceives.RemoveAt(i);
            else _pendingReceives[i] = (entry, count, tries - 1);
        }

        if (_lootMoneyPending && _net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player) &&
            player.Fields.Coinage >= _lootMoneyBefore + _lootMoneyExpected)
        {
            EmitInterface("loot", "economy", "VERIFIED", _net.PlayerGuid,
                $"money={_lootMoneyBefore}->{player.Fields.Coinage};expected={_lootMoneyExpected}");
            _lootMoneyPending = false;
        }

        if (!_loot.IsOpen) return;

        // The engine closes the window itself when the last row leaves (vmangos releases only
        // in HandleLootReleaseOpcode; the client is the one that decides the window is done).
        if (_loot.TakeAutoRelease()) { ReleaseLoot(); return; }

        // Corpse despawned under us, or the player walked away: release. The real client
        // stands you up and closes on movement; distance is our movement proxy.
        if (!_entities.TryGet(_loot.Source, out _)) { ReleaseLoot(); return; }
        if (_controller is not null &&
            Vector3.DistanceSquared(_controller.Position, _lootOpenedAt) > 2.25f)
            ReleaseLoot();
    }

    private void ShowUiError(string text) => PushCenterText(text, CenterCombatTextStyle.Damage);

    private void PushCenterText(string text, CenterCombatTextStyle style)
    {
        if (_centerCombatText.Count == 20) _centerCombatText.RemoveAt(0);
        _centerCombatText.Add(new CenterText
        {
            Text = text,
            Style = style,
            Lane = _centerCombatText.Count % 5,
        });
    }

    // ── the authored LootFrame ───────────────────────────────────────────────

    private readonly record struct LootRow(bool IsCoin, byte WireSlot, uint ItemId,
        string Name, string IconPath, uint Count, Vector4 NameColor);

    private void DrawLootFrame()
    {
        UpdateLoot(); // also drains the receive-line queue while no window is open
        if (!_loot.IsOpen || _gameplayArt is null) return;

        float s = GameplayUiScale();
        Vector2 p = new Vector2(16f, 116f) * s; // the UIPanel "left" seat
        Vector2 size = new Vector2(256f, 256f) * s;
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##loot-frame", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if(_uiParityArmed&&_uiParityPanel=="loot"){BeginUiParityFrame(p,s);CollectUiParityDraw("LootFrame","Frame",p,size,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",16,116));}

        // Panel slab, the skull showing through its ring cut-out, and the title.
        DrawArt(dl, @"Interface\LootFrame\UI-LootPanel", p, new Vector2(256f, 256f), s);
        if(_uiParityArmed&&_uiParityPanel=="loot")CollectUiParityDraw("LootFrame/Texture","Texture",p,size,"LootFrame",new(@"Interface\LootFrame\UI-LootPanel",0xffffffff,"IMGUI_IMAGE","TOPLEFT","LootFrame","TOPLEFT",0,0));
        DrawArt(dl, @"Interface\TargetingFrame\TargetDead", p + new Vector2(10f, 8f) * s,
            new Vector2(58f, 58f), s);
        if(_uiParityArmed&&_uiParityPanel=="loot")CollectUiParityDraw("LootFramePortraitOverlay","Texture",p+new Vector2(10,8)*s,new Vector2(58)*s,"LootFrame",new(@"Interface\TargetingFrame\TargetDead",0xffffffff,"IMGUI_IMAGE","TOPLEFT","LootFrame","TOPLEFT",10,-8));
        DrawCenteredText(dl, p + new Vector2(116f, 26f) * s, "Items", 12f * s, VanillaGold);

        List<LootRow> rows = BuildLootRows();

        // Pagination (LootFrame.xml:111-131): 4 rows fit; >4 spends one seat on the pager.
        int perPage = rows.Count > LootRowsPerFrame ? LootRowsPerFrame - 1 : LootRowsPerFrame;
        int maxPage = Math.Max(1, (rows.Count + perPage - 1) / perPage);
        _lootPage = Math.Clamp(_lootPage, 1, maxPage);
        int first = (_lootPage - 1) * perPage;

        for (int visual = 0; visual < perPage && first + visual < rows.Count; visual++)
        {
            LootRow row = rows[first + visual];
            Vector2 rowMin = p + new Vector2(24f, 80f + visual * 41f) * s;
            DrawLootRow(dl, row, rowMin, s, visual);
        }

        if (maxPage > 1)
        {
            DrawLootPagerButton(dl, p + new Vector2(25f, 208f) * s, s, up: true,
                enabled: _lootPage > 1);
            DrawLootPagerButton(dl, p + new Vector2(111f, 208f) * s, s, up: false,
                enabled: _lootPage < maxPage);
        }

        // Close button (UI-Panel-MinimizeButton, centered on TOPRIGHT (-81,-26)).
        Vector2 closeMin = p + new Vector2(175f - 16f, 26f - 16f) * s;
        DrawArt(dl, @"Interface\Buttons\UI-Panel-MinimizeButton-Up", closeMin, new Vector2(32f), s);
        if(_uiParityArmed&&_uiParityPanel=="loot")CollectUiParityDraw("LootCloseButton","Button",closeMin,new Vector2(32)*s,"LootFrame",new(@"Interface\Buttons\UI-Panel-MinimizeButton-Up",0xffffffff,"IMGUI_IMAGE","TOPLEFT","LootFrame","TOPLEFT",159,-10));
        ImGui.SetCursorScreenPos(closeMin + new Vector2(6f) * s);
        if (ImGui.InvisibleButton("##loot-close", new Vector2(20f) * s)) ReleaseLoot();
        else if (ImGui.IsItemHovered())
            DrawArt(dl, @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight", closeMin,
                new Vector2(32f), s);

        if(_uiParityArmed&&_uiParityPanel=="loot")MarkUiParityFrameComplete();ImGui.End();
    }

    private List<LootRow> BuildLootRows()
    {
        var rows = new List<LootRow>(_loot.Items.Count + 1);
        if (_loot.Gold > 0)
        {
            // Coin icon by the highest non-zero denomination; white name (COIN_QUALITY 1).
            string coinIcon = _loot.Gold >= 10000 ? @"Interface\Icons\INV_Misc_Coin_01"
                : _loot.Gold >= 100 ? @"Interface\Icons\INV_Misc_Coin_03"
                : @"Interface\Icons\INV_Misc_Coin_05";
            rows.Add(new LootRow(true, 0, 0, FormatMoney(_loot.Gold), coinIcon, 1, Vector4.One));
        }
        foreach (LootItem item in _loot.Items)
        {
            string name = "...";
            Vector4 color = Vector4.One;
            string icon = _items?.IconForDisplay(item.DisplayInfoId)
                ?? @"Interface\Icons\INV_Misc_QuestionMark.blp";
            if (_items?.TryGet(item.ItemId, out ItemTemplate? template) == true && template is not null)
            {
                name = template.Name;
                color = ItemQualityColor(template.Quality);
                if (template.IconPath.Length > 0) icon = template.IconPath;
            }
            rows.Add(new LootRow(false, item.Slot, item.ItemId, name, icon, item.Count, color));
        }
        return rows;
    }

    private void DrawLootRow(ImDrawListPtr dl, in LootRow row, Vector2 rowMin, float s, int visual)
    {
        Vector2 iconMin = rowMin;
        Vector2 iconMax = rowMin + new Vector2(37f) * s;

        // Parchment name plate behind the text (UI-QuestItemNameFrame, 130x62 at LEFT+30).
        DrawArt(dl, @"Interface\QuestFrame\UI-QuestItemNameFrame",
            rowMin + new Vector2(30f, -12.5f) * s, new Vector2(130f, 62f), s);

        uint icon = _gameplayArt!.Handle(row.IconPath);
        if (icon != 0) dl.AddImage((nint)icon, iconMin, iconMax);
        if (row.Count > 1)
        {
            string label = row.Count.ToString();
            Vector2 extent = ImGui.CalcTextSize(label);
            dl.AddText(iconMax - extent - new Vector2(3f, 2f) * s + Vector2.One * s, 0xff000000, label);
            dl.AddText(iconMax - extent - new Vector2(3f, 2f) * s, 0xffffffff, label);
        }

        float nameSize = 11f * s;
        Vector2 namePos = new(rowMin.X + 45f * s, rowMin.Y + (37f * s - nameSize) * 0.5f);
        dl.AddText(ImGui.GetFont(), nameSize, namePos + Vector2.One * s, 0xff000000, row.Name);
        dl.AddText(ImGui.GetFont(), nameSize, namePos,
            ImGui.ColorConvertFloat4ToU32(row.NameColor), row.Name);

        ImGui.SetCursorScreenPos(rowMin);
        bool clicked = ImGui.InvisibleButton($"##loot-row-{visual}", new Vector2(160f, 37f) * s);
        if (ImGui.IsItemHovered())
        {
            uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square");
            if (highlight != 0) dl.AddImage((nint)highlight, iconMin, iconMax);
            if (!row.IsCoin && row.ItemId != 0 &&
                _items?.TryGet(row.ItemId, out ItemTemplate? template) == true && template is not null)
            {
                ItemTooltipBodySnapshot tooltipBody =
                    PrepareItemTooltipBodySnapshot(template, row.Count);
                OfferPreparedItemTooltip(new("item:loot-row", (ulong)visual), tooltipBody);
            }
        }
        if (!clicked || _net is null) return;
        if (row.IsCoin) TakeLootMoney();
        else
        {
            bool sent = _net.AutostoreLootItem(row.WireSlot);
            EmitInterface("loot", "item", sent ? "SENT" : "SEND_FAILED", _loot.Source,
                $"slot={row.WireSlot};body={Convert.ToHexString(WorldSession.BuildAutostoreLootBody(row.WireSlot))}");
        }
    }

    private void DrawLootPagerButton(ImDrawListPtr dl, Vector2 min, float s, bool up, bool enabled)
    {
        string direction = up ? "ScrollUp" : "ScrollDown";
        string state = enabled ? "Up" : "Disabled";
        DrawArt(dl, $@"Interface\ChatFrame\UI-ChatIcon-{direction}-{state}", min, new Vector2(32f), s);
        if (!enabled) return;
        ImGui.SetCursorScreenPos(min);
        if (ImGui.InvisibleButton(up ? "##loot-page-up" : "##loot-page-down", new Vector2(32f) * s))
            _lootPage += up ? -1 : 1;
    }

    private static Vector4 ItemQualityColor(uint quality) => quality switch
    {
        0 => new Vector4(0.62f, 0.62f, 0.62f, 1f),
        2 => new Vector4(0.12f, 1f, 0f, 1f),
        3 => new Vector4(0f, 0.44f, 0.87f, 1f),
        4 => new Vector4(0.64f, 0.21f, 0.93f, 1f),
        5 => new Vector4(1f, 0.50f, 0f, 1f),
        6 => new Vector4(0.90f, 0.80f, 0.50f, 1f),
        _ => Vector4.One,
    };

    /// <summary>"4 Gold 25 Silver 3 Copper", dropping zero denominations; 0 = "0 Copper".</summary>
    private static string FormatMoney(uint copper)
    {
        uint gold = copper / 10000, silver = copper / 100 % 100, coin = copper % 100;
        var parts = new List<string>(3);
        if (gold > 0) parts.Add($"{gold} Gold");
        if (silver > 0) parts.Add($"{silver} Silver");
        if (coin > 0 || parts.Count == 0) parts.Add($"{coin} Copper");
        return string.Join(" ", parts);
    }
}
