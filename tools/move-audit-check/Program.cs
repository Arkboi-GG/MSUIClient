using System.Diagnostics;
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var p = Process.Start(new ProcessStartInfo("dotnet", $"run --project \"{Path.Combine(root,"tools","move-audit","move-audit.csproj")}\" -c Release -- --check \"{Path.Combine(root,"movement-scenarios","baseline")}\" \"{Path.Combine(root,"movement-scenarios","expected")}\"") { UseShellExecute=false });
p!.WaitForExit(); return p.ExitCode;
