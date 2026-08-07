using System.Text;
using System.Text.RegularExpressions;

namespace SnapshotParity;

internal static class PacketWorkspaceWriter
{
    private const string PendingBefore = "Not yet inspected. No MSUI change is authorized.";
    private const string PendingReference = "Not yet behaviorally reviewed.";
    private const string PendingChange = "No change authorized; write policy is blocked.";
    private const string PendingAfter = "MSUI remains unchanged from this pair's target snapshot.";

    public static void Materialize(string root, SnapshotPair pair, ReviewQueue queue)
    {
        EnsurePair(queue, pair);
        string pairRoot = Path.Combine(Path.GetFullPath(root), pair.Id);
        Directory.CreateDirectory(pairRoot);
        var rows = new List<(ReviewPacket Packet, string Folder, PacketAudit Audit)>();
        foreach (ReviewPacket packet in queue.Packets)
        {
            string folderName = FolderName(packet);
            string folder = Path.Combine(pairRoot, folderName);
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, "evidence"));
            string auditPath = Path.Combine(folder, "audit.json");
            PacketAudit audit;
            if (File.Exists(auditPath))
            {
                audit = JsonStore.Read<PacketAudit>(auditPath);
                if (audit.PairId != pair.Id || audit.PacketId != packet.Id)
                    throw new InvalidDataException($"audit identity mismatch in {auditPath}");
            }
            else
            {
                audit = NewAudit(pair, packet);
                JsonStore.Write(auditPath, audit);
            }
            ReviewQueueWriter.WritePacket(Path.Combine(folder, "reference.md"), pair, queue, packet.Id);
            WriteReadme(Path.Combine(folder, "README.md"), packet, queue.Packets.Count, audit);
            rows.Add((packet, folderName, audit));
        }
        WriteIndex(Path.Combine(pairRoot, "README.md"), pair, rows);
    }

    public static PacketWorkspaceValidation Validate(string root, SnapshotPair pair, ReviewQueue queue)
    {
        EnsurePair(queue, pair);
        var result = new PacketWorkspaceValidation { PacketCount = queue.Packets.Count };
        string pairRoot = Path.Combine(Path.GetFullPath(root), pair.Id);
        foreach (ReviewPacket packet in queue.Packets)
        {
            string folder = Path.Combine(pairRoot, FolderName(packet));
            string auditPath = Path.Combine(folder, "audit.json");
            if (!Directory.Exists(folder) || !File.Exists(auditPath))
            {
                result.Errors.Add($"{packet.Id}: packet folder or audit.json is missing");
                continue;
            }
            PacketAudit audit;
            try { audit = JsonStore.Read<PacketAudit>(auditPath); }
            catch (Exception ex) { result.Errors.Add($"{packet.Id}: invalid audit.json: {ex.Message}"); continue; }
            ValidateAudit(packet, pair, audit, result);
        }
        return result;
    }

    public static int MigrateAudits(string oldRoot, string oldPairId, string newRoot,
        SnapshotPair newPair, ReviewQueue newQueue)
    {
        EnsurePair(newQueue, newPair);
        string oldPairRoot = Path.Combine(Path.GetFullPath(oldRoot), oldPairId);
        string newPairRoot = Path.Combine(Path.GetFullPath(newRoot), newPair.Id);
        if (!Directory.Exists(oldPairRoot)) throw new DirectoryNotFoundException(oldPairRoot);
        int migrated = 0;
        foreach (string auditPath in Directory.EnumerateFiles(oldPairRoot, "audit.json", SearchOption.AllDirectories))
        {
            PacketAudit audit = JsonStore.Read<PacketAudit>(auditPath);
            if (audit.Status == PacketAuditStatus.Unreviewed) continue;
            string folder = Path.GetDirectoryName(auditPath)!;
            string referencePath = Path.Combine(folder, "reference.md");
            if (!File.Exists(referencePath))
                throw new InvalidDataException($"reviewed audit lacks reference.md: {auditPath}");
            string reference = File.ReadAllText(referencePath, Encoding.UTF8);
            Match sourceMatch = Regex.Match(reference, @"Reference source: `([^`]+)`");
            Match chunkMatch = Regex.Match(Path.GetFileName(folder), @"-part-(\d+)-packet-");
            if (!sourceMatch.Success || !chunkMatch.Success)
                throw new InvalidDataException($"cannot recover packet source/chunk from {folder}");
            string source = sourceMatch.Groups[1].Value;
            int chunk = int.Parse(chunkMatch.Groups[1].Value);
            ReviewPacket packet = newQueue.Packets.SingleOrDefault(p =>
                    p.SourcePath == source && p.Chunk == chunk)
                ?? throw new InvalidDataException($"reviewed {source} part {chunk} is absent from the new queue");
            string destination = Path.Combine(newPairRoot, FolderName(packet));
            if (!Directory.Exists(destination))
                throw new DirectoryNotFoundException($"materialize the new workspace first: {destination}");
            audit.PairId = newPair.Id;
            audit.PacketId = packet.Id;
            JsonStore.Write(Path.Combine(destination, "audit.json"), audit);
            string oldEvidence = Path.Combine(folder, "evidence");
            string newEvidence = Path.Combine(destination, "evidence");
            if (Directory.Exists(oldEvidence))
                foreach (string file in Directory.EnumerateFiles(oldEvidence))
                    File.Copy(file, Path.Combine(newEvidence, Path.GetFileName(file)), overwrite: true);
            migrated++;
        }
        return migrated;
    }

    public static string FolderName(ReviewPacket packet)
    {
        string baseName = Path.GetFileNameWithoutExtension(packet.SourcePath);
        var slug = new StringBuilder();
        foreach (char c in baseName.ToLowerInvariant())
            slug.Append(char.IsLetterOrDigit(c) ? c : '-');
        string clean = string.Join('-', slug.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 48) clean = clean[..48].TrimEnd('-');
        return $"{clean}-part-{packet.Chunk:D2}-{packet.Id}";
    }

    private static PacketAudit NewAudit(SnapshotPair pair, ReviewPacket packet) => new()
    {
        PairId = pair.Id,
        PacketId = packet.Id,
        MsuiBefore = new() { Summary = PendingBefore },
        ReferenceRequirement = new() { Summary = PendingReference },
        Change = new() { Summary = PendingChange },
        MsuiAfter = new() { Summary = PendingAfter },
    };

    private static void ValidateAudit(ReviewPacket packet, SnapshotPair pair, PacketAudit audit,
        PacketWorkspaceValidation result)
    {
        string prefix = packet.Id;
        if (audit.PairId != pair.Id || audit.PacketId != packet.Id)
            result.Errors.Add($"{prefix}: pair or packet identity mismatch");
        PacketWritePolicy expected = audit.Classification switch
        {
            PacketClassification.Missing => PacketWritePolicy.Port,
            PacketClassification.Broken => PacketWritePolicy.Repair,
            PacketClassification.Different or PacketClassification.Intentional or
                PacketClassification.Equivalent or PacketClassification.NotRuntime or
                PacketClassification.InternalSupport or PacketClassification.TestOnly => PacketWritePolicy.Preserve,
            _ => PacketWritePolicy.Blocked,
        };
        if (audit.WritePolicy != expected)
            result.Errors.Add($"{prefix}: {audit.Classification} requires write policy {expected}, not {audit.WritePolicy}");
        if (audit.Status == PacketAuditStatus.Unreviewed)
        {
            result.BlockedCount++;
            if (audit.Classification != PacketClassification.Unreviewed || audit.WritePolicy != PacketWritePolicy.Blocked)
                result.Errors.Add($"{prefix}: unreviewed packets must remain classified unreviewed and blocked");
            return;
        }
        if (audit.Status == PacketAuditStatus.Blocked || audit.WritePolicy == PacketWritePolicy.Blocked)
            result.BlockedCount++;
        if (string.IsNullOrWhiteSpace(audit.MsuiBefore.Summary) || audit.MsuiBefore.Summary == PendingBefore)
            result.Errors.Add($"{prefix}: reviewed packet is missing the MSUI-before record");
        if (audit.MsuiBefore.Evidence.Count == 0)
            result.Errors.Add($"{prefix}: reviewed packet is missing MSUI-before evidence");
        if (string.IsNullOrWhiteSpace(audit.ReferenceRequirement.Summary) ||
            audit.ReferenceRequirement.Summary == PendingReference)
            result.Errors.Add($"{prefix}: reviewed packet is missing its reference requirement");
        if (audit.Classification == PacketClassification.Intentional && string.IsNullOrWhiteSpace(audit.DecisionId))
            result.Errors.Add($"{prefix}: intentional difference requires a user decision id");
        if (audit.WritePolicy == PacketWritePolicy.Preserve && audit.Change.Files.Count != 0)
            result.Errors.Add($"{prefix}: preserve policy may not list changed files");
        if (audit.Status is PacketAuditStatus.Implemented or PacketAuditStatus.Verified)
        {
            if (audit.WritePolicy is not (PacketWritePolicy.Port or PacketWritePolicy.Repair))
                result.Errors.Add($"{prefix}: implemented status requires port or repair policy");
            if (audit.Change.Files.Count == 0 || string.IsNullOrWhiteSpace(audit.Change.Summary) ||
                audit.Change.Summary == PendingChange)
                result.Errors.Add($"{prefix}: implementation is missing an exact change record");
            if (audit.MsuiAfter.Evidence.Count == 0 || string.IsNullOrWhiteSpace(audit.MsuiAfter.Summary) ||
                audit.MsuiAfter.Summary == PendingAfter)
                result.Errors.Add($"{prefix}: implementation is missing MSUI-after evidence");
        }
        if (audit.Status == PacketAuditStatus.Verified)
        {
            result.VerifiedCount++;
            if (audit.Verification.Count == 0)
                result.Errors.Add($"{prefix}: verified packet has no verification evidence");
        }
    }

    private static void WriteReadme(string path, ReviewPacket packet, int total, PacketAudit audit)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {Path.GetFileNameWithoutExtension(packet.SourcePath)} — part {packet.Chunk}").AppendLine();
        b.AppendLine($"- Packet: `{packet.Id}`");
        b.AppendLine($"- Current queue position: {packet.Sequence}/{total}");
        b.AppendLine($"- Work kind: `{packet.WorkKind}`");
        b.AppendLine($"- Reference source: `{packet.SourcePath}`");
        b.AppendLine($"- Facts: {packet.ReferenceFactIds.Count}");
        b.AppendLine($"- Unresolved facts: {packet.UnresolvedReferenceFactIds.Count}");
        b.AppendLine($"- Status: **{audit.Status}**");
        b.AppendLine($"- Classification: **{audit.Classification}**");
        b.AppendLine($"- Write policy: **{audit.WritePolicy}**").AppendLine();
        b.AppendLine("## MSUI before").AppendLine().AppendLine(audit.MsuiBefore.Summary).AppendLine();
        AppendList(b, audit.MsuiBefore.Evidence);
        b.AppendLine("## Benilla requirement").AppendLine().AppendLine(audit.ReferenceRequirement.Summary).AppendLine();
        AppendList(b, audit.ReferenceRequirement.Evidence);
        b.AppendLine("## Exact change or preservation decision").AppendLine().AppendLine(audit.Change.Summary).AppendLine();
        AppendList(b, audit.Change.Files);
        b.AppendLine("## MSUI now").AppendLine().AppendLine(audit.MsuiAfter.Summary).AppendLine();
        AppendList(b, audit.MsuiAfter.Evidence);
        b.AppendLine("## Verification").AppendLine();
        AppendList(b, audit.Verification);
        b.AppendLine("See [reference.md](reference.md) for every hash-pinned fact and mechanical navigation candidate.");
        Write(path, b.ToString());
    }

    private static void WriteIndex(string path, SnapshotPair pair,
        IReadOnlyList<(ReviewPacket Packet, string Folder, PacketAudit Audit)> rows)
    {
        var b = new StringBuilder();
        b.AppendLine($"# Packet ledger — {pair.Id}").AppendLine();
        b.AppendLine("Every packet defaults to a blocked write policy. Only `missing` may be ported and only `broken` may be repaired. `different`, `intentional`, and `equivalent` preserve MSUI. An unclear packet remains blocked.").AppendLine();
        b.AppendLine("| Queue | Packet | Source | Part | Status | Classification | Write policy |");
        b.AppendLine("|---:|---|---|---:|---|---|---|");
        foreach (var row in rows.OrderBy(r => r.Packet.Sequence))
            b.AppendLine($"| {row.Packet.Sequence} | [{row.Packet.Id}]({row.Folder}/README.md) | `{Escape(row.Packet.SourcePath)}` | {row.Packet.Chunk} | {row.Audit.Status} | {row.Audit.Classification} | {row.Audit.WritePolicy} |");
        Write(path, b.ToString());
    }

    private static void AppendList(StringBuilder b, IReadOnlyList<string> values)
    {
        if (values.Count == 0) b.AppendLine("- None recorded.").AppendLine();
        else { foreach (string value in values) b.AppendLine($"- {value}"); b.AppendLine(); }
    }

    private static void EnsurePair(ReviewQueue queue, SnapshotPair pair)
    {
        if (queue.PairId != pair.Id) throw new InvalidDataException("review queue belongs to a different snapshot pair");
    }

    private static void Write(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, value, new UTF8Encoding(false));
    }

    private static string Escape(string value) => value.Replace("|", "\\|");
}
