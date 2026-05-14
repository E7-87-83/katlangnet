namespace KatLang.Tests;

public class SequenceSupplyTests
{
    private static EvalResult<IReadOnlyList<decimal>> Eval(string source)
    {
        var parseResult = Parser.Parse(source);
        if (parseResult.HasErrors)
        {
            var message = string.Join(Environment.NewLine, parseResult.Diagnostics.Select(static diagnostic => diagnostic.Message));
            Assert.Fail($"Expected parse success but got diagnostics:{Environment.NewLine}{message}");
        }

        return Evaluator.RunFlat(new Expr.Block(parseResult.Root));
    }

    private static EvalResult<Result> EvalFull(string source)
    {
        var parseResult = Parser.Parse(source);
        if (parseResult.HasErrors)
        {
            var message = string.Join(Environment.NewLine, parseResult.Diagnostics.Select(static diagnostic => diagnostic.Message));
            Assert.Fail($"Expected parse success but got diagnostics:{Environment.NewLine}{message}");
        }

        return Evaluator.Run(new Expr.Block(parseResult.Root));
    }

    private static void AssertEval(string source, params decimal[] expected)
    {
        var result = Eval(source);
        if (result.IsError)
            Assert.Fail($"Expected success but got error: {result.Error}");

        Assert.Equal(expected, result.Value);
    }

    private static EvalError Innermost(EvalError error)
        => error is EvalError.WithContext(_, var inner) ? Innermost(inner) : error;

    private static void AssertArityFailure(string source)
    {
        var result = EvalFull(source);
        if (result.IsOk)
            Assert.Fail($"Expected evaluation failure but got: {result.Value}");

        Assert.True(
            Innermost(result.Error) is EvalError.ArityMismatch or EvalError.BadArity,
            $"Expected arity-shaped failure but got: {result.Error}");
    }

    private static void AssertEvaluationFailure(string source)
    {
        var result = EvalFull(source);
        if (result.IsOk)
            Assert.Fail($"Expected evaluation failure but got: {result.Value}");
    }

    [Fact]
    public void BasicSequenceSupply_MultiOutputPropertySuppliesFixedCallArguments()
        => AssertEval(
            """
            Pair = 10, 20
            Add(x, y) = x + y
            Add(Pair...)
            """,
            30m);

    [Fact]
    public void BasicSequenceSupply_GroupContentSuppliesFixedCallArguments()
        => AssertEval(
            """
            Pair = (10, 20)
            Add(x, y) = x + y
            Add(Pair.content...)
            """,
            30m);

    [Fact]
    public void BasicSequenceSupply_GroupWithoutContentSuppliesOneGroupedItem()
        => AssertArityFailure(
            """
            Pair = (10, 20)
            Add(x, y) = x + y
            Add(Pair...)
            """);

    [Fact]
    public void NormalCallArgument_DoesNotImplicitlySpreadMultiOutputProperty()
        => AssertArityFailure(
            """
            Pair = 10, 20
            Add(x, y) = x + y
            Add(Pair)
            """);

    [Fact]
    public void NormalCallArgument_ContentDoesNotSupplyByItself()
        => AssertArityFailure(
            """
            Pair = (10, 20)
            Add(x, y) = x + y
            Add(Pair.content)
            """);

    [Fact]
    public void PartialSequenceSupply_SuppliesTailArguments()
        => AssertEval(
            """
            Tail = 2, 3
            Use(a, b, c) = a + b + c
            Use(1, Tail...)
            """,
            6m);

    [Fact]
    public void MultipleSequenceSupplySegments_SupplyAroundNormalArgument()
        => AssertEval(
            """
            Head = 1, 2
            Tail = 4, 5
            Use(a, b, c, d, e) = a + b + c + d + e
            Use(Head..., 3, Tail...)
            """,
            15m);

    [Theory]
    [InlineData("Sum(Values...7)")]
    [InlineData("Sum(Values ... 7)")]
    public void SequenceSupplyInsideCall_SuppliesResultSequence(string call)
        => AssertEval(
            $$"""
            Values = 10, 20
            Sum(values...) = values.sum
            {{call}}
            """,
            37m);

    [Fact]
    public void VariadicSuffixBinding_CommaSeparatedSupplySegmentBindsPrefixAndSuffix()
        => AssertEval(
            """
            Values = 10, 20
            Sum(values..., val) = values.sum + val
            Sum(Values..., 7)
            """,
            37m);

    [Fact]
    public void VariadicSuffixBinding_SequenceSupplySegmentBindsPrefixAndSuffix()
        => AssertEval(
            """
            Values = 10, 20
            Sum(values..., val) = values.sum + val
            Sum(Values...7)
            """,
            37m);

    [Fact]
    public void VariadicSuffixBinding_NormalArgumentSuppliesOnlyVariadicSlot()
        => AssertEval(
            """
            Values = 10, 20
            Sum(values..., val) = values.sum + val
            Sum(Values, 7)
            """,
            37m);

    [Fact]
    public void VariadicSuffixBinding_NormalArgumentDoesNotExpandToSatisfySuffix()
        => AssertEvaluationFailure(
            """
            Values = 10, 20
            Sum(values..., val) = values.sum + val
            Sum(Values)
            """);

    [Fact]
    public void VariadicSuffixBinding_ExplicitSupplyCanSatisfySuffix()
        => AssertEval(
            """
            Values = 10, 20
            Sum(values..., val) = values.sum + val
            Sum(Values...)
            """,
            30m);

    [Fact]
    public void FlatVariadicSlotSupply_QmeanNormalCallMatchesExplicitSupply()
        => AssertEval(
            """
            Vector = range(1, 10)
            Qmean(args...) = Math.Sqrt(args.map{x * x}.sum / args.count)
            Qmean(Vector) == Qmean(Vector...)
            """,
            1m);

    [Fact]
    public void FlatVariadicSlotSupply_QmeanDotCallStillMatchesExplicitSupply()
        => AssertEval(
            """
            Vector = range(1, 10)
            Qmean(args...) = Math.Sqrt(args.map{x * x}.sum / args.count)
            Vector.Qmean() == Qmean(Vector...)
            """,
            1m);

    [Fact]
    public void FlatVariadicSlotSupply_MultiOutputPropertySuppliesTopLevelItems()
        => AssertEval(
            """
            Values = 10, 20
            Count(args...) = args.count
            Count(Values)
            """,
            2m);

    [Fact]
    public void FlatVariadicSlotSupply_VisibleGroupRemainsOneItem()
        => AssertEval(
            """
            Pair = (10, 20)
            Count(args...) = args.count
            Count(Pair)
            """,
            1m);

    [Fact]
    public void VariadicParameterForwarding_DirectCallSuppliesCompatibleVariadicSlot()
        => AssertEval(
            """
            CountItem(values..., item) = values.filter{value == item}.count
            Use(values...) = CountItem(values, 1)
            Use(1, 1, 2, 4, 4)
            """,
            2m);

    [Fact]
    public void VariadicParameterForwarding_CallbackBodySuppliesCompatibleVariadicSlot()
        => AssertEval(
            """
            CountItem(values..., item) = values.filter{value == item}.count

            Mode(values...) = {
                Freqs = values.distinct.map{CountItem(values, candidate)}
                Freqs
            }

            Mode(1, 1, 2, 4, 4)
            """,
            2m, 1m, 2m);

    [Fact]
    public void VariadicParameterForwarding_FullModeExampleSuppliesCompatibleVariadicSlot()
        => AssertEval(
            """
            CountItem(values..., item) = values.filter{value == item}.count

            Mode(values...) = {
                Freqs = values.distinct.map{CountItem(values, candidate)}
                MaxFreq = Freqs.max

                values.distinct.filter{CountItem(values, candidate) == MaxFreq}
            }

            Mode(1, 1, 2, 4, 4)
            """,
            1m, 4m);

    [Fact]
    public void VariadicParameterForwarding_NonVariadicCalleeStillReceivesOneGroupedValue()
        => AssertEval(
            """
            Group(list) = list.count
            Use(values...) = Group(values)
            Use(10, 20, 30)
            """,
            1m);

    [Fact]
    public void VariadicParameterForwarding_CompatibleTopLevelVariadicCalleeReceivesStream()
        => AssertEval(
            """
            Group(list...) = list.count
            Use(values...) = Group(values)
            Use(10, 20, 30)
            """,
            3m);

    [Fact]
    public void VariadicParameterForwarding_TopLevelCaptureStillSuppliesCompatibleVariadicSlot()
        => AssertEval(
            """
            CountItems(items...) = items.count
            Use(values...) = CountItems(values)
            Use(1, 2, 3)
            """,
            3m);

    [Fact]
    public void VariadicParameterForwarding_GroupedVariadicPatternKeepsGroupedBindingBehavior()
        => AssertEval(
            """
            CountGroup((values...)) = values.count
            Use(values...) = CountGroup(values)
            Use(10, 20, 30)
            """,
            3m);

    [Fact]
    public void VariadicParameterForwarding_GroupedVariadicCaptureSuppliesCompatibleVariadicSlot()
        => AssertEval(
            """
            FindNext(history..., pre1, pre2) = history.count + pre1 + pre2
            YSStep((history...), pre2, pre1) = FindNext(history, pre1, pre2)
            YSStep((1, 2, 3), 2, 3)
            """,
            8m);

    [Fact]
    public void VariadicParameterForwarding_GroupedCaptureStillSuppliesCompatibleVariadicSlot()
        => AssertEval(
            """
            CountItems(items...) = items.count
            Use((history...)) = CountItems(history)
            Use((1, 2, 3))
            """,
            3m);

    [Fact]
    public void GroupedVariadicCalleeBoundary_DoesNotUseFlatSlotSupply()
        => AssertEval(
            """
            CountGroup((items...)) = items.count
            Pair = 10, 20
            CountGroup(Pair)
            """,
            2m);

    [Fact]
    public void VariadicParameterForwarding_GroupedVariadicCaptureForwardsByProvenanceNotName()
        => AssertEval(
            """
            CountItems(items..., last) = items.count + last
            Use((history...), last) = CountItems(history, last)
            Use((10, 20, 30), 7)
            """,
            10m);

    [Fact]
    public void VariadicParameterForwarding_GroupedVariadicCaptureKeepsNonVariadicCalleeBoundary()
        => AssertEval(
            """
            Group(list) = list.count
            Use((history...), marker) = Group(history)
            Use((10, 20, 30), 99)
            """,
            1m);

    [Fact]
    public void VariadicParameterForwarding_GroupedVariadicCaptureOnlyExpandsInTargetVariadicSlot()
        => AssertEval(
            """
            TakeLast(first..., last) = first.count
            Use((history...), marker) = TakeLast(0, history)
            Use((10, 20, 30), 99)
            """,
            1m);

    [Fact]
    public void VariadicParameterForwarding_LoopStepGroupedVariadicCaptureSuppliesCompatibleVariadicSlot()
        => AssertEval(
            """
            FindNext(history..., pre1, pre2) = history.count + pre1 + pre2
            YSStep((history...), pre2, pre1) = FindNext(history, pre1, pre2), pre1, pre2
            YSStep.repeat(1, (1, 2, 3), 2, 3):0
            """,
            8m);

    [Fact]
    public void SequenceBuiltin_NormalArgumentContributesOneGroupedItem()
        => AssertEval(
            """
            Values = 10, 20
            count(Values)
            """,
            1m);

    [Fact]
    public void SequenceBuiltin_ExplicitSupplyContributesTopLevelItems()
        => AssertEval(
            """
            Values = 10, 20
            count(Values...)
            """,
            2m);

    [Fact]
    public void SequenceBuiltin_NumericNormalArgumentDoesNotImplicitlySupplyMultiOutputProperty()
        => AssertArityFailure(
            """
            Values = 10, 20
            sum(Values)
            """);

    [Fact]
    public void SequenceBuiltin_NumericExplicitSupplyConsumesTopLevelItems()
        => AssertEval(
            """
            Values = 10, 20
            sum(Values...)
            """,
            30m);

    [Fact]
    public void FixedBuiltin_ExplicitSupplyProvidesArguments()
        => AssertEval(
            """
            Bounds = 1, 3
            range(Bounds...)
            """,
            1m, 2m, 3m);

    [Fact]
    public void NonCallResultContext_CommaPreservesNestedBlockBoundary()
    {
        var result = EvalFull(
            """
            A = 1, { 2, 3 }
            A
            """);

        if (result.IsError)
            Assert.Fail($"Expected success but got error: {result.Error}");

        var group = Assert.IsType<Result.Group>(result.Value);
        Assert.Equal(2, group.Items.Count);
        Assert.Equal(1m, Assert.IsType<Result.Atom>(group.Items[0]).Value);
        var nested = Assert.IsType<Result.Group>(group.Items[1]);
        Assert.Equal([2m, 3m], nested.Items.Select(static item => Assert.IsType<Result.Atom>(item).Value).ToArray());
    }

    [Theory]
    [InlineData("A = 1...{ 2, 3 }")]
    [InlineData("A = 1 ... { 2, 3 }")]
    public void NonCallResultContext_SequenceSupplySuppliesNestedBlockOutput(string definition)
        => AssertEval(
            $$"""
            {{definition}}
            A
            """,
            1m, 2m, 3m);

    [Theory]
    [InlineData("(Values...).Sum")]
    [InlineData("(Values...7).Sum")]
    [InlineData("(Values ... 7).Sum")]
    public void DotCall_SequenceSuppliedReceiverBindsTopLevelVariadicFirstParameter(string call)
        => AssertEval(
            $$"""
            Values = 10, 20
            Sum(values...) = values.sum
            {{call}}
            """,
            call.Contains('7', StringComparison.Ordinal) ? 37m : 30m);

    [Theory]
    [InlineData("(Pair.content...).Sum", 30)]
    [InlineData("(Pair.content...7).Sum", 37)]
    public void DotCall_GroupContentSequenceSuppliedReceiverBindsTopLevelVariadicFirstParameter(string call, decimal expected)
        => AssertEval(
            $$"""
            Pair = (10, 20)
            Sum(values...) = values.sum
            {{call}}
            """,
            expected);

    [Fact]
    public void DotCall_SequenceSuppliedReceiverDoesNotSpreadIntoFixedParameters()
        => AssertArityFailure(
            """
            Pair = 10, 20
            Add(x, y) = x + y
            (Pair...).Add
            """);

    [Fact]
    public void DotCall_ContentSequenceSuppliedReceiverDoesNotSpreadIntoFixedParameters()
        => AssertArityFailure(
            """
            Pair = (10, 20)
            Add(x, y) = x + y
            (Pair.content...).Add
            """);

    [Fact]
    public void OldSemicolonSyntax_IsParseError()
    {
        var parseResult = Parser.ParseSyntax("1; 2");

        Assert.True(parseResult.HasErrors);
    }
}
