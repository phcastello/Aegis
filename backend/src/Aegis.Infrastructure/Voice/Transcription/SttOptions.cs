using Aegis.Application.Voice.Transcription;

namespace Aegis.Infrastructure.Voice.Transcription;

public sealed class SttOptions : ITranscriptionSettings
{
    public const string SectionName = "AegisStt";

    public bool Enabled { get; set; } = true;
    public string PrimaryProvider { get; set; } = "elevenlabs";
    public bool FallbackEnabled { get; set; } = true;
    public string FallbackProvider { get; set; } = "openai";
    public string Language { get; set; } = "por";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxAudioBytes { get; set; } = 20 * 1024 * 1024;
    public int MaxRecordingSeconds { get; set; } = 90;
    public int MaxKeyterms { get; set; } = 80;
    public string Keyterms { get; set; } = "Aegis;oito;às oito;Qdrant;TTS;STT;GPU;CPU";
}

public sealed class ElevenLabsSttOptions
{
    public const string DefaultBaseUrl = "https://api.elevenlabs.io";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "scribe_v2";
    public string BaseUrl { get; set; } = DefaultBaseUrl;
}

public sealed class OpenAiSttOptions
{
    public const string DefaultBaseUrl = "https://api.openai.com";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-transcribe";
    public string BaseUrl { get; set; } = DefaultBaseUrl;
}
