using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using MSUIClient.Formats;

namespace MSUIClient.World.Spells;

/// <summary>
/// Spell-kit and missile audio owner. SoundEntries chooses the weighted file and supplies loop,
/// volume and distance policy; the Windows backend uses the system MCI decoder so the client does
/// not need a second media package. On other platforms routing remains observable but silent.
///
/// THREADING: every MCI call and every byte of file IO happens on one dedicated worker thread.
/// mciSendString("open") alone costs 50-300 ms, and the first cut of this class ran it (plus a
/// temp-file write and a per-voice per-frame status poll) on the game thread - that was the
/// "screen freezes when a spell casts" hitch (hitch-recorder: 300-650 ms frames, one per cast).
/// The game thread only picks the variant, computes gains, and enqueues jobs; it never blocks.
/// </summary>
public sealed class SpellSoundSystem : IDisposable
{
    private sealed record Voice(long Id, string Alias, string File, SoundEntry Entry,
        ulong Unit, bool Looping, bool TrackHold);

    /// <summary>Game-thread view of a live voice, for Tick's gain math. Written by the worker
    /// when a voice actually starts or dies; read by Tick on the game thread.</summary>
    private sealed record VoiceView(SoundEntry Entry, ulong Unit, bool Looping);

    private readonly MpqMount _mpq;
    private readonly SoundEntriesCatalog? _catalog;
    private readonly Dictionary<uint, string> _lastVariant = [];
    private readonly string _tempRoot;
    private long _nextVoice;

    // Worker state. _voices and _holds are touched ONLY on the worker thread.
    private readonly BlockingCollection<Action> _jobs = [];
    private readonly Thread _worker;
    private readonly Dictionary<long, Voice> _voices = [];
    private readonly Dictionary<ulong, long> _holds = [];
    private readonly ConcurrentDictionary<long, VoiceView> _views = new();
    private double _lastPollSeconds;

    public long Plays { get; private set; }
    public long Stops => Interlocked.Read(ref _stops);
    private long _stops;
    public string LastCue { get; private set; } = "";
    public bool AudibleBackend => OperatingSystem.IsWindows();

    public SpellSoundSystem(MpqMount mpq)
    {
        _mpq = mpq;
        _catalog = SoundEntriesCatalog.Load(mpq);
        _tempRoot = Path.Combine(Path.GetTempPath(), "MSUIClient", "SpellAudio",
            Environment.ProcessId.ToString());
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "spell-audio" };
        _worker.Start();
    }

    private void WorkerLoop()
    {
        foreach (Action job in _jobs.GetConsumingEnumerable())
        {
            try { job(); }
            catch { /* one bad cue must not kill the audio thread */ }
        }
        foreach (long id in _voices.Keys.ToArray()) StopOnWorker(id);
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
        if (!OperatingSystem.IsWindows()) return 0;
        float gain = Gain(entry, source, listener);
        if (gain <= 0) return 0;
        long voiceId = Interlocked.Increment(ref _nextVoice);
        string path = variant.Path;
        _jobs.Add(() => PlayOnWorker(voiceId, path, entry, unit, looping, trackHold, gain));
        return voiceId;
    }

    public long Play(string soundName, ulong unit, Vector3 source, Vector3 listener)
    {
        if (_catalog?.TryGet(soundName, out SoundEntry entry) != true) return 0;
        return Play(entry.Id, unit, source, listener, trackHold: false);
    }

    public bool IsAuthoredLoop(uint? soundId)
        => soundId is uint id && _catalog?.TryGet(id, out SoundEntry entry) == true && entry.Looping;

    private void PlayOnWorker(long voiceId, string path, SoundEntry entry, ulong unit,
        bool looping, bool trackHold, float gain)
    {
        if (looping && trackHold && _holds.Remove(unit, out long held)) StopOnWorker(held);
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null || bytes.Length == 0) return;
        Directory.CreateDirectory(_tempRoot);
        string extension = Path.GetExtension(path);
        if (extension.Length == 0) extension = ".wav";
        string file = Path.Combine(_tempRoot, $"{voiceId}{extension}");
        File.WriteAllBytes(file, bytes);
        string alias = $"msuispell{Environment.ProcessId}_{voiceId}";
        if (Mci($"open \"{file}\" alias {alias}") != 0) { TryDelete(file); return; }
        Mci($"setaudio {alias} volume to {(int)Math.Clamp(gain * 1000f, 0, 1000)}");
        if (Mci($"play {alias}{(looping ? " repeat" : "")}") != 0)
        { Mci($"close {alias}"); TryDelete(file); return; }
        var voice = new Voice(voiceId, alias, file, entry, unit, looping, trackHold);
        _voices[voiceId] = voice;
        _views[voiceId] = new VoiceView(entry, unit, looping);
        if (looping && trackHold) _holds[unit] = voiceId;
    }

    public void StopHold(ulong unit)
        => _jobs.Add(() => { if (_holds.Remove(unit, out long voice)) StopOnWorker(voice); });

    public void Stop(long voiceId)
    {
        if (voiceId == 0) return;
        _jobs.Add(() => StopOnWorker(voiceId));
    }

    private void StopOnWorker(long voiceId)
    {
        if (!_voices.Remove(voiceId, out Voice? voice)) return;
        Mci($"stop {voice.Alias}");
        Mci($"close {voice.Alias}");
        if (voice.TrackHold && _holds.GetValueOrDefault(voice.Unit) == voiceId)
            _holds.Remove(voice.Unit);
        _views.TryRemove(voiceId, out _);
        TryDelete(voice.File);
        Interlocked.Increment(ref _stops);
    }

    /// <summary>
    /// Game-thread pulse: gain math only. Volume changes and end-of-clip polling are
    /// enqueued for the worker; polling is throttled to 4 Hz because mciSendString is
    /// a synchronous cross-process call even for a status read.
    /// </summary>
    public void Tick(Vector3 listener, Func<ulong, (bool Found, Vector3 Position)> unitPosition)
    {
        foreach ((long id, VoiceView view) in _views)
        {
            if (!view.Looping) continue;
            (bool found, Vector3 position) = unitPosition(view.Unit);
            if (!found) { Stop(id); continue; }
            float gain = Gain(view.Entry, position, listener);
            if (gain <= 0) { Stop(id); continue; }
            int volume = (int)Math.Clamp(gain * 1000f, 0, 1000);
            _jobs.Add(() =>
            {
                if (_voices.TryGetValue(id, out Voice? voice))
                    Mci($"setaudio {voice.Alias} volume to {volume}");
            });
        }

        double now = Environment.TickCount64 / 1000.0;
        if (now - _lastPollSeconds < .25) return;
        _lastPollSeconds = now;
        _jobs.Add(() =>
        {
            foreach (Voice voice in _voices.Values.ToArray())
            {
                if (voice.Looping) continue;
                var status = new StringBuilder(32);
                Mci($"status {voice.Alias} mode", status);
                if (status.ToString().Trim().Equals("stopped", StringComparison.OrdinalIgnoreCase))
                    StopOnWorker(voice.Id);
            }
        });
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
        _jobs.CompleteAdding();
        if (!_worker.Join(TimeSpan.FromSeconds(2)))
            return; // background thread; the OS reclaims MCI aliases with the process
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: false); }
        catch { }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue,
        int returnLength, nint callback);
}
