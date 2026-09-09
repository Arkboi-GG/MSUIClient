using System.Text;
using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

/// <summary>Six shipped item-enchantment sentences. A zero caster means faded, not self-cast.</summary>
public static class EnchantmentNoticeUiLaw
{
    public static string Text(CombatEnchantment notice, ulong actor, Func<ulong,string> unitName,
        Func<uint,string> itemName, Func<uint,string> enchantmentName, Func<string,string,string> globalString)
    {
        string enchant = enchantmentName(notice.EnchantmentId), item = itemName(notice.ItemEntry);
        string key, fallback; string[] values;
        if(notice.Caster == 0)
        {
            if(notice.Owner == actor)
            { key="ITEMENCHANTMENTREMOVESELF";fallback="%s has faded from your %s.";values=[enchant,item]; }
            else
            { key="ITEMENCHANTMENTREMOVEOTHER";fallback="%s has faded from %s's %s.";values=[enchant,unitName(notice.Owner),item]; }
        }
        else if(notice.Caster == actor)
        {
            if(notice.Owner == actor)
            { key="ITEMENCHANTMENTADDSELFSELF";fallback="You cast %s on your %s.";values=[enchant,item]; }
            else
            { key="ITEMENCHANTMENTADDSELFOTHER";fallback="You cast %s on %s's %s.";values=[enchant,unitName(notice.Owner),item]; }
        }
        else if(notice.Owner == actor)
        { key="ITEMENCHANTMENTADDOTHERSELF";fallback="%s casts %s on your %s.";values=[unitName(notice.Caster),enchant,item]; }
        else
        { key="ITEMENCHANTMENTADDOTHEROTHER";fallback="%s casts %s on %s's %s.";values=[unitName(notice.Caster),enchant,unitName(notice.Owner),item]; }
        // Substitute only the template. Literal %s in an authored name is not a new argument.
        string template=globalString(key,fallback);var result=new StringBuilder();int cursor=0,argument=0;
        while(template.IndexOf("%s",cursor,StringComparison.Ordinal) is int index && index>=0 && argument<values.Length)
        { result.Append(template.AsSpan(cursor,index-cursor));result.Append(values[argument++]);cursor=index+2; }
        return result.Append(template.AsSpan(cursor)).ToString();
    }
}
