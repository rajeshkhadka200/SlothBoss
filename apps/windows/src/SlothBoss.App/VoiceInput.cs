using SlothBoss.Core.Agent.Web;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace SlothBoss.App;

/// <summary>
/// Push-to-talk: records the microphone to a temporary WAV file, sends it to ElevenLabs Speech to Text,
/// and deletes the recording. Only active when the user has added an ElevenLabs key.
/// </summary>
public sealed class VoiceInput : IAsyncDisposable
{
    public const string KeyName = "ELEVENLABS_API_KEY";
    private MediaCapture? _capture;
    private StorageFile? _file;

    public static bool Available => SecretStore.Get(KeyName) is not null;
    public bool Recording => _capture is not null;

    public async Task StartAsync()
    {
        if (Recording) return;
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio });
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            throw new InvalidOperationException("Windows blocked the microphone. Allow it in Settings → Privacy & security → Microphone (\"Let desktop apps access your microphone\").");
        }
        catch (Exception e) when (e.HResult == unchecked((int)0xC00D36D5) || e is System.Runtime.InteropServices.COMException)
        {
            capture.Dispose();
            throw new InvalidOperationException("No microphone was found.");
        }
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
        _file = await folder.CreateFileAsync($"slothboss-voice-{Guid.NewGuid():N}.wav", CreationCollisionOption.GenerateUniqueName);
        await capture.StartRecordToStorageFileAsync(MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low), _file);
        _capture = capture;
    }

    /// <summary>Stops recording and returns the transcript.</summary>
    public async Task<string> StopAndTranscribeAsync(CancellationToken ct = default)
    {
        if (_capture is null || _file is null) throw new InvalidOperationException("Not recording.");
        try
        {
            await _capture.StopRecordAsync();
            _capture.Dispose();
            _capture = null;
            var key = SecretStore.Get(KeyName) ?? throw new WebServiceException("Add your ElevenLabs key in Settings → Voice.");
            await using var audio = File.OpenRead(_file.Path);
            if (audio.Length < 8000) throw new WebServiceException("The recording was too short. Hold the mic button a little longer.");
            using var client = new ElevenLabsClient(key);
            return await client.TranscribeAsync(audio, "voice.wav", ct);
        }
        finally
        {
            try { File.Delete(_file.Path); } catch (IOException) { }
            _file = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_capture is not null)
        {
            try { await _capture.StopRecordAsync(); } catch (Exception) { }
            _capture.Dispose();
            _capture = null;
        }
        if (_file is not null) { try { File.Delete(_file.Path); } catch (IOException) { } }
    }
}
