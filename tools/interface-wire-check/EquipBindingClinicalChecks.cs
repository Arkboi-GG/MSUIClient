using MSUIClient;
using MSUIClient.Engine.UI;

internal static class EquipBindingClinicalChecks
{
    public static void Run()
    {
        Check(EquipBindingUiLaw.NeedsConfirmation(2, false, true) &&
            !EquipBindingUiLaw.NeedsConfirmation(2, true, true) &&
            !EquipBindingUiLaw.NeedsConfirmation(2, false, false) &&
            !EquipBindingUiLaw.NeedsConfirmation(1, false, true) &&
            !EquipBindingUiLaw.NeedsConfirmation(3, false, true),
            "only usable, unbound BoE items prompt; loot/use binding predicates differ");
        for (int bag = 0; bag < 256; bag++)
        for (int slot = 0; slot < 256; slot++)
        {
            var wire = new InventoryUiLaw.WirePosition((byte)bag, (byte)slot);
            Check(EquipBindingUiLaw.IsEquipPosition(wire) ==
                (bag == 255 && (slot <= 22 || slot is >= 63 and <= 68)),
                "equip election must include equipped bags but exclude backpack and bank contents");
            if (EquipBindingUiLaw.FromWire((byte)bag, (byte)slot) is { } pos)
                Check(InventoryUiLaw.ToWire(pos.Container, pos.Slot) == wire,
                    "auto-equip coordinates must round-trip without changing the target slot");
        }
        foreach (bool auto in new[] { false, true })
        {
            var definition = EquipBindingUiLaw.Definition(auto);
            var shown = StaticPopupCoordinatorLaw.Show(StaticPopupCoordinatorLaw.Slots.Empty,
                definition, playerDeadOrGhost: false);
            Check(definition.HasOnHide && definition.Exclusive && definition.HideOnEscape &&
                EquipBindingUiLaw.Visible(shown.Slots) is not null,
                "bind popup must own cancellation, hiding, and replacement");
            var cancelled = StaticPopupCoordinatorLaw.Escape(shown.Slots);
            Check(cancelled.Effects.All(x => x.Kind != StaticPopupCoordinatorLaw.EffectKind.Accept) &&
                cancelled.Effects.Any(x => x.Kind == StaticPopupCoordinatorLaw.EffectKind.OnHide),
                "Escape must discard equip intent without accepting it");
        }
        string root = ClientConfig.FindRepoRoot();
        string Read(string path) => SourceText.Read(Path.Combine(root, path));
        string flow = Read("MSUIClient/GameLoop/Panels/GameLoop.EquipBinding.cs");
        string inventory = Read("MSUIClient/GameLoop/Panels/GameLoop.Inventory.cs");
        Check(flow.Contains("ControlledGuid == pending.Actor") &&
            flow.Contains("?.Guid == pending.SourceGuid") &&
            flow.Contains("== pending.DestinationGuid") &&
            flow.Contains("ActionsFor(ControlledGuid).KnownSpells") &&
            flow.Contains("if (pending is null || !EquipBindingStillValid(pending)) return;") &&
            Read("MSUIClient/GameLoop/Panels/GameLoop.Pet.cs").Contains("ClearEquipBinding();") &&
            inventory.Contains("ClearEquipBinding();"),
            "acceptance must retain actor and item identities, and body/session teardown cancels");
        Check(!inventory.Contains("_net.AutoEquipItem(") &&
            !Read("MSUIClient/GameLoop/Panels/GameLoop.CharacterPage.cs").Contains("_net.AutoEquipItem(") &&
            inventory.Contains("EquipBindingUiLaw.IsEquipPosition(plan.Source) != EquipBindingUiLaw.IsEquipPosition(plan.Destination)") &&
            inventory.Contains("? carried : target"),
            "every auto-equip and both directions of equipment swaps must use the bind gate");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
