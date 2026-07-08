namespace Aegis.Application.Email;

public sealed class EmailNotConnectedException : InvalidOperationException
{
    public EmailNotConnectedException()
        : base("Gmail is not connected.")
    {
    }
}
