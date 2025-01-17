﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ICSharpCode.CodeConverter.Shared;
using ICSharpCode.CodeConverter.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using StringComparer = System.StringComparer;
using SyntaxFactory = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using VBSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VBasic = Microsoft.CodeAnalysis.VisualBasic;
using CSSyntaxKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;
using SyntaxToken = Microsoft.CodeAnalysis.SyntaxToken;
using Microsoft.CodeAnalysis.VisualBasic;
using ICSharpCode.CodeConverter.Util.FromRoslyn;

namespace ICSharpCode.CodeConverter.CSharp
{
    /// <summary>
    /// Declaration nodes, and nodes only used directly in that declaration (i.e. never within an expression)
    /// e.g. Class, Enum, TypeConstraint
    /// </summary>
    /// <remarks>The split between this and the <see cref="ExpressionNodeVisitor"/> is purely organizational and serves no real runtime purpose.</remarks>
    internal class DeclarationNodeVisitor : VBasic.VisualBasicSyntaxVisitor<Task<CSharpSyntaxNode>>
    {
        private static readonly Type DllImportType = typeof(DllImportAttribute);
        private static readonly Type CharSetType = typeof(CharSet);
        private static readonly SyntaxToken SemicolonToken = SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SemicolonToken);
        private readonly SyntaxGenerator _csSyntaxGenerator;
        private readonly Compilation _vbCompilation;
        private readonly SemanticModel _semanticModel;
        private readonly Dictionary<VBSyntax.StatementSyntax, MemberDeclarationSyntax[]> _additionalDeclarations = new Dictionary<VBSyntax.StatementSyntax, MemberDeclarationSyntax[]>();
        private readonly TypeContext _typeContext = new TypeContext();
        private uint _failedMemberConversionMarkerCount;
        private readonly HashSet<string> _extraUsingDirectives = new HashSet<string>();
        private readonly VisualBasicEqualityComparison _visualBasicEqualityComparison;
        private HashSet<string> _accessedThroughMyClass;
        public CommentConvertingVisitorWrapper TriviaConvertingDeclarationVisitor { get; }
        private readonly CommentConvertingVisitorWrapper _triviaConvertingExpressionVisitor;
        private string _topAncestorNamespace;

        private CommonConversions CommonConversions { get; }
        private Func<VisualBasicSyntaxNode, bool, IdentifierNameSyntax, Task<VisualBasicSyntaxVisitor<Task<SyntaxList<StatementSyntax>>>>> _createMethodBodyVisitorAsync { get; }

        internal HoistedNodeState AdditionalLocals => _typeContext.HoistedState;

        public DeclarationNodeVisitor(Document document, Compilation compilation, SemanticModel semanticModel,
            CSharpCompilation csCompilation, SyntaxGenerator csSyntaxGenerator)
        {
            _vbCompilation = compilation;
            _semanticModel = semanticModel;
            _csSyntaxGenerator = csSyntaxGenerator;
            _visualBasicEqualityComparison = new VisualBasicEqualityComparison(_semanticModel, _extraUsingDirectives);
            TriviaConvertingDeclarationVisitor = new CommentConvertingVisitorWrapper(this, _semanticModel.SyntaxTree);
            var expressionEvaluator = new ExpressionEvaluator(semanticModel, _visualBasicEqualityComparison);
            var typeConversionAnalyzer = new TypeConversionAnalyzer(semanticModel, csCompilation, _extraUsingDirectives, _csSyntaxGenerator, expressionEvaluator);
            CommonConversions = new CommonConversions(document, semanticModel, typeConversionAnalyzer, csSyntaxGenerator, csCompilation, _typeContext, _visualBasicEqualityComparison);
            var expressionNodeVisitor = new ExpressionNodeVisitor(semanticModel, _visualBasicEqualityComparison, _typeContext, CommonConversions, _extraUsingDirectives);
            _triviaConvertingExpressionVisitor = expressionNodeVisitor.TriviaConvertingExpressionVisitor;
            _createMethodBodyVisitorAsync = expressionNodeVisitor.CreateMethodBodyVisitorAsync;
            CommonConversions.TriviaConvertingExpressionVisitor = _triviaConvertingExpressionVisitor;
        }

        public async Task<VBasic.VisualBasicSyntaxVisitor<Task<SyntaxList<StatementSyntax>>>> CreateMethodBodyVisitorAsync(VBasic.VisualBasicSyntaxNode node, bool isIterator = false, IdentifierNameSyntax csReturnVariable = null)
        {
            return await _createMethodBodyVisitorAsync(node, isIterator, csReturnVariable);
        }

        public override async Task<CSharpSyntaxNode> DefaultVisit(SyntaxNode node)
        {
            throw new NotImplementedException($"Conversion for {VBasic.VisualBasicExtensions.Kind(node)} not implemented, please report this issue")
                .WithNodeInformation(node);
        }

        public override async Task<CSharpSyntaxNode> VisitCompilationUnit(VBSyntax.CompilationUnitSyntax node)
        {
            var options = (VBasic.VisualBasicCompilationOptions)_semanticModel.Compilation.Options;
            var importsClauses = options.GlobalImports.Select(gi => gi.Clause).Concat(node.Imports.SelectMany(imp => imp.ImportsClauses)).ToList();

            _topAncestorNamespace = node.Members.Any(m => !IsNamespaceDeclaration(m)) ? options.RootNamespace : null;
            var fileOptionCompareValue = node.Options.Where(x => x.NameKeyword.IsKind(VBasic.SyntaxKind.CompareKeyword)).LastOrDefault()?.ValueKeyword;
            _visualBasicEqualityComparison.OptionCompareTextCaseInsensitive = fileOptionCompareValue?.IsKind(VBasic.SyntaxKind.TextKeyword) ?? options.OptionCompareText;

            var attributes = SyntaxFactory.List(await node.Attributes.SelectMany(a => a.AttributeLists).SelectManyAsync(CommonConversions.ConvertAttributeAsync));

            var sourceAndConverted = await node.Members
                .Where(m => !(m is VBSyntax.OptionStatementSyntax))
                .SelectAsync(async m => (Source: m, Converted: await ConvertMemberAsync(m)));


            var convertedMembers = string.IsNullOrEmpty(options.RootNamespace)
                ? sourceAndConverted.Select(sd => sd.Converted)
                : PrependRootNamespace(sourceAndConverted, SyntaxFactory.IdentifierName(options.RootNamespace));

            var usings = await importsClauses
                .SelectAsync(async c => await c.AcceptAsync<UsingDirectiveSyntax>(TriviaConvertingDeclarationVisitor));
            var usingDirectiveSyntax = usings
                .Concat(_extraUsingDirectives.Select(u => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u))))
                .OrderByDescending(IsSystemUsing).ThenBy(u => u.Name.ToString().Replace("global::", "")).ThenByDescending(HasSourceMapAnnotations)
                .GroupBy(u => (Name: u.Name.ToString(), Alias: u.Alias))
                .Select(g => g.First());

            return SyntaxFactory.CompilationUnit(
                SyntaxFactory.List<ExternAliasDirectiveSyntax>(),
                SyntaxFactory.List(usingDirectiveSyntax),
                attributes,
                SyntaxFactory.List(convertedMembers)
            );
        }

        private static bool IsSystemUsing(UsingDirectiveSyntax u)
        {
            return u.Name.ToString().StartsWith("System") || u.Name.ToString().StartsWith("global::System");
        }

        private static bool HasSourceMapAnnotations(UsingDirectiveSyntax c)
        {
            return c.HasAnnotations(new[] { AnnotationConstants.SourceStartLineAnnotationKind, AnnotationConstants.SourceEndLineAnnotationKind });
        }

        private IReadOnlyCollection<MemberDeclarationSyntax> PrependRootNamespace(
            IReadOnlyCollection<(VBSyntax.StatementSyntax VbNode, MemberDeclarationSyntax CsNode)> membersConversions,
            IdentifierNameSyntax rootNamespaceIdentifier)
        {

            if (_topAncestorNamespace != null) {
                var csMembers = membersConversions.ToLookup(c => ShouldBeNestedInRootNamespace(c.VbNode, rootNamespaceIdentifier.Identifier.Text), c => c.CsNode);
                var nestedMembers = csMembers[true].Select<MemberDeclarationSyntax, SyntaxNode>(x => x);
                var newNamespaceDecl = (MemberDeclarationSyntax) _csSyntaxGenerator.NamespaceDeclaration(rootNamespaceIdentifier.Identifier.Text, nestedMembers);
                return csMembers[false].Concat(new[] { newNamespaceDecl }).ToArray();
            }
            return membersConversions.Select(n => n.CsNode).ToArray();
        }

        private bool ShouldBeNestedInRootNamespace(VBSyntax.StatementSyntax vbStatement, string rootNamespace)
        {
            return _semanticModel.GetDeclaredSymbol(vbStatement)?.ToDisplayString().StartsWith(rootNamespace) == true;
        }

        private bool IsNamespaceDeclaration(VBSyntax.StatementSyntax m)
        {
            return m is VBSyntax.NamespaceBlockSyntax;
        }

        public override async Task<CSharpSyntaxNode> VisitSimpleImportsClause(VBSyntax.SimpleImportsClauseSyntax node)
        {
            var nameEqualsSyntax = node.Alias == null
                ? null
                : SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName(CommonConversions.ConvertIdentifier(node.Alias.Identifier)));

            var name = await node.Name.AcceptAsync<NameSyntax>(_triviaConvertingExpressionVisitor);

            //Add static keyword for class and module imports
            var classification = await CommonConversions.GetClassificationLastTokenAsync(node);
            var staticToken = SyntaxFactory.Token(CSSyntaxKind.StaticKeyword);
            var staticClassifications = new List<string>
                {
                    ClassificationTypeNames.ClassName,
                    ClassificationTypeNames.ModuleName
                };

            var usingDirective = staticClassifications.Contains(classification)
                ? SyntaxFactory.UsingDirective(staticToken, nameEqualsSyntax, name)
                : SyntaxFactory.UsingDirective(nameEqualsSyntax, name);

            return usingDirective;
        }

        public override async Task<CSharpSyntaxNode> VisitNamespaceBlock(VBSyntax.NamespaceBlockSyntax node)
        {
            var members = (await node.Members.SelectAsync(ConvertMemberAsync)).Where(m => m != null);
            var sym = ModelExtensions.GetDeclaredSymbol(_semanticModel, node);
            string namespaceToDeclare = await WithDeclarationNameCasingAsync(node, sym);
            var parentNamespaceSyntax = node.GetAncestor<VBSyntax.NamespaceBlockSyntax>();
            var parentNamespaceDecl = parentNamespaceSyntax != null ? ModelExtensions.GetDeclaredSymbol(_semanticModel, parentNamespaceSyntax) : null;
            var parentNamespaceFullName = parentNamespaceDecl?.ToDisplayString() ?? _topAncestorNamespace;
            if (parentNamespaceFullName != null && namespaceToDeclare.StartsWith(parentNamespaceFullName + "."))
                namespaceToDeclare = namespaceToDeclare.Substring(parentNamespaceFullName.Length + 1);

            var cSharpSyntaxNode = (CSharpSyntaxNode) _csSyntaxGenerator.NamespaceDeclaration(namespaceToDeclare, SyntaxFactory.List(members));
            return cSharpSyntaxNode;
        }

        /// <summary>
        /// Semantic model merges the symbols, but the compiled form retains multiple namespaces, which (when referenced from C#) need to keep the correct casing.
        /// <seealso cref="CommonConversions.WithDeclarationNameCasing(TypeSyntax, ITypeSymbol)"/>
        /// <seealso cref="CommonConversions.WithDeclarationName(SyntaxToken, ISymbol, string)"/>
        /// </summary>
        private async Task<string> WithDeclarationNameCasingAsync(VBSyntax.NamespaceBlockSyntax node, ISymbol sym)
        {
            var sourceName = (await node.NamespaceStatement.Name.AcceptAsync<CSharpSyntaxNode>(_triviaConvertingExpressionVisitor)).ToString();
            var namespaceToDeclare = sym?.ToDisplayString() ?? sourceName;
            int lastIndex = namespaceToDeclare.LastIndexOf(sourceName, StringComparison.OrdinalIgnoreCase);
            if (lastIndex >= 0 && lastIndex + sourceName.Length == namespaceToDeclare.Length)
            {
                namespaceToDeclare = namespaceToDeclare.Substring(0, lastIndex) + sourceName;
            }

            return namespaceToDeclare;
        }

        private async Task<IEnumerable<MemberDeclarationSyntax>> ConvertMembersAsync(VBSyntax.TypeBlockSyntax parentType)
        {
            var members = parentType.Members;

            var namedTypeSymbol = _semanticModel.GetDeclaredSymbol(parentType);
            var additionalInitializers = new AdditionalInitializers(parentType, namedTypeSymbol, _vbCompilation);
            var methodsWithHandles = MethodsWithHandles.Create(GetMethodWithHandles(parentType));

            if (methodsWithHandles.Any()) _extraUsingDirectives.Add("System.Runtime.CompilerServices");//For MethodImplOptions.Synchronized
            
            _typeContext.Push(methodsWithHandles, additionalInitializers);
            try {
                var convertedMembers = (await members.SelectManyAsync(async member =>
                        (await ConvertMemberAsync(member)).Yield().Concat(GetAdditionalDeclarations(member)))
                    );

                return WithAdditionalMembers(convertedMembers).ToArray();//Ensure evaluated before popping type context
            } finally {
                _typeContext.Pop();
            }

            IEnumerable<MemberDeclarationSyntax> WithAdditionalMembers(IEnumerable<MemberDeclarationSyntax> convertedMembers)
            {
                var otherPartsOfType = GetAllPartsOfType(parentType, namedTypeSymbol).ToArray();
                var constructorFieldInitializersFromOtherParts = otherPartsOfType
                    .Where(t => (!Equals(t.Type.SyntaxTree.FilePath, _semanticModel.SyntaxTree.FilePath) ||
                                 !t.Type.Span.Equals(parentType.Span)))
                    .SelectMany(r => GetFieldsIdentifiersWithInitializer(r.Type, r.SemanticModel));
                additionalInitializers.AdditionalInstanceInitializers.AddRange(constructorFieldInitializersFromOtherParts);
                if (additionalInitializers.RequiresInitializeComponent) {
                    // Constructor event handlers not required since they'll be inside InitializeComponent - see other use of IsDesignerGeneratedTypeWithInitializeComponent
                    if (additionalInitializers.IsBestPartToAddTypeInit) convertedMembers = convertedMembers.Concat(methodsWithHandles.CreateDelegatingMethodsRequiredByInitializeComponent());
                    additionalInitializers.AdditionalInstanceInitializers.AddRange(CommonConversions.WinformsConversions.GetNameAssignments(otherPartsOfType));
                } else {
                    additionalInitializers.AdditionalInstanceInitializers.AddRange(methodsWithHandles.GetConstructorEventHandlers());
                }

                return additionalInitializers.WithAdditionalInitializers(convertedMembers.ToList(),
                    CommonConversions.ConvertIdentifier(parentType.BlockStatement.Identifier));
            }
        }

        private IEnumerable<(VBSyntax.TypeBlockSyntax Type, SemanticModel SemanticModel)> GetAllPartsOfType(VBSyntax.TypeBlockSyntax parentTypeBlock, ITypeSymbol parentType)
        {
            return parentType.DeclaringSyntaxReferences
                            .Select(t => t.GetSyntax().Parent as VBSyntax.TypeBlockSyntax)
                            .Where(t => t != null)
                            .Select(t => (Type: t, SemanticModel: _vbCompilation.GetSemanticModel(t.SyntaxTree)));
        }

        private IEnumerable<Assignment> GetFieldsIdentifiersWithInitializer(VBSyntax.TypeBlockSyntax tbs, SemanticModel semanticModel)
        {
            return tbs.Members.OfType<VBSyntax.FieldDeclarationSyntax>()
                .SelectMany(f => f.Declarators.SelectMany(d => d.Names.Select(n => (n, d.Initializer))))
                .Where(f => !semanticModel.IsDefinitelyStatic(f.n, f.Initializer?.Value))
                .Select(f => CreateInitializer(f));
        }

        private Assignment CreateInitializer((VBSyntax.ModifiedIdentifierSyntax n, VBSyntax.EqualsValueSyntax Initializer) f)
        {
            var csId = CommonConversions.ConvertIdentifier(f.n.Identifier);
            string initializerFunctionName = CommonConversions.GetInitialValueFunctionName(f.n);
            var invocation = SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(CommonConversions.CsEscapedIdentifier(initializerFunctionName)), SyntaxFactory.ArgumentList());
            return (SyntaxFactory.IdentifierName(csId), CSSyntaxKind.SimpleAssignmentExpression, invocation);
        }

        private MemberDeclarationSyntax[] GetAdditionalDeclarations(VBSyntax.StatementSyntax member)
        {
            if (_additionalDeclarations.TryGetValue(member, out var additionalStatements))
            {
                _additionalDeclarations.Remove(member);
                return additionalStatements;
            }

            return Array.Empty<MemberDeclarationSyntax>();
        }

        /// <summary>
        /// In case of error, creates a dummy class to attach the error comment to.
        /// This is because:
        /// * Empty statements are invalid in many contexts in C#.
        /// * There may be no previous node to attach to.
        /// * Attaching to a parent would result in the code being out of order from where it was originally.
        /// </summary>
        private async Task<MemberDeclarationSyntax> ConvertMemberAsync(VBSyntax.StatementSyntax member)
        {
            try {
                return await member.AcceptAsync<MemberDeclarationSyntax>(TriviaConvertingDeclarationVisitor);
            } catch (Exception e) {
                return CreateErrorMember(member, e);
            }

            MemberDeclarationSyntax CreateErrorMember(VBSyntax.StatementSyntax memberCausingError, Exception e)
            {
                var dummyClass
                    = SyntaxFactory.ClassDeclaration("_failedMemberConversionMarker" + ++_failedMemberConversionMarkerCount);
                return dummyClass.WithCsTrailingErrorComment(memberCausingError, e);
            }
        }

        public override async Task<CSharpSyntaxNode> VisitClassBlock(VBSyntax.ClassBlockSyntax node)
        {
            _accessedThroughMyClass = GetMyClassAccessedNames(node);
            var classStatement = node.ClassStatement;
            var attributes = await CommonConversions.ConvertAttributesAsync(classStatement.AttributeLists);
            var (parameters, constraints) = await SplitTypeParametersAsync(classStatement.TypeParameterList);
            var convertedIdentifier = CommonConversions.ConvertIdentifier(classStatement.Identifier);

            return SyntaxFactory.ClassDeclaration(
                attributes, ConvertTypeBlockModifiers(classStatement, TokenContext.Global),
                convertedIdentifier,
                parameters,
                await ConvertInheritsAndImplementsAsync(node.Inherits, node.Implements),
                constraints,
                SyntaxFactory.List(await ConvertMembersAsync(node))
            );
        }

        private async Task<BaseListSyntax> ConvertInheritsAndImplementsAsync(SyntaxList<VBSyntax.InheritsStatementSyntax> inherits, SyntaxList<VBSyntax.ImplementsStatementSyntax> implements)
        {
            if (inherits.Count + implements.Count == 0)
                return null;
            var baseTypes = new List<BaseTypeSyntax>();
            foreach (var t in inherits.SelectMany(c => c.Types).Concat(implements.SelectMany(c => c.Types)))
                baseTypes.Add(SyntaxFactory.SimpleBaseType(await t.AcceptAsync<TypeSyntax>(_triviaConvertingExpressionVisitor)));
            return SyntaxFactory.BaseList(SyntaxFactory.SeparatedList(baseTypes));
        }

        public override async Task<CSharpSyntaxNode> VisitModuleBlock(VBSyntax.ModuleBlockSyntax node)
        {
            var stmt = node.ModuleStatement;
            var attributes = await CommonConversions.ConvertAttributesAsync(stmt.AttributeLists);
            var members = SyntaxFactory.List(await ConvertMembersAsync(node));
            var (parameters, constraints) = await SplitTypeParametersAsync(stmt.TypeParameterList);

            return SyntaxFactory.ClassDeclaration(
                attributes, ConvertTypeBlockModifiers(stmt, TokenContext.InterfaceOrModule, Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword),
                CommonConversions.ConvertIdentifier(stmt.Identifier),
                parameters,
                await ConvertInheritsAndImplementsAsync(node.Inherits, node.Implements),
                constraints,
                members
            );
        }

        public override async Task<CSharpSyntaxNode> VisitStructureBlock(VBSyntax.StructureBlockSyntax node)
        {
            var stmt = node.StructureStatement;
            var attributes = await CommonConversions.ConvertAttributesAsync(stmt.AttributeLists);
            var members = SyntaxFactory.List(await ConvertMembersAsync(node));

            var (parameters, constraints) = await SplitTypeParametersAsync(stmt.TypeParameterList);

            return SyntaxFactory.StructDeclaration(
                attributes, ConvertTypeBlockModifiers(stmt, TokenContext.Global), CommonConversions.ConvertIdentifier(stmt.Identifier),
                parameters,
                await ConvertInheritsAndImplementsAsync(node.Inherits, node.Implements),
                constraints,
                members
            );
        }

        public override async Task<CSharpSyntaxNode> VisitInterfaceBlock(VBSyntax.InterfaceBlockSyntax node)
        {
            var stmt = node.InterfaceStatement;
            var attributes = await CommonConversions.ConvertAttributesAsync(stmt.AttributeLists);
            var members = SyntaxFactory.List(await ConvertMembersAsync(node));

            var (parameters, constraints) = await SplitTypeParametersAsync(stmt.TypeParameterList);

            return SyntaxFactory.InterfaceDeclaration(
                attributes, ConvertTypeBlockModifiers(stmt, TokenContext.InterfaceOrModule), CommonConversions.ConvertIdentifier(stmt.Identifier),
                parameters,
                await ConvertInheritsAndImplementsAsync(node.Inherits, node.Implements),
                constraints,
                members
            );
        }

        private SyntaxTokenList ConvertTypeBlockModifiers(VBSyntax.TypeStatementSyntax stmt,
            TokenContext interfaceOrModule, params Microsoft.CodeAnalysis.CSharp.SyntaxKind[] extraModifiers)
        {
            if (IsPartialType(stmt) && !HasPartialKeyword(stmt.Modifiers)) {
                extraModifiers = extraModifiers.Concat(new[] { Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword})
                    .ToArray();
            }

            return CommonConversions.ConvertModifiers(stmt, stmt.Modifiers, interfaceOrModule, extraCsModifierKinds: extraModifiers);
        }

        private static bool HasPartialKeyword(SyntaxTokenList modifiers)
        {
            return modifiers.Any(m => m.IsKind(VBasic.SyntaxKind.PartialKeyword));
        }

        private bool IsPartialType(VBSyntax.DeclarationStatementSyntax stmt)
        {
            return _semanticModel.GetDeclaredSymbol(stmt).IsPartialClassDefinition();
        }

        public override async Task<CSharpSyntaxNode> VisitEnumBlock(VBSyntax.EnumBlockSyntax node)
        {
            var stmt = node.EnumStatement;
            // we can cast to SimpleAsClause because other types make no sense as enum-type.
            var asClause = (VBSyntax.SimpleAsClauseSyntax)stmt.UnderlyingType;
            var attributes = await stmt.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttributeAsync);
            BaseListSyntax baseList = null;
            if (asClause != null) {
                baseList = SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(SyntaxFactory.SimpleBaseType(await asClause.Type.AcceptAsync<TypeSyntax>(_triviaConvertingExpressionVisitor))));
                if (asClause.AttributeLists.Count > 0) {
                    var attributeLists = await asClause.AttributeLists.SelectManyAsync(l => CommonConversions.ConvertAttributeAsync(l));
                    attributes = attributes.Concat(
                        SyntaxFactory.AttributeList(
                            SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReturnKeyword)),
                            SyntaxFactory.SeparatedList(attributeLists.SelectMany(a => a.Attributes)))
                    ).ToArray();
                }
            }
            var members = SyntaxFactory.SeparatedList(await node.Members.SelectAsync(async m => await m.AcceptAsync<EnumMemberDeclarationSyntax>(TriviaConvertingDeclarationVisitor)));
            return SyntaxFactory.EnumDeclaration(
                SyntaxFactory.List(attributes), CommonConversions.ConvertModifiers(stmt, stmt.Modifiers, TokenContext.Global), CommonConversions.ConvertIdentifier(stmt.Identifier),
                baseList,
                members
            );
        }

        public override async Task<CSharpSyntaxNode> VisitEnumMemberDeclaration(VBSyntax.EnumMemberDeclarationSyntax node)
        {
            var attributes = await CommonConversions.ConvertAttributesAsync(node.AttributeLists);
            return SyntaxFactory.EnumMemberDeclaration(
                attributes, CommonConversions.ConvertIdentifier(node.Identifier),
                await node.Initializer.AcceptAsync<EqualsValueClauseSyntax>(_triviaConvertingExpressionVisitor)
            );
        }

        public override async Task<CSharpSyntaxNode> VisitDelegateStatement(VBSyntax.DelegateStatementSyntax node)
        {
            var attributes = await node.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttributeAsync);

            var (typeParameters, constraints) = await SplitTypeParametersAsync(node.TypeParameterList);

            TypeSyntax returnType;
            var asClause = node.AsClause;
            if (asClause == null) {
                returnType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword));
            } else {
                returnType = await asClause.Type.AcceptAsync<TypeSyntax>(_triviaConvertingExpressionVisitor);
                if (asClause.AttributeLists.Count > 0) {
                    var attributeListSyntaxs = await asClause.AttributeLists.SelectManyAsync(l => CommonConversions.ConvertAttributeAsync(l));
                    attributes = attributes.Concat(
                        SyntaxFactory.AttributeList(
                            SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReturnKeyword)),
                            SyntaxFactory.SeparatedList(attributeListSyntaxs.SelectMany(a => a.Attributes)))
                        ).ToArray();
                }
            }

            return SyntaxFactory.DelegateDeclaration(
                SyntaxFactory.List(attributes), CommonConversions.ConvertModifiers(node, node.Modifiers, TokenContext.Global),
                returnType, CommonConversions.ConvertIdentifier(node.Identifier),
                typeParameters,
                await node.ParameterList.AcceptAsync<ParameterListSyntax>(_triviaConvertingExpressionVisitor),
                constraints
            );
        }

        public override async Task<CSharpSyntaxNode> VisitFieldDeclaration(VBSyntax.FieldDeclarationSyntax node)
        {
            AdditionalLocals.PushScope();
            List<MemberDeclarationSyntax> declarations;
            try {
                declarations = await GetMemberDeclarationsAsync(node);
            } finally {
                AdditionalLocals.PopScope();
            }
            _additionalDeclarations.Add(node, declarations.Skip(1).ToArray());
            return declarations.First();
        }

        private async Task<List<MemberDeclarationSyntax>> GetMemberDeclarationsAsync(VBSyntax.FieldDeclarationSyntax node)
        {
            var attributes = (await node.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttributeAsync)).ToList();
            var convertableModifiers =
                node.Modifiers.Where(m => !SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.WithEventsKeyword));
            var isWithEvents = node.Modifiers.Any(m => SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.WithEventsKeyword));
            var convertedModifiers =
                CommonConversions.ConvertModifiers(node.Declarators[0].Names[0], convertableModifiers.ToList(), GetMemberContext(node));
            var declarations = new List<MemberDeclarationSyntax>(node.Declarators.Count);

            foreach (var declarator in node.Declarators)
            {
                var splitDeclarations = await CommonConversions.SplitVariableDeclarationsAsync(declarator, preferExplicitType: true);
                declarations.AddRange(CreateMemberDeclarations(splitDeclarations.Variables, isWithEvents, convertedModifiers, attributes));
                declarations.AddRange(splitDeclarations.Methods.Cast<MemberDeclarationSyntax>());
            }

            return declarations;
        }

        private IEnumerable<MemberDeclarationSyntax> CreateMemberDeclarations(IReadOnlyCollection<(VariableDeclarationSyntax Decl, ITypeSymbol Type)> splitDeclarationVariables,
            bool isWithEvents, SyntaxTokenList convertedModifiers, List<AttributeListSyntax> attributes)
        {

            foreach (var (decl, type) in splitDeclarationVariables)
            {
                var thisFieldModifiers = convertedModifiers;
                if (type?.SpecialType == SpecialType.System_DateTime) {
                    var index = thisFieldModifiers.IndexOf(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ConstKeyword);
                    if (index >= 0) {
                        thisFieldModifiers = thisFieldModifiers.Replace(thisFieldModifiers[index], SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword));
                    }
                }

                if (isWithEvents) {
                    var fieldDecls = CreateWithEventsMembers(thisFieldModifiers, attributes, decl);
                    foreach (var f in fieldDecls) yield return f;
                } else {
                    foreach (var method in CreateExtraMethodMembers()) yield return method;

                    if (AdditionalLocals.GetDeclarations().Count() > 0) {
                        foreach (var additionalDecl in CreateAdditionalLocalMembers(thisFieldModifiers, attributes, decl)) {
                            yield return additionalDecl;
                        }
                    } else {
                        yield return SyntaxFactory.FieldDeclaration(SyntaxFactory.List(attributes), thisFieldModifiers, decl);
                    }

                }
            }
        }

        private IEnumerable<MemberDeclarationSyntax> CreateWithEventsMembers(SyntaxTokenList convertedModifiers, List<AttributeListSyntax> attributes, VariableDeclarationSyntax decl)
        {
            _extraUsingDirectives.Add("System.Runtime.CompilerServices");
            var initializers = decl.Variables
                .Where(a => a.Initializer != null)
                .ToDictionary(v => v.Identifier.Text, v => v.Initializer);
            var fieldDecl = decl.RemoveNodes(initializers.Values, SyntaxRemoveOptions.KeepNoTrivia);
            var initializerState = _typeContext.Initializers;
            var initializerCollection = convertedModifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword))
                ? initializerState.AdditionalStaticInitializers
                : initializerState.AdditionalInstanceInitializers;
            foreach (var initializer in initializers) {
                initializerCollection.Add((SyntaxFactory.IdentifierName(initializer.Key), Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression, initializer.Value.Value));
            }

            var fieldDecls = _typeContext.MethodsWithHandles.GetDeclarationsForFieldBackedProperty(fieldDecl,
                convertedModifiers, SyntaxFactory.List(attributes));
            return fieldDecls;
        }

        private IEnumerable<MemberDeclarationSyntax> CreateAdditionalLocalMembers(SyntaxTokenList convertedModifiers, List<AttributeListSyntax> attributes, VariableDeclarationSyntax decl)
        {
            if (decl.Variables.Count > 1) {
                // Currently no way to tell which _additionalLocals would apply to which initializer
                throw new NotImplementedException(
                    "Fields with multiple declarations and initializers with ByRef parameters not currently supported");
            }

            var v = decl.Variables.First();
            var invocations = v.Initializer.Value.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToArray();
            if (invocations.Length > 1) {
                throw new NotImplementedException(
                    "Field initializers with nested method calls not currently supported");
            }

            var invocationExpressionSyntax = invocations.First();
            var methodName = invocationExpressionSyntax.Expression
                .ChildNodes().OfType<SimpleNameSyntax>().Last();
            var newMethodName = $"{methodName.Identifier.ValueText}_{v.Identifier.ValueText}";
            var declarationInfo = AdditionalLocals.GetDeclarations();

            var localVars = declarationInfo
                .Select(al => CommonConversions.CreateLocalVariableDeclarationAndAssignment(al.Prefix, al.Initializer))
                .ToArray<StatementSyntax>();

            // This should probably use a unique name like in MethodBodyVisitor - a collision is far less likely here
            var newNames = declarationInfo.ToDictionary(l => l.Id, l => l.Prefix);
            var newInitializer = HoistedNodeState.ReplaceNames(v.Initializer.Value, newNames);

            var body = SyntaxFactory.Block(localVars.Concat(SyntaxFactory.ReturnStatement(newInitializer).Yield()));
            // Method calls in initializers must be static in C# - Supporting this is #281
            var methodDecl = ValidSyntaxFactory.CreateParameterlessMethod(newMethodName, decl.Type, body);
            yield return methodDecl;

            var newVar =
                v.WithInitializer(SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(newMethodName))));
            var newVarDecl =
                SyntaxFactory.VariableDeclaration(decl.Type, SyntaxFactory.SingletonSeparatedList(newVar));

            yield return SyntaxFactory.FieldDeclaration(SyntaxFactory.List(attributes), convertedModifiers, newVarDecl);
        }

        private IReadOnlyCollection<MemberDeclarationSyntax> CreateExtraMethodMembers()
        {
            var methodsInfos = AdditionalLocals.GetParameterlessFunctions();

            // In theory this should use a unique name like in MethodBodyVisitor
            // Unfortunately we can't change the name for these since the file containing the constructor of a partial class tries to call the method with exactly this name.
            // See GetInitialValueFunctionName
            var newMethodNames = methodsInfos.ToDictionary(l => l.Id, l => l.Prefix);
            for (int i = 0; i < _typeContext.Initializers.AdditionalInstanceInitializers.Count; i++) {
                var (a, b, initializer) = _typeContext.Initializers.AdditionalInstanceInitializers[i];
                _typeContext.Initializers.AdditionalInstanceInitializers[i] = (a, b, HoistedNodeState.ReplaceNames(initializer, newMethodNames));
            }

            return methodsInfos
                .Select(al => al.AsInstanceMethod(newMethodNames[al.Id]))
                .ToArray();
        }

        private List<MethodWithHandles> GetMethodWithHandles(VBSyntax.TypeBlockSyntax parentType)
        {
            if (parentType == null || !(this._semanticModel.GetDeclaredSymbol((global::Microsoft.CodeAnalysis.SyntaxNode)parentType) is ITypeSymbol containingType)) return new List<MethodWithHandles>();

            var methodWithHandleses = containingType.GetMembers().OfType<IMethodSymbol>()
                .Where(m => HandledEvents(m).Any())
                .Select(m => {
                    var ids = HandledEvents(m)
                        .Select(p => (SyntaxFactory.Identifier(GetCSharpIdentifierText(p.EventContainer)), CommonConversions.ConvertIdentifier(p.EventMember.Identifier, sourceTriviaMapKind: SourceTriviaMapKind.None), p.Event, p.ParametersToDiscard))
                        .ToList();
                    var csFormIds = ids.Where(id => id.Item1.Text == "this" || id.Item1.Text == "base").ToList();
                    var csPropIds = ids.Except(csFormIds).ToList();
                    if (!csPropIds.Any() && !csFormIds.Any()) return null;
                    var csMethodId = SyntaxFactory.Identifier(m.Name);
                    return new MethodWithHandles(_csSyntaxGenerator, csMethodId, csPropIds, csFormIds);
                }).Where(x => x != null).ToList();
            return methodWithHandleses;

            string GetCSharpIdentifierText(VBSyntax.EventContainerSyntax p)
            {
                switch (p) {
                    //For me, trying to use "MyClass" in a Handles expression is a syntax error. Events aren't overridable anyway so I'm not sure how this would get used.
                    case VBSyntax.KeywordEventContainerSyntax kecs when kecs.Keyword.IsKind(VBasic.SyntaxKind.MyBaseKeyword):
                        return "base";
                    case VBSyntax.KeywordEventContainerSyntax _:
                        return "this";
                    case VBSyntax.WithEventsEventContainerSyntax weecs:
                        return CommonConversions.CsEscapedIdentifier(weecs.Identifier.Text).Text;
                    case VBSyntax.WithEventsPropertyEventContainerSyntax wepecs:
                        return CommonConversions.CsEscapedIdentifier(wepecs.Property.Identifier.Text).Text;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(p), p, $"Unrecognized event container: `{p}`");
                }
            }
        }

        /// <summary>
        /// VBasic.VisualBasicExtensions.HandledEvents(m) seems to optimize away some events, so just detect from syntax
        /// </summary>
        private List<(VBSyntax.EventContainerSyntax EventContainer, VBSyntax.IdentifierNameSyntax EventMember, IEventSymbol Event, int ParametersToDiscard)> HandledEvents(IMethodSymbol m)
        {
            return m.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VBSyntax.MethodStatementSyntax>()
                .Where(mbb => mbb.HandlesClause?.Events.Any() == true)
                .SelectMany(mbb => HandledEvent(mbb))
                .ToList();
        }

        private IEnumerable<(VBSyntax.EventContainerSyntax EventContainer, VBSyntax.IdentifierNameSyntax EventMember, IEventSymbol Event, int ParametersToDiscard)> HandledEvent(VBSyntax.MethodStatementSyntax mbb)
        {
            var mayRequireDiscardedParameters = !mbb.ParameterList.Parameters.Any();
            //TODO: PERF: Get group by syntax tree and get semantic model once in case it doesn't get succesfully cached
            var semanticModel = mbb.SyntaxTree == _semanticModel.SyntaxTree ? _semanticModel : _vbCompilation.GetSemanticModel(mbb.SyntaxTree, ignoreAccessibility: true);
            return mbb.HandlesClause.Events.Select(e => {
                var symbol = semanticModel.GetSymbolInfo(e.EventMember).Symbol as IEventSymbol;
                var toDiscard = mayRequireDiscardedParameters ? symbol?.Type.GetDelegateInvokeMethod()?.GetParameters().Count() ?? 0 : 0;
                var symbolParameters = symbol?.GetParameters();
                return (e.EventContainer, e.EventMember, Event: symbol, toDiscard);
            });
        }

        public override async Task<CSharpSyntaxNode> VisitPropertyStatement(VBSyntax.PropertyStatementSyntax node)
        {
            var attributes = SyntaxFactory.List(await node.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttributeAsync));
            var isReadonly = node.Modifiers.Any(m => SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.ReadOnlyKeyword));
            var isWriteOnly = node.Modifiers.Any(m => SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.WriteOnlyKeyword));
            var convertibleModifiers = node.Modifiers.Where(m => !m.IsKind(VBasic.SyntaxKind.ReadOnlyKeyword, VBasic.SyntaxKind.WriteOnlyKeyword, VBasic.SyntaxKind.DefaultKeyword));
            var modifiers = CommonConversions.ConvertModifiers(node, convertibleModifiers.ToList(), GetMemberContext(node));
            var isIndexer = CommonConversions.IsDefaultIndexer(node);
            var propSymbol = ModelExtensions.GetDeclaredSymbol(_semanticModel, node) as IPropertySymbol;
            var accessedThroughMyClass = IsAccessedThroughMyClass(node, node.Identifier, propSymbol);
            bool hasImplementation = !node.Modifiers.Any(m => m.IsKind(VBasic.SyntaxKind.MustOverrideKeyword)) && node.GetAncestor<VBSyntax.InterfaceBlockSyntax>() == null;

            var directlyConvertedCsIdentifier = CommonConversions.CsEscapedIdentifier(node.Identifier.Value as string);
            var csIdentifier = CommonConversions.ConvertIdentifier(node.Identifier);
            var additionalDeclarations = new List<MemberDeclarationSyntax>();

            var explicitInterfaceSpecifier = IsPrivateInterfaceImplementation(propSymbol) || IsRenamedInterfaceMember(propSymbol, directlyConvertedCsIdentifier, csIdentifier)
                ? SyntaxFactory.ExplicitInterfaceSpecifier(CommonConversions.GetFullyQualifiedNameSyntax(propSymbol.ExplicitInterfaceImplementations.First().ContainingType))
                : null;

            var isExplicitInterfaceImplementation = explicitInterfaceSpecifier != null;

            var additionalInterfaceImplements = explicitInterfaceSpecifier != null
                ? propSymbol.ExplicitInterfaceImplementations.Skip(1)
                : ImmutableArray<IPropertySymbol>.Empty;

            var originalModifiers = modifiers;
            var filteredModifiers = modifiers.RemoveOnly(m => m.IsCsMemberVisibility());
            var interfaceMemberIdentifiers = new List<SyntaxToken> { csIdentifier };

            var shouldConvertToMethods = ShouldConvertAsParameterizedProperty(node);

            var (initializer, vbType) = await GetVbReturnTypeAsync(node);

            var rawType = await vbType.AcceptAsync<TypeSyntax>(_triviaConvertingExpressionVisitor)
                          ?? SyntaxFactory.PredefinedType(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ObjectKeyword));

            AccessorListSyntax accessors;
            if (node.Parent is VBSyntax.PropertyBlockSyntax propertyBlock) {
                if (shouldConvertToMethods) {
                    if (accessedThroughMyClass) {
                        // Would need to create a delegating implementation to implement this
                        throw new NotImplementedException("MyClass indexing not implemented");
                    }
                    var methodDeclarationSyntaxs = await propertyBlock.Accessors.SelectAsync(async a =>
                        await a.AcceptAsync<MethodDeclarationSyntax>(TriviaConvertingDeclarationVisitor, a == propertyBlock.Accessors.First() ? SourceTriviaMapKind.All : SourceTriviaMapKind.None));
                    var accessorMethods = methodDeclarationSyntaxs.Select(WithMergedModifiers).ToArray();

                    accessorMethods.Do(method => AddRemainingInterfaceDeclarations(method, explicitInterfaceSpecifier, attributes,
                        filteredModifiers, additionalInterfaceImplements, additionalDeclarations));

                    _additionalDeclarations.Add(propertyBlock, accessorMethods.Skip(1).Concat(additionalDeclarations).ToArray());

                    return accessorMethods[0];
                }

                accessors = SyntaxFactory.AccessorList(
                    SyntaxFactory.List(
                        (await propertyBlock.Accessors.SelectAsync(async a =>
                            await a.AcceptAsync<AccessorDeclarationSyntax>(TriviaConvertingDeclarationVisitor))
                        )
                    ));
            } else if (shouldConvertToMethods && propSymbol.ContainingType.IsInterfaceType()) {
                var methodDeclarationSyntaxs = new List<MemberDeclarationSyntax>();

                if (propSymbol.GetMethod != null) {
                    methodDeclarationSyntaxs.Add(await CreateMethodDeclarationSyntaxAsync(node.ParameterList, GetMethodId(node), false));
                }

                if (propSymbol.SetMethod != null) {
                    var setMethod = await CreateMethodDeclarationSyntaxAsync(node.ParameterList, SetMethodId(node), true);
                    setMethod = AddValueSetParameter(propSymbol, setMethod, rawType, explicitInterfaceSpecifier);
                    methodDeclarationSyntaxs.Add(setMethod);
                }

                _additionalDeclarations.Add(node, methodDeclarationSyntaxs.Skip(1).ToArray());

                return methodDeclarationSyntaxs[0];
            } else {
                accessors = ConvertSimpleAccessors(isWriteOnly, isReadonly, hasImplementation, propSymbol.DeclaredAccessibility, isExplicitInterfaceImplementation);
            }

            if (isIndexer) {
                if (accessedThroughMyClass) {
                    // Not sure if this is possible
                    throw new NotImplementedException("MyClass indexing not implemented");
                }

                var parameters = await node.ParameterList.Parameters.SelectAsync(async p => await p.AcceptAsync<ParameterSyntax>(_triviaConvertingExpressionVisitor));
                var parameterList = SyntaxFactory.BracketedParameterList(SyntaxFactory.SeparatedList(parameters));
                return SyntaxFactory.IndexerDeclaration(
                    SyntaxFactory.List(attributes),
                    modifiers,
                    rawType,
                    null,
                    parameterList,
                    accessors
                );
            }

            var accessorList = GetDelegatingAccessorList(explicitInterfaceSpecifier, csIdentifier, accessors);
            var interfaceDeclParams = new PropertyDeclarationParameters(attributes, filteredModifiers, rawType, accessorList);

            AddInterfaceMemberDeclarations(additionalInterfaceImplements, additionalDeclarations, interfaceMemberIdentifiers, interfaceDeclParams);

            var renamedInterfaceImplDeclParams = new PropertyDeclarationParameters(attributes, originalModifiers,
                rawType, accessorList, directlyConvertedCsIdentifier);

            AddInterfaceMemberDeclaration(interfaceMemberIdentifiers, propSymbol, additionalDeclarations, renamedInterfaceImplDeclParams);

            if (accessedThroughMyClass) {

                var realModifiers = modifiers.RemoveOnly(m => m.IsKind(CSSyntaxKind.PrivateKeyword));
                string csIndentifierName = AddRealPropertyDelegatingToMyClassVersion(node, csIdentifier, attributes, realModifiers, rawType);
                modifiers = modifiers.Remove(modifiers.Single(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VirtualKeyword)));
                csIdentifier = SyntaxFactory.Identifier(csIndentifierName);
            } else if (explicitInterfaceSpecifier != null) {
                modifiers = filteredModifiers;
            }

            if (additionalDeclarations.Any()) {
                var declNode = (VBSyntax.StatementSyntax)node.FirstAncestorOrSelf<VBSyntax.PropertyBlockSyntax>() ?? node;
                _additionalDeclarations.Add(declNode, additionalDeclarations.ToArray());
            }

            var semicolonToken = SyntaxFactory.Token(initializer == null ? Microsoft.CodeAnalysis.CSharp.SyntaxKind.None : Microsoft.CodeAnalysis.CSharp.SyntaxKind.SemicolonToken);
            return SyntaxFactory.PropertyDeclaration(
                attributes,
                modifiers,
                rawType,
                explicitInterfaceSpecifier,
                csIdentifier, accessors,
                null,
                initializer,
                semicolonToken);

            MethodDeclarationSyntax WithMergedModifiers(MethodDeclarationSyntax member)
            {
                SyntaxTokenList originalModifiers = member.GetModifiers();
                var hasVisibility = originalModifiers.Any(m => m.IsCsVisibility(false, false));
                var modifiersToAdd = hasVisibility ? modifiers.Where(m => !m.IsCsVisibility(false, false)) : modifiers;
                var newModifiers = SyntaxFactory.TokenList(originalModifiers.Concat(modifiersToAdd));
                if (explicitInterfaceSpecifier != null) newModifiers = SyntaxFactory.TokenList(newModifiers.Where(m => !m.IsCsVisibility(false, false)));
                return member.WithModifiers(newModifiers);
            }

            async Task<MethodDeclarationSyntax> CreateMethodDeclarationSyntaxAsync(VBSyntax.ParameterListSyntax containingPropParameterList, string methodId, bool voidReturn)
            {
                var parameterListSyntax = await containingPropParameterList.AcceptAsync<ParameterListSyntax>(_triviaConvertingExpressionVisitor);
                var methodModifiers = SyntaxFactory.TokenList(modifiers.Where(m => !m.IsCsVisibility(false, false)));
                MethodDeclarationSyntax methodDeclarationSyntax = SyntaxFactory.MethodDeclaration(attributes, methodModifiers,
                    voidReturn ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(CSSyntaxKind.VoidKeyword)) : rawType,
                    explicitInterfaceSpecifier,
                    SyntaxFactory.Identifier(methodId), null,
                    (ParameterListSyntax)parameterListSyntax, SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(), null, null)
                    .WithSemicolonToken(SyntaxFactory.Token(CSSyntaxKind.SemicolonToken));
                return methodDeclarationSyntax;
            }
        }

        private void AddRemainingInterfaceDeclarations(MethodDeclarationSyntax method,
            ExplicitInterfaceSpecifierSyntax explicitInterfaceSpecifier, SyntaxList<AttributeListSyntax> attributes,
            SyntaxTokenList filteredModifiers, IEnumerable<IPropertySymbol> additionalInterfaceImplements,
            ICollection<MemberDeclarationSyntax> additionalDeclarations)
        {
            var identifier = method.Identifier;
            var clause = GetDelegatingClause(explicitInterfaceSpecifier, identifier,
                method.ParameterList, false);

            var interfaceMethodDeclParams = new MethodDeclarationParameters(attributes, filteredModifiers,
                method.ReturnType, method.TypeParameterList,
                method.ParameterList, method.ConstraintClauses, clause,
                identifier);

            AddInterfaceMemberDeclarations(additionalInterfaceImplements, additionalDeclarations,
                interfaceMethodDeclParams);
        }

        private async Task<(EqualsValueClauseSyntax Initializer, VBSyntax.TypeSyntax VbType)> GetVbReturnTypeAsync(VBSyntax.PropertyStatementSyntax node)
        {
            var initializer = await node.Initializer.AcceptAsync<EqualsValueClauseSyntax>(_triviaConvertingExpressionVisitor);
            VBSyntax.TypeSyntax vbType;
            switch (node.AsClause)
            {
                case VBSyntax.SimpleAsClauseSyntax c:
                    vbType = c.Type;
                    break;
                case VBSyntax.AsNewClauseSyntax c:
                    initializer = SyntaxFactory.EqualsValueClause(
                            await c.NewExpression.AcceptAsync<ExpressionSyntax>(_triviaConvertingExpressionVisitor));
                    vbType = VBasic.SyntaxExtensions.Type(c.NewExpression);
                    break;
                case null:
                    vbType = null;
                    break;
                default:
                    throw new NotImplementedException($"{node.AsClause.GetType().FullName} not implemented!");
            }

            return (initializer, vbType);
        }

        private static AccessorListSyntax GetDelegatingAccessorList(
            ExplicitInterfaceSpecifierSyntax explicitInterfaceSpecifier,
            SyntaxToken csIdentifier, AccessorListSyntax accessors)
        {
            if(explicitInterfaceSpecifier == null) return null;

            var getArrowClause = GetDelegatingClause(explicitInterfaceSpecifier, csIdentifier, null, false);
            var setArrowClause = GetDelegatingClause(explicitInterfaceSpecifier, csIdentifier, null, true);

            var getSetDict = new Dictionary<CSSyntaxKind, ArrowExpressionClauseSyntax> {
                {CSSyntaxKind.GetAccessorDeclaration, getArrowClause},
                {CSSyntaxKind.SetAccessorDeclaration, setArrowClause}
            };

            var delegatingAccessors = accessors.Accessors.Select(a => {
                var attributes = a.AttributeLists;
                var modifiers = a.Modifiers;

                var delegatingAccessor = SyntaxFactory.AccessorDeclaration(a.Kind(),
                    attributes, modifiers, getSetDict[a.Kind()]).WithSemicolonToken(SemicolonToken);

                return delegatingAccessor;
            });

            var delegatingAccessorList = new SyntaxList<AccessorDeclarationSyntax>(delegatingAccessors);
            return SyntaxFactory.AccessorList(delegatingAccessorList);
        }

        private string AddRealPropertyDelegatingToMyClassVersion(VBSyntax.PropertyStatementSyntax node, SyntaxToken csIdentifier,
            SyntaxList<AttributeListSyntax> attributes, SyntaxTokenList modifiers, TypeSyntax rawType)
        {
            var csIndentifierName = "MyClass" + csIdentifier.ValueText;
            ExpressionSyntax thisDotIdentifier = SyntaxFactory.ParseExpression($"this.{csIndentifierName}");
            var getReturn = SyntaxFactory.Block(SyntaxFactory.ReturnStatement(thisDotIdentifier));
            var getAccessor = SyntaxFactory.AccessorDeclaration(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration, getReturn);
            var setValue = SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression, thisDotIdentifier,
                    SyntaxFactory.IdentifierName(CommonConversions.CsEscapedIdentifier("value")))));
            var setAccessor = SyntaxFactory.AccessorDeclaration(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SetAccessorDeclaration, setValue);
            var realAccessors = SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {getAccessor, setAccessor}));
            var realDecl = SyntaxFactory.PropertyDeclaration(
                attributes,
                modifiers,
                rawType,
                null,
                csIdentifier, realAccessors,
                null,
                null,
                SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None));

            _additionalDeclarations.Add(node, new MemberDeclarationSyntax[] { realDecl });
            return csIndentifierName;
        }

        private static AccessorListSyntax ConvertSimpleAccessors(bool isWriteOnly, bool isReadonly,
            bool hasImplementation, Accessibility declaredAccessibility,
            bool isExplicitInterfaceImplementation)
        {
            AccessorListSyntax accessors;
            var getAccessor = SyntaxFactory.AccessorDeclaration(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SemicolonToken);
            var setAccessor = SyntaxFactory.AccessorDeclaration(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SetAccessorDeclaration)
                .WithSemicolonToken(SemicolonToken);

            if (isWriteOnly && declaredAccessibility != Accessibility.Private)
            {
                getAccessor = getAccessor.AddModifiers(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword));
            }

            if (isReadonly && declaredAccessibility != Accessibility.Private)
            {
                setAccessor = setAccessor.AddModifiers(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword));
            }

            var isReadOnlyInterface = (!hasImplementation || isExplicitInterfaceImplementation) && isReadonly;
            var isWriteOnlyInterface = (!hasImplementation || isExplicitInterfaceImplementation) && isWriteOnly;

            accessors = isReadOnlyInterface switch {
                true => SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {getAccessor})),
                _ => isWriteOnlyInterface switch {
                    true => SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {setAccessor})),
                    _ => SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {getAccessor, setAccessor}))
                }
            };

            return accessors;
        }

        public override async Task<CSharpSyntaxNode> VisitPropertyBlock(VBSyntax.PropertyBlockSyntax node)
        {
            return await node.PropertyStatement.AcceptAsync<CSharpSyntaxNode>(TriviaConvertingDeclarationVisitor, SourceTriviaMapKind.SubNodesOnly);
        }

        public override async Task<CSharpSyntaxNode> VisitAccessorBlock(VBSyntax.AccessorBlockSyntax node)
        {
            CSSyntaxKind blockKind;
            bool isIterator = node.IsIterator();
            var ancestoryPropertyBlock = node.GetAncestor<VBSyntax.PropertyBlockSyntax>();
            var containingPropertyStmt = ancestoryPropertyBlock?.PropertyStatement;
            var csReturnVariableOrNull = CommonConversions.GetRetVariableNameOrNull(node);
            var convertedStatements = await ConvertStatementsAsync(node.Statements, await CreateMethodBodyVisitorAsync(node, isIterator));
            var body = WithImplicitReturnStatements(node, convertedStatements, csReturnVariableOrNull);
            var attributes = await CommonConversions.ConvertAttributesAsync(node.AccessorStatement.AttributeLists);
            var modifiers = CommonConversions.ConvertModifiers(node, node.AccessorStatement.Modifiers, TokenContext.Local);
            var declaredPropSymbol = containingPropertyStmt != null ? _semanticModel.GetDeclaredSymbol(containingPropertyStmt) : null;

            string potentialMethodId;
            var explicitInterfaceSpecifier = declaredPropSymbol is { } propSymbol && IsPrivateInterfaceImplementation(propSymbol)
                ? SyntaxFactory.ExplicitInterfaceSpecifier(SyntaxFactory.IdentifierName(propSymbol.ExplicitInterfaceImplementations.First().ContainingType.Name))
                : null;

            var sourceMap = ancestoryPropertyBlock?.Accessors.FirstOrDefault() == node ? SourceTriviaMapKind.All : SourceTriviaMapKind.None;
            var returnType = containingPropertyStmt?.AsClause is VBSyntax.SimpleAsClauseSyntax asClause ?
                await asClause.Type.AcceptAsync<TypeSyntax>(_triviaConvertingExpressionVisitor, sourceMap) :
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(CSSyntaxKind.VoidKeyword));

            switch (node.Kind()) {
                case VBasic.SyntaxKind.GetAccessorBlock:
                    blockKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration;
                    potentialMethodId = GetMethodId(containingPropertyStmt);

                    if (ShouldConvertAsParameterizedProperty(containingPropertyStmt)) {
                        var method = await CreateMethodDeclarationSyntax(containingPropertyStmt?.ParameterList, false);
                        return method;
                    }
                    break;
                case VBasic.SyntaxKind.SetAccessorBlock:
                    blockKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind.SetAccessorDeclaration;
                    potentialMethodId = SetMethodId(containingPropertyStmt);

                    if (ShouldConvertAsParameterizedProperty(containingPropertyStmt)) {
                        var setMethod = await CreateMethodDeclarationSyntax(containingPropertyStmt?.ParameterList, true);
                        return AddValueSetParameter(declaredPropSymbol, setMethod, returnType, explicitInterfaceSpecifier);
                    }
                    break;
                case VBasic.SyntaxKind.AddHandlerAccessorBlock:
                    blockKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddAccessorDeclaration;
                    break;
                case VBasic.SyntaxKind.RemoveHandlerAccessorBlock:
                    blockKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind.RemoveAccessorDeclaration;
                    break;
                case VBasic.SyntaxKind.RaiseEventAccessorBlock:
                    var eventStatement = ((VBSyntax.EventBlockSyntax)node.Parent).EventStatement;
                    var eventName = CommonConversions.ConvertIdentifier(eventStatement.Identifier).ValueText;
                    potentialMethodId = $"On{eventName}";
                    return await CreateMethodDeclarationSyntax(node.AccessorStatement.ParameterList, true);
                default:
                    throw new NotSupportedException(node.Kind().ToString());
            }

            return SyntaxFactory.AccessorDeclaration(blockKind, attributes, modifiers, body);

            async Task<MethodDeclarationSyntax> CreateMethodDeclarationSyntax(VBSyntax.ParameterListSyntax containingPropParameterList, bool voidReturn)
            {
                var parameterListSyntax = await containingPropParameterList.AcceptAsync<ParameterListSyntax>(_triviaConvertingExpressionVisitor, sourceMap);
                SyntaxTokenList methodModifiers;

                if (explicitInterfaceSpecifier != null) {
                    methodModifiers = modifiers.RemoveOnly(x => x.IsKind(CSSyntaxKind.PrivateKeyword));
                    parameterListSyntax = MakeOptionalParametersRequired(parameterListSyntax);

                } else {
                    methodModifiers = modifiers;
                }

                MethodDeclarationSyntax methodDeclarationSyntax = SyntaxFactory.MethodDeclaration(attributes, methodModifiers,
                    voidReturn ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(CSSyntaxKind.VoidKeyword)) : returnType,
                    explicitInterfaceSpecifier,
                    SyntaxFactory.Identifier(potentialMethodId), null,
                    parameterListSyntax, SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(), body, null);
                return methodDeclarationSyntax;
            }
        }

        private static MethodDeclarationSyntax AddValueSetParameter(IPropertySymbol declaredPropSymbol,
            MethodDeclarationSyntax setMethod, TypeSyntax returnType,
            ExplicitInterfaceSpecifierSyntax explicitInterfaceSpecifierSyntax)
        {
            var valueParam = SyntaxFactory.Parameter(CommonConversions.CsEscapedIdentifier("value")).WithType(returnType);
            if ((declaredPropSymbol?.Parameters.Any(p => p.IsOptional) ?? false) && explicitInterfaceSpecifierSyntax == null) valueParam = valueParam.WithDefault(SyntaxFactory.EqualsValueClause(ValidSyntaxFactory.DefaultExpression));
            return setMethod.AddParameterListParameters(valueParam);
        }

        private static string SetMethodId(VBSyntax.PropertyStatementSyntax containingPropertyStmt)
        {
            return $"set_{(containingPropertyStmt.Identifier.Text)}";
        }

        private static string GetMethodId(VBSyntax.PropertyStatementSyntax containingPropertyStmt)
        {
            return $"get_{(containingPropertyStmt.Identifier.Text)}";
        }

        private static bool ShouldConvertAsParameterizedProperty(VBSyntax.PropertyStatementSyntax propStmt)
        {
            return propStmt.ParameterList?.Parameters.Any() == true
                   && !CommonConversions.IsDefaultIndexer(propStmt);
        }

        public override async Task<CSharpSyntaxNode> VisitAccessorStatement(VBSyntax.AccessorStatementSyntax node)
        {
            return SyntaxFactory.AccessorDeclaration(node.Kind().ConvertToken(), null);
        }

        public override async Task<CSharpSyntaxNode> VisitMethodBlock(VBSyntax.MethodBlockSyntax node)
        {
            var methodBlock = await node.SubOrFunctionStatement.AcceptAsync<BaseMethodDeclarationSyntax>(TriviaConvertingDeclarationVisitor, SourceTriviaMapKind.SubNodesOnly);

            var declaredSymbol = ModelExtensions.GetDeclaredSymbol(_semanticModel, node);
            if (!declaredSymbol.CanHaveMethodBody()) {
                return methodBlock;
            }
            var csReturnVariableOrNull = CommonConversions.GetRetVariableNameOrNull(node);
            var visualBasicSyntaxVisitor = await CreateMethodBodyVisitorAsync(node, node.IsIterator(), csReturnVariableOrNull);
            var convertedStatements = await ConvertStatementsAsync(node.Statements, visualBasicSyntaxVisitor);

            //  Just class events - for property events, see other use of IsDesignerGeneratedTypeWithInitializeComponent
            if (node.SubOrFunctionStatement.Identifier.Text == "InitializeComponent" && node.SubOrFunctionStatement.IsKind(VBasic.SyntaxKind.SubStatement) && declaredSymbol.ContainingType.IsDesignerGeneratedTypeWithInitializeComponent(_vbCompilation)) {
                var firstResumeLayout = convertedStatements.Statements.FirstOrDefault(IsThisResumeLayoutInvocation) ?? convertedStatements.Statements.Last();
                convertedStatements = convertedStatements.InsertNodesBefore(firstResumeLayout, _typeContext.MethodsWithHandles.GetInitializeComponentClassEventHandlers());
            }

            var body = WithImplicitReturnStatements(node, convertedStatements, csReturnVariableOrNull);

            return methodBlock.WithBody(body);
        }

        private static bool IsThisResumeLayoutInvocation(StatementSyntax s)
        {
            return s is ExpressionStatementSyntax ess && ess.Expression is InvocationExpressionSyntax ies && ies.Expression.ToString().Equals("this.ResumeLayout");
        }

        private BlockSyntax WithImplicitReturnStatements(VBSyntax.MethodBlockBaseSyntax node, BlockSyntax convertedStatements,
            IdentifierNameSyntax csReturnVariableOrNull)
        {
            if (!node.MustReturn()) return convertedStatements;
            if (_semanticModel.GetDeclaredSymbol(node) is { } ms && ms.ReturnsVoidOrAsyncTask()) {
                return convertedStatements;
            }


            var preBodyStatements = new List<StatementSyntax>();
            var postBodyStatements = new List<StatementSyntax>();

            var functionSym = ModelExtensions.GetDeclaredSymbol(_semanticModel, node);
            if (functionSym != null) {
                var returnType = CommonConversions.GetTypeSyntax(functionSym.GetReturnType());

                if (csReturnVariableOrNull != null) {
                    var retDeclaration = CommonConversions.CreateVariableDeclarationAndAssignment(
                        csReturnVariableOrNull.Identifier.ValueText, SyntaxFactory.DefaultExpression(returnType),
                        returnType);
                    preBodyStatements.Add(SyntaxFactory.LocalDeclarationStatement(retDeclaration));
                }

                ControlFlowAnalysis controlFlowAnalysis = null;
                if (!node.Statements.IsEmpty())
                    controlFlowAnalysis =
                        ModelExtensions.AnalyzeControlFlow(_semanticModel, node.Statements.First(), node.Statements.Last());

                bool mayNeedReturn = controlFlowAnalysis?.EndPointIsReachable != false;
                if (mayNeedReturn) {
                    var csReturnExpression = csReturnVariableOrNull ??
                                             (ExpressionSyntax)SyntaxFactory.DefaultExpression(returnType);
                    postBodyStatements.Add(SyntaxFactory.ReturnStatement(csReturnExpression));
                }
            }

            var statements = preBodyStatements
                .Concat(convertedStatements.Statements)
                .Concat(postBodyStatements);

            return SyntaxFactory.Block(statements);
        }

        private async Task<BlockSyntax> ConvertStatementsAsync(SyntaxList<VBSyntax.StatementSyntax> statements, VBasic.VisualBasicSyntaxVisitor<Task<SyntaxList<StatementSyntax>>> methodBodyVisitor)
        {
            return SyntaxFactory.Block(await statements.SelectManyAsync(async s => (IEnumerable<StatementSyntax>) await s.Accept(methodBodyVisitor)));
        }

        private bool IsAccessedThroughMyClass(SyntaxNode node, SyntaxToken identifier, ISymbol symbolOrNull)
        {
            bool accessedThroughMyClass = false;
            if (symbolOrNull != null && symbolOrNull.IsVirtual && !symbolOrNull.IsAbstract) {
                var classBlock = node.Ancestors().OfType<VBSyntax.ClassBlockSyntax>().FirstOrDefault();
                if (classBlock != null) {
                    accessedThroughMyClass = _accessedThroughMyClass.Contains(identifier.Text);
                }
            }

            return accessedThroughMyClass;
        }

        private static HashSet<string> GetMyClassAccessedNames(VBSyntax.ClassBlockSyntax classBlock)
        {
            var memberAccesses = classBlock.DescendantNodes().OfType<VBSyntax.MemberAccessExpressionSyntax>();
            var accessedTextNames = new HashSet<string>(memberAccesses
                .Where(mae => mae.Expression is VBSyntax.MyClassExpressionSyntax)
                .Select(mae => mae.Name.Identifier.Text), StringComparer.OrdinalIgnoreCase);
            return accessedTextNames;
        }

        public override async Task<CSharpSyntaxNode> VisitMethodStatement(VBSyntax.MethodStatementSyntax node)
        {
            var attributes = await CommonConversions.ConvertAttributesAsync(node.AttributeLists);
            bool hasBody = node.Parent is VBSyntax.MethodBlockBaseSyntax;

            if ("Finalize".Equals(node.Identifier.ValueText, StringComparison.OrdinalIgnoreCase)
                && node.Modifiers.Any(m => VBasic.VisualBasicExtensions.Kind(m) == VBasic.SyntaxKind.OverridesKeyword)) {
                var decl = SyntaxFactory.DestructorDeclaration(CommonConversions.ConvertIdentifier(node.GetAncestor<VBSyntax.TypeBlockSyntax>().BlockStatement.Identifier)
                ).WithAttributeLists(attributes);
                if (hasBody) return decl;
                return decl.WithSemicolonToken(SemicolonToken);
            } else {
                var tokenContext = GetMemberContext(node);
                var declaredSymbol = (IMethodSymbol)ModelExtensions.GetDeclaredSymbol(_semanticModel, node);
                var extraCsModifierKinds = declaredSymbol?.IsExtern == true ? new[] { Microsoft.CodeAnalysis.CSharp.SyntaxKind.ExternKeyword } : Array.Empty<Microsoft.CodeAnalysis.CSharp.SyntaxKind>();
                var convertedModifiers = CommonConversions.ConvertModifiers(node, node.Modifiers, tokenContext, extraCsModifierKinds: extraCsModifierKinds);

                bool accessedThroughMyClass = IsAccessedThroughMyClass(node, node.Identifier, declaredSymbol);

                var isPartialDefinition = declaredSymbol.IsPartialMethodDefinition();

                if (declaredSymbol.IsPartialMethodImplementation() || isPartialDefinition) {
                    var privateModifier = convertedModifiers.SingleOrDefault(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword));
                    if (privateModifier != default) {
                        convertedModifiers = convertedModifiers.Remove(privateModifier);
                    }
                    if (!HasPartialKeyword(node.Modifiers)) {
                        convertedModifiers = convertedModifiers.Add(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword));
                    }
                }
                var (typeParameters, constraints) = await SplitTypeParametersAsync(node.TypeParameterList);

                var returnType = (declaredSymbol != null ? CommonConversions.GetTypeSyntax(declaredSymbol.ReturnType) :
                    await (node.AsClause?.Type).AcceptAsync<TypeSyntax>(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.PredefinedType(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword)));

                var directlyConvertedCsIdentifier = CommonConversions.CsEscapedIdentifier(node.Identifier.Value as string);
                var csIdentifier = CommonConversions.ConvertIdentifier(node.Identifier);
                var parameterList = await node.ParameterList.AcceptAsync<ParameterListSyntax>(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.ParameterList();
                var additionalDeclarations = new List<MemberDeclarationSyntax>();

                var explicitInterfaceSpecifier = IsPrivateInterfaceImplementation(declaredSymbol) || IsRenamedInterfaceMember(declaredSymbol, directlyConvertedCsIdentifier, csIdentifier)
                    ? SyntaxFactory.ExplicitInterfaceSpecifier(CommonConversions.GetFullyQualifiedNameSyntax(declaredSymbol.ExplicitInterfaceImplementations.First().ContainingType))
                    : null;

                var additionalInterfaceImplements = explicitInterfaceSpecifier != null
                    ? declaredSymbol.ExplicitInterfaceImplementations.Skip(1)
                    : ImmutableArray<IMethodSymbol>.Empty;

                var originalConvertedModifiers = convertedModifiers;
                var originalParameterList = parameterList;

                var filteredModifiers = convertedModifiers.RemoveOnly(m => m.IsCsMemberVisibility());
                var requiredParameterList = MakeOptionalParametersRequired(parameterList);

                var interfaceMemberIdentifiers = new List<SyntaxToken> { csIdentifier };

                var clause = GetDelegatingClause(explicitInterfaceSpecifier, csIdentifier,
                    requiredParameterList, false);

                var interfaceDeclParams = new MethodDeclarationParameters(attributes, filteredModifiers, returnType, typeParameters, requiredParameterList, constraints, clause);

                AddInterfaceMemberDeclarations(additionalInterfaceImplements, additionalDeclarations, interfaceMemberIdentifiers, interfaceDeclParams);

                var renamedInterfaceImplDeclParams = new MethodDeclarationParameters(attributes, originalConvertedModifiers,
                    returnType, typeParameters, originalParameterList, constraints, clause, directlyConvertedCsIdentifier);

                AddInterfaceMemberDeclaration(interfaceMemberIdentifiers, declaredSymbol, additionalDeclarations, renamedInterfaceImplDeclParams);

                // If the method is virtual, and there is a MyClass.SomeMethod() call,
                // we need to emit a non-virtual method for it to call
                if (accessedThroughMyClass) {
                    var identifierName = "MyClass" + directlyConvertedCsIdentifier.ValueText;
                    var arrowClause = SyntaxFactory.ArrowExpressionClause(SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(identifierName), CreateDelegatingArgList(parameterList)));
                    var declModifiers = convertedModifiers;

                    // If an extra delegate method is emitted already because of interface method renaming,
                    // don't emit the virtual and accessor modifiers on the explicit implementation. 
                    if (interfaceMemberIdentifiers.Any(identifier => IsRenamedInterfaceMember(declaredSymbol,
                        renamedInterfaceImplDeclParams.Identifier, identifier))) {
                        declModifiers = declModifiers.RemoveOnly(m => m.IsCsMemberVisibility())
                           .RemoveOnly(m => m.IsKind(CSSyntaxKind.VirtualKeyword));
                    }

                    var originalNameDecl = SyntaxFactory.MethodDeclaration(
                        attributes,
                        declModifiers,
                        returnType,
                        explicitInterfaceSpecifier,
                        csIdentifier,
                        typeParameters,
                        parameterList,
                        constraints,
                        null,
                        arrowClause,
                        SyntaxFactory.Token(CSSyntaxKind.SemicolonToken)
                    );

                    additionalDeclarations.Add(originalNameDecl);
                    convertedModifiers = convertedModifiers.Remove(convertedModifiers.Single(m => m.IsKind(CSSyntaxKind.VirtualKeyword)));
                    csIdentifier = SyntaxFactory.Identifier(identifierName);
                    explicitInterfaceSpecifier = null;
                } else if (explicitInterfaceSpecifier != null) {
                    convertedModifiers = filteredModifiers;
                    parameterList = requiredParameterList;
                }

                if (additionalDeclarations.Any()) {
                    var declNode = (VBSyntax.StatementSyntax)node.Parent;
                    _additionalDeclarations.Add(declNode, additionalDeclarations.ToArray());
                }

                var decl = SyntaxFactory.MethodDeclaration(
                    attributes,
                    convertedModifiers,
                    returnType,
                    explicitInterfaceSpecifier,
                    csIdentifier,
                    typeParameters,
                    parameterList,
                    constraints,
                    null,//Body added by surrounding method block if appropriate
                    null
                );
                return hasBody && declaredSymbol.CanHaveMethodBody() ? decl : decl.WithSemicolonToken(SemicolonToken);
            }
        }

        private static void AddInterfaceMemberDeclaration(IEnumerable<SyntaxToken> interfaceMemberIdentifiers, ISymbol symbol,
            ICollection<MemberDeclarationSyntax> additionalDeclarations, DeclarationParameters interfaceImplDeclParams)
        {
            // If we had to rename the method to match the interface, emit a method for external references with the old name to point to
            if (!interfaceMemberIdentifiers.Any(identifier => IsRenamedInterfaceMember(symbol,
                interfaceImplDeclParams.Identifier, identifier))) {
                return;
            }

            var semicolonToken = SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SemicolonToken);

            Func<MemberDeclarationSyntax>
                declDelegate = interfaceImplDeclParams switch {
                    MethodDeclarationParameters methodParams => ()
                        => SyntaxFactory.MethodDeclaration(methodParams.Attributes, methodParams.Modifiers,
                            methodParams.ReturnType, null, methodParams.Identifier
                            , methodParams.TypeParameters, methodParams.ParameterList, methodParams.Constraints, null,
                            methodParams.ArrowClause, semicolonToken).WithoutSourceMapping(),

                    PropertyDeclarationParameters propertyParams => ()
                        => SyntaxFactory.PropertyDeclaration(propertyParams.Attributes, propertyParams.Modifiers,
                            propertyParams.ReturnType, null, propertyParams.Identifier, propertyParams.Accessors,
                            null, null).NormalizeWhitespace(),

                    _ => throw new ArgumentOutOfRangeException()
                };

            var declaration = declDelegate.Invoke();

            additionalDeclarations.Add(declaration);
        }

        private void AddInterfaceMemberDeclarations(IEnumerable<ISymbol> additionalInterfaceImplements,
            ICollection<MemberDeclarationSyntax> additionalDeclarations, DeclarationParameters declParams)
        {
            AddInterfaceMemberDeclarations(additionalInterfaceImplements, additionalDeclarations,
                new List<SyntaxToken>(), declParams);
        }

        private void AddInterfaceMemberDeclarations(IEnumerable<ISymbol> additionalInterfaceImplements,
            ICollection<MemberDeclarationSyntax> additionalDeclarations, ICollection<SyntaxToken> interfaceMemberIdentifiers,
            DeclarationParameters declParams)
        {
            var semicolonToken = SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SemicolonToken);
            Func<ExplicitInterfaceSpecifierSyntax, SyntaxToken, MemberDeclarationSyntax>
                declDelegate = declParams switch {
                    MethodDeclarationParameters methodParams => (explintfspec, identifier)
                        => SyntaxFactory.MethodDeclaration(methodParams.Attributes, methodParams.Modifiers,
                            methodParams.ReturnType, explintfspec, identifier
                            , methodParams.TypeParameters, methodParams.ParameterList, methodParams.Constraints, null,
                            methodParams.ArrowClause, semicolonToken).WithoutSourceMapping(),

                    PropertyDeclarationParameters propertyParams => (explintfspec, identifier)
                        => SyntaxFactory.PropertyDeclaration(propertyParams.Attributes, propertyParams.Modifiers,
                            propertyParams.ReturnType, explintfspec, identifier, propertyParams.Accessors,
                            null, null).NormalizeWhitespace(),

                    _ => throw new ArgumentOutOfRangeException()
                };

            additionalInterfaceImplements.Do(interfaceImplement => AddMemberDeclaration(additionalDeclarations,
                interfaceMemberIdentifiers, interfaceImplement, declParams.Identifier, declDelegate));
        }

        private void AddMemberDeclaration(ICollection<MemberDeclarationSyntax> additionalDeclarations, ICollection<SyntaxToken> interfaceMemberIdentifiers,
            ISymbol interfaceImplement, SyntaxToken identifier, Func<ExplicitInterfaceSpecifierSyntax, SyntaxToken, MemberDeclarationSyntax> declDelegate)
        {
            var explicitInterfaceName = CommonConversions.GetFullyQualifiedNameSyntax(interfaceImplement.ContainingType);
            var newExplicitInterfaceSpecifier = SyntaxFactory.ExplicitInterfaceSpecifier(explicitInterfaceName);
            var interfaceImplIdentifier = identifier == default
                ? SyntaxFactory.Identifier(interfaceImplement.Name)
                : identifier;

            var declaration = declDelegate.Invoke(newExplicitInterfaceSpecifier, interfaceImplIdentifier);
            additionalDeclarations.Add(declaration);

            interfaceMemberIdentifiers.Add(interfaceImplIdentifier);
        }


        private static ParameterListSyntax MakeOptionalParametersRequired(ParameterListSyntax parameterList)
        {
            if (parameterList == null) return null;

            var nonOptionalParameters = parameterList.Parameters.Select(ConvertOptionalParameter);

            var separatedSyntaxList = SyntaxFactory.SeparatedList(nonOptionalParameters);
            var newParameterList = parameterList.WithParameters(separatedSyntaxList);
            return newParameterList;
        }

        private static ParameterSyntax ConvertOptionalParameter(ParameterSyntax parameter)
        {
            var optionalAttributes = new List<string> { nameof(OptionalAttribute).GetAttributeIdentifier(),
                nameof(DefaultParameterValueAttribute).GetAttributeIdentifier() };

            var attrListsToRemove = parameter.AttributeLists.SingleOrDefault(aList => aList.Attributes
               .All(a =>
                {
                    string attrIdentifier = string.Empty;

                    if (a.Name is IdentifierNameSyntax identifierNameSyntax) {
                        attrIdentifier = identifierNameSyntax.Identifier.Text;
                    } else if (a.Name is AliasQualifiedNameSyntax aliasQualifiedNameSyntax) {
                        attrIdentifier = aliasQualifiedNameSyntax.Alias.Identifier.Text;
                    }

                    return optionalAttributes.Contains(attrIdentifier);
                }));

            var newAttrLists = parameter.AttributeLists.Remove(attrListsToRemove);
            return parameter.WithDefault(null).WithAttributeLists(newAttrLists);
        }

        private static ArrowExpressionClauseSyntax GetDelegatingClause(
            ExplicitInterfaceSpecifierSyntax explicitInterfaceSpecifier, SyntaxToken csIdentifier,
            ParameterListSyntax parameterList, bool isSetAccessor)
        {
            if (explicitInterfaceSpecifier == null) return null;

            if (parameterList != null && isSetAccessor)
                throw new InvalidOperationException("Parameterized setters shouldn't have a delegating clause. " +
                                                    $"\r\nInvalid arguments: {nameof(isSetAccessor)} = {true}," +
                                                    $" {nameof(parameterList)} has {parameterList.Parameters.Count} parameters");

            var simpleMemberAccess = GetSimpleMemberAccess(explicitInterfaceSpecifier, csIdentifier);

            var expression = parameterList != null
                ? (ExpressionSyntax)SyntaxFactory.InvocationExpression(simpleMemberAccess, CreateDelegatingArgList(parameterList))
                : simpleMemberAccess;

            var arrowClauseExpression = isSetAccessor
                ? SyntaxFactory.AssignmentExpression(CSSyntaxKind.SimpleAssignmentExpression, simpleMemberAccess,
                    SyntaxFactory.IdentifierName("value"))
                : expression;

            var arrowClause = SyntaxFactory.ArrowExpressionClause(arrowClauseExpression);
            return arrowClause;
        }

        private static MemberAccessExpressionSyntax GetSimpleMemberAccess(
            ExplicitInterfaceSpecifierSyntax explicitInterfaceSpecifier, SyntaxToken csIdentifier)
        {
            var cast = SyntaxFactory.CastExpression(SyntaxFactory.Token(CSSyntaxKind.OpenParenToken),
                explicitInterfaceSpecifier.Name, SyntaxFactory.Token(CSSyntaxKind.CloseParenToken),
                SyntaxFactory.ThisExpression());

            var parenthesized = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.Token(CSSyntaxKind.OpenParenToken), cast,
                SyntaxFactory.Token(CSSyntaxKind.CloseParenToken));

            var simpleMemberAccess = SyntaxFactory.MemberAccessExpression(
                CSSyntaxKind.SimpleMemberAccessExpression, parenthesized,
                SyntaxFactory.Token(CSSyntaxKind.DotToken), SyntaxFactory.IdentifierName(csIdentifier));

            return simpleMemberAccess;
        }

        private static bool IsPrivateInterfaceImplementation(ISymbol declaredSymbol)
        {
            return declaredSymbol switch {
                IMethodSymbol methodSymbol => methodSymbol.DeclaredAccessibility == Accessibility.Private &&
                                              methodSymbol.ExplicitInterfaceImplementations.Any(),
                IPropertySymbol propertySymbol => propertySymbol.DeclaredAccessibility == Accessibility.Private &&
                                                  propertySymbol.ExplicitInterfaceImplementations.Any(),
                _ => throw new ArgumentOutOfRangeException(nameof(declaredSymbol))
            };
        }

        private static bool IsRenamedInterfaceMember(ISymbol declaredSymbol,
            SyntaxToken directlyConvertedCsIdentifier, SyntaxToken csIdentifier)
        {
            return declaredSymbol switch {
                IMethodSymbol methodSymbol => !StringComparer.Ordinal.Equals(directlyConvertedCsIdentifier.Value,
                                                  csIdentifier.Value)
                                              && methodSymbol.ExplicitInterfaceImplementations.Any(),
                IPropertySymbol propertySymbol => !StringComparer.Ordinal.Equals(directlyConvertedCsIdentifier.Value,
                                                      csIdentifier.Value)
                                                  && propertySymbol.ExplicitInterfaceImplementations.Any(),
                _ => throw new ArgumentOutOfRangeException(nameof(declaredSymbol))
            };
        }

        private static ArgumentListSyntax CreateDelegatingArgList(ParameterListSyntax parameterList)
        {
            var refKinds = parameterList.Parameters.Select(GetSingleModifier).ToArray();
            return parameterList.Parameters.Select(p => SyntaxFactory.IdentifierName(p.Identifier)).CreateCsArgList(refKinds);
        }

        private static CSSyntaxKind? GetSingleModifier(ParameterSyntax p)
        {
            var argKinds = new CSSyntaxKind?[] { CSSyntaxKind.RefKeyword, CSSyntaxKind.OutKeyword, CSSyntaxKind.InKeyword };
            return p.Modifiers.Select(Microsoft.CodeAnalysis.CSharp.CSharpExtensions.Kind)
                .Select<CSSyntaxKind, CSSyntaxKind?>(k => k)
                .FirstOrDefault(argKinds.Contains);
        }

        private TokenContext GetMemberContext(VBSyntax.StatementSyntax member)
        {
            var parentType = member.GetAncestorOrThis<VBSyntax.TypeBlockSyntax>();
            var parentTypeKind = parentType?.Kind();
            switch (parentTypeKind) {
                case VBasic.SyntaxKind.ModuleBlock:
                    return TokenContext.MemberInModule;
                case VBasic.SyntaxKind.ClassBlock:
                    return TokenContext.MemberInClass;
                case VBasic.SyntaxKind.InterfaceBlock:
                    return TokenContext.MemberInInterface;
                case VBasic.SyntaxKind.StructureBlock:
                    return TokenContext.MemberInStruct;
                default:
                    throw new ArgumentOutOfRangeException(nameof(member));
            }
        }

        public override async Task<CSharpSyntaxNode> VisitEventBlock(VBSyntax.EventBlockSyntax node)
        {
            var block = node.EventStatement;
            var attributes = await block.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttributeAsync);
            var modifiers = CommonConversions.ConvertModifiers(block, block.Modifiers, GetMemberContext(node));

            var rawType = await (block.AsClause?.Type).AcceptAsync<TypeSyntax>(_triviaConvertingExpressionVisitor) ?? ValidSyntaxFactory.VarType;

            var convertedAccessors = await node.Accessors.SelectAsync(async a => await a.AcceptAsync<CSharpSyntaxNode>(TriviaConvertingDeclarationVisitor));
            _additionalDeclarations.Add(node, convertedAccessors.OfType<MemberDeclarationSyntax>().ToArray());
            return SyntaxFactory.EventDeclaration(
                SyntaxFactory.List(attributes),
                modifiers,
                rawType,
                null, CommonConversions.ConvertIdentifier(block.Identifier),
                SyntaxFactory.AccessorList(SyntaxFactory.List(convertedAccessors.OfType<AccessorDeclarationSyntax>()))
            );
        }

        public override async Task<CSharpSyntaxNode> VisitEventStatement(VBSyntax.EventStatementSyntax node)
        {
            var attributes = await node.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttributeAsync);
            var modifiers = CommonConversions.ConvertModifiers(node, node.Modifiers, GetMemberContext(node));
            var id = CommonConversions.ConvertIdentifier(node.Identifier);

            var symbol = _semanticModel.GetDeclaredSymbol(node);
            if (node.AsClause == null && symbol.BaseMember() == null) {
                var delegateName = SyntaxFactory.Identifier(id.ValueText + "EventHandler");

                var delegateDecl = SyntaxFactory.DelegateDeclaration(
                    SyntaxFactory.List<AttributeListSyntax>(),
                    modifiers.RemoveOnly(m => m.IsKind(CSSyntaxKind.StaticKeyword)),
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(CSSyntaxKind.VoidKeyword)),
                    delegateName,
                    null,
                    await node.ParameterList.AcceptAsync<ParameterListSyntax>(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.ParameterList(),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>()
                );

                var eventDecl = SyntaxFactory.EventFieldDeclaration(
                    SyntaxFactory.List(attributes),
                    modifiers,
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName(delegateName),
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(id)))
                );

                _additionalDeclarations.Add(node, new MemberDeclarationSyntax[] { delegateDecl });
                return eventDecl;
            }
            var type = symbol.Type != null || node.AsClause == null ? CommonConversions.GetTypeSyntax(symbol.Type) : await node.AsClause.Type.AcceptAsync<TypeSyntax>(_triviaConvertingExpressionVisitor);
            var declaration = SyntaxFactory.VariableDeclaration(type,
                                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(id)));
            return SyntaxFactory.EventFieldDeclaration(
                SyntaxFactory.List(attributes),
                modifiers,
                declaration
            );
        }

        public override async Task<CSharpSyntaxNode> VisitOperatorBlock(VBSyntax.OperatorBlockSyntax node)
        {
            return await node.BlockStatement.AcceptAsync<CSharpSyntaxNode>(TriviaConvertingDeclarationVisitor, SourceTriviaMapKind.SubNodesOnly);
        }

        public override async Task<CSharpSyntaxNode> VisitOperatorStatement(VBSyntax.OperatorStatementSyntax node)
        {
            var containingBlock = (VBSyntax.OperatorBlockSyntax) node.Parent;
            var attributes = await node.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttributeAsync);
            var attributeList = SyntaxFactory.List(attributes);
            var returnType = await (node.AsClause?.Type).AcceptAsync<TypeSyntax>(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.PredefinedType(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword));
            var parameterList = await node.ParameterList.AcceptAsync<ParameterListSyntax>(_triviaConvertingExpressionVisitor);
            var methodBodyVisitor = await CreateMethodBodyVisitorAsync(node);
            var body = SyntaxFactory.Block(await containingBlock.Statements.SelectManyAsync(async s => (IEnumerable<StatementSyntax>) await s.Accept(methodBodyVisitor)));
            var modifiers = CommonConversions.ConvertModifiers(node, node.Modifiers, GetMemberContext(node));

            var conversionModifiers = modifiers.Where(CommonConversions.IsConversionOperator).ToList();
            var nonConversionModifiers = SyntaxFactory.TokenList(modifiers.Except(conversionModifiers));

            if (conversionModifiers.Any()) {
                return SyntaxFactory.ConversionOperatorDeclaration(attributeList, nonConversionModifiers,
                    conversionModifiers.Single(), returnType, parameterList, body, null);
            }

            return SyntaxFactory.OperatorDeclaration(attributeList, nonConversionModifiers, returnType, node.OperatorToken.ConvertToken(), parameterList, body, null);
        }

        public override async Task<CSharpSyntaxNode> VisitConstructorBlock(VBSyntax.ConstructorBlockSyntax node)
        {
            var block = node.BlockStatement;
            var attributes = await block.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttributeAsync);
            var modifiers = CommonConversions.ConvertModifiers(block, block.Modifiers, GetMemberContext(node));

            var ctor = (node.Statements.FirstOrDefault() as VBSyntax.ExpressionStatementSyntax)?.Expression as VBSyntax.InvocationExpressionSyntax;
            var ctorExpression = ctor?.Expression as VBSyntax.MemberAccessExpressionSyntax;
            var ctorArgs = await (ctor?.ArgumentList).AcceptAsync<ArgumentListSyntax>(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.ArgumentList();

            IEnumerable<VBSyntax.StatementSyntax> statements;
            ConstructorInitializerSyntax ctorCall;
            if (ctorExpression == null || !ctorExpression.Name.Identifier.IsKindOrHasMatchingText(VBasic.SyntaxKind.NewKeyword)) {
                statements = node.Statements;
                ctorCall = null;
            } else if (ctorExpression.Expression is VBSyntax.MyBaseExpressionSyntax) {
                statements = node.Statements.Skip(1);
                ctorCall = SyntaxFactory.ConstructorInitializer(Microsoft.CodeAnalysis.CSharp.SyntaxKind.BaseConstructorInitializer, ctorArgs);
            } else if (ctorExpression.Expression is VBSyntax.MeExpressionSyntax || ctorExpression.Expression is VBSyntax.MyClassExpressionSyntax) {
                statements = node.Statements.Skip(1);
                ctorCall = SyntaxFactory.ConstructorInitializer(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ThisConstructorInitializer, ctorArgs);
            } else {
                statements = node.Statements;
                ctorCall = null;
            }

            var methodBodyVisitor = await CreateMethodBodyVisitorAsync(node);
            return SyntaxFactory.ConstructorDeclaration(
                SyntaxFactory.List(attributes),
                modifiers, 
                CommonConversions.ConvertIdentifier(node.GetAncestor<VBSyntax.TypeBlockSyntax>().BlockStatement.Identifier).WithoutSourceMapping(), //TODO Use semantic model for this name
                await block.ParameterList.AcceptAsync<ParameterListSyntax>(_triviaConvertingExpressionVisitor),
                ctorCall,
                SyntaxFactory.Block(await statements.SelectManyAsync(async s => (IEnumerable<StatementSyntax>) await s.Accept(methodBodyVisitor)))
            );
        }

        public override async Task<CSharpSyntaxNode> VisitDeclareStatement(VBSyntax.DeclareStatementSyntax node)
        {
            var importAttributes = new List<AttributeArgumentSyntax>();
            _extraUsingDirectives.Add(DllImportType.Namespace);
            _extraUsingDirectives.Add(CharSetType.Namespace);
            var dllImportAttributeName = SyntaxFactory.ParseName(DllImportType.Name.Replace("Attribute", ""));
            var dllImportLibLiteral = await node.LibraryName.AcceptAsync<ExpressionSyntax>(_triviaConvertingExpressionVisitor);
            importAttributes.Add(SyntaxFactory.AttributeArgument(dllImportLibLiteral));

            if (node.AliasName != null) {
                importAttributes.Add(SyntaxFactory.AttributeArgument(SyntaxFactory.NameEquals("EntryPoint"), null, await node.AliasName.AcceptAsync<ExpressionSyntax>(_triviaConvertingExpressionVisitor)));
            }

            if (!node.CharsetKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None)) {
                importAttributes.Add(SyntaxFactory.AttributeArgument(SyntaxFactory.NameEquals(CharSetType.Name), null, SyntaxFactory.MemberAccessExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ParseTypeName(CharSetType.Name), SyntaxFactory.IdentifierName(node.CharsetKeyword.Text))));
            }

            var attributeArguments = CommonConversions.CreateAttributeArgumentList(importAttributes.ToArray());
            var dllImportAttributeList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(dllImportAttributeName, attributeArguments)));

            var attributeLists = (await CommonConversions.ConvertAttributesAsync(node.AttributeLists)).Add(dllImportAttributeList);

            var modifiers = CommonConversions.ConvertModifiers(node, node.Modifiers).Add(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)).Add(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ExternKeyword));
            var returnType = await (node.AsClause?.Type).AcceptAsync<TypeSyntax>(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.PredefinedType(SyntaxFactory.Token(CSSyntaxKind.VoidKeyword));
            var parameterListSyntax = await (node.ParameterList).AcceptAsync<ParameterListSyntax>(_triviaConvertingExpressionVisitor) ??
                                      SyntaxFactory.ParameterList();

            return SyntaxFactory.MethodDeclaration(attributeLists, modifiers, returnType, null, CommonConversions.ConvertIdentifier(node.Identifier), null,
                parameterListSyntax, SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(), null, null).WithSemicolonToken(SemicolonToken);
        }

        public override async Task<CSharpSyntaxNode> VisitTypeParameterList(VBSyntax.TypeParameterListSyntax node)
        {
            return SyntaxFactory.TypeParameterList(
                SyntaxFactory.SeparatedList(await node.Parameters.SelectAsync(async p => await p.AcceptAsync<TypeParameterSyntax>(TriviaConvertingDeclarationVisitor)))
            );
        }

        private async Task<(TypeParameterListSyntax parameters, SyntaxList<TypeParameterConstraintClauseSyntax> constraints)> SplitTypeParametersAsync(VBSyntax.TypeParameterListSyntax typeParameterList)
        {
            var constraints = SyntaxFactory.List<TypeParameterConstraintClauseSyntax>();
            if (typeParameterList == null) return (null, constraints);

            var paramList = new List<TypeParameterSyntax>();
            var constraintList = new List<TypeParameterConstraintClauseSyntax>();
            foreach (var p in typeParameterList.Parameters) {
                var tp = await p.AcceptAsync<TypeParameterSyntax>(TriviaConvertingDeclarationVisitor);
                paramList.Add(tp);
                var constraint = await (p.TypeParameterConstraintClause).AcceptAsync<TypeParameterConstraintClauseSyntax>(TriviaConvertingDeclarationVisitor);
                if (constraint != null)
                    constraintList.Add(constraint);
            }
            var parameters = SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(paramList));
            constraints = SyntaxFactory.List(constraintList);
            return (parameters, constraints);
        }

        public override async Task<CSharpSyntaxNode> VisitTypeParameter(VBSyntax.TypeParameterSyntax node)
        {
            SyntaxToken variance = default(SyntaxToken);
            if (!SyntaxTokenExtensions.IsKind(node.VarianceKeyword, VBasic.SyntaxKind.None)) {
                variance = SyntaxFactory.Token(SyntaxTokenExtensions.IsKind(node.VarianceKeyword, VBasic.SyntaxKind.InKeyword) ? Microsoft.CodeAnalysis.CSharp.SyntaxKind.InKeyword : Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword);
            }
            return SyntaxFactory.TypeParameter(SyntaxFactory.List<AttributeListSyntax>(), variance, CommonConversions.ConvertIdentifier(node.Identifier));
        }

        public override async Task<CSharpSyntaxNode> VisitTypeParameterSingleConstraintClause(VBSyntax.TypeParameterSingleConstraintClauseSyntax node)
        {
            var id = SyntaxFactory.IdentifierName(CommonConversions.ConvertIdentifier(((VBSyntax.TypeParameterSyntax)node.Parent).Identifier));
            return SyntaxFactory.TypeParameterConstraintClause(id, SyntaxFactory.SingletonSeparatedList(await node.Constraint.AcceptAsync<TypeParameterConstraintSyntax>(TriviaConvertingDeclarationVisitor)));
        }

        public override async Task<CSharpSyntaxNode> VisitTypeParameterMultipleConstraintClause(VBSyntax.TypeParameterMultipleConstraintClauseSyntax node)
        {
            var id = SyntaxFactory.IdentifierName(CommonConversions.ConvertIdentifier(((VBSyntax.TypeParameterSyntax)node.Parent).Identifier));
            var constraints = await node.Constraints.SelectAsync(async c => await c.AcceptAsync<TypeParameterConstraintSyntax>(TriviaConvertingDeclarationVisitor));
            return SyntaxFactory.TypeParameterConstraintClause(id, SyntaxFactory.SeparatedList(constraints.OrderBy(c => c.Kind() == Microsoft.CodeAnalysis.CSharp.SyntaxKind.ConstructorConstraint ? 1 : 0)));
        }

        public override async Task<CSharpSyntaxNode> VisitSpecialConstraint(VBSyntax.SpecialConstraintSyntax node)
        {
            if (SyntaxTokenExtensions.IsKind(node.ConstraintKeyword, VBasic.SyntaxKind.NewKeyword))
                return SyntaxFactory.ConstructorConstraint();
            return SyntaxFactory.ClassOrStructConstraint(node.IsKind(VBasic.SyntaxKind.ClassConstraint) ? Microsoft.CodeAnalysis.CSharp.SyntaxKind.ClassConstraint : Microsoft.CodeAnalysis.CSharp.SyntaxKind.StructConstraint);
        }

        public override async Task<CSharpSyntaxNode> VisitTypeConstraint(VBSyntax.TypeConstraintSyntax node)
        {
            return SyntaxFactory.TypeConstraint(await node.Type.AcceptAsync<TypeSyntax>(_triviaConvertingExpressionVisitor));
        }

    }
}
