﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Either;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class EitherAnalyzer : DiagnosticAnalyzer
{
    public const string NotExhaustiveId = "SwitchExhaustiveness";
    private static readonly DiagnosticDescriptor NotExhaustiveRule = new
    (
        NotExhaustiveId,
        title: "The switch statement is not exhaustive",
        messageFormat: "The switch statement is not exhaustive. {0} '{1}' {2} not accounted for.",
        category: "Compiler",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public const string RedundantCaseId = "SwitchRedundantCase";
    private static readonly DiagnosticDescriptor RedundantCaseRule = new
    (
        RedundantCaseId,
        title: "The case is redundant",
        messageFormat: "The case is redundant. '{0}' is not on the list of possible cases.",
        category: "Compiler",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public const string RedundantDefaultId = "SwitchRedundantDefault";
    private static readonly DiagnosticDescriptor RedundantDefaultRule = new
    (
        RedundantDefaultId,
        title: "The default case is redundant",
        messageFormat: "The default case is redundant. {0} possible cases are handled.",
        category: "Compiler",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return ImmutableArray.Create(NotExhaustiveRule, RedundantCaseRule, RedundantDefaultRule); } }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeOperation, OperationKind.Switch);

        //SyntaxKind.ConditionalAccessExpression
        //SyntaxKind.SuppressNullableWarningExpression
    }

    private void AnalyzeOperation(OperationAnalysisContext context)
    {
        var switchOp = (ISwitchOperation)context.Operation;
        var switchSyntax = (SwitchStatementSyntax)switchOp.Syntax;
        var isNullForgiving = switchSyntax.Expression.IsKind(SyntaxKind.SuppressNullableWarningExpression);

        if (switchOp.Value.Kind != OperationKind.PropertyReference)
        {
            return;
        }

        var propertyReference = (IPropertyReferenceOperation)switchOp.Value;

        if (propertyReference.Property.Name != "Value")
        {
            return;
        }

        //Test switching on an anonymous object probably
        //Test switching on something other than either
        if (propertyReference.Instance == null ||
            propertyReference.Instance.Type == null ||
            propertyReference.Instance.Type.Kind != SymbolKind.NamedType ||
            propertyReference.Instance.Type.Name != "Either")
        {
            return;
        }

        var typeSwitchedOn = (INamedTypeSymbol)propertyReference.Instance.Type;
        if (typeSwitchedOn.Arity <= 1) //TODO test switching on type called Either that has an arity of 1 or 0
        {
            return;
        }

        ////TODO, is the symbol equality comparer thing necessary? prolly yes?
        //var nonNullableTypeArgs = typeSwitchedOn.TypeArguments
        //    .Select(ta => ta.WithNullableAnnotation(NullableAnnotation.NotAnnotated))
        //    .ToList();
        //var casesThatNeedToBeHandled = new HashSet<ITypeSymbol?>(nonNullableTypeArgs, SymbolEqualityComparer.Default);
        //var unhandledCases = new HashSet<ITypeSymbol?>(nonNullableTypeArgs, SymbolEqualityComparer.Default);
        //var mustHandleNull = typeSwitchedOn.TypeArguments.Any(ta => ta.IsReferenceType || ta.NullableAnnotation == NullableAnnotation.Annotated);
        //if (mustHandleNull)
        //{
        //    casesThatNeedToBeHandled.Add(null);
        //    unhandledCases.Add(null);
        //}

        var casesThatNeedToBeHandled = typeSwitchedOn.GetCasesThatNeedToBeHandled(isNullForgiving);
        var unhandledCases = new HashSet<ITypeSymbol?>(casesThatNeedToBeHandled, SymbolEqualityComparer.Default);

        var redundantCases = new List<(IPatternCaseClauseOperation Op, ITypeSymbol? Type)>();
        var defaultCase = default(IDefaultCaseClauseOperation);

        for (int i = 0; i < switchOp.Cases.Length; i++)
        {
            var @case = switchOp.Cases[i];
            for (int j = 0; j < @case.Clauses.Length; j++)
            {
                var clause = @case.Clauses[j];
                if (clause.CaseKind == CaseKind.Default)
                {
                    defaultCase = (IDefaultCaseClauseOperation)clause;
                    continue;
                }

                if (clause.CaseKind == CaseKind.Pattern) //THE WHEN THING! with a test!
                {
                    var c = (IPatternCaseClauseOperation)clause;

                    ITypeSymbol matchedType;

                    if (c.Pattern.Kind == OperationKind.TypePattern)
                    {
                        var p = (ITypePatternOperation)c.Pattern;
                        matchedType = p.MatchedType;
                    }
                    else if (c.Pattern.Kind == OperationKind.DeclarationPattern)
                    {
                        var p = (IDeclarationPatternOperation)c.Pattern;
                        matchedType = p.MatchedType; // the var thing check!
                    }
                    else if (c.Pattern.Kind == OperationKind.ConstantPattern)
                    {
                        var p = (IConstantPatternOperation)c.Pattern;
                        if (!p.Value.Syntax.IsKind(SyntaxKind.NullLiteralExpression))
                        {
                            continue;
                        }
                        matchedType = null;
                    }
                    else if (c.Pattern.Kind == OperationKind.RecursivePattern)
                    {
                        var p = (IRecursivePatternOperation)c.Pattern;

                        var tupleType = p.GetTupleType(context.Compilation);
                        if (tupleType == null)
                        {
                            continue;
                        }

                        matchedType = tupleType;
                    }
                    else
                    {
                        continue;
                    }

                    if (casesThatNeedToBeHandled.Contains(matchedType))
                    {
                        unhandledCases.Remove(matchedType);
                    }
                    else
                    {
                        redundantCases.Add((c, matchedType));
                    }
                    continue;
                }
            }
        }

        if (defaultCase != null)
        {
            if (unhandledCases.Count == 0)
            {
                var wordBoth = casesThatNeedToBeHandled.Count == 2 ? "Both" : "All";
                var diagnostic = Diagnostic.Create(RedundantDefaultRule, defaultCase.Syntax.GetLocation(), wordBoth);
                context.ReportDiagnostic(diagnostic);
            }
            else
            {
                unhandledCases.Clear();
            }
        }

        if (unhandledCases.Count > 0)
        {
            var (wordType, wordIs) = unhandledCases.Count > 1 ? ("Cases", "are") : ("Case", "is");
            var typeNames = string.Join(", ", unhandledCases
                .Select(t => t?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat) ?? "null")
            );

            var diagnostic = Diagnostic.Create(NotExhaustiveRule, switchSyntax.SwitchKeyword.GetLocation(), wordType, typeNames, wordIs);
            context.ReportDiagnostic(diagnostic);
        }

        foreach (var (op, type) in redundantCases)
        {
            var diagnostic = Diagnostic.Create(RedundantCaseRule, op.Syntax.GetLocation(), type?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat) ?? "null");
            context.ReportDiagnostic(diagnostic);
        }
    }

    //Analyze this:

    //DONE - //1. What if there is a clause that Doesn't match anything?
    //ex: you have Either<string, int> and there's case boolean b

    //DONE - //1.5 the default clause might be redundant when all cases are covered

    //DONE - //2. Obviously make sure all cases are covered

    //3. the default assignment

    //4. null, null-forgiveness or whatever
    //4.1 null-forgiveness is redundant because they're all value types?

    //4.5 non-nullable reference types - a whole different story

    //5. switch expression

    //6. be strict about matching specific types and forget about inheritance?

    //7. Should I call it Item? should I call it Value?

    //8. All type kinds, classes, structs, delegates, enums, record classes?, record structs?

    //DONE - //9. Matching generic types, type arguments must match like List<string> should not match List<int>!!!

    //10. Matching generic types, case T1, case T2 in a generic function baby (this is getting out of hand)
    //11. Matching Tuples
    //12. Will aliasing work? like using SomeOtherType = Either<int, string> I mean it has to right?
    //13. How about matching System.Nullable<T> when typed out Explicitly like that
    //13.5 Matching interfaces? Generic interfaces?

    //14. Either<int, int> should warn? Check how it works first, (low priority)

    //15. Base Type
}

public static class Extensions
{
    public static HashSet<ITypeSymbol?> GetCasesThatNeedToBeHandled(this INamedTypeSymbol namedType, bool ignoreNull)
    {
        var result = new HashSet<ITypeSymbol?>(SymbolEqualityComparer.Default);
        var hasNullableType = false;

        for (int i = 0; i < namedType.TypeArguments.Length; i++)
        {
            var type = namedType.TypeArguments[i];

            if (CanUnwrap(type, out var unwrappedType))
            {
                result.Add(unwrappedType);
                hasNullableType = true;
                continue;
            }

            result.Add(type);
            if (type.IsReferenceType)
            {
                hasNullableType = true;
            }
        }

        if (hasNullableType && !ignoreNull)
        {
            result.Add(null);
        }

        return result;
    }

    private static bool CanUnwrap(this ITypeSymbol type, out ITypeSymbol? unwrapped)
    {
        if (!type.IsValueType ||
            type.ContainingNamespace.Name != "System" ||
            type.Name != "Nullable" ||
            type.Kind != SymbolKind.NamedType)
        {
            unwrapped = null;
            return false;
        }

        var namedType = (INamedTypeSymbol)type;
        if (namedType.Arity != 1)
        {
            unwrapped = null;
            return false;
        }

        unwrapped = namedType.TypeArguments[0];
        return true;
    }

    public static INamedTypeSymbol? GetTupleType(this IRecursivePatternOperation pattern, Compilation compilation)
    {
        var types = new ITypeSymbol[pattern.DeconstructionSubpatterns.Length];
        for (int k = 0; k < pattern.DeconstructionSubpatterns.Length; k++)
        {
            if (pattern.DeconstructionSubpatterns[k].Kind != OperationKind.TypePattern)
            {
                return null;
            }

            var typePatternOp = (ITypePatternOperation)pattern.DeconstructionSubpatterns[k];
            types[k] = typePatternOp.MatchedType;
        }
        var tupleType = compilation.CreateTupleTypeSymbol(ImmutableArray.Create(types));

        return tupleType;
    }
}