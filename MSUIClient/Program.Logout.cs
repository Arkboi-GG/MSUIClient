using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private enum LogoutDialogKind { None, Camp, Quit }

    private const string LogoutPopupId = "##msui-logout-countdown";
    private bool _logoutQuitting;
    private bool _logoutAwaitingResponse;
    private LogoutDialogKind _logoutDialog;
    private long _logoutDeadline;

    private bool LogoutUiActive => _logoutAwaitingResponse || _logoutDialog != LogoutDialogKind.None;

    private void SetLogoutDialog(LogoutDialogKind next)
    {
        bool wasVisible = _logoutDialog != LogoutDialogKind.None;
        bool willBeVisible = next != LogoutDialogKind.None;
        if (_logoutDialog == next) return;
        _logoutDialog = next;
        string cue = GameMenuUiLaw.PopupVisibilitySound(wasVisible, willBeVisible);
        if (cue.Length > 0) PlayUiSound(cue);
    }

    private void RequestLogout(bool quitting)
    {
        if (LogoutUiActive) return;
        _logoutQuitting = quitting;
        _settingsOpen = false;
        ImGui.CloseCurrentPopup();
        PlayUiSound(quitting ? "igMainMenuQuit" : "igMainMenuLogout");

        if (_net?.LogoutRequest() == true)
        {
            _logoutAwaitingResponse = true;
            Console.WriteLine($"[logout] requested (quitting={quitting}); awaiting SMSG_LOGOUT_RESPONSE");
            return;
        }

        // There is no character session to leave. Quit remains a local process action; Logout is
        // deliberately a no-op, matching the reference glue-screen behavior.
        _logoutQuitting = false;
        if (quitting) _quitRequested = true;
    }

    private void ApplyLogoutResponse(byte[] body)
    {
        LogoutResponse response = LogoutResponse.Parse(body);
        LogoutResponseAction action = LogoutUiLaw.Decide(response, _logoutQuitting);
        Console.WriteLine($"[logout] response reason={response.Reason} instant={response.Instant}; action={action}");
        switch (action)
        {
            case LogoutResponseAction.Refused:
                _logoutAwaitingResponse = false;
                _logoutQuitting = false;
                SetLogoutDialog(LogoutDialogKind.None);
                ShowUiError(LogoutUiLaw.RefusedText);
                break;
            case LogoutResponseAction.AwaitCompletion:
                // No popup. SMSG_LOGOUT_COMPLETE is already on its way.
                SetLogoutDialog(LogoutDialogKind.None);
                _logoutAwaitingResponse = true;
                break;
            case LogoutResponseAction.ShowCampCountdown:
            case LogoutResponseAction.ShowQuitCountdown:
                _logoutAwaitingResponse = false;
                SetLogoutDialog(action == LogoutResponseAction.ShowQuitCountdown
                    ? LogoutDialogKind.Quit
                    : LogoutDialogKind.Camp);
                _logoutDeadline = Stopwatch.GetTimestamp() +
                    (long)(LogoutUiLaw.CountdownSeconds * Stopwatch.Frequency);
                break;
        }
    }

    private void ApplyLogoutCancelAck()
    {
        _logoutAwaitingResponse = false;
        _logoutQuitting = false;
        SetLogoutDialog(LogoutDialogKind.None);
        _logoutDeadline = 0;
        Console.WriteLine("[logout] server cancelled countdown");
    }

    private void CancelLogoutCountdown()
    {
        if (_logoutDialog == LogoutDialogKind.None) return;
        SetLogoutDialog(LogoutDialogKind.None);
        _logoutDeadline = 0;
        _logoutAwaitingResponse = false;
        _logoutQuitting = false;
        _net?.LogoutCancel();
        Console.WriteLine("[logout] cancellation requested");
    }

    private bool TryCancelLogoutOnEscape()
    {
        if (_logoutDialog == LogoutDialogKind.None) return false;
        CancelLogoutCountdown();
        return true;
    }

    private void ApplyLogoutComplete()
    {
        bool quit = _logoutQuitting;
        _logoutAwaitingResponse = false;
        _logoutQuitting = false;
        SetLogoutDialog(LogoutDialogKind.None);
        _logoutDeadline = 0;
        Console.WriteLine($"[logout] complete (quitting={quit})");
        if (quit)
        {
            _quitRequested = true;
            return;
        }
        ReturnToCharacterSelectAfterLogout();
    }

    private void ReturnToCharacterSelectAfterLogout()
    {
        CancelRealPortalHandoff("logout returned to character select");
        TearDownWorldContent();
        _entities.Clear();
        _combat.Clear();
        _actions.Clear();
        ResetPetActionBar();
        ResetPlayerAuras();
        ResetTargeting();
        ResetParty();
        ResetCombatFeedback();
        ResetLoot();
        ResetGameObjects();
        ResetRestXp();
        ResetDeathRez();
        ResetHearth();
        ResetTaxi();
        ResetGossip();
        ResetQuestSession(clearStatusStore: true);
        ResetMail();
        ResetAuction();
        ResetGuild();
        ResetTabard();
        _settingsOpen = false;
        _worldEntryTransitionStage = 0;
        _worldLoadStarted = false;
        _worldLoading = false;
        _loadScreen?.Dispose();
        _loadScreen = null;
        if (_character is not null) _character.Enabled = false;

        // The entry hand-off consumes the booth avatar and disposes the booth. A clean logout
        // recreates that glue-owned renderer so the refreshed roster has its native background.
        _booth?.Dispose();
        _booth = null;
        try
        {
            if (_gl is not null && _mpq is not null)
                _booth = new GlueBooth(_gl, _mpq, _config, _assetWorkers, _uploads);
        }
        catch (Exception ex) { Console.WriteLine($"[logout] booth recreation failed: {ex.Message}"); }
        _selectedChar = 0;
        _charSelectionRestored = false;
    }

    private void DrawLogoutModal()
    {
        if (_logoutDialog == LogoutDialogKind.None || _skin is null) return;

        float remaining = (float)(Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _logoutDeadline).TotalSeconds);
        if (remaining <= 0f)
        {
            // The local timeout only narrates the server's timer. It must never manufacture a
            // completion or send a cancellation after reaching zero.
            SetLogoutDialog(LogoutDialogKind.None);
            _logoutDeadline = 0;
            _logoutAwaitingResponse = true;
            return;
        }

        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new Vector2(360f, 96f) * s;
        Vector2 origin = new((display.X - size.X) * .5f, 128f * s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.SetNextWindowFocus();
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin(LogoutPopupId, flags)) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        dl.PopClipRect();
        bool quitting = _logoutDialog == LogoutDialogKind.Quit;
        GameText.DrawCentered(dl, "GameFontNormal",
            LogoutUiLaw.CountdownText(quitting, remaining),
            origin + new Vector2(180f, 34f) * s, s);

        bool primary = DrawLogoutPopupButton(dl, quitting ? "Exit now" : "Cancel",
            origin + new Vector2(quitting ? 42f : 116f, 66f) * s, s, "primary");
        bool cancel = quitting && DrawLogoutPopupButton(dl, "Cancel",
            origin + new Vector2(190f, 66f) * s, s, "cancel");
        ImGui.End();

        if (primary)
        {
            if (quitting)
            {
                SetLogoutDialog(LogoutDialogKind.None);
                _logoutDeadline = 0;
                _quitRequested = true;
            }
            else CancelLogoutCountdown();
        }
        else if (cancel) CancelLogoutCountdown();
    }

    private bool DrawLogoutPopupButton(ImDrawListPtr dl, string caption, Vector2 at, float s, string id)
    {
        Vector2 size = new Vector2(128f, 20f) * s;
        ImGui.SetCursorScreenPos(at);
        bool clicked = ImGui.InvisibleButton($"##logout-{id}", size);
        bool held = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        uint art = _skin!.TextureHandle(held ? "dialog.button.down" : "dialog.button.up");
        if (art != 0) dl.AddImage((nint)art, at, at + size, Vector2.Zero, new Vector2(1f, .625f));
        if (hovered)
        {
            uint hi = _skin.TextureHandle("dialog.button.hi");
            if (hi != 0) dl.AddImage((nint)hi, at, at + size, Vector2.Zero, new Vector2(1f, .625f));
        }
        GameText.DrawCentered(dl, hovered ? "DialogButtonHighlightText" : "DialogButtonNormalText",
            caption, at + size * .5f, s);
        return clicked;
    }
}
