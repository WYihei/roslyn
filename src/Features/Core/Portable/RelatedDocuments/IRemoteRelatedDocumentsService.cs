﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Remote;

namespace Microsoft.CodeAnalysis.RelatedDocuments;

internal interface IRemoteRelatedDocumentsService
{
    public interface ICallback
    {
        ValueTask ReportRelatedDocumentAsync(RemoteServiceCallbackId callbackId, DocumentId documentId, CancellationToken cancellationToken);
    }

    public ValueTask GetRelatedDocumentIdsAsync(
        Checksum solutionChecksum, DocumentId documentId, int position, RemoteServiceCallbackId callbackId, CancellationToken cancellationToken);
}

[ExportRemoteServiceCallbackDispatcher(typeof(IRemoteRelatedDocumentsService)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class RelatedDocumentsServiceServerCallbackDispatcher() : RemoteServiceCallbackDispatcher, IRemoteRelatedDocumentsService.ICallback
{
    private new RemoteRelatedDocumentsServiceCallback GetCallback(RemoteServiceCallbackId callbackId)
        => (RemoteRelatedDocumentsServiceCallback)base.GetCallback(callbackId);

    public ValueTask ReportRelatedDocumentAsync(RemoteServiceCallbackId callbackId, DocumentId documentId, CancellationToken cancellationToken)
        => GetCallback(callbackId).ReportRelatedDocumentAsync(documentId);
}

internal sealed class RemoteRelatedDocumentsServiceCallback(
    Func<DocumentId, CancellationToken, ValueTask> onRelatedDocumentFoundAsync,
    CancellationToken cancellationToken)
{
    public ValueTask ReportRelatedDocumentAsync(DocumentId documentId)
        => onRelatedDocumentFoundAsync(documentId, cancellationToken);
}
