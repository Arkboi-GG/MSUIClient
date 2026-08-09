namespace SnapshotParity;

internal static class PairMigration
{
    public static MigrationReport Migrate(SnapshotPair oldPair, SnapshotPair newPair,
        IReadOnlyList<BehaviorTrace> traces, IReadOnlyList<ComparisonClaim> claims,
        string outTraces, string outClaims)
    {
        if (oldPair.ReferenceSnapshotSha256 != newPair.ReferenceSnapshotSha256)
            throw new InvalidDataException("reference snapshot changed; automatic migration is forbidden");

        Dictionary<string, SourceFact> oldReference = ById(oldPair.ReferenceFacts);
        Dictionary<string, SourceFact> oldTarget = ById(oldPair.TargetFacts);
        Dictionary<string, List<SourceFact>> newReference = ByIdentity(newPair.ReferenceFacts);
        Dictionary<string, List<SourceFact>> newTarget = ByIdentity(newPair.TargetFacts);
        var report = new MigrationReport
        {
            OldPairId = oldPair.Id,
            NewPairId = newPair.Id,
            MigratedUtc = DateTimeOffset.UtcNow,
            TraceCount = traces.Count,
            ClaimCount = claims.Count,
        };

        foreach (BehaviorTrace trace in traces)
        {
            RequirePair(trace.PairId, oldPair.Id, "trace", trace.Id);
            trace.ReferenceFacts = MapAll(trace.ReferenceFacts, oldReference, newReference,
                $"trace {trace.Id} reference");
            trace.PairId = newPair.Id;
        }

        foreach (ComparisonClaim claim in claims)
        {
            RequirePair(claim.PairId, oldPair.Id, "claim", claim.Id);
            claim.ReferenceFacts = MapAll(claim.ReferenceFacts, oldReference, newReference,
                $"claim {claim.Id} reference");

            var mappedTargets = new List<FactReference>();
            var unmapped = new List<string>();
            foreach (FactReference factRef in claim.TargetFacts)
            {
                if (!oldTarget.TryGetValue(factRef.Id, out SourceFact? oldFact) ||
                    !MatchesOldReference(oldFact, factRef))
                    throw new InvalidDataException($"claim {claim.Id} has stale old target evidence {factRef.Id}");
                if (!TryMap(oldFact, newTarget, out SourceFact? mapped)) unmapped.Add(factRef.Id);
                else mappedTargets.Add(Ref(mapped!));
            }

            ClaimVerdict oldVerdict = claim.Verdict;
            if (unmapped.Count != 0)
            {
                // A mixed old/new evidence set could imply equivalence that was never reviewed. Discard it all.
                claim.TargetFacts = [];
                if (claim.Verdict is ClaimVerdict.VerifiedEquivalent or ClaimVerdict.ApprovedDeviation)
                    claim.Verdict = ClaimVerdict.ImplementedUnverified;
                claim.VerificationIds = [];
                claim.Summary = $"[Snapshot migration invalidated target evidence from {oldPair.Id}.] {claim.Summary}";
                report.DowngradedClaims.Add(new()
                {
                    ClaimId = claim.Id,
                    OldVerdict = oldVerdict,
                    NewVerdict = claim.Verdict,
                    Reason = "Target fact or source-file evidence did not map exactly.",
                    UnmappedTargetFactIds = unmapped,
                });
            }
            else
            {
                claim.TargetFacts = mappedTargets;
                if (claim.Verdict is ClaimVerdict.VerifiedEquivalent or ClaimVerdict.ApprovedDeviation)
                {
                    claim.Verdict = ClaimVerdict.ImplementedUnverified;
                    claim.VerificationIds = [];
                    claim.Summary = $"[Current-pair runtime re-verification required after migration from {oldPair.Id}.] {claim.Summary}";
                    report.DowngradedClaims.Add(new()
                    {
                        ClaimId = claim.Id,
                        OldVerdict = oldVerdict,
                        NewVerdict = claim.Verdict,
                        Reason = "Runtime verification artifacts are pinned to the previous target snapshot.",
                    });
                }
                else if (claim.Verdict.IsTerminal()) report.RetainedTerminalClaims.Add(claim.Id);
            }
            claim.PairId = newPair.Id;
        }

        JsonStore.WriteLines(outTraces, traces);
        JsonStore.WriteLines(outClaims, claims);
        return report;
    }

    private static List<FactReference> MapAll(IEnumerable<FactReference> references,
        IReadOnlyDictionary<string, SourceFact> oldFacts,
        IReadOnlyDictionary<string, List<SourceFact>> newFacts, string owner)
    {
        var mapped = new List<FactReference>();
        foreach (FactReference factRef in references)
        {
            if (!oldFacts.TryGetValue(factRef.Id, out SourceFact? oldFact) ||
                !MatchesOldReference(oldFact, factRef))
                throw new InvalidDataException($"{owner} has stale evidence {factRef.Id}");
            if (!TryMap(oldFact, newFacts, out SourceFact? newFact))
                throw new InvalidDataException($"{owner} fact {factRef.Id} does not map exactly to the new pair");
            mapped.Add(Ref(newFact!));
        }
        return mapped;
    }

    private static bool TryMap(SourceFact oldFact,
        IReadOnlyDictionary<string, List<SourceFact>> newFacts, out SourceFact? mapped)
    {
        if (newFacts.TryGetValue(Identity(oldFact), out List<SourceFact>? matches) && matches.Count == 1)
        {
            mapped = matches[0];
            return true;
        }
        // Identical repeated XML declarations share semantic/evidence identity. Fact IDs also pin
        // source position, so an exact surviving ID is the only safe disambiguator.
        SourceFact? exactId = matches?.SingleOrDefault(f => f.Id == oldFact.Id);
        if (exactId is not null)
        {
            mapped = exactId;
            return true;
        }
        mapped = null;
        return false;
    }

    private static Dictionary<string, SourceFact> ById(IEnumerable<SourceFact> facts) =>
        facts.ToDictionary(f => f.Id, StringComparer.Ordinal);

    private static Dictionary<string, List<SourceFact>> ByIdentity(IEnumerable<SourceFact> facts) =>
        facts.GroupBy(Identity, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    private static string Identity(SourceFact fact) => string.Join('\0',
        fact.Path, fact.Kind, fact.Surface, fact.Name, fact.EvidenceSha256, fact.FileSha256);

    private static FactReference Ref(SourceFact fact) =>
        new() { Id = fact.Id, EvidenceSha256 = fact.EvidenceSha256, FileSha256 = fact.FileSha256 };

    private static bool MatchesOldReference(SourceFact fact, FactReference reference) =>
        fact.EvidenceSha256.Equals(reference.EvidenceSha256, StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(reference.FileSha256) ||
         fact.FileSha256.Equals(reference.FileSha256, StringComparison.OrdinalIgnoreCase));

    private static void RequirePair(string actual, string expected, string kind, string id)
    {
        if (actual != expected) throw new InvalidDataException($"{kind} {id} belongs to {actual}, not {expected}");
    }
}
