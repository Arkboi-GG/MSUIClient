using MSUIClient.Formats;

namespace MSUIClient.World.Units;

public sealed partial class CreatureRenderer
{
    public readonly record struct VariantTextureTrace(
        int BatchIndex,
        int GeosetId,
        string Region,
        uint TextureType,
        string ResolvedTexture,
        string EffectiveTexture,
        string Supplier,
        string DemandedTexture,
        string DemandedSupplier,
        bool MissingDemandedTexture,
        string Predicted7C2Texture,
        string ProtocolRow);

    public sealed record NpcVariantTrace(
        int DisplayId,
        uint ExtraId,
        string ModelPath,
        string ModelSupplier,
        byte Race,
        byte Sex,
        uint Skin,
        uint Face,
        byte HairStyle,
        uint HairColor,
        byte FacialHair,
        string Equipment,
        string GeosetsChosen,
        string BakeTexture,
        string BakeSupplier,
        uint HelmetDisplayId,
        string HelmetSuffix,
        string HelmetModel,
        string HelmetSupplier,
        uint ShoulderDisplayId,
        string ShoulderModels,
        string ShoulderSuppliers,
        string AttachmentStatus,
        IReadOnlyList<VariantTextureTrace> Textures);

    private CharSectionsTable? _variantCharSections;

    /// <summary>
    /// Batch-only synchronous residency using the renderer's existing model and
    /// appearance builders. Normal world/portrait callers retain their async
    /// request law; the unattended host needs the specimen ready before its
    /// single main-thread bake attempt.
    /// </summary>
    public bool PrepareVariantSpecimen(int displayId)
    {
        if (_resolver is null || !_resolver.TryResolve(displayId, out CreatureModelInfo info))
            return false;
        if (!_modelCache.TryGetValue(info.ModelPath, out LoadedModel? model))
        {
            model = LoadModelMeasured(info);
            _modelCache[info.ModelPath] = model;
        }
        if (model is null) return false;
        string appearanceKey = AppearanceKey(info);
        if (!_appearanceCache.TryGetValue(appearanceKey, out Appearance? appearance))
        {
            appearance = BuildAppearanceMeasured(model, info);
            _appearanceCache[appearanceKey] = appearance;
        }
        return appearance is not null;
    }

    /// <summary>
    /// Read-only trace of the exact batch/geoset texture law used by the live
    /// creature renderer. The variant batch calls this after baking the same
    /// display, so its CSV names the strings that produced the pixels.
    /// </summary>
    public NpcVariantTrace? TraceNpcVariant(int displayId)
    {
        if (_resolver is null || !_resolver.TryResolve(displayId, out CreatureModelInfo info) ||
            !info.HasExtended)
            return null;

        (byte[] Data, string Supplier)? modelSource = _mpq.ReadFileWithSupplier(info.ModelPath);
        M2Model? model = modelSource is null ? null : M2Reader.Parse(modelSource.Value.Data);
        if (model is null || !model.IsValid) return null;

        EquipGeosets? equipment = BuildNpcEquip(info);
        HashSet<int>? visible = _geosets?.Visible(
            info.ExtRace, info.ExtSex, info.ExtHairStyle, info.ExtFacialHair, equipment);
        string geosets = visible is null
            ? "ALL"
            : string.Join(';', visible.OrderBy(value => value));

        _variantCharSections ??= LoadVariantCharSections();
        string demandedHair = ResolveDemandedHairTexture(info);
        string demandedHairSupplier = SupplierFor(demandedHair);
        string bareDescriptor = NpcBareDescriptor(info);

        var textures = new List<VariantTextureTrace>();
        string effective = "UNBOUND";
        for (int batchIndex = 0; batchIndex < model.Batches.Count; batchIndex++)
        {
            M2Batch batch = model.Batches[batchIndex];
            if (batch.SubmeshIndex >= model.Submeshes.Count) continue;
            int geosetId = model.Submeshes[batch.SubmeshIndex].Id;
            if (visible is not null && !visible.Contains(geosetId)) continue;

            uint textureType = uint.MaxValue;
            string resolved = "";
            string supplier = "";
            string demanded = "";
            string demandedSupplier = "";
            if (batch.TextureIndex < model.TextureLookup.Count)
            {
                int textureIndex = model.TextureLookup[batch.TextureIndex];
                if (textureIndex >= 0 && textureIndex < model.Textures.Count)
                {
                    M2TextureRef reference = model.Textures[textureIndex];
                    textureType = reference.Type;
                    if (IsNpcBareHeadBatch(reference.Type, geosetId))
                    {
                        resolved = bareDescriptor;
                        supplier = "generated:npc-bare";
                    }
                    else
                    {
                        IReadOnlyList<string> candidates = ResolveBatchTexture(
                            reference.Type, reference.Filename,
                            ParentDirectory(info.ModelPath), info);
                        (resolved, supplier) = FirstSupplied(candidates);
                    }
                    demanded = resolved;
                    demandedSupplier = supplier;
                    if (reference.Type == 6 && demandedHair.Length > 0)
                    {
                        demanded = demandedHair;
                        demandedSupplier = demandedHairSupplier;
                    }
                }
            }

            bool bareHeadBatch = IsNpcBareHeadBatch(textureType, geosetId);
            string rowEffective;
            if (bareHeadBatch)
            {
                rowEffective = resolved.Length > 0 ? resolved : effective;
            }
            else
            {
                if (resolved.Length > 0) effective = resolved;
                rowEffective = effective;
            }
            string predicted = resolved;
            int category = geosetId / 100;
            int variant = geosetId % 100;
            bool headType1 = textureType == 1 &&
                ((category == 0 && variant > 0) || category == 7);
            if (headType1) predicted = bareDescriptor;
            else if (textureType == 6 && demanded.Length > 0) predicted = demanded;

            string protocol = (displayId, info.ExtId, batchIndex) switch
            {
                (2072, 675, 12) => "willem-2072-675-batch12",
                (3340, 54, 15) => "control-3340-54-batch15",
                (3340, 54, 18) => "control-3340-54-batch18",
                _ => "",
            };
            textures.Add(new VariantTextureTrace(
                batchIndex, geosetId, VariantRegion(geosetId), textureType,
                resolved.Length > 0 ? resolved : "NONE",
                rowEffective, supplier.Length > 0 ? supplier : "NONE",
                demanded.Length > 0 ? demanded : "NONE",
                demandedSupplier.Length > 0 ? demandedSupplier : "NONE",
                demanded.Length > 0 && resolved.Length == 0,
                predicted.Length > 0 ? predicted : "NONE",
                protocol));
        }

        string bake = NpcBakePath(info.BakeName);
        uint helmetDisplay = info.ExtEquipment.Length > 0 ? info.ExtEquipment[0] : 0;
        uint shoulderDisplay = info.ExtEquipment.Length > 1 ? info.ExtEquipment[1] : 0;
        string suffix = RaceGenderCode(info.ExtRace, info.ExtSex);
        (string helmetModel, string helmetSupplier) = ResolveItemModel(
            helmetDisplay, "Head", suffix, firstOnly: true);
        (string shoulderModels, string shoulderSuppliers) = ResolveItemModel(
            shoulderDisplay, "Shoulder", "", firstOnly: false);
        bool authoredAttachments = helmetDisplay != 0 || shoulderDisplay != 0;

        return new NpcVariantTrace(
            displayId, info.ExtId, info.ModelPath, modelSource?.Supplier ?? "NONE",
            info.ExtRace, info.ExtSex, info.ExtSkin, info.ExtFace,
            info.ExtHairStyle, info.ExtHairColor, info.ExtFacialHair,
            string.Join(';', info.ExtEquipment), geosets,
            bake.Length > 0 ? bake : "NONE", SupplierFor(bake),
            helmetDisplay, suffix,
            helmetModel.Length > 0 ? helmetModel : "NONE",
            helmetSupplier.Length > 0 ? helmetSupplier : "NONE",
            shoulderDisplay,
            shoulderModels.Length > 0 ? shoulderModels : "NONE",
            shoulderSuppliers.Length > 0 ? shoulderSuppliers : "NONE",
            authoredAttachments ? "mounted" : "none-authored",
            textures);
    }

    private CharSectionsTable? LoadVariantCharSections()
    {
        byte[]? bytes = _mpq.ReadFile(CharSectionsTable.MpqPath);
        return bytes is null ? null : CharSectionsTable.Parse(bytes);
    }

    private string ResolveDemandedHairTexture(in CreatureModelInfo info)
    {
        CharSectionRow? row = _variantCharSections?.Find(
            info.ExtRace, info.ExtSex, CharSectionsTable.SectionHair,
            info.ExtHairStyle, (int)info.ExtHairColor);
        if (row is null || row.Texture1.Length == 0)
            row = _variantCharSections?.Find(
                info.ExtRace, info.ExtSex, CharSectionsTable.SectionHair,
                1, (int)info.ExtHairColor);
        if (row is null || row.Texture1.Length == 0) return "";
        return FirstSupplied(CharacterTextureCandidates(
            row.Texture1, info.ExtRace, info.ExtSex)).Path;
    }

    private (string Path, string Supplier) FirstSupplied(IEnumerable<string> candidates)
    {
        foreach (string path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            (byte[] Data, string Supplier)? source = _mpq.ReadFileWithSupplier(path);
            if (source is not null) return (path, source.Value.Supplier);
        }
        return ("", "");
    }

    private string SupplierFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "NONE";
        return _mpq.ReadFileWithSupplier(path)?.Supplier ?? "NONE";
    }

    private (string Models, string Suppliers) ResolveItemModel(
        uint displayId, string folder, string suffix, bool firstOnly)
    {
        ItemDisplayRow? row = displayId == 0 ? null : _itemDisplay?.Find(displayId);
        if (row is null) return ("", "");
        string[] names = firstOnly ? [row.ModelName1] : [row.ModelName1, row.ModelName2];
        var models = new List<string>();
        var suppliers = new List<string>();
        foreach (string name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            string stem = Path.GetFileNameWithoutExtension(name.Replace('/', '\\'));
            string[] candidates = folder == "Head"
                ?
                [
                    $@"Item\ObjectComponents\Head\{stem}_{suffix}.m2",
                    $@"Item\ObjectComponents\Head\{stem}{suffix}.m2",
                    $@"Item\ObjectComponents\Head\{stem}.m2",
                ]
                :
                [
                    $@"Item\ObjectComponents\Shoulder\{stem}.m2",
                ];
            (string path, string supplier) = FirstSupplied(candidates);
            if (path.Length == 0) continue;
            models.Add(path);
            suppliers.Add(supplier);
        }
        return (string.Join('|', models), string.Join('|', suppliers));
    }

    private static IEnumerable<string> CharacterTextureCandidates(
        string partial, byte race, byte sex)
    {
        string stem = partial.Replace('/', '\\').TrimStart('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        string raceFolder = RaceFolder(race);
        string gender = sex == 1 ? "Female" : "Male";
        yield return stem + ".blp";
        yield return $@"Character\{stem}.blp";
        yield return $@"Character\{raceFolder}\{gender}\{stem}.blp";
    }

    private static string NpcBakePath(string bakeName)
    {
        if (string.IsNullOrWhiteSpace(bakeName)) return "";
        string bake = bakeName.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)
            ? bakeName : bakeName + ".blp";
        return bake.Contains('\\') ? bake : @"Textures\BakedNpcTextures" + "\\" + bake;
    }

    private static string ParentDirectory(string path)
        => path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";

    private static string RaceGenderCode(byte race, byte sex) =>
        (race switch
        {
            1 => "Hu", 2 => "Or", 3 => "Dw", 4 => "Ni",
            5 => "Sc", 6 => "Ta", 7 => "Gn", 8 => "Tr", _ => "Hu",
        }) + (sex == 1 ? "F" : "M");

    private static string VariantRegion(int geosetId)
    {
        if (geosetId == 0) return "body-base";
        return (geosetId / 100) switch
        {
            0 => "hair-scalp", 1 or 2 or 3 => "facial-hair", 4 => "gloves",
            5 => "boots", 6 or 14 => "body-base", 7 => "ears", 8 => "sleeves",
            9 => "knees", 10 => "doublet", 11 => "legs", 12 => "tabard",
            13 => "robe", 15 => "cape", _ => "other",
        };
    }
}
