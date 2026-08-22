using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private enum ItemTextSourceKind { Letter, Pages }

    private sealed class ItemTextReadSession
    {
        public required ItemTextSourceKind Kind { get; init; }
        public required ulong ObjectGuid { get; init; }
        public required string Title { get; set; }
        public ulong CreatorGuid { get; init; }
        public uint TextId { get; init; }
        public uint MaterialId { get; set; }
        public uint PageHead { get; set; }
        public List<uint> Visited { get; } = [];
    }

    private ItemTextReadSession? _itemTextRead;
    private PageTextMaterialCatalog? _pageTextMaterials;
    private bool _pageTextMaterialsLoaded;
    private float _itemTextScroll;

    private void EnsurePageTextMaterials()
    {
        if (_pageTextMaterialsLoaded) return;
        _pageTextMaterialsLoaded = true;
        _pageTextMaterials = _mpq is null ? null : PageTextMaterialCatalog.Load(_mpq);
    }

    private void OpenItemTextLetter(WorldEntity instance, ItemTemplate item)
    {
        if (_itemTextRead?.ObjectGuid == instance.Guid)
        {
            CloseItemText(playSound: true);
            return;
        }
        bool wasClosed = _itemTextRead is null;
        uint textId = instance.Fields.ItemTextId;
        ulong creator = instance.Fields.ItemCreator;
        _itemTextRead = new ItemTextReadSession
        {
            Kind = ItemTextSourceKind.Letter,
            ObjectGuid = instance.Guid,
            Title = item.Name,
            CreatorGuid = creator,
            TextId = textId,
            MaterialId = item.PageMaterial,
        };
        _itemTextScroll = 0;
        if (creator != 0 && !_playerNames.ContainsKey(creator)) _net?.NameQuery(creator);
        if (textId != 0 && !_mailBodies.ContainsKey(textId) && _mailBodyPending.Add(textId))
            _net?.ItemTextQuery(textId, 0);
        if (wasClosed) PlayUiSound(ItemTextFrameUiLaw.OpenSound, "ui.item-text");
        EmitInterface("item-text", "open", "OPENED_LETTER", instance.Guid,
            $"entry={instance.Entry};text={textId};creator={creator};wire=ITEM_TEXT_QUERY_ONLY");
    }

    private void OpenItemTextPages(ulong guid, string title, uint pageHead, uint materialId)
    {
        if (_itemTextRead?.ObjectGuid == guid)
        {
            CloseItemText(playSound: true);
            return;
        }
        bool wasClosed = _itemTextRead is null;
        var session = new ItemTextReadSession
        {
            Kind = ItemTextSourceKind.Pages,
            ObjectGuid = guid,
            Title = title,
            PageHead = pageHead,
            MaterialId = materialId,
        };
        if (pageHead != 0) session.Visited.Add(pageHead);
        _itemTextRead = session;
        _itemTextScroll = 0;
        QueryItemTextPageIfMissing(pageHead, guid);
        if (wasClosed) PlayUiSound(ItemTextFrameUiLaw.OpenSound, "ui.item-text");
        EmitInterface("item-text", "open", "OPENED_PAGES", guid,
            $"page={pageHead};material={materialId};wire=PAGE_TEXT_QUERY_ONLY");
    }

    private void OpenGameObjectText(WorldEntity go)
    {
        _gameObjectGuid = go.Guid;
        if (_gameObjectTemplates.TryGetValue(go.Entry, out GameObjectTemplate? template))
            OpenItemTextPages(go.Guid, template.Name, unchecked((uint)Math.Max(0, template.Data[0])),
                unchecked((uint)Math.Max(0, template.Data[2])));
        else
        {
            OpenItemTextPages(go.Guid, "...", 0, 0);
            RequireGameObjectTemplate(go);
        }
    }

    private bool CloseItemText(bool playSound)
    {
        if (_itemTextRead is null) return false;
        ulong guid = _itemTextRead.ObjectGuid;
        _itemTextRead = null;
        _itemTextScroll = 0;
        if (_gameObjectGuid == guid) _gameObjectGuid = 0;
        if (playSound) PlayUiSound(ItemTextFrameUiLaw.CloseSound, "ui.item-text");
        EmitInterface("item-text", "close", "CLOSED", guid, $"sound={playSound}");
        return true;
    }

    private void RefreshOpenGameObjectText(GameObjectTemplate template)
    {
        if (_itemTextRead is not { Kind: ItemTextSourceKind.Pages } read ||
            !_entities.TryGet(read.ObjectGuid, out WorldEntity go) || go.Entry != template.Entry) return;
        read.Title = template.Name;
        read.MaterialId = unchecked((uint)Math.Max(0, template.Data[2]));
        read.PageHead = unchecked((uint)Math.Max(0, template.Data[0]));
        if (read.Visited.Count == 0 && read.PageHead != 0) read.Visited.Add(read.PageHead);
        QueryItemTextPageIfMissing(read.PageHead, read.ObjectGuid);
    }

    private (uint Id, string Text, uint Next)? ItemTextPage(uint id)
    {
        foreach (var page in _gameObjectPages)
            if (page.Id == id) return page;
        return null;
    }

    private void QueryItemTextPageIfMissing(uint pageId, ulong guid)
    {
        if (pageId == 0 || ItemTextPage(pageId) is not null) return;
        bool sent = _net?.PageTextQuery(pageId) == true;
        EmitInterface("item-text", "page-query", sent ? "SENT" : "SEND_FAILED", guid,
            $"page={pageId};body={Convert.ToHexString(WorldSession.BuildPageTextQueryBody(pageId))}");
    }

    private void TurnItemTextPage(bool next)
    {
        if (_itemTextRead is not { Kind: ItemTextSourceKind.Pages } read || read.Visited.Count == 0) return;
        if (next)
        {
            var current = ItemTextPage(read.Visited[^1]);
            if (current is not { Next: not 0 }) return;
            read.Visited.Add(current.Value.Next);
            QueryItemTextPageIfMissing(current.Value.Next, read.ObjectGuid);
        }
        else
        {
            if (read.Visited.Count <= 1) return;
            read.Visited.RemoveAt(read.Visited.Count - 1);
        }
        _itemTextScroll = 0;
        PlayUiSound(ItemTextFrameUiLaw.PageSound, "ui.item-text");
    }

    private void DrawItemTextFrame()
    {
        if (_itemTextRead is not { } read || _gameplayArt is null) return;
        if (!BeginVanillaWindow("##item-text", ItemTextFrameUiLaw.FrameOrigin(1f),
                ItemTextFrameUiLaw.FrameSize(1f), out ImDrawListPtr draw, out Vector2 origin,
                out float scale)) { ImGui.End(); return; }

        DrawArt(draw, ItemTextFrameUiLaw.BookIcon, origin + ItemTextFrameUiLaw.Icon.Min * scale,
            ItemTextFrameUiLaw.Icon.Size, scale);
        DrawFourPieceShell(draw, origin, scale, ItemTextFrameUiLaw.TopLeftArt,
            ItemTextFrameUiLaw.TopRightArt, ItemTextFrameUiLaw.BottomLeftArt,
            ItemTextFrameUiLaw.BottomRightArt);

        EnsurePageTextMaterials();
        string material = _pageTextMaterials?.Name(read.MaterialId) ?? ItemTextFrameUiLaw.DefaultMaterial;
        if (!material.Equals(ItemTextFrameUiLaw.DefaultMaterial, StringComparison.Ordinal))
        {
            DrawMaterialCorner(draw, origin, scale, material, "TopLeft", ItemTextFrameUiLaw.MaterialTopLeft);
            DrawMaterialCorner(draw, origin, scale, material, "TopRight", ItemTextFrameUiLaw.MaterialTopRight);
            DrawMaterialCorner(draw, origin, scale, material, "BotLeft", ItemTextFrameUiLaw.MaterialBottomLeft);
            DrawMaterialCorner(draw, origin, scale, material, "BotRight", ItemTextFrameUiLaw.MaterialBottomRight);
        }

        uint titleColor = ImGui.ColorConvertFloat4ToU32(ItemTextFrameUiLaw.TitleColor(material));
        GameText.DrawCentered(draw, "GameFontNormal", read.Title,
            origin + ItemTextFrameUiLaw.Title.Min * scale + ItemTextFrameUiLaw.Title.Size * scale * .5f,
            scale, titleColor);

        string source = "";
        string? creator = null;
        int pageNumber = 1;
        bool hasNext = false;
        if (read.Kind == ItemTextSourceKind.Letter)
        {
            source = _mailBodies.GetValueOrDefault(read.TextId, "");
            if (read.CreatorGuid != 0)
                creator = _playerNames.GetValueOrDefault(read.CreatorGuid, "...");
        }
        else if (read.Visited.Count > 0)
        {
            pageNumber = read.Visited.Count;
            if (ItemTextPage(read.Visited[^1]) is { } page)
            {
                source = page.Text;
                hasNext = page.Next != 0;
            }
        }
        if (ItemTextFrameUiLaw.HasPaging(pageNumber, hasNext))
            GameText.DrawCentered(draw, "GameFontNormal", pageNumber.ToString(),
                origin + ItemTextFrameUiLaw.PageCenter * scale, scale, titleColor);

        string body = ItemTextFrameUiLaw.ComposeBody(ItemTextFrameUiLaw.VisibleText(source), creator);
        string[] lines = WrapTooltipText(body, "ItemTextFontNormal", scale,
            ItemTextFrameUiLaw.Body.Width * scale).ToArray();
        float pitch = GameText.LinePitch("ItemTextFontNormal", scale);
        float contentHeightLogical = lines.Length * GameText.LinePitch("ItemTextFontNormal", 1f);
        float maximum = ItemTextFrameUiLaw.MaximumScroll(contentHeightLogical);
        _itemTextScroll = Math.Clamp(_itemTextScroll, 0, maximum);
        Vector2 bodyMin = origin + ItemTextFrameUiLaw.Body.Min * scale;
        Vector2 bodyMax = bodyMin + ItemTextFrameUiLaw.Body.Size * scale;
        ImGui.SetCursorScreenPos(origin + ItemTextFrameUiLaw.Scroll.Min * scale);
        ImGui.InvisibleButton("##item-text-scroll", ItemTextFrameUiLaw.Scroll.Size * scale);
        if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            _itemTextScroll = Math.Clamp(_itemTextScroll - ImGui.GetIO().MouseWheel * 3f *
                GameText.LinePitch("ItemTextFontNormal", 1f), 0, maximum);
        draw.PushClipRect(bodyMin, bodyMax, true);
        uint textColor = ImGui.ColorConvertFloat4ToU32(ItemTextFrameUiLaw.TextColor(material));
        for (int i = 0; i < lines.Length; i++)
            GameText.Draw(draw, "ItemTextFontNormal", lines[i],
                bodyMin + new Vector2(0, i * pitch - _itemTextScroll * scale), scale, textColor);
        draw.PopClipRect();

        if (maximum > 0)
            DrawVanillaScrollBar(draw, "##item-text-bar",
                origin + ItemTextFrameUiLaw.ScrollBar.Min * scale,
                ItemTextFrameUiLaw.ScrollBar.Height, scale,
                (int)MathF.Round(_itemTextScroll), (int)MathF.Ceiling(maximum),
                value => _itemTextScroll = value);

        if (ItemTextFrameUiLaw.CanPrevious(pageNumber))
        {
            DrawItemTextPageButton(draw, origin, scale, previous: true);
            GameText.Draw(draw, "GameFontNormal", "Prev",
                origin + ItemTextFrameUiLaw.PrevLabel * scale, scale, titleColor);
        }
        if (ItemTextFrameUiLaw.CanNext(hasNext))
        {
            DrawItemTextPageButton(draw, origin, scale, previous: false);
            GameText.DrawRightAligned(draw, "GameFontNormal", "Next",
                origin + ItemTextFrameUiLaw.NextLabelRight * scale, scale, titleColor);
        }
        DrawImageButton(draw, "##item-text-close", origin + ItemTextFrameUiLaw.Close.Min * scale,
            ItemTextFrameUiLaw.Close.Size * scale,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) CloseItemText(playSound: true);
        ImGui.End();
    }

    private void DrawMaterialCorner(ImDrawListPtr draw, Vector2 origin, float scale,
        string material, string corner, ItemTextFrameUiLaw.LogicalRect rect) =>
        DrawArt(draw, ItemTextFrameUiLaw.MaterialArt(material, corner),
            origin + rect.Min * scale, rect.Size, scale);

    private void DrawItemTextPageButton(ImDrawListPtr draw, Vector2 origin, float scale, bool previous)
    {
        ItemTextFrameUiLaw.LogicalRect rect = previous ? ItemTextFrameUiLaw.Prev : ItemTextFrameUiLaw.Next;
        string stem = previous ? "UI-SpellbookIcon-PrevPage" : "UI-SpellbookIcon-NextPage";
        DrawImageButton(draw, previous ? "##item-text-prev" : "##item-text-next",
            origin + rect.Min * scale, rect.Size * scale,
            $@"Interface\Buttons\{stem}-Up", $@"Interface\Buttons\{stem}-Down",
            @"Interface\Buttons\UI-Common-MouseHilight");
        if (ImGui.IsItemClicked()) TurnItemTextPage(!previous);
    }
}
