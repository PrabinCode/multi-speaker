using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MultiSpeaker;

/// <summary>
/// Makes the source speaker play only one side by setting its Windows left/right balance.
/// The source plays straight from Windows, so this is the only way to split it. On devices whose
/// volume Windows applies in software, the balance is applied before loopback capture, so the copied
/// sound would lose that side as well; <see cref="Probe"/> detects that before it's used.
/// </summary>
static class SourceBalance
{
    const double ToneHz = 19_500;  // inaudible, and rare in music, so playing audio doesn't disturb the check
    const float ToneGain = 0.01f;  // -40 dB

    /// <summary>
    /// Plays a quiet high tone, mutes the right channel, and checks whether loopback capture still
    /// has the tone on the right. True: safe to use. Null: inconclusive (e.g. the volume is at 0).
    /// </summary>
    public static bool? Probe(MMDevice device)
    {
        var volume = device.AudioEndpointVolume;
        if (volume.Channels.Count < 2)
            return false;
        var mix = device.AudioClient.MixFormat;
        if (mix.SampleRate < 2.2 * ToneHz)
            return false;  // e.g. a headset in call mode; no room for an inaudible tone

        var tone = new SignalGenerator(mix.SampleRate, mix.Channels) { Frequency = ToneHz, Gain = ToneGain, Type = SignalGeneratorType.Sin };
        float rightBefore = volume.Channels[1].VolumeLevelScalar;
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        output.Init(tone);
        output.Play();
        try
        {
            volume.Channels[1].VolumeLevelScalar = 0f;
            Thread.Sleep(150);
            var (left, right) = MeasureTone(device);
            if (left < 1e-6)
                return null;
            return right > 0.5 * left;
        }
        finally
        {
            volume.Channels[1].VolumeLevelScalar = rightBefore;
            output.Stop();
        }
    }

    public static void Apply(MMDevice device, ChannelMode mode)
    {
        var volume = device.AudioEndpointVolume;
        float master = volume.MasterVolumeLevelScalar;
        volume.Channels[0].VolumeLevelScalar = mode == ChannelMode.Right ? 0f : master;
        volume.Channels[1].VolumeLevelScalar = mode == ChannelMode.Left ? 0f : master;
    }

    /// <summary>Back to centered: both sides at the current volume.</summary>
    public static void Restore(MMDevice device)
    {
        var volume = device.AudioEndpointVolume;
        float level = Enumerable.Range(0, volume.Channels.Count).Max(i => volume.Channels[i].VolumeLevelScalar);
        for (int i = 0; i < volume.Channels.Count; i++)
            volume.Channels[i].VolumeLevelScalar = level;
    }

    /// <summary>Amplitude of the probe tone in the first two loopback channels.</summary>
    static (double Left, double Right) MeasureTone(MMDevice device)
    {
        var left = new List<float>();
        var right = new List<float>();
        using var capture = new WasapiLoopbackCapture(device);
        var format = capture.WaveFormat;
        int bytes = format.BitsPerSample / 8;
        capture.DataAvailable += (_, e) =>
        {
            lock (left)
            {
                for (int at = 0; at + format.BlockAlign <= e.BytesRecorded; at += format.BlockAlign)
                {
                    left.Add(Sample(e.Buffer, at, bytes));
                    right.Add(Sample(e.Buffer, at + bytes, bytes));
                }
            }
        };
        capture.StartRecording();
        Thread.Sleep(500);
        capture.StopRecording();
        Thread.Sleep(50);
        lock (left)
            return (Goertzel(left, format.SampleRate), Goertzel(right, format.SampleRate));
    }

    static float Sample(byte[] buffer, int at, int bytes) =>
        bytes == 4 ? BitConverter.ToSingle(buffer, at) : BitConverter.ToInt16(buffer, at) / 32768f;

    /// <summary>Amplitude of a single frequency in the signal.</summary>
    static double Goertzel(List<float> samples, int sampleRate)
    {
        if (samples.Count == 0)
            return 0;
        double coeff = 2 * Math.Cos(2 * Math.PI * ToneHz / sampleRate), s1 = 0, s2 = 0;
        foreach (float x in samples)
        {
            double s = x + coeff * s1 - s2;
            s2 = s1;
            s1 = s;
        }
        double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(power, 0)) / samples.Count;
    }
}
