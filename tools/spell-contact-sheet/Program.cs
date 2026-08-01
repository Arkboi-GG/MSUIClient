using SkiaSharp;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: spell-contact-sheet <repo-root> <output.png>");
    return 2;
}

string root = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
string[] schools = ["physical", "fire", "frost", "arcane", "holy", "nature", "shadow"];
const int cellWidth = 600, cellHeight = 338, labelHeight = 38;
using var sheet = new SKBitmap(cellWidth * 2, (cellHeight + labelHeight) * schools.Length,
    SKColorType.Rgba8888, SKAlphaType.Premul);
using var canvas = new SKCanvas(sheet);
canvas.Clear(new SKColor(18, 18, 20));
using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
using var font = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 22);
var index = new List<string> { "row,school,stage,path" };

for (int row = 0; row < schools.Length; row++)
{
    for (int col = 0; col < 2; col++)
    {
        string stage = col == 0 ? "precast" : "cast";
        string relative = $"dumps/gameplay-n1c-anim-v2-{schools[row]}-{stage}.png";
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        using SKBitmap source = SKBitmap.Decode(path) ?? throw new InvalidDataException(path);
        float y = row * (cellHeight + labelHeight);
        canvas.DrawBitmap(source, new SKRect(col * cellWidth, y, (col + 1) * cellWidth, y + cellHeight));
        canvas.DrawText($"{schools[row].ToUpperInvariant()} — {stage.ToUpperInvariant()}",
            col * cellWidth + 12, y + cellHeight + 27, SKTextAlign.Left, font, paint);
        index.Add($"{row + 1},{schools[row]},{stage},{relative}");
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using SKData encoded = sheet.Encode(SKEncodedImageFormat.Png, 95);
using FileStream stream = File.Create(output);
encoded.SaveTo(stream);
File.WriteAllLines(Path.ChangeExtension(output, ".txt"), index);
Console.WriteLine($"[spell-contact-sheet] wrote {output} ({sheet.Width}x{sheet.Height})");
return 0;
