using System.Buffers;

namespace OneDiff.Documents.Serialization;

public static partial class ComparisonJson
{
    /// <summary>
    /// Reads a comparison from a stream, to its end. The stream is read asynchronously and the JSON is then parsed in memory, so
    /// this works on streams that refuse synchronous reads, such as an ASP.NET Core request body under its default settings.
    /// Everything else is as <see cref="Read(ReadOnlySpan{byte}, ComparisonJsonLimits?, bool)"/>.
    /// </summary>
    /// <param name="utf8">The JSON text, UTF-8, read from its current position; not closed.</param>
    /// <param name="limits">Bounds on size, array length and depth; <see cref="ComparisonJsonLimits.Default"/> when null. The size bound is enforced while reading.</param>
    /// <param name="verify">Whether to re-run the completeness checks on what was read. Default true.</param>
    /// <param name="cancellationToken">Observed while reading the stream.</param>
    /// <exception cref="ComparisonFormatException">The input is not a readable comparison or exceeds a limit.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<Comparison> ReadAsync(Stream utf8, ComparisonJsonLimits? limits = null, bool verify = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        limits ??= ComparisonJsonLimits.Default;
        if (limits.MaxInputBytes <= 0)
        {
            throw new ArgumentException("limits must be positive", nameof(limits));
        }

        var buffer = new ArrayBufferWriter<byte>(16384);
        while (true)
        {
            Memory<byte> room = buffer.GetMemory(16384);
            int n = await utf8.ReadAsync(room, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            buffer.Advance(n);
            if (buffer.WrittenCount > limits.MaxInputBytes)
            {
                throw new ComparisonFormatException($"The input is over the limit of {limits.MaxInputBytes} bytes.");
            }
        }

        return Read(buffer.WrittenSpan, limits, verify);
    }

    /// <summary>Serializes a comparison to a stream asynchronously (the JSON is built in memory, then written), without closing it.</summary>
    /// <param name="comparison">What to write.</param>
    /// <param name="output">The destination.</param>
    /// <param name="cancellationToken">Observed while writing to the stream.</param>
    public static async Task WriteAsync(Comparison comparison, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteAsync(Write(comparison), cancellationToken).ConfigureAwait(false);
    }
}
