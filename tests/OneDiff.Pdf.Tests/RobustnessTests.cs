using OneDiff.Documents;
using OneDiff.Documents.Model;
using OnePdf;

namespace OneDiff.Pdf.Tests;

public class RobustnessTests
{
    private static byte[] Sample() => Fixtures.Make(new Fixtures.Spec { Paragraphs = ["The supplier shall deliver the goods.", "Payment is due in sixty days."], PageHeight = 200 });

    private static void MustReadOrRejectCleanly(byte[] bytes)
    {
        try
        {
            CompareDocument doc = new PdfDocumentSource().Read(DocumentInput.FromBytes(bytes, "x.pdf"));
            Assert.True(doc.Count >= 0);
        }
        catch (InvalidDataException e)
        {
            Assert.NotNull(PdfSourceErrors.FailureOf(e));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("%PDF-")]
    [InlineData("%PDF-1.7\n")]
    [InlineData("not a pdf at all")]
    public void GarbageIsRejectedWithATypedError(string text)
    {
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => new PdfDocumentSource().Read(DocumentInput.FromString(text)));
        Assert.Equal(PdfSourceFailure.Malformed, PdfSourceErrors.FailureOf(e));
    }

    [Fact]
    public void TruncationsAtManyPointsNeverCrash()
    {
        byte[] pdf = Sample();
        for (int cut = 0; cut < pdf.Length; cut += Math.Max(1, pdf.Length / 60))
        {
            MustReadOrRejectCleanly(pdf.AsSpan(0, cut).ToArray());
        }
    }

    [Fact]
    public void CorruptedBytesNeverCrash()
    {
        byte[] pdf = Sample();
        var rnd = new Random(7);
        for (int round = 0; round < 80; round++)
        {
            byte[] copy = (byte[])pdf.Clone();
            for (int k = 0; k < 6; k++)
            {
                copy[rnd.Next(copy.Length)] = (byte)rnd.Next(256);
            }

            MustReadOrRejectCleanly(copy);
        }
    }

    [Fact]
    [Trait("Host", "Native")] // the browser runtime has no AES
    public void EncryptedWithoutPasswordIsRejectedAndWithItIsRead()
    {
        byte[] plain = Sample();
        byte[] locked;
        using (PdfDocument doc = PdfDocument.Open(plain, new PdfOpenOptions { AllowEditing = true }))
        using (var ms = new MemoryStream())
        {
            doc.Save(ms, new PdfSaveOptions { Encryption = PdfEncryptionAction.Set, NewEncryption = new PdfEncryptionOptions { UserPassword = "secret" } });
            locked = ms.ToArray();
        }

        InvalidDataException none = Assert.Throws<InvalidDataException>(() => new PdfDocumentSource().Read(DocumentInput.FromBytes(locked)));
        Assert.Equal(PdfSourceFailure.Encrypted, PdfSourceErrors.FailureOf(none));
        InvalidDataException wrong = Assert.Throws<InvalidDataException>(() => new PdfDocumentSource(new PdfSourceOptions { Password = "nope" }).Read(DocumentInput.FromBytes(locked)));
        Assert.Equal(PdfSourceFailure.Encrypted, PdfSourceErrors.FailureOf(wrong));
        CompareDocument ok = new PdfDocumentSource(new PdfSourceOptions { Password = "secret" }).Read(DocumentInput.FromBytes(locked));
        Assert.Contains(ok.Nodes, n => n.Text.Contains("supplier", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Host", "Native")] // the browser runtime has no AES
    public void EncryptedInputIsMarkedWithThePasswordAndStaysEncrypted()
    {
        static byte[] Lock(byte[] plain)
        {
            using PdfDocument doc = PdfDocument.Open(plain, new PdfOpenOptions { AllowEditing = true });
            using var ms = new MemoryStream();
            doc.Save(ms, new PdfSaveOptions { Encryption = PdfEncryptionAction.Set, NewEncryption = new PdfEncryptionOptions { UserPassword = "secret" } });
            return ms.ToArray();
        }

        byte[] a = Lock(Fixtures.Make(new Fixtures.Spec { Paragraphs = ["Alpha beta gamma.", "Dropped."] }));
        byte[] b = Lock(Fixtures.Make(new Fixtures.Spec { Paragraphs = ["Alpha delta gamma."] }));
        var source = new PdfDocumentSource(new PdfSourceOptions { Password = "secret" });
        Comparison c = Comparison.Of(source.Read(DocumentInput.FromBytes(a)), source.Read(DocumentInput.FromBytes(b)));
        using var ms = new MemoryStream();
        PdfMarkupResult r = new PdfRedline { Password = "secret" }.MarkNewDocument(c, b, ms);
        Assert.NotEmpty(r.Marks);
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(ms.ToArray()).Pages[0].Annotations.Count);
        Assert.Equal(r.Marks.Length, Run.ReadAnnotations(ms.ToArray(), "secret").Count);
        Assert.Throws<InvalidDataException>(() => PdfRedline.OnPages.MarkNewDocument(c, b, new MemoryStream()));
    }

    [Fact]
    public void LimitsAreEnforced()
    {
        byte[] pdf = Fixtures.Make(new Fixtures.Spec { PageHeight = 150, Paragraphs = [string.Concat(Enumerable.Repeat("word ", 600))] });
        InvalidDataException pages = Assert.Throws<InvalidDataException>(() => new PdfDocumentSource(new PdfSourceOptions { MaxPages = 1 }).Read(DocumentInput.FromBytes(pdf)));
        Assert.Equal(PdfSourceFailure.TooLarge, PdfSourceErrors.FailureOf(pages));
        InvalidDataException chars = Assert.Throws<InvalidDataException>(() => new PdfDocumentSource(new PdfSourceOptions { MaxCharacters = 100 }).Read(DocumentInput.FromBytes(pdf)));
        Assert.Equal(PdfSourceFailure.TooLarge, PdfSourceErrors.FailureOf(chars));
        Assert.Throws<ArgumentException>(() => new PdfDocumentSource(new PdfSourceOptions { MaxPages = 0 }));
    }

    [Fact]
    public void ADamagedFileThatNeededRepairIsMarkedByRewritingIt()
    {
        byte[] good = Fixtures.Make(new Fixtures.Spec { Paragraphs = ["Alpha beta gamma."] });
        byte[] damaged = System.Text.Encoding.Latin1.GetBytes(System.Text.Encoding.Latin1.GetString(good).Replace("startxref", "startxreX", StringComparison.Ordinal));
        byte[] other = Fixtures.Make(new Fixtures.Spec { Paragraphs = ["Alpha delta gamma."] });
        Comparison c = Comparison.Of(Run.Read(good), Run.Read(damaged));
        Assert.Empty(c.Changes);
        Comparison d = Comparison.Of(Run.Read(good), Run.Read(other));
        Assert.NotEmpty(d.Changes);
        Comparison e = Comparison.Of(Run.Read(damaged), Run.Read(other));
        using var ms = new MemoryStream();
        PdfMarkupResult r = PdfRedline.OnPages.MarkNewDocument(e, other, ms);
        Assert.NotEmpty(r.Marks);
        Comparison f = Comparison.Of(Run.Read(other), Run.Read(damaged));
        using var ms2 = new MemoryStream();
        PdfMarkupResult r2 = PdfRedline.OnPages.MarkNewDocument(f, damaged, ms2);
        Assert.NotEmpty(r2.Marks);
        Assert.False(r2.SavedIncrementally);
        Assert.Equal(r2.Marks.Length, Run.ReadAnnotations(ms2.ToArray()).Count);
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new PdfDocumentSource().Read(DocumentInput.FromBytes(Sample()), cts.Token));
    }

    [Fact]
    public void MarkingAMalformedOrWrongInputFailsCleanly()
    {
        byte[] a = Sample();
        byte[] b = Fixtures.Make(new Fixtures.Spec { Paragraphs = ["Changed text here."] });
        Run r = Run.Of(a, b);
        Assert.Throws<ArgumentException>(() => PdfRedline.OnPages.MarkNewDocument(r.Comparison, a, new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() => PdfRedline.OnPages.MarkNewDocument(null!, b, new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() => PdfRedline.OnPages.MarkNewDocument(r.Comparison, b, null!));
    }
}
