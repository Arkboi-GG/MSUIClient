using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class ItemTextFrameClinicalChecks
{
    public static void Run()
    {
        Check(ItemTextFrameUiLaw.FrameOrigin(1.5f) == new Vector2(0, 156) &&
              ItemTextFrameUiLaw.FrameSize(1.5f) == new Vector2(576, 768) &&
              ItemTextFrameUiLaw.Icon == new ItemTextFrameUiLaw.LogicalRect(10, 8, 58, 58) &&
              ItemTextFrameUiLaw.Title == new ItemTextFrameUiLaw.LogicalRect(86, 19, 224, 14) &&
              ItemTextFrameUiLaw.Scroll == new ItemTextFrameUiLaw.LogicalRect(38, 76, 280, 355) &&
              ItemTextFrameUiLaw.Body == new ItemTextFrameUiLaw.LogicalRect(38, 91, 270, 304) &&
              ItemTextFrameUiLaw.BodyLineMin(new Vector2(38, 91), 3, 14, 7, 2) ==
                  new Vector2(38, 119) &&
              ItemTextFrameUiLaw.Close == new ItemTextFrameUiLaw.LogicalRect(323, 10, 32, 32),
            "item-text frame/scroll/body geometry drift");

        Check(ItemTextFrameUiLaw.TopLeftArt.EndsWith("UI-ItemText-TopLeft") &&
              ItemTextFrameUiLaw.TopRightArt.EndsWith("UI-SpellbookPanel-TopRight") &&
              ItemTextFrameUiLaw.BottomLeftArt.EndsWith("UI-ItemText-BotLeft") &&
              ItemTextFrameUiLaw.BottomRightArt.EndsWith("UI-SpellbookPanel-BotRight") &&
              ItemTextFrameUiLaw.MaterialArt("Stone", "BotRight") ==
                  @"Interface\ItemTextFrame\ItemText-Stone-BotRight",
            "item-text shell/material art drift");

        Check(ItemTextFrameUiLaw.TextColor("Stone") == new Vector4(1, 1, 1, 1) &&
              ItemTextFrameUiLaw.TextColor("Marble") == new Vector4(0, 0, 0, 1) &&
              ItemTextFrameUiLaw.TextColor("Silver") == new Vector4(.12f, .12f, .12f, 1) &&
              ItemTextFrameUiLaw.TextColor("Bronze") == new Vector4(.18f, .12f, .06f, 1) &&
              ItemTextFrameUiLaw.TitleColor("Parchment") == new Vector4(0, 0, 0, 1) &&
              ItemTextFrameUiLaw.TitleColor("Bronze") == new Vector4(.93f, .82f, 0, 1),
            "item-text exact material palette drift");

        Check(ItemTextFrameUiLaw.HasPaging(1, true) &&
              !ItemTextFrameUiLaw.HasPaging(1, false) &&
              ItemTextFrameUiLaw.HasPaging(2, false) &&
              !ItemTextFrameUiLaw.CanPrevious(1) && ItemTextFrameUiLaw.CanPrevious(2) &&
              ItemTextFrameUiLaw.CanNext(true) && !ItemTextFrameUiLaw.CanNext(false) &&
              ItemTextFrameUiLaw.ComposeBody("hello", "Nico") ==
                  "\nhello\n\nFrom,\nNico\n\n" &&
              ItemTextFrameUiLaw.VisibleText("<HTML><BODY><P>Hello &amp; bye</P></BODY></HTML>") ==
                  "Hello & bye",
            "item-text paging/creator/SimpleHTML law drift");

        IReadOnlyList<ItemTextFrameUiLaw.TextBlock> blocks =
            ItemTextFrameUiLaw.ComposeBlocks(
                "<HTML><BODY><H1 align=\"center\">Ranks</H1><BR/>" +
                "<P align=\"right\">Private</P></BODY></HTML>", null);
        Check(blocks.SequenceEqual(new[]
              {
                  new ItemTextFrameUiLaw.TextBlock("", ItemTextFrameUiLaw.TextAlignment.Left),
                  new ItemTextFrameUiLaw.TextBlock("Ranks", ItemTextFrameUiLaw.TextAlignment.Center),
                  new ItemTextFrameUiLaw.TextBlock("", ItemTextFrameUiLaw.TextAlignment.Left),
                  new ItemTextFrameUiLaw.TextBlock("Private", ItemTextFrameUiLaw.TextAlignment.Right),
                  new ItemTextFrameUiLaw.TextBlock("", ItemTextFrameUiLaw.TextAlignment.Left),
              }) &&
              ItemTextFrameUiLaw.BodyLineX(38, 270, 100,
                  ItemTextFrameUiLaw.TextAlignment.Center) == 123 &&
              ItemTextFrameUiLaw.BodyLineX(38, 270, 100,
                  ItemTextFrameUiLaw.TextAlignment.Right) == 208,
            "item-text SimpleHTML block alignment drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.ItemText.cs"));
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        string objects = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.GameObjects.cs"));
        string items = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "Items.cs"));
        string fields = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "ObjectFields.cs"));

        Check(runtime.Contains("ItemTextFrameUiLaw.FrameOrigin", StringComparison.Ordinal) &&
              runtime.Contains("ItemTextFontNormal", StringComparison.Ordinal) &&
              runtime.Contains("ItemTextQuery(textId, 0)", StringComparison.Ordinal) &&
              runtime.Contains("PageTextQuery(pageId, guid)", StringComparison.Ordinal) &&
              runtime.Contains("_pageTextPending.Add(pageId)", StringComparison.Ordinal) &&
              runtime.Contains("BuildPageTextQueryBody(pageId, guid)", StringComparison.Ordinal) &&
              runtime.Contains("read.Visited.Add", StringComparison.Ordinal) &&
              runtime.Contains("read.Visited.RemoveAt", StringComparison.Ordinal) &&
              runtime.Contains("ItemTextFrameUiLaw.MaterialArt", StringComparison.Ordinal) &&
              runtime.Contains("ItemTextFrameUiLaw.BodyLineMin", StringComparison.Ordinal) &&
              runtime.Contains("ItemTextFrameUiLaw.ComposeBlocks", StringComparison.Ordinal) &&
              runtime.Contains("ItemTextFrameUiLaw.BodyLineX", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              !runtime.Contains("BeginVanillaWindow(\"##item-text\", new Vector2",
                  StringComparison.Ordinal) &&
              !runtime.Contains("CMSG_READ_ITEM", StringComparison.Ordinal),
            "item-text renderer/cache path bypasses the law");

        Check(inventory.IndexOf("instance.Fields.ItemTextId != 0", StringComparison.Ordinal) <
                  inventory.IndexOf("InventoryUiLaw.UnwrapsGift", StringComparison.Ordinal) &&
              inventory.Contains("item.PageText != 0", StringComparison.Ordinal) &&
              objects.Contains("go.GameObjectType == 9", StringComparison.Ordinal) &&
              objects.Contains("LOCAL_ITEM_TEXT_OPEN", StringComparison.Ordinal) &&
              objects.Contains("OpenGameObjectText(go)", StringComparison.Ordinal) &&
              objects.Contains("_pageTextPending.Remove(id)", StringComparison.Ordinal) &&
              !objects.Contains("PageTextQuery(next)", StringComparison.Ordinal) &&
              !objects.Contains("_gameObjectPages.Clear()", StringComparison.Ordinal) &&
              items.Contains("item.PageMaterial = r.ReadU32()", StringComparison.Ordinal) &&
              fields.Contains("ITEM_FIELD_CREATOR = 10", StringComparison.Ordinal) &&
              fields.Contains("ItemCreator => GetGuid(ITEM_FIELD_CREATOR)", StringComparison.Ordinal),
            "item-text readable inventory/world/template routing drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
