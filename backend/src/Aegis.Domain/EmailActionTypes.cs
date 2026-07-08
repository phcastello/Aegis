namespace Aegis.Domain;

public static class EmailActionTypes
{
    public const string MarkRead = "mark_read";
    public const string MarkUnread = "mark_unread";
    public const string Star = "star";
    public const string Unstar = "unstar";
    public const string MarkImportant = "mark_important";
    public const string UnmarkImportant = "unmark_important";

    public static bool IsKnown(string actionType)
    {
        return actionType is MarkRead
            or MarkUnread
            or Star
            or Unstar
            or MarkImportant
            or UnmarkImportant;
    }
}
