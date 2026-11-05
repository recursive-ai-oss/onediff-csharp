using System.Text;
using OneDiff.Documents.Html;
using OneDiff.Documents.Model;
using OneDiff.Documents.Semantics;
using OneDiff.Documents.Serialization;
using OneDiff.Documents.Verification;
using static OneDiff.Documents.Tests.Helpers;

namespace OneDiff.Documents.Tests;

/// <summary>A stream that, like an ASP.NET Core request or response body by default, refuses every synchronous call.</summary>
internal sealed class AsyncOnlyStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed.");

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");

    public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous operations are disallowed.");

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");

    public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("Synchronous operations are disallowed.");

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
}

public class TrustAndAsyncTests
{
    private const string Old = "The supplier shall deliver within thirty days of the order.\n\nPayment is due in sixty days.";
    private const string New = "The supplier shall deliver within forty days of the order.\n\nPayment is due in sixty days.";

    [Fact]
    public async Task StreamsThatRefuseSynchronousIoWorkThroughTheAsyncSiblings()
    {
        await using var oldStream = new AsyncOnlyStream(new MemoryStream(Encoding.UTF8.GetBytes(Old)));
        await using var newStream = new AsyncOnlyStream(new MemoryStream(Encoding.UTF8.GetBytes(New)));
        var sink = new MemoryStream();
        await using var output = new AsyncOnlyStream(sink);

        await Redline.CreateAsync(oldStream, newStream, output, RedlineForm.Html);

        string html = Encoding.UTF8.GetString(sink.ToArray());
        Assert.Contains("forty", html, StringComparison.Ordinal);

        // The same bytes as the synchronous route.
        Assert.Equal(Redline.Create(Encoding.UTF8.GetBytes(Old), Encoding.UTF8.GetBytes(New), RedlineForm.Html), sink.ToArray());
    }

    [Fact]
    public async Task JsonReadAndWriteAsyncRoundTrip()
    {
        Comparison c = CompareText(Old, New);
        var sink = new MemoryStream();
        await ComparisonJson.WriteAsync(c, new AsyncOnlyStream(sink));
        Assert.Equal(ComparisonJson.Write(c), sink.ToArray());

        Comparison back = await ComparisonJson.ReadAsync(new AsyncOnlyStream(new MemoryStream(sink.ToArray())));
        Assert.True(back.Verification.Passed);
        Assert.Equal(sink.ToArray(), ComparisonJson.Write(back));

        await Assert.ThrowsAsync<ComparisonFormatException>(() => ComparisonJson.ReadAsync(new MemoryStream(sink.ToArray()), new ComparisonJsonLimits(MaxInputBytes: 100)));
    }

    [Fact]
    public async Task FilesAsyncAndCancellation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onediff-async-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.txt"), b = Path.Combine(dir, "b.txt"), o = Path.Combine(dir, "o.html");
            await File.WriteAllTextAsync(a, Old);
            await File.WriteAllTextAsync(b, New);
            await Redline.CreateAsync(a, b, o);
            Comparison viaAsync = await Comparison.OfAsync(a, b);
            await viaAsync.SaveAsync(Path.Combine(dir, "o2.html"));
            Assert.Equal(File.ReadAllBytes(o), File.ReadAllBytes(Path.Combine(dir, "o2.html")));
            Assert.Equal(Comparison.Of(a, b).Changes.Length, viaAsync.Changes.Length);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Redline.CreateAsync(a, b, o, RedlineForm.Json, null, cts.Token));
            Assert.Throws<OperationCanceledException>(() => viaAsync.SaveAs(Path.Combine(dir, "o3.html"), cts.Token));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AComparisonReadFromJsonIsVerifiedHereOrSaysItIsNot()
    {
        Comparison c = CompareText(Old, New);
        byte[] json = ComparisonJson.Write(c);

        Comparison back = ComparisonJson.Read(json);
        Assert.True(back.Verification.VerifiedHere);
        Assert.True(back.Verification.Passed);

        // The stored claim is not trusted: tamper with a change's text and the reader says so.
        string text = Encoding.UTF8.GetString(json);
        string tampered = text.Replace("\"forty\"", "\"ninety\"", StringComparison.Ordinal);
        Assert.NotEqual(text, tampered);
        Comparison bad = ComparisonJson.Read(Encoding.UTF8.GetBytes(tampered));
        Assert.NotEmpty(bad.Check());
        Assert.False(bad.Verification.Passed);
        Assert.True(bad.Verification.VerifiedHere);

        // Without verification the value never claims a pass, and Check() reports failures without changing it.
        Comparison unverified = ComparisonJson.Read(json, verify: false);
        Assert.False(unverified.Verification.VerifiedHere);
        Assert.False(unverified.Verification.Passed);
        Assert.Empty(unverified.Check());
        Assert.False(unverified.Verification.Passed);
        Assert.True(ComparisonJson.Read(json).Verification.Passed);

        // A comparison the stages made was verified here.
        Assert.True(c.Verification.VerifiedHere);
    }

    [Fact]
    public void ErrorFactsAreOnTheJsonException()
    {
        ComparisonFormatException e = Assert.Throws<ComparisonFormatException>(() => ComparisonJson.Read("{\"schema\": nope}"u8));
        Assert.NotNull(e.LineNumber);
        Assert.NotNull(e.BytePositionInLine);
    }

    private sealed class WarningSource(string name, int tag) : IDocumentSource
    {
        public string Name => name;

        public bool CanRead(DocumentInput input) => input.Content.Span.StartsWith("%WARN"u8);

        public CompareDocument Read(DocumentInput input, CancellationToken cancellationToken = default)
        {
            var b = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(input.Name, input.Sha256Hex(), "text/x-warn");
            b.Add(NodeKind.Paragraph, new NodeContent { Text = "tag " + tag });
            b.AddWarning(DiagnosticSeverity.Warning, "x.damaged", "part of the file was unreadable");
            return b.Build();
        }
    }

    [Fact]
    public void ASourceWarningBecomesADiagnosticAndStrictThrows()
    {
        var source = new WarningSource("warn-test", 1);
        CompareDocument damaged = CompareDocument.From(DocumentInput.FromString("%WARN", "bad.warn"), source);
        CompareDocument fine = CompareDocument.FromText("tag 1");

        Comparison c = Comparison.Of(damaged, fine);
        Diagnostic d = Assert.Single(c.Diagnostics, static d => d.Code == "source.old.x.damaged");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("bad.warn", d.Message, StringComparison.Ordinal);

        // The warning is in the JSON too, so it survives a round trip.
        Assert.Contains(ComparisonJson.Read(ComparisonJson.Write(c)).Diagnostics, static d => d.Code == "source.old.x.damaged");

        ComparisonVerificationException e = Assert.Throws<ComparisonVerificationException>(() => Comparison.Of(damaged, fine, CompareSettings.Strict));
        Assert.Contains("source.old.x.damaged", e.Message, StringComparison.Ordinal);
    }

    private sealed class NamedSource(string name) : IDocumentSource
    {
        public string Name => name;

        public bool CanRead(DocumentInput input) => input.Content.Span.StartsWith("%PRIO"u8);

        public CompareDocument Read(DocumentInput input, CancellationToken cancellationToken = default)
        {
            var b = new CompareDocumentBuilder().WithMetadata(name);
            b.Add(NodeKind.Paragraph, new NodeContent { Text = name });
            return b.Build();
        }
    }

    [Fact]
    public void SourcePriorityDoesNotDependOnRegistrationOrder()
    {
        var generalist = new NamedSource("prio-general");
        var specialist = new NamedSource("prio-special");
        var input = DocumentInput.FromString("%PRIO");

        // Specialist first, then the generalist registered later at the default priority: the specialist still wins.
        DocumentSources.Register(specialist, 50);
        DocumentSources.Register(generalist);
        Assert.Same(specialist, DocumentSources.Sniff(input));

        // And the other order, and a repeat registration.
        DocumentSources.Register(generalist);
        DocumentSources.Register(specialist, 50);
        Assert.Same(specialist, DocumentSources.Sniff(input));
        Assert.Single(DocumentSources.Registered, static s => s.Name == "prio-special");
    }

    [Fact]
    public void AnUnrecognizedInputNamesTheSourcesThatWereTried()
    {
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => CompareDocument.From(DocumentInput.FromBytes(new byte[] { 0, 1, 2, 3 }, "blob.bin")));
        Assert.Contains("tried:", e.Message, StringComparison.Ordinal);
        Assert.Contains("plain-text", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WholeWordMarksWidenAnInnerEditAndLeaveTheChangesAlone()
    {
        Comparison c = CompareText("The term is 30 days from signing.", "The term is 60 days from signing.");

        string exact = HtmlRedline.Inline(c, RedlineStyle.Blackline);
        Assert.Contains("3<span class=\"sr\"> end deleted", exact, StringComparison.Ordinal);

        string whole = HtmlRedline.Inline(c, RedlineStyle.Blackline with { MarkWholeWords = true });
        Assert.Contains("30<span class=\"sr\"> end deleted", whole, StringComparison.Ordinal);
        Assert.Contains("60<span class=\"sr\"> end inserted", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("3<span class=\"sr\"> end deleted", whole, StringComparison.Ordinal);

        // A whole inserted word is not widened into its neighbours.
        Comparison ins = CompareText("Notices must be written.", "Notices must be clearly written.");
        string insHtml = HtmlRedline.Inline(ins, RedlineStyle.Blackline with { MarkWholeWords = true });
        Assert.Contains("clearly", insHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("written<span class=\"sr\"> end deleted", insHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextClauseNumbersAreListLabelsAndRenumberingIsANonSubstantiveCascade()
    {
        string clauses(params string[] items) => string.Join("\n\n", items);
        CompareDocument oldDoc = CompareDocument.FromText(clauses(
            "1. The supplier shall deliver the goods.",
            "2. Payment is due within thirty days.",
            "3. The buyer shall inspect the goods.",
            "4. This agreement is governed by Delaware law."));
        CompareDocument newDoc = CompareDocument.FromText(clauses(
            "1. The supplier shall deliver the goods.",
            "2. A new clause on warranties is added here.",
            "3. Payment is due within thirty days.",
            "4. The buyer shall inspect the goods.",
            "5. This agreement is governed by Delaware law."));

        Node item = oldDoc.Nodes.First(static n => n.Kind == NodeKind.ListItem);
        Assert.Equal("1.", item.Attributes?.Get(FormatKeys.ListLabel));
        Assert.StartsWith("The supplier", item.Text, StringComparison.Ordinal);

        Comparison c = Compare(oldDoc, newDoc);
        Assert.DoesNotContain(c.Changes, static ch => ch.Type == ChangeType.Number && ch.IsSubstantive);
        Assert.Contains(c.Groups, static g => g.Kind == GroupKind.Cascade);
        Assert.Single(c.Changes, static ch => ch.IsSubstantive);
    }
}
