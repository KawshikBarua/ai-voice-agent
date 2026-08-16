namespace AiReceptionist.Api.Common;

public static class SqlUtil
{
    /// <summary>Escapes LIKE wildcards in user/caller-supplied text so "%", "_" and "["
    /// match literally instead of matching everything.</summary>
    public static string EscapeLike(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}
