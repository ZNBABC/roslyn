﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.CodeAnalysis.UseAutoProperty
{
    internal abstract class AbstractUseAutoPropertyAnalyzer<TPropertyDeclaration, TFieldDeclaration, TVariableDeclarator, TExpression> : DiagnosticAnalyzer, IBuiltInAnalyzer
        where TPropertyDeclaration : SyntaxNode
        where TFieldDeclaration : SyntaxNode
        where TVariableDeclarator : SyntaxNode
        where TExpression : SyntaxNode
    {
        public const string UseAutoProperty = nameof(UseAutoProperty);
        public const string UseAutoPropertyFadedToken = nameof(UseAutoPropertyFadedToken);

        private readonly static DiagnosticDescriptor Descriptor = new DiagnosticDescriptor(
            UseAutoProperty, FeaturesResources.UseAutoProperty, FeaturesResources.UseAutoProperty,
            "Language", DiagnosticSeverity.Hidden, isEnabledByDefault: true);

        private readonly static DiagnosticDescriptor FadedTokenDescriptor = new DiagnosticDescriptor(
            UseAutoPropertyFadedToken, FeaturesResources.UseAutoProperty, FeaturesResources.UseAutoProperty,
            "Language", DiagnosticSeverity.Hidden, isEnabledByDefault: true,
            customTags: new[] { WellKnownDiagnosticTags.Unnecessary });

        public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor, FadedTokenDescriptor);

        protected abstract bool SupportsReadOnlyProperties(Compilation compilation);
        protected abstract bool SupportsPropertyInitializer(Compilation compilation);
        protected abstract TExpression GetFieldInitializer(TVariableDeclarator variable, CancellationToken cancellationToken);
        protected abstract string GetGetterFieldName(IMethodSymbol getMethod, CancellationToken cancellationToken);
        protected abstract string GetSetterFieldName(IMethodSymbol setMethod, CancellationToken cancellationToken);
        protected abstract SyntaxNode GetNodeToFade(TFieldDeclaration fieldDeclaration, TVariableDeclarator variableDeclarator);

        public sealed override void Initialize(AnalysisContext context)
        {
            context.RegisterSymbolAction(AnalyzerPropertiesInType, SymbolKind.NamedType);
        }

        private void AnalyzerPropertiesInType(SymbolAnalysisContext symbolContext)
        {
            var namedType = (INamedTypeSymbol)symbolContext.Symbol;
            foreach (var property in namedType.GetMembers().OfType<IPropertySymbol>())
            {
                AnalyzeProperty(property, symbolContext);
            }
        }

        private void AnalyzeProperty(IPropertySymbol property, SymbolAnalysisContext symbolContext)
        {
            if (property.IsIndexer)
            {
                return;
            }

            // The property can't be virtual.  We don't know if it is overridden somewhere.  If it 
            // is, then calls to it may not actually assign to the field.
            if (property.IsVirtual || property.IsOverride || property.IsSealed)
            {
                return;
            }

            if (property.IsWithEvents)
            {
                return;
            }

            if (property.Parameters.Length > 0)
            {
                return;
            }

            // Need at least a getter.
            if (property.GetMethod == null)
            {
                return;
            }

            var containingType = property.ContainingType;
            if (containingType == null)
            {
                return;
            }

            var declarations = property.DeclaringSyntaxReferences;
            if (declarations.Length != 1)
            {
                return;
            }

            var cancellationToken = symbolContext.CancellationToken;
            var propertyDeclaration = property.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken).FirstAncestorOrSelf<TPropertyDeclaration>();
            if (propertyDeclaration == null)
            {
                return;
            }

            var getterField = GetGetterField(containingType, property.GetMethod, cancellationToken);
            if (getterField == null)
            {
                return;
            }

            // If the user made the field readonly, we only want to convert it to a property if we
            // can keep it readonly.
            if (getterField.IsReadOnly && !SupportsReadOnlyProperties(symbolContext.Compilation))
            {
                return;
            }

            // Don't want to remove constants.
            if (getterField.IsConst)
            {
                return;
            }

            // Field and property should match in static-ness
            if (getterField.IsStatic != property.IsStatic)
            {
                return;
            }

            if (!containingType.Equals(getterField.ContainingType))
            {
                // Field and property have to be in the same type.
                return;
            }

            // Property and field have to agree on type.
            if (!property.Type.Equals(getterField.Type))
            {
                return;
            }

            if (getterField.DeclaringSyntaxReferences.Length != 1)
            {
                return;
            }

            // A setter is optional though.
            var setMethod = property.SetMethod;
            if (setMethod != null)
            {
                var setterField =  GetSetterField(containingType, setMethod, cancellationToken);
                if (setterField != getterField)
                {
                    // If there is a getter and a setter, they both need to agree on which field they are 
                    // writing to.
                    return;
                }
            }

            var fieldReference = getterField.DeclaringSyntaxReferences[0];
            var variableDeclarator = fieldReference.GetSyntax(symbolContext.CancellationToken) as TVariableDeclarator;
            if (variableDeclarator == null)
            {
                return;
            }

            var initializer = GetFieldInitializer(variableDeclarator, cancellationToken);
            if (initializer != null && !SupportsPropertyInitializer(symbolContext.Compilation))
            {
                return;
            }

            var fieldDeclaration = variableDeclarator?.Parent?.Parent as TFieldDeclaration;
            if (fieldDeclaration == null)
            {
                return;
            }

            // Can't remove the field if it has attributes on it.
            if (getterField.GetAttributes().Length > 0)
            {
                return;
            }

            // Looks like a viable property/field to convert into an auto property.
            var result = new AnalysisResult(property, getterField, propertyDeclaration, fieldDeclaration, variableDeclarator,
                property.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            ProcessResult(result, symbolContext);
        }

        private IFieldSymbol GetSetterField(INamedTypeSymbol containingType, IMethodSymbol setMethod, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CheckFieldAccessExpression(containingType, GetSetterFieldName(setMethod, cancellationToken), cancellationToken);
        }

        private IFieldSymbol GetGetterField(INamedTypeSymbol containingType, IMethodSymbol getMethod, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CheckFieldAccessExpression(containingType, GetGetterFieldName(getMethod, cancellationToken), cancellationToken);
        }

        private IFieldSymbol CheckFieldAccessExpression(INamedTypeSymbol containingType, string fieldName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return null;
            }

            var symbols = containingType.GetMembers(fieldName);
            if (symbols.Length != 1)
            {
                return null;
            }

            var symbol = symbols[0];
            if (symbol?.Kind != SymbolKind.Field)
            {
                return null;
            }

            if (symbol.DeclaringSyntaxReferences.Length > 1)
            {
                return null;
            }

            return (IFieldSymbol)symbol;
        }

        private void ProcessResult(AnalysisResult result, SymbolAnalysisContext compilationContext)
        {
            // Check if there are additional reasons we think this field might be ineligible for 
            // replacing with an auto prop.
            if (!IsEligibleHeuristic(result.Field, result.PropertyDeclaration, compilationContext.Compilation, compilationContext.CancellationToken))
            {
                return;
            }

            var propertyDeclaration = result.PropertyDeclaration;
            var variableDeclarator = result.VariableDeclarator;
            var nodeToFade = GetNodeToFade(result.FieldDeclaration, variableDeclarator);

            var properties = ImmutableDictionary<string, string>.Empty.Add(nameof(result.SymbolEquivalenceKey), result.SymbolEquivalenceKey);

            // Fade out the field/variable we are going to remove.
            var diagnostic1 = Diagnostic.Create(FadedTokenDescriptor, nodeToFade.GetLocation());
            compilationContext.ReportDiagnostic(diagnostic1);

            // Now add diagnostics to both the field and the property saying we can convert it to 
            // an auto property.  For each diagnostic store both location so we can easily retrieve
            // them when performing the code fix.
            IEnumerable<Location> additionalLocations = new Location[] { propertyDeclaration.GetLocation(), variableDeclarator.GetLocation() };

            var diagnostic2 = Diagnostic.Create(Descriptor, propertyDeclaration.GetLocation(), additionalLocations, properties);
            compilationContext.ReportDiagnostic(diagnostic2);

            var diagnostic3 = Diagnostic.Create(Descriptor, nodeToFade.GetLocation(), additionalLocations, properties);
            compilationContext.ReportDiagnostic(diagnostic3);
        }

        protected virtual bool IsEligibleHeuristic(IFieldSymbol field, TPropertyDeclaration propertyDeclaration, Compilation compilation, CancellationToken cancellationToken)
        {
            return true;
        }

        public DiagnosticAnalyzerCategory GetAnalyzerCategory()
        {
            return DiagnosticAnalyzerCategory.SemanticSpanAnalysis;
        }

        internal class AnalysisResult
        {
            public readonly IPropertySymbol Property;
            public readonly IFieldSymbol Field;
            public readonly TPropertyDeclaration PropertyDeclaration;
            public readonly TFieldDeclaration FieldDeclaration;
            public readonly TVariableDeclarator VariableDeclarator;
            public readonly string SymbolEquivalenceKey;

            public AnalysisResult(
                IPropertySymbol property,
                IFieldSymbol field,
                TPropertyDeclaration propertyDeclaration,
                TFieldDeclaration fieldDeclaration,
                TVariableDeclarator variableDeclarator,
                string symbolEquivalenceKey)
            {
                Property = property;
                Field = field;
                PropertyDeclaration = propertyDeclaration;
                FieldDeclaration = fieldDeclaration;
                VariableDeclarator = variableDeclarator;
                SymbolEquivalenceKey = symbolEquivalenceKey;
            }
        }
    }
}
