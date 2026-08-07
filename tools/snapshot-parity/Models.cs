using System.Text.Json.Serialization;

namespace SnapshotParity;

internal static class Schema
{
    public const int Version = 1;
    public const string CapturePolicy = "source-snapshot-v1";
}

internal sealed class SnapshotManifest
{
    public int SchemaVersion { get; set; } = Schema.Version;
    public string Tool { get; set; } = "snapshot-parity";
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public string AggregateSha256 { get; set; } = "";
    public string Root { get; set; } = "";
    public DateTimeOffset CapturedUtc { get; set; }
    public string CapturePolicy { get; set; } = Schema.CapturePolicy;
    public List<string> Exclusions { get; set; } = [];
    public List<SnapshotFile> Files { get; set; } = [];
}

internal sealed class SnapshotFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Role { get; set; } = "";
}

internal sealed class FactIndex
{
    public int SchemaVersion { get; set; } = Schema.Version;
    public string SnapshotId { get; set; } = "";
    public string SnapshotKind { get; set; } = "";
    public string SnapshotSha256 { get; set; } = "";
    public DateTimeOffset IndexedUtc { get; set; }
    public List<SourceFact> Facts { get; set; } = [];
}

internal sealed class SourceFact
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Surface { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string Evidence { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public string FileSha256 { get; set; } = "";
    public bool ReviewRequired { get; set; } = true;
}

internal sealed class SnapshotPair
{
    public int SchemaVersion { get; set; } = Schema.Version;
    public string Id { get; set; } = "";
    public DateTimeOffset ComparedUtc { get; set; }
    public string ReferenceSnapshotId { get; set; } = "";
    public string ReferenceSnapshotSha256 { get; set; } = "";
    public string TargetSnapshotId { get; set; } = "";
    public string TargetSnapshotSha256 { get; set; } = "";
    public List<SourceFact> ReferenceFacts { get; set; } = [];
    public List<SourceFact> TargetFacts { get; set; } = [];
    public List<CandidateMapping> Candidates { get; set; } = [];
}

internal sealed class CandidateMapping
{
    public string ReferenceFactId { get; set; } = "";
    public List<string> TargetFactIds { get; set; } = [];
    public string Basis { get; set; } = "";
}

internal sealed class BehaviorTrace
{
    public int SchemaVersion { get; set; } = Schema.Version;
    public string Id { get; set; } = "";
    public string PairId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Surface { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Preconditions { get; set; } = "";
    public string Behavior { get; set; } = "";
    public string NegativeBehavior { get; set; } = "";
    public List<FactReference> ReferenceFacts { get; set; } = [];
    public string Reviewer { get; set; } = "";
    public DateTimeOffset ReviewedUtc { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ClaimVerdict
{
    Unknown,
    CandidateMatch,
    Gap,
    Divergent,
    ImplementedUnverified,
    VerifiedEquivalent,
    ApprovedDeviation,
    NotRuntime,
    InternalSupport,
    TestOnly,
}

internal static class ClaimVerdictExtensions
{
    public static bool IsTerminal(this ClaimVerdict verdict) => verdict is
        ClaimVerdict.VerifiedEquivalent or
        ClaimVerdict.ApprovedDeviation or
        ClaimVerdict.NotRuntime or
        ClaimVerdict.InternalSupport or
        ClaimVerdict.TestOnly;
}

internal sealed class ComparisonClaim
{
    public int SchemaVersion { get; set; } = Schema.Version;
    public string Id { get; set; } = "";
    public string PairId { get; set; } = "";
    public List<string> TraceIds { get; set; } = [];
    public List<FactReference> ReferenceFacts { get; set; } = [];
    public List<FactReference> TargetFacts { get; set; } = [];
    public ClaimVerdict Verdict { get; set; }
    public string Summary { get; set; } = "";
    public string Behavior { get; set; } = "";
    public string NegativeBehavior { get; set; } = "";
    public List<string> VerificationIds { get; set; } = [];
    public string? DecisionId { get; set; }
    public string Reviewer { get; set; } = "";
    public DateTimeOffset ReviewedUtc { get; set; }
}

internal sealed class FactReference
{
    public string Id { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
}

internal sealed class ValidationResult
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public int RequiredReferenceFacts { get; set; }
    public int ClaimedReferenceFacts { get; set; }
}

internal sealed class ReviewQueue
{
    public int SchemaVersion { get; set; } = Schema.Version;
    public string PairId { get; set; } = "";
    public DateTimeOffset GeneratedUtc { get; set; }
    public int CoveredReferenceFacts { get; set; }
    public int UnreviewedReferenceFacts { get; set; }
    public List<ReviewPacket> Packets { get; set; } = [];
}

internal sealed class ReviewPacket
{
    public string Id { get; set; } = "";
    public int Sequence { get; set; }
    public string WorkKind { get; set; } = "review";
    public string SourcePath { get; set; } = "";
    public int Chunk { get; set; }
    public List<string> Surfaces { get; set; } = [];
    public List<string> ReferenceFactIds { get; set; } = [];
    public List<string> UnresolvedReferenceFactIds { get; set; } = [];
    public int MechanicalCandidateCount { get; set; }
    public List<PacketClaim> Claims { get; set; } = [];
}

internal sealed class PacketClaim
{
    public string Id { get; set; } = "";
    public ClaimVerdict Verdict { get; set; }
    public string Summary { get; set; } = "";
    public string Behavior { get; set; } = "";
    public string NegativeBehavior { get; set; } = "";
    public List<string> VerificationIds { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum PacketClassification
{
    Unreviewed,
    Missing,
    Broken,
    Different,
    Intentional,
    Equivalent,
    NotRuntime,
    InternalSupport,
    TestOnly,
    Unclear,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum PacketWritePolicy { Blocked, Port, Repair, Preserve }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum PacketAuditStatus { Unreviewed, Reviewed, Implemented, Verified, Blocked }

internal sealed class PacketAudit
{
    public int SchemaVersion { get; set; } = Schema.Version;
    public string PairId { get; set; } = "";
    public string PacketId { get; set; } = "";
    public PacketAuditStatus Status { get; set; } = PacketAuditStatus.Unreviewed;
    public PacketClassification Classification { get; set; } = PacketClassification.Unreviewed;
    public PacketWritePolicy WritePolicy { get; set; } = PacketWritePolicy.Blocked;
    public AuditSection MsuiBefore { get; set; } = new();
    public AuditSection ReferenceRequirement { get; set; } = new();
    public AuditChange Change { get; set; } = new();
    public AuditSection MsuiAfter { get; set; } = new();
    public List<string> Verification { get; set; } = [];
    public string? DecisionId { get; set; }
    public string Reviewer { get; set; } = "";
    public DateTimeOffset? ReviewedUtc { get; set; }
}

internal sealed class AuditSection
{
    public string Summary { get; set; } = "";
    public List<string> Evidence { get; set; } = [];
}

internal sealed class AuditChange
{
    public string Summary { get; set; } = "";
    public List<string> Files { get; set; } = [];
}

internal sealed class PacketWorkspaceValidation
{
    public List<string> Errors { get; } = [];
    public int PacketCount { get; set; }
    public int VerifiedCount { get; set; }
    public int BlockedCount { get; set; }
}

internal sealed class MigrationReport
{
    public int SchemaVersion { get; set; } = Schema.Version;
    public string OldPairId { get; set; } = "";
    public string NewPairId { get; set; } = "";
    public DateTimeOffset MigratedUtc { get; set; }
    public int TraceCount { get; set; }
    public int ClaimCount { get; set; }
    public List<string> RetainedTerminalClaims { get; set; } = [];
    public List<MigrationDowngrade> DowngradedClaims { get; set; } = [];
}

internal sealed class MigrationDowngrade
{
    public string ClaimId { get; set; } = "";
    public ClaimVerdict OldVerdict { get; set; }
    public ClaimVerdict NewVerdict { get; set; }
    public List<string> UnmappedTargetFactIds { get; set; } = [];
}
