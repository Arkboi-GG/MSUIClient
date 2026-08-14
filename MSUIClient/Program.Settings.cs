using System.Numerics;
using ImGuiNET;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.World;

namespace MSUIClient;

/// <summary>
/// The player-facing menu - Escape opens the Game Menu, which opens Video
/// Options, laid out the way 1.12 lays them out.
///
/// THE SHAPE IS BLIZZARD'S, READ OUT OF THE ARCHIVE
///   `Interface\FrameXML\GameMenuFrame.xml` and `OptionsFrame.xml` ship inside
///   interface.MPQ. The frame sizes, the button size, the header offsets and the
///   three backdrop definitions below are transcribed from them rather than
///   eyeballed from a screenshot. The first version of this file invented a
///   left-rail layout that exists nowhere in WoW; this one does not invent
///   anything structural.
///
///   GameMenuFrame  195 x 246, buttons 144 x 21, first button 37 below the top,
///                  1px gaps, and a 16px gap before Continue.
///   OptionsFrame   450 x 575 on first open. Renderer-specific controls scroll
///                  inside it; the player may enlarge/shrink each options page
///                  and that useful size is remembered in settings.json.
///
/// THIS IS THE FIRST THING ON THE NON-DEVTOOLS SIDE OF THE SEAM
///   FOUNDATION_PLAN section 12 says developer tooling ships off. DrawSettingsModal
///   is called BEFORE Gui()'s early return so it survives a shipping build. Do not
///   move it after that return and do not add a DevTools check to it.
///
/// LIVE APPLY, NOT APPLY-ON-OK
///   Everything takes effect as you drag. This client's whole working method is
///   by-eye A/B (handbook section 6) and an apply-on-OK dialog breaks it. The
///   snapshot taken when the menu opens is what Cancel restores.
///
/// WHAT CLOSES IT
///   Escape from Video Options goes BACK to the Game Menu, like the real client.
///   Escape from the Game Menu closes it and writes settings.json. Cancel inside
///   Video Options restores the snapshot and writes nothing.
/// </summary>
public sealed partial class GameLoop
{
    private const string MenuPopupId = "##msui-game-menu";

    /// <summary>Which frame the menu is showing. 0 is the Game Menu itself.</summary>
    private enum MenuPage { GameMenu = 0, Video, Controls }

    /// <summary>
    /// Handed over by Program.Main, which loads it BEFORE the window exists
    /// because resolution, sample count and anisotropy are decided at window
    /// creation and cannot be changed afterwards.
    /// </summary>
    public SettingsStore? SettingsFile { get; set; }

    private WowSkin? _skin;
    private Engine.UI.GlueAdditive? _glueAdd;

    private bool _settingsOpen;
    private bool _settingsPopupRequested;
    private bool _settingsPopupCloseRequested;
    private bool _settingsCancelling;
    private bool _escapeKeyDown;

    /// <summary>Escape seen this frame, not yet acted on. Spent inside the popup scope.</summary>
    private bool _escapePressed;

    /// <summary>Set by Exit Game. Spent by Update, between frames. See ConsumeQuitRequest.</summary>
    private bool _quitRequested;

    /// <summary>
    /// A clutter coverage value changed and the scatter has to be rebuilt. NOT
    /// acted on while a widget is still held: a full re-scatter was measured at
    /// 2,438 ms at radius 45 and grows with the square of it, so firing one per
    /// frame of a slider drag freezes the client solid. Spent on mouse release.
    /// </summary>
    private bool _clutterRescatterPending;
    private GameSettings? _settingsSnapshot;
    private MenuPage _menuPage = MenuPage.GameMenu;
    private string _presetNameInput = "";
    private int _selectedPreset;
    private string _settingsStatus = "";
    private MenuPage _measuredMenuPage = (MenuPage)(-1);
    private Vector2 _measuredMenuSize;
    private Vector2 _measuredMenuDisplay;
    private float _measuredMenuScale;

    /// <summary>Last frame's measured height per group box, so the backdrop can be drawn first.</summary>
    private readonly Dictionary<string, float> _boxHeights = new();
    private Vector2 _boxStart;
    private string _boxId = "";

    /// <summary>True while the menu owns input. Read by Update to stop the player walking.</summary>
    public bool SettingsModalOpen => _settingsOpen || _settingsPopupCloseRequested || LogoutUiActive;

    private GameSettings Settings => SettingsFile?.Settings ?? _fallbackSettings;
    private readonly GameSettings _fallbackSettings = GameSettings.Defaults();

    private float S => _skin?.Scale ?? 1f;

    // ── lifecycle ────────────────────────────────────────────────────────────

    private void InitSettings(GL gl)
    {
        SettingsFile ??= SettingsStore.Load(_config.RepoRoot);

        _skin = WowSkin.Load(gl, _mpq);
        _skin.Scale = Math.Clamp(_config.Window.UiScale, 0.5f, 4f);
        _skin.Textured = Settings.Display.TexturedFrame;
        // True-additive overlay for the char-select highlight. Guarded: if the shader/GL setup fails,
        // leave it null and the highlight silently falls back to the straight-alpha translucent draw.
        try { _glueAdd = new Engine.UI.GlueAdditive(gl); }
        catch (Exception ex) { _glueAdd = null; Console.WriteLine($"[glue-add] disabled: {ex.Message}"); }

        ApplySettings(Settings);
        Console.WriteLine("[settings] applied to the live renderers");
    }

    /// <summary>
    /// Escape, latched on the key's rising edge and consumed by the Gui pass.
    ///
    /// IMGUI DOES NOT CLOSE MODALS ON ESCAPE, AND THE FIRST VERSION ASSUMED IT DID.
    ///   NavUpdateCancelRequest excludes them by name:
    ///
    ///       if (g.OpenPopupStack.Size > 0 &&
    ///           !(g.OpenPopupStack.back().Window->Flags & ImGuiWindowFlags_Modal))
    ///           ClosePopupToLevel(...);
    ///
    ///   so the p_open flag handed to BeginPopupModal never goes false and the
    ///   "let ImGui's Escape reach us" plumbing could not fire even once. Escape
    ///   opened the menu and then did nothing at all. Every level of it is ours.
    ///
    /// WHY IT IS LATCHED RATHER THAN ACTED ON HERE
    ///   Update() runs outside any ImGui window, and CloseCurrentPopup is only
    ///   legal inside the popup's Begin/End scope. So the press is recorded here
    ///   and spent in DrawSettingsModal, which is inside it.
    /// </summary>
    private void UpdateSettingsInput()
    {
        // Deliberately modifier-insensitive. This preserves MSUI's current Escape contract while
        // the rising-edge latch prevents OS/key-repeat from consuming more than one layer.
        bool escape = InputKeyDown(Silk.NET.Input.Key.Escape);
        // Dev-tool pre-gate, deliberately OUTSIDE GameMenuUiLaw (the law transcribes
        // vanilla layers; an armed NPC-dev edit mode is tooling and owns Escape first).
        if (escape && !_escapeKeyDown && ConsumeDevEditEscape())
        {
            _escapeKeyDown = escape;
            return;
        }
        if (escape && !_escapeKeyDown)
        {
            GameMenuEscapePlan plan = GameMenuUiLaw.ResolveEscape(new(
                HasCarriedItem || HasActionBarCursor,
                _logoutDialog != LogoutDialogKind.None ||
                    PartyFrameUiLaw.IsPartyInviteVisible(_staticPopupSlots) ||
                    _mailConfirmation is not null || _enchantConfirmation is not null ||
                    _skillUnlearnConfirmation is not null,
                _settingsOpen && _menuPage != MenuPage.GameMenu,
                _settingsOpen && _menuPage == MenuPage.GameMenu,
                _splitContainer != InventoryUiLaw.EmptyContainer,
                _worldMapOpen || _commanderMapOpen,
                _openMailId != 0,
                _net is { IsInWorld: true } && (_autoRepeatSpell != 0 ||
                    _pendingCastSpell != 0 ||
                    _castBarPhase == CastBarPhase.Casting && _castBarSpell != 0),
                _groundCastSpell != 0 || _itemCastSpell != 0,
                _loot.IsOpen || HasPlayerPanelForEscape(),
                _selectionGuid != 0));

            if (plan.ClearCarriedCursor) ClearCarriedItemOnEscape();
            switch (plan.Layer)
            {
                case GameMenuEscapeLayer.Popup:
                    _ = TryCancelLogoutOnEscape() || TryDismissStaticPopupOnEscape() ||
                        TryDismissMailConfirmationOnEscape() ||
                        TryDismissEnchantConfirmationOnEscape() ||
                        TryDismissSkillUnlearnConfirmationOnEscape();
                    break;
                case GameMenuEscapeLayer.Options:
                case GameMenuEscapeLayer.GameMenu:
                    _escapePressed = true;
                    break;
                case GameMenuEscapeLayer.StackSplit:
                    TryCancelStackSplitOnEscape();
                    break;
                case GameMenuEscapeLayer.WorldMap:
                    _worldMapOpen = false;
                    _commanderMapOpen = false;
                    break;
                case GameMenuEscapeLayer.OpenMail:
                    CloseOpenMail(playSound: true, autoDelete: true);
                    break;
                case GameMenuEscapeLayer.SpellCast:
                    TryCancelSpellOnEscape();
                    break;
                case GameMenuEscapeLayer.SpellTargeting:
                    TryCancelSpellTargetingOnEscape();
                    break;
                case GameMenuEscapeLayer.PlayerPanel:
                    _ = TryCloseLootOnEscape() || TryClosePlayerPanelOnEscape();
                    break;
                case GameMenuEscapeLayer.Target:
                    TryClearTargetOnEscape();
                    break;
                case GameMenuEscapeLayer.OpenGameMenu:
                    _escapePressed = true;
                    break;
            }
        }
        _escapeKeyDown = escape;
    }

    private bool HasPlayerPanelForEscape() =>
        _bindingCapture is not null || _keybindingsOpen || _tradeOpen || _inspectOpen ||
        _auctionOpen || _mailOpen || _gossipMenu is not null || _gossipText is not null ||
        QuestNpcPanelNow() != QuestNpcPanel.None ||
        _vendor is not null || _trainer is not null || _gameObjectGuid != 0 || _worldMapOpen ||
        _commanderMapOpen ||
        _macroOpen || _helpOpen || _socialOpen || _guildOpen || _professionOpen || _bankOpen ||
        _tabardOpen || _taxiOpen && !_taxiLocked || _talentOpen || _questLogOpen ||
        _spellbookOpen || _characterOpen || _backpackOpen || _keyringOpen ||
        _equippedBagOpen.Any(open => open);

    private bool TryClosePlayerPanelOnEscape()
    {
        if (_bindingCapture is not null) { _bindingCapture = null; return true; }
        if (_keybindingsOpen)
        {
            if (_bindingSnapshot is not null)
            { _bindings.Clear(); foreach (var pair in _bindingSnapshot) _bindings[pair.Key] = pair.Value; }
            _bindingSnapshot = null; _keybindingsOpen = false; return true;
        }
        if (_tradeOpen) { _net?.CancelTrade(); ResetTrade(); return true; }
        if (_inspectOpen) { CloseInspect(playSound: true); return true; }
        if (_auctionOpen) { ResetAuction(); return true; }
        if (_mailOpen) { CloseMailSession(); return true; }
        if (_gossipMenu is not null || _gossipText is not null) { ResetGossip(); return true; }
        if (QuestNpcPanelNow() != QuestNpcPanel.None) { CloseQuestNpcFrame(playSound: true); return true; }
        if (_vendor is not null) { CloseVendorSession(); return true; }
        if (_trainer is not null) { _trainer = null; return true; }
        if (_gameObjectGuid != 0) { _gameObjectGuid = 0; return true; }
        if (_worldMapOpen) { _worldMapOpen = false; return true; }
        if (_commanderMapOpen) { _commanderMapOpen = false; return true; }
        if (_macroOpen) { _macroOpen = false; return true; }
        if (_helpOpen) { _helpOpen = false; return true; }
        if (_socialOpen) { _socialOpen = false; return true; }
        if (_guildOpen) { _guildOpen = false; return true; }
        if (_professionOpen) { _professionOpen = false; return true; }
        if (_bankOpen) { _bankOpen = false; return true; }
        if (_tabardOpen) { _tabardOpen = false; return true; }
        if (_taxiOpen && !_taxiLocked) { _taxiOpen = false; return true; }
        if (_talentOpen) { _talentOpen = false; return true; }
        if (_questLogOpen) { _questLogOpen = false; return true; }
        if (_spellbookOpen) { SetSpellbookOpen(false); return true; }
        if (_characterOpen) { SetCharacterPageOpen(false); return true; }
        if (CloseAllBagWindows()) return true;
        return false;
    }

    private void OpenSettings()
    {
        if (_settingsOpen) return;
        // ShowUIPanel's center ownership closes displaced panels before GameMenuFrame appears.
        // Reuse each panel's existing close path so its side effects and MSUI sounds stay intact.
        if (_splitContainer != InventoryUiLaw.EmptyContainer) CancelStackSplit();
        for (int closed = 0; closed < 32 && TryClosePlayerPanelOnEscape(); closed++) { }
        _settingsSnapshot = Settings.Clone();
        _settingsCancelling = false;
        _settingsOpen = true;
        _settingsPopupRequested = true;
        _settingsPopupCloseRequested = false;
        _menuPage = MenuPage.GameMenu;
        _settingsStatus = "";
        PlayUiSound(GameMenuUiLaw.OpenSound);
    }

    private void ToggleSettingsFromMicroButton()
    {
        if (GameMenuUiLaw.MicroToggle(_settingsOpen) == GameMenuToggleAction.Open)
        {
            OpenSettings();
            return;
        }

        _settingsOpen = false;
        // The micro button is drawn outside the popup scope. Keep one teardown frame alive so
        // DrawSettingsModal can legally pop ImGui's modal stack instead of leaving a ghost owner.
        _settingsPopupCloseRequested = true;
        PlayUiSound(GameMenuUiLaw.EscapeCloseSound);
        if (!_settingsCancelling) CommitSettings();
        _settingsCancelling = false;
    }

    // ── the frame ────────────────────────────────────────────────────────────

    /// <summary>Drawn from Gui() BEFORE the DevTools early return. See the class remarks.</summary>
    private void DrawSettingsModal()
    {
        // Escape with the menu shut opens it. Handled before OpenPopup below so
        // the popup lands on this frame rather than the next.
        //
        // A text field owns Escape while it has focus - typing a preset name and
        // hitting Escape should abandon the field, not the whole menu.
        if (_escapePressed && !_settingsOpen && !ImGui.GetIO().WantTextInput)
        {
            _escapePressed = false;
            OpenSettings();
        }

        if (_settingsPopupRequested)
        {
            _measuredMenuPage = (MenuPage)(-1);
            ImGui.OpenPopup(MenuPopupId);
            _settingsPopupRequested = false;
        }

        if (!_settingsOpen && !_settingsPopupCloseRequested)
        { _escapePressed = false; return; }

        var io = ImGui.GetIO();
        var centre = new Vector2(io.DisplaySize.X * 0.5f, io.DisplaySize.Y * 0.5f);
        var size = PageSize(io.DisplaySize);
        bool optionsPage = _menuPage != MenuPage.GameMenu;
        bool optionsEnvironmentChanged = optionsPage && _measuredMenuPage == _menuPage &&
            GameMenuUiLaw.OptionsEnvironmentChanged(
                _measuredMenuDisplay, io.DisplaySize, _measuredMenuScale, S);
        ImGuiCond optionsPlacement = optionsEnvironmentChanged
            ? ImGuiCond.Always
            : ImGuiCond.Appearing;

        ImGui.SetNextWindowPos(centre,
            optionsPage ? optionsPlacement : ImGuiCond.Always,
            new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(size, optionsPage ? optionsPlacement : ImGuiCond.Always);
        if (optionsPage)
        {
            var maximum = new Vector2(
                MathF.Max(1f, io.DisplaySize.X * GameMenuUiLaw.OptionsViewportWidth),
                MathF.Max(1f, io.DisplaySize.Y * GameMenuUiLaw.OptionsViewportHeight));
            var minimum = Vector2.Min(
                new Vector2(GameMenuUiLaw.OptionsMinWidth,
                    GameMenuUiLaw.OptionsMinHeight) * S,
                maximum);
            ImGui.SetNextWindowSizeConstraints(minimum, maximum);
        }

        _skin?.PushStyle();

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings;
        if (!optionsPage) flags |= ImGuiWindowFlags.NoResize;

        // p_open is here only so ImGui's own Escape handling reaches us. It is
        // read as "the user pressed Escape", not as "close everything" - Video
        // Options steps back to the Game Menu rather than closing.
        bool notEscaped = true;

        // A MODAL POPUP IS FILLED WITH PopupBg, NOT WindowBg.
        //   That one line cost run 3 its entire frame. PopupBg was left at the
        //   near-opaque dark fill that combo boxes and tooltips want, so ImGui
        //   painted the window solid before we drew anything, and the backdrop
        //   then composited over black instead of over the world.
        //
        //   The visible damage was not the missing translucency - it was the
        //   BORDER. The frame art is dark grey metal, so against a black fill
        //   only its highlight edge survived and the whole frame read as a thin
        //   bright hairline. Over the world it reads as heavy riveted metal,
        //   which is what it is.
        //
        //   Pushed transparent for Begin only, then popped immediately: ImGui
        //   samples the background colour once at Begin, and every nested popup
        //   after that still wants the opaque one.
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0f, 0f, 0f, 0f));
        bool showing = ImGui.BeginPopupModal(MenuPopupId, ref notEscaped, flags);
        ImGui.PopStyleColor();

        if (showing)
        {
            if (_settingsPopupCloseRequested)
            {
                _settingsPopupCloseRequested = false;
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
                _skin?.PopStyle();
                _escapePressed = false;
                return;
            }

            var min = ImGui.GetWindowPos();
            size = ImGui.GetWindowSize();
            var max = min + size;
            if (optionsPage)
                RememberPageSize(size, io.DisplaySize, optionsEnvironmentChanged);
            var dl = ImGui.GetWindowDrawList();
            bool parityProof = _uiParityArmed &&
                (_uiParityPanel == "game-menu" || _uiParityPanel == "options");
            if (parityProof)
            {
                BeginUiParityFrame(min);
                SnapshotUiParityScenario();
            }
            string parityRoot=_uiParityPanel=="options"?"OptionsFrame":"GameMenuFrame";
            Vector4 fullScreenClip = new(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
            if (parityProof)
                CollectUiParityDraw(parityRoot,"Frame",min,size,"UIParent",
                    new("",0,"IMGUI_HOST","CENTER","UIParent","CENTER",0,0,
                        ContentRect: new(min.X,min.Y,max.X,max.Y), ClipRect: fullScreenClip,
                        ClipMask: "FULL_SCREEN_FOR_FRAME_ART", Visible: true, Strata: "DIALOG"));

            // THE FRAME IS DRAWN OUTSIDE ImGui's CLIP RECT, SO THE CLIP RECT HAS
            // TO GO. Begin() leaves the window's clip rectangle inset
            // HORIZONTALLY by half of WindowPadding and VERTICALLY by only the
            // border size:
            //
            //     InnerClipRect.Min.x = InnerRect.Min.x + max(floor(pad.x/2), border)
            //     InnerClipRect.Min.y = InnerRect.Min.y + border
            //
            // At UI scale 1.8 that is 21 px on the left and right and 0 top and
            // bottom. The visible metal of a 32-px edge cell sits 9.9 to 19.8 px
            // in - entirely inside the horizontal inset and entirely outside the
            // vertical one. Which is exactly what run 4 drew: top and bottom bars
            // correct, both side bars gone, and the header plaque (which hangs
            // 21.6 px ABOVE the window) sliced off at the top.
            //
            // Nothing was wrong with the art, the slices or the tiling. The frame
            // simply is not "content", so it must not be clipped like content.
            dl.PushClipRectFullScreen();
            _skin?.DrawBackdrop(dl, min, max, WowSkin.Dialog);
            _skin?.HeaderPlaque(dl, min, size.X, PageTitle());
            Vector2 backdropMin = min + new Vector2(WowSkin.Dialog.InsetL,
                WowSkin.Dialog.InsetT) * S;
            Vector2 backdropMax = max - new Vector2(WowSkin.Dialog.InsetR,
                WowSkin.Dialog.InsetB) * S;
            bool backdropTexture = _skin?.TextureHandle(WowSkin.Dialog.Bg) != 0;
            bool edgeTexture = _skin?.TextureHandle(WowSkin.Dialog.Edge) != 0;
            if (parityProof)
            {
                string tiledUv = $"0|0|{(backdropMax.X-backdropMin.X)/(WowSkin.Dialog.TileSize*S):R}|" +
                    $"{(backdropMax.Y-backdropMin.Y)/(WowSkin.Dialog.TileSize*S):R}";
                CollectUiParityDraw(parityRoot+"/BackdropBackground","BackdropBackground",
                    backdropMin,backdropMax-backdropMin,parityRoot,
                    new(backdropTexture ? @"Interface\DialogFrame\UI-DialogBox-Background" : "",
                        backdropTexture ? 0xffffffff : ImGui.ColorConvertFloat4ToU32(WowSkin.Fill),
                        "BACKGROUND","TOPLEFT",parityRoot,"TOPLEFT",
                        WowSkin.Dialog.InsetL,-WowSkin.Dialog.InsetT,
                        TexCoords: backdropTexture ? tiledUv : "", ContentRect:
                            new(backdropMin.X,backdropMin.Y,backdropMax.X,backdropMax.Y),
                        ClipRect: fullScreenClip, ClipMask: "FULL_SCREEN_FOR_FRAME_ART",
                        BlendMode: "BLEND", Visible: true, Strata: "DIALOG"));
                CollectUiParityDraw(parityRoot+"/BackdropEdge","BackdropEdge",min,size,parityRoot,
                    new(edgeTexture ? @"Interface\DialogFrame\UI-DialogBox-Border" : "",
                        edgeTexture ? 0xffffffff : ImGui.ColorConvertFloat4ToU32(WowSkin.GoldDim),
                        "BORDER","TOPLEFT",parityRoot,"TOPLEFT",0,0,
                        TexCoords: edgeTexture ? "8-cell-nine-slice;edge=32" : "",
                        ContentRect: new(min.X,min.Y,max.X,max.Y), ClipRect: fullScreenClip,
                        ClipMask: "FULL_SCREEN_FOR_FRAME_ART", BlendMode: "BLEND",
                        Visible: true, Strata: "DIALOG"));
            }
            Vector2 headerMin=min+new Vector2(
                (size.X-GameMenuUiLaw.HeaderWidth*S)*.5f,GameMenuUiLaw.HeaderTop*S);
            Vector2 headerSize=new(GameMenuUiLaw.HeaderWidth*S,GameMenuUiLaw.HeaderHeight*S);
            string parityHeader=_uiParityPanel=="options"?"OptionsFrameHeader":"GameMenuFrameHeader";
            bool headerVisible = _skin?.TextureHandle("dialog.header") != 0;
            if (parityProof)
                CollectUiParityDraw(parityHeader,"Texture",headerMin,headerSize,parityRoot,
                    new(@"Interface\DialogFrame\UI-DialogBox-Header",0xffffffff,"ARTWORK","TOP",
                        parityRoot,"TOP",0,12,TexCoords:"0|0|1|1",
                        ContentRect:new(headerMin.X,headerMin.Y,headerMin.X+headerSize.X,
                            headerMin.Y+headerSize.Y),ClipRect:fullScreenClip,
                        ClipMask:"FULL_SCREEN_FOR_FRAME_ART",BlendMode:"BLEND",
                        Visible:headerVisible,Strata:"DIALOG"));
            string title = PageTitle();
            Vector2 titleSize = ImGui.CalcTextSize(title);
            Vector2 titleMin = new(min.X + size.X*.5f-titleSize.X*.5f,
                headerMin.Y+GameMenuUiLaw.HeaderTitleTop*S);
            string parityTitle=_uiParityPanel=="options"?"OptionsFrameTitle":"GameMenuFrameTitle";
            if (parityProof)
                CollectUiParityDraw(parityTitle,"FontString",titleMin,titleSize,parityRoot,
                    new("",ImGui.ColorConvertFloat4ToU32(WowSkin.Gold),"ARTWORK","TOP",
                        parityRoot,"TOP",0,GameMenuUiLaw.HeaderTitleTop+GameMenuUiLaw.HeaderTop,
                        FontFace.FrizQt,ImGui.GetFontSize()/MathF.Max(S,.001f),
                        ContentRect:new(titleMin.X,titleMin.Y,titleMin.X+titleSize.X,
                            titleMin.Y+titleSize.Y),ClipRect:fullScreenClip,
                        ClipMask:"FULL_SCREEN_FOR_FRAME_ART",BlendMode:"BLEND",
                        Visible:true,Strata:"DIALOG"));
            dl.PopClipRect();

            // The plaque hangs 12 above the frame and its VISIBLE metal ends about
            // 23 below the frame top - the 256x64 art is mostly transparent
            // padding. Blizzard puts the first game-menu button's centre at 37,
            // i.e. its top at 26.5, so 30 clears the plaque with a hair to spare.
            ImGui.SetCursorPosY(30f * S);

            switch (_menuPage)
            {
                case MenuPage.GameMenu: DrawGameMenu(size); break;
                case MenuPage.Video: DrawVideoOptions(size); break;
                case MenuPage.Controls: DrawControlsPage(size); break;
            }
            MarkUiParityFrameComplete();

            // Spent HERE, inside Begin/End, because that is the only scope in
            // which CloseCurrentPopup is legal. Consumed after the page has
            // drawn so a text field that took focus this frame gets first refusal.
            if (_escapePressed && !ImGui.GetIO().WantTextInput)
            {
                _escapePressed = false;
                HandleEscape();
            }

            ImGui.EndPopup();
        }

        if (!showing) _settingsPopupCloseRequested = false;

        _skin?.PopStyle();

        // A held slider re-scatters on release, not on every frame of the drag.
        if (_clutterRescatterPending && !ImGui.IsAnyItemActive())
        {
            _clutterRescatterPending = false;
            _foliage?.ForceRescatter();
        }

        // notEscaped is ignored on purpose: ImGui never clears it for a modal.
        // It exists only because BeginPopupModal has no (name, flags) overload.
        _escapePressed = false;
    }

    /// <summary>
    /// Escape steps back one level, exactly as it does in the real client:
    /// Video Options -> Game Menu -> gone. Only ever called from inside the
    /// popup's Begin/End scope, which is what makes CloseCurrentPopup legal.
    /// </summary>
    private void HandleEscape()
    {
        if (_menuPage != MenuPage.GameMenu)
        {
            // Back to the Game Menu. The popup has to be closed and reopened
            // rather than just repainted, because the two pages are different
            // sizes and SetNextWindowSize only takes effect on a fresh Begin.
            _menuPage = MenuPage.GameMenu;
            _settingsPopupRequested = true;
            ImGui.CloseCurrentPopup();
            return;
        }

        _settingsOpen = false;
        ImGui.CloseCurrentPopup();
        PlayUiSound(GameMenuUiLaw.EscapeCloseSound);

        if (!_settingsCancelling) CommitSettings();
        _settingsCancelling = false;
    }

    private string PageTitle() => _menuPage switch
    {
        MenuPage.Video => "Video Options",
        MenuPage.Controls => "Interface Options",
        _ => "Main Menu",
    };

    /// <summary>
    /// GameMenuFrame is 195x246 in vanilla with eight buttons. Ours is derived
    /// from the same constants so it stays right when a button is added.
    /// OptionsFrame starts at 450x575; Video and Interface Options remember the
    /// player's independently resized dimensions while their bodies keep scrolling.
    /// </summary>
    private Vector2 PageSize(Vector2 display)
    {
        if (_menuPage == MenuPage.GameMenu)
        {
            // patch.MPQ's build-5875 GameMenuFrame.xml is authoritative: 195x246.
            return new Vector2(GameMenuUiLaw.FrameWidth, GameMenuUiLaw.FrameHeight) * S;
        }

        // FrameXML's OptionsFrame supplies the first-open size. From then on each
        // options page owns its remembered size, still clamped to this viewport.
        var layout = Settings.MenuLayout ??= new GameSettings.MenuLayoutSettings();
        var logical = _menuPage == MenuPage.Video
            ? new Vector2(layout.VideoWidth, layout.VideoHeight)
            : new Vector2(layout.ControlsWidth, layout.ControlsHeight);
        return GameMenuUiLaw.ResolveOptionsSize(logical, S, display);
    }

    private void RememberPageSize(
        Vector2 physicalSize, Vector2 display, bool environmentReflowed)
    {
        // Establish an appearance baseline without rewriting settings. Thereafter only
        // a real live-size change (resize grip or viewport constraint) updates persistence;
        // an Interface-scale edit by itself does not silently reinterpret this window.
        if (_measuredMenuPage != _menuPage || environmentReflowed)
        {
            _measuredMenuPage = _menuPage;
            _measuredMenuSize = physicalSize;
            _measuredMenuDisplay = display;
            _measuredMenuScale = S;
            return;
        }
        _measuredMenuDisplay = display;
        _measuredMenuScale = S;
        if (Vector2.DistanceSquared(_measuredMenuSize, physicalSize) < .25f) return;
        _measuredMenuSize = physicalSize;

        Vector2 logical = GameMenuUiLaw.ToLogicalOptionsSize(physicalSize, S);
        var layout = Settings.MenuLayout ??= new GameSettings.MenuLayoutSettings();
        if (_menuPage == MenuPage.Video)
        {
            layout.VideoWidth = logical.X;
            layout.VideoHeight = logical.Y;
        }
        else if (_menuPage == MenuPage.Controls)
        {
            layout.ControlsWidth = logical.X;
            layout.ControlsHeight = logical.Y;
        }
    }

    // ── the Game Menu ────────────────────────────────────────────────────────

    private void DrawGameMenu(Vector2 size)
    {
        var button = new Vector2(GameMenuUiLaw.ButtonWidth, GameMenuUiLaw.ButtonHeight) * S;
        float x = (size.X - button.X) * 0.5f;
        bool parityProof = _uiParityArmed && _uiParityPanel == "game-menu";
        Vector2 frameMin = ImGui.GetWindowPos();
        Vector2 frameMax = frameMin + ImGui.GetWindowSize();
        Vector4 frameClip = new(frameMin.X, frameMin.Y, frameMax.X, frameMax.Y);

        void Row(string id, string label, float y, string point, string relativeTo,
            string relativePoint, string offsetY, bool enabled, Action onClick, string? tip = null)
        {
            ImGui.SetCursorPos(new Vector2(x, y * S));
            Vector2 actualMin = ImGui.GetCursorScreenPos();
            float authoredOffsetY = float.Parse(offsetY,
                System.Globalization.CultureInfo.InvariantCulture);
            bool pressed;
            WowSkin.PanelButtonDrawState? drawState = null;
            if (_skin is not null)
            {
                pressed = _skin.PanelButton(label, button, enabled, out var state);
                drawState = state;
            }
            else pressed = enabled && ImGui.Button(label, button);

            if (parityProof && drawState is { } drawn)
            {
                CollectUiParityDraw(id,"Button",drawn.Min,drawn.Size,"GameMenuFrame",
                    new("",0,"HIT_TARGET",point,relativeTo,relativePoint,0,authoredOffsetY,
                        ContentRect:new(drawn.Min.X,drawn.Min.Y,drawn.Min.X+drawn.Size.X,
                            drawn.Min.Y+drawn.Size.Y),ClipRect:frameClip,
                        ClipMask:"ImGui-window",Visible:true,Enabled:drawn.Enabled,
                        InteractionState:drawn.InteractionState,HitMin:drawn.Min,
                        HitMax:drawn.Min+drawn.Size,Strata:"DIALOG"));
                if (drawn.StateTexturePath.Length > 0)
                    CollectUiParityDraw(id+"/"+drawn.StateTextureRole,drawn.StateTextureRole,
                        drawn.Min,drawn.Size,id,
                        new(drawn.StateTexturePath,0xffffffff,"ARTWORK","CENTER",id,"CENTER",0,0,
                            TexCoords:$"{drawn.StateUvMin.X:R}|{drawn.StateUvMin.Y:R}|"+
                                $"{drawn.StateUvMax.X:R}|{drawn.StateUvMax.Y:R}",
                            ContentRect:new(drawn.Min.X,drawn.Min.Y,drawn.Min.X+drawn.Size.X,
                                drawn.Min.Y+drawn.Size.Y),ClipRect:frameClip,
                            ClipMask:"ImGui-window",BlendMode:"BLEND",Visible:true,
                            InteractionState:drawn.InteractionState,Strata:"DIALOG"));
                Vector2 pushedOffset = drawn.Held ? new Vector2(1f,1f) : Vector2.Zero;
                CollectUiParityDraw(id+"/Label","FontString",drawn.TextMin,drawn.TextSize,id,
                    new("",drawn.TextColor,"OVERLAY","CENTER",id,"CENTER",
                        pushedOffset.X,pushedOffset.Y,FontFace.FrizQt,
                        ImGui.GetFontSize()/MathF.Max(S,.001f),
                        ContentRect:new(drawn.TextMin.X,drawn.TextMin.Y,
                            drawn.TextMin.X+drawn.TextSize.X,drawn.TextMin.Y+drawn.TextSize.Y),
                        ClipRect:frameClip,ClipMask:"ImGui-window",BlendMode:"BLEND",
                        Visible:true,Enabled:drawn.Enabled,
                        InteractionState:drawn.InteractionState,Strata:"DIALOG"));
                Vector2 shadowMin = drawn.TextMin + Vector2.One;
                CollectUiParityDraw(id+"/LabelShadow","FontString",shadowMin,drawn.TextSize,id,
                    new("",ImGui.ColorConvertFloat4ToU32(WowSkin.Shadow),"OVERLAY","CENTER",
                        id,"CENTER",pushedOffset.X+1,pushedOffset.Y+1,FontFace.FrizQt,
                        ImGui.GetFontSize()/MathF.Max(S,.001f),
                        ContentRect:new(shadowMin.X,shadowMin.Y,shadowMin.X+drawn.TextSize.X,
                            shadowMin.Y+drawn.TextSize.Y),ClipRect:frameClip,
                        ClipMask:"ImGui-window",BlendMode:"BLEND",Visible:true,
                        InteractionState:drawn.InteractionState,Strata:"DIALOG"));
                if (drawn.HighlightVisible)
                    CollectUiParityDraw(id+"/HighlightTexture","HighlightTexture",drawn.Min,
                        drawn.Size,id,
                        new(drawn.HighlightTexturePath,
                            ImGui.ColorConvertFloat4ToU32(new Vector4(1,1,1,
                                GameMenuUiLaw.HighlightAlpha)),"HIGHLIGHT","CENTER",id,"CENTER",0,0,
                            TexCoords:$"{drawn.StateUvMin.X:R}|{drawn.StateUvMin.Y:R}|"+
                                $"{drawn.StateUvMax.X:R}|{drawn.StateUvMax.Y:R}",
                            ContentRect:new(drawn.Min.X,drawn.Min.Y,drawn.Min.X+drawn.Size.X,
                                drawn.Min.Y+drawn.Size.Y),ClipRect:frameClip,
                            ClipMask:"ImGui-window",BlendMode:"BLEND",Visible:true,
                            InteractionState:"highlighted",Strata:"DIALOG"));
            }
            if (pressed) onClick();
            if (tip is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        }

        Row("GameMenuButtonOptions", "Video Options", GameMenuUiLaw.ButtonTop(0), "CENTER", "", "TOP", "-37",
            true, () => { PlayUiSound("igMainMenuOption"); Go(MenuPage.Video); });
        Row("GameMenuButtonSoundOptions", "Sound Options", GameMenuUiLaw.ButtonTop(1), "TOP", "GameMenuButtonOptions", "BOTTOM", "-1",
            false, () => { },
            "There is no sound subsystem yet - this button exists so the menu does\n" +
            "not change shape when there is.");
        Row("GameMenuButtonUIOptions", "Interface Options", GameMenuUiLaw.ButtonTop(2), "TOP", "GameMenuButtonSoundOptions", "BOTTOM", "-1",
            true, () => { PlayUiSound("igMainMenuOption"); Go(MenuPage.Controls); });
        Row("GameMenuButtonKeybindings", "Key Bindings", GameMenuUiLaw.ButtonTop(3), "TOP", "GameMenuButtonUIOptions", "BOTTOM", "-1",
            true, () =>
            {
                PlayUiSound("igMainMenuOption");
                if (!_settingsCancelling) CommitSettings();
                _settingsCancelling = false;
                _settingsOpen = false;
                ImGui.CloseCurrentPopup();
                OpenKeybindings();
            });
        Row("GameMenuButtonMacros", "Macros", GameMenuUiLaw.ButtonTop(4), "TOP", "GameMenuButtonKeybindings", "BOTTOM", "-1",
            true, () =>
            {
                PlayUiSound("igMainMenuOption");
                if (!_settingsCancelling) CommitSettings();
                _settingsCancelling = false;
                _settingsOpen = false;
                ImGui.CloseCurrentPopup();
                OpenMacros();
            });
        Row("GameMenuButtonLogout", "Logout", GameMenuUiLaw.ButtonTop(5), "TOP", "GameMenuButtonMacros", "BOTTOM", "-1",
            _net is { IsInWorld: true } && !LogoutUiActive, () => RequestLogout(quitting: false));

        // NOT _window.Close() - that runs the whole teardown synchronously and
        // the rest of this ImGui frame then draws into freed memory. Flag it and
        // let Update act between frames. See ConsumeQuitRequest.
        Row("GameMenuButtonQuit", "Exit Game", GameMenuUiLaw.ButtonTop(6), "TOP", "GameMenuButtonLogout", "BOTTOM", "-1",
            !LogoutUiActive, () => RequestLogout(quitting: true));
        Row("GameMenuButtonContinue", "Return to Game", GameMenuUiLaw.ButtonTop(7), "TOP", "GameMenuButtonQuit", "BOTTOM", "-16", true, () =>
        {
            PlayUiSound("igMainMenuContinue");
            if (!_settingsCancelling) CommitSettings();
            _settingsCancelling = false;
            _settingsOpen = false;
            ImGui.CloseCurrentPopup();
        });

    }

    private void Go(MenuPage page)
    {
        _menuPage = page;
        _settingsPopupRequested = true;   // resize needs a fresh open
        ImGui.CloseCurrentPopup();
    }

    // ── group boxes ──────────────────────────────────────────────────────────
    //
    // A box's backdrop has to be drawn BEFORE its contents to land behind them,
    // which means knowing the height before the contents exist. Rather than split
    // the draw list into channels - an API whose shape has moved between ImGui
    // releases - the height is remembered from last frame. The only artefact is
    // one frame of wrong height when a drill-down opens, and it self-corrects.

    private void BeginBox(string id, string caption)
    {
        var dl = ImGui.GetWindowDrawList();

        if (!string.IsNullOrEmpty(caption))
        {
            // White, not gold. In the real Video Options frame the box captions
            // ("Display", "World Appearance") are the plain face; only the
            // control labels inside them are yellow.
            var at = ImGui.GetCursorScreenPos();
            dl.AddText(at, ImGui.ColorConvertFloat4ToU32(WowSkin.Normal), caption);
            ImGui.Dummy(new Vector2(1f, ImGui.GetTextLineHeight()));
        }

        _boxId = id;
        _boxStart = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        float height = _boxHeights.TryGetValue(id, out float h) ? h : ImGui.GetFrameHeight() * 2f;

        _skin?.DrawBackdrop(dl, _boxStart, _boxStart + new Vector2(width, height), WowSkin.Tooltip);

        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(1f, 8f * S));
        ImGui.Indent(12f * S);
    }

    private void EndBox()
    {
        ImGui.Unindent(12f * S);
        ImGui.Dummy(new Vector2(1f, 8f * S));
        ImGui.EndGroup();

        _boxHeights[_boxId] = ImGui.GetItemRectMax().Y - _boxStart.Y;
        ImGui.Dummy(new Vector2(1f, 10f * S));
    }

    // ── Video Options ────────────────────────────────────────────────────────

    private void DrawVideoOptions(Vector2 size)
    {
        var s = Settings;

        float bodyHeight = PanelBodyHeight(presets: true);

        if (ImGui.BeginChild("##video-body", new Vector2(0f, bodyHeight)))
        {

            BeginBox("quality", "Quality");
            {
                // Five buttons and four gaps across whatever the box actually has.
                float row = ControlWidth();
                var qButton = new Vector2((row - 4f * 8f * S) / 5f,
                                          WowSkin.ButtonArt.Y * S * 1.1f);
                for (int i = 0; i < GameSettings.QualityNames.Length; i++)
                {
                    if (i > 0) ImGui.SameLine();
                    string name = GameSettings.QualityNames[i];
                    if (Button(name + "##quality", qButton))
                    {
                        s.ApplyQuality(name);
                        ApplySettings(s);
                        _settingsStatus = $"quality set to {name}";
                    }
                }
                ImGui.TextDisabled($"current: {s.ActivePreset}");
            }
            EndBox();

            BeginBox("display", "Display");
            {
                Check("Fullscreen", () => s.Display.Fullscreen,
                    v => { s.Display.Fullscreen = v; _window.Fullscreen = v; },
                    "True fullscreen at the desktop resolution. Alt+Enter toggles it any time.");

                Check("VSync", () => s.Display.VSync, v => { s.Display.VSync = v; _window.VSync = v; },
                    "Caps the frame rate to the monitor and stops tearing. Turning it off is a\n" +
                    "DIAGNOSTIC as much as a preference - SYSTEM_STREAMING.md section 5A.17.");

                Check("Multisampling", () => s.Display.MultisamplingEnabled,
                    v => { s.Display.MultisamplingEnabled = v; _window.MultisamplingEnabled = v; },
                    $"The GL enable. This run's framebuffer has {_window.FramebufferSamples}x samples;\n" +
                    "the sample COUNT below needs a restart. On Iris Xe 4x cost 5-7 FPS in\n" +
                    "Trade District, which is why the default is a true 1x buffer.");

                Check("Textured frame (Blizzard UI art)", () => s.Display.TexturedFrame,
                    v => { s.Display.TexturedFrame = v; if (_skin is not null) _skin.Textured = v; },
                    "Off draws a plain panel instead of the Interface\\ BLPs.");

                if (Slider("uiscale", "Interface scale", () => s.Display.UiScale,
                        v => s.Display.UiScale = v, 0.5f, 3f, "x{0:F2}"))
                {
                    // Through the window's law, NOT a raw FontGlobalScale assignment:
                    // the atlas is supersampled and overwriting its compensation scales
                    // this very menu off the screen. See ClientWindow.ApplyUiFontScale.
                    float v = Math.Clamp(s.Display.UiScale, 0.5f, 4f);
                    _window.ApplyUiFontScale(v, s.Display.FontScale);
                    if (_skin is not null) _skin.Scale = v;
                }

                if (Slider("fontscale", "Font size", () => s.Display.FontScale,
                        v => s.Display.FontScale = v, 0.5f, 3f, "x{0:F2}",
                        "Text size on its own - panels and buttons stay put, only the type\n" +
                        "grows. For a 4K panel where the interface is the right size but the\n" +
                        "words are too small to read. Stays crisp all the way to 3.00x,\n" +
                        "because the typeface is rasterised that much larger than it draws."))
                {
                    _window.ApplyUiFontScale(
                        Math.Clamp(s.Display.UiScale, 0.5f, 4f), s.Display.FontScale);
                }

                if (ImGui.TreeNode("Advanced##display"))
                {
                    Restart();
                    IntSlider("winw", "Window width", () => s.Display.WindowWidth,
                        v => s.Display.WindowWidth = v, 640, 3840);
                    IntSlider("winh", "Window height", () => s.Display.WindowHeight,
                        v => s.Display.WindowHeight = v, 480, 2160);
                    IntSlider("msaa", "Multisample count", () => s.Display.MsaaSamples,
                        v => s.Display.MsaaSamples = v, 1, 16);
                    Slider("aniso", "Anisotropic filtering", () => s.Display.Anisotropy,
                        v => s.Display.Anisotropy = v, 1f, 16f, "{0:F0}x");
                    ImGui.TreePop();
                }
            }
            EndBox();

            BeginBox("painterly", "Painterly mode");
            {
                // Neither of these invalidates art by hand: Check calls
                // ApplySettings, which refreshes cached painterly art whenever
                // the state it was baked from moved. Doing it here as well
                // double-bumped the epoch and hid that fact.
                Check("Painterly world", () => s.Display.Painterly,
                    v =>
                    {
                        s.Display.Painterly = v;
                        if (_painterly is not null) _painterly.Enabled = v;
                    },
                    "A restrained crisp-flat treatment: it keeps the original textures\n" +
                    "and colour while lightly organising broad values and silhouettes.\n" +
                    "The HUD has its own switch below.");

                Check("Painterly HUD", () => s.Display.PainterlyUi,
                    v => s.Display.PainterlyUi = v,
                    "Uses the square painted frames and styled icon copies independently\n" +
                    "from the world effect. Turn it off for clean world-only comparisons.");

                Slider("pbands", "Colour bands", () => s.Display.PainterlyBands,
                    v => { s.Display.PainterlyBands = v; if (_painterly is not null) _painterly.Bands = v; },
                    3f, 24f, "{0:F0}",
                    "How many steps are available in the flattened broad lighting. The\n" +
                    "strength control below decides how visibly those steps replace the\n" +
                    "authored values.");

                Slider("pbandstrength", "Value flattening", () => s.Display.PainterlyBandStrength,
                    v =>
                    {
                        s.Display.PainterlyBandStrength = v;
                        if (_painterly is not null) _painterly.BandStrength = v;
                    },
                    0f, 1f, "{0:F2}",
                    "How strongly the stepped values replace the original broad lighting.\n" +
                    "0 preserves it, 1 fully posterises it; crisp-flat uses a restrained\n" +
                    "blend so textures stay readable without looking photographic.");

                Slider("pdetail", "Detail", () => s.Display.PainterlyDetail,
                    v => { s.Display.PainterlyDetail = v; if (_painterly is not null) _painterly.Detail = v; },
                    0f, 2f, "{0:F2}",
                    "How much source texture survives. 1 keeps the original amount; 0\n" +
                    "removes fine texture, while values above 1 sharpen it and can make\n" +
                    "a high-resolution scene busy.");

                Slider("pink", "Ink lines", () => s.Display.PainterlyInk,
                    v => { s.Display.PainterlyInk = v; if (_painterly is not null) _painterly.Ink = v; },
                    0f, 1f, "{0:F2}",
                    "Gently darkens strong colour boundaries. Keep this low when the\n" +
                    "original painted textures and classic shadows already separate\n" +
                    "objects; 0 turns the extra ink off entirely.");

                Slider("psil", "Silhouettes", () => s.Display.PainterlySilhouette,
                    v => { s.Display.PainterlySilhouette = v; if (_painterly is not null) _painterly.Silhouette = v; },
                    0f, 1f, "{0:F2}",
                    "Adds a line where depth breaks, helping a dark figure separate from\n" +
                    "a dark background. Modest values aid readability; high values can\n" +
                    "make characters and props look like cut-out stickers.");

                Slider("pdepth", "Distance calm", () => s.Display.PainterlyDepthFade,
                    v => { s.Display.PainterlyDepthFade = v; if (_painterly is not null) _painterly.DepthFade = v; },
                    0f, 1f, "{0:F2}",
                    "Eases generated outlines, dither and grain off with distance. Authored\n" +
                    "texture stays crisp; 0 treats every distance the same.");

                Slider("pvalue", "Light and shade", () => s.Display.PainterlyContrast,
                    v => { s.Display.PainterlyContrast = v; if (_painterly is not null) _painterly.Contrast = v; },
                    0f, 1f, "{0:F2}",
                    "Blends in an extra light/dark S-curve before the value steps. 0 is\n" +
                    "the source lighting; the crisp-flat baseline stays close to it.");

                Slider("plift", "Brightness", () => s.Display.PainterlyLift,
                    v => { s.Display.PainterlyLift = v; if (_painterly is not null) _painterly.Lift = v; },
                    0.5f, 2f, "{0:F2}",
                    "Opens up the midtones without clipping anything - blacks stay black\n" +
                    "and highlights stay put. Raise it when the styling reads as murk;\n" +
                    "1.00 leaves the source brightness alone.");

                Slider("psat", "Colour richness", () => s.Display.PainterlySaturation,
                    v => { s.Display.PainterlySaturation = v; if (_painterly is not null) _painterly.Saturation = v; },
                    0f, 2f, "{0:F2}",
                    "Saturation. 1.00 leaves the source colour alone.");

                if (ImGui.TreeNode("Advanced##painterly"))
                {
                    Slider("pwarm", "Sun/shade colour", () => s.Display.PainterlyWarmth,
                        v => { s.Display.PainterlyWarmth = v; if (_painterly is not null) _painterly.Warmth = v; },
                        0f, 1f, "{0:F2}",
                        "Split tone: pushes lit surfaces warm and shadows cool. 0 preserves\n" +
                        "the authored source colour; crisp-flat uses only a slight tint.");

                    Slider("pinkgate", "Ink threshold", () => s.Display.PainterlyInkThreshold,
                        v => { s.Display.PainterlyInkThreshold = v; if (_painterly is not null) _painterly.InkThreshold = v; },
                        0.01f, 0.5f, "{0:F2}",
                        "How strong an edge must be before it inks. Raise it if textured\n" +
                        "ground starts drawing lines; lower it if silhouettes are not\n" +
                        "separating from their background.");

                    Slider("pcalmstart", "Calm starts", () => s.Display.PainterlyCalmStart,
                        v =>
                        {
                            s.Display.PainterlyCalmStart = MathF.Min(v, s.Display.PainterlyCalmEnd - 1f);
                            if (_painterly is not null) _painterly.CalmStart = s.Display.PainterlyCalmStart;
                        },
                        5f, 300f, "{0:F0} yd",
                        "World distance where generated outlines and texture marks begin to settle.");

                    Slider("pcalmend", "Calm completes", () => s.Display.PainterlyCalmEnd,
                        v =>
                        {
                            s.Display.PainterlyCalmEnd = MathF.Max(v, s.Display.PainterlyCalmStart + 1f);
                            if (_painterly is not null) _painterly.CalmEnd = s.Display.PainterlyCalmEnd;
                        },
                        10f, 600f, "{0:F0} yd",
                        "World distance where generated-mark calming reaches full strength.");

                    Slider("pdither", "Band dither", () => s.Display.PainterlyDither,
                        v => { s.Display.PainterlyDither = v; if (_painterly is not null) _painterly.Dither = v; },
                        0f, 1f, "{0:F2}",
                        "A stable pattern that hides contour rings at low band counts. The\n" +
                        "crisp-flat profile uses only a trace; raise it when choosing much\n" +
                        "stronger flattening.");

                    Check("Native-resolution world canvas",
                        () => s.Display.PainterlyCanvasHeight == 0,
                        v =>
                        {
                            s.Display.PainterlyCanvasHeight = v ? 0 : 1440;
                            if (_painterly is not null)
                                _painterly.CanvasHeight = s.Display.PainterlyCanvasHeight;
                        },
                        "On styles every physical pixel. The crisp-flat baseline leaves this\n" +
                        "off so the world can use a clean near-integer pixel scale while the\n" +
                        "HUD remains native and crisp.");
                    if (s.Display.PainterlyCanvasHeight > 0)
                    {
                        IntSlider("pcanvas", "World canvas height",
                            () => s.Display.PainterlyCanvasHeight,
                            v =>
                            {
                                s.Display.PainterlyCanvasHeight = v;
                                if (_painterly is not null) _painterly.CanvasHeight = v;
                            },
                            720, 2160,
                            "1440 is the crisp-flat cap: the renderer picks a nearby exact\n" +
                            "pixel scale when possible (for example 1200p at 3840x2400).\n" +
                            "This affects the styled world only, never text or HUD art.");
                    }

                    Slider("pgrain", "Canvas grain", () => s.Display.PainterlyGrain,
                        v => { s.Display.PainterlyGrain = v; if (_painterly is not null) _painterly.Grain = v; },
                        0f, 1f, "{0:F2}",
                        "Paper tooth over the finished image. 0 preserves a clean source\n" +
                        "image and is the crisp-flat baseline.");
                    ImGui.TreePop();
                }
            }
            EndBox();

            BeginBox("view", "View distance");
            {
                if (Slider("viewdist", "View distance", () => s.View.DistancePercent,
                        v => { s.View.DistancePercent = v; s.View.DistanceCustom = false; },
                        0f, 100f, "{0:F0}%",
                        "Moves fog, building distance and the far plane together. Vanilla's\n" +
                        "unpatched farclip ceiling was 777 yards, about 42% here."))
                {
                    s.ResolveViewDistance();
                    MarkCustomPreset();
                }
                if (s.View.DistanceCustom) ImGui.TextDisabled("  (custom - drag to take back over)");

                Slider("fov", "Field of view", () => s.View.FieldOfView,
                    v => { s.View.FieldOfView = v; _window.Camera.FieldOfViewDegrees = v; },
                    30f, 110f, "{0:F0} deg");

                if (ImGui.TreeNode("Advanced##view"))
                {
                    Check("Draw distance fog", () => s.View.FogEnabled,
                        v => { s.View.FogEnabled = v; _atmosphere.FogEnabled = v; });
                    if (Slider("fogs", "Fog starts", () => s.View.FogStart, v => s.View.FogStart = v,
                            0f, 1500f, "{0:F0} yd")) CustomiseView();
                    if (Slider("foge", "Fog fully opaque", () => s.View.FogEnd, v => s.View.FogEnd = v,
                            100f, 2000f, "{0:F0} yd")) CustomiseView();
                    Check("Stop submitting past fog", () => s.View.CullAtFogEnd,
                        v => { s.View.CullAtFogEnd = v; _atmosphere.CullAtFogEnd = v; });
                    Check("Match camera far plane to fog", () => s.View.CoupleFarPlaneToFog,
                        v => { s.View.CoupleFarPlaneToFog = v; _coupleFarPlaneToFog = v; });
                    if (Slider("bdist", "Building distance", () => s.View.BuildingDistance,
                            v => s.View.BuildingDistance = v, 300f, 1250f, "{0:F0} yd")) CustomiseView();
                    if (Slider("far", "Far plane", () => s.View.FarPlane, v => s.View.FarPlane = v,
                            500f, 4000f, "{0:F0} yd")) CustomiseView();
                    Slider("near", "Near plane", () => s.View.NearPlane,
                        v => { s.View.NearPlane = v; _window.Camera.NearPlane = v; }, 0.01f, 2f, "{0:F2} yd");
                    ImGui.TreePop();
                }
            }
            EndBox();

            BeginBox("detail", "Environment detail");
            {
                if (Slider("objdet", "Object detail", () => s.Detail.ObjectDetailPercent,
                        v => { s.Detail.ObjectDetailPercent = v; s.Detail.ObjectDetailCustom = false; },
                        0f, 100f, "{0:F0}%",
                        "Trees, rocks, fences and furniture - about 785 placements per tile, so\n" +
                        "the single biggest change to how the world looks AND to load cost."))
                {
                    s.ResolveObjectDetail();
                    MarkCustomPreset();
                }

                if (Slider("blddet", "Building detail", () => s.Detail.BuildingDetailPercent,
                        v => { s.Detail.BuildingDetailPercent = v; s.Detail.BuildingDetailCustom = false; },
                        0f, 100f, "{0:F0}%",
                        "How aggressively distant city geometry becomes low-poly shells. Does\n" +
                        "NOT move building distance - that belongs to view distance."))
                {
                    s.ResolveBuildingDetail();
                    MarkCustomPreset();
                }

                if (ImGui.TreeNode("Advanced - doodads##detail"))
                {
                    Check("Draw doodads", () => s.Detail.Doodads, v => s.Detail.Doodads = v);
                    if (Slider("ddist", "Doodad distance", () => s.Detail.DoodadDistance,
                            v => s.Detail.DoodadDistance = v, 50f, 1200f, "{0:F0} yd")) CustomiseObjects();
                    if (Check("Stream only nearby doodads", () => s.Detail.DoodadDemandStreaming,
                            v => s.Detail.DoodadDemandStreaming = v,
                            "Parses and uploads M2s as you approach instead of up front. Cuts\n" +
                            "startup hard; costs a little pop-in.")) CustomiseObjects();
                    Check("GPU instancing", () => s.Detail.DoodadInstancing, v => s.Detail.DoodadInstancing = v,
                        "One instanced draw per model batch instead of one per copy. Off is the\n" +
                        "legacy path and is kept only for A/B.");
                    Check("Frustum culling##doodads", () => s.Detail.DoodadFrustumCulling,
                        v => s.Detail.DoodadFrustumCulling = v);
                    Check("Flat cull bounds", () => s.Detail.DoodadFlatCullBounds,
                        v => s.Detail.DoodadFlatCullBounds = v,
                        "Struct-of-arrays bounds for the cull loop: 55.8 ms -> 0.3 ms on a\n" +
                        "crossing frame, though SYSTEM_STREAMING.md 5A.15 records the A/B is\n" +
                        "not clean yet. Leave it on.");
                    Slider("dcut", "Doodad alpha cut", () => s.Detail.DoodadAlphaCutoff,
                        v => s.Detail.DoodadAlphaCutoff = v, 0f, 1f, "{0:F2}");
                    ImGui.TreePop();
                }

                if (ImGui.TreeNode("Advanced - buildings##detail"))
                {
                    Check("Draw buildings", () => s.Detail.Buildings, v => s.Detail.Buildings = v);
                    Check("Frustum culling##wmo", () => s.Detail.WmoFrustumCulling,
                        v => s.Detail.WmoFrustumCulling = v);
                    Check("Swap distance-only city shells", () => s.Detail.DistanceLodShells,
                        v => s.Detail.DistanceLodShells = v,
                        "Stormwind's cathedral and entrance silhouettes are distance shells:\n" +
                        "visible on approach, absent inside. Runtime-check both.");
                    Check("Force two-sided", () => s.Detail.ForceTwoSided, v => s.Detail.ForceTwoSided = v,
                        "If missing walls reappear when this is on, the geometry was never lost -\n" +
                        "it was wound inward and culled.");
                    Slider("wcut", "Building alpha cutoff", () => s.Detail.WmoAlphaCutoff,
                        v => s.Detail.WmoAlphaCutoff = v, 0f, 1f, "{0:F2}");
                    if (IntSlider("imp", "Impostor max verts", () => s.Detail.ImpostorMaxVertices,
                            v => s.Detail.ImpostorMaxVertices = v, 0, 6000,
                            "Groups under this vertex count become distance-only shells.\n" +
                            "Reclassifies the whole city live - no reload.")) CustomiseBuildings();
                    Slider("insm", "Inside margin", () => s.Detail.InsideMargin,
                        v => s.Detail.InsideMargin = v, -400f, 400f, "{0:F0} yd");
                    if (Slider("icull", "Interior cull (from outside)", () => s.Detail.InteriorCullDistance,
                            v => s.Detail.InteriorCullDistance = v, 20f, 800f, "{0:F0} yd")) CustomiseBuildings();
                    if (Slider("guard", "Shell near-guard", () => s.Detail.ShellNearGuard,
                            v => s.Detail.ShellNearGuard = v, 0f, 600f, "{0:F0} yd")) CustomiseBuildings();
                    if (Check("Occlusion cull exterior (BVH)", () => s.Detail.OcclusionCulling,
                            v => s.Detail.OcclusionCulling = v,
                            "Hides exterior groups fully behind geometry. Only culls when EVERY\n" +
                            "corner is blocked, so it does nothing across an open courtyard -\n" +
                            "that is the known ceiling, not a bug.")) CustomiseBuildings();
                    Slider("occd", "Occlusion min distance", () => s.Detail.OcclusionMinDistance,
                        v => s.Detail.OcclusionMinDistance = v, 10f, 400f, "{0:F0} yd");
                    ImGui.TreePop();
                }
            }
            EndBox();

            BeginBox("clutter", "Ground clutter");
            {
                Check("Show grass and ground effects", () => s.Clutter.Enabled, v => s.Clutter.Enabled = v);
                Slider("cdens", "Clutter density", () => s.Clutter.Density, v => s.Clutter.Density = v,
                    0f, 4f, "x{0:F2}",
                    "Multiplies the density GroundEffectTexture.dbc authored per texture layer.");
                Slider("crad", "Clutter distance", () => s.Clutter.Radius, v => s.Clutter.Radius = v,
                    5f, 120f, "{0:F0} yd");

                // The distance slider scatters grass; the FADE window decides how
                // far of it you can actually see, and the two are separate values.
                // FoliageRenderer's own note: "THIS DEFAULTS ON BECAUSE THE
                // SLIDERS LIE OTHERWISE - FadeEnd was a fixed 45 yd while Radius
                // went to 120". Unlinked, grass thins from 30 and is gone by 45
                // however far it was scattered, which reads as a hard cap at about
                // forty yards. So the effective numbers are printed, always.
                if (_foliage is not null)
                {
                    var fol = _foliage;
                    ImGui.TextDisabled(
                        $"visible to {fol.EffectiveFadeEnd:F0} yd, thinning from " +
                        $"{fol.EffectiveFadeStart:F0} yd - {fol.InstanceCount:N0} tuft(s) placed");

                    if (!fol.LinkFadeToRadius && fol.FadeEnd < fol.Radius - 1f)
                        ImGui.TextColored(new Vector4(1f, 0.72f, 0.30f, 1f),
                            $"Scattered to {fol.Radius:F0} yd but faded out by {fol.FadeEnd:F0} yd - " +
                            "turn on \"Fade follows distance\" under Advanced.");
                }

                if (ImGui.TreeNode("Advanced##clutter"))
                {
                    ImGui.TextDisabled("Coverage - all of these are baked in at scatter time.");
                    IntSlider("cmpc", "Max per cell", () => s.Clutter.MaxPerCell,
                        v => s.Clutter.MaxPerCell = v, 0, 24);
                    Slider("cscale", "Scale", () => s.Clutter.Scale, v => s.Clutter.Scale = v, 0.1f, 4f, "{0:F2}");
                    Slider("cjit", "Scale jitter", () => s.Clutter.ScaleJitter, v => s.Clutter.ScaleJitter = v,
                        0f, 0.9f, "{0:F2}");
                    IntSlider("ccap", "Instance cap", () => s.Clutter.MaxInstances,
                        v => s.Clutter.MaxInstances = v, 1000, 80000);
                    Slider("cres", "Rescatter after moving", () => s.Clutter.RescatterDistance,
                        v => s.Clutter.RescatterDistance = v, 1f, 40f, "{0:F0} yd");

                    ImGui.TextDisabled("Placement rules Blizzard baked into the terrain (1.12).");
                    Check("Per-cell layer map", () => s.Clutter.UseCellLayerMap, v => s.Clutter.UseCellLayerMap = v,
                        "MCNK 0x40: two bits per cell naming which texture layer supplies that\n" +
                        "cell's ground effect. Off guesses from the alpha maps, which is what\n" +
                        "used to grow grass on the Northshire cobblestone.");
                    Check("No-doodad mask", () => s.Clutter.UseNoDoodadMask, v => s.Clutter.UseNoDoodadMask = v,
                        "MCNK 0x50: one artist-authored bit per cell meaning \"place nothing\n" +
                        "here\". In Northshire it traces the road.");
                    Check("Skip terrain holes", () => s.Clutter.SkipHoles, v => s.Clutter.SkipHoles = v,
                        "MCNK 0x3C: cells cut away so a dungeon entrance is reachable.");
                    Check("Skip cells under water", () => s.Clutter.SkipDeepLiquidCells,
                        v => s.Clutter.SkipDeepLiquidCells = v,
                        "Grass does not grow in the river. This renderer had no idea liquid\n" +
                        "existed, so land clutter scattered happily along the riverbed.\n" +
                        "Depth-gated, not a blanket cull - reeds at the shallow margin are\n" +
                        "authored by the riverbed's own texture layer and are correct.");
                    if (s.Clutter.SkipDeepLiquidCells)
                    {
                        Slider("cliqd", "  Water depth cutoff", () => s.Clutter.LiquidFoliageMaxDepth,
                            v => s.Clutter.LiquidFoliageMaxDepth = v, 0f, 4f, "{0:F2} yd",
                            "Cells under water deeper than this stop scattering. Lower cuts\n" +
                            "further into the shallows and takes the reeds with it; higher\n" +
                            "lets grass back into the channel.");
                        if (_foliage is not null)
                            ImGui.TextDisabled($"  {_foliage.LiquidCells} cell(s) skipped as underwater last scatter");
                    }

                    ImGui.TextDisabled("Wind and fade.");
                    Slider("cwind", "Wind strength", () => s.Clutter.WindStrength,
                        v => s.Clutter.WindStrength = v, 0f, 0.4f, "{0:F3}");
                    Slider("cwspd", "Wind speed", () => s.Clutter.WindSpeed,
                        v => s.Clutter.WindSpeed = v, 0f, 5f, "{0:F2}");
                    Check("Fade follows distance", () => s.Clutter.LinkFadeToRadius,
                        v => s.Clutter.LinkFadeToRadius = v,
                        "On, the fade window comes from the distance slider so raising it\n" +
                        "actually shows more grass. Off, clutter past 'fade end' is invisible\n" +
                        "no matter how large the radius is.");
                    if (s.Clutter.LinkFadeToRadius)
                        Slider("cfsf", "Fade start (fraction)", () => s.Clutter.FadeStartFraction,
                            v => s.Clutter.FadeStartFraction = v, 0.1f, 1f, "{0:F2}");
                    else
                    {
                        Slider("cfs", "Fade start", () => s.Clutter.FadeStart,
                            v => s.Clutter.FadeStart = v, 0f, 120f, "{0:F0} yd");
                        Slider("cfe", "Fade end", () => s.Clutter.FadeEnd,
                            v => s.Clutter.FadeEnd = v, 1f, 120f, "{0:F0} yd");
                    }

                    ImGui.TextDisabled("Look.");
                    Slider("ccut", "Alpha cutoff##clutter", () => s.Clutter.AlphaCutoff,
                        v => s.Clutter.AlphaCutoff = v, 0.05f, 0.95f, "{0:F2}");
                    Slider("cbri", "Brightness##clutter", () => s.Clutter.Brightness,
                        v => s.Clutter.Brightness = v, 0.2f, 2f, "{0:F2}");

                    ImGui.TextDisabled("Types - retail hid clutter selectively. Uncheck Rock to clear the road.");
                    foreach (FoliageKind kind in Enum.GetValues<FoliageKind>())
                    {
                        string key = kind.ToString();
                        bool on = !s.Clutter.KindEnabled.TryGetValue(key, out bool stored) || stored;
                        if (Check(key + "##clutterKind", () => on, v => s.Clutter.KindEnabled[key] = v))
                            ApplyClutter(s);

                        float keep = s.Clutter.KindDensity.TryGetValue(key, out float k) ? k : 1f;
                        // Relative, not fixed: this row lives two indents deep and
                        // fixed offsets overflow the box exactly the way the
                        // sliders did.
                        float kindRow = ControlWidth();
                        ImGui.SameLine(MathF.Max(kindRow * 0.42f, 120f * S));
                        ImGui.SetNextItemWidth(MathF.Max(kindRow * 0.34f, 90f * S));
                        if (ImGui.SliderFloat($"##keep{key}", ref keep, 0f, 1f, "x%.2f"))
                        {
                            s.Clutter.KindDensity[key] = keep;
                            ApplyClutter(s);
                        }
                        if (_foliage is not null)
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled(_foliage.KindInstances(kind).ToString());
                        }
                    }
                    ImGui.TreePop();
                }
            }
            EndBox();

            BeginBox("water", "Water");
            {
                Check("Render water", () => s.Water.Enabled, v => s.Water.Enabled = v);

                Check("Draw WMO liquid", () => s.Water.DrawWmoLiquid,
                    v => s.Water.DrawWmoLiquid = v,
                    "MLIQ surfaces inside buildings and dungeons: Blackrock's lava\n" +
                    "lake, the Stormwind canals. Draw-only - WMO liquid does not\n" +
                    "affect swimming, submersion or the underwater tint.");
                if (s.Water.DrawWmoLiquid && _liquid is not null)
                    ImGui.TextDisabled($"  {_liquid.WmoSurfaceCount} WMO surface(s) meshed, " +
                        $"{_liquid.WmoSurfacesDrawnLastFrame} drawn last frame");

                Check("Authored water colours (Light.dbc)  [KNOWN BAD]",
                    () => s.Water.UseAuthoredColors,
                    v => s.Water.UseAuthoredColors = v,
                    "LEAVE THIS OFF. Takes ocean/river colour from LightIntBand 13-16.\n" +
                    "The band indexing is correct and the values are real, but they are\n" +
                    "NOT a texture tint: water.frag multiplies the animated liquid\n" +
                    "texture by them, Azeroth's river-close is (0.000, 0.114, 0.161)\n" +
                    "with red exactly zero, and those texture frames ARE the bright\n" +
                    "animated highlights. Result is dark, monocolour, static-looking\n" +
                    "water. WoWee reads these same bands and refuses to use them.\n" +
                    "Off is the tuned look. SYSTEM_WATER.md section 5.");

                if (s.Water.UseAuthoredColors)
                    ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f),
                        "This is known to break the river - see the tooltip.");
                else if (_liquid is not null && !_liquid.AuthoredColorsActive)
                    ImGui.TextDisabled("Using the hand-tuned colours (the shipping look).");
                if (Slider("wdet", "Water detail", () => s.Water.DetailPercent,
                        v => { s.Water.DetailPercent = v; s.Water.DetailCustom = false; },
                        0f, 100f, "{0:F0}%",
                        "Animation rate, frame cross-fade and shoreline softness. Does NOT touch\n" +
                        "the colour set - 1.12 water is a dark, near-opaque textured surface and\n" +
                        "SYSTEM_WATER.md Draft 2 records why that is not a preference."))
                {
                    s.ResolveWaterDetail();
                    MarkCustomPreset();
                }

                if (ImGui.TreeNode("Advanced##water"))
                {
                    ImGui.TextDisabled("Texture and animation.");
                    Slider("wts", "Texture scale (tiling)", () => s.Water.TextureScale,
                        v => s.Water.TextureScale = v, 0.01f, 1f, "{0:F3}");
                    if (Slider("wfps", "Animation FPS", () => s.Water.AnimationFps,
                            v => s.Water.AnimationFps = v, 0f, 60f, "{0:F1}")) s.Water.DetailCustom = true;
                    if (Slider("wfb", "Frame blend", () => s.Water.FrameBlend,
                            v => s.Water.FrameBlend = v, 0f, 1f, "{0:F2}",
                            "0 twinkles between frames, 1 glides.")) s.Water.DetailCustom = true;
                    Slider("wtb", "Texture brightness", () => s.Water.TexBrightness,
                        v => s.Water.TexBrightness = v, 0f, 3f, "{0:F2}");
                    Slider("wtc", "Texture contrast", () => s.Water.TexContrast,
                        v => s.Water.TexContrast = v, 0.2f, 2.5f, "{0:F2}");

                    var tint = new Vector3(s.Water.TintR, s.Water.TintG, s.Water.TintB);
                    if (ImGui.ColorEdit3("Texture tint", ref tint))
                    {
                        s.Water.TintR = tint.X; s.Water.TintG = tint.Y; s.Water.TintB = tint.Z;
                        ApplyWater(s);
                    }

                    ImGui.TextDisabled("Opacity and depth.");
                    Slider("wop", "Opacity (deep)", () => s.Water.Opacity, v => s.Water.Opacity = v,
                        0f, 1f, "{0:F2}");
                    if (Slider("wsf", "Shoreline alpha", () => s.Water.ShoreFade,
                            v => s.Water.ShoreFade = v, 0f, 1f, "{0:F2}")) s.Water.DetailCustom = true;
                    if (Slider("wsw", "Shoreline width", () => s.Water.ShoreWidth,
                            v => s.Water.ShoreWidth = v, 0.05f, 5f, "{0:F2} yd")) s.Water.DetailCustom = true;
                    Slider("wdd", "Deep darkening", () => s.Water.DepthDarken,
                        v => s.Water.DepthDarken = v, 0.1f, 1f, "{0:F2}");
                    Slider("wdr", "Depth rate", () => s.Water.DepthRate,
                        v => s.Water.DepthRate = v, 0.01f, 1f, "{0:F3}");

                    ImGui.TextDisabled("Body colour - the water texture supplies NONE. See SYSTEM_WATER.md section 8.");
                    var rBody = new Vector3(s.Water.RiverBodyR, s.Water.RiverBodyG, s.Water.RiverBodyB);
                    if (ImGui.ColorEdit3("River / lake body", ref rBody))
                    {
                        s.Water.RiverBodyR = rBody.X; s.Water.RiverBodyG = rBody.Y; s.Water.RiverBodyB = rBody.Z;
                        ApplyWater(s);
                    }
                    var oBody = new Vector3(s.Water.OceanBodyR, s.Water.OceanBodyG, s.Water.OceanBodyB);
                    if (ImGui.ColorEdit3("Ocean body", ref oBody))
                    {
                        s.Water.OceanBodyR = oBody.X; s.Water.OceanBodyG = oBody.Y; s.Water.OceanBodyB = oBody.Z;
                        ApplyWater(s);
                    }
                    Slider("whg", "Highlight gain", () => s.Water.HighlightGain,
                        v => s.Water.HighlightGain = v, 0f, 16f, "{0:F2}",
                        "How hard the animated liquid texture is ADDED over the body colour.\n" +
                        "lake_a.blp is a near-black greyscale mask peaking at 0.158 luminance -\n" +
                        "it is the sparkle, not the surface. 0 gives a dead still surface,\n" +
                        "which is the quickest way to judge the body colour on its own.");

                    ImGui.TextDisabled("Walking wake - the trail you leave wading. PLAN_16.");
                    Check("Walking wake", () => s.Water.WakeEnabled, v => s.Water.WakeEnabled = v,
                        "Stamps Blizzard's own XTextures\\splash\\wake.blp along your recent\n" +
                        "path while you are wading. Off, or strength 0, is a bit-identical\n" +
                        "surface to before the feature existed.");
                    if (s.Water.WakeEnabled)
                    {
                        Slider("wkst", "  Wake strength", () => s.Water.WakeStrength,
                            v => s.Water.WakeStrength = v, 0f, 2f, "{0:F2}");
                        Slider("wkln", "  V length", () => s.Water.WakeLength,
                            v => s.Water.WakeLength = v, 0.5f, 20f, "{0:F2} yd",
                            "How far the V trails behind you.");
                        Slider("wkwd", "  V width", () => s.Water.WakeWidth,
                            v => s.Water.WakeWidth = v, 0.3f, 12f, "{0:F2} yd",
                            "How wide the arms spread at the tail.");
                        Slider("wkah", "  Apex ahead", () => s.Water.WakeAhead,
                            v => s.Water.WakeAhead = v, -2f, 4f, "{0:F2} yd",
                            "Where the point of the V sits relative to your feet.");
                        Slider("wkfs", "  Full-strength speed", () => s.Water.WakeFullSpeed,
                            v => s.Water.WakeFullSpeed = v, 0.5f, 10f, "{0:F2} yd/s",
                            "Movement speed at which the wake reaches full visibility.");
                        Slider("wkfd", "  Fade out", () => s.Water.WakeFade,
                            v => s.Water.WakeFade = v, 0.05f, 3f, "{0:F2} s",
                            "How long the churn lingers after you stop.");
                        Slider("wkrp", "  Wavefronts", () => s.Water.WakeRepeat,
                            v => s.Water.WakeRepeat = v, 0.5f, 8f, "{0:F2}",
                            "How many crests fit along the length. 1 is a single frozen\n" +
                            "chevron; higher gives a train of them streaming backward.");
                        Slider("wkwl", "  World lock", () => s.Water.WakeWorldLock,
                            v => s.Water.WakeWorldLock = v, 0f, 2f, "{0:F2}",
                            "1.0 = crests stay put in the river and you move THROUGH\n" +
                            "them (what the real client does). 0 = the V rides along\n" +
                            "stuck to you, which is what the first version did wrong.");
                        Slider("wkop", "  Alpha lift", () => s.Water.WakeOpacity,
                            v => s.Water.WakeOpacity = v, 0f, 1f, "{0:F2}",
                            "A wake happens in shallow water, where the shoreline fade has\n" +
                            "already made the surface faint. This lifts it back.");
                        var wc = new Vector3(s.Water.WakeColorR, s.Water.WakeColorG, s.Water.WakeColorB);
                        if (ImGui.ColorEdit3("  Wake colour", ref wc))
                        {
                            s.Water.WakeColorR = wc.X; s.Water.WakeColorG = wc.Y; s.Water.WakeColorB = wc.Z;
                            ApplyWater(s);
                        }
                        if (_liquid is not null)
                            ImGui.TextDisabled(_liquid.HasWakeTexture
                                ? $"  wake.blp loaded, amount {_liquid.WakeAmount:F2}"
                                : $"  wake.blp NOT loaded - analytic V, amount {_liquid.WakeAmount:F2}");
                    }

                    ImGui.TextDisabled("Lighting.");
                    Slider("wbr", "Base brightness##water", () => s.Water.Brightness,
                        v => s.Water.Brightness = v, 0f, 2f, "{0:F2}");
                    Slider("wam", "Ambient amount", () => s.Water.AmbientAmount,
                        v => s.Water.AmbientAmount = v, 0f, 2f, "{0:F2}");
                    Slider("wsa", "Sun amount", () => s.Water.SunAmount,
                        v => s.Water.SunAmount = v, 0f, 1f, "{0:F2}");
                    Slider("wss", "Sky sheen (grazing)", () => s.Water.SkySheen,
                        v => s.Water.SkySheen = v, 0f, 1f, "{0:F2}");

                    ImGui.TextDisabled("Geometry waves - 0 is correct for 1.12. See SYSTEM_WATER.md Draft 2.");
                    Slider("wwa", "Wave amplitude", () => s.Water.WaveAmplitude,
                        v => s.Water.WaveAmplitude = v, 0f, 2f, "{0:F2}");
                    Slider("wws", "Wave speed", () => s.Water.WaveSpeed,
                        v => s.Water.WaveSpeed = v, 0f, 3f, "{0:F2}");
                    ImGui.TreePop();
                }
            }
            EndBox();

            BeginBox("light", "Lighting and sky");
            {
                // Both modes resolve the real Light.dbc lighting for your
                // position and time; they differ in interpretation. Switching
                // pushes the mode's recommended values (doorway spill) exactly
                // like a quality button pushes its numbers - and quality
                // buttons never touch the mode (it is not a quality dial).
                {
                    int mode = (int)s.Lighting.Mode;
                    ImGui.SetNextItemWidth(200f * S);
                    if (ImGui.Combo("Lighting mode##lightmode", ref mode,
                                    _lightingModeLabels, _lightingModeLabels.Length))
                    {
                        s.Lighting.ApplyLightingModeDefaults((LightingMode)mode);
                        ApplySettings(s);
                        _settingsStatus = $"lighting mode set to {_lightingModeLabels[mode]}";
                    }
                    Tip("MSUI Lighting is this client's tuned look: the authored Light.dbc\n" +
                        "colours applied directly, plus a boosted interior doorway glow.\n" +
                        "1.12 Parity follows the vanilla client as closely as we can: the same\n" +
                        "colours scaled by the real day/night intensity curve (World\\dnc.db)\n" +
                        "and a neutral doorway glow. Switching resets the mode's recommended\n" +
                        "values; Advanced below can still override them.");
                }
                Check("Time-of-day lighting", () => s.Lighting.DynamicLighting,
                    v => s.Lighting.DynamicLighting = v);

                // Where the world clock comes from (v7). Server is the vanilla
                // behaviour: SMSG_LOGIN_SETTIMESPEED's game time, advanced
                // locally, with the machine's wall clock as the offline
                // fallback. Fixed keeps the old pinned-hour slider; Cycle is
                // the accelerated debug day/night.
                {
                    int src = (int)s.Lighting.TimeSource;
                    ImGui.SetNextItemWidth(200f * S);
                    if (ImGui.Combo("Time of day##timesource", ref src,
                                    _timeSourceLabels, _timeSourceLabels.Length))
                    {
                        s.Lighting.TimeSource = (TimeSource)src;
                        _timeSource = s.Lighting.TimeSource;
                        _devTimePin = false;   // an explicit source choice ends any dev pin
                        if (_timeSource == TimeSource.Fixed)
                            _atmosphere.TimeOfDayHours = s.Lighting.TimeOfDay;
                        _settingsStatus = $"time of day now {_timeSourceLabels[src]}";
                    }
                    Tip("Server tracks the game-world clock the server sends, like the\n" +
                        "vanilla client - day and night actually pass. Until a server time\n" +
                        "arrives (offline, creator mode) it follows this machine's clock.\n" +
                        "Fixed pins the world at the hour below.\n" +
                        "Cycle runs an accelerated day/night for debugging.");

                    if (s.Lighting.TimeSource == TimeSource.Fixed)
                        Slider("tod", "Hour", () => s.Lighting.TimeOfDay,
                            v => { s.Lighting.TimeOfDay = v; _atmosphere.TimeOfDayHours = v; },
                            0f, 24f, "{0:F2} h");
                    else
                        ImGui.TextDisabled($"  world clock: {WorldClockDescription()}");

                    if (s.Lighting.TimeSource == TimeSource.Cycle)
                        Slider("ghpm", "Game hours per minute", () => s.Lighting.GameHoursPerMinute,
                            v => { s.Lighting.GameHoursPerMinute = v; _gameHoursPerMinute = v; },
                            0.1f, 12f, "{0:F1}");
                }

                if (ImGui.TreeNode("Advanced##lighting"))
                {
                    Slider("sun", "Sun strength", () => s.Lighting.SunStrength,
                        v => s.Lighting.SunStrength = v, 0f, 2f, "{0:F2}");
                    Slider("amb", "Ambient strength", () => s.Lighting.AmbientStrength,
                        v => s.Lighting.AmbientStrength = v, 0f, 2f, "{0:F2}");
                    Slider("mcsh", "Baked terrain shadows", () => s.Lighting.TerrainShadowStrength,
                        v =>
                        {
                            s.Lighting.TerrainShadowStrength = v;
                            if (_terrain is not null) _terrain.AuthoredShadowStrength = v;
                        },
                        0f, 1f, "{0:F2}",
                        "Strength of the broad hand-authored MCSH shadows stored in the\n" +
                        "original terrain. This is classic scene structure, not a modern\n" +
                        "dynamic shadow-map effect.");
                    Slider("unitblob", "Unit contact shadows", () => s.Lighting.UnitShadowOpacity,
                        v =>
                        {
                            s.Lighting.UnitShadowOpacity = v;
                            if (_unitShadows is not null) _unitShadows.Opacity = v;
                        },
                        0f, 1f, "{0:F2}",
                        "Opacity of the classic ShadowBlob under grounded characters.\n" +
                        "It anchors feet without adding a modern shadow-map pass.");

                    Check("Baked interior light - buildings (MOCV)", () => s.Lighting.WmoVertexColors,
                        v => s.Lighting.WmoVertexColors = v);
                    Check("Baked interior light - props (MODD)", () => s.Lighting.DoodadInteriorLighting,
                        v => s.Lighting.DoodadInteriorLighting = v);
                    Check("Link the two interior brightnesses", () => s.Lighting.LinkInteriorBrightness,
                        v => s.Lighting.LinkInteriorBrightness = v,
                        "A barrel only matches the floor it stands on while both use the same\n" +
                        "factor. That is SYSTEM_DOODAD_LIGHTING.md's one invariant.");

                    if (Slider("ib", "Interior brightness", () => s.Lighting.InteriorBrightness,
                            v => s.Lighting.InteriorBrightness = v, 0.5f, 4f, "x{0:F2}",
                            "2.00 is vanilla: the classic path halves MOCV at load and doubles it\n" +
                            "at draw.") && s.Lighting.LinkInteriorBrightness)
                        s.Lighting.DoodadInteriorBrightness = s.Lighting.InteriorBrightness;

                    if (!s.Lighting.LinkInteriorBrightness)
                        Slider("dib", "Prop interior brightness", () => s.Lighting.DoodadInteriorBrightness,
                            v => s.Lighting.DoodadInteriorBrightness = v, 0.5f, 4f, "x{0:F2}");

                    Slider("spill", "Interior doorway glow", () => s.Lighting.InteriorSpill,
                        v => s.Lighting.InteriorSpill = v, 0.5f, 3f, "x{0:F2}",
                        "Extra multiplier on baked interior light, on top of Interior\n" +
                        "brightness - it decides how strongly a lit room spills from its\n" +
                        "doorway (the Northshire Abbey glow). MSUI Lighting recommends " +
                        $"{GameSettings.LightingSettings.MsuiInteriorSpill:F1};\n" +
                        $"1.12 Parity recommends the neutral {GameSettings.LightingSettings.ParityInteriorSpill:F1}. " +
                        "Switching mode resets it.");

                    Check("Draw the sky gradient", () => s.Lighting.SkyEnabled, v => s.Lighting.SkyEnabled = v);
                    Slider("skym", "Sky horizon band", () => s.Lighting.SkyStopMiddle,
                        v => s.Lighting.SkyStopMiddle = v, 0f, 1f, "{0:F3}",
                        "One of the three band heights SYSTEM_EXTERIOR_LIGHTING.md section 4\n" +
                        "still records as OURS and still a guess. It needs a refs/ capture, not\n" +
                        "a slider - but the slider is how you find the value to check.");
                    Slider("sky1", "Sky band 1", () => s.Lighting.SkyStopBand1,
                        v => s.Lighting.SkyStopBand1 = v, 0f, 1f, "{0:F3}");
                    Slider("sky2", "Sky band 2", () => s.Lighting.SkyStopBand2,
                        v => s.Lighting.SkyStopBand2 = v, 0f, 1f, "{0:F3}");
                    ImGui.TreePop();
                }
            }
            EndBox();
        }
        ImGui.EndChild();

        if (ImGui.BeginChild("##video-footer", Vector2.Zero))
            DrawPanelFooter(size, presets: true);
        ImGui.EndChild();
    }

    // ── the other pages ──────────────────────────────────────────────────────

    private void DrawControlsPage(Vector2 size)
    {
        var s = Settings;
        float bodyHeight = PanelBodyHeight(presets: false);

        if (ImGui.BeginChild("##controls-body", new Vector2(0f, bodyHeight)))
        {
            BeginBox("mouse", "Mouse");
            {
                Slider("msens", "Mouse sensitivity", () => s.Controls.MouseSensitivity,
                    v => { s.Controls.MouseSensitivity = v; _window.MouseSensitivity = v; },
                    0.1f, 10f, "x{0:F2}");
                Check("Invert vertical look", () => s.Controls.InvertPitch,
                    v => { s.Controls.InvertPitch = v; _config.Camera.InvertPitch = v; });
                Check("Raw cursor", () => s.Controls.RawCursor,
                    v => { s.Controls.RawCursor = v; _window.RawCursor = v; },
                    "Unbounded look with the cursor locked - the mode a game wants, and the\n" +
                    "one a platform is most likely to refuse. If look is dead, turn this OFF\n" +
                    "first.");
            }
            EndBox();

            BeginBox("crpg", "CRPG / RTS");
            {
                Check("RTS commands on party portraits", () => s.Controls.RtsCommands,
                    v => s.Controls.RtsCommands = v,
                    "A command strip beside each party portrait: role (Tank/Healer/DPS,\n" +
                    "feeds rotations later), Hold (stand your ground) and Patrol (loop the\n" +
                    "current waypoint chain).");
                Check("Cut buildings away in the free view", () => s.Controls.FreeViewCutaway,
                    v => s.Controls.FreeViewCutaway = v,
                    "Divinity-style: while you command a toon that is indoors, its\n" +
                    "building's shell and roof are hidden so the room shows from the sky.\n" +
                    "Untick for the untouched renderer.");
                Check("Free-view camera collides with the world", () => s.Controls.FreeViewCameraCollision,
                    v => s.Controls.FreeViewCameraCollision = v,
                    "The free camera is a floating body: it stops at walls, ceilings and\n" +
                    "the ground instead of ghosting through them — a room contains its\n" +
                    "own view, and you fly through the door to see the next one.");
            }
            EndBox();

            BeginBox("camera", "Camera");
            {
                Check("Camera collision", () => s.Controls.CameraCollision,
                    v => { s.Controls.CameraCollision = v; _config.Camera.Collision = v; },
                    "Pulls the camera in when terrain or a building is between it and you.\n" +
                    "Off means the camera sits underground and you see through the world.");

                if (ImGui.TreeNode("Advanced##camera"))
                {
                    Slider("turn", "Turn speed", () => s.Controls.TurnSpeedDegrees,
                        v => { s.Controls.TurnSpeedDegrees = v; _turnSpeed = v * MathF.PI / 180f; },
                        45f, 360f, "{0:F0} deg/s");
                    Slider("eye", "Eye height", () => s.Controls.EyeHeight,
                        v => { s.Controls.EyeHeight = v; _window.Camera.EyeHeight = v; }, 0f, 10f, "{0:F2} yd");
                    Slider("maxd", "Max camera distance", () => s.Controls.MaxCameraDistance,
                        v => { s.Controls.MaxCameraDistance = v; _window.Camera.MaxDistance = v; }, 5f, 80f, "{0:F0} yd");
                    Slider("clr", "Collision clearance", () => s.Controls.CameraClearance,
                        v => { s.Controls.CameraClearance = v; _config.Camera.Clearance = v; }, 0.05f, 2f, "{0:F2} yd");
                    Slider("rest", "Restore speed", () => s.Controls.CameraRestoreSpeed,
                        v => { s.Controls.CameraRestoreSpeed = v; _config.Camera.RestoreSpeed = v; }, 1f, 30f, "{0:F1} yd/s",
                        "Pulling in is instant; pushing back out is not, because a camera that\n" +
                        "snaps outward every time you clear a doorway is nauseating.");
                    ImGui.TreePop();
                }
            }
            EndBox();

            BeginBox("binds", "Current keys");
            {
                ImGui.TextWrapped(
                    "W/S walk, A/D turn, Q/E strafe (hold RIGHT mouse to swap A/D to strafe). " +
                    "Arrow keys turn and walk, PgUp/PgDn look up and down, Shift walks, Space " +
                    "jumps, F toggles fly, C toggles the collision wireframe. LEFT mouse swings " +
                    "the camera without turning you; RIGHT mouse turns you and the camera " +
                    "together; moving re-centres the camera behind you. Wheel zooms.");
                ImGui.TextDisabled("Rebindable keys are not built yet.");
            }
            EndBox();
        }
        ImGui.EndChild();

        if (ImGui.BeginChild("##controls-footer", Vector2.Zero))
            DrawPanelFooter(size, presets: false);
        ImGui.EndChild();
    }

    private void DrawStreamingPage(Vector2 size)
    {
        var s = Settings;
        float bodyHeight = PanelBodyHeight(presets: false);

        if (ImGui.BeginChild("##stream-body", new Vector2(0f, bodyHeight)))
        {
            BeginBox("resid", "Residency");
            {
                ImGui.TextWrapped(
                    "How much world is kept resident around you. Read SYSTEM_STREAMING.md " +
                    "before changing what these mean - the felt micro-stutter is a frame-pacing " +
                    "bug and is NOT a workload problem, so raising or lowering these will not " +
                    "fix it.");
                Restart();

                IntSlider("tiler", "Terrain ring radius", () => s.Streaming.TileRadius,
                    v => s.Streaming.TileRadius = v, 1, 3,
                    "1 is a moving 3x3 block of ADT tiles. Each step up is a lot more memory.");
                IntSlider("wmor", "Building preload radius", () => s.Streaming.WmoPreloadRadius,
                    v => s.Streaming.WmoPreloadRadius = v, 1, 4,
                    "2 keeps the visible 3x3 terrain block but parses buildings referenced by\n" +
                    "the surrounding 5x5. The extra RAM buys about one tile of warning.");
                Check("Block startup until the outer ring is resident",
                    () => s.Streaming.DrainPreloadsAtStartup, v => s.Streaming.DrainPreloadsAtStartup = v,
                    "The legacy startup mode. The default starts as soon as the visible set is\n" +
                    "ready and warms the outer ring in the background.");
            }
            EndBox();

            BeginBox("residnow", "Right now");
            {
                if (_terrain is not null) ImGui.Text($"resident tiles      {_terrain.TileCount}");
                if (_wmo is not null) ImGui.Text($"building preloads   {_wmo.PendingPreloads} queued");
                if (_doodads is not null) ImGui.Text($"doodad preloads     {_doodads.PendingPreloads} queued");
            }
            EndBox();
        }
        ImGui.EndChild();

        if (ImGui.BeginChild("##stream-footer", Vector2.Zero))
            DrawPanelFooter(size, presets: false);
        ImGui.EndChild();
    }

    // ── footer ───────────────────────────────────────────────────────────────

    private float PanelBodyHeight(bool presets)
    {
        float available = MathF.Max(ImGui.GetContentRegionAvail().Y, 1f);
        float footer = MathF.Min(PanelFooterReserve(presets), MathF.Max(available - 1f, 1f));
        return MathF.Max(available - footer, 1f);
    }

    private float PanelFooterReserve(bool presets)
    {
        float available = MathF.Max(ImGui.GetContentRegionAvail().X, 1f);
        float spacingX = ImGui.GetStyle().ItemSpacing.X;
        var button = new Vector2(WowSkin.ButtonArt.X * 1.35f,
            WowSkin.ButtonArt.Y * 1.15f) * S;

        int PackedRows(params float[] widths)
        {
            int rows = 1;
            float used = 0f;
            foreach (float rawWidth in widths)
            {
                float width = MathF.Min(rawWidth, available);
                if (used <= 0f) used = width;
                else if (used + spacingX + width <= available) used += spacingX + width;
                else { rows++; used = width; }
            }
            return rows;
        }

        int rows = PackedRows(button.X, button.X, button.X, button.X);
        if (presets)
        {
            rows += SettingsFile is { Presets.Count: > 0 }
                ? PackedRows(180f * S, button.X, 170f * S, button.X, button.X)
                : PackedRows(180f * S, button.X);
        }
        if (!string.IsNullOrEmpty(_settingsStatus)) rows++;

        float rowHeight = MathF.Max(button.Y, ImGui.GetFrameHeight());
        return rows * rowHeight + MathF.Max(rows - 1, 0) * ImGui.GetStyle().ItemSpacing.Y + 4f * S;
    }

    private void DrawPanelFooter(Vector2 size, bool presets)
    {
        float available = MathF.Max(ImGui.GetContentRegionAvail().X, 1f);
        var button = new Vector2(
            MathF.Min(WowSkin.ButtonArt.X * 1.35f * S, available),
            WowSkin.ButtonArt.Y * 1.15f * S);
        float lineUsed = 0f;

        void Place(float rawWidth)
        {
            float width = MathF.Min(rawWidth, available);
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            if (lineUsed > 0f && lineUsed + spacing + width <= available)
            {
                ImGui.SameLine();
                lineUsed += spacing + width;
            }
            else lineUsed = width;
        }

        if (presets)
        {
            float inputWidth = MathF.Min(180f * S, available);
            Place(inputWidth);
            ImGui.SetNextItemWidth(inputWidth);
            ImGui.InputText("##presetName", ref _presetNameInput, 48u);

            Place(button.X);
            if (Button("Save preset", button) && SettingsFile is not null &&
                !string.IsNullOrWhiteSpace(_presetNameInput))
            {
                SettingsFile.SavePreset(_presetNameInput);
                Settings.ActivePreset = _presetNameInput.Trim();
                _settingsStatus = $"saved preset '{_presetNameInput.Trim()}'";
                _presetNameInput = "";
            }

            if (SettingsFile is { Presets.Count: > 0 })
            {
                var names = new string[SettingsFile.Presets.Count];
                for (int i = 0; i < names.Length; i++) names[i] = SettingsFile.Presets[i].Name;
                _selectedPreset = Math.Clamp(_selectedPreset, 0, names.Length - 1);

                float comboWidth = MathF.Min(170f * S, available);
                Place(comboWidth);
                ImGui.SetNextItemWidth(comboWidth);
                ImGui.Combo("##presetPick", ref _selectedPreset, names, names.Length);

                Place(button.X);
                if (Button("Load", button))
                {
                    var preset = SettingsFile.Presets[_selectedPreset];
                    var loaded = preset.Settings.Clone();
                    // Layout is a global usability preference, not part of a
                    // renderer preset. Do not replace it invisibly on preset load.
                    loaded.MenuLayout = Settings.MenuLayout ??
                        new GameSettings.MenuLayoutSettings();
                    loaded.ResolveComposites();
                    SettingsFile.Replace(loaded);
                    ApplySettings(loaded);
                    _settingsStatus = $"loaded preset '{preset.Name}'";
                }

                Place(button.X);
                if (Button("Delete", button))
                {
                    string gone = SettingsFile.Presets[_selectedPreset].Name;
                    SettingsFile.DeletePreset(gone);
                    _selectedPreset = 0;
                    _settingsStatus = $"deleted preset '{gone}'";
                }
            }
        }

        lineUsed = 0f;
        Place(button.X);
        if (Button("Defaults", button))
        {
            ResetVisiblePageToDefaults();
            _settingsStatus = "page reset to shipped defaults";
        }

        Place(button.X);
        if (Button("Adopt live", button))
        {
            CaptureSettings(Settings);
            _settingsStatus = "adopted the values the renderers are actually using";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Pull whatever the renderers are set to RIGHT NOW into these settings.\n" +
                "This is the bridge from a DevTools tuning session to a saved preference:\n" +
                "dial it in on the HUD, adopt, Okay. It is what replaces hand-copying a\n" +
                "slider position back into a field initialiser.");

        Place(button.X);
        if (Button("Cancel", button)) CancelSettings();

        Place(button.X);
        if (Button("Okay (Esc)", button))
        {
            CommitSettings();
            _menuPage = MenuPage.GameMenu;
            _settingsPopupRequested = true;
            ImGui.CloseCurrentPopup();
        }

        if (!string.IsNullOrEmpty(_settingsStatus))
            ImGui.TextColored(WowSkin.Muted, _settingsStatus);
    }

    /// <summary>
    /// Act on a pending Exit Game. Called at the very top of Update, which is the
    /// only point in the loop that is outside an ImGui frame AND before any
    /// renderer is touched.
    ///
    /// ClientWindow.Close raises Closing synchronously, and Closing runs
    /// GameLoop.Dispose - which deletes the skin's textures, every renderer's
    /// buffers and finally the GL context. Doing that from a button handler meant
    /// the remaining widgets of that frame, and then ImGuiController.Render,
    /// walked freed memory. The crash surfaced as an AccessViolationException on
    /// whatever widget happened to come next, which points nowhere near the
    /// button that caused it.
    ///
    /// Returns true when the caller must return immediately and touch nothing.
    /// </summary>
    private bool ConsumeQuitRequest()
    {
        if (!_quitRequested) return false;
        _quitRequested = false;

        CommitSettings();
        _settingsOpen = false;
        _window.Close();
        return true;
    }

    private void CancelSettings()
    {
        if (_settingsSnapshot is not null && SettingsFile is not null)
        {
            SettingsFile.Replace(_settingsSnapshot);
            ApplySettings(_settingsSnapshot);
        }

        _settingsCancelling = true;
        _menuPage = MenuPage.GameMenu;
        _settingsPopupRequested = true;
        ImGui.CloseCurrentPopup();
    }

    private void CommitSettings()
    {
        SettingsFile?.Save();
        Console.WriteLine($"[settings] saved {SettingsFile?.FilePath}");
    }

    /// <summary>Reset only the page you can see. A "Defaults" on the video page that wiped controls would be a trap.</summary>
    private void ResetVisiblePageToDefaults()
    {
        var s = Settings;
        var d = GameSettings.Defaults();

        switch (_menuPage)
        {
            case MenuPage.Video:
                s.Display = d.Display; s.View = d.View; s.Detail = d.Detail;
                s.Clutter = d.Clutter; s.Water = d.Water; s.Lighting = d.Lighting;
                break;
            case MenuPage.Controls:
                s.Controls = d.Controls;
                break;
        }

        s.ActivePreset = "Custom";
        s.ResolveComposites();
        ApplySettings(s);
    }

    // ── composite bookkeeping ────────────────────────────────────────────────

    private void CustomiseView() { Settings.View.DistanceCustom = true; MarkCustomPreset(); }
    private void CustomiseObjects() { Settings.Detail.ObjectDetailCustom = true; MarkCustomPreset(); }
    private void CustomiseBuildings() { Settings.Detail.BuildingDetailCustom = true; MarkCustomPreset(); }

    private void MarkCustomPreset()
    {
        if (Array.Exists(GameSettings.QualityNames,
                n => string.Equals(n, Settings.ActivePreset, StringComparison.OrdinalIgnoreCase)))
            Settings.ActivePreset = "Custom";
    }

    // ── widget helpers ───────────────────────────────────────────────────────
    //
    // Get/set delegates rather than ref locals: the settings live on nested
    // classes as properties, and a property cannot be passed by ref. Same shape
    // the old water and foliage tuning windows used.

    private bool Button(string label, Vector2 size, bool enabled = true)
        => _skin is not null ? _skin.PanelButton(label, size, enabled)
         : enabled && ImGui.Button(label, size);

    private static void Tip(string? tip)
    {
        if (tip is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    private static void Restart()
        => ImGui.TextColored(new Vector4(1f, 0.72f, 0.30f, 1f), "Applies on the next launch.");

    /// <summary>
    /// Width for a full-width control, measured AT THE POINT OF USE.
    ///
    /// It used to be computed once per page and passed down, which was wrong the
    /// moment anything indented: BeginBox indents 12 and an Advanced TreeNode
    /// another ~21, so every control inside a drill-down was about 59 px wider
    /// than its box and its right-aligned value ran off the edge.
    ///
    /// GetContentRegionAvail already accounts for the current indent and for the
    /// child's scrollbar, so asking it here is both correct and shorter. The
    /// 12 * S trailing margin mirrors BeginBox's leading indent, which is what
    /// keeps a control centred inside its group box rather than flush right.
    /// </summary>
    private float ControlWidth()
        => MathF.Max(ImGui.GetContentRegionAvail().X - 12f * S, 60f);

    private bool Slider(
        string id, string caption, Func<float> get, Action<float> set,
        float lo, float hi, string format, string? tip = null)
    {
        float width = ControlWidth();
        float v = get();
        bool changed;

        if (_skin is not null)
            changed = _skin.SliderFloat(id, caption, ref v, lo, hi,
                string.Format(System.Globalization.CultureInfo.InvariantCulture, format, v), width);
        else
        {
            ImGui.SetNextItemWidth(width);
            changed = ImGui.SliderFloat(caption + "##" + id, ref v, lo, hi, "%.2f");
        }

        Tip(tip);
        if (!changed) return false;

        set(v);
        ApplySettings(Settings);
        return true;
    }

    private bool IntSlider(
        string id, string caption, Func<int> get, Action<int> set,
        int lo, int hi, string? tip = null)
    {
        float width = ControlWidth();
        float v = get();
        bool changed;

        if (_skin is not null)
            changed = _skin.SliderFloat(id, caption, ref v, lo, hi, $"{(int)MathF.Round(v)}", width);
        else
        {
            int iv = get();
            ImGui.SetNextItemWidth(width);
            changed = ImGui.SliderInt(caption + "##" + id, ref iv, lo, hi);
            v = iv;
        }

        Tip(tip);
        if (!changed) return false;

        set((int)MathF.Round(v));
        ApplySettings(Settings);
        return true;
    }

    private bool Check(string label, Func<bool> get, Action<bool> set, string? tip = null)
    {
        bool v = get();
        bool changed = _skin is not null ? _skin.CheckBox(label, ref v) : ImGui.Checkbox(label, ref v);
        Tip(tip);

        if (!changed) return false;

        set(v);
        ApplySettings(Settings);
        return true;
    }

    // ── apply / capture ──────────────────────────────────────────────────────

    /// <summary>
    /// Push every live setting onto the object that owns it. Called on every
    /// widget change (cheap - a few dozen property assignments), on load, on
    /// preset load, and on Cancel.
    ///
    /// Restart-scoped values (resolution, sample COUNT, anisotropy, the streaming
    /// radii) are deliberately absent: they are read by Program.Main and by world
    /// construction, and pretending to apply them here would be a lie.
    /// </summary>
    private void ApplySettings(GameSettings s)
    {
        _window.VSync = s.Display.VSync;
        _window.Fullscreen = s.Display.Fullscreen;
        _window.MultisamplingEnabled = s.Display.MultisamplingEnabled;
        if (_skin is not null) _skin.Textured = s.Display.TexturedFrame;

        // Interface scale, so Okay, Cancel and preset loads all put the UI back
        // where the settings say it is. Without this the slider's live effect
        // outlived a Cancel: the scale reverted in the file and stayed changed
        // on screen, which reads as "I can no longer change it".
        float uiScale = Math.Clamp(s.Display.UiScale, 0.5f, 4f);
        _window.ApplyUiFontScale(uiScale, s.Display.FontScale);
        if (_skin is not null) _skin.Scale = uiScale;

        if (_painterly is not null)
        {
            // config render.painterly true is a hard-on (scripted/headless runs
            // seed it there); otherwise the menu setting owns the toggle.
            _painterly.Enabled = s.Display.Painterly || _config.Render.Painterly;
            _painterly.Bands = s.Display.PainterlyBands;
            _painterly.BandStrength = s.Display.PainterlyBandStrength;
            _painterly.Detail = s.Display.PainterlyDetail;
            _painterly.Ink = s.Display.PainterlyInk;
            _painterly.InkThreshold = s.Display.PainterlyInkThreshold;
            _painterly.Silhouette = s.Display.PainterlySilhouette;
            _painterly.DepthFade = s.Display.PainterlyDepthFade;
            _painterly.CalmStart = s.Display.PainterlyCalmStart;
            _painterly.CalmEnd = s.Display.PainterlyCalmEnd;
            _painterly.Saturation = s.Display.PainterlySaturation;
            _painterly.Contrast = s.Display.PainterlyContrast;
            _painterly.Lift = s.Display.PainterlyLift;
            _painterly.Warmth = s.Display.PainterlyWarmth;
            _painterly.Grain = s.Display.PainterlyGrain;
            _painterly.Dither = s.Display.PainterlyDither;
            _painterly.CanvasHeight = Math.Clamp(s.Display.PainterlyCanvasHeight, 0, 4320);
        }
        // Styled icons and portraits were baked with the OLD knobs. Conditional,
        // because this method runs on every settings widget change and rebaking
        // them on an unrelated slider drag is not free.
        RefreshPainterlyArt();

        var cam = _window.Camera;
        cam.FieldOfViewDegrees = s.View.FieldOfView;
        cam.NearPlane = s.View.NearPlane;
        cam.EyeHeight = s.Controls.EyeHeight;
        cam.MaxDistance = s.Controls.MaxCameraDistance;

        // The far plane is either coupled to fog by the loop or set here. Setting
        // it while coupling is on would fight ApplyAtmosphere every frame.
        _coupleFarPlaneToFog = s.View.CoupleFarPlaneToFog;
        if (!_coupleFarPlaneToFog) cam.FarPlane = s.View.FarPlane;

        _atmosphere.FogEnabled = s.View.FogEnabled;
        _atmosphere.FogStart = MathF.Min(s.View.FogStart, s.View.FogEnd - 1f);
        _atmosphere.FogEnd = MathF.Max(s.View.FogEnd, s.View.FogStart + 1f);
        _atmosphere.CullAtFogEnd = s.View.CullAtFogEnd;
        _atmosphere.DynamicLighting = s.Lighting.DynamicLighting;
        _atmosphere.Mode = s.Lighting.Mode;
        _atmosphere.SunStrength = s.Lighting.SunStrength;
        _atmosphere.AmbientStrength = s.Lighting.AmbientStrength;
        if (_terrain is not null)
            _terrain.AuthoredShadowStrength = Math.Clamp(s.Lighting.TerrainShadowStrength, 0f, 1f);
        if (_unitShadows is not null)
            _unitShadows.Opacity = Math.Clamp(s.Lighting.UnitShadowOpacity, 0f, 1f);

        _timeSource = s.Lighting.TimeSource;
        _gameHoursPerMinute = s.Lighting.GameHoursPerMinute;
        // Fixed means the SETTING owns the hour; seed it so a saved fixed time
        // survives a restart. The tracking sources (Server / Cycle) write the
        // clock every frame in UpdateWorldClock and need no seed.
        if (_timeSource == TimeSource.Fixed)
            _atmosphere.TimeOfDayHours = s.Lighting.TimeOfDay;

        _window.MouseSensitivity = s.Controls.MouseSensitivity;
        _window.RawCursor = s.Controls.RawCursor;
        _config.Camera.InvertPitch = s.Controls.InvertPitch;
        _config.Camera.Collision = s.Controls.CameraCollision;
        _config.Camera.Clearance = s.Controls.CameraClearance;
        _config.Camera.RestoreSpeed = s.Controls.CameraRestoreSpeed;
        _turnSpeed = s.Controls.TurnSpeedDegrees * MathF.PI / 180f;

        if (_sky is not null)
        {
            _sky.Enabled = s.Lighting.SkyEnabled;
            _sky.StopMiddle = s.Lighting.SkyStopMiddle;
            _sky.StopBand1 = s.Lighting.SkyStopBand1;
            _sky.StopBand2 = s.Lighting.SkyStopBand2;
        }

        if (_wmo is not null)
        {
            bool reclassify = _wmo.ImpostorMaxVertices != s.Detail.ImpostorMaxVertices;

            _wmo.Enabled = s.Detail.Buildings;
            _wmo.FrustumCulling = s.Detail.WmoFrustumCulling;
            _wmo.UseDistanceLodShells = s.Detail.DistanceLodShells;
            _wmo.ForceTwoSided = s.Detail.ForceTwoSided;
            _wmo.AlphaCutoff = s.Detail.WmoAlphaCutoff;
            _wmo.DrawDistance = s.View.BuildingDistance;
            _wmo.ImpostorMaxVertices = s.Detail.ImpostorMaxVertices;
            _wmo.InsideInstanceMargin = s.Detail.InsideMargin;
            _wmo.InteriorCullDistance = s.Detail.InteriorCullDistance;
            _wmo.ShellNearGuard = s.Detail.ShellNearGuard;
            _wmo.OcclusionCulling = s.Detail.OcclusionCulling;
            _wmo.OcclusionMinDistance = s.Detail.OcclusionMinDistance;
            _wmo.UseVertexColors = s.Lighting.WmoVertexColors;
            _wmo.VertexColorScale = s.Lighting.InteriorBrightness;
            _wmo.InteriorBrightness = s.Lighting.InteriorSpill;
            _wmo.UsePortalCulling = s.Detail.WmoPortalCulling;
            _wmo.AppearFade = s.Detail.AppearFade;
            _wmo.AppearFadeSeconds = s.Detail.AppearFadeSeconds;

            if (reclassify) _wmo.ReclassifyShells();
            _config.Render.WmoDistance = s.View.BuildingDistance;
        }

        if (_doodads is not null)
        {
            bool distanceMoved = MathF.Abs(_doodads.DrawDistance - s.Detail.DoodadDistance) > 0.01f;

            _doodads.Enabled = s.Detail.Doodads;
            _doodads.FrustumCulling = s.Detail.DoodadFrustumCulling;
            _doodads.UseInstancing = s.Detail.DoodadInstancing;
            _doodads.FlatCullBounds = s.Detail.DoodadFlatCullBounds;
            _doodads.AlphaCutoff = s.Detail.DoodadAlphaCutoff;
            _doodads.DrawDistance = s.Detail.DoodadDistance;
            _doodads.InteriorLighting = s.Lighting.DoodadInteriorLighting;
            _doodads.VertexColorScale = s.Lighting.LinkInteriorBrightness
                ? s.Lighting.InteriorBrightness
                : s.Lighting.DoodadInteriorBrightness;
            _doodads.AppearFade = s.Detail.AppearFade;
            _doodads.AppearFadeSeconds = s.Detail.AppearFadeSeconds;

            _config.Render.DoodadDistance = s.Detail.DoodadDistance;

            if (_demandStreamDoodads != s.Detail.DoodadDemandStreaming)
            {
                _demandStreamDoodads = s.Detail.DoodadDemandStreaming;
                _doodadDemandDelay = 0f;
            }

            // Object residency is derived from the draw distance, so a change has
            // to invalidate the resident centre or the ring never grows.
            if (distanceMoved) _residentCentre = null;
        }

        ApplyClutter(s);
        ApplyWater(s);
    }

    private void ApplyClutter(GameSettings s)
    {
        if (_foliage is null) return;
        var f = _foliage;

        // Coverage is baked in at SCATTER time, not read per frame, so a change to
        // any of these looks dead until you walk. Force the rebuild.
        bool rescatter =
            MathF.Abs(f.Radius - s.Clutter.Radius) > 0.01f ||
            MathF.Abs(f.DensityScale - s.Clutter.Density) > 0.001f ||
            f.MaxPerCell != s.Clutter.MaxPerCell ||
            MathF.Abs(f.Scale - s.Clutter.Scale) > 0.001f ||
            MathF.Abs(f.ScaleJitter - s.Clutter.ScaleJitter) > 0.001f ||
            f.MaxInstances != s.Clutter.MaxInstances ||
            f.UseCellLayerMap != s.Clutter.UseCellLayerMap ||
            f.UseNoDoodadMask != s.Clutter.UseNoDoodadMask ||
            f.SkipHoles != s.Clutter.SkipHoles ||
            f.SkipDeepLiquidCells != s.Clutter.SkipDeepLiquidCells ||
            MathF.Abs(f.LiquidFoliageMaxDepth - s.Clutter.LiquidFoliageMaxDepth) > 0.001f;

        f.Enabled = s.Clutter.Enabled;
        f.Radius = s.Clutter.Radius;
        f.DensityScale = s.Clutter.Density;
        f.MaxPerCell = s.Clutter.MaxPerCell;
        f.Scale = s.Clutter.Scale;
        f.ScaleJitter = s.Clutter.ScaleJitter;
        f.MaxInstances = s.Clutter.MaxInstances;
        f.RescatterDistance = s.Clutter.RescatterDistance;
        f.WindStrength = s.Clutter.WindStrength;
        f.WindSpeed = s.Clutter.WindSpeed;
        f.LinkFadeToRadius = s.Clutter.LinkFadeToRadius;
        f.FadeStartFraction = s.Clutter.FadeStartFraction;
        f.FadeStart = s.Clutter.FadeStart;
        f.FadeEnd = s.Clutter.FadeEnd;
        f.AlphaCutoff = s.Clutter.AlphaCutoff;
        f.Brightness = s.Clutter.Brightness;
        f.UseCellLayerMap = s.Clutter.UseCellLayerMap;
        f.UseNoDoodadMask = s.Clutter.UseNoDoodadMask;
        f.SkipHoles = s.Clutter.SkipHoles;
        f.SkipDeepLiquidCells = s.Clutter.SkipDeepLiquidCells;
        f.LiquidFoliageMaxDepth = s.Clutter.LiquidFoliageMaxDepth;

        foreach (FoliageKind kind in Enum.GetValues<FoliageKind>())
        {
            string key = kind.ToString();

            if (s.Clutter.KindEnabled.TryGetValue(key, out bool on) && f.KindEnabled(kind) != on)
            {
                f.SetKindEnabled(kind, on);
                rescatter = true;
            }

            if (s.Clutter.KindDensity.TryGetValue(key, out float keep) &&
                MathF.Abs(f.KindDensity(kind) - keep) > 0.001f)
            {
                f.SetKindDensity(kind, keep);
                rescatter = true;
            }
        }

        // Deferred, not immediate - see _clutterRescatterPending. Coverage is
        // baked in at scatter time so the change IS invisible until it runs, but
        // one rebuild on release beats sixty during the drag.
        if (rescatter) _clutterRescatterPending = true;
    }

    private void ApplyWater(GameSettings s)
    {
        if (_liquid is null) return;
        var w = _liquid;

        w.Enabled = s.Water.Enabled;
        w.WmoLiquidEnabled = s.Water.DrawWmoLiquid;
        w.UseAuthoredColors = s.Water.UseAuthoredColors;
        w.TextureScale = s.Water.TextureScale;
        w.AnimationFps = s.Water.AnimationFps;
        w.FrameBlend = s.Water.FrameBlend;
        w.TexBrightness = s.Water.TexBrightness;
        w.TexContrast = s.Water.TexContrast;
        w.TexTint = new Vector3(s.Water.TintR, s.Water.TintG, s.Water.TintB);
        w.Opacity = s.Water.Opacity;
        w.ShoreFade = s.Water.ShoreFade;
        w.ShoreWidth = s.Water.ShoreWidth;
        w.DepthDarken = s.Water.DepthDarken;
        w.DepthRate = s.Water.DepthRate;
        w.Brightness = s.Water.Brightness;
        w.AmbientAmount = s.Water.AmbientAmount;
        w.SunAmount = s.Water.SunAmount;
        w.SkySheen = s.Water.SkySheen;
        w.WaveAmplitude = s.Water.WaveAmplitude;
        w.WaveSpeed = s.Water.WaveSpeed;
        w.RiverBody = new Vector3(s.Water.RiverBodyR, s.Water.RiverBodyG, s.Water.RiverBodyB);
        w.OceanBody = new Vector3(s.Water.OceanBodyR, s.Water.OceanBodyG, s.Water.OceanBodyB);
        w.HighlightGain = s.Water.HighlightGain;

        // The wake. Lifetime and spacing change the SHAPE of the trail rather
        // than its look, so a change there clears the existing samples instead
        // of leaving a half-old half-new trail on screen for a second.
        w.WakeEnabled = s.Water.WakeEnabled;
        w.WakeStrength = s.Water.WakeStrength;
        w.WakeLength = s.Water.WakeLength;
        w.WakeWidth = s.Water.WakeWidth;
        w.WakeAhead = s.Water.WakeAhead;
        w.WakeFullSpeed = s.Water.WakeFullSpeed;
        w.WakeFade = s.Water.WakeFade;
        w.WakeRepeat = s.Water.WakeRepeat;
        w.WakeWorldLock = s.Water.WakeWorldLock;
        w.WakeOpacity = s.Water.WakeOpacity;
        w.WakeColor = new Vector3(s.Water.WakeColorR, s.Water.WakeColorG, s.Water.WakeColorB);
    }

    /// <summary>
    /// The reverse: read whatever the renderers are actually set to right now and
    /// write it into the settings object.
    ///
    /// This is the bridge PLAN_11 exists to build. Every previous by-eye session -
    /// the lighting retune, the foliage curation, the water Draft 2 look - ended
    /// with a set of slider positions that had to be hand-copied into a field
    /// initialiser or lost. Tune on the HUD, press Adopt live, press Okay.
    /// </summary>
    private void CaptureSettings(GameSettings s)
    {
        s.Display.VSync = _window.VSync;
        s.Display.Fullscreen = _window.Fullscreen;
        s.Display.MultisamplingEnabled = _window.MultisamplingEnabled;

        if (_painterly is not null)
        {
            // Mirror live state (the debug panel can change it directly), same
            // reason CaptureSettings mirrors Fullscreen for Alt+Enter.
            s.Display.Painterly = _painterly.Enabled;
            s.Display.PainterlyBands = _painterly.Bands;
            s.Display.PainterlyBandStrength = _painterly.BandStrength;
            s.Display.PainterlyDetail = _painterly.Detail;
            s.Display.PainterlyInk = _painterly.Ink;
            s.Display.PainterlyInkThreshold = _painterly.InkThreshold;
            s.Display.PainterlySilhouette = _painterly.Silhouette;
            s.Display.PainterlyDepthFade = _painterly.DepthFade;
            s.Display.PainterlyCalmStart = _painterly.CalmStart;
            s.Display.PainterlyCalmEnd = _painterly.CalmEnd;
            s.Display.PainterlySaturation = _painterly.Saturation;
            s.Display.PainterlyContrast = _painterly.Contrast;
            s.Display.PainterlyLift = _painterly.Lift;
            s.Display.PainterlyWarmth = _painterly.Warmth;
            s.Display.PainterlyGrain = _painterly.Grain;
            s.Display.PainterlyDither = _painterly.Dither;
            s.Display.PainterlyCanvasHeight = _painterly.CanvasHeight;
        }

        var cam = _window.Camera;
        s.View.FieldOfView = cam.FieldOfViewDegrees;
        s.View.NearPlane = cam.NearPlane;
        s.View.FarPlane = cam.FarPlane;
        s.View.FogEnabled = _atmosphere.FogEnabled;
        s.View.FogStart = _atmosphere.FogStart;
        s.View.FogEnd = _atmosphere.FogEnd;
        s.View.CullAtFogEnd = _atmosphere.CullAtFogEnd;
        s.View.CoupleFarPlaneToFog = _coupleFarPlaneToFog;
        s.View.DistanceCustom = true;

        s.Lighting.DynamicLighting = _atmosphere.DynamicLighting;
        // NOT UseAuthoredData: since v6 that is a transient probe A/B, and
        // mirroring it here is exactly how it would leak back into the file.
        s.Lighting.Mode = _atmosphere.Mode;
        s.Lighting.SunStrength = _atmosphere.SunStrength;
        s.Lighting.AmbientStrength = _atmosphere.AmbientStrength;
        if (_terrain is not null)
            s.Lighting.TerrainShadowStrength = _terrain.AuthoredShadowStrength;
        if (_unitShadows is not null)
            s.Lighting.UnitShadowOpacity = _unitShadows.Opacity;
        s.Lighting.TimeSource = _timeSource;
        s.Lighting.GameHoursPerMinute = _gameHoursPerMinute;
        // Only Fixed writes the live hour back: under Server / Cycle the clock
        // is derived state, and capturing it would stomp the saved Fixed hour.
        if (_timeSource == TimeSource.Fixed)
            s.Lighting.TimeOfDay = _atmosphere.TimeOfDayHours;

        if (_sky is not null)
        {
            s.Lighting.SkyEnabled = _sky.Enabled;
            s.Lighting.SkyStopMiddle = _sky.StopMiddle;
            s.Lighting.SkyStopBand1 = _sky.StopBand1;
            s.Lighting.SkyStopBand2 = _sky.StopBand2;
        }

        s.Controls.MouseSensitivity = _window.MouseSensitivity;
        s.Controls.RawCursor = _window.RawCursor;
        s.Controls.InvertPitch = _config.Camera.InvertPitch;
        s.Controls.CameraCollision = _config.Camera.Collision;
        s.Controls.CameraClearance = _config.Camera.Clearance;
        s.Controls.CameraRestoreSpeed = _config.Camera.RestoreSpeed;
        s.Controls.EyeHeight = cam.EyeHeight;
        s.Controls.MaxCameraDistance = cam.MaxDistance;
        s.Controls.TurnSpeedDegrees = _turnSpeed * 180f / MathF.PI;

        if (_wmo is not null)
        {
            s.Detail.Buildings = _wmo.Enabled;
            s.Detail.WmoFrustumCulling = _wmo.FrustumCulling;
            s.Detail.DistanceLodShells = _wmo.UseDistanceLodShells;
            s.Detail.ForceTwoSided = _wmo.ForceTwoSided;
            s.Detail.WmoAlphaCutoff = _wmo.AlphaCutoff;
            s.Detail.ImpostorMaxVertices = _wmo.ImpostorMaxVertices;
            s.Detail.InsideMargin = _wmo.InsideInstanceMargin;
            s.Detail.InteriorCullDistance = _wmo.InteriorCullDistance;
            s.Detail.ShellNearGuard = _wmo.ShellNearGuard;
            s.Detail.OcclusionCulling = _wmo.OcclusionCulling;
            s.Detail.OcclusionMinDistance = _wmo.OcclusionMinDistance;
            s.View.BuildingDistance = _wmo.DrawDistance;
            s.Lighting.WmoVertexColors = _wmo.UseVertexColors;
            s.Lighting.InteriorBrightness = _wmo.VertexColorScale;
            s.Lighting.InteriorSpill = _wmo.InteriorBrightness;
            s.Detail.BuildingDetailCustom = true;
        }

        if (_doodads is not null)
        {
            s.Detail.Doodads = _doodads.Enabled;
            s.Detail.DoodadFrustumCulling = _doodads.FrustumCulling;
            s.Detail.DoodadInstancing = _doodads.UseInstancing;
            s.Detail.DoodadFlatCullBounds = _doodads.FlatCullBounds;
            s.Detail.DoodadAlphaCutoff = _doodads.AlphaCutoff;
            s.Detail.DoodadDistance = _doodads.DrawDistance;
            s.Detail.DoodadDemandStreaming = _demandStreamDoodads;
            s.Lighting.DoodadInteriorLighting = _doodads.InteriorLighting;
            s.Lighting.DoodadInteriorBrightness = _doodads.VertexColorScale;
            s.Detail.ObjectDetailCustom = true;
        }

        if (_foliage is not null)
        {
            var f = _foliage;
            s.Clutter.Enabled = f.Enabled;
            s.Clutter.Radius = f.Radius;
            s.Clutter.Density = f.DensityScale;
            s.Clutter.MaxPerCell = f.MaxPerCell;
            s.Clutter.Scale = f.Scale;
            s.Clutter.ScaleJitter = f.ScaleJitter;
            s.Clutter.MaxInstances = f.MaxInstances;
            s.Clutter.RescatterDistance = f.RescatterDistance;
            s.Clutter.WindStrength = f.WindStrength;
            s.Clutter.WindSpeed = f.WindSpeed;
            s.Clutter.LinkFadeToRadius = f.LinkFadeToRadius;
            s.Clutter.FadeStartFraction = f.FadeStartFraction;
            s.Clutter.FadeStart = f.FadeStart;
            s.Clutter.FadeEnd = f.FadeEnd;
            s.Clutter.AlphaCutoff = f.AlphaCutoff;
            s.Clutter.Brightness = f.Brightness;
            s.Clutter.UseCellLayerMap = f.UseCellLayerMap;
            s.Clutter.UseNoDoodadMask = f.UseNoDoodadMask;
            s.Clutter.SkipHoles = f.SkipHoles;
            s.Clutter.SkipDeepLiquidCells = f.SkipDeepLiquidCells;
            s.Clutter.LiquidFoliageMaxDepth = f.LiquidFoliageMaxDepth;

            foreach (FoliageKind kind in Enum.GetValues<FoliageKind>())
            {
                string key = kind.ToString();
                s.Clutter.KindEnabled[key] = f.KindEnabled(kind);
                s.Clutter.KindDensity[key] = f.KindDensity(kind);
            }
        }

        if (_liquid is not null)
        {
            var w = _liquid;
            s.Water.Enabled = w.Enabled;
            s.Water.DrawWmoLiquid = w.WmoLiquidEnabled;
            s.Water.UseAuthoredColors = w.UseAuthoredColors;
            s.Water.WakeEnabled = w.WakeEnabled;
            s.Water.WakeStrength = w.WakeStrength;
            s.Water.WakeLength = w.WakeLength;
            s.Water.WakeWidth = w.WakeWidth;
            s.Water.WakeAhead = w.WakeAhead;
            s.Water.WakeFullSpeed = w.WakeFullSpeed;
            s.Water.WakeFade = w.WakeFade;
            s.Water.WakeRepeat = w.WakeRepeat;
            s.Water.WakeWorldLock = w.WakeWorldLock;
            s.Water.WakeOpacity = w.WakeOpacity;
            s.Water.TextureScale = w.TextureScale;
            s.Water.AnimationFps = w.AnimationFps;
            s.Water.FrameBlend = w.FrameBlend;
            s.Water.TexBrightness = w.TexBrightness;
            s.Water.TexContrast = w.TexContrast;
            s.Water.TintR = w.TexTint.X;
            s.Water.TintG = w.TexTint.Y;
            s.Water.TintB = w.TexTint.Z;
            s.Water.Opacity = w.Opacity;
            s.Water.ShoreFade = w.ShoreFade;
            s.Water.ShoreWidth = w.ShoreWidth;
            s.Water.DepthDarken = w.DepthDarken;
            s.Water.DepthRate = w.DepthRate;
            s.Water.Brightness = w.Brightness;
            s.Water.AmbientAmount = w.AmbientAmount;
            s.Water.SunAmount = w.SunAmount;
            s.Water.SkySheen = w.SkySheen;
            s.Water.WaveAmplitude = w.WaveAmplitude;
            s.Water.WaveSpeed = w.WaveSpeed;
            s.Water.DetailCustom = true;
        }

        s.ActivePreset = "Custom";
    }

    // ── DevTools readout ─────────────────────────────────────────────────────

    /// <summary>
    /// Which Interface paths resolved. DevTools only - an instrument, not a
    /// setting. The layout knobs the first version needed are gone: the edge
    /// layout is now read off the texture rather than dialled in.
    /// </summary>
    private void DrawUiSkinPanel()
    {
        if (_skin is null) return;
        if (!ImGui.CollapsingHeader("UI skin")) return;

        ImGui.Text($"  {_skin.FoundCount}/{_skin.Pieces.Count} texture(s) resolved");

        float scale = _skin.Scale;
        if (ImGui.SliderFloat("Frame art scale", ref scale, 0.5f, 4f, "x%.2f"))
            _skin.Scale = scale;

        bool textured = _skin.Textured;
        if (ImGui.Checkbox("Textured frame", ref textured))
            _skin.Textured = textured;

        if (ImGui.TreeNode("Texture paths"))
        {
            foreach (var piece in _skin.Pieces)
            {
                if (piece.Found)
                    ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f),
                        $"  ok      {piece.Path}  {piece.Width}x{piece.Height}");
                else
                    ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f),
                        $"  MISSING {piece.Path}  ({piece.Note})");
            }
            ImGui.TreePop();
        }
    }
}
