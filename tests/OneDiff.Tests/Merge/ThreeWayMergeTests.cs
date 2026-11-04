using System.Text;
using OneDiff.Merge;

namespace OneDiff.Tests.Merge;

/// <summary>
/// Expected texts in <see cref="Cases"/> are what git merge-file 2.51.2 printed for the same inputs, with -L ours -L base -L theirs.
/// </summary>
public class ThreeWayMergeTests
{
    private const ConflictStyle M = ConflictStyle.Merge;
    private const ConflictStyle D3 = ConflictStyle.Diff3;
    private const ConflictStyle Z3 = ConflictStyle.ZDiff3;

    public static TheoryData<string, string, string, ConflictStyle, ConflictResolution, string, int> Cases => new()
    {
        // clean merges, false conflicts
        { "a\nb\nc\n", "a\nX\nc\n", "a\nb\nc\nd\n", M, ConflictResolution.None, "a\nX\nc\nd\n", 0 },
        { "a\nb\nc\n", "a\nX\nc\n", "a\nX\nc\n", M, ConflictResolution.None, "a\nX\nc\n", 0 },
        { "a\nb\nc\n", "a\nX\nc\n", "a\nX\nc\n", D3, ConflictResolution.None, "a\nX\nc\n", 0 },
        { "a\nb\nz\n", "a\nX\nY\nz\n", "a\nX\nY\nz\n", D3, ConflictResolution.None, "a\nX\nY\nz\n", 0 },
        { "a\nb\nc\nd\ne\nf\nz\n", "a\nB\nc\nd\ne\nf\nz\n", "a\nb\nc\nD\ne\nf\nz\n", M, ConflictResolution.None, "a\nB\nc\nD\ne\nf\nz\n", 0 },
        { "x\n", "x\ny\n", "w\nx\n", M, ConflictResolution.None, "w\nx\ny\n", 0 },
        { "", "x\n", "", M, ConflictResolution.None, "x\n", 0 },
        { "a\n", "", "", M, ConflictResolution.None, "", 0 },
        { "a\nb\nc\n", "", "a\nb\nc\n", M, ConflictResolution.None, "", 0 },
        { "", "a\n", "a\n", M, ConflictResolution.None, "a\n", 0 },
        { "a\nb\n", "a\nb\n", "a\nb\n", M, ConflictResolution.None, "a\nb\n", 0 },

        // plain conflicts
        { "a\nb\nc\n", "a\nX\nc\n", "a\nY\nc\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nX\n=======\nY\n>>>>>>> theirs\nc\n", 1 },
        { "a\nb\nc\n", "a\nX\nc\n", "a\nY\nc\n", D3, ConflictResolution.None, "a\n<<<<<<< ours\nX\n||||||| base\nb\n=======\nY\n>>>>>>> theirs\nc\n", 1 },
        { "a\nb\nc\n", "a\nX\nc\n", "a\nY\nc\n", Z3, ConflictResolution.None, "a\n<<<<<<< ours\nX\n||||||| base\nb\n=======\nY\n>>>>>>> theirs\nc\n", 1 },
        { "a\nb\nc\nd\n", "a\nX\nc\nd\n", "a\nb\nY\nd\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nX\nc\n=======\nb\nY\n>>>>>>> theirs\nd\n", 1 },
        { "a\nb\nc\n", "a\nI\nb\nc\n", "a\nJ\nb\nc\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nI\n=======\nJ\n>>>>>>> theirs\nb\nc\n", 1 },
        { "a\nb\nc\n", "a\nI\nb\nc\n", "a\nc\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nI\nb\n=======\n>>>>>>> theirs\nc\n", 1 },
        { "a\nb\nc\nd\n", "a\nB\nc\nd\n", "a\nb\nI\nc\nd\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nB\n=======\nb\nI\n>>>>>>> theirs\nc\nd\n", 1 },
        { "a\nb\nc\nd\ne\nf\nz\n", "a\nB\nc\nD\ne\nf\nz\n", "a\nb\nCC\nd\ne\nf\nz\n", D3, ConflictResolution.None, "a\n<<<<<<< ours\nB\nc\nD\n||||||| base\nb\nc\nd\n=======\nb\nCC\nd\n>>>>>>> theirs\ne\nf\nz\n", 1 },
        { "a\n", "", "b\n", M, ConflictResolution.None, "<<<<<<< ours\n=======\nb\n>>>>>>> theirs\n", 1 },
        { "", "x\n", "y\n", M, ConflictResolution.None, "<<<<<<< ours\nx\n=======\ny\n>>>>>>> theirs\n", 1 },
        { "a\nb\nc\n", "", "a\nb\nd\n", M, ConflictResolution.None, "<<<<<<< ours\n=======\na\nb\nd\n>>>>>>> theirs\n", 1 },

        // line endings
        { "a\nb\nc\n", "a\nb\nc\nX", "a\nb\nc\nY", M, ConflictResolution.None, "a\nb\nc\n<<<<<<< ours\nX\n=======\nY\n>>>>>>> theirs\n", 1 },
        { "a\nb\nc\n", "a\nb\nc\nX", "a\nb\nc\nX", M, ConflictResolution.None, "a\nb\nc\nX", 0 },
        { "a\nb\nc\n", "a\nb\nc", "a\nb\nc", M, ConflictResolution.None, "a\nb\nc", 0 },
        { "a\nb\nc\n", "a\nb\nc", "a\nb\nc\n", M, ConflictResolution.None, "a\nb\nc", 0 },
        { "a\nb\nc\n", "a\nb\nc", "a\nb\nd\n", M, ConflictResolution.None, "a\nb\n<<<<<<< ours\nc\n=======\nd\n>>>>>>> theirs\n", 1 },
        { "a\nb", "a\nX", "a\nX\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nX\n=======\nX\n>>>>>>> theirs\n", 1 },
        { "a\r\nb\r\nc\r\n", "a\r\nX\r\nc\r\n", "a\r\nY\r\nc\r\n", M, ConflictResolution.None, "a\r\n<<<<<<< ours\r\nX\r\n=======\r\nY\r\n>>>>>>> theirs\r\nc\r\n", 1 },
        { "a\r\nb\r\nc\r\n", "a\r\nX\r\nc\r\n", "a\r\nY\r\nc\r\n", D3, ConflictResolution.None, "a\r\n<<<<<<< ours\r\nX\r\n||||||| base\r\nb\r\n=======\r\nY\r\n>>>>>>> theirs\r\nc\r\n", 1 },
        { "a\nb\nc\n", "a\r\nX\r\nc\r\n", "a\r\nY\r\nc\r\n", M, ConflictResolution.None, "a\r\n<<<<<<< ours\nX\r\n=======\nY\r\n>>>>>>> theirs\nc\r\n", 1 },
        { "a\r\nb\n", "a\r\nX\n", "a\r\nY\n", M, ConflictResolution.None, "a\r\n<<<<<<< ours\r\nX\n=======\r\nY\n>>>>>>> theirs\r\n", 1 },
        { "a\nb\r\n", "a\nX\r\n", "a\nY\r\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nX\r\n=======\nY\r\n>>>>>>> theirs\n", 1 },
        { "b\r\nc\r\n", "X\r\nc\r\n", "Y\r\nc\r\n", M, ConflictResolution.None, "<<<<<<< ours\r\nX\r\n=======\r\nY\r\n>>>>>>> theirs\r\nc\r\n", 1 },
        { "b\n", "X\r\n", "Y\r\n", M, ConflictResolution.None, "<<<<<<< ours\nX\r\n=======\nY\r\n>>>>>>> theirs\n", 1 },
        { "a\r\nb\r\n", "X\r\na\r\nb\r\n", "Y\r\na\r\nb\r\n", M, ConflictResolution.None, "<<<<<<< ours\r\nX\r\n=======\r\nY\r\n>>>>>>> theirs\r\na\r\nb\r\n", 1 },
        { "a\r\nb\r\n", "X\na\r\nb\r\n", "Y\na\r\nb\r\n", M, ConflictResolution.None, "<<<<<<< ours\nX\n=======\nY\n>>>>>>> theirs\na\r\nb\r\n", 1 },
        { "a\r\nb\r\n", "a\r\nX\r\n", "", M, ConflictResolution.None, "<<<<<<< ours\r\na\r\nX\r\n=======\r\n>>>>>>> theirs\r\n", 1 },
        { "", "X\r\n", "Y\r\n", M, ConflictResolution.None, "<<<<<<< ours\nX\r\n=======\nY\r\n>>>>>>> theirs\n", 1 },
        { "a\r\nb\r\n", "a\r\nX", "a\r\nY", M, ConflictResolution.None, "a\r\n<<<<<<< ours\r\nX\r\n=======\r\nY\r\n>>>>>>> theirs\r\n", 1 },

        // narrowing a conflict
        { "a\nb\nc\n", "a\nX\nY\nZ\nc\n", "a\nX\nQ\nZ\nc\n", M, ConflictResolution.None, "a\nX\n<<<<<<< ours\nY\n=======\nQ\n>>>>>>> theirs\nZ\nc\n", 1 },
        { "a\nb\nc\n", "a\nX\nY\nZ\nc\n", "a\nX\nQ\nZ\nc\n", D3, ConflictResolution.None, "a\n<<<<<<< ours\nX\nY\nZ\n||||||| base\nb\n=======\nX\nQ\nZ\n>>>>>>> theirs\nc\n", 1 },
        { "a\nb\nc\n", "a\nX\nY\nZ\nc\n", "a\nX\nQ\nZ\nc\n", Z3, ConflictResolution.None, "a\nX\n<<<<<<< ours\nY\n||||||| base\nb\n=======\nQ\n>>>>>>> theirs\nZ\nc\n", 1 },
        { "a\nb\nz\n", "a\nX\nY\nz\n", "a\nX\nY\nW\nz\n", M, ConflictResolution.None, "a\nX\nY\n<<<<<<< ours\n=======\nW\n>>>>>>> theirs\nz\n", 1 },
        { "a\nb\nz\n", "a\nX\nY\nz\n", "a\nX\nY\nW\nz\n", Z3, ConflictResolution.None, "a\nX\nY\n<<<<<<< ours\n||||||| base\nb\n=======\nW\n>>>>>>> theirs\nz\n", 1 },
        { "a\nb\nz\n", "a\nX\nY\nW\nz\n", "a\nY\nz\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nX\nY\nW\n=======\nY\n>>>>>>> theirs\nz\n", 1 },
        { "a\nb\nz\n", "a\nX\nY\nW\nz\n", "a\nY\nz\n", Z3, ConflictResolution.None, "a\n<<<<<<< ours\nX\nY\nW\n||||||| base\nb\n=======\nY\n>>>>>>> theirs\nz\n", 1 },
        { "a\nb\nc\n", "a\nb\nI\nJ\nc\n", "a\nb\nI\nc\n", M, ConflictResolution.None, "a\nb\nI\n<<<<<<< ours\nJ\n=======\n>>>>>>> theirs\nc\n", 1 },
        { "a\nb\nc\n", "a\nb\nI\nJ\nc\n", "a\nb\nI\nc\n", D3, ConflictResolution.None, "a\nb\n<<<<<<< ours\nI\nJ\n||||||| base\n=======\nI\n>>>>>>> theirs\nc\n", 1 },
        { "a\nb\nc\nd\n", "a\nX\nY\nd\n", "a\nX\nc\nd\n", M, ConflictResolution.None, "a\nX\n<<<<<<< ours\nY\n=======\nc\n>>>>>>> theirs\nd\n", 1 },
        { "a\nb\nz\n", "a\nX\nX\nz\n", "a\nX\nz\n", Z3, ConflictResolution.None, "a\nX\n<<<<<<< ours\nX\n||||||| base\nb\n=======\n>>>>>>> theirs\nz\n", 1 },
        { "a\nb\nz\n", "a\nA\nB\nA\nz\n", "a\nA\nQ\nA\nz\n", Z3, ConflictResolution.None, "a\nA\n<<<<<<< ours\nB\n||||||| base\nb\n=======\nQ\n>>>>>>> theirs\nA\nz\n", 1 },

        // conflicts close together
        { "a\nb1\nb2\nb3\nb4\nb5\nb6\nb7\nb8\nj\n", "a\nO1\nm1\nm2\nm3\nO2\nj\n", "a\nT1\nm1\nm2\nm3\nT2\nj\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nO1\nm1\nm2\nm3\nO2\n=======\nT1\nm1\nm2\nm3\nT2\n>>>>>>> theirs\nj\n", 1 },
        { "a\nb1\nb2\nb3\nb4\nb5\nb6\nb7\nb8\nj\n", "a\nO1\nm1\nm2\nm3\nm4\nO2\nj\n", "a\nT1\nm1\nm2\nm3\nm4\nT2\nj\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nO1\n=======\nT1\n>>>>>>> theirs\nm1\nm2\nm3\nm4\n<<<<<<< ours\nO2\n=======\nT2\n>>>>>>> theirs\nj\n", 2 },
        { "a\nb1\nb2\nb3\nb4\nb5\nb6\nb7\nb8\nj\n", "a\nO1\nm1\nm2\nm3\nm4\nO2\nj\n", "a\nT1\nm1\nm2\nm3\nm4\nT2\nj\n", D3, ConflictResolution.None, "a\n<<<<<<< ours\nO1\nm1\nm2\nm3\nm4\nO2\n||||||| base\nb1\nb2\nb3\nb4\nb5\nb6\nb7\nb8\n=======\nT1\nm1\nm2\nm3\nm4\nT2\n>>>>>>> theirs\nj\n", 1 },
        { "a\nB\nc1\nc2\nc3\nD\nz\n", "a\nBo\nc1\nc2\nc3\nDo\nz\n", "a\nBt\nc1\nc2\nc3\nDt\nz\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nBo\nc1\nc2\nc3\nDo\n=======\nBt\nc1\nc2\nc3\nDt\n>>>>>>> theirs\nz\n", 1 },
        { "a\nB\nc1\nc2\nc3\nc4\nD\nz\n", "a\nBo\nc1\nc2\nc3\nc4\nDo\nz\n", "a\nBt\nc1\nc2\nc3\nc4\nDt\nz\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nBo\n=======\nBt\n>>>>>>> theirs\nc1\nc2\nc3\nc4\n<<<<<<< ours\nDo\n=======\nDt\n>>>>>>> theirs\nz\n", 2 },
        { "a\nB\nc1\nc2\nc3\nD\nz\n", "a\nBo\nc1\nc2\nc3\nDo\nz\n", "a\nBt\nc1\nc2\nc3\nDt\nz\n", D3, ConflictResolution.None, "a\n<<<<<<< ours\nBo\n||||||| base\nB\n=======\nBt\n>>>>>>> theirs\nc1\nc2\nc3\n<<<<<<< ours\nDo\n||||||| base\nD\n=======\nDt\n>>>>>>> theirs\nz\n", 2 },
        { "a\nB\n}\n}\n}\n}\n}\n}\nD\nz\n", "a\nBo\n}\n}\n}\n}\n}\n}\nDo\nz\n", "a\nBt\n}\n}\n}\n}\n}\n}\nDt\nz\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nBo\n}\n}\n}\n}\n}\n}\nDo\n=======\nBt\n}\n}\n}\n}\n}\n}\nDt\n>>>>>>> theirs\nz\n", 1 },
        { "a\nB\n9\n9\n9\n9\nD\nz\n", "a\nBo\n9\n9\n9\n9\nDo\nz\n", "a\nBt\n9\n9\n9\n9\nDt\nz\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nBo\n=======\nBt\n>>>>>>> theirs\n9\n9\n9\n9\n<<<<<<< ours\nDo\n=======\nDt\n>>>>>>> theirs\nz\n", 2 },
        { "a\nB\n\u00e9\n\u00e9\n\u00e9\n\u00e9\nD\nz\n", "a\nBo\n\u00e9\n\u00e9\n\u00e9\n\u00e9\nDo\nz\n", "a\nBt\n\u00e9\n\u00e9\n\u00e9\n\u00e9\nDt\nz\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nBo\n\u00e9\n\u00e9\n\u00e9\n\u00e9\nDo\n=======\nBt\n\u00e9\n\u00e9\n\u00e9\n\u00e9\nDt\n>>>>>>> theirs\nz\n", 1 },
        { "a\nB\nc1\nc2\nc3\nc4\nD\nz\n", "a\nBo\nc1\nc2x\nc3\nc4\nDo\nz\n", "a\nBt\nc1\nc2\nc3\nc4\nDt\nz\n", M, ConflictResolution.None, "a\n<<<<<<< ours\nBo\n=======\nBt\n>>>>>>> theirs\nc1\nc2x\nc3\nc4\n<<<<<<< ours\nDo\n=======\nDt\n>>>>>>> theirs\nz\n", 2 },

        // matching the sides after a different split of the changes, and where runs of equal lines may sit
        { "\n\nA = {\n\n\n\nB1\n\n\n\nB2\nC\n\n\n", "A = {\n\n\n\nN\n\nC\n\n\n", "A = {\n\n\n\nN\n\nC\n", M, ConflictResolution.None, "A = {\n\n\n\nN\n\nC\n", 0 },
        { "\n\nA = {\n\n\n\nB1\n\n\n\nB2\nC\n\n\n", "A = {\n\n\n\nN\n\nC\n\n\n", "A = {\n\n\n\nN\n\nC\n", D3, ConflictResolution.None, "A = {\n\n\n\n<<<<<<< ours\nN\n\n||||||| base\nB1\n\n\n\nB2\n=======\nN\n\n>>>>>>> theirs\nC\n", 1 },
        { "\n\nA = {\n\n\n\nB1\n\n\n\nB2\nC\n\n\n", "A = {\n\n\n\nN\n\nC\n\n\n", "A = {\n\n\n\nN\n\nC\n", Z3, ConflictResolution.None, "A = {\n\n\n\nN\n\n<<<<<<< ours\n||||||| base\nB1\n\n\n\nB2\n=======\n>>>>>>> theirs\nC\n", 1 },
        { "V\nT\n", "V\n", "P\nV\nV\n", M, ConflictResolution.None, "P\nV\nV\n", 0 },
        { "V\nT\n", "V\n", "P\nV\nV\n", D3, ConflictResolution.None, "P\nV\nV\n", 0 },
        { "V\nT\n", "V\n", "P\nV\nV\n", Z3, ConflictResolution.None, "P\nV\nV\n", 0 },
        { "", "M\nM\n", "T\nM\n", M, ConflictResolution.None, "<<<<<<< ours\nM\n=======\nT\n>>>>>>> theirs\nM\n", 1 },
        { "", "M\nM\n", "T\nM\n", D3, ConflictResolution.None, "<<<<<<< ours\nM\nM\n||||||| base\n=======\nT\nM\n>>>>>>> theirs\n", 1 },
        { "", "M\nM\n", "T\nM\n", Z3, ConflictResolution.None, "<<<<<<< ours\nM\n||||||| base\n=======\nT\n>>>>>>> theirs\nM\n", 1 },
        { "b\n", "A\nQ\nA\n", "A\n", M, ConflictResolution.None, "A\n<<<<<<< ours\nQ\nA\n=======\n>>>>>>> theirs\n", 1 },
        { "b\n", "A\nQ\nA\n", "A\n", D3, ConflictResolution.None, "<<<<<<< ours\nA\nQ\nA\n||||||| base\nb\n=======\nA\n>>>>>>> theirs\n", 1 },
        { "b\n", "A\nQ\nA\n", "A\n", Z3, ConflictResolution.None, "A\n<<<<<<< ours\nQ\nA\n||||||| base\nb\n=======\n>>>>>>> theirs\n", 1 },

        // resolutions
        { "a\nb\nz\n", "a\nX\nY\nz\n", "a\nQ\nY\nz\n", M, ConflictResolution.Ours, "a\nX\nY\nz\n", 0 },
        { "a\nb\nz\n", "a\nX\nY\nz\n", "a\nQ\nY\nz\n", M, ConflictResolution.Theirs, "a\nQ\nY\nz\n", 0 },
        { "a\nb\nz\n", "a\nX\nY\nz\n", "a\nQ\nY\nz\n", M, ConflictResolution.Union, "a\nX\nQ\nY\nz\n", 0 },
        { "a\nb\nz\n", "a\nX\nY\nZ\nz\n", "a\nX\nQ\nZ\nz\n", M, ConflictResolution.Union, "a\nX\nY\nQ\nZ\nz\n", 0 },
        { "a\nb\nz\n", "a\nX\nY\nZ\nz\n", "a\nX\nQ\nZ\nz\n", D3, ConflictResolution.Union, "a\nX\nY\nZ\nX\nQ\nZ\nz\n", 0 },
        { "a\nb\nz\n", "a\nX\nY\nZ\nz\n", "a\nX\nQ\nZ\nz\n", Z3, ConflictResolution.Union, "a\nX\nY\nQ\nZ\nz\n", 0 },
        { "a\nb\nz\n", "a\nX\nY\nZ\nz\n", "a\nX\nQ\nZ\nz\n", D3, ConflictResolution.Ours, "a\nX\nY\nZ\nz\n", 0 },
        { "a\nb1\nb2\nb3\nb4\nb5\nz\n", "a\nO1\nm1\nO2\nz\n", "a\nT1\nm1\nT2\nz\n", M, ConflictResolution.Union, "a\nO1\nm1\nO2\nT1\nm1\nT2\nz\n", 0 },
        { "a\nb1\nb2\nb3\nb4\nb5\nz\n", "a\nO1\nm1\nm2\nm3\nm4\nO2\nz\n", "a\nT1\nm1\nm2\nm3\nm4\nT2\nz\n", M, ConflictResolution.Union, "a\nO1\nT1\nm1\nm2\nm3\nm4\nO2\nT2\nz\n", 0 },
        { "a\nb1\nb2\nb3\nb4\nb5\nz\n", "a\nO1\nm1\nm2\nm3\nm4\nO2\nz\n", "a\nT1\nm1\nm2\nm3\nm4\nT2\nz\n", D3, ConflictResolution.Union, "a\nO1\nm1\nm2\nm3\nm4\nO2\nT1\nm1\nm2\nm3\nm4\nT2\nz\n", 0 },
        { "a\nb\n", "a\nX", "a\nY", M, ConflictResolution.Ours, "a\nX", 0 },
        { "a\nb\n", "a\nX", "a\nY", M, ConflictResolution.Theirs, "a\nY", 0 },
        { "a\nb\n", "a\nX", "a\nY", M, ConflictResolution.Union, "a\nX\nY", 0 },
        { "a\nb\n", "a\nX", "a\nY\n", M, ConflictResolution.Union, "a\nX\nY\n", 0 },
        { "a\nb\n", "a\nX", "a\nY\n", M, ConflictResolution.Theirs, "a\nY\n", 0 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesGitMergeFile(string baseText, string ours, string theirs, ConflictStyle style, ConflictResolution resolution, string expected, int conflicts)
    {
        var settings = new MergeSettings { Style = style, Resolution = resolution };
        TextMerge merge = ThreeWayMerge.Lines(baseText, ours, theirs, settings);

        Assert.Equal(expected, merge.Text);
        Assert.Equal(conflicts, merge.ConflictCount);
        AssertRegionsTile(merge);
    }

    [Fact]
    public void MarkerSizeAndLabels()
    {
        var settings = new MergeSettings { Style = D3, MarkerSize = 10, OursLabel = "A", BaseLabel = "B", TheirsLabel = null };
        TextMerge merge = Diff.Merge("a\nb\nz\n", "a\nX\nz\n", "a\nY\nz\n", settings);

        Assert.Equal("a\n<<<<<<<<<< A\nX\n|||||||||| B\nb\n==========\nY\n>>>>>>>>>>\nz\n", merge.Text);
    }

    [Fact]
    public void RegionsDescribeOneSidedChanges()
    {
        TextMerge merge = Diff.Merge("a\nb\nc\nd\ne\nf\n", "a\nB\nc\nd\ne\nF\n", "a\nb\nc\nD\ne\nf\n");

        MergeRegionKind[] kinds =
        [
            MergeRegionKind.Unchanged, MergeRegionKind.OursOnly, MergeRegionKind.Unchanged,
            MergeRegionKind.TheirsOnly, MergeRegionKind.Unchanged, MergeRegionKind.OursOnly,
        ];
        Assert.Equal(kinds, merge.Regions.Select(r => r.Kind).ToArray());
        Assert.Equal(new MergeRegion(MergeRegionKind.TheirsOnly, new ItemRange(3, 1), new ItemRange(3, 1), new ItemRange(3, 1)), merge.Regions[3]);
        Assert.Equal("a\nB\nc\nD\ne\nF\n", merge.Text);
    }

    [Fact]
    public void ConflictRegionHoldsAllThreeSides()
    {
        TextMerge merge = Diff.Merge("a\nb\nc\n", "a\nX\nX2\nc\n", "a\nY\nc\n", new MergeSettings { Style = D3 });

        MergeRegion conflict = Assert.Single(merge.Regions, r => r.Kind == MergeRegionKind.Conflict);
        Assert.Equal("b\n", merge.Slice(MergeSide.Base, conflict.Base).ToString());
        Assert.Equal("X\nX2\n", merge.Slice(MergeSide.Ours, conflict.Ours).ToString());
        Assert.Equal("Y\n", merge.Slice(MergeSide.Theirs, conflict.Theirs).ToString());
        Assert.Equal(3, merge.LineCount(MergeSide.Base));
    }

    [Fact]
    public void ItemsMergeAnyType()
    {
        ItemMerge merge = Diff.Merge<string>(["a", "b", "c"], ["a", "X", "c"], ["a", "b", "c", "d"]);

        Assert.False(merge.HasConflicts);
        Assert.Equal([MergeRegionKind.Unchanged, MergeRegionKind.OursOnly, MergeRegionKind.Unchanged, MergeRegionKind.TheirsOnly],
            merge.Regions.Select(r => r.Kind).ToArray());

        ItemMerge conflict = Diff.Merge<string>(["a", "b", "c"], ["a", "X", "c"], ["a", "Y", "c"]);
        Assert.Equal(1, conflict.ConflictCount);
    }

    [Fact]
    public void RandomMergesKeepTheirPromises()
    {
        var random = new Random(4401);
        foreach (ConflictStyle style in new[] { M, D3, Z3 })
        {
            for (int round = 0; round < 400; round++)
            {
                string[] baseLines = RandomLines(random, random.Next(0, 14));
                string baseText = Join(baseLines);
                string ours = Join(Mutate(random, baseLines));
                string theirs = Join(Mutate(random, baseLines));
                var settings = new MergeSettings { Style = style };

                TextMerge merge = ThreeWayMerge.Lines(baseText, ours, theirs, settings);
                AssertRegionsTile(merge);

                // Resolving toward one side equals stitching that side's lines (and the other side's one-sided changes) together.
                foreach (ConflictResolution resolution in new[] { ConflictResolution.Ours, ConflictResolution.Theirs })
                {
                    TextMerge resolved = ThreeWayMerge.Lines(baseText, ours, theirs, settings with { Resolution = resolution });
                    Assert.Equal(Stitch(resolved, resolution), resolved.Text);
                    Assert.Equal(0, resolved.ConflictCount);
                }

                if (!merge.HasConflicts)
                {
                    Assert.Equal(Stitch(merge, ConflictResolution.Ours), merge.Text);

                    // With nothing in dispute, the order of the two sides cannot matter.
                    Assert.Equal(merge.Text, ThreeWayMerge.Lines(baseText, theirs, ours, settings).Text);
                }

                // A side that changed nothing never gets in the way, and identical sides merge to themselves.
                Assert.Equal(ours, ThreeWayMerge.Lines(baseText, ours, baseText, settings).Text);
                Assert.Equal(theirs, ThreeWayMerge.Lines(baseText, baseText, theirs, settings).Text);
                Assert.Equal(ours, ThreeWayMerge.Lines(baseText, ours, ours, settings).Text);
            }
        }
    }

    [Fact]
    public void InvalidSettingsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Diff.Merge("a", "a", "a", new MergeSettings { MarkerSize = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Diff.Merge("a", "a", "a", new MergeSettings { Style = (ConflictStyle)9 }));
        Assert.Throws<ArgumentNullException>(() => Diff.Merge(null!, "a", "a"));
    }

    // Stitches the merged text back together from the regions: both-sides-agree stretches from ours, one-sided stretches from the
    // changed side, and conflicts from the side the resolution names.
    private static string Stitch(TextMerge merge, ConflictResolution resolution)
    {
        var text = new StringBuilder();
        foreach (MergeRegion r in merge.Regions)
        {
            switch (r.Kind)
            {
                case MergeRegionKind.TheirsOnly:
                    text.Append(merge.Slice(MergeSide.Theirs, r.Theirs));
                    break;
                case MergeRegionKind.Conflict when resolution == ConflictResolution.Theirs:
                    text.Append(merge.Slice(MergeSide.Theirs, r.Theirs));
                    break;
                default:
                    text.Append(merge.Slice(MergeSide.Ours, r.Ours));
                    break;
            }
        }

        return text.ToString();
    }

    private static void AssertRegionsTile(TextMerge merge)
    {
        int b = 0;
        int o = 0;
        int t = 0;
        foreach (MergeRegion r in merge.Regions)
        {
            Assert.Equal(b, r.Base.Start);
            Assert.Equal(o, r.Ours.Start);
            Assert.Equal(t, r.Theirs.Start);
            b = r.Base.End;
            o = r.Ours.End;
            t = r.Theirs.End;

            switch (r.Kind)
            {
                case MergeRegionKind.Unchanged:
                    Assert.Equal(merge.Slice(MergeSide.Base, r.Base).ToString(), merge.Slice(MergeSide.Ours, r.Ours).ToString());
                    Assert.Equal(merge.Slice(MergeSide.Base, r.Base).ToString(), merge.Slice(MergeSide.Theirs, r.Theirs).ToString());
                    break;
                case MergeRegionKind.OursOnly:
                    Assert.Equal(merge.Slice(MergeSide.Base, r.Base).ToString(), merge.Slice(MergeSide.Theirs, r.Theirs).ToString());
                    break;
                case MergeRegionKind.TheirsOnly:
                    Assert.Equal(merge.Slice(MergeSide.Base, r.Base).ToString(), merge.Slice(MergeSide.Ours, r.Ours).ToString());
                    break;
                case MergeRegionKind.BothSame:
                    Assert.Equal(merge.Slice(MergeSide.Ours, r.Ours).ToString(), merge.Slice(MergeSide.Theirs, r.Theirs).ToString());
                    break;
            }
        }

        Assert.Equal(merge.LineCount(MergeSide.Base), b);
        Assert.Equal(merge.LineCount(MergeSide.Ours), o);
        Assert.Equal(merge.LineCount(MergeSide.Theirs), t);
    }

    private static string[] RandomLines(Random random, int count) =>
        Enumerable.Range(0, count).Select(_ => "l" + random.Next(5)).ToArray();

    private static string[] Mutate(Random random, string[] lines)
    {
        var result = new List<string>(lines);
        int edits = random.Next(0, 4);
        for (int k = 0; k < edits; k++)
        {
            int at = random.Next(0, result.Count + 1);
            switch (random.Next(3))
            {
                case 0:
                    result.Insert(at, "n" + random.Next(5));
                    break;
                case 1 when at < result.Count:
                    result.RemoveAt(at);
                    break;
                case 2 when at < result.Count:
                    result[at] = "m" + random.Next(5);
                    break;
            }
        }

        return result.ToArray();
    }

    private static string Join(string[] lines) => lines.Length == 0 ? string.Empty : string.Join("\n", lines) + "\n";
}
