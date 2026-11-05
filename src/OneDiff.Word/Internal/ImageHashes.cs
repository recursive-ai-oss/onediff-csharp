namespace OneDiff.Word.Internal;

/// <summary>Content-derived image fingerprints.</summary>
internal static class ImageHashes
{
    /// <summary>A perceptual hash of the image, or null when the format is not one this source decodes.</summary>
    public static string? Perceptual(ReadOnlySpan<byte> image) => null;
}
