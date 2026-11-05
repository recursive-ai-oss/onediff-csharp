using OneDiff.Documents;

namespace OneDiff.Nlp;

/// <summary>
/// Plugs OneNLP's recognizers into comparisons: per comparison through <see cref="UseOneNlp"/>, or for the whole process with
/// <see cref="Register"/>.
/// </summary>
public static class OneNlpRecognizers
{
    /// <summary>
    /// Makes <see cref="NlpValueRecognizer.Instance"/> the default value recognizer of every comparison whose settings name none,
    /// process-wide (<see cref="ValueRecognizers.Register"/>). Call it once at startup; calling it again is harmless.
    /// </summary>
    public static void Register() => ValueRecognizers.Register(NlpValueRecognizer.Instance);

    /// <summary>Returns these settings with <see cref="NlpValueRecognizer.Instance"/> as the value recognizer.</summary>
    /// <param name="settings">The settings to derive from, for example <see cref="CompareSettings.Legal"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public static CompareSettings UseOneNlp(this CompareSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { ValueRecognizer = NlpValueRecognizer.Instance };
    }
}
