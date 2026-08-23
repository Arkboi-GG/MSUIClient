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
    public static readonly LogicalRect Divider = new(9, 131, 256, 32);
    public static readonly LogicalRect Name = new(20, 21, 170, 12);
    public static readonly LogicalRect Description = new(20, 35, 170, 92);
    public static readonly LogicalRect AtWarCheck = new(14, 143, 26, 26);
    public static readonly LogicalRect InactiveCheck = new(78, 143, 26, 26);
    public static readonly LogicalRect MainScreenCheck = new(14, 166, 26, 26);

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
