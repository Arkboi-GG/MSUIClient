using System.Net.Sockets;
using System.Text;
using System.Text.Json;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: vmangos-ra <client-config.json> <transcript.txt> <command> [command ...]");
    return 2;
}

string configPath=Path.GetFullPath(args[0]);
string outputPath=Path.GetFullPath(args[1]);
using JsonDocument json=JsonDocument.Parse(File.ReadAllText(configPath));
JsonElement root=json.RootElement, server=root.GetProperty("server");
string host=root.GetProperty("realmdHost").GetString() ?? throw new InvalidDataException("realmdHost missing");
string account=server.GetProperty("account").GetString() ?? throw new InvalidDataException("server.account missing");
string password=server.GetProperty("password").GetString() ?? throw new InvalidDataException("server.password missing");

using var client=new TcpClient();
await client.ConnectAsync(host,3443);
using NetworkStream stream=client.GetStream();
var transcript=new StringBuilder();
transcript.Append(await ReadBurst(stream,TimeSpan.FromSeconds(3)));
await SendLine(stream,account);
transcript.Append(await ReadBurst(stream,TimeSpan.FromSeconds(3)));
await SendLine(stream,password);
transcript.Append(await ReadBurst(stream,TimeSpan.FromSeconds(4)));
foreach(string command in args.Skip(2))
{
    if(command.StartsWith("@listen=",StringComparison.OrdinalIgnoreCase)&&
       double.TryParse(command[8..],System.Globalization.CultureInfo.InvariantCulture,out double seconds)&&seconds>0)
    {
        transcript.AppendLine($"> @listen={seconds:R}");
        transcript.Append(await ReadWindow(stream,TimeSpan.FromSeconds(seconds)));
        continue;
    }
    transcript.AppendLine($"> {command}");
    await SendLine(stream,command);
    transcript.Append(await ReadBurst(stream,TimeSpan.FromSeconds(3)));
}

string safe=transcript.ToString().Replace(account,"[ACCOUNT]",StringComparison.OrdinalIgnoreCase)
    .Replace(password,"[REDACTED]",StringComparison.Ordinal);
safe=string.Join('\n',safe.Replace("\r\n","\n",StringComparison.Ordinal).Replace('\r','\n')
    .Split('\n').Select(line=>line.TrimEnd()))+'\n';
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath,safe,new UTF8Encoding(false));
Console.WriteLine($"[vmangos-ra] wrote {args.Length-2} command response(s) to {outputPath}");
return 0;

static async Task SendLine(NetworkStream stream,string text)
{
    byte[] bytes=Encoding.UTF8.GetBytes(text+"\r\n");
    await stream.WriteAsync(bytes);
    await stream.FlushAsync();
}

static async Task<string> ReadBurst(NetworkStream stream,TimeSpan maximum)
{
    var bytes=new List<byte>();
    byte[] buffer=new byte[8192];
    DateTime deadline=DateTime.UtcNow+maximum;
    DateTime idle=DateTime.UtcNow+TimeSpan.FromMilliseconds(350);
    while(DateTime.UtcNow<deadline)
    {
        if(stream.DataAvailable)
        {
            int count=await stream.ReadAsync(buffer);
            if(count==0) break;
            bytes.AddRange(buffer.AsSpan(0,count).ToArray());
            idle=DateTime.UtcNow+TimeSpan.FromMilliseconds(350);
        }
        else
        {
            if(bytes.Count>0&&DateTime.UtcNow>=idle) break;
            await Task.Delay(20);
        }
    }
    // Preserve printable server text while dropping Telnet negotiation/control bytes.
    var text=new StringBuilder();
    for(int i=0;i<bytes.Count;i++)
    {
        byte value=bytes[i];
        if(value==255&&i+2<bytes.Count) { i+=2; continue; }
        if(value is 9 or 10 or 13 || value>=32) text.Append((char)value);
    }
    return text.ToString();
}

static async Task<string> ReadWindow(NetworkStream stream,TimeSpan duration)
{
    var bytes=new List<byte>();
    byte[] buffer=new byte[8192];
    DateTime deadline=DateTime.UtcNow+duration;
    while(DateTime.UtcNow<deadline)
    {
        if(stream.DataAvailable)
        {
            int count=await stream.ReadAsync(buffer);
            if(count==0) break;
            bytes.AddRange(buffer.AsSpan(0,count).ToArray());
        }
        else await Task.Delay(20);
    }
    var text=new StringBuilder();
    for(int i=0;i<bytes.Count;i++)
    {
        byte value=bytes[i];
        if(value==255&&i+2<bytes.Count) { i+=2; continue; }
        if(value is 9 or 10 or 13 || value>=32) text.Append((char)value);
    }
    return text.ToString();
}
