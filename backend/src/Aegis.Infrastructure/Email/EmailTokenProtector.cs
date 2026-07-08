using Microsoft.AspNetCore.DataProtection;

namespace Aegis.Infrastructure.Email;

public sealed class EmailTokenProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector protector =
        dataProtectionProvider.CreateProtector("Aegis.Email.GmailTokens.v1");

    public string Protect(string token)
    {
        return protector.Protect(token);
    }

    public string Unprotect(string protectedToken)
    {
        return protector.Unprotect(protectedToken);
    }
}
