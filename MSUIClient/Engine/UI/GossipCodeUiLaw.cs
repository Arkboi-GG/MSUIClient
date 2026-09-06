using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public static class GossipCodeUiLaw
{
    public const string PopupType = "GOSSIP_ENTER_CODE";
    public const string Prompt = "Please enter code:";
    public static readonly StaticPopupCoordinatorLaw.Definition Definition = new(PopupType,
        HideOnEscape: true, HasAccept: true, HasOnShow: true, HasOnHide: true,
        HasEditBox: true, HasEditBoxEnter: true, Exclusive: true);

    public sealed record Request(ulong Actor, GossipMenu Menu, uint ListId);
    public static bool StillCurrent(Request request, ulong actor, GossipMenu? menu) =>
        actor != 0 && request.Actor == actor && ReferenceEquals(request.Menu, menu) &&
        request.Menu.Options.Any(x => x.ListId == request.ListId && x.Coded);
}
