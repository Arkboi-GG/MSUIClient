using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private string? _characterLoginFailureMessage;

    private bool PrepareCharacterLoginFailureDialog()
    {
        if (_net?.TryTakeCharacterLoginFailure(out _) == true)
        {
            _characterLoginFailureMessage = "Unable to enter the world. Please try again.";
            _loginFailureDismissed = false;
            CloseDeleteConfirm();
            CancelPendingWorldCurtain();
        }
        return _characterLoginFailureMessage is not null;
    }

    private void DrawCharacterLoginFailureDialog(ImDrawListPtr draw, Vector2 display, float scale)
    {
        if (_characterLoginFailureMessage is not { } message) return;
        DrawLoginFailureDialog(draw, display, scale, message);
        if (_loginFailureDismissed) _characterLoginFailureMessage = null;
    }
}
