using System.IO;
using System.Media;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Процедурный синтезатор тактильных звуков (Haptic Audio).
/// Генерирует короткие звуковые импульсы на лету в памяти (WAV в RAM) без внешних файлов.
/// </summary>
public static class HapticAudio
{
    public static bool Enabled { get; set; } = true;

    private static SoundPlayer? _clickPlayer;
    private static SoundPlayer? _hoverPlayer;
    private static SoundPlayer? _selectPlayer;
    private static SoundPlayer? _successPlayer;
    private static SoundPlayer? _scrollTickPlayer;

    static HapticAudio()
    {
        try
        {
            _hoverPlayer = new SoundPlayer(new MemoryStream(GenerateWave(frequency: 880, endFreq: 1200, durationMs: 25, volume: 0.07)));
            _hoverPlayer.Load();

            _clickPlayer = new SoundPlayer(new MemoryStream(GenerateWave(frequency: 480, endFreq: 240, durationMs: 35, volume: 0.15)));
            _clickPlayer.Load();

            _selectPlayer = new SoundPlayer(new MemoryStream(GenerateWave(frequency: 620, endFreq: 950, durationMs: 45, volume: 0.18)));
            _selectPlayer.Load();

            _successPlayer = new SoundPlayer(new MemoryStream(GenerateSuccessChime()));
            _successPlayer.Load();

            _scrollTickPlayer = new SoundPlayer(new MemoryStream(GenerateWave(frequency: 1500, endFreq: 1900, durationMs: 10, volume: 0.04)));
            _scrollTickPlayer.Load();
        }
        catch
        {
            // Безопасный фоллбек, если аудиоустройство недоступно
        }
    }

    public static void PlayHover()
    {
        if (!Enabled) return;
        try { _hoverPlayer?.Play(); } catch { }
    }

    public static void PlayScrollTick()
    {
        if (!Enabled) return;
        try { _scrollTickPlayer?.Play(); } catch { }
    }

    public static void PlayClick()
    {
        if (!Enabled) return;
        try { _clickPlayer?.Play(); } catch { }
    }

    public static void PlaySelect()
    {
        if (!Enabled) return;
        try { _selectPlayer?.Play(); } catch { }
    }

    public static void PlaySuccess()
    {
        if (!Enabled) return;
        try { _successPlayer?.Play(); } catch { }
    }

    private static byte[] GenerateWave(int frequency, int endFreq, int durationMs, double volume)
    {
        int sampleRate = 22050;
        int numSamples = (sampleRate * durationMs) / 1000;
        short[] samples = new short[numSamples];

        double phase = 0;
        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / numSamples;
            double currentFreq = frequency + (endFreq - frequency) * t;
            double envelope = Math.Sin(Math.PI * t); // Smooth bell curve envelope
            phase += 2 * Math.PI * currentFreq / sampleRate;
            double val = Math.Sin(phase) * envelope * volume * short.MaxValue;
            samples[i] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
        }

        return CreateWavHeaderAndData(samples, sampleRate);
    }

    private static byte[] GenerateSuccessChime()
    {
        int sampleRate = 22050;
        int durationMs = 380;
        int numSamples = (sampleRate * durationMs) / 1000;
        short[] samples = new short[numSamples];

        // Мажорный трезвучный аккорд (C6 - E6 - G6: 1046 Hz, 1318 Hz, 1568 Hz)
        double p1 = 0, p2 = 0, p3 = 0;
        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / numSamples;
            double envelope = Math.Pow(1.0 - t, 1.8); // Exponential decay
            p1 += 2 * Math.PI * 1046.5 / sampleRate;
            p2 += 2 * Math.PI * 1318.5 / sampleRate;
            p3 += 2 * Math.PI * 1567.98 / sampleRate;

            double val = (Math.Sin(p1) * 0.4 + Math.Sin(p2) * 0.35 + Math.Sin(p3) * 0.35) * envelope * 0.22 * short.MaxValue;
            samples[i] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
        }

        return CreateWavHeaderAndData(samples, sampleRate);
    }

    private static byte[] CreateWavHeaderAndData(short[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int subChunk2Size = samples.Length * 2;
        int chunkSize = 36 + subChunk2Size;

        // RIFF header
        bw.Write(new[] { 'R', 'I', 'F', 'F' });
        bw.Write(chunkSize);
        bw.Write(new[] { 'W', 'A', 'V', 'E' });

        // fmt chunk
        bw.Write(new[] { 'f', 'm', 't', ' ' });
        bw.Write(16); // Subchunk1Size
        bw.Write((short)1); // PCM format
        bw.Write((short)1); // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2); // ByteRate
        bw.Write((short)2); // BlockAlign
        bw.Write((short)16); // BitsPerSample

        // data chunk
        bw.Write(new[] { 'd', 'a', 't', 'a' });
        bw.Write(subChunk2Size);
        for (int i = 0; i < samples.Length; i++)
        {
            bw.Write(samples[i]);
        }

        return ms.ToArray();
    }
}
