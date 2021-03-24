using System.Collections.Generic;

namespace dotnet_gqlgen
{
    public static class Utilities
    {
        public static string Join(this IEnumerable<string> items, string separator = "")
        {
            return string.Join(separator, items);
        }
    }
}