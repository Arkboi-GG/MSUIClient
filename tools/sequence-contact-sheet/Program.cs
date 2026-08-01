using Microsoft.VisualBasic.FileIO;
using SkiaSharp;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: sequence-contact-sheet <sequence.csv> <capture-directory> <output.png>");
    return 2;
}

string csv = Path.GetFullPath(args[0]);
string captures = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[2]);
var rows = new List<Dictionary<string, string>>();
using (var parser = new TextFieldParser(csv) { TextFieldType = FieldType.Delimited })
{
    parser.SetDelimiters(",");
    string[] header = parser.ReadFields() ?? throw new InvalidDataException("missing CSV header");
    while (!parser.EndOfData)
    {
        string[] fields = parser.ReadFields() ?? [];
        if (fields.Length != header.Length) continue;
        var row = header.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second,
            StringComparer.OrdinalIgnoreCase);
        if (row["row_kind"] == "SAMPLE") rows.Add(row);
    }
}
if (rows.Count < 14) throw new InvalidDataException($"sequence contains {rows.Count} samples; 14 required");

const int columns = 4, cellWidth = 400, imageHeight = 225, labelHeight = 72;
int sheetRows = (rows.Count + columns - 1) / columns;
using var sheet = new SKBitmap(cellWidth * columns, (imageHeight + labelHeight) * sheetRows,
    SKColorType.Rgba8888, SKAlphaType.Premul);
using var canvas = new SKCanvas(sheet);
canvas.Clear(new SKColor(18, 18, 20));
using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
using var verdictPaint = new SKPaint { IsAntialias = true, Color = new SKColor(255, 214, 102) };
using var font = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 17);
var index = new List<string> { "sample,frame,time,expected,played,verdict,path" };

for (int i = 0; i < rows.Count; i++)
{
    Dictionary<string, string> row = rows[i];
    string frame = row["frame"];
    string path = Path.Combine(captures, $"gameplay-{frame}.png");
    using SKBitmap source = SKBitmap.Decode(path) ?? throw new InvalidDataException(path);
    int col = i % columns, sheetRow = i / columns;
    float x = col * cellWidth, y = sheetRow * (imageHeight + labelHeight);
    canvas.DrawBitmap(source, new SKRect(x, y, x + cellWidth, y + imageHeight));
    canvas.DrawText($"{i:00}  t={row["time"]}s  stage={row["actual_stage"]}", x + 8,
        y + imageHeight + 21, SKTextAlign.Left, font, paint);
    canvas.DrawText($"expected={row["expected_animation_id"]} played={row["played_animation_id"]}  {row["animation_verdict"]}",
        x + 8, y + imageHeight + 45, SKTextAlign.Left, font, verdictPaint);
    canvas.DrawText($"renderer={row["renderer_state"]} blend={row["blend_weight"]}", x + 8,
        y + imageHeight + 66, SKTextAlign.Left, font, paint);
    index.Add($"{i},{frame},{row["time"]},{row["expected_animation_id"]},{row["played_animation_id"]},{row["animation_verdict"]},{path}");
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using SKData encoded = sheet.Encode(SKEncodedImageFormat.Png, 95);
using FileStream stream = File.Create(output);
encoded.SaveTo(stream);
File.WriteAllLines(Path.ChangeExtension(output, ".txt"), index);
Console.WriteLine($"[sequence-contact-sheet] wrote {output} ({sheet.Width}x{sheet.Height}, {rows.Count} frames)");
return 0;
