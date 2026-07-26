namespace Aegis.Infrastructure.Voice;

public sealed class AegisTtsOptions
{
    public const string SectionName = "AegisTts";
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "http://10.1.1.47:8001";
    public string Profile { get; set; } = "AegisVoicev1.0";
    public int DefaultPriority { get; set; } = 50;
    public int ConnectTimeoutSeconds { get; set; } = 5;
    public int FirstAudioTimeoutSeconds { get; set; } = 90;
    public int IdleStreamTimeoutSeconds { get; set; } = 30;
    public string? ApiToken { get; set; }
}
