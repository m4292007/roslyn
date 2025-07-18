﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeGeneration;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.GenerateDefaultConstructors;
using Microsoft.CodeAnalysis.GenerateFromMembers;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PickMembers;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.GenerateConstructors;

using static GenerateFromMembersHelpers;

/// <summary>
/// This <see cref="CodeRefactoringProvider"/> is responsible for allowing a user to pick a set of members from a class
/// or struct, and then generate a constructor for that takes in matching parameters and assigns them to those members.
/// The members can be picked using a actual selection in the editor, or they can be picked using a picker control that
/// will then display all the viable members and allow the user to pick which ones they want to use.
/// <para/>
/// This <see cref="CodeRefactoringProvider"/> also gives users a way to generate constructors for a derived type that
/// delegate to a base type.  For all accessible constructors in the base type, the user will be offered to create a
/// constructor in the derived type with the same signature if they don't already have one.  This way, a user can
/// override a type and easily create all the forwarding constructors.
/// <para/>
/// Importantly, this type is not responsible for generating constructors when the user types something like "new
/// MyType(x, y, z)".
/// </summary>
/// <remarks>
/// For testing purposes only.
/// </remarks>
internal abstract partial class AbstractGenerateConstructorsCodeRefactoringProvider(IPickMembersService? pickMembersService_forTesting)
    : CodeRefactoringProvider
{
    public sealed record GenerateConstructorIntentData(Accessibility? Accessibility);

    private const string AddNullChecksId = nameof(AddNullChecksId);

    private readonly IPickMembersService? _pickMembersService_forTesting = pickMembersService_forTesting;

    protected AbstractGenerateConstructorsCodeRefactoringProvider() : this(null)
    {
    }

    protected abstract bool ContainingTypesOrSelfHasUnsafeKeyword(INamedTypeSymbol containingType);
    protected abstract string ToDisplayString(IParameterSymbol parameter, SymbolDisplayFormat format);
    protected abstract ValueTask<bool> PrefersThrowExpressionAsync(Document document, CancellationToken cancellationToken);
    protected abstract IFieldSymbol? TryMapToWritableInstanceField(IPropertySymbol property, CancellationToken cancellationToken);

    public override Task ComputeRefactoringsAsync(CodeRefactoringContext context)
    {
        return ComputeRefactoringsAsync(
            context.Document,
            context.Span,
            context.RegisterRefactoring,
            actions => context.RegisterRefactorings(actions),
            desiredAccessibility: null,
            context.CancellationToken);
    }

    private async Task ComputeRefactoringsAsync(
        Document document,
        TextSpan textSpan,
        Action<CodeAction, TextSpan> registerSingleAction,
        Action<ImmutableArray<CodeAction>> registerMultipleActions,
        Accessibility? desiredAccessibility,
        CancellationToken cancellationToken)
    {
        // TODO: https://github.com/dotnet/roslyn/issues/5778. Not supported in REPL for now.
        if (document.Project.IsSubmission)
            return;

        if (document.Project.Solution.WorkspaceKind == WorkspaceKind.MiscellaneousFiles)
            return;

        // First, see if we can offer a specific constructor based on what the user has selected, or has their caret on.
        var actions = await GenerateConstructorFromMembersAsync(
            document, textSpan, addNullChecks: false, desiredAccessibility, cancellationToken).ConfigureAwait(false);
        if (!actions.IsDefaultOrEmpty)
        {
            registerMultipleActions(actions);
        }
        else if (textSpan.IsEmpty)
        {
            // If that produced nothing, and the user hasn't selected anything explicitly, then also offer the option to
            // generate a constructor with a dialog.
            var nonSelectionAction = await HandleNonSelectionAsync(document, textSpan, desiredAccessibility, cancellationToken).ConfigureAwait(false);
            if (nonSelectionAction is var (codeAction, applicableToSpan))
                registerSingleAction(codeAction, applicableToSpan);
        }

        // Finally, offer to generate default constructors that delegate to the base type if any are missing.
        var defaultConstructorService = document.GetRequiredLanguageService<IGenerateDefaultConstructorsService>();
        var defaultConstructorActions = await defaultConstructorService.GenerateDefaultConstructorsAsync(
            document, textSpan, forRefactoring: true, cancellationToken).ConfigureAwait(false);

        registerMultipleActions(defaultConstructorActions);
    }

    private async Task<(CodeAction CodeAction, TextSpan ApplicableToSpan)?> HandleNonSelectionAsync(
        Document document,
        TextSpan textSpan,
        Accessibility? desiredAccessibility,
        CancellationToken cancellationToken)
    {
        var helpers = document.GetRequiredLanguageService<IRefactoringHelpersService>();
        var sourceText = await document.GetValueTextAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        // We offer the refactoring when the user is either on the header of a class/struct,
        // or if they're between any members of a class/struct and are on a blank line.
        if (!helpers.IsOnTypeHeader(root, textSpan.Start, out var typeDeclaration) &&
            !helpers.IsBetweenTypeMembers(sourceText, root, textSpan.Start, out typeDeclaration))
        {
            return null;
        }

        var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        // Only supported on classes/structs.
        var containingType = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken: cancellationToken) as INamedTypeSymbol;
        if (containingType?.TypeKind is not TypeKind.Class and not TypeKind.Struct)
        {
            return null;
        }

        // No constructors for static classes.
        if (containingType.IsStatic)
        {
            return null;
        }

        // Find all the possible writable instance fields/properties.  If there are any, then
        // show a dialog to the user to select the ones they want.  Otherwise, if there are none
        // don't offer to generate anything.
        var viableMembers = containingType.GetMembers().WhereAsArray(IsWritableInstanceFieldOrProperty);
        if (viableMembers.Length == 0)
        {
            return null;
        }

        // We shouldn't offer a refactoring if the compilation doesn't contain the ArgumentNullException type,
        // as we use it later on in our computations.
        var argumentNullExceptionType = typeof(ArgumentNullException).FullName;
        if (argumentNullExceptionType is null || semanticModel.Compilation.GetTypeByMetadataName(argumentNullExceptionType) is null)
        {
            return null;
        }

        using var _ = ArrayBuilder<PickMembersOption>.GetInstance(out var pickMemberOptions);
        var canAddNullCheck = viableMembers.Any(
            static m => m.GetSymbolType().CanAddNullCheck());

        if (canAddNullCheck)
        {
            // ILegacyGlobalOptionsWorkspaceService is not provided in LSP, so don't give the code action with Dialog if it is null
            var globalOptions = document.Project.Solution.Services.GetService<ILegacyGlobalOptionsWorkspaceService>();
            if (globalOptions == null)
            {
                return null;
            }

            var optionValue = globalOptions.GetGenerateConstructorFromMembersOptionsAddNullChecks(document.Project.Language);
            pickMemberOptions.Add(new PickMembersOption(
                AddNullChecksId,
                FeaturesResources.Add_null_checks,
                optionValue));
        }

        return (new GenerateConstructorWithDialogCodeAction(
                this, document, textSpan, containingType, desiredAccessibility, viableMembers,
                pickMemberOptions.ToImmutable()), typeDeclaration.Span);
    }

    public async Task<ImmutableArray<CodeAction>> GenerateConstructorFromMembersAsync(
        Document document, TextSpan textSpan, bool addNullChecks, Accessibility? desiredAccessibility, CancellationToken cancellationToken)
    {
        using (Logger.LogBlock(FunctionId.Refactoring_GenerateFromMembers_GenerateConstructorFromMembers, cancellationToken))
        {
            var info = await GetSelectedMemberInfoAsync(document, textSpan, allowPartialSelection: true, cancellationToken).ConfigureAwait(false);
            if (info != null)
            {
                var state = await State.TryGenerateAsync(this, document, textSpan, info.ContainingType, desiredAccessibility, info.SelectedMembers, cancellationToken).ConfigureAwait(false);
                if (state != null && state.MatchingConstructor == null)
                    return GetCodeActions(document, state, addNullChecks);
            }

            return default;
        }
    }

    private ImmutableArray<CodeAction> GetCodeActions(Document document, State state, bool addNullChecks)
    {
        using var result = TemporaryArray<CodeAction>.Empty;

        result.Add(new FieldDelegatingCodeAction(this, document, state, addNullChecks));
        if (state.DelegatedConstructor != null)
            result.Add(new ConstructorDelegatingCodeAction(this, document, state, addNullChecks));

        return result.ToImmutableAndClear();
    }

    private static async Task<Document> AddNavigationAnnotationAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        var nodes = root.GetAnnotatedNodes(CodeGenerator.Annotation);
        var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();

        foreach (var node in nodes)
        {
            var parameterList = syntaxFacts.GetParameterList(node);
            if (parameterList != null)
            {
                var closeParen = parameterList.GetLastToken();
                var newRoot = root.ReplaceToken(closeParen, closeParen.WithAdditionalAnnotations(NavigationAnnotation.Create()));
                return document.WithSyntaxRoot(newRoot);
            }
        }

        return document;
    }
}
