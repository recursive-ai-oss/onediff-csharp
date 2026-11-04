namespace OneDiff.Tests;

public class InternerTests
{
    [Fact]
    public void IdsAreDenseInFirstSeenOrder()
    {
        var interner = new TokenInterner<string>();
        Assert.Equal([0, 1, 0, 2, 1], interner.Intern(["a", "b", "a", "c", "b"]));
        Assert.Equal(3, interner.Count);
        Assert.Equal("c", interner[2]);
        Assert.Equal(1, interner.Intern("b"));
    }

    [Fact]
    public void ComparerDecidesEquality()
    {
        var interner = new TokenInterner<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal([0, 0, 1], interner.Intern(["Ab", "aB", "c"]));
        Assert.Equal("Ab", interner[0]);
    }

    [Fact]
    public void DestinationLengthMustMatch()
    {
        var interner = new TokenInterner<int>();
        Assert.Throws<ArgumentException>(() => interner.Intern([1, 2], new int[1]));
    }
}
