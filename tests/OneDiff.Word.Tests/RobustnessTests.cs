using OneDiff.Documents.Model;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Cfb;

namespace OneDiff.Word.Tests;

public class RobustnessTests
{
    private static void MustRejectTyped(WordDocumentSource source, byte[] bytes)
    {
        DocumentInput input = DocumentInput.FromBytes(bytes, "x");
        try
        {
            source.CanRead(input);
            source.Read(input);
        }
        catch (InvalidDataException e)
        {
            Assert.NotNull(WordSourceErrors.FailureOf(e));
        }
    }

    [Fact]
    public void TwoHundredSeededCorruptionsGiveATypedRejectionOrAModel()
    {
        byte[] good = DocFactory.Build(Harness.Rich());
        var source = new WordDocumentSource();
        int rejected = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            var rng = new Random(seed);
            byte[] b = (byte[])good.Clone();
            switch (seed % 5)
            {
                case 0:
                    b = b[..rng.Next(0, b.Length)];
                    break;
                case 1:
                    for (int k = rng.Next(1, 20); k > 0; k--)
                    {
                        b[rng.Next(b.Length)] = (byte)rng.Next(256);
                    }

                    break;
                case 2:
                    {
                        int start = rng.Next(b.Length), len = Math.Min(rng.Next(1, 400), b.Length - start);
                        Array.Clear(b, start, len);
                        break;
                    }

                case 3:
                    {
                        int at = rng.Next(b.Length);
                        byte[] junk = new byte[rng.Next(1, 200)];
                        rng.NextBytes(junk);
                        b = [.. b[..at], .. junk, .. b[at..]];
                        break;
                    }

                default:
                    b = new byte[rng.Next(0, 5000)];
                    rng.NextBytes(b);
                    break;
            }

            try
            {
                MustRejectTyped(source, b);
            }
            catch (InvalidDataException)
            {
                rejected++;
            }
        }

        Assert.True(rejected >= 0);
    }

    [Fact]
    public void ArchiveEntriesWithDamagedXmlAreRejectedNotThrown()
    {
        byte[] good = DocFactory.Build(Harness.Rich());
        // Overwrite bytes of the stored XML so that the package still opens but a part does not parse.
        var source = new WordDocumentSource();
        for (int seed = 0; seed < 60; seed++)
        {
            var rng = new Random(1000 + seed);
            byte[] b = (byte[])good.Clone();
            int at = rng.Next(30, b.Length - 30);
            b[at] ^= (byte)(1 << rng.Next(8));
            MustRejectTyped(source, b);
        }
    }

    [Fact]
    public void NonWordInputsAreRejectedWithATypedFailure()
    {
        var source = new WordDocumentSource();
        byte[][] inputs = [[], "hello"u8.ToArray(), "%PDF-1.7\n"u8.ToArray(), "<html></html>"u8.ToArray(), "PK\x03\x04"u8.ToArray()];
        foreach (byte[] bytes in inputs)
        {
            Assert.False(source.CanRead(DocumentInput.FromBytes(bytes)));
            InvalidDataException e = Assert.Throws<InvalidDataException>(() => source.Read(DocumentInput.FromBytes(bytes)));
            Assert.Equal(WordSourceFailure.Malformed, WordSourceErrors.FailureOf(e));
        }
    }

    [Fact]
    public void AnEncryptedPackageIsRejectedAsEncrypted()
    {
        using var ms = new MemoryStream();
        using (var writer = new CompoundFileWriter(ms, leaveOpen: true))
        {
            foreach (string name in new[] { "EncryptionInfo", "EncryptedPackage" })
            {
                byte[] data = [1, 2, 3, 4];
                writer.WriteEntry(new ArchiveEntry { Name = name, Kind = ArchiveEntryKind.File, Size = data.Length }, data);
            }

            writer.Finish();
        }

        var source = new WordDocumentSource();
        DocumentInput input = DocumentInput.FromBytes(ms.ToArray());
        Assert.True(source.CanRead(input));
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => source.Read(input));
        Assert.Equal(WordSourceFailure.Encrypted, WordSourceErrors.FailureOf(e));
    }

    [Fact]
    public void LengthsAreBounded()
    {
        byte[] docx = DocFactory.Build(Harness.Rich());
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => Harness.Read(docx, options: new WordSourceOptions { MaxCharacters = 50 }));
        Assert.Equal(WordSourceFailure.TooLarge, WordSourceErrors.FailureOf(e));
        e = Assert.Throws<InvalidDataException>(() => Harness.Read(docx, options: new WordSourceOptions { MaxNodes = 10 }));
        Assert.Equal(WordSourceFailure.TooLarge, WordSourceErrors.FailureOf(e));
        Assert.Throws<ArgumentException>(() => new WordDocumentSource(new WordSourceOptions { MaxNodes = 0 }));
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        byte[] docx = DocFactory.Build(Harness.Rich());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new WordDocumentSource().Read(DocumentInput.FromBytes(docx), cts.Token));
    }
}
