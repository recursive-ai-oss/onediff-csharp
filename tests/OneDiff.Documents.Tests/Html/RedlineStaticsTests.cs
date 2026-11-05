using System.Text;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Tests.Html;

/// <summary>The happy path: statics, SaveAs, and the registration seam an adapter package uses.</summary>
public class RedlineStaticsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "onediff-redline-" + Guid.NewGuid().ToString("N"));

    public RedlineStaticsTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        Directory.Delete(dir, true);
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string text)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void CreateChoosesTheFormFromTheExtension()
    {
        string a = Write("v1.txt", "The supplier shall deliver within thirty days.\n\nPayment is due in sixty days.");
        string b = Write("v2.txt", "The supplier shall deliver within forty days.\n\nPayment is due in sixty days.");
        string html = Path.Combine(dir, "redline.html"), json = Path.Combine(dir, "redline.json");
        Redline.Create(a, b, html);
        Redline.Create(a, b, json);
        string page = File.ReadAllText(html);
        Assert.StartsWith("<!DOCTYPE html>", page, StringComparison.Ordinal);
        Assert.Contains("Comparison of v1.txt and v2.txt", page, StringComparison.Ordinal);
        Assert.DoesNotContain(dir, page, StringComparison.Ordinal);
        Comparison back = Serialization.ComparisonJson.Read(File.ReadAllBytes(json));
        Assert.Empty(back.Check());
        Assert.NotEmpty(back.Changes);

        // Explicit form ignores the extension; the side-by-side form defaults to its own look.
        string sbs = Path.Combine(dir, "x.dat");
        Redline.Create(a, b, sbs, RedlineForm.HtmlSideBySide);
        Assert.Contains("class=\"sbs\"", File.ReadAllText(sbs), StringComparison.Ordinal);
        Assert.Contains("#0072b2", File.ReadAllText(sbs), StringComparison.Ordinal);
    }

    [Fact]
    public void SaveAsAndStreamsAndBytes()
    {
        string a = Write("a.txt", "One two three.");
        string b = Write("b.txt", "One two four.");
        Comparison c = Comparison.Of(a, b);
        string html = Path.Combine(dir, "o.html");
        c.SaveAs(html);
        c.SaveAs(Path.Combine(dir, "o2.html"), RedlineStyle.Developer);
        Assert.Contains("#22863a", File.ReadAllText(Path.Combine(dir, "o2.html")), StringComparison.Ordinal);
        Assert.Equal(File.ReadAllBytes(html), c.ToBytes(RedlineForm.Html));

        byte[] viaBytes = Redline.Create(File.ReadAllBytes(a), File.ReadAllBytes(b), RedlineForm.Json);
        Assert.NotEmpty(Serialization.ComparisonJson.Read(viaBytes).Changes);

        using var oldS = File.OpenRead(a);
        using var newS = File.OpenRead(b);
        using var outS = new MemoryStream();
        Redline.Create(oldS, newS, outS, RedlineForm.Html);
        Assert.True(outS.Length > 1000);
    }

    [Fact]
    public void SettingsStyleIsUsedUnlessTheFormPrefersAnother()
    {
        Comparison c = Comparison.Of(DocumentInput.FromString("One two three."), DocumentInput.FromString("One two four."), CompareSettings.Legal with { Style = RedlineStyle.Word });
        string inline = Encoding.UTF8.GetString(c.ToBytes(RedlineForm.Html));
        Assert.Contains("#c00000", inline, StringComparison.Ordinal);
        string sbs = Encoding.UTF8.GetString(c.ToBytes(RedlineForm.HtmlSideBySide));
        Assert.Contains("#c00000", sbs, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedOutputsAndInputsSayWhichPackageToAdd()
    {
        string a = Write("a.txt", "x");
        var ex = Assert.Throws<NotSupportedException>(() => Redline.Create(a, a, Path.Combine(dir, "out.docx")));
        Assert.Contains("coming in a 1.x release", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RecursiveAI.OneDiff.Pdf", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OneDiff.Word", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<NotSupportedException>(() => Redline.FormFor("out.pdf"));
        Assert.Contains("RecursiveAI.OneDiff.Pdf", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<NotSupportedException>(() => Redline.FormFor("out"));
        Assert.Contains("no extension", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<NotSupportedException>(() => Redline.FormFor("out.weird"));
        Assert.Contains("'.weird'", ex.Message, StringComparison.Ordinal);

        string pdf = Path.Combine(dir, "real.pdf");
        File.WriteAllBytes(pdf, "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n"u8.ToArray());
        ex = Assert.Throws<NotSupportedException>(() => Comparison.Of(pdf, pdf));
        Assert.Contains("RecursiveAI.OneDiff.Pdf", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Register", ex.Message, StringComparison.Ordinal);
    }

    // ---- the seam: what an adapter package does ----

    /// <summary>A stand-in for an adapter's source: reads "%XPDF" files as plain text after the header line.</summary>
    private sealed class FakePdfSource : IDocumentSource
    {
        public string Name => "fake-pdf";

        public bool CanRead(DocumentInput input) => input.Content.Span.StartsWith("%XPDF"u8);

        public CompareDocument Read(DocumentInput input, CancellationToken cancellationToken = default)
        {
            string text = Encoding.UTF8.GetString(input.Content.Span);
            return CompareDocument.FromText(text[(text.IndexOf('\n', StringComparison.Ordinal) + 1)..], input.Name);
        }
    }

    /// <summary>A stand-in for an adapter's output form: writes a marker plus the number of changes.</summary>
    private sealed class FakePdfForm() : RedlineForm("fake-pdf", [".xpdf"])
    {
        public override void Write(Comparison comparison, Stream output, RedlineStyle style, CancellationToken cancellationToken = default) =>
            output.Write(Encoding.UTF8.GetBytes($"%XPDF changes={comparison.Changes.Length} style={style.Name}\n"));
    }

    /// <summary>What a package's <c>Register</c> method is: two calls.</summary>
    private static void RegisterFakePdf()
    {
        Redline.Register(new FakePdfSource());
        Redline.Register(new FakePdfForm());
    }

    [Fact]
    public void AdapterPackagesPlugInWithOneRegisterCall()
    {
        string a = Write("a.xpdf", "%XPDF-1\nThe supplier shall deliver within thirty days.");
        string b = Write("b.xpdf", "%XPDF-1\nThe supplier shall deliver within forty days.");
        string outPath = Path.Combine(dir, "out.xpdf");

        RegisterFakePdf();
        RegisterFakePdf(); // idempotent
        Assert.Single(Redline.Forms, f => f.Name == "fake-pdf");
        Assert.Single(DocumentSources.Registered, s => s.Name == "fake-pdf");

        Redline.Create(a, b, outPath);
        Assert.Equal("%XPDF changes=1 style=Blackline\n", File.ReadAllText(outPath));

        // The same adapter's source also feeds the built-in forms, and the form is usable with no registration at all.
        Redline.Create(a, b, Path.Combine(dir, "x.html"));
        Assert.Contains("forty", File.ReadAllText(Path.Combine(dir, "x.html")), StringComparison.Ordinal);
        Comparison c = Comparison.Of(a, b);
        Assert.Equal("%XPDF changes=1 style=SideBySide\n", Encoding.UTF8.GetString(c.ToBytes(new FakePdfForm(), RedlineStyle.SideBySide)));
    }

    [Fact]
    public void ExtensionsAreCaseInsensitiveAndTheLatestRegistrationWins()
    {
        Assert.Same(RedlineForm.Html, Redline.FormFor("OUT.HTML"));
        Assert.Same(RedlineForm.Html, Redline.FormFor("a.htm"));
        Assert.Same(RedlineForm.Json, Redline.FormFor("a.json"));
    }

    [Fact]
    public void FormsRejectBadExtensionLists()
    {
        Assert.Throws<ArgumentException>(() => new BadForm());
    }

    private sealed class BadForm() : RedlineForm("bad", ["pdf"])
    {
        public override void Write(Comparison comparison, Stream output, RedlineStyle style, CancellationToken cancellationToken = default)
        {
        }
    }
}
