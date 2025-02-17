﻿global using System.Threading.Tasks;
global using Xunit;
global using TaggedCase = (string Case, bool Tagged);
using System;
using System.Collections.Generic;
using System.Linq;

namespace RhymesOfUncertainty.Test;

internal static class Shared
{
    internal static readonly string Structs = @"
readonly struct Either<T1, T2>
{
    public object Value { get; }
}
readonly struct Either<T1, T2, T3>
{
    public object Value { get; }
}
";

    internal static string GenerateSwitch(SwitchType switchType, IList<string> typesToCheck, IList<string> casesChecked, SwitchGenerationOptions options = null)
    {
        options ??= new SwitchGenerationOptions();
        var cases = new List<Either<TaggedCase, TaggedCase[]>>();

        foreach (var @case in casesChecked)
        {
            if (@case.Contains('|'))
            {
                cases.Add(@case.Split('|').Select(GetTaggedCase).ToArray());
            }
            else
            {
                cases.Add(GetTaggedCase(@case));
            }
        }

        return switchType switch
        {
            SwitchType.Stmt => GenerateSwitchStmt(typesToCheck, cases, tagSwitch: options.TagSwitch, usings: options.Usings, isNullForgiving: options.IsNullForgiving, tagExpr: options.TagExpr),
            SwitchType.Expr => GenerateSwitchExpr(typesToCheck, cases.Select(c => (TaggedCase)c.Value).ToArray(), tagSwitch: options.TagSwitch, usings: options.Usings, isNullForgiving: options.IsNullForgiving, tagExpr: options.TagExpr),
            _ => throw new ArgumentOutOfRangeException(nameof(switchType)),
        };

        static TaggedCase GetTaggedCase(string c)
        {
            return c.StartsWith("t:") ? new TaggedCase(c[2..], true) : new TaggedCase(c, false);
        }
    }

    internal static string GenerateSwitchStmt
    (
        IList<string> typesToCheck,
        IList<Either<TaggedCase, TaggedCase[]>> casesChecked,
        bool tagSwitch = false,
        string[] usings = default,
        bool isNullForgiving = false,
        bool tagExpr = false
    )
    {
        int tagNumber = 0;

        var code =
            string.Join("\n", (usings ?? []).Select(u => $"using {u};")) +
            "\n" +
            Structs +
@$"
class C
{{
    void M(Either<{string.Join(", ", typesToCheck)}> x)
    {{
        {TagIfNecessary("switch", tagSwitch)} ({Expr()})
        {{
            {string.Join("\r\n            ", casesChecked.Select(GetCase).Select(c => $"{c}\n                break;"))}
        }}
    }}
}}
";
        return code;

        string Expr()
        {
            return TagIfNecessary(isNullForgiving ? "x.Value!" : "x.Value", tagExpr);
        }

        string GetCase(Either<TaggedCase, TaggedCase[]> caseChecked)
        {
            return caseChecked.Value switch
            {
                TaggedCase tc => GetSingleCase(tc.Case, tc.Tagged),
                TaggedCase[] tcs => string.Join("\n            ", tcs.Select(tc => GetSingleCase(tc.Case, tc.Tagged)))
            };
        }

        string GetSingleCase(string @case, bool tagged)
        {
            return @case is "default" ? TagIfNecessary("default:", tagged) : TagIfNecessary($"case {@case}:", tagged);
        }

        string TagIfNecessary(string s, bool necessary)
        {
            return necessary ? $"{{|#{tagNumber++}:{s}|}}" : s;
        }
    }

    internal static string GenerateSwitchExpr
    (
        IList<string> typesToCheck,
        TaggedCase[] casesChecked,
        bool tagSwitch = false,
        string[] usings = default,
        bool isNullForgiving = false,
        bool tagExpr = false
    )
    {
        int tagNumber = 0;

        var code =
            string.Join("\n", (usings ?? []).Select(u => $"using {u};")) +
            "\n" +
            Structs +
@$"
class C
{{
    int M(Either<{string.Join(", ", typesToCheck)}> x)
    {{
        return {Expr()} {TagIfNecessary("switch", tagSwitch)}
        {{
            {string.Join(",\n            ", casesChecked.Select(GetCase))}
        }};
    }}
}}
";
        return code;

        string Expr()
        {
            return TagIfNecessary(isNullForgiving ? "x.Value!" : "x.Value", tagExpr);
        }

        string GetCase(TaggedCase tc)
        {
            return $"{TagIfNecessary(tc.Case, tc.Tagged)} => 0";
        }

        string TagIfNecessary(string s, bool necessary)
        {
            return necessary ? $"{{|#{tagNumber++}:{s}|}}" : s;
        }
    }
}

public enum SwitchType
{
    Stmt,
    Expr,
}

internal class SwitchGenerationOptions
{
    internal bool TagSwitch { get; init; } = false;
    internal string[] Usings { get; init; } = default;
    internal bool IsNullForgiving { get; init; } = false;
    internal bool TagExpr { get; init; } = false;
}