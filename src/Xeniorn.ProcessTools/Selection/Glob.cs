namespace Xeniorn.ProcessTools.Selection;

/// <summary>Minimal case-insensitive wildcard matching: <c>*</c> matches any run of characters, <c>?</c> exactly one.</summary>
internal static class Glob
{
    public static bool IsMatch(string pattern, string text)
    {
        // iterative two-pointer match with backtracking to the last '*': no regex allocation per sample
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || SameChar(pattern[p], text[t])))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    private static bool SameChar(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
