using System.Globalization;
using System.Numerics;
using System.Text.Json;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed record LiveRunOptions(string OutputDirectory, string? Protocol, double TimeoutSeconds, string? Character);

public static partial class Program
{
    private static bool TryParseLiveRunArgs(string[] args, out LiveRunOptions? options,
        out string? configPath, out string? error)
    {
        options = null; configPath = null; error = null;
        string output = "live-runs"; string? protocol = null, character = null; double timeout = 120;
        for (int i=0;i<args.Length;i++)
        {
            string arg=args[i];
            if (arg=="--live-bootstrap") continue;
            if (arg is "--live-protocol" or "--out" or "--timeout" or "--character")
            {
                if (++i>=args.Length) { error=$"missing value for {arg}"; return false; }
                if (arg=="--live-protocol") protocol=args[i];
                else if (arg=="--out") output=args[i];
                else if (arg=="--character") character=args[i];
                else if (!double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out timeout) || timeout<=0)
                { error="--timeout must be positive"; return false; }
                continue;
            }
            if (arg.StartsWith('-')) { error=$"unknown option {arg}"; return false; }
            if (configPath is not null) { error=$"unexpected argument {arg}"; return false; }
            configPath=arg;
        }
        options=new(output,protocol,timeout,character); return true;
    }
}

public sealed partial class GameLoop
{
    private readonly LiveRunOptions? _liveRunOptions;
    private double _liveRunElapsed;
    private bool _liveTeleportSent;
    private double _liveTeleportSentAt;
    private readonly HashSet<string> _liveHeld = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _liveSteps;
    private int _liveStep;
    private double _liveWaitUntil;
    private string? _liveWaitPattern;
    private double _liveWaitTimeout;
    private string? _liveSpellWaitResult;
    private double _liveSpellWaitTimeout;
    private int _liveSpellWaitAfter;
    private string? _liveInterfaceWaitFamily;
    private string? _liveInterfaceWaitStep;
    private double _liveInterfaceWaitTimeout;
    private int _liveInterfaceWaitAfter;
    private uint _liveLastCastSpell;
    private readonly List<string> _liveLog = [];
    private string _liveStamp = "";
    private readonly List<ulong> _liveSpawnGuids = [];
    private HashSet<ulong>? _liveSpawnBefore;
    private double _liveSelectWaitStarted;
    private ulong _liveAnchorGuid;
    public int LiveRunExitCode { get; private set; } = 1;

    private void AdvanceLiveRun(float dt)
    {
        if (_liveRunOptions is null) return;
        _liveRunElapsed += dt;
        if (_liveRunElapsed > _liveRunOptions.TimeoutSeconds)
        { FinishLiveBootstrap("TIMEOUT", "world did not become ready"); return; }
        if (_net is not { IsInWorld:true } || _worldLoading || _controller is null || _character is null) return;
        if (_liveTeleportSent)
        {
            // Once the protocol has started it owns position. Do not keep testing
            // the bootstrap arena after a later scenario step deliberately moves
            // the character away from it.
            if (_liveSteps is not null) { AdvanceProtocol(); return; }
            var readyArena=VantageStore.Load(_config.RepoRoot).Find("movement-arena");
            if (readyArena is null) { FinishLiveBootstrap("NO_VANTAGE","movement-arena missing"); return; }
            float dx=_controller.Position.X-readyArena.X,dy=_controller.Position.Y-readyArena.Y,dz=_controller.Position.Z-readyArena.Z;
            bool atArena=dx*dx+dy*dy+dz*dz<=9f;
            if (!atArena && NowSeconds()-_liveTeleportSentAt<2.0) return;
            if (!atArena && !_liveLog.Contains("TELEPORT_UNCONFIRMED"))
            {
                _liveLog.Add("TELEPORT_UNCONFIRMED");
                EmitCombat("BootstrapTeleportUnconfirmed","gm-command-no-position-change",0,
                    $"position={_controller.Position.X:R}|{_controller.Position.Y:R}|{_controller.Position.Z:R}");
            }
            if (_liveRunOptions.Protocol is null) { FinishLiveBootstrap("READY", "world+wire+verdict ready"); return; }
            AdvanceProtocol(); return;
        }
        var arena=VantageStore.Load(_config.RepoRoot).Find("movement-arena");
        if (arena is null) { FinishLiveBootstrap("NO_VANTAGE", "movement-arena missing"); return; }
        string command=string.Create(CultureInfo.InvariantCulture,
            $".go xyz {arena.X:R} {arena.Y:R} {arena.Z:R} {arena.Map}");
        _liveTeleportSent=SendGmCommand(command,"live-bootstrap");
        if(_liveTeleportSent) _liveTeleportSentAt=NowSeconds();
        if (!_liveTeleportSent) FinishLiveBootstrap("GM_SEND_FAILED", command);
    }

    private void AdvanceProtocol()
    {
        if (_liveSteps is null)
        {
            string path=Path.GetFullPath(Path.IsPathRooted(_liveRunOptions!.Protocol!) ? _liveRunOptions.Protocol! :
                Path.Combine(_config.RepoRoot,_liveRunOptions.Protocol!));
            _liveSteps=File.ReadLines(path).Select(x=>x.Split('#')[0].Trim()).Where(x=>x.Length>0).ToList();
            _liveStamp=DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture);
            _liveLog.Add($"START protocol={path}");
        }
        double now=NowSeconds();
        if (_liveWaitUntil>now) return;
        if (_liveSpellWaitResult is not null)
        {
            bool foundSpell = _verdicts.Snapshot("spell-sweep").OfType<SpellSweepVerdict>()
                .Skip(_liveSpellWaitAfter).Any(v => v.SpellId == _liveLastCastSpell &&
                    v.Result.Equals(_liveSpellWaitResult, StringComparison.OrdinalIgnoreCase));
            if (foundSpell)
            { Log(true,$"waitspell {_liveSpellWaitResult}"); _liveSpellWaitResult=null; _liveStep++; }
            else if (now>=_liveSpellWaitTimeout)
            { Log(false,$"waitspell {_liveSpellWaitResult} timeout"); _liveSpellWaitResult=null; _liveStep++; }
            else return;
        }
        if (_liveInterfaceWaitFamily is not null)
        {
            bool found = _verdicts.Snapshot("interface").OfType<InterfaceVerdict>()
                .Skip(_liveInterfaceWaitAfter).Any(v =>
                    v.Family.Equals(_liveInterfaceWaitFamily, StringComparison.OrdinalIgnoreCase) &&
                    v.Step.Equals(_liveInterfaceWaitStep, StringComparison.OrdinalIgnoreCase) &&
                    v.Outcome is not "SENT" and not "SEND_FAILED");
            if (found)
            {
                Log(true, $"probe-interface {_liveInterfaceWaitFamily} {_liveInterfaceWaitStep} response");
                _liveInterfaceWaitFamily = null; _liveInterfaceWaitStep = null; _liveStep++;
            }
            else if (now >= _liveInterfaceWaitTimeout)
            {
                EmitInterface(_liveInterfaceWaitFamily, _liveInterfaceWaitStep!,
                    "BLOCKED-BY:F-SILENT-INTERACT", _selectionGuid, "boundedWaitExpired=true");
                Log(true, $"probe-interface {_liveInterfaceWaitFamily} {_liveInterfaceWaitStep} blocked");
                _liveInterfaceWaitFamily = null; _liveInterfaceWaitStep = null; _liveStep++;
            }
            else return;
        }
        if (_liveWaitPattern is not null)
        {
            if (VerdictLines().Any(x=>x.Contains(_liveWaitPattern,StringComparison.OrdinalIgnoreCase)))
            { Log(true,$"waitfor {_liveWaitPattern}"); _liveWaitPattern=null; _liveStep++; }
            else if (now>=_liveWaitTimeout)
            { Log(false,$"waitfor {_liveWaitPattern} timeout"); _liveWaitPattern=null; _liveStep++; }
            else return;
        }
        if (_liveStep>=_liveSteps!.Count) { FinishProtocol(); return; }
        string line=_liveSteps[_liveStep];
        try
        {
            string[] p=line.Split(' ',3,StringSplitOptions.RemoveEmptyEntries);
            switch(p[0].ToLowerInvariant())
            {
                case "gm":
                    if(line[3..].StartsWith(".npc spawn add",StringComparison.OrdinalIgnoreCase))
                        _liveSpawnBefore=_entities.Units.Where(x=>x.IsCreature).Select(x=>x.Guid).ToHashSet();
                    Log(SendGmCommand(line[3..],"protocol-runner"),line); break;
                case "wait": _liveWaitUntil=now+double.Parse(p[1],CultureInfo.InvariantCulture); Log(true,line); break;
                case "waitfor":
                    string[] w=line[8..].Split(' '); double timeout=double.Parse(w[^1],CultureInfo.InvariantCulture);
                    _liveWaitPattern=string.Join(' ',w[..^1]); _liveWaitTimeout=now+timeout; return;
                case "assert": Log(VerdictLines().Any(x=>x.Contains(line[7..],StringComparison.OrdinalIgnoreCase)),line); break;
                case "select":
                    RefreshLiveSpawnIdentities();
                    bool self=p[1].Equals("self",StringComparison.OrdinalIgnoreCase);
                    bool anchor=p[1].Equals("anchor",StringComparison.OrdinalIgnoreCase);
                    bool npcFlagNearest=p[1].StartsWith("npc-flag-nearest:",StringComparison.OrdinalIgnoreCase);
                    bool entryNearest=p[1].StartsWith("entry-nearest:",StringComparison.OrdinalIgnoreCase);
                    bool objectEntryNearest=p[1].StartsWith("object-entry-nearest:",StringComparison.OrdinalIgnoreCase);
                    bool mailboxNearest=p[1].Equals("mailbox-nearest",StringComparison.OrdinalIgnoreCase);
                    int ordinal=self||anchor||npcFlagNearest||mailboxNearest?0:int.Parse(p[1].Split(':')[^1],CultureInfo.InvariantCulture);
                    bool wildEntryNearest=p[1].StartsWith("wild-entry-nearest:",StringComparison.OrdinalIgnoreCase);
                    bool wildEntry=p[1].StartsWith("wild-entry:",StringComparison.OrdinalIgnoreCase);
                    bool wildHostile=p[1].StartsWith("wild-hostile:",StringComparison.OrdinalIgnoreCase);
                    bool wild=p[1].StartsWith("wild:",StringComparison.OrdinalIgnoreCase);
                    bool spawned=p[1].StartsWith("spawn:",StringComparison.OrdinalIgnoreCase);
                    ulong guid=self?_net?.PlayerGuid??0:npcFlagNearest?LiveNpcFlagNearestGuid(p[1].Split(':')[^1]):
                        mailboxNearest?LiveMailboxNearestGuid():
                        objectEntryNearest?LiveObjectEntryNearestGuid(ordinal):
                        entryNearest?LiveEntryNearestGuid(ordinal):
                        anchor&&_entities.TryGet(_liveAnchorGuid,out _)?_liveAnchorGuid:
                        wildEntryNearest?LiveWildEntryNearestGuid(ordinal):wildEntry?LiveWildEntryGuid(ordinal):wildHostile?LiveWildHostileGuid(ordinal):
                        wild?LiveWildGuid(ordinal):LiveSpawnGuid(ordinal);
                    bool unavailable=guid==0||(spawned&&!_entities.TryGet(guid,out _));
                    if(unavailable&&now-(_liveSelectWaitStarted==0?now:_liveSelectWaitStarted)<5)
                    {
                        if(_liveSelectWaitStarted==0) _liveSelectWaitStarted=now;
                        _liveWaitUntil=now+0.05;
                        return;
                    }
                    _liveSelectWaitStarted=0;
                    if(guid!=0) CommitSelection(guid,false); Log(guid!=0,$"{line} guid=0x{guid:X16}"); break;
                case "anchor":
                    RefreshLiveSpawnIdentities();
                    bool selectedAnchor=p[1].Equals("selected",StringComparison.OrdinalIgnoreCase);
                    int anchorOrdinal=selectedAnchor?0:int.Parse(p[1].Split(':')[^1],CultureInfo.InvariantCulture);
                    bool spawnedAnchor=p[1].StartsWith("spawn:",StringComparison.OrdinalIgnoreCase);
                    ulong anchorGuid=selectedAnchor?_selectionGuid:spawnedAnchor?LiveSpawnGuid(anchorOrdinal):
                        p[1].StartsWith("wild-entry-nearest:",StringComparison.OrdinalIgnoreCase)
                        ?LiveWildEntryNearestGuid(anchorOrdinal):p[1].StartsWith("wild-entry:",StringComparison.OrdinalIgnoreCase)
                            ?LiveWildEntryGuid(anchorOrdinal):p[1].StartsWith("wild-hostile:",StringComparison.OrdinalIgnoreCase)
                            ?LiveWildHostileGuid(anchorOrdinal):LiveWildGuid(anchorOrdinal);
                    WorldEntity? anchorTarget=null;
                    if(anchorGuid==0||!_entities.TryGet(anchorGuid,out anchorTarget))
                    {
                        if(now-(_liveSelectWaitStarted==0?now:_liveSelectWaitStarted)<5)
                        {
                            if(_liveSelectWaitStarted==0) _liveSelectWaitStarted=now;
                            _liveWaitUntil=now+0.05;
                            return;
                        }
                    }
                    _liveSelectWaitStarted=0;
                    _liveAnchorGuid=anchorGuid;
                    bool anchored=anchorGuid!=0&&anchorTarget is not null&&SendGmCommand(
                        string.Create(CultureInfo.InvariantCulture,
                            $".go xyz {anchorTarget.Position.X:R} {anchorTarget.Position.Y:R} {anchorTarget.Position.Z:R} {_config.Start.Map}"),
                        "protocol-runner-anchor");
                    Log(anchored,$"{line} guid=0x{anchorGuid:X16} position="+
                        (anchorTarget is null?"unavailable":$"{anchorTarget.Position.X:R}|{anchorTarget.Position.Y:R}|{anchorTarget.Position.Z:R}"));
                    break;
                case "attack":
                    if(p[1]=="start")
                    {
                        _lastAttackPreconditionGatePassed=null;
                        CommitSelection(_selectionGuid,true);
                        Log(_lastAttackPreconditionGatePassed==true,
                            $"{line} gate={(_lastAttackPreconditionGatePassed==true?"PASS":"REFUSED")}");
                    }
                    else { StopAttack("user-cancel"); Log(true,line); }
                    break;
                case "interact":
                    Log(p[1].Equals("gossip", StringComparison.OrdinalIgnoreCase) && RequestGossip(_selectionGuid),
                        $"{line} guid=0x{_selectionGuid:X16}");
                    break;
                case "gossip":
                    if (p[1].Equals("close", StringComparison.OrdinalIgnoreCase))
                    {
                        ResetGossip();
                        Log(true, line);
                    }
                    else if (!p[1].Equals("select", StringComparison.OrdinalIgnoreCase))
                        Log(false, $"unknown {line}");
                    else
                    {
                        int option = int.Parse(p[2], CultureInfo.InvariantCulture);
                        Log(SelectGossipOption(option), line);
                    }
                    break;
                case "vendor":
                    if(p[1].Equals("open",StringComparison.OrdinalIgnoreCase))
                        Log(RequestVendor(_selectionGuid),$"{line} guid=0x{_selectionGuid:X16}");
                    else Log(false,$"unknown {line}");
                    break;
                case "trainer":
                    if (p[1].Equals("open", StringComparison.OrdinalIgnoreCase))
                        Log(RequestTrainer(_selectionGuid), $"{line} guid=0x{_selectionGuid:X16}");
                    else if (p[1].Equals("simulate", StringComparison.OrdinalIgnoreCase))
                    { SimulateTrainerList(); Log(true, line); }
                    else if (p[1].Equals("buy", StringComparison.OrdinalIgnoreCase))
                        Log(BuyTrainerSpell(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("buy-first", StringComparison.OrdinalIgnoreCase))
                        Log(BuyFirstAvailableTrainerSpell(), line);
                    else Log(false, $"unknown {line}");
                    break;
                case "quest":
                    if (p[1].Equals("status", StringComparison.OrdinalIgnoreCase))
                        Log(RequestQuestStatus(_selectionGuid), $"{line} guid=0x{_selectionGuid:X16}");
                    else if (p[1].Equals("hello", StringComparison.OrdinalIgnoreCase))
                        Log(RequestQuestHello(_selectionGuid), $"{line} guid=0x{_selectionGuid:X16}");
                    else if (p[1].Equals("query", StringComparison.OrdinalIgnoreCase))
                        Log(RequestQuestDetails(_selectionGuid, uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("accept", StringComparison.OrdinalIgnoreCase)) Log(AcceptQuest(), line);
                    else if (p[1].Equals("complete", StringComparison.OrdinalIgnoreCase)) Log(RequestQuestCompletion(), line);
                    else if (p[1].Equals("request-reward", StringComparison.OrdinalIgnoreCase)) Log(RequestQuestReward(), line);
                    else if (p[1].Equals("choose", StringComparison.OrdinalIgnoreCase))
                        Log(ChooseQuestReward(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("abandon", StringComparison.OrdinalIgnoreCase))
                        Log(AbandonQuest(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("inspect-log", StringComparison.OrdinalIgnoreCase)) Log(InspectQuestLog(), line);
                    else if (p[1].Equals("simulate", StringComparison.OrdinalIgnoreCase))
                    { SimulateQuestFlow(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "loot":
                    if (p[1].Equals("request", StringComparison.OrdinalIgnoreCase)) Log(RequestLoot(_selectionGuid), line);
                    else if (p[1].Equals("money", StringComparison.OrdinalIgnoreCase)) Log(TakeLootMoney(), line);
                    else if (p[1].Equals("item-first", StringComparison.OrdinalIgnoreCase)) Log(TakeFirstLootItem(), line);
                    else if (p[1].Equals("release", StringComparison.OrdinalIgnoreCase))
                    { bool wasOpen = _loot.IsOpen; ReleaseLoot(); Log(wasOpen, line); }
                    else if (p[1].Equals("simulate", StringComparison.OrdinalIgnoreCase))
                    { SimulateLootFlow(); Log(true, line); }
                    else if (p[1].Equals("simulate-empty", StringComparison.OrdinalIgnoreCase))
                    { SimulateLootFlow(empty: true); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "character":
                    if (p[1].Equals("inspect", StringComparison.OrdinalIgnoreCase)) Log(InspectCharacterInventory(), line);
                    else if (p[1].Equals("open", StringComparison.OrdinalIgnoreCase))
                    { _characterOpen = true; _paperDollDirty = true; Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "inventory":
                    if (p[1].StartsWith("equip-entry", StringComparison.OrdinalIgnoreCase))
                        Log(EquipBackpackEntry(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].StartsWith("unequip-slot", StringComparison.OrdinalIgnoreCase))
                        Log(UnequipSlot(int.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else Log(false, $"unknown {line}");
                    break;
                case "bank":
                    if (p[1].Equals("open", StringComparison.OrdinalIgnoreCase)) Log(RequestBank(_selectionGuid), line);
                    else if (p[1].Equals("deposit-entry", StringComparison.OrdinalIgnoreCase))
                        Log(DepositBankEntry(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("withdraw-entry", StringComparison.OrdinalIgnoreCase))
                        Log(WithdrawBankEntry(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("buy-slot", StringComparison.OrdinalIgnoreCase)) Log(BuyNextBankSlot(), line);
                    else Log(false, $"unknown {line}");
                    break;
                case "mail":
                    string[] mail = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (mail[1].Equals("open", StringComparison.OrdinalIgnoreCase)) Log(RequestMail(_selectionGuid), line);
                    else if (mail[1].Equals("send", StringComparison.OrdinalIgnoreCase) && mail.Length == 6)
                        Log(SendMailFlow(mail[2], uint.Parse(mail[3], CultureInfo.InvariantCulture),
                            uint.Parse(mail[4], CultureInfo.InvariantCulture), uint.Parse(mail[5], CultureInfo.InvariantCulture)), line);
                    else if (mail[1].Equals("take-money-first", StringComparison.OrdinalIgnoreCase))
                    { uint id = FirstMailId("money"); Log(id != 0 && TakeMailMoney(id), $"{line} id={id}"); }
                    else if (mail[1].Equals("take-item-first", StringComparison.OrdinalIgnoreCase))
                    { uint id = FirstMailId("item"); Log(id != 0 && TakeMailItem(id), $"{line} id={id}"); }
                    else if (mail[1].Equals("return-first", StringComparison.OrdinalIgnoreCase))
                    { uint id = FirstMailId("any"); Log(id != 0 && ReturnMail(id), $"{line} id={id}"); }
                    else if (mail[1].Equals("delete-first", StringComparison.OrdinalIgnoreCase))
                    { uint id = FirstMailId("deletable"); Log(id != 0 && DeleteMail(id), $"{line} id={id}"); }
                    else if (mail[1].Equals("simulate-list", StringComparison.OrdinalIgnoreCase))
                    { SimulateMailList(); Log(true, line); }
                    else if (mail[1].Equals("simulate-actions", StringComparison.OrdinalIgnoreCase))
                    { SimulateMailActions(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "interface-blocked":
                    string[] blocked = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                    EmitInterface(blocked[1], blocked[2], $"BLOCKED-BY:{blocked[3]}", _selectionGuid, "boundedWaitExpired=true");
                    Log(true, line);
                    break;
                case "spellbook":
                    int known = EmitKnownSpellInventory();
                    Log(known > 0, $"{line} known={known}");
                    break;
                case "cast":
                    uint castSpell = uint.Parse(p[1], CultureInfo.InvariantCulture);
                    int beforeCast = _verdicts.Snapshot("spell-sweep").Count;
                    TryCast(castSpell);
                    _liveLastCastSpell = castSpell;
                    _liveSpellWaitAfter = _verdicts.Snapshot("spell-sweep").Count;
                    Log(_verdicts.Snapshot("spell-sweep").Count > beforeCast, line);
                    break;
                case "waitspell":
                    string[] spellWait = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    _liveSpellWaitResult = spellWait[1];
                    _liveSpellWaitTimeout = now + double.Parse(spellWait[2], CultureInfo.InvariantCulture);
                    return;
                case "probe-interface":
                    string[] interfaceProbe = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    _liveInterfaceWaitFamily = interfaceProbe[1];
                    _liveInterfaceWaitStep = interfaceProbe[2];
                    _liveInterfaceWaitTimeout = now + double.Parse(interfaceProbe[3], CultureInfo.InvariantCulture);
                    _liveInterfaceWaitAfter = _verdicts.Snapshot("interface").Count;
                    return;
                case "aura":
                    bool expectedAura = p[1].Equals("present", StringComparison.OrdinalIgnoreCase);
                    uint auraSpell = uint.Parse(p[2], CultureInfo.InvariantCulture);
                    Log(EmitAuraEffectCheck(auraSpell, expectedAura), line);
                    break;
                case "aura-cancel":
                    Log(TryCancelAuraBySpell(uint.Parse(p[1], CultureInfo.InvariantCulture), "PROTOCOL_RUNNER"), line);
                    break;
                case "aura-simulate":
                    string[] auraSim = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Log(auraSim.Length == 7 && SimulateAura(auraSim[1],
                        byte.Parse(auraSim[2], CultureInfo.InvariantCulture),
                        uint.Parse(auraSim[3], CultureInfo.InvariantCulture),
                        byte.Parse(auraSim[4], CultureInfo.InvariantCulture),
                        byte.Parse(auraSim[5], CultureInfo.InvariantCulture),
                        uint.Parse(auraSim[6], CultureInfo.InvariantCulture)), line);
                    break;
                case "spell-blocked":
                    EmitSpellBlocked(_liveLastCastSpell, p[1]);
                    Log(true, line);
                    break;
                case "spell-failure":
                    string[] failure = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    ApplySpellCastFailureResult(
                        uint.Parse(failure[1], CultureInfo.InvariantCulture),
                        Convert.ToByte(failure[2], 16));
                    Log(true, line);
                    break;
                case "spell-animation":
                    string[] animation = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Log(animation.Length == 3 &&
                        PresentSpellAnimation(uint.Parse(animation[1], CultureInfo.InvariantCulture), animation[2], "SYNTHETIC_DBC_RENDERER"), line);
                    break;
                case "spell-effect":
                    string[] effect = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Log(effect.Length == 3 && PresentSpellEffect(
                        uint.Parse(effect[1], CultureInfo.InvariantCulture), effect[2]), line);
                    break;
                case "spell-animation-sample":
                    string[] animationSample = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Log(animationSample.Length == 3 &&
                        SampleSpellAnimation(uint.Parse(animationSample[1], CultureInfo.InvariantCulture),
                            animationSample[2], "RENDERER_POST_TICK"), line);
                    break;
                case "channel-simulate":
                    string[] channelStart = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    uint simulatedSpell = uint.Parse(channelStart[1], CultureInfo.InvariantCulture);
                    uint simulatedDuration = uint.Parse(channelStart[2], CultureInfo.InvariantCulture);
                    BeginChannel(simulatedSpell, simulatedDuration);
                    Log(true, line);
                    break;
                case "channel-update":
                    UpdateChannel(uint.Parse(p[1], CultureInfo.InvariantCulture));
                    Log(true, line);
                    break;
                case "channel-tick":
                    string[] channelTick = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    EmitChannelVerdict("TICK", remainingMs: (uint)Math.Max(0,
                        (_castBarEnds - NowSeconds()) * 1000.0), tickKind: channelTick[1].ToUpperInvariant(),
                        amount: uint.Parse(channelTick[2], CultureInfo.InvariantCulture), source: "SYNTHETIC_WIRE_REPLAY");
                    Log(true, line);
                    break;
                case "trace": if(p[1]=="start") { _combatTraceName=p[2]; StartCombatTrace(); } else StopCombatTrace(); Log(true,line); break;
                case "move-trace": if(p[1]=="start") StartMovementTrace(p[2]); else StopMovementTrace(); Log(true,line); break;
                case "wire-trace":
                    if(p[1]=="start") Log(true,$"{line} path={_wireLog.Start(_config.RepoRoot)}");
                    else { _wireLog.Stop(); Log(true,line); }
                    break;
                case "socket-trace":
                    if(p[1]=="start") { StartSocketTrace(p[2]); Log(true,$"{line} path={_socketTracePath}"); }
                    else { StopSocketTrace(); Log(true,line); }
                    break;
                case "dump": _currentVantage=p[1]; ArmGameplayDump(); Log(true,line); break;
                case "press": _liveHeld.Add(NormalizeMovementKey(p[1])); Log(true,line); break;
                case "release": _liveHeld.Remove(NormalizeMovementKey(p[1])); Log(true,line); break;
                case "waitdeath":
                    int deathOrdinal=int.Parse(p[1].Split(':')[^1],CultureInfo.InvariantCulture);
                    ulong deathGuid=LiveSpawnGuid(deathOrdinal);
                    if(deathGuid==0) deathGuid=_selectionGuid;
                    bool dead=_entities.TryGet(deathGuid,out WorldEntity victim)&&victim.IsDead;
                    Log(dead,$"{line} guid=0x{deathGuid:X16} health={(dead?0:victim?.Fields.Health??0)}"); break;
                case "waitgone":
                    int goneOrdinal=int.Parse(p[1].Split(':')[^1],CultureInfo.InvariantCulture);
                    ulong goneGuid=LiveSpawnGuid(goneOrdinal);
                    if(goneGuid==0) goneGuid=_selectionGuid;
                    bool gone=goneGuid!=0&&!_entities.TryGet(goneGuid,out _);
                    Log(gone,$"{line} guid=0x{goneGuid:X16} descriptorPresent={!gone}"); break;
                default: Log(false,$"unknown {line}"); break;
            }
        }
        catch(Exception ex) { Log(false,$"{line} error={ex.GetType().Name}:{ex.Message}"); }
        _liveStep++;
    }

    private IEnumerable<string> VerdictLines()=>_verdicts.SnapshotAll().Select(v=>$"[{v.Channel}] {v.ToLine()}");

    private ulong LiveSpawnGuid(int ordinal)
    {
        // SpawnObserved is the authoritative identity assertion. Resolve from
        // the verdict ring as well as the cache so a descriptor that lands at a
        // protocol-step boundary cannot be lost to ordering between refreshes.
        ulong[] observed=_verdicts.Snapshot("combat").OfType<CombatVerdict>()
            .Where(x=>x.Event=="SpawnObserved").Select(x=>x.TargetGuid).Distinct().ToArray();
        if(ordinal>0&&ordinal<=observed.Length) return observed[ordinal-1];
        return ordinal>0&&ordinal<=_liveSpawnGuids.Count?_liveSpawnGuids[ordinal-1]:0;
    }
    private ulong LiveWildGuid(int ordinal)
    {
        if(_controller is null||ordinal<=0) return 0;
        var candidates=_entities.Units
            .Where(x=>x.IsCreature&&!_liveSpawnGuids.Contains(x.Guid))
            .Select(x=>(Unit:x,Distance:Vector3.Distance(x.Position,_controller.Position)))
            .OrderBy(x=>x.Unit.Guid).ToList();
        if(ordinal>candidates.Count) return 0;
        var selected=candidates[ordinal-1];
        EmitCombat("WildObserved","pre-existing-object-store",selected.Unit.Guid,
            $"entry={selected.Unit.Fields.Entry};distance={selected.Distance:R};"+
            $"faction={selected.Unit.Fields.FactionTemplate};flags=0x{selected.Unit.Fields.UnitFlags:X8};position="+
            $"{selected.Unit.Position.X:R}|{selected.Unit.Position.Y:R}|{selected.Unit.Position.Z:R}");
        return selected.Unit.Guid;
    }
    private ulong LiveWildEntryGuid(int entry)
    {
        if(_controller is null||entry<=0) return 0;
        WorldEntity? selected=_entities.Units
            .Where(x=>x.IsCreature&&!_liveSpawnGuids.Contains(x.Guid)&&x.Entry==(uint)entry)
            .OrderBy(x=>x.Guid).FirstOrDefault();
        if(selected is null) return 0;
        float distance=Vector3.Distance(selected.Position,_controller.Position);
        EmitCombat("WildObserved","pre-existing-object-store-entry-control",selected.Guid,
            $"entry={selected.Fields.Entry};distance={distance:R};faction={selected.Fields.FactionTemplate};"+
            $"flags=0x{selected.Fields.UnitFlags:X8};position="+
            $"{selected.Position.X:R}|{selected.Position.Y:R}|{selected.Position.Z:R}");
        return selected.Guid;
    }
    private ulong LiveWildEntryNearestGuid(int entry)
    {
        if(_controller is null||entry<=0) return 0;
        WorldEntity? selected=_entities.Units
            .Where(x=>x.IsCreature&&!_liveSpawnGuids.Contains(x.Guid)&&!x.IsDead&&x.Entry==(uint)entry)
            .OrderBy(x=>Vector3.Distance(x.Position,_controller.Position)).ThenBy(x=>x.Guid).FirstOrDefault();
        if(selected is null) return 0;
        float distance=Vector3.Distance(selected.Position,_controller.Position);
        EmitCombat("WildObserved","pre-existing-object-store-entry-nearest",selected.Guid,
            $"entry={selected.Fields.Entry};distance={distance:R};faction={selected.Fields.FactionTemplate};"+
            $"flags=0x{selected.Fields.UnitFlags:X8};position="+
            $"{selected.Position.X:R}|{selected.Position.Y:R}|{selected.Position.Z:R}");
        return selected.Guid;
    }
    private ulong LiveWildHostileGuid(int ordinal)
    {
        if(_controller is null||ordinal<=0) return 0;
        var candidates=_entities.Units
            .Where(x=>x.IsCreature&&!_liveSpawnGuids.Contains(x.Guid)&&!x.IsDead&&
                x.Fields.UnitFlags==0&&ReactionPlayerToward(x)==FactionReaction.Hostile)
            .Select(x=>(Unit:x,Distance:Vector3.Distance(x.Position,_controller.Position)))
            .OrderBy(x=>x.Distance).ThenBy(x=>x.Unit.Guid).ToList();
        if(ordinal>candidates.Count) return 0;
        var selected=candidates[ordinal-1];
        EmitCombat("WildObserved","pre-existing-object-store-hostile",selected.Unit.Guid,
            $"entry={selected.Unit.Fields.Entry};distance={selected.Distance:R};faction={selected.Unit.Fields.FactionTemplate};"+
            $"reaction=Hostile;flags=0x{selected.Unit.Fields.UnitFlags:X8};position="+
            $"{selected.Unit.Position.X:R}|{selected.Unit.Position.Y:R}|{selected.Unit.Position.Z:R}");
        return selected.Unit.Guid;
    }
    private ulong LiveNpcFlagNearestGuid(string flagName)
    {
        if(_controller is null) return 0;
        uint flag=flagName.ToLowerInvariant() switch
        {
            "vendor" => NpcVendor,
            "trainer" => NpcTrainer,
            "quest" or "questgiver" => NpcQuestGiver,
            "flightmaster" => NpcFlightMaster,
            "innkeeper" => NpcInnkeeper,
            "banker" => NpcBanker,
            "auctioneer" => NpcAuctioneer,
            _ => 0,
        };
        if(flag==0) return 0;
        WorldEntity? selected=_entities.Units
            .Where(x=>x.IsCreature&&!x.IsDead&&(x.NpcFlags&flag)!=0)
            .OrderBy(x=>Vector3.Distance(x.Position,_controller.Position)).ThenBy(x=>x.Guid)
            .FirstOrDefault();
        if(selected is null) return 0;
        float distance=Vector3.Distance(selected.Position,_controller.Position);
        EmitInterface("gossip","npc-flag-observed","PASS",selected.Guid,
            $"class={flagName.ToLowerInvariant()};entry={selected.Entry};npcFlags=0x{selected.NpcFlags:X8};distance={distance:R}");
        return selected.Guid;
    }
    private ulong LiveEntryNearestGuid(int entry)
    {
        if (_controller is null || entry <= 0) return 0;
        WorldEntity? selected = _entities.Units.Where(x => x.IsCreature && !x.IsDead && x.Entry == (uint)entry)
            .OrderBy(x => Vector3.Distance(x.Position, _controller.Position)).ThenBy(x => x.Guid).FirstOrDefault();
        if (selected is null) return 0;
        float distance = Vector3.Distance(selected.Position, _controller.Position);
        EmitCombat("EntryObserved", "object-store-entry-nearest", selected.Guid,
            $"entry={entry};distance={distance:R};position={selected.Position.X:R}|{selected.Position.Y:R}|{selected.Position.Z:R}");
        return selected.Guid;
    }
    private ulong LiveObjectEntryNearestGuid(int entry)
    {
        if (_controller is null || entry <= 0) return 0;
        WorldEntity? selected = _entities.Entities.Values.Where(x => x.IsGameObject && x.Entry == (uint)entry)
            .OrderBy(x => Vector3.Distance(x.Position, _controller.Position)).ThenBy(x => x.Guid).FirstOrDefault();
        if (selected is null) return 0;
        float distance = Vector3.Distance(selected.Position, _controller.Position);
        EmitInterface("mail", "mailbox-observed", "PASS", selected.Guid,
            $"entry={entry};distance={distance:R};position={selected.Position.X:R}|{selected.Position.Y:R}|{selected.Position.Z:R}");
        return selected.Guid;
    }
    private ulong LiveMailboxNearestGuid()
    {
        if (_controller is null) return 0;
        WorldEntity? selected = _entities.Entities.Values.Where(x => x.IsGameObject && x.GameObjectType == 19)
            .OrderBy(x => Vector3.Distance(x.Position, _controller.Position)).ThenBy(x => x.Guid).FirstOrDefault();
        if (selected is null) return 0;
        float distance = Vector3.Distance(selected.Position, _controller.Position);
        EmitInterface("mail", "mailbox-observed", "PASS", selected.Guid,
            $"entry={selected.Entry};type={selected.GameObjectType};distance={distance:R};position={selected.Position.X:R}|{selected.Position.Y:R}|{selected.Position.Z:R}");
        return selected.Guid;
    }
    private void RefreshLiveSpawnIdentities()
    {
        if (_liveSpawnBefore is null || _controller is null) return;
        var appeared=_entities.Units
            .Where(x=>x.IsCreature&&!_liveSpawnBefore.Contains(x.Guid))
            .Select(x=>(Unit:x,Distance:Vector3.Distance(x.Position,_controller.Position)))
            .OrderBy(x=>x.Distance).ThenBy(x=>x.Unit.Guid).ToList();
        foreach(var candidate in appeared)
        {
            if(_liveSpawnGuids.Contains(candidate.Unit.Guid)) continue;
            _liveSpawnGuids.Add(candidate.Unit.Guid);
            EmitCombat("SpawnObserved","post-command-entity-delta",candidate.Unit.Guid,
                $"entry={candidate.Unit.Fields.Entry};distance={candidate.Distance:R};"+
                $"within3={(candidate.Distance<=3f).ToString().ToLowerInvariant()};position="+
                $"{candidate.Unit.Position.X:R}|{candidate.Unit.Position.Y:R}|{candidate.Unit.Position.Z:R}");
        }
        if(appeared.Count>0) _liveSpawnBefore=null;
    }

    private void Log(bool pass,string text)
    {
        RefreshLiveSpawnIdentities();
        string entry=$"{_liveStep+1},{(pass?"PASS":"FAIL")},{text}"; _liveLog.Add(entry); Console.WriteLine($"[protocol] {entry}");
    }

    private void FinishProtocol()
    {
        StopCombatTrace(); StopMovementTrace(); StopSocketTrace(); _wireLog.Stop(); _liveHeld.Clear();
        string dir=Path.GetFullPath(Path.IsPathRooted(_liveRunOptions!.OutputDirectory)?_liveRunOptions.OutputDirectory:Path.Combine(_config.RepoRoot,_liveRunOptions.OutputDirectory));
        Directory.CreateDirectory(dir);
        string log=Path.Combine(dir,$"runner-{_liveStamp}.csv"), verdict=Path.Combine(dir,$"verdicts-{_liveStamp}.txt");
        File.WriteAllLines(log,new[]{"step,result,detail"}.Concat(_liveLog)); File.WriteAllLines(verdict,VerdictLines());
        string spellCsv=Path.Combine(dir,$"spell-sweep-{_liveStamp}.csv");
        WriteSpellSweepCsv(spellCsv);
        string castBarCsv=Path.Combine(dir,$"cast-bar-{_liveStamp}.csv");
        WriteCastBarCsv(castBarCsv);
        string spellAnimationCsv=Path.Combine(dir,$"spell-animation-{_liveStamp}.csv");
        WriteSpellAnimationCsv(spellAnimationCsv);
        string spellChannelCsv=Path.Combine(dir,$"spell-channel-{_liveStamp}.csv");
        WriteSpellChannelCsv(spellChannelCsv);
        string spellAuraCsv=Path.Combine(dir,$"spell-aura-{_liveStamp}.csv");
        WriteSpellAuraCsv(spellAuraCsv);
        string spellErrorCsv=Path.Combine(dir,$"spell-error-{_liveStamp}.csv");
        WriteSpellErrorCsv(spellErrorCsv);
        int failures=_liveLog.Count(x=>x.Contains(",FAIL,"));
        Console.WriteLine($"[live-run] PROTOCOL_DONE failures={failures}; log={log}; verdicts={verdict}; spells={spellCsv}; castbar={castBarCsv}; animations={spellAnimationCsv}; channels={spellChannelCsv}; auras={spellAuraCsv}; errors={spellErrorCsv}");
        LiveRunExitCode=failures==0?0:1; _window.Close();
    }

    private void WriteSpellSweepCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,class_id,spell_id,name,school,cast_type,result_enum,animation_state,effect_check,target_type,gcd_ready,cooldown_ready,resource_type,resource_before,resource_cost,resolved_guid,sent"
        };
        foreach (SpellSweepVerdict v in _verdicts.Snapshot("spell-sweep").OfType<SpellSweepVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture), Csv(v.Character),
                v.ClassId, v.SpellId, Csv(v.SpellName), v.School, v.CastType, v.Result,
                Csv(v.AnimationState), Csv(v.EffectCheck), v.TargetType, v.GcdReady, v.CooldownReady,
                v.ResourceType, v.ResourceBefore, v.ResourceCost, $"0x{v.ResolvedGuid:X16}", v.Sent));
        File.WriteAllLines(path, lines);
    }

    private void WriteCastBarCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,spell_id,name,event,classification,server_duration_ms,dbc_cast_time_ms,dbc_duration_ms,phase,started,ends,pushback_total_ms,cancel_source,animation_state"
        };
        foreach (CastBarVerdict v in _verdicts.Snapshot("cast-bar").OfType<CastBarVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture), Csv(v.Character),
                v.SpellId, Csv(v.SpellName), v.Event, v.Classification, v.ServerDurationMs,
                v.DbcCastTimeMs, v.DbcDurationMs, v.Phase,
                v.StartedAt.ToString("F3", CultureInfo.InvariantCulture),
                v.EndsAt.ToString("F3", CultureInfo.InvariantCulture), v.PushbackTotalMs,
                v.CancelSource, Csv(v.AnimationState)));
        File.WriteAllLines(path, lines);
    }

    private void WriteSpellAnimationCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,spell_id,name,school,stage,kit_id,authored_animation_id,requested_animation_id,played_animation_id,resolution,renderer_state,moving,legal_while_moving,movement_interrupts,base_animation,previous_base_animation,action_animation,hold_animation,blend_weight,source"
        };
        foreach (SpellAnimationVerdict v in _verdicts.Snapshot("spell-animation").OfType<SpellAnimationVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture),
                Csv(v.Character), v.SpellId, Csv(v.SpellName), v.School, v.Stage, v.KitId,
                v.AuthoredAnimationId, v.RequestedAnimationId, v.PlayedAnimationId,
                v.Resolution, Csv(v.RendererState), v.Moving, v.LegalWhileMoving,
                v.MovementInterrupts, Csv(v.BaseAnimation), Csv(v.PreviousBaseAnimation),
                Csv(v.ActionAnimation), Csv(v.HoldAnimation),
                v.BlendWeight.ToString("F4", CultureInfo.InvariantCulture), v.Source));
        File.WriteAllLines(path, lines);
    }

    private void WriteSpellChannelCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,spell_id,name,event,duration_ms,remaining_ms,tick_index,tick_delta_ms,tick_kind,amount,moving,animation_state,source"
        };
        foreach (ChannelSpellVerdict v in _verdicts.Snapshot("spell-channel").OfType<ChannelSpellVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture),
                Csv(v.Character), v.SpellId, Csv(v.SpellName), v.Event, v.DurationMs,
                v.RemainingMs, v.TickIndex, v.TickDeltaMs.ToString("F3", CultureInfo.InvariantCulture),
                v.TickKind, v.Amount, v.Moving, Csv(v.AnimationState), v.Source));
        File.WriteAllLines(path, lines);
    }

    private void WriteSpellAuraCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,unit_guid,slot,spell_id,name,event,helpful,cancelable,stacks,duration_ms,remaining_ms,display,source"
        };
        foreach (AuraVerdict v in _verdicts.Snapshot("spell-aura").OfType<AuraVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture),
                Csv(v.Character), $"0x{v.UnitGuid:X16}", v.Slot, v.SpellId, Csv(v.SpellName), v.Event,
                v.Helpful, v.Cancelable, v.Stacks, v.DurationMs, v.RemainingMs, v.Display, v.Source));
        File.WriteAllLines(path, lines);
    }

    private void WriteSpellErrorCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,spell_id,name,reason,display_text,displayed,source"
        };
        foreach (SpellErrorVerdict v in _verdicts.Snapshot("spell-error").OfType<SpellErrorVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture),
                Csv(v.Character), v.SpellId, Csv(v.SpellName), v.Reason,
                Csv(v.DisplayText), v.Displayed, v.Source));
        File.WriteAllLines(path, lines);
    }

    private void FinishLiveBootstrap(string result, string detail)
    {
        string dir=Path.GetFullPath(Path.IsPathRooted(_liveRunOptions!.OutputDirectory)
            ? _liveRunOptions.OutputDirectory : Path.Combine(_config.RepoRoot,_liveRunOptions.OutputDirectory));
        Directory.CreateDirectory(dir);
        string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture);
        string path=Path.Combine(dir,$"bootstrap-{stamp}.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new { result,detail,
            account=_config.Server.Account,character=_config.Server.Character,realm=_config.Server.Realm,
            elapsed=_liveRunElapsed },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"[live-run] {result}: {detail}; artifact={path}");
        LiveRunExitCode=result=="READY"?0:1;
        _window.Close();
    }
}
