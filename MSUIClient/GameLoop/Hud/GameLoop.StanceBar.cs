using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private IReadOnlyList<SpellInfo> CurrentStanceForms()
    {
        if (_spellCatalog is null) return [];
        return StanceBarUiLaw.Forms(_actions.KnownSpells
            .Select(id => _spellCatalog.TryGet(id, out SpellInfo spell) ? spell : (SpellInfo?)null)
            .Where(spell => spell.HasValue).Select(spell => spell!.Value));
    }

    private bool StanceBarVisible => CurrentStanceForms().Count > 0;
    private bool PetOrStanceActionBarVisible => PetActionBarVisible || StanceBarVisible;

    private bool StanceSpellActive(in SpellInfo spell, WorldEntity? player)
    {
        byte form = player?.Fields.ShapeshiftForm ?? 0;
        uint spellId = spell.Id;
        bool aura = player?.Fields.Auras().Any(row => row.SpellId == spellId) == true;
        return StanceBarUiLaw.Active(spell, form, aura);
    }

    private bool StanceSpellCastable(in SpellInfo spell, WorldEntity? player, bool active,
        double now)
    {
        if (active) return true;
        if (player is null || player.IsDead ||
            _actions.IsOnCooldown(spell.Id, 0, spell, now))
            return false;
        return ControlledActorSpellResourceGate(spell, out _, out _);
    }

    private void ActivateStanceSpell(in SpellInfo spell, bool active)
    {
        if (BarsReadOnly || _net is null) return;
        uint formId = StanceBarUiLaw.FormId(spell);
        bool formCancelable = formId == 0 ||
            _shapeshiftForms?.TryGet(formId, out ShapeshiftFormInfo form) != true ||
            form.Cancelable;
        if (StanceBarUiLaw.CancelActive(formId, active, formCancelable))
        {
            _net.CancelAura(spell.Id);
            return;
        }
        if (!active) TryCast(spell.Id);
    }

    private void DrawStanceBar()
    {
        if ((_net is not { IsInWorld: true } && !HudPreview) || _gameplayArt is null) return;
        if (_freeView) return;   // commander console: no body chrome
        IReadOnlyList<SpellInfo> forms = CurrentStanceForms();
        if (forms.Count == 0) return;
        float scale = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 mainBar = GameplayBarMin(display, scale);
        // MSUI's two bottom reference bars are currently always shown. The managed law still
        // owns this decision so a future bar toggle moves the stance row without renderer edits.
        bool bottomLeftShown = true;
        var managedState = new UiParentManagedState(bottomLeftShown, BottomRightShown: true,
            RightLeftShown: false, RightRightShown: false,
            PetOrStanceShown: true, ReputationShown: false, MaxLevelShown: false);
        Vector2 origin = StanceBarUiLaw.FrameOrigin(mainBar, scale, managedState);
        bool raised = bottomLeftShown;
        float frameWidth = StanceBarUiLaw.FrameWidth(forms.Count);
        WorldEntity? player = _entities.TryGet(ControlledGuid, out WorldEntity self) ? self : null;
        double now = NowSeconds();
        var hovered = new bool[forms.Count];
        var pushed = new bool[forms.Count];
        var clicked = new bool[forms.Count];
        ImGuiWindowFlags inputFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
        for (int i = 0; i < forms.Count; i++)
        {
            Vector2 min = origin + new Vector2(StanceBarUiLaw.ButtonX(i),
                StanceBarUiLaw.ButtonTop) * scale;
            Vector2 size = new Vector2(StanceBarUiLaw.ButtonSize) * scale;
            ImGui.SetNextWindowPos(min, ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0);
            if (ImGui.Begin($"##stance-hit-{i}", inputFlags))
            {
                ImGui.SetCursorScreenPos(min);
                clicked[i] = ImGui.InvisibleButton($"##stance-{i}", size);
                hovered[i] = ImGui.IsItemHovered();
                pushed[i] = ImGui.IsItemActive() || BindingDown(ShapeshiftBinding(i));
            }
            ImGui.End();
        }
        ImGui.PopStyleVar(3);

        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(frameWidth, StanceBarUiLaw.FrameHeight) * scale,
            ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags drawFlags = inputFlags | ImGuiWindowFlags.NoMouseInputs;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
        if (!ImGui.Begin("##vanilla-stance-bar", drawFlags))
        {
            ImGui.End();
            ImGui.PopStyleVar(3);
            return;
        }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        DrawStanceShelf(dl, origin, scale, raised, forms.Count);
        PreparedSharedSpellTooltip? tooltip = null;
        for (int i = 0; i < forms.Count; i++)
        {
            SpellInfo spell = forms[i];
            bool active = StanceSpellActive(spell, player);
            bool castable = StanceSpellCastable(spell, player, active, now);
            Vector2 min = origin + new Vector2(StanceBarUiLaw.ButtonX(i),
                StanceBarUiLaw.ButtonTop) * scale;
            Vector2 max = min + new Vector2(StanceBarUiLaw.ButtonSize) * scale;
            string iconPath = StanceBarUiLaw.Icon(spell, active);
            uint icon = _gameplayArt.Handle(iconPath);
            if (icon != 0)
                dl.AddImage((nint)icon, min, max, Vector2.Zero, Vector2.One,
                    castable ? 0xffffffff : 0xff666666);
            float ringSize = StanceBarUiLaw.RingSize(raised);
            Vector2 ringCenter = (min + max) * .5f - new Vector2(0, scale);
            Vector2 ringHalf = new(ringSize * .5f * scale);
            uint ring = _gameplayArt.Handle(StanceBarUiLaw.RingPath);
            if (ring != 0) dl.AddImage((nint)ring, ringCenter - ringHalf, ringCenter + ringHalf);
            if (_actions.TryCooldownDisplay(spell.Id, 0, spell, now,
                    out CooldownDisplay cooldown))
            {
                if (cooldown.SweepFraction is { } sweep) DrawCooldownSwipe(dl, min, max, sweep);
                else if (cooldown.FlashProgress is { } flash) DrawCooldownFlash(dl, min, max, flash);
            }
            if (active)
            {
                uint check = _gameplayArt.AdditiveHandle(StanceBarUiLaw.CheckedPath);
                if (check != 0) dl.AddImage((nint)check, min, max);
            }
            if (pushed[i])
            {
                uint depress = _gameplayArt.Handle(StanceBarUiLaw.DepressPath);
                if (depress != 0) dl.AddImage((nint)depress, min, max);
            }
            if (hovered[i])
            {
                uint highlight = _gameplayArt.BrightHighlightHandle(StanceBarUiLaw.HighlightPath);
                if (highlight != 0) dl.AddImage((nint)highlight, min, max);
                tooltip = PrepareSharedSpellTooltip(new("stance", (ulong)(i + 1)),
                    spell.Id, scale, SpellTooltipPlacement.DefaultBottomRight);
            }
            if (clicked[i]) ActivateStanceSpell(spell, active);
        }
        dl.PopClipRect();
        ImGui.End();
        ImGui.PopStyleVar(3);
        if (tooltip is { } prepared)
            OfferPreservedSharedGameTooltipRenderer(prepared.Owner,
                () => DrawSpellTooltip(prepared.Snapshot));
    }

    private void DrawStanceShelf(ImDrawListPtr dl, Vector2 origin, float scale,
        bool raised, int formCount)
    {
        if (raised) return;
        Vector2 left = origin + new Vector2(0,
            StanceBarUiLaw.FrameHeight - StanceBarUiLaw.ShelfLeftSize.Y) * scale;
        uint ends = _gameplayArt?.Handle(@"Interface\ShapeshiftBar\ShapeshiftBarEnds") ?? 0;
        if (ends != 0)
            dl.AddImage((nint)ends, left, left + StanceBarUiLaw.ShelfLeftSize * scale,
                new Vector2(0, 0), new Vector2(.453125f, 1));
        Vector2 middle = left + new Vector2(StanceBarUiLaw.ShelfLeftSize.X, 0) * scale;
        if (StanceBarUiLaw.ShowMiddleShelf(raised, formCount))
        {
            uint middleArt = _gameplayArt?.Handle(
                @"Interface\ShapeshiftBar\ShapeshiftBarMiddle") ?? 0;
            if (middleArt != 0)
                dl.AddImage((nint)middleArt, middle,
                    middle + StanceBarUiLaw.ShelfMiddleSize * scale);
        }
        Vector2 right = middle + new Vector2(StanceBarUiLaw.ShelfMiddleSize.X, 0) * scale;
        if (ends != 0)
            dl.AddImage((nint)ends, right, right + StanceBarUiLaw.ShelfRightSize * scale,
                new Vector2(.453125f, 0), new Vector2(.875f, 1));
    }
}
