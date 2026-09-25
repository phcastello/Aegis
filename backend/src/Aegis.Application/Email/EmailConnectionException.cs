namespace Aegis.Application.Email;

public sealed class EmailConnectionException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
