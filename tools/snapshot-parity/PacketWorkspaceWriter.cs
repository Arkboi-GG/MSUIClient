using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;

namespace SnapshotParity;

internal static class PacketWorkspaceWriter
{
    private const string PendingBefore = "Not yet inspected. No MSUI change is authorized.";
    private const string PendingReference = "Not yet behaviorally reviewed.";
    private const string PendingChange = "No change authorized; write policy is blocked.";
    private const string PendingAfter = "MSUI remains unchanged from this pair's target snapshot.";
    internal static readonly string[] RequiredAcceptanceChecks =
    [
        "reference-dependency-closure",
        "state-reachability",
        "runtime-wire-contract",
        "input-modifier-contract",
        "visual-geometry-anchors",
        "visual-containment-cropping",
        "texture-coordinates-layering",
        "interaction-bounds-states",
        "dynamic-content-boundaries",
        "audio-count-timing",
        "negative-behavior",
        "preserved-difference-regression",
        "deterministic-verification",
        "live-visual-verification",
    ];

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
            ValidateAudit(packet, pair, audit, folder, result);
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
            if (audit.Status == PacketAuditStatus.Verified)
            {
                audit.Status = audit.WritePolicy is PacketWritePolicy.Port or PacketWritePolicy.Repair
                    ? PacketAuditStatus.Implemented : PacketAuditStatus.Reviewed;
                audit.VerificationArtifactIds.Clear();
                audit.Acceptance = RequiredAcceptanceChecks.ToDictionary(id => id,
                    _ => new AcceptanceCheckpoint
                    {
                        Result = AcceptanceResult.Unreviewed,
                        Summary = $"Re-verification required after migration from {oldPairId}.",
                    }, StringComparer.Ordinal);
                audit.Verification.Add($"HISTORICAL ONLY: verification belongs to {oldPairId}; current-pair acceptance reset");
            }
            audit.PairId = newPair.Id;
            audit.PacketId = packet.Id;
            JsonStore.Write(Path.Combine(destination, "audit.json"), audit);
            string oldEvidence = Path.Combine(folder, "evidence");
            string newEvidence = Path.Combine(destination, "evidence");
            if (Directory.Exists(oldEvidence))
                CopyTree(oldEvidence, newEvidence);
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
        Acceptance = RequiredAcceptanceChecks.ToDictionary(id => id, _ => new AcceptanceCheckpoint(),
            StringComparer.Ordinal),
    };

    private static void ValidateAudit(ReviewPacket packet, SnapshotPair pair, PacketAudit audit, string folder,
        PacketWorkspaceValidation result)
    {
        string prefix = packet.Id;
        if (audit.PairId != pair.Id || audit.PacketId != packet.Id)
            result.Errors.Add($"{prefix}: pair or packet identity mismatch");
        PacketWritePolicy expected = ExpectedPolicy(audit.Classification);
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
        ValidateDossierEvidence(folder, prefix, pair, audit, result);
        if (audit.Classification == PacketClassification.Intentional && string.IsNullOrWhiteSpace(audit.DecisionId))
            result.Errors.Add($"{prefix}: intentional difference requires a user decision id");
        if (audit.WritePolicy == PacketWritePolicy.Preserve && audit.Change.Files.Count != 0)
            result.Errors.Add($"{prefix}: preserve policy may not list changed files");
        if (audit.Status is PacketAuditStatus.Implemented or PacketAuditStatus.Verified)
        {
            if (string.IsNullOrWhiteSpace(audit.Reviewer) || audit.ReviewedUtc is null)
                result.Errors.Add($"{prefix}: implementation has no reviewer and review timestamp");
            if (audit.WritePolicy is not (PacketWritePolicy.Port or PacketWritePolicy.Repair))
                result.Errors.Add($"{prefix}: implemented status requires port or repair policy");
            if (audit.Change.Files.Count == 0 || string.IsNullOrWhiteSpace(audit.Change.Summary) ||
                audit.Change.Summary == PendingChange)
                result.Errors.Add($"{prefix}: implementation is missing an exact change record");
            if (audit.MsuiAfter.Evidence.Count == 0 || string.IsNullOrWhiteSpace(audit.MsuiAfter.Summary) ||
                audit.MsuiAfter.Summary == PendingAfter)
                result.Errors.Add($"{prefix}: implementation is missing MSUI-after evidence");
            ValidateTraceDispositions(packet, audit, prefix, result);
            foreach (string id in RequiredAcceptanceChecks)
            {
                if (!audit.Acceptance.TryGetValue(id, out AcceptanceCheckpoint? check))
                {
                    result.Errors.Add($"{prefix}: implementation is missing acceptance checkpoint {id}");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(check.Summary))
                    result.Errors.Add($"{prefix}: acceptance checkpoint {id} has no finding, remaining gate, or N/A rationale");
                if (!check.Evidence.Any(IsEvidencePath))
                    result.Errors.Add($"{prefix}: acceptance checkpoint {id} has no packet-local evidence");
            }
        }
        if (audit.Status == PacketAuditStatus.Verified)
        {
            result.VerifiedCount++;
            if (packet.UnresolvedReferenceFactIds.Count != 0 || packet.WorkKind != "completed")
                result.Errors.Add($"{prefix}: verified packet still has unresolved or nonterminal facts");
            if (audit.Verification.Count == 0)
                result.Errors.Add($"{prefix}: verified packet has no verification evidence");
            if (packet.Claims.Count == 0 || packet.Claims.Any(c => !c.Verdict.IsTerminal()))
                result.Errors.Add($"{prefix}: verified packet lacks terminal linked claims");
            foreach (string id in RequiredAcceptanceChecks)
            {
                if (!audit.Acceptance.TryGetValue(id, out AcceptanceCheckpoint? check))
                {
                    continue;
                }
                if (check.Result is AcceptanceResult.Unreviewed or AcceptanceResult.Fail)
                    result.Errors.Add($"{prefix}: acceptance checkpoint {id} is {check.Result}");
            }
            ValidateEvidence(folder, prefix, pair, packet, audit, result);
        }
    }

    private static void ValidateDossierEvidence(string folder, string prefix, SnapshotPair pair, PacketAudit audit,
        PacketWorkspaceValidation result)
    {
        string evidenceRoot = Path.Combine(Path.GetFullPath(folder), "evidence");
        void RequireSection(string section, IEnumerable<string> values)
        {
            if (!values.Any(IsEvidencePath))
                result.Errors.Add($"{prefix}: {section} has no packet-local evidence path");
        }
        RequireSection("MSUI-before", audit.MsuiBefore.Evidence);
        RequireSection("reference requirement", audit.ReferenceRequirement.Evidence);
        if (audit.Status is PacketAuditStatus.Implemented or PacketAuditStatus.Verified)
            RequireSection("MSUI-after", audit.MsuiAfter.Evidence);

        IEnumerable<string> references = audit.MsuiBefore.Evidence
            .Concat(audit.ReferenceRequirement.Evidence)
            .Concat(audit.MsuiAfter.Evidence)
            .Concat(audit.Verification)
            .Concat(audit.Acceptance.Values.SelectMany(check => check.Evidence))
            .Concat(audit.TraceDispositions.SelectMany(disposition => disposition.Evidence));
        foreach (string reference in references.Where(IsEvidencePath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string relative = reference["evidence/".Length..].Replace('\\', '/');
            string full;
            try { full = ResolveEvidencePath(evidenceRoot, relative); }
            catch (InvalidDataException ex) { result.Errors.Add($"{prefix}: {ex.Message}"); continue; }
            if (!File.Exists(full))
                result.Errors.Add($"{prefix}: dossier evidence is missing: {reference}");
            else if (new FileInfo(full).Length == 0)
                result.Errors.Add($"{prefix}: dossier evidence is empty: {reference}");
        }
        if (audit.Status == PacketAuditStatus.Implemented)
            ValidateEvidenceIndex(folder, prefix, pair, audit, result);
    }

    private static void ValidateTraceDispositions(ReviewPacket packet, PacketAudit audit, string prefix,
        PacketWorkspaceValidation result)
    {
        var expectedTraceIds = packet.Claims.SelectMany(c => c.TraceIds)
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var dispositions = new Dictionary<string, TraceDisposition>(StringComparer.Ordinal);
        foreach (TraceDisposition disposition in audit.TraceDispositions)
        {
            if (string.IsNullOrWhiteSpace(disposition.TraceId) ||
                !dispositions.TryAdd(disposition.TraceId, disposition))
            {
                result.Errors.Add($"{prefix}: trace dispositions have an empty or duplicate trace id");
                continue;
            }
            PacketWritePolicy policy = ExpectedPolicy(disposition.Classification);
            if (disposition.WritePolicy != policy)
                result.Errors.Add($"{prefix}: trace {disposition.TraceId} classification requires {policy}");
            if (string.IsNullOrWhiteSpace(disposition.Summary) || !disposition.Evidence.Any(IsEvidencePath))
                result.Errors.Add($"{prefix}: trace {disposition.TraceId} lacks a finding or packet-local evidence");
            if (policy is PacketWritePolicy.Port or PacketWritePolicy.Repair)
            {
                if (disposition.ChangedSymbols.Count == 0)
                    result.Errors.Add($"{prefix}: changed trace {disposition.TraceId} has no exact symbol/hunk mapping");
                ValidateSymbolMappings(prefix, disposition.TraceId, "changed", disposition.ChangedSymbols, result);
            }
            else if (policy == PacketWritePolicy.Preserve)
            {
                if (disposition.ChangedSymbols.Count != 0)
                    result.Errors.Add($"{prefix}: preserved trace {disposition.TraceId} lists changed symbols");
                if (disposition.PreservedSymbols.Count == 0)
                    result.Errors.Add($"{prefix}: preserved trace {disposition.TraceId} has no protected symbol mapping");
                ValidateSymbolMappings(prefix, disposition.TraceId, "preserved", disposition.PreservedSymbols, result);
            }
        }
        foreach (string traceId in expectedTraceIds.Where(id => !dispositions.ContainsKey(id)))
            result.Errors.Add($"{prefix}: linked trace {traceId} has no per-trace disposition");
        foreach (string traceId in dispositions.Keys.Where(id => !expectedTraceIds.Contains(id)))
            result.Errors.Add($"{prefix}: trace disposition {traceId} is not linked to this packet");
    }

    private static void ValidateSymbolMappings(string prefix, string traceId, string kind,
        IEnumerable<string> mappings, PacketWorkspaceValidation result)
    {
        foreach (string mapping in mappings)
            if (string.IsNullOrWhiteSpace(mapping) || !mapping.Contains(':') ||
                mapping.Contains('*') || mapping.Contains('?') || mapping.EndsWith(':'))
                result.Errors.Add($"{prefix}: trace {traceId} has non-exact {kind} mapping {mapping}");
    }

    private static PacketWritePolicy ExpectedPolicy(PacketClassification classification) => classification switch
    {
        PacketClassification.Missing => PacketWritePolicy.Port,
        PacketClassification.Broken => PacketWritePolicy.Repair,
        PacketClassification.Different or PacketClassification.Intentional or
            PacketClassification.Equivalent or PacketClassification.NotRuntime or
            PacketClassification.InternalSupport or PacketClassification.TestOnly => PacketWritePolicy.Preserve,
        _ => PacketWritePolicy.Blocked,
    };

    public static int SealEvidence(string packetFolder, SnapshotPair pair)
    {
        string folder = Path.GetFullPath(packetFolder);
        string auditPath = Path.Combine(folder, "audit.json");
        PacketAudit audit = JsonStore.Read<PacketAudit>(auditPath);
        if (audit.PairId != pair.Id)
            throw new InvalidDataException($"audit belongs to {audit.PairId}, not {pair.Id}");
        string evidence = Path.Combine(folder, "evidence");
        if (!Directory.Exists(evidence)) throw new DirectoryNotFoundException(evidence);
        string indexPath = Path.Combine(evidence, "evidence-index.json");
        string[] files = Directory.EnumerateFiles(evidence, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(indexPath), StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidDataException("cannot seal an empty evidence directory");
        var index = new PacketEvidenceIndex
        {
            PairId = pair.Id,
            PacketId = audit.PacketId,
            TargetSnapshotId = pair.TargetSnapshotId,
            GeneratedUtc = DateTimeOffset.UtcNow,
        };
        foreach (string file in files)
        {
            using FileStream stream = File.OpenRead(file);
            index.Files.Add(new()
            {
                Path = Path.GetRelativePath(evidence, file).Replace('\\', '/'),
                Length = stream.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            });
        }
        JsonStore.Write(indexPath, index);
        return index.Files.Count;
    }

    private static void ValidateEvidence(string folder, string prefix, SnapshotPair pair, ReviewPacket packet,
        PacketAudit audit,
        PacketWorkspaceValidation result)
    {
        Dictionary<string, PacketEvidenceFile>? indexed =
            ValidateEvidenceIndex(folder, prefix, pair, audit, result);
        if (indexed is null) return;
        string evidenceRoot = Path.Combine(Path.GetFullPath(folder), "evidence");
        IEnumerable<string> references = audit.MsuiBefore.Evidence
            .Concat(audit.ReferenceRequirement.Evidence)
            .Concat(audit.MsuiAfter.Evidence)
            .Concat(audit.Verification)
            .Concat(audit.Acceptance.Values.SelectMany(check => check.Evidence))
            .Concat(audit.TraceDispositions.SelectMany(disposition => disposition.Evidence));
        foreach (string reference in references.Where(IsEvidencePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string relative = reference["evidence/".Length..].Replace('\\', '/');
            string full;
            try { full = ResolveEvidencePath(evidenceRoot, relative); }
            catch (InvalidDataException ex) { result.Errors.Add($"{prefix}: {ex.Message}"); continue; }
            if (!File.Exists(full)) result.Errors.Add($"{prefix}: referenced evidence is missing: {reference}");
            else if (!indexed.ContainsKey(relative)) result.Errors.Add($"{prefix}: referenced evidence is not sealed: {reference}");
        }
        RequireSelfContained(prefix, "MSUI-before", audit.MsuiBefore.Evidence, result);
        RequireSelfContained(prefix, "reference", audit.ReferenceRequirement.Evidence, result);
        RequireSelfContained(prefix, "MSUI-after", audit.MsuiAfter.Evidence, result);
        RequireSelfContained(prefix, "verification", audit.Verification, result);
        ValidateVerificationManifest(evidenceRoot, indexed, prefix, pair, packet, audit, result);
    }

    private static Dictionary<string, PacketEvidenceFile>? ValidateEvidenceIndex(string folder, string prefix,
        SnapshotPair pair, PacketAudit audit, PacketWorkspaceValidation result)
    {
        string evidenceRoot = Path.Combine(Path.GetFullPath(folder), "evidence");
        string indexPath = Path.Combine(evidenceRoot, "evidence-index.json");
        if (!File.Exists(indexPath))
        {
            result.Errors.Add($"{prefix}: implemented/verified packet has no sealed evidence index");
            return null;
        }
        PacketEvidenceIndex index;
        try { index = JsonStore.Read<PacketEvidenceIndex>(indexPath); }
        catch (Exception ex)
        {
            result.Errors.Add($"{prefix}: invalid evidence index: {ex.Message}");
            return null;
        }
        if (index.PairId != pair.Id || index.PacketId != audit.PacketId ||
            index.TargetSnapshotId != pair.TargetSnapshotId)
            result.Errors.Add($"{prefix}: evidence index is not pinned to this pair, packet, and target snapshot");
        if (index.GeneratedUtc == default)
            result.Errors.Add($"{prefix}: evidence index has no seal timestamp");
        var indexed = new Dictionary<string, PacketEvidenceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (PacketEvidenceFile entry in index.Files)
        {
            string relative = entry.Path.Replace('\\', '/');
            if (!indexed.TryAdd(relative, entry))
            {
                result.Errors.Add($"{prefix}: duplicate evidence index entry {relative}");
                continue;
            }
            string full = ResolveEvidencePath(evidenceRoot, relative);
            if (!File.Exists(full))
            {
                result.Errors.Add($"{prefix}: sealed evidence file is missing: evidence/{relative}");
                continue;
            }
            var info = new FileInfo(full);
            if (info.Length == 0 || entry.Length == 0)
                result.Errors.Add($"{prefix}: zero-length evidence file: evidence/{relative}");
            using FileStream stream = File.OpenRead(full);
            string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (info.Length != entry.Length || !hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                result.Errors.Add($"{prefix}: sealed evidence changed: evidence/{relative}");
        }
        string[] actual = Directory.EnumerateFiles(evidenceRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(indexPath), StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(evidenceRoot, path).Replace('\\', '/')).ToArray();
        foreach (string relative in actual.Where(path => !indexed.ContainsKey(path)))
            result.Errors.Add($"{prefix}: unsealed evidence file: evidence/{relative}");
        return indexed;
    }

    private static void ValidateVerificationManifest(string evidenceRoot,
        IReadOnlyDictionary<string, PacketEvidenceFile> indexed, string prefix, SnapshotPair pair,
        ReviewPacket packet, PacketAudit audit, PacketWorkspaceValidation result)
    {
        const string manifestRelative = "verification-manifest.json";
        string manifestPath = Path.Combine(evidenceRoot, manifestRelative);
        if (!File.Exists(manifestPath) || !indexed.ContainsKey(manifestRelative))
        {
            result.Errors.Add($"{prefix}: verified packet has no sealed typed verification manifest");
            return;
        }
        VerificationArtifactManifest manifest;
        try { manifest = JsonStore.Read<VerificationArtifactManifest>(manifestPath); }
        catch (Exception ex)
        {
            result.Errors.Add($"{prefix}: invalid verification manifest: {ex.Message}");
            return;
        }
        if (manifest.PairId != pair.Id || manifest.PacketId != audit.PacketId ||
            manifest.ReferenceSnapshotSha256 != pair.ReferenceSnapshotSha256 ||
            manifest.TargetSnapshotSha256 != pair.TargetSnapshotSha256)
            result.Errors.Add($"{prefix}: verification manifest is not pinned to this pair and snapshots");

        var artifacts = new Dictionary<string, VerificationArtifact>(StringComparer.Ordinal);
        var targetFiles = pair.TargetFacts.Where(f => f.Kind == "file")
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (VerificationArtifact artifact in manifest.Artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Id) || !artifacts.TryAdd(artifact.Id, artifact))
            {
                result.Errors.Add($"{prefix}: verification artifacts have an empty or duplicate id");
                continue;
            }
            if (artifact.Result != VerificationArtifactResult.Pass || artifact.AssertionsPassed <= 0 ||
                artifact.AssertionsFailed != 0)
                result.Errors.Add($"{prefix}: artifact {artifact.Id} is not a passing assertion result");
            if (string.IsNullOrWhiteSpace(artifact.ScenarioId))
                result.Errors.Add($"{prefix}: artifact {artifact.Id} has no stable scenario id");
            if (!targetFiles.TryGetValue(artifact.ToolPath, out SourceFact? tool) ||
                !tool.FileSha256.Equals(artifact.ToolSha256, StringComparison.OrdinalIgnoreCase))
                result.Errors.Add($"{prefix}: artifact {artifact.Id} tool hash is absent or stale");
            if (artifact.Files.Count == 0 || artifact.CheckpointIds.Count == 0 || artifact.TraceIds.Count == 0)
                result.Errors.Add($"{prefix}: artifact {artifact.Id} lacks files, checkpoints, or trace coverage");
            if (artifact.Files.Count != artifact.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                result.Errors.Add($"{prefix}: artifact {artifact.Id} lists duplicate files");
            foreach (string file in artifact.Files)
            {
                if (!IsEvidencePath(file))
                {
                    result.Errors.Add($"{prefix}: artifact {artifact.Id} file is not packet-local: {file}");
                    continue;
                }
                string relative = file["evidence/".Length..].Replace('\\', '/');
                if (!indexed.ContainsKey(relative) || !File.Exists(ResolveEvidencePath(evidenceRoot, relative)))
                    result.Errors.Add($"{prefix}: artifact {artifact.Id} file is missing or unsealed: {file}");
            }
            foreach (string checkpoint in artifact.CheckpointIds.Where(id => !RequiredAcceptanceChecks.Contains(id)))
                result.Errors.Add($"{prefix}: artifact {artifact.Id} names unknown checkpoint {checkpoint}");
            ValidateArtifactResult(evidenceRoot, indexed, prefix, pair, packet, audit, artifact, result);
        }

        foreach (string id in audit.VerificationArtifactIds)
            if (!artifacts.ContainsKey(id)) result.Errors.Add($"{prefix}: audit references missing artifact {id}");
        if (audit.VerificationArtifactIds.Count == 0)
            result.Errors.Add($"{prefix}: verified packet has no typed verification artifact ids");

        var expectedTraceIds = packet.Claims.SelectMany(c => c.TraceIds)
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (PacketClaim claim in packet.Claims.Where(c => c.Verdict is
                     ClaimVerdict.VerifiedEquivalent or ClaimVerdict.ApprovedDeviation))
            foreach (string id in claim.VerificationIds)
                if (!artifacts.ContainsKey(id))
                    result.Errors.Add($"{prefix}: claim {claim.Id} verification id does not resolve: {id}");

        foreach ((string checkpointId, AcceptanceCheckpoint check) in audit.Acceptance)
        {
            if (!RequiredAcceptanceChecks.Contains(checkpointId)) continue;
            if (check.ArtifactIds.Count == 0)
            {
                result.Errors.Add($"{prefix}: checkpoint {checkpointId} has no typed artifact ids");
                continue;
            }
            VerificationArtifact[] resolved = check.ArtifactIds
                .Where(artifacts.ContainsKey).Select(id => artifacts[id]).ToArray();
            foreach (string missing in check.ArtifactIds.Where(id => !artifacts.ContainsKey(id)))
                result.Errors.Add($"{prefix}: checkpoint {checkpointId} references missing artifact {missing}");
            foreach (VerificationArtifact artifact in resolved)
            {
                if (!artifact.CheckpointIds.Contains(checkpointId, StringComparer.Ordinal))
                    result.Errors.Add($"{prefix}: artifact {artifact.Id} does not declare checkpoint {checkpointId}");
                foreach (string traceId in artifact.TraceIds.Where(id => !expectedTraceIds.Contains(id)))
                    result.Errors.Add($"{prefix}: artifact {artifact.Id} covers unrelated trace {traceId}");
            }
            ValidateArtifactKinds(prefix, checkpointId, check, resolved, result);
        }
    }

    private static void ValidateArtifactResult(string evidenceRoot,
        IReadOnlyDictionary<string, PacketEvidenceFile> indexed, string prefix, SnapshotPair pair,
        ReviewPacket packet, PacketAudit audit, VerificationArtifact artifact,
        PacketWorkspaceValidation result)
    {
        if (!IsEvidencePath(artifact.ResultFile) ||
            !artifact.Files.Contains(artifact.ResultFile, StringComparer.OrdinalIgnoreCase))
        {
            result.Errors.Add($"{prefix}: artifact {artifact.Id} has no packet-local result file in its file set");
            return;
        }
        string resultRelative = artifact.ResultFile["evidence/".Length..].Replace('\\', '/');
        if (!indexed.ContainsKey(resultRelative))
        {
            result.Errors.Add($"{prefix}: artifact {artifact.Id} result file is missing or unsealed");
            return;
        }
        string resultPath;
        try { resultPath = ResolveEvidencePath(evidenceRoot, resultRelative); }
        catch (InvalidDataException ex) { result.Errors.Add($"{prefix}: {ex.Message}"); return; }
        VerificationResultEnvelope envelope;
        try { envelope = JsonStore.Read<VerificationResultEnvelope>(resultPath); }
        catch (Exception ex)
        {
            result.Errors.Add($"{prefix}: artifact {artifact.Id} result file is invalid: {ex.Message}");
            return;
        }
        if (envelope.Kind != "snapshot-parity-verification-result")
            result.Errors.Add($"{prefix}: artifact {artifact.Id} result file has the wrong kind");
        if (envelope.PairId != pair.Id || envelope.PacketId != packet.Id ||
            envelope.ReferenceSnapshotSha256 != pair.ReferenceSnapshotSha256 ||
            envelope.TargetSnapshotSha256 != pair.TargetSnapshotSha256)
            result.Errors.Add($"{prefix}: artifact {artifact.Id} result is not pinned to this pair and snapshots");
        if (envelope.ArtifactId != artifact.Id || envelope.ArtifactKind != artifact.Kind ||
            envelope.Result != artifact.Result || envelope.ScenarioId != artifact.ScenarioId ||
            envelope.Provenance != artifact.Provenance || envelope.ToolPath != artifact.ToolPath ||
            !envelope.ToolSha256.Equals(artifact.ToolSha256, StringComparison.OrdinalIgnoreCase) ||
            envelope.AssertionsPassed != artifact.AssertionsPassed ||
            envelope.AssertionsFailed != artifact.AssertionsFailed)
            result.Errors.Add($"{prefix}: artifact {artifact.Id} result does not match its typed artifact declaration");
        if (envelope.Result != VerificationArtifactResult.Pass || envelope.AssertionsPassed <= 0 ||
            envelope.AssertionsFailed != 0 || envelope.GeneratedUtc == default)
            result.Errors.Add($"{prefix}: artifact {artifact.Id} result is not a dated passing assertion result");
        if (artifact.Kind == VerificationArtifactKind.ScreenshotReview &&
            string.IsNullOrWhiteSpace(envelope.Reviewer))
            result.Errors.Add($"{prefix}: screenshot review artifact {artifact.Id} has no reviewer");

        var envelopeFiles = new Dictionary<string, VerificationResultFile>(StringComparer.OrdinalIgnoreCase);
        foreach (VerificationResultFile file in envelope.Files)
        {
            if (!IsEvidencePath(file.Path) || !envelopeFiles.TryAdd(file.Path, file))
            {
                result.Errors.Add($"{prefix}: artifact {artifact.Id} result lists an invalid or duplicate evidence file");
                continue;
            }
            string relative = file.Path["evidence/".Length..].Replace('\\', '/');
            if (!indexed.TryGetValue(relative, out PacketEvidenceFile? sealedFile) ||
                !sealedFile.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                result.Errors.Add($"{prefix}: artifact {artifact.Id} result file hash is absent or stale: {file.Path}");
        }
        string[] rawArtifactFiles = artifact.Files
            .Where(path => !path.Equals(artifact.ResultFile, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rawArtifactFiles.Length == 0)
            result.Errors.Add($"{prefix}: artifact {artifact.Id} has no raw evidence behind its result envelope");
        foreach (string file in rawArtifactFiles.Where(path => !envelopeFiles.ContainsKey(path)))
            result.Errors.Add($"{prefix}: artifact {artifact.Id} result does not hash-pin {file}");
        foreach (string file in envelopeFiles.Keys.Where(path =>
                     !rawArtifactFiles.Contains(path, StringComparer.OrdinalIgnoreCase)))
            result.Errors.Add($"{prefix}: artifact {artifact.Id} result hashes undeclared file {file}");

        string[] allowedProofKinds = artifact.Kind switch
        {
            VerificationArtifactKind.SourceEvidence => ["source-evidence"],
            VerificationArtifactKind.Build => ["build-result"],
            VerificationArtifactKind.DeterministicTest => ["deterministic-test"],
            VerificationArtifactKind.LiveProtocol => ["live-protocol"],
            VerificationArtifactKind.UiDraw => ["ui-draw"],
            VerificationArtifactKind.UiDiff => ["ui-mechanical-diff", "ui-containment"],
            VerificationArtifactKind.ScreenshotReview => ["screenshot-review"],
            VerificationArtifactKind.InputTrace => ["input-trace"],
            VerificationArtifactKind.StateTrace => ["state-trace"],
            VerificationArtifactKind.WireTrace => ["wire-trace"],
            VerificationArtifactKind.SoundTrace => ["sound-trace"],
            _ => [],
        };
        if (!allowedProofKinds.Contains(envelope.ProofKind, StringComparer.Ordinal))
        {
            result.Errors.Add($"{prefix}: artifact {artifact.Id} has unsupported proof kind {envelope.ProofKind}");
            return;
        }
        if (!IsEvidencePath(envelope.ProofFile) ||
            !envelopeFiles.ContainsKey(envelope.ProofFile) ||
            !artifact.Files.Contains(envelope.ProofFile, StringComparer.OrdinalIgnoreCase))
        {
            result.Errors.Add($"{prefix}: artifact {artifact.Id} proof file is not declared and hash-pinned");
            return;
        }
        string proofRelative = envelope.ProofFile["evidence/".Length..].Replace('\\', '/');
        string proofPath;
        try { proofPath = ResolveEvidencePath(evidenceRoot, proofRelative); }
        catch (InvalidDataException ex) { result.Errors.Add($"{prefix}: {ex.Message}"); return; }
        JsonDocument proof;
        try { proof = JsonDocument.Parse(File.ReadAllText(proofPath)); }
        catch (Exception ex)
        {
            result.Errors.Add($"{prefix}: artifact {artifact.Id} proof is not structured JSON: {ex.Message}");
            return;
        }
        using (proof)
        {
            JsonElement root = proof.RootElement;
            if (!TryString(root, "kind", out string proofKind) || proofKind != envelope.ProofKind ||
                !TryString(root, "result", out string proofResult) || proofResult != "PASS")
                result.Errors.Add($"{prefix}: artifact {artifact.Id} proof kind/result was not parsed as PASS");
            if (!TryInt(root, "assertionsPassed", out int passed) || passed != artifact.AssertionsPassed ||
                !TryInt(root, "assertionsFailed", out int failed) || failed != 0)
                result.Errors.Add($"{prefix}: artifact {artifact.Id} proof assertion counts do not match");
            if (artifact.Kind == VerificationArtifactKind.Build &&
                (!TryInt(root, "exitCode", out int exitCode) || exitCode != 0))
                result.Errors.Add($"{prefix}: build artifact {artifact.Id} has no parsed zero exit code");
            if (artifact.Kind == VerificationArtifactKind.LiveProtocol &&
                (!TryBool(root, "inWorld", out bool inWorld) || !inWorld ||
                 !TryString(root, "networkState", out string networkState) || networkState != "InWorld"))
                result.Errors.Add($"{prefix}: live artifact {artifact.Id} does not prove a server-connected InWorld run");
            if (envelope.ProofKind == "ui-mechanical-diff")
                ValidateUiMechanicalDiff(evidenceRoot, indexed, prefix, packet, audit, artifact,
                    proofRelative, root, result);
            else if (envelope.ProofKind == "ui-containment")
                ValidateUiContainment(evidenceRoot, indexed, prefix, artifact, proofRelative, root, result);
        }
    }

    private static void ValidateUiMechanicalDiff(string evidenceRoot,
        IReadOnlyDictionary<string, PacketEvidenceFile> indexed, string prefix, ReviewPacket packet,
        PacketAudit audit, VerificationArtifact artifact, string manifestRelative, JsonElement root,
        PacketWorkspaceValidation result)
    {
        if (!TryInt(root, "mechanicalDeltas", out int deltas) || deltas != 0 ||
            !TryInt(root, "referenceRows", out int referenceRows) || referenceRows <= 0 ||
            !TryInt(root, "instrumentedRows", out int instrumented) ||
            !TryInt(root, "notDrawnRows", out int notDrawn) || instrumented + notDrawn != referenceRows)
            result.Errors.Add($"{prefix}: UI diff artifact {artifact.Id} has deltas or incomplete reference coverage");
        if (!TryInt(root, "verdictRows", out int verdictRows) || verdictRows != artifact.AssertionsPassed)
            result.Errors.Add($"{prefix}: UI diff artifact {artifact.Id} assertion count is not its verdict row count");

        string? output = ValidateEmbeddedFile(root, "output", evidenceRoot, indexed, prefix,
            artifact, manifestRelative, result);
        ValidateEmbeddedFile(root, "expected", evidenceRoot, indexed, prefix,
            artifact, manifestRelative, result);
        ValidateEmbeddedFile(root, "actual", evidenceRoot, indexed, prefix,
            artifact, manifestRelative, result);
        ValidateEmbeddedFile(root, "selection", evidenceRoot, indexed, prefix,
            artifact, manifestRelative, result);
        if (root.TryGetProperty("adjudications", out JsonElement adjudications) &&
            adjudications.ValueKind != JsonValueKind.Null)
            ValidateEmbeddedFile(root, "adjudications", evidenceRoot, indexed, prefix,
                artifact, manifestRelative, result);
        ValidateEmbeddedFile(root, "tool", evidenceRoot, indexed, prefix,
            artifact, manifestRelative, result);
        if (output is null) return;

        string outputPath = ResolveEvidencePath(evidenceRoot, output);
        string[] lines = File.ReadAllLines(outputPath);
        const string header = "panel,element,field,expected,actual,verdict,decisionId,reason";
        if (lines.Length < 2 || lines[0] != header)
        {
            result.Errors.Add($"{prefix}: UI diff artifact {artifact.Id} has an invalid or empty verdict CSV");
            return;
        }
        string[] allowedDecisionIds = packet.Claims
            .Where(claim => claim.Verdict == ClaimVerdict.ApprovedDeviation &&
                            !string.IsNullOrWhiteSpace(claim.DecisionId))
            .Select(claim => claim.DecisionId!)
            .Append(audit.DecisionId ?? "")
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        int preserved = 0;
        foreach (string line in lines.Skip(1))
        {
            string[] fields;
            try { fields = ParseCsv(line).ToArray(); }
            catch (InvalidDataException ex)
            {
                result.Errors.Add($"{prefix}: UI diff artifact {artifact.Id} CSV is invalid: {ex.Message}");
                return;
            }
            if (fields.Length != 8)
            {
                result.Errors.Add($"{prefix}: UI diff artifact {artifact.Id} CSV row has {fields.Length} fields");
                return;
            }
            if (fields[5] == "DELTA")
                result.Errors.Add($"{prefix}: UI diff artifact {artifact.Id} contains an unresolved delta");
            if (fields[5] != "PRESERVED-DIFFERENCE") continue;
            preserved++;
            if (string.IsNullOrWhiteSpace(fields[6]) || !allowedDecisionIds.Contains(fields[6], StringComparer.Ordinal))
                result.Errors.Add($"{prefix}: UI diff artifact {artifact.Id} uses unapproved decision {fields[6]}");
        }
        if (!TryInt(root, "preservedDifferences", out int declaredPreserved) || declaredPreserved != preserved)
            result.Errors.Add($"{prefix}: UI diff artifact {artifact.Id} preserved-difference count does not match its CSV");
    }

    private static void ValidateUiContainment(string evidenceRoot,
        IReadOnlyDictionary<string, PacketEvidenceFile> indexed, string prefix, VerificationArtifact artifact,
        string manifestRelative, JsonElement root, PacketWorkspaceValidation result)
    {
        if (!TryInt(root, "insideChanged", out int insideChanged) || insideChanged <= 0 ||
            !TryInt(root, "outsideChanged", out int outsideChanged) || outsideChanged != 0)
            result.Errors.Add($"{prefix}: containment artifact {artifact.Id} does not prove visible pixels stayed inside");
        ValidateEmbeddedFile(root, "visible", evidenceRoot, indexed, prefix,
            artifact, manifestRelative, result);
        ValidateEmbeddedFile(root, "hidden", evidenceRoot, indexed, prefix,
            artifact, manifestRelative, result);
        if (root.TryGetProperty("diffImage", out JsonElement diffImage) &&
            diffImage.ValueKind == JsonValueKind.Object)
            ValidateEmbeddedFile(root, "diffImage", evidenceRoot, indexed, prefix,
                artifact, manifestRelative, result);
    }

    private static string? ValidateEmbeddedFile(JsonElement root, string property, string evidenceRoot,
        IReadOnlyDictionary<string, PacketEvidenceFile> indexed, string prefix, VerificationArtifact artifact,
        string manifestRelative, PacketWorkspaceValidation result)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Object ||
            !TryString(value, "path", out string path) || !TryString(value, "sha256", out string sha))
        {
            result.Errors.Add($"{prefix}: artifact {artifact.Id} proof has no hashed {property} file");
            return null;
        }
        string baseDirectory = Path.GetDirectoryName(manifestRelative)?.Replace('\\', '/') ?? "";
        string combined = Path.GetFullPath(Path.Combine(evidenceRoot, baseDirectory,
            path.Replace('/', Path.DirectorySeparatorChar)));
        string evidenceFull = Path.GetFullPath(evidenceRoot).TrimEnd(Path.DirectorySeparatorChar) +
                              Path.DirectorySeparatorChar;
        if (!combined.StartsWith(evidenceFull, StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add($"{prefix}: artifact {artifact.Id} proof {property} escapes packet evidence");
            return null;
        }
        string relative = Path.GetRelativePath(evidenceRoot, combined).Replace('\\', '/');
        string declared = "evidence/" + relative;
        if (!artifact.Files.Contains(declared, StringComparer.OrdinalIgnoreCase) ||
            !indexed.TryGetValue(relative, out PacketEvidenceFile? file) ||
            !file.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
            result.Errors.Add($"{prefix}: artifact {artifact.Id} proof {property} file is missing, undeclared, or stale");
        return relative;
    }

    private static bool TryString(JsonElement root, string property, out string value)
    {
        value = "";
        return root.TryGetProperty(property, out JsonElement element) &&
               element.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value = element.GetString() ?? "");
    }

    private static bool TryInt(JsonElement root, string property, out int value)
    {
        value = 0;
        return root.TryGetProperty(property, out JsonElement element) && element.TryGetInt32(out value);
    }

    private static bool TryBool(JsonElement root, string property, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(property, out JsonElement element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = element.GetBoolean();
        return true;
    }

    private static IEnumerable<string> ParseCsv(string line)
    {
        var value = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                yield return value.ToString();
                value.Clear();
            }
            else value.Append(c);
        }
        if (quoted) throw new InvalidDataException("unterminated quoted CSV field");
        yield return value.ToString();
    }

    private static void ValidateArtifactKinds(string prefix, string checkpointId, AcceptanceCheckpoint check,
        IReadOnlyList<VerificationArtifact> artifacts, PacketWorkspaceValidation result)
    {
        if (check.Result == AcceptanceResult.NotApplicable)
        {
            if (!artifacts.Any(a => a.Kind is VerificationArtifactKind.SourceEvidence or
                                    VerificationArtifactKind.DeterministicTest))
                result.Errors.Add($"{prefix}: N/A checkpoint {checkpointId} lacks applicability evidence");
            return;
        }
        VerificationArtifactKind[] allowed = checkpointId switch
        {
            "reference-dependency-closure" => [VerificationArtifactKind.SourceEvidence],
            "state-reachability" => [VerificationArtifactKind.StateTrace, VerificationArtifactKind.LiveProtocol],
            "runtime-wire-contract" => [VerificationArtifactKind.WireTrace, VerificationArtifactKind.LiveProtocol],
            "input-modifier-contract" => [VerificationArtifactKind.InputTrace],
            "visual-geometry-anchors" => [VerificationArtifactKind.UiDiff],
            "visual-containment-cropping" => [VerificationArtifactKind.UiDiff, VerificationArtifactKind.ScreenshotReview],
            "texture-coordinates-layering" => [VerificationArtifactKind.UiDiff, VerificationArtifactKind.UiDraw,
                VerificationArtifactKind.ScreenshotReview],
            "interaction-bounds-states" => [VerificationArtifactKind.InputTrace, VerificationArtifactKind.UiDiff],
            "dynamic-content-boundaries" => [VerificationArtifactKind.LiveProtocol, VerificationArtifactKind.StateTrace,
                VerificationArtifactKind.ScreenshotReview],
            "audio-count-timing" => [VerificationArtifactKind.SoundTrace],
            "negative-behavior" => [VerificationArtifactKind.LiveProtocol, VerificationArtifactKind.InputTrace,
                VerificationArtifactKind.StateTrace, VerificationArtifactKind.WireTrace, VerificationArtifactKind.SoundTrace],
            "preserved-difference-regression" => [VerificationArtifactKind.LiveProtocol,
                VerificationArtifactKind.StateTrace, VerificationArtifactKind.ScreenshotReview,
                VerificationArtifactKind.DeterministicTest],
            "deterministic-verification" => [VerificationArtifactKind.DeterministicTest],
            "live-visual-verification" => [VerificationArtifactKind.UiDiff, VerificationArtifactKind.ScreenshotReview],
            _ => [],
        };
        if (!artifacts.Any(a => allowed.Contains(a.Kind)))
            result.Errors.Add($"{prefix}: checkpoint {checkpointId} lacks an artifact of the required kind");
        if (checkpointId is "visual-containment-cropping" or "live-visual-verification")
        {
            if (!artifacts.Any(a => a.Kind == VerificationArtifactKind.UiDiff) ||
                !artifacts.Any(a => a.Kind == VerificationArtifactKind.ScreenshotReview))
                result.Errors.Add($"{prefix}: checkpoint {checkpointId} requires both UI diff and screenshot review");
        }
        if (checkpointId is "state-reachability" or "runtime-wire-contract" or "input-modifier-contract" or
            "interaction-bounds-states" or "dynamic-content-boundaries" or "audio-count-timing" or
            "negative-behavior" or "live-visual-verification")
            foreach (VerificationArtifact artifact in artifacts.Where(a => a.Provenance == FixtureProvenance.SyntheticStage))
                result.Errors.Add($"{prefix}: synthetic artifact {artifact.Id} cannot prove {checkpointId}");
    }

    private static void RequireSelfContained(string prefix, string section, IEnumerable<string> values,
        PacketWorkspaceValidation result)
    {
        if (!values.Any(IsEvidencePath))
            result.Errors.Add($"{prefix}: verified packet {section} section has no self-contained evidence path");
    }

    private static bool IsEvidencePath(string value) =>
        value.StartsWith("evidence/", StringComparison.OrdinalIgnoreCase);

    private static string ResolveEvidencePath(string evidenceRoot, string relative)
    {
        string root = Path.GetFullPath(evidenceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"evidence path escapes packet folder: {relative}");
        return full;
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
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
        b.AppendLine("### Typed verification artifacts").AppendLine();
        AppendList(b, audit.VerificationArtifactIds);
        b.AppendLine("## Per-trace change and preservation map").AppendLine();
        b.AppendLine("| Trace | Classification | Policy | Finding | Changed symbols/hunks | Preserved symbols | Evidence |");
        b.AppendLine("|---|---|---|---|---|---|---|");
        foreach (TraceDisposition disposition in audit.TraceDispositions)
        {
            b.AppendLine($"| `{Escape(disposition.TraceId)}` | {disposition.Classification} | {disposition.WritePolicy} | {Escape(disposition.Summary)} | " +
                $"{Join(disposition.ChangedSymbols)} | {Join(disposition.PreservedSymbols)} | {Join(disposition.Evidence)} |");
        }
        if (audit.TraceDispositions.Count == 0) b.AppendLine("| — | — | — | Not yet mapped | — | — | — |");
        b.AppendLine();
        b.AppendLine("## Acceptance checkpoints").AppendLine();
        b.AppendLine("A packet cannot be verified from outer dimensions or a successful build alone. Every checkpoint must pass or carry an evidence-backed not-applicable ruling.").AppendLine();
        b.AppendLine("| Checkpoint | Result | Finding | Evidence | Artifact IDs |");
        b.AppendLine("|---|---|---|---|---|");
        foreach (string id in RequiredAcceptanceChecks)
        {
            if (!audit.Acceptance.TryGetValue(id, out AcceptanceCheckpoint? check))
            {
                b.AppendLine($"| `{id}` | Missing | — | — | — |");
                continue;
            }
            string evidence = check.Evidence.Count == 0 ? "—" : string.Join("<br>", check.Evidence.Select(Escape));
            b.AppendLine($"| `{id}` | {check.Result} | {Escape(check.Summary)} | {evidence} | {Join(check.ArtifactIds)} |");
        }
        b.AppendLine();
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

    private static string Join(IEnumerable<string> values)
    {
        string[] items = values.Select(Escape).ToArray();
        return items.Length == 0 ? "—" : string.Join("<br>", items);
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
