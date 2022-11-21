﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Editor.Shared.Preview;
using Microsoft.CodeAnalysis.Editor;
using System.Collections.Immutable;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.CodeAnalysis.Editor.Shared.Tagging;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Editor.Tagging;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Workspaces;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using System.Linq;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.Diagnostics
{
    internal sealed class DiagnosticDataTag : ITag
    {
        public readonly DiagnosticData DiagnosticData;

        public DiagnosticDataTag(DiagnosticData diagnosticData)
        {
            DiagnosticData = diagnosticData;
        }
    }

    internal enum RawDiagnosticType
    {
        Compiler = 1 << 0,
        Analyzer = 1 << 1,
        Syntax = 1 << 2,
        Semantic = 1 << 3,
    }

    internal interface IRawDiagnosticsTaggerProviderCallback
    {
        bool IsEnabled { get; }
        bool SupportsDiagnosticMode(DiagnosticMode mode);
        bool IncludeDiagnostic(DiagnosticData data);
        ITagSpan<DiagnosticDataTag>? CreateTagSpan(Workspace workspace, SnapshotSpan span, DiagnosticData data);

        /// <summary>
        /// Get the <see cref="DiagnosticDataLocation"/> that should have the tag applied to it.
        /// In most cases, this is the <see cref="DiagnosticData.DataLocation"/> but overrides can change it (e.g. unnecessary classifications).
        /// </summary>
        /// <param name="diagnosticData">the diagnostic containing the location(s).</param>
        /// <returns>an array of locations that should have the tag applied.</returns>
        ImmutableArray<DiagnosticDataLocation> GetLocationsToTag(DiagnosticData diagnosticData);
    }

    internal sealed class RawDiagnosticsTaggerProvider : AsynchronousTaggerProvider<DiagnosticDataTag>
    {
        private readonly RawDiagnosticType _diagnosticType;
        private readonly IDiagnosticService _diagnosticService;
        private readonly IDiagnosticAnalyzerService _analyzerService;

        private readonly IRawDiagnosticsTaggerProviderCallback _callback;

        public RawDiagnosticsTaggerProvider(
            RawDiagnosticType diagnosticType,
            IThreadingContext threadingContext,
            IDiagnosticService diagnosticService,
            IDiagnosticAnalyzerService analyzerService,
            IGlobalOptionService globalOptions,
            ITextBufferVisibilityTracker? visibilityTracker,
            IAsynchronousOperationListener listener)
            : base(threadingContext, globalOptions, visibilityTracker, listener)
        {
            _diagnosticType = diagnosticType;
            _diagnosticService = diagnosticService;
            _analyzerService = analyzerService;
        }

        protected sealed override TaggerDelay EventChangeDelay => TaggerDelay.Short;
        protected sealed override TaggerDelay AddedTagNotificationDelay => TaggerDelay.OnIdle;

        /// <summary>
        /// When we hear about a new event cancel the costly work we're doing and compute against the latest snapshot.
        /// </summary>
        protected override bool CancelOnNewWork => true;

        protected sealed override ITaggerEventSource CreateEventSource(ITextView? textView, ITextBuffer subjectBuffer)
        {
            // OnTextChanged is added for diagnostics in source generated files: it's possible that the analyzer driver
            // executed on content which was produced by a source generator but is not yet reflected in an open text
            // buffer for that generated file. In this case, we need to update the tags after the buffer updates (which
            // triggers a text changed event) to ensure diagnostics are positioned correctly.
            return TaggerEventSources.Compose(
                TaggerEventSources.OnDocumentActiveContextChanged(subjectBuffer),
                TaggerEventSources.OnWorkspaceRegistrationChanged(subjectBuffer),
                TaggerEventSources.OnDiagnosticsChanged(subjectBuffer, _diagnosticService),
                TaggerEventSources.OnTextChanged(subjectBuffer));
        }

        protected override Task ProduceTagsAsync(
            TaggerContext<DiagnosticDataTag> context, DocumentSnapshotSpan spanToTag, int? caretPosition, CancellationToken cancellationToken)
        {
            return ProduceTagsAsync(context, spanToTag, cancellationToken);
        }

        private async Task ProduceTagsAsync(
            TaggerContext<DiagnosticDataTag> context, DocumentSnapshotSpan spanToTag, CancellationToken cancellationToken)
        {
            if (!_callback.IsEnabled)
                return;

            var diagnosticMode = GlobalOptions.GetDiagnosticMode(InternalDiagnosticsOptions.NormalDiagnosticMode);
            if (!_callback.SupportsDiagnosticMode(diagnosticMode))
                return;

            var document = spanToTag.Document;
            if (document == null)
                return;

            var snapshot = spanToTag.SnapshotSpan.Snapshot;

            var workspace = document.Project.Solution.Workspace;

            // See if we've marked any spans as those we want to suppress diagnostics for.
            // This can happen for buffers used in the preview workspace where some feature
            // is generating code that it doesn't want errors shown for.
            var buffer = snapshot.TextBuffer;
            var suppressedDiagnosticsSpans = (NormalizedSnapshotSpanCollection?)null;
            buffer?.Properties.TryGetProperty(PredefinedPreviewTaggerKeys.SuppressDiagnosticsSpansKey, out suppressedDiagnosticsSpans);

            var sourceText = snapshot.AsText();

            try
            {
                var diagnostics = await _analyzerService.GetDiagnosticsForSpanAsync(
                    document, range: null, cancellationToken: cancellationToken).ConfigureAwait(false);

                var requestedSpan = spanToTag.SnapshotSpan;

                foreach (var diagnosticData in diagnostics)
                {
                    if (_callback.IncludeDiagnostic(diagnosticData))
                    {
                        // We're going to be retrieving the diagnostics against the last time the engine
                        // computed them against this document *id*.  That might have been a different
                        // version of the document vs what we're looking at now.  But that's ok:
                        // 
                        // 1) GetExistingOrCalculatedTextSpan will ensure that the diagnostics spans are
                        //    contained within 'editorSnapshot'.
                        // 2) We'll eventually hear about an update to the diagnostics for this document
                        //    for whatever edits happened between the last time and this current snapshot.
                        //    So we'll eventually reach a point where the diagnostics exactly match the
                        //    editorSnapshot.

                        var diagnosticSpans = _callback.GetLocationsToTag(diagnosticData)
                            .Select(loc => loc.UnmappedFileSpan.GetClampedTextSpan(sourceText).ToSnapshotSpan(snapshot));
                        foreach (var diagnosticSpan in diagnosticSpans)
                        {
                            if (diagnosticSpan.IntersectsWith(requestedSpan) && !IsSuppressed(suppressedDiagnosticsSpans, diagnosticSpan))
                            {
                                var tagSpan = _callback.CreateTagSpan(workspace, diagnosticSpan, diagnosticData);
                                if (tagSpan != null)
                                    context.AddTag(tagSpan);
                            }
                        }
                    }
                }
            }
            catch (ArgumentOutOfRangeException ex) when (FatalError.ReportAndCatch(ex))
            {
                // https://devdiv.visualstudio.com/DefaultCollection/DevDiv/_workitems?id=428328&_a=edit&triage=false
                // explicitly report NFW to find out what is causing us for out of range. stop crashing on such
                // occasions
                return;
            }
        }

        private static bool IsSuppressed(NormalizedSnapshotSpanCollection? suppressedSpans, SnapshotSpan span)
            => suppressedSpans != null && suppressedSpans.IntersectsWith(span);
    }
}
