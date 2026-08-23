using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using MSUIClient.Formats;

namespace MSUIClient.World.Sound;

/// <summary>A play request, in the only terms the device understands: a file, a
/// category, and an absolute gain. Everything about WHICH file and WHY belongs to
/// the caller; the extra fields are carried only so the journal can name it.</summary>
public readonly record struct AudioPlayRequest(
    string Path,
    string Category,
    float Gain,
    bool Looping,
    string RequestedCue = "",
    uint SoundId = 0,
    ulong Owner = 0,
    bool TrackHold = false,
    /// <summary>Start even at zero gain. A crossfade begins silent and rises; an
    /// effect that is already inaudible should never open a device at all.</summary>
    bool StartWhenSilent = false,
    /// <summary>Log the start. The long-lived world beds are worth a line each;
    /// a combat cue firing ten times a second is not.</summary>
    bool Announce = false,
    /// <summary>Stereo balance, -1 left through 0 center to +1 right.</summary>
    float Pan = 0f);

/// <summary>
/// THE AUDIO DEVICE, and nothing else: one MCI/DirectShow backend, one worker
/// thread, one master mix, one set of live voices.
///
/// It knows about files, gains, categories and voice ids. It does NOT know what a
/// spell is, what a zone is, or that SoundEntries.dbc exists - those are policy,
/// and policy lives in the systems above (SpellSoundSystem, WorldSoundscape). The
/// split exists because there is exactly one sound device and several unrelated
/// callers, and the previous arrangement had the world soundscape borrowing the
/// spell system's channels through a hole punched in it for that purpose.
///
/// EVERY VOICE IS PCM THE DRIVER ALREADY HOLDS. Reading, decoding and the WAV
/// header repair all happen on the thread pool; the worker thread exists only to
/// keep waveOut calls off the game thread and to keep a stop ordered ahead of the
/// play that replaces it. Callers never block, and playback cannot be starved by
/// this process because nothing in this process feeds it.
///
/// This replaced MCI on 2026-08-16, and the reason is worth keeping: MCI's
/// MPEGVideo device skipped continuously on zone music while the client was idle,
/// and it did so through a DirectShow graph whose COM apartment and notification
/// window lived on this thread, streaming off a temp file. A synthesized sine and
/// an ambience bed through the path below were both clean in the same session.
///
/// On platforms without winmm the routing stays fully observable - journal,
/// counters, voice ids, liveness - and silent.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public sealed record SoundPlayJournalEntry(long Sequence, double TimeSeconds,
        string Category, string RequestedCue, uint SoundId, string ResolvedPath,
        ulong Owner, bool Looping, bool TrackHold);

    /// <summary>A live voice. There is one kind now: PCM the driver already holds.</summary>
    private sealed record Voice(long Id, string Category, long StartedAtMs, bool Looping,
        WaveOutVoice Pcm);

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
    private readonly ConcurrentDictionary<string, byte[]> _customFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private long _nextVoice;

    // Worker state. _voices is touched ONLY on the worker thread.
    //
    // A plain queue plus an event rather than BlockingCollection, because the
    // worker must wait on the WINDOW MESSAGE QUEUE as well as on work, and only a
    // real kernel handle can go into MsgWaitForMultipleObjectsEx alongside it.
    private readonly ConcurrentQueue<Action> _jobQueue = new();
    private readonly AutoResetEvent _jobSignal = new(false);
    private volatile bool _shutdown;
    private readonly Thread _worker;
    private readonly Dictionary<long, Voice> _voices = [];

    /// <summary>
    /// Voices that have been REQUESTED and not yet stopped or finished.
    ///
    /// Registered at request time rather than at start time, on purpose: the worker
    /// takes real time to open an mp3, and IsLive answering "dead" during that
    /// window made the music transport declare every track ended one frame after
    /// starting it - scheduling the silence interval OVER a track that then played
    /// to completion. Every failure path removes the entry again.
    /// </summary>
    private readonly ConcurrentDictionary<long, byte> _live = new();

    private readonly ConcurrentQueue<SoundPlayJournalEntry> _playJournal = new();

    /// <summary>
    /// Latest wanted volume per voice, plus a single latch saying a drain job is
    /// already in the queue.
    ///
    /// A VOLUME IS A STATE, NOT AN EVENT: only the newest value matters, so a fade
    /// must never become a queue of individual commands. A 5 s ambience crossfade
    /// legitimately produces a new value nearly every frame, and enqueueing one
    /// closure per change put 45 synchronous MCI round trips behind a blocking
    /// open - measured, in a raid, with two voices live. Collapsed here, a fade of
    /// any length costs one setaudio per voice per worker drain.
    /// </summary>
    private readonly record struct PendingMix(int Volume, int? Pan);
    private readonly ConcurrentDictionary<long, PendingMix> _pendingVolume = new();
    private int _volumeDrainQueued;

    private double _lastPollSeconds;

    public long Plays => Interlocked.Read(ref _plays);
    private long _plays;
    public long Stops => Interlocked.Read(ref _stops);
    private long _stops;

    /// <summary>Whether this platform actually has a backend behind the routing.</summary>
    public bool Audible => OperatingSystem.IsWindows();

    public AudioMixer(MpqMount mpq)
    {
        _mpq = mpq;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "audio-mixer",
            // The worker owns every MCI call and pumps DirectShow's hidden
            // notification window. Keep that pump responsive while startup's
            // asset parsers and upload worker are busy, especially after the
            // process loses Windows' foreground scheduling boost.
            Priority = ThreadPriority.AboveNormal,
        };
        // No apartment state and no message pump: MCI was the only thing here
        // that was COM, and it is gone.
        _worker.Start();
    }

    public IReadOnlyList<SoundPlayJournalEntry> JournalSnapshot() => _playJournal.ToArray();

    /// <summary>Whether a voice is still live (requested and not yet stopped or
    /// finished). The end-of-clip poll retires finished one-shots, so a music
    /// track's end shows up here within a quarter second.</summary>
    public bool IsLive(long voiceId) => voiceId != 0 && _live.ContainsKey(voiceId);

    /// <summary>Register bytes under a virtual path so they can be played before
    /// they exist in any MPQ (the creator's authored cues). The virtual path is
    /// also what the creator exporter writes, so preview and patch name the same
    /// asset.</summary>
    public void RegisterCustomFile(string virtualPath, byte[] bytes)
    {
        if (!string.IsNullOrWhiteSpace(virtualPath) && bytes.Length > 0)
            _customFiles[virtualPath.Replace('/', '\\')] = bytes;
    }

    public void RemoveCustomFile(string virtualPath)
    {
        if (!string.IsNullOrWhiteSpace(virtualPath))
            _customFiles.TryRemove(virtualPath.Replace('/', '\\'), out _);
    }

    private const string TestTonePath = "msui-audio-selftest.wav";

    /// <summary>
    /// A KNOWN-GOOD SIGNAL THROUGH A KNOWN-GOOD PATH, so "is the skipping ours"
    /// stops being a matter of opinion.
    ///
    /// The samples are synthesized here, so no MPQ, no decoder and no disk touch
    /// them. They go out as one prepared waveOut buffer the driver owns outright
    /// and loops itself, so this process feeds it nothing and can starve it in no
    /// way. A continuous sine is also the easiest waveform in existence to hear a
    /// discontinuity in.
    ///
    /// If THIS skips, the fault is below every line of code in this directory.
    /// </summary>
    public long PlayTestTone(int hz = 440, float seconds = 2f)
    {
        const int rate = 44100;
        int frames = (int)(rate * Math.Clamp(seconds, 0.25f, 10f));
        var pcm = new byte[frames * 2];

        // A whole number of cycles, so the loop point is continuous and any click
        // heard at the wrap is the SYSTEM's, not an artefact of this generator.
        double cycles = Math.Max(1, Math.Round(frames * (double)hz / rate));
        for (int i = 0; i < frames; i++)
        {
            double phase = 2.0 * Math.PI * cycles * i / frames;
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2, 2), (short)(Math.Sin(phase) * 9000));
        }

        byte[] wav = BuildWav(pcm, channels: 1, rate: rate, bits: 16);
        RegisterCustomFile(TestTonePath, wav);
        Console.WriteLine($"[audio] SELF TEST: {hz} Hz sine, {frames} frames, synthesized in " +
                          "memory, looped by the driver. Any break you hear in this is not ours.");
        return Play(new AudioPlayRequest(TestTonePath, "ambience", 0.35f, Looping: true,
            RequestedCue: "audio-self-test", StartWhenSilent: true, Announce: true));
    }

    /// <summary>Start a voice. Returns its id, or 0 when nothing was started -
    /// which is not an error: an inaudible or unsupported request is still
    /// journalled and counted, because "we decided to play this" is the fact the
    /// audio protocols assert on.</summary>
    public long Play(in AudioPlayRequest request)
    {
        long sequence = Interlocked.Increment(ref _plays);
        _playJournal.Enqueue(new(sequence, Environment.TickCount64 / 1000.0,
            request.Category, request.RequestedCue, request.SoundId, request.Path,
            request.Owner, request.Looping, request.TrackHold));
        while (_playJournal.Count > 4096) _playJournal.TryDequeue(out _);

        if (!Audible) return 0;
        if (request.Gain <= 0f && !request.StartWhenSilent) return 0;

        long voiceId = Interlocked.Increment(ref _nextVoice);
        _live[voiceId] = 0;
        string path = request.Path;
        string category = request.Category;
        float gain = request.Gain;
        bool looping = request.Looping;
        bool announce = request.Announce;
        float pan = request.Pan;
        Task<PreparedSource> fileTask = PrepareFile(voiceId, path);
        Enqueue(() => PlayOnWorker(voiceId, path, fileTask, looping, gain, pan, category, announce));
        return voiceId;
    }

    public void Stop(long voiceId)
    {
        if (voiceId == 0) return;
        _live.TryRemove(voiceId, out _);
        Enqueue(() => StopOnWorker(voiceId));
    }

    /// <summary>Set a voice's absolute output gain (0..1). The caller owns the
    /// whole product - entry volume, category amp, fade envelope.</summary>
    public void SetVoiceGain(long voiceId, float gain)
    {
        if (voiceId == 0) return;
        QueueVolume(voiceId, (int)Math.Clamp(gain * 1000f, 0, 1000), pan: null);
    }

    /// <summary>Set one positional voice's absolute gain and stereo balance atomically.</summary>
    public void SetVoiceGainPan(long voiceId, float gain, float pan)
    {
        if (voiceId == 0) return;
        QueueVolume(voiceId, (int)Math.Clamp(gain * 1000f, 0, 1000),
            (int)Math.Clamp(pan * 1000f, -1000f, 1000f));
    }

    /// <summary>Record the wanted volume and make sure exactly one drain job is
    /// pending. Callable from the game thread at frame rate without growing the
    /// queue by a frame's worth of work each time.</summary>
    private void QueueVolume(long voiceId, int volume, int? pan)
    {
        _pendingVolume[voiceId] = new PendingMix(volume, pan);
        if (Interlocked.Exchange(ref _volumeDrainQueued, 1) == 0)
            Enqueue(ApplyPendingVolumes);
    }

    private void ApplyPendingVolumes()
    {
        // Clear the latch BEFORE draining: a value written while this runs has to
        // be able to queue a fresh job, or the last step of a fade can be stranded
        // until something else changes. The cost of losing that race is one
        // redundant drain, which is nothing; the cost of the other order is a
        // voice stuck at the wrong volume.
        Interlocked.Exchange(ref _volumeDrainQueued, 0);
        foreach (long id in _pendingVolume.Keys.ToArray())
        {
            if (!_pendingVolume.TryRemove(id, out PendingMix mix)) continue;
            if (!_voices.TryGetValue(id, out Voice? voice)) continue;
            if (mix.Pan is int pan) voice.Pcm.SetMix(mix.Volume / 1000f, pan / 1000f);
            else voice.Pcm.SetGain(mix.Volume / 1000f);
        }
    }

    /// <summary>
    /// Retire finished one-shots. The driver owns every voice outright now, so
    /// this reads a flag in the buffer header - no round trip, and looping voices
    /// never finish at all.
    /// </summary>
    public void PollFinished()
    {
        double now = Environment.TickCount64 / 1000.0;
        if (now - _lastPollSeconds < .25) return;
        _lastPollSeconds = now;
        Enqueue(() =>
        {
            foreach (Voice voice in _voices.Values.ToArray())
                if (voice.Pcm.Finished) StopOnWorker(voice.Id);
        });
    }

    // ── worker ───────────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        // A plain work queue now. Nothing on this thread is a cross-process call
        // any more: opening a waveOut device costs a millisecond or two against
        // the 50-300 ms mciSendString("open") used to, and there is no COM
        // apartment and no notification window left to pump. The thread survives
        // only to keep device work off the game thread and to keep stops ordered
        // ahead of the plays that replace them.
        long lastRanAt = Environment.TickCount64;
        while (!_shutdown)
        {
            try
            {
                while (_jobQueue.TryDequeue(out Action? job)) job();
            }
            catch { /* one bad cue must not kill the audio thread */ }

            // THE STALL WATCHDOG, kept because it is what settled this: it says
            // whether a gap belongs to our own work (queue non-empty) or to the OS
            // not scheduling this thread (queue empty). It should now never fire.
            long ranAt = Environment.TickCount64;
            long gap = ranAt - lastRanAt;
            if (gap >= AudioStallWarnMs && ranAt - _lastStallReportAtMs >= 1000)
            {
                _lastStallReportAtMs = ranAt;
                Console.WriteLine($"[audio] worker stalled for {gap} ms " +
                                  $"(queue {_jobQueue.Count}, voices {_voices.Count})");
            }
            lastRanAt = ranAt;
            _jobSignal.WaitOne(WorkerWakeMs);
        }
        foreach (long id in _voices.Keys.ToArray()) StopOnWorker(id);
    }

    /// <summary>Hand a job to the worker and wake it now, rather than at the end of
    /// whatever it is currently sleeping through.</summary>
    private void Enqueue(Action job)
    {
        if (_shutdown) return;
        _jobQueue.Enqueue(job);
        _jobSignal.Set();
    }

    private const int WorkerWakeMs = 15;

    /// <summary>Pump gap worth reporting. Comfortably above the wake interval and
    /// a normal status round trip, comfortably below a single blocking open.</summary>
    private const int AudioStallWarnMs = 120;

    private long _lastStallReportAtMs;

    /// <summary>Decoded, ready-to-play PCM in a RIFF/WAVE wrapper. Null means the
    /// asset could not be found or decoded.</summary>
    private readonly record struct PreparedSource(byte[]? Pcm);

    /// <summary>
    /// Read and DECODE off the audio thread, so what the worker receives is
    /// already samples.
    ///
    /// This is where the whole cost of a cue now lives - the MPQ read, the header
    /// repair, the MP3 decode - and none of it is anywhere near playback. Started
    /// at request time so it overlaps whatever the worker is doing, and job order
    /// is unchanged: the worker still runs plays and stops as they were asked for.
    /// </summary>
    private Task<PreparedSource> PrepareFile(long voiceId, string path)
        => Task.Run(() =>
        {
            _ = voiceId;
            bool fromCustom = _customFiles.TryGetValue(path, out byte[]? custom);
            byte[]? bytes = fromCustom ? custom : _mpq.ReadFile(path);
            if (bytes is null || bytes.Length == 0)
            {
                Console.WriteLine($"[audio] '{path}' not found in the MPQs");
                return default;
            }
            if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                return Mp3Decoder.TryDecode(bytes, path, out byte[] decoded)
                    ? new PreparedSource(decoded) : default;

            // A custom preview's array is the caller's, held in _customFiles and
            // replayable; patch our own copy of it, never theirs.
            if (fromCustom) bytes = (byte[])bytes.Clone();
            SanitizeWavHeader(bytes);
            return new PreparedSource(bytes);
        });

    private void PlayOnWorker(long voiceId, string path, Task<PreparedSource> fileTask,
        bool looping, float gain, float pan, string category, bool announce)
    {
        // Stopped before it ever started: do not open a device for a dead voice.
        if (!_live.ContainsKey(voiceId)) return;

        PreparedSource source;
        try
        {
            source = fileTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[audio] '{path}' could not be prepared ({category}) - {ex.Message}");
            _live.TryRemove(voiceId, out _);
            return;
        }
        if (source.Pcm is not { } pcmBytes)
        {
            // Loudly, always: a missing file that presents later as "the zone
            // has no music" is the vmap lesson all over again.
            Console.WriteLine($"[audio] '{path}' could not be decoded ({category})");
            _live.TryRemove(voiceId, out _);
            return;
        }

        if (WaveOutVoice.Open(pcmBytes, looping, gain, pan) is not { } voice)
        {
            Console.WriteLine($"[audio] waveOut refused '{path}' ({category})");
            _live.TryRemove(voiceId, out _);
            return;
        }
        if (announce)
            Console.WriteLine($"[audio] playing '{path}' ({category}, loop={looping}, " +
                              $"{pcmBytes.Length / 1024} KB resident)");
        _voices[voiceId] = new Voice(voiceId, category, Environment.TickCount64, looping, voice);
    }

    private void StopOnWorker(long voiceId)
    {
        _live.TryRemove(voiceId, out _);
        if (!_voices.Remove(voiceId, out Voice? voice)) return;
        _pendingVolume.TryRemove(voiceId, out _);
        voice.Pcm.Dispose();
        Interlocked.Increment(ref _stops);
    }

    // ── file repair ──────────────────────────────────────────────────────────

    /// <summary>
    /// Repair the malformed fmt chunk many vanilla WAVs ship with: stereo
    /// 16-bit files whose blockAlign says 2 (and whose byteRate follows suit).
    /// MCI's waveaudio device validates blockAlign == channels * bits / 8 and
    /// refuses the file with MCIERR 326 ("no wave device can play this
    /// format") - which is how every ZoneAmbience bed came out silent. The
    /// data itself is fine; only the two derived header fields lie. Patched in
    /// place on our own copy of the bytes, PCM (format tag 1) only.
    /// </summary>
    /// <summary>Wrap raw PCM in the RIFF/WAVE container the waveOut path takes.
    /// The decoders hand back samples; this is the only thing between them and the
    /// driver.</summary>
    internal static byte[] BuildWav(byte[] pcm, int channels, int rate, int bits)
    {
        int blockAlign = channels * bits / 8;
        var wav = new byte[44 + pcm.Length];
        void Ascii(int at, string tag)
        {
            for (int i = 0; i < 4; i++) wav[at + i] = (byte)tag[i];
        }
        Ascii(0, "RIFF");
        BitConverter.TryWriteBytes(wav.AsSpan(4, 4), 36 + pcm.Length);
        Ascii(8, "WAVE");
        Ascii(12, "fmt ");
        BitConverter.TryWriteBytes(wav.AsSpan(16, 4), 16);
        BitConverter.TryWriteBytes(wav.AsSpan(20, 2), (ushort)1);   // PCM
        BitConverter.TryWriteBytes(wav.AsSpan(22, 2), (ushort)channels);
        BitConverter.TryWriteBytes(wav.AsSpan(24, 4), rate);
        BitConverter.TryWriteBytes(wav.AsSpan(28, 4), rate * blockAlign);
        BitConverter.TryWriteBytes(wav.AsSpan(32, 2), (ushort)blockAlign);
        BitConverter.TryWriteBytes(wav.AsSpan(34, 2), (ushort)bits);
        Ascii(36, "data");
        BitConverter.TryWriteBytes(wav.AsSpan(40, 4), pcm.Length);
        pcm.CopyTo(wav, 44);
        return wav;
    }

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

    public void Dispose()
    {
        _shutdown = true;
        _jobSignal.Set();
        if (!_worker.Join(TimeSpan.FromSeconds(2)))
            return; // background thread; the OS reclaims MCI aliases with the process
        _jobSignal.Dispose();
    }

}
