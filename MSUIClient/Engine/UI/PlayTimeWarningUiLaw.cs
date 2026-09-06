using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public static class PlayTimeWarningUiLaw
{
    public static string? Text(PlayTimeWarningPacket packet, Func<string, string, string> globalString)
    {
        string key, fallback;
        switch (packet.Flag)
        {
            case 0x1000:
                key = "ERR_APPROACHING_PARTIAL_PLAY_TIME";
                fallback = "You have %s until you enter tired time. Your rewards will be cut in half.";
                break;
            case 0x2000:
                key = "ERR_APPROACHING_NO_PLAY_TIME";
                fallback = "You have %s until you enter unhealthy time, at which point you will no longer receive experience or loot until you have logged out for 5 hours.";
                break;
            case 0x80000000:
                return globalString("ERR_UNHEALTHY_TIME", "You are in unhealthy time, you should log off now.");
            default: return null;
        }
        return globalString(key, fallback).Replace("%s", ChatFrameLaw.FormatDuration((uint)Math.Max(0, packet.SecondsLeft)));
    }
}
