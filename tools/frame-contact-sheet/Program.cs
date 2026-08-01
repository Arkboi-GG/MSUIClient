using SkiaSharp;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: frame-contact-sheet <repo-root> <output.png> <capture-prefix> <label> [label...]");
    return 2;
}

string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]), prefix = args[2];
string[] labels = args[3..];
const int columns = 3, cellWidth = 533, cellHeight = 300, labelHeight = 38;
int rows = (labels.Length + columns - 1) / columns;
using var sheet = new SKBitmap(cellWidth * columns, (cellHeight + labelHeight) * rows,
    SKColorType.Rgba8888, SKAlphaType.Premul);
using var canvas = new SKCanvas(sheet); canvas.Clear(new SKColor(18, 18, 20));
using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
using var font = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 21);
var index = new List<string> { "cell,label,path" };

for (int i = 0; i < labels.Length; i++)
{
    string label = labels[i], relative = $"dumps/gameplay-{prefix}-{label}.png";
    string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    using SKBitmap source = SKBitmap.Decode(path) ?? throw new InvalidDataException(path);
    int col = i % columns, row = i / columns; float x = col * cellWidth, y = row * (cellHeight + labelHeight);
    canvas.DrawBitmap(source, new SKRect(x, y, x + cellWidth, y + cellHeight));
    canvas.DrawText(label.Replace('-', ' ').ToUpperInvariant(), x + 12, y + cellHeight + 27,
        SKTextAlign.Left, font, paint);
    index.Add($"{i + 1},{label},{relative}");
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using SKData encoded = sheet.Encode(SKEncodedImageFormat.Png, 95);
using FileStream stream = File.Create(output); encoded.SaveTo(stream);
File.WriteAllLines(Path.ChangeExtension(output, ".txt"), index);
Console.WriteLine($"[frame-contact-sheet] wrote {output} ({sheet.Width}x{sheet.Height}, {labels.Length} frames)");
return 0;
