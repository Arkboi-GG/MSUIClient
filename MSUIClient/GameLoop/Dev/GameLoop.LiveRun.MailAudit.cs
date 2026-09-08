using System.Globalization;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // Bounded test gestures through shipping handlers. Dispatch is not a server verdict.
    private bool RunLiveMailAudit(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 2 && p[1] == "inspect")
        {
            Console.WriteLine($"[live-mail-state] actor=0x{ControlledGuid:X};open={_mailOpen};box=0x{_mailboxGuid:X};pending={_mailSendPending};confirmation={_mailConfirmation};count={_mail.Count}");
            foreach (MailRow row in _mail)
                Console.WriteLine($"[live-mail-row] id={row.Id};sender=0x{row.Sender:X};subject={row.Subject};item={row.ItemEntry};count={row.ItemCount};money={row.Money};cod={row.Cod}");
            return true;
        }
        if (p.Length != 3 || p[1] is not ("send-release" or "send-port" or "send-freeze" or "send-walk")) return false;
        if (p[1] == "send-release" && ControlledGuid == LocalPlayerGuid) return false;
        if (p[1] == "send-port" && (ControlledGuid != LocalPlayerGuid || _controller is null)) return false;
        if (p[1] == "send-freeze" && (!_freeView || OwnedActiveTacticalLock is not null)) return false;
        if (p[1] == "send-walk" && (_freeView || _controller is null)) return false;
        ulong sender = ControlledGuid;
        string subject = $"GI39-{p[1]}-{DateTime.Now:HHmmssfff}";
        if (!SendMailFlow(p[2], 0, 0, 0, subject, "September 8 asynchronous send audit")) return false;
        Console.WriteLine($"[live-mail-race] sender=0x{sender:X};receiver={p[2]};subject={subject};transition={p[1]};serverReplyRequired=true");
        if (p[1] == "send-release") RequestControlRelease(toFreecam: false);
        else if (p[1] == "send-freeze") RequestTacticalFreezeToggle();
        else if (p[1] == "send-walk") _liveInputHeld.Add(Silk.NET.Input.Key.W);
        else
        {
            var position = _controller!.Position;
            return SendGmCommand(string.Create(CultureInfo.InvariantCulture,
                $".go xyz {position.X + 50:R} {position.Y:R} {position.Z:R} {_config.Start.Map}"), "authorized-mail-range-race");
        }
        return true;
    }
}
