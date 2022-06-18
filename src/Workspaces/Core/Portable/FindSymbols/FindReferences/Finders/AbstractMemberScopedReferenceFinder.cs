﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.FindSymbols.Finders
{
    internal abstract class AbstractMemberScopedReferenceFinder<TSymbol> : AbstractReferenceFinder<TSymbol>
        where TSymbol : ISymbol
    {
        protected sealed override bool CanFind(TSymbol symbol)
            => true;

        protected sealed override Task<ImmutableArray<Document>> DetermineDocumentsToSearchAsync(
            TSymbol symbol,
            HashSet<string>? globalAliases,
            Project project,
            IImmutableSet<Document>? documents,
            FindReferencesSearchOptions options,
            CancellationToken cancellationToken)
        {
            var location = symbol.Locations.FirstOrDefault();
            if (location == null || !location.IsInSource)
            {
                return SpecializedTasks.EmptyImmutableArray<Document>();
            }

            var document = project.GetDocument(location.SourceTree);
            if (document == null)
            {
                return SpecializedTasks.EmptyImmutableArray<Document>();
            }

            if (documents != null && !documents.Contains(document))
            {
                return SpecializedTasks.EmptyImmutableArray<Document>();
            }

            return Task.FromResult(ImmutableArray.Create(document));
        }

        protected sealed override async ValueTask<ImmutableArray<FinderLocation>> FindReferencesInDocumentAsync(
            TSymbol symbol,
            FindReferencesDocumentState state,
            FindReferencesSearchOptions options,
            CancellationToken cancellationToken)
        {
            var container = GetContainer(symbol);
            if (container != null)
            {
                return await FindReferencesInContainerAsync(symbol, container, state, cancellationToken).ConfigureAwait(false);
            }

            if (symbol.ContainingType != null && symbol.ContainingType.IsScriptClass)
            {
                var syntaxTree = state.SyntaxTree;
                var syntaxFacts = state.SyntaxFacts;
                var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                var tokens = root.DescendantTokens();

                return await FindReferencesInTokensWithSymbolNameAsync(
                    symbol, state, tokens, cancellationToken).ConfigureAwait(false);
            }

            return ImmutableArray<FinderLocation>.Empty;
        }

        private static ISymbol? GetContainer(ISymbol symbol)
        {
            for (var current = symbol; current != null; current = current.ContainingSymbol)
            {
                if (current is IPropertySymbol)
                {
                    return current;
                }

                // If this is an initializer for a property's backing field, then we want to 
                // search for results within the property itself.
                if (current is IFieldSymbol field)
                {
                    if (field.IsImplicitlyDeclared &&
                        field.AssociatedSymbol?.Kind == SymbolKind.Property)
                    {
                        return field.AssociatedSymbol;
                    }
                    else
                    {
                        return field;
                    }
                }

                if (current is IMethodSymbol method &&
                    method.MethodKind != MethodKind.AnonymousFunction &&
                    method.MethodKind != MethodKind.LocalFunction)
                {
                    return method;
                }
            }

            return null;
        }

        protected static ValueTask<ImmutableArray<FinderLocation>> FindReferencesInTokensWithSymbolNameAsync(
            TSymbol symbol,
            FindReferencesDocumentState state,
            IEnumerable<SyntaxToken> tokens,
            CancellationToken cancellationToken)
        {
            return FindReferencesInTokensWithSymbolNameAsync(
                symbol, state, tokens, findParentNode: null, cancellationToken);
        }

        protected static ValueTask<ImmutableArray<FinderLocation>> FindReferencesInTokensWithSymbolNameAsync(
            TSymbol symbol,
            FindReferencesDocumentState state,
            IEnumerable<SyntaxToken> tokens,
            Func<SyntaxToken, SyntaxNode>? findParentNode,
            CancellationToken cancellationToken)
        {
            var name = symbol.Name;
            var syntaxFacts = state.SyntaxFacts;
            var symbolsMatch = GetStandardSymbolsMatchFunction(
                symbol, findParentNode, state, cancellationToken);

            return FindReferencesInTokensAsync(
                state,
                tokens,
                t => IdentifiersMatch(syntaxFacts, name, t),
                symbolsMatch,
                cancellationToken);
        }

        private ValueTask<ImmutableArray<FinderLocation>> FindReferencesInContainerAsync(
            TSymbol symbol,
            ISymbol container,
            FindReferencesDocumentState state,
            CancellationToken cancellationToken)
        {
            return FindReferencesInContainerAsync(
                symbol, container, state, findParentNode: null, cancellationToken);
        }

        private ValueTask<ImmutableArray<FinderLocation>> FindReferencesInContainerAsync(
            TSymbol symbol,
            ISymbol container,
            FindReferencesDocumentState state,
            Func<SyntaxToken, SyntaxNode>? findParentNode,
            CancellationToken cancellationToken)
        {
            var service = state.Document.GetRequiredLanguageService<ISymbolDeclarationService>();
            var declarations = service.GetDeclarations(container);
            var tokens = declarations.SelectMany(r => r.GetSyntax(cancellationToken).DescendantTokens());

            var name = symbol.Name;
            var syntaxFacts = state.SyntaxFacts;
            var symbolsMatch = GetStandardSymbolsMatchFunction(
                symbol, findParentNode, state, cancellationToken);
            var tokensMatch = GetTokensMatchFunction(syntaxFacts, name);

            return FindReferencesInTokensAsync(
                state,
                tokens,
                tokensMatch,
                symbolsMatch,
                cancellationToken);
        }

        protected abstract Func<SyntaxToken, bool> GetTokensMatchFunction(ISyntaxFactsService syntaxFacts, string name);
    }
}
