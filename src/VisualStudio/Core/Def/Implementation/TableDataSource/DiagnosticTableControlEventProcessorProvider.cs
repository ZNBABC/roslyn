﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.VisualStudio.LanguageServices.Implementation.Suppression;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.TableDataSource
{
    [Export(typeof(ITableControlEventProcessorProvider))]
    [DataSourceType(StandardTableDataSources.ErrorTableDataSource)]
    [DataSource(VisualStudioDiagnosticListTable.IdentifierString)]
    [Name(Name)]
    [Order(Before = "default")]
    internal partial class DiagnosticTableControlEventProcessorProvider : AbstractTableControlEventProcessorProvider<DiagnosticData>
    {
        internal const string Name = "C#/VB Diagnostic Table Event Processor";
        private readonly DiagnosticTableControlSuppressionStateService _suppressionStateService;
        
        [ImportingConstructor]
        public DiagnosticTableControlEventProcessorProvider(
            DiagnosticTableControlSuppressionStateService suppressionStateService)
        {
            _suppressionStateService = suppressionStateService;
        }

        protected override EventProcessor CreateEventProcessor()
        {
            var suppressionStateEventProcessor = new SuppressionStateEventProcessor(_suppressionStateService);
            return new AggregateDiagnosticTableControlEventProcessor(additionalEventProcessors: suppressionStateEventProcessor);
        }
    }
}
