using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiSpeaker;

/// <summary>
/// Plays the captured audio on one speaker after a configurable delay.
/// Audio is held in a ring buffer; the playback rate is nudged by up to ±0.5%
/// so the buffer stays at the requested delay. That absorbs the clock drift
/// between the source device and this speaker, and also converts sample rates.
/// </summary>
sealed class SpeakerOutput : IWaveProvider, IDisposable
{
    public const int MaxDelayMs = 2000;
    const int BaseLatencyMs = 60;         // cushion against capture/playback jitter
    const int ResyncThresholdMs = 80;     // bigger errors (e.g. delay edited) jump instead of drifting
    const double MaxRateCorrection = 0.005;
    const double CorrectionGain = 0.05;   // rate change per second of buffer error

    readonly object sync = new();
    readonly WasapiOut player;
    readonly int sourceRate;
    readonly int outputRate;
    readonly int channels;
    readonly float[] ring;
    readonly int ringFrames;
    float[] scratch = [];

    long writtenFrames;
    double readFrame;
    double averageBuffered;
    bool playing;  // false while silent and waiting for the buffer to reach the target delay
    int appliedDelayMs;

    public string Name { get; }
    public WaveFormat WaveFormat { get; }
    public volatile int DelayMs;
    /// <summary>Plays silence while keeping timing intact; used while measuring other speakers.</summary>
    public volatile bool Muted;

    /// <summary>Raised from the audio thread when playback stops; the exception is null on a normal stop.</summary>
    public event Action<SpeakerOutput, Exception?>? Stopped;

    public SpeakerOutput(MMDevice device, int sourceSampleRate, int delayMs)
    {
        Name = device.FriendlyName;
        DelayMs = delayMs;
        sourceRate = sourceSampleRate;

        // Render in the device's own mix format so Windows does no further conversion.
        var mix = device.AudioClient.MixFormat;
        outputRate = mix.SampleRate;
        channels = mix.Channels;
        WaveFormat = new WaveFormatExtensible(outputRate, 32, channels);

        ringFrames = sourceRate * (MaxDelayMs + BaseLatencyMs + 1000) / 1000;
        ring = new float[ringFrames * channels];

        player = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        player.PlaybackStopped += (_, e) => Stopped?.Invoke(this, e.Exception);
        player.Init(this);
    }

    public void Start() => player.Play();

    /// <summary>Called from the capture thread with interleaved float samples.</summary>
    public void Write(float[] source, int frames, int sourceChannels)
    {
        lock (sync)
        {
            for (int f = 0; f < frames; f++)
            {
                int dst = (int)(writtenFrames % ringFrames) * channels;
                int src = f * sourceChannels;
                for (int c = 0; c < channels; c++)
                {
                    ring[dst + c] =
                        sourceChannels == 1 ? source[src] :
                        channels == 1 ? (source[src] + source[src + 1]) * 0.5f :
                        c < sourceChannels ? source[src + c] : 0f;
                }
                writtenFrames++;
            }

            // Playback stalled long enough to overrun the ring: skip ahead.
            if (writtenFrames - readFrame > ringFrames - 2)
                playing = false;
        }
    }

    /// <summary>Called by WASAPI on its playback thread.</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        int frames = count / (channels * 4);
        int samples = frames * channels;
        if (scratch.Length < samples)
            scratch = new float[samples];

        lock (sync)
            Fill(scratch, frames);
        if (Muted)
            Array.Clear(scratch, 0, samples);

        Buffer.BlockCopy(scratch, 0, buffer, offset, samples * 4);
        return samples * 4;
    }

    void Fill(float[] output, int frames)
    {
        double target = (DelayMs + BaseLatencyMs) * sourceRate / 1000.0;
        double resync = ResyncThresholdMs * sourceRate / 1000.0;
        double buffered = writtenFrames - readFrame;

        if (DelayMs != appliedDelayMs)
        {
            // Apply a delay edit at once by moving the read position; the ring still holds the
            // older audio, so an increase briefly repeats sound instead of going silent.
            appliedDelayMs = DelayMs;
            if (playing && writtenFrames >= target)
            {
                readFrame = writtenFrames - target;
                averageBuffered = buffered = target;
            }
        }

        if (playing && buffered < target - resync)
            playing = false;  // the source went quiet: refill with silence

        if (!playing)
        {
            if (buffered < target)
            {
                Array.Clear(output, 0, frames * channels);
                return;
            }
            readFrame = writtenFrames - target;
            averageBuffered = target;
            playing = true;
        }
        else if (buffered > target + resync)
        {
            readFrame = writtenFrames - target;  // fell behind (e.g. the output stalled): jump forward
            averageBuffered = target;
        }

        averageBuffered += (writtenFrames - readFrame - averageBuffered) * 0.02;
        double correction = Math.Clamp(
            (averageBuffered - target) / sourceRate * CorrectionGain, -MaxRateCorrection, MaxRateCorrection);
        double step = (double)sourceRate / outputRate * (1 + correction);

        for (int f = 0; f < frames; f++)
        {
            long i0 = (long)readFrame;
            if (i0 + 1 >= writtenFrames)
            {
                Array.Clear(output, f * channels, (frames - f) * channels);
                playing = false;
                return;
            }

            float frac = (float)(readFrame - i0);
            int a = (int)(i0 % ringFrames) * channels;
            int b = (int)((i0 + 1) % ringFrames) * channels;
            for (int c = 0; c < channels; c++)
                output[f * channels + c] = ring[a + c] + (ring[b + c] - ring[a + c]) * frac;

            readFrame += step;
        }
    }

    public void Dispose()
    {
        player.Stop();
        player.Dispose();
    }
}
