namespace MSUIClient.Engine.UI;

/// <summary>LootFrame_OnShow's Lua-owned open fork. Normal non-empty loot is C-side and silent here.</summary>
public static class LootFrameUiLaw
{
    public const byte FishingLootType = 3;
    public const string EmptyOpenSound = "LOOTWINDOWOPENEMPTY";
    public const string FishingOpenSound = "FISHING REEL IN";
    public const string CorpseOverlay = @"Interface\TargetingFrame\TargetDead";
    public const string FishingOverlay = @"Interface\LootFrame\FishingLoot-Icon";
    public const string SoundCategory = "ui.loot";

    public readonly record struct OpenPresentation(string OverlayPath, string? SoundCue);

    public static OpenPresentation OnShow(byte lootType, int itemCount, uint gold)
    {
        int visibleRows = Math.Max(0, itemCount) + (gold > 0 ? 1 : 0);
        if (visibleRows == 0) return new(CorpseOverlay, EmptyOpenSound);
        if (lootType == FishingLootType) return new(FishingOverlay, FishingOpenSound);
        return new(CorpseOverlay, null);
    }
}
