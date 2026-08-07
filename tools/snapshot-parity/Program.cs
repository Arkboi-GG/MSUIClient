using SnapshotParity;

if (args.Length == 0)
{
    Usage();
    return 2;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "capture" => Capture(args[1..]),
        "index" => Index(args[1..]),
        "compare" => Compare(args[1..]),
        "validate" => Validate(args[1..]),
        "report" => Report(args[1..]),
        "queue" => Queue(args[1..]),
        "packet" => Packet(args[1..]),
        "workspace" => Workspace(args[1..]),
        "workspace-validate" => WorkspaceValidate(args[1..]),
        "workspace-migrate" => WorkspaceMigrate(args[1..]),
        "claim-update" => ClaimUpdate(args[1..]),
        "claim-targets" => ClaimTargets(args[1..]),
        "trace-add" => TraceAdd(args[1..]),
        "claim-add" => ClaimAdd(args[1..]),
        "migrate" => Migrate(args[1..]),
        "self-test" => SelfTest(),
        _ => throw new ArgumentException($"unknown command {args[0]}")
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[snapshot-parity] FAIL {ex.Message}");
    return 1;
}

static int Capture(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    string kind = Need(o, "kind"), root = Need(o, "root"), manifestPath = Need(o, "manifest");
    SnapshotManifest manifest = SnapshotCapture.Capture(kind, root, o.GetValueOrDefault("bundle"));
    JsonStore.Write(manifestPath, manifest);
    Console.WriteLine($"[snapshot-parity] captured {manifest.Id}: {manifest.Files.Count:N0} files, {manifest.AggregateSha256}");
    return 0;
}

static int Index(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotManifest manifest = JsonStore.Read<SnapshotManifest>(Need(o, "manifest"));
    FactIndex index = FactIndexer.Build(manifest);
    JsonStore.Write(Need(o, "facts"), index);
    Console.WriteLine($"[snapshot-parity] indexed {index.Facts.Count:N0} facts from {manifest.Id}");
    return 0;
}

static int Compare(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotManifest referenceManifest = JsonStore.Read<SnapshotManifest>(Need(o, "reference-manifest"));
    FactIndex referenceFacts = JsonStore.Read<FactIndex>(Need(o, "reference-facts"));
    SnapshotManifest targetManifest = JsonStore.Read<SnapshotManifest>(Need(o, "target-manifest"));
    FactIndex targetFacts = JsonStore.Read<FactIndex>(Need(o, "target-facts"));
    SnapshotPair pair = ComparisonEngine.Compare(referenceManifest, referenceFacts, targetManifest, targetFacts);
    JsonStore.Write(Need(o, "pair"), pair);
    Console.WriteLine($"[snapshot-parity] compared {pair.Id}: {pair.ReferenceFacts.Count:N0} reference facts, " +
        $"{pair.TargetFacts.Count:N0} target facts, {pair.Candidates.Count:N0} mechanical candidates");
    return 0;
}

static int Validate(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotPair pair = JsonStore.Read<SnapshotPair>(Need(o, "pair"));
    List<BehaviorTrace> traces = JsonStore.ReadLines<BehaviorTrace>(Need(o, "traces"));
    List<ComparisonClaim> claims = JsonStore.ReadLines<ComparisonClaim>(Need(o, "claims"));
    ValidationResult result = ComparisonEngine.Validate(pair, traces, claims);
    foreach (string warning in result.Warnings) Console.Error.WriteLine($"[snapshot-parity] WARN {warning}");
    foreach (string error in result.Errors) Console.Error.WriteLine($"[snapshot-parity] ERROR {error}");
    Console.WriteLine($"[snapshot-parity] coverage {result.ClaimedReferenceFacts:N0}/{result.RequiredReferenceFacts:N0}; " +
        $"{result.Errors.Count:N0} error(s), {result.Warnings.Count:N0} warning(s)");
    return result.Errors.Count == 0 ? 0 : 3;
}

static int Report(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotPair pair = JsonStore.Read<SnapshotPair>(Need(o, "pair"));
    List<BehaviorTrace> traces = JsonStore.ReadLines<BehaviorTrace>(Need(o, "traces"));
    List<ComparisonClaim> claims = JsonStore.ReadLines<ComparisonClaim>(Need(o, "claims"));
    string output = Need(o, "out");
    ReportWriter.Write(output, pair, traces, claims);
    Console.WriteLine($"[snapshot-parity] wrote {Path.GetFullPath(output)}");
    return 0;
}

static int Queue(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotPair pair = JsonStore.Read<SnapshotPair>(Need(o, "pair"));
    List<BehaviorTrace> traces = JsonStore.ReadLines<BehaviorTrace>(Need(o, "traces"));
    List<ComparisonClaim> claims = JsonStore.ReadLines<ComparisonClaim>(Need(o, "claims"));
    ReviewQueue queue = ReviewQueueWriter.Build(pair, traces, claims);
    JsonStore.Write(Need(o, "out"), queue);
    Console.WriteLine($"[snapshot-parity] queued {queue.UnreviewedReferenceFacts:N0} facts in " +
        $"{queue.Packets.Count:N0} bounded review packets for {queue.PairId}");
    return 0;
}

static int Packet(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotPair pair = JsonStore.Read<SnapshotPair>(Need(o, "pair"));
    ReviewQueue queue = JsonStore.Read<ReviewQueue>(Need(o, "queue"));
    ReviewQueueWriter.WritePacket(Need(o, "out"), pair, queue, Need(o, "id"));
    Console.WriteLine($"[snapshot-parity] materialized review packet {Need(o, "id")}");
    return 0;
}

static int Workspace(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotPair pair = JsonStore.Read<SnapshotPair>(Need(o, "pair"));
    ReviewQueue queue = JsonStore.Read<ReviewQueue>(Need(o, "queue"));
    PacketWorkspaceWriter.Materialize(Need(o, "out"), pair, queue);
    Console.WriteLine($"[snapshot-parity] materialized {queue.Packets.Count:N0} packet folders for {pair.Id}");
    return 0;
}

static int WorkspaceValidate(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotPair pair = JsonStore.Read<SnapshotPair>(Need(o, "pair"));
    ReviewQueue queue = JsonStore.Read<ReviewQueue>(Need(o, "queue"));
    PacketWorkspaceValidation result = PacketWorkspaceWriter.Validate(Need(o, "root"), pair, queue);
    foreach (string error in result.Errors) Console.Error.WriteLine($"[snapshot-parity] ERROR {error}");
    Console.WriteLine($"[snapshot-parity] packet workspace {result.VerifiedCount:N0} verified, " +
        $"{result.BlockedCount:N0} blocked/unreviewed, {result.Errors.Count:N0} error(s), " +
        $"{result.PacketCount:N0} packet(s)");
    return result.Errors.Count == 0 ? 0 : 3;
}

static int WorkspaceMigrate(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotPair pair = JsonStore.Read<SnapshotPair>(Need(o, "new-pair"));
    ReviewQueue queue = JsonStore.Read<ReviewQueue>(Need(o, "new-queue"));
    int count = PacketWorkspaceWriter.MigrateAudits(Need(o, "old-root"), Need(o, "old-pair-id"),
        Need(o, "new-root"), pair, queue);
    Console.WriteLine($"[snapshot-parity] migrated {count:N0} reviewed packet audit(s) to {pair.Id}");
    return 0;
}

static int ClaimUpdate(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    string path = Need(o, "claims"), id = Need(o, "id");
    if (!Enum.TryParse(Need(o, "verdict"), true, out ClaimVerdict verdict) || verdict == ClaimVerdict.Unknown)
        throw new ArgumentException("--verdict is invalid");
    JsonStore.UpdateLine<ComparisonClaim>(path, c => c.Id == id, claim =>
    {
        claim.Verdict = verdict;
        if (o.TryGetValue("summary", out string? summary)) claim.Summary = summary;
        if (o.TryGetValue("verification", out string? verification))
            claim.VerificationIds = claim.VerificationIds.Concat(verification.Split('|',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.Ordinal).ToList();
        if (o.TryGetValue("decision", out string? decision)) claim.DecisionId = decision;
        claim.Reviewer = o.GetValueOrDefault("reviewer", "Codex");
        claim.ReviewedUtc = DateTimeOffset.UtcNow;
        if (claim.Verdict == ClaimVerdict.VerifiedEquivalent &&
            (claim.TargetFacts.Count == 0 || claim.VerificationIds.Count == 0))
            throw new InvalidDataException("verifiedEquivalent requires target and verification evidence");
        if (claim.Verdict == ClaimVerdict.ApprovedDeviation && string.IsNullOrWhiteSpace(claim.DecisionId))
            throw new InvalidDataException("approvedDeviation requires --decision");
    });
    Console.WriteLine($"[snapshot-parity] updated {id} to {verdict}");
    return 0;
}

static int ClaimTargets(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotPair pair = JsonStore.Read<SnapshotPair>(Need(o, "pair"));
    string path = Need(o, "claims"), id = Need(o, "id");
    string[] ids = Need(o, "target-facts").Split('|',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (ids.Length == 0) throw new ArgumentException("--target-facts requires at least one fact id");
    var target = pair.TargetFacts.ToDictionary(f => f.Id, StringComparer.Ordinal);
    var references = new List<FactReference>();
    foreach (string factId in ids.Distinct(StringComparer.Ordinal))
    {
        if (!target.TryGetValue(factId, out SourceFact? fact))
            throw new InvalidDataException($"target fact {factId} is not in {pair.Id}");
        references.Add(new() { Id = fact.Id, EvidenceSha256 = fact.EvidenceSha256 });
    }
    JsonStore.UpdateLine<ComparisonClaim>(path, c => c.Id == id, claim =>
    {
        if (claim.PairId != pair.Id)
            throw new InvalidDataException($"claim {id} belongs to {claim.PairId}, not {pair.Id}");
        claim.TargetFacts = references;
        claim.Reviewer = o.GetValueOrDefault("reviewer", "Codex");
        claim.ReviewedUtc = DateTimeOffset.UtcNow;
    });
    Console.WriteLine($"[snapshot-parity] replaced {id} target evidence with {references.Count:N0} fact(s)");
    return 0;
}

static int TraceAdd(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotPair pair = JsonStore.Read<SnapshotPair>(Need(o, "pair"));
    string path = Need(o, "traces"), id = Need(o, "id");
    if (JsonStore.ReadLines<BehaviorTrace>(path).Any(t => t.Id == id))
        throw new InvalidDataException($"trace {id} already exists");
    var reference = pair.ReferenceFacts.ToDictionary(f => f.Id, StringComparer.Ordinal);
    List<FactReference> facts = FactReferences(Need(o, "reference-facts"), reference, "reference");
    var trace = new BehaviorTrace
    {
        Id = id,
        PairId = pair.Id,
        Name = Need(o, "name"),
        Surface = Need(o, "surface"),
        Trigger = Need(o, "trigger"),
        Preconditions = Need(o, "preconditions"),
        Behavior = Need(o, "behavior"),
        NegativeBehavior = Need(o, "negative"),
        ReferenceFacts = facts,
        Reviewer = o.GetValueOrDefault("reviewer", "Codex"),
        ReviewedUtc = DateTimeOffset.UtcNow,
    };
    JsonStore.AppendLine(path, trace);
    Console.WriteLine($"[snapshot-parity] added trace {id} with {facts.Count:N0} reference fact(s)");
    return 0;
}

static int ClaimAdd(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotPair pair = JsonStore.Read<SnapshotPair>(Need(o, "pair"));
    string path = Need(o, "claims"), id = Need(o, "id");
    if (JsonStore.ReadLines<ComparisonClaim>(path).Any(c => c.Id == id))
        throw new InvalidDataException($"claim {id} already exists");
    string[] traceIds = Need(o, "trace-ids").Split('|',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var traces = JsonStore.ReadLines<BehaviorTrace>(Need(o, "traces"))
        .ToDictionary(t => t.Id, StringComparer.Ordinal);
    foreach (string traceId in traceIds)
        if (!traces.TryGetValue(traceId, out BehaviorTrace? trace) || trace.PairId != pair.Id)
            throw new InvalidDataException($"trace {traceId} is absent or stale");
    if (!Enum.TryParse(Need(o, "verdict"), true, out ClaimVerdict verdict) || verdict == ClaimVerdict.Unknown)
        throw new ArgumentException("--verdict is invalid");
    var target = pair.TargetFacts.ToDictionary(f => f.Id, StringComparer.Ordinal);
    List<FactReference> targetFacts = o.TryGetValue("target-facts", out string? targetIds)
        ? FactReferences(targetIds, target, "target") : [];
    var claim = new ComparisonClaim
    {
        Id = id,
        PairId = pair.Id,
        TraceIds = traceIds.Distinct(StringComparer.Ordinal).ToList(),
        TargetFacts = targetFacts,
        Verdict = verdict,
        Summary = Need(o, "summary"),
        Behavior = Need(o, "behavior"),
        NegativeBehavior = Need(o, "negative"),
        VerificationIds = o.TryGetValue("verification", out string? verification)
            ? verification.Split('|', StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList() : [],
        DecisionId = o.GetValueOrDefault("decision"),
        Reviewer = o.GetValueOrDefault("reviewer", "Codex"),
        ReviewedUtc = DateTimeOffset.UtcNow,
    };
    if (claim.Verdict == ClaimVerdict.VerifiedEquivalent &&
        (claim.TargetFacts.Count == 0 || claim.VerificationIds.Count == 0))
        throw new InvalidDataException("verifiedEquivalent requires target and verification evidence");
    if (claim.Verdict == ClaimVerdict.ApprovedDeviation && string.IsNullOrWhiteSpace(claim.DecisionId))
        throw new InvalidDataException("approvedDeviation requires --decision");
    JsonStore.AppendLine(path, claim);
    Console.WriteLine($"[snapshot-parity] added {verdict} claim {id}");
    return 0;
}

static List<FactReference> FactReferences(string ids, IReadOnlyDictionary<string, SourceFact> facts,
    string kind)
{
    var references = new List<FactReference>();
    foreach (string id in ids.Split('|', StringSplitOptions.RemoveEmptyEntries |
                 StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal))
    {
        if (!facts.TryGetValue(id, out SourceFact? fact))
            throw new InvalidDataException($"{kind} fact {id} is not in the pair");
        references.Add(new() { Id = fact.Id, EvidenceSha256 = fact.EvidenceSha256 });
    }
    if (references.Count == 0) throw new ArgumentException($"at least one {kind} fact is required");
    return references;
}

static int Migrate(string[] arguments)
{
    Dictionary<string, string> o = Options(arguments);
    SnapshotPair oldPair = JsonStore.Read<SnapshotPair>(Need(o, "old-pair"));
    SnapshotPair newPair = JsonStore.Read<SnapshotPair>(Need(o, "new-pair"));
    List<BehaviorTrace> traces = JsonStore.ReadLines<BehaviorTrace>(Need(o, "traces"));
    List<ComparisonClaim> claims = JsonStore.ReadLines<ComparisonClaim>(Need(o, "claims"));
    MigrationReport report = PairMigration.Migrate(oldPair, newPair, traces, claims,
        Need(o, "out-traces"), Need(o, "out-claims"));
    JsonStore.Write(Need(o, "report"), report);
    Console.WriteLine($"[snapshot-parity] migrated {report.TraceCount:N0} traces and {report.ClaimCount:N0} claims; " +
        $"retained {report.RetainedTerminalClaims.Count:N0} terminal claims, downgraded {report.DowngradedClaims.Count:N0}");
    return 0;
}

static int SelfTest()
{
    string root = Path.Combine(Path.GetTempPath(), $"snapshot-parity-{Guid.NewGuid():N}");
    string referenceRoot = Path.Combine(root, "reference"), targetRoot = Path.Combine(root, "target");
    Directory.CreateDirectory(Path.Combine(referenceRoot, "crates", "demo", "src"));
    Directory.CreateDirectory(Path.Combine(targetRoot, "Net"));
    File.WriteAllText(Path.Combine(referenceRoot, "Cargo.toml"), "[workspace]\nmembers=[\"crates/*\"]\n");
    File.WriteAllText(Path.Combine(referenceRoot, "crates", "demo", "src", "lib.rs"),
        "pub enum Opcode { SMSG_DEMO = 0x123 }\n#[test]\nfn packet_shape() { assert_eq!(1, 1); }\n");
    File.WriteAllText(Path.Combine(targetRoot, "Net", "Opcodes.cs"),
        "public enum Op { SMSG_DEMO = 0x123 }\n");
    SnapshotManifest reference = SnapshotCapture.Capture("benilla", referenceRoot, null);
    SnapshotManifest target = SnapshotCapture.Capture("msui", targetRoot, null);
    FactIndex referenceFacts = FactIndexer.Build(reference), targetFacts = FactIndexer.Build(target);
    SnapshotPair pair = ComparisonEngine.Compare(reference, referenceFacts, target, targetFacts);
    SourceFact opcode = referenceFacts.Facts.Single(f => f.Kind == "opcode" && f.Name == "SMSG_DEMO");
    if (!pair.Candidates.Any(c => c.ReferenceFactId == opcode.Id))
        throw new InvalidDataException("opcode candidate was not discovered");
    string report = Path.Combine(root, "report.md");
    ReportWriter.Write(report, pair, [], []);
    if (!File.ReadAllText(report).Contains("SMSG_DEMO", StringComparison.Ordinal))
        throw new InvalidDataException("report omitted unreviewed opcode");
    ReviewQueue queue = ReviewQueueWriter.Build(pair, [], []);
    if (queue.Packets.Count == 0 || queue.UnreviewedReferenceFacts == 0)
        throw new InvalidDataException("review queue omitted unreviewed facts");
    var trace = new BehaviorTrace
    {
        Id = "trace-self-test", PairId = pair.Id, Name = "demo wire behavior",
        Surface = "protocol", Trigger = "send demo", Preconditions = "session is active",
        Behavior = "sends opcode 0x123", NegativeBehavior = "does not send another opcode",
        ReferenceFacts = [new() { Id = opcode.Id, EvidenceSha256 = opcode.EvidenceSha256 }],
    };
    SourceFact targetOpcode = pair.TargetFacts.Single(f => f.Kind == "opcode" && f.Name == "SMSG_DEMO");
    var claim = new ComparisonClaim
    {
        Id = "claim-self-test", PairId = pair.Id, TraceIds = [trace.Id], Verdict = ClaimVerdict.Gap,
        Summary = "not implemented", Behavior = trace.Behavior,
        NegativeBehavior = trace.NegativeBehavior,
    };
    ReviewQueue gapQueue = ReviewQueueWriter.Build(pair, [trace], [claim]);
    if (!gapQueue.Packets.SelectMany(p => p.ReferenceFactIds).Contains(opcode.Id))
        throw new InvalidDataException("nonterminal gap disappeared from the work queue");
    if (gapQueue.Packets[0].WorkKind != "implementation" ||
        gapQueue.Packets[0].Claims.All(c => c.Id != claim.Id))
        throw new InvalidDataException("nonterminal gap did not become the first implementation packet");
    claim.Verdict = ClaimVerdict.VerifiedEquivalent;
    claim.TargetFacts = [new() { Id = targetOpcode.Id, EvidenceSha256 = targetOpcode.EvidenceSha256 }];
    claim.VerificationIds = ["self-test"];
    ReviewQueue resolvedQueue = ReviewQueueWriter.Build(pair, [trace], [claim]);
    ReviewPacket completedPacket = resolvedQueue.Packets.Single(p => p.ReferenceFactIds.Contains(opcode.Id));
    if (completedPacket.UnresolvedReferenceFactIds.Contains(opcode.Id))
        throw new InvalidDataException("terminally resolved fact remained unresolved in packet history");
    string packet = Path.Combine(root, "packet.md");
    ReviewQueueWriter.WritePacket(packet, pair, queue, queue.Packets[0].Id);
    if (!File.ReadAllText(packet).Contains("Audit contract", StringComparison.Ordinal))
        throw new InvalidDataException("review packet omitted its audit contract");
    string workspace = Path.Combine(root, "workspace");
    PacketWorkspaceWriter.Materialize(workspace, pair, queue);
    PacketWorkspaceValidation workspaceResult = PacketWorkspaceWriter.Validate(workspace, pair, queue);
    if (workspaceResult.Errors.Count != 0 || workspaceResult.PacketCount != queue.Packets.Count)
        throw new InvalidDataException("packet workspace did not materialize a valid blocked audit");
    string updateLedger = Path.Combine(root, "update.jsonl");
    JsonStore.WriteLines(updateLedger, new[] { claim });
    JsonStore.UpdateLine<ComparisonClaim>(updateLedger, c => c.Id == claim.Id,
        c => c.Summary = "updated safely");
    if (JsonStore.ReadLines<ComparisonClaim>(updateLedger).Single().Summary != "updated safely")
        throw new InvalidDataException("guarded JSONL update failed");
    string changedTargetRoot = Path.Combine(root, "target-changed");
    Directory.CreateDirectory(Path.Combine(changedTargetRoot, "Net"));
    File.WriteAllText(Path.Combine(changedTargetRoot, "Net", "Opcodes.cs"),
        "public enum Op { SMSG_DEMO = 0x124 }\n");
    SnapshotManifest changedTarget = SnapshotCapture.Capture("msui", changedTargetRoot, null);
    FactIndex changedTargetFacts = FactIndexer.Build(changedTarget);
    SnapshotPair changedPair = ComparisonEngine.Compare(reference, referenceFacts, changedTarget, changedTargetFacts);
    string migratedTraces = Path.Combine(root, "migrated-traces.jsonl");
    string migratedClaims = Path.Combine(root, "migrated-claims.jsonl");
    MigrationReport migration = PairMigration.Migrate(pair, changedPair, [trace], [claim],
        migratedTraces, migratedClaims);
    ComparisonClaim downgraded = JsonStore.ReadLines<ComparisonClaim>(migratedClaims).Single();
    if (migration.DowngradedClaims.Count != 1 || downgraded.Verdict != ClaimVerdict.ImplementedUnverified ||
        downgraded.TargetFacts.Count != 0 || downgraded.VerificationIds.Count != 0 ||
        JsonStore.ReadLines<BehaviorTrace>(migratedTraces).Single().PairId != changedPair.Id)
        throw new InvalidDataException("pair migration did not invalidate changed target evidence");
    string oldAuditPath = Path.Combine(workspace, pair.Id,
        PacketWorkspaceWriter.FolderName(queue.Packets[0]), "audit.json");
    PacketAudit oldAudit = JsonStore.Read<PacketAudit>(oldAuditPath);
    oldAudit.Status = PacketAuditStatus.Reviewed;
    oldAudit.Classification = PacketClassification.Equivalent;
    oldAudit.WritePolicy = PacketWritePolicy.Preserve;
    oldAudit.MsuiBefore = new() { Summary = "Present", Evidence = ["old snapshot"] };
    oldAudit.ReferenceRequirement = new() { Summary = "Equivalent", Evidence = ["reference"] };
    JsonStore.Write(oldAuditPath, oldAudit);
    ReviewQueue changedQueue = ReviewQueueWriter.Build(changedPair, [], []);
    string changedWorkspace = Path.Combine(root, "changed-workspace");
    PacketWorkspaceWriter.Materialize(changedWorkspace, changedPair, changedQueue);
    if (PacketWorkspaceWriter.MigrateAudits(workspace, pair.Id, changedWorkspace, changedPair,
            changedQueue) != 1)
        throw new InvalidDataException("reviewed packet audit migration count was wrong");
    ReviewPacket changedPacket = changedQueue.Packets.Single(p =>
        p.SourcePath == queue.Packets[0].SourcePath && p.Chunk == queue.Packets[0].Chunk);
    PacketAudit migratedAudit = JsonStore.Read<PacketAudit>(Path.Combine(changedWorkspace,
        changedPair.Id, PacketWorkspaceWriter.FolderName(changedPacket), "audit.json"));
    if (migratedAudit.PairId != changedPair.Id || migratedAudit.PacketId != changedPacket.Id ||
        migratedAudit.MsuiBefore.Summary != "Present")
        throw new InvalidDataException("reviewed packet audit identity was not migrated");
    Console.WriteLine($"[snapshot-parity] self-test PASS ({referenceFacts.Facts.Count} reference facts, " +
        $"{targetFacts.Facts.Count} target facts, {pair.Candidates.Count} candidates)");
    return 0;
}

static Dictionary<string, string> Options(string[] arguments)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < arguments.Length; i++)
    {
        if (!arguments[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= arguments.Length)
            throw new ArgumentException($"bad option {arguments[i]}");
        options[arguments[i][2..]] = arguments[++i];
    }
    return options;
}

static string Need(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out string? value) ? value : throw new ArgumentException($"--{name} required");

static void Usage() => Console.Error.WriteLine(
    "usage: snapshot-parity capture --kind benilla|msui --root PATH --manifest PATH [--bundle PATH]\n" +
    "       snapshot-parity index --manifest PATH --facts PATH\n" +
    "       snapshot-parity compare --reference-manifest PATH --reference-facts PATH " +
        "--target-manifest PATH --target-facts PATH --pair PATH\n" +
    "       snapshot-parity validate --pair PATH --traces PATH --claims PATH\n" +
    "       snapshot-parity report --pair PATH --traces PATH --claims PATH --out PATH\n" +
    "       snapshot-parity queue --pair PATH --traces PATH --claims PATH --out PATH\n" +
    "       snapshot-parity packet --pair PATH --queue PATH --id PACKET_ID --out PATH\n" +
    "       snapshot-parity workspace --pair PATH --queue PATH --out DIRECTORY\n" +
    "       snapshot-parity workspace-validate --pair PATH --queue PATH --root DIRECTORY\n" +
    "       snapshot-parity workspace-migrate --old-root DIRECTORY --old-pair-id PAIR_ID " +
        "--new-root DIRECTORY --new-pair PATH --new-queue PATH\n" +
    "       snapshot-parity claim-update --claims PATH --id CLAIM_ID --verdict VERDICT " +
        "[--verification ID|ID] [--summary TEXT] [--decision ID]\n" +
    "       snapshot-parity claim-targets --pair PATH --claims PATH --id CLAIM_ID " +
        "--target-facts ID|ID\n" +
    "       snapshot-parity trace-add --pair PATH --traces PATH --id ID --name TEXT --surface TEXT " +
        "--trigger TEXT --preconditions TEXT --behavior TEXT --negative TEXT --reference-facts ID|ID\n" +
    "       snapshot-parity claim-add --pair PATH --traces PATH --claims PATH --id ID --trace-ids ID|ID " +
        "--verdict VERDICT --summary TEXT --behavior TEXT --negative TEXT [--target-facts ID|ID] " +
        "[--verification ID|ID] [--decision ID]\n" +
    "       snapshot-parity migrate --old-pair PATH --new-pair PATH --traces PATH --claims PATH " +
        "--out-traces PATH --out-claims PATH --report PATH\n" +
    "       snapshot-parity self-test");
