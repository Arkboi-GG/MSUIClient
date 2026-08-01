using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const float GameObjectInteractDistance = 6f;
    private ulong _gameObjectGuid;
    private uint _gameObjectAnimation;
    private readonly List<(uint Id, string Text, uint Next)> _gameObjectPages = [];

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

    private bool UseGameObject(ulong guid)
    {
        WorldEntity? go = null;
        float distance = float.PositiveInfinity;
        string outcome;
        if (_net is not { IsInWorld: true } || _controller is null) outcome = "REFUSED_NOT_IN_WORLD";
        else if (!_entities.TryGet(guid, out go) || !go.IsGameObject) outcome = "REFUSED_NOT_GAMEOBJECT";
        else if ((distance = Vector3.Distance(_controller.Position, go.Position)) > GameObjectInteractDistance) outcome = "REFUSED_RANGE";
        else outcome = _net.GameObjectUse(guid) ? "SENT" : "SEND_FAILED";
        if (outcome == "SENT") { _gameObjectGuid = guid; _gameObjectAnimation = 0; _gameObjectPages.Clear(); }
        EmitInterface("gameobject", "use", outcome, guid,
            $"entry={go?.Entry ?? 0};type={go?.GameObjectType ?? 0};kind={GameObjectKind(go?.GameObjectType ?? uint.MaxValue)};distance={distance:R};limit={GameObjectInteractDistance:R};body={Convert.ToHexString(WorldSession.BuildGameObjectUseBody(guid))}");
        return outcome == "SENT";
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
}
