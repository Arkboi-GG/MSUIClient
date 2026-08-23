using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Current Benilla ReputationFrame detail-dialog geometry and wire-state rules.</summary>
public static class ReputationFrameUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size)
    {
        public Vector2 Max => Min + Size;
    }

    public readonly record struct CheckGeometry(ScreenRect Hit, Vector2 MarkMin,
        Vector2 MarkSize, Vector2 LabelPosition);

    public const byte Visible = 0x01;
    public const byte AtWar = 0x02;
    public const byte Header = 0x08;
    public const byte PeaceForced = 0x10;
    public const byte Inactive = 0x20;
    public const int VisibleRows = 15;
    public const int RowPitch = 26;
    public const int WatchedNone = -1;
    public const uint InactiveHeaderKey = uint.MaxValue;

    // ReputationDetailFrame: TOPLEFT to CharacterFrame TOPRIGHT (-33,-28), 212x203.
    public static readonly Vector2 DetailOffset = new(351, -28);
    public static readonly Vector2 DetailSize = new(212, 203);
    public static readonly LogicalRect Close = new(177, 3, 32, 32);
    public static readonly LogicalRect DetailArt = new(11, 11, 256, 128);
    public static readonly LogicalRect Corner = new(174, 7, 32, 32);
    public static readonly LogicalRect Divider = new(9, 131, 256, 32);
    public static readonly LogicalRect Name = new(20, 21, 170, 12);
    public static readonly LogicalRect Description = new(20, 35, 170, 92);
    public static readonly LogicalRect AtWarCheck = new(14, 143, 26, 26);
    public static readonly LogicalRect InactiveCheck = new(78, 143, 26, 26);
    public static readonly LogicalRect MainScreenCheck = new(14, 166, 26, 26);

    // CheckedTexture and label seats used by the authored reputation switches.
    public const float SwordMarkX = 3;
    public const float SwordMarkY = -5;
    public const float SwordMarkSize = 32;
    public const float CheckLabelX = 24;
    public const float CheckLabelY = 7;

    public static ScreenRect DetailScreenRect(Vector2 characterOrigin, float scale) =>
        new(characterOrigin + DetailOffset * scale, DetailSize * scale);

    public static ScreenRect ToScreenRect(Vector2 origin, LogicalRect logical, float scale) =>
        new(origin + logical.Min * scale, logical.Size * scale);

    public static CheckGeometry Check(Vector2 origin, LogicalRect logical, float scale,
        bool sword)
    {
        ScreenRect hit = ToScreenRect(origin, logical, scale);
        Vector2 markMin = sword
            ? hit.Min + new Vector2(SwordMarkX, SwordMarkY) * scale
            : hit.Min;
        Vector2 markSize = sword ? new Vector2(SwordMarkSize) * scale : hit.Size;
        return new(hit, markMin, markSize,
            hit.Min + new Vector2(CheckLabelX, CheckLabelY) * scale);
    }

    public static bool IsVisible(byte flags) => (flags & Visible) != 0;
    public static bool IsHeader(byte flags) => (flags & Header) != 0;
    public static bool IsAtWar(byte flags) => (flags & AtWar) != 0;
    public static bool IsInactive(byte flags) => (flags & Inactive) != 0;
    public static bool CanToggleAtWar(byte flags, int standing) =>
        (flags & PeaceForced) == 0 && standing >= -3000;
    public static byte WithAtWar(byte flags, bool enabled) =>
        enabled ? (byte)(flags | AtWar) : (byte)(flags & ~AtWar);
    public static byte WithInactive(byte flags, bool enabled) =>
        enabled ? (byte)(flags | Inactive) : (byte)(flags & ~Inactive);

    public static byte[] SlotAndFlagBody(uint slot, bool enabled) =>
        [(byte)slot, (byte)(slot >> 8), (byte)(slot >> 16), (byte)(slot >> 24), enabled ? (byte)1 : (byte)0];

    public static byte[] WatchedBody(int slot) =>
        [(byte)slot, (byte)(slot >> 8), (byte)(slot >> 16), (byte)(slot >> 24)];
}
