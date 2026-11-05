using OneDiff.Documents.Model;
using OneDiff.Word.Internal;

namespace OneDiff.Word.Tests;

public class ImageTests
{
    [Fact]
    public void PerceptualHashIgnoresFilteringAndBrightnessAndSeesStructure()
    {
        byte[] left = DocFactory.PngGray(32, 32, (x, _) => (byte)(x * 7));
        byte[] leftSub = DocFactory.PngGray(32, 32, (x, _) => (byte)(x * 7), filter: 1);
        byte[] leftUp = DocFactory.PngGray(32, 32, (x, _) => (byte)(x * 7), filter: 2);
        byte[] darker = DocFactory.PngGray(32, 32, (x, _) => (byte)((x * 7) / 2));
        byte[] right = DocFactory.PngGray(32, 32, (x, _) => (byte)(220 - (x * 7)));
        byte[] down = DocFactory.PngGray(32, 32, (_, y) => (byte)(y * 7));

        string h = ImageHashes.Perceptual(left)!;
        Assert.Equal(16, h.Length);
        Assert.Equal(h, ImageHashes.Perceptual(leftSub));
        Assert.Equal(h, ImageHashes.Perceptual(leftUp));
        Assert.Equal(h, ImageHashes.Perceptual(darker));
        Assert.NotEqual(h, ImageHashes.Perceptual(right));
        Assert.NotEqual(h, ImageHashes.Perceptual(down));
    }

    [Fact]
    public void ForeignOrDamagedImagesHaveNoPerceptualHash()
    {
        Assert.Null(ImageHashes.Perceptual([]));
        Assert.Null(ImageHashes.Perceptual("GIF89a...."u8));
        byte[] png = DocFactory.PngGray(16, 16, (x, y) => (byte)(x ^ y));
        Assert.Null(ImageHashes.Perceptual(png.AsSpan(0, png.Length - 30)));
        var rng = new Random(3);
        for (int i = 0; i < 100; i++)
        {
            byte[] b = (byte[])png.Clone();
            b[rng.Next(b.Length)] ^= (byte)(1 << rng.Next(8));
            _ = ImageHashes.Perceptual(b);
        }
    }

    [Fact]
    public void ImagesInDocumentsCarryBothHashes()
    {
        byte[] docx = DocFactory.Build(new DocSpec(new Para(new Img(DocFactory.PngGray(16, 16, (x, _) => (byte)(x * 15)), "ramp"))));
        Node image = Harness.Read(docx).Nodes.Single(n => n.Kind == NodeKind.Image);
        Assert.Equal(64, image.Attributes.Get("hash.content")!.Length);
        Assert.Equal(16, image.Attributes.Get("hash.perceptual")!.Length);
        Assert.Equal("ramp", image.Attributes.Get("image.alt"));
    }
}
