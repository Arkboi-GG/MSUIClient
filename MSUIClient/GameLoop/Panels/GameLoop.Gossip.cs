using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const uint NpcGossip = WorldCursorUiLaw.Gossip;
    private const uint NpcQuestGiver = WorldCursorUiLaw.Questgiver;
    private const uint NpcVendor = WorldCursorUiLaw.Vendor;
    private const uint NpcFlightMaster = WorldCursorUiLaw.FlightMaster;
    private const uint NpcTrainer = WorldCursorUiLaw.Trainer;
    private const uint NpcInnkeeper = WorldCursorUiLaw.Innkeeper;
    private const uint NpcBanker = WorldCursorUiLaw.Banker;
    private const uint NpcAuctioneer = WorldCursorUiLaw.Auctioneer;
    private const uint NpcTabardDesigner = WorldCursorUiLaw.TabardDesigner;
    private const uint GossipNpcFlags = NpcGossip | NpcQuestGiver | NpcVendor |
        NpcFlightMaster | NpcTrainer | WorldCursorUiLaw.SpiritHealer |
        WorldCursorUiLaw.SpiritGuide | NpcInnkeeper | NpcBanker |
        WorldCursorUiLaw.Petitioner | NpcTabardDesigner | WorldCursorUiLaw.Battlemaster |
        NpcAuctioneer | WorldCursorUiLaw.StableMaster;
    private const float GossipInteractDistance = NpcSessionUiLaw.ServiceRange;

    private GossipMenu? _gossipMenu;
    private string? _gossipGreeting;
    private readonly Dictionary<uint, NpcText> _npcTextRecords = [];
    private uint _gossipSourceFlags;

    private void ResetGossip()
    {
        _gossipMenu = null;
        _gossipGreeting = null;
        _gossipSourceFlags = 0;
    }

    private bool RequestGossip(ulong guid)
    {
        string outcome;
        string detail;
        WorldEntity? target = null;
        float distance = float.PositiveInfinity;
        if (_net is not { IsInWorld: true } || _controller is null)
        {
            outcome = "REFUSED_NOT_IN_WORLD";
            detail = "inWorld=false";
        }
        else if (!_entities.TryGet(guid, out target) || !target.IsCreature)
        {
            outcome = "REFUSED_NOT_CREATURE";
            detail = "descriptorPresent=false";
        }
        else if (target.IsDead)
        {
            outcome = "REFUSED_DEAD";
            detail = $"health={target.Fields.Health}/{target.Fields.MaxHealth}";
        }
        else if ((target.NpcFlags & GossipNpcFlags) == 0)
        {
            outcome = "REFUSED_NO_SUPPORTED_NPC_FLAG";
            detail = $"npcFlags=0x{target.NpcFlags:X8}";
        }
        else if (!NpcSessionUiLaw.InRange(
                     Vector3.DistanceSquared(_controller.Position, target.Position)))
        {
            distance = Vector3.Distance(_controller.Position, target.Position);
            outcome = "REFUSED_RANGE";
            detail = $"distance={distance:R};limit={GossipInteractDistance:R};npcFlags=0x{target.NpcFlags:X8}";
        }
        else
        {
            bool sent = _net.GossipHello(guid);
            outcome = sent ? "SENT" : "SEND_FAILED";
            detail = $"distance={distance:R};npcFlags=0x{target.NpcFlags:X8};route={ClassifyGossipRoute(target.NpcFlags, "")}";
            if (sent)
            {
                _gossipMenu = null;
                _gossipGreeting = null;
                _gossipSourceFlags = target.NpcFlags;
            }
        }
        EmitInterface("gossip", "hello", outcome, guid, detail);
        return outcome == "SENT";
    }

    private bool UpdateGossipLifecycle()
    {
        if (_gossipMenu is null || _controller is null) return false;
        ulong sourceGuid = _gossipMenu.SourceGuid;
        bool sourceAvailable = _entities.TryGet(sourceGuid, out WorldEntity source);
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(_controller.Position, source.Position)
            : float.PositiveInfinity;
        if (!NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared))
            return false;
        ResetGossip();
        EmitInterface("gossip", "lifecycle-close", "CLOSED", sourceGuid,
            sourceAvailable
                ? $"distanceSquared={distanceSquared:R};limitSquared={NpcSessionUiLaw.ServiceRangeSquared:R}"
                : "source-despawned");
        return true;
    }

    private void ApplyGossipMenu(byte[] body)
    {
        GossipMenu menu = GossipPackets.ParseMenu(body);
        // Some streamed spawns carry a stale innkeeper bit even though their creature-query
        // identity explicitly names another vendor profession. Never expose the bind menu for
        // that mismatch; enter the vendor service directly.
        if (_entities.TryGet(menu.SourceGuid, out WorldEntity routedSource) &&
            !IsInnkeeper(routedSource) && (routedSource.NpcFlags & NpcVendor) != 0)
        {
            EmitInterface("gossip", "menu", "REROUTED_VENDOR", menu.SourceGuid,
                $"textId={menu.TextId};npcFlags=0x{routedSource.NpcFlags:X8}");
            ResetGossip();
            RequestVendor(menu.SourceGuid);
            return;
        }
        _gossipMenu = menu;
        _gossipGreeting = null;
        byte sourceGender = 0;
        if (_entities.TryGet(menu.SourceGuid, out WorldEntity source))
        {
            _gossipSourceFlags = source.NpcFlags;
            sourceGender = source.Fields.Bytes0.Gender;
        }
        EmitInterface("gossip", "menu", "DECODED", menu.SourceGuid,
            $"textId={menu.TextId};options={menu.Options.Count};quests={menu.Quests.Count};npcFlags=0x{_gossipSourceFlags:X8}");
        if (_npcTextRecords.TryGetValue(menu.TextId, out NpcText? cached))
        {
            _gossipGreeting = DrawGossipGreeting(cached, sourceGender);
            EmitInterface("gossip", "text-query", "CACHE_HIT", menu.SourceGuid,
                $"textId={menu.TextId};gender={sourceGender}");
        }
        else
        {
            bool sent = _net?.NpcTextQuery(menu.TextId, menu.SourceGuid) == true;
            EmitInterface("gossip", "text-query", sent ? "SENT" : "SEND_FAILED", menu.SourceGuid,
                $"textId={menu.TextId}");
        }
    }

    private void ApplyNpcText(byte[] body)
    {
        NpcText text = GossipPackets.ParseText(body);
        _npcTextRecords[text.TextId] = text;
        if (_gossipMenu is null || text.TextId != _gossipMenu.TextId)
        {
            EmitInterface("gossip", "text", "IGNORED_STALE", 0,
                $"textId={text.TextId};openTextId={_gossipMenu?.TextId ?? 0}");
            return;
        }
        byte sourceGender = _entities.TryGet(_gossipMenu.SourceGuid, out WorldEntity source)
            ? source.Fields.Bytes0.Gender
            : (byte)0;
        _gossipGreeting = DrawGossipGreeting(text, sourceGender);
        EmitInterface("gossip", "text", "DECODED", _gossipMenu.SourceGuid,
            $"textId={text.TextId};blocks={text.Blocks.Count};gender={sourceGender};selectedChars={_gossipGreeting.Length}");
    }

    private static string DrawGossipGreeting(NpcText text, byte sourceGender) =>
        GossipUiLaw.SelectGreeting(text.Blocks, sourceGender,
            GossipUiLaw.GreetingRoll(Random.Shared)) ?? "";

    private bool SelectGossipOption(int visualIndex)
    {
        if (_gossipMenu is null || visualIndex < 0 || visualIndex >= _gossipMenu.Options.Count)
        {
            EmitInterface("gossip", "select", "REFUSED_NO_OPTION", _gossipMenu?.SourceGuid ?? 0,
                $"visualIndex={visualIndex};count={_gossipMenu?.Options.Count ?? 0}");
            return false;
        }
        GossipOption option = _gossipMenu.Options[visualIndex];
        if (option.Coded)
        {
            EmitInterface("gossip", "select", "REFUSED_CODE_REQUIRED", _gossipMenu.SourceGuid,
                $"visualIndex={visualIndex};listId={option.ListId}");
            return false;
        }
        string route = ClassifyGossipRoute(_gossipSourceFlags, option.Text);
        bool sent = _net?.GossipSelect(_gossipMenu.SourceGuid, option.ListId) == true;
        EmitInterface("gossip", "select", sent ? "SENT" : "SEND_FAILED", _gossipMenu.SourceGuid,
            $"visualIndex={visualIndex};listId={option.ListId};icon={option.Icon};route={route};text={SanitizeEvidence(option.Text)}");
        return sent;
    }

    private static string ClassifyGossipRoute(uint flags, string optionText)
    {
        if ((flags & NpcVendor) != 0) return "vendor";
        if ((flags & NpcTrainer) != 0) return "trainer";
        if ((flags & NpcFlightMaster) != 0) return "flightmaster";
        if ((flags & NpcInnkeeper) != 0) return "innkeeper";
        if ((flags & NpcBanker) != 0) return "banker";
        if ((flags & NpcAuctioneer) != 0) return "auctioneer";
        if ((flags & NpcQuestGiver) != 0) return "quest";
        return optionText.Length == 0 ? "unknown" : "gossip";
    }

    private static string SanitizeEvidence(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ',').Trim();

    private bool IsInnkeeper(WorldEntity npc)
    {
        if ((npc.NpcFlags & NpcInnkeeper) == 0) return false;
        if (!_creatureQueryRecords.TryGetValue(npc.Entry, out CreatureQueryInfo? identity) ||
            identity is null)
            return true;
        return identity.Name.Contains("Innkeeper", StringComparison.OrdinalIgnoreCase) ||
               (identity.Subname?.Contains("Innkeeper", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void EmitInterface(string family, string step, string outcome, ulong guid, string detail)
    {
        var verdict = new InterfaceVerdict(NowSeconds(), family, step, outcome, guid, detail);
        _verdicts.Add(verdict);
        if (_config.DevTools) Console.WriteLine($"[verdict:interface] {verdict.ToLine()}");
    }

    private void DrawGossipFrame()
    {
        if (_gossipMenu is null) return;
        // The creature query can complete after SMSG_GOSSIP_MESSAGE. Re-check here so a late
        // profession identity still closes a wrongly offered bind menu exactly once.
        if (_entities.TryGet(_gossipMenu.SourceGuid, out WorldEntity identifiedSource) &&
            !IsInnkeeper(identifiedSource) && (identifiedSource.NpcFlags & NpcVendor) != 0)
        {
            ulong vendorGuid = _gossipMenu.SourceGuid;
            ResetGossip();
            RequestVendor(vendorGuid);
            return;
        }
        float s = GameplayUiScale();
        Vector2 size = new Vector2(384f, 512f) * s;
        Vector2 p = new(0,104f*s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);ImGuiWindowFlags flags=ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##gossip-frame", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="gossip"){BeginUiParityFrame(p,s);CollectUiParityDraw("GossipFrame","Frame",p,size,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        WorldEntity? source = _entities.TryGet(_gossipMenu.SourceGuid, out WorldEntity foundSource)
            ? foundSource : null;
        // The portrait is an ARTWORK region below the shell textures. Drawing it first lets the
        // authored transparent circular aperture mask the square portrait naturally.
        if (source is not null)
            DrawUnitPortraitImage(dl,source,p+new Vector2(7,6)*s,60*s,0,false);
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[
            ("GossipFrameGreetingPanel/Texture",@"Interface\QuestFrame\UI-QuestGreeting-TopLeft",Vector2.Zero,new(256,256)),
            ("GossipFrameGreetingPanel/Texture#2",@"Interface\QuestFrame\UI-QuestGreeting-TopRight",new(256,0),new(128,256)),
            ("GossipFrameGreetingPanel/Texture#3",@"Interface\QuestFrame\UI-QuestGreeting-BotLeft",new(0,256),new(256,256)),
            ("GossipFrameGreetingPanel/Texture#4",@"Interface\QuestFrame\UI-QuestGreeting-BotRight",new(256,256),new(128,256)),
            ("GossipFrameGreetingPanel/Texture#5",@"Interface\QuestFrame\UI-Quest-BotLeftPatch",new(22,380),new(128,64))];
        foreach(var r in art){Vector2 m=p+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="gossip")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"GossipFrameGreetingPanel",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","GossipFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        string sourceName = source is not null
            ? source.IsPlayer
                ? _playerNames.GetValueOrDefault(source.Guid, "Player")
                : _creatureNames.GetValueOrDefault(source.Entry, $"Creature {source.Entry}")
            : $"0x{_gossipMenu.SourceGuid:X16}";
        // Match the established NPC-frame title box used by QuestFrame: centered in the header
        // bar at y=30, not centered on the bar's upper border.
        DrawNpcModalTitle(dl,sourceName,p+new Vector2(192,30)*s,s);
        string greeting = _gossipGreeting ?? $"Loading text {_gossipMenu.TextId}...";
        // GossipGreetingScrollFrame TOPLEFT (23,-81), then GossipGreetingText
        // TOPLEFT (10,-10), width 270, inherits QuestFont.
        float used=DrawQuestWrappedText(dl,ExpandQuestText(greeting),
            p+new Vector2(33,91)*s,270,"QuestFont",s,
            FontObjectLaw.Get("QuestFont").Color);
        // First title is greeting BOTTOMLEFT +(-10,-20), relative to the scroll child.
        float rowY=111+used/s;

        for (int i = 0; i < _gossipMenu.Options.Count; i++)
        {
            GossipOption option = _gossipMenu.Options[i];
            if (DrawGossipTitleRow(dl,$"##gossip-option-{i}",
                    p+new Vector2(23,rowY)*s,s,option.Text,GossipUiLaw.OptionIcon(option.Icon),
                    out float rowHeight)) SelectGossipOption(i);
            rowY+=rowHeight;
        }
        foreach (GossipQuest quest in _gossipMenu.Quests)
        {
            string title = $"[{quest.Level}] {ExpandQuestText(quest.Title)}";
            if (DrawGossipTitleRow(dl,$"##gossip-quest-{quest.QuestId}",
                    p+new Vector2(23,rowY)*s,s,title,
                    GossipUiLaw.QuestIcon(quest.Icon),out float rowHeight))
            {
                if (QuestFrameUiLaw.GreetingAction(quest.Icon) == QuestGreetingAction.Complete)
                    RequestQuestCompletion(_gossipMenu.SourceGuid, quest.QuestId);
                else
                    RequestQuestDetails(_gossipMenu.SourceGuid, quest.QuestId);
            }
            rowY+=rowHeight;
        }
        // BOTTOMRIGHT relative to GossipFrame BOTTOMRIGHT (-39,+73), 78x22.
        if(VanillaButton(dl,"##gossip-goodbye","Goodbye",p+new Vector2(267,417)*s,new Vector2(78,22),s))ResetGossip();
        Vector2 close=p+new Vector2(326,15)*s;DrawImageButton(dl,"##gossip-close",close,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())ResetGossip();
        if(_uiParityArmed&&_uiParityPanel=="gossip")MarkUiParityFrameComplete();ImGui.End();
    }

    private bool DrawGossipTitleRow(ImDrawListPtr dl, string id, Vector2 min, float s,
        string text, string iconPath, out float logicalAdvance)
    {
        float textHeight = Math.Max(16,
            MeasureQuestWrappedText(text,275,"QuestFont",s)/s);
        Vector2 hitSize = new(300,textHeight+2);
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton(id,hitSize*s);
        if (ImGui.IsItemHovered())
        {
            uint highlight = _gameplayArt?.BrightHighlightHandle(
                @"Interface\QuestFrame\UI-QuestTitleHighlight") ?? 0;
            if (highlight != 0)
                dl.AddImage((nint)highlight,min,min+hitSize*s);
        }
        DrawArt(dl,iconPath,min+new Vector2(3,1)*s,new Vector2(16),s);
        DrawQuestWrappedText(dl,text,min+new Vector2(20,0)*s,275,
            "QuestFont",s,FontObjectLaw.Get("QuestFont").Color);
        logicalAdvance=textHeight+3; // next button: previous BOTTOMLEFT + (0,-3)
        return clicked;
    }

}
