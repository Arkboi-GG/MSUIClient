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

            // Decode in frame-sized bites into a growing buffer. A 55 s stereo
            // 44.1 kHz track lands around 10 MB of PCM, which is the whole point:
            // it is resident, so playback reads nothing.
            var pcm = new MemoryStream(Math.Max(1 << 16, mp3.Length * 8));
            var samples = new float[channels * 4096];
            var block = new byte[samples.Length * 2];
            while (true)
            {
                int read = file.ReadSamples(samples, 0, samples.Length);
                if (read <= 0) break;
                for (int i = 0; i < read; i++)
                {
                    // Clamp: MP3 reconstruction legitimately overshoots +/-1 and a
                    // wrapped short is a loud click, which would read as exactly
                    // the defect this whole change is removing.
                    int value = (int)(Math.Clamp(samples[i], -1f, 1f) * Scale);
                    block[i * 2] = (byte)value;
                    block[i * 2 + 1] = (byte)(value >> 8);
                }
                pcm.Write(block, 0, read * 2);
            }

            if (pcm.Length == 0) return false;
            wav = AudioMixer.BuildWav(pcm.ToArray(), channels, rate, bits: 16);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[audio] MP3 decode failed for '{path}' - {ex.Message}");
            return false;
        }
    }
}
