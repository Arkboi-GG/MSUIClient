using System.Security.Cryptography;
using System.Text;

namespace SnapshotParity;

internal static class ComparisonEngine
{
    private static readonly HashSet<string> CandidateKinds = new(StringComparer.Ordinal)
    {
        "opcode", "update-field", "event", "asset", "dbc", "ui-element", "ui-handler",
        "shader-entry", "shader-binding",
    };

    public static SnapshotPair Compare(SnapshotManifest referenceManifest, FactIndex reference,
        SnapshotManifest targetManifest, FactIndex target)
    {
        CheckIndex(referenceManifest, reference);
        CheckIndex(targetManifest, target);
        string pairHash = Hash($"{referenceManifest.AggregateSha256}\0{targetManifest.AggregateSha256}");
        var pair = new SnapshotPair
        {
            Id = $"pair-{pairHash[..16]}",
            ComparedUtc = DateTimeOffset.UtcNow,
            ReferenceSnapshotId = referenceManifest.Id,
            ReferenceSnapshotSha256 = referenceManifest.AggregateSha256,
            TargetSnapshotId = targetManifest.Id,
            TargetSnapshotSha256 = targetManifest.AggregateSha256,
            ReferenceFacts = reference.Facts,
            TargetFacts = target.Facts,
        };
        var targetByKey = target.Facts.Where(f => CandidateKinds.Contains(f.Kind))
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(f => f.Id).Take(20).ToList(),
                StringComparer.OrdinalIgnoreCase);
        foreach (SourceFact fact in reference.Facts.Where(f => CandidateKinds.Contains(f.Kind)))
        {
            if (!targetByKey.TryGetValue(Key(fact), out List<string>? targets)) continue;
            pair.Candidates.Add(new CandidateMapping
            {
                ReferenceFactId = fact.Id,
                TargetFactIds = targets,
                Basis = $"exact normalized {fact.Kind} identity",
            });
        }
        return pair;
    }

    public static ValidationResult Validate(SnapshotPair pair, IReadOnlyList<BehaviorTrace> traces,
        IReadOnlyList<ComparisonClaim> claims)
    {
        var result = new ValidationResult();
        var reference = pair.ReferenceFacts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var target = pair.TargetFacts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var traceById = new Dictionary<string, BehaviorTrace>(StringComparer.Ordinal);
        foreach (BehaviorTrace trace in traces)
        {
            if (trace.Id.Length == 0 || !traceById.TryAdd(trace.Id, trace))
            {
                result.Errors.Add("trace ids must be non-empty and unique");
                continue;
            }
            if (!trace.PairId.Equals(pair.Id, StringComparison.Ordinal))
                result.Errors.Add($"trace {trace.Id} names stale pair {trace.PairId}");
            if (trace.ReferenceFacts.Count == 0) result.Errors.Add($"trace {trace.Id} has no reference facts");
            if (string.IsNullOrWhiteSpace(trace.Name)) result.Errors.Add($"trace {trace.Id} has no name");
            if (string.IsNullOrWhiteSpace(trace.Trigger)) result.Errors.Add($"trace {trace.Id} has no trigger");
            if (string.IsNullOrWhiteSpace(trace.Preconditions)) result.Errors.Add($"trace {trace.Id} has no preconditions");
            if (string.IsNullOrWhiteSpace(trace.Behavior)) result.Errors.Add($"trace {trace.Id} has no behavior");
            if (string.IsNullOrWhiteSpace(trace.NegativeBehavior)) result.Errors.Add($"trace {trace.Id} has no negative behavior");
            foreach (FactReference factRef in trace.ReferenceFacts)
                ValidateFactReference(factRef, reference, trace.Id, "trace", covered, result);
        }
        var claimIds = new HashSet<string>(StringComparer.Ordinal);
        var claimedTraceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ComparisonClaim claim in claims)
        {
            if (claim.Id.Length == 0 || !claimIds.Add(claim.Id))
                result.Errors.Add("claim ids must be non-empty and unique");
            if (!claim.PairId.Equals(pair.Id, StringComparison.Ordinal))
                result.Errors.Add($"claim {claim.Id} names stale pair {claim.PairId}");
            if (claim.ReferenceFacts.Count == 0 && claim.TraceIds.Count == 0)
                result.Errors.Add($"claim {claim.Id} has no reference facts or traces");
            foreach (string traceId in claim.TraceIds)
            {
                if (!traceById.ContainsKey(traceId)) result.Errors.Add($"claim {claim.Id} references missing trace {traceId}");
                else if (!claimedTraceIds.Add(traceId)) result.Warnings.Add($"trace {traceId} appears in multiple claims");
            }
            foreach (FactReference factRef in claim.ReferenceFacts)
                ValidateFactReference(factRef, reference, claim.Id, "claim", covered, result);
            foreach (FactReference factRef in claim.TargetFacts)
            {
                if (!target.TryGetValue(factRef.Id, out SourceFact? fact))
                {
                    result.Errors.Add($"claim {claim.Id} references missing target fact {factRef.Id}");
                    continue;
                }
                if (!fact.EvidenceSha256.Equals(factRef.EvidenceSha256, StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add($"claim {claim.Id} has stale target evidence {factRef.Id}");
                if (string.IsNullOrWhiteSpace(factRef.FileSha256) ||
                    !fact.FileSha256.Equals(factRef.FileSha256, StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add($"claim {claim.Id} has stale target file evidence {factRef.Id}");
            }
            ValidateVerdict(claim, result);
        }
        foreach (string traceId in traceById.Keys.Where(id => !claimedTraceIds.Contains(id)).Take(200))
            result.Errors.Add($"reviewed trace {traceId} has no comparison claim");
        if (traceById.Count - claimedTraceIds.Count > 200)
            result.Errors.Add($"...and {traceById.Count - claimedTraceIds.Count - 200} more unclaimed traces");
        SourceFact[] required = pair.ReferenceFacts.Where(f => f.ReviewRequired).ToArray();
        result.RequiredReferenceFacts = required.Length;
        result.ClaimedReferenceFacts = required.Count(f => covered.Contains(f.Id));
        foreach (SourceFact fact in required.Where(f => !covered.Contains(f.Id)).Take(200))
            result.Errors.Add($"unreviewed reference fact {fact.Id}: {fact.Path}:{fact.Line} {fact.Kind} {fact.Name}");
        if (required.Length - result.ClaimedReferenceFacts > 200)
            result.Errors.Add($"...and {required.Length - result.ClaimedReferenceFacts - 200} more unreviewed facts");
        return result;
    }

    private static void ValidateFactReference(FactReference factRef, IReadOnlyDictionary<string, SourceFact> facts,
        string ownerId, string ownerKind, HashSet<string> covered, ValidationResult result)
    {
        if (!facts.TryGetValue(factRef.Id, out SourceFact? fact))
        {
            result.Errors.Add($"{ownerKind} {ownerId} references missing reference fact {factRef.Id}");
            return;
        }
        if (!fact.EvidenceSha256.Equals(factRef.EvidenceSha256, StringComparison.OrdinalIgnoreCase))
            result.Errors.Add($"{ownerKind} {ownerId} has stale reference evidence {factRef.Id}");
        if (string.IsNullOrWhiteSpace(factRef.FileSha256) ||
            !fact.FileSha256.Equals(factRef.FileSha256, StringComparison.OrdinalIgnoreCase))
            result.Errors.Add($"{ownerKind} {ownerId} has stale reference file evidence {factRef.Id}");
        if (!covered.Add(factRef.Id))
            result.Warnings.Add($"reference fact {factRef.Id} is covered more than once");
    }

    private static void ValidateVerdict(ComparisonClaim claim, ValidationResult result)
    {
        if (claim.Verdict is ClaimVerdict.Unknown or ClaimVerdict.CandidateMatch or ClaimVerdict.Gap or
            ClaimVerdict.Divergent or ClaimVerdict.ImplementedUnverified)
            result.Errors.Add($"claim {claim.Id} has non-terminal verdict {claim.Verdict}");
        if (string.IsNullOrWhiteSpace(claim.Summary))
            result.Errors.Add($"claim {claim.Id} has no summary");
        if (claim.Verdict is ClaimVerdict.CandidateMatch or ClaimVerdict.Gap or ClaimVerdict.Divergent or
            ClaimVerdict.ImplementedUnverified or ClaimVerdict.VerifiedEquivalent or ClaimVerdict.ApprovedDeviation)
        {
            if (claim.TraceIds.Count == 0) result.Errors.Add($"claim {claim.Id} requires a reviewed behavior trace");
            if (claim.Verdict != ClaimVerdict.Gap && claim.TargetFacts.Count == 0)
                result.Errors.Add($"claim {claim.Id} requires target evidence");
            if (string.IsNullOrWhiteSpace(claim.Behavior)) result.Errors.Add($"claim {claim.Id} has no positive behavior");
            if (string.IsNullOrWhiteSpace(claim.NegativeBehavior)) result.Errors.Add($"claim {claim.Id} has no negative behavior");
        }
        if (claim.Verdict == ClaimVerdict.VerifiedEquivalent && claim.VerificationIds.Count == 0)
            result.Errors.Add($"claim {claim.Id} has no verification evidence");
        if (claim.Verdict == ClaimVerdict.ApprovedDeviation && string.IsNullOrWhiteSpace(claim.DecisionId))
            result.Errors.Add($"claim {claim.Id} has no approved decision id");
        if (claim.Verdict == ClaimVerdict.ApprovedDeviation && claim.VerificationIds.Count == 0)
            result.Errors.Add($"claim {claim.Id} has no deviation verification evidence");
    }

    private static void CheckIndex(SnapshotManifest manifest, FactIndex index)
    {
        if (manifest.Id != index.SnapshotId || manifest.AggregateSha256 != index.SnapshotSha256)
            throw new InvalidDataException($"fact index is stale for {manifest.Id}");
    }

    private static string Key(SourceFact fact) => $"{fact.Kind}\0{Normalize(fact.Name)}";
    private static string Normalize(string value) => value.Replace('\\', '/').Trim().ToLowerInvariant();
    private static string Hash(string value) => SnapshotCapture.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
