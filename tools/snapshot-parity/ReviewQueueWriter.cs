using System.Security.Cryptography;
using System.Text;

namespace SnapshotParity;

internal static class ReviewQueueWriter
{
    private const int MaximumFactsPerPacket = 200;

    public static ReviewQueue Build(SnapshotPair pair, IReadOnlyList<BehaviorTrace> traces,
        IReadOnlyList<ComparisonClaim> claims)
    {
        var traceFacts = traces.ToDictionary(t => t.Id,
            t => t.ReferenceFacts.Select(f => f.Id).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var claimFacts = claims.ToDictionary(c => c.Id, c => c.ReferenceFacts.Select(f => f.Id)
            .Concat(c.TraceIds.Where(traceFacts.ContainsKey).SelectMany(id => traceFacts[id]))
            .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var covered = claims.Where(c => c.Verdict.IsTerminal())
            .SelectMany(c => claimFacts[c.Id])
            .ToHashSet(StringComparer.Ordinal);
        ComparisonClaim[] pendingClaims = claims.Where(c => !c.Verdict.IsTerminal()).ToArray();
        var pendingIds = pendingClaims.SelectMany(c => claimFacts[c.Id])
            .Where(id => !covered.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var candidateIds = pair.Candidates.Select(c => c.ReferenceFactId).ToHashSet(StringComparer.Ordinal);
        SourceFact[] unresolved = pair.ReferenceFacts
            .Where(f => f.ReviewRequired && !covered.Contains(f.Id))
            .ToArray();
        var queue = new ReviewQueue
        {
            PairId = pair.Id,
            GeneratedUtc = DateTimeOffset.UtcNow,
            CoveredReferenceFacts = pair.ReferenceFacts.Count(f => f.ReviewRequired && covered.Contains(f.Id)),
            UnreviewedReferenceFacts = unresolved.Length,
        };
        var packets = new List<ReviewPacket>();
        // Chunk the immutable required surface before removing covered facts. This makes a packet's
        // pair/source/chunk identity stable as individual facts move through review and verification.
        foreach (IGrouping<string, SourceFact> file in pair.ReferenceFacts
                     .Where(f => f.ReviewRequired)
                     .GroupBy(f => f.Path, StringComparer.Ordinal))
        {
            SourceFact[] allFileFacts = file.OrderBy(f => f.Line).ThenBy(f => f.Id, StringComparer.Ordinal).ToArray();
            for (int offset = 0, chunk = 1; offset < allFileFacts.Length;
                 offset += MaximumFactsPerPacket, chunk++)
            {
                SourceFact[] fixedSlice = allFileFacts.Skip(offset).Take(MaximumFactsPerPacket).ToArray();
                SourceFact[] slice = fixedSlice.Where(f => !covered.Contains(f.Id)).ToArray();
                var sliceIds = slice.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
                var fixedSliceIds = fixedSlice.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
                bool implementation = slice.Any(f => pendingIds.Contains(f.Id));
                string seed = $"{pair.Id}\0{file.Key}\0{chunk}";
                packets.Add(new ReviewPacket
                {
                    Id = $"packet-{Hash(seed)[..16]}",
                    WorkKind = implementation ? "implementation" : slice.Length == 0 ? "completed" : "review",
                    SourcePath = file.Key,
                    Chunk = chunk,
                    Surfaces = (slice.Length == 0 ? fixedSlice : slice)
                        .Select(f => f.Surface).Distinct(StringComparer.Ordinal).Order().ToList(),
                    ReferenceFactIds = fixedSlice.Select(f => f.Id).ToList(),
                    UnresolvedReferenceFactIds = slice.Select(f => f.Id).ToList(),
                    MechanicalCandidateCount = fixedSlice.Count(f => candidateIds.Contains(f.Id)),
                    Claims = claims.Where(c => claimFacts[c.Id].Overlaps(fixedSliceIds)).Select(c => new PacketClaim
                    {
                        Id = c.Id, TraceIds = [.. c.TraceIds], Verdict = c.Verdict, Summary = c.Summary,
                        Behavior = c.Behavior, NegativeBehavior = c.NegativeBehavior,
                        VerificationIds = [.. c.VerificationIds],
                        DecisionId = c.DecisionId,
                    }).ToList(),
                });
            }
        }
        var sourcePriority = packets.GroupBy(p => p.SourcePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key,
                g => g.SelectMany(p => p.Surfaces).Select(Priority).DefaultIfEmpty(9).Min(),
                StringComparer.Ordinal);
        queue.Packets = packets
            .OrderBy(p => p.WorkKind == "implementation" ? 0 : p.WorkKind == "review" ? 1 : 2)
            .ThenBy(p => sourcePriority[p.SourcePath])
            .ThenBy(p => p.SourcePath, StringComparer.Ordinal)
            .ThenBy(p => p.Chunk)
            .ToList();
        for (int i = 0; i < queue.Packets.Count; i++) queue.Packets[i].Sequence = i + 1;
        return queue;
    }

    public static void WritePacket(string path, SnapshotPair pair, ReviewQueue queue, string packetId)
    {
        if (queue.PairId != pair.Id) throw new InvalidDataException("review queue belongs to a different snapshot pair");
        ReviewPacket packet = queue.Packets.SingleOrDefault(p => p.Id == packetId)
            ?? throw new InvalidDataException($"review packet {packetId} not found");
        var reference = pair.ReferenceFacts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var target = pair.TargetFacts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var candidates = pair.Candidates.GroupBy(c => c.ReferenceFactId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key,
                g => g.SelectMany(c => c.TargetFactIds).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var b = new StringBuilder();
        b.AppendLine($"# Snapshot parity review packet {packet.Id}").AppendLine();
        b.AppendLine($"Pair: `{pair.Id}`  ");
        b.AppendLine($"Sequence: {packet.Sequence}/{queue.Packets.Count}  ");
        b.AppendLine($"Work kind: `{packet.WorkKind}`  ");
        b.AppendLine($"Reference source: `{packet.SourcePath}`  ");
        b.AppendLine($"Facts: {packet.ReferenceFactIds.Count}; unresolved: {packet.UnresolvedReferenceFactIds.Count}; mechanical candidates: {packet.MechanicalCandidateCount}").AppendLine();
        b.AppendLine("## Audit contract").AppendLine();
        if (packet.WorkKind == "implementation")
            b.AppendLine("This source chunk contains nonterminal implementation claims. Treat those claims as implementation and verification obligations, and behaviorally review any remaining unclaimed facts in the same chunk. Implement only facts classified missing or reproduced-broken; preserve present-but-different MSUI behavior. Run deterministic and live verification appropriate to the surface, capture a new MSUI snapshot, migrate only still-valid evidence, and re-review each claim. Do not start another packet while this implementation packet remains nonterminal.").AppendLine();
        else if (packet.WorkKind == "review")
            b.AppendLine("Read the complete reference file and follow every imported, called, registered, or data dependency needed to understand these facts. Do not infer equivalence from names. Emit atomic behavior traces with trigger, preconditions, positive behavior, negative behavior, and hash-pinned facts. Then emit one comparison claim per trace, or explicit classification claims for non-runtime/support/test facts. Implement and verify every gap or divergence before assigning a terminal verdict. Nonterminal claims remain in the implementation queue. If a behavior reaches facts in another packet, cite them too; packet boundaries are workload boundaries, not semantic boundaries.").AppendLine();
        else
            b.AppendLine("Every required fact in this fixed source chunk has a terminal reviewed disposition. The packet remains materialized as immutable before/change/after history and must be re-opened if a future snapshot invalidates its evidence.").AppendLine();
        if (packet.Claims.Count > 0)
        {
            b.AppendLine("## Current implementation obligations").AppendLine();
            foreach (PacketClaim claim in packet.Claims)
            {
                b.AppendLine($"### `{claim.Id}` — {claim.Verdict}").AppendLine();
                b.AppendLine(claim.Summary).AppendLine();
                b.AppendLine($"- Positive behavior: {claim.Behavior}");
                b.AppendLine($"- Negative behavior: {claim.NegativeBehavior}");
                b.AppendLine($"- Existing verification: {(claim.VerificationIds.Count == 0 ? "none" : string.Join(", ", claim.VerificationIds.Select(v => $"`{v}`")))}").AppendLine();
            }
        }
        b.AppendLine("## Reference facts").AppendLine();
        b.AppendLine("| Fact | Location | Kind | Surface | Name | Evidence SHA-256 | Evidence |");
        b.AppendLine("|---|---|---|---|---|---|---|");
        foreach (string id in packet.ReferenceFactIds)
        {
            SourceFact fact = reference[id];
            b.AppendLine($"| `{fact.Id}` | `{Escape(fact.Path)}:{fact.Line}` | {Escape(fact.Kind)} | {Escape(fact.Surface)} | {Escape(fact.Name)} | `{fact.EvidenceSha256}` | {Escape(fact.Evidence)} |");
        }
        b.AppendLine().AppendLine("## Mechanical MSUI candidates").AppendLine();
        b.AppendLine("> These rows are navigation hints only. They are not parity evidence.").AppendLine();
        b.AppendLine("| Reference fact | Target fact | Target location | Kind | Name | Evidence SHA-256 |");
        b.AppendLine("|---|---|---|---|---|---|");
        foreach (string id in packet.ReferenceFactIds)
        {
            if (!candidates.TryGetValue(id, out string[]? targetIds)) continue;
            foreach (string targetId in targetIds)
            {
                SourceFact fact = target[targetId];
                b.AppendLine($"| `{id}` | `{fact.Id}` | `{Escape(fact.Path)}:{fact.Line}` | {Escape(fact.Kind)} | {Escape(fact.Name)} | `{fact.EvidenceSha256}` |");
            }
        }
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, b.ToString(), new UTF8Encoding(false));
    }

    private static int Priority(string surface) => surface switch
    {
        "protocol" => 0, "state" => 1, "data" => 2, "runtime" => 3, "events" => 4,
        "input-events" => 5, "presentation" => 6, "rendering" => 7, "verification" => 8,
        _ => 9,
    };
    private static string Hash(string value) => SnapshotCapture.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
