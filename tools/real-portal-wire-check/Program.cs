using System.Numerics;
using MSUIClient.Net;
using MSUIClient.World.Portals;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

static void Rejects(Action action, string message)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        return; // Expected protocol-boundary rejection.
    }
    throw new InvalidDataException($"accepted malformed portal packet: {message}");
}

Check((ushort)Op.CMSG_SUI_PORTAL_PREPARE == 0x034C &&
      (ushort)Op.SMSG_SUI_PORTAL_DESCRIPTOR == 0x034D &&
      (ushort)Op.CMSG_SUI_PORTAL_READY == 0x034E &&
      (ushort)Op.SMSG_SUI_PORTAL_STATE == 0x034F,
    "REAL_PORTALS opcode allocation drifted");
Check((ushort)PortalDescriptorFlags.OneWay == 0x0001 &&
      (ushort)PortalDescriptorFlags.PartyOnly == 0x0002 &&
      (ushort)PortalDescriptorFlags.ClickFallback == 0x0004 &&
      (ushort)PortalDescriptorFlags.SameMapHint == 0x0008 &&
      (ushort)PortalDescriptorFlags.Bidirectional == 0x0010,
    "descriptor flag allocation drifted");

static byte[] ControlAckWithTrailer(uint? magic = SuiCapabilityWire.Magic, int trailerBytes = 8)
{
    var w = new PacketWriter(33);
    w.WriteU64(0);                 // zero-guid capability probe
    w.WriteU8(1);                  // old-core DENY_NOT_FOUND
    for (int i = 0; i < 4; i++) w.WriteF32(0f);
    if (trailerBytes >= 4) w.WriteU32(magic ?? 0);
    if (trailerBytes >= 8) w.WriteU32(SuiCapabilityWire.RealPortalsV1);
    return w.ToArray();
}

static bool ReadControlAckCapabilities(byte[] body, out uint capabilities)
{
    var r = new PacketReader(body);
    r.ReadU64();
    r.ReadU8();
    for (int i = 0; i < 4; i++) r.ReadF32();
    return SuiCapabilityWire.TryRead(r, out capabilities);
}

Check(!ReadControlAckCapabilities(ControlAckWithTrailer(trailerBytes: 0), out _),
    "legacy 25-byte control ACK invented a capability trailer");
Check(ReadControlAckCapabilities(ControlAckWithTrailer(), out uint capabilityMask) &&
      (capabilityMask & SuiCapabilityWire.RealPortalsV1) != 0,
    "33-byte control ACK did not advertise portal-v1");
Check(!ReadControlAckCapabilities(ControlAckWithTrailer(magic: 0xDEADBEEF), out _),
    "bad capability magic was accepted");
Check(!ReadControlAckCapabilities(ControlAckWithTrailer(trailerBytes: 4), out _),
    "partial capability trailer was accepted");

var prepare = PortalWire.Prepare(0x10203040, 0xF130000012345678);
byte[] prepareBody = PortalWire.BuildPrepare(prepare);
Check(prepareBody.Length == PortalWire.PrepareLength &&
      PortalWire.ParsePrepare(prepareBody) == prepare,
    "prepare packet did not round-trip exactly");

var descriptor = new PortalDescriptorPacket(
    PortalWire.ProtocolVersion,
    PortalDescriptorResult.Ok,
    PortalDescriptorFlags.OneWay | PortalDescriptorFlags.ClickFallback,
    RequestId: 0x10203040,
    PortalGuid: 0xF130000012345678,
    SpawnGeneration: 7,
    DescriptorRevision: 9,
    Ticket: 0x8877665544332211,
    PortalEntry: 176296,
    TeleportSpellId: 17334,
    RemainingLifetimeMs: 59_000,
    SourceCenter: new Vector3(11.25f, -22.5f, 33.75f),
    SourceYaw: 1.25f,
    HalfWidth: 2.75f,
    HalfHeight: 4.25f,
    PlaneEpsilon: 0.08f,
    PreviewMapId: 0,
    PreviewPosition: new Vector3(-8833.4f, 628.6f, 94.0f),
    PreviewOrientation: 2.5f);
byte[] descriptorBody = PortalWire.BuildDescriptor(descriptor);
Check(descriptorBody.Length == PortalWire.DescriptorLength &&
      PortalWire.ParseDescriptor(descriptorBody) == descriptor,
    "descriptor packet did not round-trip exactly");

var ready = PortalWire.Ready(
    PortalLoadResult.Ready,
    descriptor.PortalGuid,
    descriptor.SpawnGeneration,
    descriptor.DescriptorRevision,
    descriptor.Ticket);
byte[] readyBody = PortalWire.BuildReady(ready);
Check(readyBody.Length == PortalWire.ReadyLength &&
      PortalWire.ParseReady(readyBody) == ready,
    "ready packet did not round-trip exactly");

var state = new PortalStatePacket(
    PortalWire.ProtocolVersion,
    PortalStateCode.Ready,
    Reason: 0,
    descriptor.PortalGuid,
    descriptor.SpawnGeneration,
    descriptor.DescriptorRevision,
    descriptor.Ticket,
    LeaseOrRetryMs: 5_000);
byte[] stateBody = PortalWire.BuildState(state);
Check(stateBody.Length == PortalWire.StateLength &&
      PortalWire.ParseState(stateBody) == state,
    "state packet did not round-trip exactly");

Rejects(() => PortalWire.ParsePrepare(prepareBody[..^1]), "short prepare");
Rejects(() => PortalWire.ParseDescriptor([.. descriptorBody, 0]), "long descriptor");
Rejects(() => PortalWire.ParseReady(readyBody[..^1]), "short ready");
Rejects(() => PortalWire.ParseState([.. stateBody, 0]), "long state");

byte[] badPrepareReserved = (byte[])prepareBody.Clone();
badPrepareReserved[1] = 1;
Rejects(() => PortalWire.ParsePrepare(badPrepareReserved), "prepare reserved byte");

byte[] badPrepareFlags = (byte[])prepareBody.Clone();
badPrepareFlags[2] = 1;
Rejects(() => PortalWire.ParsePrepare(badPrepareFlags), "prepare request flags");

byte[] badReadyReserved = (byte[])readyBody.Clone();
badReadyReserved[2] = 1;
Rejects(() => PortalWire.ParseReady(badReadyReserved), "ready reserved field");

byte[] badStateReserved = (byte[])stateBody.Clone();
badStateReserved[3] = 1;
Rejects(() => PortalWire.ParseState(badStateReserved), "state reserved byte");

byte[] nonReadyLease = (byte[])stateBody.Clone();
nonReadyLease[1] = (byte)PortalStateCode.Failed;
Rejects(() => PortalWire.ParseState(nonReadyLease), "lease on non-ready state");

byte[] badDescriptorResult = (byte[])descriptorBody.Clone();
badDescriptorResult[1] = 0xFF;
Rejects(() => PortalWire.ParseDescriptor(badDescriptorResult), "descriptor result enum");

byte[] badReadyResult = (byte[])readyBody.Clone();
badReadyResult[1] = 0xFF;
Rejects(() => PortalWire.ParseReady(badReadyResult), "ready result enum");

byte[] badStateCode = (byte[])stateBody.Clone();
badStateCode[1] = 0xFF;
Rejects(() => PortalWire.ParseState(badStateCode), "state enum");

byte[] nonFiniteDescriptor = (byte[])descriptorBody.Clone();
BitConverter.GetBytes(float.NaN).CopyTo(nonFiniteDescriptor, 44); // sourceCenterX
Rejects(() => PortalWire.ParseDescriptor(nonFiniteDescriptor), "non-finite geometry");

byte[] unknownDescriptorFlag = (byte[])descriptorBody.Clone();
unknownDescriptorFlag[3] = 0x80;
Rejects(() => PortalWire.ParseDescriptor(unknownDescriptorFlag), "unknown descriptor flag");

byte[] zeroGuidPrepare = (byte[])prepareBody.Clone();
Array.Clear(zeroGuidPrepare, 8, 8);
Rejects(() => PortalWire.ParsePrepare(zeroGuidPrepare), "empty portal GUID");

byte[] zeroTicketReady = (byte[])readyBody.Clone();
Array.Clear(zeroTicketReady, 20, 8);
Rejects(() => PortalWire.ParseReady(zeroTicketReady), "empty ticket");

var sceneDescriptor = new PortalDescriptor
{
    Version = descriptor.Version,
    Result = (byte)descriptor.Result,
    Flags = (ushort)descriptor.Flags,
    RequestId = descriptor.RequestId,
    PortalGuid = descriptor.PortalGuid,
    SpawnGeneration = descriptor.SpawnGeneration,
    DescriptorRevision = descriptor.DescriptorRevision,
    Ticket = descriptor.Ticket,
    PortalEntry = descriptor.PortalEntry,
    TeleportSpellId = descriptor.TeleportSpellId,
    RemainingLifetimeMs = descriptor.RemainingLifetimeMs,
    SourceCenter = descriptor.SourceCenter,
    SourceYaw = descriptor.SourceYaw,
    HalfWidth = descriptor.HalfWidth,
    HalfHeight = descriptor.HalfHeight,
    PlaneEpsilon = descriptor.PlaneEpsilon,
    PreviewMapId = descriptor.PreviewMapId,
    PreviewPosition = descriptor.PreviewPosition,
    PreviewOrientation = descriptor.PreviewOrientation,
};
Check(sceneDescriptor.IsValid, "scene descriptor rejected a valid wire descriptor");
Check(MathF.Abs(sceneDescriptor.DestinationFrame.Center.Z -
                (descriptor.PreviewPosition.Z + descriptor.HalfHeight + 0.1f)) < 0.0001f,
    "stock feet destination was not lifted to doorway centre");

PortalFrame source = PortalFrame.FromYaw(new Vector3(10f, 20f, 30f), 0.35f);
PortalFrame destination = PortalFrame.FromYaw(new Vector3(-5f, 7f, 11f), -1.1f);
Vector3 sourcePoint = source.Center + source.Right * 2f + source.Up * 1.5f + source.Normal * 3f;
Vector3 expectedPoint = destination.Center + destination.Right * 2f +
                        destination.Up * 1.5f + destination.Normal * 3f;
Check(Vector3.Distance(destination.TransformPoint(sourcePoint, source), expectedPoint) < 0.0001f,
    "portal frame transform did not preserve basis coefficients");

Check(source.TryNormalize(out PortalFrame previewSource),
    "portal preview source test frame was degenerate");
Check(destination.TryNormalize(out PortalFrame previewDestination),
    "portal preview destination test frame was degenerate");
const float previewBoom = 8f;
const float previewLateral = 2f;
const float previewHeight = 1f;

// Front approach: looking inward is -source.Normal. The required 180-degree
// turn puts the destination camera behind the stock landing pose, looking in
// the authoritative arrival-facing direction. Horizontal screen-right is
// preserved as -destination.Right (the normalized frame's camera-right).
Vector3 frontEye = previewSource.Center + previewSource.Normal * previewBoom +
                   previewSource.Right * previewLateral +
                   previewSource.Up * previewHeight;
Check(PortalPreviewCameraLaw.TryCreate(
        previewSource, previewDestination, frontEye, -previewSource.Normal,
        out PortalPreviewCameraLaw.Mapping frontMapping) &&
      frontMapping.SourceSide == 1,
    "front-side portal preview mapping was not resolved");
Vector3 mappedFrontEye = frontMapping.TransformPoint(frontEye);
Vector3 expectedFrontEye = previewDestination.Center -
                           previewDestination.Normal * previewBoom -
                           previewDestination.Right * previewLateral +
                           previewDestination.Up * previewHeight;
Check(Vector3.Distance(mappedFrontEye, expectedFrontEye) < 0.0001f &&
      Vector3.Distance(frontMapping.TransformDirection(-previewSource.Normal),
          previewDestination.Normal) < 0.0001f &&
      Vector3.Distance(frontMapping.TransformDirection(previewSource.Right),
          -previewDestination.Right) < 0.0001f,
    "front-side mapping did not perform the exact handed 180-degree turn");

// Rear approach needs no turn: +source.Normal is already the inward axis. Its
// screen-right is -source.Right and must still become destination screen-right.
Vector3 rearEye = previewSource.Center - previewSource.Normal * previewBoom +
                  previewSource.Right * previewLateral +
                  previewSource.Up * previewHeight;
Check(PortalPreviewCameraLaw.TryCreate(
        previewSource, previewDestination, rearEye, previewSource.Normal,
        out PortalPreviewCameraLaw.Mapping rearMapping) &&
      rearMapping.SourceSide == -1,
    "rear-side portal preview mapping was not resolved");
Vector3 mappedRearEye = rearMapping.TransformPoint(rearEye);
Vector3 expectedRearEye = previewDestination.Center -
                          previewDestination.Normal * previewBoom +
                          previewDestination.Right * previewLateral +
                          previewDestination.Up * previewHeight;
Check(Vector3.Distance(mappedRearEye, expectedRearEye) < 0.0001f &&
      Vector3.Distance(rearMapping.TransformDirection(previewSource.Normal),
          previewDestination.Normal) < 0.0001f &&
      Vector3.Distance(rearMapping.TransformDirection(-previewSource.Right),
          -previewDestination.Right) < 0.0001f,
    "rear-side mapping did not preserve destination-forward and screen-right");
Check(Vector3.Dot(mappedFrontEye - previewDestination.Center,
          previewDestination.Normal) < 0f &&
      Vector3.Dot(mappedRearEye - previewDestination.Center,
          previewDestination.Normal) < 0f,
    "a two-sided preview camera was placed in front of the arrival pose");

Check(PortalExitClipLaw.TryCreate(previewDestination, 0.1f,
        out MSUIClient.World.WorldClipPlane exitClip),
    "destination exit clip plane was not created");
Vector3 clippedPoint = previewDestination.Center - previewDestination.Normal * 2f;
Vector3 retainedPoint = previewDestination.Center + previewDestination.Normal * 2f;
Check(exitClip.SignedDistance(clippedPoint) < 0f &&
      exitClip.SignedDistance(retainedPoint) > 0f,
    "destination exit plane retained the camera-side half-space");
Vector3 clipCamera = previewDestination.Center - previewDestination.Normal * 8f +
                     previewDestination.Right * 3f;
Vector4 relativeEquation = exitClip.RelativeEquation(clipCamera);
Vector3 relativeRetained = retainedPoint - clipCamera;
float relativeDistance = Vector3.Dot(
    new Vector3(relativeEquation.X, relativeEquation.Y, relativeEquation.Z),
    relativeRetained) + relativeEquation.W;
Check(MathF.Abs(relativeDistance - exitClip.SignedDistance(retainedPoint)) < 0.0001f,
    "camera-relative exit equation changed signed distance");

// On the mathematical plane, view direction supplies the otherwise ambiguous
// side deterministically for both inward directions.
Check(PortalPreviewCameraLaw.TryCreate(
        previewSource, previewDestination, previewSource.Center,
        -previewSource.Normal, out PortalPreviewCameraLaw.Mapping frontPlaneMapping) &&
      frontPlaneMapping.SourceSide == 1 &&
      PortalPreviewCameraLaw.TryCreate(
        previewSource, previewDestination, previewSource.Center,
        previewSource.Normal, out PortalPreviewCameraLaw.Mapping rearPlaneMapping) &&
      rearPlaneMapping.SourceSide == -1,
    "portal-plane camera direction did not disambiguate front and rear views");

PortalFrame crossingFrame = PortalFrame.FromYaw(new Vector3(10f, 20f, 4.1f), 0f);
Check(PortalCrossingLaw.TryCross(
        new Vector3(11f, 20f, 0.1f),
        new Vector3(10.4f, 20f, 0.1f),
        crossingFrame, 3f, 4f, 0.5f, 2f,
        out PortalCrossingLaw.Crossing crossing) &&
      crossing.FromSide == 1 && MathF.Abs(crossing.Fraction - (5f / 6f)) < 0.0001f,
    "front capsule contact short of the centre plane was not detected");
Check(PortalCrossingLaw.TryCross(
        new Vector3(9f, 20f, 0.1f),
        new Vector3(9.6f, 20f, 0.1f),
        crossingFrame, 3f, 4f, 0.5f, 2f,
        out crossing) && crossing.FromSide == -1 &&
      MathF.Abs(crossing.Fraction - (5f / 6f)) < 0.0001f,
    "rear capsule contact short of the centre plane was not detected");
Check(!PortalCrossingLaw.TryCross(
        new Vector3(11f, 20f, 0.1f),
        new Vector3(11.1f, 20f, 0.1f),
        crossingFrame, 3f, 4f, 0.5f, 2f, out _),
    "capsule moving away from the portal was accepted");
Check(!PortalCrossingLaw.TryCross(
        new Vector3(11f, 24f, 0.1f),
        new Vector3(9f, 24f, 0.1f),
        crossingFrame, 3f, 4f, 0.5f, 2f, out _),
    "crossing outside the expanded horizontal edge was accepted");
int armedSide = PortalCrossingLaw.ResolveArmedSide(
    armedSide: 0, signedDistance: 2f, planeEpsilon: 0.35f, latchAvailable: true);
Check(armedSide == 1, "front clear side did not arm the crossing latch");
armedSide = PortalCrossingLaw.ResolveArmedSide(
    armedSide, signedDistance: -2f, planeEpsilon: 0.35f, latchAvailable: true);
Check(armedSide == -1,
    "walking around the aperture did not rearm the latch from the rear side");
Check(PortalCrossingLaw.ResolveArmedSide(
        armedSide: 0, signedDistance: -2f, planeEpsilon: 0.35f, latchAvailable: false) == 0,
    "crossing cooldown incorrectly rearmed the latch");
Check(PortalCrossingLaw.TryRayHit(
        new Vector3(15f, 20f, 4.1f), -Vector3.UnitX,
        crossingFrame, 3f, 4f, 20f, out float portalRayHit) &&
      MathF.Abs(portalRayHit - 5f) < 0.0001f,
    "front aperture ray hit failed");
Check(PortalCrossingLaw.TryRayHit(
        new Vector3(5f, 20f, 4.1f), Vector3.UnitX,
        crossingFrame, 3f, 4f, 20f, out portalRayHit) &&
      MathF.Abs(portalRayHit - 5f) < 0.0001f,
    "rear aperture ray hit failed");
Check(!PortalCrossingLaw.TryRayHit(
        new Vector3(15f, 24f, 4.1f), -Vector3.UnitX,
        crossingFrame, 3f, 4f, 20f, out _),
    "ray outside the aperture was accepted");

Vector3 arrival = new(-9003.46f, 870.031f, 29.6206f);
Check(PortalArrivalLaw.HasNearbySupport(arrival, 29.5f),
    "nearby portal arrival floor was rejected");
Check(!PortalArrivalLaw.HasNearbySupport(arrival, 96f),
    "terrain far above an interior portal arrival was accepted as floor support");
Check(!PortalArrivalLaw.HasNearbySupport(arrival, -100f),
    "a distant surface below the portal arrival was accepted as safe support");
Check(!PortalArrivalLaw.HasNearbySupport(arrival, 25f),
    "a floor requiring a visible post-teleport fall was accepted as ready");
Check(!PortalArrivalLaw.HasNearbySupport(arrival, 31f),
    "a ceiling above the portal arrival was accepted as floor support");

Vector3 preparedArrival = new(-8913.23f, 554.633f, 93.7944f);
Check(PortalHandoffLaw.MatchesPreparedSameMap(
        0, 0, preparedArrival, preparedArrival + new Vector3(1f, -1f, 0.2f)),
    "matching same-map portal handoff was rejected");
Check(PortalHandoffLaw.MatchesPreparedDestination(
        1, preparedArrival, 1, preparedArrival + new Vector3(0f, 0f, 1f)),
    "matching NEW_WORLD portal handoff was rejected");
Check(!PortalHandoffLaw.MatchesPreparedDestination(
        0, preparedArrival, 1, preparedArrival),
    "different-map world transfer consumed a prepared portal handoff");
Check(!PortalHandoffLaw.MatchesPreparedDestination(
        0, preparedArrival, 0,
        preparedArrival + Vector3.UnitX * (PortalHandoffLaw.DestinationTolerance + 0.1f)),
    "unrelated same-map teleport consumed a prepared portal handoff");
Check(!PortalHandoffLaw.MatchesPreparedDestination(
        0, preparedArrival, 0, new Vector3(float.NaN, 0f, 0f)),
    "non-finite authoritative destination consumed a portal handoff");

Vector3 portalObserver = Vector3.Zero;
Check(PortalVisualRelevanceLaw.IsRelevant(
        portalObserver, new Vector3(89.9f, 0f, 0f), currentlyTracked: false),
    "nearby untracked portal was not admitted for presentation");
Check(!PortalVisualRelevanceLaw.IsRelevant(
        portalObserver, new Vector3(90.1f, 0f, 0f), currentlyTracked: false),
    "distant stale portal was admitted for presentation");
Check(PortalVisualRelevanceLaw.IsRelevant(
        portalObserver, new Vector3(110f, 0f, 0f), currentlyTracked: true),
    "tracked portal was dropped inside the hysteresis band");
Check(!PortalVisualRelevanceLaw.IsRelevant(
        portalObserver, new Vector3(120.1f, 0f, 0f), currentlyTracked: true),
    "tracked portal survived beyond the presentation exit radius");
Check(!PortalVisualRelevanceLaw.IsRelevant(
        portalObserver, new Vector3(float.NaN, 0f, 0f), currentlyTracked: true),
    "non-finite portal position was presentation-relevant");
Check(!PortalVisualRelevanceLaw.MissingEntityGraceExpired(10.49, 10.0),
    "a transient entity-store gap immediately removed a nearby portal");
Check(PortalVisualRelevanceLaw.MissingEntityGraceExpired(10.50, 10.0),
    "a missing portal survived beyond its entity-store grace period");

Console.WriteLine("real-portal-wire-check: PASS");
