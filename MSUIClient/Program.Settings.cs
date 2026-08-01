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
///   GameMenuFrame  195 x 226, buttons 144 x 21, first button 37 below the top,
///                  1px gaps, and a 16px gap before Continue.
///   OptionsFrame   450 x 575 in vanilla. Ours is larger because this client
///                  exposes several times as many controls - that is the one
///                  deliberate departure, and the content scrolls.
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
    private enum MenuPage { GameMenu = 0, Video, Controls, Streaming }

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

    /// <summary>Last frame's measured height per group box, so the backdrop can be drawn first.</summary>
    private readonly Dictionary<string, float> _boxHeights = new();
    private Vector2 _boxStart;
    private string _boxId = "";

    /// <summary>True while the menu owns input. Read by Update to stop the player walking.</summary>
    public bool SettingsModalOpen => _settingsOpen;

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
        bool escape = _window.IsDown(Silk.NET.Input.Key.Escape);
        if (escape && !_escapeKeyDown)
        {
            // 1.12 Escape order: stop casting first, then close open panels (the loot
            // window), and only then open the game menu.
            if (!TryCancelSpellOnEscape() && !TryCloseLootOnEscape()) _escapePressed = true;
        }
        _escapeKeyDown = escape;
    }

    private void OpenSettings()
    {
        _settingsSnapshot = Settings.Clone();
        _settingsCancelling = false;
        _settingsOpen = true;
        _settingsPopupRequested = true;
        _menuPage = MenuPage.GameMenu;
        _settingsStatus = "";
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
            ImGui.OpenPopup(MenuPopupId);
            _settingsPopupRequested = false;
        }

        if (!_settingsOpen) { _escapePressed = false; return; }

        var io = ImGui.GetIO();
        var centre = new Vector2(io.DisplaySize.X * 0.5f, io.DisplaySize.Y * 0.5f);
        var size = PageSize(io.DisplaySize);

        ImGui.SetNextWindowPos(centre, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);

        _skin?.PushStyle();

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings;

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
            var min = ImGui.GetWindowPos();
            var max = min + ImGui.GetWindowSize();
            var dl = ImGui.GetWindowDrawList();
            BeginUiParityFrame(min);
            string parityRoot=_uiParityPanel=="options"?"OptionsFrame":"GameMenuFrame";
            CollectUiParityDraw(parityRoot,"Frame",min,size,"",
                new("",0,"IMGUI_HOST","CENTER","","",0,0));

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
            Vector2 headerMin=min+new Vector2((size.X-256f*S)*.5f,-12f*S),headerSize=new Vector2(256f,64f)*S;
            string parityHeader=_uiParityPanel=="options"?"OptionsFrameHeader":"GameMenuFrameHeader";
            CollectUiParityDraw(parityHeader,"Texture",headerMin,headerSize,parityRoot,
                new(@"Interface\DialogFrame\UI-DialogBox-Header",0xffffffff,"IMGUI_IMAGE","TOP",parityRoot,"TOP",0,12));
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
                case MenuPage.Streaming: DrawStreamingPage(size); break;
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

        if (!_settingsCancelling) CommitSettings();
        _settingsCancelling = false;
    }

    private string PageTitle() => _menuPage switch
    {
        MenuPage.Video => "Video Options",
        MenuPage.Controls => "Camera and Controls",
        MenuPage.Streaming => "Streaming",
        _ => "Main Menu",
    };

    /// <summary>
    /// GameMenuFrame is 195x226 in vanilla with seven buttons. Ours is derived
    /// from the same constants so it stays right when a button is added.
    /// OptionsFrame is 450x575; ours is bigger because this client has several
    /// times as many controls, and it scrolls.
    /// </summary>
    private Vector2 PageSize(Vector2 display)
    {
        if (_menuPage == MenuPage.GameMenu)
        {
            // patch.MPQ's build-5875 GameMenuFrame.xml is authoritative: 195x246.
            return new Vector2(195f, 246f) * S;
        }

        // Vanilla's OptionsFrame is 450 wide. Ours is 540 because this client
        // exposes several times as many controls, but it is sized in UI PIXELS,
        // not as a fraction of the screen. Run 2 used 52% of the display, which
        // on a wide panel produced a 2000-pixel frame full of 1500-pixel sliders
        // - proportions that exist in no version of WoW.
        float w = MathF.Min(540f * S, display.X * 0.94f);
        float ht = MathF.Min(620f * S, display.Y * 0.90f);
        return new Vector2(w, ht);
    }

    // ── the Game Menu ────────────────────────────────────────────────────────

    private void DrawGameMenu(Vector2 size)
    {
        var button = WowSkin.MenuButton * S;
        float x = (size.X - button.X) * 0.5f;

        void Row(string id, string label, float y, string point, string relativeTo,
            string relativePoint, string offsetY, bool enabled, Action onClick, string? tip = null)
        {
            ImGui.SetCursorPos(new Vector2(x, y * S));
            Vector2 actualMin = ImGui.GetCursorScreenPos();
            CollectUiParityDraw(id,"Button",actualMin,button,"GameMenuFrame",
                new(@"Interface\Buttons\UI-Panel-Button-Up",0xffffffff,"IMGUI_BUTTON",point,relativeTo,relativePoint,0,float.Parse(offsetY,System.Globalization.CultureInfo.InvariantCulture),@"Fonts\FRIZQT__.TTF",12));
            if (Button(label, button, enabled)) onClick();
            if (tip is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        }

        Row("GameMenuButtonOptions", "Video Options", 26.5f, "CENTER", "", "TOP", "-37",
            true, () => Go(MenuPage.Video));
        Row("GameMenuButtonSoundOptions", "Sound Options", 48.5f, "TOP", "GameMenuButtonOptions", "BOTTOM", "-1",
            false, () => { },
            "There is no sound subsystem yet - this button exists so the menu does\n" +
            "not change shape when there is.");
        Row("GameMenuButtonUIOptions", "Interface Options", 70.5f, "TOP", "GameMenuButtonSoundOptions", "BOTTOM", "-1",
            true, () => Go(MenuPage.Controls));
        Row("GameMenuButtonKeybindings", "Key Bindings", 92.5f, "TOP", "GameMenuButtonUIOptions", "BOTTOM", "-1",
            true, () => { _settingsOpen=false;ImGui.CloseCurrentPopup();_keybindingsOpen=true; });
        Row("GameMenuButtonMacros", "Macros", 114.5f, "TOP", "GameMenuButtonKeybindings", "BOTTOM", "-1",
            true, () => { _settingsOpen=false;ImGui.CloseCurrentPopup();_macroOpen=true; });
        Row("GameMenuButtonLogout", "Logout", 136.5f, "TOP", "GameMenuButtonMacros", "BOTTOM", "-1",
            false, () => { }, "Character logout is not built yet.");

        // NOT _window.Close() - that runs the whole teardown synchronously and
        // the rest of this ImGui frame then draws into freed memory. Flag it and
        // let Update act between frames. See ConsumeQuitRequest.
        Row("GameMenuButtonQuit", "Exit Game", 158.5f, "TOP", "GameMenuButtonLogout", "BOTTOM", "-1",
            true, () => _quitRequested = true);
        Row("GameMenuButtonContinue", "Return to Game", 195.5f, "TOP", "GameMenuButtonQuit", "BOTTOM", "-16", true, () =>
        {
            CommitSettings();
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

        float footer = (WowSkin.ButtonArt.Y * 1.6f + 40f) * S;
        float bodyHeight = MathF.Max(ImGui.GetContentRegionAvail().Y - footer, 100f);

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
                    float v = Math.Clamp(s.Display.UiScale, 0.5f, 4f);
                    ImGui.GetIO().FontGlobalScale = v;
                    if (_skin is not null) _skin.Scale = v;
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
                Check("Time-of-day lighting", () => s.Lighting.DynamicLighting,
                    v => s.Lighting.DynamicLighting = v);
                Check("Use authored lighting data (Light.dbc)", () => s.Lighting.UseAuthoredData,
                    v => s.Lighting.UseAuthoredData = v,
                    "On resolves the real zone lighting for your position and time out of the\n" +
                    "MPQs. Off falls back to hand-invented constants that\n" +
                    "SYSTEM_EXTERIOR_LIGHTING.md replaced with data. Leave it on.");

                if (ImGui.TreeNode("Advanced##lighting"))
                {
                    Slider("sun", "Sun strength", () => s.Lighting.SunStrength,
                        v => s.Lighting.SunStrength = v, 0f, 2f, "{0:F2}");
                    Slider("amb", "Ambient strength", () => s.Lighting.AmbientStrength,
                        v => s.Lighting.AmbientStrength = v, 0f, 2f, "{0:F2}");

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

                    Check("Cycle time of day", () => s.Lighting.CycleTimeOfDay,
                        v => { s.Lighting.CycleTimeOfDay = v; _cycleTimeOfDay = v; });
                    Slider("ghpm", "Game hours per minute", () => s.Lighting.GameHoursPerMinute,
                        v => { s.Lighting.GameHoursPerMinute = v; _gameHoursPerMinute = v; }, 0.1f, 12f, "{0:F1}");
                    Slider("tod", "Time of day", () => s.Lighting.TimeOfDay,
                        v => { s.Lighting.TimeOfDay = v; _atmosphere.TimeOfDayHours = v; }, 0f, 24f, "{0:F2} h");
                    ImGui.TreePop();
                }
            }
            EndBox();
        }
        ImGui.EndChild();

        DrawPanelFooter(size, presets: true);
    }

    // ── the other pages ──────────────────────────────────────────────────────

    private void DrawControlsPage(Vector2 size)
    {
        var s = Settings;
        float footer = (WowSkin.ButtonArt.Y * 1.6f + 40f) * S;
        float bodyHeight = MathF.Max(ImGui.GetContentRegionAvail().Y - footer, 100f);

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

        DrawPanelFooter(size, presets: false);
    }

    private void DrawStreamingPage(Vector2 size)
    {
        var s = Settings;
        float footer = (WowSkin.ButtonArt.Y * 1.6f + 40f) * S;
        float bodyHeight = MathF.Max(ImGui.GetContentRegionAvail().Y - footer, 100f);

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

        DrawPanelFooter(size, presets: false);
    }

    // ── footer ───────────────────────────────────────────────────────────────

    private void DrawPanelFooter(Vector2 size, bool presets)
    {
        var button = new Vector2(WowSkin.ButtonArt.X * 1.35f, WowSkin.ButtonArt.Y * 1.15f) * S;

        if (presets)
        {
            ImGui.SetNextItemWidth(180f * S);
            ImGui.InputText("##presetName", ref _presetNameInput, 48u);
            ImGui.SameLine();

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

                ImGui.SameLine();
                ImGui.SetNextItemWidth(170f * S);
                ImGui.Combo("##presetPick", ref _selectedPreset, names, names.Length);

                ImGui.SameLine();
                if (Button("Load", button))
                {
                    var preset = SettingsFile.Presets[_selectedPreset];
                    var loaded = preset.Settings.Clone();
                    loaded.ResolveComposites();
                    SettingsFile.Replace(loaded);
                    ApplySettings(loaded);
                    _settingsStatus = $"loaded preset '{preset.Name}'";
                }

                ImGui.SameLine();
                if (Button("Delete", button))
                {
                    string gone = SettingsFile.Presets[_selectedPreset].Name;
                    SettingsFile.DeletePreset(gone);
                    _selectedPreset = 0;
                    _settingsStatus = $"deleted preset '{gone}'";
                }
            }
        }

        if (Button("Defaults", button))
        {
            ResetVisiblePageToDefaults();
            _settingsStatus = "page reset to shipped defaults";
        }

        ImGui.SameLine();
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

        ImGui.SameLine();
        if (Button("Cancel", button)) CancelSettings();

        ImGui.SameLine();
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
            case MenuPage.Streaming:
                s.Streaming = d.Streaming;
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
        _window.MultisamplingEnabled = s.Display.MultisamplingEnabled;
        if (_skin is not null) _skin.Textured = s.Display.TexturedFrame;

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
        _atmosphere.UseAuthoredData = s.Lighting.UseAuthoredData;
        _atmosphere.SunStrength = s.Lighting.SunStrength;
        _atmosphere.AmbientStrength = s.Lighting.AmbientStrength;

        _cycleTimeOfDay = s.Lighting.CycleTimeOfDay;
        _gameHoursPerMinute = s.Lighting.GameHoursPerMinute;

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
        s.Display.MultisamplingEnabled = _window.MultisamplingEnabled;

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
        s.Lighting.UseAuthoredData = _atmosphere.UseAuthoredData;
        s.Lighting.SunStrength = _atmosphere.SunStrength;
        s.Lighting.AmbientStrength = _atmosphere.AmbientStrength;
        s.Lighting.CycleTimeOfDay = _cycleTimeOfDay;
        s.Lighting.GameHoursPerMinute = _gameHoursPerMinute;
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
