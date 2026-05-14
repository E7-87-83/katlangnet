namespace KatLang.Runtime;

// Data-only runtime input slot used to prepare future binding input unification.
// It does not bind, evaluate, or apply policy.
internal readonly record struct BindingInputSlot(
    Result? Value,
    Algorithm? Algorithm,
    EvalError? ValueError,
    int? VariadicSlotEmittedCount)
{
    public static BindingInputSlot FromUserCallItem(
        Result? value,
        Algorithm? algorithm,
        EvalError? valueError,
        int? variadicSlotEmittedCount = null)
        => new(value, algorithm, valueError, variadicSlotEmittedCount);

    public static BindingInputSlot FromEvaluatedValue(Result value)
        => new(value, Algorithm: null, ValueError: null, VariadicSlotEmittedCount: null);
}
