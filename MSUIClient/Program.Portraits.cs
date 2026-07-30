using System.Numerics;
using ImGuiNET;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private PortraitRenderTarget? _playerPortrait;
    private PortraitRenderTarget? _targetPortrait;
    private PortraitRenderTarget? _paperDoll;
    private bool _playerPortraitDirty = true;
    private bool _paperDollDirty = true;
    private bool _playerPortraitUsable;
    private bool _targetPortraitUsable;
    private bool _playerPortraitFailureDumped;
    private bool _targetPortraitFailureDumped;
    private float _paperDollRotation;
    private ulong _portraitTargetGuid;
    private int _portraitTargetDisplay;
    private float _portraitTargetScale;
    private int _portraitRequestDisplay;
    private float _portraitRequestScale;
    private double _playerPortraitRetryAt;
    private double _targetPortraitRetryAt;

    private void InitPortraits(GL gl)
    {
        try
        {
            _playerPortrait = new PortraitRenderTarget(gl);
            _targetPortrait = new PortraitRenderTarget(gl);
            _paperDoll = new PortraitRenderTarget(gl, 466, 448);
        }
        catch (Exception ex)
        {
            _playerPortrait?.Dispose();
            _targetPortrait?.Dispose();
            _paperDoll?.Dispose();
            _playerPortrait = null;
            _targetPortrait = null;
            _paperDoll = null;
            Console.WriteLine($"[portrait] render targets unavailable: {ex.Message}");
        }
    }

    private void BakeDirtyPortraits()
    {
        if (_playerPortraitDirty && NowSeconds() >= _playerPortraitRetryAt &&
            _playerPortrait is not null && _character is { Loaded: true, Enabled: true })
        {
            CharacterRenderer.UnitState state = BuildUnitState();
            // Benilla/reference booth law: scale 1, origin-local, frozen pose. A portrait must not
            // inherit live world translation, locomotion heading, animation phase, or display scale.
            state.Position = Vector3.Zero;
            state.Yaw = -_character.HeadingOffsetDegrees * MathF.PI / 180f;
            state.Forward = 0f;
            state.Strafe = 0f;
            float savedModelScale = _character.ModelScale;
            bool savedBindPose = _character.BindPose;
            bool savedFrozenStandPose = _character.FrozenStandPose;
            _character.ModelScale = 1f;
            _character.BindPose = false;
            _character.FrozenStandPose = true;

            bool authoredCamera = _character.TryGetAuthoredPortrait(state,
                out M2PortraitCamera authored, out Matrix4x4 portraitTransform);
            // A degenerate authored camera (eye == target) would normalize a zero forward into
            // NaNs and bake garbage; the reference tolerates it via normalize_or_zero. Fall back.
            authoredCamera = authoredCamera &&
                Vector3.DistanceSquared(authored.Position, authored.Target) > 1e-8f;
            Camera camera = authoredCamera
                ? AuthoredPortraitCamera(authored, portraitTransform)
                : BoundsPortraitCamera(Vector3.Zero, state.Yaw, _character.BindPoseHeight());
            bool usedFallbackCamera = !authoredCamera;

            Vector3 sunDirection = _character.SunDirection;
            Vector3 sunColor = _character.SunColor;
            float sunIntensity = _character.SunIntensity;
            Vector3 ambientColor = _character.AmbientColor;
            float ambientIntensity = _character.AmbientIntensity;
            float fogStart = _character.FogStart;
            float fogEnd = _character.FogEnd;
            try
            {
                // Stable portrait-booth light, independent of the zone's time of day.
                _character.SunDirection = Vector3.Normalize(new Vector3(0.25f, -0.45f, 0.85f));
                _character.SunColor = new Vector3(0.85f, 0.82f, 0.78f);
                _character.SunIntensity = 1f;
                _character.AmbientColor = new Vector3(0.58f, 0.56f, 0.54f);
                _character.AmbientIntensity = 1f;
                _character.FogStart = 1000f;
                _character.FogEnd = 2000f;
                _playerPortrait.Bake(() => _character.Render(camera, state));
                PortraitRenderTarget.ReadbackStats stats = _playerPortrait.Analyze();
                if (!stats.HasSubject && authoredCamera)
                {
                    Console.WriteLine($"[portrait] player authored bake blank ({stats}); " +
                        $"eye={authored.Position}, target={authored.Target}, fov={authored.FieldOfView:F4}, " +
                        "retrying bounds camera");
                    Camera fallback = BoundsPortraitCamera(
                        Vector3.Zero, state.Yaw, _character.BindPoseHeight());
                    _playerPortrait.Bake(() => _character.Render(fallback, state));
                    stats = _playerPortrait.Analyze();
                    usedFallbackCamera = true;
                }
                _playerPortraitUsable = stats.HasSubject;
                // Reference presentation: the round portrait is an inscribed circle cut from the
                // square bake; the frame ring's corners are transparent and hide nothing.
                if (_playerPortraitUsable) _playerPortrait.ApplyCircularMask();
                Console.WriteLine($"[portrait] player bake " +
                    $"{(_playerPortraitUsable ? "ready" : "BLANK")} ({stats}, " +
                    $"camera={(usedFallbackCamera ? "bounds" : "authored")}, pieces={_character.VisiblePieces})");
                if (!_playerPortraitUsable && !_playerPortraitFailureDumped)
                {
                    DumpFailedPortrait(_playerPortrait, "player");
                    _playerPortraitFailureDumped = true;
                }
                if (_playerPortraitUsable)
                {
                    _playerPortraitDirty = false;
                    _playerPortraitRetryAt = 0;
                    _playerPortraitFailureDumped = false;
                }
                else
                {
                    // A transient blank bake must not become the permanent TemporaryPortrait.
                    _playerPortraitDirty = true;
                    _playerPortraitRetryAt = NowSeconds() + 1.0;
                }
            }
            finally
            {
                _character.SunDirection = sunDirection;
                _character.SunColor = sunColor;
                _character.SunIntensity = sunIntensity;
                _character.AmbientColor = ambientColor;
                _character.AmbientIntensity = ambientIntensity;
                _character.FogStart = fogStart;
                _character.FogEnd = fogEnd;
                _character.ModelScale = savedModelScale;
                _character.BindPose = savedBindPose;
                _character.FrozenStandPose = savedFrozenStandPose;
            }
        }

        if (_characterOpen && _paperDollDirty && _paperDoll is not null &&
            _character is { Loaded: true, Enabled: true })
        {
            CharacterRenderer.UnitState state = BuildUnitState();
            float scale = MathF.Max(0.1f, _character.ModelScale);
            float distance = 4.15f * scale;
            Camera camera = PortraitCamera(state.Position, state.Yaw + _paperDollRotation,
                1.25f * scale, distance);
            camera.FieldOfViewDegrees = 43f;
            camera.AspectRatio = 233f / 224f;
            WithPortraitLighting(() => _paperDoll.Bake(() => _character.Render(camera, state), transparent: true));
            _paperDollDirty = false;
        }

        if (_targetPortrait is null || _creatures is null || _selectionGuid == 0 ||
            !_entities.TryGet(_selectionGuid, out WorldEntity target) || !target.IsCreature)
        {
            _portraitTargetGuid = 0;
            _portraitRequestDisplay = 0;
            _targetPortraitUsable = false;
            return;
        }

        // Benilla keys the frozen booth image by rendered parts, not by unit GUID. In the data
        // available here, display + scale are the appearance key. Two wolves with the same
        // appearance therefore reuse the same still instead of rebaking at a different phase.
        bool requestChanged = target.DisplayId != _portraitRequestDisplay ||
                              MathF.Abs(target.Scale - _portraitRequestScale) > 0.0001f;
        if (requestChanged)
        {
            _portraitRequestDisplay = target.DisplayId;
            _portraitRequestScale = target.Scale;
            _targetPortraitRetryAt = 0;
            _targetPortraitFailureDumped = false;
            _targetPortraitUsable = false;
        }

        bool appearanceChanged = target.DisplayId != _portraitTargetDisplay ||
                       MathF.Abs(target.Scale - _portraitTargetScale) > 0.0001f;
        if (_targetPortraitUsable && !appearanceChanged)
        {
            _portraitTargetGuid = target.Guid;
            return;
        }
        bool changed = requestChanged || appearanceChanged || !_targetPortraitUsable;
        if (!changed || NowSeconds() < _targetPortraitRetryAt ||
            !_creatures.TryGetPortraitFraming(target, out var framing)) return;

        bool targetUsesAuthored = _creatures.TryGetAuthoredPortrait(target,
                out M2PortraitCamera targetAuthored, out Matrix4x4 targetPortraitTransform) &&
            Vector3.DistanceSquared(targetAuthored.Position, targetAuthored.Target) > 1e-8f;
        Camera targetCamera = targetUsesAuthored
            ? AuthoredPortraitCamera(targetAuthored, targetPortraitTransform)
            : CreatureBoundsPortraitCamera(target, framing);
        bool drawn = false;
        _targetPortrait.Bake(() => drawn = _creatures.RenderPortrait(targetCamera, target));
        PortraitRenderTarget.ReadbackStats targetStats = _targetPortrait.Analyze();
        if (drawn && !targetStats.HasSubject)
        {
            Camera fallback = CreatureBoundsPortraitCamera(target, framing);
            _targetPortrait.Bake(() => drawn = _creatures.RenderPortrait(fallback, target));
            targetStats = _targetPortrait.Analyze();
        }
        _targetPortraitUsable = drawn && targetStats.HasSubject;
        if (_targetPortraitUsable)
        {
            _targetPortrait.ApplyCircularMask();
            _portraitTargetGuid = target.Guid;
            _portraitTargetDisplay = target.DisplayId;
            _portraitTargetScale = target.Scale;
            _targetPortraitRetryAt = 0;
            _targetPortraitFailureDumped = false;
        }
        else
        {
            _targetPortraitRetryAt = NowSeconds() + 1.0;
            if (!_targetPortraitFailureDumped)
            {
                Console.WriteLine($"[portrait] target bake BLANK ({targetStats}, display={target.DisplayId})");
                DumpFailedPortrait(_targetPortrait, $"target-{target.DisplayId}");
                _targetPortraitFailureDumped = true;
            }
        }
    }

    private static Camera BoundsPortraitCamera(Vector3 feet, float modelYaw, float modelHeight)
    {
        float head = MathF.Max(0.3f, modelHeight);
        float target = 0.92f * head;
        float window = Math.Clamp(0.34f * head, 0.55f, 1.10f);
        const float fovyDegrees = 0.5f * 180f / MathF.PI;
        float distance = (window * 0.5f) /
            MathF.Tan(fovyDegrees * 0.5f * MathF.PI / 180f);
        Camera camera = PortraitCamera(feet, modelYaw + 0.42f, target, distance);
        camera.FieldOfViewDegrees = fovyDegrees;
        camera.NearPlane = MathF.Max(0.02f, distance - head);
        return camera;
    }

    private static Camera CreatureBoundsPortraitCamera(WorldEntity target,
        CreatureRenderer.PortraitFraming framing)
    {
        Camera camera = PortraitCamera(target.Position, target.Orientation + 0.42f,
            framing.EyeHeight, framing.Distance);
        camera.FieldOfViewDegrees = 0.5f * 180f / MathF.PI;
        camera.NearPlane = MathF.Max(0.02f, framing.Distance - framing.Height);
        return camera;
    }

    private static Camera PortraitCamera(Vector3 feet, float modelYaw, float eyeHeight, float distance) => new()
    {
        Target = feet,
        Yaw = modelYaw + MathF.PI,
        Pitch = 0.02f,
        Distance = distance,
        EffectiveDistance = distance,
        EyeHeight = eyeHeight,
        FieldOfViewDegrees = 38f,
        AspectRatio = 1f,
        NearPlane = 0.05f,
        FarPlane = 100f,
    };

    private static Camera AuthoredPortraitCamera(M2PortraitCamera authored, Matrix4x4 modelTransform)
    {
        Vector3 position = Vector3.Transform(authored.Position, modelTransform);
        Vector3 target = Vector3.Transform(authored.Target, modelTransform);
        Vector3 up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, modelTransform));
        Vector3 forward = Vector3.Normalize(target - position);
        if (MathF.Abs(authored.Roll) > 0.00001f)
            up = Vector3.Normalize(Vector3.Transform(up,
                Matrix4x4.CreateFromAxisAngle(forward, authored.Roll)));

        float modelScale = Vector3.TransformNormal(Vector3.UnitX, modelTransform).Length();
        modelScale = float.IsFinite(modelScale) && modelScale > 0f ? modelScale : 1f;
        return new Camera
        {
            AuthoredPosition = position,
            AuthoredTarget = target,
            AuthoredUp = up,
            // gxumath treats the M2 value as diagonal FOV. At the portrait
            // path's 4:3 diagonal convention, vertical half-angle is 0.3 * fov.
            AuthoredVerticalFieldOfViewRadians = authored.FieldOfView * 0.6f,
            // The final 256x256 portrait surface is square-true; 4:3 only
            // participates in the diagonal-to-vertical crop conversion above.
            AspectRatio = 1f,
            NearPlane = MathF.Max(0.02f, authored.NearClip * modelScale),
            FarPlane = authored.FarClip * modelScale,
        };
    }

    private void WithPortraitLighting(Action draw)
    {
        if (_character is null) return;
        Vector3 sunDirection = _character.SunDirection;
        Vector3 sunColor = _character.SunColor;
        float sunIntensity = _character.SunIntensity;
        Vector3 ambientColor = _character.AmbientColor;
        float ambientIntensity = _character.AmbientIntensity;
        float fogStart = _character.FogStart;
        float fogEnd = _character.FogEnd;
        try
        {
            _character.SunDirection = Vector3.Normalize(new Vector3(0.25f, -0.45f, 0.85f));
            _character.SunColor = new Vector3(0.85f, 0.82f, 0.78f);
            _character.SunIntensity = 1f;
            _character.AmbientColor = new Vector3(0.58f, 0.56f, 0.54f);
            _character.AmbientIntensity = 1f;
            _character.FogStart = 1000f;
            _character.FogEnd = 2000f;
            draw();
        }
        finally
        {
            _character.SunDirection = sunDirection;
            _character.SunColor = sunColor;
            _character.SunIntensity = sunIntensity;
            _character.AmbientColor = ambientColor;
            _character.AmbientIntensity = ambientIntensity;
            _character.FogStart = fogStart;
            _character.FogEnd = fogEnd;
        }
    }

    private static void DrawPortrait(uint textureHandle, float size)
    {
        if (textureHandle == 0)
        {
            ImGui.Dummy(new Vector2(size, size));
            return;
        }

        // OpenGL's framebuffer origin is bottom-left; ImGui's image origin is top-left.
        // The Blizzard frame art supplies the circular chrome. AddImageRounded is deliberately
        // avoided: on this backend it produced a single textured fan triangle, not a full disc.
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = min + new Vector2(size, size);
        ImGui.GetWindowDrawList().AddImage((nint)textureHandle, min, max,
            new Vector2(0, 1), new Vector2(1, 0));
        ImGui.Dummy(new Vector2(size, size));
    }

    private static void DumpFailedPortrait(PortraitRenderTarget target, string name)
    {
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "portrait-diagnostics");
            string path = Path.Combine(directory, $"{name}-blank.png");
            target.SavePng(path);
            Console.WriteLine($"[portrait] failed FBO dumped to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[portrait] failed to dump {name}: {ex.Message}");
        }
    }

    private void DisposePortraits()
    {
        _playerPortrait?.Dispose();
        _targetPortrait?.Dispose();
        _paperDoll?.Dispose();
        _playerPortrait = null;
        _targetPortrait = null;
        _paperDoll = null;
    }
}
