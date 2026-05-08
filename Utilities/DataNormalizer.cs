using System.Globalization;

namespace NetBoxSync.Utilities;

public static class DataNormalizer
{
    public static string NormalizeString(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Apply Trim and convert to UpperInvariant for consistency across the project
        return input.Trim().ToUpperInvariant();
    }
}
