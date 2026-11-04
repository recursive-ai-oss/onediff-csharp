namespace OneDiff.Documents.Model;

/// <summary>Checks that strings stored in the model can be written as JSON and re-read: no unpaired UTF-16 surrogates.</summary>
internal static class WellFormed
{
    public static bool IsValid(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    public static string Check(string s, string what)
    {
        if (!IsValid(s))
        {
            throw new ArgumentException($"{what} contains an unpaired surrogate; replace it with U+FFFD");
        }

        return s;
    }
}
