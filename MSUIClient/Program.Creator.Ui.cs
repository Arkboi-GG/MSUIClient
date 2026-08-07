using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.World.Units;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator-mode HUD: a top-left row of red glue buttons opening skinned panels.
// Character (race/sex/appearance), Gear (tier sets + item search), Teleport
// (preset locations + world map), Target (spawn/despawn a practice dummy),
// Spells (the Phase 3 spell workshop).
//
// The bar is its own small auto-sized ImGui window (NOT full-screen - a
// full-screen invisible window would steal the camera's mouse input). Panels
// are skinned-ImGui windows, the settings-modal approach.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private enum CreatorPanel { None, Character, Gear, Teleport, Target, Spells }
    private CreatorPanel _creatorPanel;

    // Character look state (defaults mirror the offline test character).
    private byte _creatorRace = 1;      // ChrRaces id, Human
    private byte _creatorSex;           // 0 male, 1 female
    private readonly int[] _creatorDials = new int[5];   // skin, face, hairStyle, hairColor, facialHair
    private CharCreateCatalog? _creatorCatalog;
    private bool _creatorCatalogTried;

    // Gear state: normalized slot key -> worn piece. Seeded from Battlegear of Might.
    private readonly record struct CreatorPiece(string Name, uint DisplayId, int InventoryType);
    private Dictionary<int, CreatorPiece>? _creatorEquip;
    private int _creatorClassIndex;     // index into CreatorTierSets.Classes
    private CreatorItemTable? _creatorItems;
    private bool _creatorItemsTried;
    private bool _creatorSearchOpen;
    private readonly byte[] _creatorSearchBuf = new byte[64];
    private int _creatorSearchSlot = -1;   // inventoryType filter, -1 = any
    private List<CreatorItemTable.Item>? _creatorSearchResults;

    private static readonly (string Label, byte Race)[] CreatorRaces =
    {
        ("Human", 1), ("Dwarf", 3), ("Night Elf", 4), ("Gnome", 7),
        ("Orc", 2), ("Undead", 5), ("Tauren", 6), ("Troll", 8),
    };

    private static readonly (string Label, int InvType)[] CreatorSearchSlots =
    {
        ("Any slot", -1), ("Head", 1), ("Shoulder", 3), ("Chest", 5), ("Robe", 20),
        ("Shirt", 4), ("Tabard", 19), ("Back", 16), ("Waist", 6), ("Legs", 7),
        ("Feet", 8), ("Wrist", 9), ("Hands", 10), ("Main Hand", 21), ("One-Hand", 13),
        ("Two-Hand", 17), ("Off Hand", 22), ("Held", 23), ("Shield", 14), ("Ranged", 15),
    };

    private static readonly Vector4[] CreatorQualityColors =
    {
        new(0.62f, 0.62f, 0.62f, 1f),   // poor
        new(1f, 1f, 1f, 1f),            // common
        new(0.12f, 1f, 0f, 1f),         // uncommon
        new(0f, 0.44f, 0.87f, 1f),      // rare
        new(0.64f, 0.21f, 0.93f, 1f),   // epic
        new(1f, 0.50f, 0f, 1f),         // legendary
        new(0.90f, 0.80f, 0.50f, 1f),   // artifact
    };

    /// <summary>Widget/panel size multiplier (GameSettings.Creator.UiScale, persisted).</summary>
    private float CreatorUiScale => Math.Clamp(Settings.Creator.UiScale, 0.6f, 2.5f);

    /// <summary>Text-only size multiplier (GameSettings.Creator.TextScale, persisted).</summary>
    private float CreatorTextScale => Math.Clamp(Settings.Creator.TextScale, 0.6f, 2.5f);

    private bool _creatorUiOptionsOpen;

    /// <summary>The creator-mode overlay: menu bar + whichever panel is open.</summary>
    private void DrawCreatorHud()
    {
        DrawCreatorMenuBar();
        switch (_creatorPanel)
        {
            case CreatorPanel.Character: DrawCreatorCharacterPanel(); break;
            case CreatorPanel.Gear: DrawCreatorGearPanel(); break;
            case CreatorPanel.Teleport: DrawCreatorTeleportPanel(); break;
            case CreatorPanel.Target: DrawCreatorTargetPanel(); break;
            case CreatorPanel.Spells: DrawCreatorSpellsPanel(); break;
        }
        if (_creatorSearchOpen) DrawCreatorItemSearch();
    }

    private void DrawCreatorMenuBar()
    {
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorUiScale;
        ImGui.SetNextWindowPos(new Vector2(8f * s, 6f * s), ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysAutoResize
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.Begin("##creator-bar", flags)) { ImGui.End(); return; }

        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;
        var size = new Vector2(118f * s, 30f * s);

        CreatorBarButton("Character", CreatorPanel.Character, size);
        ImGui.SameLine();
        CreatorBarButton("Gear", CreatorPanel.Gear, size);
        ImGui.SameLine();
        CreatorBarButton("Teleport", CreatorPanel.Teleport, size);
        ImGui.SameLine();
        CreatorBarButton("Target", CreatorPanel.Target, size);
        ImGui.SameLine();
        CreatorBarButton("Spells", CreatorPanel.Spells, size);

        // The UI-options toggle: scale dials live in their own little panel.
        ImGui.SameLine();
        if (_skin?.GlueButton("UI", new Vector2(46f * s, 30f * s)) ?? ImGui.SmallButton("UI"))
            _creatorUiOptionsOpen = !_creatorUiOptionsOpen;

        if (_skin is not null) _skin.Scale = savedScale;
        ImGui.End();

        if (_creatorUiOptionsOpen) DrawCreatorUiOptions();
    }

    /// <summary>Creator UI options: separate dials for widget size and text size,
    /// each live while dragging and saved when the drag ends.</summary>
    private void DrawCreatorUiOptions()
    {
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        ImGui.SetNextWindowPos(new Vector2(640f * s, 64f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(300f * cs, 0f), ImGuiCond.Always);
        PushCreatorStyle();
        bool open = true;
        if (ImGui.Begin("###creator-ui-options", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse))
        {
            if (DrawCreatorPanelChrome("Creator UI")) open = false;
            ImGui.SetWindowFontScale(CreatorTextScale);

            float ui = Settings.Creator.UiScale;
            ImGui.SetNextItemWidth(180f * cs);
            if (ImGui.SliderFloat("Widget scale", ref ui, 0.6f, 2f, "%.2fx"))
                Settings.Creator.UiScale = ui;
            bool save = ImGui.IsItemDeactivatedAfterEdit();

            float text = Settings.Creator.TextScale;
            ImGui.SetNextItemWidth(180f * cs);
            if (ImGui.SliderFloat("Text scale", ref text, 0.6f, 2f, "%.2fx"))
                Settings.Creator.TextScale = text;
            save |= ImGui.IsItemDeactivatedAfterEdit();

            if (ImGui.Button("Reset"))
            {
                Settings.Creator.UiScale = 1f;
                Settings.Creator.TextScale = 1f;
                save = true;
            }
            if (save) SettingsFile?.Save();
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        PopCreatorStyle();
        if (!open) _creatorUiOptionsOpen = false;
    }

    private void CreatorBarButton(string label, CreatorPanel panel, Vector2 size)
    {
        bool clicked = _skin?.GlueButton(label, size) ?? ImGui.Button(label, size);
        if (clicked) _creatorPanel = _creatorPanel == panel ? CreatorPanel.None : panel;
    }

    // ── panel chrome ─────────────────────────────────────────────────────────
    // WowSkin.PushStyle deliberately leaves WindowBg TRANSPARENT (the settings
    // modal paints its own dialog art). Creator panels must therefore paint
    // their own chrome or the world bleeds straight through the widgets:
    // an almost-opaque warm fill + the riveted UI-DialogBox nine-slice border,
    // plus opaque dark title bars (the skin never styles ImGui's title bar).

    private int _creatorStyleColors;
    private int _creatorStyleVars;

    private void PushCreatorStyle()
    {
        _skin?.PushStyle();
        _creatorStyleColors = 0;
        _creatorStyleVars = 0;
        void C(ImGuiCol which, Vector4 color) { ImGui.PushStyleColor(which, color); _creatorStyleColors++; }
        void V(ImGuiStyleVar which, Vector2 value) { ImGui.PushStyleVar(which, value); _creatorStyleVars++; }

        C(ImGuiCol.Text, new Vector4(0.96f, 0.93f, 0.86f, 1f));
        C(ImGuiCol.TextDisabled, new Vector4(0.80f, 0.68f, 0.42f, 1f));   // section headers read gold, not grey
        C(ImGuiCol.FrameBg, new Vector4(0.13f, 0.13f, 0.14f, 0.90f));     // grey input/slider wells
        C(ImGuiCol.ChildBg, new Vector4(0.06f, 0.06f, 0.07f, 0.45f));

        // Breathing room, following the widget dial: the skin's paddings are
        // tuned for the dense settings modal and read cramped in the creator.
        float ps = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorUiScale;
        V(ImGuiStyleVar.WindowPadding, new Vector2(20f, 18f) * ps);
        V(ImGuiStyleVar.ItemSpacing, new Vector2(10f, 9f) * ps);
        V(ImGuiStyleVar.FramePadding, new Vector2(9f, 6f) * ps);
    }

    private void PopCreatorStyle()
    {
        if (_creatorStyleVars > 0) { ImGui.PopStyleVar(_creatorStyleVars); _creatorStyleVars = 0; }
        if (_creatorStyleColors > 0) { ImGui.PopStyleColor(_creatorStyleColors); _creatorStyleColors = 0; }
        _skin?.PopStyle();
    }

    /// <summary>
    /// The 1.12 dialog chrome, replacing ImGui's window decoration entirely:
    /// UI-DialogBox border + background over a near-opaque fill, the
    /// UI-DialogBox-Header plaque hanging above the frame with the title, and
    /// the round UI-Panel-MinimizeButton close. Returns true when close was
    /// clicked. Call right after a successful Begin on a NoTitleBar window.
    /// </summary>
    private bool DrawCreatorPanelChrome(string title)
    {
        var dl = ImGui.GetWindowDrawList();
        Vector2 min = ImGui.GetWindowPos();
        Vector2 max = min + ImGui.GetWindowSize();
        float ps = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorUiScale;

        // The header plaque hangs ABOVE the frame (GameMenuFrame.xml numbers);
        // the window clip rect would eat it, so override while painting chrome.
        dl.PushClipRect(min - new Vector2(64f * ps, 64f * ps),
                        max + new Vector2(64f * ps, 64f * ps), false);
        // Semi-grey fill like the in-game menus, a touch darker, INSET from the
        // window rect so nothing bleeds past the border art's rounded corners.
        var fillInset = new Vector2(5f, 5f) * ps;
        dl.AddRectFilled(min + fillInset, max - fillInset,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.11f, 0.11f, 0.12f, 0.62f)));
        if (_skin is not null)
        {
            float saved = _skin.Scale;
            _skin.Scale = ps;
            _skin.DrawBackdrop(dl, min, max, WowSkin.Dialog);
            _skin.HeaderPlaque(dl, min, max.X - min.X, title);
            _skin.Scale = saved;
        }
        dl.PopClipRect();

        // Round red close button, top-right on the frame - vanilla's 32px art,
        // full-size so it is comfortably clickable.
        Vector2 keep = ImGui.GetCursorPos();
        var closeSize = new Vector2(38f, 38f) * ps;
        var closePos = new Vector2(max.X - closeSize.X - 1f * ps, min.Y + 1f * ps);
        bool closed = DrawImageButtonClicked(dl, $"##creator-close-{title}", closePos, closeSize,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        ImGui.SetCursorPos(keep);

        // Clear the plaque's visible plate before content begins.
        ImGui.Dummy(new Vector2(1f, 16f * ps));
        return closed;
    }

    /// <summary>
    /// Begin a creator panel window under the bar, dressed in the real 1.12
    /// dialog chrome. Returns false when closed. Width and text both follow the
    /// creator dials - widths are re-asserted every frame (cond Always) so the
    /// dial applies live; the panels are fixed-layout, so losing manual resize
    /// costs nothing.
    /// </summary>
    private bool BeginCreatorPanel(string title, float width)
    {
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        ImGui.SetNextWindowPos(new Vector2(8f * s, 64f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(width * cs, 0f), ImGuiCond.Always);
        PushCreatorStyle();
        if (!ImGui.Begin($"###creator-{title}", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            PopCreatorStyle();
            return false;
        }
        if (DrawCreatorPanelChrome(title)) _creatorPanel = CreatorPanel.None;
        ImGui.SetWindowFontScale(CreatorTextScale);
        return true;
    }

    private void EndCreatorPanel()
    {
        ImGui.SetWindowFontScale(1f);
        ImGui.End();
        PopCreatorStyle();
    }

    // ── text-aware sizing ────────────────────────────────────────────────────
    // The widget dial sizes padding and minimum widths; HEIGHTS always derive
    // from the live text size, and widths grow to fit their caption - so no
    // combination of the two dials ever clips a label.

    /// <summary>Control height that always fits the current text scale.</summary>
    private float CreatorRowHeight => ImGui.GetTextLineHeight() + 8f * CreatorUiScale;

    /// <summary>A button at least minWidth wide, grown to fit its caption, as tall as the
    /// text - drawn with the real UI-Panel-Button art (the vanilla in-game button).</summary>
    private bool CreatorButton(string label, float minWidth = 0f)
    {
        Vector2 text = ImGui.CalcTextSize(label);
        var size = new Vector2(MathF.Max(minWidth, text.X + 36f * CreatorUiScale), CreatorRowHeight);
        return _skin?.PanelButton(label, size) ?? ImGui.Button(label, size);
    }

    // ── drill-down categories ────────────────────────────────────────────────

    private readonly Dictionary<string, bool> _creatorCategoryOpen = new();

    /// <summary>
    /// A vanilla expandable category row - the quest-log +/- button art with a
    /// gold header. Returns true while expanded. The id is stable storage for the
    /// open state; the visible label may change freely.
    /// </summary>
    private bool CreatorCategory(string id, string label, bool defaultOpen = false)
    {
        if (!_creatorCategoryOpen.TryGetValue(id, out bool open)) open = defaultOpen;
        float cs = CreatorUiScale;
        float h = CreatorRowHeight;
        var dl = ImGui.GetWindowDrawList();
        Vector2 pos = ImGui.GetCursorScreenPos();
        float avail = MathF.Max(ImGui.GetContentRegionAvail().X, 60f);
        if (ImGui.InvisibleButton($"##cat-{id}", new Vector2(avail, h)))
        {
            open = !open;
            _creatorCategoryOpen[id] = open;
        }
        bool hovered = ImGui.IsItemHovered();

        float icon = MathF.Min(h - 2f, 18f * cs);
        var iconMin = pos + new Vector2(0f, (h - icon) * 0.5f);
        uint plusMinus = _gameplayArt?.Handle(open
            ? @"Interface\Buttons\UI-MinusButton-Up"
            : @"Interface\Buttons\UI-PlusButton-Up") ?? 0;
        if (plusMinus != 0)
            dl.AddImage((nint)plusMinus, iconMin, iconMin + new Vector2(icon, icon));
        else
            dl.AddText(iconMin, 0xffffffff, open ? "-" : "+");

        var textPos = new Vector2(pos.X + icon + 6f * cs, pos.Y + (h - ImGui.GetTextLineHeight()) * 0.5f);
        dl.AddText(textPos + new Vector2(1f, 1f), 0xdd000000, label);
        dl.AddText(textPos, hovered ? 0xffffffff : VanillaGold, label);
        return open;
    }

    /// <summary>The widest caption in a set, plus button padding - a grid column width.
    /// The pad covers ImGui's frame padding on both sides (the skin pushes 6px-scaled
    /// each side) with margin, so the widest label never clips.</summary>
    private float CreatorColumnWidth(IEnumerable<string> labels)
    {
        float widest = 0f;
        foreach (string label in labels)
            widest = MathF.Max(widest, ImGui.CalcTextSize(label).X);
        return widest + 36f * CreatorUiScale;
    }

    /// <summary>Combo width sized to its widest option (plus the arrow button).</summary>
    private float CreatorComboWidth(IEnumerable<string> labels) =>
        CreatorColumnWidth(labels) + ImGui.GetFrameHeight();

    // ── Character ────────────────────────────────────────────────────────────

    private void DrawCreatorCharacterPanel()
    {
        if (!BeginCreatorPanel("Character", 490f)) return;
        float cs = CreatorUiScale;

        if (!_creatorCatalogTried)
        {
            _creatorCatalogTried = true;
            _creatorCatalog = CharCreateCatalog.Load(_config.ClientDataPath);
        }

        if (CreatorCategory("char-race", "Race & Sex", defaultOpen: true))
        {
            ImGui.Indent(10f * cs);
            float raceW = CreatorColumnWidth(CreatorRaces.Select(r => r.Label));
            var dl = ImGui.GetWindowDrawList();
            for (int i = 0; i < CreatorRaces.Length; i++)
            {
                if (i % 4 != 0) ImGui.SameLine();
                bool active = _creatorRace == CreatorRaces[i].Race;
                var size = new Vector2(raceW, CreatorRowHeight);
                bool clicked = _skin?.PanelButton(CreatorRaces[i].Label, size)
                               ?? ImGui.Button(CreatorRaces[i].Label, size);
                if (active)   // gold rim marks the worn race, vanilla checked-tab style
                    dl.AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), VanillaGold, 0f,
                        ImDrawFlags.None, MathF.Max(1f, 2f * cs));
                if (clicked && !active)
                {
                    _creatorRace = CreatorRaces[i].Race;
                    ClampCreatorDials();
                    ApplyCreatorLook(modelChanged: true);
                }
            }

            int sex = _creatorSex;
            if (ImGui.RadioButton("Male", ref sex, 0) | ImGui.RadioButton("Female", ref sex, 1))
            {
                if (sex != _creatorSex)
                {
                    _creatorSex = (byte)sex;
                    ClampCreatorDials();
                    ApplyCreatorLook(modelChanged: true);
                }
            }
            ImGui.Unindent(10f * cs);
            ImGui.Spacing();
        }

        int[] counts = _creatorCatalog?.DialCounts(_creatorRace, _creatorSex) ?? new[] { 10, 10, 10, 10, 10 };
        if (CreatorCategory("char-appearance", "Appearance", defaultOpen: true))
        {
            ImGui.Indent(10f * cs);
            string[] dialNames = { "Skin", "Face", "Hair style", "Hair color", _creatorSex == 1 ? "Markings" : "Facial hair" };
            bool dialsChanged = false;
            for (int i = 0; i < 5; i++)
            {
                int max = Math.Max(counts[i] - 1, 0);
                int value = Math.Min(_creatorDials[i], max);
                ImGui.SetNextItemWidth(240f * cs);
                if (ImGui.SliderInt(dialNames[i], ref value, 0, max) && value != _creatorDials[i])
                {
                    _creatorDials[i] = value;
                    dialsChanged = true;
                }
            }
            if (dialsChanged) ApplyCreatorLook(modelChanged: false);

            if (CreatorButton("Randomize"))
            {
                var rng = Random.Shared;
                for (int i = 0; i < 5; i++)
                    _creatorDials[i] = counts[i] > 0 ? rng.Next(counts[i]) : 0;
                ApplyCreatorLook(modelChanged: false);
            }
            ImGui.Unindent(10f * cs);
        }

        EndCreatorPanel();
    }

    private void ClampCreatorDials()
    {
        int[] counts = _creatorCatalog?.DialCounts(_creatorRace, _creatorSex) ?? new[] { 1, 1, 1, 1, 1 };
        for (int i = 0; i < 5; i++)
            _creatorDials[i] = Math.Clamp(_creatorDials[i], 0, Math.Max(counts[i] - 1, 0));
    }

    /// <summary>
    /// Push the creator's race/sex/dials/equipment onto the live world character.
    /// Race/sex changes need a synchronous model re-Load (the GlueBooth create-
    /// preview approach); dial/equipment changes ride the async appearance path.
    /// </summary>
    private void ApplyCreatorLook(bool modelChanged)
    {
        if (_character is null) return;
        CharacterEquipment kit = BuildCreatorEquipment();
        if (modelChanged)
        {
            string folder = CreatorRaceFolder(_creatorRace);
            string gender = _creatorSex == 1 ? "Female" : "Male";
            if (!_character.Load(folder, gender))
            {
                Console.WriteLine($"[creator] could not load {folder} {gender}");
                return;
            }
            _character.SkinId = _creatorDials[0];
            _character.FaceId = _creatorDials[1];
            _character.HairStyleId = _creatorDials[2];
            _character.HairColorId = _creatorDials[3];
            _character.FacialHairId = _creatorDials[4];
            _character.Equipment = kit;
            _character.Reload();
        }
        else
        {
            _character.QueueAppearanceUpdate(_creatorDials[0], _creatorDials[1],
                _creatorDials[2], _creatorDials[3], _creatorDials[4], kit);
        }
        SaveCreatorLook();
    }

    private static string CreatorRaceFolder(byte race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "NightElf",
        5 => "Scourge", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "Human"
    };

    // ── Gear ─────────────────────────────────────────────────────────────────

    /// <summary>Chest/robe share a key; weapon-ish types collapse to hand slots.</summary>
    private static int CreatorSlotKey(int inventoryType) => inventoryType switch
    {
        20 => CharacterEquipment.Slot.Chest,
        13 or 17 => CharacterEquipment.Slot.MainHand,
        14 or 23 => CharacterEquipment.Slot.OffHand,
        25 or 26 => CharacterEquipment.Slot.Ranged,
        _ => inventoryType,
    };

    private Dictionary<int, CreatorPiece> CreatorEquip
    {
        get
        {
            if (_creatorEquip is null)
            {
                _creatorEquip = new Dictionary<int, CreatorPiece>();
                // A persisted look wins; a fresh install starts in the Battlegear.
                if (Settings.Creator.Equipment is { Count: > 0 } saved)
                {
                    foreach (var piece in saved)
                        _creatorEquip[CreatorSlotKey(piece.InventoryType)] =
                            new CreatorPiece(piece.Name, piece.DisplayId, piece.InventoryType);
                }
                else
                {
                    foreach (var piece in CharacterEquipment.BattlegearOfMight().Pieces)
                        _creatorEquip[CreatorSlotKey(piece.InventoryType)] =
                            new CreatorPiece(piece.Name, piece.DisplayId, piece.InventoryType);
                }
            }
            return _creatorEquip;
        }
    }

    /// <summary>Load the persisted creator look into the live fields and wear it.
    /// Called once when a creator session enters the world.</summary>
    private void RestoreCreatorLook()
    {
        var saved = Settings.Creator;
        _creatorRace = saved.Race is >= 1 and <= 8 ? saved.Race : (byte)1;
        _creatorSex = saved.Sex == 1 ? (byte)1 : (byte)0;
        if (saved.Dials is { Length: 5 })
            Array.Copy(saved.Dials, _creatorDials, 5);
        _creatorEquip = null;   // re-seed from the persisted equipment
        ApplyCreatorLook(modelChanged: true);
        Console.WriteLine($"[creator] restored look: race {_creatorRace} sex {_creatorSex}, " +
                          $"{CreatorEquip.Count} piece(s)");
    }

    /// <summary>Persist the current creator look. Called from ApplyCreatorLook, so
    /// every race/sex/dial/gear change sticks into the next session.</summary>
    private void SaveCreatorLook()
    {
        var target = Settings.Creator;
        target.Race = _creatorRace;
        target.Sex = _creatorSex;
        target.Dials = (int[])_creatorDials.Clone();
        target.Equipment = CreatorEquip.Values
            .Select(p => new GameSettings.CreatorPieceSetting
            { Name = p.Name, DisplayId = p.DisplayId, InventoryType = p.InventoryType })
            .ToList();
        SettingsFile?.Save();
    }

    private CharacterEquipment BuildCreatorEquipment()
    {
        var kit = new CharacterEquipment();
        foreach (var piece in CreatorEquip.Values)
            kit.Add(piece.Name, piece.DisplayId, piece.InventoryType);
        return kit;
    }

    private void DrawCreatorGearPanel()
    {
        if (!BeginCreatorPanel("Gear", 380f)) return;
        float cs = CreatorUiScale;

        if (CreatorCategory("gear-tiers", "Tier Sets", defaultOpen: true))
        {
            ImGui.Indent(10f * cs);
            string[] classes = CreatorTierSets.Classes;
            _creatorClassIndex = Math.Clamp(_creatorClassIndex, 0, classes.Length - 1);
            ImGui.SetNextItemWidth(CreatorComboWidth(classes));
            ImGui.Combo("Class", ref _creatorClassIndex, classes, classes.Length);
            foreach (string tier in CreatorTierSets.Tiers)
            {
                if (tier != CreatorTierSets.Tiers[0]) ImGui.SameLine();
                if (CreatorButton(tier, 56f * cs))
                    ApplyCreatorTierSet(classes[_creatorClassIndex], tier);
            }
            ImGui.TextDisabled("Weapons are kept when swapping tier sets.");
            ImGui.Unindent(10f * cs);
            ImGui.Spacing();
        }

        if (CreatorCategory("gear-worn", "Worn Equipment", defaultOpen: true))
        {
            ImGui.Indent(10f * cs);
            int? removeKey = null;
            foreach (var (key, piece) in CreatorEquip.OrderBy(p => p.Key))
            {
                ImGui.PushID(key);
                if (ImGui.SmallButton("x")) removeKey = key;
                ImGui.SameLine();
                ImGui.TextUnformatted($"{CreatorSlotName(key)}: {piece.Name}");
                ImGui.PopID();
            }
            if (removeKey is { } gone)
            {
                CreatorEquip.Remove(gone);
                ApplyCreatorLook(modelChanged: false);
            }

            ImGui.Spacing();
            if (CreatorButton("Find item...")) _creatorSearchOpen = true;
            ImGui.SameLine();
            if (CreatorButton("Undress"))
            {
                CreatorEquip.Clear();
                ApplyCreatorLook(modelChanged: false);
            }
            ImGui.Unindent(10f * cs);
        }

        EndCreatorPanel();
    }

    private static string CreatorSlotName(int slotKey) => slotKey switch
    {
        1 => "Head", 3 => "Shoulder", 4 => "Shirt", 5 => "Chest", 6 => "Waist",
        7 => "Legs", 8 => "Feet", 9 => "Wrist", 10 => "Hands", 15 => "Ranged",
        16 => "Back", 19 => "Tabard", 21 => "Main Hand", 22 => "Off Hand",
        _ => $"Slot {slotKey}",
    };

    private void ApplyCreatorTierSet(string cls, string tier)
    {
        if (!CreatorTierSets.Sets.TryGetValue(cls, out var tiers) ||
            !tiers.TryGetValue(tier, out var pieces)) return;

        // Tier sets are armor: drop worn armor, keep hands/ranged (the weapons).
        var keep = new[] { CharacterEquipment.Slot.MainHand, CharacterEquipment.Slot.OffHand, CharacterEquipment.Slot.Ranged };
        foreach (int key in CreatorEquip.Keys.Where(k => !keep.Contains(k)).ToList())
            CreatorEquip.Remove(key);
        foreach (var piece in pieces)
        {
            if (piece.InventoryType == 11) continue;   // rings have no visual
            CreatorEquip[CreatorSlotKey(piece.InventoryType)] =
                new CreatorPiece(piece.Name, piece.DisplayId, piece.InventoryType);
        }
        ApplyCreatorLook(modelChanged: false);
        Console.WriteLine($"[creator] dressed {cls} {tier} ({pieces.Length} pieces)");
    }

    private void DrawCreatorItemSearch()
    {
        if (!_creatorItemsTried)
        {
            _creatorItemsTried = true;
            _creatorItems = CreatorItemTable.Load(_config.RepoRoot);
        }

        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        ImGui.SetNextWindowPos(new Vector2(390f * s, 64f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(420f * cs, 480f * cs), ImGuiCond.FirstUseEver);
        PushCreatorStyle();
        bool open = true;
        if (ImGui.Begin("###creator-find-item", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse))
        {
            if (DrawCreatorPanelChrome("Find Item")) open = false;
            ImGui.SetWindowFontScale(CreatorTextScale);
            if (_creatorItems is null)
            {
                ImGui.TextWrapped("creator-items.tsv is missing. Regenerate it from " +
                                  "MangosSuperUI (/Items/Search dump) and restart.");
            }
            else
            {
                ImGui.SetNextItemWidth(200f * cs);
                bool changed = ImGui.InputText("##search", _creatorSearchBuf, (uint)_creatorSearchBuf.Length);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(130f * cs);
                int slotIndex = Array.FindIndex(CreatorSearchSlots, x => x.InvType == _creatorSearchSlot);
                if (slotIndex < 0) slotIndex = 0;
                string[] slotLabels = CreatorSearchSlots.Select(x => x.Label).ToArray();
                if (ImGui.Combo("##slot", ref slotIndex, slotLabels, slotLabels.Length))
                {
                    _creatorSearchSlot = CreatorSearchSlots[slotIndex].InvType;
                    changed = true;
                }

                string query = BufToString(_creatorSearchBuf);
                if (changed || _creatorSearchResults is null)
                    _creatorSearchResults = query.Length >= 2 || _creatorSearchSlot >= 0
                        ? _creatorItems.Search(query, _creatorSearchSlot)
                        : new List<CreatorItemTable.Item>();

                ImGui.TextDisabled(query.Length < 2 && _creatorSearchSlot < 0
                    ? "Type at least 2 letters, or pick a slot."
                    : $"{_creatorSearchResults.Count} result(s), click to equip");

                if (ImGui.BeginChild("##results", new Vector2(0f, -4f)))
                {
                    foreach (var item in _creatorSearchResults)
                    {
                        var color = CreatorQualityColors[Math.Min(item.Quality, (byte)6)];
                        ImGui.PushStyleColor(ImGuiCol.Text, color);
                        bool clicked = ImGui.Selectable($"{item.Name}##{item.Entry}");
                        ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"entry {item.Entry}  display {item.DisplayId}\n" +
                                             $"{CreatorSlotName(CreatorSlotKey(item.InventoryType))}  ilvl {item.ItemLevel}");
                        if (clicked)
                        {
                            CreatorEquip[CreatorSlotKey(item.InventoryType)] =
                                new CreatorPiece(item.Name, item.DisplayId, item.InventoryType);
                            ApplyCreatorLook(modelChanged: false);
                        }
                    }
                }
                ImGui.EndChild();
            }
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        PopCreatorStyle();
        if (!open) _creatorSearchOpen = false;
    }

    // ── Placeholders wired in the next slices ────────────────────────────────

    private partial void DrawCreatorTeleportPanel();
    private partial void DrawCreatorTargetPanel();
    private partial void DrawCreatorSpellsPanel();
}
