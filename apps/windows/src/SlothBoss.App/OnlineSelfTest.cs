using System.Text;
using SlothBoss.Core.Agent.Web;

namespace SlothBoss.App;

/// <summary>
/// SlothBoss.exe --selftest-online &lt;report.txt&gt;: one real call to each configured partner service with the
/// keys saved in Settings (or environment variables). Writes PASS/FAIL/SKIP lines; never writes the keys.
/// </summary>
public static class OnlineSelfTest
{
    public static async Task RunAsync(string reportPath)
    {
        var log = new StringBuilder();
        void Line(string status, string name, string detail) => log.AppendLine($"{status} {name} :: {detail}");

        if (SecretStore.Get(SecretStore.SerpApi) is { } serp)
        {
            try
            {
                using var c = new SerpApiClient(serp);
                var account = await c.AccountAsync(default);
                var results = await c.SearchAsync("open source local LLM desktop assistant", 5, default);
                Line(results.Count > 0 ? "PASS" : "FAIL", "SerpApi search", $"{results.Count} results; first: {results.FirstOrDefault()?.Url}; searches left: {account.SearchesLeft}");
            }
            catch (WebServiceException e) { Line("FAIL", "SerpApi search", e.Message); }
        }
        else Line("SKIP", "SerpApi search", "no key");

        if (SecretStore.Get(SecretStore.Browserbase) is { } bb)
        {
            try
            {
                using var c = new BrowserbaseClient(bb);
                var page = await c.FetchMarkdownAsync("https://example.com/", default);
                Line(page.Content.Contains("Example Domain") ? "PASS" : "FAIL", "Browserbase fetch", $"{page.Content.Length} characters of Markdown");
            }
            catch (WebServiceException e) { Line("FAIL", "Browserbase fetch", e.Message); }
        }
        else Line("SKIP", "Browserbase fetch", "no key");

        if (SecretStore.Get(VoiceInput.KeyName) is { } xi)
        {
            try
            {
                using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
                using var speech = await synth.SynthesizeTextToStreamAsync("Organize my downloads folder.");
                await using var audio = speech.AsStreamForRead();
                using var c = new ElevenLabsClient(xi);
                var text = await c.TranscribeAsync(audio, "test.wav", default);
                Line(text.Contains("download", StringComparison.OrdinalIgnoreCase) ? "PASS" : "FAIL", "ElevenLabs speech-to-text", $"heard: “{text}”");
            }
            catch (Exception e) when (e is WebServiceException or System.Runtime.InteropServices.COMException) { Line("FAIL", "ElevenLabs speech-to-text", e.Message); }
        }
        else Line("SKIP", "ElevenLabs speech-to-text", "no key");

        await File.WriteAllTextAsync(reportPath, log.ToString());
    }
}
