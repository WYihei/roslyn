﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using static System.String;

namespace CSharpSyntaxGenerator.Grammar
{
    internal class GrammarGenerator
    {
        private readonly ImmutableArray<TreeType> _nodes;
        private readonly Dictionary<string, List<Production>> _nameToProductions;

        public GrammarGenerator(Tree tree)
        {
            // Syntax refers to a special pseudo-element 'Modifier'.  Just synthesize that since
            // it's useful in the g4 grammar.
            var modifierKeywords = from mod in GetKinds<DeclarationModifiers>()
                                   let modKeyword = mod + "Keyword"
                                   from kind in SyntaxKinds
                                   where modKeyword == kind.ToString()
                                   select new Kind { Name = modKeyword };

            tree.Types.Add(new Node
            {
                Name = "Modifier",
                Children = { new Field { Type = SyntaxToken, Kinds = modifierKeywords.ToList() } }
            });

            _nodes = tree.Types.Where(t => t.Name != "CSharpSyntaxNode").ToImmutableArray();
            _nameToProductions = _nodes.ToDictionary(n => n.Name, _ => new List<Production>());
        }

        public string Run()
        {
            foreach (var node in _nodes)
            {
                // If this node has a base-type, then have the base-type point to this node as a
                // valid production for itself.
                if (node.Base is string nodeBase && _nameToProductions.TryGetValue(nodeBase, out var baseProductions))
                    baseProductions.Add(RuleReference(node.Name));

                if (node is Node)
                {
                    if (node.Children.Count == 0)
                        continue;

                    // Convert a rule of `a: (x | y | z)` into:
                    // a: x
                    //  | y
                    //  | z;
                    if (node.Children.Count == 1 && node.Children[0] is Field field && field.IsToken)
                    {
                        ProcessProductions(node, field.Kinds.Select(k =>
                            new List<TreeTypeChild> { new Field { Type = SyntaxToken, Kinds = { k } } }).ToArray());
                    }
                    else
                    {
                        ProcessProductions(node, node.Children);
                    }
                }
            }

            // The grammar will bottom out with certain lexical productions.  Just emit a few empty
            // productions in the grammar file indicating what's going on, and making it so that the
            // g4 file is considered legal (i.e. no rule references names of rules that don't
            // exist).

            foreach (var kind in s_lexicalTokens)
                _nameToProductions.Add(kind.ToString(), new List<Production> { new Production("/* see lexical specification */") });

            return GenerateResult();
        }

        private void ProcessProductions(TreeType node, params List<TreeTypeChild>[] productions)
            => _nameToProductions[node.Name].AddRange(productions.Select(p => ProcessChildren(p, delim: " ")));

        private string GenerateResult()
        {
            // Keep track of the rules we've emitted.  Once we've emitted a rule once, no need to do
            // it again, even if it's referenced by another rule.
            var seen = new HashSet<string>();
            var normalizedRules = new List<(string name, ImmutableArray<string> productions)>();

            // Process each major section.
            foreach (var section in s_majorSections)
                AddNormalizedRules(section);

            // Now go through the entire list and print out any other rules not hit transitively
            // from those sections.
            foreach (var name in _nameToProductions.Keys.OrderBy(a => a, StringComparer.Ordinal))
                AddNormalizedRules(name);

            return
@"// <auto-generated />
grammar csharp;" + Join("", normalizedRules.Select(t => Generate(t.name, t.productions)));

            void AddNormalizedRules(string name)
            {
                // Only consider the rule if it's the first time we're seeing it.
                if (seen.Add(name))
                {
                    // Order the productions alphabetically for consistency and to keep us independent
                    // from whatever ordering changes happen in Syntax.xml.
                    var sorted = _nameToProductions[name].OrderBy(v => v.Text, StringComparer.Ordinal).ToImmutableArray();
                    if (sorted.Length > 0)
                        normalizedRules.Add((Normalize(name), sorted.Select(s => s.Text).ToImmutableArray()));

                    // Now proceed in depth-first fashion through the rules the productions of this rule
                    // reference.  This helps keep related rules of these productions close by.
                    //
                    // Note: if we hit a major-section node, we don't recurse in.  This keeps us from
                    // travelling too far away, and keeps the major sections relatively cohesive.
                    var references = sorted.SelectMany(t => t.RuleReferences).Where(r => !s_majorSections.Contains(r));
                    foreach (var reference in references)
                        AddNormalizedRules(reference);
                }
            }
        }

        private static string Generate(string name, ImmutableArray<string> productions)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(name);
            sb.Append("  : ");

            if (productions.Length == 0)
            {
                throw new InvalidOperationException("Rule didn't have any productions: " + name);
            }

            sb.AppendJoin(Environment.NewLine + "  | ", productions);
            sb.AppendLine();
            sb.Append("  ;");

            return sb.ToString();
        }

        /// <summary>
        /// Returns the g4 production string for this rule based on the children it has. Also
        /// returns all the names of other rules this particular production references.
        /// </summary>
        private Production ProcessChildren(List<TreeTypeChild> children, string delim)
        {
            var result = children.Select(child => child switch
            {
                Choice c => ProcessChildren(c.Children, " | ").Parenthesize(),
                Sequence s => ProcessChildren(s.Children, " ").Parenthesize(),
                Field f => GetFieldType(f).WithSuffix(f.IsOptional ? "?" : ""),
                _ => throw new InvalidOperationException(),
            }).Where(p => p.Text.Length > 0);

            return new Production(
                Join(delim, result.Select(t => t.Text)),
                result.SelectMany(t => t.RuleReferences));
        }

        private Production GetFieldType(Field field)
            // 'bool' fields are for the few boolean properties we generate on DirectiveTrivia.
            // They're not relevant to the grammar, so we just return an empty production here
            // which will be filtered out by the caller.
            => field.Type == "bool" ? new Production("") :
               field.Type == "CSharpSyntaxNode" ? HandleCSharpSyntaxNodeField(field) :
               field.Type.StartsWith("SeparatedSyntaxList") ? HandleSeparatedSyntaxListField(field) :
               field.Type.StartsWith("SyntaxList") ? HandleSyntaxListField(field) :
               field.IsToken ? HandleSyntaxTokenField(field) : RuleReference(field.Type);

        private static Production HandleSyntaxTokenField(Field field)
        {
            var production = new Production(field.Kinds.Count == 0
                ? GetTokenText(GetTokenKind(field.Name))
                : Join(" | ", GetTokenKindStrings(field)));
            return field.Kinds.Count <= 1 ? production : production.Parenthesize();
        }

        private Production HandleCSharpSyntaxNodeField(Field field)
            => RuleReference(field.Kinds.Single().Name + Syntax);

        private Production HandleSeparatedSyntaxListField(Field field)
        {
            var production = RuleReference(field.Type[("SeparatedSyntaxList".Length + 1)..^1]);
            var result = production.WithSuffix(" (',' " + production + ")*");
            result = field.AllowTrailingSeparator != null ? result.WithSuffix(" ','?") : result;
            return field.MinCount != null ? result : result.Parenthesize().WithSuffix("?");
        }

        private Production HandleSyntaxListField(Field field)
            => GetSyntaxListUnderlyingType(field).WithSuffix(field.MinCount != null ? "+" : "*");

        private Production GetSyntaxListUnderlyingType(Field field)
            => field.Name switch
            {
                // Specialized token lists that we want the grammar to be more precise about. i.e.
                // we don't want `Commas` to be in the grammar as `token*` (implying that it could
                // be virtually any token.
                "Commas" => new Production("','"),
                "Modifiers" => RuleReference("Modifier"),
                "Tokens" => new Production(Normalize("Token")),
                "TextTokens" => new Production(Normalize("XmlTextLiteralToken")),
                _ => RuleReference(field.Type[("SyntaxList".Length + 1)..^1])
            };

        private static IEnumerable<string> GetTokenKindStrings(Field field)
            => field.Kinds.Select(k => GetTokenText(GetTokenKind(k.Name))).OrderBy(a => a, StringComparer.Ordinal);

        private static string GetTokenText(SyntaxKind kind)
        {
            switch (kind)
            {
                case SyntaxKind.EndOfFileToken:
                    // Emit the special antlr EOF token indicating this production should consume
                    // the entire file.
                    return "EOF";
                case SyntaxKind.EndOfDocumentationCommentToken:
                case SyntaxKind.EndOfDirectiveToken:
                    // Don't emit anything in the production for these.
                    return null;
                case SyntaxKind.OmittedTypeArgumentToken:
                case SyntaxKind.OmittedArraySizeExpressionToken:
                    // Indicate that these productions are explicitly empty.
                    return "/* epsilon */";
            }

            // Map these token kinds to just a synthesized rule that we state is
            // declared elsewhere.
            if (s_lexicalTokens.Contains(kind.ToString()))
                return Normalize(kind.ToString());

            var result = SyntaxFacts.GetText(kind);
            if (result == "")
                throw new NotImplementedException("Unexpected SyntaxKind: " + kind);

            return result == "'"
                ? @"'\''"
                : "'" + result + "'";
        }

        private static IEnumerable<TSyntaxKind> GetKinds<TSyntaxKind>() where TSyntaxKind : struct, System.Enum
            => typeof(TSyntaxKind).GetFields(BindingFlags.Public | BindingFlags.Static)
                                  .Select(f => (TSyntaxKind)f.GetValue(null));

        private static IEnumerable<SyntaxKind> SyntaxKinds => GetKinds<SyntaxKind>();

        private static SyntaxKind GetTokenKind(string tokenName)
            => tokenName == "Identifier"
                ? SyntaxKind.IdentifierToken
                : SyntaxKinds.Where(k => k.ToString() == tokenName).Single();

        private Production RuleReference(string ruleName)
            => _nameToProductions.ContainsKey(ruleName)
                ? new Production(Normalize(ruleName), ImmutableArray.Create(ruleName))
                : throw new InvalidOperationException("No rule found with name: " + ruleName);

        /// <summary>
        /// Converts a <c>PascalCased</c> name into <c>snake_cased</c> name.
        /// </summary>
        private static string Normalize(string name)
            => s_normalizationRegex.Replace(name.EndsWith(Syntax) ? name[..^Syntax.Length] : name, "_").ToLower();

        private static readonly Regex s_normalizationRegex = new Regex(@"
            (?<=[A-Z])(?=[A-Z][a-z]) |
            (?<=[^A-Z])(?=[A-Z]) |
            (?<=[A-Za-z])(?=[^A-Za-z])", RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

        // Special constants we use in a few places.

        private const string Syntax = "Syntax";
        private const string SyntaxToken = "SyntaxToken";

        private static readonly ImmutableArray<string> s_lexicalTokens
            = ImmutableArray.Create(
                "Token",
                nameof(SyntaxKind.IdentifierToken),
                nameof(SyntaxKind.CharacterLiteralToken),
                nameof(SyntaxKind.StringLiteralToken),
                nameof(SyntaxKind.NumericLiteralToken),
                nameof(SyntaxKind.InterpolatedStringTextToken),
                nameof(SyntaxKind.XmlTextLiteralToken));

        // This is optional, but makes the emitted g4 file a bit nicer.  Basically, we define a few
        // major sections (generally, corresponding to base nodes that have a lot of derived nodes).
        // If we hit these nodes while recursing through another node, we won't just print them out
        // then.  Instead, we'll wait till we're done with the previous nodes, then start emitting
        // these.  This helps organize the final g4 document into reasonable sections.  i.e. you
        // generally see all the member declarations together, and all the statements together and
        // all the expressions together.  Without this, just processing CompilationUnit will cause
        // expressions to print early because of things like:
        // CompilationUnit->AttributeList->AttributeArg->Expression.

        private static readonly ImmutableArray<string> s_majorSections = ImmutableArray.Create(
            "CompilationUnitSyntax",
            "MemberDeclarationSyntax",
            "StatementSyntax",
            "ExpressionSyntax",
            "TypeSyntax",
            "XmlNodeSyntax",
            "StructuredTriviaSyntax");
    }

    internal struct Production
    {
        /// <summary>
        /// The line of text to include in the grammar file.  i.e. everything after
        /// <c>:</c> in <c>: 'extern' 'alias' identifier_token ';'</c>.
        /// </summary>
        public readonly string Text;

        /// <summary>
        /// The names of other rules that are referenced by this rule.  Used purely as an aid to
        /// help order productions when emitting.  In general, we want to keep referenced rules
        /// close to the rule that references them.
        /// </summary>
        public readonly ImmutableArray<string> RuleReferences;

        public Production(string text)
            : this(text, ImmutableArray<string>.Empty)
        {
        }

        public Production(string text, IEnumerable<string> ruleReferences)
        {
            Text = text;
            RuleReferences = ruleReferences.ToImmutableArray();
        }

        public Production WithPrefix(string prefix) => new Production(prefix + this, RuleReferences);
        public Production WithSuffix(string suffix) => new Production(this + suffix, RuleReferences);
        public Production Parenthesize() => WithPrefix("(").WithSuffix(")");
        public override string ToString() => Text;
    }
}

namespace Microsoft.CodeAnalysis
{
    internal static class GreenNode
    {
        internal const int ListKind = 1; // See SyntaxKind.
    }
}
