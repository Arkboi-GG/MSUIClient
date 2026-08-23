using System.Text;
using MSUIClient.Engine;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private string _cameraPoseIdentity = "";
    private string _cameraPosePath = "";

    private void LoadCameraPoseForWorldEntry()
    {
        if (_net is null || string.IsNullOrWhiteSpace(_net.RealmName) ||
            string.IsNullOrWhiteSpace(_net.PlayerName)) return;
        string identity = $"{_net.RealmName}\n{_net.PlayerName}";
        if (_cameraPoseIdentity == identity) return;

        // A different identity should ordinarily arrive only after the previous logout edge. Save
        // defensively before changing the path so a reconnect race cannot assign one pose to another.
        if (_cameraPoseIdentity.Length > 0) SaveCameraPoseForSession(forgetIdentity: true);
        _cameraPoseIdentity = identity;
        _cameraPosePath = Path.Combine(_config.RepoRoot, "camera",
            CameraPoseLaw.CharacterFileName(_net.RealmName, _net.PlayerName));
        if (!File.Exists(_cameraPosePath)) return;

        try
        {
            Camera camera = _window.Camera;
            CameraPoseLaw.Pose pose = CameraPoseLaw.Parse(File.ReadAllText(_cameraPosePath),
                camera.MinDistance, camera.MaxDistance, Camera.PitchLimit);
            if (pose.Distance is float distance)
                camera.Distance = camera.EffectiveDistance = distance;
            if (pose.PitchRadians is float pitch) camera.Pitch = pitch;
            Console.WriteLine($"[camera] restored pose from {_cameraPosePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[camera] restore failed: {ex.Message}");
        }
    }

    private void SaveCameraPoseForSession(bool forgetIdentity = false)
    {
        string path = _cameraPosePath;
        try
        {
            if (path.Length > 0)
            {
                Camera camera = _window.Camera;
                WriteCameraPoseAtomic(path,
                    CameraPoseLaw.Render(camera.Distance, camera.Pitch));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[camera] save failed: {ex.Message}");
        }
        finally
        {
            if (forgetIdentity)
            {
                _cameraPoseIdentity = "";
                _cameraPosePath = "";
            }
        }
    }

    private static void WriteCameraPoseAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
