using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private PetPersonalityCatalog? _petPersonalities;
    private bool _petPersonalitiesLoaded;

    private void DrawPetHappiness(WorldEntity pet, float scale)
    {
        if (!PetMenuUiLaw.Predicates(pet.Fields.SummonedBy, ControlledGuid,
                pet.Fields.UnitFlags).CanAbandon) return;
        if (!_petPersonalitiesLoaded && _mpq is not null)
        {
            _petPersonalitiesLoaded = true;
            if (_mpq.ReadFile(PetPersonalityCatalog.MpqPath) is { } bytes)
                _petPersonalities = PetPersonalityCatalog.Parse(bytes);
        }
        uint raw = pet.Fields.GetU32(ObjectFields.UNIT_POWER1 + 4) ?? 0;
        if (_petPersonalities?.Happiness(raw) is not { } happiness) return;
        Vector2 origin = (PetFrameUiLaw.Origin + PetFrameUiLaw.HappinessOffset) * scale;
        Vector2 size = PetFrameUiLaw.HappinessSize * scale;
        CollectGameplayLayout("pet-happiness", PetFrameUiLaw.Origin.X + PetFrameUiLaw.HappinessOffset.X,
            PetFrameUiLaw.Origin.Y + PetFrameUiLaw.HappinessOffset.Y,
            PetFrameUiLaw.HappinessSize.X, PetFrameUiLaw.HappinessSize.Y, origin, size);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
        bool begun = ImGui.Begin("##pet-happiness", ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
        ImGui.PopStyleVar(3);
        if (begun)
        {
            ImGui.SetCursorScreenPos(origin);
            ImGui.InvisibleButton("##pet-happiness-hover", size);
            uint texture = _gameplayArt!.Handle(PetFrameUiLaw.HappinessTexture);
            var uv = PetFrameUiLaw.HappinessUv(happiness.Bucket);
            if (texture != 0) ImGui.GetWindowDrawList().AddImage((nint)texture, origin,
                origin + size, uv.Min, uv.Max);
            if (ImGui.IsItemHovered())
            {
                string title = InventoryGlobalString($"PET_HAPPINESS{happiness.Bucket}",
                    happiness.Bucket switch { 1 => "Unhappy", 2 => "Content", _ => "Happy" });
                var lines = new List<GameTooltipLine>
                {
                    new(title, GameTooltipTextTone.White),
                    new(InventoryGlobalString("PET_DAMAGE_PERCENTAGE", "Causes %d%% of normal damage")
                        .Replace("%d", $"{happiness.DamagePercent:0}").Replace("%%", "%"),
                        GameTooltipTextTone.White),
                };
                if (happiness.LoyaltyRate != 0)
                    lines.Add(new(InventoryGlobalString(happiness.LoyaltyRate > 0
                        ? "GAINING_LOYALTY" : "LOSING_LOYALTY", happiness.LoyaltyRate > 0
                        ? "Gaining Loyalty" : "Losing Loyalty"), GameTooltipTextTone.White));
                OfferOwnerAnchoredSharedGameTooltip(new("pet-happiness", pet.Guid),
                    lines.ToArray(), origin + new Vector2(size.X, 0), Vector2.Zero);
            }
        }
        ImGui.End();
    }
}
