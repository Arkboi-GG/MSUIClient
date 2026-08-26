using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // ── functional-input state (stage 3) ──────────────────────────────────────
    private string _chatInput = "";
    private bool _chatEditCursorToEnd;
    private ImGuiInputTextCallback? _chatEditCallback;
    private bool _chatEditJustOpened, _chatEditActivated;
    // Sender GUIDs already NAME_QUERY'd, so an unresolved sender is asked once.
    private readonly HashSet<ulong> _chatNameQueried = [];
    private readonly List<ChatMessagePacket> _pendingChatMacros = [];
    private readonly List<CombatXpGain> _pendingChatXp = [];
    private readonly List<ChannelNoticePacket> _pendingChatChannelNotices = [];
    private readonly List<string?> _chatChannels = [];
    private bool _chatMenuOpen, _chatMenuOpenedThisFrame;
    private ChatMenuLevel _chatMenuSubmenu;
    private double _chatMenuCloseAt;
    private string _chatLastTellTarget = "";
    private ExplorationSoundCatalog? _explorationSounds;
    private ChatLanguageCatalog? _chatLanguages;
    private bool _chatLanguageLoadAttempted;

    // One buffered line: the cleaned text plus its 1.12 message type (the type
    // picks the colour through ChatFrameLaw). Ring-buffered at MaxLines.
    private readonly List<(string Text, ChatFrameLaw.MsgType Type)> _chat = [];
    private int _chatScroll;

    // Hover reveal (FloatingChatFrame.lua): the chrome + tabs sit at alpha 0 and
    // fade in only when the cursor rests on the frame. _chatReveal is 0..1.
    private float _chatReveal, _chatRevealTarget, _chatHoverTime;
    private bool _chatEditOpen;                       // edit box shown (Enter) - wired in a later stage
    private bool _chatDragDirty;
    private ChatFrameLaw.MsgType _chatSendType = ChatFrameLaw.MsgType.Say;
    private int _chatSelectedTab;                     // 0 = General, 1 = Combat Log

    // The docked tabs. General + Combat Log always exist in 1.12; Guild/Raid tabs
    // appear only in a guild/raid (that membership state isn't tracked yet, so
    // they're omitted rather than shown empty).
    private static readonly string[] ChatTabs = ["General", "Combat Log"];

    private void AddChatMessage(string text) => AddChatMessage(text, ChatFrameLaw.MsgType.System);

    private void AddChatMessage(string text, ChatFrameLaw.MsgType type)
    {
        // A multi-line MOTD (or any server string carrying a raw \r/\n) must not
        // survive into the stored line: the markup-aware wrapper owns pixel width,
        // so an embedded newline reaches the renderer unaccounted-for and its
        // second physical text row spills into the pitch slot reserved for the
        // chat entry above it - the exact overlap this was added to chase down.
        string cleaned = string.Join(' ', text.Replace('\r', ' ')
            .Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (cleaned.Length == 0) return;
        if (_chat.Count == ChatFrameLaw.MaxLines) _chat.RemoveAt(0);
        _chat.Add((cleaned, type));
        _chatScroll = 0;
    }

    private void ApplyExplorationExperience(byte[] body)
    {
        ExplorationExperiencePacket packet = ExplorationPackets.Parse(body);

        // The jingle is independent of whether AreaTable can name the id and plays
        // even for a zero-XP discovery (max level / an unleveled area).
        if (World.Sound.AudioFeaturePolicy.ExpandedWorldAudioEnabled && _mpq is not null)
            _explorationSounds ??= ExplorationSoundCatalog.Load(_mpq);
        if (World.Sound.AudioFeaturePolicy.ExpandedWorldAudioEnabled &&
            _entities.TryGet(ControlledGuid, out WorldEntity player) &&
            _explorationSounds?.Kit(player.Fields.Bytes0.Race) is uint kit)
        {
            PlaySpellSound(ControlledGuid, kit, trackHold: false);
        }

        EnsureAreaTableForMinimap();
        string area = _areas?.AreaName(packet.AreaId) ?? "";
        if (area.Length == 0) return;
        ShowUiInfo(ChatFrameLaw.FormatExplorationToast(area));
        if (packet.Experience > 0)
            AddChatMessage(ChatFrameLaw.FormatExplorationLine(area, packet.Experience),
                ChatFrameLaw.MsgType.System);
    }

    // ── stage 2: live incoming (SMSG_MESSAGECHAT / SMSG_NOTIFICATION) ──────────

    /// <summary>
    /// Parse a 1.12 SMSG_MESSAGECHAT body and post it typed+coloured. Wire body:
    /// u8 type, u32 language, then a per-type sender block (MONSTER_* carry the
    /// name inline; CHANNEL carries the channel name then the sender GUID; all
    /// others just the sender GUID), then u32 msgLen, the message cstring, u8 tag.
    /// The dispatcher wraps this in try/catch, so a malformed body logs, not crashes.
    /// </summary>
    private void HandleMessageChat(byte[] body)
    {
        ChatMessagePacket packet = ChatPackets.ParseMessage(body);
        ChatFrameLaw.IgnoredSenderAction ignored = ChatFrameLaw.IgnoredSender(
            _ignored.Contains(packet.SenderGuid), (ChatFrameLaw.MsgType)packet.Type,
            packet.Language);
        if (ignored == ChatFrameLaw.IgnoredSenderAction.DropAndNotify)
            _net?.ChatIgnored(packet.SenderGuid);
        if (ignored != ChatFrameLaw.IgnoredSenderAction.Continue) return;
        if (!TryPostWireChat(packet)) _pendingChatMacros.Add(packet);
    }

    /// <summary>Compose one decoded line. False means its macro subject name is still in flight;
    /// the caller retains the raw packet and retries when the name/creature query answers.</summary>
    private bool TryPostWireChat(ChatMessagePacket packet)
    {
        var type = (ChatFrameLaw.MsgType)packet.Type;
        string message = packet.Text;
        if (ChatFrameLaw.MacroExpanded(type))
        {
            ulong subjectGuid = packet.TargetGuid != 0 ? packet.TargetGuid : packet.SenderGuid;
            if (!TryResolveChatMacroSubject(subjectGuid, out QuestTextMacroLaw.Subject? subject))
                return false;
            QuestTextMacroLaw.Expansion expansion =
                QuestTextMacroLaw.ExpandChecked(message, subject, _questWorldStates);
            if (!expansion.Clean) return true; // the reference drops a known-unexpandable line
            message = expansion.Text;
        }

        message = ApplyChatLanguage(type, packet.Language, message,
            out uint effectiveLanguage, out uint defaultLanguage);

        string sender = packet.SenderName.Length > 0
            ? packet.SenderName
            : ResolveChatName(packet.SenderGuid);
        if (type == ChatFrameLaw.MsgType.Whisper && sender.Length > 0)
            _chatLastTellTarget = sender;
        if (type == ChatFrameLaw.MsgType.System) UpdateGmModeFrom(message);
        string channel = packet.Channel.Length == 0 ? "" :
            ChatChannelLaw.DisplayName(_chatChannels, packet.Channel);
        AddChatMessage(ChatFrameLaw.FormatLine(type, sender, channel, message, packet.ChatTag,
            effectiveLanguage, defaultLanguage), type);
        TrySpawnChatBubble(packet.SenderGuid, type, message);
        return true;
    }

    private string ApplyChatLanguage(ChatFrameLaw.MsgType type, uint wireLanguage, string text,
        out uint effectiveLanguage, out uint defaultLanguage)
    {
        bool havePlayer = _entities.TryGet(LocalPlayerGuid, out WorldEntity self) && self.IsPlayer;
        uint flags = havePlayer ? self.Fields.PlayerFlags : 0;
        byte race = havePlayer ? self.Fields.Bytes0.Race : _net?.Player?.Race ?? 0;
        defaultLanguage = race == 0 ? 0 : ChatLanguageLaw.DefaultLanguage(race);
        effectiveLanguage = ChatLanguageLaw.EffectiveLanguage(
            type, wireLanguage, havePlayer, flags);
        if (effectiveLanguage == 0 || !havePlayer) return text;
        if (_mpq is null) return text;

        if (!_chatLanguageLoadAttempted)
        {
            _chatLanguageLoadAttempted = true;
            _chatLanguages = ChatLanguageCatalog.Load(_mpq);
        }
        _spellCatalog ??= SpellCatalog.Load(_mpq);
        _skillLines ??= SkillLineCatalog.Load(_mpq);
        if (_chatLanguages is null || _spellCatalog is null || _skillLines is null) return text;

        uint skill = 0;
        foreach (uint knownSpell in _actions.KnownSpells.OrderBy(id => id))
        {
            if (_spellCatalog.DeclaredLanguage(knownSpell) != effectiveLanguage) continue;
            uint skillLine = _skillLines.SpellLine(knownSpell);
            if (skillLine != 0) skill = self.Fields.PlayerLanguageSkillValue(skillLine);
        }
        return _chatLanguages.GarbleChat(effectiveLanguage, skill, text);
    }

    private bool TryResolveChatMacroSubject(ulong guid, out QuestTextMacroLaw.Subject? subject)
    {
        subject = null;
        if (guid == 0) return true;

        string? name = null;
        if (guid == LocalPlayerGuid && _net?.PlayerName is { Length: > 0 } own) name = own;
        else if (_playerNames.TryGetValue(guid, out string? known) && known.Length > 0) name = known;

        if (_entities.TryGet(guid, out WorldEntity unit))
        {
            if (unit.IsCreature)
            {
                if (!_creatureNames.TryGetValue(unit.Entry, out name) || name.Length == 0)
                {
                    if (_net is not null && TryBeginCreatureQuery(unit.Entry))
                        _net.CreatureQuery(unit.Entry, guid);
                    return false;
                }
                // The reference's non-player arm substitutes the unit name for race/class.
                subject = new QuestTextMacroLaw.Subject(name, name, name, unit.Fields.Bytes0.Gender);
                return true;
            }
            if (name is not null)
            {
                var bytes = unit.Fields.Bytes0;
                subject = new QuestTextMacroLaw.Subject(name, RaceName(bytes.Race),
                    ClassName(bytes.Class), bytes.Gender);
                return true;
            }
        }

        if (name is not null)
        {
            subject = _playerTraits.TryGetValue(guid, out PlayerTraits traits)
                ? new QuestTextMacroLaw.Subject(name, RaceName(traits.Race),
                    ClassName(traits.Class), traits.Gender)
                : new QuestTextMacroLaw.Subject(name, "", "", 0);
            return true;
        }
        if (_chatNameQueried.Add(guid)) _net?.NameQuery(guid);
        return false;
    }

    private void FlushPendingChatMacros(ulong guid = 0)
    {
        for (int i = _pendingChatMacros.Count - 1; i >= 0; i--)
        {
            ChatMessagePacket packet = _pendingChatMacros[i];
            ulong subjectGuid = packet.TargetGuid != 0 ? packet.TargetGuid : packet.SenderGuid;
            if (guid != 0 && subjectGuid != guid) continue;
            if (TryPostWireChat(packet)) _pendingChatMacros.RemoveAt(i);
        }
    }

    private void PostCombatXpGain(CombatXpGain xp)
    {
        // Non-kill awards use the unnamed global string. A named kill follows
        // Benilla's ask-and-defer route so the feed never invents a placeholder.
        if (!xp.Kill || xp.Victim == 0)
        {
            AddChatMessage(ChatFrameLaw.FormatXpGain(null, xp.Total, 0),
                ChatFrameLaw.MsgType.CombatXpGain);
            return;
        }

        if (!TryResolveCombatXpVictim(xp.Victim, out string name))
        {
            _pendingChatXp.Add(xp);
            BeginCombatXpVictimQuery(xp.Victim);
            return;
        }

        uint bonus = xp.Total >= xp.Base ? xp.Total - xp.Base : 0;
        AddChatMessage(ChatFrameLaw.FormatXpGain(name, xp.Total, bonus),
            ChatFrameLaw.MsgType.CombatXpGain);
    }

    private bool TryResolveCombatXpVictim(ulong guid, out string name)
    {
        if (_playerNames.TryGetValue(guid, out string? player) && player.Length > 0)
        {
            name = player;
            return true;
        }

        uint entry = GuidInfo.Entry(guid) ??
            (_entities.TryGet(guid, out WorldEntity entity) ? entity.Entry : 0);
        if (entry != 0 && _creatureNames.TryGetValue(entry, out string? creature) &&
            creature.Length > 0)
        {
            name = creature;
            return true;
        }

        // A completed negative query follows the reference's bounded fallback.
        if (entry != 0 && _creatureQueryRecords.ContainsKey(entry))
        {
            name = "Unknown";
            return true;
        }

        name = "";
        return false;
    }

    private void BeginCombatXpVictimQuery(ulong guid)
    {
        if (_net is null) return;
        uint entry = GuidInfo.Entry(guid) ??
            (_entities.TryGet(guid, out WorldEntity entity) ? entity.Entry : 0);
        if (entry != 0)
        {
            if (TryBeginCreatureQuery(entry)) _net.CreatureQuery(entry, guid);
        }
        else if (_chatNameQueried.Add(guid)) _net.NameQuery(guid);
    }

    private void FlushPendingChatXp(ulong guid = 0)
    {
        for (int i = _pendingChatXp.Count - 1; i >= 0; i--)
        {
            CombatXpGain xp = _pendingChatXp[i];
            if (guid != 0 && xp.Victim != guid) continue;
            if (!TryResolveCombatXpVictim(xp.Victim, out string name)) continue;
            uint bonus = xp.Total >= xp.Base ? xp.Total - xp.Base : 0;
            AddChatMessage(ChatFrameLaw.FormatXpGain(name, xp.Total, bonus),
                ChatFrameLaw.MsgType.CombatXpGain);
            _pendingChatXp.RemoveAt(i);
        }
    }

    private void HandleChannelNotice(byte[] body)
    {
        ChannelNoticePacket packet = ChannelPackets.ParseNotice(body);
        if (!TryPostChannelNotice(packet)) _pendingChatChannelNotices.Add(packet);
    }

    private bool TryPostChannelNotice(ChannelNoticePacket packet)
    {
        // MODE_CHANGE has no GlobalStrings notice in 1.12.
        if (packet.Notice == ChannelNotice.ModeChange) return true;

        string first = packet.Name;
        string second = "";
        if (packet.FirstGuid != 0 && !TryResolveChannelPlayer(packet.FirstGuid, out first))
            return false;
        if (packet.SecondGuid != 0 && !TryResolveChannelPlayer(packet.SecondGuid, out second))
            return false;

        // The confirmation owns numbering. Claim before YOU_JOINED is composed;
        // free only after YOU_LEFT is composed, preserving the leaving line's slot.
        if (packet.Notice == ChannelNotice.YouJoined)
            ChatChannelLaw.ClaimSlot(_chatChannels, packet.Channel);
        string display = ChatChannelLaw.DisplayName(_chatChannels, packet.Channel);

        if (packet.Notice is ChannelNotice.Joined or ChannelNotice.Left)
        {
            AddChatMessage(ChatChannelLaw.FormatMember(display, first,
                    packet.Notice == ChannelNotice.Joined),
                packet.Notice == ChannelNotice.Joined
                    ? ChatFrameLaw.MsgType.ChannelJoin : ChatFrameLaw.MsgType.ChannelLeave);
        }
        else if (ChatChannelLaw.FormatNotice(packet.Notice, display, first, second) is { } line)
        {
            AddChatMessage(line, packet.Notice == ChannelNotice.Invite
                ? ChatFrameLaw.MsgType.ChannelNoticeUser : ChatFrameLaw.MsgType.ChannelNotice);
        }

        if (packet.Notice == ChannelNotice.YouLeft)
            ChatChannelLaw.FreeSlot(_chatChannels, packet.Channel);
        return true;
    }

    private bool TryResolveChannelPlayer(ulong guid, out string name)
    {
        if (guid == LocalPlayerGuid && _net?.PlayerName is { Length: > 0 } own)
        {
            name = own;
            return true;
        }
        if (_playerNames.TryGetValue(guid, out string? known) && known.Length > 0)
        {
            name = known;
            return true;
        }
        if (_chatNameQueried.Add(guid)) _net?.NameQuery(guid);
        name = "";
        return false;
    }

    private void FlushPendingChatChannelNotices(ulong guid = 0)
    {
        for (int i = _pendingChatChannelNotices.Count - 1; i >= 0; i--)
        {
            ChannelNoticePacket packet = _pendingChatChannelNotices[i];
            if (guid != 0 && packet.FirstGuid != guid && packet.SecondGuid != guid) continue;
            if (!TryPostChannelNotice(packet)) continue;
            _pendingChatChannelNotices.RemoveAt(i);
        }
    }

    private void HandleChannelList(byte[] body)
    {
        ChannelListPacket packet = ChannelPackets.ParseList(body);
        string display = ChatChannelLaw.DisplayName(_chatChannels, packet.Channel);
        AddChatMessage(ChatChannelLaw.FormatList(display, packet.Members.Count),
            ChatFrameLaw.MsgType.ChannelList);
    }

    /// <summary>SMSG_NOTIFICATION is a lone cstring - post it as a system line.</summary>
    private void HandleNotification(byte[] body) =>
        AddChatMessage(new PacketReader(body).ReadCString(), ChatFrameLaw.MsgType.System);

    /// <summary>
    /// SMSG_TEXT_EMOTE (0x0105): u64 sourceGuid, u32 textEmote, u32 emoteNum
    /// (unused in 1.12 - EmotesTextLaw picks the sentence by viewer perspective
    /// and gender, not by this counter), u32 namlen, then the target's name
    /// cstring (namlen==1 is just the lone NUL byte for "no target" - ReadCString
    /// handles that correctly without needing the length itself).
    ///
    /// The server sends raw ids only; VMaNGOS never resolves EmotesText.dbc's
    /// text array server-side (confirmed against its own EmotesTextEntry struct,
    /// which only reads Id and the animation id), so building the sentence is
    /// entirely on us - see EmotesTextLaw's doc comment for the full derivation.
    /// </summary>
    private void HandleTextEmoteReceive(byte[] body)
    {
        var r = new PacketReader(body);
        ulong sourceGuid = r.ReadU64();
        uint textEmote = r.ReadU32();
        r.ReadU32();                                   // emoteNum - unused, see above
        r.ReadU32();                                   // namlen - ReadCString doesn't need it
        string targetName = r.ReadCString();

        bool hasTarget = targetName.Length > 0;
        bool viewerIsEmoter = sourceGuid == LocalPlayerGuid;
        bool viewerIsTarget = hasTarget &&
            string.Equals(targetName, _net?.PlayerName, StringComparison.OrdinalIgnoreCase);
        bool emoterIsFemale = _entities.TryGet(sourceGuid, out WorldEntity emoter) &&
            emoter.Fields.Bytes0.Gender == 1;
        string emoterName = ResolveChatName(sourceGuid);

        // SMSG_TEXT_EMOTE is also the receive-side voice trigger. Race/sex are
        // descriptor data, never inferred from the name or display; absent race
        // stays silent. The server echo routes our own emotes through this same path.
        if (World.Sound.AudioFeaturePolicy.ExpandedWorldAudioEnabled &&
            _soundscapePlaybackArmed && emoter is not null && _spellSounds is not null &&
            _controller is not null)
        {
            var traits = emoter.Fields.Bytes0;
            if (traits.Race != 0 && _emoteTextSounds?.TryGet(
                    textEmote, traits.Race, traits.Gender, out uint voiceKit) == true)
            {
                Vector3 source = TryGetWorldBodyPose(sourceGuid, out WorldBodyPose bodyPose)
                    ? bodyPose.Position
                    : emoter.Position;
                _spellSounds.Play(voiceKit, sourceGuid, source, _controller.Position,
                    forceLoop: false, trackHold: false, category: "sfx");
            }
        }

        string? line = EmotesTextLaw.Resolve((int)textEmote, hasTarget, viewerIsEmoter,
            viewerIsTarget, emoterIsFemale, emoterName, targetName);
        if (line is not null) AddChatMessage(line, ChatFrameLaw.MsgType.TextEmote);
    }

    /// <summary>A sender GUID to a name via the shared cache; query once if unknown.</summary>
    private string ResolveChatName(ulong guid)
    {
        if (guid == 0) return "";
        if (_playerNames.TryGetValue(guid, out string? n) && n.Length > 0) return n;
        // The client already knows its own name from char-select/login (NetworkClient.PlayerName)
        // - _playerNames only ever gets seeded by a NAME_QUERY round-trip, so without this the
        // player's own very first chat line always showed the GUID placeholder (same fallback
        // OwnCharacterPartyRow already uses for the party frame).
        if (guid == LocalPlayerGuid && _net?.PlayerName is { Length: > 0 } own) return own;
        if (_chatNameQueried.Add(guid)) _net?.NameQuery(guid);
        return $"Player-{guid & 0xffff:X4}";           // placeholder until the response lands
    }

    // The GM-mode flag the combat dev-tools read used to be scraped from server
    // chat by the old crude decoder; keep that side effect on system lines.
    private void UpdateGmModeFrom(string text)
    {
        if (text.Contains("GM mode is ON", StringComparison.OrdinalIgnoreCase)) _serverGmMode = true;
        else if (text.Contains("GM mode is OFF", StringComparison.OrdinalIgnoreCase)) _serverGmMode = false;
    }

    private void DrawChatFrame()
    {
        if (_gameplayArt is null) return;
        float s = GameplayUiScale();
        Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        Settings.HudLayout ??= new GameSettings.HudLayoutSettings();
        Vector2 authoredRoot = ChatFrameLaw.FrameOrigin(logicalDisplay);
        // The free view docks the SQUARE minimap to the bottom-left corner the
        // chat normally owns — the chat frame lifts above that furniture.
        if (_freeView) authoredRoot.Y -= 124f;
        Vector2 savedOffset = new(Settings.HudLayout.ChatOffsetX, Settings.HudLayout.ChatOffsetY);
        Vector2 root = ChatFrameLaw.ClampFrameOrigin(authoredRoot + savedOffset, logicalDisplay);
        DrawChatMover(ref root, authoredRoot, logicalDisplay, s);
        Vector2 rootPx = root * s;
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();

        UpdateChatReveal(root, s);

        if (_uiParityArmed && _uiParityPanel == "chat-frame")
        {
            BeginUiParityFrame(rootPx, s);
            CollectUiParity("ChatFrame1", "ScrollingMessageFrame", rootPx,
                ChatFrameLaw.FrameScaledSize(s),
                parent: "", point: "BOTTOMLEFT", offsetX: "32", offsetY: "85", strata: "BACKGROUND");
            CollectUiParity("ChatFrame1/FontString", "FontString", rootPx,
                ChatFrameLaw.FrameScaledSize(s),
                parent: "ChatFrame1", font: "ChatFontNormal", fontPath: @"Fonts\ARIALN.TTF",
                fontSize: "14", color: "#FFFFFFFF", strata: "BACKGROUND");
            CollectUiParity("ChatFrame1Background", "Texture",
                (root + ChatFrameLaw.BackgroundRect.Min) * s,
                ChatFrameLaw.BackgroundRect.Size * s,
                parent: "ChatFrame1", point: "TOPLEFT", offsetX: "-2", offsetY: "3",
                texture: ChatFrameLaw.Background, layer: "BACKGROUND", strata: "BACKGROUND");
        }

        DrawChatChrome(dl, root, s);
        DrawChatMessages(dl, root, s);
        // The left button column is always visible; all four seats come from the FrameXML anchor
        // chain frozen in ChatFrameLaw rather than an ImGui-derived ladder.
        DrawChatMenuButton(dl, root + ChatFrameLaw.MenuButtonRect.Min);
        DrawChatScrollButton(dl, root + ChatFrameLaw.ScrollUpButtonRect.Min,
            "ScrollUp", () => _chatScroll++);
        DrawChatScrollButton(dl, root + ChatFrameLaw.ScrollDownButtonRect.Min, "ScrollDown",
            () => _chatScroll = Math.Max(0, _chatScroll - 1));
        DrawChatScrollButton(dl, root + ChatFrameLaw.ScrollEndButtonRect.Min,
            "ScrollEnd", () => _chatScroll = 0);
        DrawChatTabs(dl, root, s);
        DrawChatMenu(root + ChatFrameLaw.MenuButtonRect.Min);

        if (_chatEditOpen) DrawChatEditBox(dl, root, s);

        if (_uiParityArmed && _uiParityPanel == "chat-frame") MarkUiParityFrameComplete();
    }

    private void DrawChatMover(ref Vector2 root, Vector2 authoredRoot,
        Vector2 logicalDisplay, float scale)
    {
        if (!Settings.HudLayout.ChatUnlocked) return;

        Vector2 handleSize = new(92f, 22f);
        Vector2 handleOffset = new(ChatFrameLaw.FrameWidth - handleSize.X, -30f);
        Vector2 handle = root + handleOffset;
        ImGui.SetNextWindowPos(handle * scale);
        ImGui.SetNextWindowSize(handleSize * scale);
        ImGui.SetNextWindowBgAlpha(.82f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav;
        ImGui.Begin("##chat-frame-mover", flags);
        ImGui.Button("Drag chat##chat-frame-drag", handleSize * scale);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            root = ChatFrameLaw.ClampFrameOrigin(
                root + ImGui.GetIO().MouseDelta / MathF.Max(.01f, scale), logicalDisplay);
            Vector2 offset = root - authoredRoot;
            Settings.HudLayout.ChatOffsetX = offset.X;
            Settings.HudLayout.ChatOffsetY = offset.Y;
            _chatDragDirty = true;
        }
        if (_chatDragDirty && ImGui.IsItemDeactivated())
        {
            SettingsFile?.Save();
            _chatDragDirty = false;
        }
        ImGui.End();
    }

    /// <summary>
    /// FCF reveal: a cursor that rests STILL on the frame's hover rect (expanded
    /// 45 up into the tabs, 10 down, 5 sides) for RevealDelay arms the reveal;
    /// leaving disarms it. The reveal eases toward its target over FadeTime, and
    /// every chrome alpha is (its target × reveal).
    /// </summary>
    private void UpdateChatReveal(Vector2 root, float s)
    {
        float dt = ImGui.GetIO().DeltaTime;
        ChatFrameLaw.ScreenRect hover = ChatFrameLaw.HoverScreenRect(root, s);
        bool over = ImGui.IsMouseHoveringRect(hover.Min, hover.Max, false);
        bool moved = ImGui.GetIO().MouseDelta.LengthSquared() > 0.01f;

        if (over)
        {
            _chatHoverTime = moved ? 0f : _chatHoverTime + dt;
            if (_chatHoverTime > ChatFrameLaw.RevealDelay) _chatRevealTarget = 1f;
        }
        else
        {
            _chatRevealTarget = 0f;
            _chatHoverTime = 0f;
        }
        if (_chatEditOpen) _chatRevealTarget = 1f;   // typing keeps it revealed

        float span = dt / ChatFrameLaw.FadeTime;
        if (_chatReveal < _chatRevealTarget) _chatReveal = MathF.Min(_chatRevealTarget, _chatReveal + span);
        else if (_chatReveal > _chatRevealTarget) _chatReveal = MathF.Max(_chatRevealTarget, _chatReveal - span);
    }

    /// <summary>Background + 8-slice border, both white art vertex-tinted BLACK
    /// (FCF_SetWindowColor ships 0,0,0), at ChromeAlpha × reveal.</summary>
    private void DrawChatChrome(ImDrawListPtr dl, Vector2 root, float s)
    {
        if (_chatReveal <= 0.001f) return;
        uint tint = BlackAlpha(ChatFrameLaw.ChromeAlpha * _chatReveal);

        // Background stretches -2/+2 in x, +3/-6 in y past the frame rect.
        DrawChatTexture(dl, root + ChatFrameLaw.BackgroundRect.Min,
            ChatFrameLaw.BackgroundRect.Size, ChatFrameLaw.Background,
            Vector2.Zero, Vector2.One, tint);

        for (int slice = 0; slice < 8; slice++)
        {
            ChatFrameLaw.LogicalRect rect = ChatFrameLaw.BorderRect(slice);
            DrawBorderSlice(dl, root + rect.Min, rect.Size, slice, tint);
        }
    }

    private void DrawBorderSlice(ImDrawListPtr dl, Vector2 min, Vector2 size, int slice, uint tint)
    {
        DrawChatTexture(dl, min, size, ChatFrameLaw.Border,
            ChatFrameLaw.BorderUvMin(slice), ChatFrameLaw.BorderUvMax(slice), tint);
    }

    private void DrawChatMessages(ImDrawListPtr dl, Vector2 root, float s)
    {
        float wrapPx = (ChatFrameLaw.FrameWidth - 8f) * s;
        float pitch = GameText.LinePitch(ChatFrameLaw.ChatFont, s);
        if (pitch <= 0f) return;

        var lines = new List<UiTextMarkupLine>();
        foreach (var (text, type) in _chat)
        {
            if (!ChatFrameLaw.VisibleInTab(type, _chatSelectedTab)) continue;
            uint color = ChatFrameLaw.Color(type);
            Vector4 baseColor = ImGui.ColorConvertU32ToFloat4(color);
            lines.AddRange(UiTextMarkupLaw.Wrap(text, baseColor,
                glyph => GameText.MeasureWidth(ChatFrameLaw.ChatFont, glyph, s), wrapPx));
        }

        int maxVisible = Math.Max(1, (int)(ChatFrameLaw.FrameHeight * s / pitch));
        _chatScroll = Math.Clamp(_chatScroll, 0, Math.Max(0, lines.Count - maxVisible));
        int bottom = lines.Count - _chatScroll;
        int top = Math.Max(0, bottom - maxVisible);

        for (int i = bottom - 1, row = 0; i >= top; i--, row++)
        {
            Vector2 at = ChatFrameLaw.MessagePosition(root, row, pitch, s);
            foreach (UiTextColorRun run in lines[i].Runs)
            {
                uint runColor = ImGui.ColorConvertFloat4ToU32(run.Color);
                GameText.Draw(dl, ChatFrameLaw.ChatFont, run.Text, at, s, runColor);
                float width = GameText.MeasureWidth(ChatFrameLaw.ChatFont, run.Text, s);
                if (run.Link is { Markup.Length: > 0 } link && width > 0)
                {
                    // Chat is painted on the background draw list and has no
                    // ImGui window. A window-bound InvisibleButton here activates
                    // ImGui's otherwise-hidden Debug##Default fallback. Link
                    // interaction is purely rectangular, like the chat controls.
                    Vector2 linkSize = ChatFrameLaw.LinkHitSize(width, pitch);
                    if (ImGui.IsMouseHoveringRect(at, at + linkSize, false))
                    {
                        ChatFrameLaw.ScreenLine underline =
                            ChatFrameLaw.LinkUnderline(at, width, pitch);
                        dl.AddLine(underline.Start, underline.End, runColor, MathF.Max(1, s));
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                            ActivateChatLink(link, ImGuiMouseButton.Left);
                        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                            ActivateChatLink(link, ImGuiMouseButton.Right);
                    }
                }
                at.X += width;
            }
        }
    }

    private void DrawChatScrollButton(ImDrawListPtr dl, Vector2 logicalMin, string direction, Action click)
    {
        float s = GameplayUiScale();
        Vector2 min = logicalMin * s,
            size = ChatFrameLaw.ControlButtonScaledSize(s);
        bool hovered = ImGui.IsMouseHoveringRect(min, min + size, false);
        bool pressed = hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        string texture = $@"Interface\ChatFrame\UI-ChatIcon-{direction}-{(pressed ? "Down" : "Up")}";
        uint handle = _gameplayArt?.Handle(texture) ?? 0;
        // Always visible (the reference keeps the scroll column up regardless of hover).
        if (handle != 0) dl.AddImage((nint)handle, min, min + size);
        // UI-Common-MouseHilight, ADD-blended - same hover overlay every other icon
        // button in this codebase uses (DrawChatTabs above, Mail's page buttons, …).
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\UI-Common-MouseHilight") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, min, min + size);
        }
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) click();

        if (!_uiParityArmed || _uiParityPanel != "chat-frame") return;
        string name = direction switch { "ScrollUp" => "ChatFrame1UpButton",
            "ScrollDown" => "ChatFrame1DownButton", _ => "ChatFrame1BottomButton" };
        string point = direction == "ScrollUp" ? "BOTTOM" : direction == "ScrollDown" ? "BOTTOM" : "BOTTOMLEFT";
        string relative = direction == "ScrollUp" ? "ChatFrame1DownButton"
            : direction == "ScrollDown" ? "ChatFrame1BottomButton" : "";
        string relativePoint = direction is "ScrollUp" or "ScrollDown" ? "TOP" : "";
        string ox = direction == "ScrollEnd" ? "-32" : direction == "ScrollDown" ? "0" : "";
        string oy = direction == "ScrollEnd" ? "-4" : direction == "ScrollDown" ? "-2" : "";
        CollectUiParity(name, "Button", min, size, parent: "ChatFrame1", point: point,
            relativeTo: relative, relativePoint: relativePoint, offsetX: ox, offsetY: oy,
            texture: texture, strata: "BACKGROUND");
        CollectUiParity(name + "/NormalTexture", "NormalTexture", min, size, parent: name,
            texture: texture, strata: "BACKGROUND");
    }

    /// <summary>
    /// The docked tab row (General, Combat Log, …): each a 3-slice ChatFrameTab
    /// auto-sized to label+37, label in GameFontNormalSmall (FRIZQT 10 gold),
    /// UI-Character-Tab-Highlight (ADD) on mouseover. The tabs fade with the frame
    /// - selected at 1.0 × reveal, unselected at 0.5 × reveal. Label and highlight
    /// both sit below the tab centre (FrameXML y=-5 / -7, screen-down).
    /// </summary>
    private void DrawChatTabs(ImDrawListPtr dl, Vector2 root, float s)
    {
        if (_chatReveal <= 0.001f) return;
        int em = GameText.EmPixels(ChatFrameLaw.TabFont, s);
        float x = 0f;   // logical x from root

        for (int t = 0; t < ChatTabs.Length; t++)
        {
            string label = ChatTabs[t];
            bool selected = t == _chatSelectedTab;
            float midW = GameText.MeasureWidth(ChatFrameLaw.TabFont, label, s) / s + 5f;
            ChatFrameLaw.TabLayout tab = ChatFrameLaw.TabGeometry(root, x, midW, s, em);
            float alpha = (selected ? ChatFrameLaw.TabSelectedAlpha : ChatFrameLaw.TabUnselectedAlpha) * _chatReveal;
            uint tint = WhiteAlpha(alpha);

            DrawChatTexture(dl, tab.Left.Min, tab.Left.Size, ChatFrameLaw.Tab,
                ChatFrameLaw.TabLeftUvMin, ChatFrameLaw.TabLeftUvMax, tint);
            DrawChatTexture(dl, tab.Middle.Min, tab.Middle.Size, ChatFrameLaw.Tab,
                ChatFrameLaw.TabMiddleUvMin, ChatFrameLaw.TabMiddleUvMax, tint);
            DrawChatTexture(dl, tab.Right.Min, tab.Right.Size, ChatFrameLaw.Tab,
                ChatFrameLaw.TabRightUvMin, Vector2.One, tint);

            if (ImGui.IsMouseHoveringRect(tab.Hit.Min * s,
                    (tab.Hit.Min + tab.Hit.Size) * s, false))
            {
                uint hl = _gameplayArt?.AdditiveHandle(ChatFrameLaw.TabHighlight) ?? 0;
                if (hl != 0)
                    dl.AddImage((nint)hl,
                        tab.Highlight.Min * s,
                        (tab.Highlight.Min + tab.Highlight.Size) * s,
                        Vector2.Zero, Vector2.One, WhiteAlpha(_chatReveal));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _chatSelectedTab = t;
            }

            uint labelColor = WithAlpha(FontObjectLaw.Get(ChatFrameLaw.TabFont).Color, alpha);
            GameText.Draw(dl, ChatFrameLaw.TabFont, label, tab.LabelPosition, s, labelColor);

            x += tab.Width;
        }
    }

    /// <summary>The speech-bubble ChatFrameMenuButton, always visible.</summary>
    private void DrawChatMenuButton(ImDrawListPtr dl, Vector2 logicalMin)
    {
        float s = GameplayUiScale();
        Vector2 min = logicalMin * s,
            size = ChatFrameLaw.ControlButtonScaledSize(s);
        bool hovered = ImGui.IsMouseHoveringRect(min, min + size, false);
        bool pressed = hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        string texture = pressed
            ? @"Interface\ChatFrame\UI-ChatIcon-Chat-Down" : ChatFrameLaw.MenuButton;
        uint handle = _gameplayArt?.Handle(texture) ?? 0;
        if (handle != 0) dl.AddImage((nint)handle, min, min + size);
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\UI-Common-MouseHilight") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, min, min + size);
        }
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _chatMenuOpen = true;
            _chatMenuOpenedThisFrame = true;
            _chatMenuSubmenu = ChatMenuLevel.None;
            _chatMenuCloseAt = NowSeconds() + ChatMenuUiLaw.TimeoutSeconds;
            PlayUiSound(ChatMenuUiLaw.OpenSound, ChatMenuUiLaw.SoundCategory);
        }
    }

    private void DrawChatMenu(Vector2 buttonMin)
    {
        if (!_chatMenuOpen) return;
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize / s;
        Vector2 mouse = ImGui.GetIO().MousePos / s;
        IReadOnlyList<ChatMenuRow> rootRows = ChatMenuUiLaw.Rows(ChatMenuLevel.Root);
        Vector2 rootOrigin = ChatMenuUiLaw.RootOrigin(buttonMin, rootRows.Count, display);
        int rootHover = ChatMenuUiLaw.HitRow(mouse, rootOrigin, rootRows.Count);

        if (rootHover >= 0)
        {
            ChatMenuLevel nested = rootRows[rootHover].Nested;
            if (nested != ChatMenuLevel.None) _chatMenuSubmenu = nested;
            else _chatMenuSubmenu = ChatMenuLevel.None;
        }

        IReadOnlyList<ChatMenuRow> childRows = ChatMenuUiLaw.Rows(_chatMenuSubmenu);
        int parentRow = _chatMenuSubmenu switch
        {
            ChatMenuLevel.Emote => 5,
            ChatMenuLevel.VoiceEmote => 7,
            _ => -1,
        };
        Vector2 childOrigin = parentRow >= 0
            ? ChatMenuUiLaw.SubmenuOrigin(rootOrigin, parentRow, childRows.Count, display)
            : Vector2.Zero;
        int childHover = parentRow >= 0
            ? ChatMenuUiLaw.HitRow(mouse, childOrigin, childRows.Count) : -1;

        bool overRoot = ChatMenuUiLaw.Contains(mouse, rootOrigin, rootRows.Count);
        bool overChild = parentRow >= 0 &&
            ChatMenuUiLaw.Contains(mouse, childOrigin, childRows.Count);
        if (overRoot || overChild)
            _chatMenuCloseAt = NowSeconds() + ChatMenuUiLaw.TimeoutSeconds;
        else if (NowSeconds() >= _chatMenuCloseAt)
        {
            CloseChatMenu();
            return;
        }

        ImDrawListPtr dl = ImGui.GetForegroundDrawList();
        DrawChatMenuLevel(dl, rootOrigin, rootRows, rootHover, s);
        if (parentRow >= 0)
            DrawChatMenuLevel(dl, childOrigin, childRows, childHover, s);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (childHover >= 0)
            {
                ChatMenuRow row = childRows[childHover];
                if (row.Command.Length > 0)
                {
                    _chatInput = row.Command;
                    SubmitChat();
                }
                PlayUiSound(ChatMenuUiLaw.RowSound, ChatMenuUiLaw.SoundCategory);
                CloseChatMenu();
            }
            else if (rootHover >= 0)
            {
                ChatMenuRow row = rootRows[rootHover];
                if (row.InputPrefix.Length > 0)
                {
                    string prefill = row.InputPrefix == "/r " && _chatLastTellTarget.Length > 0
                        ? $"/w {_chatLastTellTarget} " : row.InputPrefix;
                    OpenChatEditWith(prefill);
                }
                PlayUiSound(ChatMenuUiLaw.RowSound, ChatMenuUiLaw.SoundCategory);
                CloseChatMenu();
            }
            else if (!_chatMenuOpenedThisFrame) CloseChatMenu();
        }
        _chatMenuOpenedThisFrame = false;
    }

    private void DrawChatMenuLevel(ImDrawListPtr dl, Vector2 logicalOrigin,
        IReadOnlyList<ChatMenuRow> rows, int hoveredRow, float s)
    {
        Vector2 origin = logicalOrigin * s;
        Vector2 size = ChatMenuUiLaw.CardScaledSize(rows.Count, s);
        _skin!.DrawBackdrop(dl, origin, origin + size, WowSkin.Tooltip);
        uint highlight = _gameplayArt?.AdditiveHandle(
            @"Interface\QuestFrame\UI-QuestTitleHighlight") ?? 0;

        for (int i = 0; i < rows.Count; i++)
        {
            ChatMenuRow row = rows[i];
            Vector2 rowMin = (logicalOrigin + ChatMenuUiLaw.RowOrigin(i)) * s;
            Vector2 rowSize = ChatMenuUiLaw.RowSize * s;
            if (i == hoveredRow && highlight != 0)
                dl.AddImage((nint)highlight, rowMin, rowMin + rowSize);
            string font = i == hoveredRow ? "GameFontHighlight" : "GameFontNormal";
            GameText.Draw(dl, font, row.Label,
                (logicalOrigin + ChatMenuUiLaw.TextOrigin(i)) * s, s);
            if (row.Shortcut.Length == 0) continue;
            float shortcutWidth = GameText.MeasureWidth(font, row.Shortcut, s);
            Vector2 shortcutPos = ChatMenuUiLaw.ShortcutPosition(
                rowMin, rowSize, shortcutWidth, s);
            GameText.Draw(dl, font, row.Shortcut, shortcutPos, s);
        }
    }

    private void CloseChatMenu()
    {
        _chatMenuOpen = false;
        _chatMenuOpenedThisFrame = false;
        _chatMenuSubmenu = ChatMenuLevel.None;
    }

    /// <summary>
    /// The chat input: opaque 3-piece UI-ChatInputBorder, header ("Say:"/"Guild:"…)
    /// in ChatFontNormal coloured by the current send type. Shown only while typing.
    /// </summary>
    private void DrawChatEditBox(ImDrawListPtr dl, Vector2 root, float s)
    {
        _chatSendType = PeekChatType(_chatInput);
        string header = ChatFrameLaw.Header(_chatSendType);
        uint color = ChatFrameLaw.Color(_chatSendType);
        int em = GameText.EmPixels(ChatFrameLaw.ChatFont, s);
        float headerWidth = GameText.MeasureWidth(ChatFrameLaw.ChatFont, header, s);
        ChatFrameLaw.EditLayout edit = ChatFrameLaw.EditGeometry(
            root, s, em, headerWidth, ImGui.GetFontSize());

        DrawChatTexture(dl, edit.Left.Min, edit.Left.Size,
            ChatFrameLaw.EditLeft, Vector2.Zero, Vector2.One, 0xffffffffu);
        DrawChatTexture(dl, edit.Middle.Min, edit.Middle.Size,
            ChatFrameLaw.EditRight, Vector2.Zero, ChatFrameLaw.EditMiddleUvMax, 0xffffffffu);
        DrawChatTexture(dl, edit.Right.Min, edit.Right.Size,
            ChatFrameLaw.EditRight, ChatFrameLaw.EditRightUvMin, Vector2.One, 0xffffffffu);

        // The header reflects the send type peeked from a leading /slash, and tints
        // both it and the typed text (Say white, Guild green, Whisper pink…).
        GameText.Draw(dl, ChatFrameLaw.ChatFont, header, edit.HeaderPosition, s, color);

        // The editable field is a transparent ImGui InputText overlaid after the
        // header. Its focus sets WantCaptureKeyboard, which the movement gate at
        // Program.cs already honours - so WASD won't walk while typing.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, edit.FramePadding);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, 0u);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.SetNextWindowPos(edit.InputPosition);
        ImGui.SetNextWindowSize(edit.InputSize);
        if (ImGui.Begin("##chat-input-window", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoNav))
        {
            if (_chatEditJustOpened) { ImGui.SetKeyboardFocusHere(); _chatEditJustOpened = false; }
            ImGui.SetNextItemWidth(edit.InputSize.X);
            unsafe { _chatEditCallback ??= ChatEditCursorCallback; }
            bool submit = ImGui.InputText("##chat-edit", ref _chatInput, 255,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackAlways,
                _chatEditCallback);
            bool active = ImGui.IsItemActive();
            if (active) _chatEditActivated = true;
            if (submit) SubmitChat();
            else if (_chatEditActivated && !active) CloseChatEdit();   // escape / click-away
        }
        ImGui.End();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    private void OpenChatEdit()
    {
        _chatEditOpen = true;
        _chatEditJustOpened = true;
        _chatEditActivated = false;
        _chatInput = "";
        _chatSendType = ChatFrameLaw.MsgType.Say;
    }

    /// <summary>ChatFrame_SendTell: open the edit box pre-filled (e.g. "/w Name ").</summary>
    private void OpenChatEditWith(string prefill)
    {
        OpenChatEdit();
        _chatInput = prefill;
        _chatEditCursorToEnd = true;
    }

    private void InsertChatText(string text)
    {
        int room = Math.Max(0, 255 - _chatInput.Length);
        if (room > 0) _chatInput += text[..Math.Min(text.Length, room)];
        _chatEditCursorToEnd = true;
    }

    // Focus via SetKeyboardFocusHere selects the whole buffer, so the first keystroke would
    // replace a prefill; the one-shot callback parks the caret at the end instead.
    private unsafe int ChatEditCursorCallback(ImGuiInputTextCallbackData* data)
    {
        if (_chatEditCursorToEnd)
        {
            data->CursorPos = data->BufTextLen;
            data->SelectionStart = data->BufTextLen;
            data->SelectionEnd = data->BufTextLen;
            _chatEditCursorToEnd = false;
        }
        return 0;
    }

    private void CloseChatEdit()
    {
        _chatEditOpen = false;
        _chatEditActivated = false;
        _chatInput = "";
    }

    private void SubmitChat()
    {
        SubmitChatLine(_chatInput);
        CloseChatEdit();
    }

    /// <summary>Shared ChatFrame submission path for typed input and EXECUTE_CHAT_LINE macros.</summary>
    private void SubmitChatLine(string input)
    {
        string raw = input.Trim();
        if (raw.StartsWith('/'))
        {
            int split = raw.IndexOf(' ');
            string command = (split < 0 ? raw : raw[..split]).ToLowerInvariant();
            if (command is "/r" or "/reply")
            {
                if (_chatLastTellTarget.Length == 0)
                {
                    AddChatMessage("You have nobody to reply to yet.");
                    return;
                }
                string text = split < 0 ? "" : raw[(split + 1)..].TrimStart();
                raw = $"/w {_chatLastTellTarget} {text}";
            }
        }
        if (raw.Length > 0 && !TrySubmitClientSlashCommand(raw) && !TrySubmitTextEmote(raw))
        {
            (ChatFrameLaw.MsgType type, string? target, string message) = ParseChatCommand(raw);
            // The server echoes our own line back as SMSG_MESSAGECHAT, so there is
            // no local echo here - it appears when the round-trip lands.
            if (message.Length > 0) _net?.SendChat((uint)type, target, message);
        }
    }

    /// <summary>Client-owned slash verbs that send a non-chat opcode.</summary>
    private bool TrySubmitClientSlashCommand(string raw)
    {
        if (!raw.StartsWith('/')) return false;
        int space = raw.IndexOf(' ');
        string command = (space < 0 ? raw : raw[..space]).ToLowerInvariant();
        string args = space < 0 ? "" : raw[(space + 1)..];
        if (command == "/partytest")
        {
            string mode = args.ToLowerInvariant();
            switch (mode)
            {
                case "off":
                    ResetParty();
                    break;
                case "invite":
                    ShowPartyTestInvite();
                    break;
                case "mark":
                    PartyTestSandboxLaw.ApplyRaidTarget(
                        _partyRaidTargets, _selectionGuid, requested: 8);
                    break;
                default:
                    ApplyPartyTestRoster(lead: mode == "lead");
                    break;
            }
            return true;
        }
        if (ChatChannelLaw.TryResolveAdmin(_chatChannels, command, args, out var channelAdmin))
        {
            if (channelAdmin.Channel.Length > 0)
            {
                switch (channelAdmin.Command)
                {
                    case ChannelAdminCommand.Password:
                        _net?.ChannelPassword(channelAdmin.Channel, channelAdmin.Value);
                        break;
                    case ChannelAdminCommand.SetOwner:
                        _net?.ChannelSetOwner(channelAdmin.Channel, channelAdmin.Value);
                        break;
                    case ChannelAdminCommand.Owner:
                        _net?.ChannelOwner(channelAdmin.Channel);
                        break;
                    case ChannelAdminCommand.Moderator:
                        _net?.ChannelModerator(channelAdmin.Channel, channelAdmin.Value);
                        break;
                    case ChannelAdminCommand.Unmoderator:
                        _net?.ChannelUnmoderator(channelAdmin.Channel, channelAdmin.Value);
                        break;
                    case ChannelAdminCommand.Mute:
                        _net?.ChannelMute(channelAdmin.Channel, channelAdmin.Value);
                        break;
                    case ChannelAdminCommand.Unmute:
                        _net?.ChannelUnmute(channelAdmin.Channel, channelAdmin.Value);
                        break;
                    case ChannelAdminCommand.Invite:
                        _net?.ChannelInvite(channelAdmin.Channel, channelAdmin.Value);
                        break;
                    case ChannelAdminCommand.Kick:
                        _net?.ChannelKick(channelAdmin.Channel, channelAdmin.Value);
                        break;
                    case ChannelAdminCommand.Ban:
                        _net?.ChannelBan(channelAdmin.Channel, channelAdmin.Value);
                        break;
                    case ChannelAdminCommand.Unban:
                        _net?.ChannelUnban(channelAdmin.Channel, channelAdmin.Value);
                        break;
                    case ChannelAdminCommand.Announcements:
                        _net?.ChannelAnnouncements(channelAdmin.Channel);
                        break;
                    case ChannelAdminCommand.Moderate:
                        _net?.ChannelModerate(channelAdmin.Channel);
                        break;
                }
            }
            return true;
        }
        if (args.Length == 0 && StandStateUiLaw.ResolveCommand(command) is { } standState)
        {
            // SetStandState is client-volunteered in 1.12: commit locally now so the body reacts
            // on this frame, then send the u32 for the server to relay to nearby observers.
            TrySetLocalStandState(standState);
            return true;
        }
        if (args.Length == 0 && PartyLeadCommandLaw.IsClaimLead(command))
        {
            RequestPartyLeadClaim();
            return true;
        }
        if (GroupSlashCommandLaw.Resolve(command) is { } groupCommand)
        {
            string? name = ResolveGroupSlashTarget(args);
            if (name is null) return true; // vanilla's bare/no-player-target silent no-op
            switch (groupCommand)
            {
                case GroupSlashCommand.Invite:
                    _net?.GroupInvite(name);
                    break;
                case GroupSlashCommand.Uninvite:
                {
                    PartyMember? member = _partyMembers.FirstOrDefault(member =>
                        member.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (member is null || !TryPartyTestUninvite(member.Guid))
                        _net?.GroupUninvite(name);
                    break;
                }
                case GroupSlashCommand.Promote:
                {
                    PartyMember? member = _partyMembers.FirstOrDefault(member =>
                        member.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (member is null) AddChatMessage($"{name} is not in your party.");
                    else if (!TryPartyTestPromote(member.Guid))
                        _net?.GroupSetLeader(member.Guid);
                    break;
                }
            }
            return true;
        }
        switch (command)
        {
            case "/cast" or "/spell":
            {
                string name = args.Trim();
                if (name.Length == 0) return true;
                SpellInfo? spell = _spellCatalog?.FindKnownByName(name, _actions.KnownSpells);
                if (spell is { } found) TryCast(found.Id);
                else AddChatMessage($"Unknown spell: {name}");
                return true;
            }
            case "/use":
            {
                string name = args.Trim();
                if (name.Length == 0) return true;
                ItemTemplate? item = _items?.FindByName(name);
                if (item is not null) UseItemAction(item.Entry);
                else AddChatMessage($"Unknown item: {name}");
                return true;
            }
            case "/startattack":
                if (_selectionGuid != 0) CommitSelection(_selectionGuid, beginAttack: true);
                return true;
            case "/stopattack":
                StopAttack("slash-command");
                return true;
            case "/follow" or "/fol" or "/f":
            {
                string query = args.Trim();
                if (query.Length > 0)
                {
                    if (TryResolveAutoFollowByName(query, out ulong guid, out string name))
                        StartAutoFollow(guid, name);
                }
                else if (_selectionGuid != 0 &&
                    _entities.TryGet(_selectionGuid, out WorldEntity target))
                {
                    StartAutoFollow(_selectionGuid, AutoFollowTargetName(_selectionGuid, target));
                }
                return true;
            }
            case "/join" or "/channel" or "/chan":
            {
                string[] words = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 0)
                    _net?.JoinChannel(words[0], words.Length > 1 ? words[1].Trim() : "");
                return true;
            }
            case "/leave" or "/chatleave" or "/chatexit":
            {
                string selector = args.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";
                string name = ResolveChannelSelector(selector);
                if (name.Length > 0) _net?.LeaveChannel(name);
                return true;
            }
            case "/chatlist" or "/chatwho" or "/chatinfo":
            {
                string selector = args.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";
                string name = ResolveChannelSelector(selector);
                if (name.Length > 0) _net?.ChannelList(name);
                return true;
            }
            case "/pvp":
                _net?.TogglePvp();
                return true;
            case "/played":
                _net?.PlayedTime();
                return true;
            case "/ginfo":
                _net?.GuildInfo();
                return true;
            case "/random" or "/rand" or "/rnd" or "/roll":
            {
                uint[] values = args.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(word => uint.TryParse(word, out uint value) ? (uint?)value : null)
                    .Where(value => value.HasValue).Select(value => value!.Value).Take(2).ToArray();
                (uint min, uint max) = values.Length switch
                {
                    >= 2 => (values[0], values[1]),
                    1 => (1u, values[0]),
                    _ => (1u, 100u),
                };
                _net?.RandomRoll(min, max);
                return true;
            }
            default:
                return false;
        }
    }

    private bool TrySetLocalStandState(byte standState)
    {
        if (!CanAuthorControlledGameplay || ControlledGuid != LocalPlayerGuid ||
            !StandStateUiLaw.IsClientState(standState) ||
            _net?.StandStateChange(standState) != true ||
            !_entities.TryGet(LocalPlayerGuid, out WorldEntity self)) return false;
        self.Fields.SetUnitStandState(standState);
        return true;
    }

    private string ResolveChannelSelector(string selector)
    {
        if (int.TryParse(selector, out int number))
            return ChatChannelLaw.NameOf(_chatChannels, number) ?? "";
        return selector;
    }

    private string? ResolveGroupSlashTarget(string args)
    {
        string name = args.Trim();
        if (name.Length > 0) return name;
        if (_selectionGuid == 0 || !_entities.TryGet(_selectionGuid, out WorldEntity target) ||
            !target.IsPlayer) return null;
        return _playerNames.TryGetValue(_selectionGuid, out string? known) && known.Length > 0
            ? known : null;
    }

    private void HandlePlayedTime(byte[] body)
    {
        ChatPackets.PlayedTime played = ChatPackets.ParsePlayedTime(body);
        (string total, string level) = ChatFrameLaw.FormatPlayedTime(played.Total, played.Level);
        AddChatMessage(total);
        AddChatMessage(level);
    }

    private void HandleRandomRoll(byte[] body)
    {
        ChatPackets.RandomRoll roll = ChatPackets.ParseRandomRoll(body);
        AddChatMessage(ChatFrameLaw.FormatRandomRoll(ResolveChatName(roll.Guid), roll.Result,
            roll.Minimum, roll.Maximum));
    }

    /// <summary>
    /// One of the 169 numbered text emotes (/wave, /dance, ...)? Sends
    /// CMSG_TEXT_EMOTE against the current target (untargeted if none) and
    /// returns true. Must run before ParseChatCommand: that function's default
    /// case doesn't know these commands and would otherwise send the literal
    /// "/wave" text as a Say. Anything with extra words after the command (e.g.
    /// "/e is thinking") is deliberately NOT matched here - that is CHAT_MSG_EMOTE,
    /// a different, still-unwired freeform-text system (see the TODO on
    /// DrawChatMenuButton), not one of these canned ones.
    /// </summary>
    private bool TrySubmitTextEmote(string raw)
    {
        if (!raw.StartsWith('/') || raw.Contains(' ')) return false;
        string lower = raw.ToLowerInvariant();
        if (EmoteCommandLaw.Resolve(lower) is not { } id) return false;
        if (!CanAuthorControlledGameplay)
        {
            ShowUiError("Cannot emote while in Free View.");
            return true;
        }

        if (MovementGatedCommands.Contains(lower) && IsMoving())
        {
            // Real client rejects these outright client-side, no round trip -
            // Cam confirmed from 1.12 play: /sit /kneel /stand /dance /sleep
            // /lie all refuse while moving with this exact message, unlike
            // ordinary emotes and instant casts, which DO play while moving
            // (masked to the upper body - see CharacterRenderer.ChooseClip's
            // "KNOWN WRONG, not yet fixed" comment for that still-unbuilt half).
            // Nothing is sent: not the chat-text emote, not a stand-state change.
            AddChatMessage("You cannot do this while moving.", ChatFrameLaw.MsgType.System);
            return true;
        }

        _net?.SendTextEmote((uint)id, _selectionGuid);
        if (StandStateCommands.TryGetValue(lower, out UnitStandState requested))
            SubmitStandStateChange(requested);
        return true;
    }

    /// <summary>Whether the local player currently has movement keys held - the
    /// same intent signal BuildUnitState feeds the renderer, not a
    /// server-confirmed velocity. Real client validation is instant and
    /// client-only, so this matches it rather than waiting on a spline.</summary>
    private bool IsMoving() => MathF.Abs(_moveForward) > 0.01f || MathF.Abs(_moveStrafe) > 0.01f;

    /// <summary>The stand-state/state-emote commands the real client refuses
    /// outright while moving ("You cannot do this while moving."), rather than
    /// letting them fly and masking them to the upper body like ordinary
    /// emotes. Laydown's aliases are included even though this client doesn't
    /// render its pose yet (see StandStateCommands) - the refusal is real
    /// regardless of whether the pose itself is built.</summary>
    private static readonly HashSet<string> MovementGatedCommands = new()
    {
        "/sit", "/kneel", "/stand", "/dance", "/sleep",
        "/lay", "/laydown", "/lie", "/liedown",
    };

    /// <summary>The text-emote commands that are ALSO a real pose change, not
    /// just a chat line. See SubmitStandStateChange's doc comment for why the
    /// text emote above can never carry this on its own.</summary>
    private static readonly Dictionary<string, UnitStandState> StandStateCommands = new()
    {
        ["/sit"] = UnitStandState.Sit,
        ["/kneel"] = UnitStandState.Kneel,
        ["/sleep"] = UnitStandState.Sleep,
        ["/stand"] = UnitStandState.Stand,
    };

    /// <summary>
    /// /sit and /kneel toggle like the real client: asking for the state you
    /// are already in stands you back up rather than restating it. /sleep and
    /// /stand do not need that check - re-sleeping is a no-op server-side, and
    /// /stand only ever means Stand regardless of current state.
    /// </summary>
    private void SubmitStandStateChange(UnitStandState requested)
    {
        UnitStandState current = _entities.TryGet(LocalPlayerGuid, out WorldEntity self)
            ? (UnitStandState)self.Fields.StandState : UnitStandState.Stand;
        bool togglesOff = requested is UnitStandState.Sit or UnitStandState.Kneel && current == requested;
        _net?.SendStandStateChange(togglesOff ? UnitStandState.Stand : requested);
    }

    /// <summary>
    /// Split a typed line into (type, target, message). A leading /slash picks the
    /// channel (/s /y /g /o /p /raid, and /w Name for a whisper); anything else is
    /// Say. Unknown slashes fall through to Say verbatim.
    /// </summary>
    private (ChatFrameLaw.MsgType, string?, string) ParseChatCommand(string raw)
    {
        if (!raw.StartsWith('/')) return (ChatFrameLaw.MsgType.Say, null, raw);
        int sp = raw.IndexOf(' ');
        string cmd = (sp < 0 ? raw : raw[..sp]).ToLowerInvariant();
        string rest = sp < 0 ? "" : raw[(sp + 1)..].TrimStart();
        if (ChatChannelLaw.TryResolveSend(_chatChannels, cmd, rest,
                out string channel, out string channelMessage))
            return (ChatFrameLaw.MsgType.Channel, channel, channelMessage);
        switch (cmd)
        {
            case "/s" or "/say": return (ChatFrameLaw.MsgType.Say, null, rest);
            case "/y" or "/yell": return (ChatFrameLaw.MsgType.Yell, null, rest);
            case "/g" or "/guild": return (ChatFrameLaw.MsgType.Guild, null, rest);
            case "/o" or "/officer": return (ChatFrameLaw.MsgType.Officer, null, rest);
            case "/p" or "/party": return (ChatFrameLaw.MsgType.Party, null, rest);
            case "/raid" or "/ra": return (ChatFrameLaw.MsgType.Raid, null, rest);
            case "/e" or "/em" or "/emote":
                return (ChatFrameLaw.MsgType.Emote, null, rest);
            case "/bg" or "/battleground":
                return (ChatFrameLaw.MsgType.Battleground, null, rest);
            case "/w" or "/whisper" or "/tell" or "/t":
            {
                int sp2 = rest.IndexOf(' ');
                string target = sp2 < 0 ? rest : rest[..sp2];
                string msg = sp2 < 0 ? "" : rest[(sp2 + 1)..].TrimStart();
                return (ChatFrameLaw.MsgType.Whisper, target.Length > 0 ? target : null, msg);
            }
            default: return (ChatFrameLaw.MsgType.Say, null, raw);
        }
    }

    /// <summary>The send type implied by a leading /slash, for the header display.</summary>
    private ChatFrameLaw.MsgType PeekChatType(string input)
    {
        string raw = input.TrimStart();
        if (!raw.StartsWith('/')) return ChatFrameLaw.MsgType.Say;
        int sp = raw.IndexOf(' ');
        string cmd = (sp < 0 ? raw : raw[..sp]).ToLowerInvariant();
        string rest = sp < 0 ? "" : raw[(sp + 1)..].TrimStart();
        if (ChatChannelLaw.TryResolveSend(_chatChannels, cmd, rest, out _, out _))
            return ChatFrameLaw.MsgType.Channel;
        return cmd switch
        {
            "/y" or "/yell" => ChatFrameLaw.MsgType.Yell,
            "/g" or "/guild" => ChatFrameLaw.MsgType.Guild,
            "/o" or "/officer" => ChatFrameLaw.MsgType.Officer,
            "/p" or "/party" => ChatFrameLaw.MsgType.Party,
            "/raid" or "/ra" => ChatFrameLaw.MsgType.Raid,
            "/bg" or "/battleground" => ChatFrameLaw.MsgType.Battleground,
            "/e" or "/em" or "/emote" => ChatFrameLaw.MsgType.Emote,
            "/w" or "/whisper" or "/tell" or "/t" => ChatFrameLaw.MsgType.Whisper,
            _ => ChatFrameLaw.MsgType.Say,
        };
    }

    private void DrawChatTexture(ImDrawListPtr dl, Vector2 logicalMin, Vector2 logicalSize,
        string texture, Vector2 uv0, Vector2 uv1, uint tint)
    {
        uint handle = _gameplayArt?.Handle(texture) ?? 0;
        if (handle == 0) return;
        float s = GameplayUiScale();
        Vector2 min = logicalMin * s;
        dl.AddImage((nint)handle, min, min + logicalSize * s, uv0, uv1, tint);
    }

    // ABGR white/black at a given 0..1 alpha (WithAlpha lives in Program.Nameplates.cs).
    private static uint WhiteAlpha(float a) => WithAlpha(0xFFFFFFFFu, a);
    private static uint BlackAlpha(float a) => WithAlpha(0xFF000000u, a);
}
