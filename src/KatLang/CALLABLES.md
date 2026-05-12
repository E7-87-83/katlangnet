# Callable signatures and binding plans
This note documents the C# implementation architecture for callable signatures, diagnostics, and binding-plan metadata.

## Purpose

KatLang has several places that need to describe a callable without executing it: diagnostics, semantic tooling, builtin metadata, user-call routing, and generic loop-step shape selection. The shared callable models describe that surface and shape once, while runtime executors keep ownership of the language semantics that require evaluated values, algorithm bindings, callback counts, or loop state.

## Core models

`CallableSignature` describes the callable surface: parameter patterns, flattened parameter metadata, parameter source (`explicit`, `implicit`, `builtin`, or `synthetic`), and user-facing display text.

`CallableSignatureDiagnostics` derives callable arity and formatting facts from a signature: min/max top-level argument counts, top-level variadic facts, shared bad-arity formatting, and validation text for multiple top-level variadics.

`CallableBindingPlan` describes callable parameter binding shape: flat fixed captures, top-level variadic captures, prefix/variadic/suffix layout, grouped one-slot nodes, grouped variadics as nested variadics, and nested recursive parameter patterns.

## Evaluator usage

User-call route and layout decisions use `CallableBindingPlan`; user-call execution still uses the existing binders.

Flat fixed user calls use plan-derived parameter names and the shared flat fixed binding helper. They preserve call-site expression boundaries: comma arguments are one slot each, bare result-join expressions explicitly contribute joined items, postfix spread expressions explicitly supply one expression's immediate result stream, and ordinary multi-output values remain one slot. Algorithm/value binding semantics remain executor behavior.

Flat variadic user calls use the shared plan-native flat variadic layout described below. User-call argument-expression evaluation, postfix spread stream supply, dot-call boundary preservation, and algorithm/value binding channels remain executor behavior. An explicit spread dot-call receiver is allowed only when the leading receiver parameter is the top-level variadic capture; fixed receiver parameters still preserve one boundary and reject spread.

Patterned and grouped user calls route through `CallableBindingPlan`, but execution remains `ParameterPattern`-based. That executor owns runtime semantics not represented by the plan, including algorithm/value binding channels, nested grouped capture behavior, explicit block-to-group item handling, singleton grouped scalar fallback, and counted callback projection.

## Loop-step usage

Generic `Algorithm.User` loop-step shape selection uses `CallableBindingPlan` to choose patterned, flat fixed, or flat variadic binding. Actual evaluated-slot loop binding still uses the existing runtime helpers.

Non-user loop steps stay on the runtime-specific path. Optimized loops remain separate and keep their existing fallback checks and scalar assumptions.

## Builtins and callbacks

Builtin metadata uses `CallableSignature` and `CallableBindingPlan` where it is safe to describe builtin call shape. Builtin runtime binding remains custom because sequence builtins own collection extraction, suffix preparation, callback invocation, and result-count rules.

`CallableBindingPlan` can describe `Algorithm.User` callback shapes, but callback runtime binding remains executor-owned. Reduce consults the plan only as read-only shape data to detect a top-level variadic accumulator side; the reducer executor still owns accumulator input shaping and binding. Counted callback parameters, grouped callback patterns, projection rules, and reducer accumulator behavior stay in the callback executor. Conditional and builtin callbacks intentionally remain outside plan classification because they use orthogonal binding models. Map/filter top-level variadic callbacks and reduce variadics before the current-item boundary still bind one projected item per invocation; this is characterized legacy behavior, not a plan-native variadic callback semantics commitment. Future callback migration should introduce a `CallbackBindingInput` / policy model first.

## Conditionals are separate

Conditional branches use `Pattern`, not `ParameterPattern`, and intentionally remain outside `CallableBindingPlan`. `Pattern` owns conditional-specific semantics such as literal matching, ordered branch selection, value-only bindings, singleton group normalization, no true variadic branch patterns, and separate counted matching helpers. If richer branch diagnostics, editor branch visualization, conditional guards, or runtime matcher refactoring need a shared shape model, add a separate `ConditionalBranchPatternPlan`; do not fold conditional branches into `CallableBindingPlan`.

## Runtime semantics still owned by executors

`CallableBindingPlan` is a shape model, not an executor. Do not move runtime-only semantics into it by accident. In particular, keep these boundaries explicit:

- Flat variadic user-call argument-expression evaluation, postfix spread handling, and dot-call boundary handling remain executor behavior; the binding kernel and capture construction are shared.
- Patterned/grouped execution remains `ParameterPattern`-based.
- Builtin runtime semantics remain custom.
- Callback runtime binding remains custom.
- Generic loop-step binding still uses evaluated-slot loop helpers after user-step shape selection.
- Optimized loops remain separate.
- Conditional branch matching remains separate.

## Flat variadic executor boundary

Flat variadic user-call binding and generic `Algorithm.User` loop-step evaluated-slot binding share a plan-native flat variadic layout derived from `CallableBindingPlan`. The layout carries the callable signature and variadic parameter name; declaration order comes from the signature.

`BindCallableArguments` remains the shared suffix-from-back binding kernel for prefix binding, variadic middle capture, suffix binding, and arity checks. `CreateVariadicCapture` still owns variadic capture value/count construction. Runtime input construction remains context-specific: user calls build call items from expressions, counts, algorithm arguments, and dot-call boundary flags; loop binding receives already evaluated state slots. Patterned/grouped execution remains `ParameterPattern`-based, and callback and builtin runtime binding remain custom and out of scope.

## Migration boundaries / follow-up areas

Future work should start from characterization tests before moving more execution logic. The current safe boundary is shared surface/diagnostics/shape plus selected user-call and generic user loop-step routing. Any deeper migration must preserve runtime semantics for algorithm/value channels, grouped captures, explicit block arguments, counted callback views, loop state slots, builtin sequence rules, and conditional branch matching.