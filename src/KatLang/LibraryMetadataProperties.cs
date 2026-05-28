namespace KatLang;

internal static class LibraryMetadataProperties
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "Author",
        "Version",
        "Description",
    };

    public static bool IsReservedName(string name)
        => Names.Contains(name);
}
