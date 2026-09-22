using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// A generic creator file picker: the audio importer's directory browser, made
// reusable (texture import, and whatever imports next). One instance, one
// callback; Escape/Cancel closes it.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private string? _creatorFilePickerTitle;
    private string[] _creatorFilePickerExtensions = [];
    private Action<string>? _creatorFilePickerOnPick;
    private string _creatorFilePickerDir = "";
    private string _creatorFilePickerError = "";

    private void OpenCreatorFilePicker(string title, string[] extensions, Action<string> onPick)
    {
        _creatorFilePickerTitle = title;
        _creatorFilePickerExtensions = extensions;
        _creatorFilePickerOnPick = onPick;
        _creatorFilePickerError = "";
        string remembered = Settings.Creator.ImportDirectory;
        _creatorFilePickerDir = remembered.Length > 0 && Directory.Exists(remembered)
            ? remembered
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } pictures &&
              Directory.Exists(pictures) ? pictures : Environment.CurrentDirectory;
    }

    private void CloseCreatorFilePicker()
    {
        _creatorFilePickerTitle = null;
        _creatorFilePickerOnPick = null;
    }

    private void DrawCreatorFilePicker()
    {
        if (_creatorFilePickerTitle is not { } title) return;
        bool close = false;
        float cs = CreatorUiScale;
        ImGui.SetNextWindowSize(new Vector2(560f * cs, 520f * cs), ImGuiCond.FirstUseEver);
        PushCreatorStyle();
        if (ImGui.Begin("###creator-file-picker", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            if (DrawCreatorPanelChrome(title)) close = true;
            ImGui.SetWindowFontScale(CreatorTextScale);
            BeginCreatorContent();

            ImGui.TextWrapped("Choose a file (" + string.Join(", ", _creatorFilePickerExtensions) +
                              "). It is copied into the design; MSUI never depends on this disk path afterwards.");
            if (_creatorFilePickerError.Length > 0)
                ImGui.TextWrapped($"Could not import: {_creatorFilePickerError}");

            ImGui.TextDisabled(_creatorFilePickerDir);
            if (ImGui.SmallButton("Up"))
            {
                try
                {
                    DirectoryInfo? parent = Directory.GetParent(_creatorFilePickerDir);
                    if (parent is not null) _creatorFilePickerDir = parent.FullName;
                }
                catch (Exception ex) { _creatorFilePickerError = ex.Message; }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel")) close = true;

            ImGui.BeginChild("##creator-file-list", new Vector2(0f, 380f * cs), true);
            try
            {
                var dirs = new List<string>();
                DirectoryInfo? parent = Directory.GetParent(_creatorFilePickerDir);
                if (parent is null && OperatingSystem.IsWindows())
                    dirs.AddRange(DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName));
                dirs.AddRange(Directory.EnumerateDirectories(_creatorFilePickerDir)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(300));

                string? nextDir = null;
                foreach (string dir in dirs)
                {
                    string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (name.Length == 0) name = dir;
                    if (ImGui.Selectable($"[{name}]")) nextDir = dir;
                }
                string? picked = null;
                foreach (string file in Directory.EnumerateFiles(_creatorFilePickerDir)
                             .Where(f => _creatorFilePickerExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                             .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(500))
                {
                    if (ImGui.Selectable(Path.GetFileName(file))) picked = file;
                }
                if (nextDir is not null) _creatorFilePickerDir = nextDir;
                if (picked is not null && _creatorFilePickerOnPick is { } onPick)
                {
                    _creatorFilePickerError = "";
                    Settings.Creator.ImportDirectory = _creatorFilePickerDir;
                    SettingsFile?.Save();
                    onPick(picked);
                    if (_creatorFilePickerError.Length == 0) close = true;
                }
            }
            catch (Exception ex) { _creatorFilePickerError = ex.Message; }
            ImGui.EndChild();

            EndCreatorContent();
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        PopCreatorStyle();
        if (close) CloseCreatorFilePicker();
    }
}
