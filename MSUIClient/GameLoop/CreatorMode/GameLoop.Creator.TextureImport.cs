using System.Buffers.Binary;
using MSUIClient.Creator;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Spell workshop: TEXTURE IMPORT (shared_docs/SPELL_CREATOR_IDE.md §2.7)
//
// Your own art into a texture slot: a PNG is converted to BLP2 (DXT3, alpha)
// at the nearest power-of-two size, a BLP is taken as is. The bytes live at a
// custom MPQ path served by the mount's override layer, so every renderer
// (particles, meshes, ribbons) reads them like archive art; the slot is then
// swapped to that path through the ordinary texture-swap patch, so the M2's
// texture table names the file. The session export ships the bytes as an
// extra file at that path, which is exactly how the Completer already writes
// tinted BLPs into the patch - no consumer change needed.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private void BeginCreatorTextureImport(CreatorModelDoc model, int slot)
    {
        OpenCreatorFilePicker($"Import an image into slot {slot} of {Path.GetFileName(model.Path)}",
            [".png", ".blp"], file => ImportCreatorTexture(model, slot, file));
    }

    /// <summary>Convert / accept the file, register it at a custom path, swap the slot to it.</summary>
    private void ImportCreatorTexture(CreatorModelDoc model, int slot, string file)
    {
        if (_creatorSpell is not { } doc || _mpq is null) return;
        try
        {
            string full = Path.GetFullPath(file);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            byte[] blp;
            switch (ext)
            {
                case ".blp":
                    blp = File.ReadAllBytes(full);
                    if (blp.Length < 4 || blp[0] != (byte)'B' || blp[1] != (byte)'L' || blp[2] != (byte)'P')
                        throw new InvalidDataException("The selected file is not a BLP.");
                    break;
                case ".png":
                {
                    (int width, int height) = PngDimensions(full);
                    int w = Math.Clamp(NextPowerOfTwo(width), 16, 1024);
                    int h = Math.Clamp(NextPowerOfTwo(height), 16, 1024);
                    blp = new BlpWriterService().ConvertPngToBlpVanillaMatched(full, w, h, useDxt1: false)
                          ?? throw new InvalidDataException("The PNG could not be converted to BLP.");
                    break;
                }
                default:
                    throw new InvalidDataException("Spell art must be a PNG or a BLP file.");
            }

            string spell = CreatorAudioAssetToken(doc.Info.Name);
            if (spell.Length == 0) spell = $"spell_{doc.Info.Id}";
            string stem = CreatorAudioAssetToken(Path.GetFileNameWithoutExtension(full));
            if (stem.Length == 0) stem = "image";
            string path = $@"Spells\Custom\{doc.Info.Id}_{spell}\{stem}_{doc.ImportedTextures.Count + 1}.blp";

            _mpq.SetOverride(path, blp);
            doc.ImportedTextures[path] = blp;
            ApplyCreatorTextureSwapEverywhere(doc, model, slot, path);
            Console.WriteLine($"[creator] imported {Path.GetFileName(full)} -> {path} ({blp.Length} bytes) " +
                              $"into slot {slot} of {Path.GetFileName(model.Path)}");
        }
        catch (Exception ex)
        {
            _creatorFilePickerError = ex.Message;
            Console.WriteLine($"[creator] texture import failed: {ex.Message}");
        }
    }

    private static (int Width, int Height) PngDimensions(string path)
    {
        using FileStream stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        if (stream.Read(header) < 24 || header[1] != (byte)'P' || header[2] != (byte)'N' || header[3] != (byte)'G')
            throw new InvalidDataException("The selected file is not a PNG.");
        int width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        int height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (width <= 0 || height <= 0) throw new InvalidDataException("The PNG has no size.");
        return (width, height);
    }

    private static int NextPowerOfTwo(int value)
    {
        int p = 1;
        while (p < value) p <<= 1;
        return p;
    }
}
