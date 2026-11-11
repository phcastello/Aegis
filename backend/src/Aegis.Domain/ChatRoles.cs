// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
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
