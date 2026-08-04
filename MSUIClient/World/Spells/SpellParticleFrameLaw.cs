using System.Numerics;

namespace MSUIClient.World.Spells;

/// <summary>
/// Pure coordinate-frame and motion operations shared by both spell-particle storage lanes.
/// Ordinary particles bake the posed emitter frame at birth and retain only the effect root as
/// their live anchor. Model-space particles retain the complete posed emitter frame and fold it
/// at draw. Follow and inherited motion use the same clamped step as integration and emission.
/// </summary>
public static class SpellParticleFrameLaw
{
    public const float MaximumStepSeconds = .1f;
    public const float InheritIntervalSeconds = 1f / 30f;

    public static float SimulationStep(float dt)
        => float.IsFinite(dt) && dt > 0f ? MathF.Min(dt, MaximumStepSeconds) : 0f;

    /// <summary>
    /// Reconstruct the reference Transform carried by a joint-owned emitter. The source matrices
    /// can contain shear after hierarchical multiplication; the original runtime copies the live
    /// GlobalTransform through its scale/rotation/translation decomposition before simulation.
    /// Translation is deliberately removed because emitter origin is carried separately.
    /// </summary>
    public static Matrix4x4 ComposeEmitterLinearFrame(Matrix4x4 emitterModelFrame,
        Matrix4x4 modelToWorld)
    {
        Matrix4x4 composed = emitterModelFrame * modelToWorld;
        if (!Matrix4x4.Decompose(composed, out Vector3 scale, out Quaternion rotation, out _))
        {
            composed.M41 = composed.M42 = composed.M43 = 0f;
            return composed;
        }
        return Matrix4x4.CreateScale(scale) *
            Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
    }

    public static Vector3 DrawModelVector(Vector3 stored, Matrix4x4 emitterLinearFrame)
        => Vector3.TransformNormal(stored, emitterLinearFrame);

    public static Vector3 DrawModelPoint(Vector3 stored, Vector3 emitterWorld,
        Matrix4x4 emitterLinearFrame)
        => emitterWorld + DrawModelVector(stored, emitterLinearFrame);

    public static Vector3 RebasedEmitterOrigin(Vector3 emitterPosition, Vector3 jointPivot,
        Vector3 jointTranslation, Matrix4x4 jointLinearFrame)
        => jointTranslation + DrawModelVector(emitterPosition - jointPivot, jointLinearFrame);

    public static Vector3 StoreModelVector(Vector3 world, Matrix4x4 emitterLinearFrame)
        => Matrix4x4.Invert(emitterLinearFrame, out Matrix4x4 inverse)
            ? Vector3.TransformNormal(world, inverse)
            : world;

    public static float XScale(Matrix4x4 emitterLinearFrame)
    {
        float scale = new Vector3(emitterLinearFrame.M11, emitterLinearFrame.M12,
            emitterLinearFrame.M13).Length();
        return float.IsFinite(scale) && scale > 0f ? scale : 1f;
    }

    public static Vector3 FollowCorrectionWorld(bool enabled, Vector3 emitterDelta, float dt,
        float speed1, float scale1, float speed2, float scale2, bool storedFrameRidesEmitter)
    {
        if (!enabled || dt <= 0f || emitterDelta == Vector3.Zero ||
            MathF.Abs(speed2 - speed1) < 1e-6f)
            return Vector3.Zero;
        float slope = (scale2 - scale1) / (speed2 - speed1);
        float intercept = scale1 - slope * speed1;
        float fraction = Math.Clamp(slope * emitterDelta.Length() / dt + intercept, 0f, 1f);
        return (storedFrameRidesEmitter ? fraction - 1f : fraction) * emitterDelta;
    }

    /// <summary>
    /// Reference 30 Hz sample-and-hold. The trigger is strictly greater than 1/30 s, samples only
    /// the current frame's emitter delta, and publishes zero until a particle was already alive.
    /// </summary>
    public static void UpdateInheritedMotion(float dt, Vector3 emitterDelta, float inheritScale,
        bool hasLiveParticle, ref float accumulator, ref Vector3 heldVelocity)
    {
        if (dt <= 0f) return;
        accumulator += dt;
        if (accumulator <= InheritIntervalSeconds) return;
        heldVelocity = hasLiveParticle
            ? emitterDelta * (InheritIntervalSeconds / accumulator) * inheritScale
            : Vector3.Zero;
        accumulator = 0f;
    }

    public static Vector3 StoreAtBirth(Vector3 birthWorld, Vector3 rootCloudAnchorWorld,
        Quaternion hostAttachmentRotation)
        => Vector3.Transform(birthWorld - rootCloudAnchorWorld,
            Quaternion.Inverse(hostAttachmentRotation));

    public static Vector3 StoreVector(Vector3 worldVector, Quaternion hostAttachmentRotation)
        => Vector3.Transform(worldVector, Quaternion.Inverse(hostAttachmentRotation));

    public static Vector3 DrawWorld(Vector3 stored, Vector3 rootCloudAnchorWorld,
        Quaternion hostAttachmentRotation)
        => rootCloudAnchorWorld + Vector3.Transform(stored, hostAttachmentRotation);

    public static Vector3 DrawVector(Vector3 stored, Quaternion hostAttachmentRotation)
        => Vector3.Transform(stored, hostAttachmentRotation);
}
