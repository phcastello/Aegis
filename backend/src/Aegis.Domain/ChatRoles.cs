namespace Aegis.Domain;

public static class ChatRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";

    public static bool IsKnown(string role)
    {
        return role is User or Assistant or System;
    }
}
