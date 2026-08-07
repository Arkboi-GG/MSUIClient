namespace MSUIClient.Engine.UI;

public enum PartyInviteDismissal
{
    Accept,
    DeclineButton,
    EscapeOrTimeout,
    ServerCancel,
}

public readonly record struct PartyInviteWireCount(int Accept, int Decline);

public static class PartyFrameUiLaw
{
    public const int MemberCount = 4;
    public const float FrameWidth = 128f;
    public const float FrameHeight = 53f;
    public const float FirstX = 10f;
    public const float FirstY = 128f;
    public const float PetlessStride = 63f;
    public const float InviteTimeoutSeconds = 60f;

    public const byte Online = 0x01;
    public const byte Pvp = 0x02;
    public const byte Dead = 0x04;
    public const byte Ghost = 0x08;
    public const byte PvpFfa = 0x10;

    public static float MemberY(int zeroBasedIndex)
    {
        if (zeroBasedIndex is < 0 or >= MemberCount)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));
        return FirstY + zeroBasedIndex * PetlessStride;
    }

    public static bool Has(byte status, byte bit) => (status & bit) != 0;

    // PartyMemberFrame.lua uses a one-second triangle with 0.5-second legs between
    // 127/255 and 1.0. The caller applies it only to a living, connected member at <=20% HP.
    public static float LowHealthAlpha(float seconds)
    {
        float t = seconds - MathF.Floor(seconds);
        const float low = 127f / 255f;
        return t < .5f
            ? 1f - t * (1f - low) * 2f
            : low + (t - .5f) * (1f - low) * 2f;
    }

    // The reference's explicit Decline button calls DeclineGroup in OnCancel and then again in
    // OnHide. Escape/timeout only run OnHide. Accept sets the guard before OnHide.
    public static PartyInviteWireCount InviteWires(PartyInviteDismissal dismissal) => dismissal switch
    {
        PartyInviteDismissal.Accept => new(1, 0),
        PartyInviteDismissal.DeclineButton => new(0, 2),
        PartyInviteDismissal.EscapeOrTimeout => new(0, 1),
        _ => new(0, 0),
    };
}
