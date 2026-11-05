using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Ocr.Internal;

namespace OneDiff.Ocr.Tests;

/// <summary>Running page furniture reads alike on every page of a scan, whatever the recognizer made of the page number.</summary>
public class FurnitureTests
{
    [Theory]
    [InlineData("Page 2 / 3", "Page 3 / 3")]
    [InlineData("Page 2/.3", "Page 1/ 3")]
    [InlineData("Seite 2 von 12", "Seite 10 von 12")]
    public void PageNumbersFoldToOneText(string a, string b)
    {
        ITextNormalizer[] n = [OcrPageNumberNormalizer.Instance];
        Assert.Equal(TextNormalization.Normalize(a, n).Text, TextNormalization.Normalize(b, n).Text);
    }

    [Fact]
    public void HeaderWithRecognitionErrorIsAlike()
    {
        Assert.True(PageFurniture.Alike(PageFurniture.LettersOf("Dienstleistungsvertrag"), PageFurniture.LettersOf("Dienstleistüngsvertrag")));
        Assert.False(PageFurniture.Alike(PageFurniture.LettersOf("Dienstleistungsvertrag"), PageFurniture.LettersOf("Haftung und Gewährleistung")));
    }
}
