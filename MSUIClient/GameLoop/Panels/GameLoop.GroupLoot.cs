using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>GroupLootFrame: four concurrent Need/Greed/Pass dialogs and their exact wire family.</summary>
public sealed partial class GameLoop
{
    private enum GroupLootLineKind { Announce, Won, AllPassed }

    private readonly record struct PendingGroupLootLine(
        GroupLootLineKind Kind, uint ItemId, ulong Player, byte RollNumber,
        byte RollType, GroupLootVote? Vote, int TriesLeft);

    private readonly GroupLootRollState _groupLootRolls = new();
    private readonly List<PendingGroupLootLine> _pendingGroupLootLines = [];
    private (LootRollKey Key, GroupLootVote Vote)? _groupLootConfirm;

    private void ApplyLootStartRoll(byte[] body)
    {
        LootStartRoll packet = LootPackets.ParseStartRoll(body);
        GroupLootRollState.ActiveRoll? opened = _groupLootRolls.Start(packet, NowSeconds());
        if (_items is not null && _net is not null)
            _items.Require(packet.ItemId, packet.LootedTarget, _net);
        EmitInterface("group-loot", "start", opened is null ? "IGNORED" : "OPEN",
            packet.LootedTarget,
            $"slot={packet.ItemSlot};item={packet.ItemId};property={packet.RandomPropertyId};" +
            $"countdown={packet.CountdownMs};rollId={opened?.Id ?? 0}");
    }

    private void ApplyLootRoll(byte[] body)
    {
        LootRollAnnouncement packet = LootPackets.ParseRoll(body);
        if (_items is not null && _net is not null)
            _items.Require(packet.ItemId, packet.LootedTarget, _net);
        _pendingGroupLootLines.Add(new(GroupLootLineKind.Announce, packet.ItemId,
            packet.Roller, packet.RollNumber, packet.RollType, packet.Vote, 120));
        EmitInterface("group-loot", packet.IsDice ? "dice" : "vote", "ANNOUNCED",
            packet.LootedTarget,
            $"slot={packet.ItemSlot};item={packet.ItemId};roller={packet.Roller:X16};" +
            $"number={packet.RollNumber};type={packet.RollType};vote={packet.Vote}");
    }

    private void ApplyLootRollWon(byte[] body)
    {
        LootRollWon packet = LootPackets.ParseRollWon(body);
        var key = new LootRollKey(packet.LootedTarget, packet.ItemSlot);
        _groupLootRolls.Close(key);
        if (_groupLootConfirm?.Key == key) _groupLootConfirm = null;
        if (_items is not null && _net is not null)
            _items.Require(packet.ItemId, packet.LootedTarget, _net);
        _pendingGroupLootLines.Add(new(GroupLootLineKind.Won, packet.ItemId,
            packet.Winner, packet.RollNumber, packet.RollType, null, 120));
        EmitInterface("group-loot", "resolution", "WON", packet.LootedTarget,
            $"slot={packet.ItemSlot};item={packet.ItemId};winner={packet.Winner:X16};" +
            $"number={packet.RollNumber};type={packet.RollType}");
    }

    private void ApplyLootAllPassed(byte[] body)
    {
        LootAllPassed packet = LootPackets.ParseAllPassed(body);
        var key = new LootRollKey(packet.LootedTarget, packet.ItemSlot);
        _groupLootRolls.Close(key);
        if (_groupLootConfirm?.Key == key) _groupLootConfirm = null;
        if (_items is not null && _net is not null)
            _items.Require(packet.ItemId, packet.LootedTarget, _net);
        _pendingGroupLootLines.Add(new(GroupLootLineKind.AllPassed, packet.ItemId,
            0, 0, 0, null, 120));
        EmitInterface("group-loot", "resolution", "ALL_PASSED", packet.LootedTarget,
            $"slot={packet.ItemSlot};item={packet.ItemId};property={packet.RandomPropertyId}");
    }

    private void VoteOnGroupLoot(GroupLootRollState.ActiveRoll roll, GroupLootVote vote,
        bool confirmed = false)
    {
        if (_groupLootRolls.Find(roll.Key) is null) return;
        bool bindOnPickup = _items?.TryGet(roll.ItemId, out ItemTemplate? template) == true &&
                            template?.Bonding == 1;
        if (!confirmed && bindOnPickup && vote is GroupLootVote.Need or GroupLootVote.Greed)
        {
            _groupLootConfirm = (roll.Key, vote);
            EmitInterface("group-loot", "confirm", "OPEN", roll.Key.LootedTarget,
                $"slot={roll.Key.ItemSlot};item={roll.ItemId};vote={vote};wire=none");
            return;
        }

        bool sent = _net?.LootRoll(roll.Key.LootedTarget, roll.Key.ItemSlot, vote) == true;
        if (sent)
        {
            _groupLootRolls.Close(roll.Key); // the 5875 client predicts this close
            if (_groupLootConfirm?.Key == roll.Key) _groupLootConfirm = null;
        }
        EmitInterface("group-loot", "vote", sent ? "SENT" : "SEND_FAILED",
            roll.Key.LootedTarget,
            $"slot={roll.Key.ItemSlot};item={roll.ItemId};vote={(byte)vote};" +
            $"body={Convert.ToHexString(LootPackets.BuildRollBody(
                roll.Key.LootedTarget, roll.Key.ItemSlot, vote))}");
    }

    private void UpdateGroupLootChat()
    {
        for (int i = 0; i < _pendingGroupLootLines.Count;)
        {
            PendingGroupLootLine pending = _pendingGroupLootLines[i];
            if (_items?.TryGet(pending.ItemId, out ItemTemplate? item) != true || item is null)
            {
                if (pending.TriesLeft <= 1) _pendingGroupLootLines.RemoveAt(i);
                else { _pendingGroupLootLines[i] = pending with { TriesLeft = pending.TriesLeft - 1 }; i++; }
                continue;
            }

            bool isSelf = pending.Player != 0 && pending.Player == LocalPlayerGuid;
            bool needsName = pending.Kind switch
            {
                GroupLootLineKind.Announce => pending.RollNumber is >= 1 and <= 100 || !isSelf,
                GroupLootLineKind.Won => !isSelf,
                _ => false,
            };
            string? name = needsName ? TryResolveGroupLootName(pending.Player) : null;
            if (needsName && name is null)
            {
                if (pending.TriesLeft <= 1) _pendingGroupLootLines.RemoveAt(i);
                else { _pendingGroupLootLines[i] = pending with { TriesLeft = pending.TriesLeft - 1 }; i++; }
                continue;
            }

            string link = $"[{item.Name}]";
            string text = FormatGroupLootLine(pending, name, isSelf, link);
            AddChatMessage(text, ChatFrameLaw.MsgType.Loot);
            _pendingGroupLootLines.RemoveAt(i);
        }
    }

    private string? TryResolveGroupLootName(ulong guid)
    {
        if (guid == 0) return null;
        if (guid == LocalPlayerGuid && _net?.PlayerName is { Length: > 0 } own) return own;
        if (_playerNames.TryGetValue(guid, out string? name) && name.Length > 0) return name;
        if (_chatNameQueried.Add(guid)) _net?.NameQuery(guid);
        return null;
    }

    private static string FormatGroupLootLine(in PendingGroupLootLine line, string? name,
        bool isSelf, string link)
    {
        if (line.Kind == GroupLootLineKind.AllPassed) return $"Everyone passed on: {link}";
        if (line.Kind == GroupLootLineKind.Won)
            return isSelf ? $"You won: {link}" : $"{name} won: {link}";
        if (line.RollNumber is >= 1 and <= 100)
            return $"{(line.RollType == (byte)GroupLootVote.Need ? "Need" : "Greed")} Roll - " +
                   $"{line.RollNumber} for {link} by {name}";
        if (isSelf)
            return line.Vote switch
            {
                GroupLootVote.Need => $"You have selected Need for: {link}",
                GroupLootVote.Greed => $"You have selected Greed for: {link}",
                _ => $"You passed on: {link}",
            };
        return line.Vote switch
        {
            GroupLootVote.Need => $"{name} has selected Need for: {link}",
            GroupLootVote.Greed => $"{name} has selected Greed for: {link}",
            _ => $"{name} passed on: {link}",
        };
    }

    private void DrawGroupLootFrames()
    {
        UpdateGroupLootChat();
        if (_gameplayArt is null || _skin is null) return;
        var managed = new UiParentManagedState(
            BottomLeftShown: true, BottomRightShown: true,
            RightLeftShown: Enumerable.Range(36, 12).Any(slot => _actions[slot] is not null),
            RightRightShown: Enumerable.Range(24, 12).Any(slot => _actions[slot] is not null),
            PetOrStanceShown: PetOrStanceActionBarVisible, ReputationShown: false, MaxLevelShown: false);
        float scale = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        _skin.Scale = scale;
        var clicked = new List<(GroupLootRollState.ActiveRoll Roll, GroupLootVote Vote)>();

        for (int frameIndex = 0; frameIndex < GroupLootRollState.FrameCount; frameIndex++)
        {
            GroupLootRollState.ActiveRoll? active = _groupLootRolls.Frames[frameIndex];
            if (active is null) continue;
            DrawOneGroupLootFrame(active, frameIndex, display, scale, managed, clicked);
        }
        foreach ((GroupLootRollState.ActiveRoll roll, GroupLootVote vote) in clicked)
            VoteOnGroupLoot(roll, vote);
    }

    private void DrawOneGroupLootFrame(GroupLootRollState.ActiveRoll roll, int frameIndex,
        Vector2 display, float scale, in UiParentManagedState managed,
        List<(GroupLootRollState.ActiveRoll Roll, GroupLootVote Vote)> clicked)
    {
        GroupLootFrameUiLaw.ScreenRect frame = GroupLootFrameUiLaw.FrameRect(
            display, scale, frameIndex, managed);
        ImGui.SetNextWindowPos(frame.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin($"##group-loot-frame-{frameIndex + 1}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();
        ItemTemplate? item = null;
        _items?.TryGet(roll.ItemId, out item);
        bool bop = item?.Bonding == 1;
        _skin!.DrawBackdrop(draw, frame.Min, frame.Min + frame.Size,
            bop ? WowSkin.DialogGold : WowSkin.Dialog);
        DrawArt(draw, GroupLootFrameUiLaw.EmptySlotPath,
            frame.Min + GroupLootFrameUiLaw.ItemPlateMin * scale,
            new Vector2(GroupLootFrameUiLaw.ItemPlateSize), scale);
        DrawArt(draw, GroupLootFrameUiLaw.NamePlatePath,
            frame.Min + GroupLootFrameUiLaw.NamePlateMin * scale, new Vector2(128, 64), scale);
        if (bop)
            DrawArt(draw, GroupLootFrameUiLaw.DragonPath,
                frame.Min + GroupLootFrameUiLaw.DragonMin * scale, new Vector2(120), scale);
        DrawArt(draw, bop ? GroupLootFrameUiLaw.GoldCornerPath : GroupLootFrameUiLaw.PlainCornerPath,
            frame.Min + GroupLootFrameUiLaw.CornerMin * scale, new Vector2(32), scale);

        string iconPath = item?.IconPath ?? @"Interface\Icons\INV_Misc_QuestionMark";
        Vector2 iconMin = frame.Min + GroupLootFrameUiLaw.IconMin * scale;
        DrawArt(draw, iconPath, iconMin, new Vector2(GroupLootFrameUiLaw.IconSize), scale);
        ImGui.SetCursorScreenPos(iconMin);
        ImGui.InvisibleButton($"##group-loot-icon-{roll.Id}",
            new Vector2(GroupLootFrameUiLaw.IconSize) * scale);
        if (ImGui.GetIO().KeyCtrl && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            TryOnDressUp(roll.ItemId);
        if (ImGui.IsItemHovered() && item is not null)
            OfferPreparedItemTooltip(new("item:group-loot", roll.Id),
                PrepareItemTooltipBodySnapshot(item, 1));

        if (item is not null)
        {
            Vector2 textMin = frame.Min + GroupLootFrameUiLaw.NameMin * scale;
            Vector2 textMax = textMin + GroupLootFrameUiLaw.NameSize * scale;
            draw.PushClipRect(textMin, textMax, true);
            GameText.Draw(draw, "GameFontNormalSmall", item.Name, textMin, scale,
                ImGui.ColorConvertFloat4ToU32(GroupLootFrameUiLaw.QualityColor(item.Quality)));
            draw.PopClipRect();
        }

        DrawGroupLootTimer(draw, frame.Min, roll, scale);
        if (DrawGroupLootVoteButton(draw, roll.Id, GroupLootVote.Pass,
                frame.Min + GroupLootFrameUiLaw.PassMin * scale, scale, "Pass"))
            clicked.Add((roll, GroupLootVote.Pass));
        if (DrawGroupLootVoteButton(draw, roll.Id, GroupLootVote.Need,
                frame.Min + GroupLootFrameUiLaw.NeedMin * scale, scale, "Need"))
            clicked.Add((roll, GroupLootVote.Need));
        if (DrawGroupLootVoteButton(draw, roll.Id, GroupLootVote.Greed,
                frame.Min + GroupLootFrameUiLaw.GreedMin * scale, scale, "Greed"))
            clicked.Add((roll, GroupLootVote.Greed));
        draw.PopClipRect();
        ImGui.End();
    }

    private void DrawGroupLootTimer(ImDrawListPtr draw, Vector2 origin,
        GroupLootRollState.ActiveRoll roll, float scale)
    {
        double remaining = _groupLootRolls.RemainingMilliseconds(roll, NowSeconds());
        float ratio = roll.CountdownMs == 0 ? 0 :
            Math.Clamp((float)(remaining / roll.CountdownMs), 0, 1);
        Vector2 min = origin + GroupLootFrameUiLaw.TimerMin * scale;
        Vector2 size = new(GroupLootFrameUiLaw.TimerWidth * ratio,
            GroupLootFrameUiLaw.TimerHeight * scale);
        size.X *= scale;
        uint fill = _gameplayArt!.Handle(GroupLootFrameUiLaw.TimerFillPath);
        if (fill != 0 && size.X > 0)
            draw.AddImage((nint)fill, min, min + size, Vector2.Zero,
                new Vector2(ratio, 1), 0xff00ffff);
        DrawArt(draw, GroupLootFrameUiLaw.TimerBorderPath,
            origin + GroupLootFrameUiLaw.TimerBorderMin * scale,
            new Vector2(GroupLootFrameUiLaw.TimerBorderWidth,
                GroupLootFrameUiLaw.TimerBorderHeight), scale);
    }

    private bool DrawGroupLootVoteButton(ImDrawListPtr draw, ulong rollId, GroupLootVote vote,
        Vector2 min, float scale, string tooltip)
    {
        Vector2 size = new Vector2(GroupLootFrameUiLaw.VoteButtonSize) * scale;
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##group-loot-{rollId}-{vote}", size);
        bool active = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        (string up, string down, string highlight) = vote switch
        {
            GroupLootVote.Need => (GroupLootFrameUiLaw.NeedUpPath,
                GroupLootFrameUiLaw.NeedDownPath, GroupLootFrameUiLaw.NeedHighlightPath),
            GroupLootVote.Greed => (GroupLootFrameUiLaw.GreedUpPath,
                GroupLootFrameUiLaw.GreedDownPath, GroupLootFrameUiLaw.GreedHighlightPath),
            _ => (GroupLootFrameUiLaw.PassUpPath,
                GroupLootFrameUiLaw.PassDownPath, GroupLootFrameUiLaw.PassHighlightPath),
        };
        DrawArt(draw, active ? down : up, min, new Vector2(GroupLootFrameUiLaw.VoteButtonSize), scale);
        if (hovered)
        {
            DrawArt(draw, highlight, min, new Vector2(GroupLootFrameUiLaw.VoteButtonSize), scale);
            OfferPreservedSharedGameTooltipRenderer(
                new($"group-loot-{vote.ToString().ToLowerInvariant()}", rollId), () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(tooltip);
                    ImGui.EndTooltip();
                });
        }
        return clicked;
    }

    private bool TryDismissGroupLootConfirmationOnEscape()
    {
        if (_groupLootConfirm is null) return false;
        var pending = _groupLootConfirm.Value;
        _groupLootConfirm = null;
        EmitInterface("group-loot", "confirm", "CANCELLED_ESCAPE", pending.Key.LootedTarget,
            $"slot={pending.Key.ItemSlot};vote={pending.Vote};wire=none");
        return true;
    }

    private void DrawGroupLootConfirmation()
    {
        if (_groupLootConfirm is not { } pending || _skin is null) return;
        GroupLootRollState.ActiveRoll? roll = _groupLootRolls.Find(pending.Key);
        if (roll is null) { _groupLootConfirm = null; return; }
        float scale = GameplayUiScale();
        string[] lines = WrapTooltipText(GroupLootFrameUiLaw.ConfirmText,
            "GameFontHighlight", scale, GroupLootFrameUiLaw.ConfirmTextWidth * scale).ToArray();
        float pitch = GameText.LinePitch("GameFontHighlight", 1f);
        float textHeight = lines.Length * pitch;
        GroupLootFrameUiLaw.ScreenRect frame = GroupLootFrameUiLaw.ConfirmRect(
            ImGui.GetIO().DisplaySize, scale, textHeight);
        ImGui.SetNextWindowPos(frame.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin("##group-loot-confirm", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        _skin.DrawBackdrop(draw, frame.Min, frame.Min + frame.Size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                frame.Min + GroupLootFrameUiLaw.ConfirmTextCenter((i + .5f) * pitch) * scale,
                scale);
        bool accept = DrawGroupLootConfirmButton(draw, 1, GroupLootFrameUiLaw.AcceptText,
            frame.Min + GroupLootFrameUiLaw.ConfirmButtonMin(1, textHeight) * scale, scale);
        bool cancel = DrawGroupLootConfirmButton(draw, 2, GroupLootFrameUiLaw.CancelText,
            frame.Min + GroupLootFrameUiLaw.ConfirmButtonMin(2, textHeight) * scale, scale);
        ImGui.End();
        if (accept) VoteOnGroupLoot(roll, pending.Vote, confirmed: true);
        else if (cancel)
        {
            _groupLootConfirm = null;
            EmitInterface("group-loot", "confirm", "CANCELLED", pending.Key.LootedTarget,
                $"slot={pending.Key.ItemSlot};vote={pending.Vote};wire=none");
        }
    }

    private bool DrawGroupLootConfirmButton(ImDrawListPtr draw, int index, string caption,
        Vector2 min, float scale)
    {
        Vector2 size = new Vector2(GroupLootFrameUiLaw.ConfirmButtonWidth,
            GroupLootFrameUiLaw.ConfirmButtonHeight) * scale;
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##group-loot-confirm-{index}", size);
        bool active = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        uint art = _skin!.TextureHandle(active ? "dialog.button.down" : "dialog.button.up");
        if (art != 0) draw.AddImage((nint)art, min, min + size, Vector2.Zero, new Vector2(1, .625f));
        if (hovered)
        {
            uint highlight = _gameplayArt?.BrightHighlightHandle(
                @"Interface\Buttons\UI-DialogBox-Button-Highlight") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, min, min + size, Vector2.Zero, new Vector2(1, .625f));
        }
        GameText.DrawCentered(draw, hovered ? "GameFontHighlight" : "GameFontNormal",
            caption, min + size * .5f, scale);
        return clicked;
    }
}
