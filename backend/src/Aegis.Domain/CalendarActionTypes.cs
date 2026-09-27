namespace Aegis.Domain;

public static class CalendarActionTypes
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public static bool IsKnown(string value) => value is Create or Update or Delete;
}
