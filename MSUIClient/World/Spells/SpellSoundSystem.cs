using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using MSUIClient.Formats;

namespace MSUIClient.World.Spells;

/// <summary>
/// Spell-kit and missile audio owner. SoundEntries chooses the weighted file and supplies loop,
/// volume and distance policy; the Windows backend uses the system MCI decoder so the client does
/// not need a second media package. On other platforms routing remains observable but silent.
/// </summary>
public sealed class SpellSoundSystem : IDisposable
{
    private sealed record Voice(long Id, string Alias, string File, SoundEntry Entry,
        ulong Unit, Vector3 Source, bool Looping, bool TrackHold);

    private readonly MpqMount _mpq;
    private readonly SoundEntriesCatalog? _catalog;
    private readonly Dictionary<long, Voice> _voices = [];
    private readonly Dictionary<ulong, long> _holds = [];
    private readonly Dictionary<uint, string> _lastVariant = [];
    private readonly string _tempRoot;
    private long _nextVoice;

    public long Plays { get; private set; }
    public long Stops { get; private set; }
    public string LastCue { get; private set; } = "";
    public bool AudibleBackend => OperatingSystem.IsWindows();

    public SpellSoundSystem(MpqMount mpq)
    {
        _mpq = mpq;
        _catalog = SoundEntriesCatalog.Load(mpq);
        _tempRoot = Path.Combine(Path.GetTempPath(), "MSUIClient", "SpellAudio",
            Environment.ProcessId.ToString());
    }

    public long Play(uint? soundId, ulong unit, Vector3 source, Vector3 listener,
        bool forceLoop = false, bool trackHold = true)
    {
        if (soundId is not uint id || id == 0 || _catalog?.TryGet(id, out SoundEntry entry) != true ||
            entry.Variants.Count == 0) return 0;
        SoundVariant variant = PickVariant(entry);
        LastCue = $"{id}:{variant.Path}";
        Plays++;
        bool looping = forceLoop || entry.Looping;
        if (looping && trackHold) StopHold(unit);
        if (!OperatingSystem.IsWindows()) return 0;
        float gain = Gain(entry, source, listener);
        if (gain <= 0) return 0;
        byte[]? bytes = _mpq.ReadFile(variant.Path);
        if (bytes is null || bytes.Length == 0) return 0;
        try
        {
            Directory.CreateDirectory(_tempRoot);
            long voiceId = ++_nextVoice;
            string extension = Path.GetExtension(variant.Path);
            if (extension.Length == 0) extension = ".wav";
            string file = Path.Combine(_tempRoot, $"{voiceId}{extension}");
            File.WriteAllBytes(file, bytes);
            string alias = $"msuispell{Environment.ProcessId}_{voiceId}";
            if (Mci($"open \"{file}\" alias {alias}") != 0)
            { TryDelete(file); return 0; }
            Mci($"setaudio {alias} volume to {(int)Math.Clamp(gain * 1000f, 0, 1000)}");
            if (Mci($"play {alias}{(looping ? " repeat" : "")}") != 0)
            { Mci($"close {alias}"); TryDelete(file); return 0; }
            var voice = new Voice(voiceId, alias, file, entry, unit, source, looping, trackHold);
            _voices[voiceId] = voice;
            if (looping && trackHold) _holds[unit] = voiceId;
            return voiceId;
        }
        catch { return 0; }
    }

    public void StopHold(ulong unit)
    {
        if (_holds.Remove(unit, out long voice)) Stop(voice);
    }

    public void Stop(long voiceId)
    {
        if (!_voices.Remove(voiceId, out Voice? voice)) return;
        Mci($"stop {voice.Alias}");
        Mci($"close {voice.Alias}");
        if (voice.TrackHold && _holds.GetValueOrDefault(voice.Unit) == voiceId)
            _holds.Remove(voice.Unit);
        TryDelete(voice.File);
        Stops++;
    }

    public void Tick(Vector3 listener, Func<ulong, (bool Found, Vector3 Position)> unitPosition)
    {
        foreach (Voice voice in _voices.Values.ToArray())
        {
            if (!voice.Looping)
            {
                var status = new StringBuilder(32);
                Mci($"status {voice.Alias} mode", status);
                if (status.ToString().Trim().Equals("stopped", StringComparison.OrdinalIgnoreCase))
                    Stop(voice.Id);
                continue;
            }
            (bool found, Vector3 position) = unitPosition(voice.Unit);
            if (!found) { Stop(voice.Id); continue; }
            float gain = Gain(voice.Entry, position, listener);
            if (gain <= 0) { Stop(voice.Id); continue; }
            Mci($"setaudio {voice.Alias} volume to {(int)Math.Clamp(gain * 1000f, 0, 1000)}");
        }
    }

    private SoundVariant PickVariant(in SoundEntry entry)
    {
        IReadOnlyList<SoundVariant> candidates = entry.Variants;
        uint total = 0;
        foreach (SoundVariant candidate in candidates) total += candidate.Weight;
        SoundVariant picked;
        if (total == 0) picked = candidates[Random.Shared.Next(candidates.Count)];
        else
        {
            uint roll = (uint)Random.Shared.NextInt64(total);
            picked = candidates[0];
            foreach (SoundVariant candidate in candidates)
            {
                if (roll < candidate.Weight) { picked = candidate; break; }
                roll -= candidate.Weight;
            }
        }
        if (entry.NoDuplicates && candidates.Count > 1 &&
            _lastVariant.GetValueOrDefault(entry.Id) == picked.Path)
        {
            SoundVariant alternate = candidates.FirstOrDefault(candidate => candidate.Path != picked.Path);
            if (!string.IsNullOrEmpty(alternate.Path)) picked = alternate;
        }
        _lastVariant[entry.Id] = picked.Path;
        return picked;
    }

    private static float Gain(in SoundEntry entry, Vector3 source, Vector3 listener)
    {
        float volume = float.IsFinite(entry.Volume) ? Math.Clamp(entry.Volume, 0f, 1f) : 1f;
        if (entry.CutoffDistance <= 0) return volume;
        float distance = Vector3.Distance(source, listener);
        if (distance >= entry.CutoffDistance) return 0;
        if (distance <= entry.MinDistance) return volume;
        float span = Math.Max(.001f, entry.CutoffDistance - entry.MinDistance);
        return volume * (1f - (distance - entry.MinDistance) / span);
    }

    private static int Mci(string command, StringBuilder? result = null)
        => OperatingSystem.IsWindows()
            ? mciSendString(command, result, result?.Capacity ?? 0, 0)
            : -1;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        foreach (long voice in _voices.Keys.ToArray()) Stop(voice);
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: false); }
        catch { }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue,
        int returnLength, nint callback);
}
