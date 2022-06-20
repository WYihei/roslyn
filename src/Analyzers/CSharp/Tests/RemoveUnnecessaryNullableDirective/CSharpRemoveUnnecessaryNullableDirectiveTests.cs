﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.CodeFixes.RemoveUnnecessaryNullableDirective;
using Microsoft.CodeAnalysis.Editor.UnitTests.CodeActions;
using Microsoft.CodeAnalysis.RemoveUnnecessaryNullableDirective;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.Analyzers.UnitTests.RemoveUnnecessaryNullableDirective
{
    using VerifyCS = CSharpCodeFixVerifier<
        CSharpRemoveUnnecessaryNullableDirectiveDiagnosticAnalyzer,
        CSharpRemoveUnnecessaryNullableDirectiveCodeFixProvider>;

    [Trait(Traits.Feature, Traits.Features.CodeActionsRemoveUnnecessaryNullableDirective)]
    public class CSharpRemoveUnnecessaryNullableDirectiveTests
    {
        [Theory]
        [InlineData(NullableContextOptions.Annotations, NullableContextOptions.Annotations)]
        [InlineData(NullableContextOptions.Annotations, NullableContextOptions.Enable)]
        [InlineData(NullableContextOptions.Warnings, NullableContextOptions.Warnings)]
        [InlineData(NullableContextOptions.Warnings, NullableContextOptions.Enable)]
        [InlineData(NullableContextOptions.Enable, NullableContextOptions.Annotations)]
        [InlineData(NullableContextOptions.Enable, NullableContextOptions.Warnings)]
        [InlineData(NullableContextOptions.Enable, NullableContextOptions.Enable)]
        public async Task TestUnnecessaryDisableDiffersFromCompilation(NullableContextOptions compilationContext, NullableContextOptions codeContext)
        {
            await VerifyCodeFixAsync(
                compilationContext,
                $$"""
                [|#nullable {{GetDisableDirectiveContext(codeContext)}}|]
                class Program
                {
                }
                """,
                $$"""

                class Program
                {
                }
                """);
        }

        [Fact]
        public async Task TestUnnecessaryDisableEnumDeclaration()
        {
            await VerifyCodeFixAsync(
                NullableContextOptions.Enable,
                """
                [|#nullable disable|]
                enum EnumName
                {
                    First,
                    Second,
                }
                """,
                """

                enum EnumName
                {
                    First,
                    Second,
                }
                """);
        }

        [Theory]
        [InlineData("disable")]
        [InlineData("restore")]
        public async Task TestUnnecessaryDisableAtEndOfFile(string keyword)
        {
            await VerifyCodeFixAsync(
                NullableContextOptions.Disable,
                $$"""
                #nullable enable
                struct StructName
                {
                    string Field;
                }
                [|#nullable {{keyword}}|]

                """,
                $$"""
                #nullable enable
                struct StructName
                {
                    string Field;
                }
                

                """);
        }

        private static string GetDisableDirectiveContext(NullableContextOptions options)
        {
            return options switch
            {
                NullableContextOptions.Warnings => "disable warnings",
                NullableContextOptions.Annotations => "disable annotations",
                NullableContextOptions.Enable => "disable",
                _ => throw ExceptionUtilities.UnexpectedValue(options),
            };
        }

        private static async Task VerifyCodeFixAsync(NullableContextOptions compilationNullableContextOptions, string source, string fixedSource)
        {
            await new VerifyCS.Test
            {
                TestCode = source,
                FixedCode = fixedSource,
                SolutionTransforms =
                {
                    (solution, projectId) =>
                    {
                        var compilationOptions = (CSharpCompilationOptions?)solution.GetRequiredProject(projectId).CompilationOptions;
                        Contract.ThrowIfNull(compilationOptions);

                        return solution.WithProjectCompilationOptions(projectId, compilationOptions.WithNullableContextOptions(compilationNullableContextOptions));
                    },
                },
            }.RunAsync();
        }
    }
}
