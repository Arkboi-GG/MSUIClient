using System.Collections.ObjectModel;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly record struct PreparedSharedGameTooltipPhysicalLine(
        string Text,
        Vector2 Position);

    private readonly record struct PreparedSharedGameTooltipRow(
        string FontObject,
        uint Color,
        ReadOnlyCollection<PreparedSharedGameTooltipPhysicalLine> PhysicalLines);

    private readonly record struct PreparedSharedGameTooltipMoneyCoin(
        string AmountText,
        Vector2 NumberPosition,
        Vector2 IconMinimum,
        Vector2 IconMaximum,
        Vector2 UvMinimum,
        Vector2 UvMaximum,
        uint Tint);

    private readonly record struct PreparedSharedGameTooltipThicken(
        Vector2 Minimum,
        Vector2 Maximum,
        Vector4 Tint);

    /// <summary>
    /// A frame-local, immutable render receipt. The callback receives pixels, colors, texture
    /// handles, and health geometry only; it cannot observe a later world or tooltip mutation.
    /// </summary>
    private sealed record PreparedSharedGameTooltipRenderer(
        WowSkin Skin,
        Vector2 Position,
        Vector2 Size,
        float Scale,
        ReadOnlyCollection<PreparedSharedGameTooltipRow> Rows,
        ReadOnlyCollection<PreparedSharedGameTooltipMoneyCoin> MoneyCoins,
        uint MoneyTexture,
        PreparedSharedGameTooltipThicken Thicken,
        Vector4 BackdropFillTint,
        Vector4 BackdropEdgeTint,
        bool HealthVisible,
        Vector2 HealthMinimum,
        Vector2 HealthSize,
        float HealthFraction,
        uint HealthTexture,
        float Alpha);

    private static (Vector4 Fill, Vector4 Edge) SharedGameTooltipBackdropTints(float alpha)
    {
        float clamped = Math.Clamp(alpha, 0f, 1f);
        return (new Vector4(.09f, .09f, .19f, clamped),
            new Vector4(1f, 1f, 1f, clamped));
    }

    private static PreparedSharedGameTooltipThicken SharedGameTooltipThicken(
        Vector2 position,
        Vector2 size,
        float scale,
        float alpha)
    {
        Vector2 inset = new(5f * scale);
        return new(position + inset, position + size - inset,
            new Vector4(.09f, .09f, .19f, .4f * Math.Clamp(alpha, 0f, 1f)));
    }

    private static Vector4 SharedGameTooltipReactionColor(int? reaction) => reaction switch
    {
        1 or 2 => new(.8f, .3f, .22f, 1f),
        3 => new(.75f, .27f, 0f, 1f),
        4 => new(.9f, .7f, 0f, 1f),
        >= 5 and <= 8 => new(0f, .6f, .1f, 1f),
        _ => Vector4.One,
    };

    private static Vector4 SharedGameTooltipToneColor(
        GameTooltipTextTone tone,
        int? unitReaction)
    {
        return tone switch
        {
            GameTooltipTextTone.UnitReaction => SharedGameTooltipReactionColor(unitReaction),
            GameTooltipTextTone.Gold => new(1f, .82f, 0f, 1f),
            GameTooltipTextTone.White => Vector4.One,
            GameTooltipTextTone.Normal => new(1f, .82f, 0f, 1f),
            GameTooltipTextTone.Red => new(1f, 32f / 255f, 32f / 255f, 1f),
            GameTooltipTextTone.Green => new(0f, 1f, 0f, 1f),
            GameTooltipTextTone.LockOpen => new(64f / 255f, 192f / 255f, 64f / 255f, 1f),
            GameTooltipTextTone.OwnerColor => Vector4.One,
            _ => Vector4.One,
        };
    }

    /// <summary>The frozen UIParent-managed default BOTTOMRIGHT seat.</summary>
    private static Vector2 SharedGameTooltipDefaultAnchor(
        Vector2 display,
        Vector2 size,
        float scale,
        in UiParentManagedState managed)
    {
        UiParentManagedPlacement x = UiParentUiLaw.Resolve(
            UiParentManagedConsumer.ContainerOffsetX, managed);
        UiParentManagedPlacement y = UiParentUiLaw.Resolve(
            UiParentManagedConsumer.ContainerOffsetY, managed);
        return new(display.X - (x.X + 13f) * scale - size.X,
            display.Y - y.Y * scale - size.Y);
    }

    private static Vector2 SharedGameTooltipClampToScreen(
        Vector2 position,
        Vector2 size,
        Vector2 display)
    {
        float left = position.X;
        float right = left + size.X;
        if (left < 0f)
        {
            right -= left;
            left = 0f;
        }
        if (right > display.X)
        {
            left -= right - display.X;
            right = display.X;
        }

        float top = position.Y;
        float bottom = top + size.Y;
        if (bottom > display.Y)
        {
            top -= bottom - display.Y;
            bottom = display.Y;
        }
        if (top < 0f)
        {
            bottom -= top;
            top = 0f;
        }
        return new(left, top);
    }

    private PreparedSharedGameTooltipRenderer? PrepareSharedGameTooltipRenderer(
        GameTooltipRuntimeSnapshot snapshot,
        Vector2? ownerTopRight = null)
    {
        if (_skin is null || !snapshot.Lifecycle.Visible || snapshot.Lifecycle.Alpha <= 0f ||
            snapshot.Anchor is not (GameTooltipAnchorKind.DefaultBottomRight or
                GameTooltipAnchorKind.OwnerRight) ||
            snapshot.Anchor == GameTooltipAnchorKind.OwnerRight && ownerTopRight is null ||
            (snapshot.Lines.Length == 0 && snapshot.Money is null))
            return null;

        float scale = GameplayUiScale();
        float alpha = Math.Clamp(snapshot.Lifecycle.Alpha, 0f, 1f);
        int moneyRowIndex = snapshot.Money is null ? -1 : snapshot.Lines.Length;
        int logicalRowCount = snapshot.Lines.Length + (snapshot.Money is null ? 0 : 1);
        var physicalTexts = new string[logicalRowCount][];
        var fontObjects = new string[logicalRowCount];
        var widths = new float[logicalRowCount];
        var heights = new float[logicalRowCount];
        for (int i = 0; i < snapshot.Lines.Length; i++)
        {
            string fontObject = i == 0 ? "GameTooltipHeaderText" : "GameTooltipText";
            fontObjects[i] = fontObject;
            physicalTexts[i] = snapshot.Lines[i].Wrap
                ? GameTooltipUiLaw.WrapText(snapshot.Lines[i].Text,
                    GameTooltipUiLaw.NewbieWrapWidth * scale,
                    text => GameText.MeasureWidth(fontObject, text, scale))
                : [snapshot.Lines[i].Text];
            widths[i] = physicalTexts[i]
                .Select(text => GameText.MeasureWidth(fontObject, text, scale))
                .DefaultIfEmpty(0f).Max();
            heights[i] = physicalTexts[i].Length * GameText.LinePitch(fontObject, scale);
        }

        GameTooltipMoneyRowGeometry? moneyGeometry = null;
        string[] moneyTexts = [];
        if (snapshot.Money is GameTooltipMoneyParts money)
        {
            GameTooltipCoin[] visibleCoins = money.VisibleCoins();
            moneyTexts = visibleCoins.Select(coin => coin.Amount.ToString()).ToArray();
            float[] numberWidths = moneyTexts.Select(text =>
                GameText.MeasureWidth(GameTooltipUiLaw.MoneyFontObject, text, scale)).ToArray();
            moneyGeometry = GameTooltipUiLaw.MoneyRowGeometry(money, numberWidths, scale);
            fontObjects[moneyRowIndex] = "GameTooltipText";
            physicalTexts[moneyRowIndex] = [""];
            widths[moneyRowIndex] = moneyGeometry.ContentWidth;
            heights[moneyRowIndex] = GameText.EmPixels("GameTooltipText", scale);
        }

        float cursor = GameTooltipUiLaw.Padding * scale;
        var rowTops = new float[logicalRowCount];
        for (int i = 0; i < logicalRowCount; i++)
        {
            rowTops[i] = cursor;
            cursor += heights[i];
            if (i + 1 < logicalRowCount)
                cursor += GameTooltipUiLaw.LogicalRowGap * scale;
        }
        Vector2 size = new(widths.Max() + GameTooltipUiLaw.Padding * 2f * scale,
            cursor + GameTooltipUiLaw.Padding * scale);

        bool rightLeftShown = Enumerable.Range(36, 12).Any(slot => _actions[slot] is not null);
        bool rightRightShown = Enumerable.Range(24, 12).Any(slot => _actions[slot] is not null);
        var managed = new UiParentManagedState(
            BottomLeftShown: true,
            BottomRightShown: true,
            RightLeftShown: rightLeftShown,
            RightRightShown: rightRightShown,
            PetOrStanceShown: PetActionBarVisible,
            ReputationShown: false,
            MaxLevelShown: false);
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 position = snapshot.Anchor == GameTooltipAnchorKind.OwnerRight
            ? new Vector2(ownerTopRight!.Value.X, ownerTopRight.Value.Y - size.Y)
            : SharedGameTooltipDefaultAnchor(display, size, scale, managed);
        position = SharedGameTooltipClampToScreen(position, size, display);

        var rows = new PreparedSharedGameTooltipRow[logicalRowCount];
        for (int i = 0; i < logicalRowCount; i++)
        {
            GameTooltipTextTone tone = i < snapshot.Lines.Length
                ? snapshot.Lines[i].Tone
                : GameTooltipTextTone.White;
            Vector4 color = SharedGameTooltipToneColor(tone, snapshot.UnitReaction);
            color.W *= alpha;
            var lines = new PreparedSharedGameTooltipPhysicalLine[physicalTexts[i].Length];
            float linePitch = GameText.LinePitch(fontObjects[i], scale);
            for (int line = 0; line < physicalTexts[i].Length; line++)
                lines[line] = new(physicalTexts[i][line],
                    position + new Vector2(GameTooltipUiLaw.Padding * scale,
                        rowTops[i] + line * linePitch));
            rows[i] = new(fontObjects[i], ImGui.ColorConvertFloat4ToU32(color),
                Array.AsReadOnly(lines));
        }

        var moneyCoins = new PreparedSharedGameTooltipMoneyCoin[
            moneyGeometry?.Coins.Length ?? 0];
        if (moneyGeometry is not null)
        {
            float contentLeft = position.X + GameTooltipUiLaw.Padding * scale;
            float iconTop = position.Y + rowTops[moneyRowIndex] +
                (heights[moneyRowIndex] - GameTooltipUiLaw.MoneyCoinSize * scale) * .5f;
            float numberTop = GameText.BoxCenteredTop(GameTooltipUiLaw.MoneyFontObject,
                iconTop, GameTooltipUiLaw.MoneyCoinSize, scale);
            uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, alpha));
            for (int i = 0; i < moneyGeometry.Coins.Length; i++)
            {
                GameTooltipMoneyCoinGeometry coin = moneyGeometry.Coins[i];
                moneyCoins[i] = new(moneyTexts[i],
                    new Vector2(contentLeft + coin.NumberX, numberTop),
                    new Vector2(contentLeft + coin.IconX, iconTop),
                    new Vector2(contentLeft + coin.IconX + GameTooltipUiLaw.MoneyCoinSize * scale,
                        iconTop + GameTooltipUiLaw.MoneyCoinSize * scale),
                    new Vector2(coin.TexCoords.Left, coin.TexCoords.Top),
                    new Vector2(coin.TexCoords.Right, coin.TexCoords.Bottom), tint);
            }
        }
        uint moneyTexture = moneyGeometry is null
            ? 0
            : _gameplayArt?.Handle(GameTooltipUiLaw.MoneyTexturePath) ?? 0;

        bool healthVisible = snapshot.Health.Visible;
        uint maximum = Math.Max(1u, snapshot.Health.Maximum);
        float healthFraction = healthVisible
            ? Math.Clamp((float)snapshot.Health.Value / maximum, 0f, 1f)
            : 0f;
        Vector2 healthMinimum = position + new Vector2(2f * scale, size.Y + scale);
        Vector2 healthSize = new(size.X - 4f * scale, 8f * scale);
        uint healthTexture = _gameplayArt?.Handle(
            @"Interface\TargetingFrame\UI-TargetingFrame-BarFill") ?? 0;

        (Vector4 fillTint, Vector4 edgeTint) = SharedGameTooltipBackdropTints(alpha);
        PreparedSharedGameTooltipThicken thicken =
            SharedGameTooltipThicken(position, size, scale, alpha);
        return new(_skin, position, size, scale, Array.AsReadOnly(rows),
            Array.AsReadOnly(moneyCoins), moneyTexture, thicken, fillTint, edgeTint,
            healthVisible, healthMinimum, healthSize, healthFraction, healthTexture, alpha);
    }

    private void DrawPreparedSharedGameTooltip(PreparedSharedGameTooltipRenderer prepared)
    {
        ImGui.SetNextWindowPos(prepared.Position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(prepared.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool begun = ImGui.Begin("##shared-game-tooltip",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.Tooltip);
        ImGui.PopStyleVar(2);
        if (!begun)
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();
        float savedSkinScale = prepared.Skin.Scale;
        try
        {
            prepared.Skin.Scale = prepared.Scale;
            prepared.Skin.DrawBackdrop(draw, prepared.Position,
                prepared.Position + prepared.Size, WowSkin.Tooltip,
                prepared.BackdropFillTint, prepared.BackdropEdgeTint);
        }
        finally
        {
            prepared.Skin.Scale = savedSkinScale;
        }
        draw.AddRectFilled(prepared.Thicken.Minimum, prepared.Thicken.Maximum,
            ImGui.ColorConvertFloat4ToU32(prepared.Thicken.Tint));
        foreach (PreparedSharedGameTooltipRow row in prepared.Rows)
            foreach (PreparedSharedGameTooltipPhysicalLine line in row.PhysicalLines)
                GameText.Draw(draw, row.FontObject, line.Text, line.Position,
                    prepared.Scale, row.Color);

        foreach (PreparedSharedGameTooltipMoneyCoin coin in prepared.MoneyCoins)
            if (prepared.MoneyTexture != 0)
                draw.AddImage((nint)prepared.MoneyTexture, coin.IconMinimum, coin.IconMaximum,
                    coin.UvMinimum, coin.UvMaximum, coin.Tint);
        foreach (PreparedSharedGameTooltipMoneyCoin coin in prepared.MoneyCoins)
            GameText.Draw(draw, GameTooltipUiLaw.MoneyFontObject, coin.AmountText,
                coin.NumberPosition, prepared.Scale, coin.Tint);

        if (prepared.HealthVisible && prepared.HealthTexture != 0 &&
            prepared.HealthFraction > 0f)
        {
            Vector2 healthMaximum = new(
                prepared.HealthMinimum.X + prepared.HealthSize.X * prepared.HealthFraction,
                prepared.HealthMinimum.Y + prepared.HealthSize.Y);
            draw.AddImage((nint)prepared.HealthTexture, prepared.HealthMinimum, healthMaximum,
                Vector2.Zero, new Vector2(prepared.HealthFraction, 1f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 1f, 0f, prepared.Alpha)));
        }
        draw.PopClipRect();
        ImGui.End();
    }
}
