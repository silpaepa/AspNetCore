// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.AspNetCore.Mvc.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class TagHelpersInCodeBlocksAnalyzer : DiagnosticAnalyzer
    {
        public TagHelpersInCodeBlocksAnalyzer()
        {
            TagHelperInCodeBlockDiagnostic = DiagnosticDescriptors.MVC1006_FunctionsContainingTagHelpersMustBeAsyncAndReturnTask;
            SupportedDiagnostics = ImmutableArray.Create(new[] { TagHelperInCodeBlockDiagnostic });
        }

        private DiagnosticDescriptor TagHelperInCodeBlockDiagnostic { get; }

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.RegisterCompilationStartAction(context =>
            {
                var symbolCache = new SymbolCache(context.Compilation);

                if (symbolCache.TagHelperRunnerRunAsyncMethodSymbol == null)
                {
                    // No-op if we can't find bits we care about.
                    return;
                }

                InitializeWorker(context, symbolCache);
            });
        }

        private void InitializeWorker(CompilationStartAnalysisContext context, SymbolCache symbolCache)
        {
            context.RegisterOperationBlockStartAction(startBlockContext =>
            {
                startBlockContext.RegisterOperationAction(context =>
                {
                    var awaitOperation = (IAwaitOperation)context.Operation;

                    if (awaitOperation.Operation.Kind != OperationKind.Invocation)
                    {
                        return;
                    }

                    var invocationOperation = (IInvocationOperation)awaitOperation.Operation;

                    if (!IsTagHelperRunnerRunAsync(invocationOperation.TargetMethod, symbolCache))
                    {
                        return;
                    }

                    var parent = context.Operation.Parent;
                    while (parent != null && !IsParentMethod(parent))
                    {
                        parent = parent.Parent;
                    }

                    if (parent == null)
                    {
                        return;
                    }

                    var methodSymbol = (IMethodSymbol)(parent switch
                    {
                        ILocalFunctionOperation localFunctionOperation => localFunctionOperation.Symbol,
                        IAnonymousFunctionOperation anonymousFunctionOperation => anonymousFunctionOperation.Symbol,
                        IMethodBodyOperation methodBodyOperation => startBlockContext.OwningSymbol,
                        _ => null,
                    });

                    if (methodSymbol == null)
                    {
                        // Unsupported operation type.
                        return;
                    }

                    if (!methodSymbol.IsAsync ||
                        !symbolCache.TaskType.IsAssignableFrom(methodSymbol.ReturnType))
                    {
                        if (parent.Syntax.GetDiagnostics().Any(diagnostic => diagnostic.Id == TagHelperInCodeBlockDiagnostic.Id))
                        {
                            // Diagnostic has already been logged on this syntax item, no-op.
                            return;
                        }

                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                TagHelperInCodeBlockDiagnostic,
                                parent.Syntax.GetLocation()));
                    }

                }, OperationKind.Await);
            });
        }

        private bool IsTagHelperRunnerRunAsync(IMethodSymbol method, SymbolCache symbolCache)
        {
            if (method.IsGenericMethod)
            {
                return false;
            }

            if (method != symbolCache.TagHelperRunnerRunAsyncMethodSymbol)
            {
                return false;
            }

            return true;
        }

        private bool IsParentMethod(IOperation operation)
        {
            if (operation.Kind == OperationKind.LocalFunction)
            {
                return true;
            }

            if (operation.Kind == OperationKind.MethodBody)
            {
                return true;
            }

            if (operation.Kind == OperationKind.AnonymousFunction)
            {
                return true;
            }

            return false;
        }

        private readonly struct SymbolCache
        {
            public SymbolCache(Compilation compilation)
            {
                var tagHelperRunnerType = compilation.GetTypeByMetadataName(SymbolNames.TagHelperRunnerTypeName);
                var members = tagHelperRunnerType.GetMembers(SymbolNames.RunAsyncMethodName);

                TagHelperRunnerRunAsyncMethodSymbol = members.Length == 1 ? (IMethodSymbol)members[0] : null;
                TaskType = compilation.GetTypeByMetadataName(SymbolNames.TaskTypeName);
            }

            public IMethodSymbol TagHelperRunnerRunAsyncMethodSymbol { get; }

            public INamedTypeSymbol TaskType { get; }
        }
    }
}
