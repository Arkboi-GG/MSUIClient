using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const float GameObjectInteractDistance = 6f;
    private const float MagePortalClickInteractDistance = 10f;
    private ulong _gameObjectGuid;
    private uint _gameObjectAnimation;
    private readonly List<(uint Id, string Text, uint Next)> _gameObjectPages = [];
    private sealed record GameObjectTemplate(uint Entry, uint Type, uint DisplayId, string Name,
        string Icon, int[] Data)
    {
        public uint LockId => Type switch
        {
            0 or 1 => unchecked((uint)Math.Max(0, Data[1])),
            2 or 3 or 6 or 10 or 12 or 13 or 24 or 26 => unchecked((uint)Math.Max(0, Data[0])),
            25 => unchecked((uint)Math.Max(0, Data[4])),
            _ => 0
        };
    }
    private readonly Dictionary<uint, GameObjectTemplate> _gameObjectTemplates = [];
    private readonly HashSet<uint> _gameObjectTemplatePending = [];
    private LockCatalog? _locks;
    private SpellFocusCatalog? _spellFoci;

    private static string GameObjectKind(uint type) => type switch
    {
        0 => "door", 1 => "button/lever", 2 => "questgiver", 3 => "chest",
        5 => "generic", 6 => "trap", 8 => "spell-focus", 9 => "text",
        10 => "goober", 19 => "mailbox", 22 => "spellcaster", _ => $"type-{type}"
    };

    private void ResetGameObjects()
    {
        _gameObjectGuid = 0; _gameObjectAnimation = 0; _gameObjectPages.Clear();
    }

    private void RequireGameObjectTemplate(WorldEntity entity)
    {
        if (!entity.IsGameObject || entity.Entry == 0 || _gameObjectTemplates.ContainsKey(entity.Entry) ||
            !_gameObjectTemplatePending.Add(entity.Entry)) return;
        _net?.GameObjectQuery(entity.Entry, entity.Guid);
        EmitInterface("gathering", "template-query", "SENT", entity.Guid, $"entry={entity.Entry}");
    }

    private void ApplyGameObjectQuery(byte[] body)
    {
        var r = new PacketReader(body);
        uint rawEntry = r.ReadU32();
        uint entry = rawEntry & 0x7fff_ffffu;
        _gameObjectTemplatePending.Remove(entry);
        if ((rawEntry & 0x8000_0000u) != 0)
        {
            EmitInterface("gathering", "template", "MISSING", 0, $"entry={entry}");
            return;
        }
        uint type = r.ReadU32(), display = r.ReadU32();
        string name = r.ReadCString();
        r.ReadCString(); r.ReadCString(); r.ReadCString();
        string icon = r.ReadCString();
        var data = new int[24];
        for (int i = 0; i < data.Length; i++) data[i] = r.ReadI32();
        var template = new GameObjectTemplate(entry, type, display, name, icon, data);
        _gameObjectTemplates[entry] = template;
        EnsureLockCatalog();
        uint lockType = _locks?.ResourceLockType(template.LockId) ?? 0;
        EmitInterface("gathering", "template", "DECODED", 0,
            $"entry={entry};name={SanitizeEvidence(name)};type={type};display={display};lock={template.LockId};lockType={lockType};resource={ResourceKind(lockType)}");
    }

    private void EnsureLockCatalog()
    {
        if (_locks is not null || _mpq is null) return;
        _locks = LockCatalog.Load(_mpq);
        EmitInterface("gathering", "lock-catalog", _locks is null ? "FAILED" : "LOADED", 0,
            $"rows={_locks?.Count ?? 0}");
    }

    private void EnsureSpellFocusCatalog()
    {
        if (_spellFoci is not null || _mpq is null) return;
        _spellFoci = SpellFocusCatalog.Load(_mpq);
    }

    private string SpellFocusName(uint focusId)
    {
        EnsureSpellFocusCatalog();
        return _spellFoci?.Name(focusId) ?? $"Spell Focus {focusId}";
    }

    private bool HasNearbySpellFocus(uint focusId)
    {
        if (focusId == 0) return true;
        if (_controller is null) return false;
        foreach (WorldEntity go in _entities.Entities.Values.Where(x => x.IsGameObject))
        {
            RequireGameObjectTemplate(go);
            if (!_gameObjectTemplates.TryGetValue(go.Entry, out GameObjectTemplate? template) ||
                template.Type != 8 || template.Data.Length < 2 ||
                unchecked((uint)Math.Max(0, template.Data[0])) != focusId) continue;
            float limit = Math.Clamp(template.Data[1], 0, 10);
            if (limit > 0 && Vector3.Distance(go.Position, _controller.Position) <= limit) return true;
        }
        return false;
    }

    private static string ResourceKind(uint lockType) => lockType switch
    {
        2 => "herb", 3 => "mineral", _ => "none"
    };

    private bool UseGameObject(ulong guid)
    {
        WorldEntity? go = null;
        float distance = float.PositiveInfinity;
        float interactDistance = GameObjectInteractDistance;
        string outcome;
        if (_net is not { IsInWorld: true } || _controller is null) outcome = "REFUSED_NOT_IN_WORLD";
        else if (!_entities.TryGet(guid, out go) || !go.IsGameObject) outcome = "REFUSED_NOT_GAMEOBJECT";
        else if ((distance = Vector3.Distance(_controller.Position, go.Position)) >
                 (interactDistance = IsStockPortalEntry(go.Entry)
                     ? MagePortalClickInteractDistance
                     : GameObjectInteractDistance)) outcome = "REFUSED_RANGE";
        else if (go.GameObjectType == 19)
        {
            // Mailbox use is a local panel open. The mail window's CheckInbox equivalent owns the
            // first CMSG_GET_MAIL_LIST; build 5875 sends no CMSG_GAMEOBJ_USE for a mailbox.
            outcome = RequestMail(guid) ? "OPENED_MAIL" : "REFUSED_MAIL";
        }
        else
        {
            uint opener = FindKnownOpenLockSpell(go.Entry);
            if (opener != 0)
            {
                outcome = _net.CastSpellOnGameObject(opener, guid) ? "SENT_OPEN_LOCK_SPELL" : "SEND_FAILED";
                if (outcome.StartsWith("SENT", StringComparison.Ordinal)) _pendingCastSpell = opener;
            }
            else
            {
                bool sent = _net.GameObjectUse(guid);
                outcome = sent ? "SENT" : "SEND_FAILED";
                // Arm only after a READY portal's ordinary authoritative use
                // was successfully queued. Proximity, clicks on other objects,
                // and failed sends retain the normal loading-screen path.
                if (sent && IsStockPortalEntry(go.Entry))
                    ArmRealPortalHandoffAfterSuccessfulUse(guid);
            }
        }
        if (outcome.StartsWith("SENT", StringComparison.Ordinal))
        { _gameObjectGuid = guid; _gameObjectAnimation = 0; _gameObjectPages.Clear(); }
        uint lockSpell = go is null ? 0 : FindKnownOpenLockSpell(go.Entry);
        string body = go?.GameObjectType == 19 ? "LOCAL_MAIL_OPEN" : lockSpell != 0
            ? Convert.ToHexString(WorldSession.BuildCastSpellOnGameObjectBody(lockSpell, guid))
            : Convert.ToHexString(WorldSession.BuildGameObjectUseBody(guid));
        EmitInterface("gameobject", "use", outcome, guid,
            $"entry={go?.Entry ?? 0};type={go?.GameObjectType ?? 0};kind={GameObjectKind(go?.GameObjectType ?? uint.MaxValue)};distance={distance:R};limit={interactDistance:R};openSpell={lockSpell};body={body}");
        return outcome.StartsWith("SENT", StringComparison.Ordinal) ||
            outcome.Equals("OPENED_MAIL", StringComparison.Ordinal);
    }

    private uint FindKnownOpenLockSpell(uint gameObjectEntry)
    {
        if (!_gameObjectTemplates.TryGetValue(gameObjectEntry, out GameObjectTemplate? template) ||
            template.LockId == 0 || _spellCatalog is null) return 0;
        EnsureLockCatalog();
        if (_locks is null) return 0;
        HashSet<int> lockTypes = _locks.Slots(template.LockId)
            .Where(slot => slot.KeyType == LockCatalog.KeySkill)
            .Select(slot => unchecked((int)slot.Index)).ToHashSet();
        if (lockTypes.Count == 0) return 0;
        foreach (uint known in _actions.KnownSpells.OrderBy(id => id))
        {
            if (!_spellCatalog.TryGet(known, out SpellInfo spell) ||
                spell.EffectIds is not { } effects || spell.EffectMiscValues is not { } misc) continue;
            for (int lane = 0; lane < Math.Min(effects.Length, misc.Length); lane++)
                if (effects[lane] == 33 && lockTypes.Contains(misc[lane])) return known;
        }
        return 0;
    }

    private void SnapshotGameObjects()
    {
        var rows = _entities.Entities.Values.Where(x => x.IsGameObject)
            .OrderBy(x => _controller is null ? 0 : Vector3.Distance(x.Position, _controller.Position)).Take(128).ToArray();
        foreach (WorldEntity go in rows)
        {
            float distance = _controller is null ? float.PositiveInfinity : Vector3.Distance(go.Position, _controller.Position);
            EmitInterface("gameobject", "presence", "OBSERVED", go.Guid,
                $"entry={go.Entry};type={go.GameObjectType};kind={GameObjectKind(go.GameObjectType)};distance={distance:R};display={go.Fields.GameObjectDisplayId}");
        }
        uint[] required = [0, 1, 3, 8];
        foreach (uint type in required)
            EmitInterface("gameobject", "class-presence", rows.Any(x => x.GameObjectType == type) ? "PRESENT" : "ABSENT", 0,
                $"type={type};kind={GameObjectKind(type)};observed={rows.Count(x => x.GameObjectType == type)}");
    }

    private void SnapshotGathering()
    {
        EnsureLockCatalog();
        uint mask = 0;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            mask = player.Fields.PlayerTrackResources;
        var rows = _entities.Entities.Values.Where(x => x.IsGameObject).OrderBy(x =>
            _controller is null ? 0 : Vector3.Distance(x.Position, _controller.Position)).ToArray();
        foreach (WorldEntity go in rows) RequireGameObjectTemplate(go);
        foreach (WorldEntity bobber in rows.Where(x => x.GameObjectType == 17))
            EmitInterface("gathering", "fishing-bobber", "OBSERVED", bobber.Guid,
                $"entry={bobber.Entry};distance={(_controller is null ? float.PositiveInfinity : Vector3.Distance(bobber.Position, _controller.Position)):R};" +
                $"position={bobber.Position.X:R}|{bobber.Position.Y:R}|{bobber.Position.Z:R}");
        foreach (WorldEntity go in rows)
        {
            if (!_gameObjectTemplates.TryGetValue(go.Entry, out GameObjectTemplate? template)) continue;
            uint lockType = _locks?.ResourceLockType(template.LockId) ?? 0;
            if (lockType is not (2 or 3)) continue;
            float distance = _controller is null ? float.PositiveInfinity :
                Vector3.Distance(go.Position, _controller.Position);
            EmitInterface("gathering", "node", "OBSERVED", go.Guid,
                $"entry={go.Entry};name={SanitizeEvidence(template.Name)};resource={ResourceKind(lockType)};" +
                $"lock={template.LockId};lockType={lockType};distance={distance:R};tracked={_locks?.MatchesResourceMask(template.LockId, mask) == true};" +
                $"position={go.Position.X:R}|{go.Position.Y:R}|{go.Position.Z:R}");
        }
        EmitInterface("gathering", "snapshot", "COMPLETE", _net?.PlayerGuid ?? 0,
            $"mask=0x{mask:X8};objects={rows.Length};templates={_gameObjectTemplates.Count};pending={_gameObjectTemplatePending.Count};" +
            $"herbs={rows.Count(x => _gameObjectTemplates.TryGetValue(x.Entry, out GameObjectTemplate? t) && _locks?.ResourceLockType(t.LockId) == 2)};" +
            $"minerals={rows.Count(x => _gameObjectTemplates.TryGetValue(x.Entry, out GameObjectTemplate? t) && _locks?.ResourceLockType(t.LockId) == 3)}");
    }

    private void ApplyGameObjectCustomAnim(byte[] body)
    {
        if (body.Length < 12) throw new InvalidDataException($"custom animation bytes={body.Length}");
        var r = new PacketReader(body); ulong guid = r.ReadU64(); uint animation = r.ReadU32();
        _gameObjectGuid = guid; _gameObjectAnimation = animation;
        EmitInterface("gameobject", "animation", "RECEIVED", guid, $"animation={animation};bytes={body.Length}");
    }

    private void ApplyPageText(byte[] body)
    {
        var r = new PacketReader(body); uint id = r.ReadU32(); string text = r.ReadCString(); uint next = r.ReadU32();
        if (r.Remaining != 0) throw new InvalidDataException($"page trailing={r.Remaining}");
        _gameObjectPages.RemoveAll(x => x.Id == id); _gameObjectPages.Add((id, text, next));
        EmitInterface("gameobject", "page", "DECODED", _gameObjectGuid,
            $"page={id};next={next};chars={text.Length};text={SanitizeEvidence(text)}");
        if (next != 0)
        {
            bool sent = _net?.PageTextQuery(next) == true;
            EmitInterface("gameobject", "page-query", sent ? "SENT" : "SEND_FAILED", _gameObjectGuid,
                $"page={next};body={Convert.ToHexString(WorldSession.BuildPageTextQueryBody(next))}");
        }
    }

    private void SimulateGameObjectFlow()
    {
        ulong[] guids = [0xF110000003000001, 0xF110000000000002, 0xF110000001000003, 0xF110000008000004];
        uint[] types = [3, 0, 1, 8];
        for (int i = 0; i < types.Length; i++)
            EmitInterface("gameobject", "replay-use", "SERVER_EFFECT", guids[i],
                $"type={types[i]};kind={GameObjectKind(types[i])};body={Convert.ToHexString(WorldSession.BuildGameObjectUseBody(guids[i]))};effect={(types[i] == 3 ? "loot-open" : types[i] == 8 ? "focus-present" : "state-animation")}");
        var anim = new PacketWriter(); anim.WriteU64(guids[1]); anim.WriteU32(1); ApplyGameObjectCustomAnim(anim.ToArray());
        var page = new PacketWriter(); page.WriteU32(77); page.WriteCString("A weathered inscription."); page.WriteU32(0); ApplyPageText(page.ToArray());
        EmitInterface("gameobject", "profession-prerequisite", "PRESENT", 0,
            "herb=Silverleaf;vein=Copper Vein;professionItem=2-8:CLOSED-PASS");
    }

    private void DrawGameObjectFrame()
    {
        if (_gameObjectGuid == 0 && _gameObjectPages.Count == 0) return;
        if (_gameplayArt is not null) { DrawItemTextFrame(); return; }
        ImGui.SetNextWindowSize(new Vector2(390, 240), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("World Object##gameobject")) { ImGui.End(); return; }
        if (_entities.TryGet(_gameObjectGuid, out WorldEntity go))
            ImGui.TextUnformatted($"{GameObjectKind(go.GameObjectType)} · entry {go.Entry}");
        else ImGui.TextUnformatted($"Object 0x{_gameObjectGuid:X16}");
        ImGui.TextDisabled($"Last animation: {_gameObjectAnimation}");
        foreach (var page in _gameObjectPages) { ImGui.Separator(); ImGui.TextWrapped(page.Text); }
        if (ImGui.Button("Use again") && _gameObjectGuid != 0) UseGameObject(_gameObjectGuid);
        ImGui.SameLine(); if (ImGui.Button("Close")) ResetGameObjects();
        ImGui.End();
    }

    private void DrawItemTextFrame()
    {
        if (_gameObjectPages.Count == 0 || _gameplayArt is null) return;
        if (!BeginVanillaWindow("##item-text", new Vector2(0, 104), new Vector2(384, 512),
                out ImDrawListPtr dl, out Vector2 origin, out float s)) { ImGui.End(); return; }
        DrawFourPieceShell(dl, origin, s,
            @"Interface\ItemTextFrame\UI-ItemText-TopLeft", @"Interface\ItemTextFrame\UI-ItemText-TopRight",
            @"Interface\ItemTextFrame\UI-ItemText-BotLeft", @"Interface\ItemTextFrame\UI-ItemText-BotRight");
        Vector2 textAt = origin + new Vector2(38, 82) * s;
        foreach (var page in _gameObjectPages)
        {
            dl.AddText(ImGui.GetFont(), 11f * s, textAt, 0xff202020, page.Text);
            textAt.Y += MathF.Max(30, (page.Text.Length / 42 + 1) * 15) * s;
        }
        DrawImageButton(dl, "##item-text-close", origin + new Vector2(323, 9) * s, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) ResetGameObjects();
        ImGui.End();
    }
}
