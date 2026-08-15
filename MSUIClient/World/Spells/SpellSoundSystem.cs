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
    public sealed record SoundPlayJournalEntry(long Sequence, double TimeSeconds,
        string Category, string RequestedCue, uint SoundId, string ResolvedPath,
        ulong Owner, bool Looping, bool TrackHold);

    private sealed record Voice(long Id, string Alias, string File, SoundEntry Entry,
        ulong Unit, bool Looping, bool TrackHold, long StartedAtMs = 0, bool Managed = false);

    /// <summary>Game-thread view of a live voice, for Tick's gain math. Written by the worker
    /// when a voice actually starts or dies; read by Tick on the game thread. Managed voices
    /// (the world soundscape's music/ambience channels) run their own gain envelopes, so Tick
    /// neither follows their unit nor overwrites their volume.</summary>
    private sealed record VoiceView(SoundEntry Entry, ulong Unit, bool Looping,
        string Category = "spell", bool Managed = false);

    // ── the master mix (2026-08-14, world-soundscape pass) ───────────────────
    // Category volumes follow the 1.12 registrar defaults (music 0.4, ambience
    // 0.6 - a fresh vanilla install is NOT uniform full volume). Categories are
    // decided by the CALLER, never derived from SoundEntries.SoundType: that is
    // the reference client's own rule (benilla pinned finding B3).

    public bool SoundEnabled { get; set; } = true;
    public bool MusicEnabled { get; set; } = true;
    public bool AmbienceEnabled { get; set; } = true;
    public float MasterVolume { get; set; } = 1f;
    public float EffectsVolume { get; set; } = 1f;
    public float MusicVolume { get; set; } = 0.4f;
    public float AmbienceVolume { get; set; } = 0.6f;

    /// <summary>The category multiplier applied on top of per-entry gain.</summary>
    public float CategoryAmp(string category)
    {
        if (!SoundEnabled) return 0f;
        float amp = category switch
        {
            "music" => MusicEnabled ? MusicVolume : 0f,
            "ambience" => AmbienceEnabled ? AmbienceVolume : 0f,
            _ => EffectsVolume,
        };
        return Math.Clamp(amp * MasterVolume, 0f, 1f);
    }

    private readonly MpqMount _mpq;
    private readonly SoundEntriesCatalog? _catalog;
    private readonly ConcurrentDictionary<string, byte[]> _customFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _lastVariant = [];
    private readonly string _tempRoot;
    private long _nextVoice;

    // Worker state. _voices and _holds are touched ONLY on the worker thread.
    private readonly BlockingCollection<Action> _jobs = [];
    private readonly Thread _worker;
    private readonly Dictionary<long, Voice> _voices = [];
    private readonly Dictionary<ulong, long> _holds = [];
    private readonly ConcurrentDictionary<long, VoiceView> _views = new();
    private readonly ConcurrentQueue<SoundPlayJournalEntry> _playJournal = new();
    private double _lastPollSeconds;

    public long Plays => Interlocked.Read(ref _plays);
    private long _plays;
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
        // STA, because MCI's MPEGVideo driver (the .mp3 path - all zone music)
        // is DirectShow underneath and needs COM on the calling thread. Without
        // this, "open x.mp3" fails with MCIERR 266 (cannot load driver) while
        // the same command works from any STA shell - a maddening asymmetry
        // that cost a debugging round on 2026-08-14.
        if (OperatingSystem.IsWindows()) _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    private void WorkerLoop()
    {
        // TryTake with a timeout instead of the blocking enumerable, so the
        // thread can PUMP WINDOW MESSAGES between jobs. The MPEGVideo driver
        // (every .mp3 - all zone music) is DirectShow underneath: it creates a
        // hidden notification window on this thread, and without dispatching
        // its messages playback stalls dead about ten seconds in - which
        // presented as "music tracks mysteriously end early".
        while (!_jobs.IsCompleted)
        {
            try
            {
                if (_jobs.TryTake(out Action? job, 50)) job?.Invoke();
            }
            catch { /* one bad cue must not kill the audio thread */ }
            PumpMessages();
        }
        foreach (long id in _voices.Keys.ToArray()) StopOnWorker(id);
    }

    private static void PumpMessages()
    {
        if (!OperatingSystem.IsWindows()) return;
        while (PeekMessage(out NativeMessage message, 0, 0, 0, 1))   // PM_REMOVE
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    public IReadOnlyList<SoundPlayJournalEntry> JournalSnapshot() => _playJournal.ToArray();

    /// <summary>Describe an authored cue without exposing the catalog owner.</summary>
    public bool TryGetEntry(uint? soundId, out SoundEntry entry)
    {
        entry = default;
        return soundId is uint id && id != 0 && _catalog?.TryGet(id, out entry) == true;
    }

    public long Play(uint? soundId, ulong unit, Vector3 source, Vector3 listener,
        bool forceLoop = false, bool trackHold = true, string category = "spell")
    {
        if (soundId is not uint id || id == 0 || _catalog?.TryGet(id, out SoundEntry entry) != true ||
            entry.Variants.Count == 0) return 0;
        return PlayResolved(id.ToString(), category, entry, unit, source, listener, forceLoop, trackHold);
    }

    /// <summary>
    /// Play a kit on a soundscape-owned channel: 2D (source == listener), no
    /// unit hold, and MANAGED - the caller runs the gain envelope via
    /// <see cref="SetVoiceGain"/> and Tick leaves the voice alone. The initial
    /// MCI volume is set from startGain, not from the entry, so a crossfade can
    /// begin at zero.
    /// </summary>
    public long PlayManaged(uint soundId, string category, bool forceLoop, float startGain)
    {
        if (soundId == 0 || _catalog?.TryGet(soundId, out SoundEntry entry) != true ||
            entry.Variants.Count == 0) return 0;
        SoundVariant variant = PickVariant(entry);
        LastCue = $"{entry.Id}:{variant.Path}";
        bool looping = forceLoop || entry.Looping;
        long sequence = Interlocked.Increment(ref _plays);
        _playJournal.Enqueue(new(sequence, Environment.TickCount64 / 1000.0,
            category, soundId.ToString(), entry.Id, variant.Path, 0, looping, false));
        while (_playJournal.Count > 4096) _playJournal.TryDequeue(out _);
        if (!OperatingSystem.IsWindows()) return 0;
        long voiceId = Interlocked.Increment(ref _nextVoice);
        string path = variant.Path;
        SoundEntry resolvedEntry = entry;
        float gain = Math.Clamp(startGain, 0f, 1f);
        // Register the view NOW, on the game thread. The worker takes real
        // time to start an mp3 (MPQ read, temp write, DirectShow open), and
        // IsLive answering "dead" during that window made the music transport
        // declare every track ended one frame after starting it - scheduling
        // the silence interval OVER a track that then played to completion.
        // The worker removes the view again on any failure path.
        _views[voiceId] = new VoiceView(resolvedEntry, 0, looping, category, Managed: true);
        _jobs.Add(() => PlayOnWorker(voiceId, path, resolvedEntry, 0, looping,
            trackHold: false, gain, category, managed: true));
        return voiceId;
    }

    /// <summary>Whether a voice is still live (started and not yet stopped or
    /// finished). The 4 Hz end-of-clip poll retires finished one-shots, so a
    /// music track's end shows up here within a quarter second.</summary>
    public bool IsLive(long voiceId) => voiceId != 0 && _views.ContainsKey(voiceId);

    /// <summary>Set a managed voice's absolute output gain (0..1). The caller
    /// owns the whole product - entry volume, category amp, fade envelope.</summary>
    public void SetVoiceGain(long voiceId, float gain)
    {
        if (voiceId == 0) return;
        int volume = (int)Math.Clamp(gain * 1000f, 0, 1000);
        _jobs.Add(() =>
        {
            if (_voices.TryGetValue(voiceId, out Voice? voice))
                Mci($"setaudio {voice.Alias} volume to {volume}");
        });
    }

    public long Play(string soundName, ulong unit, Vector3 source, Vector3 listener,
        string category = "ui")
    {
        if (_catalog?.TryGet(soundName, out SoundEntry entry) != true || entry.Variants.Count == 0) return 0;
        return PlayResolved(soundName, category, entry, unit, source, listener,
            forceLoop: false, trackHold: false);
    }

    /// <summary>
    /// Preview/play a creator-owned WAV or MP3 before it exists in an MPQ or in
    /// SoundEntries.dbc. The virtual MPQ path is also the path written by the
    /// creator exporter, so live preview and the produced patch name the same
    /// asset. The ordinary worker, positional gain and lifecycle machinery are
    /// deliberately reused.
    /// </summary>
    public long PlayCustom(string requestedCue, string virtualPath, byte[] bytes,
        ulong unit, Vector3 source, Vector3 listener, float volume, bool looping,
        bool noDuplicates, float minDistance, float cutoffDistance,
        bool trackHold = false, string category = "creator", uint extraFlags = 0,
        uint eax = 0)
    {
        if (string.IsNullOrWhiteSpace(virtualPath) || bytes.Length == 0) return 0;
        virtualPath = virtualPath.Replace('/', '\\');
        _customFiles[virtualPath] = bytes;
        uint flags = extraFlags & ~(0x200u | 0x20u);
        flags |= (looping ? 0x200u : 0u) | (noDuplicates ? 0x20u : 0u);
        var entry = new SoundEntry(0, 1, requestedCue,
            [new SoundVariant(virtualPath, 1)],
            Math.Clamp(volume, 0f, 1f), flags,
            Math.Max(0f, minDistance), Math.Max(0f, cutoffDistance), eax);
        return PlayResolved(requestedCue, category, entry, unit, source, listener,
            forceLoop: false, trackHold: trackHold);
    }

    public void RemoveCustomFile(string virtualPath)
    {
        if (!string.IsNullOrWhiteSpace(virtualPath))
            _customFiles.TryRemove(virtualPath.Replace('/', '\\'), out _);
    }

    private long PlayResolved(string requestedCue, string category, in SoundEntry entry,
        ulong unit, Vector3 source, Vector3 listener, bool forceLoop, bool trackHold)
    {
        SoundVariant variant = PickVariant(entry);
        LastCue = $"{entry.Id}:{variant.Path}";
        bool looping = forceLoop || entry.Looping;
        long sequence = Interlocked.Increment(ref _plays);
        _playJournal.Enqueue(new(sequence, Environment.TickCount64 / 1000.0,
            category, requestedCue, entry.Id, variant.Path, unit, looping, trackHold));
        while (_playJournal.Count > 4096) _playJournal.TryDequeue(out _);
        if (!OperatingSystem.IsWindows()) return 0;
        float gain = Gain(entry, source, listener) * CategoryAmp(category);
        if (gain <= 0) return 0;
        long voiceId = Interlocked.Increment(ref _nextVoice);
        string path = variant.Path;
        SoundEntry resolvedEntry = entry;
        _jobs.Add(() => PlayOnWorker(voiceId, path, resolvedEntry, unit, looping, trackHold,
            gain, category, managed: false));
        return voiceId;
    }

    public bool IsAuthoredLoop(uint? soundId)
        => soundId is uint id && _catalog?.TryGet(id, out SoundEntry entry) == true && entry.Looping;

    private void PlayOnWorker(long voiceId, string path, SoundEntry entry, ulong unit,
        bool looping, bool trackHold, float gain, string category = "spell", bool managed = false)
    {
        if (looping && trackHold && _holds.Remove(unit, out long held)) StopOnWorker(held);
        byte[]? bytes = _customFiles.TryGetValue(path, out byte[]? custom)
            ? custom : _mpq.ReadFile(path);
        if (bytes is null || bytes.Length == 0)
        {
            // Loudly, always: a missing file that presents later as "the zone
            // has no music" is the vmap lesson all over again.
            Console.WriteLine($"[audio] '{path}' not found in the MPQs ({category})");
            _views.TryRemove(voiceId, out _);
            return;
        }
        Directory.CreateDirectory(_tempRoot);
        string extension = Path.GetExtension(path);
        if (extension.Length == 0) extension = ".wav";
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            SanitizeWavHeader(bytes);
        string file = Path.Combine(_tempRoot, $"{voiceId}{extension}");
        File.WriteAllBytes(file, bytes);
        string alias = $"msuispell{Environment.ProcessId}_{voiceId}";
        int openError = Mci($"open \"{file}\" alias {alias}");
        if (openError != 0)
        {
            Console.WriteLine($"[audio] MCI open failed ({openError}) for '{path}' ({category})");
            _views.TryRemove(voiceId, out _);
            TryDelete(file);
            return;
        }
        Mci($"setaudio {alias} volume to {(int)Math.Clamp(gain * 1000f, 0, 1000)}");
        // "repeat" is an MPEGVideo (mp3) keyword only - waveaudio rejects it
        // with MCIERR 259, which is why every looping .wav cue was silently
        // failing to start. WAV loops restart from the 4 Hz poll instead.
        bool nativeRepeat = looping &&
            extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
        int playError = Mci($"play {alias}{(nativeRepeat ? " repeat" : "")}");
        if (playError != 0)
        {
            Console.WriteLine($"[audio] MCI play failed ({playError}) for '{path}' ({category})");
            _views.TryRemove(voiceId, out _);
            Mci($"close {alias}");
            TryDelete(file);
            return;
        }
        if (managed) Console.WriteLine($"[audio] playing '{path}' ({category}, loop={looping})");
        var voice = new Voice(voiceId, alias, file, entry, unit, looping, trackHold,
            Environment.TickCount64, managed);
        _voices[voiceId] = voice;
        _views[voiceId] = new VoiceView(entry, unit, looping, category, managed);
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
            if (!view.Looping || view.Managed) continue;
            (bool found, Vector3 position) = unitPosition(view.Unit);
            if (!found) { Stop(id); continue; }
            float gain = Gain(view.Entry, position, listener) * CategoryAmp(view.Category);
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
                var status = new StringBuilder(32);
                int statusError = Mci($"status {voice.Alias} mode", status);
                if (!status.ToString().Trim().Equals("stopped", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (voice.Managed)
                    Console.WriteLine($"[audio] '{Path.GetFileName(voice.File)}' mode " +
                        $"'{status.ToString().Trim()}' (err {statusError}) after " +
                        $"{(Environment.TickCount64 - voice.StartedAtMs) / 1000.0:F1}s " +
                        $"(loop={voice.Looping})");
                if (voice.Looping)
                {
                    // waveaudio has no native repeat: wrap the loop by hand.
                    Mci($"seek {voice.Alias} to start");
                    Mci($"play {voice.Alias}");
                }
                else
                {
                    StopOnWorker(voice.Id);
                }
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

    /// <summary>
    /// Repair the malformed fmt chunk many vanilla WAVs ship with: stereo
    /// 16-bit files whose blockAlign says 2 (and whose byteRate follows suit).
    /// MCI's waveaudio device validates blockAlign == channels * bits / 8 and
    /// refuses the file with MCIERR 326 ("no wave device can play this
    /// format") - which is how every ZoneAmbience bed came out silent. The
    /// data itself is fine; only the two derived header fields lie. Patched in
    /// place on our own copy of the bytes, PCM (format tag 1) only.
    /// </summary>
    private static void SanitizeWavHeader(byte[] wav)
    {
        if (wav.Length < 44 ||
            wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
            wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
            return;

        // Walk the chunks to the fmt chunk; it is almost always at 12 but a
        // LIST chunk before it costs nothing to step over.
        int at = 12;
        while (at + 8 <= wav.Length)
        {
            uint chunkSize = BitConverter.ToUInt32(wav, at + 4);
            if (wav[at] == 'f' && wav[at + 1] == 'm' && wav[at + 2] == 't' && wav[at + 3] == ' ')
            {
                int fmt = at + 8;
                if (fmt + 16 > wav.Length) return;
                ushort format = BitConverter.ToUInt16(wav, fmt);
                if (format != 1) return;   // PCM only; compressed blockAligns are real
                ushort channels = BitConverter.ToUInt16(wav, fmt + 2);
                uint rate = BitConverter.ToUInt32(wav, fmt + 4);
                ushort bits = BitConverter.ToUInt16(wav, fmt + 14);
                if (channels is 0 or > 8 || bits is 0 or > 32) return;
                ushort expectedAlign = (ushort)(channels * bits / 8);
                uint expectedRate = rate * expectedAlign;
                if (BitConverter.ToUInt16(wav, fmt + 12) != expectedAlign)
                    BitConverter.TryWriteBytes(wav.AsSpan(fmt + 12, 2), expectedAlign);
                if (BitConverter.ToUInt32(wav, fmt + 8) != expectedRate)
                    BitConverter.TryWriteBytes(wav.AsSpan(fmt + 8, 4), expectedRate);
                return;
            }
            at += 8 + (int)chunkSize + ((int)chunkSize & 1);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, nint hwnd,
        uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref NativeMessage message);
}
