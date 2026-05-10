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

Flat fixed user calls use plan-derived parameter names and the shared flat fixed binding helper. Existing final-argument unpacking, dot-call boundary preservation, and algorithm/value binding semantics remain executor behavior.

Flat variadic user calls use a plan-derived flat variadic layout adapter to feed the existing variadic executor. The flat variadic executor internals remain unchanged.

Patterned and grouped user calls route through `CallableBindingPlan`, but execution remains `ParameterPattern`-based. That executor owns runtime semantics not represented by the plan, including algorithm/value binding channels, nested grouped capture behavior, explicit block-to-group item handling, singleton grouped scalar fallback, and counted callback projection.

## Loop-step usage

Generic `Algorithm.User` loop-step shape selection uses `CallableBindingPlan` to choose patterned, flat fixed, or flat variadic binding. Actual evaluated-slot loop binding still uses the existing runtime helpers.

Non-user loop steps stay on the runtime-specific path. Optimized loops remain separate and keep their existing fallback checks and scalar assumptions.

## Builtins and callbacks

Builtin metadata uses `CallableSignature` and `CallableBindingPlan` where it is safe to describe builtin call shape. Builtin runtime binding remains custom because sequence builtins own collection extraction, suffix preparation, callback invocation, and result-count rules.

Callback shape parity is tested against callable plans, but callback runtime binding remains custom. Counted callback parameters, grouped callback patterns, projection rules, and reducer accumulator behavior stay in the callback executor.

## Conditionals are separate

Conditional branches use `Pattern`, not `ParameterPattern`. Do not force conditional branches into `CallableBindingPlan`; a separate `ConditionalBranchPatternPlan` should wait until there is a concrete need.

## Runtime semantics still owned by executors

`CallableBindingPlan` is a shape model, not an executor. Do not move runtime-only semantics into it by accident. In particular, keep these boundaries explicit:

- Flat variadic user-call internals still run through the existing executor behind a plan-derived layout adapter.
- Patterned/grouped execution remains `ParameterPattern`-based.
- Builtin runtime semantics remain custom.
- Callback runtime binding remains custom.
- Generic loop-step binding still uses evaluated-slot loop helpers after user-step shape selection.
- Optimized loops remain separate.
- Conditional branch matching remains separate.

## Migration boundaries / follow-up areas

Future work should start from characterization tests before moving more execution logic. The current safe boundary is shared surface/diagnostics/shape plus selected user-call and generic user loop-step routing. Any deeper migration must preserve runtime semantics for algorithm/value channels, grouped captures, explicit block arguments, counted callback views, loop state slots, builtin sequence rules, and conditional branch matching.