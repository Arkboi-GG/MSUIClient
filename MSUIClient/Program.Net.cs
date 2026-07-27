using System.Numerics;
using System.Text;
using ImGuiNET;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

// Phase 2 networking glue for the game loop. Hooks in Program.cs:
//   Load()    -> InitNet(gl);  and BeginWorldLoad(gl) only runs when OFFLINE
//   Update()  -> PumpNet(dt);  then a guard that skips gameplay until the world loads
//   Gui()     -> NetHud();     login screen -> character select -> in-world status
//   Dispose() -> _net?.Dispose();
//
// The glue flow mirrors the real client (and benilla): Login screen -> Character
// Select (roster, pick a character) -> World. Networked mode stays STATELESS until
// SMSG_LOGIN_VERIFY_WORLD assigns a character + spawn point; only then does
// BeginWorldLoad run. Offline mode is unchanged.
public sealed partial class GameLoop
{
    private NetworkClient? _net;
    private readonly EntityStore _entities = new();   // game-thread-owned world model (from UPDATE_OBJECT)
    private GL? _gl;                                   // kept so we can start the world load after login
    private GlueScene? _glue;                          // the login-screen glue scene (UI_MainMenu)
    private CreatureRenderer? _creatures;              // draws the streamed creatures/NPCs (UPDATE_OBJECT)
    private bool _worldLoadStarted;                   // false until BeginWorldLoad has run (offline or on login)
    private long _netInbound;
    private int _netUpdatesLastFrame;
    private int _creaturesLogged;

    // Login-screen input buffers (password is never persisted anywhere).
    private readonly byte[] _acctBuf = new byte[64];
    private readonly byte[] _passBuf = new byte[128];
    private bool _loginInit;

    // Character-select selection.
    private int _selectedChar;

    /// <summary>Create the network client (does not connect). Called at the end of Load(). Stores gl for the deferred world load.</summary>
    private void InitNet(GL gl)
    {
        _gl = gl;
        if (!_config.Server.Enabled)
        {
            Console.WriteLine("[net] disabled (server.enabled = false) - offline mode");
            return;
        }

        // Live mode: do NOT render the offline debug character (the base HumanMale in Battlegear).
        // benilla shows no character at the login screen; the selected character appears only at
        // character-select, and the real in-world player model comes from the roster on login.
        if (_character is not null) _character.Enabled = false;

        // The login-screen glue scene (UI_MainMenu burning gate). Best-effort; draws only if it loads.
        try { if (_mpq is not null) _glue = new GlueScene(gl, _mpq); }
        catch (Exception ex) { Console.WriteLine($"[glue] init failed: {ex.Message}"); }

        // The networked creature/NPC renderer. Loads the creature DBCs; draws
        // every streamed Unit as its M2 once we are in world. Best-effort.
        try { if (_mpq is not null) _creatures = new CreatureRenderer(gl, _mpq); }
        catch (Exception ce) { Console.WriteLine($"[creature] init failed: {ce.Message}"); }

        try
        {
            _net = new NetworkClient(_config.ToNetSettings());
            if (_config.Server.AutoConnect &&
                !string.IsNullOrWhiteSpace(_config.Server.Account) &&
                !string.IsNullOrWhiteSpace(_config.Server.Password))
            {
                _net.Login(_config.Server.Account, _config.Server.Password);
                Console.WriteLine($"[net] auto-login as {_config.Server.Account}");
            }
            else
            {
                Console.WriteLine("[net] ready - log in via the in-client login screen");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[net] init failed: {ex.Message}");
            _net = null;
        }
    }

    /// <summary>Pump the network client once per frame. Called near the top of Update(dt), before the world-load guard.</summary>
    private void PumpNet(float dt)
    {
        if (_net is null) return;

        // The server has assigned our character + spawn point (SMSG_LOGIN_VERIFY_WORLD / SMSG_NEW_WORLD).
        if (_net.TakeEnterWorld() is { } enter && _controller is not null)
        {
            _entities.Clear();
            _creaturesLogged = 0;

            // Commit to the server-authoritative spawn. BeginWorldLoad reads _config.Start for the
            // load centre, and its Finish phase teleports us onto real ground there.
            _config.Start.Map = (int)enter.Map;
            _config.Start.X = enter.Position.X;
            _config.Start.Y = enter.Position.Y;
            _config.Start.Z = enter.Position.Z;
            _config.Start.Orientation = enter.Orientation;
            _controller.Teleport(enter.Position.X, enter.Position.Y, enter.Position.Z);
            _controller.Yaw = enter.Orientation;
            _window.Camera.Target = _controller.Position;

            if (!_worldLoadStarted)
            {
                Console.WriteLine($"[net] entering world: map {enter.Map} at " +
                                  $"({enter.Position.X:F0}, {enter.Position.Y:F0}, {enter.Position.Z:F0}) - loading");
                if (_gl is not null) BeginWorldLoad(_gl);
                _worldLoadStarted = true;
                _glue?.Dispose(); _glue = null;   // login art no longer needed once in world

                // Build the player avatar from the LOGGED-IN character (race/gender/skin/hair +
                // the roster's 19 equipment display ids), instead of the offline test body.
                ApplyServerCharacter();
            }
            else
            {
                Console.WriteLine($"[net] moved to map {enter.Map} at " +
                                  $"({enter.Position.X:F0}, {enter.Position.Y:F0}, {enter.Position.Z:F0})");
            }
        }

        // Drain + dispatch the inbound packet stream into the entity store.
        int updates = 0;
        while (_net.TryDequeue(out ushort opcode, out byte[] body))
        {
            _netInbound++;
            try
            {
                switch ((Op)opcode)
                {
                    case Op.SMSG_UPDATE_OBJECT:
                        ApplyUpdates(UpdateObjectParser.Parse(body));
                        updates++;
                        break;
                    case Op.SMSG_COMPRESSED_UPDATE_OBJECT:
                        ApplyUpdates(UpdateObjectParser.ParseCompressed(body));
                        updates++;
                        break;
                    case Op.SMSG_DESTROY_OBJECT:
                        _entities.Remove(new PacketReader(body).ReadU64());
                        break;
                    case Op.SMSG_MONSTER_MOVE:
                        {
                            // Creature locomotion: attach the server spline so this NPC walks.
                            var mm = MonsterMoveParser.Parse(body);
                            if (mm is not null) _entities.ApplyMonsterMove(mm, MovementInfo.ClientUptimeMs());
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[net] parse error on opcode 0x{opcode:X4}: {ex.Message}");
            }
        }
        _netUpdatesLastFrame = updates;

        // Advance every in-progress creature spline so NPCs actually move between packets.
        _entities.TickSplines(MovementInfo.ClientUptimeMs());
    }

    private void ApplyUpdates(List<ObjectUpdate> updates)
    {
        foreach (var u in updates)
        {
            _entities.Apply(u);
            if ((u.Kind is UpdateKind.CreateObject or UpdateKind.CreateObject2) &&
                u.Type == ObjectTypeId.Unit && _creaturesLogged < 50)
            {
                _creaturesLogged++;
                var p = u.Movement?.Position ?? Vector3.Zero;
                Console.WriteLine($"[net] creature entry {u.Fields?.Entry ?? GuidInfo.Entry(u.Guid) ?? 0} " +
                                  $"display {u.Fields?.DisplayId ?? 0} L{u.Fields?.Level ?? 0} at ({p.X:F0}, {p.Y:F0}, {p.Z:F0})");
            }
        }
    }

    /// <summary>
    /// Configure the player avatar from the logged-in roster character instead of the
    /// offline test body. Faithful to benilla: the picked roster Character carries every
    /// appearance byte AND 19 already-resolved equipment display ids, which is sufficient
    /// to build the correct model at spawn (benilla's char-select preview builds the exact
    /// same composited model straight from the roster).
    /// </summary>
    private void ApplyServerCharacter()
    {
        if (_character is null) return;
        Character? c = _net?.Player;
        if (c is null) { _character.Enabled = true; return; }

        string race = RaceFolder(c.Race);
        string gender = c.Gender == 1 ? "Female" : "Male";

        // Re-load the base model only if the race/gender differs from what is loaded
        // (the offline test body is Human/Male; most logins will differ).
        if (!race.Equals(_character.Race, StringComparison.OrdinalIgnoreCase) ||
            !gender.Equals(_character.Gender, StringComparison.OrdinalIgnoreCase))
        {
            if (!_character.Load(race, gender))
                Console.WriteLine($"[character] could not load {race} {gender}; keeping current body");
        }

        _character.SkinId = c.Skin;
        _character.FaceId = c.Face;
        _character.HairStyleId = c.HairStyle;
        _character.HairColorId = c.HairColor;
        _character.FacialHairId = c.FacialHair;
        _character.Equipment = BuildEquipment(c);
        _character.Reload();          // rebuild texture slots + geosets, then composite the gear
        _character.Enabled = true;

        Console.WriteLine($"[character] player model: {race} {gender} " +
                          $"skin {c.Skin} face {c.Face} hair {c.HairStyle}/{c.HairColor} facial {c.FacialHair} " +
                          $"({c.Equipment.Count(e => e.DisplayId != 0)} equipped)");
    }

    /// <summary>Turn the roster's 19 visible-item display ids into a dressable equipment set.</summary>
    private static CharacterEquipment BuildEquipment(Character c)
    {
        const uint HideHelm = 0x400, HideCloak = 0x800;   // CHARACTER_FLAG_HIDE_HELM / _HIDE_CLOAK
        var kit = new CharacterEquipment();
        for (int i = 0; i < c.Equipment.Length; i++)
        {
            var eq = c.Equipment[i];
            if (eq.DisplayId == 0) continue;
            int inv = eq.InventoryType;
            if (inv == CharacterEquipment.Slot.Head && (c.Flags & HideHelm) != 0) continue;
            if (inv == CharacterEquipment.Slot.Cloak && (c.Flags & HideCloak) != 0) continue;
            kit.Add($"slot{i}", eq.DisplayId, inv);
        }
        return kit;
    }

    /// <summary>ChrRaces id -> character model folder name (Undead's folder is "Scourge").</summary>
    private static string RaceFolder(byte race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "NightElf",
        5 => "Scourge", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "Human"
    };

    /// <summary>Draw the login glue scene (UI_MainMenu) behind the glue UI. Called from Render(), networked + pre-world only.</summary>
    private void DrawGlueScene()
    {
        if (_glue is not { Ok: true } || _gl is null) return;
        if (!_config.Server.Enabled || _net is null || _worldLoadStarted) return;

        Span<int> vp = stackalloc int[4];
        _gl.GetInteger(GetPName.Viewport, vp);
        int w = vp[2] > 0 ? vp[2] : _config.Window.Width;
        int h = vp[3] > 0 ? vp[3] : _config.Window.Height;
        _glue.Render(w, h);
    }

    /// <summary>Draw the streamed creatures/NPCs. Called from Render() world pass; in-world only.</summary>
    private void DrawCreatures()
    {
        if (_creatures is null || _net is null || !_net.IsInWorld) return;
        _creatures.Render(_window.Camera, _entities);
    }

    // ── Glue screens: Login -> Character Select -> in-world status ──────────────────────────────

    /// <summary>Called from Gui(). Draws whichever glue/status screen matches the connection state.</summary>
    private void NetHud()
    {
        if (!_config.Server.Enabled || _net is null) return;

        NetState st = _net.State;
        if (st == NetState.InWorld) { DrawInWorldPanel(); return; }

        // All pre-world screens are centered so they read as glue screens, not stray dev panels.
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(460, 0), ImGuiCond.Always);

        if (st == NetState.CharacterSelect) DrawCharacterSelect();
        else if (st is NetState.Idle or NetState.Failed or NetState.Disconnected) DrawLoginScreen();
        else DrawConnecting();
    }

    private void DrawLoginScreen()
    {
        if (!ImGui.Begin("Login", ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }
        if (!_loginInit) { WriteBuf(_acctBuf, _config.Server.Account ?? ""); _loginInit = true; }

        ImGui.TextUnformatted("Log in to your realm");
        ImGui.TextDisabled($"{_config.RealmdHost}:{_config.RealmdPort}");
        ImGui.Separator();
        ImGui.Spacing();
        // Type normally — the account is uppercased on the wire by SRP (Srp6Client.Normalize),
        // so forcing caps in the box is wrong (it made the field caps-only).
        ImGui.InputText("Account", _acctBuf, (uint)_acctBuf.Length);
        bool submit = ImGui.InputText("Password", _passBuf, (uint)_passBuf.Length,
                                      ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.Spacing();
        if (ImGui.Button("Log In", new Vector2(150, 0)) || submit)
        {
            string a = BufToString(_acctBuf), p = BufToString(_passBuf);
            if (a.Length > 0 && p.Length > 0) _net!.Login(a, p);
        }
        if (_net!.State == NetState.Failed)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), _net.Status);
        }
        ImGui.End();
    }

    private void DrawConnecting()
    {
        if (!ImGui.Begin("Connecting", ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }
        ImGui.TextUnformatted($"{_net!.State}...");
        ImGui.TextWrapped(_net.Status);
        ImGui.Spacing();
        if (ImGui.Button("Cancel")) { _net.Stop(); Array.Clear(_passBuf); }
        ImGui.End();
    }

    private void DrawCharacterSelect()
    {
        if (!ImGui.Begin("Character Select", ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }
        var chars = _net!.Characters;

        ImGui.TextUnformatted("Character Select");
        ImGui.TextDisabled(_net.Status);
        ImGui.Separator();

        if (chars.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped("No characters on this account. Create one in another 1.12 client for now " +
                              "(in-client character creation is the next step).");
            ImGui.Spacing();
        }
        else
        {
            if (_selectedChar >= chars.Count) _selectedChar = 0;
            ulong enterGuid = 0;
            for (int i = 0; i < chars.Count; i++)
            {
                Character c = chars[i];
                if (ImGui.Selectable($"{c.Name}##{i}", i == _selectedChar,
                        ImGuiSelectableFlags.AllowDoubleClick, new Vector2(150, 0)))
                {
                    _selectedChar = i;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) enterGuid = c.Guid;
                }
                ImGui.SameLine(170);
                ImGui.TextDisabled($"Level {c.Level} {RaceName(c.Race)} {ClassName(c.Class)}" +
                                   (c.IsGhost ? "  (dead)" : ""));
            }
            if (enterGuid != 0) _net.SelectCharacter(enterGuid);
        }

        ImGui.Separator();
        if (chars.Count > 0 && ImGui.Button("Enter World", new Vector2(150, 0)))
            _net.SelectCharacter(chars[_selectedChar].Guid);
        ImGui.SameLine();
        if (ImGui.Button("Back")) { _net.Stop(); Array.Clear(_passBuf); }
        ImGui.End();
    }

    private void DrawInWorldPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(390, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Server", ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }
        ImGui.TextUnformatted($"player: {_net!.PlayerName}   guid 0x{_net.PlayerGuid:X}");
        ImGui.TextUnformatted($"entities: {_entities.Count}  (creatures {_entities.CreatureCount}, players {_entities.PlayerCount})");
        ImGui.TextUnformatted($"packets in: {_netInbound}  (updates last frame {_netUpdatesLastFrame})");
        ImGui.TextUnformatted($"moving (splines): {_entities.MovingCount}");
        if (_creatures is not null)
        {
            ImGui.TextUnformatted($"creatures drawn: {_creatures.DrawnLastFrame}" +
                                  (_creatures.Ok ? "" : "  (renderer off - creature DBCs missing)"));
            bool drawC = _creatures.Enabled;
            if (ImGui.Checkbox("Draw creatures", ref drawC)) _creatures.Enabled = drawC;
            float hoff = _creatures.HeadingOffsetDegrees;
            if (ImGui.SliderFloat("Creature heading deg", ref hoff, -180f, 180f)) _creatures.HeadingOffsetDegrees = hoff;
            float cscale = _creatures.ScaleMultiplier;
            if (ImGui.SliderFloat("Creature scale x", ref cscale, 0.1f, 3f)) _creatures.ScaleMultiplier = cscale;
            ImGui.TextUnformatted($"animated: {_creatures.AnimatedLastFrame}");
            bool animC = _creatures.Animate;
            if (ImGui.Checkbox("Animate creatures", ref animC)) _creatures.Animate = animC;
            float adist = _creatures.AnimateDistance;
            if (ImGui.SliderFloat("Animate distance", ref adist, 20f, 300f)) _creatures.AnimateDistance = adist;
        }
        if (_controller is not null && ImGui.CollapsingHeader("Nearest units"))
        {
            Vector3 me = _controller.Position;
            foreach (var e in _entities.NearestUnits(me, 12))
                ImGui.TextUnformatted(
                    $"{Vector3.Distance(e.Position, me),5:F0}yd  " +
                    $"{(e.IsPlayer ? "player" : $"npc {e.Entry}")}  disp {e.DisplayId}  L{e.Level}  " +
                    $"hp {e.HealthFraction * 100f,3:F0}%{(e.IsMoving ? "  walking" : "")}");
        }
        ImGui.Separator();
        if (ImGui.Button("Disconnect")) { _net.Stop(); Array.Clear(_passBuf); }
        ImGui.End();
    }

    private static string RaceName(byte r) => r switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "Night Elf",
        5 => "Undead", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "?"
    };

    private static string ClassName(byte c) => c switch
    {
        1 => "Warrior", 2 => "Paladin", 3 => "Hunter", 4 => "Rogue", 5 => "Priest",
        7 => "Shaman", 8 => "Mage", 9 => "Warlock", 11 => "Druid", _ => "?"
    };

    private static string BufToString(byte[] b)
    {
        int n = Array.IndexOf(b, (byte)0);
        if (n < 0) n = b.Length;
        return Encoding.UTF8.GetString(b, 0, n);
    }

    private static void WriteBuf(byte[] b, string s)
    {
        Array.Clear(b);
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        Array.Copy(bytes, b, Math.Min(bytes.Length, b.Length - 1));
    }
}
