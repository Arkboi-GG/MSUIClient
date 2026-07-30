using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;
using Silk.NET.OpenGL;
using SkiaSharp;

namespace MSUIClient;

public sealed record PortraitBatchOptions(
    string? OutputDirectory,
    string? ListFile,
    int? Limit,
    string? DiffFile,
    bool Unmasked);

public static partial class Program
{
    private static bool TryParsePortraitBatchArgs(string[] args,
        out PortraitBatchOptions? options, out string? configPath, out string? error)
    {
        options = null;
        configPath = null;
        error = null;
        if (!args.Contains("--portrait-batch", StringComparer.OrdinalIgnoreCase))
        {
            configPath = args.Length > 0 ? args[0] : null;
            return true;
        }

        string? output = null, list = null, diff = null;
        int? limit = null;
        bool unmasked = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--portrait-batch", StringComparison.OrdinalIgnoreCase)) continue;
            if (arg.Equals("--unmasked", StringComparison.OrdinalIgnoreCase))
            {
                unmasked = true;
                continue;
            }
            if (arg is "--out" or "--list" or "--limit" or "--diff")
            {
                if (++i >= args.Length)
                {
                    error = $"missing value for {arg}";
                    return false;
                }
                string value = args[i];
                if (arg == "--out") output = value;
                else if (arg == "--list") list = value;
                else if (arg == "--diff") diff = value;
                else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                             out int parsed) || parsed <= 0)
                {
                    error = "--limit must be a positive integer";
                    return false;
                }
                else limit = parsed;
                continue;
            }
            if (arg.StartsWith('-'))
            {
                error = $"unknown option {arg}";
                return false;
            }
            if (configPath is not null)
            {
                error = $"unexpected argument {arg}";
                return false;
            }
            configPath = arg;
        }

        options = new PortraitBatchOptions(output, list, limit, diff, unmasked);
        return true;
    }

    private static void PrintPortraitBatchUsage() => Console.Error.WriteLine(
        "usage: MSUIClient [config.json] --portrait-batch [--out <dir>] " +
        "[--list <file>] [--limit <n>] [--diff <verdicts.csv>] [--unmasked]");
}

public sealed partial class GameLoop
{
    // Derived from the 2026-07-30 codex-full Ready distribution
    // (p1≈24k, p50≈45k, p99≈59k); informational, not correctness law.
    private const int BatchTinySubjectMaxExclusive = 8_000;
    private const int BatchFullSubjectMinInclusive = 63_000;
    private const int BatchCacheChunk = 128;
    private const double BatchSpecimenTimeoutSeconds = 10.0;
    private const string BatchCsvHeader =
        "key,kind,displayId,modelPath,outcome,cameraSource,authoredRetried,subjectPx," +
        "rgbLo,rgbHi,alphaLo,alphaHi,meanLuma,pieces,bindPoseHeight,eyeHeight,distance," +
        "fovyDeg,nearPlane,elapsedMs,note";

    private readonly PortraitBatchOptions? _portraitBatchOptions;
    private PortraitRenderTarget? _batchPortraitTarget;
    private readonly List<BatchSpecimen> _batchSpecimens = [];
    private readonly List<BatchResult> _batchResults = [];
    private readonly List<(string Key, byte[] Rgba)> _batchSheetCells = [];
    private string _batchOutputDirectory = "";
    private int _batchIndex;
    private int _batchSheetIndex;
    private Stopwatch? _batchClock;
    private bool _batchFinished;
    public int PortraitBatchExitCode { get; private set; } = 1;

    private readonly record struct BatchSpecimen(
        string Key, string Kind, int DisplayId, string ModelPath, string? SkipNote);

    private readonly record struct BatchResult(
        string Key, string Kind, int DisplayId, string ModelPath,
        PortraitOutcome Outcome, string CameraSource, bool AuthoredRetried,
        int SubjectPx, int RgbLo, int RgbHi, int AlphaLo, int AlphaHi, double MeanLuma, int Pieces,
        float BindPoseHeight, float EyeHeight, float Distance, float FovyDeg,
        float NearPlane, double ElapsedMs, string Note)
    {
        public string CsvLine => string.Join(',',
            Csv(Key), Csv(Kind), DisplayId.ToString(CultureInfo.InvariantCulture), Csv(ModelPath),
            Outcome, Csv(CameraSource), AuthoredRetried.ToString().ToLowerInvariant(),
            SubjectPx.ToString(CultureInfo.InvariantCulture),
            RgbLo.ToString(CultureInfo.InvariantCulture), RgbHi.ToString(CultureInfo.InvariantCulture),
            AlphaLo.ToString(CultureInfo.InvariantCulture), AlphaHi.ToString(CultureInfo.InvariantCulture),
            F(MeanLuma),
            Pieces.ToString(CultureInfo.InvariantCulture), F(BindPoseHeight), F(EyeHeight),
            F(Distance), F(FovyDeg), F(NearPlane), F(ElapsedMs), Csv(Note));

        private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        private static string Csv(string value) => value.Replace(',', ';').Replace('\r', ' ').Replace('\n', ' ');
    }

    private void InitPortraitBatch(GL gl)
    {
        try
        {
            _mpq = new MpqMount(_config.ClientDataPath);
            AdtTerrainReader.StormLibExtractor = _mpq.ReadFile;
            _creatures = new CreatureRenderer(gl, _mpq, _config);
            _portraitOverrides = PortraitOverrideStore.Load(_config.RepoRoot);
            _batchPortraitTarget = new PortraitRenderTarget(gl);

            PortraitBatchOptions options = _portraitBatchOptions!;
            _batchOutputDirectory = ResolveBatchPath(options.OutputDirectory ??
                Path.Combine("portrait-batch", DateTime.Now.ToString("yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture)));
            Directory.CreateDirectory(_batchOutputDirectory);
            BuildBatchSpecimenList(options);
            _batchClock = Stopwatch.StartNew();
            Console.WriteLine($"[batch] ready: {_batchSpecimens.Count} specimen(s), " +
                              $"out={_batchOutputDirectory}, masked={!options.Unmasked}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[batch] initialization failed: {ex}");
            FinishPortraitBatch(incomplete: true, error: ex.Message);
        }
    }

    private string ResolveBatchPath(string path) => Path.GetFullPath(
        Path.IsPathRooted(path) ? path : Path.Combine(_config.RepoRoot, path));

    private void BuildBatchSpecimenList(PortraitBatchOptions options)
    {
        IReadOnlyList<CreatureRenderer.PortraitSpecimen> catalog =
            _creatures?.PortraitSpecimens ?? Array.Empty<CreatureRenderer.PortraitSpecimen>();
        var byId = catalog.ToDictionary(x => x.DisplayId);
        if (options.ListFile is null)
        {
            _batchSpecimens.AddRange(catalog.Select(x => new BatchSpecimen(
                CreaturePortraitKey(x.DisplayId), "creature", x.DisplayId, x.ModelPath, null)));
        }
        else
        {
            string listPath = ResolveBatchPath(options.ListFile);
            foreach (string sourceLine in File.ReadLines(listPath))
            {
                string line = sourceLine.Split('#', 2)[0].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("creature:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.AsSpan("creature:".Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out int displayId))
                {
                    _batchSpecimens.Add(byId.TryGetValue(displayId, out var specimen)
                        ? new BatchSpecimen(CreaturePortraitKey(displayId), "creature", displayId,
                            specimen.ModelPath, null)
                        : new BatchSpecimen(CreaturePortraitKey(displayId), "creature", displayId,
                            "", "display-not-found"));
                }
                else if (line.StartsWith("player:", StringComparison.OrdinalIgnoreCase))
                {
                    _batchSpecimens.Add(new BatchSpecimen(line.ToLowerInvariant(), "player", 0,
                        "", "unsupported-v1"));
                }
                else
                {
                    _batchSpecimens.Add(new BatchSpecimen(line, "unknown", 0, "", "invalid-list-entry"));
                }
            }
        }

        if (options.Limit is { } limit && _batchSpecimens.Count > limit)
            _batchSpecimens.RemoveRange(limit, _batchSpecimens.Count - limit);
    }

    private void StepPortraitBatch()
    {
        if (_batchFinished) return;
        if (_batchPortraitTarget is null || _creatures is null)
        {
            FinishPortraitBatch(incomplete: true, error: "batch renderer unavailable");
            return;
        }
        if (_batchIndex >= _batchSpecimens.Count)
        {
            FinishPortraitBatch(incomplete: false, error: null);
            return;
        }

        try
        {
            BatchSpecimen specimen = _batchSpecimens[_batchIndex];
            BatchResult result = BakeBatchSpecimen(specimen);
            _batchResults.Add(result);
            _batchIndex++;

            if (_batchIndex % 25 == 0 || _batchIndex == _batchSpecimens.Count)
            {
                int ready = _batchResults.Count(x => x.Outcome == PortraitOutcome.Ready);
                int blank = _batchResults.Count(x => x.Outcome == PortraitOutcome.Blank);
                int notDrawn = _batchResults.Count(x => x.Outcome == PortraitOutcome.NotDrawn);
                int skipped = _batchResults.Count(x => x.Outcome == PortraitOutcome.Skipped);
                Console.WriteLine($"[batch] {_batchIndex}/{_batchSpecimens.Count} " +
                    $"ok={ready} blank={blank} notdrawn={notDrawn} skipped={skipped}");
            }

            if (_batchIndex % BatchCacheChunk == 0)
                _creatures.ClearPortraitCache();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[batch] incomplete at {_batchIndex + 1}: {ex}");
            FinishPortraitBatch(incomplete: true, error: ex.Message);
        }
    }

    private BatchResult BakeBatchSpecimen(BatchSpecimen specimen)
    {
        var timer = Stopwatch.StartNew();
        _batchPortraitTarget!.Bake(() => { });
        if (specimen.SkipNote is not null)
        {
            SaveBatchImage(specimen);
            return SkippedBatchResult(specimen, timer.Elapsed.TotalMilliseconds, specimen.SkipNote);
        }

        var entity = new WorldEntity
        {
            Guid = 0xB47C_0000_0000UL + (uint)specimen.DisplayId,
            Type = ObjectTypeId.Unit,
            Fields = ObjectFields.ForSyntheticUnit(specimen.DisplayId, 1f),
            Position = Vector3.Zero,
            Orientation = 0f,
        };
        (PortraitTuning tuning, bool storeHit) = ResolveTuningWithHit(specimen.Key);
        if (!TryBakeCreaturePortrait(_batchPortraitTarget, entity, tuning, storeHit,
                out CreaturePortraitBake bake))
        {
            SaveBatchImage(specimen);
            return SkippedBatchResult(specimen, timer.Elapsed.TotalMilliseconds, "model-unavailable");
        }

        PortraitOutcome outcome = !bake.Drawn
            ? PortraitOutcome.NotDrawn
            : bake.Stats.HasSubject ? PortraitOutcome.Ready : PortraitOutcome.Blank;
        string note = timer.Elapsed.TotalSeconds > BatchSpecimenTimeoutSeconds ? "timeout" : "";
        if (note == "timeout") outcome = PortraitOutcome.Skipped;
        PortraitCameraSource cameraSource =
            EffectivePortraitCameraSource(storeHit, tuning, bake.UsedBounds);
        var verdict = new PortraitVerdict(
            NowSeconds(), PortraitSubject.Lab, outcome, cameraSource,
            bake.AuthoredRetriedAsBounds, bake.Stats.SubjectPixels,
            bake.Stats.MinRgb, bake.Stats.MaxRgb, bake.Stats.MinAlpha, bake.Stats.MaxAlpha,
            -1, specimen.DisplayId, bake.Framing.Height,
            bake.UsedBounds ? bake.Camera.EyeHeight : 0f,
            bake.UsedBounds ? bake.Camera.Distance : 0f,
            bake.Camera.AuthoredVerticalFieldOfViewRadians is float authoredFovy
                ? authoredFovy * 180f / MathF.PI : bake.Camera.FieldOfViewDegrees,
            bake.Camera.NearPlane);
        _verdicts.Add(verdict);

        SaveBatchImage(specimen);

        return new BatchResult(specimen.Key, specimen.Kind, specimen.DisplayId,
            specimen.ModelPath, outcome, cameraSource.ToString(), bake.AuthoredRetriedAsBounds,
            bake.Stats.SubjectPixels, bake.Stats.MinRgb, bake.Stats.MaxRgb,
            bake.Stats.MinAlpha, bake.Stats.MaxAlpha, bake.Stats.MeanLuma, -1, bake.Framing.Height,
            verdict.EyeHeight, verdict.Distance, verdict.FovyDegrees, verdict.NearPlane,
            timer.Elapsed.TotalMilliseconds, note);
    }

    private void SaveBatchImage(BatchSpecimen specimen)
    {
        if (!_portraitBatchOptions!.Unmasked) _batchPortraitTarget!.ApplyCircularMask();
        string pngPath = Path.Combine(_batchOutputDirectory, FileNameForKey(specimen.Key) + ".png");
        _batchPortraitTarget!.SavePng(pngPath);
        _batchSheetCells.Add((specimen.Key, _batchPortraitTarget.CaptureRgba()));
        if (_batchSheetCells.Count == 64) FlushBatchContactSheet();
    }

    private static BatchResult SkippedBatchResult(BatchSpecimen specimen, double elapsedMs, string note) =>
        new(specimen.Key, specimen.Kind, specimen.DisplayId, specimen.ModelPath,
            PortraitOutcome.Skipped, "", false, 0, 0, 0, 0, 0, 0, -1,
            0f, 0f, 0f, 0f, 0f, elapsedMs, note);

    private static string FileNameForKey(string key)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(key.Select(c => invalid.Contains(c) || c == ':' ? '-' : c).ToArray());
    }

    private void FlushBatchContactSheet()
    {
        if (_batchSheetCells.Count == 0) return;
        const int cell = 256, columns = 8, rows = 8, width = cell * columns, height = cell * rows;
        byte[] sheet = new byte[width * height * 4];
        for (int index = 0; index < _batchSheetCells.Count; index++)
        {
            int cellX = (index % columns) * cell;
            int cellY = (index / columns) * cell;
            byte[] rgba = _batchSheetCells[index].Rgba;
            for (int y = 0; y < cell; y++)
                System.Buffer.BlockCopy(rgba, y * cell * 4, sheet,
                    ((cellY + y) * width + cellX) * 4, cell * 4);
            string digits = _batchSheetCells[index].Key.StartsWith("creature:", StringComparison.Ordinal)
                ? _batchSheetCells[index].Key["creature:".Length..] : "0";
            StampDigits(sheet, width, height, cellX + 3, cellY + cell - 10, digits);
        }

        string path = Path.Combine(_batchOutputDirectory,
            $"contact-sheet-{++_batchSheetIndex:00}.png");
        SaveRgbaPng(path, width, height, sheet);
        _batchSheetCells.Clear();
    }

    private static readonly string[][] BatchDigits =
    [
        ["11111","10001","10001","10001","10001","10001","11111"],
        ["00100","01100","00100","00100","00100","00100","01110"],
        ["11110","00001","00001","11110","10000","10000","11111"],
        ["11110","00001","00001","01110","00001","00001","11110"],
        ["10010","10010","10010","11111","00010","00010","00010"],
        ["11111","10000","10000","11110","00001","00001","11110"],
        ["01111","10000","10000","11110","10001","10001","01110"],
        ["11111","00001","00010","00100","01000","01000","01000"],
        ["01110","10001","10001","01110","10001","10001","01110"],
        ["01110","10001","10001","01111","00001","00001","11110"],
    ];

    private static void StampDigits(byte[] rgba, int width, int height, int x, int y, string digits)
    {
        foreach (char digit in digits)
        {
            if (digit is < '0' or > '9') continue;
            string[] glyph = BatchDigits[digit - '0'];
            for (int gy = 0; gy < 7; gy++)
            for (int gx = 0; gx < 5; gx++)
            {
                if (glyph[gy][gx] != '1') continue;
                SetSheetPixel(rgba, width, height, x + gx + 1, y + gy + 1, 0, 0, 0, 220);
                SetSheetPixel(rgba, width, height, x + gx, y + gy, 255, 235, 120, 255);
            }
            x += 6;
        }
    }

    private static void SetSheetPixel(byte[] rgba, int width, int height, int x, int y,
        byte r, byte g, byte b, byte a)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
        int i = (y * width + x) * 4;
        rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a;
    }

    private static void SaveRgbaPng(string path, int width, int height, byte[] rgba)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            bitmap.SetPixel(x, y, new SKColor(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]));
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    private void FinishPortraitBatch(bool incomplete, string? error)
    {
        if (_batchFinished) return;
        _batchFinished = true;
        try
        {
            FlushBatchContactSheet();
            Directory.CreateDirectory(_batchOutputDirectory);
            File.WriteAllLines(Path.Combine(_batchOutputDirectory, "verdicts.csv"),
                new[] { BatchCsvHeader }.Concat(_batchResults.Select(x => x.CsvLine)));
            WriteBatchDiff();
            WriteBatchSummary(incomplete, error);
            int blanks = _batchResults.Count(x => x.Outcome == PortraitOutcome.Blank);
            PortraitBatchExitCode = incomplete ? 1 : blanks == 0 ? 0 : 3;
            Console.WriteLine($"[batch] complete: {_batchResults.Count}/{_batchSpecimens.Count}, " +
                              $"blanks={blanks}, exit={PortraitBatchExitCode}");
        }
        catch (Exception ex)
        {
            PortraitBatchExitCode = 1;
            Console.Error.WriteLine($"[batch] output failed: {ex}");
        }
        _window.Close();
    }

    private void WriteBatchSummary(bool incomplete, string? error)
    {
        int blanks = _batchResults.Count(x => x.Outcome == PortraitOutcome.Blank);
        var tiny = _batchResults.Where(x => x.Outcome == PortraitOutcome.Ready &&
            x.SubjectPx < BatchTinySubjectMaxExclusive).OrderBy(x => x.SubjectPx).ToArray();
        var full = _batchResults.Where(x => x.Outcome == PortraitOutcome.Ready &&
            x.SubjectPx >= BatchFullSubjectMinInclusive).OrderByDescending(x => x.SubjectPx).ToArray();
        var lines = new List<string>
        {
            $"specimens: {_batchResults.Count}/{_batchSpecimens.Count}",
            $"Ready: {_batchResults.Count(x => x.Outcome == PortraitOutcome.Ready)}",
            $"Blank: {blanks}",
            $"NotDrawn: {_batchResults.Count(x => x.Outcome == PortraitOutcome.NotDrawn)}",
            $"Skipped: {_batchResults.Count(x => x.Outcome == PortraitOutcome.Skipped)}",
            $"G1 blanks: {(blanks == 0 ? "PASS" : "FAIL")} ({blanks})",
            $"tiny: {tiny.Length} Ready below {BatchTinySubjectMaxExclusive} (informational)",
            $"full: {full.Length} Ready at/above {BatchFullSubjectMinInclusive} (informational)",
            $"durationSeconds: {_batchClock?.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) ?? "0"}",
            $"clientGitDescribe: {TryGitDescribe()}",
            "startupPath: batch-only GL + MPQ/DBC + creature renderer; login/network/world load skipped",
            "modelLoading: synchronous on the shared creature portrait path; elapsed >10s is marked timeout after return",
            $"cachePolicy: release creature model/texture cache every {BatchCacheChunk} specimens",
        };
        if (incomplete) lines.Add($"incomplete: {error ?? "unknown error"}");
        lines.Add("");
        lines.Add("top 20 worst:");
        lines.AddRange(_batchResults
            .OrderByDescending(x => x.Outcome == PortraitOutcome.Blank)
            .ThenBy(x => x.SubjectPx)
            .Take(20)
            .Select(x => $"{x.Key}: {x.Outcome}, subjectPx={x.SubjectPx}, note={x.Note}"));
        if (tiny.Length > 0)
        {
            lines.Add("");
            lines.Add("tiny — 20 most extreme:");
            lines.AddRange(tiny.Take(20).Select(x => $"{x.Key}: subjectPx={x.SubjectPx}"));
        }
        if (full.Length > 0)
        {
            lines.Add("");
            lines.Add("full — 20 most extreme:");
            lines.AddRange(full.Take(20).Select(x => $"{x.Key}: subjectPx={x.SubjectPx}"));
        }
        File.WriteAllLines(Path.Combine(_batchOutputDirectory, "summary.txt"), lines);
    }

    private string TryGitDescribe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = _config.RepoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "describe", "--always", "--dirty", "--tags" },
            });
            if (process is null || !process.WaitForExit(2000)) return "unavailable";
            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : "unavailable";
        }
        catch { return "unavailable"; }
    }

    private void WriteBatchDiff()
    {
        if (_portraitBatchOptions?.DiffFile is not { } diffFile) return;
        string path = ResolveBatchPath(diffFile);
        var previous = ReadPriorBatch(path);
        var changes = new List<string>();
        foreach (BatchResult current in _batchResults)
        {
            if (!previous.TryGetValue(current.Key, out var old)) continue;
            bool outcomeChanged = !old.Outcome.Equals(current.Outcome.ToString(), StringComparison.OrdinalIgnoreCase);
            bool pixelsChanged = old.SubjectPx == 0
                ? current.SubjectPx != 0
                : Math.Abs(current.SubjectPx - old.SubjectPx) / (double)Math.Abs(old.SubjectPx) > 0.15;
            if (outcomeChanged || pixelsChanged)
                changes.Add($"{current.Key}: {old.Outcome}/{old.SubjectPx} -> " +
                            $"{current.Outcome}/{current.SubjectPx}");
        }
        File.WriteAllLines(Path.Combine(_batchOutputDirectory, "diff.txt"), changes);
        foreach (string change in changes) Console.WriteLine($"[batch-diff] {change}");
    }

    private static Dictionary<string, (string Outcome, int SubjectPx)> ReadPriorBatch(string path)
    {
        var result = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        using var reader = File.OpenText(path);
        string[] headers = (reader.ReadLine() ?? "").Split(',');
        int keyIndex = Array.IndexOf(headers, "key");
        int outcomeIndex = Array.IndexOf(headers, "outcome");
        int subjectIndex = Array.IndexOf(headers, "subjectPx");
        if (keyIndex < 0 || outcomeIndex < 0 || subjectIndex < 0)
            throw new InvalidDataException("--diff CSV is missing key/outcome/subjectPx columns");
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string[] fields = line.Split(',');
            if (fields.Length <= Math.Max(keyIndex, Math.Max(outcomeIndex, subjectIndex)) ||
                !int.TryParse(fields[subjectIndex], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int subjectPx)) continue;
            result[fields[keyIndex]] = (fields[outcomeIndex], subjectPx);
        }
        return result;
    }
}
