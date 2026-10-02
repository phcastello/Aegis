namespace Aegis.Application.Email;

public sealed class EmailProviderException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
