using System.Text;

namespace SnapshotParity;

internal static class ReportWriter
{
    public static void Write(string path, SnapshotPair pair, IReadOnlyList<BehaviorTrace> traces,
        IReadOnlyList<ComparisonClaim> claims)
    {
        ValidationResult validation = ComparisonEngine.Validate(pair, traces, claims);
        var candidateIds = pair.Candidates.Select(c => c.ReferenceFactId).ToHashSet(StringComparer.Ordinal);
        var b = new StringBuilder();
        b.AppendLine("# Snapshot parity report").AppendLine();
        b.AppendLine($"Pair: `{pair.Id}`").AppendLine();
        b.AppendLine($"- Reference: `{pair.ReferenceSnapshotId}` (`{pair.ReferenceSnapshotSha256}`)");
        b.AppendLine($"- Target: `{pair.TargetSnapshotId}` (`{pair.TargetSnapshotSha256}`)");
        b.AppendLine($"- Reference facts: {pair.ReferenceFacts.Count:N0}");
        b.AppendLine($"- Target facts: {pair.TargetFacts.Count:N0}");
        b.AppendLine($"- Mechanical candidate mappings: {pair.Candidates.Count:N0}");
        b.AppendLine($"- Reviewed behavior traces: {traces.Count:N0}");
        b.AppendLine($"- Reviewed required facts: {validation.ClaimedReferenceFacts:N0}/{validation.RequiredReferenceFacts:N0}");
        var traceFacts = traces.ToDictionary(t => t.Id,
            t => t.ReferenceFacts.Select(f => f.Id).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var resolved = claims.Where(c => c.Verdict.IsTerminal())
            .SelectMany(c => c.ReferenceFacts.Select(f => f.Id)
                .Concat(c.TraceIds.Where(traceFacts.ContainsKey).SelectMany(id => traceFacts[id])))
            .ToHashSet(StringComparer.Ordinal);
        int resolvedRequired = pair.ReferenceFacts.Count(f => f.ReviewRequired && resolved.Contains(f.Id));
        b.AppendLine($"- Terminally resolved required facts: {resolvedRequired:N0}/{validation.RequiredReferenceFacts:N0}");
        b.AppendLine($"- Validation errors: {validation.Errors.Count:N0}");
        b.AppendLine();
        b.AppendLine("> Candidate mappings are discovery aids, not equivalence claims. Only reviewed claims can establish parity.");
        b.AppendLine();
        b.AppendLine("## Reference fact surface").AppendLine();
        b.AppendLine("| Surface | Kind | Facts | Mechanical candidates |");
        b.AppendLine("|---|---|---:|---:|");
        foreach (var group in pair.ReferenceFacts.GroupBy(f => (f.Surface, f.Kind))
                     .OrderBy(g => g.Key.Surface).ThenBy(g => g.Key.Kind))
            b.AppendLine($"| {Escape(group.Key.Surface)} | {Escape(group.Key.Kind)} | {group.Count():N0} | {group.Count(f => candidateIds.Contains(f.Id)):N0} |");
        b.AppendLine();
        b.AppendLine("## Claim verdicts").AppendLine();
        if (claims.Count == 0) b.AppendLine("No reviewed claims exist yet.");
        else
        {
            b.AppendLine("| Verdict | Claims | Reference facts |");
            b.AppendLine("|---|---:|---:|");
            foreach (var group in claims.GroupBy(c => c.Verdict).OrderBy(g => g.Key.ToString()))
            {
                int factCount = group.SelectMany(c => c.ReferenceFacts.Select(f => f.Id)
                        .Concat(c.TraceIds.Where(traceFacts.ContainsKey).SelectMany(id => traceFacts[id])))
                    .Distinct(StringComparer.Ordinal).Count();
                b.AppendLine($"| {group.Key} | {group.Count():N0} | {factCount:N0} |");
            }
        }
        b.AppendLine();
        b.AppendLine("## First unresolved reference facts").AppendLine();
        b.AppendLine("| Fact | Location | Kind | Name | Candidate? |");
        b.AppendLine("|---|---|---|---|---|");
        foreach (SourceFact fact in pair.ReferenceFacts.Where(f => f.ReviewRequired && !resolved.Contains(f.Id)).Take(250))
            b.AppendLine($"| `{fact.Id}` | `{Escape(fact.Path)}:{fact.Line}` | {Escape(fact.Kind)} | {Escape(fact.Name)} | {(candidateIds.Contains(fact.Id) ? "yes" : "no")} |");
        int unseen = validation.RequiredReferenceFacts - resolvedRequired - 250;
        if (unseen > 0) b.AppendLine().AppendLine($"The table is capped; {unseen:N0} additional required facts remain unresolved.");
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, b.ToString(), new UTF8Encoding(false));
    }

    private static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
