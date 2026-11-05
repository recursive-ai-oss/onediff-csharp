namespace OneDiff.Documents;

/// <summary>
/// The process-wide default value recognizer, for packages that bring a better one (<c>RecursiveAI.OneDiff.Nlp</c>). A recognizer
/// chosen in <see cref="CompareSettings.ValueRecognizer"/> wins over it; with neither, <see cref="BuiltInValueRecognizer"/> is used.
/// </summary>
public static class ValueRecognizers
{
    private static IValueRecognizer? registered;

    /// <summary>The recognizer registered with <see cref="Register"/>, or null.</summary>
    public static IValueRecognizer? Registered => Volatile.Read(ref registered);

    /// <summary>
    /// Makes <paramref name="recognizer"/> the default for every comparison whose settings name none, process-wide; the latest
    /// registration wins. Call it once at startup. Pass null to go back to the built-in recognizer.
    /// </summary>
    public static void Register(IValueRecognizer? recognizer) => Volatile.Write(ref registered, recognizer);

    /// <summary>The recognizer a comparison with these settings uses.</summary>
    /// <param name="settings">The comparison's settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public static IValueRecognizer For(CompareSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ValueRecognizer ?? Registered ?? BuiltInValueRecognizer.Instance;
    }
}
