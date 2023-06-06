﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Microsoft.CodeAnalysis.Shared.Extensions;

internal sealed class DirectiveInfo<TDirectiveTriviaSyntax>(
    IDictionary<TDirectiveTriviaSyntax, TDirectiveTriviaSyntax?> directiveMap,
    IDictionary<TDirectiveTriviaSyntax, ImmutableArray<TDirectiveTriviaSyntax>> conditionalMap)
    where TDirectiveTriviaSyntax : SyntaxNode
{
    // Maps a directive to its pair
    public IDictionary<TDirectiveTriviaSyntax, TDirectiveTriviaSyntax?> DirectiveMap { get; } = directiveMap;

    // Maps a #If/#elif/#else/#endIf directive to its list of matching #If/#elif/#else/#endIf directives
    public IDictionary<TDirectiveTriviaSyntax, ImmutableArray<TDirectiveTriviaSyntax>> ConditionalMap { get; } = conditionalMap;
}
