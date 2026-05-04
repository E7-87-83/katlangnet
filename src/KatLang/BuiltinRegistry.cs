namespace KatLang;

internal enum BuiltinCallStyle
{
    Plain,
    Dot,
}

internal enum SequenceBuiltinSuffixArgKind
{
    Algorithm,
    Value,
    WholeNumber,
}

internal readonly record struct CallableParameter(
    string Name,
    ParameterKind Kind = ParameterKind.Normal)
{
    public string DisplayName => Kind == ParameterKind.Variadic ? $"{Name}..." : Name;
}

internal readonly record struct CallableSignature(
    string Name,
    IReadOnlyList<CallableParameter> Parameters)
{
    public int RequiredNormalParameterCount => Parameters.Count(static parameter => parameter.Kind == ParameterKind.Normal);

    public int VariadicParameterCount => Parameters.Count(static parameter => parameter.Kind == ParameterKind.Variadic);

    public bool HasAtMostOneVariadic => VariadicParameterCount <= 1;

    public int VariadicParameterIndex
    {
        get
        {
            for (var index = 0; index < Parameters.Count; index++)
            {
                if (Parameters[index].Kind == ParameterKind.Variadic)
                    return index;
            }

            return -1;
        }
    }

    public bool HasVariadicParameter => VariadicParameterIndex >= 0;

    public bool AcceptsItemCount(int itemCount)
        => HasVariadicParameter
            ? itemCount >= RequiredNormalParameterCount
            : itemCount == Parameters.Count;

    public string DisplayText => Parameters.Count == 0
        ? Name
        : $"{Name}({string.Join(", ", Parameters.Select(static parameter => parameter.DisplayName))})";

    public string? ValidateMessage()
    {
        if (!HasAtMostOneVariadic)
            return $"Callable signature `{Name}` cannot contain more than one variadic parameter.";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in Parameters)
        {
            if (parameter.Name.Length == 0)
                return $"Callable signature `{Name}` contains an empty parameter name.";

            if (!IsIdentifierLike(parameter.Name))
                return $"Callable signature `{Name}` contains invalid parameter name `{parameter.Name}`.";

            if (!seen.Add(parameter.Name))
                return $"Callable signature `{Name}` contains duplicate parameter name `{parameter.Name}`.";
        }

        return null;
    }

    public EvalError? Validate()
        => ValidateMessage() is { } message ? new EvalError.IllegalInEval(message) : null;

    public void ValidateOrThrow()
    {
        if (ValidateMessage() is { } message)
            throw new InvalidOperationException(message);
    }

    private static bool IsIdentifierLike(string name)
    {
        if (name.Length == 0 || (!char.IsLetter(name[0]) && name[0] != '_'))
            return false;

        return name.Skip(1).All(static c => char.IsLetterOrDigit(c) || c == '_');
    }
}

internal readonly record struct SequenceBuiltinSuffixArgDescriptor(
    string Name,
    SequenceBuiltinSuffixArgKind Kind = SequenceBuiltinSuffixArgKind.Algorithm);

internal enum SequenceBuiltinEmptyPolicy
{
    AllowEmpty,
    RequireAnyItem,
    RequireEachInputNonEmpty,
}

internal enum SequenceBuiltinItemShapeConstraint
{
    Any,
    SingleNumeric,
}

internal readonly record struct SequenceBuiltinMetadata(
    IReadOnlyList<SequenceBuiltinSuffixArgDescriptor> SuffixArgs,
    SequenceBuiltinEmptyPolicy EmptyPolicy,
    SequenceBuiltinItemShapeConstraint ItemShapeConstraint)
{
    public IReadOnlyList<CallableParameter> Parameters { get; } = CreateParameters(SuffixArgs);

    private static IReadOnlyList<CallableParameter> CreateParameters(
        IReadOnlyList<SequenceBuiltinSuffixArgDescriptor> suffixArgs)
    {
        var parameters = new List<CallableParameter>(suffixArgs.Count + 1)
        {
            new("values", ParameterKind.Variadic),
        };

        parameters.AddRange(suffixArgs.Select(static descriptor => new CallableParameter(descriptor.Name)));
        return parameters;
    }
}

internal enum MathMemberKind
{
    Constant,
    UnaryFunction,
    BinaryFunction,
}

internal readonly record struct MathMemberDescriptor(string Name, MathMemberKind Kind, decimal ConstantValue = 0m)
{
    public int Arity => Kind switch
    {
        MathMemberKind.Constant => 0,
        MathMemberKind.UnaryFunction => 1,
        MathMemberKind.BinaryFunction => 2,
        _ => throw new InvalidOperationException($"Unsupported Math member kind '{Kind}'."),
    };
}

internal sealed class BuiltinDescriptor
{
    public BuiltinDescriptor(
        BuiltinId id,
        int? fixedArity,
        IReadOnlyList<CallableParameter> plainParameters,
        IReadOnlyList<CallableParameter> dotParameters,
        SequenceBuiltinMetadata? sequenceMetadata = null)
    {
        Id = id;
        Name = id.ToString();
        FixedArity = fixedArity;
        PlainParameters = plainParameters;
        DotParameters = dotParameters;
        PlainParameterNames = plainParameters.Select(static parameter => parameter.DisplayName).ToArray();
        DotParameterNames = dotParameters.Select(static parameter => parameter.DisplayName).ToArray();
        PlainSignature = new CallableSignature(Name, plainParameters);
        DotSignature = new CallableSignature(Name, dotParameters);
        PlainSignature.ValidateOrThrow();
        DotSignature.ValidateOrThrow();
        SequenceMetadata = sequenceMetadata;
    }

    public BuiltinId Id { get; }

    public string Name { get; }

    public int? FixedArity { get; }

    public CallableSignature PlainSignature { get; }

    public CallableSignature DotSignature { get; }

    public IReadOnlyList<CallableParameter> PlainParameters { get; }

    public IReadOnlyList<CallableParameter> DotParameters { get; }

    public IReadOnlyList<string> PlainParameterNames { get; }

    public IReadOnlyList<string> DotParameterNames { get; }

    public SequenceBuiltinMetadata? SequenceMetadata { get; }

    public bool AcceptsArity(int count)
    {
        if (SequenceMetadata is { } metadata)
        {
            return PlainSignature.AcceptsItemCount(count);
        }

        return FixedArity == count;
    }

    public string DescribeArity()
    {
        if (SequenceMetadata is { } metadata)
        {
            var totalArgCountDesc = BuiltinRegistry.DescribeSequenceBuiltinTotalArgs(PlainSignature);
            if (metadata.SuffixArgs.Count == 0)
                return totalArgCountDesc;

            return $"{totalArgCountDesc} arguments ({PlainSignature.DisplayText})";
        }

        return FixedArity?.ToString() ?? "?";
    }

    public IReadOnlyList<string> GetParameterNames(BuiltinCallStyle callStyle)
        => callStyle == BuiltinCallStyle.Dot ? DotParameterNames : PlainParameterNames;

    public IReadOnlyList<CallableParameter> GetParameters(BuiltinCallStyle callStyle)
        => callStyle == BuiltinCallStyle.Dot ? DotParameters : PlainParameters;

    public CallableSignature GetSignature(BuiltinCallStyle callStyle)
        => callStyle == BuiltinCallStyle.Dot ? DotSignature : PlainSignature;
}

internal enum MathAlgorithmFlavor
{
    Runtime,
    SignatureOnly,
}

internal static class BuiltinRegistry
{
    public const string EmptyBuiltinName = "empty";

    private static readonly SequenceBuiltinMetadata FilterSequenceMetadata =
        new([new("predicate")], SequenceBuiltinEmptyPolicy.AllowEmpty, SequenceBuiltinItemShapeConstraint.Any);

    private static readonly SequenceBuiltinMetadata MapSequenceMetadata =
        new([new("mapper")], SequenceBuiltinEmptyPolicy.AllowEmpty, SequenceBuiltinItemShapeConstraint.Any);

    private static readonly SequenceBuiltinMetadata OrderSequenceMetadata =
        new([], SequenceBuiltinEmptyPolicy.AllowEmpty, SequenceBuiltinItemShapeConstraint.SingleNumeric);

    private static readonly SequenceBuiltinMetadata OrderDescSequenceMetadata =
        new([], SequenceBuiltinEmptyPolicy.AllowEmpty, SequenceBuiltinItemShapeConstraint.SingleNumeric);

    private static readonly SequenceBuiltinMetadata CountSequenceMetadata =
        new([], SequenceBuiltinEmptyPolicy.AllowEmpty, SequenceBuiltinItemShapeConstraint.Any);

    private static readonly SequenceBuiltinMetadata ContainsSequenceMetadata =
        new([new("item", SequenceBuiltinSuffixArgKind.Value)], SequenceBuiltinEmptyPolicy.AllowEmpty, SequenceBuiltinItemShapeConstraint.Any);

    private static readonly SequenceBuiltinMetadata FirstSequenceMetadata =
        new([], SequenceBuiltinEmptyPolicy.RequireAnyItem, SequenceBuiltinItemShapeConstraint.Any);

    private static readonly SequenceBuiltinMetadata LastSequenceMetadata =
        new([], SequenceBuiltinEmptyPolicy.RequireAnyItem, SequenceBuiltinItemShapeConstraint.Any);

    private static readonly SequenceBuiltinMetadata DistinctSequenceMetadata =
        new([], SequenceBuiltinEmptyPolicy.AllowEmpty, SequenceBuiltinItemShapeConstraint.Any);

    private static readonly SequenceBuiltinMetadata TakeSequenceMetadata =
        new([new("count", SequenceBuiltinSuffixArgKind.WholeNumber)], SequenceBuiltinEmptyPolicy.AllowEmpty, SequenceBuiltinItemShapeConstraint.Any);

    private static readonly SequenceBuiltinMetadata SkipSequenceMetadata =
        new([new("count", SequenceBuiltinSuffixArgKind.WholeNumber)], SequenceBuiltinEmptyPolicy.AllowEmpty, SequenceBuiltinItemShapeConstraint.Any);

    private static readonly SequenceBuiltinMetadata MinSequenceMetadata =
        new([], SequenceBuiltinEmptyPolicy.RequireAnyItem, SequenceBuiltinItemShapeConstraint.SingleNumeric);

    private static readonly SequenceBuiltinMetadata MaxSequenceMetadata =
        new([], SequenceBuiltinEmptyPolicy.RequireAnyItem, SequenceBuiltinItemShapeConstraint.SingleNumeric);

    private static readonly SequenceBuiltinMetadata SumSequenceMetadata =
        new([], SequenceBuiltinEmptyPolicy.AllowEmpty, SequenceBuiltinItemShapeConstraint.SingleNumeric);

    private static readonly SequenceBuiltinMetadata AvgSequenceMetadata =
        new([], SequenceBuiltinEmptyPolicy.RequireAnyItem, SequenceBuiltinItemShapeConstraint.SingleNumeric);

    private static readonly SequenceBuiltinMetadata ReduceSequenceMetadata =
        new([new("reducer"), new("initial")], SequenceBuiltinEmptyPolicy.AllowEmpty, SequenceBuiltinItemShapeConstraint.Any);

    private static readonly BuiltinDescriptor[] Builtins =
    [
        Fixed(BuiltinId.@empty),
        Fixed(BuiltinId.@if, "condition", "whenTrue", "whenFalse"),
        Fixed(BuiltinId.@while, "step", "initialState"),
        Fixed(BuiltinId.@repeat, "step", "count", "initialState"),
        Fixed(BuiltinId.@atoms, "value"),
        Fixed(BuiltinId.@range, "start", "stop"),
        Sequence(BuiltinId.@filter, FilterSequenceMetadata),
        Sequence(BuiltinId.@map, MapSequenceMetadata),
        Sequence(BuiltinId.@order, OrderSequenceMetadata),
        Sequence(BuiltinId.@orderDesc, OrderDescSequenceMetadata),
        Sequence(BuiltinId.@count, CountSequenceMetadata),
        Sequence(BuiltinId.@contains, ContainsSequenceMetadata),
        Sequence(BuiltinId.@first, FirstSequenceMetadata),
        Sequence(BuiltinId.@last, LastSequenceMetadata),
        Sequence(BuiltinId.@distinct, DistinctSequenceMetadata),
        Sequence(BuiltinId.@take, TakeSequenceMetadata),
        Sequence(BuiltinId.@skip, SkipSequenceMetadata),
        Sequence(BuiltinId.@min, MinSequenceMetadata),
        Sequence(BuiltinId.@max, MaxSequenceMetadata),
        Sequence(BuiltinId.@sum, SumSequenceMetadata),
        Sequence(BuiltinId.@avg, AvgSequenceMetadata),
        Sequence(BuiltinId.@reduce, ReduceSequenceMetadata),
    ];

    private static readonly IReadOnlyDictionary<BuiltinId, BuiltinDescriptor> BuiltinsById =
        Builtins.ToDictionary(static descriptor => descriptor.Id);

    private static readonly MathMemberDescriptor[] MathMemberDescriptors =
    [
        new("Pi", MathMemberKind.Constant, 3.1415926535897932384626433833m),
        new("E", MathMemberKind.Constant, 2.7182818284590452353602874714m),
        new("Abs", MathMemberKind.UnaryFunction),
        new("Ceil", MathMemberKind.UnaryFunction),
        new("Floor", MathMemberKind.UnaryFunction),
        new("Round", MathMemberKind.UnaryFunction),
        new("Sign", MathMemberKind.UnaryFunction),
        new("Sqrt", MathMemberKind.UnaryFunction),
        new("Ln", MathMemberKind.UnaryFunction),
        new("Lg", MathMemberKind.UnaryFunction),
        new("Sin", MathMemberKind.UnaryFunction),
        new("Asin", MathMemberKind.UnaryFunction),
        new("Cos", MathMemberKind.UnaryFunction),
        new("Acos", MathMemberKind.UnaryFunction),
        new("Tan", MathMemberKind.UnaryFunction),
        new("Atan", MathMemberKind.UnaryFunction),
        new("Pow", MathMemberKind.BinaryFunction),
        new("Log", MathMemberKind.BinaryFunction),
    ];

    public static IReadOnlyList<BuiltinDescriptor> AllBuiltins => Builtins;

    public static IReadOnlyList<string> BuiltinNames { get; } = Builtins
        .Select(static descriptor => descriptor.Name)
        .ToArray();

    public static IReadOnlyList<string> RuntimePreludeExtraNames { get; } = ["Math"];

    public static IReadOnlyList<string> SemanticPreludeExtraNames { get; } = ["Math", "load"];

    public static IReadOnlyList<string> ParameterDetectorPreludeNames { get; } =
        BuiltinNames.Concat(SemanticPreludeExtraNames).ToArray();

    public static IReadOnlyList<MathMemberDescriptor> MathMembers => MathMemberDescriptors;

    public static IReadOnlyList<string> MathMemberNames { get; } = MathMemberDescriptors
        .Select(static member => member.Name)
        .ToArray();

    public static bool IsMathFunctionMember(string name)
        => MathMemberDescriptors.Any(member => member.Name == name && member.Kind != MathMemberKind.Constant);

    public static IReadOnlyList<string> LoadParameterNames { get; } = ["url"];

    public static BuiltinDescriptor GetBuiltin(BuiltinId builtin)
        => BuiltinsById[builtin];

    public static bool TryGetSequenceMetadata(BuiltinId builtin, out SequenceBuiltinMetadata metadata)
    {
        if (GetBuiltin(builtin).SequenceMetadata is { } sequenceMetadata)
        {
            metadata = sequenceMetadata;
            return true;
        }

        metadata = default;
        return false;
    }

    public static IReadOnlyList<string> GetBuiltinParameterNames(BuiltinId builtin, BuiltinCallStyle callStyle)
        => GetBuiltin(builtin).GetParameterNames(callStyle);

    public static IReadOnlyList<CallableParameter> GetBuiltinParameters(BuiltinId builtin, BuiltinCallStyle callStyle)
        => GetBuiltin(builtin).GetParameters(callStyle);

    public static Algorithm.User CreateMathAlgorithm(MathAlgorithmFlavor flavor)
        => new(
            Parent: null,
            Parameters: [],
            Opens: [],
            Properties: MathMemberDescriptors.Select(member => CreateMathProperty(member, flavor)).ToList(),
            Output: []);

    public static Algorithm.User CreateRuntimePreludeAlgorithm(Algorithm.User? mathAlgorithm = null)
        => CreatePreludeAlgorithm(includeLoad: false, mathAlgorithm ?? CreateMathAlgorithm(MathAlgorithmFlavor.Runtime));

    public static Algorithm.User CreateSemanticPreludeAlgorithm(Algorithm.User? mathAlgorithm = null)
        => CreatePreludeAlgorithm(includeLoad: true, mathAlgorithm ?? CreateMathAlgorithm(MathAlgorithmFlavor.SignatureOnly));

    private static Property CreateMathProperty(MathMemberDescriptor member, MathAlgorithmFlavor flavor)
        => new(member.Name, CreateMathMemberAlgorithm(member, flavor), IsPublic: true);

    private static Algorithm.User CreateMathMemberAlgorithm(MathMemberDescriptor member, MathAlgorithmFlavor flavor)
    {
        var parameterNames = CreateMathParameterNames(member.Arity);

        return flavor switch
        {
            MathAlgorithmFlavor.Runtime when member.Kind == MathMemberKind.Constant => new Algorithm.User(
                Parent: null,
                Parameters: Algorithm.NormalParameters(parameterNames),
                Opens: [],
                Properties: [],
                Output: [new Expr.Num(member.ConstantValue)]),
            MathAlgorithmFlavor.Runtime => new Algorithm.User(
                Parent: null,
                Parameters: Algorithm.NormalParameters(parameterNames),
                Opens: [],
                Properties: [],
                Output: [new Expr.NativeCall(member.Name, parameterNames)]),
            MathAlgorithmFlavor.SignatureOnly => new Algorithm.User(
                Parent: null,
                Parameters: Algorithm.NormalParameters(parameterNames),
                Opens: [],
                Properties: [],
                Output: []),
            _ => throw new InvalidOperationException($"Unsupported Math algorithm flavor '{flavor}'."),
        };
    }

    private static Algorithm.User CreateLoadAlgorithm()
        => new(Parent: null, Parameters: Algorithm.NormalParameters(LoadParameterNames), Opens: [], Properties: [], Output: []);

    private static Algorithm.User CreatePreludeAlgorithm(bool includeLoad, Algorithm.User mathAlgorithm)
    {
        var properties = new List<Property>(Builtins.Length + (includeLoad ? 2 : 1));
        foreach (var builtin in Builtins)
            properties.Add(new Property(builtin.Name, new Algorithm.Builtin(builtin.Id), IsPublic: true));

        if (includeLoad)
            properties.Add(new Property("load", CreateLoadAlgorithm(), IsPublic: true));

        properties.Add(new Property("Math", mathAlgorithm, IsPublic: true));

        return new Algorithm.User(Parent: null, Parameters: [], Opens: [], Properties: properties, Output: []);
    }

    private static string[] CreateMathParameterNames(int arity) => arity switch
    {
        0 => [],
        1 => ["x"],
        2 => ["x", "y"],
        _ => throw new InvalidOperationException($"Unsupported Math arity '{arity}'."),
    };

    private static BuiltinDescriptor Fixed(BuiltinId id, params string[] parameterNames)
    {
        var parameters = parameterNames.Select(static name => new CallableParameter(name)).ToArray();
        return new(id, parameterNames.Length, parameters, parameters);
    }

    private static BuiltinDescriptor Sequence(BuiltinId id, SequenceBuiltinMetadata metadata)
        => new(
            id,
            fixedArity: null,
            plainParameters: metadata.Parameters,
            dotParameters: CreateSequenceDotParameters(metadata),
            sequenceMetadata: metadata);

    private static IReadOnlyList<CallableParameter> CreateSequenceDotParameters(SequenceBuiltinMetadata metadata)
        => metadata.Parameters
            .Where(static parameter => parameter.Kind == ParameterKind.Normal)
            .ToArray();

    internal static string DescribeSequenceBuiltinTotalArgs(CallableSignature signature)
    {
        var minimum = signature.RequiredNormalParameterCount;
        return signature.HasVariadicParameter
            ? minimum == 0 ? "any number of" : $"at least {minimum}"
            : signature.Parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
