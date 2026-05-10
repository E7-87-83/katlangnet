namespace KatLang.Tests;

public class CallableBindingPlanQueryTests
{
    private static CallableBindingPlan PlanFor(string source, string name)
    {
        var parseResult = Parser.Parse(source);
        Assert.False(
            parseResult.HasErrors,
            string.Join(Environment.NewLine, parseResult.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var property = parseResult.Root.Properties.Single(property => property.Name == name);
        return CallableBindingPlan.FromSignature(CallableSignature.FromAlgorithm(name, property.Value));
    }

    private static void AssertFlatFixedLayout(
        CallableBindingPlan plan,
        params (string Name, CallableParameterSource Source)[] expected)
    {
        Assert.True(plan.TryGetFlatFixedLayout(out var captures));
        Assert.Equal(expected.Select(static item => item.Name).ToArray(), captures.Select(static capture => capture.Name).ToArray());
        Assert.Equal(expected.Select(static item => item.Source).ToArray(), captures.Select(static capture => capture.Source).ToArray());
    }

    private static void AssertFlatVariadicLayout(
        CallableBindingPlan plan,
        string[] expectedPrefixNames,
        string variadicName,
        CallableParameterSource variadicSource,
        string[] expectedSuffixNames,
        CallableParameterSource suffixSource)
    {
        Assert.True(plan.TryGetFlatVariadicLayout(out var prefix, out var variadic, out var suffix));
        Assert.Equal(expectedPrefixNames, prefix.Select(static capture => capture.Name).ToArray());
        Assert.Equal(variadicName, variadic.Name);
        Assert.Equal(variadicSource, variadic.Source);
        Assert.True(variadic.IsTopLevel);
        Assert.Equal(expectedSuffixNames, suffix.Select(static capture => capture.Name).ToArray());
        Assert.All(suffix, capture => Assert.Equal(suffixSource, capture.Source));
    }

    private static void AssertQueryFacts(
        CallableBindingPlan plan,
        bool requiresPatternedBinding,
        bool hasOnlyFlatTopLevelCaptures,
        bool hasOnlyFlatFixedTopLevelCaptures,
        bool hasTopLevelVariadic,
        bool hasNestedVariadic,
        int min,
        int? max)
    {
        Assert.Equal(requiresPatternedBinding, plan.RequiresPatternedBinding);
        Assert.Equal(hasOnlyFlatTopLevelCaptures, plan.HasOnlyFlatTopLevelCaptures);
        Assert.Equal(hasOnlyFlatFixedTopLevelCaptures, plan.HasOnlyFlatFixedTopLevelCaptures);
        Assert.Equal(hasTopLevelVariadic, plan.HasTopLevelVariadic);
        Assert.Equal(hasNestedVariadic, plan.HasNestedVariadic);
        Assert.Equal(hasTopLevelVariadic, plan.TopLevelVariadicCapture is not null);
        Assert.Equal(min, plan.ArityFacts.MinTopLevelArgumentCount);
        Assert.Equal(max, plan.ArityFacts.MaxTopLevelArgumentCount);
    }

    [Fact]
    public void ExplicitScalarFixedLayout_SucceedsAsFlatFixed()
    {
        var plan = PlanFor("Add(x, y) = x + y", "Add");

        AssertQueryFacts(
            plan,
            requiresPatternedBinding: false,
            hasOnlyFlatTopLevelCaptures: true,
            hasOnlyFlatFixedTopLevelCaptures: true,
            hasTopLevelVariadic: false,
            hasNestedVariadic: false,
            min: 2,
            max: 2);
        AssertFlatFixedLayout(plan, ("x", CallableParameterSource.Explicit), ("y", CallableParameterSource.Explicit));
        Assert.False(plan.TryGetFlatVariadicLayout(out _, out _, out _));
    }

    [Fact]
    public void ImplicitScalarFixedLayout_SucceedsAsFlatFixedWithImplicitSources()
    {
        var plan = PlanFor("Add = x + y", "Add");

        AssertQueryFacts(
            plan,
            requiresPatternedBinding: false,
            hasOnlyFlatTopLevelCaptures: true,
            hasOnlyFlatFixedTopLevelCaptures: true,
            hasTopLevelVariadic: false,
            hasNestedVariadic: false,
            min: 2,
            max: 2);
        AssertFlatFixedLayout(plan, ("x", CallableParameterSource.Implicit), ("y", CallableParameterSource.Implicit));
    }

    [Fact]
    public void GroupedExplicitLayout_RequiresPatternedBinding()
    {
        var plan = PlanFor("PairSum((x, y)) = x + y", "PairSum");

        AssertQueryFacts(
            plan,
            requiresPatternedBinding: true,
            hasOnlyFlatTopLevelCaptures: false,
            hasOnlyFlatFixedTopLevelCaptures: false,
            hasTopLevelVariadic: false,
            hasNestedVariadic: false,
            min: 1,
            max: 1);
        Assert.False(plan.TryGetFlatFixedLayout(out _));
        Assert.False(plan.TryGetFlatVariadicLayout(out _, out _, out _));
    }

    [Fact]
    public void TopLevelVariadicLayout_SucceedsAsFlatVariadic()
    {
        var plan = PlanFor("CountValues(values...) = values.count", "CountValues");

        AssertQueryFacts(
            plan,
            requiresPatternedBinding: false,
            hasOnlyFlatTopLevelCaptures: true,
            hasOnlyFlatFixedTopLevelCaptures: false,
            hasTopLevelVariadic: true,
            hasNestedVariadic: false,
            min: 0,
            max: null);
        Assert.NotNull(plan.TopLevelVariadicCapture);
        Assert.Equal("values", plan.TopLevelVariadicCapture.Name);
        Assert.False(plan.TryGetFlatFixedLayout(out _));
        AssertFlatVariadicLayout(plan, [], "values", CallableParameterSource.Explicit, [], CallableParameterSource.Explicit);
    }

    [Fact]
    public void VariadicSuffixLayout_ReturnsFlatSuffixCaptures()
    {
        var plan = PlanFor("Scale(items..., factor) = items.map{n * factor}", "Scale");

        AssertQueryFacts(
            plan,
            requiresPatternedBinding: false,
            hasOnlyFlatTopLevelCaptures: true,
            hasOnlyFlatFixedTopLevelCaptures: false,
            hasTopLevelVariadic: true,
            hasNestedVariadic: false,
            min: 1,
            max: null);
        AssertFlatVariadicLayout(plan, [], "items", CallableParameterSource.Explicit, ["factor"], CallableParameterSource.Explicit);
    }

    [Fact]
    public void GroupedVariadicLayout_IsNestedNotTopLevel()
    {
        var plan = PlanFor("CountGroup((values...)) = values.count", "CountGroup");

        AssertQueryFacts(
            plan,
            requiresPatternedBinding: true,
            hasOnlyFlatTopLevelCaptures: false,
            hasOnlyFlatFixedTopLevelCaptures: false,
            hasTopLevelVariadic: false,
            hasNestedVariadic: true,
            min: 1,
            max: 1);
        Assert.Null(plan.TopLevelVariadicCapture);
        Assert.False(plan.TryGetFlatFixedLayout(out _));
        Assert.False(plan.TryGetFlatVariadicLayout(out _, out _, out _));
    }

    [Fact]
    public void MixedPatternedAndTopLevelVariadicLayout_RequiresPatternedBindingFirst()
    {
        var plan = PlanFor("F((inner...), outer...) = inner; outer", "F");

        AssertQueryFacts(
            plan,
            requiresPatternedBinding: true,
            hasOnlyFlatTopLevelCaptures: false,
            hasOnlyFlatFixedTopLevelCaptures: false,
            hasTopLevelVariadic: true,
            hasNestedVariadic: true,
            min: 1,
            max: null);
        Assert.NotNull(plan.TopLevelVariadicCapture);
        Assert.Equal("outer", plan.TopLevelVariadicCapture.Name);
        Assert.False(plan.TryGetFlatFixedLayout(out _));
        Assert.False(plan.TryGetFlatVariadicLayout(out _, out _, out _));
    }

    [Fact]
    public void NestedGroupedRecursiveLayout_PreservesNestedVariadicFacts()
    {
        var plan = PlanFor("G(((history...), previous)) = history.count + previous", "G");

        AssertQueryFacts(
            plan,
            requiresPatternedBinding: true,
            hasOnlyFlatTopLevelCaptures: false,
            hasOnlyFlatFixedTopLevelCaptures: false,
            hasTopLevelVariadic: false,
            hasNestedVariadic: true,
            min: 1,
            max: 1);
        Assert.Equal(["history", "previous"], plan.Captures.Select(static capture => capture.Name).ToArray());
        Assert.False(plan.TryGetFlatFixedLayout(out _));
        Assert.False(plan.TryGetFlatVariadicLayout(out _, out _, out _));
    }

    [Fact]
    public void BuiltinMapLayout_SucceedsAsFlatVariadicWithBuiltinSources()
    {
        var plan = CallableBindingPlan.FromSignature(CallableSignature.FromBuiltin(BuiltinId.map));

        AssertQueryFacts(
            plan,
            requiresPatternedBinding: false,
            hasOnlyFlatTopLevelCaptures: true,
            hasOnlyFlatFixedTopLevelCaptures: false,
            hasTopLevelVariadic: true,
            hasNestedVariadic: false,
            min: 1,
            max: null);
        AssertFlatVariadicLayout(plan, [], "values", CallableParameterSource.Builtin, ["mapper"], CallableParameterSource.Builtin);
    }

    [Theory]
    [InlineData(BuiltinId.filter, "predicate")]
    [InlineData(BuiltinId.take, "count")]
    [InlineData(BuiltinId.skip, "count")]
    public void BuiltinSuffixSequenceLayouts_SucceedAsFlatVariadicWithOneSuffix(
        BuiltinId builtin,
        string suffixName)
    {
        var plan = CallableBindingPlan.FromSignature(CallableSignature.FromBuiltin(builtin));

        AssertQueryFacts(
            plan,
            requiresPatternedBinding: false,
            hasOnlyFlatTopLevelCaptures: true,
            hasOnlyFlatFixedTopLevelCaptures: false,
            hasTopLevelVariadic: true,
            hasNestedVariadic: false,
            min: 1,
            max: null);
        AssertFlatVariadicLayout(plan, [], "values", CallableParameterSource.Builtin, [suffixName], CallableParameterSource.Builtin);
    }

    [Theory]
    [InlineData(BuiltinId.count)]
    [InlineData(BuiltinId.sum)]
    public void BuiltinUnsuffixedSequenceLayouts_SucceedAsFlatVariadicWithoutSuffix(BuiltinId builtin)
    {
        var plan = CallableBindingPlan.FromSignature(CallableSignature.FromBuiltin(builtin));

        AssertQueryFacts(
            plan,
            requiresPatternedBinding: false,
            hasOnlyFlatTopLevelCaptures: true,
            hasOnlyFlatFixedTopLevelCaptures: false,
            hasTopLevelVariadic: true,
            hasNestedVariadic: false,
            min: 0,
            max: null);
        AssertFlatVariadicLayout(plan, [], "values", CallableParameterSource.Builtin, [], CallableParameterSource.Builtin);
    }

    [Fact]
    public void LoopStepShapeQueries_DescribeSignaturesWithoutMigratingLoopBinding()
    {
        var flat = PlanFor("Step(a, b) = b, a + b, 1", "Step");
        AssertQueryFacts(
            flat,
            requiresPatternedBinding: false,
            hasOnlyFlatTopLevelCaptures: true,
            hasOnlyFlatFixedTopLevelCaptures: true,
            hasTopLevelVariadic: false,
            hasNestedVariadic: false,
            min: 2,
            max: 2);
        AssertFlatFixedLayout(flat, ("a", CallableParameterSource.Explicit), ("b", CallableParameterSource.Explicit));

        var variadic = PlanFor("Step(values...) = values; 1", "Step");
        AssertQueryFacts(
            variadic,
            requiresPatternedBinding: false,
            hasOnlyFlatTopLevelCaptures: true,
            hasOnlyFlatFixedTopLevelCaptures: false,
            hasTopLevelVariadic: true,
            hasNestedVariadic: false,
            min: 0,
            max: null);
        AssertFlatVariadicLayout(variadic, [], "values", CallableParameterSource.Explicit, [], CallableParameterSource.Explicit);

        var grouped = PlanFor("Step((x, y)) = x + y, 0", "Step");
        AssertQueryFacts(
            grouped,
            requiresPatternedBinding: true,
            hasOnlyFlatTopLevelCaptures: false,
            hasOnlyFlatFixedTopLevelCaptures: false,
            hasTopLevelVariadic: false,
            hasNestedVariadic: false,
            min: 1,
            max: 1);
        Assert.False(grouped.TryGetFlatFixedLayout(out _));
        Assert.False(grouped.TryGetFlatVariadicLayout(out _, out _, out _));
    }
}