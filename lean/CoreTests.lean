import KatLang

--------------------------------------------------------------------------------
-- dotCall semantics tests
--------------------------------------------------------------------------------

namespace KatLangTests
open KatLang (alg algWithParameters algWithParameterPatterns algPrivate privateProp publicProp privateLocalProp publicLocalProp runFlat runResult Algorithm Error Result PropExposure)

def hasContext (target : String) : Error -> Bool
  | .withContext msg inner => msg = target || hasContext target inner
  | _ => false

def innermostIsBadArity : Error -> Bool
  | .withContext _ inner => innermostIsBadArity inner
  | .badArity => true
  | _ => false

def innermostIsArityMismatch (expected actual : Nat) : Error -> Bool
  | .withContext _ inner => innermostIsArityMismatch expected actual inner
  | .arityMismatch e a => e = expected && a = actual
  | _ => false

def innermostIsTypeMismatch (expected : String) : Error -> Bool
  | .withContext _ inner => innermostIsTypeMismatch expected inner
  | .typeMismatch actual => actual = expected
  | _ => false

def innermostIsMissingOutput : Error -> Bool
  | .withContext _ inner => innermostIsMissingOutput inner
  | .missingOutput => true
  | _ => false

def innermostIsSequenceSupplyMissingOutput (side : String) : Error -> Bool
  | .withContext _ inner => innermostIsSequenceSupplyMissingOutput side inner
  | .sequenceSupplyMissingOutput actual => actual = side
  | _ => false

def innermostIsExplicitParamsRequireOutput : Error -> Bool
  | .withContext _ inner => innermostIsExplicitParamsRequireOutput inner
  | .explicitParamsRequireOutput => true
  | _ => false

def innermostIsSpecialOutputAccess : Error -> Bool
  | .withContext _ inner => innermostIsSpecialOutputAccess inner
  | .specialOutputAccess => true
  | _ => false

def innermostIsIllegalInEval (target : String) : Error -> Bool
  | .withContext _ inner => innermostIsIllegalInEval target inner
  | .illegalInEval actual => actual = target
  | _ => false

def innermostIsUnknownName (target : String) : Error -> Bool
  | .withContext _ inner => innermostIsUnknownName target inner
  | .unknownName name => name = target
  | _ => false

def innermostIsNotPublicProperty (owner : String) (name : String) : Error -> Bool
  | .withContext _ inner => innermostIsNotPublicProperty owner name inner
  | .notPublicProperty actualOwner actualName => actualOwner = owner && actualName = name
  | _ => false

def innermostIsLocalOnlyProperty (owner : String) (name : String) (exposure : PropExposure) : Error -> Bool
  | .withContext _ inner => innermostIsLocalOnlyProperty owner name exposure inner
  | .localOnlyProperty actualOwner actualName actualExposure =>
      actualOwner = owner && actualName = name && actualExposure = exposure
  | _ => false

def innermostIsIllegalInOpen (msg : String) : Error -> Bool
  | .withContext _ inner => innermostIsIllegalInOpen msg inner
  | .illegalInOpen actual => actual = msg
  | _ => false

def innermostIsBadOpenForm (msg : String) : Error -> Bool
  | .withContext _ inner => innermostIsBadOpenForm msg inner
  | .badOpenForm actual => actual = msg
  | _ => false

--------------------------------------------------------------------------------
-- callable signature validation tests
--------------------------------------------------------------------------------

def callableSignatureValidates (signature : KatLang.CallableSignature) : Bool :=
  match KatLang.CallableSignature.validate signature with
  | .ok () => true
  | .error _ => false

def callableSignatureValidationRejectsMultipleVariadic : Bool :=
  let signature : KatLang.CallableSignature := {
    name := "Bad"
    parameters := [
      { name := "a", kind := KatLang.ParameterKind.variadic },
      { name := "b", kind := KatLang.ParameterKind.variadic }
    ]
  }
  match KatLang.CallableSignature.validate signature with
  | .error (.illegalInEval message) =>
      message = "Callable signature `Bad` cannot contain more than one variadic parameter."
  | _ => false

#guard callableSignatureValidationRejectsMultipleVariadic

def callableSignatureValidationRejectsInvalidParameterName : Bool :=
  let signature : KatLang.CallableSignature := {
    name := "Bad"
    parameters := [{ name := "initial accumulator" }]
  }
  match KatLang.CallableSignature.validate signature with
  | .error (.illegalInEval message) =>
      message = "Callable signature `Bad` contains invalid parameter name `initial accumulator`."
  | _ => false

#guard callableSignatureValidationRejectsInvalidParameterName

def callableSignatureValidationRejectsDuplicateParameterName : Bool :=
  let signature : KatLang.CallableSignature := {
    name := "Bad"
    parameters := [{ name := "x" }, { name := "x" }]
  }
  match KatLang.CallableSignature.validate signature with
  | .error (.illegalInEval message) =>
      message = "Callable signature `Bad` contains duplicate parameter name `x`."
  | _ => false

#guard callableSignatureValidationRejectsDuplicateParameterName

def builtinSequenceSignaturesValidate : Bool :=
  let builtins := [
    KatLang.Builtin.sumBuiltin,
    KatLang.Builtin.countBuiltin,
    KatLang.Builtin.mapBuiltin,
    KatLang.Builtin.filterBuiltin,
    KatLang.Builtin.reduceBuiltin,
    KatLang.Builtin.takeBuiltin,
    KatLang.Builtin.skipBuiltin
  ]
  builtins.all fun builtin =>
    match KatLang.sequenceBuiltinMetadata? builtin with
    | some metadata =>
        callableSignatureValidates (metadata.signature (KatLang.builtinDisplayName builtin))
    | none => false

#guard builtinSequenceSignaturesValidate

def algorithmParametersPreserveNameAndKindTogether : Bool :=
  let algorithm := algWithParameters [
    { name := "values", kind := .variadic },
    { name := "factor", kind := .normal }
  ] [] [] [.param "values"]
  Algorithm.parameters algorithm == [
    { name := "values", kind := .variadic },
    { name := "factor", kind := .normal }
  ]
  && Algorithm.params algorithm == ["values", "factor"]
  && Algorithm.paramKinds algorithm == [.variadic, .normal]

#guard algorithmParametersPreserveNameAndKindTogether

-- Test 1: Structural property access (0-param) → value access
-- a.X where X has 0 params → evaluates property directly
def propAlg : Algorithm :=
  alg [] [] [] [.num 42]

def receiver1 : Algorithm :=
  algPrivate [] [] [("X", propAlg)] []

def test1 : Bool :=
  match runFlat (.dotCall (.block receiver1) "X" none) with
  | Except.ok [42] => true
  | _ => false

#guard test1
-- EXPECTED: Except.ok [42]
#eval runFlat (.dotCall (.block receiver1) "X" none)

-- Test 2: Structural property with params, no args → arity mismatch (navigation-only)
-- a.F where F(x) = x + 1, no args → error (no receiver injection)
def incAlg : Algorithm :=
  alg ["x"] [] [] [.binary .add (.param "x") (.num 1)]

def receiver2 : Algorithm :=
  algPrivate [] [] [("F", incAlg)] [.num 10]

def test2a : Bool :=
  match runResult (.dotCall (.block receiver2) "F" none) with
  | Except.error _ => true   -- arity mismatch: F expects 1 arg, got 0
  | Except.ok _ => false

#guard test2a
-- EXPECTED: Except.error (arityMismatch 1 0)
#eval runResult (.dotCall (.block receiver2) "F" none)

-- Test 2b: Structural property with explicit args → direct binding (navigation-only)
-- a.F(10) where F(x) = x + 1 → 11
def test2b : Bool :=
  match runFlat (.dotCall (.block receiver2) "F" (some (alg [] [] [] [.num 10]))) with
  | Except.ok [11] => true
  | _ => false

#guard test2b
-- EXPECTED: Except.ok [11]
#eval runFlat (.dotCall (.block receiver2) "F" (some (alg [] [] [] [.num 10])))

-- Test 2c: Bare use of a parameterized property → arity mismatch with property context
def receiver2c : Algorithm :=
  algPrivate [] [] [("A", alg ["x"] [] [] [.param "x"])] [.resolve "A"]

def test2c : Bool :=
  match runResult (.block receiver2c) with
  | Except.error err =>
      hasContext "while evaluating property A" err
      && innermostIsArityMismatch 1 0 err
  | Except.ok _ => false

#guard test2c
-- EXPECTED: Except.error (withContext "while evaluating property A" (arityMismatch 1 0))
#eval runResult (.block receiver2c)

-- direct-call ordinary algorithm tests
--------------------------------------------------------------------------------

def directCallAlg : Algorithm :=
  alg ["x"] [] [] [.binary .add (.param "x") (.num 1)]

def directCallRoot : Algorithm :=
  algPrivate [] [] [("Algo", directCallAlg)] [
    .call (.resolve "Algo") (alg [] [] [] [.num 6])
  ]

def directCallWorks : Bool :=
  match runFlat (.block directCallRoot) with
  | Except.ok [7] => true
  | _ => false

#guard directCallWorks

def directCallArityRoot : Algorithm :=
  algPrivate [] [] [("Algo", directCallAlg)] [
    .call (.resolve "Algo") (alg [] [] [] [])
  ]

def directCallUsesOwnArity : Bool :=
  match runResult (.block directCallArityRoot) with
  | Except.error err =>
      hasContext "while evaluating call to Algo" err
      && innermostIsArityMismatch 1 0 err
  | Except.ok _ => false

#guard directCallUsesOwnArity

def zeroArgOutputAlg : Algorithm :=
  algPrivate [] [] [] [.num 5]

def zeroArgOutputCallRoot : Algorithm :=
  algPrivate [] [] [("Algo", zeroArgOutputAlg)] [
    .call (.resolve "Algo") (alg [] [] [] [])
  ]

def zeroArgOutputCallWorks : Bool :=
  match runFlat (.block zeroArgOutputCallRoot) with
  | Except.ok [5] => true
  | _ => false

#guard zeroArgOutputCallWorks

def zeroArgOutputRejectsExtraArgsRoot : Algorithm :=
  algPrivate [] [] [("Algo", zeroArgOutputAlg)] [
    .call (.resolve "Algo") (alg [] [] [] [.num 6])
  ]

def zeroArgOutputRejectsExtraArgs : Bool :=
  match runResult (.block zeroArgOutputRejectsExtraArgsRoot) with
  | Except.error err => innermostIsArityMismatch 0 1 err
  | Except.ok _ => false

#guard zeroArgOutputRejectsExtraArgs

def helperOutputAlg : Algorithm :=
  algPrivate [] [] [
    ("Helper", alg ["x"] [] [] [.binary .mul (.param "x") (.num 2)])
  ] [.num 5]

def helperDotCallRoot : Algorithm :=
  algPrivate [] [] [("Algo", helperOutputAlg)] [
    .dotCall (.resolve "Algo") "Helper" (some (alg [] [] [] [.num 6]))
  ]

def helperDotCallStillWorks : Bool :=
  match runFlat (.block helperDotCallRoot) with
  | Except.ok [12] => true
  | _ => false

#guard helperDotCallStillWorks

def capturedLocalHelperAlg : Algorithm :=
  alg ["x"] [] [
    privateLocalProp "Prop" .localCapturedAncestorParams
      (alg [] [] [] [.binary .add (.param "x") (.num 1)])
  ] [
    .binary .mul (.resolve "Prop") (.num 2)
  ]

def capturedLocalHelperRoot : Algorithm :=
  algPrivate [] [] [("Algo", capturedLocalHelperAlg)] [
    .call (.resolve "Algo") (alg [] [] [] [.num 6])
  ]

def capturedLocalHelperStillWorks : Bool :=
  match runFlat (.block capturedLocalHelperRoot) with
  | Except.ok [14] => true
  | _ => false

#guard capturedLocalHelperStillWorks

def capturedLocalOnlyAlg : Algorithm :=
  alg ["x"] [] [
    privateLocalProp "Prop" .localCapturedAncestorParams
      (alg [] [] [] [.binary .add (.param "x") (.num 1)])
  ] [
    .param "x"
  ]

def capturedLocalOnlyDotRoot : Algorithm :=
  algPrivate [] [] [("Algo", capturedLocalOnlyAlg)] [
    .dotCall (.resolve "Algo") "Prop" none
  ]

def capturedLocalOnlyDotRejected : Bool :=
  match runResult (.block capturedLocalOnlyDotRoot) with
  | Except.error err => innermostIsLocalOnlyProperty "Algo" "Prop" .localCapturedAncestorParams err
  | Except.ok _ => false

#guard capturedLocalOnlyDotRejected

def capturedLocalOnlyDotCallRoot : Algorithm :=
  algPrivate [] [] [("Algo", capturedLocalOnlyAlg)] [
    .dotCall (.resolve "Algo") "Prop" (some (alg [] [] [] [.num 6]))
  ]

def capturedLocalOnlyDotCallRejected : Bool :=
  match runResult (.block capturedLocalOnlyDotCallRoot) with
  | Except.error err => innermostIsLocalOnlyProperty "Algo" "Prop" .localCapturedAncestorParams err
  | Except.ok _ => false

#guard capturedLocalOnlyDotCallRejected

def helperDirectCallStillFailsRoot : Algorithm :=
  algPrivate [] [] [("Algo", helperOutputAlg)] [
    .call (.resolve "Algo") (alg [] [] [] [.num 6])
  ]

def helperDirectCallStillFails : Bool :=
  match runResult (.block helperDirectCallStillFailsRoot) with
  | Except.error err => innermostIsArityMismatch 0 1 err
  | Except.ok _ => false

#guard helperDirectCallStillFails

def parametrizedValuePositionRoot : Algorithm :=
  algPrivate [] [] [("Algo", directCallAlg)] [
    .resolve "Algo"
  ]

def parametrizedValuePositionRejectsBareUse : Bool :=
  match runResult (.block parametrizedValuePositionRoot) with
  | Except.error err =>
      hasContext "while evaluating property Algo" err
      && innermostIsArityMismatch 1 0 err
  | Except.ok _ => false

#guard parametrizedValuePositionRejectsBareUse

def innerDirectAlg : Algorithm :=
  alg ["x"] [] [] [.binary .add (.param "x") (.num 10)]

def outerDirectCallAlg : Algorithm :=
  algPrivate [] [] [("Inner", innerDirectAlg)] [
    .call (.resolve "Inner") (alg [] [] [] [.num 5])
  ]

def nestedDirectCallRoot : Algorithm :=
  algPrivate [] [] [("Outer", outerDirectCallAlg)] [
    .resolve "Outer",
    .dotCall (.resolve "Outer") "Inner" (some (alg [] [] [] [.num 5]))
  ]

def nestedDirectCallWorks : Bool :=
  match runFlat (.block nestedDirectCallRoot) with
  | Except.ok [15, 15] => true
  | _ => false

#guard nestedDirectCallWorks

def conditionalLocalInnerAlg : Algorithm :=
  .conditional none [] [
    ⟨ .litInt 0,
      alg [] [] [
        privateLocalProp "Inner" .localConditional (alg [] [] [] [.num 1])
      ] [.num 0] ⟩,
    ⟨ .bind "x",
      alg [] [] [
        privateLocalProp "Inner" .localConditional
          (alg [] [] [] [.binary .add (.param "x") (.num 1)])
      ] [.param "x"] ⟩
  ]

def conditionalLocalInnerRoot : Algorithm :=
  algPrivate [] [] [("Outer", conditionalLocalInnerAlg)] [
    .dotCall (.resolve "Outer") "Inner" none
  ]

def conditionalLocalInnerRejected : Bool :=
  match runResult (.block conditionalLocalInnerRoot) with
  | Except.error err => innermostIsLocalOnlyProperty "Outer" "Inner" .localConditional err
  | Except.ok _ => false

#guard conditionalLocalInnerRejected

def conditionalSplitHelpersAlg : Algorithm :=
  .conditional none [] [
    ⟨ .litInt 0,
      alg [] [] [
        privateLocalProp "First" .localConditional (alg [] [] [] [.num 1])
      ] [.num 0] ⟩,
    ⟨ .bind "x",
      alg [] [] [
        privateLocalProp "Second" .localConditional
          (alg [] [] [] [.binary .add (.param "x") (.num 1)])
      ] [.param "x"] ⟩
  ]

def conditionalSplitHelpersRoot : Algorithm :=
  algPrivate [] [] [("Outer", conditionalSplitHelpersAlg)] [
    .dotCall (.resolve "Outer") "Second" none
  ]

def conditionalSplitHelpersRejected : Bool :=
  match runResult (.block conditionalSplitHelpersRoot) with
  | Except.error err => innermostIsLocalOnlyProperty "Outer" "Second" .localConditional err
  | Except.ok _ => false

#guard conditionalSplitHelpersRejected

def publicOutputAlg : Algorithm :=
  alg ["x"] [] [] [.binary .add (.param "x") (.num 1)]

def outputDotCallRejectedRoot : Algorithm :=
  algPrivate [] [] [("Algo", publicOutputAlg)] [
    .dotCall (.resolve "Algo") "Output" (some (alg [] [] [] [.num 6]))
  ]

def outputDotCallRejected : Bool :=
  match runResult (.block outputDotCallRejectedRoot) with
  | Except.error err => innermostIsSpecialOutputAccess err
  | Except.ok _ => false

#guard outputDotCallRejected

def nestedOutputDotCallRejectedRoot : Algorithm :=
  algPrivate [] [] [("Outer", outerDirectCallAlg)] [
    .dotCall (.dotCall (.resolve "Outer") "Inner" none) "Output" (some (alg [] [] [] [.num 6]))
  ]

def nestedOutputDotCallRejected : Bool :=
  match runResult (.block nestedOutputDotCallRejectedRoot) with
  | Except.error err => innermostIsSpecialOutputAccess err
  | Except.ok _ => false

#guard nestedOutputDotCallRejected

def bareOutputAccessRejectedRoot : Algorithm :=
  algPrivate [] [] [("Algo", zeroArgOutputAlg)] [
    .dotCall (.resolve "Algo") "Output" none
  ]

def bareOutputAccessRejected : Bool :=
  match runResult (.block bareOutputAccessRejectedRoot) with
  | Except.error err => innermostIsSpecialOutputAccess err
  | Except.ok _ => false

#guard bareOutputAccessRejected

def stringLiteralSatisfiesInvariant : Bool :=
  KatLang.postElabInvariant (.stringLiteral "abc")

#guard stringLiteralSatisfiesInvariant

def stringOutputAlgSatisfiesInvariant : Bool :=
  KatLang.postElabInvariantAlg (alg [] [] [] [.stringLiteral "abc"])

#guard stringOutputAlgSatisfiesInvariant

def unresolvedLoadViolatesInvariant : Bool :=
  !KatLang.postElabInvariant
    (.call (.resolve "load") (alg [] [] [] [.stringLiteral "https://katlang.org/lib.kat"]))

#guard unresolvedLoadViolatesInvariant

def outputDotCallViolatesInvariant : Bool :=
  !KatLang.postElabInvariant (.dotCall (.resolve "Algo") "Output" none)

#guard outputDotCallViolatesInvariant

def structuralOutputPropertyViolatesInvariant : Bool :=
  !KatLang.postElabInvariantAlg
    (alg [] [] [privateProp "Output" (alg [] [] [] [.num 1])] [.num 2])

#guard structuralOutputPropertyViolatesInvariant

def helperPropertySatisfiesInvariant : Bool :=
  KatLang.postElabInvariantAlg
    (alg [] [] [privateProp "Helper" (alg [] [] [] [.num 1])] [.stringLiteral "abc"])

#guard helperPropertySatisfiesInvariant

--------------------------------------------------------------------------------
-- missingOutput semantics tests
--------------------------------------------------------------------------------

def noOutputGroupAlg : Algorithm :=
  algPrivate [] [] [("X", alg [] [] [] [.num 1])] []

def missingOutputRootOnlyDefinitions : Algorithm :=
  algPrivate [] [] [("A", noOutputGroupAlg)] []

def missingOutputRootOnlyDefinitionsFails : Bool :=
  match runResult (.block missingOutputRootOnlyDefinitions) with
  | Except.error err => innermostIsMissingOutput err
  | Except.ok _ => false

#guard missingOutputRootOnlyDefinitionsFails

def missingOutputRootWithTrailingOutput : Bool :=
  match runFlat (.block (algPrivate [] [] [("T", alg [] [] [] [.num 4])] [
    .resolve "T"
  ])) with
  | Except.ok [4] => true
  | _ => false

#guard missingOutputRootWithTrailingOutput

def missingOutputRootWithExplicitEmptyOutput : Bool :=
  match runResult (.block (algPrivate [] [] [("T", alg [] [] [] [.num 4])] [
    .resolve "empty"
  ])) with
  | Except.ok (.group []) => true
  | _ => false

#guard missingOutputRootWithExplicitEmptyOutput

def missingOutputRootValueDoesNotEqualEmpty : Bool :=
  match runFlat (.block (algPrivate [] [] [("T", alg [] [] [] [.num 4])] [
    .binary .eq (.resolve "T") (.resolve "empty")
  ])) with
  | Except.ok [0] => true
  | _ => false

#guard missingOutputRootValueDoesNotEqualEmpty

def missingOutputMultipleDefinitionsRoot : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Price", alg [] [] [] [.num 10]),
    ("Tax", alg [] [] [] [.num 2]),
    ("Total", alg [] [] [] [.binary .add (.resolve "Price") (.resolve "Tax")])
  ] [])) with
  | Except.error err => innermostIsMissingOutput err
  | Except.ok _ => false

#guard missingOutputMultipleDefinitionsRoot

def missingOutputMultipleDefinitionsWithOutput : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Price", alg [] [] [] [.num 10]),
    ("Tax", alg [] [] [] [.num 2]),
    ("Total", alg [] [] [] [.binary .add (.resolve "Price") (.resolve "Tax")])
  ] [
    .resolve "Total"
  ])) with
  | Except.ok [12] => true
  | _ => false

#guard missingOutputMultipleDefinitionsWithOutput

def missingOutputValid2Root : Algorithm :=
  algPrivate [] [] [("A", noOutputGroupAlg)] [
    .dotCall (.resolve "A") "X" none
  ]

def missingOutputValid2 : Bool :=
  match runFlat (.block missingOutputValid2Root) with
  | Except.ok [1] => true
  | _ => false

#guard missingOutputValid2

def applyMissingOutputAlg : Algorithm :=
  alg ["f"] [] [] [
    .call (.param "f") (alg [] [] [] [.num 4])
  ]

def incMissingOutputAlg : Algorithm :=
  alg ["x"] [] [] [
    .binary .add (.param "x") (.num 1)
  ]

def missingOutputValid3Root : Algorithm :=
  algPrivate [] [] [("Apply", applyMissingOutputAlg), ("Inc", incMissingOutputAlg)] [
    .call (.resolve "Apply") (alg [] [] [] [.resolve "Inc"])
  ]

def missingOutputValid3 : Bool :=
  match runFlat (.block missingOutputValid3Root) with
  | Except.ok [5] => true
  | _ => false

#guard missingOutputValid3

def holderMissingOutputAlg : Algorithm :=
  algPrivate [] [] [("F", noOutputGroupAlg)] [.num 0]

def missingOutputValid4Root : Algorithm :=
  algPrivate [] [] [("Holder", holderMissingOutputAlg)] [.resolve "Holder"]

def missingOutputValid4 : Bool :=
  match runFlat (.block missingOutputValid4Root) with
  | Except.ok [0] => true
  | _ => false

#guard missingOutputValid4

def missingOutputError5Root : Algorithm :=
  algPrivate [] [] [("A", noOutputGroupAlg)] [.resolve "A"]

def missingOutputError5 : Bool :=
  match runResult (.block missingOutputError5Root) with
  | Except.error err =>
      hasContext "while evaluating property A" err
      && innermostIsMissingOutput err
  | Except.ok _ => false

#guard missingOutputError5

def missingOutputError6Root : Algorithm :=
  algPrivate [] [] [("A", noOutputGroupAlg)] [
    .call (.resolve "A") (alg [] [] [] [])
  ]

def missingOutputError6 : Bool :=
  match runResult (.block missingOutputError6Root) with
  | Except.error err =>
      hasContext "while evaluating call to A" err
      && innermostIsMissingOutput err
  | Except.ok _ => false

#guard missingOutputError6

def missingOutputError6bRoot : Algorithm :=
  algPrivate [] [] [("A", noOutputGroupAlg)] [
    .call (.resolve "A") (alg [] [] [] [.num 6])
  ]

def missingOutputError6b : Bool :=
  match runResult (.block missingOutputError6bRoot) with
  | Except.error err =>
      hasContext "while evaluating call to A" err
      && innermostIsMissingOutput err
  | Except.ok _ => false

#guard missingOutputError6b

def missingOutputError7Root : Algorithm :=
  algPrivate [] [] [("A", noOutputGroupAlg)] [
    .binary .add (.resolve "A") (.num 1)
  ]

def missingOutputError7 : Bool :=
  match runResult (.block missingOutputError7Root) with
  | Except.error err =>
      hasContext "while evaluating property A" err
      && innermostIsMissingOutput err
  | Except.ok _ => false

#guard missingOutputError7

def missingOutputError8Root : Algorithm :=
  algPrivate [] [] [("A", noOutputGroupAlg)] [
    .unary .minus (.resolve "A")
  ]

def missingOutputError8 : Bool :=
  match runResult (.block missingOutputError8Root) with
  | Except.error err =>
      hasContext "while evaluating property A" err
      && innermostIsMissingOutput err
  | Except.ok _ => false

#guard missingOutputError8

def missingOutputError9Root : Algorithm :=
  algPrivate [] [] [
    ("A", noOutputGroupAlg),
    ("B", alg [] [] [] [.resolve "A"])
  ] [
    .resolve "B"
  ]

def missingOutputError9 : Bool :=
  match runResult (.block missingOutputError9Root) with
  | Except.error err => innermostIsMissingOutput err
  | Except.ok _ => false

#guard missingOutputError9

def useMissingOutputAlg : Algorithm :=
  alg ["f"] [] [] [.num 0]

def missingOutputValid10Root : Algorithm :=
  algPrivate [] [] [("A", noOutputGroupAlg), ("Use", useMissingOutputAlg)] [
    .call (.resolve "Use") (alg [] [] [] [.resolve "A"])
  ]

def missingOutputValid10 : Bool :=
  match runFlat (.block missingOutputValid10Root) with
  | Except.ok [0] => true
  | _ => false

#guard missingOutputValid10

--------------------------------------------------------------------------------
-- explicit empty output builtin tests
--------------------------------------------------------------------------------

def explicitEmptyExpr : KatLang.Expr := .resolve "empty"

def sequenceSupply (expr : KatLang.Expr) : KatLang.Expr :=
  .sequenceSupply expr explicitEmptyExpr

def sequenceSuppliedReceiver (expr : KatLang.Expr) : KatLang.Expr :=
  .block (alg [] [] [] [sequenceSupply expr])

def explicitEmptyOutputBody : KatLang.Expr :=
  .block (alg [] [] [] [explicitEmptyExpr])

def missingOutputBodyExpr : KatLang.Expr :=
  .block (alg [] [] [] [])

def explicitEmptyIsEvenAlg : Algorithm :=
  alg ["x"] [] [] [
    .binary .eq (.binary .mod (.param "x") (.num 2)) (.num 0)
  ]

def explicitEmptyNoOutputContainer : Algorithm :=
  algPrivate [] [] [("Prop", alg [] [] [] [.num 7])] []

def explicitEmptyProducesZeroValues : Bool :=
  match runResult explicitEmptyExpr, runFlat explicitEmptyExpr with
  | Except.ok (.group []), Except.ok [] => true
  | _, _ => false

#guard explicitEmptyProducesZeroValues

def explicitEmptyCallSyntaxFails : Bool :=
  match runResult (.call explicitEmptyExpr (alg [] [] [] [])) with
  | Except.error err =>
      innermostIsIllegalInEval "`empty` is a builtin constant; use `empty` without call syntax." err
  | Except.ok _ => false

#guard explicitEmptyCallSyntaxFails

def explicitEmptyCountsAsZero : Bool :=
  match runFlat (.block (algPrivate [] [] [("A", alg [] [] [] [explicitEmptyExpr])] [
    .dotCall explicitEmptyExpr "count" none,
    .call (.resolve "count") (alg [] [] [] [explicitEmptyExpr]),
    .dotCall explicitEmptyOutputBody "count" none,
    .dotCall (.block (alg [] [] [] [explicitEmptyExpr])) "count" none,
    .dotCall (.resolve "A") "count" none
  ])) with
  | Except.ok [0, 0, 0, 0, 0] => true
  | _ => false

#guard explicitEmptyCountsAsZero

def explicitEmptyEquality : Bool :=
  match runFlat (.block (alg [] [] [] [
    .binary .eq explicitEmptyExpr explicitEmptyExpr,
    .binary .ne explicitEmptyExpr explicitEmptyExpr,
    .binary .eq explicitEmptyExpr explicitEmptyOutputBody,
    .binary .eq explicitEmptyOutputBody explicitEmptyExpr,
    .binary .eq
      (.call (.resolve "filter") (alg [] [] [] [
        .num 1,
        .num 3,
        .num 5,
        .block explicitEmptyIsEvenAlg
      ]))
      explicitEmptyExpr,
    .binary .eq
      explicitEmptyExpr
      (.call (.resolve "filter") (alg [] [] [] [
        .num 1,
        .num 3,
        .num 5,
        .block explicitEmptyIsEvenAlg
      ])),
    .binary .eq
      (.dotCall (.num 0) "skip" (some (alg [] [] [] [.num 1])))
      explicitEmptyExpr
  ])) with
  | Except.ok [1, 0, 1, 1, 1, 1, 1] => true
  | _ => false

#guard explicitEmptyEquality

def explicitEmptySequenceSupplyContributesNoItems : Bool :=
  match runFlat (.sequenceSupply (.num 1) (.sequenceSupply explicitEmptyExpr (.num 2))) with
  | Except.ok [1, 2] => true
  | _ => false

#guard explicitEmptySequenceSupplyContributesNoItems

def missingOutputBodyAsResultStillFails : Bool :=
  match runResult (.block (alg [] [] [] [missingOutputBodyExpr])) with
  | Except.error err => innermostIsMissingOutput err
  | Except.ok _ => false

#guard missingOutputBodyAsResultStillFails

def missingOutputBodyCountStillFails : Bool :=
  let dotCount :=
    match runResult (.dotCall missingOutputBodyExpr "count" none) with
    | Except.error err => innermostIsMissingOutput err
    | Except.ok _ => false
  let plainCount :=
    match runResult (.call (.resolve "count") (alg [] [] [] [missingOutputBodyExpr])) with
    | Except.error err => innermostIsMissingOutput err
    | Except.ok _ => false
  dotCount && plainCount

#guard missingOutputBodyCountStillFails

def missingOutputBodyEqualityStillFails : Bool :=
  let leftMissing :=
    match runResult (.binary .eq missingOutputBodyExpr explicitEmptyExpr) with
    | Except.error err => innermostIsMissingOutput err
    | Except.ok _ => false
  let rightMissing :=
    match runResult (.binary .eq explicitEmptyExpr missingOutputBodyExpr) with
    | Except.error err => innermostIsMissingOutput err
    | Except.ok _ => false
  let bothMissing :=
    match runResult (.binary .eq missingOutputBodyExpr missingOutputBodyExpr) with
    | Except.error err => innermostIsMissingOutput err
    | Except.ok _ => false
  leftMissing && rightMissing && bothMissing

#guard missingOutputBodyEqualityStillFails

def missingOutputContainerPropertyStillFails : Bool :=
  let countFails :=
    match runResult (.block (algPrivate [] [] [("Lib", explicitEmptyNoOutputContainer)] [
      .dotCall (.resolve "Lib") "count" none
    ])) with
    | Except.error err => innermostIsMissingOutput err
    | Except.ok _ => false
  let equalityFails :=
    match runResult (.block (algPrivate [] [] [("Lib", explicitEmptyNoOutputContainer)] [
      .binary .eq (.resolve "Lib") explicitEmptyExpr
    ])) with
    | Except.error err => innermostIsMissingOutput err
    | Except.ok _ => false
  countFails && equalityFails

#guard missingOutputContainerPropertyStillFails

--------------------------------------------------------------------------------
-- explicit algorithm params require output
--------------------------------------------------------------------------------

def noOutputHelperContainer : Algorithm :=
  algPrivate [] [] [("Prop", alg [] [] [] [.num 7])] []

def invalidExplicitParamClauseAlg : Algorithm :=
  Algorithm.elaborateClauseDefinition (KatLang.Pattern.bind "x") noOutputHelperContainer

def explicitParamsWithoutOutputRejected : Bool :=
  match KatLang.validateExplicitParamOutputInvariant invalidExplicitParamClauseAlg with
  | Except.error Error.explicitParamsRequireOutput => true
  | _ => false

#guard explicitParamsWithoutOutputRejected

def explicitParamsWithoutOutputRejectedAtRun : Bool :=
  match runResult (.block (algPrivate [] [] [("Algo", invalidExplicitParamClauseAlg)] [.num 0])) with
  | Except.error err => innermostIsExplicitParamsRequireOutput err
  | Except.ok _ => false

#guard explicitParamsWithoutOutputRejectedAtRun

def parameterizedChildPropertyContainer : Algorithm :=
  algPrivate [] [] [("Prop", alg ["x", "y"] [] [] [.num 7])] []

def parameterizedChildPropertyWithoutOuterParamsStillValid : Bool :=
  match runFlat (.block (algPrivate [] [] [("Algo", parameterizedChildPropertyContainer)] [
    .dotCall (.resolve "Algo") "Prop" (some (alg [] [] [] [.num 1, .num 2]))
  ])) with
  | Except.ok [7] => true
  | _ => false

#guard parameterizedChildPropertyWithoutOuterParamsStillValid

-- Test 3: Extension property call (lexical fallback)
-- Receiver has no G, but lexical scope defines G(x) = x * 2
-- Receiver output = 5 → 10
def extAlg : Algorithm :=
  alg ["x"] [] [] [.binary .mul (.param "x") (.num 2)]

def outer3 : Algorithm :=
  algPrivate [] [] [("G", extAlg)] [
    .dotCall (.block (alg [] [] [] [.num 5])) "G" none
  ]

def test3 : Bool :=
  match runFlat (.block outer3) with
  | Except.ok [10] => true
  | _ => false

#guard test3
-- EXPECTED: Except.ok [10]
#eval runFlat (.block outer3)

def userVariadicDotCallCountItemsAlg : Algorithm :=
  algWithParameters [{ name := "items", kind := .variadic }] [] [] [
    .dotCall (.param "items") "count" none
  ]

def userVariadicDotCallCountItemsRoot : Algorithm :=
  algPrivate [] [] [("CountItems", userVariadicDotCallCountItemsAlg)] [
    .dotCall (.block (alg [] [] [] [.num 1, .num 2])) "CountItems" none
  ]

def userVariadicDotCallReceiverCountsTopLevelItems : Bool :=
  match runFlat (.block userVariadicDotCallCountItemsRoot) with
  | Except.ok [2] => true
  | _ => false

#guard userVariadicDotCallReceiverCountsTopLevelItems

def userVariadicDotCallMeanAlg : Algorithm :=
  algWithParameters [{ name := "vector", kind := .variadic }] [] [] [
    .dotCall (.param "vector") "sum" none
  ]

def userVariadicDotCallMeanRoot : Algorithm :=
  algPrivate [] [] [("Mean", userVariadicDotCallMeanAlg)] [
    .dotCall (.block (alg [] [] [] [.num 1, .num 2])) "Mean" none
  ]

def userVariadicDotCallReceiverBindsTopLevelItems : Bool :=
  match runFlat (.block userVariadicDotCallMeanRoot) with
  | Except.ok [3] => true
  | _ => false

#guard userVariadicDotCallReceiverBindsTopLevelItems

def userNonVariadicDotCallCountOneAlg : Algorithm :=
  alg ["value"] [] [] [
    .dotCall (.param "value") "count" none
  ]

def userNonVariadicDotCallCountOneRoot : Algorithm :=
  algPrivate [] [] [("CountOne", userNonVariadicDotCallCountOneAlg)] [
    .dotCall (.block (alg [] [] [] [.num 1, .num 2])) "CountOne" none
  ]

def userNonVariadicDotCallReceiverStaysGrouped : Bool :=
  match runFlat (.block userNonVariadicDotCallCountOneRoot) with
  | Except.ok [1] => true
  | _ => false

#guard userNonVariadicDotCallReceiverStaysGrouped

-- Test 4: Ambiguous extension via opens (error case)
-- Two opens both export G → ambiguousOpen error
def libA : Algorithm :=
  alg [] [] [publicProp "G" (alg ["x"] [] [] [.binary .add (.param "x") (.num 1)])] []

def libB : Algorithm :=
  alg [] [] [publicProp "G" (alg ["x"] [] [] [.binary .add (.param "x") (.num 2)])] []

def caller4 : Algorithm :=
  alg [] [.block libA, .block libB] [] [
    .dotCall (.block (alg [] [] [] [.num 5])) "G" none
  ]

def test4 : Bool :=
  match runResult (.block caller4) with
  | Except.error _ => true
  | Except.ok _ => false

#guard test4
-- EXPECTED: Expect.error (Error.ambiguousOpen "G" [...])
#eval runResult (.block caller4)

-- Open resolution regressions
--------------------------------------------------------------------------------

def openPrivateHeadLib : Algorithm :=
  alg [] []
    [ publicProp "X" (alg [] [] [] [.num 1])
    , privateProp "Hidden" (alg [] [] [] [.num 2])
    , privateProp "PrivateSub" (alg [] [] [publicProp "Y" (alg [] [] [] [.num 3])] [])
    ]
    []

-- Models the surface form:
--   open Lib
--   Lib = { ... }
-- where the open appears first and `Lib` is defined later in the same body.
def openPrivateHeadLaterRoot : Algorithm :=
  algPrivate [] [.resolve "Lib"] [("Lib", openPrivateHeadLib)] [.resolve "X"]

def openPrivateHeadLaterWorks : Bool :=
  match runFlat (.block openPrivateHeadLaterRoot) with
  | Except.ok [1] => true
  | _ => false

#guard openPrivateHeadLaterWorks

def openDoesNotExposePrivateMemberRoot : Algorithm :=
  algPrivate [] [.resolve "Lib"] [("Lib", openPrivateHeadLib)] [.resolve "Hidden"]

def openDoesNotExposePrivateMember : Bool :=
  match runResult (.block openDoesNotExposePrivateMemberRoot) with
  | Except.error err => innermostIsUnknownName "Hidden" err
  | Except.ok _ => false

#guard openDoesNotExposePrivateMember

def openMissingHeadRoot : Algorithm :=
  alg [] [.resolve "Missing"] [] [.resolve "X"]

def openMissingHeadStillErrors : Bool :=
  match runResult (.block openMissingHeadRoot) with
  | Except.error err =>
      hasContext "while resolving open: Missing" err
      && innermostIsUnknownName "Missing" err
  | Except.ok _ => false

#guard openMissingHeadStillErrors

def openBuiltinTargetRoot : Algorithm :=
  alg [] [.resolve "if"] [] [.resolve "X"]

def openBuiltinTargetStillIllegal : Bool :=
  match runResult (.block openBuiltinTargetRoot) with
  | Except.error err =>
      hasContext "while resolving open: if" err
      && innermostIsIllegalInOpen "builtin 'if'" err
  | Except.ok _ => false

#guard openBuiltinTargetStillIllegal

def openQualifiedPrivatePathRoot : Algorithm :=
  algPrivate [] [.dotCall (.resolve "Lib") "PrivateSub" none] [("Lib", openPrivateHeadLib)] [.resolve "Y"]

def openQualifiedPrivatePathStillRestricted : Bool :=
  match runResult (.block openQualifiedPrivatePathRoot) with
  | Except.error err =>
      hasContext "while resolving open: Lib.PrivateSub" err
      && innermostIsNotPublicProperty "Lib" "PrivateSub" err
  | Except.ok _ => false

#guard openQualifiedPrivatePathStillRestricted

def openedMemberBuiltinIfAlg : Algorithm :=
  alg ["x"] [] [] [
    .call (.resolve "if") (alg [] [] [] [
      .binary .gt (.param "x") (.num 0),
      .num 1,
      .num 0
    ])
  ]

def openedMemberBuiltinIfVec : Algorithm :=
  alg [] [] [publicProp "Test" openedMemberBuiltinIfAlg] []

def openedMemberBuiltinIfRoot : Algorithm :=
  algPrivate [] [.resolve "Vec"] [("Vec", openedMemberBuiltinIfVec)] [
    .call (.resolve "Test") (alg [] [] [] [.num 35])
  ]

def openedMemberBuiltinIfWorks : Bool :=
  match runFlat (.block openedMemberBuiltinIfRoot) with
  | Except.ok [1] => true
  | _ => false

#guard openedMemberBuiltinIfWorks

def openedMemberBuiltinSumVec : Algorithm :=
  alg [] [] [publicProp "SumPair" (alg ["x", "y"] [] [] [
    .dotCall (.block (alg [] [] [] [.param "x", .param "y"])) "sum" none
  ])] []

def openedMemberBuiltinSumRoot : Algorithm :=
  algPrivate [] [.resolve "Vec"] [("Vec", openedMemberBuiltinSumVec)] [
    .call (.resolve "SumPair") (alg [] [] [] [.num 3, .num 4])
  ]

def openedMemberBuiltinSumWorks : Bool :=
  match runFlat (.block openedMemberBuiltinSumRoot) with
  | Except.ok [7] => true
  | _ => false

#guard openedMemberBuiltinSumWorks

def openedMemberDefinitionSiteCaptureVec : Algorithm :=
  alg [] [] [
    publicProp "Test" (alg ["x"] [] [] [.binary .add (.resolve "A") (.param "x")])
  ] []

def openedMemberDefinitionSiteCaptureScope : Algorithm :=
  algPrivate [] [.resolve "Vec"] [("A", alg [] [] [] [.num 100])] [
    .call (.resolve "Test") (alg [] [] [] [.num 5])
  ]

def openedMemberDefinitionSiteCaptureRoot : Algorithm :=
  algPrivate [] [] [
    ("A", alg [] [] [] [.num 10]),
    ("Vec", openedMemberDefinitionSiteCaptureVec),
    ("Scope", openedMemberDefinitionSiteCaptureScope)
  ] [.resolve "Scope"]

def openedMemberUsesDefinitionSiteNotOpenerSite : Bool :=
  match runFlat (.block openedMemberDefinitionSiteCaptureRoot) with
  | Except.ok [15] => true
  | _ => false

#guard openedMemberUsesDefinitionSiteNotOpenerSite

-- Test 5: Structural property takes precedence over lexical extension
-- a.G where G(x) = x+1 is structural on receiver, no args → arity mismatch (navigation-only)
-- Even though lexical scope also defines G, structural match takes priority → error, not fallback
def localExt : Algorithm :=
  alg ["x"] [] [] [.binary .mul (.param "x") (.num 100)]

def receiver5 : Algorithm :=
  algPrivate [] [] [("G", incAlg)] [.num 5]

def outer5 : Algorithm :=
  algPrivate [] [] [("G", localExt)] [
    .dotCall (.block receiver5) "G" none
  ]

def test5a : Bool :=
  match runResult (.block outer5) with
  | Except.error _ => true   -- structural G found but arity mismatch (no fallback to lexical)
  | Except.ok _ => false

#guard test5a
-- EXPECTED: Except.error (arityMismatch 1 0)
#eval runResult (.block outer5)

-- Test 5b: Structural property with explicit args → navigation wins over lexical
-- a.G(5) where structural G(x)=x+1 → 6 (not localExt which would give 500)
def test5b : Bool :=
  match runFlat (.block (algPrivate [] [] [("G", localExt)] [
    .dotCall (.block receiver5) "G" (some (alg [] [] [] [.num 5]))
  ])) with
  | Except.ok [6] => true
  | _ => false

#guard test5b
-- EXPECTED: Except.ok [6] (structural incAlg wins, not localExt)
#eval runFlat (.block (algPrivate [] [] [("G", localExt)] [
    .dotCall (.block receiver5) "G" (some (alg [] [] [] [.num 5]))
  ]))

-- Test 6: Numbers.count as algorithm argument to Repeat
-- Repeat(step, Numbers.count, init) where Numbers = [10,20,30]
-- step(x) = x + 1, init = 0, count = Numbers.count = 3
-- Result: 0 → 1 → 2 → 3
open KatLang (resolve param num)

def numbersAlg : Algorithm :=
  alg [] [] [] [.num 10, .num 20, .num 30]

-- step: single-param algorithm that adds 1
def stepAlg : Algorithm :=
  alg ["x"] [] [] [.binary .add (.param "x") (.num 1)]

-- Root algorithm that calls Repeat(step, Numbers.count, init)
def repeatArityRoot : Algorithm :=
  algPrivate [] [] [("Numbers", numbersAlg), ("Step", stepAlg)] [
    .call (resolve "repeat")
      (alg [] [] [] [
        resolve "Step",
        .dotCall (resolve "Numbers") "count" none,
        .block (alg [] [] [] [.num 0])
      ])
  ]

def test6 : Bool :=
  match runFlat (.block repeatArityRoot) with
  | Except.ok [3] => true
  | _ => false

#guard test6
-- EXPECTED: Except.ok [3] (step applied 3 times: 0→1→2→3)
#eval runFlat (.block repeatArityRoot)

-- Test 7: Numbers.count as Repeat count (comprehensive)
-- Uses 6 output expressions to verify correct count
def numbersAlg7 : Algorithm :=
  alg [] [] [] [.num 3, .num 5, .num 9, .num 1, .num 0, .num 6]

def testAlg7 : Algorithm :=
  algPrivate [] [] [("Numbers", numbersAlg7)] [
    .call (resolve "repeat")
      (alg [] [] [] [
        .block (alg ["x"] [] [] [.binary .add (.param "x") (.num 1)]),  -- step: increment
        .dotCall (resolve "Numbers") "count" none,                      -- count: 6
        .block (alg [] [] [] [.num 0])                                   -- init: 0
      ])
  ]

def test7 : Bool :=
  match runFlat (.block testAlg7) with
  | Except.ok [6] => true
  | _ => false

#guard test7
-- EXPECTED: Except.ok [6] (step applied 6 times: 0→1→2→3→4→5→6)
#eval runFlat (.block testAlg7)

-- Test 8: 0-param structural property used as Algorithm argument
-- a.X in algorithm position where X has 0 params, returns 42
def xAlg : Algorithm :=
  alg [] [] [] [.num 42]

def receiver8 : Algorithm :=
  algPrivate [] [] [("X", xAlg)] []

-- Use Atoms to force evaluation of the arg algorithm
def test8 : Bool :=
  match runFlat (.call (.resolve "atoms") (alg [] [] [] [.dotCall (.block receiver8) "X" none])) with
  | Except.ok [42] => true
  | _ => false

#guard test8
#eval runFlat (.call (.resolve "atoms") (alg [] [] [] [.dotCall (.block receiver8) "X" none]))

def contentTripleExpr : KatLang.Expr :=
  .block (alg [] [] [] [.num 1, .num 2, .num 3])

def contentPairExpr12 : KatLang.Expr :=
  .block (alg [] [] [] [.num 1, .num 2])

def contentPairExpr34 : KatLang.Expr :=
  .block (alg [] [] [] [.num 3, .num 4])

def contentNestedExpr : KatLang.Expr :=
  .block (alg [] [] [] [contentPairExpr12, contentPairExpr34])

def contentPlainGroupedValues : Bool :=
  match runFlat (.call (.resolve "content") (alg [] [] [] [contentTripleExpr])) with
  | Except.ok [1, 2, 3] => true
  | _ => false

#guard contentPlainGroupedValues

def contentDotCallGroupedReceiver : Bool :=
  match runFlat (.dotCall contentTripleExpr "content" none) with
  | Except.ok [1, 2, 3] => true
  | _ => false

#guard contentDotCallGroupedReceiver

def contentMultiplePlainArgumentsFailsArity : Bool :=
  match runResult (.call (.resolve "content") (alg [] [] [] [.num 1, .num 2, .num 3])) with
  | Except.error err =>
      hasContext "expected 1 arguments" err && innermostIsArityMismatch 0 3 err
  | Except.ok _ => false

#guard contentMultiplePlainArgumentsFailsArity

def contentNestedGroupsPreservesInnerGroups : Bool :=
  match runResult (.call (.resolve "content") (alg [] [] [] [contentNestedExpr])) with
  | Except.ok (.group [
      .group [.atom 1, .atom 2],
      .group [.atom 3, .atom 4]
    ]) => true
  | _ => false

#guard contentNestedGroupsPreservesInnerGroups

def contentDiffersFromAtomsByPreservingNestedGroups : Bool :=
  match runResult (.dotCall contentNestedExpr "content" none),
        runFlat (.dotCall contentNestedExpr "atoms" none) with
  | Except.ok (.group [
      .group [.atom 1, .atom 2],
      .group [.atom 3, .atom 4]
    ]), Except.ok [1, 2, 3, 4] => true
  | _, _ => false

#guard contentDiffersFromAtomsByPreservingNestedGroups

def contentEmitsProjectedTopLevelCount : Bool :=
  match runFlat (.dotCall (.dotCall contentNestedExpr "content" none) "count" none) with
  | Except.ok [2] => true
  | _ => false

#guard contentEmitsProjectedTopLevelCount

-- Test 9: Structural property with params, no args → arity mismatch (navigation-only)
-- a.Inc where Inc(x) = x + 1, no args → error
def incAlg9 : Algorithm :=
  alg ["x"] [] [] [.binary .add (.param "x") (.num 1)]

def receiver9 : Algorithm :=
  algPrivate [] [] [("Inc", incAlg9)] [.num 5]

def test9a : Bool :=
  match runResult (.dotCall (.block receiver9) "Inc" none) with
  | Except.error _ => true   -- arity mismatch: Inc expects 1 arg, got 0
  | Except.ok _ => false

#guard test9a
#eval runResult (.dotCall (.block receiver9) "Inc" none)

-- Test 9b: Structural property with explicit args → direct binding
-- a.Inc(5) where Inc(x) = x + 1 → 6
def test9b : Bool :=
  match runFlat (.dotCall (.block receiver9) "Inc" (some (alg [] [] [] [.num 5]))) with
  | Except.ok [6] => true
  | _ => false

#guard test9b
#eval runFlat (.dotCall (.block receiver9) "Inc" (some (alg [] [] [] [.num 5])))

-- Test 10: dotCall with args (a.X(extra)) passed as builtin argument (navigation-only)
-- Repeat(step, a.Count(bias), init)
-- a has Count(b) = 2 + b, bias = 1 → count = 3
-- step(x) = x + 10, init = 0 → 0→10→20→30
-- Note: Count takes 1 param; no receiver injection in navigation-only semantics
def countAlg : Algorithm :=
  alg ["b"] [] [] [.binary .add (.num 2) (.param "b")]

def receiver10 : Algorithm :=
  algPrivate [] [] [("Count", countAlg)] [.num 99]

def test10 : Bool :=
  match runFlat (.block (algPrivate [] [] [("R", receiver10)] [
    .call (resolve "repeat")
      (alg [] [] [] [
        .block (alg ["x"] [] [] [.binary .add (.param "x") (.num 10)]),  -- step
        .dotCall (resolve "R") "Count" (some (alg [] [] [] [.num 1])),   -- count: R.Count(1) = 3
        .block (alg [] [] [] [.num 0])                                     -- init
      ])
  ])) with
  | Except.ok [30] => true
  | _ => false

#guard test10
#eval runFlat (.block (algPrivate [] [] [("R", receiver10)] [
  .call (resolve "repeat")
    (alg [] [] [] [
      .block (alg ["x"] [] [] [.binary .add (.param "x") (.num 10)]),
      .dotCall (resolve "R") "Count" (some (alg [] [] [] [.num 1])),
      .block (alg [] [] [] [.num 0])
    ])
]))

-- Test 11: dotCall none syntax for count in Repeat argument position
-- Repeat(Add, Numbers.count, 0, 0) where Numbers.count is encoded as .dotCall
-- Numbers = [3,5,9,1,0,6] → count = 6
-- Add(a,sum) = (a+1, sum + Numbers[a])
-- Result: sum of all Numbers = 3+5+9+1+0+6 = 24, extracted via index 1
def numbersAlg11 : Algorithm :=
  alg [] [] [] [.num 3, .num 5, .num 9, .num 1, .num 0, .num 6]

def addAlg11 : Algorithm :=
  alg ["a", "sum"] [] [] [
    .binary .add (.param "a") (.num 1),
    .binary .add (.param "sum") (.index (resolve "Numbers") (.param "a"))
  ]

def testAlg11 : Algorithm :=
  algPrivate [] [] [("Numbers", numbersAlg11), ("Add", addAlg11)] [
    .index
      (.call (resolve "repeat")
        (alg [] [] [] [
          resolve "Add",
          .dotCall (resolve "Numbers") "count" none,     -- ← no-arg dotCall
          .num 0,
          .num 0
        ]))
      (.num 1)
  ]

def test11 : Bool :=
  match runFlat (.block testAlg11) with
  | Except.ok [24] => true
  | _ => false

#guard test11
-- EXPECTED: Except.ok [24]
#eval runFlat (.block testAlg11)

-- Test 12: dotCall count as Repeat count (simple increment)
-- Same as Test 7 but with dotCall none syntax
-- Numbers has 3 outputs → count = 3, step(x) = x + 1, init = 0 → 3
def numbersAlg12 : Algorithm :=
  alg [] [] [] [.num 10, .num 20, .num 30]

def testAlg12 : Algorithm :=
  algPrivate [] [] [("Numbers", numbersAlg12)] [
    .call (resolve "repeat")
      (alg [] [] [] [
        .block (alg ["x"] [] [] [.binary .add (.param "x") (.num 1)]),  -- step
        .dotCall (resolve "Numbers") "count" none,                       -- ← no-arg dotCall
        .block (alg [] [] [] [.num 0])                                   -- init
      ])
  ]

def test12 : Bool :=
  match runFlat (.block testAlg12) with
  | Except.ok [3] => true
  | _ => false

#guard test12
-- EXPECTED: Except.ok [3]
#eval runFlat (.block testAlg12)

-- Regression: recursive dot-call arguments bind both value and algorithm views,
-- but builtin argument preparation must use the current parameter value when it
-- exists. Otherwise atoms(values) re-enters list.skip(1) while list is computing.
def recursiveDotCallListAlg : Algorithm :=
  alg [] [] [] [
    .call (resolve "atoms") (alg [] [] [] [.param "values"])
  ]

def recursiveDotCallReduceCollectionAlg : Algorithm :=
  algPrivate ["values"] [] [("list", recursiveDotCallListAlg)] [
    .call (resolve "if") (alg [] [] [] [
      .binary .le (.dotCall (resolve "list") "count" none) (.num 1),
      resolve "list",
      .dotCall
        (.dotCall (resolve "list") "skip" (some (alg [] [] [] [.num 1])))
        "reduceCollection"
        none
    ])
  ]

def recursiveDotCallRoot : Algorithm :=
  algPrivate [] [] [("reduceCollection", recursiveDotCallReduceCollectionAlg)] [
    .call (resolve "reduceCollection") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2, .num 3, .num 4])
    ])
  ]

def test12a : Bool :=
  match runFlat (.block recursiveDotCallRoot) with
  | Except.ok [4] => true
  | _ => false

#guard test12a

-- Test 13: named multi-output receiver no longer exposes arity
def arityRemovedRoot13 : Algorithm :=
  algPrivate [] [] [("Data", alg [] [] [] [.num 1, .num 7])] [
    .dotCall (resolve "Data") "arity" none
  ]

def test13 : Bool :=
  match runResult (.block arityRemovedRoot13) with
  | Except.error err => innermostIsUnknownName "arity" err
  | Except.ok _ => false

#guard test13
#eval runResult (.block arityRemovedRoot13)

-- Test 14: inline grouped receiver no longer exposes arity
def test14 : Bool :=
  match runResult (.dotCall (.block (alg [] [] [] [.num 1, .num 7])) "arity" none) with
  | Except.error err => innermostIsUnknownName "arity" err
  | Except.ok _ => false

#guard test14
#eval runResult (.dotCall (.block (alg [] [] [] [.num 1, .num 7])) "arity" none)

-- Test 14a: extra grouped receiver layer no longer exposes arity
def test14a : Bool :=
  match runResult (.dotCall (.block (alg [] [] [] [.block (alg [] [] [] [.num 1, .num 7])])) "arity" none) with
  | Except.error err => innermostIsUnknownName "arity" err
  | Except.ok _ => false

#guard test14a
#eval runResult (.dotCall (.block (alg [] [] [] [.block (alg [] [] [] [.num 1, .num 7])])) "arity" none)

-- Test 14b: count still works for named, inline, and nested grouped receivers
def countReceiverRoot14b : Algorithm :=
  algPrivate [] [] [("Data", alg [] [] [] [.num 1, .num 7])] [
    .dotCall (resolve "Data") "count" none,
    .dotCall (.block (alg [] [] [] [.num 1, .num 7])) "count" none,
    .dotCall (.block (alg [] [] [] [.block (alg [] [] [] [.num 1, .num 7])])) "count" none
  ]

def test14b : Bool :=
  match runFlat (.block countReceiverRoot14b) with
  | Except.ok [2, 2, 1] => true
  | _ => false

#guard test14b
#eval runFlat (.block countReceiverRoot14b)

-- Test 14d: old length intrinsic name is no longer recognized
def test14d : Bool :=
  match runResult (.dotCall (.block (alg [] [] [] [.num 1, .num 2])) "length" none) with
  | Except.error _ => true
  | Except.ok _ => false

#guard test14d
#eval runResult (.dotCall (.block (alg [] [] [] [.num 1, .num 2])) "length" none)

-- Test 15: user-defined higher-order call keeps eager value ABI
-- ApplyTwice(f, x) = f(f(x)); passing Inc as an algorithm argument should work.
def incAlg15 : Algorithm :=
  alg ["x"] [] [] [.binary .add (.param "x") (.num 1)]

def applyTwiceAlg15 : Algorithm :=
  alg ["f", "x"] [] [] [
    .call (.param "f") (alg [] [] [] [
      .call (.param "f") (alg [] [] [] [.param "x"])
    ])
  ]

def test15 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Inc", incAlg15), ("ApplyTwice", applyTwiceAlg15)] [
    .call (resolve "ApplyTwice") (alg [] [] [] [resolve "Inc", .num 10])
  ])) with
  | Except.ok [12] => true
  | _ => false

#guard test15
#eval runFlat (.block (algPrivate [] [] [("Inc", incAlg15), ("ApplyTwice", applyTwiceAlg15)] [
  .call (resolve "ApplyTwice") (alg [] [] [] [resolve "Inc", .num 10])
]))

-- Test 16: higher-order args preserve flat fixed expression boundaries.
-- UsePair(f, x, y) = f(x) + y; a grouped second argument is one argument
-- expression, while sequenceSupply supplies x and y explicitly.
def usePairAlg16 : Algorithm :=
  alg ["f", "x", "y"] [] [] [
    .binary .add
      (.call (.param "f") (alg [] [] [] [.param "x"]))
      (.param "y")
  ]

def pairArg16 : Algorithm :=
  alg [] [] [] [.num 10, .num 20]

def test16GroupedArgDoesNotUnpack : Bool :=
  match runResult (.block (algPrivate [] [] [("Inc", incAlg15), ("UsePair", usePairAlg16)] [
    .call (resolve "UsePair") (alg [] [] [] [resolve "Inc", .block pairArg16])
  ])) with
  | Except.error err => innermostIsArityMismatch 1 0 err
  | Except.ok _ => false

#guard test16GroupedArgDoesNotUnpack

def test16SequenceSupplySuppliesValues : Bool :=
  match runFlat (.block (algPrivate [] [] [("Inc", incAlg15), ("UsePair", usePairAlg16)] [
    .call (resolve "UsePair") (alg [] [] [] [resolve "Inc", .sequenceSupply (.num 10) (.num 20)])
  ])) with
  | Except.ok [31] => true
  | _ => false

#guard test16SequenceSupplySuppliesValues
#eval runFlat (.block (algPrivate [] [] [("Inc", incAlg15), ("UsePair", usePairAlg16)] [
  .call (resolve "UsePair") (alg [] [] [] [resolve "Inc", .sequenceSupply (.num 10) (.num 20)])
]))

-- Test 16a: ordinary dot-call fallback preserves receiver as one argument boundary.
def dotCallBoundaryAddAlg16a : Algorithm :=
  alg ["a", "b"] [] [] [
    .binary .add (.param "a") (.param "b")
  ]

def dotCallBoundaryPairReceiverAlg16a : Algorithm :=
  alg [] [] [] [.num 3, .num 7]

def dotCallBoundaryNormalCallsStillWork16a : Bool :=
  match runFlat (.block (algPrivate [] [] [("F", dotCallBoundaryAddAlg16a)] [
    .call (resolve "F") (alg [] [] [] [.num 3, .num 7])
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard dotCallBoundaryNormalCallsStillWork16a

def dotCallBoundaryGroupedDirectCallDoesNotUnpack16a : Bool :=
  match runResult (.block (algPrivate [] [] [("F", dotCallBoundaryAddAlg16a)] [
    .call (resolve "F") (alg [] [] [] [.block dotCallBoundaryPairReceiverAlg16a])
  ])) with
  | Except.error err => innermostIsArityMismatch 1 0 err
  | Except.ok _ => false

#guard dotCallBoundaryGroupedDirectCallDoesNotUnpack16a

def dotCallBoundaryScalarReceiverStillWorks16a : Bool :=
  match runFlat (.block (algPrivate [] [] [("F", dotCallBoundaryAddAlg16a)] [
    .dotCall (.num 3) "F" (some (alg [] [] [] [.num 7]))
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard dotCallBoundaryScalarReceiverStillWorks16a

def dotCallBoundaryMultiOutputReceiverNoArgsFails16a : Bool :=
  match runResult (.block (algPrivate [] [] [("F", dotCallBoundaryAddAlg16a)] [
    .dotCall (.block dotCallBoundaryPairReceiverAlg16a) "F" none
  ])) with
  | Except.error _ => true
  | Except.ok _ => false

#guard dotCallBoundaryMultiOutputReceiverNoArgsFails16a

def dotCallBoundaryMultiOutputReceiverEmptyArgsFails16a : Bool :=
  match runResult (.block (algPrivate [] [] [("F", dotCallBoundaryAddAlg16a)] [
    .dotCall (.block dotCallBoundaryPairReceiverAlg16a) "F" (some (alg [] [] [] []))
  ])) with
  | Except.error _ => true
  | Except.ok _ => false

#guard dotCallBoundaryMultiOutputReceiverEmptyArgsFails16a

def dotCallBoundaryCountedPathDoesNotSpread16a : Bool :=
  match runResult (.block (algPrivate [] [] [("F", dotCallBoundaryAddAlg16a)] [
    .dotCall
      (.dotCall (.block dotCallBoundaryPairReceiverAlg16a) "F" none)
      "count"
      none
  ])) with
  | Except.error _ => true
  | Except.ok _ => false

#guard dotCallBoundaryCountedPathDoesNotSpread16a

def dotCallBoundaryGroupReceiverAlg16a : Algorithm :=
  alg ["x"] [] [] [.param "x"]

def dotCallBoundaryOneParamGetsGroupedReceiver16a : Bool :=
  match runResult (.block (algPrivate [] [] [("G", dotCallBoundaryGroupReceiverAlg16a)] [
    .dotCall (.block dotCallBoundaryPairReceiverAlg16a) "G" none
  ])) with
  | Except.ok (.group [.atom 3, .atom 7]) => true
  | _ => false

#guard dotCallBoundaryOneParamGetsGroupedReceiver16a

def dotCallBoundaryFinalExplicitGroupedArgDoesNotUnpack16a : Bool :=
  let hAlg := alg ["a", "b", "c"] [] [] [
    .binary .add
      (.binary .add (.param "a") (.param "b"))
      (.param "c")
  ]
  match runResult (.block (algPrivate [] [] [("H", hAlg)] [
    .dotCall (.num 3) "H" (some (alg [] [] [] [
      .block (alg [] [] [] [.num 4, .num 5])
    ]))
  ])) with
  | Except.error err => innermostIsArityMismatch 1 0 err
  | Except.ok _ => false

#guard dotCallBoundaryFinalExplicitGroupedArgDoesNotUnpack16a

def dotCallBoundarySequenceSupplySuppliesExtraArgs16a : Bool :=
  let hAlg := alg ["a", "b", "c"] [] [] [
    .binary .add
      (.binary .add (.param "a") (.param "b"))
      (.param "c")
  ]
  match runFlat (.block (algPrivate [] [] [("H", hAlg)] [
    .dotCall (.num 3) "H" (some (alg [] [] [] [
      .sequenceSupply (.num 4) (.num 5)
    ]))
  ])) with
  | Except.ok [12] => true
  | _ => false

#guard dotCallBoundarySequenceSupplySuppliesExtraArgs16a

def flatFixedIssue101PairAlg : Algorithm :=
  alg [] [] [] [.num 10, .num 20]

def flatFixedIssue101GroupedPairAlg : Algorithm :=
  alg [] [] [] [.block flatFixedIssue101PairAlg]

def flatFixedIssue101AddAlg : Algorithm :=
  alg ["x", "y"] [] [] [.binary .add (.param "x") (.param "y")]

def flatFixedIssue101UseAlg : Algorithm :=
  alg ["a", "b", "c"] [] [] [
    .binary .add
      (.binary .add (.param "a") (.param "b"))
      (.param "c")
  ]

def flatFixedIssue101PairDoesNotUnpack : Bool :=
  match runResult (.block (algPrivate [] [] [("Pair", flatFixedIssue101PairAlg), ("Add", flatFixedIssue101AddAlg)] [
    .call (resolve "Add") (alg [] [] [] [resolve "Pair"])
  ])) with
  | Except.error err => innermostIsArityMismatch 1 0 err
  | Except.ok _ => false

#guard flatFixedIssue101PairDoesNotUnpack

def flatFixedIssue101ContentDoesNotSpread : Bool :=
  match runResult (.block (algPrivate [] [] [("Pair", flatFixedIssue101GroupedPairAlg), ("Add", flatFixedIssue101AddAlg)] [
    .call (resolve "Add") (alg [] [] [] [.dotCall (resolve "Pair") "content" none])
  ])) with
  | Except.error err => innermostIsArityMismatch 1 0 err
  | Except.ok _ => false

#guard flatFixedIssue101ContentDoesNotSpread

def flatFixedIssue101SeparateArgsWork : Bool :=
  match runFlat (.block (algPrivate [] [] [("Add", flatFixedIssue101AddAlg)] [
    .call (resolve "Add") (alg [] [] [] [.num 10, .num 20])
  ])) with
  | Except.ok [30] => true
  | _ => false

#guard flatFixedIssue101SeparateArgsWork

def flatFixedIssue101ExplicitIndexingWorks : Bool :=
  match runFlat (.block (algPrivate [] [] [("Pair", flatFixedIssue101PairAlg), ("Add", flatFixedIssue101AddAlg)] [
    .call (resolve "Add") (alg [] [] [] [
      .index (resolve "Pair") (.num 0),
      .index (resolve "Pair") (.num 1)
    ])
  ])) with
  | Except.ok [30] => true
  | _ => false

#guard flatFixedIssue101ExplicitIndexingWorks

def flatFixedIssue101MixedPrefixDoesNotUnpack : Bool :=
  match runResult (.block (algPrivate [] [] [("Tail", alg [] [] [] [.num 2, .num 3]), ("Use", flatFixedIssue101UseAlg)] [
    .call (resolve "Use") (alg [] [] [] [.num 1, resolve "Tail"])
  ])) with
  | Except.error err => innermostIsArityMismatch 1 0 err
  | Except.ok _ => false

#guard flatFixedIssue101MixedPrefixDoesNotUnpack

def flatFixedIssue101SequenceSupplySuppliesArgs : Bool :=
  match runFlat (.block (algPrivate [] [] [("Tail", alg [] [] [] [.num 2, .num 3]), ("Use", flatFixedIssue101UseAlg)] [
    .call (resolve "Use") (alg [] [] [] [.sequenceSupply (.num 1) (resolve "Tail")])
  ])) with
  | Except.ok [6] => true
  | _ => false

#guard flatFixedIssue101SequenceSupplySuppliesArgs

def variadicParameterForwardingCountItemAlg : Algorithm :=
  algWithParameters [
    { name := "values", kind := .variadic },
    { name := "item", kind := .normal }
  ] [] [] [
    .dotCall
      (.dotCall (.param "values") "filter" (some (alg [] [] [] [
        .block (alg ["value"] [] [] [
          .binary .eq (.param "value") (.param "item")
        ])
      ])))
      "count"
      none
  ]

def variadicParameterForwardingModeFreqsExpr : KatLang.Expr :=
  .dotCall
    (.dotCall (.param "values") "distinct" none)
    "map"
    (some (alg [] [] [] [
      .block (alg ["candidate"] [] [] [
        .call (resolve "CountItem") (alg [] [] [] [.param "values", .param "candidate"])
      ])
    ]))

def variadicParameterForwardingDirectUseAlg : Algorithm :=
  algWithParameters [{ name := "values", kind := .variadic }] [] [] [
    .call (resolve "CountItem") (alg [] [] [] [.param "values", .num 1])
  ]

def variadicParameterForwardingDirectCall : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("CountItem", variadicParameterForwardingCountItemAlg),
    ("Use", variadicParameterForwardingDirectUseAlg)
  ] [
    .call (resolve "Use") (alg [] [] [] [.num 1, .num 1, .num 2, .num 4, .num 4])
  ])) with
  | Except.ok [2] => true
  | _ => false

#guard variadicParameterForwardingDirectCall

def variadicParameterForwardingFreqsAlg : Algorithm :=
  algWithParameters [{ name := "values", kind := .variadic }] [] [] [
    variadicParameterForwardingModeFreqsExpr
  ]

def variadicParameterForwardingCallbackBody : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("CountItem", variadicParameterForwardingCountItemAlg),
    ("Mode", variadicParameterForwardingFreqsAlg)
  ] [
    .call (resolve "Mode") (alg [] [] [] [.num 1, .num 1, .num 2, .num 4, .num 4])
  ])) with
  | Except.ok [2, 1, 2] => true
  | _ => false

#guard variadicParameterForwardingCallbackBody

def variadicParameterForwardingModeAlg : Algorithm :=
  algWithParameters [{ name := "values", kind := .variadic }] [] [
    privateProp "Freqs" (alg [] [] [] [variadicParameterForwardingModeFreqsExpr]),
    privateProp "MaxFreq" (alg [] [] [] [.dotCall (resolve "Freqs") "max" none])
  ] [
    .dotCall
      (.dotCall (.param "values") "distinct" none)
      "filter"
      (some (alg [] [] [] [
        .block (alg ["candidate"] [] [] [
          .binary .eq
            (.call (resolve "CountItem") (alg [] [] [] [.param "values", .param "candidate"]))
            (resolve "MaxFreq")
        ])
      ]))
  ]

def variadicParameterForwardingFullMode : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("CountItem", variadicParameterForwardingCountItemAlg),
    ("Mode", variadicParameterForwardingModeAlg)
  ] [
    .call (resolve "Mode") (alg [] [] [] [.num 1, .num 1, .num 2, .num 4, .num 4])
  ])) with
  | Except.ok [1, 4] => true
  | _ => false

#guard variadicParameterForwardingFullMode

def variadicParameterForwardingNonVariadicGroupAlg : Algorithm :=
  alg ["list"] [] [] [.dotCall (.param "list") "count" none]

def variadicParameterForwardingNonVariadicUseAlg : Algorithm :=
  algWithParameters [{ name := "values", kind := .variadic }] [] [] [
    .call (resolve "Group") (alg [] [] [] [.param "values"])
  ]

def variadicParameterForwardingNonVariadicCalleeStaysGrouped : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Group", variadicParameterForwardingNonVariadicGroupAlg),
    ("Use", variadicParameterForwardingNonVariadicUseAlg)
  ] [
    .call (resolve "Use") (alg [] [] [] [.num 10, .num 20, .num 30])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard variadicParameterForwardingNonVariadicCalleeStaysGrouped

def variadicParameterForwardingCountGroupAlg : Algorithm :=
  algWithParameterPatterns [
    .group [.capture { name := "values", kind := .variadic }]
  ] [] [] [.dotCall (.param "values") "count" none]

def variadicParameterForwardingGroupedUseAlg : Algorithm :=
  algWithParameters [{ name := "values", kind := .variadic }] [] [] [
    .call (resolve "CountGroup") (alg [] [] [] [.param "values"])
  ]

def variadicParameterForwardingGroupedVariadicPatternPreservesBehavior : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("CountGroup", variadicParameterForwardingCountGroupAlg),
    ("Use", variadicParameterForwardingGroupedUseAlg)
  ] [
    .call (resolve "Use") (alg [] [] [] [.num 10, .num 20, .num 30])
  ])) with
  | Except.ok [3] => true
  | _ => false

#guard variadicParameterForwardingGroupedVariadicPatternPreservesBehavior

def variadicParameterForwardingGroupedHistoryArg : KatLang.Expr :=
  .block (alg [] [] [] [.num 1, .num 2, .num 3])

def variadicParameterForwardingFindNextAlg : Algorithm :=
  algWithParameters [
    { name := "history", kind := .variadic },
    { name := "pre1", kind := .normal },
    { name := "pre2", kind := .normal }
  ] [] [] [
    .binary .add
      (.binary .add (.dotCall (.param "history") "count" none) (.param "pre1"))
      (.param "pre2")
  ]

def variadicParameterForwardingGroupedStepAlg : Algorithm :=
  algWithParameterPatterns [
    .group [.capture { name := "history", kind := .variadic }],
    .capture { name := "pre2", kind := .normal },
    .capture { name := "pre1", kind := .normal }
  ] [] [] [
    .call (resolve "FindNext") (alg [] [] [] [.param "history", .param "pre1", .param "pre2"])
  ]

def variadicParameterForwardingGroupedVariadicCaptureSuppliesCompatibleSlot : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("FindNext", variadicParameterForwardingFindNextAlg),
    ("YSStep", variadicParameterForwardingGroupedStepAlg)
  ] [
    .call (resolve "YSStep") (alg [] [] [] [variadicParameterForwardingGroupedHistoryArg, .num 2, .num 3])
  ])) with
  | Except.ok [8] => true
  | _ => false

#guard variadicParameterForwardingGroupedVariadicCaptureSuppliesCompatibleSlot

def variadicParameterForwardingCountItemsByOtherNameAlg : Algorithm :=
  algWithParameters [
    { name := "items", kind := .variadic },
    { name := "last", kind := .normal }
  ] [] [] [
    .binary .add (.dotCall (.param "items") "count" none) (.param "last")
  ]

def variadicParameterForwardingGroupedHistoryUseOtherNameAlg : Algorithm :=
  algWithParameterPatterns [
    .group [.capture { name := "history", kind := .variadic }],
    .capture { name := "last", kind := .normal }
  ] [] [] [
    .call (resolve "CountItems") (alg [] [] [] [.param "history", .param "last"])
  ]

def variadicParameterForwardingGroupedVariadicCaptureForwardsByProvenanceNotName : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("CountItems", variadicParameterForwardingCountItemsByOtherNameAlg),
    ("Use", variadicParameterForwardingGroupedHistoryUseOtherNameAlg)
  ] [
    .call (resolve "Use") (alg [] [] [] [variadicParameterForwardingGroupedHistoryArg, .num 7])
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard variadicParameterForwardingGroupedVariadicCaptureForwardsByProvenanceNotName

def variadicParameterForwardingGroupedHistoryNonVariadicUseAlg : Algorithm :=
  algWithParameterPatterns [
    .group [.capture { name := "history", kind := .variadic }],
    .capture { name := "marker", kind := .normal }
  ] [] [] [
    .call (resolve "Group") (alg [] [] [] [.param "history"])
  ]

def variadicParameterForwardingGroupedCaptureKeepsNonVariadicBoundary : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Group", variadicParameterForwardingNonVariadicGroupAlg),
    ("Use", variadicParameterForwardingGroupedHistoryNonVariadicUseAlg)
  ] [
    .call (resolve "Use") (alg [] [] [] [variadicParameterForwardingGroupedHistoryArg, .num 99])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard variadicParameterForwardingGroupedCaptureKeepsNonVariadicBoundary

def variadicParameterForwardingTakeLastAlg : Algorithm :=
  algWithParameters [
    { name := "first", kind := .variadic },
    { name := "last", kind := .normal }
  ] [] [] [
    .dotCall (.param "first") "count" none
  ]

def variadicParameterForwardingGroupedHistoryTakeLastUseAlg : Algorithm :=
  algWithParameterPatterns [
    .group [.capture { name := "history", kind := .variadic }],
    .capture { name := "marker", kind := .normal }
  ] [] [] [
    .call (resolve "TakeLast") (alg [] [] [] [.num 0, .param "history"])
  ]

def variadicParameterForwardingGroupedCaptureOnlyExpandsInTargetVariadicSlot : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("TakeLast", variadicParameterForwardingTakeLastAlg),
    ("Use", variadicParameterForwardingGroupedHistoryTakeLastUseAlg)
  ] [
    .call (resolve "Use") (alg [] [] [] [variadicParameterForwardingGroupedHistoryArg, .num 99])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard variadicParameterForwardingGroupedCaptureOnlyExpandsInTargetVariadicSlot

def variadicParameterForwardingGroupedLoopStepAlg : Algorithm :=
  algWithParameterPatterns [
    .group [.capture { name := "history", kind := .variadic }],
    .capture { name := "pre2", kind := .normal },
    .capture { name := "pre1", kind := .normal }
  ] [] [] [
    .call (resolve "FindNext") (alg [] [] [] [.param "history", .param "pre1", .param "pre2"]),
    .param "pre1",
    .param "pre2"
  ]

def variadicParameterForwardingLoopStepGroupedCaptureSuppliesCompatibleSlot : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("FindNext", variadicParameterForwardingFindNextAlg),
    ("YSStep", variadicParameterForwardingGroupedLoopStepAlg)
  ] [
    .index
      (.dotCall (resolve "YSStep") "repeat" (some (alg [] [] [] [
        .num 1, variadicParameterForwardingGroupedHistoryArg, .num 2, .num 3
      ])))
      (.num 0)
  ])) with
  | Except.ok [8] => true
  | _ => false

#guard variadicParameterForwardingLoopStepGroupedCaptureSuppliesCompatibleSlot

def flatFixedIssue101NestedBlockBoundaryPreserved : Bool :=
  match runResult (.block (algPrivate [] [] [("A", alg [] [] [] [.num 1, .block (alg [] [] [] [.num 2, .num 3])])] [
    resolve "A"
  ])) with
  | Except.ok (.group [.atom 1, .group [.atom 2, .atom 3]]) => true
  | _ => false

#guard flatFixedIssue101NestedBlockBoundaryPreserved

def flatFixedIssue101ExplicitOuterBodyBlockEquivalent : Bool :=
  match runResult (.block (algPrivate [] [] [("A", alg [] [] [] [.block (alg [] [] [] [.num 1, .block (alg [] [] [] [.num 2, .num 3])])])] [
    resolve "A"
  ])) with
  | Except.ok (.group [.atom 1, .group [.atom 2, .atom 3]]) => true
  | _ => false

#guard flatFixedIssue101ExplicitOuterBodyBlockEquivalent

def flatFixedIssue101SequenceSupplyFlattensNestedBlock : Bool :=
  match runFlat (.block (algPrivate [] [] [("A", alg [] [] [] [.sequenceSupply (.num 1) (.block (alg [] [] [] [.num 2, .num 3]))])] [
    resolve "A"
  ])) with
  | Except.ok [1, 2, 3] => true
  | _ => false

#guard flatFixedIssue101SequenceSupplyFlattensNestedBlock

def flatFixedIssue101DotReceiverDoesNotUnpack : Bool :=
  match runResult (.block (algPrivate [] [] [("Pair", flatFixedIssue101PairAlg), ("Add", flatFixedIssue101AddAlg)] [
    .dotCall (resolve "Pair") "Add" none
  ])) with
  | Except.error err => innermostIsArityMismatch 1 0 err
  | Except.ok _ => false

#guard flatFixedIssue101DotReceiverDoesNotUnpack

def flatFixedIssue101SequenceSuppliedDotReceiverDoesNotUnpack : Bool :=
  match runResult (.block (algPrivate [] [] [("Pair", flatFixedIssue101PairAlg), ("Add", flatFixedIssue101AddAlg)] [
    .dotCall (sequenceSuppliedReceiver (resolve "Pair")) "Add" none
  ])) with
  | Except.error err => innermostIsArityMismatch 1 0 err
  | Except.ok _ => false

#guard flatFixedIssue101SequenceSuppliedDotReceiverDoesNotUnpack

def dotCallBoundarySequenceBuiltinsStillExpand16a : Bool :=
  match runFlat (.block (alg [] [] [] [
    .dotCall (.block dotCallBoundaryPairReceiverAlg16a) "sum" none,
    .dotCall (.block dotCallBoundaryPairReceiverAlg16a) "count" none,
    .dotCall (.block dotCallBoundaryPairReceiverAlg16a) "first" none,
    .dotCall (.block dotCallBoundaryPairReceiverAlg16a) "last" none
  ])) with
  | Except.ok [10, 2, 3, 7] => true
  | _ => false

#guard dotCallBoundarySequenceBuiltinsStillExpand16a

-- Test 17: extra higher-order args are not silently ignored
-- TakeFunc(f) called with two algorithm args should raise arity mismatch.
def takeFuncAlg17 : Algorithm :=
  alg ["f"] [] [] [.num 0]

def test17 : Bool :=
  match runResult (.block (algPrivate [] [] [("Inc", incAlg15), ("TakeFunc", takeFuncAlg17)] [
    .call (resolve "TakeFunc") (alg [] [] [] [resolve "Inc", resolve "Inc"])
  ])) with
  | Except.error _ => true
  | Except.ok _ => false

#guard test17
#eval runResult (.block (algPrivate [] [] [("Inc", incAlg15), ("TakeFunc", takeFuncAlg17)] [
  .call (resolve "TakeFunc") (alg [] [] [] [resolve "Inc", resolve "Inc"])
]))

-- Test 18: structural property calls share higher-order binding semantics
-- Receiver.ApplyTwice(Inc, 10) should bind Inc through AlgEnv and return 12.
def receiver18 : Algorithm :=
  algPrivate [] [] [("ApplyTwice", applyTwiceAlg15)] []

def outer18 : Algorithm :=
  algPrivate [] [] [("Inc", incAlg15), ("Receiver", receiver18)] [
    .dotCall (resolve "Receiver") "ApplyTwice" (some (alg [] [] [] [resolve "Inc", .num 10]))
  ]

def test18 : Bool :=
  match runFlat (.block outer18) with
  | Except.ok [12] => true
  | _ => false

#guard test18
#eval runFlat (.block outer18)

-- Test 19: zero-parameter inline blocks passed to higher-order parameters are
-- treated uniformly as value/output structures.
-- Reading the parameter as a value works, but callability is not inferred from
-- having one output, and output count does not change that binding mode.
def constSevenAlg19 : Algorithm :=
  alg [] [] [] [.num 7]

def twoValueAlg19 : Algorithm :=
  alg [] [] [] [.num 1, .num 2]

def readInlineArgAlg19 : Algorithm :=
  alg ["f"] [] [] [
    .param "f"
  ]

def callInlineArgAlg19 : Algorithm :=
  alg ["f"] [] [] [
    .call (.param "f") (alg [] [] [] [])
  ]

def test19 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Apply", readInlineArgAlg19)] [
    .call (resolve "Apply") (alg [] [] [] [.block constSevenAlg19])
  ])) with
  | Except.ok [7] => true
  | _ => false

#guard test19
#eval runFlat (.block (algPrivate [] [] [("Apply", readInlineArgAlg19)] [
  .call (resolve "Apply") (alg [] [] [] [.block constSevenAlg19])
]))

def test19SingleOutputCallRejected : Bool :=
  match runFlat (.block (algPrivate [] [] [("Apply", callInlineArgAlg19)] [
    .call (resolve "Apply") (alg [] [] [] [.block constSevenAlg19])
  ])) with
  | Except.error _ => true
  | _ => false

#guard test19SingleOutputCallRejected
#eval runFlat (.block (algPrivate [] [] [("Apply", callInlineArgAlg19)] [
  .call (resolve "Apply") (alg [] [] [] [.block constSevenAlg19])
]))

def test19MultiOutput : Bool :=
  match runFlat (.block (algPrivate [] [] [("Apply", readInlineArgAlg19)] [
    .call (resolve "Apply") (alg [] [] [] [.block twoValueAlg19])
  ])) with
  | Except.ok [1, 2] => true
  | _ => false

#guard test19MultiOutput
#eval runFlat (.block (algPrivate [] [] [("Apply", readInlineArgAlg19)] [
  .call (resolve "Apply") (alg [] [] [] [.block twoValueAlg19])
]))

def test19MultiOutputCallRejected : Bool :=
  match runFlat (.block (algPrivate [] [] [("Apply", callInlineArgAlg19)] [
    .call (resolve "Apply") (alg [] [] [] [.block twoValueAlg19])
  ])) with
  | Except.error _ => true
  | _ => false

#guard test19MultiOutputCallRejected
#eval runFlat (.block (algPrivate [] [] [("Apply", callInlineArgAlg19)] [
  .call (resolve "Apply") (alg [] [] [] [.block twoValueAlg19])
]))

-- Test 19a: same-name clause-group elaboration classifies a sole plain-binder
-- clause as an ordinary algorithm, not a conditional.
def applyClauseBody19a : Algorithm :=
  alg [] [] [] [
    .call (.param "f") (alg [] [] [] [.param "x"])
  ]

def applyClauseAlg19a : Algorithm :=
  Algorithm.elaborateClauseGroup [{
    pattern := KatLang.Pattern.group [KatLang.Pattern.bind "x", KatLang.Pattern.bind "f"]
    body := applyClauseBody19a
  }]

def test19aShape : Bool :=
  match applyClauseAlg19a with
  | .mk _ [.capture { name := "x", kind := .normal }, .capture { name := "f", kind := .normal }] _ _ _ => true
  | _ => false

#guard test19aShape

def test19aRun : Bool :=
  match runFlat (.block (algPrivate [] [] [("Inc", incAlg15), ("Apply", applyClauseAlg19a)] [
    .call (resolve "Apply") (alg [] [] [] [.num 9, resolve "Inc"])
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard test19aRun

def idClauseAlg19a : Algorithm :=
  Algorithm.elaborateClauseGroup [{
    pattern := KatLang.Pattern.bind "x"
    body := alg [] [] [] [.param "x"]
  }]

def test19aSingleBinderShape : Bool :=
  match Algorithm.elaborateClauseGroup [{
      pattern := KatLang.Pattern.bind "x"
      body := alg [] [] [] [.param "x"]
    }] with
  | .mk _ [.capture { name := "x", kind := .normal }] _ _ _ => true
  | _ => false

#guard test19aSingleBinderShape

def test19aSingleBinderRun : Bool :=
  match runFlat (.block (algPrivate [] [] [("Id", idClauseAlg19a)] [
    .call (resolve "Id") (alg [] [] [] [.num 7])
  ])) with
  | Except.ok [7] => true
  | _ => false

#guard test19aSingleBinderRun

def fallbackClauseAlg19a : Algorithm :=
  Algorithm.elaborateClauseGroup [
    {
      pattern := KatLang.Pattern.litInt 0
      body := alg [] [] [] [.num 0]
    },
    {
      pattern := KatLang.Pattern.bind "x"
      body := alg [] [] [] [.num 1]
    }
  ]

def test19aMultiClauseShape : Bool :=
  match fallbackClauseAlg19a with
  | .conditional _ _ [_, _] => true
  | _ => false

#guard test19aMultiClauseShape

def test19aMultiClauseRun : Bool :=
  match runFlat (.block (algPrivate [] [] [("F", fallbackClauseAlg19a)] [
    .call (resolve "F") (alg [] [] [] [.num 2])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test19aMultiClauseRun

def test19aLiteralPatternIsConditional : Bool :=
  match Algorithm.elaborateClauseGroup [{
      pattern := KatLang.Pattern.litInt 1
      body := alg [] [] [] [.num 42]
    }] with
  | .conditional _ _ [_] => true
  | _ => false

#guard test19aLiteralPatternIsConditional

def test19aGroupedPatternIsOrdinaryStructuredParameter : Bool :=
  match Algorithm.elaborateClauseGroup [{
      pattern := KatLang.Pattern.group [
        KatLang.Pattern.bind "x",
        KatLang.Pattern.group [KatLang.Pattern.bind "acc", KatLang.Pattern.bind "counter"]
      ]
      body := alg [] [] [] [.param "x"]
    }] with
  | .mk _ [.capture { name := "x" }, .group [.capture { name := "acc" }, .capture { name := "counter" }]] _ _ _ => true
  | _ => false

#guard test19aGroupedPatternIsOrdinaryStructuredParameter

-- Test 19b: compatibility fallback for a manually constructed single-branch
-- flat-binder conditional still preserves higher-order args in the core AST.
def applyCondAlg19b : Algorithm :=
  .conditional none [] [
    ⟨ KatLang.Pattern.group [KatLang.Pattern.bind "x", KatLang.Pattern.bind "f"],
      alg [] [] [] [
        .call (.param "f") (alg [] [] [] [.param "x"])
      ] ⟩
  ]

def test19b : Bool :=
  match runFlat (.block (algPrivate [] [] [("Inc", incAlg15), ("Apply", applyCondAlg19b)] [
    .call (resolve "Apply") (alg [] [] [] [.num 9, resolve "Inc"])
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard test19b
#eval runFlat (.block (algPrivate [] [] [("Inc", incAlg15), ("Apply", applyCondAlg19b)] [
  .call (resolve "Apply") (alg [] [] [] [.num 9, resolve "Inc"])
]))

-- Test 19c: structural property call preserves higher-order args for the same subset
def receiver19c : Algorithm :=
  algPrivate [] [] [("Apply", applyCondAlg19b)] []

def test19c : Bool :=
  match runFlat (.block (algPrivate [] [] [("Inc", incAlg15), ("Receiver", receiver19c)] [
    .dotCall (resolve "Receiver") "Apply" (some (alg [] [] [] [.num 9, resolve "Inc"]))
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard test19c
#eval runFlat (.block (algPrivate [] [] [("Inc", incAlg15), ("Receiver", receiver19c)] [
  .dotCall (resolve "Receiver") "Apply" (some (alg [] [] [] [.num 9, resolve "Inc"]))
]))

-- Test 19d: grouped eager values stay whole when a sibling argument binds only
-- through AlgEnv.
def evenPredicateAlg19d : Algorithm :=
  alg ["n"] [] [] [
    .binary .eq
      (.binary .mod (.index (.param "n") (.num 1)) (.num 2))
      (.num 0)
  ]

def occurrenceCountAlg19d : Algorithm :=
  alg ["values", "predicate"] [] [] [
    .dotCall
      (.call (.resolve "filter") (alg [] [] [] [.param "values", .param "predicate"]))
      "count"
      none
  ]

def test19d : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("OccurrenceCount", occurrenceCountAlg19d)
  ] [
    .call (.resolve "OccurrenceCount") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block evenPredicateAlg19d
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test19d
#eval runFlat (.block (algPrivate [] [] [
  ("OccurrenceCount", occurrenceCountAlg19d)
] [
  .call (.resolve "OccurrenceCount") (alg [] [] [] [
    .block (alg [] [] [] [.num 1, .num 2]),
    .block evenPredicateAlg19d
  ])
]))

-- Test 19e: inline predicate captures an outer value parameter rather than
-- re-declaring it as a local parameter.
--
-- Plain-call sequence builtins now expand emitted top-level items from
-- ordinary arguments. This test still uses explicit top-level pair arguments
-- to keep the capture shape obvious.
def occurrenceCountAlg19e : Algorithm :=
  alg ["target"] [] [] [
    .dotCall
      (.call (.resolve "filter") (alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 10]),
        .block (alg [] [] [] [.num 2, .num 20]),
        .block (alg [] [] [] [.num 2, .num 30]),
        .block (alg ["item"] [] [] [
          .binary .eq
            (.index (.param "item") (.num 1))
            (.index (.param "target") (.num 1))
        ])
      ]))
      "count"
      none
  ]

def test19e : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("OccurrenceCount", occurrenceCountAlg19e)
  ] [
    .call (.resolve "OccurrenceCount") (alg [] [] [] [
      .block (alg [] [] [] [.num 2, .num 20])
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test19e
#eval runFlat (.block (algPrivate [] [] [
  ("OccurrenceCount", occurrenceCountAlg19e)
] [
  .call (.resolve "OccurrenceCount") (alg [] [] [] [
    .block (alg [] [] [] [.num 2, .num 20])
  ])
]))

-- if builtin tests
-- if(cond, whenTrue, whenFalse): the only supported form.
--------------------------------------------------------------------------------

-- Test 20: 3-arg if true → produce then-branch value
-- if(1, 5, 6) → [5]
def test20 : Bool :=
  match runFlat (.call (resolve "if") (alg [] [] [] [.num 1, .num 5, .num 6])) with
  | Except.ok [5] => true
  | _ => false

#guard test20
#eval runFlat (.call (resolve "if") (alg [] [] [] [.num 1, .num 5, .num 6]))

-- Test 21: 3-arg if false → produce else-branch value
-- if(0, 5, 6) → [6]
def test21 : Bool :=
  match runFlat (.call (resolve "if") (alg [] [] [] [.num 0, .num 5, .num 6])) with
  | Except.ok [6] => true
  | _ => false

#guard test21
#eval runFlat (.call (resolve "if") (alg [] [] [] [.num 0, .num 5, .num 6]))

--------------------------------------------------------------------------------
-- Conditional algorithm tests
--------------------------------------------------------------------------------

open KatLang (Pattern CondBranch)

-- Test 22: K combinator via conditional algorithm
-- K(a, b) = a  →  K(10, 20) => 10
def kAlg : Algorithm :=
  .conditional none [] [
    ⟨ .group [.bind "a", .bind "b"],
      alg [] [] [] [.param "a"] ⟩
  ]

def test34 : Bool :=
  match runFlat (.block (algPrivate [] [] [("K", kAlg)] [
    .call (resolve "K") (alg [] [] [] [.num 10, .num 20])
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard test34
#eval runFlat (.block (algPrivate [] [] [("K", kAlg)] [
  .call (resolve "K") (alg [] [] [] [.num 10, .num 20])
]))

-- Test 35: Multiple branches with literal match
-- Else(1, (a, b)) = a
-- Else(c, (a, b)) = b
def elseAlg : Algorithm :=
  .conditional none [] [
    ⟨ .group [.litInt 1, .group [.bind "a", .bind "b"]],
      alg [] [] [] [.param "a"] ⟩,
    ⟨ .group [.bind "c", .group [.bind "a", .bind "b"]],
      alg [] [] [] [.param "b"] ⟩
  ]

-- Else(1, (2, 3)) → first branch matches → a = 2
def test35a : Bool :=
  match runFlat (.block (algPrivate [] [] [("Else", elseAlg)] [
    .call (resolve "Else") (alg [] [] [] [.num 1, .block (alg [] [] [] [.num 2, .num 3])])
  ])) with
  | Except.ok [2] => true
  | _ => false

#guard test35a

-- Else(0, (2, 3)) → second branch matches → b = 3
def test35b : Bool :=
  match runFlat (.block (algPrivate [] [] [("Else", elseAlg)] [
    .call (resolve "Else") (alg [] [] [] [.num 0, .block (alg [] [] [] [.num 2, .num 3])])
  ])) with
  | Except.ok [3] => true
  | _ => false

#guard test35b

-- Test 36: Non-exhaustive — no match → error
-- Sign(1) = 1; Sign(-1) = -1;  Sign(0) → noMatchingBranch
def signAlg : Algorithm :=
  .conditional none [] [
    ⟨ .litInt 1,  alg [] [] [] [.num 1] ⟩,
    ⟨ .litInt (-1), alg [] [] [] [.num (-1)] ⟩
  ]

def test36 : Bool :=
  match runResult (.block (algPrivate [] [] [("Sign", signAlg)] [
    .call (resolve "Sign") (alg [] [] [] [.num 0])
  ])) with
  | Except.error _ => true    -- noMatchingBranch
  | Except.ok _    => false

#guard test36

-- Test 37: First-match-wins
-- F(x) = 1  (catch-all, always matches)
-- F(1) = 2  (never reached)
-- F(1) → 1
def firstMatchAlg : Algorithm :=
  .conditional none [] [
    ⟨ .bind "x", alg [] [] [] [.num 1] ⟩,
    ⟨ .litInt 1,  alg [] [] [] [.num 2] ⟩
  ]

def test37 : Bool :=
  match runFlat (.block (algPrivate [] [] [("F", firstMatchAlg)] [
    .call (resolve "F") (alg [] [] [] [.num 1])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test37

-- Test 22: 2-arg if is rejected
def test22 : Bool :=
  match runResult (.call (resolve "if") (alg [] [] [] [.num 1, .num 5])) with
  | Except.error _ => true
  | Except.ok _ => false

#guard test22
#eval runResult (.call (resolve "if") (alg [] [] [] [.num 1, .num 5]))

-- Test 23: 2-arg if in addition is rejected
def test23 : Bool :=
  match runResult (.binary .add (.num 10) (.call (resolve "if") (alg [] [] [] [.num 1, .num 5]))) with
  | Except.error _ => true
  | Except.ok _ => false

#guard test23
#eval runResult (.binary .add (.num 10) (.call (resolve "if") (alg [] [] [] [.num 1, .num 5])))

-- Test 24: 2-arg if in multiplication is rejected
def test24 : Bool :=
  match runResult (.binary .mul (.num 10) (.call (resolve "if") (alg [] [] [] [
    .binary .lt (.num 7) (.num 6),
    .num 1
  ]))) with
  | Except.error _ => true
  | Except.ok _ => false

#guard test24
#eval runResult (.binary .mul (.num 10) (.call (resolve "if") (alg [] [] [] [
  .binary .lt (.num 7) (.num 6),
  .num 1
])))

-- Test 25: Sequence supply with 3-arg if selects the else branch
-- 1, if(0, 2, 9), 3 → [1, 9, 3]
def test25 : Bool :=
  match runFlat (.sequenceSupply (.num 1) (.sequenceSupply (.call (resolve "if") (alg [] [] [] [.num 0, .num 2, .num 9])) (.num 3))) with
  | Except.ok [1, 9, 3] => true
  | _ => false

#guard test25
#eval runFlat (.sequenceSupply (.num 1) (.sequenceSupply (.call (resolve "if") (alg [] [] [] [.num 0, .num 2, .num 9])) (.num 3)))

def sequenceSupply1234 : KatLang.Expr :=
  .sequenceSupply (.sequenceSupply (.sequenceSupply (.num 1) (.num 2)) (.num 3)) (.num 4)

def test25a : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "sum") (alg [] [] [] [sequenceSupply1234]),
    .call (resolve "count") (alg [] [] [] [sequenceSupply1234]),
    .call (resolve "first") (alg [] [] [] [sequenceSupply1234]),
    .call (resolve "last") (alg [] [] [] [sequenceSupply1234])
  ])) with
  | Except.ok [10, 4, 1, 4] => true
  | _ => false

#guard test25a

def test25b : Bool :=
  let groupedLeft := .sequenceSupply (.block (alg [] [] [] [.num 1, .num 2])) (.num 3)
  let groupedRight := .sequenceSupply (.num 1) (.block (alg [] [] [] [.num 2, .num 3]))
  match runFlat (.block (alg [] [] [] [
    .call (resolve "count") (alg [] [] [] [groupedLeft]),
    .call (resolve "count") (alg [] [] [] [groupedRight])
  ])) with
  | Except.ok [3, 3] => true
  | _ => false

#guard test25b

def test25bNestedGroups : Bool :=
  let nestedLeft := .sequenceSupply (.block (alg [] [] [] [.block (alg [] [] [] [.num 1, .num 2])])) (.num 3)
  let nestedMiddle := .sequenceSupply (.block (alg [] [] [] [.num 1, .block (alg [] [] [] [.num 2, .num 3])])) (.num 4)
  match runResult (.block (alg [] [] [] [nestedLeft, nestedMiddle])) with
  | Except.ok value =>
      value == Result.group [
        Result.group [Result.group [Result.atom 1, Result.atom 2], Result.atom 3],
        Result.group [Result.atom 1, Result.group [Result.atom 2, Result.atom 3], Result.atom 4]
      ]
  | _ => false

#guard test25bNestedGroups

def sequenceSupplyNamedGroupedOperandPreservesBoundary : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("A", alg [] [] [] [.block (alg [] [] [] [.num 1, .num 2])])
  ] [
    .sequenceSupply (resolve "A") (.num 3)
  ])) with
  | Except.ok (.group [.group [.atom 1, .atom 2], .atom 3]) => true
  | _ => false

#guard sequenceSupplyNamedGroupedOperandPreservesBoundary

def test25bCommaSimilarity : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("A", alg [] [] [] [.num 1, .num 2]),
    ("B", alg [] [] [] [.sequenceSupply (.num 1) (.num 2)])
  ] [
    .dotCall (resolve "A") "count" none,
    .dotCall (resolve "B") "count" none
  ])) with
  | Except.ok [2, 2] => true
  | _ => false

#guard test25bCommaSimilarity

def test25c : Bool :=
  let pThenMore := .sequenceSupply (.sequenceSupply (.sequenceSupply (resolve "P") (.num 3)) (.num 4)) (.num 5)
  match runFlat (.block (algPrivate [] [] [
    ("P", alg [] [] [] [.num 1, .num 2]),
    ("X", alg [] [] [] [.call (resolve "sum") (alg [] [] [] [pThenMore])])
  ] [
    resolve "X"
  ])) with
  | Except.ok [15] => true
  | _ => false

#guard test25c

def test25dResultShape : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("A", alg [] [] [] [.num 1, .num 2]),
    ("F", alg ["a"] [] [] [.param "a", .num 3])
  ] [
    .dotCall (resolve "A") "F" none
  ])) with
  | Except.ok value =>
      value == Result.group [Result.group [Result.atom 1, Result.atom 2], Result.atom 3]
  | _ => false

#guard test25dResultShape

def test25e : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("A", alg [] [] [] [.num 1, .num 2]),
    ("F", alg ["a"] [] [] [.sequenceSupply (.param "a") (.num 3)])
  ] [
    .dotCall (resolve "A") "F" none
  ])) with
  | Except.ok [1, 2, 3] => true
  | _ => false

#guard test25e

def test25f : Bool :=
  let a := alg [] [] [publicProp "X" (alg [] [] [] [.num 1])] [.num 10]
  let b := alg [] [] [publicProp "Y" (alg [] [] [] [.num 2])] [.num 20]
  match runFlat (.block (algPrivate [] [] [
    ("A", a),
    ("B", b),
    ("C", alg [] [] [] [.sequenceSupply (resolve "A") (resolve "B")])
  ] [
    resolve "C"
  ])) with
  | Except.ok [10, 20] => true
  | _ => false

#guard test25f

def test25g : Bool :=
  let a := alg [] [] [publicProp "X" (alg [] [] [] [.num 1])] [.num 10]
  let b := alg [] [] [publicProp "Y" (alg [] [] [] [.num 2])] [.num 20]
  match runFlat (.block (algPrivate [] [] [
    ("A", a),
    ("B", b),
    ("C", alg [] [] [] [.sequenceSupply (resolve "A") (resolve "B")])
  ] [
    .dotCall (resolve "C") "X" none
  ])) with
  | Except.error err => innermostIsUnknownName "X" err
  | _ => false

#guard test25g

def test25h : Bool :=
  let bad := .block (alg [] [] [privateProp "X" (alg [] [] [] [.num 1])] [])
  match runFlat (.sequenceSupply bad (.num 3)) with
  | Except.error err => innermostIsSequenceSupplyMissingOutput "left" err
  | _ => false

#guard test25h

def test25i : Bool :=
  let bad := .block (alg [] [] [privateProp "X" (alg [] [] [] [.num 1])] [])
  match runFlat (.sequenceSupply (.num 3) bad) with
  | Except.error err => innermostIsSequenceSupplyMissingOutput "right" err
  | _ => false

#guard test25i

def test25j : Bool :=
  let a := alg [] [] [publicProp "X" (alg [] [] [] [.num 1])] []
  let b := alg [] [] [publicProp "Y" (alg [] [] [] [.num 2])] []
  match runFlat (.block (algPrivate [] [.sequenceSupply (resolve "A") (resolve "B")] [
    ("A", a),
    ("B", b)
  ] [
    .binary .add (resolve "X") (resolve "Y")
  ])) with
  | Except.error err => innermostIsBadOpenForm "sequenceSupply: A...B" err
  | _ => false

#guard test25j

-- Test 26: Nested 3-arg if uses the selected inner branch
-- if(1, if(1, 5, 6), 9) → [5]
def test26 : Bool :=
  match runFlat (.call (resolve "if") (alg [] [] [] [
    .num 1,
    .call (resolve "if") (alg [] [] [] [.num 1, .num 5, .num 6]),
    .num 9
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test26

-- Test 27: Nested 3-arg if uses the outer else branch
-- if(0, if(1, 5, 6), 9) → [9]
def test27 : Bool :=
  match runFlat (.call (resolve "if") (alg [] [] [] [
    .num 0,
    .call (resolve "if") (alg [] [] [] [.num 1, .num 5, .num 6]),
    .num 9
  ])) with
  | Except.ok [9] => true
  | _ => false

#guard test27

-- Test 28: 3-arg if still works — if(1, 10, 20) → [10]
def test28 : Bool :=
  match runFlat (.call (resolve "if") (alg [] [] [] [.num 1, .num 10, .num 20])) with
  | Except.ok [10] => true
  | _ => false

#guard test28

-- Test 29: 3-arg if false → if(0, 10, 20) → [20]
def test29 : Bool :=
  match runFlat (.call (resolve "if") (alg [] [] [] [.num 0, .num 10, .num 20])) with
  | Except.ok [20] => true
  | _ => false

#guard test29

-- Test 30: 3-arg if with non-zero condition → true
-- if(42, 7, 9) → [7]
def test30 : Bool :=
  match runFlat (.call (resolve "if") (alg [] [] [] [.num 42, .num 7, .num 9])) with
  | Except.ok [7] => true
  | _ => false

#guard test30

-- Test 31: 3-arg if with negative condition → true
-- if(-1, 7, 9) → [7]
def test31 : Bool :=
  match runFlat (.call (resolve "if") (alg [] [] [] [.num (-1), .num 7, .num 9])) with
  | Except.ok [7] => true
  | _ => false

#guard test31

--------------------------------------------------------------------------------
-- string intrinsic tests
--------------------------------------------------------------------------------

-- Test 52: string intrinsic on positive integer via algorithm
-- (block [123]).string → Result.str "123"
def test52 : Bool :=
  match runResult (.dotCall (.block (alg [] [] [] [.num 123])) "string" none) with
  | Except.ok (Result.str "123") => true
  | _ => false

#guard test52
#eval runResult (.dotCall (.block (alg [] [] [] [.num 123])) "string" none)

-- Test 53: string intrinsic on zero
-- (block [0]).string → Result.str "0"
def test53 : Bool :=
  match runResult (.dotCall (.block (alg [] [] [] [.num 0])) "string" none) with
  | Except.ok (Result.str "0") => true
  | _ => false

#guard test53

-- Test 54: string intrinsic on negative integer
-- (block [-5]).string → Result.str "-5"
def test54 : Bool :=
  match runResult (.dotCall (.block (alg [] [] [] [.num (-5)])) "string" none) with
  | Except.ok (Result.str "-5") => true
  | _ => false

#guard test54

-- Test 55: string intrinsic on a named property
-- A = 123; A.string → Result.str "123"
def test55 : Bool :=
  let innerAlg := algPrivate [] [] [("A", alg [] [] [] [.num 123])] [
    .dotCall (.resolve "A") "string" none
  ]
  match runResult (.block innerAlg) with
  | Except.ok (Result.str "123") => true
  | _ => false

#guard test55

-- Test 56: string intrinsic on numeric literal (notAnAlgorithm path)
-- (.num 42).string → Result.str "42"
def test56 : Bool :=
  match runResult (.dotCall (.num 42) "string" none) with
  | Except.ok (Result.str "42") => true
  | _ => false

#guard test56

-- Test 57: string intrinsic on string literal → typeMismatch error
-- ("hello").string → Error.typeMismatch
def test57 : Bool :=
  match runResult (.dotCall (.stringLiteral "hello") "string" none) with
  | Except.error _ => true
  | _ => false

#guard test57

-- Test 58: string intrinsic on multi-output → typeMismatch error
-- (1, 2).string → Error (group is not a numeric atom)
def test58 : Bool :=
  match runResult (.dotCall (.block (alg [] [] [] [.num 1, .num 2])) "string" none) with
  | Except.error _ => true
  | _ => false

#guard test58

--------------------------------------------------------------------------------
-- range builtin tests
--------------------------------------------------------------------------------

-- Test 59: ascending inclusive range
def test59 : Bool :=
  match runFlat (.call (resolve "range") (alg [] [] [] [.num 1, .num 10])) with
  | Except.ok [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] => true
  | _ => false

#guard test59

-- Test 60: descending inclusive range
def test60 : Bool :=
  match runFlat (.call (resolve "range") (alg [] [] [] [.num 10, .num 1])) with
  | Except.ok [10, 9, 8, 7, 6, 5, 4, 3, 2, 1] => true
  | _ => false

#guard test60

-- Test 61: equal bounds produce a singleton
def test61 : Bool :=
  match runFlat (.call (resolve "range") (alg [] [] [] [.num 5, .num 5])) with
  | Except.ok [5] => true
  | _ => false

#guard test61

-- Test 62: negative to positive bounds remain inclusive and ordered
def test62 : Bool :=
  match runFlat (.call (resolve "range") (alg [] [] [] [.num (-2), .num 2])) with
  | Except.ok [-2, -1, 0, 1, 2] => true
  | _ => false

#guard test62

-- Test 32: Unary / binary composition with 2-arg if is rejected
def test32 : Bool :=
  match runResult (.binary .add (.num 10) (.unary .minus (.call (resolve "if") (alg [] [] [] [.num 0, .num 5])))) with
  | Except.error _ => true
  | Except.ok _ => false

#guard test32

-- Test 33: if arity mismatch — 1 arg → error
def test33 : Bool :=
  match runResult (.call (resolve "if") (alg [] [] [] [.num 1])) with
  | Except.error _ => true
  | Except.ok _ => false

#guard test33

--------------------------------------------------------------------------------
-- String literal tests (first-class string values)
--------------------------------------------------------------------------------

-- Test 38: String literal evaluates to Result.str
def test38 : Bool :=
  match runResult (.stringLiteral "hello") with
  | Except.ok (.str "hello") => true
  | _ => false

#guard test38

-- Test 39: String equality — same values
def test39 : Bool :=
  match runFlat (.binary .eq (.stringLiteral "a") (.stringLiteral "a")) with
  | Except.ok [1] => true
  | _ => false

#guard test39

-- Test 40: String equality — different values
def test40 : Bool :=
  match runFlat (.binary .eq (.stringLiteral "a") (.stringLiteral "b")) with
  | Except.ok [0] => true
  | _ => false

#guard test40

-- Test 41: String inequality
def test41 : Bool :=
  match runFlat (.binary .ne (.stringLiteral "a") (.stringLiteral "b")) with
  | Except.ok [1] => true
  | _ => false

#guard test41

-- Test 42: String equality is case-sensitive
def test42 : Bool :=
  match runFlat (.binary .eq (.stringLiteral "Apples") (.stringLiteral "apples")) with
  | Except.ok [0] => true
  | _ => false

#guard test42

-- Test 43: Unsupported binary operation on strings → typeMismatch
def test43 : Bool :=
  match runResult (.binary .add (.stringLiteral "a") (.stringLiteral "b")) with
  | Except.error (Error.typeMismatch _) => true
  | _ => false

#guard test43

-- Test 44: Mixed string/number in binary → typeMismatch
def test44 : Bool :=
  match runResult (.binary .add (.num 1) (.stringLiteral "a")) with
  | Except.error (Error.typeMismatch _) => true
  | _ => false

#guard test44

-- Test 45: Unary minus on string → typeMismatch
def test45 : Bool :=
  match runResult (.unary .minus (.stringLiteral "hello")) with
  | Except.error (Error.typeMismatch _) => true
  | _ => false

#guard test45

def numericScalarModLeftGroupMessage : String :=
  "operator `mod` expects numeric scalar operands, but the left operand was a group with 4 items: (3, 4, 5, 6)"

def numericScalarModRightGroupMessage : String :=
  "operator `mod` expects numeric scalar operands, but the right operand was a group with 4 items: (3, 4, 5, 6)"

-- Test 45a: grouped left operand in a numeric operator reports scalar shape
def test45a : Bool :=
  match runResult (.binary .mod
    (.block (alg [] [] [] [.num 3, .num 4, .num 5, .num 6]))
    (.num 2)) with
  | Except.error err =>
      hasContext "while evaluating `(3, 4, 5, 6) mod 2`" err &&
      innermostIsTypeMismatch numericScalarModLeftGroupMessage err
  | _ => false

#guard test45a

-- Test 45b: grouped right operand in a numeric operator reports scalar shape
def test45b : Bool :=
  match runResult (.binary .mod
    (.num 2)
    (.block (alg [] [] [] [.num 3, .num 4, .num 5, .num 6]))) with
  | Except.error err =>
      hasContext "while evaluating `2 mod (3, 4, 5, 6)`" err &&
      innermostIsTypeMismatch numericScalarModRightGroupMessage err
  | _ => false

#guard test45b

-- Test 46: Conditional algorithm with string literal pattern
-- Price('apples') = 0.80  (using Int for simplicity: 80)
def priceAlg : Algorithm :=
  .conditional none [] [
    ⟨ .litString "apples",  alg [] [] [] [.num 80] ⟩,
    ⟨ .litString "tomatoes", alg [] [] [] [.num 120] ⟩
  ]

def test46 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Price", priceAlg)] [
    .call (resolve "Price") (alg [] [] [] [.stringLiteral "apples"])
  ])) with
  | Except.ok [80] => true
  | _ => false

#guard test46

-- Test 47: Conditional algorithm with string pattern — no match
def test47 : Bool :=
  match runResult (.block (algPrivate [] [] [("Price", priceAlg)] [
    .call (resolve "Price") (alg [] [] [] [.stringLiteral "bananas"])
  ])) with
  | Except.error _ => true   -- noMatchingBranch
  | Except.ok _    => false

#guard test47

-- Test 48: String passed as algorithm argument
-- Echo = x, Echo('hello') → 'hello'
def echoAlg : Algorithm := alg ["x"] [] [] [.param "x"]
def test48 : Bool :=
  match runResult (.block (algPrivate [] [] [("Echo", echoAlg)] [
    .call (resolve "Echo") (alg [] [] [] [.stringLiteral "hello"])
  ])) with
  | Except.ok (.str "hello") => true
  | _ => false

#guard test48

-- Test 49: String stored in property and returned
-- Name = 'KatLang', output = Name
def test49 : Bool :=
  let nameAlg := alg [] [] [] [.stringLiteral "KatLang"]
  match runResult (.block (algPrivate [] [] [("Name", nameAlg)] [resolve "Name"])) with
  | Except.ok (.str "KatLang") => true
  | _ => false

#guard test49

-- Test 50: Pattern matching — litString in isMatchEquivalent
def test50a : Bool := Pattern.isMatchEquivalent (.litString "a") (.litString "a")
def test50b : Bool := !Pattern.isMatchEquivalent (.litString "a") (.litString "b")
def test50c : Bool := !Pattern.isMatchEquivalent (.litString "a") (.litInt 1)
def test50d : Bool := !Pattern.isMatchEquivalent (.litString "a") (.bind "x")

#guard test50a
#guard test50b
#guard test50c
#guard test50d

-- Test 51: Block with unresolved implicit params → unresolvedImplicitParams error
-- A block whose algorithm has params (unresolved names become params) should
-- produce unresolvedImplicitParams, not arityMismatch.
def test51 : Bool :=
  -- param "x" makes the block have params=["x"]
  match runResult (.block (alg ["x"] [] [] [.param "x"])) with
  | Except.error (Error.unresolvedImplicitParams ["x"]) => true
  | _ => false

#guard test51

--------------------------------------------------------------------------------
-- filter builtin tests
--------------------------------------------------------------------------------

def isEvenAlg63 : Algorithm :=
  alg ["x"] [] [] [.binary .eq (.binary .mod (.param "x") (.num 2)) (.num 0)]

def isPositiveAlg64 : Algorithm :=
  alg ["x"] [] [] [.binary .gt (.param "x") (.num 0)]

def isNegativeAlg65 : Algorithm :=
  alg ["x"] [] [] [.binary .lt (.param "x") (.num 0)]

def badTruthAlg66 : Algorithm :=
  alg ["x"] [] [] [.stringLiteral "not-a-number"]

def alwaysFalseAlg66a : Algorithm :=
  alg ["x"] [] [] [.num 0]

def keepTenGroupAlg66b : Algorithm :=
  .conditional none [] [
    ⟨ .group [
        .group [
          .bind "a", .bind "b", .bind "c", .bind "d", .bind "e",
          .bind "f", .bind "g", .bind "h", .bind "i", .bind "j"
        ]
      ],
      alg [] [] [] [.num 1] ⟩,
    ⟨ .bind "x", alg [] [] [] [.num 0] ⟩
  ]

def keepFourGroupAlg66c : Algorithm :=
  .conditional none [] [
    ⟨ .group [.group [.bind "a", .bind "b", .bind "c", .bind "d"]],
      alg [] [] [] [.num 1] ⟩,
    ⟨ .bind "x", alg [] [] [] [.num 0] ⟩
  ]

def rejectFourGroupAlg66d : Algorithm :=
  .conditional none [] [
    ⟨ .group [.group [.bind "a", .bind "b", .bind "c", .bind "d"]],
      alg [] [] [] [.num 0] ⟩,
    ⟨ .bind "x", alg [] [] [] [.num 1] ⟩
  ]

def markThreeGroupAlg66e : Algorithm :=
  .conditional none [] [
    ⟨ .group [.group [.bind "a", .bind "b", .bind "c"]],
      alg [] [] [] [.num 1] ⟩,
    ⟨ .bind "x", alg [] [] [] [.num 0] ⟩
  ]

def keepPairAlg67 : Algorithm :=
  .conditional none [] [
    ⟨ .group [.bind "tag", .bind "value"],
      alg [] [] [] [.binary .eq (.binary .mod (.param "tag") (.num 2)) (.num 0)] ⟩
  ]

def badMultiFalseAlg68 : Algorithm :=
  alg ["x"] [] [] [.num 0, .num 999]

def badMultiTrueAlg69 : Algorithm :=
  alg ["x"] [] [] [.num 5, .num 0]

def badGroupedAlg70 : Algorithm :=
  alg ["x"] [] [] [.block (alg [] [] [] [.num 1, .num 0])]

def emptyTruthAlg71 : Algorithm :=
  alg ["x"] [] [] [
    .call (resolve "take") (alg [] [] [] [
      .param "x",
      .num 0
    ])
  ]

-- Test 63: plain-call filter iterates emitted range items
def test63 : Bool :=
  match runFlat (.block (algPrivate [] [] [("KeepTenGroup", keepTenGroupAlg66b)] [
    .call (resolve "filter") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 10])),
      .resolve "KeepTenGroup"
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test63

-- Test 64: descending ranges iterate emitted items in plain-call filter
def test64 : Bool :=
  match runFlat (.block (algPrivate [] [] [("KeepTenGroup", keepTenGroupAlg66b)] [
    .call (resolve "filter") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 10, .num 1])),
      .resolve "KeepTenGroup"
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test64

-- Test 65: a grouped-only predicate does not match scalar emitted range items
def test65 : Bool :=
  match runFlat (.block (algPrivate [] [] [("KeepFourGroup", keepFourGroupAlg66c)] [
    .call (resolve "filter") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
      .resolve "KeepFourGroup"
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test65

-- Test 66: a grouped-only rejection predicate keeps scalar emitted range items
def test66 : Bool :=
  match runFlat (.block (algPrivate [] [] [("RejectFourGroup", rejectFourGroupAlg66d)] [
    .call (resolve "filter") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
      .resolve "RejectFourGroup"
    ])
  ])) with
  | Except.ok [1, 2, 3, 4] => true
  | _ => false

#guard test66

-- Variadic-style top-level sequence binding contract.

-- A normal range argument is one grouped item; explicit sequenceSupply supplies items.
def sequenceBoundaryLawFilterCommaRangeSourcePreservesBoundary : Bool :=
  match runFlat (.block (algPrivate [] [] [("IsEven", isEvenAlg63)] [
    .call (resolve "filter") (alg [] [] [] [
      .call (resolve "range") (alg [] [] [] [.num 3, .num 6]),
      .num 8,
      .resolve "IsEven"
    ])
  ])) with
  | Except.error _ => true
  | Except.ok _ => false

#guard sequenceBoundaryLawFilterCommaRangeSourcePreservesBoundary

-- Explicit sequence supply projects range content for filter.
def sequenceBoundaryLawFilterSequenceSupplyRangeSourceExpands : Bool :=
  match runFlat (.block (algPrivate [] [] [("IsEven", isEvenAlg63)] [
    .call (resolve "filter") (alg [] [] [] [
      .sequenceSupply
        (.call (resolve "range") (alg [] [] [] [.num 3, .num 6]))
        (.num 8),
      .resolve "IsEven"
    ])
  ])) with
  | Except.ok [4, 6, 8] => true
  | _ => false

#guard sequenceBoundaryLawFilterSequenceSupplyRangeSourceExpands

-- Named multi-output single source is one grouped item unless explicitly supplied.
def sequenceBoundaryLawFilterNamedSingleSourcePreservesBoundary : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("IsEven", isEvenAlg63),
    ("Data", alg [] [] [] [.num 3, .num 4, .num 5, .num 6])
  ] [
    .call (resolve "filter") (alg [] [] [] [
      .resolve "Data",
      .resolve "IsEven"
    ])
  ])) with
  | Except.error _ => true
  | Except.ok _ => false

#guard sequenceBoundaryLawFilterNamedSingleSourcePreservesBoundary

-- Named multi-output dot-call receiver is one source and iterates receiver items.
def sequenceBoundaryLawFilterDotReceiverExpands : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("IsEven", isEvenAlg63),
    ("Data", alg [] [] [] [.num 3, .num 4, .num 5, .num 6])
  ] [
    .dotCall (.resolve "Data") "filter" (some (alg [] [] [] [.resolve "IsEven"]))
  ])) with
  | Except.ok [4, 6] => true
  | _ => false

#guard sequenceBoundaryLawFilterDotReceiverExpands

-- Named multi-output plus a comma-separated scalar still preserves the named boundary.
def sequenceBoundaryLawFilterCommaNamedSourcePreservesBoundary : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("IsEven", isEvenAlg63),
    ("Data", alg [] [] [] [.num 3, .num 4, .num 5, .num 6])
  ] [
    .call (resolve "filter") (alg [] [] [] [
      .resolve "Data",
      .num 8,
      .resolve "IsEven"
    ])
  ])) with
  | Except.error _ => true
  | Except.ok _ => false

#guard sequenceBoundaryLawFilterCommaNamedSourcePreservesBoundary

-- Sequence supply explicitly exposes named multi-output content.
def sequenceBoundaryLawFilterSequenceSupplyNamedSourceExpands : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("IsEven", isEvenAlg63),
    ("Data", alg [] [] [] [.num 3, .num 4, .num 5, .num 6])
  ] [
    .call (resolve "filter") (alg [] [] [] [
      .sequenceSupply (.resolve "Data") (.num 8),
      .resolve "IsEven"
    ])
  ])) with
  | Except.ok [4, 6, 8] => true
  | _ => false

#guard sequenceBoundaryLawFilterSequenceSupplyNamedSourceExpands

-- Test 67: filtering an already-empty grouped boundary stays empty
def test67 : Bool :=
  match runFlat (.block (algPrivate [] [] [("KeepFourGroup", keepFourGroupAlg66c), ("RejectFourGroup", rejectFourGroupAlg66d)] [
    .call (resolve "filter") (alg [] [] [] [
      sequenceSupply (.call (resolve "filter") (alg [] [] [] [
        sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
        .resolve "RejectFourGroup"
      ])),
      .resolve "KeepFourGroup"
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test67

-- Test 68: grouped elements are preserved whole and in order
def test68 : Bool :=
  match runResult (.block (algPrivate [] [] [("KeepPair", keepPairAlg67)] [
    .call (resolve "filter") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 10]),
      .block (alg [] [] [] [.num 2, .num 20]),
      .block (alg [] [] [] [.num 3, .num 30]),
      .block (alg [] [] [] [.num 4, .num 40]),
      .resolve "KeepPair"
    ])
  ])) with
  | Except.ok (.group [
      .group [.atom 2, .atom 20],
      .group [.atom 4, .atom 40]
    ]) => true
  | _ => false

#guard test68

-- Test 69: multi-output predicate starting with 0 is rejected
def test69 : Bool :=
  match runResult (.block (algPrivate [] [] [("Bad", badMultiFalseAlg68)] [
    .call (resolve "filter") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 3])),
      .resolve "Bad"
    ])
  ])) with
  | Except.error err => hasContext "filter predicate must return exactly one atomic numeric value" err && innermostIsBadArity err
  | _ => false

#guard test69

-- Test 70: multi-output predicate starting with nonzero is also rejected
def test70 : Bool :=
  match runResult (.block (algPrivate [] [] [("Bad", badMultiTrueAlg69)] [
    .call (resolve "filter") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 3])),
      .resolve "Bad"
    ])
  ])) with
  | Except.error err => hasContext "filter predicate must return exactly one atomic numeric value" err && innermostIsBadArity err
  | _ => false

#guard test70

-- Test 71: grouped predicate result is rejected
def test71 : Bool :=
  match runResult (.block (algPrivate [] [] [("Bad", badGroupedAlg70)] [
    .call (resolve "filter") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 3])),
      .resolve "Bad"
    ])
  ])) with
  | Except.error err => hasContext "filter predicate must return exactly one atomic numeric value" err && innermostIsBadArity err
  | _ => false

#guard test71

-- Test 72: empty predicate result is rejected
def test72 : Bool :=
  match runResult (.block (algPrivate [] [] [("Bad", emptyTruthAlg71)] [
    .call (resolve "filter") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 3])),
      .resolve "Bad"
    ])
  ])) with
  | Except.error err => hasContext "filter predicate must return exactly one atomic numeric value" err && innermostIsBadArity err
  | _ => false

#guard test72

-- Test 73: string predicate result is rejected
def test73 : Bool :=
  match runResult (.block (algPrivate [] [] [("BadTruth", badTruthAlg66)] [
    .call (resolve "filter") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 3])),
      .resolve "BadTruth"
    ])
  ])) with
  | Except.error err => hasContext "filter predicate must return exactly one atomic numeric value" err && innermostIsBadArity err
  | _ => false

#guard test73

-- Test 74: builtin arity mismatch still follows normal conventions
def test74 : Bool :=
  match runResult (.call (resolve "filter") (alg [] [] [] [])) with
  | Except.error _ => true
  | _ => false

#guard test74

-- Test 75: filter predicate arity mismatch explains the implicit item argument
def test75 : Bool :=
  match runResult (.dotCall
    (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
    "filter"
    (some (alg [] [] [] [.num 1]))) with
  | Except.error err =>
      hasContext "while evaluating filter predicate for item 0: 1 (filter passes each iterated collection item as collected; sequence parameters use values... top-level binding and nested groups stay grouped)" err &&
      innermostIsArityMismatch 0 1 err
  | _ => false

#guard test75

--------------------------------------------------------------------------------
-- reduce builtin tests
--------------------------------------------------------------------------------

def addAlg76 : Algorithm :=
  alg ["x", "total"] [] [] [.binary .add (.param "x") (.param "total")]

def mulAlg77 : Algorithm :=
  alg ["x", "total"] [] [] [
    .binary .add
      (.binary .mul (.param "total") (.num 10))
      (.dotCall (.param "x") "count" none)
  ]

def digitsAlg78 : Algorithm :=
  .conditional none [] [
    ⟨ .group [
        .group [.bind "a", .bind "b", .bind "c", .bind "d"],
        .bind "acc"
      ],
      alg [] [] [] [
        .binary .add
          (.binary .mul (.param "a") (.num 1000))
          (.binary .add
            (.binary .mul (.param "b") (.num 100))
            (.binary .add
              (.binary .mul (.param "c") (.num 10))
              (.param "d")))
      ] ⟩,
    ⟨ .group [.bind "x", .bind "acc"],
      alg [] [] [] [.num 0] ⟩
  ]

def reduceGroupedItemAlg79 : Algorithm :=
  .conditional none [] [
    ⟨ .group [.group [.bind "tag", .bind "value"], .bind "acc"],
      alg [] [] [] [.binary .add (.param "acc") (.param "value")] ⟩
  ]

def reduceStatsAlg80 : Algorithm :=
  alg ["x", "acc"] [] [] [
    .block (alg [] [] [] [
      .binary .add (.dotCall (.param "x") "count" none) (.index (.param "acc") (.num 0)),
      .binary .add (.index (.param "acc") (.num 1)) (.num 1)
    ])
  ]

def reduceEmptyBoundaryAlg80a : Algorithm :=
  alg ["x", "acc"] [] [] [
    .binary .add
      (.binary .add (.param "acc") (.num 100))
      (.dotCall (.param "x") "count" none)
  ]

def reduceEmptyBoundaryGroupedAccAlg80b : Algorithm :=
  alg ["x", "acc"] [] [] [
    .block (alg [] [] [] [
      .binary .add
        (.binary .add (.index (.param "acc") (.num 0)) (.num 100))
        (.dotCall (.param "x") "count" none),
      .binary .add (.index (.param "acc") (.num 1)) (.num 1)
    ])
  ]

def addItemCountAlg80c : Algorithm :=
  alg ["x", "acc"] [] [] [
    .binary .add
      (.dotCall (.param "x") "count" none)
      (.param "acc")
  ]

def reduceEmptyAlg81 : Algorithm :=
  alg ["x", "acc"] [] [] [
    .call (resolve "take") (alg [] [] [] [
      .param "x",
      .num 0
    ])
  ]

def reduceMultiAlg82 : Algorithm :=
  alg ["x", "acc"] [] [] [.param "acc", .param "x"]

def sequenceBoundaryLawAocCountMatchStepAlg : Algorithm :=
  algPrivate ["element", "tt"] [] [
    ("T", alg [] [] [] [
      .call (resolve "atoms") (alg [] [] [] [.param "tt"])
    ])
  ] [
    .block (alg [] [] [] [
      .dotCall (resolve "T") "first" none,
      .binary .add
        (.index (resolve "T") (.num 1))
        (.call (resolve "if") (alg [] [] [] [
          .binary .eq (.param "element") (.dotCall (resolve "T") "first" none),
          .num 1,
          .num 0
        ]))
    ])
  ]

-- Exact AoC-style regression: Right is a named multi-output property passed
-- to a values... reduce input, so top-level binding must iterate its items.
def sequenceBoundaryLawAocNamedReduceSource : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Left", alg [] [] [] [.num 3, .num 4, .num 2, .num 1, .num 3, .num 3]),
    ("Right", alg [] [] [] [.num 4, .num 3, .num 5, .num 3, .num 9, .num 3]),
    ("CountMatchStep", sequenceBoundaryLawAocCountMatchStepAlg),
    ("MatchCount", alg ["value"] [] [] [
      .index
        (.call (resolve "reduce") (alg [] [] [] [
          sequenceSupply (resolve "Right"),
          resolve "CountMatchStep",
          .block (alg [] [] [] [.param "value", .num 0])
        ]))
        (.num 1)
    ]),
    ("SimilarityAt", alg ["value"] [] [] [
      .binary .mul
        (.param "value")
        (.call (resolve "MatchCount") (alg [] [] [] [.param "value"]))
    ]),
    ("Part2", alg [] [] [] [
      .dotCall
        (.dotCall (resolve "Left") "map" (some (alg [] [] [] [resolve "SimilarityAt"])))
        "sum"
        none
    ])
  ] [
    resolve "Part2"
  ])) with
  | Except.ok [31] => true
  | _ => false

#guard sequenceBoundaryLawAocNamedReduceSource

-- Test 76: dot-call reduce over range with additive step
def test76 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Add", addAlg76)] [
    .dotCall
      (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
      "reduce"
      (some (alg [] [] [] [.resolve "Add", .num 0]))
  ])) with
  | Except.ok [15] => true
  | _ => false

#guard test76

-- Test 77: plain-call reduce iterates emitted range items
def test77 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Mul", mulAlg77)] [
    .call (resolve "reduce") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
      .resolve "Mul",
      .num 1
    ])
  ])) with
  | Except.ok [11111] => true
  | _ => false

#guard test77

-- Test 77a: plain-call reduce can still observe grouped range content explicitly
def test77a : Bool :=
  match runFlat (.block (algPrivate [] [] [("AddItemCount", addItemCountAlg80c)] [
    .call (resolve "reduce") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 3, .num 6])),
      .resolve "AddItemCount",
      .num 0
    ])
  ])) with
  | Except.ok [4] => true
  | _ => false

#guard test77a

-- Test 78: grouped-only reduce branches do not match scalar emitted range items
def test78 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Digits", digitsAlg78)] [
    .call (resolve "reduce") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
      .resolve "Digits",
      .num 0
    ])
  ])) with
  | Except.ok [0] => true
  | _ => false

#guard test78

-- Test 79: reducing an empty plain-call collection returns the initial accumulator
def test79 : Bool :=
  match runResult (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a), ("MarkEmptyBoundary", reduceEmptyBoundaryAlg80a)] [
    .call (resolve "reduce") (alg [] [] [] [
      sequenceSupply (.call (resolve "filter") (alg [] [] [] [
        sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
        .resolve "AlwaysFalse"
      ])),
      .resolve "MarkEmptyBoundary",
      .num 0
    ])
  ])) with
  | Except.ok (.atom 0) => true
  | _ => false

#guard test79

-- Test 80: grouped accumulators also stay unchanged when reducing an empty collection
def test80 : Bool :=
  match runResult (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a), ("MarkEmptyBoundary", reduceEmptyBoundaryGroupedAccAlg80b)] [
    .call (resolve "reduce") (alg [] [] [] [
      sequenceSupply (.call (resolve "filter") (alg [] [] [] [
        sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
        .resolve "AlwaysFalse"
      ])),
      .resolve "MarkEmptyBoundary",
      .block (alg [] [] [] [.num 7, .num 9])
    ])
  ])) with
  | Except.ok (.group [.atom 7, .atom 9]) => true
  | _ => false

#guard test80

-- Test 81: grouped collection elements are passed to the step as whole values
def test81 : Bool :=
  match runFlat (.block (algPrivate [] [] [("TakeValue", reduceGroupedItemAlg79)] [
    .call (resolve "reduce") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 10]),
      .block (alg [] [] [] [.num 2, .num 20]),
      .block (alg [] [] [] [.num 3, .num 30]),
      .resolve "TakeValue",
      .num 0
    ])
  ])) with
  | Except.ok [60] => true
  | _ => false

#guard test81

-- Test 82: grouped accumulators keep their shape while emitted range items are reduced
def test82 : Bool :=
  match runResult (.block (algPrivate [] [] [("Stats", reduceStatsAlg80)] [
    .call (resolve "reduce") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
      .resolve "Stats",
      .block (alg [] [] [] [.num 0, .num 0])
    ])
  ])) with
  | Except.ok (.group [.atom 4, .atom 4]) => true
  | _ => false

#guard test82

-- Test 83: reduce step must not return an empty result
def test83 : Bool :=
  match runResult (.block (algPrivate [] [] [("Bad", reduceEmptyAlg81)] [
    .call (resolve "reduce") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 3])),
      .resolve "Bad",
      .num 0
    ])
  ])) with
  | Except.error err => hasContext "reduce step must return a single accumulator value" err && innermostIsBadArity err
  | _ => false

#guard test83

-- Test 84: reduce step must not return multiple top-level outputs
def test84 : Bool :=
  match runResult (.block (algPrivate [] [] [("Bad", reduceMultiAlg82)] [
    .call (resolve "reduce") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 3])),
      .resolve "Bad",
      .num 0
    ])
  ])) with
  | Except.error err => hasContext "reduce step must return a single accumulator value" err && innermostIsBadArity err
  | _ => false

#guard test84

-- Test 84a: reduce requires reducer and initial suffix items
def test84a : Bool :=
  match runResult (.block (algPrivate [] [] [("Add", addAlg76)] [
    .call (resolve "reduce") (alg [] [] [] [
      .num 1
    ])
  ])) with
  | Except.error err =>
      hasContext "Builtin 'reduce' expects at least 2 item(s) for reduce(values..., reducer, initial), but received 1." err
      && innermostIsArityMismatch 2 1 err
  | _ => false

#guard test84a

-- Test 84b: reduce reports a missing initial accumulator before evaluating the step as the accumulator
def test84b : Bool :=
  match runResult (.block (algPrivate [] [] [("Add", addAlg76)] [
    .call (resolve "reduce") (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3,
      .resolve "Add"
    ])
  ])) with
  | Except.error err =>
      hasContext "while preparing reduce initial accumulator" err
      && innermostIsBadArity err
  | _ => false

#guard test84b

--------------------------------------------------------------------------------
-- map builtin tests
--------------------------------------------------------------------------------

def doubleAlg85 : Algorithm :=
  alg ["x"] [] [] [.binary .mul (.param "x") (.num 2)]

def takeMiddleGroupAlg85a : Algorithm :=
  .conditional none [] [
    ⟨ .group [.group [.bind "a", .bind "b", .bind "c", .bind "d", .bind "e"]],
      alg [] [] [] [.param "c"] ⟩,
    ⟨ .bind "x", alg [] [] [] [.num 0] ⟩
  ]

def squareAlg86 : Algorithm :=
  alg ["x"] [] [] [.binary .mul (.param "x") (.param "x")]

def tagAlg87 : Algorithm :=
  .conditional none [] [
    ⟨ .group [.group [.bind "first", .bind "b", .bind "c", .bind "d", .bind "last"]],
      alg [] [] [] [
        .binary .add (.binary .mul (.param "first") (.num 10)) (.param "last")
      ] ⟩,
    ⟨ .bind "x", alg [] [] [] [.num 0] ⟩
  ]

def countMembersAlg88a : Algorithm :=
  alg ["x"] [] [] [
    .dotCall (.param "x") "count" none
  ]

def takePairValueAlg89 : Algorithm :=
  .conditional none [] [
    ⟨ .group [.bind "tag", .bind "value"],
      alg [] [] [] [.param "value"] ⟩
  ]

def pairWithSquareAlg90 : Algorithm :=
  .conditional none [] [
    ⟨ .group [.group [.bind "first", .bind "middle", .bind "last"]],
      alg [] [] [] [
        .block (alg [] [] [] [
          .param "first",
          .param "last"
        ])
      ] ⟩,
    ⟨ .bind "x",
      alg [] [] [] [
        .block (alg [] [] [] [.num 0, .num 0])
      ] ⟩
  ]

def mapEmptyAlg91 : Algorithm :=
  alg ["x"] [] [] [
    .call (resolve "take") (alg [] [] [] [
      .param "x",
      .num 0
    ])
  ]

def mapMultiAlg92 : Algorithm :=
  alg ["x"] [] [] [
    .param "x",
    .num 0
  ]

-- Test 85: dot-call map doubles each range element left-to-right
def test85 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Double", doubleAlg85)] [
    .dotCall
      (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
      "map"
      (some (alg [] [] [] [.resolve "Double"]))
  ])) with
  | Except.ok [2, 4, 6, 8, 10] => true
  | _ => false

#guard test85

def factorialMapAlg85a : Algorithm :=
  alg ["n"] [] [] [
    .call (resolve "if") (alg [] [] [] [
      .binary .eq (.param "n") (.num 0),
      .num 1,
      .binary .mul
        (.call (resolve "Factorial") (alg [] [] [] [
          .binary .sub (.param "n") (.num 1)
        ]))
        (.param "n")
    ])
  ]

def test85a : Bool :=
  match runFlat (.block (algPrivate [] [] [("Factorial", factorialMapAlg85a)] [
    .dotCall
      (.block (alg [] [] [] [.num 0, .num 1, .num 2, .num 3, .num 4]))
      "map"
      (some (alg [] [] [] [.resolve "Factorial"]))
  ])) with
  | Except.ok [1, 1, 2, 6, 24] => true
  | _ => false

#guard test85a

-- Test 86: plain-call map iterates emitted range items
def test86 : Bool :=
  match runFlat (.block (algPrivate [] [] [("TakeMiddle", takeMiddleGroupAlg85a)] [
    .call (resolve "map") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5])),
      .resolve "TakeMiddle"
    ])
  ])) with
  | Except.ok [0, 0, 0, 0, 0] => true
  | _ => false

#guard test86

-- Test 86a: plain-call map applies scalar transforms to emitted range items
def test86a : Bool :=
  match runFlat (.block (algPrivate [] [] [("Double", doubleAlg85)] [
    .call (resolve "map") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5])),
      .resolve "Double"
    ])
  ])) with
  | Except.ok [2, 4, 6, 8, 10] => true
  | _ => false

#guard test86a

-- Test 87: grouped-only map branches do not match scalar emitted range items
def test87 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Tag", tagAlg87)] [
    .call (resolve "map") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 5, .num 1])),
      .resolve "Tag"
    ])
  ])) with
  | Except.ok [0, 0, 0, 0, 0] => true
  | _ => false

#guard test87

-- Test 88: empty grouped callback items project to zero outputs inside map
def test88 : Bool :=
  match runResult (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a), ("CountMembers", countMembersAlg88a)] [
    .call (resolve "map") (alg [] [] [] [
      sequenceSupply (.call (resolve "filter") (alg [] [] [] [
        sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
        .resolve "AlwaysFalse"
      ])),
      .resolve "CountMembers"
    ])
  ])) with
  | Except.ok (.group []) => true
  | _ => false

#guard test88

-- Test 89: grouped collection elements are passed to the transform as whole values
def test89 : Bool :=
  match runFlat (.block (algPrivate [] [] [("TakeValue", takePairValueAlg89)] [
    .call (resolve "map") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 10]),
      .block (alg [] [] [] [.num 2, .num 20]),
      .block (alg [] [] [] [.num 3, .num 30]),
      .resolve "TakeValue"
    ])
  ])) with
  | Except.ok [10, 20, 30] => true
  | _ => false

#guard test89

-- Test 90: grouped mapped results are accepted for emitted range items
def test90 : Bool :=
  match runResult (.block (algPrivate [] [] [("PairWithSquare", pairWithSquareAlg90)] [
    .call (resolve "map") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 3])),
      .resolve "PairWithSquare"
    ])
  ])) with
  | Except.ok (.group [
      .group [.atom 0, .atom 0],
      .group [.atom 0, .atom 0],
      .group [.atom 0, .atom 0]
    ]) => true
  | _ => false

#guard test90

-- Test 91: map transform must not return an empty result
def test91 : Bool :=
  match runResult (.block (algPrivate [] [] [("Bad", mapEmptyAlg91)] [
    .call (resolve "map") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 3])),
      .resolve "Bad"
    ])
  ])) with
  | Except.error err => hasContext "map transform must return a single element" err && innermostIsBadArity err
  | _ => false

#guard test91

-- Test 92: map transform must not return multiple top-level outputs
def test92 : Bool :=
  match runResult (.block (algPrivate [] [] [("Bad", mapMultiAlg92)] [
    .call (resolve "map") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 3])),
      .resolve "Bad"
    ])
  ])) with
  | Except.error err => hasContext "map transform must return a single element" err && innermostIsBadArity err
  | _ => false

#guard test92

--------------------------------------------------------------------------------
-- sum builtin tests
--------------------------------------------------------------------------------

def isEvenAlg93 : Algorithm :=
  alg ["x"] [] [] [
    .binary .eq (.binary .mod (.param "x") (.num 2)) (.num 0)
  ]

-- Test 93: plain-call sum adds expanded range items
def test93 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "sum") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
    ])
  ])) with
  | Except.ok [15] => true
  | _ => false

#guard test93

-- Test 94: dot-call sum uses receiver injection with no explicit args
def test94 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .dotCall
      (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
      "sum"
      none
  ])) with
  | Except.ok [15] => true
  | _ => false

#guard test94

-- Test 95: descending ranges also expand for plain-call sum
def test95 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "sum") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 5, .num 1]))
    ])
  ])) with
  | Except.ok [15] => true
  | _ => false

#guard test95

-- Test 96: sum composes with filter and preserves strict top-level semantics
def test96 : Bool :=
  match runFlat (.block (algPrivate [] [] [("IsEven", isEvenAlg93)] [
    .dotCall
      (.dotCall
        (.call (resolve "range") (alg [] [] [] [.num 1, .num 10]))
        "filter"
        (some (alg [] [] [] [.resolve "IsEven"])))
      "sum"
      none
  ])) with
  | Except.ok [30] => true
  | _ => false

#guard test96

-- Test 97: sum composes with map and sums the mapped top-level elements
def test97 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Square", squareAlg86)] [
    .dotCall
      (.dotCall
        (.call (resolve "range") (alg [] [] [] [.num 1, .num 4]))
        "map"
        (some (alg [] [] [] [.resolve "Square"])))
      "sum"
      none
  ])) with
  | Except.ok [30] => true
  | _ => false

#guard test97

-- Test 98: plain-call sum of an empty collection returns zero
def test98 : Bool :=
  match runFlat (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .call (resolve "sum") (alg [] [] [] [
      sequenceSupply (.call (resolve "filter") (alg [] [] [] [
        sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
        .resolve "AlwaysFalse"
      ]))
    ])
  ])) with
  | Except.ok [0] => true
  | _ => false

#guard test98

-- Test 99: a single atomic value is treated as a one-element collection
def test99 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "sum") (alg [] [] [] [.num 5])
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test99

-- Test 100: grouped top-level elements are rejected rather than flattened
def test100 : Bool :=
  let groupedPairs := .block (alg [] [] [] [
    .block (alg [] [] [] [.num 1, .num 2]),
    .block (alg [] [] [] [.num 3, .num 4])
  ])
  match runResult (.block (alg [] [] [] [
    .call (resolve "sum") (alg [] [] [] [groupedPairs])
  ])) with
  | Except.error err => hasContext "sum expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test100

-- Test 101: string elements are rejected by sum
def test101 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "sum") (alg [] [] [] [.stringLiteral "hello"])
  ])) with
  | Except.error err => hasContext "sum expects each collection element to be a single numeric value; item 0 was string value \"hello\"" err && innermostIsBadArity err
  | _ => false

#guard test101

--------------------------------------------------------------------------------
-- count builtin tests
--------------------------------------------------------------------------------

-- Test 102: plain-call count counts expanded range items
def test102 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "count") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
    ])
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test102

-- Test 103: dot-call count uses receiver injection with no explicit args
def test103 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .dotCall
      (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
      "count"
      none
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test103

-- Test 103a: dot-call count matches the shared grouped receiver examples
def countReceiverNormalizationRoot103a : Algorithm :=
  algPrivate [] [] [
    ("Data1", alg [] [] [] [.num 1, .num 7]),
    ("Data2", alg [] [] [] [.block (alg [] [] [] [.num 1, .num 7])])
  ] [
    .dotCall (.resolve "Data1") "count" none,
    .dotCall (.resolve "Data2") "count" none,
    .dotCall (.block (alg [] [] [] [.num 1, .num 7])) "count" none,
    .dotCall (.block (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 7])
    ])) "count" none
  ]

def test103a : Bool :=
  match runFlat (.block countReceiverNormalizationRoot103a) with
  | Except.ok [2, 1, 2, 1] => true
  | _ => false

#guard test103a

-- Test 103b: nested grouped receiver boundaries are preserved after one strip
def test103b : Bool :=
  let groupedPairs := .block (alg [] [] [] [
    .block (alg [] [] [] [.num 1, .num 2]),
    .block (alg [] [] [] [.num 3, .num 4])
  ])
  match runFlat (.block (alg [] [] [] [
    .dotCall groupedPairs "count" none,
    .dotCall (.block (alg [] [] [] [groupedPairs])) "count" none
  ])) with
  | Except.ok [2, 1] => true
  | _ => false

#guard test103b

-- Test 104: descending ranges still count all expanded top-level items
def test104 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "count") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 5, .num 1]))
    ])
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test104

-- Test 105: count composes with filter over kept top-level elements
def test105 : Bool :=
  match runFlat (.block (algPrivate [] [] [("IsEven", isEvenAlg93)] [
    .dotCall
      (.dotCall
        (.call (resolve "range") (alg [] [] [] [.num 1, .num 10]))
        "filter"
        (some (alg [] [] [] [.resolve "IsEven"])))
      "count"
      none
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test105

-- Test 106: count composes with map and counts mapped top-level elements
def test106 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Square", squareAlg86)] [
    .dotCall
      (.dotCall
        (.call (resolve "range") (alg [] [] [] [.num 1, .num 4]))
        "map"
        (some (alg [] [] [] [.resolve "Square"])))
      "count"
      none
  ])) with
  | Except.ok [4] => true
  | _ => false

#guard test106

-- Test 107: plain-call count of an empty collection is zero
def test107 : Bool :=
  match runFlat (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .call (resolve "count") (alg [] [] [] [
      sequenceSupply (.call (resolve "filter") (alg [] [] [] [
        sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
        .resolve "AlwaysFalse"
      ]))
    ])
  ])) with
  | Except.ok [0] => true
  | _ => false

#guard test107

-- Test 107a: dot-call count of an empty filtered receiver is zero
def test107a : Bool :=
  match runFlat (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .dotCall
      (.dotCall
        (.block (alg [] [] [] [.num 1, .num 5, .num 3]))
        "filter"
        (some (alg [] [] [] [.resolve "AlwaysFalse"])))
      "count"
      none
  ])) with
  | Except.ok [0] => true
  | _ => false

#guard test107a

-- Test 107b: count with no collection argument returns zero
def test107b : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "count") (alg [] [] [] [])
  ])) with
  | Except.ok [0] => true
  | _ => false

#guard test107b

-- Test 108: a single grouped value captured by values... counts as one top-level item
def test108 : Bool :=
  let groupedPairs := .block (alg [] [] [] [
    .block (alg [] [] [] [.num 1, .num 2]),
    .block (alg [] [] [] [.num 3, .num 4])
  ])
  match runFlat (.block (alg [] [] [] [
    .call (resolve "count") (alg [] [] [] [groupedPairs])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test108

-- Test 109: a single atomic value is treated as a one-element collection
def test109 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "count") (alg [] [] [] [.num 5])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test109

-- Test 110: string elements are valid top-level elements for count
def test110 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "count") (alg [] [] [] [.stringLiteral "hello"])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test110

-- Test 110a: plain-call contains searches expanded range items
def test110a : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "contains") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5])),
      .num 3
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test110a

-- Test 110b: contains returns zero when no top-level item matches
def test110b : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "contains") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5])),
      .num 9
    ])
  ])) with
  | Except.ok [0] => true
  | _ => false

#guard test110b

-- Test 110c: dot-call contains matches plain-call receiver semantics
def test110c : Bool :=
  match runFlat (.block (alg [] [] [] [
    .dotCall
      (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
      "contains"
      (some (alg [] [] [] [.num 4]))
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test110c

-- Test 110d: contains compares grouped top-level items structurally
def test110d : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "contains") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 1, .num 2])
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test110d

-- Test 110e: contains searches top-level items only, not nested grouped members
def test110e : Bool :=
  let groupedPairs := .block (alg [] [] [] [
    .block (alg [] [] [] [.num 1, .num 2]),
    .block (alg [] [] [] [.num 3, .num 4])
  ])
  match runFlat (.block (alg [] [] [] [
    .call (resolve "contains") (alg [] [] [] [
      groupedPairs,
      .block (alg [] [] [] [.num 1, .num 2])
    ])
  ])) with
  | Except.ok [0] => true
  | _ => false

#guard test110e

-- Test 110f: selection-projected content follows the same contains rules in both call styles
def containsProjectionRoot110f : Algorithm :=
  algPrivate [] [] [
    ("Data", alg [] [] [] [
      .block (alg [] [] [] [.num 7, .num 6, .num 4, .num 2, .num 1]),
      .block (alg [] [] [] [.num 1, .num 2, .num 3, .num 4, .num 5])
    ])
  ] [
    .call (resolve "contains") (alg [] [] [] [
      sequenceSupply (.index (.resolve "Data") (.num 0)),
      .num 4
    ]),
    .dotCall (.index (.resolve "Data") (.num 0)) "contains" (some (alg [] [] [] [.num 4]))
  ]

def test110f : Bool :=
  match runFlat (.block containsProjectionRoot110f) with
  | Except.ok [1, 1] => true
  | _ => false

#guard test110f

-- Test 110g: contains keeps a multi-output suffix helper outside values...
def test110g : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Item", alg [] [] [] [.num 1, .num 2])
  ] [
    .call (resolve "contains") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .resolve "Item"
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test110g

--------------------------------------------------------------------------------
-- min builtin tests
--------------------------------------------------------------------------------

def negateAlg111 : Algorithm :=
  alg ["x"] [] [] [
    .unary .minus (.param "x")
  ]

-- Test 111: plain-call min compares expanded range items
def test111 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "min") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test111

-- Test 112: dot-call min uses receiver injection with no explicit args
def test112 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .dotCall
      (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
      "min"
      none
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test112

-- Test 113: descending ranges also expand for plain-call min
def test113 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "min") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 5, .num 1]))
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test113

-- Test 114: min composes with filter over kept top-level elements
def test114 : Bool :=
  match runFlat (.block (algPrivate [] [] [("IsEven", isEvenAlg93)] [
    .dotCall
      (.dotCall
        (.call (resolve "range") (alg [] [] [] [.num 1, .num 10]))
        "filter"
        (some (alg [] [] [] [.resolve "IsEven"])))
      "min"
      none
  ])) with
  | Except.ok [2] => true
  | _ => false

#guard test114

-- Test 115: min composes with map and compares mapped top-level elements
def test115 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Negate", negateAlg111)] [
    .dotCall
      (.dotCall
        (.call (resolve "range") (alg [] [] [] [.num 1, .num 4]))
        "map"
        (some (alg [] [] [] [.resolve "Negate"])))
      "min"
      none
  ])) with
  | Except.ok [-4] => true
  | _ => false

#guard test115

-- Test 116: plain-call min requires a non-empty collection
def test116 : Bool :=
  match runResult (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .call (resolve "min") (alg [] [] [] [
      sequenceSupply (.call (resolve "filter") (alg [] [] [] [
        sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
        .resolve "AlwaysFalse"
      ]))
    ])
  ])) with
  | Except.error err => hasContext "min requires a non-empty collection" err && innermostIsBadArity err
  | _ => false

#guard test116

-- Test 117: a single atomic value is treated as a one-element collection
def test117 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "min") (alg [] [] [] [.num 5])
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test117

-- Test 118: grouped top-level elements are rejected rather than flattened
def test118 : Bool :=
  let groupedPairs := .block (alg [] [] [] [
    .block (alg [] [] [] [.num 1, .num 2]),
    .block (alg [] [] [] [.num 3, .num 4])
  ])
  match runResult (.block (alg [] [] [] [
    .call (resolve "min") (alg [] [] [] [groupedPairs])
  ])) with
  | Except.error err => hasContext "min expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test118

-- Test 119: string elements are rejected by min
def test119 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "min") (alg [] [] [] [.stringLiteral "hello"])
  ])) with
  | Except.error err => hasContext "min expects each collection element to be a single numeric value; item 0 was string value \"hello\"" err && innermostIsBadArity err
  | _ => false

#guard test119

--------------------------------------------------------------------------------
-- max builtin tests
--------------------------------------------------------------------------------

-- Test 120: plain-call max compares expanded range items
def test120 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "max") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
    ])
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test120

-- Test 121: dot-call max uses receiver injection with no explicit args
def test121 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .dotCall
      (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
      "max"
      none
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test121

-- Test 122: descending ranges also expand for plain-call max
def test122 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "max") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 5, .num 1]))
    ])
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test122

-- Test 123: max composes with filter over kept top-level elements
def test123 : Bool :=
  match runFlat (.block (algPrivate [] [] [("IsEven", isEvenAlg93)] [
    .dotCall
      (.dotCall
        (.call (resolve "range") (alg [] [] [] [.num 1, .num 10]))
        "filter"
        (some (alg [] [] [] [.resolve "IsEven"])))
      "max"
      none
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard test123

-- Test 124: max composes with map and compares mapped top-level elements
def test124 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Negate", negateAlg111)] [
    .dotCall
      (.dotCall
        (.call (resolve "range") (alg [] [] [] [.num 1, .num 4]))
        "map"
        (some (alg [] [] [] [.resolve "Negate"])))
      "max"
      none
  ])) with
  | Except.ok [-1] => true
  | _ => false

#guard test124

-- Test 125: plain-call max requires a non-empty collection
def test125 : Bool :=
  match runResult (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .call (resolve "max") (alg [] [] [] [
      sequenceSupply (.call (resolve "filter") (alg [] [] [] [
        sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 4])),
        .resolve "AlwaysFalse"
      ]))
    ])
  ])) with
  | Except.error err => hasContext "max requires a non-empty collection" err && innermostIsBadArity err
  | _ => false

#guard test125

-- Test 126: a single atomic value is treated as a one-element collection
def test126 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "max") (alg [] [] [] [.num 5])
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test126

-- Test 127: grouped top-level elements are rejected rather than flattened
def test127 : Bool :=
  let groupedPairs := .block (alg [] [] [] [
    .block (alg [] [] [] [.num 1, .num 2]),
    .block (alg [] [] [] [.num 3, .num 4])
  ])
  match runResult (.block (alg [] [] [] [
    .call (resolve "max") (alg [] [] [] [groupedPairs])
  ])) with
  | Except.error err => hasContext "max expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test127

-- Test 128: string elements are rejected by max
def test128 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "max") (alg [] [] [] [.stringLiteral "hello"])
  ])) with
  | Except.error err => hasContext "max expects each collection element to be a single numeric value; item 0 was string value \"hello\"" err && innermostIsBadArity err
  | _ => false

#guard test128

-- Test 129: plain-call avg averages expanded range items
def test129 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "avg") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
    ])
  ])) with
  | Except.ok [3] => true
  | _ => false

#guard test129

-- Test 130: dot-call avg uses receiver injection with no explicit args
def test130 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .dotCall
      (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
      "avg"
      none
  ])) with
  | Except.ok [3] => true
  | _ => false

#guard test130

-- Test 131: descending ranges also expand for plain-call avg
def test131 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "avg") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 5, .num 1]))
    ])
  ])) with
  | Except.ok [3] => true
  | _ => false

#guard test131

-- Test 132: avg composes with filter over kept top-level elements
def test132 : Bool :=
  match runFlat (.block (algPrivate [] [] [("IsEven", isEvenAlg93)] [
    .dotCall
      (.dotCall
        (.call (resolve "range") (alg [] [] [] [.num 1, .num 10]))
        "filter"
        (some (alg [] [] [] [.resolve "IsEven"])))
      "avg"
      none
  ])) with
  | Except.ok [6] => true
  | _ => false

#guard test132

-- Test 133: avg composes with map and averages mapped top-level elements
def test133 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Double", doubleAlg85)] [
    .dotCall
      (.dotCall
        (.call (resolve "range") (alg [] [] [] [.num 1, .num 4]))
        "map"
        (some (alg [] [] [] [.resolve "Double"])))
      "avg"
      none
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test133

-- Test 134: plain-call avg requires a non-empty collection
def test134 : Bool :=
  match runResult (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .call (resolve "avg") (alg [] [] [] [
      .call (resolve "filter") (alg [] [] [] [
        .call (resolve "range") (alg [] [] [] [.num 1, .num 4]),
        .resolve "AlwaysFalse"
      ])
    ])
  ])) with
  | Except.error err => hasContext "avg requires a non-empty collection" err && innermostIsBadArity err
  | _ => false

#guard test134

-- Test 135: a single atomic value is treated as a one-element collection
def test135 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "avg") (alg [] [] [] [.num 5])
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test135

-- Test 136: grouped top-level elements are rejected rather than flattened
def test136 : Bool :=
  let groupedPairs := .block (alg [] [] [] [
    .block (alg [] [] [] [.num 1, .num 2]),
    .block (alg [] [] [] [.num 3, .num 4])
  ])
  match runResult (.block (alg [] [] [] [
    .call (resolve "avg") (alg [] [] [] [groupedPairs])
  ])) with
  | Except.error err => hasContext "avg expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test136

-- Test 137: string elements are rejected by avg
def test137 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "avg") (alg [] [] [] [.stringLiteral "hello"])
  ])) with
  | Except.error err => hasContext "avg expects each collection element to be a single numeric value; item 0 was string value \"hello\"" err && innermostIsBadArity err
  | _ => false

#guard test137

--------------------------------------------------------------------------------
-- order builtins tests
--------------------------------------------------------------------------------

-- Test 138: ordinary builtin-call order sorts direct multi-argument inputs ascending and preserves duplicates
def test138 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "order") (alg [] [] [] [
      .num 3,
      .num 4,
      .num 2,
      .num 1,
      .num 3,
      .num 3
    ])
  ])) with
  | Except.ok [1, 2, 3, 3, 3, 4] => true
  | _ => false

#guard test138

-- Test 139: dot-call order sorts property output ascending
def test139 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Values", alg [] [] [] [.num 3, .num 4, .num 2, .num 1, .num 3, .num 3])] [
    .dotCall (.resolve "Values") "order" none
  ])) with
  | Except.ok [1, 2, 3, 3, 3, 4] => true
  | _ => false

#guard test139

-- Test 140: dot-call orderDesc sorts descending and preserves duplicates
def test140 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Values", alg [] [] [] [.num 3, .num 4, .num 2, .num 1, .num 3, .num 3])] [
    .dotCall (.resolve "Values") "orderDesc" none
  ])) with
  | Except.ok [4, 3, 3, 3, 2, 1] => true
  | _ => false

#guard test140

-- Test 141: sorting a descending range returns ascending output for order
def test141 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .dotCall
      (.call (resolve "range") (alg [] [] [] [.num 5, .num 1]))
      "order"
      none
  ])) with
  | Except.ok [1, 2, 3, 4, 5] => true
  | _ => false

#guard test141

-- Test 142: dot-call order preserves empty receiver outputs
def test142 : Bool :=
  match runFlat (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .dotCall
      (.call (resolve "filter") (alg [] [] [] [
        .call (resolve "range") (alg [] [] [] [.num 1, .num 4]),
        .resolve "AlwaysFalse"
      ]))
      "order"
      none
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test142

-- Test 143: unsupported sortable elements are rejected by order
def test143 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "order") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .stringLiteral "hello"])
    ])
  ])) with
  | Except.error err => hasContext "order expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test143

--------------------------------------------------------------------------------
-- first/last builtin tests
--------------------------------------------------------------------------------

-- Test 144: plain-call first returns the first expanded range item
def test144 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "first") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test144

-- Test 145: dot-call first uses receiver injection with no explicit args
def test145 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .dotCall
      (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
      "first"
      none
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test145

-- Test 146: plain-call last returns the last expanded range item
def test146 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "last") (alg [] [] [] [
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
    ])
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test146

-- Test 147: dot-call last uses receiver injection with no explicit args
def test147 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .dotCall
      (.call (resolve "range") (alg [] [] [] [.num 1, .num 5]))
      "last"
      none
  ])) with
  | Except.ok [5] => true
  | _ => false

#guard test147

-- Test 148: first preserves a single grouped value captured by values... unchanged
def test148 : Bool :=
  let groupedPairs := .block (alg [] [] [] [
    .block (alg [] [] [] [.num 1, .num 2]),
    .block (alg [] [] [] [.num 3, .num 4])
  ])
  match runResult (.block (alg [] [] [] [
    .call (resolve "first") (alg [] [] [] [groupedPairs])
  ])) with
  | Except.ok (.group [
      .group [.atom 1, .atom 2],
      .group [.atom 3, .atom 4]
    ]) => true
  | _ => false

#guard test148

-- Test 149: last preserves a single grouped value captured by values... unchanged
def test149 : Bool :=
  let groupedPairs := .block (alg [] [] [] [
    .block (alg [] [] [] [.num 1, .num 2]),
    .block (alg [] [] [] [.num 3, .num 4])
  ])
  match runResult (.block (alg [] [] [] [
    .call (resolve "last") (alg [] [] [] [groupedPairs])
  ])) with
  | Except.ok (.group [
      .group [.atom 1, .atom 2],
      .group [.atom 3, .atom 4]
    ]) => true
  | _ => false

#guard test149

-- Test 150: plain-call first requires a non-empty collection
def test150 : Bool :=
  match runResult (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .call (resolve "first") (alg [] [] [] [
      .call (resolve "filter") (alg [] [] [] [
        .call (resolve "range") (alg [] [] [] [.num 1, .num 4]),
        .resolve "AlwaysFalse"
      ])
    ])
  ])) with
  | Except.error err => hasContext "first requires a non-empty collection" err && innermostIsBadArity err
  | _ => false

#guard test150

-- Test 151: plain-call last requires a non-empty collection
def test151 : Bool :=
  match runResult (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .call (resolve "last") (alg [] [] [] [
      .call (resolve "filter") (alg [] [] [] [
        .call (resolve "range") (alg [] [] [] [.num 1, .num 4]),
        .resolve "AlwaysFalse"
      ])
    ])
  ])) with
  | Except.error err => hasContext "last requires a non-empty collection" err && innermostIsBadArity err
  | _ => false

#guard test151

-- Additional sequence-input builtin regression tests

def test151a : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "order") (alg [] [] [] [.num 3, .num 4, .num 2, .num 1, .num 3, .num 3])
  ])) with
  | Except.ok [1, 2, 3, 3, 3, 4] => true
  | _ => false

#guard test151a

def test151b : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "orderDesc") (alg [] [] [] [.num 3, .num 4, .num 2, .num 1, .num 3, .num 3])
  ])) with
  | Except.ok [4, 3, 3, 3, 2, 1] => true
  | _ => false

#guard test151b

def test151c : Bool :=
  match runFlat (.block (algPrivate [] [] [("Values", alg [] [] [] [.num 3, .num 4, .num 2])] [
    .call (resolve "order") (alg [] [] [] [sequenceSupply (.resolve "Values"), .num 1, .num 3])
  ])) with
  | Except.ok [1, 2, 3, 3, 4] => true
  | _ => false

#guard test151c

def test151d : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "order") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ])
  ])) with
  | Except.error err => hasContext "order expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test151d

def test151e : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "orderDesc") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ])
  ])) with
  | Except.error err => hasContext "orderDesc expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test151e

def test151f : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "first") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ])
  ])) with
  | Except.ok (.group [.atom 1, .atom 2]) => true
  | _ => false

#guard test151f

def test151g : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "last") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ])
  ])) with
  | Except.ok (.group [.atom 3, .atom 4]) => true
  | _ => false

#guard test151g

def test151h : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "count") (alg [] [] [] [.num 10, .num 20, .num 30])
  ])) with
  | Except.ok [3] => true
  | _ => false

#guard test151h

def test151i : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "count") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ])
  ])) with
  | Except.ok [2] => true
  | _ => false

#guard test151i

def test151j : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "sum") (alg [] [] [] [.num 10, .num 20, .num 30])
  ])) with
  | Except.ok [60] => true
  | _ => false

#guard test151j

def test151k : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "min") (alg [] [] [] [.num 10, .num 4, .num 7])
  ])) with
  | Except.ok [4] => true
  | _ => false

#guard test151k

def test151l : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "max") (alg [] [] [] [.num 10, .num 4, .num 7])
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard test151l

def test151m : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "avg") (alg [] [] [] [.num 10, .num 20, .num 30])
  ])) with
  | Except.ok [20] => true
  | _ => false

#guard test151m

def test151n : Bool :=
  match runFlat (.block (algPrivate [] [] [("KeepFourGroup", keepFourGroupAlg66c)] [
    .call (resolve "filter") (alg [] [] [] [
      .num 1,
      .num 2,
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 3, .num 6])),
      .resolve "KeepFourGroup"
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test151n

def test151o : Bool :=
  match runFlat (.block (algPrivate [] [] [("MarkThreeGroup", markThreeGroupAlg66e)] [
    .call (resolve "map") (alg [] [] [] [
      .num 1,
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 2, .num 4])),
      .resolve "MarkThreeGroup"
    ])
  ])) with
  | Except.ok [0, 0, 0, 0] => true
  | _ => false

#guard test151o

def test151ob : Bool :=
  match runFlat (.block (algPrivate [] [] [("MarkThreeGroup", markThreeGroupAlg66e)] [
    .call (resolve "map") (alg [] [] [] [
      .sequenceSupply
        (.num 1)
        (.call (resolve "range") (alg [] [] [] [.num 2, .num 4])),
      .resolve "MarkThreeGroup"
    ])
  ])) with
  | Except.ok [0, 0, 0, 0] => true
  | _ => false

#guard test151ob

def test151oc : Bool :=
  match runFlat (.block (algPrivate [] [] [("MarkThreeGroup", markThreeGroupAlg66e)] [
    .call (resolve "filter") (alg [] [] [] [
      .sequenceSupply
        (.num 1)
        (.call (resolve "range") (alg [] [] [] [.num 2, .num 4])),
      .resolve "MarkThreeGroup"
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test151oc

def markGroupedRangeDirectCallAlg151oa : Algorithm :=
  .conditional none [] [
    ⟨ .group [.group [.bind "a", .bind "b", .bind "c"]],
      alg [] [] [] [.num 1] ⟩,
    ⟨ .bind "x", alg [] [] [] [.num 0] ⟩
  ]

def test151oa : Bool :=
  match runFlat (.block (algPrivate [] [] [("MarkGroupedRange", markGroupedRangeDirectCallAlg151oa)] [
    .call (resolve "MarkGroupedRange") (alg [] [] [] [
      .call (resolve "range") (alg [] [] [] [.num 1, .num 3])
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test151oa

def test151p : Bool :=
  match runFlat (.block (algPrivate [] [] [("AddItemCount", addItemCountAlg80c)] [
    .call (resolve "reduce") (alg [] [] [] [
      .num 1,
      .num 2,
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 3, .num 4])),
      .resolve "AddItemCount",
      .num 0
    ])
  ])) with
  | Except.ok [4] => true
  | _ => false

#guard test151p

def addGroupedRangeAlg151pb : Algorithm :=
  .conditional none [] [
    ⟨ .group [.group [.bind "a", .bind "b", .bind "c"], .bind "acc"],
      alg [] [] [] [.binary .add (.param "acc") (.num 100)] ⟩,
    ⟨ .group [.bind "x", .bind "acc"],
      alg [] [] [] [.binary .add (.param "acc") (.param "x")] ⟩
  ]

def test151pb : Bool :=
  match runFlat (.block (algPrivate [] [] [("AddGroupedRange", addGroupedRangeAlg151pb)] [
    .call (resolve "reduce") (alg [] [] [] [
      .num 1,
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 2, .num 4])),
      .resolve "AddGroupedRange",
      .num 0
    ])
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard test151pb

def test151pc : Bool :=
  match runFlat (.block (algPrivate [] [] [("AddGroupedRange", addGroupedRangeAlg151pb)] [
    .call (resolve "reduce") (alg [] [] [] [
      .sequenceSupply
        (.num 1)
        (.call (resolve "range") (alg [] [] [] [.num 2, .num 4])),
      .resolve "AddGroupedRange",
      .num 0
    ])
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard test151pc

def test151q : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "order") (alg [] [] [] [
      .block (alg [] [] [] [.num 3, .num 4, .num 2, .num 1, .num 3, .num 3])
    ])
  ])) with
  | Except.error err => hasContext "order expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test151q

def test151r : Bool :=
  match runFlat (.block (algPrivate [] [] [("Values", alg [] [] [] [.num 3, .num 4, .num 2, .num 1, .num 3, .num 3])] [
    .call (resolve "order") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.ok [1, 2, 3, 3, 3, 4] => true
  | _ => false

#guard test151r

def test151s : Bool :=
  match runResult (.block (algPrivate [] [] [("Values", alg [] [] [] [.block (alg [] [] [] [.num 3, .num 4, .num 2])])] [
    .call (resolve "order") (alg [] [] [] [.resolve "Values"])
  ])) with
  | Except.error err => hasContext "order expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test151s

def test151t : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "orderDesc") (alg [] [] [] [
      .block (alg [] [] [] [.num 3, .num 4, .num 2, .num 1, .num 3, .num 3])
    ])
  ])) with
  | Except.error err => hasContext "orderDesc expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test151t

def test151u : Bool :=
  match runFlat (.block (algPrivate [] [] [("Values", alg [] [] [] [.num 3, .num 4, .num 2, .num 1, .num 3, .num 3])] [
    .call (resolve "orderDesc") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.ok [4, 3, 3, 3, 2, 1] => true
  | _ => false

#guard test151u

def test151v : Bool :=
  match runResult (.block (algPrivate [] [] [("Values", alg [] [] [] [.block (alg [] [] [] [.num 3, .num 4, .num 2])])] [
    .call (resolve "orderDesc") (alg [] [] [] [.resolve "Values"])
  ])) with
  | Except.error err => hasContext "orderDesc expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test151v

def test151w : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "count") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2])
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test151w

def test151x : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "first") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2])
    ])
  ])) with
  | Except.ok (.group [.atom 1, .atom 2]) => true
  | _ => false

#guard test151x

def test151y : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "last") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2])
    ])
  ])) with
  | Except.ok (.group [.atom 1, .atom 2]) => true
  | _ => false

#guard test151y

-- Additional uniform sequence-extraction wrapper regressions

def test152 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("KeepSecondEven", evenPredicateAlg19d),
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2])
    ])
  ] [
    .call (resolve "filter") (alg [] [] [] [
      .resolve "Values",
      .resolve "KeepSecondEven"
    ])
  ])) with
  | Except.ok (.group [.atom 1, .atom 2]) => true
  | _ => false

#guard test152

def test153 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("TakeValue", takePairValueAlg89),
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2])
    ])
  ] [
    .call (resolve "map") (alg [] [] [] [
      .resolve "Values",
      .resolve "TakeValue"
    ])
  ])) with
  | Except.ok [2] => true
  | _ => false

#guard test153

def test154 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("AddValue", reduceGroupedItemAlg79),
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2])
    ])
  ] [
    .call (resolve "reduce") (alg [] [] [] [
      .resolve "Values",
      .resolve "AddValue",
      .num 0
    ])
  ])) with
  | Except.ok [2] => true
  | _ => false

#guard test154

def test155 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2, .num 3])
    ])
  ] [
    .call (resolve "count") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test155

def test156 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [.num 1, .num 2, .num 3])
  ] [
    .call (resolve "count") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.ok [3] => true
  | _ => false

#guard test156

def test157 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2])
    ])
  ] [
    .call (resolve "first") (alg [] [] [] [.resolve "Values"])
  ])) with
  | Except.ok (.group [.atom 1, .atom 2]) => true
  | _ => false

#guard test157

def test158 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2])
    ])
  ] [
    .call (resolve "last") (alg [] [] [] [.resolve "Values"])
  ])) with
  | Except.ok (.group [.atom 1, .atom 2]) => true
  | _ => false

#guard test158

def test159 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 10, .num 20, .num 30])
    ])
  ] [
    .call (resolve "sum") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.error err => hasContext "sum expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test159

def test160 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 10, .num 20, .num 30])
    ])
  ] [
    .call (resolve "min") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.error err => hasContext "min expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test160

def test161 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 10, .num 20, .num 30])
    ])
  ] [
    .call (resolve "max") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.error err => hasContext "max expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test161

def test162 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 10, .num 20, .num 30])
    ])
  ] [
    .call (resolve "avg") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.error err => hasContext "avg expects each collection element to be a single numeric value; item 0 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test162

def test163 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [.num 10, .num 20, .num 30])
  ] [
    .call (resolve "sum") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.ok [60] => true
  | _ => false

#guard test163

def test164 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [.num 10, .num 4, .num 7])
  ] [
    .call (resolve "min") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.ok [4] => true
  | _ => false

#guard test164

def test165 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [.num 10, .num 4, .num 7])
  ] [
    .call (resolve "max") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard test165

def test166 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [.num 10, .num 20, .num 30])
  ] [
    .call (resolve "avg") (alg [] [] [] [sequenceSupply (.resolve "Values")])
  ])) with
  | Except.ok [20] => true
  | _ => false

#guard test166

def test167 : Bool :=
  match runFlat (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .dotCall
      (.call (resolve "filter") (alg [] [] [] [
        .call (resolve "range") (alg [] [] [] [.num 1, .num 4]),
        .resolve "AlwaysFalse"
      ]))
      "orderDesc"
      none
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test167

def test168 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "avg") (alg [] [] [] [.num 1, .num 2])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test168

def test169 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "avg") (alg [] [] [] [.num (-1), .num (-2)])
  ])) with
  | Except.ok [-2] => true
  | _ => false

#guard test169

def test170 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "order") (alg [] [] [] [
      .num 1,
      .block (alg [] [] [] [.num 2, .num 3])
    ])
  ])) with
  | Except.error err => hasContext "order expects each collection element to be a single numeric value; item 1 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test170

def test171 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "orderDesc") (alg [] [] [] [
      .num 1,
      .block (alg [] [] [] [.num 2, .num 3])
    ])
  ])) with
  | Except.error err => hasContext "orderDesc expects each collection element to be a single numeric value; item 1 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test171

def test172 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "min") (alg [] [] [] [
      .num 1,
      .block (alg [] [] [] [.num 2, .num 3])
    ])
  ])) with
  | Except.error err => hasContext "min expects each collection element to be a single numeric value; item 1 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test172

def test173 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "max") (alg [] [] [] [
      .num 1,
      .block (alg [] [] [] [.num 2, .num 3])
    ])
  ])) with
  | Except.error err => hasContext "max expects each collection element to be a single numeric value; item 1 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test173

def test174 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "sum") (alg [] [] [] [
      .num 1,
      .block (alg [] [] [] [.num 2, .num 3])
    ])
  ])) with
  | Except.error err => hasContext "sum expects each collection element to be a single numeric value; item 1 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test174

def test175 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "avg") (alg [] [] [] [
      .num 1,
      .block (alg [] [] [] [.num 2, .num 3])
    ])
  ])) with
  | Except.error err => hasContext "avg expects each collection element to be a single numeric value; item 1 was grouped value" err && innermostIsBadArity err
  | _ => false

#guard test175

def test176 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "take") (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3,
      .num 4,
      .num 5,
      .num 3
    ])
  ])) with
  | Except.ok [1, 2, 3] => true
  | _ => false

#guard test176

def test177 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "skip") (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3,
      .num 4,
      .num 5,
      .num 3
    ])
  ])) with
  | Except.ok [4, 5] => true
  | _ => false

#guard test177

def test178 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "take") (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3,
      .num 0
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test178

def test179 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "skip") (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3,
      .num 0
    ])
  ])) with
  | Except.ok [1, 2, 3] => true
  | _ => false

#guard test179

def test180 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "take") (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3,
      .num (-2)
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test180

def test181 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "skip") (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3,
      .num (-2)
    ])
  ])) with
  | Except.ok [1, 2, 3] => true
  | _ => false

#guard test181

def test182 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "take") (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3,
      .num 10
    ])
  ])) with
  | Except.ok [1, 2, 3] => true
  | _ => false

#guard test182

def test183 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "skip") (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3,
      .num 10
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test183

def test184 : Bool :=
  match runFlat (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .call (resolve "take") (alg [] [] [] [
      .call (resolve "filter") (alg [] [] [] [
        .call (resolve "range") (alg [] [] [] [.num 1, .num 4]),
        .resolve "AlwaysFalse"
      ]),
      .num 3
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test184

def test185 : Bool :=
  match runFlat (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .call (resolve "skip") (alg [] [] [] [
      .call (resolve "filter") (alg [] [] [] [
        .call (resolve "range") (alg [] [] [] [.num 1, .num 4]),
        .resolve "AlwaysFalse"
      ]),
      .num 3
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test185

def test186 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "take") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4]),
      .num 1
    ])
  ])) with
  | Except.ok (.group [.atom 1, .atom 2]) => true
  | _ => false

#guard test186

def test187 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "skip") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4]),
      .num 1
    ])
  ])) with
  | Except.ok (.group [.atom 3, .atom 4]) => true
  | _ => false

#guard test187

def test188 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2, .num 3])
    ])
  ] [
    .call (resolve "take") (alg [] [] [] [
      sequenceSupply (.resolve "Values"),
      .num 1
    ])
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 3]) => true
  | _ => false

#guard test188

def test189 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [.num 1, .num 2, .num 3])
  ] [
    .call (resolve "take") (alg [] [] [] [
      sequenceSupply (.resolve "Values"),
      .num 1
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test189

def test190 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2, .num 3])
    ])
  ] [
    .call (resolve "skip") (alg [] [] [] [
      .resolve "Values",
      .num 1
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test190

def test191 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [.num 1, .num 2, .num 3])
  ] [
    .call (resolve "skip") (alg [] [] [] [
      sequenceSupply (.resolve "Values"),
      .num 1
    ])
  ])) with
  | Except.ok [2, 3] => true
  | _ => false

#guard test191

def test192 : Bool :=
  match runResult (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .call (resolve "take") (alg [] [] [] [
      .num 1,
      .num 2,
      .call (resolve "filter") (alg [] [] [] [
        .call (resolve "range") (alg [] [] [] [.num 1, .num 4]),
        .resolve "AlwaysFalse"
      ])
    ])
  ])) with
  | Except.error err => hasContext "take count must be exactly one whole-number value" err && innermostIsBadArity err
  | _ => false

#guard test192

def test193 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "take") (alg [] [] [] [
      .num 3,
      .num 4,
      .block (alg [] [] [] [.num 1, .num 2])
    ])
  ])) with
  | Except.error err => hasContext "take count must be exactly one whole-number value" err && innermostIsBadArity err
  | _ => false

#guard test193

def test194 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "skip") (alg [] [] [] [
      .num 1,
      .num 2,
      .stringLiteral "hello"
    ])
  ])) with
  | Except.error err => hasContext "skip count must be exactly one whole-number value" err && innermostIsBadArity err
  | _ => false

#guard test194

def test195 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "skip") (alg [] [] [] [
      .num 3,
      .num 4,
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 2]))
    ])
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard test195

def test196 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "distinct") (alg [] [] [] [
      .num 3,
      .num 1,
      .num 3,
      .num 2,
      .num 1,
      .num 2
    ])
  ])) with
  | Except.ok [3, 1, 2] => true
  | _ => false

#guard test196

def test197 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "distinct") (alg [] [] [] [
      .num 4,
      .num 4,
      .num 4,
      .num 4
    ])
  ])) with
  | Except.ok [4] => true
  | _ => false

#guard test197

def test198 : Bool :=
  match runFlat (.block (alg [] [] [] [
    .call (resolve "distinct") (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3
    ])
  ])) with
  | Except.ok [1, 2, 3] => true
  | _ => false

#guard test198

def test199 : Bool :=
  match runFlat (.block (algPrivate [] [] [("AlwaysFalse", alwaysFalseAlg66a)] [
    .call (resolve "distinct") (alg [] [] [] [
      .call (resolve "filter") (alg [] [] [] [
        .call (resolve "range") (alg [] [] [] [.num 1, .num 4]),
        .resolve "AlwaysFalse"
      ])
    ])
  ])) with
  | Except.ok [] => true
  | _ => false

#guard test199

def test200 : Bool :=
  match runResult (.block (alg [] [] [] [
    .call (resolve "distinct") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ])
  ])) with
  | Except.ok (.group [
      .group [.atom 1, .atom 2],
      .group [.atom 3, .atom 4]
    ]) => true
  | _ => false

#guard test200

def test201 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2]),
        .block (alg [] [] [] [.num 1, .num 2]),
        .block (alg [] [] [] [.num 3, .num 4])
      ])
    ])
  ] [
    .call (resolve "distinct") (alg [] [] [] [
      .resolve "Values"
    ])
  ])) with
  | Except.ok (.group [
      .group [.atom 1, .atom 2],
      .group [.atom 1, .atom 2],
      .group [.atom 3, .atom 4]
    ]) => true
  | _ => false

#guard test201

def test202 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ])
  ] [
    .call (resolve "distinct") (alg [] [] [] [
      sequenceSupply (.resolve "Values")
    ])
  ])) with
  | Except.ok (.group [
      .group [.atom 1, .atom 2],
      .group [.atom 3, .atom 4]
    ]) => true
  | _ => false

#guard test202

def test203 : Bool :=
  match runFlat (.dotCall (.block (alg [] [] [] [
    .num 3,
    .num 5,
    .num 3,
    .num 6,
    .num 3
  ])) "order" none) with
  | Except.ok [3, 3, 3, 5, 6] => true
  | _ => false

#guard test203

def test204 : Bool :=
  match runFlat (.dotCall (.block (alg [] [] [] [
    .num 3,
    .num 5,
    .num 3,
    .num 6,
    .num 3
  ])) "orderDesc" none) with
  | Except.ok [6, 5, 3, 3, 3] => true
  | _ => false

#guard test204

def test205 : Bool :=
  match runFlat (.dotCall (.block (alg [] [] [] [
    .num 3,
    .num 5,
    .num 3,
    .num 6,
    .num 3
  ])) "count" none) with
  | Except.ok [5] => true
  | _ => false

#guard test205

def test206 : Bool :=
  match runFlat (.dotCall (.block (alg [] [] [] [
    .num 3,
    .num 5,
    .num 3
  ])) "sum" none) with
  | Except.ok [11] => true
  | _ => false

#guard test206

def test207 : Bool :=
  match runFlat (.dotCall (.block (alg [] [] [] [
    .num 1,
    .num 2,
    .num 1,
    .num 3
  ])) "distinct" none) with
  | Except.ok [1, 2, 3] => true
  | _ => false

#guard test207

def test208 : Bool :=
  match runFlat (.dotCall (.block (alg [] [] [] [
    .num 1,
    .num 2,
    .num 3
  ])) "take" (some (alg [] [] [] [.num 2]))) with
  | Except.ok [1, 2] => true
  | _ => false

#guard test208

def test209 : Bool :=
  match runFlat (.dotCall (.block (alg [] [] [] [
    .num 1,
    .num 2,
    .num 3
  ])) "skip" (some (alg [] [] [] [.num 1]))) with
  | Except.ok [2, 3] => true
  | _ => false

#guard test209

def test210 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Double", doubleAlg85)] [
    .dotCall (.block (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3
    ])) "map" (some (alg [] [] [] [.resolve "Double"]))
  ])) with
  | Except.ok [2, 4, 6] => true
  | _ => false

#guard test210

def test211 : Bool :=
  match runFlat (.block (algPrivate [] [] [("IsEven", isEvenAlg93)] [
    .dotCall (.block (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3,
      .num 4
    ])) "filter" (some (alg [] [] [] [.resolve "IsEven"]))
  ])) with
  | Except.ok [2, 4] => true
  | _ => false

#guard test211

def test212 : Bool :=
  match runFlat (.block (algPrivate [] [] [("Add", addAlg76)] [
    .dotCall (.block (alg [] [] [] [
      .num 1,
      .num 2,
      .num 3
    ])) "reduce" (some (alg [] [] [] [
      .resolve "Add",
      .num 0
    ]))
  ])) with
  | Except.ok [6] => true
  | _ => false

#guard test212

def test213 : Bool :=
  match runFlat (.dotCall (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [.num 3, .num 1, .num 2])
  ] [
    .resolve "Values"
  ])) "order" none) with
  | Except.ok [1, 2, 3] => true
  | _ => false

#guard test213

def test214 : Bool :=
  let inlineReceiver := .block (alg [] [] [] [
    .num 3,
    .num 5,
    .num 3,
    .num 6,
    .num 3
  ])
  let groupedReceiver := .block (alg [] [] [] [inlineReceiver])
  let namedGroupedFails :=
    match runResult (.block (algPrivate [] [] [
      ("Data", alg [] [] [] [inlineReceiver])
    ] [
      .dotCall (.resolve "Data") "order" none
    ])) with
    | Except.error err =>
        hasContext "order expects each collection element to be a single numeric value; item 0 was grouped value" err
          && innermostIsBadArity err
    | _ => false
  let inlineReceiverWorks :=
    match runFlat (.dotCall inlineReceiver "order" none) with
    | Except.ok [3, 3, 3, 5, 6] => true
    | _ => false
  let doubleParenReceiverFails :=
    match runResult (.dotCall groupedReceiver "order" none) with
    | Except.error err =>
        hasContext "order expects each collection element to be a single numeric value; item 0 was grouped value" err
          && innermostIsBadArity err
    | _ => false
  namedGroupedFails && inlineReceiverWorks && doubleParenReceiverFails

#guard test214

def test215 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Data", alg [] [] [] [
      .block (alg [] [] [] [.num 7, .num 6, .num 4, .num 2, .num 1]),
      .block (alg [] [] [] [.num 1, .num 2, .num 3, .num 4, .num 5])
    ])
  ] [
    .call (resolve "count") (alg [] [] [] [sequenceSupply (.index (.resolve "Data") (.num 0))]),
    .dotCall (.index (.resolve "Data") (.num 0)) "count" none
    , .call (resolve "order") (alg [] [] [] [sequenceSupply (.index (.resolve "Data") (.num 0))])
    , .dotCall (.index (.resolve "Data") (.num 0)) "order" none
  ])) with
  | Except.ok [5, 5, 1, 2, 4, 6, 7, 1, 2, 4, 6, 7] => true
  | _ => false

#guard test215

def test215a : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("A", alg [] [] [] [.num 7, .num 8])
  ] [
    .index (.resolve "A") (.num 0)
  ])) with
  | Except.ok (.atom 7) => true
  | _ => false

#guard test215a

def test215b : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("A", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ])
  ] [
    .index (.resolve "A") (.num 0)
  ])) with
  | Except.ok (.group [.atom 1, .atom 2]) => true
  | _ => false

#guard test215b

def test215c : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("A", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ])
  ] [
    .call (resolve "count") (alg [] [] [] [sequenceSupply (.index (.resolve "A") (.num 0))]),
    .dotCall (.index (.resolve "A") (.num 0)) "count" none
  ])) with
  | Except.ok [2, 2] => true
  | _ => false

#guard test215c

def test215cWrappedProjectionBoundary : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("A", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ]),
    ("Projected", alg [] [] [] [
      .index (.resolve "A") (.num 0)
    ])
  ] [
    .call (resolve "count") (alg [] [] [] [sequenceSupply (.index (.resolve "A") (.num 0))]),
    .call (resolve "count") (alg [] [] [] [sequenceSupply (.resolve "Projected")])
  ])) with
  | Except.ok [2, 2] => true
  | _ => false

#guard test215cWrappedProjectionBoundary

def test215d : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("A", alg [] [] [] [
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2]),
        .block (alg [] [] [] [.num 3, .num 4])
      ]),
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 5, .num 6]),
        .block (alg [] [] [] [.num 7, .num 8])
      ])
    ])
  ] [
    .index (.resolve "A") (.num 0)
  ])) with
  | Except.ok (.group [
      .group [.atom 1, .atom 2],
      .group [.atom 3, .atom 4]
    ]) => true
  | _ => false

#guard test215d

def test215e : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("A", alg [] [] [] [
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2]),
        .block (alg [] [] [] [.num 3, .num 4])
      ]),
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 5, .num 6]),
        .block (alg [] [] [] [.num 7, .num 8])
      ])
    ])
  ] [
    .index (.index (.resolve "A") (.num 0)) (.num 1)
  ])) with
  | Except.ok (.group [.atom 3, .atom 4]) => true
  | _ => false

#guard test215e

def test215f : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("A", alg [] [] [] [
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2]),
        .block (alg [] [] [] [.num 3, .num 4])
      ]),
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 5, .num 6]),
        .block (alg [] [] [] [.num 7, .num 8])
      ])
    ])
  ] [
    .call (resolve "count") (alg [] [] [] [sequenceSupply (.index (.resolve "A") (.num 0))]),
    .call (resolve "count") (alg [] [] [] [sequenceSupply (.index (.index (.resolve "A") (.num 0)) (.num 1))])
  ])) with
  | Except.ok [2, 2] => true
  | _ => false

#guard test215f

def test215g : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("A", alg [] [] [] [
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2]),
        .block (alg [] [] [] [.num 3, .num 4])
      ]),
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 5, .num 6]),
        .block (alg [] [] [] [.num 7, .num 8])
      ])
    ])
  ] [
    .call (resolve "sum") (alg [] [] [] [.index (.resolve "A") (.num 0)])
  ])) with
  | Except.error err =>
      hasContext "sum expects each collection element to be a single numeric value; item 0 was grouped value" err
        && innermostIsBadArity err
  | _ => false

#guard test215g

def test216 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Values", alg [] [] [] [
      .block (alg [] [] [] [.num 4, .num 5, .num 4, .num 6])
    ])
  ] [
    .dotCall (.resolve "Values") "first" none,
    .dotCall (.resolve "Values") "last" none,
    .dotCall (.resolve "Values") "distinct" none,
    .dotCall (.resolve "Values") "take" (some (alg [] [] [] [.num 2])),
    .dotCall (.resolve "Values") "skip" (some (alg [] [] [] [.num 1]))
  ])) with
  | Except.ok (.group [
      .group [.atom 4, .atom 5, .atom 4, .atom 6],
      .group [.atom 4, .atom 5, .atom 4, .atom 6],
      .group [.atom 4, .atom 5, .atom 4, .atom 6],
      .group [.atom 4, .atom 5, .atom 4, .atom 6]
    ]) => true
  | _ => false

#guard test216

def test217 : Bool :=
  let runBuiltin := fun (name : String) =>
    runResult (.block (algPrivate [] [] [
      ("Values", alg [] [] [] [
        .block (alg [] [] [] [.num 10, .num 20, .num 30])
      ])
    ] [
      .dotCall (.resolve "Values") name none
    ]))
  let minFails :=
    match runBuiltin "min" with
    | Except.error err =>
        hasContext "min expects each collection element to be a single numeric value; item 0 was grouped value" err
          && innermostIsBadArity err
    | _ => false
  let maxFails :=
    match runBuiltin "max" with
    | Except.error err =>
        hasContext "max expects each collection element to be a single numeric value; item 0 was grouped value" err
          && innermostIsBadArity err
    | _ => false
  let sumFails :=
    match runBuiltin "sum" with
    | Except.error err =>
        hasContext "sum expects each collection element to be a single numeric value; item 0 was grouped value" err
          && innermostIsBadArity err
    | _ => false
  let avgFails :=
    match runBuiltin "avg" with
    | Except.error err =>
        hasContext "avg expects each collection element to be a single numeric value; item 0 was grouped value" err
          && innermostIsBadArity err
    | _ => false
  let orderFails :=
    match runBuiltin "order" with
    | Except.error err =>
        hasContext "order expects each collection element to be a single numeric value; item 0 was grouped value" err
          && innermostIsBadArity err
    | _ => false
  let orderDescFails :=
    match runBuiltin "orderDesc" with
    | Except.error err =>
        hasContext "orderDesc expects each collection element to be a single numeric value; item 0 was grouped value" err
          && innermostIsBadArity err
    | _ => false
  minFails && maxFails && sumFails && avgFails && orderFails && orderDescFails

#guard test217

def test218 : Bool :=
  let keepSecondEven : Algorithm :=
    alg ["pair"] [] [] [
      .binary .eq
        (.binary .mod (.index (.param "pair") (.num 1)) (.num 2))
        (.num 0)
    ]
  let takeFirstAlg : Algorithm :=
    alg ["x"] [] [] [
      .index (.param "x") (.num 0)
    ]
  let addItemCount : Algorithm :=
    alg ["item", "acc"] [] [] [
      .binary .add
        (.dotCall (.param "item") "count" none)
        (.param "acc")
    ]
  let filterResult :=
    runResult (.block (algPrivate [] [] [
      ("Values", alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2])
      ]),
      ("KeepSecondEven", keepSecondEven)
    ] [
      .dotCall (.resolve "Values") "filter" (some (alg [] [] [] [.resolve "KeepSecondEven"]))
    ]))
  let mapResult :=
    runResult (.block (algPrivate [] [] [
      ("Values", alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2, .num 3])
      ]),
      ("TakeFirst", takeFirstAlg)
    ] [
      .dotCall (.resolve "Values") "map" (some (alg [] [] [] [.resolve "TakeFirst"]))
    ]))
  let reduceResult :=
    runFlat (.block (algPrivate [] [] [
      ("Values", alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2, .num 3])
      ]),
      ("AddItemCount", addItemCount)
    ] [
      .dotCall (.resolve "Values") "reduce" (some (alg [] [] [] [.resolve "AddItemCount", .num 0]))
    ]))
  let filterOk :=
    match filterResult with
    | Except.ok (.group [.atom 1, .atom 2]) => true
    | _ => false
  let mapOk :=
    match mapResult with
    | Except.ok (.atom 1) => true
    | _ => false
  let reduceOk :=
    match reduceResult with
    | Except.ok [3] => true
    | _ => false
  filterOk && mapOk && reduceOk

#guard test218

def test219 : Bool :=
  match runResult (.dotCall (.block (alg [] [] [] [
    .block (alg [] [] [] [.num 1, .num 2]),
    .block (alg [] [] [] [.num 3, .num 4])
  ])) "sum" none) with
  | Except.error err =>
      hasContext "sum expects each collection element to be a single numeric value; item 0 was grouped value" err
        && innermostIsBadArity err
  | _ => false

#guard test219

--------------------------------------------------------------------------------
-- Sequence-boundary cleanup regressions
--------------------------------------------------------------------------------

def test228 : Bool :=
  match runFlat (.call (resolve "count") (alg [] [] [] [
    .num 3,
    .num 4,
    sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5])),
    .num 7
  ])) with
  | Except.ok [8] => true
  | _ => false

#guard test228

def test229 : Bool :=
  let groupedRange := .block (alg [] [] [] [.num 1, .num 2, .num 3, .num 4, .num 5])
  match runFlat (.block (alg [] [] [] [
    .call (resolve "contains") (alg [] [] [] [
      .num 3,
      .num 4,
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5])),
      .num 7,
      .num 5
    ]),
    .call (resolve "contains") (alg [] [] [] [
      .num 3,
      .num 4,
      sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5])),
      .num 7,
      groupedRange
    ])
  ])) with
  | Except.ok [1, 0] => true
  | _ => false

#guard test229

def test230 : Bool :=
  match runFlat (.call (resolve "order") (alg [] [] [] [
    .num 3,
    .num 4,
    sequenceSupply (.call (resolve "range") (alg [] [] [] [.num 1, .num 5])),
    .num 7
  ])) with
  | Except.ok [1, 2, 3, 3, 4, 4, 5, 7] => true
  | _ => false

#guard test230

def test231 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Data", alg [] [] [] [
      .block (alg [] [] [] [.num 7, .num 6, .num 4, .num 2, .num 1]),
      .block (alg [] [] [] [.num 1, .num 2, .num 3, .num 4, .num 5])
    ])
  ] [
    .call (resolve "count") (alg [] [] [] [
      sequenceSupply (.index (.resolve "Data") (.num 0))
    ]),
    .dotCall (.index (.resolve "Data") (.num 0)) "count" none,
    .call (resolve "order") (alg [] [] [] [
      sequenceSupply (.index (.resolve "Data") (.num 0))
    ]),
    .dotCall (.index (.resolve "Data") (.num 0)) "order" none
  ])) with
  | Except.ok [5, 5, 1, 2, 4, 6, 7, 1, 2, 4, 6, 7] => true
  | _ => false

#guard test231

def test232 : Bool :=
  let firstReport := .block (alg [] [] [] [.num 7, .num 6, .num 4, .num 2, .num 1])
  let secondReport := .block (alg [] [] [] [.num 1, .num 2, .num 7, .num 8, .num 9])
  let safeReportProjected : Algorithm :=
    let report := .param "report"
    let itemAt (i : Int) := .index report (.num i)
    let desc (i : Int) := .binary .gt (itemAt i) (itemAt (i + 1))
    let stepOk (i : Int) := .binary .le (.binary .sub (itemAt i) (itemAt (i + 1))) (.num 3)
    let descendingChecks :=
      .binary .and
        (desc 0)
        (.binary .and
          (desc 1)
          (.binary .and (desc 2) (desc 3)))
    let stepChecks :=
      .binary .and
        (stepOk 0)
        (.binary .and
          (stepOk 1)
          (.binary .and (stepOk 2) (stepOk 3)))
    alg ["report"] [] [] [
      .binary .and descendingChecks stepChecks
    ]
  match runResult (.block (algPrivate [] [] [
    ("IsSafe", safeReportProjected)
  ] [
    .call (resolve "filter") (alg [] [] [] [
      firstReport,
      secondReport,
      .resolve "IsSafe"
    ])
  ])) with
  | Except.ok (.group [.atom 7, .atom 6, .atom 4, .atom 2, .atom 1]) => true
  | _ => false

#guard test232

def test233 : Bool :=
  let takeFirstProjected : Algorithm :=
    alg ["report"] [] [] [
      .index (.param "report") (.num 0)
    ]
  match runFlat (.block (algPrivate [] [] [
    ("TakeFirst", takeFirstProjected)
  ] [
    .call (resolve "map") (alg [] [] [] [
      .block (alg [] [] [] [.num 7, .num 6, .num 4, .num 2, .num 1]),
      .block (alg [] [] [] [.num 1, .num 2, .num 7, .num 8, .num 9]),
      .resolve "TakeFirst"
    ])
  ])) with
  | Except.ok [7, 1] => true
  | _ => false

#guard test233

def test234 : Bool :=
  let countItem : Algorithm :=
    alg ["x"] [] [] [
      .dotCall (.param "x") "count" none
    ]
  match runFlat (.block (algPrivate [] [] [
    ("Items", alg [] [] [] [
      .call (resolve "range") (alg [] [] [] [.num 1, .num 3]),
      .num 7
    ]),
    ("CountItem", countItem)
  ] [
    .dotCall (.resolve "Items") "count" none,
    .dotCall (.index (.resolve "Items") (.num 0)) "count" none,
    .dotCall (.index (.resolve "Items") (.num 1)) "count" none,
    .dotCall (.resolve "Items") "map" (some (alg [] [] [] [.resolve "CountItem"]))
  ])) with
  | Except.ok [2, 3, 1, 3, 1] => true
  | _ => false

#guard test234

def test235 : Bool :=
  let takeFirstProjected : Algorithm :=
    alg ["x"] [] [] [
      .index (.param "x") (.num 0)
    ]
  let hasThreeItems : Algorithm :=
    alg ["x"] [] [] [
      .binary .eq
        (.dotCall (.param "x") "count" none)
        (.num 3)
    ]
  match runFlat (.block (algPrivate [] [] [
    ("Items", alg [] [] [] [
      .call (resolve "range") (alg [] [] [] [.num 1, .num 3]),
      .num 7
    ]),
    ("TakeFirst", takeFirstProjected),
    ("HasThreeItems", hasThreeItems)
  ] [
    .dotCall (.resolve "Items") "map" (some (alg [] [] [] [.resolve "TakeFirst"])),
    .dotCall
      (.dotCall (.resolve "Items") "filter" (some (alg [] [] [] [.resolve "HasThreeItems"])))
      "count"
      none
  ])) with
  | Except.ok [1, 7, 1] => true
  | _ => false

#guard test235

--------------------------------------------------------------------------------
-- Focused reduce callback projection regressions
--------------------------------------------------------------------------------

def reduceCurrentSelectionSignatureAlg236 : Algorithm :=
  alg ["current", "acc"] [] [] [
    .binary .add
      (.binary .mul (.param "acc") (.num 100))
      (.binary .add
        (.binary .mul (.dotCall (.param "current") "count" none) (.num 10))
        (.dotCall (.param "current") "sum" none))
  ]

def reduceCurrentOneLevelSignatureAlg237 : Algorithm :=
  alg ["current", "acc"] [] [] [
    .binary .add
      (.binary .mul (.param "acc") (.num 100))
      (.binary .add
        (.binary .mul (.dotCall (.param "current") "count" none) (.num 10))
        (.dotCall (.index (.param "current") (.num 0)) "count" none))
  ]

def reduceAccumulatorAsymmetryAlg238 : Algorithm :=
  alg ["current", "acc"] [] [] [
    .block (alg [] [] [] [
      .binary .add
        (.binary .mul (.index (.param "acc") (.num 0)) (.num 100))
        (.binary .add
          (.binary .mul (.dotCall (.param "current") "count" none) (.num 10))
          (.dotCall (.param "acc") "count" none)),
      .dotCall (.param "acc") "count" none
    ])
  ]

def test236 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Signature", reduceCurrentSelectionSignatureAlg236),
    ("Items", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ])
  ] [
    .dotCall (.index (.resolve "Items") (.num 0)) "count" none,
    .dotCall (.index (.resolve "Items") (.num 0)) "sum" none,
    .dotCall (.index (.resolve "Items") (.num 1)) "count" none,
    .dotCall (.index (.resolve "Items") (.num 1)) "sum" none,
    .dotCall (.resolve "Items") "reduce" (some (alg [] [] [] [.resolve "Signature", .num 0])),
    .call (.resolve "reduce") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4]),
      .resolve "Signature",
      .num 0
    ])
  ])) with
  | Except.ok [2, 3, 2, 7, 2327, 2327] => true
  | _ => false

#guard test236

def test237 : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Signature", reduceCurrentOneLevelSignatureAlg237),
    ("Items", alg [] [] [] [
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2]),
        .block (alg [] [] [] [.num 3, .num 4])
      ])
    ])
  ] [
    .dotCall (.index (.resolve "Items") (.num 0)) "count" none,
    .dotCall (.resolve "Items") "reduce" (some (alg [] [] [] [.resolve "Signature", .num 0])),
    .call (.resolve "reduce") (alg [] [] [] [
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2]),
        .block (alg [] [] [] [.num 3, .num 4])
      ]),
      .resolve "Signature",
      .num 0
    ])
  ])) with
  | Except.ok [2, 22, 22] => true
  | _ => false

#guard test237

def test238 : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Signature", reduceAccumulatorAsymmetryAlg238),
    ("Items", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ])
  ] [
    .dotCall (.resolve "Items") "reduce" (some (alg [] [] [] [
      .resolve "Signature",
      .block (alg [] [] [] [.num 0, .num 9, .num 8])
    ]))
  ])) with
  | Except.ok (.group [.atom 2121, .atom 1]) => true
  | _ => false

#guard test238

def reduceVariadicAppendAlg239 : Algorithm :=
  algWithParameters [{ name := "item" }, { name := "history", kind := .variadic }] [] [] [
    .block (alg [] [] [] [.sequenceSupply (.param "history") (.param "item")])
  ]

def reduceVariadicAppendContentAlg240 : Algorithm :=
  algWithParameters [{ name := "item" }, { name := "history", kind := .variadic }] [] [] [
    .block (alg [] [] [] [
      .sequenceSupply (.dotCall (.param "history") "content" none) (.param "item")
    ])
  ]

def reduceScalarSumAlg241 : Algorithm :=
  alg ["item", "total"] [] [] [
    .binary .add (.param "total") (.param "item")
  ]

def reduceStructuralAppendAlg242 : Algorithm :=
  alg ["item", "history"] [] [] [
    .block (alg [] [] [] [.sequenceSupply (.param "history") (.param "item")])
  ]

def reduceVariadicAccumulatorStateFlattens : Bool :=
  match runResult (.block (algPrivate [] [] [("Append", reduceVariadicAppendAlg239)] [
    .call (resolve "reduce") (alg [] [] [] [
      .num 2,
      .num 3,
      .num 4,
      .resolve "Append",
      .num 1
    ])
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 3, .atom 4]) => true
  | _ => false

#guard reduceVariadicAccumulatorStateFlattens

def reduceVariadicAccumulatorContentWorkaroundStillWorks : Bool :=
  match runResult (.block (algPrivate [] [] [("Append", reduceVariadicAppendContentAlg240)] [
    .call (resolve "reduce") (alg [] [] [] [
      .num 2,
      .num 3,
      .num 4,
      .resolve "Append",
      .num 1
    ])
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 3, .atom 4]) => true
  | _ => false

#guard reduceVariadicAccumulatorContentWorkaroundStillWorks

def reduceScalarReducerBehaviorRemainsUnchanged : Bool :=
  match runFlat (.block (algPrivate [] [] [("Sum", reduceScalarSumAlg241)] [
    .call (resolve "reduce") (alg [] [] [] [
      .num 2,
      .num 3,
      .num 4,
      .resolve "Sum",
      .num 1
    ])
  ])) with
  | Except.ok [10] => true
  | _ => false

#guard reduceScalarReducerBehaviorRemainsUnchanged

def reduceNonVariadicAccumulatorPreservesStructuralAccumulator : Bool :=
  match runResult (.block (algPrivate [] [] [("Append", reduceStructuralAppendAlg242)] [
    .call (resolve "reduce") (alg [] [] [] [
      .num 2,
      .num 3,
      .num 4,
      .resolve "Append",
      .num 1
    ])
  ])) with
  | Except.ok (.group [.group [.group [.atom 1, .atom 2], .atom 3], .atom 4]) => true
  | _ => false

#guard reduceNonVariadicAccumulatorPreservesStructuralAccumulator

--------------------------------------------------------------------------------
-- Sequence builtin dot-call regression sweep
--------------------------------------------------------------------------------

private def dotSweepAtomsAlg (xs : List Int) : Algorithm :=
  alg [] [] [] (xs.map (fun value => .num value))

private def dotSweepGroupedExpr (xs : List Int) : KatLang.Expr :=
  KatLang.block (dotSweepAtomsAlg xs)

private def dotSweepGroupedAlg (xs : List Int) : Algorithm :=
  alg [] [] [] [dotSweepGroupedExpr xs]

private def dotSweepPairAlg (first second : List Int) : Algorithm :=
  alg [] [] [] [dotSweepGroupedExpr first, dotSweepGroupedExpr second]

private def dotSweepTopLevelItemCountAlg : Algorithm :=
  alg ["x"] [] [] [.dotCall (.param "x") "count" none]

private def dotSweepKeepCountThreeAlg : Algorithm :=
  alg ["x"] [] [] [
    .binary .eq (.dotCall (.param "x") "count" none) (.num 3)
  ]

private def dotSweepAddTopLevelItemCountAlg : Algorithm :=
  alg ["item", "acc"] [] [] [
    .binary .add (.dotCall (.param "item") "count" none) (.param "acc")
  ]

private def dotSweepAddOneAlg : Algorithm :=
  alg ["x"] [] [] [.binary .add (.param "x") (.num 1)]

private def dotSweepIsGreaterThanOneAlg : Algorithm :=
  alg ["x"] [] [] [.binary .gt (.param "x") (.num 1)]

private def dotSweepAddAlg : Algorithm :=
  alg ["x", "total"] [] [] [.binary .add (.param "x") (.param "total")]

def sequenceBuiltinDotCallCountSweep : Bool :=
  let data0 := .index (resolve "Data") (.num 0)
  match runFlat (.block (algPrivate [] [] [
    ("Values", dotSweepAtomsAlg [1, 2, 3]),
    ("Grouped", dotSweepGroupedAlg [1, 2, 3]),
    ("Data", dotSweepPairAlg [3, 1, 2] [9, 8, 7])
  ] [
    .dotCall (resolve "Values") "count" none,
    .call (resolve "count") (alg [] [] [] [sequenceSupply (resolve "Values")]),
    .dotCall (resolve "Grouped") "count" none,
    .call (resolve "count") (alg [] [] [] [sequenceSupply (resolve "Grouped")]),
    .dotCall data0 "count" none,
    .call (resolve "count") (alg [] [] [] [sequenceSupply data0])
  ])) with
  | Except.ok [3, 3, 1, 1, 3, 3] => true
  | _ => false

#guard sequenceBuiltinDotCallCountSweep

def sequenceBuiltinDotCallContainsSweep : Bool :=
  let data0 := .index (resolve "Data") (.num 0)
  match runFlat (.block (algPrivate [] [] [
    ("Values", dotSweepAtomsAlg [1, 2, 3]),
    ("Grouped", dotSweepGroupedAlg [1, 2, 3]),
    ("Data", dotSweepPairAlg [3, 1, 2] [9, 8, 7])
  ] [
    .dotCall (resolve "Values") "contains" (some (alg [] [] [] [.num 2])),
    .call (resolve "contains") (alg [] [] [] [sequenceSupply (resolve "Values"), .num 2]),
    .dotCall (resolve "Grouped") "contains" (some (alg [] [] [] [.num 2])),
    .dotCall (resolve "Grouped") "contains" (some (alg [] [] [] [dotSweepGroupedExpr [1, 2, 3]])),
    .dotCall data0 "contains" (some (alg [] [] [] [.num 2])),
    .call (resolve "contains") (alg [] [] [] [sequenceSupply data0, .num 2])
  ])) with
  | Except.ok [1, 1, 0, 1, 1, 1] => true
  | _ => false

#guard sequenceBuiltinDotCallContainsSweep

def sequenceBuiltinDotCallOrderSweep : Bool :=
  let data0 := .index (resolve "Data") (.num 0)
  match runFlat (.block (algPrivate [] [] [
    ("Values", dotSweepAtomsAlg [3, 1, 2]),
    ("Data", dotSweepPairAlg [3, 1, 2] [9, 8, 7])
  ] [
    .dotCall (resolve "Values") "order" none,
    .dotCall (resolve "Values") "orderDesc" none,
    .dotCall data0 "order" none,
    .call (resolve "order") (alg [] [] [] [sequenceSupply data0]),
    .dotCall data0 "orderDesc" none,
    .call (resolve "orderDesc") (alg [] [] [] [sequenceSupply data0])
  ])) with
  | Except.ok [1, 2, 3, 3, 2, 1, 1, 2, 3, 1, 2, 3, 3, 2, 1, 3, 2, 1] => true
  | _ => false

#guard sequenceBuiltinDotCallOrderSweep

def sequenceBuiltinDotCallOrderBoundarySweep : Bool :=
  let orderValues :=
    match runFlat (.block (algPrivate [] [] [
      ("Values", dotSweepAtomsAlg [3, 1, 2])
    ] [
      .call (resolve "order") (alg [] [] [] [sequenceSupply (resolve "Values")])
    ])) with
    | Except.ok [1, 2, 3] => true
    | _ => false
  let orderDescValues :=
    match runFlat (.block (algPrivate [] [] [
      ("Values", dotSweepAtomsAlg [3, 1, 2])
    ] [
      .call (resolve "orderDesc") (alg [] [] [] [sequenceSupply (resolve "Values")])
    ])) with
    | Except.ok [3, 2, 1] => true
    | _ => false
  let groupedOrder :=
    match runResult (.block (algPrivate [] [] [
      ("Grouped", dotSweepGroupedAlg [3, 1, 2])
    ] [
      .dotCall (resolve "Grouped") "order" none
    ])) with
    | Except.error err =>
        hasContext "order expects each collection element to be a single numeric value; item 0 was grouped value" err &&
        innermostIsBadArity err
    | _ => false
  let groupedOrderDesc :=
    match runResult (.block (algPrivate [] [] [
      ("Grouped", dotSweepGroupedAlg [3, 1, 2])
    ] [
      .dotCall (resolve "Grouped") "orderDesc" none
    ])) with
    | Except.error err =>
        hasContext "orderDesc expects each collection element to be a single numeric value; item 0 was grouped value" err &&
        innermostIsBadArity err
    | _ => false
  orderValues && orderDescValues && groupedOrder && groupedOrderDesc

#guard sequenceBuiltinDotCallOrderBoundarySweep

def sequenceBuiltinDotCallFirstLastSweep : Bool :=
  let data0 := .index (resolve "Data") (.num 0)
  match runFlat (.block (algPrivate [] [] [
    ("Values", dotSweepAtomsAlg [5, 6, 7]),
    ("Data", dotSweepPairAlg [9, 8, 7] [3, 2, 1])
  ] [
    .dotCall (resolve "Values") "first" none,
    .dotCall (resolve "Values") "last" none,
    .dotCall data0 "first" none,
    .call (resolve "first") (alg [] [] [] [sequenceSupply data0]),
    .dotCall data0 "last" none,
    .call (resolve "last") (alg [] [] [] [sequenceSupply data0])
  ])) with
  | Except.ok [5, 7, 9, 9, 7, 7] => true
  | _ => false

#guard sequenceBuiltinDotCallFirstLastSweep

def sequenceBuiltinDotCallFirstLastGroupedSweep : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Grouped", dotSweepGroupedAlg [5, 6, 7])
  ] [
    .dotCall (resolve "Grouped") "first" none,
    .call (resolve "first") (alg [] [] [] [resolve "Grouped"]),
    .dotCall (resolve "Grouped") "last" none,
    .call (resolve "last") (alg [] [] [] [resolve "Grouped"])
  ])) with
  | Except.ok (.group [
      .group [.atom 5, .atom 6, .atom 7],
      .group [.atom 5, .atom 6, .atom 7],
      .group [.atom 5, .atom 6, .atom 7],
      .group [.atom 5, .atom 6, .atom 7]
    ]) => true
  | _ => false

#guard sequenceBuiltinDotCallFirstLastGroupedSweep

def sequenceBuiltinDotCallDistinctSweep : Bool :=
  let data0 := .index (resolve "Data") (.num 0)
  match runFlat (.block (algPrivate [] [] [
    ("Values", dotSweepAtomsAlg [1, 2, 1, 3]),
    ("Data", dotSweepPairAlg [1, 2, 1, 3] [9, 8, 9])
  ] [
    .dotCall (resolve "Values") "distinct" none,
    .dotCall data0 "distinct" none,
    .call (resolve "distinct") (alg [] [] [] [sequenceSupply data0])
  ])) with
  | Except.ok [1, 2, 3, 1, 2, 3, 1, 2, 3] => true
  | _ => false

#guard sequenceBuiltinDotCallDistinctSweep

def sequenceBuiltinDotCallDistinctGroupedSweep : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Grouped", dotSweepGroupedAlg [1, 2, 1, 3])
  ] [
    .dotCall (resolve "Grouped") "distinct" none,
    .call (resolve "distinct") (alg [] [] [] [resolve "Grouped"])
  ])) with
  | Except.ok (.group [
      .group [.atom 1, .atom 2, .atom 1, .atom 3],
      .group [.atom 1, .atom 2, .atom 1, .atom 3]
    ]) => true
  | _ => false

#guard sequenceBuiltinDotCallDistinctGroupedSweep

def sequenceBuiltinDotCallTakeSkipSweep : Bool :=
  let data0 := .index (resolve "Data") (.num 0)
  match runFlat (.block (algPrivate [] [] [
    ("Values", dotSweepAtomsAlg [1, 2, 3]),
    ("Data", dotSweepPairAlg [7, 6, 4, 2, 1] [1, 2, 3, 4, 5])
  ] [
    .dotCall (resolve "Values") "take" (some (alg [] [] [] [.num 2])),
    .call (resolve "take") (alg [] [] [] [sequenceSupply (resolve "Values"), .num 2]),
    .dotCall (resolve "Values") "skip" (some (alg [] [] [] [.num 1])),
    .call (resolve "skip") (alg [] [] [] [sequenceSupply (resolve "Values"), .num 1]),
    .dotCall data0 "take" (some (alg [] [] [] [.num 2])),
    .call (resolve "take") (alg [] [] [] [sequenceSupply data0, .num 2]),
    .dotCall data0 "skip" (some (alg [] [] [] [.num 2])),
    .call (resolve "skip") (alg [] [] [] [sequenceSupply data0, .num 2])
  ])) with
  | Except.ok [1, 2, 1, 2, 2, 3, 2, 3, 7, 6, 7, 6, 4, 2, 1, 4, 2, 1] => true
  | _ => false

#guard sequenceBuiltinDotCallTakeSkipSweep

def sequenceBuiltinDotCallTakeSkipGroupedSweep : Bool :=
  let takeOk :=
    match runResult (.block (algPrivate [] [] [
      ("Grouped", dotSweepGroupedAlg [1, 2, 3])
    ] [
      .dotCall (resolve "Grouped") "take" (some (alg [] [] [] [.num 2])),
      .call (resolve "take") (alg [] [] [] [resolve "Grouped", .num 2])
    ])) with
    | Except.ok (.group [
        .group [.atom 1, .atom 2, .atom 3],
        .group [.atom 1, .atom 2, .atom 3]
      ]) => true
    | _ => false
  let skipDotOk :=
    match runFlat (.block (algPrivate [] [] [
      ("Grouped", dotSweepGroupedAlg [1, 2, 3])
    ] [
      .dotCall (resolve "Grouped") "skip" (some (alg [] [] [] [.num 1]))
    ])) with
    | Except.ok [] => true
    | _ => false
  let skipPlainOk :=
    match runFlat (.block (algPrivate [] [] [
      ("Grouped", dotSweepGroupedAlg [1, 2, 3])
    ] [
      .call (resolve "skip") (alg [] [] [] [resolve "Grouped", .num 1])
    ])) with
    | Except.ok [] => true
    | _ => false
  takeOk && skipDotOk && skipPlainOk

#guard sequenceBuiltinDotCallTakeSkipGroupedSweep

def sequenceBuiltinDotCallNamedReceiverBoundarySweep : Bool :=
  let namedMulti :=
    match runFlat (.block (algPrivate [] [] [
      ("A", dotSweepAtomsAlg [1, 2, 3])
    ] [
      .dotCall (resolve "A") "take" (some (alg [] [] [] [.num 2])),
      .dotCall (resolve "A") "count" none
    ])) with
    | Except.ok [1, 2, 3] => true
    | _ => false
  let namedGrouped :=
    match runResult (.block (algPrivate [] [] [
      ("A", dotSweepGroupedAlg [1, 2, 3])
    ] [
      .dotCall (resolve "A") "take" (some (alg [] [] [] [.num 2])),
      .dotCall (resolve "A") "count" none
    ])) with
    | Except.ok (.group [.group [.atom 1, .atom 2, .atom 3], .atom 1]) => true
    | _ => false
  let supplied :=
    match runFlat (.block (algPrivate [] [] [
      ("A", alg [] [] [] [.sequenceSupply (.sequenceSupply (.num 1) (.num 2)) (.num 3)])
    ] [
      .dotCall (resolve "A") "take" (some (alg [] [] [] [.num 2]))
    ])) with
    | Except.ok [1, 2] => true
    | _ => false
  namedMulti && namedGrouped && supplied

#guard sequenceBuiltinDotCallNamedReceiverBoundarySweep

def sequenceBuiltinDotCallUserAndConditionalReceiverBoundarySweep : Bool :=
  let userCall :=
    match runFlat (.block (algPrivate [] [] [
      ("F", alg ["x"] [] [] [.param "x", .binary .add (.param "x") (.num 1), .binary .add (.param "x") (.num 2)]),
      ("G", alg ["x"] [] [] [.block (alg [] [] [] [.param "x", .binary .add (.param "x") (.num 1), .binary .add (.param "x") (.num 2)])])
    ] [
      .dotCall (.call (resolve "F") (alg [] [] [] [.num 1])) "count" none,
      .dotCall (.call (resolve "F") (alg [] [] [] [.num 1])) "take" (some (alg [] [] [] [.num 2])),
      .dotCall (.call (resolve "G") (alg [] [] [] [.num 1])) "count" none
    ])) with
    | Except.ok [3, 1, 2, 1] => true
    | _ => false
  let conditional :=
    let chooseMulti : Algorithm :=
      .conditional none [] [
        { pattern := KatLang.Pattern.litInt 1, body := alg [] [] [] [.num 1, .num 2, .num 3] },
        { pattern := KatLang.Pattern.bind "x", body := alg [] [] [] [.num 4, .num 5, .num 6] }
      ]
    let chooseGrouped : Algorithm :=
      .conditional none [] [
        { pattern := KatLang.Pattern.litInt 1, body := alg [] [] [] [.block (alg [] [] [] [.num 1, .num 2, .num 3])] },
        { pattern := KatLang.Pattern.bind "x", body := alg [] [] [] [.block (alg [] [] [] [.num 4, .num 5, .num 6])] }
      ]
    match runFlat (.block (algPrivate [] [] [
      ("ChooseMulti", chooseMulti),
      ("ChooseGrouped", chooseGrouped)
    ] [
      .dotCall (.call (resolve "ChooseMulti") (alg [] [] [] [.num 1])) "take" (some (alg [] [] [] [.num 2])),
      .dotCall (.call (resolve "ChooseGrouped") (alg [] [] [] [.num 1])) "count" none
    ])) with
    | Except.ok [1, 2, 1] => true
    | _ => false
  userCall && conditional

#guard sequenceBuiltinDotCallUserAndConditionalReceiverBoundarySweep

def sequenceBuiltinDotCallInlineReceiverSweep : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("AddOne", dotSweepAddOneAlg),
    ("IsLarge", dotSweepIsGreaterThanOneAlg),
    ("Add", dotSweepAddAlg)
  ] [
    .dotCall (dotSweepGroupedExpr [1, 2, 3]) "count" none,
    .dotCall (dotSweepGroupedExpr [1, 2, 3]) "contains" (some (alg [] [] [] [.num 2])),
    .dotCall (dotSweepGroupedExpr [3, 1, 2]) "order" none,
    .dotCall (dotSweepGroupedExpr [5, 6, 7]) "first" none,
    .dotCall (dotSweepGroupedExpr [5, 6, 7]) "last" none,
    .dotCall (dotSweepGroupedExpr [1, 2, 1, 3]) "distinct" none,
    .dotCall (dotSweepGroupedExpr [1, 2, 3]) "take" (some (alg [] [] [] [.num 2])),
    .dotCall (dotSweepGroupedExpr [1, 2, 3]) "skip" (some (alg [] [] [] [.num 1])),
    .dotCall (dotSweepGroupedExpr [10, 4, 7]) "min" none,
    .dotCall (dotSweepGroupedExpr [10, 4, 7]) "max" none,
    .dotCall (dotSweepGroupedExpr [3, 5, 3]) "sum" none,
    .dotCall (dotSweepGroupedExpr [10, 4, 7]) "avg" none,
    .dotCall (dotSweepGroupedExpr [1, 2, 3]) "map" (some (alg [] [] [] [resolve "AddOne"])),
    .dotCall (dotSweepGroupedExpr [1, 2, 3, 4]) "filter" (some (alg [] [] [] [resolve "IsLarge"])),
    .dotCall (dotSweepGroupedExpr [1, 2, 3]) "reduce" (some (alg [] [] [] [resolve "Add", .num 0]))
  ])) with
  | Except.ok [3, 1, 1, 2, 3, 5, 7, 1, 2, 3, 1, 2, 2, 3, 4, 10, 11, 7, 2, 3, 4, 2, 3, 4, 6] => true
  | _ => false

#guard sequenceBuiltinDotCallInlineReceiverSweep

def sequenceBuiltinDotCallNumericAggregationSweep : Bool :=
  let data0 := .index (resolve "Data") (.num 0)
  match runFlat (.block (algPrivate [] [] [
    ("Values", dotSweepAtomsAlg [1, 2, 3]),
    ("Data", dotSweepPairAlg [3, 1, 2] [9, 8, 7])
  ] [
    .dotCall (resolve "Values") "sum" none,
    .dotCall (resolve "Values") "avg" none,
    .dotCall (resolve "Values") "min" none,
    .dotCall (resolve "Values") "max" none,
    .dotCall data0 "sum" none,
    .call (resolve "sum") (alg [] [] [] [sequenceSupply data0]),
    .dotCall data0 "avg" none,
    .call (resolve "avg") (alg [] [] [] [sequenceSupply data0]),
    .dotCall data0 "min" none,
    .call (resolve "min") (alg [] [] [] [sequenceSupply data0]),
    .dotCall data0 "max" none,
    .call (resolve "max") (alg [] [] [] [sequenceSupply data0])
  ])) with
  | Except.ok [6, 2, 1, 3, 6, 6, 2, 2, 1, 1, 3, 3] => true
  | _ => false

#guard sequenceBuiltinDotCallNumericAggregationSweep

def sequenceBuiltinDotCallNumericAggregationBoundarySweep : Bool :=
  let sumValues :=
    match runFlat (.block (algPrivate [] [] [
      ("Values", dotSweepAtomsAlg [1, 2, 3])
    ] [
      .call (resolve "sum") (alg [] [] [] [sequenceSupply (resolve "Values")])
    ])) with
    | Except.ok [6] => true
    | _ => false
  let sumGrouped :=
    match runResult (.block (algPrivate [] [] [
      ("Grouped", dotSweepGroupedAlg [1, 2, 3])
    ] [
      .dotCall (resolve "Grouped") "sum" none
    ])) with
    | Except.error err =>
        hasContext "sum expects each collection element to be a single numeric value; item 0 was grouped value" err &&
        innermostIsBadArity err
    | _ => false
  let avgValues :=
    match runFlat (.block (algPrivate [] [] [
      ("Values", dotSweepAtomsAlg [1, 2, 3])
    ] [
      .call (resolve "avg") (alg [] [] [] [sequenceSupply (resolve "Values")])
    ])) with
    | Except.ok [2] => true
    | _ => false
  let avgGrouped :=
    match runResult (.block (algPrivate [] [] [
      ("Grouped", dotSweepGroupedAlg [1, 2, 3])
    ] [
      .dotCall (resolve "Grouped") "avg" none
    ])) with
    | Except.error err =>
        hasContext "avg expects each collection element to be a single numeric value; item 0 was grouped value" err &&
        innermostIsBadArity err
    | _ => false
  let minValues :=
    match runFlat (.block (algPrivate [] [] [
      ("Values", dotSweepAtomsAlg [1, 2, 3])
    ] [
      .call (resolve "min") (alg [] [] [] [sequenceSupply (resolve "Values")])
    ])) with
    | Except.ok [1] => true
    | _ => false
  let minGrouped :=
    match runResult (.block (algPrivate [] [] [
      ("Grouped", dotSweepGroupedAlg [1, 2, 3])
    ] [
      .dotCall (resolve "Grouped") "min" none
    ])) with
    | Except.error err =>
        hasContext "min expects each collection element to be a single numeric value; item 0 was grouped value" err &&
        innermostIsBadArity err
    | _ => false
  let maxValues :=
    match runFlat (.block (algPrivate [] [] [
      ("Values", dotSweepAtomsAlg [1, 2, 3])
    ] [
      .call (resolve "max") (alg [] [] [] [sequenceSupply (resolve "Values")])
    ])) with
    | Except.ok [3] => true
    | _ => false
  let maxGrouped :=
    match runResult (.block (algPrivate [] [] [
      ("Grouped", dotSweepGroupedAlg [1, 2, 3])
    ] [
      .dotCall (resolve "Grouped") "max" none
    ])) with
    | Except.error err =>
        hasContext "max expects each collection element to be a single numeric value; item 0 was grouped value" err &&
        innermostIsBadArity err
    | _ => false
  sumValues && sumGrouped && avgValues && avgGrouped && minValues && minGrouped && maxValues && maxGrouped

#guard sequenceBuiltinDotCallNumericAggregationBoundarySweep

def sequenceBuiltinDotCallMapSweep : Bool :=
  let data0 := .index (resolve "Data") (.num 0)
  match runFlat (.block (algPrivate [] [] [
    ("ItemCount", dotSweepTopLevelItemCountAlg),
    ("AddOne", dotSweepAddOneAlg),
    ("Items", alg [] [] [] [dotSweepGroupedExpr [1, 2, 3], .num 7]),
    ("Grouped", dotSweepGroupedAlg [1, 2, 3]),
    ("Data", dotSweepPairAlg [1, 2, 3] [4, 5, 6])
  ] [
    .dotCall (resolve "Items") "map" (some (alg [] [] [] [resolve "ItemCount"])),
    .call (resolve "map") (alg [] [] [] [sequenceSupply (resolve "Items"), resolve "ItemCount"]),
    .dotCall (resolve "Grouped") "map" (some (alg [] [] [] [resolve "ItemCount"])),
    .call (resolve "map") (alg [] [] [] [sequenceSupply (resolve "Grouped"), resolve "ItemCount"]),
    .dotCall data0 "map" (some (alg [] [] [] [resolve "AddOne"])),
    .call (resolve "map") (alg [] [] [] [sequenceSupply data0, resolve "AddOne"])
  ])) with
  | Except.ok [3, 1, 3, 1, 3, 3, 2, 3, 4, 2, 3, 4] => true
  | _ => false

#guard sequenceBuiltinDotCallMapSweep

def sequenceBuiltinDotCallFilterSweep : Bool :=
  let data0 := .index (resolve "Data") (.num 0)
  match runFlat (.block (algPrivate [] [] [
    ("KeepCountThree", dotSweepKeepCountThreeAlg),
    ("IsLarge", dotSweepIsGreaterThanOneAlg),
    ("Items", alg [] [] [] [dotSweepGroupedExpr [1, 2, 3], dotSweepGroupedExpr [4, 5, 6], .num 7]),
    ("Grouped", dotSweepGroupedAlg [1, 2, 3]),
    ("Data", dotSweepPairAlg [1, 2, 3] [4, 5, 6])
  ] [
    .dotCall (.dotCall (resolve "Items") "filter" (some (alg [] [] [] [resolve "KeepCountThree"]))) "count" none,
    .dotCall (.call (resolve "filter") (alg [] [] [] [sequenceSupply (resolve "Items"), resolve "KeepCountThree"])) "count" none,
    .dotCall (.dotCall (resolve "Grouped") "filter" (some (alg [] [] [] [resolve "KeepCountThree"]))) "count" none,
    .dotCall (.call (resolve "filter") (alg [] [] [] [sequenceSupply (resolve "Grouped"), resolve "KeepCountThree"])) "count" none,
    .dotCall (.dotCall data0 "filter" (some (alg [] [] [] [resolve "IsLarge"]))) "count" none,
    .dotCall (.call (resolve "filter") (alg [] [] [] [sequenceSupply data0, resolve "IsLarge"])) "count" none
  ])) with
  | Except.ok [2, 2, 1, 1, 2, 2] => true
  | _ => false

#guard sequenceBuiltinDotCallFilterSweep

def sequenceBuiltinDotCallReduceSweep : Bool :=
  let data0 := .index (resolve "Data") (.num 0)
  match runFlat (.block (algPrivate [] [] [
    ("AddItemCount", dotSweepAddTopLevelItemCountAlg),
    ("Add", dotSweepAddAlg),
    ("Items", alg [] [] [] [dotSweepGroupedExpr [1, 2, 3], .num 7]),
    ("Grouped", dotSweepGroupedAlg [1, 2, 3]),
    ("Data", dotSweepPairAlg [1, 2, 3] [4, 5, 6])
  ] [
    .dotCall (resolve "Items") "reduce" (some (alg [] [] [] [resolve "AddItemCount", .num 0])),
    .call (resolve "reduce") (alg [] [] [] [sequenceSupply (resolve "Items"), resolve "AddItemCount", .num 0]),
    .dotCall (resolve "Grouped") "reduce" (some (alg [] [] [] [resolve "AddItemCount", .num 0])),
    .call (resolve "reduce") (alg [] [] [] [sequenceSupply (resolve "Grouped"), resolve "AddItemCount", .num 0]),
    .dotCall data0 "reduce" (some (alg [] [] [] [resolve "Add", .num 0])),
    .call (resolve "reduce") (alg [] [] [] [sequenceSupply data0, resolve "Add", .num 0])
  ])) with
  | Except.ok [4, 4, 3, 3, 6, 6] => true
  | _ => false

#guard sequenceBuiltinDotCallReduceSweep

--------------------------------------------------------------------------------
-- variadic user-parameter tests
--------------------------------------------------------------------------------

def variadicGroupAlg : Algorithm :=
  algWithParameters [{ name := "list", kind := .variadic }] [] [] [.param "list"]

def normalGroupAlg : Algorithm :=
  alg ["list"] [] [] [.param "list"]

def sequenceSupply1230 : KatLang.Expr :=
  .sequenceSupply (.sequenceSupply (.num 10) (.num 20)) (.num 30)

def variadicSimpleRoot : Algorithm :=
  algPrivate [] [] [
    ("Arg", alg [] [] [] [.num 1, .num 2, .num 3]),
    ("Group", variadicGroupAlg)
  ] [
    .dotCall (.dotCall (sequenceSuppliedReceiver (resolve "Arg")) "Group" none) "count" none
  ]

def variadicDotCallCapturesTopLevelItems : Bool :=
  match runFlat (.block variadicSimpleRoot) with
  | Except.ok [3] => true
  | _ => false

#guard variadicDotCallCapturesTopLevelItems

def normalParameterStillPreservesBoundary : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Arg", alg [] [] [] [.num 1, .num 2, .num 3]),
    ("Group", normalGroupAlg)
  ] [
    .dotCall (.dotCall (resolve "Arg") "Group" none) "count" none
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard normalParameterStillPreservesBoundary

def variadicNestedGroupsRoot : Algorithm :=
  algPrivate [] [] [
    ("Arg", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ]),
    ("Group", variadicGroupAlg)
  ] [
    .dotCall (.dotCall (sequenceSuppliedReceiver (resolve "Arg")) "Group" none) "count" none,
    .dotCall (.call (resolve "atoms") (alg [] [] [] [.dotCall (sequenceSuppliedReceiver (resolve "Arg")) "Group" none])) "count" none
  ]

def variadicPreservesNestedGroups : Bool :=
  match runFlat (.block variadicNestedGroupsRoot) with
  | Except.ok [2, 4] => true
  | _ => false

#guard variadicPreservesNestedGroups

def variadicScaleAlg : Algorithm :=
  algWithParameters [{ name := "values", kind := .variadic }, { name := "factor" }] [] [] [
    .dotCall (.param "values") "map" (some (alg [] [] [] [
      .block (alg ["n"] [] [] [.binary .mul (.param "n") (.param "factor")])
    ]))
  ]

def variadicTotalWithFeeAlg : Algorithm :=
  algWithParameters [{ name := "values", kind := .variadic }, { name := "fee" }] [] [] [
    .binary .add
      (.dotCall (.param "values") "sum" none)
      (.param "fee")
  ]

def variadicMeanAlg : Algorithm :=
  algWithParameters [{ name := "values", kind := .variadic }] [] [] [
    .binary .div
      (.dotCall (.param "values") "sum" none)
      (.dotCall (.param "values") "count" none)
  ]

def variadicCountAlg : Algorithm :=
  algWithParameters [{ name := "values", kind := .variadic }] [] [] [
    .dotCall (.param "values") "count" none
  ]

def variadicAtomsCountAlg : Algorithm :=
  algWithParameters [{ name := "values", kind := .variadic }] [] [] [
    .dotCall (.call (resolve "atoms") (alg [] [] [] [.param "values"])) "count" none
  ]

def ordinaryCountAlg : Algorithm :=
  alg ["list"] [] [] [
    .dotCall (.param "list") "count" none
  ]

def variadicMeanMatchesBuiltinSumCount : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Arg", alg [] [] [] [.num 1, .num 2, .num 3]),
    ("Mean", variadicMeanAlg),
    ("Direct", alg [] [] [] [
      .binary .div
        (.dotCall (resolve "Arg") "sum" none)
        (.dotCall (resolve "Arg") "count" none)
    ])
  ] [
    .call (resolve "Mean") (alg [] [] [] [sequenceSupply (resolve "Arg")]),
    .dotCall (sequenceSuppliedReceiver (resolve "Arg")) "Mean" none,
    resolve "Direct"
  ])) with
  | Except.ok [2, 2, 2] => true
  | _ => false

#guard variadicMeanMatchesBuiltinSumCount

def variadicNestedGroupsAgreeWithBuiltinCountAndAtoms : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Arg", alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .block (alg [] [] [] [.num 3, .num 4])
    ]),
    ("CountViaVariadic", variadicCountAlg),
    ("CountAtoms", variadicAtomsCountAlg)
  ] [
    .call (resolve "CountViaVariadic") (alg [] [] [] [sequenceSupply (resolve "Arg")]),
    .dotCall (sequenceSuppliedReceiver (resolve "Arg")) "CountViaVariadic" none,
    .dotCall (resolve "Arg") "count" none,
    .call (resolve "CountAtoms") (alg [] [] [] [sequenceSupply (resolve "Arg")])
  ])) with
  | Except.ok [2, 2, 2, 4] => true
  | _ => false

#guard variadicNestedGroupsAgreeWithBuiltinCountAndAtoms

def ordinaryAndVariadicCountStayStructurallyDifferent : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Arg", alg [] [] [] [.num 1, .num 2, .num 3]),
    ("Ordinary", ordinaryCountAlg),
    ("Variadic", variadicCountAlg)
  ] [
    .dotCall (resolve "Arg") "Ordinary" none,
    .dotCall (sequenceSuppliedReceiver (resolve "Arg")) "Variadic" none
  ])) with
  | Except.ok [1, 3] => true
  | _ => false

#guard ordinaryAndVariadicCountStayStructurallyDifferent

def variadicBeforeSuffixSupportsDotCall : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Arg", alg [] [] [] [.num 1, .num 2, .num 3]),
    ("Scale", variadicScaleAlg)
  ] [
    .dotCall (sequenceSuppliedReceiver (resolve "Arg")) "Scale" (some (alg [] [] [] [.num 10]))
  ])) with
  | Except.ok [10, 20, 30] => true
  | _ => false

#guard variadicBeforeSuffixSupportsDotCall

def variadicInlineTupleDotCallWithSuffixCapturesReceiverItems : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("TotalWithFee", variadicTotalWithFeeAlg)
  ] [
    .dotCall (.block (alg [] [] [] [sequenceSupply1230]))
      "TotalWithFee" (some (alg [] [] [] [.num 5]))
  ])) with
  | Except.ok [65] => true
  | _ => false

#guard variadicInlineTupleDotCallWithSuffixCapturesReceiverItems

def variadicNamedMultiOutputDotCallWithSuffixStillWorks : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Data", alg [] [] [] [.num 10, .num 20, .num 30]),
    ("TotalWithFee", variadicTotalWithFeeAlg)
  ] [
    .dotCall (sequenceSuppliedReceiver (resolve "Data")) "TotalWithFee" (some (alg [] [] [] [.num 5]))
  ])) with
  | Except.ok [65] => true
  | _ => false

#guard variadicNamedMultiOutputDotCallWithSuffixStillWorks

def variadicInlineTupleDotCallMatchesNamedReceiver : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Data", alg [] [] [] [.num 10, .num 20, .num 30]),
    ("TotalWithFee", variadicTotalWithFeeAlg)
  ] [
    .dotCall (sequenceSuppliedReceiver (resolve "Data")) "TotalWithFee" (some (alg [] [] [] [.num 5])),
    .dotCall (.block (alg [] [] [] [sequenceSupply1230]))
      "TotalWithFee" (some (alg [] [] [] [.num 5]))
  ])) with
  | Except.ok [65, 65] => true
  | _ => false

#guard variadicInlineTupleDotCallMatchesNamedReceiver

def variadicNestedInlineTupleDotCallPreservesGroup : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("TotalWithFee", variadicTotalWithFeeAlg)
  ] [
    .dotCall (.block (alg [] [] [] [
      .block (alg [] [] [] [.num 10, .num 20, .num 30])
    ])) "TotalWithFee" (some (alg [] [] [] [.num 5]))
  ])) with
  | Except.error err => innermostIsBadArity err
  | Except.ok _ => false

#guard variadicNestedInlineTupleDotCallPreservesGroup

def ordinaryInlineTupleDotCallStillPreservesReceiverBoundary : Bool :=
  match runFlat (.block (algPrivate [] [] [
    ("Group", ordinaryCountAlg)
  ] [
    .dotCall (.block (alg [] [] [] [.num 10, .num 20, .num 30])) "Group" none
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard ordinaryInlineTupleDotCallStillPreservesReceiverBoundary

def sequenceBuiltinInlineTupleDotCallBehaviorUnchanged : Bool :=
  let inlineSum :=
    .dotCall (.block (alg [] [] [] [.num 10, .num 20, .num 30])) "sum" none
  let nestedSum :=
    .dotCall (.block (alg [] [] [] [
      .block (alg [] [] [] [.num 10, .num 20, .num 30])
    ])) "sum" none
  let inlineWorks :=
    match runFlat inlineSum with
    | Except.ok [60] => true
    | _ => false
  let nestedFails :=
    match runResult nestedSum with
    | Except.error err => innermostIsBadArity err
    | Except.ok _ => false
  inlineWorks && nestedFails

#guard sequenceBuiltinInlineTupleDotCallBehaviorUnchanged

def variadicScaleMatchesBuiltinMap : Bool :=
  let builtinMap := .dotCall (resolve "Arg") "map" (some (alg [] [] [] [
    .block (alg ["n"] [] [] [.binary .mul (.param "n") (.num 10)])
  ]))
  match runFlat (.block (algPrivate [] [] [
    ("Arg", alg [] [] [] [.num 1, .num 2, .num 3]),
    ("Scale", variadicScaleAlg)
  ] [
    .sequenceSupply
      (.dotCall (sequenceSuppliedReceiver (resolve "Arg")) "Scale" (some (alg [] [] [] [.num 10])))
      builtinMap
  ])) with
  | Except.ok [10, 20, 30, 10, 20, 30] => true
  | _ => false

#guard variadicScaleMatchesBuiltinMap

def variadicBindingErrorRoot : Algorithm :=
  algPrivate [] [] [
    ("F", algWithParameters [{ name := "first" }, { name := "rest", kind := .variadic }, { name := "last" }] [] [] [
      .param "first", .param "rest", .param "last"
    ])
  ] [
    .call (resolve "F") (alg [] [] [] [.num 1])
  ]

def variadicBindingErrorWhenNormalParamsCannotBind : Bool :=
  match runResult (.block variadicBindingErrorRoot) with
  | Except.error err => innermostIsArityMismatch 2 1 err
  | Except.ok _ => false

#guard variadicBindingErrorWhenNormalParamsCannotBind

def groupedVariadicCountAlg : Algorithm :=
  algWithParameterPatterns [.group [.capture { name := "xs", kind := .variadic }]] [] [] [
    .dotCall (.param "xs") "count" none
  ]

def groupedVariadicFirstAlg : Algorithm :=
  algWithParameterPatterns [.group [.capture { name := "xs", kind := .variadic }]] [] [] [
    .index (.param "xs") (.num 0)
  ]

def groupedVariadicMixedAlg : Algorithm :=
  algWithParameterPatterns [
    .group [.capture { name := "xs", kind := .variadic }],
    .capture { name := "a" },
    .capture { name := "b" }
  ] [] [] [
    .dotCall (.param "xs") "count" none,
    .param "a",
    .param "b"
  ]

def groupedVariadicCapturesImmediateGroupItems : Bool :=
  match runFlat (.block (algPrivate [] [] [("F", groupedVariadicCountAlg)] [
    .call (resolve "F") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2, .num 3])
    ])
  ])) with
  | Except.ok [3] => true
  | _ => false

#guard groupedVariadicCapturesImmediateGroupItems

def groupedVariadicRemovesOnlyOneGroupBoundary : Bool :=
  match runFlat (.block (algPrivate [] [] [("F", groupedVariadicCountAlg)] [
    .call (resolve "F") (alg [] [] [] [
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2]),
        .num 3
      ])
    ])
  ])) with
  | Except.ok [2] => true
  | _ => false

#guard groupedVariadicRemovesOnlyOneGroupBoundary

def groupedVariadicPreservesNestedGroupItem : Bool :=
  match runResult (.block (algPrivate [] [] [("F", groupedVariadicFirstAlg)] [
    .call (resolve "F") (alg [] [] [] [
      .block (alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2]),
        .num 3
      ])
    ])
  ])) with
  | Except.ok (.group [.atom 1, .atom 2]) => true
  | _ => false

#guard groupedVariadicPreservesNestedGroupItem

def groupedVariadicRequiresGroupedSlot : Bool :=
  match runResult (.block (algPrivate [] [] [("F", groupedVariadicCountAlg)] [
    .call (resolve "F") (alg [] [] [] [.num 1, .num 2, .num 3])
  ])) with
  | Except.error err => innermostIsArityMismatch 1 3 err
  | Except.ok _ => false

#guard groupedVariadicRequiresGroupedSlot

def groupedVariadicWithMixedTopLevelParameters : Bool :=
  match runFlat (.block (algPrivate [] [] [("F", groupedVariadicMixedAlg)] [
    .call (resolve "F") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2, .num 3]),
      .num 4,
      .num 5
    ])
  ])) with
  | Except.ok [3, 4, 5] => true
  | _ => false

#guard groupedVariadicWithMixedTopLevelParameters

def groupedSeparateVariadicsDifferentLevelsAlg : Algorithm :=
  algWithParameterPatterns [
    .group [.capture { name := "inner", kind := .variadic }],
    .capture { name := "outer", kind := .variadic }
  ] [] [] [
    .dotCall (.param "inner") "count" none,
    .dotCall (.param "outer") "count" none
  ]

def groupedSeparateVariadicsDifferentLevelsBindIndependently : Bool :=
  match runFlat (.block (algPrivate [] [] [("F", groupedSeparateVariadicsDifferentLevelsAlg)] [
    .call (resolve "F") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2]),
      .num 3,
      .num 4
    ])
  ])) with
  | Except.ok [2, 2] => true
  | _ => false

#guard groupedSeparateVariadicsDifferentLevelsBindIndependently

def groupedHeadTailAlg : Algorithm :=
  algWithParameterPatterns [
    .group [
      .capture { name := "head" },
      .capture { name := "tail", kind := .variadic }
    ]
  ] [] [] [
    .param "head",
    .dotCall (.param "tail") "count" none
  ]

def groupedHeadTailPatternBindsWithinOneSlot : Bool :=
  match runFlat (.block (algPrivate [] [] [("F", groupedHeadTailAlg)] [
    .call (resolve "F") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2, .num 3, .num 4])
    ])
  ])) with
  | Except.ok [1, 3] => true
  | _ => false

#guard groupedHeadTailPatternBindsWithinOneSlot

def groupedFirstMiddleLastAlg : Algorithm :=
  algWithParameterPatterns [
    .group [
      .capture { name := "first" },
      .capture { name := "middle", kind := .variadic },
      .capture { name := "last" }
    ]
  ] [] [] [
    .param "first",
    .dotCall (.param "middle") "count" none,
    .param "last"
  ]

def groupedFirstMiddleLastPatternBindsWithinOneSlot : Bool :=
  match runFlat (.block (algPrivate [] [] [("F", groupedFirstMiddleLastAlg)] [
    .call (resolve "F") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2, .num 3, .num 4, .num 5])
    ])
  ])) with
  | Except.ok [1, 3, 5] => true
  | _ => false

#guard groupedFirstMiddleLastPatternBindsWithinOneSlot

def groupedVariadicWithSuffixInsideGroupAlg : Algorithm :=
  algWithParameterPatterns [
    .group [
      .capture { name := "history", kind := .variadic },
      .capture { name := "pre2" }
    ],
    .capture { name := "pre1" }
  ] [] [] [
    .dotCall (.param "history") "count" none,
    .param "pre2",
    .param "pre1"
  ]

def groupedVariadicWithSuffixInsideGroupBindsWithinOneSlot : Bool :=
  match runFlat (.block (algPrivate [] [] [("F", groupedVariadicWithSuffixInsideGroupAlg)] [
    .call (resolve "F") (alg [] [] [] [
      .block (alg [] [] [] [.num 1, .num 2, .num 3]),
      .num 4
    ])
  ])) with
  | Except.ok [2, 3, 4] => true
  | _ => false

#guard groupedVariadicWithSuffixInsideGroupBindsWithinOneSlot

def groupedVariadicWithSuffixInsideGroupRequiresSuffixValue : Bool :=
  match runResult (.block (algPrivate [] [] [("F", groupedVariadicWithSuffixInsideGroupAlg)] [
    .call (resolve "F") (alg [] [] [] [
      .block (alg [] [] [] []),
      .num 4
    ])
  ])) with
  | Except.error _ => true
  | Except.ok _ => false

#guard groupedVariadicWithSuffixInsideGroupRequiresSuffixValue

def groupedVariadicIsNotTopLevelVariadic : Bool :=
  let groupedCall :=
    runFlat (.block (algPrivate [] [] [("F", algWithParameterPatterns [
      .group [.capture { name := "xs", kind := .variadic }], .capture { name := "y" }
    ] [] [] [
      .dotCall (.param "xs") "count" none,
      .param "y"
    ])] [
      .call (resolve "F") (alg [] [] [] [
        .block (alg [] [] [] [.num 1, .num 2]),
        .num 3
      ])
    ]))
  let flatCall :=
    runResult (.block (algPrivate [] [] [("F", algWithParameterPatterns [
      .group [.capture { name := "xs", kind := .variadic }], .capture { name := "y" }
    ] [] [] [
      .dotCall (.param "xs") "count" none,
      .param "y"
    ])] [
      .call (resolve "F") (alg [] [] [] [.num 1, .num 2, .num 3])
    ]))
  match groupedCall, flatCall with
  | Except.ok [2, 3], Except.error err => innermostIsArityMismatch 2 3 err
  | _, _ => false

#guard groupedVariadicIsNotTopLevelVariadic

def groupedVariadicLoopStepPreservesGroupedHistorySlot : Bool :=
  let step := algWithParameterPatterns [
    .group [.capture { name := "history", kind := .variadic }],
    .capture { name := "previous" }
  ] [] [] [
    .sequenceSupply (.param "history") (.binary .add (.param "previous") (.num 1)),
    .binary .add (.param "previous") (.num 1)
  ]
  match runResult (.block (algPrivate [] [] [("Step", step)] [
    .index
      (.dotCall (resolve "Step") "repeat" (some (alg [] [] [] [
        .num 2,
        .block (alg [] [] [] [.num 1, .num 2]),
        .num 2
      ])))
      (.num 0)
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 3, .atom 4]) => true
  | _ => false

#guard groupedVariadicLoopStepPreservesGroupedHistorySlot

def groupedVariadicLoopStepWithSuffixInsideGroupPreservesStateShape : Bool :=
  let step := algWithParameterPatterns [
    .group [
      .capture { name := "history", kind := .variadic },
      .capture { name := "previous" }
    ],
    .capture { name := "current" }
  ] [] [] [
    .sequenceSupply (.param "history") (.param "current"),
    .param "current"
  ]
  match runResult (.block (algPrivate [] [] [("Step", step)] [
    .index
      (.dotCall (resolve "Step") "repeat" (some (alg [] [] [] [
        .num 2,
        .block (alg [] [] [] [.num 1, .num 2]),
        .num 3
      ])))
      (.num 0)
  ])) with
  | Except.ok (.group [.atom 1, .atom 3]) => true
  | _ => false

#guard groupedVariadicLoopStepWithSuffixInsideGroupPreservesStateShape

def loopVariadicHistoryLastExpr : KatLang.Expr :=
  .dotCall (.call (resolve "atoms") (alg [] [] [] [.param "history"])) "last" none

def loopVariadicNextExpr : KatLang.Expr :=
  .binary .add loopVariadicHistoryLastExpr (.num 1)

def loopVariadicAppendNextAlg : Algorithm :=
  algWithParameters [{ name := "history", kind := .variadic }] [] [] [
    .sequenceSupply (.param "history") loopVariadicNextExpr
  ]

def loopVariadicContinueFlagExpr : KatLang.Expr :=
  .call (resolve "if") (alg [] [] [] [
    .binary .lt loopVariadicNextExpr (.num 6),
    .num 1,
    .num 0
  ])

def loopVariadicWhileAppendNextAlg : Algorithm :=
  algWithParameters [{ name := "history", kind := .variadic }] [] [] [
    .sequenceSupply
      (.sequenceSupply (.param "history") loopVariadicNextExpr)
      loopVariadicContinueFlagExpr
  ]

def loopVariadicInitialState : Algorithm :=
  alg [] [] [] [.num 1, .num 2, .num 4]

def variadicLoopStepRepeatOneIterationCapturesStateItems : Bool :=
  match runResult (.block (algPrivate [] [] [("Step", loopVariadicAppendNextAlg)] [
    .dotCall (resolve "Step") "repeat" (some (alg [] [] [] [
      .num 1,
      .num 1,
      .num 2,
      .num 4
    ]))
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 4, .atom 5]) => true
  | _ => false

#guard variadicLoopStepRepeatOneIterationCapturesStateItems

def variadicLoopStepRepeatTwoIterationsKeepsExpandedState : Bool :=
  match runResult (.block (algPrivate [] [] [("Step", loopVariadicAppendNextAlg)] [
    .dotCall (resolve "Step") "repeat" (some (alg [] [] [] [
      .num 2,
      .num 1,
      .num 2,
      .num 4
    ]))
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 4, .atom 5, .atom 6]) => true
  | _ => false

#guard variadicLoopStepRepeatTwoIterationsKeepsExpandedState

def variadicLoopStepWhileUsesExpandedState : Bool :=
  match runResult (.block (algPrivate [] [] [("Step", loopVariadicWhileAppendNextAlg)] [
    .dotCall (resolve "Step") "while" (some (alg [] [] [] [
      .num 1,
      .num 2,
      .num 4
    ]))
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 4, .atom 5]) => true
  | _ => false

#guard variadicLoopStepWhileUsesExpandedState

def sequenceBuiltinDotCallVariadicRepeatReceiverTakeUsesFinalStateSlots : Bool :=
  match runFlat (.block (algPrivate [] [] [("Step", loopVariadicAppendNextAlg)] [
    .dotCall
      (.dotCall (resolve "Step") "repeat" (some (alg [] [] [] [
        .num 3,
        .num 1,
        .num 2,
        .num 4
      ])))
      "take"
      (some (alg [] [] [] [.num 5]))
  ])) with
  | Except.ok [1, 2, 4, 5, 6] => true
  | _ => false

#guard sequenceBuiltinDotCallVariadicRepeatReceiverTakeUsesFinalStateSlots

def ordinaryRunStepStillRejectsMultiValueState : Bool :=
  match KatLang.runStep
      (alg ["history"] [] [] [.param "history"])
      KatLang.EvalCtx.empty
      []
      (.group [.atom 1, .atom 2, .atom 4]) with
  | Except.error err => innermostIsArityMismatch 0 2 err
  | _ => false

#guard ordinaryRunStepStillRejectsMultiValueState

def loopBoundaryPairStepAlg : Algorithm :=
  alg ["a", "b"] [] [] [
    .param "b",
    .binary .add (.param "a") (.param "b")
  ]

def loopBoundaryPairWhileStepAlg : Algorithm :=
  alg ["a", "b"] [] [] [
    .binary .add (.param "a") (.num 1),
    .binary .add (.param "b") (.num 10),
    .binary .lt (.param "a") (.num 2)
  ]

def loopBoundaryGroupedRepeatStepAlg : Algorithm :=
  alg ["x"] [] [] [
    .block (alg [] [] [] [.param "x", .binary .add (.param "x") (.num 1)])
  ]

def loopBoundaryGroupedWhileStepAlg : Algorithm :=
  alg ["x"] [] [] [
    .block (alg [] [] [] [.param "x", .binary .add (.param "x") (.num 1)]),
    .num 0
  ]

def sequenceBuiltinDotCallRepeatReceiverTakeUsesFinalStateSlots : Bool :=
  match runFlat (.block (algPrivate [] [] [("Step", loopBoundaryPairStepAlg)] [
    .dotCall
      (.dotCall (resolve "Step") "repeat" (some (alg [] [] [] [
        .num 1,
        .num 1,
        .num 2
      ])))
      "take"
      (some (alg [] [] [] [.num 1]))
  ])) with
  | Except.ok [2] => true
  | _ => false

#guard sequenceBuiltinDotCallRepeatReceiverTakeUsesFinalStateSlots

def sequenceBuiltinDotCallRepeatReceiverCountUsesFinalStateSlots : Bool :=
  match runFlat (.block (algPrivate [] [] [("Step", loopBoundaryPairStepAlg)] [
    .dotCall
      (.dotCall (resolve "Step") "repeat" (some (alg [] [] [] [
        .num 1,
        .num 1,
        .num 2
      ])))
      "count"
      none
  ])) with
  | Except.ok [2] => true
  | _ => false

#guard sequenceBuiltinDotCallRepeatReceiverCountUsesFinalStateSlots

def sequenceBuiltinDotCallRepeatGroupedStateCountsOneItem : Bool :=
  match runFlat (.block (algPrivate [] [] [("Step", loopBoundaryGroupedRepeatStepAlg)] [
    .dotCall
      (.dotCall (resolve "Step") "repeat" (some (alg [] [] [] [
        .num 1,
        .num 1
      ])))
      "count"
      none
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard sequenceBuiltinDotCallRepeatGroupedStateCountsOneItem

def sequenceBuiltinDotCallWhileReceiverTakeUsesFinalStateSlots : Bool :=
  match runFlat (.block (algPrivate [] [] [("Step", loopBoundaryPairWhileStepAlg)] [
    .dotCall
      (.dotCall (resolve "Step") "while" (some (alg [] [] [] [
        .num 0,
        .num 0
      ])))
      "take"
      (some (alg [] [] [] [.num 1]))
  ])) with
  | Except.ok [2] => true
  | _ => false

#guard sequenceBuiltinDotCallWhileReceiverTakeUsesFinalStateSlots

def sequenceBuiltinDotCallWhileReceiverCountUsesFinalStateSlots : Bool :=
  match runFlat (.block (algPrivate [] [] [("Step", loopBoundaryPairWhileStepAlg)] [
    .dotCall
      (.dotCall (resolve "Step") "while" (some (alg [] [] [] [
        .num 0,
        .num 0
      ])))
      "count"
      none
  ])) with
  | Except.ok [2] => true
  | _ => false

#guard sequenceBuiltinDotCallWhileReceiverCountUsesFinalStateSlots

def sequenceBuiltinDotCallWhileGroupedStateCountsOneItem : Bool :=
  match runFlat (.block (algPrivate [] [] [("Step", loopBoundaryGroupedWhileStepAlg)] [
    .dotCall
      (.dotCall (resolve "Step") "while" (some (alg [] [] [] [
        .num 1
      ])))
      "count"
      none
  ])) with
  | Except.ok [1] => true
  | _ => false

#guard sequenceBuiltinDotCallWhileGroupedStateCountsOneItem

def loopBoundarySumPairStepAlg : Algorithm :=
  alg ["a", "b"] [] [] [
    .binary .add (.param "a") (.param "b")
  ]

def loopBoundaryIdentityAlg : Algorithm :=
  alg ["history"] [] [] [.param "history"]

def loopBoundaryVariadicIdentityAlg : Algorithm :=
  algWithParameters [{ name := "values", kind := .variadic }] [] [] [.param "values"]

def loopBoundaryContentHistoryExpr : KatLang.Expr :=
  .call (resolve "content") (alg [] [] [] [.param "history"])

def loopBoundaryGroupedHistoryStepAlg : Algorithm :=
  alg ["history"] [] [] [
    .block (alg [] [] [] [
      .sequenceSupply loopBoundaryContentHistoryExpr loopVariadicNextExpr
    ])
  ]

def loopBoundaryContentHistoryStepAlg : Algorithm :=
  alg ["history"] [] [] [
    .sequenceSupply loopBoundaryContentHistoryExpr loopVariadicNextExpr
  ]

def loopInitialManyExplicitArgsCreateManySlots : Bool :=
  match runResult (.block (algPrivate [] [] [("Step", loopBoundaryPairStepAlg)] [
    .dotCall (resolve "Step") "repeat" (some (alg [] [] [] [.num 1, .num 1, .num 2]))
  ])) with
  | Except.ok (.group [.atom 2, .atom 3]) => true
  | _ => false

#guard loopInitialManyExplicitArgsCreateManySlots

def loopInitialExplicitVariadicStepStillGetsManySlots : Bool :=
  match runResult (.block (algPrivate [] [] [("Step", loopBoundaryVariadicIdentityAlg)] [
    .dotCall (resolve "Step") "repeat" (some (alg [] [] [] [.num 1, .num 1, .num 2, .num 3]))
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 3]) => true
  | _ => false

#guard loopInitialExplicitVariadicStepStillGetsManySlots

def loopInitialGroupedPropertyArgIsOneSlot : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Step", loopBoundaryIdentityAlg),
    ("List", alg [] [] [] [.num 1, .num 2, .num 4])
  ] [
    .dotCall (resolve "Step") "repeat" (some (alg [] [] [] [.num 1, resolve "List"]))
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 4]) => true
  | _ => false

#guard loopInitialGroupedPropertyArgIsOneSlot

def loopInitialGroupedArgDoesNotSatisfyTwoOrdinaryParams : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Step", loopBoundarySumPairStepAlg),
    ("Pair", alg [] [] [] [.num 1, .num 2])
  ] [
    .dotCall (resolve "Step") "repeat" (some (alg [] [] [] [.num 1, resolve "Pair"]))
  ])) with
  | Except.error err => innermostIsArityMismatch 1 0 err
  | _ => false

#guard loopInitialGroupedArgDoesNotSatisfyTwoOrdinaryParams

def loopInitialExplicitSelectionsSplitGroupedArg : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Step", loopBoundarySumPairStepAlg),
    ("Pair", alg [] [] [] [.num 1, .num 2])
  ] [
    .dotCall (resolve "Step") "repeat" (some (alg [] [] [] [
      .num 1,
      .index (resolve "Pair") (.num 0),
      .index (resolve "Pair") (.num 1)
    ]))
  ])) with
  | Except.ok (.atom 3) => true
  | _ => false

#guard loopInitialExplicitSelectionsSplitGroupedArg

def loopInitialGroupedHistorySlotCanBePreservedAcrossRepeat : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Step", loopBoundaryGroupedHistoryStepAlg),
    ("List", alg [] [] [] [.num 1, .num 2, .num 4])
  ] [
    .dotCall (resolve "Step") "repeat" (some (alg [] [] [] [.num 2, resolve "List"]))
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 4, .atom 5, .atom 6]) => true
  | _ => false

#guard loopInitialGroupedHistorySlotCanBePreservedAcrossRepeat

def loopInitialContentStepOutputStillBecomesNextStateSlots : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Step", loopBoundaryContentHistoryStepAlg),
    ("List", alg [] [] [] [.num 1, .num 2, .num 4])
  ] [
    .dotCall (resolve "Step") "repeat" (some (alg [] [] [] [.num 2, resolve "List"]))
  ])) with
  | Except.error err => innermostIsArityMismatch 0 3 err
  | _ => false

#guard loopInitialContentStepOutputStillBecomesNextStateSlots

def loopInitialMultiOutputPropertyArgIsOneSlot : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Step", loopBoundaryIdentityAlg),
    ("Values", alg [] [] [] [.num 1, .num 2, .num 4])
  ] [
    .dotCall (resolve "Step") "repeat" (some (alg [] [] [] [.num 1, resolve "Values"]))
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 4]) => true
  | _ => false

#guard loopInitialMultiOutputPropertyArgIsOneSlot

def loopInitialExplicitSelectionsSplitMultiOutputProperty : Bool :=
  match runResult (.block (algPrivate [] [] [
    ("Step", loopBoundaryVariadicIdentityAlg),
    ("Values", alg [] [] [] [.num 1, .num 2, .num 4])
  ] [
    .dotCall (resolve "Step") "repeat" (some (alg [] [] [] [
      .num 1,
      .index (resolve "Values") (.num 0),
      .index (resolve "Values") (.num 1),
      .index (resolve "Values") (.num 2)
    ]))
  ])) with
  | Except.ok (.group [.atom 1, .atom 2, .atom 4]) => true
  | _ => false

#guard loopInitialExplicitSelectionsSplitMultiOutputProperty

end KatLangTests
