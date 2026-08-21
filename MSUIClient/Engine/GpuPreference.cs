using System.Runtime.InteropServices;

namespace MSUIClient.Engine;

/// <summary>
/// Registers this executable for the HIGH-PERFORMANCE GPU in Windows' per-app
/// graphics preference (Settings > System > Display > Graphics), so hybrid
/// laptops (integrated + discrete) run the client on the discrete GPU.
///
/// A managed exe cannot export the classic NvOptimusEnablement symbol; this
/// registry key is the modern mechanism and covers OpenGL on current drivers.
/// The preference keys on the exe PATH, so it applies however the app is
/// launched (Visual Studio, terminal, double-click). Windows reads it at
/// process start, so a fresh copy's FIRST run registers and the next run rides
/// the discrete GPU. An existing entry — the user's explicit choice in the
/// Settings app — is never overwritten.
/// </summary>
public static class GpuPreference
{
    private const string SubKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string HighPerformance = "GpuPreference=2;";

    public static void RegisterHighPerformance()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Environment.ProcessPath is not { Length: > 0 } exe) return;
        // Under `dotnet MSUIClient.dll` the process is the shared dotnet.exe
        // host; tagging that would flip every .NET app on the machine.
        if (!Path.GetFileNameWithoutExtension(exe)
                .Equals("MSUIClient", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            // Value exists (RegGetValue succeeds) = someone already chose; respect it.
            uint size = 0;
            if (RegGetValue(HkeyCurrentUser, SubKey, exe, RrfRtAny,
                    out _, null, ref size) == 0) return;

            if (RegSetKeyValue(HkeyCurrentUser, SubKey, exe, RegSz, HighPerformance,
                    (uint)((HighPerformance.Length + 1) * sizeof(char))) == 0)
                Console.WriteLine("[gpu] registered for the high-performance GPU " +
                                  "(Windows per-app graphics preference; takes effect " +
                                  "on the next launch)");
        }
        catch
        {
            // Quality-of-life only - registry trouble must never stop the client.
        }
    }

    private static readonly nint HkeyCurrentUser = unchecked((nint)0x80000001);
    private const uint RegSz = 1;
    private const uint RrfRtAny = 0x0000ffff;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetKeyValue(nint hKey, string lpSubKey, string lpValueName,
        uint dwType, string lpData, uint cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegGetValue(nint hKey, string lpSubKey, string lpValue,
        uint dwFlags, out uint pdwType, byte[]? pvData, ref uint pcbData);
}
