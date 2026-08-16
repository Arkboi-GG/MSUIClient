using MSUIClient.Net;

internal static class RtsWireClinicalChecks
{
    public static int Run(string root)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidDataException(message);
        }

        void ExpectInvalid(Action action, string message)
        {
            checks++;
            bool rejected = false;
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException)
            {
                rejected = true;
            }
            if (!rejected) throw new InvalidDataException(message);
        }

        const ulong allianceHero = 0x0000_0000_0102_0304ul;
        const ulong hordeHero = 0x0000_0000_1122_3344ul;

        byte[] BuildZoneIntel(byte zoneStride = 10, byte controller = 0x81,
            uint firstZone = 12, uint secondZone = 14, byte unitFlags = 3,
            float unitX = 1f, bool trailing = false)
        {
            var w = new PacketWriter();
            w.WriteU16(2); w.WriteU8(zoneStride);
            void Zone(uint id, ushort bots, ushort players, byte control, byte tail)
            {
                w.WriteU32(id); w.WriteU16(bots); w.WriteU16(players);
                if (zoneStride >= RtsWire.ZoneIntelTerritoryRowBytes) w.WriteU8(control);
                int known = zoneStride >= RtsWire.ZoneIntelTerritoryRowBytes
                    ? RtsWire.ZoneIntelTerritoryRowBytes : RtsWire.ZoneIntelLegacyRowBytes;
                for (int i = known; i < zoneStride; i++) w.WriteU8(tail);
            }
            Zone(firstZone, 4, 2, controller, 0xA1);
            Zone(secondZone, 0, 0, 2, 0xA2);
            w.WriteU8(1); w.WriteU8(30);
            w.WriteU64(allianceHero); w.WriteU32(0); w.WriteU32(firstZone);
            w.WriteF32(unitX); w.WriteF32(2); w.WriteF32(3); w.WriteU8(unitFlags); w.WriteU8(0xB1);
            if (trailing) w.WriteU8(0xFF);
            return w.ToArray();
        }

        RtsZoneIntelSnapshot intel = RtsWire.ParseZoneIntel(BuildZoneIntel());
        Check(intel.Zones.Length == 2 && intel.Zones[0].ZoneId == 12 &&
              intel.Zones[0].Owner == RtsTerritoryOwner.Alliance && intel.Zones[0].Contested &&
              intel.Zones[1].Bots == 0 && intel.Zones[1].Players == 0 &&
              intel.Zones[1].Owner == RtsTerritoryOwner.Horde,
            "typed territory zone/future-stride parsing drift");
        Check(intel.Units.Length == 1 && intel.Units[0].Guid == allianceHero &&
              intel.Units[0].Alive && intel.Units[0].IsBot && intel.Units[0].Position.X == 1,
            "typed zone-intel unit/future-stride parsing drift");

        var legacy = new PacketWriter();
        legacy.WriteU16(1); legacy.WriteU8(8); legacy.WriteU32(12);
        legacy.WriteU16(1); legacy.WriteU16(2); legacy.WriteU8(0); legacy.WriteU8(29);
        RtsZoneIntelSnapshot legacyIntel = RtsWire.ParseZoneIntel(legacy.ToArray());
        Check(legacyIntel.Zones[0].Owner == RtsTerritoryOwner.Neutral &&
              !legacyIntel.Zones[0].Contested,
            "legacy stride-8 zone did not decode as neutral");
        ExpectInvalid(() => RtsWire.ParseZoneIntel(BuildZoneIntel(zoneStride: 7)),
            "undersized zone-intel stride was accepted");
        ExpectInvalid(() => RtsWire.ParseZoneIntel(BuildZoneIntel(controller: 0x83)),
            "invalid territory controller was accepted");
        ExpectInvalid(() => RtsWire.ParseZoneIntel(BuildZoneIntel(secondZone: 12)),
            "duplicate territory zone was accepted");
        ExpectInvalid(() => RtsWire.ParseZoneIntel(BuildZoneIntel(unitFlags: 4)),
            "unknown zone-intel unit flag was accepted");
        ExpectInvalid(() => RtsWire.ParseZoneIntel(BuildZoneIntel(unitX: float.NaN)),
            "non-finite zone-intel unit position was accepted");
        ExpectInvalid(() => RtsWire.ParseZoneIntel(BuildZoneIntel(trailing: true)),
            "trailing zone-intel data was accepted");

        Check(RtsWire.TerritoryCaptureWorldStateId == 0x53550001,
            "territory capture world-state id drift");
        Check(RtsWire.TryDecodeTerritoryCaptureState(0x002A5DE9,
                  out RtsTerritoryCaptureState capture) &&
              capture.Phase == RtsTerritoryCapturePhase.Contested &&
              capture.Owner == RtsTerritoryOwner.Alliance &&
              capture.Attacker == RtsTerritoryOwner.Horde &&
              capture.ProgressPermille == 375 && capture.RemainingSeconds == 42,
            "territory packed golden vector drift");
        Check(RtsWire.TryDecodeTerritoryCaptureState(0, out RtsTerritoryCaptureState hidden) &&
              !hidden.Visible, "zero territory state did not decode as hidden");
        Check(!RtsWire.TryDecodeTerritoryCaptureState(1, out _),
            "nonzero hidden territory state was accepted");
        uint excessiveProgress = (42u << 16) | (1001u << 6) | (2u << 4) | (2u << 2) | 1u;
        Check(!RtsWire.TryDecodeTerritoryCaptureState(excessiveProgress, out _),
            "territory progress above 1000 was accepted");
        uint selfAttack = (20u << 16) | (100u << 6) | (2u << 4) | (1u << 2) | 1u;
        Check(!RtsWire.TryDecodeTerritoryCaptureState(selfAttack, out _),
            "incumbent self-attack territory state was accepted");

        byte[] BuildState(bool trailing = false, byte mode = 1,
            ulong firstHeroGuid = allianceHero, byte firstHeroTeam = 0,
            byte firstHeroLevel = 2, byte firstHeroDead = 0)
        {
            var w = new PacketWriter();
            w.WriteU8(mode);
            w.WriteU8(3);
            w.WriteU8(27); // 26 known + one future byte
            for (int team = 0; team < 2; team++)
            {
                w.WriteU64((ulong)(1000 + team));
                w.WriteI32(10 + team);
                w.WriteI32(20 + team);
                w.WriteI32(30 + team);
                w.WriteU16((ushort)(2 + team));
                w.WriteU16((ushort)(1 + team));
                w.WriteU16(4);
                w.WriteU8((byte)(0xA0 + team));
            }
            w.WriteU8(2);
            w.WriteU8(13); // 12 known + one future byte
            w.WriteU64(firstHeroGuid); w.WriteU8(firstHeroTeam); w.WriteU8(firstHeroLevel);
            w.WriteU8(firstHeroDead); w.WriteU8(0); w.WriteU8(0xB0);
            w.WriteU64(hordeHero); w.WriteU8(1); w.WriteU8(5); w.WriteU8(1); w.WriteU8(0); w.WriteU8(0xB1);
            w.WriteU8(1);
            w.WriteU8(8); // 7 known + one future byte
            w.WriteU32(36); w.WriteU8(1); w.WriteU8(2); w.WriteU8(0); w.WriteU8(0xC0);
            if (trailing) w.WriteU8(0xFF);
            return w.ToArray();
        }

        RtsStateSnapshot state = RtsWire.ParseState(BuildState());
        Check(state.Mode == 1 && state.Modules == 3, "RTS header drift");
        Check(state.Factions.Length == 2 && state.Factions[0].HonorPool == 1000 &&
              state.Factions[1].Ore == 11 && state.Factions[0].HeroSlotCap == 4,
            "RTS faction block/future-stride drift");
        Check(state.Heroes.Length == 2 && state.Heroes[0].Guid == allianceHero &&
              state.Heroes[0].Team == 0 && state.Heroes[0].HeroLevel == 2 &&
              !state.Heroes[0].Dead && state.Heroes[1].Guid == hordeHero && state.Heroes[1].Dead,
            "RTS hero block/full-GUID/future-stride drift");
        Check(state.Dungeons.Length == 1 && state.Dungeons[0].MapId == 36 &&
              state.Dungeons[0].Controller == 1 && state.Dungeons[0].LiveRunFlags == 2,
            "RTS dungeon block/future-stride drift");

        ExpectInvalid(() => RtsWire.ParseState([1, 3, 25]),
            "undersized RTS faction stride was accepted");

        var shortHero = new PacketWriter();
        shortHero.WriteU8(1); shortHero.WriteU8(3); shortHero.WriteU8(26);
        for (int team = 0; team < 2; team++)
        {
            shortHero.WriteU64(0); shortHero.WriteI32(0); shortHero.WriteI32(0); shortHero.WriteI32(0);
            shortHero.WriteU16(0); shortHero.WriteU16(0); shortHero.WriteU16(0);
        }
        shortHero.WriteU8(0); shortHero.WriteU8(11);
        ExpectInvalid(() => RtsWire.ParseState(shortHero.ToArray()),
            "undersized RTS hero stride was accepted");

        var shortDungeon = new PacketWriter();
        shortDungeon.WriteBytes(shortHero.AsSpan()[..^2]);
        shortDungeon.WriteU8(0); shortDungeon.WriteU8(12);
        shortDungeon.WriteU8(0); shortDungeon.WriteU8(6);
        ExpectInvalid(() => RtsWire.ParseState(shortDungeon.ToArray()),
            "undersized RTS dungeon stride was accepted");
        ExpectInvalid(() => RtsWire.ParseState(BuildState(trailing: true)),
            "trailing RTS state data was accepted");
        ExpectInvalid(() => RtsWire.ParseState(BuildState(mode: 2)),
            "invalid RTS mode was accepted");
        ExpectInvalid(() => RtsWire.ParseState(BuildState(firstHeroGuid: 0)),
            "zero RTS hero GUID was accepted");
        ExpectInvalid(() => RtsWire.ParseState(BuildState(firstHeroTeam: 2)),
            "invalid RTS hero team was accepted");
        ExpectInvalid(() => RtsWire.ParseState(BuildState(firstHeroLevel: 0)),
            "zero RTS hero level was accepted");
        ExpectInvalid(() => RtsWire.ParseState(BuildState(firstHeroLevel: 6)),
            "out-of-range RTS hero level was accepted");
        ExpectInvalid(() => RtsWire.ParseState(BuildState(firstHeroDead: 2)),
            "invalid RTS hero dead byte was accepted");

        byte[] truncated = BuildState()[..^1];
        checks++;
        try
        {
            RtsWire.ParseState(truncated);
            throw new InvalidDataException("truncated RTS state was accepted");
        }
        catch (EndOfStreamException) { }

        byte[] action = RtsWire.BuildActionBody(2, 0x0102_0304_0506_0708ul);
        Check(action.SequenceEqual(Convert.FromHexString("020807060504030201")),
            "RTS action golden body drift");
        ExpectInvalid(() => RtsWire.BuildActionBody(0, allianceHero),
            "invalid RTS action code was accepted");
        ExpectInvalid(() => RtsWire.BuildActionBody(1, 0),
            "zero RTS action subject was accepted");

        var resultWriter = new PacketWriter();
        resultWriter.WriteU8(3); resultWriter.WriteU8(2); resultWriter.WriteU64(allianceHero);
        resultWriter.WriteU64(999);
        RtsActionResultWire result = RtsWire.ParseActionResult(resultWriter.ToArray());
        Check(result.Action == 3 && result.Result == 2 && result.SubjectGuid == allianceHero &&
              result.PoolAfter == 999, "RTS action-result body drift");
        resultWriter.WriteU8(0);
        ExpectInvalid(() => RtsWire.ParseActionResult(resultWriter.ToArray()),
            "trailing RTS action-result data was accepted");

        byte[] forceRequest = RtsWire.BuildForceRosterRequestBody(
            0x11223344, 12, 0xAABBCCDD, 200);
        Check(forceRequest.SequenceEqual(Convert.FromHexString(
                "00443322110C000000DDCCBBAAC8")),
            "RTS force-roster request golden body drift");
        Check(RtsWire.BuildForceRosterRequestBody(1, 0, 0, 0).Length ==
              RtsWire.ForceRequestBytes,
            "RTS force-roster default-limit request drift");
        ExpectInvalid(() => RtsWire.BuildForceRosterRequestBody(0, 12, 0),
            "zero RTS force-roster request id was accepted");
        ExpectInvalid(() => RtsWire.BuildForceRosterRequestBody(1, 12, 0, 201),
            "oversized RTS force-roster request page was accepted");

        const uint forceRequestId = 77;
        const uint forceZone = 12;
        const ulong firstBot = 0x0000_0000_0102_0304ul;
        const ulong secondBot = 0x0000_0000_1122_3344ul;
        byte[] BuildForcePage(bool trailing = false, byte stride = 33,
            ulong row1 = firstBot, ulong row2 = secondBot, uint rowZone = forceZone,
            uint? next = null, float secondX = -4f)
        {
            var w = new PacketWriter();
            w.WriteU32(forceRequestId);
            w.WriteU32(forceZone);
            w.WriteU32(next ?? unchecked((uint)row2));
            w.WriteU16(2);
            w.WriteU8(2);
            w.WriteU8(stride);
            void Row(ulong guid, float x, byte race, byte cls, byte level, byte flags, byte tail)
            {
                w.WriteU64(guid); w.WriteU32(0); w.WriteU32(rowZone);
                w.WriteF32(x); w.WriteF32(2); w.WriteF32(3);
                w.WriteU8(race); w.WriteU8(cls); w.WriteU8(level); w.WriteU8(flags);
                if (stride > RtsWire.ForceRowBytes)
                    for (int i = RtsWire.ForceRowBytes; i < stride; i++) w.WriteU8(tail);
            }
            Row(row1, 1, 1, 8, 22, 0x1D, 0xA1);
            Row(row2, secondX, 3, 1, 31, 0x62, 0xA2);
            if (trailing) w.WriteU8(0xFF);
            return w.ToArray();
        }

        RtsForceRosterPage forcePage = RtsWire.ParseForceRoster(BuildForcePage());
        Check(forcePage.RequestId == forceRequestId && forcePage.ZoneId == forceZone &&
              forcePage.NextGuidLow == unchecked((uint)secondBot) && forcePage.Total == 2 &&
              forcePage.Units.Length == 2,
            "RTS force-roster header/pagination drift");
        Check(forcePage.Units[0].Guid == firstBot && forcePage.Units[0].MapId == 0 &&
              forcePage.Units[0].ZoneId == forceZone && forcePage.Units[0].Position.X == 1 &&
              forcePage.Units[0].Race == 1 && forcePage.Units[0].Class == 8 &&
              forcePage.Units[0].Level == 22 && forcePage.Units[0].Alive &&
              forcePage.Units[0].ControlEligibleNow && forcePage.Units[0].DeclaredHero,
            "RTS force-roster row/full-GUID/future-stride drift");
        Check(forcePage.Units[1].Busy && forcePage.Units[1].HeroDead &&
              forcePage.Units[1].InstanceableMap && !forcePage.Units[1].Alive,
            "RTS force-roster flag drift");
        ExpectInvalid(() => RtsWire.ParseForceRoster(BuildForcePage(stride: 31)),
            "undersized RTS force-row stride was accepted");
        ExpectInvalid(() => RtsWire.ParseForceRoster(BuildForcePage(trailing: true)),
            "trailing RTS force-roster data was accepted");
        ExpectInvalid(() => RtsWire.ParseForceRoster(
                BuildForcePage(row1: secondBot, row2: firstBot, next: unchecked((uint)firstBot))),
            "reversed RTS force-roster GUID order was accepted");
        ExpectInvalid(() => RtsWire.ParseForceRoster(BuildForcePage(rowZone: 14)),
            "wrong-zone RTS force-roster row was accepted");
        ExpectInvalid(() => RtsWire.ParseForceRoster(BuildForcePage(next: 123)),
            "RTS force-roster cursor not matching its last row was accepted");
        ExpectInvalid(() => RtsWire.ParseForceRoster(BuildForcePage(secondX: float.NaN)),
            "non-finite RTS force-roster position was accepted");
        var tooManyForces = new PacketWriter();
        tooManyForces.WriteU32(1); tooManyForces.WriteU32(0); tooManyForces.WriteU32(0);
        tooManyForces.WriteU16(201); tooManyForces.WriteU8(201);
        tooManyForces.WriteU8(RtsWire.ForceRowBytes);
        ExpectInvalid(() => RtsWire.ParseForceRoster(tooManyForces.ToArray()),
            "RTS force-roster count above 200 was accepted");
        checks++;
        try
        {
            RtsWire.ParseForceRoster(BuildForcePage()[..^2]);
            throw new InvalidDataException("truncated RTS force-roster page was accepted");
        }
        catch (EndOfStreamException) { }

        Check((ushort)Op.CMSG_SUI_RTS_STATE == 0x0346 &&
              (ushort)Op.SMSG_SUI_RTS_STATE == 0x0347 &&
              (ushort)Op.CMSG_SUI_RTS_ACTION == 0x0348 &&
              (ushort)Op.SMSG_SUI_RTS_ACTION_RESULT == 0x0349 &&
              (ushort)Op.CMSG_SUI_FORCE_ROSTER == 0x034A &&
              (ushort)Op.SMSG_SUI_FORCE_ROSTER == 0x034B,
            "RTS opcode allocation drift");

        string session = File.ReadAllText(Path.Combine(root, "MSUIClient", "Net", "WorldSession.cs"));
        Check(session.Contains("BuildSuiRtsActionBody(action, subjectGuid)", StringComparison.Ordinal) &&
              session.Contains("RtsWire.BuildActionBody(action, subjectGuid)", StringComparison.Ordinal),
            "WorldSession bypassed the tested RTS action body law");
        Check(session.Contains("BuildSuiForceRosterBody(requestId, zoneId, afterGuidLow, limit)",
                  StringComparison.Ordinal) &&
              session.Contains("RtsWire.BuildForceRosterRequestBody(requestId, zoneId, afterGuidLow, limit)",
                  StringComparison.Ordinal),
            "WorldSession bypassed the tested RTS force-roster request law");
        return checks;
    }
}
