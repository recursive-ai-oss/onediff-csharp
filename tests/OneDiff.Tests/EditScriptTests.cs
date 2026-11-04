namespace OneDiff.Tests;

public class EditScriptTests
{
    [Fact]
    public void BuilderMergesNeighboursAndPutsDeletesFirst()
    {
        var b = new EditScriptBuilder();
        b.Equal(2);
        b.Insert(1);
        b.Delete(2);
        b.Insert(1);
        b.Equal(0);
        b.Equal(1);
        b.Equal(3);
        EditScript s = b.ToEditScript();

        Assert.Equal(
            [Edit.Equal(0, 0, 2), Edit.Delete(2, 2, 2), Edit.Insert(4, 2, 2), Edit.Equal(4, 4, 4)],
            s.Edits.ToArray());
        Assert.Equal(8, s.OldLength);
        Assert.Equal(8, s.NewLength);
        Assert.True(EditScript.TryValidate(s.Edits, s.OldLength, s.NewLength, out _));
    }

    [Fact]
    public void IdenticalAndEmpty()
    {
        Assert.True(EditScript.Identical(0).IsIdentical);
        Assert.Empty(EditScript.Identical(0));
        EditScript s = EditScript.Identical(3);
        Assert.True(s.IsIdentical);
        Assert.Equal(0, s.EditDistance);
        Assert.Throws<ArgumentOutOfRangeException>(() => EditScript.Identical(-1));
    }

    public static TheoryData<Edit[], int, int> Invalid => new()
    {
        { [Edit.Equal(1, 1, 2)], 3, 3 },                                  // does not start at 0
        { [Edit.Equal(0, 0, 2)], 3, 3 },                                  // does not cover
        { [new Edit(EditKind.Equal, 0, 2, 0, 1)], 2, 1 },                 // unequal lengths
        { [Edit.Delete(0, 0, 0)], 0, 0 },                                 // empty run
        { [Edit.Delete(0, 1, 0), Edit.Delete(1, 1, 0)], 2, 0 },           // unmerged neighbours
        { [Edit.Insert(0, 0, 1), Edit.Delete(0, 1, 1)], 1, 1 },           // insert before delete
        { [Edit.Equal(0, 0, 1), Edit.Equal(1, 1, 1)], 2, 2 },             // unmerged equal runs
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void RejectsBrokenInvariants(Edit[] edits, int oldLength, int newLength)
    {
        Assert.False(EditScript.TryValidate(edits, oldLength, newLength, out string? error));
        Assert.NotNull(error);
        Assert.Throws<ArgumentException>(() => EditScript.Create(edits, oldLength, newLength));
    }

    [Fact]
    public void ApplyUnapplyAndInvert()
    {
        string[] oldItems = ["a", "b", "c", "d"];
        string[] newItems = ["a", "x", "y", "d", "e"];
        EditScript s = EditScript.Create(
            [Edit.Equal(0, 0, 1), Edit.Delete(1, 2, 1), Edit.Insert(3, 1, 2), Edit.Equal(3, 3, 1), Edit.Insert(4, 4, 1)], 4, 5);

        Assert.True(s.Matches<string>(oldItems, newItems));
        Assert.Equal(newItems, s.Apply<string>(oldItems, newItems));
        Assert.Equal(oldItems, s.Unapply<string>(oldItems, newItems));
        Assert.Equal(3, s.InsertedCount);
        Assert.Equal(2, s.DeletedCount);
        Assert.Equal(5, s.EditDistance);

        EditScript inv = s.Invert();
        Assert.Equal(5, inv.OldLength);
        Assert.Equal(4, inv.NewLength);
        Assert.Equal(oldItems, inv.Apply<string>(newItems, oldItems));
        Assert.Equal(s.Edits.ToArray(), inv.Invert().Edits.ToArray());
    }

    [Fact]
    public void MatchesCatchesFalseEqualRuns()
    {
        EditScript s = EditScript.Identical(2);
        Assert.False(s.Matches<int>([1, 2], [1, 3]));
        Assert.True(s.Matches("ab".AsSpan(), "AB".AsSpan(), new CaseInsensitiveChars()));
    }

    [Fact]
    public void LengthMismatchThrows()
    {
        EditScript s = EditScript.Identical(2);
        Assert.Throws<ArgumentException>(() => s.Apply<int>([1], [1, 2]));
    }

    private sealed class CaseInsensitiveChars : IEqualityComparer<char>
    {
        public bool Equals(char x, char y) => char.ToLowerInvariant(x) == char.ToLowerInvariant(y);

        public int GetHashCode(char obj) => char.ToLowerInvariant(obj);
    }
}
