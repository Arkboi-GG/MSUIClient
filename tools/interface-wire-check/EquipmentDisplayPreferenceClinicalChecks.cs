using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class EquipmentDisplayPreferenceClinicalChecks
{
    public static void Run()
    {
        Check(EquipmentDisplayPreferenceLaw.HideHelm == 0x400 &&
              EquipmentDisplayPreferenceLaw.HideCloak == 0x800 &&
              EquipmentDisplayPreferenceLaw.HelmShown(0) &&
              EquipmentDisplayPreferenceLaw.CloakShown(0) &&
              !EquipmentDisplayPreferenceLaw.HelmShown(0x400) &&
              !EquipmentDisplayPreferenceLaw.CloakShown(0x800),
            "PLAYER_FLAGS equipment-display bits drift");

        Check(!EquipmentDisplayPreferenceLaw.EquipmentSlotShown(0, 0x400) &&
              EquipmentDisplayPreferenceLaw.EquipmentSlotShown(14, 0x400) &&
              !EquipmentDisplayPreferenceLaw.EquipmentSlotShown(14, 0x800) &&
              EquipmentDisplayPreferenceLaw.EquipmentSlotShown(1, 0xc00) &&
              !EquipmentDisplayPreferenceLaw.InventoryTypeShown(1, 0x400) &&
              !EquipmentDisplayPreferenceLaw.InventoryTypeShown(16, 0x800),
            "head/back equipment-slot or inventory-type filtering drift");

        Check(!EquipmentDisplayPreferenceLaw.DressUpPieceShown(0, false, 0x400) &&
              EquipmentDisplayPreferenceLaw.DressUpPieceShown(0, true, 0x400) &&
              !EquipmentDisplayPreferenceLaw.DressUpPieceShown(14, false, 0x800) &&
              EquipmentDisplayPreferenceLaw.DressUpPieceShown(14, true, 0x800),
            "Dressing Room must hide worn pieces but show explicit substitutions");

        var belief = new EquipmentDisplayPreferenceController();
        Check(belief.Observe(0) && belief.HelmShown && belief.CloakShown,
            "first descriptor observation did not establish shown defaults");
        Check(belief.Request(EquipmentDisplayPreference.Helm, false) ==
                  EquipmentDisplayPreference.Helm && !belief.HelmShown &&
              belief.Request(EquipmentDisplayPreference.Helm, false) is null,
            "set-to-flip gate or optimistic helm belief drift");
        Check(!belief.Observe(0) && !belief.HelmShown,
            "repeated stale wire value erased the optimistic belief");
        Check(belief.Observe(0x400) && !belief.HelmShown && belief.CloakShown &&
              belief.Observe(0xc00) && !belief.CloakShown,
            "real PLAYER_FLAGS edges did not overwrite optimistic belief");

        var fields = new ObjectFields();
        fields.SetU32(ObjectFields.PLAYER_FLAGS, 0xc00);
        Check(!fields.PlayerShowsHelm && !fields.PlayerShowsCloak,
            "typed PLAYER_FLAGS display accessors drift");

        Check((ushort)Op.CMSG_TOGGLE_HELM == 0x02b9 &&
              (ushort)Op.CMSG_TOGGLE_CLOAK == 0x02ba &&
              WorldSession.BuildToggleHelmBody().Length == 0 &&
              WorldSession.BuildToggleCloakBody().Length == 0,
            "equipment-display opcode or empty-body wire shape drift");

        CheckRuntimeWiring();
    }

    private static void CheckRuntimeWiring()
    {
        string root = ClientConfig.FindRepoRoot();
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Settings.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string renderer = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "PlayerRenderer.cs"));
        string dressUp = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.DressUp.cs"));
        string search = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "UI",
            "OptionsSearchUiLaw.cs"));

        Check(settings.IndexOf("Show Cloak", StringComparison.Ordinal) <
                  settings.IndexOf("Show Helm", StringComparison.Ordinal) &&
              settings.Contains("_equipmentDisplayPreferences.Request", StringComparison.Ordinal) &&
              settings.Contains("_net?.ToggleHelm()", StringComparison.Ordinal) &&
              settings.Contains("_net?.ToggleCloak()", StringComparison.Ordinal),
            "Options rows lost reference order, optimistic set gate, or network sends");
        Check(net.Contains("playerFlagsBefore != updatedPlayer.Fields.PlayerFlags",
                  StringComparison.Ordinal) &&
              net.Contains("_equipmentDisplayPreferences.Observe", StringComparison.Ordinal) &&
              net.Contains("EquipmentDisplayPreferenceLaw.InventoryTypeShown",
                  StringComparison.Ordinal) &&
              net.Contains("EquipmentDisplayPreferenceLaw.EquipmentSlotShown",
                  StringComparison.Ordinal),
            "self/controlled descriptor-edge equipment rebuild seam drift");
        Check(renderer.Contains("EquipmentDisplayPreferenceLaw.EquipmentSlotShown",
                  StringComparison.Ordinal) &&
              renderer.Contains("EquipmentDisplayPreferenceLaw.HideHelm",
                  StringComparison.Ordinal) &&
              renderer.Contains("sb.Append(\"hidden:\")", StringComparison.Ordinal),
            "remote-player public flag filtering or appearance signature drift");
        Check(dressUp.Contains("EquipmentDisplayPreferenceLaw.DressUpPieceShown",
                  StringComparison.Ordinal) &&
              search.Contains("\"Show Cloak\"", StringComparison.Ordinal) &&
              search.Contains("\"Show Helm\"", StringComparison.Ordinal),
            "Dressing Room or searchable Options binding drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
