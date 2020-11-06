﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Editor.Host;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.FileHeaders;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Designer.Interfaces;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation
{
    /// <summary>
    /// The base class of both the Roslyn editor factories.
    /// </summary>
    internal abstract class AbstractEditorFactory : IVsEditorFactory, IVsEditorFactory4, IVsEditorFactoryNotify
    {
        private const string FormPhysicalViewName = "Form";
        private const string CodePhysicalViewName = "Code";
        private readonly IComponentModel _componentModel;
        private Microsoft.VisualStudio.OLE.Interop.IServiceProvider? _oleServiceProvider;
        private bool _encoding;

        protected AbstractEditorFactory(IComponentModel componentModel)
            => _componentModel = componentModel;

        protected abstract string ContentTypeName { get; }
        protected abstract string LanguageName { get; }
        protected abstract SyntaxGenerator SyntaxGenerator { get; }
        protected abstract AbstractFileHeaderHelper FileHeaderHelper { get; }

        public void SetEncoding(bool value)
            => _encoding = value;

        int IVsEditorFactory.Close()
            => VSConstants.S_OK;

        public int CreateEditorInstance(
            uint grfCreateDoc,
            string pszMkDocument,
            string? pszPhysicalView,
            IVsHierarchy vsHierarchy,
            uint itemid,
            IntPtr punkDocDataExisting,
            out IntPtr ppunkDocView,
            out IntPtr ppunkDocData,
            out string pbstrEditorCaption,
            out Guid pguidCmdUI,
            out int pgrfCDW)
        {
            ppunkDocView = IntPtr.Zero;
            ppunkDocData = IntPtr.Zero;
            pbstrEditorCaption = string.Empty;
            pguidCmdUI = Guid.Empty;
            pgrfCDW = 0;

            var physicalView = pszPhysicalView ?? "Code";
            IVsTextBuffer? textBuffer = null;

            // Is this document already open? If so, let's see if it's a IVsTextBuffer we should re-use. This allows us
            // to properly handle multiple windows open for the same document.
            if (punkDocDataExisting != IntPtr.Zero)
            {
                var docDataExisting = Marshal.GetObjectForIUnknown(punkDocDataExisting);

                textBuffer = docDataExisting as IVsTextBuffer;

                if (textBuffer == null)
                {
                    // We are incompatible with the existing doc data
                    return VSConstants.VS_E_INCOMPATIBLEDOCDATA;
                }
            }

            // Do we need to create a text buffer?
            if (textBuffer == null)
            {
                try
                {
                    textBuffer = GetDocumentData(grfCreateDoc, pszMkDocument, vsHierarchy, itemid) as IVsTextBuffer;
                    if (textBuffer == null)
                    {
                        return VSConstants.E_FAIL;
                    }
                }
                catch (Exception e)
                {
                    return Marshal.GetHRForException(e);
                }
            }

            // If the text buffer is marked as read-only, ensure that the padlock icon is displayed
            // next the new window's title and that [Read Only] is appended to title.
            var readOnlyStatus = READONLYSTATUS.ROSTATUS_NotReadOnly;
            if (ErrorHandler.Succeeded(textBuffer.GetStateFlags(out var textBufferFlags)) &&
                0 != (textBufferFlags & ((uint)BUFFERSTATEFLAGS.BSF_FILESYS_READONLY | (uint)BUFFERSTATEFLAGS.BSF_USER_READONLY)))
            {
                readOnlyStatus = READONLYSTATUS.ROSTATUS_ReadOnly;
            }

            switch (physicalView)
            {
                case "Form":

                    // We must create the WinForms designer here
                    (pbstrEditorCaption, pguidCmdUI, ppunkDocView) = GetWinFormsDesignerData(vsHierarchy, itemid, textBuffer, readOnlyStatus, initializeView: true);
                    break;

                case "Code":

                    (pbstrEditorCaption, pguidCmdUI, ppunkDocView) = GetCodeEditorData(textBuffer, readOnlyStatus, initializeView: true);
                    break;

                default:

                    return VSConstants.E_INVALIDARG;
            }

            ppunkDocData = Marshal.GetIUnknownForObject(textBuffer);

            return VSConstants.S_OK;
        }

        public object GetDocumentData(uint grfCreate, string pszMkDocument, IVsHierarchy pHier, uint itemid)
        {
            var editorAdaptersFactoryService = _componentModel.GetService<IVsEditorAdaptersFactoryService>();

            var contentTypeRegistryService = _componentModel.GetService<IContentTypeRegistryService>();
            var contentType = contentTypeRegistryService.GetContentType(ContentTypeName);
            IVsTextBuffer? textBuffer = editorAdaptersFactoryService.CreateVsTextBufferAdapter(_oleServiceProvider, contentType);

            if (_encoding)
            {
                if (textBuffer is IVsUserData userData)
                {
                    // The editor shims require that the boxed value when setting the PromptOnLoad flag is a uint
                    var hresult = userData.SetData(
                        VSConstants.VsTextBufferUserDataGuid.VsBufferEncodingPromptOnLoad_guid,
                        (uint)__PROMPTONLOADFLAGS.codepagePrompt);

                    Marshal.ThrowExceptionForHR(hresult);
                }
            }

            return textBuffer;
        }

        public object GetDocumentView(uint grfCreate, string pszPhysicalView, IVsHierarchy pHier, IntPtr punkDocData, uint itemid)
        {
            throw new NotImplementedException();
        }

        public string GetEditorCaption(string pszMkDocument, string pszPhysicalView, IVsHierarchy pHier, IntPtr punkDocData, out Guid pguidCmdUI)
        {
            var physicalView = pszPhysicalView ?? CodePhysicalViewName;
            var readOnlyStatus = READONLYSTATUS.ROSTATUS_NotReadOnly;

            IVsTextBuffer? textBuffer = null;
            if (punkDocData != IntPtr.Zero)
            {
                var docData = Marshal.GetObjectForIUnknown(punkDocData);
                textBuffer = docData as IVsTextBuffer;

                if (textBuffer != null)
                {
                    if (ErrorHandler.Succeeded(textBuffer.GetStateFlags(out var textBufferFlags)) &&
                        0 != (textBufferFlags & ((uint)BUFFERSTATEFLAGS.BSF_FILESYS_READONLY | (uint)BUFFERSTATEFLAGS.BSF_USER_READONLY)))
                    {
                        readOnlyStatus = READONLYSTATUS.ROSTATUS_ReadOnly;
                    }
                }
            }

            string editorCaption;
            switch (physicalView)
            {
                case FormPhysicalViewName:

                    (editorCaption, pguidCmdUI, _) = GetWinFormsDesignerData(pHier, itemid: 0, textBuffer, readOnlyStatus, initializeView: false);
                    break;

                case CodePhysicalViewName:

                    (editorCaption, pguidCmdUI, _) = GetCodeEditorData(textBuffer, readOnlyStatus, initializeView: false);
                    break;

                default:
                    pguidCmdUI = Guid.Empty;
                    editorCaption = string.Empty;
                    break;
            }

            return editorCaption;
        }

        public bool ShouldDeferUntilIntellisenseIsReady(uint grfCreate, string pszMkDocument, string pszPhysicalView)
        {
            return FormPhysicalViewName.Equals(pszPhysicalView, StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetWinFormsLoaderName(IVsHierarchy vsHierarchy)
        {
            const string LoaderName = "Microsoft.VisualStudio.Design.Serialization.CodeDom.VSCodeDomDesignerLoader";
            const string NewLoaderName = "Microsoft.VisualStudio.Design.Core.Serialization.CodeDom.VSCodeDomDesignerLoader";

            // If this is a netcoreapp3.0 (or newer), we must create the newer WinForms designer.
            // TODO: This check will eventually move into the WinForms designer itself.
            if (!vsHierarchy.TryGetTargetFrameworkMoniker((uint)VSConstants.VSITEMID.Root, out var targetFrameworkMoniker) ||
                string.IsNullOrWhiteSpace(targetFrameworkMoniker))
            {
                return LoaderName;
            }

            try
            {
                var frameworkName = new FrameworkName(targetFrameworkMoniker);
                if (frameworkName.Identifier == ".NETCoreApp" && frameworkName.Version?.Major >= 3)
                {
                    return NewLoaderName;
                }
            }
            catch
            {
                // Fall back to the old loader name if there are any failures
                // while parsing the TFM.
            }

            return LoaderName;
        }

        private (string editorCaption, Guid commandUI, IntPtr documentView) GetWinFormsDesignerData(IVsHierarchy vsHierarchy, uint itemid, IVsTextBuffer? textBuffer, READONLYSTATUS readOnlyStatus, bool initializeView)
        {
            var loaderName = GetWinFormsLoaderName(vsHierarchy);
            var designerService = (IVSMDDesignerService)_oleServiceProvider.QueryService<SVSMDDesignerService>();
            var designerLoader = (IVSMDDesignerLoader)designerService.CreateDesignerLoader(loaderName);
            if (designerLoader is null)
            {
                return GetCodeEditorData(textBuffer, readOnlyStatus, initializeView);
            }

            try
            {
                if (initializeView)
                {
                    designerLoader.Initialize(_oleServiceProvider, vsHierarchy, (int)itemid, textBuffer as IVsTextLines);
                }
                var editorCaption = designerLoader.GetEditorCaption((int)readOnlyStatus);

                var designer = designerService.CreateDesigner(_oleServiceProvider, designerLoader);

                var documentView = initializeView ? Marshal.GetIUnknownForObject(designer.View) : IntPtr.Zero;
                var commandUI = designer.CommandGuid;

                return (editorCaption, commandUI, documentView);
            }
            finally
            {
                designerLoader.Dispose();
            }
        }

        private (string editorCaption, Guid commandUI, IntPtr documentView) GetCodeEditorData(IVsTextBuffer? textBuffer, READONLYSTATUS readOnlyStatus, bool initializeView)
        {
            var editorAdaptersFactoryService = _componentModel.GetService<IVsEditorAdaptersFactoryService>();
            var codeWindow = editorAdaptersFactoryService.CreateVsCodeWindowAdapter(_oleServiceProvider);

            if (initializeView)
            {
                codeWindow.SetBuffer(textBuffer as IVsTextLines);
            }

            codeWindow.GetEditorCaption(readOnlyStatus, out var editorCaption);

            var documentView = initializeView ? Marshal.GetIUnknownForObject(codeWindow) : IntPtr.Zero;
            var commandUI = VSConstants.GUID_TextEditorFactory;

            return (editorCaption, commandUI, documentView);
        }

        public int MapLogicalView(ref Guid rguidLogicalView, out string? pbstrPhysicalView)
        {
            pbstrPhysicalView = null;

            if (rguidLogicalView == VSConstants.LOGVIEWID.Primary_guid ||
                rguidLogicalView == VSConstants.LOGVIEWID.Debugging_guid ||
                rguidLogicalView == VSConstants.LOGVIEWID.Code_guid ||
                rguidLogicalView == VSConstants.LOGVIEWID.TextView_guid)
            {
                return VSConstants.S_OK;
            }
            else if (rguidLogicalView == VSConstants.LOGVIEWID.Designer_guid)
            {
                pbstrPhysicalView = "Form";
                return VSConstants.S_OK;
            }
            else
            {
                return VSConstants.E_NOTIMPL;
            }
        }

        int IVsEditorFactory.SetSite(Microsoft.VisualStudio.OLE.Interop.IServiceProvider psp)
        {
            _oleServiceProvider = psp;
            return VSConstants.S_OK;
        }

        int IVsEditorFactoryNotify.NotifyDependentItemSaved(IVsHierarchy pHier, uint itemidParent, string pszMkDocumentParent, uint itemidDpendent, string pszMkDocumentDependent)
            => VSConstants.S_OK;

        int IVsEditorFactoryNotify.NotifyItemAdded(uint grfEFN, IVsHierarchy pHier, uint itemid, string pszMkDocument)
        {
            // Is this being added from a template?
            if (((__EFNFLAGS)grfEFN & __EFNFLAGS.EFN_ClonedFromTemplate) != 0)
            {
                var waitIndicator = _componentModel.GetService<IWaitIndicator>();
                // TODO(cyrusn): Can this be cancellable?
                waitIndicator.Wait(
                    "Intellisense",
                    allowCancel: false,
                    action: c => FormatDocumentCreatedFromTemplate(pHier, itemid, pszMkDocument, c.CancellationToken));
            }

            return VSConstants.S_OK;
        }

        int IVsEditorFactoryNotify.NotifyItemRenamed(IVsHierarchy pHier, uint itemid, string pszMkDocumentOld, string pszMkDocumentNew)
            => VSConstants.S_OK;

        protected virtual Task<Document> OrganizeUsingsCreatedFromTemplateAsync(Document document, CancellationToken cancellationToken)
            => Formatter.OrganizeImportsAsync(document, cancellationToken);

        private void FormatDocumentCreatedFromTemplate(IVsHierarchy hierarchy, uint itemid, string filePath, CancellationToken cancellationToken)
        {
            // A file has been created on disk which the user added from the "Add Item" dialog. We need
            // to include this in a workspace to figure out the right options it should be formatted with.
            // This requires us to place it in the correct project.
            var workspace = _componentModel.GetService<VisualStudioWorkspace>();
            var solution = workspace.CurrentSolution;

            ProjectId? projectIdToAddTo = null;

            foreach (var projectId in solution.ProjectIds)
            {
                if (workspace.GetHierarchy(projectId) == hierarchy)
                {
                    projectIdToAddTo = projectId;
                    break;
                }
            }

            if (projectIdToAddTo == null)
            {
                // We don't have a project for this, so we'll just make up a fake project altogether
                var temporaryProject = solution.AddProject(
                    name: nameof(FormatDocumentCreatedFromTemplate),
                    assemblyName: nameof(FormatDocumentCreatedFromTemplate),
                    language: LanguageName);

                solution = temporaryProject.Solution;
                projectIdToAddTo = temporaryProject.Id;
            }

            var documentId = DocumentId.CreateNewId(projectIdToAddTo);
            var forkedSolution = solution.AddDocument(DocumentInfo.Create(documentId, filePath, loader: new FileTextLoader(filePath, defaultEncoding: null), filePath: filePath));
            var addedDocument = forkedSolution.GetDocument(documentId)!;

            var rootToFormat = addedDocument.GetSyntaxRootSynchronously(cancellationToken);
            Contract.ThrowIfNull(rootToFormat);
            var documentOptions = ThreadHelper.JoinableTaskFactory.Run(() => addedDocument.GetOptionsAsync(cancellationToken));

            // Apply file header preferences
            var fileHeaderTemplate = documentOptions.GetOption(CodeStyleOptions2.FileHeaderTemplate);
            if (!string.IsNullOrEmpty(fileHeaderTemplate))
            {
                var documentWithFileHeader = ThreadHelper.JoinableTaskFactory.Run(() =>
                {
                    var newLineText = documentOptions.GetOption(FormattingOptions.NewLine, rootToFormat.Language);
                    var newLineTrivia = SyntaxGenerator.EndOfLine(newLineText);
                    return AbstractFileHeaderCodeFixProvider.GetTransformedSyntaxRootAsync(
                        SyntaxGenerator.SyntaxFacts,
                        FileHeaderHelper,
                        newLineTrivia,
                        addedDocument,
                        cancellationToken);
                });

                addedDocument = addedDocument.WithSyntaxRoot(documentWithFileHeader);
                rootToFormat = documentWithFileHeader;
            }

            // Organize using directives
            addedDocument = ThreadHelper.JoinableTaskFactory.Run(() => OrganizeUsingsCreatedFromTemplateAsync(addedDocument, cancellationToken));
            rootToFormat = ThreadHelper.JoinableTaskFactory.Run(() => addedDocument.GetRequiredSyntaxRootAsync(cancellationToken).AsTask());

            // Format document
            var unformattedText = addedDocument.GetTextSynchronously(cancellationToken);
            var formattedRoot = Formatter.Format(rootToFormat, workspace, documentOptions, cancellationToken);
            var formattedText = formattedRoot.GetText(unformattedText.Encoding, unformattedText.ChecksumAlgorithm);

            // Ensure the line endings are normalized. The formatter doesn't touch everything if it doesn't need to.
            var targetLineEnding = documentOptions.GetOption(FormattingOptions.NewLine)!;

            var originalText = formattedText;
            foreach (var originalLine in originalText.Lines)
            {
                var originalNewLine = originalText.ToString(CodeAnalysis.Text.TextSpan.FromBounds(originalLine.End, originalLine.EndIncludingLineBreak));

                // Check if we have a line ending, so we don't go adding one to the end if we don't need to.
                if (originalNewLine.Length > 0 && originalNewLine != targetLineEnding)
                {
                    var currentLine = formattedText.Lines[originalLine.LineNumber];
                    var currentSpan = CodeAnalysis.Text.TextSpan.FromBounds(currentLine.End, currentLine.EndIncludingLineBreak);
                    formattedText = formattedText.WithChanges(new TextChange(currentSpan, targetLineEnding));
                }
            }

            IOUtilities.PerformIO(() =>
            {
                using var textWriter = new StreamWriter(filePath, append: false, encoding: formattedText.Encoding);
                // We pass null here for cancellation, since cancelling in the middle of the file write would leave the file corrupted
                formattedText.Write(textWriter, cancellationToken: CancellationToken.None);
            });
        }
    }
}
