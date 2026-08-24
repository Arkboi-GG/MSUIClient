using System.Numerics;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const string WorldUnitTooltipSurface = "world-unit";
    private const string WorldGameObjectTooltipSurface = "world-gameobject";

    private sealed record GameTooltipRuntimeSnapshot(
        GameTooltipLifecycleState Lifecycle,
        GameTooltipAnchorKind Anchor,
        GameTooltipLine[] Lines,
        GameTooltipMoneyParts? Money,
        int ComparisonCount,
        string? LiveUnitToken,
        GameTooltipHealthState Health,
        int? UnitReaction,
        Vector2? Cursor);

    private sealed record SharedGameTooltipRenderer(
        GameTooltipOwnerToken Token,
        Action Renderer);

    private enum SharedGameTooltipLeaveMode
    {
        ImmediateHide,
        Fade,
    }

    private readonly record struct SharedGameTooltipLeavePolicy(
        SharedGameTooltipLeaveMode Mode,
        double FadeSeconds)
    {
        public static readonly SharedGameTooltipLeavePolicy ImmediateHide =
            new(SharedGameTooltipLeaveMode.ImmediateHide, 0d);

        public static SharedGameTooltipLeavePolicy Fade(double fadeSeconds)
        {
            if (!double.IsFinite(fadeSeconds) || fadeSeconds <= 0d)
                throw new ArgumentOutOfRangeException(nameof(fadeSeconds));
            return new(SharedGameTooltipLeaveMode.Fade, fadeSeconds);
        }

        public bool IsValid => Mode == SharedGameTooltipLeaveMode.ImmediateHide
            ? FadeSeconds == 0d
            : Mode == SharedGameTooltipLeaveMode.Fade &&
              double.IsFinite(FadeSeconds) && FadeSeconds > 0d;
    }

    private GameTooltipLifecycleState _sharedTooltipLifecycle =
        GameTooltipUiLaw.EmptyLifecycle;
    private GameTooltipAnchorKind _sharedTooltipAnchor = GameTooltipAnchorKind.Preserve;
    private GameTooltipLine[] _sharedTooltipLines = [];
    private GameTooltipMoneyParts? _sharedTooltipMoney;
    private int _sharedTooltipComparisonCount;
    private string? _sharedTooltipLiveUnitToken;
    private GameTooltipHealthState _sharedTooltipHealth = GameTooltipHealthState.Hidden;
    private int? _sharedTooltipUnitReaction;
    private Vector2? _sharedTooltipCursor;
    private SharedGameTooltipRenderer? _pendingSharedTooltipRenderer;
    private bool _sharedTooltipFrameOpen;
    private bool _sharedTooltipFrameResolved;
    private GameTooltipOwnerToken _sharedTooltipOpeningOwnerToken;
    private double _sharedTooltipFrameTime;
    private bool _sharedTooltipOpeningOwnerSeen;
    private bool _sharedTooltipDepartureApplied;
    private GameTooltipOwnerToken _sharedTooltipRetainedPolicyToken;
    private SharedGameTooltipLeavePolicy _sharedTooltipRetainedLeavePolicy;

    /// <summary>
    /// Opens the one-frame arbitration window. Surface adapters may retain their existing
    /// renderers by submitting one callback together with the exact owner generation they
    /// published. This coordinator never draws or manufactures tooltip content itself.
    /// </summary>
    private void BeginSharedGameTooltipFrame(double now)
    {
        TickSharedGameTooltip(now);
        _pendingSharedTooltipRenderer = null;
        _sharedTooltipFrameOpen = true;
        _sharedTooltipFrameResolved = false;
        _sharedTooltipOpeningOwnerToken = CurrentSharedGameTooltipOwnerToken();
        _sharedTooltipFrameTime = now;
        _sharedTooltipOpeningOwnerSeen = false;
        _sharedTooltipDepartureApplied = false;
    }

    /// <summary>
    /// Offers an existing renderer for this frame only. A stale generation cannot enter the
    /// pending slot, and a later exact-owner offer replaces the earlier callback without
    /// invoking either renderer during semantic publication.
    /// </summary>
    private bool QueueSharedGameTooltipRenderer(
        in GameTooltipOwnerToken token,
        in SharedGameTooltipLeavePolicy leavePolicy,
        Action renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (!leavePolicy.IsValid)
            throw new ArgumentOutOfRangeException(nameof(leavePolicy));
        if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved ||
            !SharedGameTooltipIsOwned(token))
            return false;
        _sharedTooltipRetainedPolicyToken = token;
        _sharedTooltipRetainedLeavePolicy = leavePolicy;
        if (token == _sharedTooltipOpeningOwnerToken)
            _sharedTooltipOpeningOwnerSeen = true;
        _pendingSharedTooltipRenderer = new(token, renderer);
        return true;
    }

    /// <summary>
    /// Applies the explicitly registered leave policy once when the exact owner present at frame
    /// open was not offered again. Replacement owners are never hidden or faded by a stale lease.
    /// </summary>
    private bool ApplyRetainedDeparture()
    {
        if (_sharedTooltipDepartureApplied) return false;
        _sharedTooltipDepartureApplied = true;
        GameTooltipOwnerToken opening = _sharedTooltipOpeningOwnerToken;
        if (!opening.IsValid || _sharedTooltipOpeningOwnerSeen ||
            opening != _sharedTooltipRetainedPolicyToken ||
            !SharedGameTooltipIsOwned(opening))
            return false;
        return _sharedTooltipRetainedLeavePolicy.Mode switch
        {
            SharedGameTooltipLeaveMode.ImmediateHide => HideSharedGameTooltip(opening),
            SharedGameTooltipLeaveMode.Fade => BeginSharedGameTooltipFade(opening,
                _sharedTooltipFrameTime, _sharedTooltipRetainedLeavePolicy.FadeSeconds),
            _ => false,
        };
    }

    /// <summary>
    /// Resolves at the tooltip stratum once per frame. The ownership check is repeated at the
    /// last possible moment so an owner replaced after submission cannot paint stale pixels.
    /// The callback is removed before invocation, making this seam render-only and non-reentrant.
    /// </summary>
    private bool ResolveAndDrawSharedGameTooltip()
    {
        if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved) return false;
        _sharedTooltipFrameResolved = true;
        ApplyRetainedDeparture();
        SharedGameTooltipRenderer? pending = _pendingSharedTooltipRenderer;
        _pendingSharedTooltipRenderer = null;
        if (pending is null || !SharedGameTooltipIsOwned(pending.Token)) return false;
        pending.Renderer();
        return true;
    }

    /// <summary>Closes the frame and drops any unpainted callback; callbacks never cross frames.</summary>
    private void EndSharedGameTooltipFrame()
    {
        ApplyRetainedDeparture();
        _pendingSharedTooltipRenderer = null;
        _sharedTooltipFrameOpen = false;
        _sharedTooltipFrameResolved = false;
    }

    private GameTooltipOwnerToken ClaimSharedGameTooltip(
        in GameTooltipOwnerKey owner)
    {
        GameTooltipLifecycleTransition transition =
            GameTooltipUiLaw.Claim(_sharedTooltipLifecycle, owner);
        ApplySharedGameTooltipTransition(transition);
        return transition.Token;
    }

    private bool SharedGameTooltipIsOwned(in GameTooltipOwnerToken token)
        => GameTooltipUiLaw.IsOwned(_sharedTooltipLifecycle, token);

    private GameTooltipOwnerToken CurrentSharedGameTooltipOwnerToken()
        => _sharedTooltipLifecycle.Owner is GameTooltipOwnerKey owner
            ? new(owner, _sharedTooltipLifecycle.Generation)
            : default;

    /// <summary>
    /// Replaces every owner-scoped semantic channel without changing any existing surface
    /// renderer. Unlike an ownership reclaim or a health-only push, a typed publication is a
    /// fresh Set-style transaction: old money, comparisons, live-unit state, and health cannot
    /// survive it. Publishing fresh content also cancels an in-flight fade for this exact owner.
    /// </summary>
    private bool PublishSharedGameTooltip(
        in GameTooltipOwnerToken token,
        GameTooltipContent content,
        Vector2? cursor = null)
    {
        GameTooltipLifecycleTransition clear =
            GameTooltipUiLaw.ClearContent(_sharedTooltipLifecycle, token);
        if (!clear.Accepted) return false;
        ApplySharedGameTooltipTransition(clear);

        GameTooltipLifecycleTransition transition =
            GameTooltipUiLaw.Show(_sharedTooltipLifecycle, token);
        if (!transition.Accepted)
            throw new InvalidOperationException(
                "An exact GameTooltip owner was rejected after its content clear.");
        ApplySharedGameTooltipTransition(transition);
        _sharedTooltipAnchor = content.Anchor;
        _sharedTooltipLines = [.. content.Lines];
        _sharedTooltipLiveUnitToken = content.LiveUnitToken;
        _sharedTooltipHealth = content.Health ?? GameTooltipHealthState.Hidden;
        _sharedTooltipUnitReaction = content.UnitReaction;
        _sharedTooltipCursor = content.Anchor == GameTooltipAnchorKind.Cursor ? cursor : null;
        return true;
    }

    private bool SetSharedGameTooltipMoney(
        in GameTooltipOwnerToken token,
        uint copperValue)
    {
        GameTooltipLifecycleTransition transition =
            GameTooltipUiLaw.Show(_sharedTooltipLifecycle, token);
        if (!transition.Accepted) return false;
        ApplySharedGameTooltipTransition(transition);
        _sharedTooltipMoney = GameTooltipUiLaw.Money(copperValue);
        return true;
    }

    private bool SetSharedGameTooltipComparisonCount(
        in GameTooltipOwnerToken token,
        int comparisonCount)
    {
        if (!SharedGameTooltipIsOwned(token)) return false;
        _sharedTooltipComparisonCount = Math.Clamp(comparisonCount, 0, 2);
        return true;
    }

    private bool ClearSharedGameTooltip(in GameTooltipOwnerToken token)
    {
        GameTooltipLifecycleTransition transition =
            GameTooltipUiLaw.ClearContent(_sharedTooltipLifecycle, token);
        ApplySharedGameTooltipTransition(transition);
        return transition.Accepted;
    }

    /// <summary>
    /// Adapts an already-functional opaque tooltip renderer without inventing a typed payload.
    /// The frame guard must run before Claim: an out-of-stratum offer cannot change ownership.
    /// Claim resurrects the exact fixed control, Clear mirrors SetOwner's full content reset, and
    /// Queue retains the prepared renderer for this frame with the authored immediate leave law.
    /// </summary>
    private bool OfferPreservedSharedGameTooltipRenderer(
        in GameTooltipOwnerKey owner,
        Action preparedRenderer)
    {
        ArgumentNullException.ThrowIfNull(preparedRenderer);
        if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved) return false;

        GameTooltipOwnerToken token = ClaimSharedGameTooltip(owner);
        if (!ClearSharedGameTooltip(token))
            throw new InvalidOperationException(
                "A freshly claimed GameTooltip owner rejected its exact content clear.");
        return QueueSharedGameTooltipRenderer(token,
            SharedGameTooltipLeavePolicy.ImmediateHide, preparedRenderer);
    }

    /// <summary>
    /// Publishes an ordinary owner-anchored GameTooltip through the shared classic renderer.
    /// Anchor/pivot are the resolved FrameXML SetOwner seat, so ANCHOR_LEFT and ANCHOR_RIGHT
    /// surfaces share one content/render path without falling back to an ImGui tooltip window.
    /// </summary>
    private bool OfferOwnerAnchoredSharedGameTooltip(
        in GameTooltipOwnerKey owner,
        GameTooltipLine[] lines,
        Vector2 anchor,
        Vector2 pivot)
    {
        if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved ||
            _skin is null || lines.Length == 0) return false;
        GameTooltipOwnerToken token = ClaimSharedGameTooltip(owner);
        if (!PublishSharedGameTooltip(token,
                new GameTooltipContent(GameTooltipAnchorKind.OwnerRight, lines)))
            throw new InvalidOperationException(
                "A freshly claimed owner-anchored GameTooltip rejected its content.");
        PreparedSharedGameTooltipRenderer? prepared =
            PrepareSharedGameTooltipRenderer(SharedGameTooltipSnapshot(), anchor, pivot);
        if (prepared is null) return false;
        return QueueSharedGameTooltipRenderer(token,
            SharedGameTooltipLeavePolicy.ImmediateHide,
            () => DrawPreparedSharedGameTooltip(prepared));
    }

    private bool BeginSharedGameTooltipFade(
        in GameTooltipOwnerToken token,
        double now,
        double fadeSeconds = GameTooltipUiLaw.WorldFadeSeconds)
    {
        GameTooltipLifecycleTransition transition =
            GameTooltipUiLaw.BeginFade(_sharedTooltipLifecycle, token, now, fadeSeconds);
        ApplySharedGameTooltipTransition(transition);
        return transition.Accepted;
    }

    private bool HideSharedGameTooltip(in GameTooltipOwnerToken token)
    {
        GameTooltipLifecycleTransition transition =
            GameTooltipUiLaw.Hide(_sharedTooltipLifecycle, token);
        ApplySharedGameTooltipTransition(transition);
        return transition.Accepted;
    }

    private void TickSharedGameTooltip(double now)
    {
        GameTooltipLifecycleTransition transition =
            GameTooltipUiLaw.TickFade(_sharedTooltipLifecycle, now);
        ApplySharedGameTooltipTransition(transition);
    }

    /// <summary>
    /// Health pushes update only an exact owner's retained token. Lines, money, comparisons,
    /// anchor, and fade state are intentionally unchanged.
    /// </summary>
    private bool TryRefreshSharedGameTooltipUnit(
        in GameTooltipOwnerToken token,
        in GameTooltipUnitSnapshot pushed)
    {
        if (!SharedGameTooltipIsOwned(token) ||
            !GameTooltipUiLaw.TryLiveUnitHealth(_sharedTooltipLiveUnitToken, pushed,
                out GameTooltipHealthState health))
            return false;
        _sharedTooltipHealth = health;
        return true;
    }

    /// <summary>
    /// Responder for the already-existing world-unit hover identity. It does not pick a unit or
    /// alter the current Targeting path.
    /// </summary>
    private bool TryShowWorldUnitGameTooltip(
        ulong unitGuid,
        in GameTooltipUnitSnapshot unit,
        out GameTooltipOwnerToken token)
    {
        token = default;
        GameTooltipContent? content = GameTooltipUiLaw.UnitContent(unit);
        if (content is null) return false;
        token = ClaimSharedGameTooltip(new(WorldUnitTooltipSurface, unitGuid));
        return PublishSharedGameTooltip(token, content);
    }

    /// <summary>
    /// Conditional responder only. A downstream picker must first supply a stable world-GO
    /// identity and cursor verdict; this method does not create that missing ingress.
    /// </summary>
    private bool TryShowWorldGameObjectGameTooltip(
        ulong gameObjectGuid,
        in GameTooltipGameObjectSnapshot gameObject,
        Vector2? cursor,
        out GameTooltipOwnerToken token)
    {
        token = default;
        if (gameObject.CursorAnchored && cursor is null) return false;
        GameTooltipContent content = GameTooltipUiLaw.GameObjectContent(gameObject);
        token = ClaimSharedGameTooltip(new(WorldGameObjectTooltipSurface, gameObjectGuid));
        return PublishSharedGameTooltip(token, content, cursor);
    }

    /// <summary>Moves a cursor-owned plate without rebuilding content or changing fade state.</summary>
    private bool MoveSharedGameTooltip(
        in GameTooltipOwnerToken token,
        Vector2 cursor)
    {
        if (!SharedGameTooltipIsOwned(token) ||
            _sharedTooltipAnchor != GameTooltipAnchorKind.Cursor ||
            !_sharedTooltipLifecycle.Visible)
            return false;
        _sharedTooltipCursor = cursor;
        return true;
    }

    /// <summary>
    /// Conditional detailed-tip responder. The typed default anchor is renderable now; terse
    /// OwnerRight remains semantic-only until a producer supplies immutable owner geometry.
    /// </summary>
    private bool TryShowNewbieGameTooltip(
        in GameTooltipOwnerKey owner,
        bool showDetailedTips,
        string? normalText,
        string newbieText,
        bool noNormalText,
        out GameTooltipOwnerToken token)
    {
        token = default;
        if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved) return false;
        GameTooltipNewbieContent newbie = GameTooltipUiLaw.NewbieTip(showDetailedTips,
            normalText, newbieText, noNormalText);
        if (!newbie.Visible || newbie.Anchor != GameTooltipAnchorKind.DefaultBottomRight)
            return false;
        if (_skin is null) return false;

        token = ClaimSharedGameTooltip(owner);
        if (!PublishSharedGameTooltip(token,
                new GameTooltipContent(newbie.Anchor, newbie.Lines)))
            throw new InvalidOperationException(
                "A freshly claimed newbie GameTooltip rejected its typed publication.");

        PreparedSharedGameTooltipRenderer? prepared =
            PrepareSharedGameTooltipRenderer(SharedGameTooltipSnapshot());
        if (prepared is null)
            throw new InvalidOperationException(
                "A presentable detailed newbie GameTooltip could not be prepared.");
        if (!QueueSharedGameTooltipRenderer(token,
                SharedGameTooltipLeavePolicy.ImmediateHide,
                () => DrawPreparedSharedGameTooltip(prepared)))
            throw new InvalidOperationException(
                "A prepared detailed newbie GameTooltip could not enter its frame slot.");
        return true;
    }

    private GameTooltipRuntimeSnapshot SharedGameTooltipSnapshot()
        => new(_sharedTooltipLifecycle, _sharedTooltipAnchor, [.. _sharedTooltipLines],
            _sharedTooltipMoney, _sharedTooltipComparisonCount,
            _sharedTooltipLiveUnitToken, _sharedTooltipHealth, _sharedTooltipUnitReaction,
            _sharedTooltipCursor);

    private void ApplySharedGameTooltipTransition(
        in GameTooltipLifecycleTransition transition)
    {
        _sharedTooltipLifecycle = transition.State;
        GameTooltipClearScope clear = transition.ClearScope;
        if ((clear & GameTooltipClearScope.Lines) != 0)
            _sharedTooltipLines = [];
        if ((clear & GameTooltipClearScope.Money) != 0)
            _sharedTooltipMoney = null;
        if ((clear & GameTooltipClearScope.Comparisons) != 0)
            _sharedTooltipComparisonCount = 0;
        if ((clear & GameTooltipClearScope.LiveUnit) != 0)
        {
            _sharedTooltipLiveUnitToken = null;
            _sharedTooltipUnitReaction = null;
        }
        if ((clear & GameTooltipClearScope.Health) != 0)
            _sharedTooltipHealth = GameTooltipHealthState.Hidden;
        if (clear == GameTooltipClearScope.All)
        {
            _sharedTooltipAnchor = GameTooltipAnchorKind.Preserve;
            _sharedTooltipCursor = null;
        }
    }
}
