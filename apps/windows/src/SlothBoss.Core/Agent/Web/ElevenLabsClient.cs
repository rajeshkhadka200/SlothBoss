using System.Net;
using System.Net.Http.Headers;

namespace SlothBoss.Core.Agent.Web;

/// <summary>
/// ElevenLabs Speech to Text (https://elevenlabs.io/docs/api-reference/speech-to-text/convert).
/// Used only to turn a spoken request into text; the recording is sent to ElevenLabs when the user
/// presses the microphone button, and nothing else is.
/// </summary>
public sealed class ElevenLabsClient(string apiKey, HttpMessageHandler? handler = null) : IDisposable
{
    public static readonly Uri BaseUri = new("https://api.elevenlabs.io/");
    public const string Model = "scribe_v2";
    private readonly HttpClient _http = Http.Create(handler);

    public async Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Model), "model_id");
        var file = new StreamContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav" : "application/octet-stream");
        form.Add(file, "file", fileName);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "v1/speech-to-text")) { Content = form };
        req.Headers.Add("xi-api-key", apiKey);
        var (status, json, body) = await Http.SendAsync(_http, req, "ElevenLabs", ct).ConfigureAwait(false);
        if (status == HttpStatusCode.OK && json?["text"] is { } text) return text.GetValue<string>().Trim();
        var detail = json?["detail"]?["message"]?.GetValue<string>() ?? json?["detail"]?.ToString() ?? (body.Length < 200 ? body : null);
        throw status switch
        {
            HttpStatusCode.Unauthorized => new WebServiceException("ElevenLabs rejected the API key. Check it in Settings → Voice."),
            HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden => new WebServiceException("Your ElevenLabs plan doesn't allow this request" + (detail is null ? "." : $": {detail}")),
            HttpStatusCode.TooManyRequests => new WebServiceException("ElevenLabs is busy or your quota is used up. Try again later.", transient: true),
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest => new WebServiceException("ElevenLabs couldn't process the recording" + (detail is null ? "." : $": {detail}")),
            _ => new WebServiceException($"ElevenLabs error (HTTP {(int)status})" + (detail is null ? "." : $": {detail}"), transient: (int)status >= 500),
        };
    }

    public void Dispose() => _http.Dispose();
}
