using System.Security.Cryptography;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;

namespace OneDiff.Word.Tests;

public class DigestTests
{
    /// <summary>The comparison JSON of 20 seeded pairs, hashed together, so that two machines can be compared by one line.</summary>
    [Fact]
    public void TwentyPairDigestIsStable()
    {
        var source = new WordDocumentSource();
        using var jsonHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int seed = 5000; seed < 5020; seed++)
        {
            var gen = new Gen(seed);
            DocSpec oldSpec = gen.Document();
            DocSpec newSpec = gen.Edit(oldSpec);
            byte[] oldBytes = DocFactory.Build(oldSpec), newBytes = DocFactory.Build(newSpec);
            fileHash.AppendData(oldBytes);
            fileHash.AppendData(newBytes);
            Comparison c = Comparison.Of(source.Read(DocumentInput.FromBytes(oldBytes, "old.docx")), source.Read(DocumentInput.FromBytes(newBytes, "new.docx")));
            byte[] json = ComparisonJson.Write(c);
            Assert.Equal(json, ComparisonJson.Write(Comparison.Of(source.Read(DocumentInput.FromBytes(oldBytes, "old.docx")), source.Read(DocumentInput.FromBytes(newBytes, "new.docx")))));
            jsonHash.AppendData(json);
        }

        string line = $"word digest: files={Convert.ToHexStringLower(fileHash.GetHashAndReset())} json={Convert.ToHexStringLower(jsonHash.GetHashAndReset())}";
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("ONEDIFF_WORD_DIGEST") is { Length: > 0 } path)
        {
            File.WriteAllText(path, line + "\n");
        }
    }
}
