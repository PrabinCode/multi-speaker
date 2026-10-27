using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiSpeaker;

/// <summary>Captures everything Windows plays on the source device and fans it out to the speakers.</summary>
sealed class AudioRouter : IDisposable
{
    readonly WasapiLoopbackCapture capture;
    readonly WaveFormat format;
    readonly List<SpeakerOutput> outputs = [];
    float[] scratch = [];

    public event Action<Exception?>? CaptureStopped;

    public AudioRouter(MMDevice source)
    {
        capture = new WasapiLoopbackCapture(source);
        format = capture.WaveFormat;
        if (format.BitsPerSample is not (32 or 16))
            throw new NotSupportedException($"Unsupported source format: {format}");

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += (_, e) => CaptureStopped?.Invoke(e.Exception);
    }

    /// <summary>Adds a speaker. Must be called before <see cref="Start"/>.</summary>
    public SpeakerOutput AddSpeaker(MMDevice device, string name, int delayMs)
    {
        var output = new SpeakerOutput(device, name, format.SampleRate, delayMs);
        outputs.Add(output);
        return output;
    }

    public void Start()
    {
        foreach (var output in outputs)
            output.Start();
        capture.StartRecording();
    }

    void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int samples = e.BytesRecorded / bytesPerSample;
        if (scratch.Length < samples)
            scratch = new float[samples];

        // The Windows mix format is 32-bit float in practice; 16-bit PCM is handled just in case.
        if (bytesPerSample == 4)
            Buffer.BlockCopy(e.Buffer, 0, scratch, 0, samples * 4);
        else
            for (int i = 0; i < samples; i++)
                scratch[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;

        int frames = samples / format.Channels;
        foreach (var output in outputs)
            output.Write(scratch, frames, format.Channels);
    }

    public void Dispose()
    {
        capture.StopRecording();
        capture.Dispose();
        foreach (var output in outputs)
            output.Dispose();
    }
}
