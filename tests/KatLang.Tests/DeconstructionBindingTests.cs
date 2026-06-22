namespace KatLang.Tests;

/// <summary>
/// Deconstruction binding patterns: a comma binding pattern with one movable rest
/// binding, shared by assignment binding (<c>x, y..., z = RHS</c>) and function
/// parameter binding (<c>F(x, y..., z)</c>). The rest binding captures its items as
/// one grouped sequence value and may appear at the start, middle, or end.
/// </summary>
public class DeconstructionBindingTests
{
    private static decimal[] Atoms(string source)
        => KatLangEngine.EvaluateToAtoms(source).ToArray();

    private static void AssertAtoms(string source, params decimal[] expected)
        => Assert.Equal(expected, Atoms(source));

    private static bool Fails(string source)
        => KatLangEngine.Run(source).IsFailure;

    // ───────────────────────── Assignment deconstruction ─────────────────────

    [Fact]
    public void Assignment_MovableRest_BindsAroundCapturedMiddle()
    {
        const string define = "A = 1, 2, 3, 4, 5\nx, y..., z = A\n";
        AssertAtoms(define + "x", 1);
        AssertAtoms(define + "y", 2, 3, 4);
        AssertAtoms(define + "y.count", 3);
        AssertAtoms(define + "z", 5);
        AssertAtoms(define + "x + y.sum + z", 15);
    }

    [Fact]
    public void Assignment_GroupedSequenceValue_IsConsumedElementByElement()
        // Deconstructing a single grouped value consumes its elements (rule 4).
        => AssertAtoms("A = 1, 2, 3, 4, 5\nx, y..., z = A\nx, y.count, z", 1, 3, 5);

    [Fact]
    public void Assignment_DirectItemStream_BindsLikeGroupedValue()
    {
        const string define = "x, y..., z = 1, 2, 3, 4, 5\n";
        AssertAtoms(define + "x", 1);
        AssertAtoms(define + "y", 2, 3, 4);
        AssertAtoms(define + "z", 5);
    }

    [Fact]
    public void Assignment_RestCapturesZeroItems_AsEmptyGroupedValue()
    {
        const string define = "x, y..., z = 1, 2\n";
        AssertAtoms(define + "x", 1);
        AssertAtoms(define + "y.count", 0);
        AssertAtoms(define + "z", 2);
        // The empty rest is a grouped sequence value, so summing it is 0.
        AssertAtoms(define + "x + y.sum + z", 3);
    }

    [Fact]
    public void Assignment_NoRest_RequiresExactCount()
    {
        AssertAtoms("x, y = 1, 2\nx", 1);
        AssertAtoms("x, y = 1, 2\ny", 2);
    }

    [Fact]
    public void Assignment_RestAtStart_CapturesLeadingItems()
    {
        const string define = "head..., last = 1, 2, 3\n";
        AssertAtoms(define + "head", 1, 2);
        AssertAtoms(define + "head.count", 2);
        AssertAtoms(define + "last", 3);
    }

    [Fact]
    public void Assignment_RestAtEnd_CapturesTrailingItems()
    {
        const string define = "first, tail... = 1, 2, 3\n";
        AssertAtoms(define + "first", 1);
        AssertAtoms(define + "tail", 2, 3);
        AssertAtoms(define + "tail.count", 2);
    }

    [Fact]
    public void Assignment_MatchingAlgorithm_BindsPrefixSuffixAndMiddle()
    {
        // p1, p2, rest..., q1, q2 against i1..i7 binds the middle three to rest.
        const string define = "p1, p2, rest..., q1, q2 = 1, 2, 3, 4, 5, 6, 7\n";
        AssertAtoms(define + "p1, p2, rest.count, q1, q2", 1, 2, 3, 6, 7);
        AssertAtoms(define + "rest", 3, 4, 5);
    }

    [Fact]
    public void Assignment_DeconstructionAfterOutputLine_DoesNotAbsorbIntoOutput()
    {
        // An implicit output line ends at a following deconstruction assignment:
        // the output stays the single `F(A)` row (15), and `x, y..., z = A` defines
        // its own (unused) properties instead of being swallowed as more output.
        AssertAtoms(
            """
            A = 1, 2, 3, 4, 5
            F(x, y..., z) = x + y.sum + z

            F(A)

            x, y..., z = A
            """,
            15);

        // The deconstructed properties remain usable when referenced after the
        // output line.
        AssertAtoms(
            """
            A = 1, 2, 3, 4, 5
            x, y..., z = A
            x + y.sum + z
            """,
            15);
    }

    [Fact]
    public void Assignment_ScalarRhs_RestAtEndCapturesZeroItems()
    {
        // A scalar right-hand side is a one-item stream, so the fixed `first` binds
        // it and the rest captures zero items.
        const string define = "first, tail... = 1\n";
        AssertAtoms(define + "first", 1);
        AssertAtoms(define + "tail.count", 0);
        AssertAtoms(define + "tail"); // the empty rest is the empty sequence value (), which has no atoms
    }

    [Fact]
    public void Assignment_ScalarRhs_RestAtStartCapturesZeroItems()
    {
        const string define = "head..., last = 1\n";
        AssertAtoms(define + "head.count", 0);
        AssertAtoms(define + "head"); // the empty rest is the empty sequence value (), which has no atoms
        AssertAtoms(define + "last", 1);
    }

    [Fact]
    public void Assignment_SingleRestOnly_IsNotAValidAssignmentForm()
    {
        // `all... = RHS` is not a comma deconstruction (no comma) and not an
        // ordinary single-name assignment, so it is rejected. Rest-only collecting
        // belongs to function parameters (Sum(values...)), not to assignment.
        Assert.True(Fails("all... = 1, 2, 3\nall"));
        Assert.True(Fails("all... = 1\nall"));
    }

    [Theory]
    [InlineData("x, y = 1, 2, 3\nx")]           // too many items
    [InlineData("x, y..., z = 1\nx")]           // fewer than the two fixed bindings
    public void Assignment_ArityMismatch_Fails(string source)
        => Assert.True(Fails(source));

    [Fact]
    public void Assignment_TooFewItems_ReportsArityMismatch()
    {
        // A scalar right-hand side is a one-item stream; matching it against two
        // fixed targets is an arity mismatch (expected 2, actual 1), not a generic
        // shape/BadArity failure.
        var result = Evaluator.Run(new Expr.Block(Parser.Parse("x, y = 1\nx").Root));

        Assert.True(result.IsError);
        var arity = Assert.IsType<EvalError.ArityMismatch>(Innermost(result.Error));
        Assert.Equal(2, arity.Expected);
        Assert.Equal(1, arity.Actual);
    }

    private static EvalError Innermost(EvalError error)
    {
        while (error is EvalError.WithContext context)
            error = context.Inner;

        return error;
    }

    [Fact]
    public void Assignment_MultipleRestBindings_IsRejected()
    {
        var result = KatLangEngine.Run("a..., b... = 1, 2, 3\na");
        Assert.True(result.IsFailure);
        Assert.Contains("at most one rest binding", result.ToDisplayString(), StringComparison.Ordinal);
    }

    // ───────────────────── Function-parameter deconstruction ──────────────────

    [Fact]
    public void Parameter_SingleGroupedArgument_IsOpenedAndDeconstructed()
        => AssertAtoms("A = 1, 2, 3, 4, 5\nF(x, y..., z) = x + y.sum + z\nF(A)", 15);

    [Fact]
    public void Parameter_SpreadArgument_IsDeconstructed()
        => AssertAtoms("A = 1, 2, 3, 4, 5\nF(x, y..., z) = x + y.sum + z\nF(A...)", 15);

    [Fact]
    public void Parameter_DirectItemStream_IsDeconstructed()
        => AssertAtoms("F(x, y..., z) = x + y.sum + z\nF(1, 2, 3, 4, 5)", 15);

    [Fact]
    public void Parameter_RestCapturesZeroItems()
        // x = 1, y = (), z = 2; sum of the empty rest is 0.
        => AssertAtoms("F(x, y..., z) = x + y.sum + z\nF(1, 2)", 3);

    [Fact]
    public void Parameter_MatchingAlgorithm_BindsPrefixSuffixAndMiddle()
        => AssertAtoms(
            "F(p1, p2, rest..., q1, q2) = p1, p2, rest.count, q1, q2\nF(1, 2, 3, 4, 5, 6, 7)",
            1, 2, 3, 6, 7);

    [Fact]
    public void Parameter_ScalarArgument_RestCapturesZeroItems()
        // A single scalar argument is a one-item stream: first = 1, tail = ().
        => AssertAtoms("F(first, tail...) = first, tail.count\nF(1)", 1, 0);

    // ─────────────── Existing behavior preserved (regression guards) ──────────

    [Fact]
    public void SingleNameCapture_StillPacksRightHandSide()
        => AssertAtoms("c = 1, 2, 3\nc.count", 3);

    [Fact]
    public void RestOnlyVariadicCall_ConsumesItemStream()
    {
        // Rest-only `Sum(values...)` is the degenerate item-stream case: a single
        // grouped argument is opened, and separate slots bind the same stream.
        AssertAtoms("c = 1, 2, 3\nSum(values...) = values.sum\nSum(c)", 6);
        AssertAtoms("Sum(values...) = values.sum\nSum(1, 2, 3)", 6);
    }

    [Fact]
    public void ExpressionSpread_StillOpensInExpressionPosition()
    {
        AssertAtoms("A = 1, 2, 3\n(A...).count", 3);
        AssertAtoms("A = 1, 2, 3\nB = 4, 5\n(A..., B...).count", 5);
    }

    // ─────────────────── Aspect 2: unified item-stream binding ─────────────────

    [Fact]
    public void RestOnly_AllCallFormsConsumeSameItemStream()
    {
        // G(x...) = x.sum binds an item stream; all four supply forms give 15.
        const string g = "A = 1, 2, 3, 4, 5\nG(x...) = x.sum\n";
        AssertAtoms(g + "G(A)", 15);
        AssertAtoms(g + "G(A...)", 15);
        AssertAtoms("G(x...) = x.sum\nG(1, 2, 3, 4, 5)", 15);
        AssertAtoms("G(x...) = x.sum\nG((1, 2, 3, 4, 5))", 15);
    }

    [Fact]
    public void RestOnly_EmptyCallBindsEmptyStream()
        // An empty call binds an empty item stream (min arity 0): sum is 0.
        => AssertAtoms("G(x...) = x.sum\nG()", 0);

    [Fact]
    public void RestWithSuffix_AllCallFormsConsumeSameItemStream()
    {
        // x = (1, 2, 3, 4), y = 5; x.sum + y = 15 for every supply form.
        const string f = "A = 1, 2, 3, 4, 5\nF(x..., y) = x.sum + y\n";
        AssertAtoms(f + "F(A)", 15);
        AssertAtoms(f + "F(A...)", 15);
        AssertAtoms("F(x..., y) = x.sum + y\nF(1, 2, 3, 4, 5)", 15);
        AssertAtoms("F(x..., y) = x.sum + y\nF((1, 2, 3, 4, 5))", 15);
    }

    [Fact]
    public void RestWithPrefixAndSuffix_AllCallFormsConsumeSameItemStream()
    {
        const string h = "A = 1, 2, 3, 4, 5\nH(x, y..., z) = x + y.sum + z\n";
        AssertAtoms(h + "H(A)", 15);
        AssertAtoms(h + "H(A...)", 15);
        AssertAtoms("H(x, y..., z) = x + y.sum + z\nH(1, 2, 3, 4, 5)", 15);
        AssertAtoms("H(x, y..., z) = x + y.sum + z\nH((1, 2, 3, 4, 5))", 15);
    }

    [Fact]
    public void SiblingGroupedValues_ArePreservedUnlessExplicitlyOpened()
    {
        // Multiple sibling grouped values are preserved (count 2), not flattened.
        AssertAtoms("A = 1, 2\nB = 3, 4\nG(x...) = x.count\nG(A, B)", 2);
        // Only explicit `...` opens them into one stream (count 4).
        AssertAtoms("A = 1, 2\nB = 3, 4\nG(x...) = x.count\nG(A..., B...)", 4);
    }

    [Fact]
    public void RepeatedSingletonBoundary_IsNormalizedThroughNesting()
    {
        // Singleton-boundary normalization repeats through nested grouped values:
        // (((1, 2, 3, 4, 5))) is opened twice down to the same five-item stream.
        // Rest-only, rest+suffix, and prefix+rest+suffix all reach 15.
        AssertAtoms("G(x...) = x.sum\nG(((1, 2, 3, 4, 5)))", 15);
        AssertAtoms("F(x..., y) = x.sum + y\nF(((1, 2, 3, 4, 5)))", 15);
        AssertAtoms("H(x, y..., z) = x + y.sum + z\nH(((1, 2, 3, 4, 5)))", 15);
    }

    [Fact]
    public void CallbackSequenceValueDeconstruction_OnScalarElement_StaysStrict()
        // Callback deconstruction is deferred: the counted callback path keeps the
        // strict singleton-only scalar fallback (matching Lean), so a sequence-value
        // deconstruction callback applied to scalar map elements fails instead of
        // silently deconstructing each scalar into first/tail.
        => Assert.True(Fails("F((first, tail...)) = first, tail.count\nmap((1, 2, 3), F)"));

    [Fact]
    public void CallbackDeconstruction_OnSequenceValueRows_BindsPerRow()
    {
        // A deconstruction-shaped callback applied per sequence-value row binds
        // x/y.../z within each row: (1, 2, 3) -> 1 + 2 + 3 = 6 and
        // (4, 5, 6) -> 4 + 5 + 6 = 15. The flat and sequence-value parameter
        // forms agree here. This pins the deferred callback boundary: row
        // callbacks work while scalar-element deconstruction stays strict above.
        AssertAtoms("Rows = (1, 2, 3), (4, 5, 6)\nF(x, y..., z) = x + y.sum + z\nRows.map(F)", 6, 15);
        AssertAtoms("Rows = (1, 2, 3), (4, 5, 6)\nF((x, y..., z)) = x + y.sum + z\nRows.map(F)", 6, 15);
    }
}
