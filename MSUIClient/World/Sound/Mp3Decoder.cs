using NLayer;

namespace MSUIClient.World.Sound;

/// <summary>
/// MP3 to PCM, once, off the audio thread - what got zone music off MCI.
///
/// WHY THIS REPLACED THE SYSTEM DECODER. Measured 2026-08-16, in Onyxia's Lair:
/// the ambience bed, playing as one in-memory buffer through waveOut, was clean.
/// The music track, playing through MCI's MPEGVideo device off a temp file,
/// skipped - continuously, for the whole track, while the client was doing nothing
/// but four status calls a second. A synthesized sine through the waveOut path was
/// clean in the same breath, which ruled out the machine, the audio driver and our
/// own scheduling and left MCI holding it alone.
///
/// Decoding here rather than at playback time is the point: the samples are fully
/// resident before a device is opened, so nothing about playback can be starved by
/// a decoder, a disk or a filter graph.
///
/// NLayer is a managed Layer I/II/III decoder - no native binary, no COM, nothing
/// platform-specific. The comment this file replaces argued for MCI on the grounds
/// that it avoided "a second media package"; that reasoning was sound right up
/// until the package it avoided turned out to be the thing that skipped.
/// </summary>
internal static class Mp3Decoder
{
    /// <summary>16-bit is what waveOut takes and what everything else here speaks.
    /// NLayer decodes to float, so this is the only conversion in the path.</summary>
    private const float Scale = short.MaxValue;

    /// <summary>Ceiling on one decoded track, so a corrupt duration cannot ask for a
    /// gigabyte. Comfortably past the longest music the client ships.</summary>
    private const long MaxPcmBytes = 192L << 20;

    /// <summary>Decode a whole MP3 to a RIFF/WAVE buffer. False leaves the caller
    /// on its existing path rather than silent.</summary>
    public static bool TryDecode(byte[] mp3, string path, out byte[] wav)
    {
        wav = [];
        if (mp3.Length == 0) return false;
        try
        {
            using var source = new MemoryStream(mp3, writable: false);
            using var file = new MpegFile(source);
            int channels = file.Channels;
            int rate = file.SampleRate;
            if (channels is not (1 or 2) || rate <= 0) return false;

            // ONE ALLOCATION FOR THE WHOLE TRACK, and the samples land directly in
            // it behind the WAV header. This used to be a growing MemoryStream, then
            // ToArray(), then BuildWav's own array plus a copy, then the voice's
            // unmanaged copy: four full-track buffers for one song. On the 28 MB glue
            // theme that is over a hundred megabytes of allocation, three of them on
            // the Large Object Heap, at the exact moment the client is also streaming
            // a world - and the 2026-08-30 log caught what that costs: the audio
            // worker descheduled 250 ms with an EMPTY queue, the game thread at
            // 0.35 M cycles/ms ("blocked or descheduled"), and the device measurably
            // 180 ms short of real time. Starting a track must not be a memory storm.
            long estimate = (long)(file.Duration.TotalSeconds * rate * channels) * 2;
            if (estimate <= 0 || estimate > MaxPcmBytes) estimate = 1 << 20;
            // A second of slack absorbs the usual encoder-padding disagreement
            // between the duration estimate and the decoded sample count.
            var buffer = new byte[AudioMixer.HeaderBytes +
                                  (int)Math.Min(MaxPcmBytes, estimate + rate * channels * 2L)];
            var samples = new float[channels * 4096];
            int written = 0;
            while (true)
            {
                int read = file.ReadSamples(samples, 0, samples.Length);
                if (read <= 0) break;
                int need = AudioMixer.HeaderBytes + written + read * 2;
                if (need > buffer.Length)
                {
                    // The estimate was short. Grow once by half rather than doubling
                    // per block; a correct estimate never reaches this.
                    long grown = Math.Min(MaxPcmBytes + AudioMixer.HeaderBytes,
                        Math.Max(need, buffer.LongLength + buffer.LongLength / 2));
                    if (grown <= buffer.LongLength) break;   // at the ceiling: stop cleanly
                    Array.Resize(ref buffer, (int)grown);
                }
                var target = buffer.AsSpan(AudioMixer.HeaderBytes + written);
                for (int i = 0; i < read; i++)
                {
                    // Clamp: MP3 reconstruction legitimately overshoots +/-1 and a
                    // wrapped short is a loud click, which would read as exactly
                    // the defect this whole change is removing.
                    int value = (int)(Math.Clamp(samples[i], -1f, 1f) * Scale);
                    target[i * 2] = (byte)value;
                    target[i * 2 + 1] = (byte)(value >> 8);
                }
                written += read * 2;
            }

            if (written == 0) return false;
            // The chunk sizes describe the samples; any slack after them is ignored
            // by every reader on this path (see AudioMixer.WriteWavHeader).
            AudioMixer.WriteWavHeader(buffer, written, channels, rate, bits: 16);
            wav = buffer;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[audio] MP3 decode failed for '{path}' - {ex.Message}");
            return false;
        }
    }
}
