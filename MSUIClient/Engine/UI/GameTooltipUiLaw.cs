using System.Text;

namespace MSUIClient.Engine.UI;

[Flags]
public enum GameTooltipClearScope
{
    None = 0,
    Lines = 1 << 0,
    Money = 1 << 1,
    Comparisons = 1 << 2,
    LiveUnit = 1 << 3,
    Health = 1 << 4,
    All = Lines | Money | Comparisons | LiveUnit | Health,
}

public enum GameTooltipAnchorKind
{
    OwnerRight,
    DefaultBottomRight,
    Cursor,
    Preserve,
}

public enum GameTooltipTextTone
{
    OwnerColor,
    Gold,
    White,
    Normal,
    Red,
    Green,
    LockOpen,
    UnitReaction,
}

public enum GameTooltipCoinKind
{
    Gold,
    Silver,
    Copper,
}

public readonly record struct GameTooltipOwnerKey(string Surface, ulong Identity);

public readonly record struct GameTooltipOwnerToken(
    GameTooltipOwnerKey Owner,
    long Generation)
{
    public bool IsValid => Generation > 0 && !string.IsNullOrWhiteSpace(Owner.Surface);
}

public readonly record struct GameTooltipLifecycleState(
    GameTooltipOwnerKey? Owner,
    long Generation,
    bool Visible,
    double? FadeStartedAt,
    double FadeSeconds,
    float Alpha);

public readonly record struct GameTooltipLifecycleTransition(
    GameTooltipLifecycleState State,
    GameTooltipOwnerToken Token,
    bool Accepted,
    bool Replaced,
    GameTooltipClearScope ClearScope);

public readonly record struct GameTooltipLine(
    string Text,
    GameTooltipTextTone Tone,
    bool Wrap = false);

public readonly record struct GameTooltipHealthState(
    bool Visible,
    uint Maximum,
    uint Value)
{
    public static readonly GameTooltipHealthState Hidden = new(false, 0, 0);
}

public sealed record GameTooltipContent(
    GameTooltipAnchorKind Anchor,
    GameTooltipLine[] Lines,
    string? LiveUnitToken = null,
    GameTooltipHealthState? Health = null,
    int? UnitReaction = null);

public readonly record struct GameTooltipCoin(
    GameTooltipCoinKind Kind,
    uint Amount);

public readonly record struct GameTooltipCoinTexCoords(
    float Left,
    float Top,
    float Right,
    float Bottom);

public readonly record struct GameTooltipMoneyCoinGeometry(
    GameTooltipCoin Coin,
    float NumberX,
    float NumberWidth,
    float IconX,
    float FrameWidth,
    GameTooltipCoinTexCoords TexCoords);

public sealed record GameTooltipMoneyRowGeometry(
    GameTooltipMoneyCoinGeometry[] Coins,
    float ContentWidth);

public readonly record struct GameTooltipMoneyParts(
    uint CopperValue,
    uint Gold,
    uint Silver,
    uint Copper,
    bool ShowGold,
    bool ShowSilver,
    bool ShowCopper)
{
    public GameTooltipCoin[] VisibleCoins()
    {
        var coins = new List<GameTooltipCoin>(3);
        if (ShowGold) coins.Add(new(GameTooltipCoinKind.Gold, Gold));
        if (ShowSilver) coins.Add(new(GameTooltipCoinKind.Silver, Silver));
        if (ShowCopper) coins.Add(new(GameTooltipCoinKind.Copper, Copper));
        return coins.ToArray();
    }
}

public readonly record struct GameTooltipNewbieContent(
    bool Visible,
    GameTooltipAnchorKind Anchor,
    GameTooltipLine[] Lines);

/// <summary>
/// The semantic unit snapshot consumed by the shared tooltip law. Presentation adapters remain
/// responsible for their existing fonts, pixels, anchors, and backdrops.
/// </summary>
public readonly record struct GameTooltipUnitSnapshot(
    string Token,
    bool Exists,
    string Name,
    string? Subtitle,
    int Level,
    uint PlayerLevel,
    int Reaction,
    bool IsPlayer,
    string? Race,
    string? Class,
    string? CreatureTypeName,
    uint Rank,
    bool Dead,
    string? FactionName,
    bool Pvp,
    bool Skinnable,
    bool Civilian,
    bool RacialLeader,
    uint Health,
    uint MaxHealth);

public readonly record struct GameTooltipGameObjectLine(
    string Text,
    GameTooltipTextTone Tone);

/// <summary>
/// A downstream game-object picker may supply this snapshot. This type deliberately does not
/// select, ray-cast, or retain a world game object.
/// </summary>
public readonly record struct GameTooltipGameObjectSnapshot(
    string Name,
    GameTooltipGameObjectLine[] Lines,
    bool CursorAnchored);

/// <summary>
/// Shared GameTooltip ownership, lifecycle, and content laws. These are coordinator semantics,
/// not authority to replace already-functional per-surface rendering.
/// </summary>
public static class GameTooltipUiLaw
{
    public const double WorldFadeSeconds = 0.5d;
    public const int UnknownHostileLevelDelta = 10;
    public const float Padding = 10f;
    public const float LogicalRowGap = 2f;
    public const float NewbieWrapWidth = 260f;
    public const float WrapWidthEpsilon = .25f;
    public const string MoneyFontObject = "NumberFontNormal";
    public const string MoneyTexturePath = @"Interface\MoneyFrame\UI-MoneyIcons";
    public const float MoneyCoinSize = 13f;
    public const float MoneyCoinGap = 4f;
    public const float MoneyRowInset = 4f;

    private static readonly uint[] GreyLevelBands =
        [4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 12, 12, 12, 12, 12, 12, 12];

    public static readonly GameTooltipLifecycleState EmptyLifecycle =
        new(null, 0, false, null, WorldFadeSeconds, 0f);

    public static GameTooltipLifecycleTransition Claim(
        in GameTooltipLifecycleState state,
        in GameTooltipOwnerKey owner)
    {
        if (string.IsNullOrWhiteSpace(owner.Surface))
            throw new ArgumentException("A tooltip owner surface is required.", nameof(owner));

        if (state.Owner is GameTooltipOwnerKey current && current == owner)
        {
            var retained = state with
            {
                Visible = true,
                FadeStartedAt = null,
                FadeSeconds = WorldFadeSeconds,
                Alpha = 1f,
            };
            return new(retained, new(owner, state.Generation), true, false,
                GameTooltipClearScope.None);
        }

        if (state.Generation == long.MaxValue)
            throw new InvalidOperationException("GameTooltip owner generation exhausted.");

        long generation = state.Generation + 1;
        var claimed = new GameTooltipLifecycleState(owner, generation, true, null,
            WorldFadeSeconds, 1f);
        return new(claimed, new(owner, generation), true, state.Owner is not null,
            GameTooltipClearScope.All);
    }

    public static bool IsOwned(
        in GameTooltipLifecycleState state,
        in GameTooltipOwnerToken token)
        => token.IsValid && state.Owner == token.Owner && state.Generation == token.Generation;

    /// <summary>Fresh content or an explicit show resurrects the exact owner at full alpha.</summary>
    public static GameTooltipLifecycleTransition Show(
        in GameTooltipLifecycleState state,
        in GameTooltipOwnerToken token)
    {
        if (!IsOwned(state, token))
            return new(state, token, false, false, GameTooltipClearScope.None);
        return new(state with
        {
            Visible = true,
            FadeStartedAt = null,
            FadeSeconds = WorldFadeSeconds,
            Alpha = 1f,
        }, token, true, false, GameTooltipClearScope.None);
    }

    public static GameTooltipLifecycleTransition ClearContent(
        in GameTooltipLifecycleState state,
        in GameTooltipOwnerToken token)
        => IsOwned(state, token)
            ? new(state, token, true, false, GameTooltipClearScope.All)
            : new(state, token, false, false, GameTooltipClearScope.None);

    /// <summary>
    /// Arms a fade only for the exact live owner. A stale leave cannot fade a replacement.
    /// </summary>
    public static GameTooltipLifecycleTransition BeginFade(
        in GameTooltipLifecycleState state,
        in GameTooltipOwnerToken token,
        double now,
        double fadeSeconds = WorldFadeSeconds)
    {
        if (!IsOwned(state, token) || !state.Visible)
            return new(state, token, false, false, GameTooltipClearScope.None);
        if (!double.IsFinite(fadeSeconds) || fadeSeconds <= 0d)
            return Hide(state, token);
        if (state.FadeStartedAt is not null)
            return new(state, token, true, false, GameTooltipClearScope.None);
        return new(state with
        {
            FadeStartedAt = now,
            FadeSeconds = fadeSeconds,
            Alpha = 1f,
        }, token, true, false, GameTooltipClearScope.None);
    }

    /// <summary>A real hide drops the owner and every owner-scoped content channel.</summary>
    public static GameTooltipLifecycleTransition Hide(
        in GameTooltipLifecycleState state,
        in GameTooltipOwnerToken token)
    {
        if (!IsOwned(state, token))
            return new(state, token, false, false, GameTooltipClearScope.None);
        return new(state with
        {
            Owner = null,
            Visible = false,
            FadeStartedAt = null,
            FadeSeconds = WorldFadeSeconds,
            Alpha = 0f,
        }, token, true, false, GameTooltipClearScope.All);
    }

    /// <summary>Advances the active generation's 1→0 fade and performs the terminal hide.</summary>
    public static GameTooltipLifecycleTransition TickFade(
        in GameTooltipLifecycleState state,
        double now)
    {
        if (state.Owner is not GameTooltipOwnerKey owner ||
            state.FadeStartedAt is not double started)
            return new(state, default, false, false, GameTooltipClearScope.None);

        var token = new GameTooltipOwnerToken(owner, state.Generation);
        double elapsed = Math.Max(0d, now - started);
        if (elapsed >= state.FadeSeconds)
            return Hide(state, token);

        float alpha = (float)Math.Clamp(1d - elapsed / state.FadeSeconds, 0d, 1d);
        return new(state with { Alpha = alpha }, token, true, false,
            GameTooltipClearScope.None);
    }

    /// <summary>
    /// Zero is a visible copper-zero row; clearing money is a separate owner-scoped operation.
    /// Nonzero values collapse zero denominations and retain gold-to-silver-to-copper order.
    /// </summary>
    public static GameTooltipMoneyParts? Money(uint copperValue)
    {
        uint gold = copperValue / 10_000;
        uint silver = copperValue % 10_000 / 100;
        uint copper = copperValue % 100;
        return new(copperValue, gold, silver, copper,
            ShowGold: gold > 0,
            ShowSilver: silver > 0,
            ShowCopper: copper > 0 || copperValue == 0);
    }

    public static GameTooltipCoinTexCoords MoneyCoinTexCoords(GameTooltipCoinKind kind) =>
        kind switch
        {
            GameTooltipCoinKind.Gold => new(0f, 0f, .25f, 1f),
            GameTooltipCoinKind.Silver => new(.25f, 0f, .5f, 1f),
            GameTooltipCoinKind.Copper => new(.5f, 0f, .75f, 1f),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>
    /// Resolves the measured money row in physical pixels. Each supplied number width must be
    /// measured with <see cref="MoneyFontObject"/> at <paramref name="scale"/>. The returned
    /// content width includes the authored four-pixel leading inset and trailing slot gap.
    /// </summary>
    public static GameTooltipMoneyRowGeometry MoneyRowGeometry(
        in GameTooltipMoneyParts money,
        IReadOnlyList<float> measuredNumberWidths,
        float scale = 1f)
    {
        ArgumentNullException.ThrowIfNull(measuredNumberWidths);
        if (!float.IsFinite(scale) || scale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(scale));

        GameTooltipCoin[] visible = money.VisibleCoins();
        if (measuredNumberWidths.Count != visible.Length)
            throw new ArgumentException(
                "One measured NumberFontNormal width is required for each visible coin.",
                nameof(measuredNumberWidths));

        float x = MoneyRowInset * scale;
        var coins = new GameTooltipMoneyCoinGeometry[visible.Length];
        for (int i = 0; i < visible.Length; i++)
        {
            float numberWidth = measuredNumberWidths[i];
            if (!float.IsFinite(numberWidth) || numberWidth < 0f)
                throw new ArgumentOutOfRangeException(nameof(measuredNumberWidths));
            float iconX = x + numberWidth;
            float frameWidth = numberWidth + MoneyCoinSize * scale;
            coins[i] = new(visible[i], x, numberWidth, iconX, frameWidth,
                MoneyCoinTexCoords(visible[i].Kind));
            x += frameWidth + MoneyCoinGap * scale;
        }
        return new(coins, x);
    }

    public static string MoneyString(uint copperValue)
    {
        uint gold = copperValue / 10_000;
        uint silver = copperValue % 10_000 / 100;
        uint copper = copperValue % 100;
        string result = gold > 0 ? $"{gold}g " : "";
        if (silver > 0 || gold > 0) result += $"{silver}s ";
        return result + $"{copper}c";
    }

    /// <summary>
    /// Word-wraps one logical tooltip row. Returned physical lines remain members of that row;
    /// callers add the two-pixel tooltip gap only after the complete returned group.
    /// </summary>
    public static string[] WrapText(
        string text,
        float maximumWidth,
        Func<string, float> measureWidth)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measureWidth);
        if (!float.IsFinite(maximumWidth) || maximumWidth <= 0f)
            throw new ArgumentOutOfRangeException(nameof(maximumWidth));

        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r", "", StringComparison.Ordinal)
                     .Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add("");
                continue;
            }

            var words = new List<(string Lead, string Text)>();
            int cursor = 0;
            while (cursor < paragraph.Length)
            {
                int leadStart = cursor;
                while (cursor < paragraph.Length && char.IsWhiteSpace(paragraph[cursor]))
                    cursor++;
                string lead = paragraph[leadStart..cursor];
                int wordStart = cursor;
                while (cursor < paragraph.Length && !char.IsWhiteSpace(paragraph[cursor]))
                    cursor++;
                if (cursor > wordStart)
                    words.Add((lead, paragraph[wordStart..cursor]));
            }
            if (words.Count == 0)
            {
                lines.Add(paragraph);
                continue;
            }

            string current = "";
            bool stoppedWithoutProgress = false;
            foreach ((string lead, string word) in words)
            {
                string candidate = current.Length == 0 ? word : current + lead + word;
                if (WrapFits(candidate, maximumWidth, measureWidth))
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = "";
                }

                string remainder = word;
                while (remainder.Length > 0)
                {
                    if (WrapFits(remainder, maximumWidth, measureWidth))
                    {
                        current = remainder;
                        break;
                    }

                    int split = LastFittingGlyph(remainder, maximumWidth, measureWidth);
                    if (split == 0)
                    {
                        stoppedWithoutProgress = true;
                        break;
                    }
                    lines.Add(remainder[..split]);
                    remainder = remainder[split..];
                }
                if (stoppedWithoutProgress) break;
            }
            if (current.Length > 0) lines.Add(current);
        }
        return lines.ToArray();
    }

    private static bool WrapFits(
        string text,
        float maximumWidth,
        Func<string, float> measureWidth)
    {
        float width = measureWidth(text);
        if (!float.IsFinite(width) || width < 0f)
            throw new ArgumentOutOfRangeException(nameof(measureWidth));
        return width <= maximumWidth + WrapWidthEpsilon;
    }

    private static int LastFittingGlyph(
        string text,
        float maximumWidth,
        Func<string, float> measureWidth)
    {
        int utf16Length = 0;
        int lastFitting = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            utf16Length += rune.Utf16SequenceLength;
            if (!WrapFits(text[..utf16Length], maximumWidth, measureWidth)) break;
            lastFitting = utf16Length;
        }
        return lastFitting;
    }

    /// <summary>The 1.12 detailed-tip branch without taking ownership of any renderer.</summary>
    public static GameTooltipNewbieContent NewbieTip(
        bool showDetailedTips,
        string? normalText,
        string newbieText,
        bool noNormalText)
    {
        if (showDetailedTips)
        {
            GameTooltipLine[] lines = normalText is not null
                ?
                [
                    new(normalText, GameTooltipTextTone.OwnerColor),
                    new(newbieText, GameTooltipTextTone.Normal, Wrap: true),
                ]
                : [new(newbieText, GameTooltipTextTone.OwnerColor, Wrap: true)];
            return new(true, GameTooltipAnchorKind.DefaultBottomRight, lines);
        }

        if (noNormalText || normalText is null)
            return new(false, GameTooltipAnchorKind.OwnerRight, []);
        return new(true, GameTooltipAnchorKind.OwnerRight,
            [new(normalText, GameTooltipTextTone.OwnerColor)]);
    }

    public static string? RankWord(uint rank) => rank switch
    {
        1 or 2 => "Elite",
        3 => "Boss",
        _ => null,
    };

    public static bool LevelReadsUnknown(in GameTooltipUnitSnapshot unit)
    {
        if (unit.IsPlayer) return false;
        if (unit.Rank == 3 || unit.Level <= 0) return true;
        return unit.Reaction is 1 or 2 &&
            (long)unit.PlayerLevel + UnknownHostileLevelDelta <= unit.Level;
    }

    public static bool UnitIsGrey(uint playerLevel, int unitLevel)
    {
        if (unitLevel < 0 || playerLevel <= (uint)unitLevel) return false;
        uint band = GreyLevelBands[(int)Math.Min(playerLevel / 5,
            (uint)GreyLevelBands.Length - 1)];
        return playerLevel - (uint)unitLevel > band;
    }

    public static string UnitLevelLine(in GameTooltipUnitSnapshot unit)
    {
        string levelText = LevelReadsUnknown(unit) ? "??" : unit.Level.ToString();
        string? classSlot;
        if (unit.Dead)
        {
            classSlot = "Corpse";
        }
        else if (unit.IsPlayer)
        {
            classSlot = (unit.Race, unit.Class) switch
            {
                ({ Length: > 0 } race, { Length: > 0 } @class) => $"{race} {@class}",
                ({ Length: > 0 } race, _) => race,
                (_, { Length: > 0 } @class) => @class,
                _ => null,
            };
        }
        else
        {
            classSlot = unit.Reaction is >= 1 and <= 4 &&
                !string.IsNullOrEmpty(unit.CreatureTypeName)
                ? unit.CreatureTypeName
                : null;
        }

        string? typeSlot = unit.IsPlayer ? "Player" : RankWord(unit.Rank);
        return (classSlot, typeSlot) switch
        {
            (not null, not null) => $"Level {levelText} {classSlot} ({typeSlot})",
            (not null, null) => $"Level {levelText} {classSlot}",
            (null, not null) => $"Level {levelText} ({typeSlot})",
            _ => $"Level {levelText}",
        };
    }

    public static GameTooltipHealthState UnitHealth(in GameTooltipUnitSnapshot unit)
    {
        if (!unit.Exists) return GameTooltipHealthState.Hidden;
        uint maximum = Math.Max(1u, unit.MaxHealth);
        return new(true, maximum, Math.Min(unit.Health, maximum));
    }

    public static GameTooltipContent? UnitContent(in GameTooltipUnitSnapshot unit)
    {
        if (!unit.Exists) return null;
        var lines = new List<GameTooltipLine>(8)
        {
            new(unit.Name, GameTooltipTextTone.UnitReaction),
        };
        if (!string.IsNullOrEmpty(unit.Subtitle))
            lines.Add(new(unit.Subtitle, GameTooltipTextTone.White));
        lines.Add(new(UnitLevelLine(unit), GameTooltipTextTone.White));
        if (!string.IsNullOrEmpty(unit.FactionName))
            lines.Add(new(unit.FactionName, GameTooltipTextTone.White));
        if (unit.Pvp) lines.Add(new("PvP", GameTooltipTextTone.White));
        if (unit.Skinnable) lines.Add(new("Skinnable", GameTooltipTextTone.Red));
        if (unit.Civilian && unit.Pvp && unit.Reaction is 1 or 2 &&
            UnitIsGrey(unit.PlayerLevel, unit.Level))
            lines.Add(new("Civilian", GameTooltipTextTone.Green));
        if (unit.RacialLeader && unit.Pvp)
            lines.Add(new("Leader", GameTooltipTextTone.White));

        string? liveToken = string.IsNullOrEmpty(unit.Token) ? null : unit.Token;
        return new(GameTooltipAnchorKind.DefaultBottomRight, lines.ToArray(), liveToken,
            UnitHealth(unit), unit.Reaction);
    }

    /// <summary>
    /// A pushed snapshot may change only the retained token's health channel. Static lines are
    /// deliberately not rebuilt, and a different token cannot mutate the current tooltip.
    /// </summary>
    public static bool TryLiveUnitHealth(
        string? retainedToken,
        in GameTooltipUnitSnapshot pushed,
        out GameTooltipHealthState health)
    {
        health = GameTooltipHealthState.Hidden;
        if (string.IsNullOrEmpty(retainedToken) || retainedToken != pushed.Token) return false;
        health = UnitHealth(pushed);
        return true;
    }

    public static GameTooltipContent GameObjectContent(
        in GameTooltipGameObjectSnapshot gameObject)
    {
        var lines = new GameTooltipLine[gameObject.Lines.Length + 1];
        lines[0] = new(gameObject.Name, GameTooltipTextTone.Gold);
        for (int i = 0; i < gameObject.Lines.Length; i++)
        {
            GameTooltipTextTone tone = gameObject.Lines[i].Tone switch
            {
                GameTooltipTextTone.White => GameTooltipTextTone.White,
                GameTooltipTextTone.Red => GameTooltipTextTone.Red,
                GameTooltipTextTone.LockOpen => GameTooltipTextTone.LockOpen,
                _ => throw new ArgumentException(
                    "Game-object requirement lines support White, Red, or LockOpen only.",
                    nameof(gameObject)),
            };
            lines[i + 1] = new(gameObject.Lines[i].Text, tone);
        }
        return new(gameObject.CursorAnchored
            ? GameTooltipAnchorKind.Cursor
            : GameTooltipAnchorKind.DefaultBottomRight, lines);
    }
}
