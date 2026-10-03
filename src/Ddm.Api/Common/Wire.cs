namespace Ddm.Api.Common;

/// <summary>JSON wire format for enums: lowercase names only, never numbers.</summary>
public static class Wire
{
    public static string Lower(Enum value) => value.ToString().ToLowerInvariant();

    public static bool TryParse<T>(string? raw, out T value) where T : struct, Enum
    {
        value = default;
        var s = raw?.Trim();
        return !string.IsNullOrEmpty(s)
            && s.All(char.IsAsciiLetter)
            && Enum.TryParse(s, ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }
}
