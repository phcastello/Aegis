// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Domain;

public static class CalendarActionTypes
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public static bool IsKnown(string value) => value is Create or Update or Delete;
}
