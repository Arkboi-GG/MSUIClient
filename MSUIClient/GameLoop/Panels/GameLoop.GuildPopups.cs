using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly byte[] _guildAddMemberInput =
        new byte[GuildFrameUiLaw.AddMemberMaxLetters + 1];
    private bool _guildAddMemberFocusRequested;
    private bool _guildAddMemberEditFocused;
    private string _guildAddRankInput = "";
    private bool _guildAddRankFocusRequested;
    private bool _guildAddRankEditFocused;
    private readonly string[] _guildNoteInputs = ["", ""];
    private readonly bool[] _guildNoteFocusRequested = new bool[2];
    private readonly bool[] _guildNoteEditFocused = new bool[2];
    private readonly string[] _guildPopupMemberNames = ["", ""];

    private void ShowGuildAddMemberPopup() => ExecuteStaticPopupPlan(
        StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            GuildFrameUiLaw.AddMemberDefinition, playerDeadOrGhost: false));

    private void ShowGuildAddRankPopup() => ExecuteStaticPopupPlan(
        StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            GuildFrameUiLaw.AddRankDefinition, playerDeadOrGhost: false));

    private void ShowGuildInvitePopup(in GuildInviteWire invite) => ExecuteStaticPopupPlan(
        StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            GuildFrameUiLaw.InviteDefinition, playerDeadOrGhost: false,
            dataToken: GuildFrameUiLaw.InvitePopupToken(invite.Inviter, invite.Guild)));

    private void ApplyGuildInvitePopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        switch (effect.Kind)
        {
            case StaticPopupCoordinatorLaw.EffectKind.Accept:
                _net?.GuildAccept();
                break;
            case StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason:
            case StaticPopupCoordinatorLaw.EffectKind.CancelOverride:
            case StaticPopupCoordinatorLaw.EffectKind.CancelClicked:
            case StaticPopupCoordinatorLaw.EffectKind.CancelTimeout:
                _net?.GuildDecline();
                break;
        }
    }

    private string GuildAddMemberInput()
    {
        int end = Array.IndexOf(_guildAddMemberInput, (byte)0);
        return Encoding.UTF8.GetString(_guildAddMemberInput, 0,
            end < 0 ? _guildAddMemberInput.Length : end);
    }

    private void ApplyGuildAddMemberPopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        switch (effect.Kind)
        {
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBox:
                Array.Clear(_guildAddMemberInput);
                break;
            case StaticPopupCoordinatorLaw.EffectKind.OnShow:
                _guildAddMemberFocusRequested = true;
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Hide:
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBoxFocus:
                _guildAddMemberFocusRequested = false;
                _guildAddMemberEditFocused = false;
                if (_chatEditOpen) _chatEditJustOpened = true;
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Accept:
            case StaticPopupCoordinatorLaw.EffectKind.EditBoxEnter:
                _net?.GuildInvite(GuildAddMemberInput());
                break;
        }
    }

    private void ApplyGuildMemberPopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        int seat = Math.Clamp(effect.Slot - 1, 0, 1);
        switch (effect.Kind)
        {
            case StaticPopupCoordinatorLaw.EffectKind.PrepareContent:
                StaticPopupCoordinatorLaw.Instance? instance = effect.Slot == 1
                    ? _staticPopupSlots.First : _staticPopupSlots.Second;
                _guildPopupMemberNames[seat] = instance?.DataToken ??
                    SelectedGuildMember()?.Name ?? "";
                break;
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBox:
                _guildNoteInputs[seat] = "";
                break;
            case StaticPopupCoordinatorLaw.EffectKind.OnShow:
                if (effect.Type is GuildFrameUiLaw.SetPublicNotePopupType or
                    GuildFrameUiLaw.SetOfficerNotePopupType)
                {
                    GuildMember? member = _guildMembers.FirstOrDefault(row =>
                        string.Equals(row.Name, _guildPopupMemberNames[seat],
                            StringComparison.OrdinalIgnoreCase));
                    string note = effect.Type == GuildFrameUiLaw.SetPublicNotePopupType
                        ? member?.PublicNote ?? "" : member?.OfficerNote ?? "";
                    _guildNoteInputs[seat] = GuildFrameUiLaw.TruncateNote(note);
                    _guildNoteFocusRequested[seat] = true;
                }
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Hide:
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBoxFocus:
                _guildNoteFocusRequested[seat] = false;
                _guildNoteEditFocused[seat] = false;
                if (_chatEditOpen) _chatEditJustOpened = true;
                if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Hide)
                    _guildPopupMemberNames[seat] = "";
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Accept:
            case StaticPopupCoordinatorLaw.EffectKind.EditBoxEnter:
                string name = _guildPopupMemberNames[seat];
                if (effect.Type == GuildFrameUiLaw.RemoveMemberPopupType)
                {
                    _net?.GuildRemove(name);
                    _guildMemberDetailOpen = false;
                    _guildSelected = -1;
                }
                else if (effect.Type == GuildFrameUiLaw.SetPublicNotePopupType)
                    _net?.GuildSetPublicNote(name, _guildNoteInputs[seat]);
                else if (effect.Type == GuildFrameUiLaw.SetOfficerNotePopupType)
                    _net?.GuildSetOfficerNote(name, _guildNoteInputs[seat]);
                break;
        }
    }

    private void ApplyGuildAddRankPopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        switch (effect.Kind)
        {
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBox:
                _guildAddRankInput = "";
                break;
            case StaticPopupCoordinatorLaw.EffectKind.OnShow:
                _guildAddRankFocusRequested = true;
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Hide:
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBoxFocus:
                _guildAddRankFocusRequested = false;
                _guildAddRankEditFocused = false;
                if (_chatEditOpen) _chatEditJustOpened = true;
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Accept:
            case StaticPopupCoordinatorLaw.EffectKind.EditBoxEnter:
                _net?.GuildAddRank(_guildAddRankInput);
                break;
        }
    }

    private void DrawGuildMemberPopups()
    {
        DrawGuildRemoveMemberPopup();
        DrawGuildNotePopup(GuildFrameUiLaw.SetPublicNotePopupType, "Set Player Note:");
        DrawGuildNotePopup(GuildFrameUiLaw.SetOfficerNotePopupType, "Set Officer Note:");
        DrawGuildAddRankPopup();
    }

    private void DrawGuildInvitePopup()
    {
        var popup = GuildFrameUiLaw.Popup(_staticPopupSlots,
            GuildFrameUiLaw.InvitePopupType);
        if (popup is not { } visible || _skin is null) return;
        float scale = GameplayUiScale();
        (string inviter, string guild) = GuildFrameUiLaw.InvitePopupData(
            visible.Instance.DataToken);
        string text = GuildFrameUiLaw.InvitePopupText(inviter, guild);
        string[] lines = WrapTooltipText(text, "GameFontHighlight", scale,
            GuildFrameUiLaw.InvitePopupTextWidth * scale).ToArray();
        float logicalTextHeight = lines.Length *
            GameText.LinePitch("GameFontHighlight", 1);
        Vector2 logicalSize = new(GuildFrameUiLaw.InvitePopupWidth,
            GuildFrameUiLaw.InvitePopupHeight(logicalTextHeight));
        Vector2 origin = StaticPopupOrigin(visible.Slot,
            GuildFrameUiLaw.InvitePopupWidth, scale);
        if (!BeginGuildPopupWindow($"##guild-invite-popup-{visible.Slot}", origin,
                logicalSize, scale, out ImDrawListPtr draw)) return;
        Vector2 size = logicalSize * scale;
        draw.PushClipRectFullScreen();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                origin + new Vector2(GuildFrameUiLaw.InvitePopupWidth * .5f,
                    GuildFrameUiLaw.InvitePopupTextTop +
                    (i + .5f) * GameText.LinePitch("GameFontHighlight", 1)) * scale, scale);
        float buttonY = GuildFrameUiLaw.InvitePopupButtonTop(logicalTextHeight);
        bool accept = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button1", "Accept",
            origin + new Vector2(GuildFrameUiLaw.InvitePopupButtonOneX, buttonY) * scale,
            scale, false, default);
        bool decline = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button2", "Decline",
            origin + new Vector2(GuildFrameUiLaw.InvitePopupButtonTwoX, buttonY) * scale,
            scale, false, default);
        draw.PopClipRect();
        ImGui.End();
        if (accept) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 1));
        else if (decline) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 2));
    }

    private void DrawGuildAddRankPopup()
    {
        var popup = GuildFrameUiLaw.Popup(_staticPopupSlots,
            GuildFrameUiLaw.AddRankPopupType);
        if (popup is not { } visible || _skin is null) return;
        float scale = GameplayUiScale();
        float textHeight = GameText.LinePitch("GameFontHighlight", 1);
        StaticPopupCoordinatorLaw.NarrowEditBoxLayout layout =
            StaticPopupCoordinatorLaw.NarrowEditLayout(textHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot, layout.Width, scale);
        Vector2 size = new(layout.Width, layout.Height);
        if (!BeginGuildPopupWindow($"##guild-add-rank-popup-{visible.Slot}", origin,
                size, scale, out ImDrawListPtr draw)) return;
        _skin.DrawBackdrop(draw, origin, origin + size * scale, WowSkin.Dialog);
        GameText.DrawCentered(draw, "GameFontHighlight", GuildFrameUiLaw.AddRankLabel,
            origin + new Vector2(layout.Width * .5f,
                layout.Text.Y + layout.Text.Height * .5f) * scale, scale);
        Vector2 editMin = origin + new Vector2(layout.EditBox.X, layout.EditBox.Y) * scale;
        DrawStaticPopupEditBoxBorder(draw, editMin, scale);
        ImGui.SetCursorScreenPos(editMin + new Vector2(0, 7) * scale);
        ImGui.SetNextItemWidth(layout.EditBox.Width * scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        if (_guildAddRankFocusRequested)
        {
            ImGui.SetKeyboardFocusHere();
            _guildAddRankFocusRequested = false;
        }
        bool entered = ImGui.InputText("##guild-add-rank-edit", ref _guildAddRankInput,
            128, ImGuiInputTextFlags.EnterReturnsTrue);
        if (_guildAddRankInput.Length > GuildFrameUiLaw.RankNameMaxLetters)
            _guildAddRankInput = _guildAddRankInput[..GuildFrameUiLaw.RankNameMaxLetters];
        _guildAddRankEditFocused = ImGui.IsItemActive();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        bool accept = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button1", "Accept",
            origin + new Vector2(layout.Button1.X, layout.Button1.Y) * scale,
            scale, false, default);
        bool cancel = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button2", "Cancel",
            origin + new Vector2(layout.Button2.X, layout.Button2.Y) * scale,
            scale, false, default);
        ImGui.End();
        if (entered)
        {
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.EditBoxEnter(
                _staticPopupSlots, visible.Slot));
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, GuildFrameUiLaw.AddRankPopupType));
        }
        else if (accept) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 1));
        else if (cancel) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 2));
    }

    private void DrawGuildRemoveMemberPopup()
    {
        var popup = GuildFrameUiLaw.Popup(_staticPopupSlots,
            GuildFrameUiLaw.RemoveMemberPopupType);
        if (popup is not { } visible || _skin is null) return;
        float scale = GameplayUiScale();
        string text = GuildFrameUiLaw.RemoveMemberText(
            visible.Instance.DataToken ?? "");
        string[] lines = WrapTooltipText(text, "GameFontHighlight", scale,
            StaticPopupCoordinatorLaw.TextWidth * scale).ToArray();
        float textHeight = lines.Length * GameText.LinePitch("GameFontHighlight", 1);
        float height = StaticPopupCoordinatorLaw.Height(textHeight,
            StaticPopupCoordinatorLaw.ButtonHeight);
        float width = StaticPopupCoordinatorLaw.BaseWidth;
        Vector2 origin = StaticPopupOrigin(visible.Slot, width, scale);
        Vector2 size = new(width, height);
        if (!BeginGuildPopupWindow($"##guild-remove-popup-{visible.Slot}", origin,
                size, scale, out ImDrawListPtr draw)) return;
        _skin.DrawBackdrop(draw, origin, origin + size * scale, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                origin + new Vector2(width * .5f,
                    StaticPopupCoordinatorLaw.TextTop +
                    (i + .5f) * GameText.LinePitch("GameFontHighlight", 1)) * scale, scale);
        float buttonY = StaticPopupCoordinatorLaw.TextTop + textHeight + 8;
        float firstX = width * .5f - 134;
        bool yes = DrawPartyInviteButton(draw, $"StaticPopup{visible.Slot}Button1", "Yes",
            origin + new Vector2(firstX, buttonY) * scale, scale, false, default);
        bool no = DrawPartyInviteButton(draw, $"StaticPopup{visible.Slot}Button2", "No",
            origin + new Vector2(firstX + 141, buttonY) * scale, scale, false, default);
        ImGui.End();
        if (yes) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 1));
        else if (no) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 2));
    }

    private void DrawGuildNotePopup(string type, string label)
    {
        var popup = GuildFrameUiLaw.Popup(_staticPopupSlots, type);
        if (popup is not { } visible || _skin is null) return;
        int seat = visible.Slot - 1;
        float scale = GameplayUiScale();
        float textHeight = GameText.LinePitch("GameFontHighlight", 1);
        StaticPopupCoordinatorLaw.WideEditBoxLayout layout =
            StaticPopupCoordinatorLaw.WideEditLayout(textHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot, layout.Width, scale);
        Vector2 size = new(layout.Width, layout.Height);
        if (!BeginGuildPopupWindow($"##guild-note-popup-{type}-{visible.Slot}", origin,
                size, scale, out ImDrawListPtr draw)) return;
        _skin.DrawBackdrop(draw, origin, origin + size * scale, WowSkin.Dialog);
        GameText.DrawCentered(draw, "GameFontHighlight", label,
            origin + new Vector2(layout.Width * .5f,
                layout.Text.Y + layout.Text.Height * .5f) * scale, scale);
        Vector2 editMin = origin + new Vector2(layout.EditBox.X, layout.EditBox.Y) * scale;
        DrawStaticPopupWideEditBoxBorder(draw, editMin, scale);
        ImGui.SetCursorScreenPos(editMin + new Vector2(0, 23) * scale);
        ImGui.SetNextItemWidth(layout.EditBox.Width * scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        if (_guildNoteFocusRequested[seat])
        {
            ImGui.SetKeyboardFocusHere();
            _guildNoteFocusRequested[seat] = false;
        }
        bool entered = ImGui.InputText($"##guild-note-edit-{seat}",
            ref _guildNoteInputs[seat], 256, ImGuiInputTextFlags.EnterReturnsTrue);
        _guildNoteInputs[seat] = GuildFrameUiLaw.TruncateNote(_guildNoteInputs[seat]);
        _guildNoteEditFocused[seat] = ImGui.IsItemActive();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        bool accept = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button1", "Accept",
            origin + new Vector2(layout.Button1.X, layout.Button1.Y) * scale,
            scale, false, default);
        bool cancel = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button2", "Cancel",
            origin + new Vector2(layout.Button2.X, layout.Button2.Y) * scale,
            scale, false, default);
        ImGui.End();
        if (entered)
        {
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.EditBoxEnter(
                _staticPopupSlots, visible.Slot));
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, type));
        }
        else if (accept) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 1));
        else if (cancel) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 2));
    }

    private static bool BeginGuildPopupWindow(string id, Vector2 origin, Vector2 logicalSize,
        float scale, out ImDrawListPtr draw)
    {
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(logicalSize * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool begun = ImGui.Begin(id, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav);
        ImGui.PopStyleVar(2);
        draw = ImGui.GetWindowDrawList();
        if (!begun) ImGui.End();
        return begun;
    }

    private void DrawStaticPopupWideEditBoxBorder(ImDrawListPtr draw, Vector2 editMin,
        float scale)
    {
        uint left = _gameplayArt?.Handle(@"Interface\ChatFrame\UI-ChatInputBorder-Left") ?? 0;
        uint right = _gameplayArt?.Handle(@"Interface\ChatFrame\UI-ChatInputBorder-Right") ?? 0;
        float width = StaticPopupCoordinatorLaw.WideEditBoxWidth;
        if (left != 0)
        {
            draw.AddImage((nint)left, editMin + new Vector2(-10, 16) * scale,
                editMin + new Vector2(246, 48) * scale);
            draw.AddImage((nint)left, editMin + new Vector2(246, 16) * scale,
                editMin + new Vector2(width - 65, 48) * scale,
                new Vector2(.29296875f, 0), Vector2.One);
        }
        if (right != 0)
            draw.AddImage((nint)right, editMin + new Vector2(width - 65, 16) * scale,
                editMin + new Vector2(width + 10, 48) * scale,
                new Vector2(.70703125f, 0), Vector2.One);
    }

    private void DrawGuildAddMemberPopup()
    {
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            GuildFrameUiLaw.AddMemberPopup(_staticPopupSlots);
        if (popup is not { } visible || _skin is null) return;
        float s = GameplayUiScale();
        float textHeight = GameText.LinePitch("GameFontHighlight", 1);
        StaticPopupCoordinatorLaw.NarrowEditBoxLayout layout =
            StaticPopupCoordinatorLaw.NarrowEditLayout(textHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot, layout.Width, s);
        Vector2 size = new Vector2(layout.Width, layout.Height) * s;

        // ImGui hosts only the law-resolved modal and hit rectangles.
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin($"##guild-add-member-popup-{visible.Slot}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        GameText.DrawCentered(dl, "GameFontHighlight", GuildFrameUiLaw.AddMemberLabel,
            origin + new Vector2(layout.Width * .5f,
                layout.Text.Y + layout.Text.Height * .5f) * s, s);
        Vector2 editMin = origin + new Vector2(layout.EditBox.X, layout.EditBox.Y) * s;
        DrawStaticPopupEditBoxBorder(dl, editMin, s);
        ImGui.SetCursorScreenPos(editMin + new Vector2(0, 7) * s);
        ImGui.SetNextItemWidth(layout.EditBox.Width * s);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        if (_guildAddMemberFocusRequested)
        {
            ImGui.SetKeyboardFocusHere();
            _guildAddMemberFocusRequested = false;
        }
        bool entered = ImGui.InputText("##guild-add-member-edit", _guildAddMemberInput,
            (uint)_guildAddMemberInput.Length, ImGuiInputTextFlags.EnterReturnsTrue);
        _guildAddMemberEditFocused = ImGui.IsItemActive();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        bool accepted = DrawPartyInviteButton(dl,
            $"StaticPopup{visible.Slot}Button1", "Accept",
            origin + new Vector2(layout.Button1.X, layout.Button1.Y) * s,
            s, capture: false, default);
        bool cancelled = DrawPartyInviteButton(dl,
            $"StaticPopup{visible.Slot}Button2", "Cancel",
            origin + new Vector2(layout.Button2.X, layout.Button2.Y) * s,
            s, capture: false, default);
        dl.PopClipRect();
        ImGui.End();

        if (entered)
        {
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.EditBoxEnter(
                _staticPopupSlots, visible.Slot));
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, GuildFrameUiLaw.AddMemberPopupType));
        }
        else if (accepted)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1));
        else if (cancelled)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 2));
    }
}
