﻿#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Roslyn.Utilities;

namespace ICSharpCode.CodeConverter.Util.FromRoslyn
{
        /// <remarks>
        /// From Microsoft.CodeAnalysis.Shared.Extensions
        /// </remarks>
        internal static partial class ITypeSymbolExtensions
        {
        private const string DefaultParameterName = "p";
        private const string DefaultBuiltInParameterName = "v";

        public static bool CanAddNullCheck(this ITypeSymbol? type)
            => type != null && (type.IsReferenceType || type.IsNullable());

        public static IList<INamedTypeSymbol> GetAllInterfacesIncludingThis(this ITypeSymbol type)
        {
            var allInterfaces = type.AllInterfaces;
            if (type is INamedTypeSymbol namedType && namedType.TypeKind == TypeKind.Interface && !allInterfaces.Contains(namedType)) {
                var result = new List<INamedTypeSymbol>(allInterfaces.Length + 1);
                result.Add(namedType);
                result.AddRange(allInterfaces);
                return result;
            }

            return allInterfaces;
        }

        public static bool IsAbstractClass(this ITypeSymbol? symbol)
        {
            return symbol?.TypeKind == TypeKind.Class && symbol.IsAbstract;
        }

        public static bool IsSystemVoid(this ITypeSymbol? symbol)
        {
            return symbol?.SpecialType == SpecialType.System_Void;
        }

        public static bool IsNullable(this ITypeSymbol? symbol)
            => symbol?.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

        public static bool IsNullable(
            this ITypeSymbol? symbol,
            out ITypeSymbol? underlyingType)
        {
            if (symbol != null && IsNullable(symbol)) {
                underlyingType = ((INamedTypeSymbol)symbol).TypeArguments[0];
                return true;
            }

            underlyingType = null;
            return false;
        }

        public static bool IsModuleType(this ITypeSymbol? symbol)
        {
            return symbol?.TypeKind == TypeKind.Module;
        }

        public static bool IsClassType(this ITypeSymbol? symbol)
        {
            return symbol?.TypeKind == TypeKind.Class;
        }

        public static bool IsInterfaceType(this ITypeSymbol? symbol)
        {
            return symbol?.TypeKind == TypeKind.Interface;
        }

        public static bool IsDelegateType(this ITypeSymbol? symbol)
        {
            return symbol?.TypeKind == TypeKind.Delegate;
        }

        public static bool IsStructType(this ITypeSymbol? symbol)
        {
            return symbol?.TypeKind == TypeKind.Struct;
        }

        public static bool IsAnonymousType(this INamedTypeSymbol? symbol)
        {
            return symbol?.IsAnonymousType == true;
        }

        private static HashSet<INamedTypeSymbol> GetOriginalInterfacesAndTheirBaseInterfaces(
            this ITypeSymbol type,
            HashSet<INamedTypeSymbol>? symbols = null)
        {
            symbols ??= new HashSet<INamedTypeSymbol>(SymbolEquivalenceComparer.Instance);

            foreach (var interfaceType in type.Interfaces) {
                symbols.Add(interfaceType.OriginalDefinition);
                symbols.AddRange(interfaceType.AllInterfaces.Select(i => i.OriginalDefinition));
            }

            return symbols;
        }

        public static IEnumerable<ITypeSymbol> GetBaseTypesAndThis(this ITypeSymbol? type)
        {
            var current = type;
            while (current != null) {
                yield return current;
                current = current.BaseType;
            }
        }

        public static IEnumerable<INamedTypeSymbol> GetBaseTypes(this ITypeSymbol type)
        {
            var current = type.BaseType;
            while (current != null) {
                yield return current;
                current = current.BaseType;
            }
        }

        public static IEnumerable<ITypeSymbol> GetContainingTypesAndThis(this ITypeSymbol? type)
        {
            var current = type;
            while (current != null) {
                yield return current;
                current = current.ContainingType;
            }
        }

        public static IEnumerable<INamedTypeSymbol> GetContainingTypes(this ITypeSymbol type)
        {
            var current = type.ContainingType;
            while (current != null) {
                yield return current;
                current = current.ContainingType;
            }
        }

        // Determine if "type" inherits from "baseType", ignoring constructed types, optionally including interfaces,
        // dealing only with original types.
        public static bool InheritsFromOrEquals(
            this ITypeSymbol type, ITypeSymbol baseType, bool includeInterfaces)
        {
            if (!includeInterfaces) {
                return InheritsFromOrEquals(type, baseType);
            }

            return type.GetBaseTypesAndThis().Concat(type.AllInterfaces).Any(t => SymbolEquivalenceComparer.Instance.Equals(t, baseType));
        }

        // Determine if "type" inherits from "baseType", ignoring constructed types and interfaces, dealing
        // only with original types.
        public static bool InheritsFromOrEquals(
            this ITypeSymbol type, ITypeSymbol baseType)
        {
            return type.GetBaseTypesAndThis().Any(t => SymbolEquivalenceComparer.Instance.Equals(t, baseType));
        }

        // Determine if "type" inherits from or implements "baseType", ignoring constructed types, and dealing
        // only with original types.
        public static bool InheritsFromOrImplementsOrEqualsIgnoringConstruction(
            this ITypeSymbol type, ITypeSymbol baseType)
        {
            var originalBaseType = baseType.OriginalDefinition;
            type = type.OriginalDefinition;

            if (SymbolEquivalenceComparer.Instance.Equals(type, originalBaseType)) {
                return true;
            }

            IEnumerable<ITypeSymbol> baseTypes = (baseType.TypeKind == TypeKind.Interface) ? type.AllInterfaces : type.GetBaseTypes();
            return baseTypes.Any(t => SymbolEquivalenceComparer.Instance.Equals(t.OriginalDefinition, originalBaseType));
        }

        // Determine if "type" inherits from "baseType", ignoring constructed types, and dealing
        // only with original types.
        public static bool InheritsFromIgnoringConstruction(
            this ITypeSymbol type, ITypeSymbol baseType)
        {
            var originalBaseType = baseType.OriginalDefinition;

            // We could just call GetBaseTypes and foreach over it, but this
            // is a hot path in Find All References. This avoid the allocation
            // of the enumerator type.
            var currentBaseType = type.BaseType;
            while (currentBaseType != null) {
                if (SymbolEquivalenceComparer.Instance.Equals(currentBaseType.OriginalDefinition, originalBaseType)) {
                    return true;
                }

                currentBaseType = currentBaseType.BaseType;
            }

            return false;
        }

        public static bool ImplementsIgnoringConstruction(
            this ITypeSymbol type, ITypeSymbol interfaceType)
        {
            var originalInterfaceType = interfaceType.OriginalDefinition;
            return type.AllInterfaces.Any(t => SymbolEquivalenceComparer.Instance.Equals(t.OriginalDefinition, originalInterfaceType));
        }

        public static bool Implements(
            this ITypeSymbol type, ITypeSymbol interfaceType)
        {
            return type.AllInterfaces.Any(t => SymbolEquivalenceComparer.Instance.Equals(t, interfaceType));
        }

        public static bool IsAttribute(this ITypeSymbol symbol)
        {
            for (var b = symbol.BaseType; b != null; b = b.BaseType) {
                if (b.MetadataName == "Attribute" &&
                    b.ContainingType == null &&
                    b.ContainingNamespace != null &&
                    b.ContainingNamespace.Name == "System" &&
                    b.ContainingNamespace.ContainingNamespace != null &&
                    b.ContainingNamespace.ContainingNamespace.IsGlobalNamespace) {
                    return true;
                }
            }

            return false;
        }

        public static bool IsFormattableString(this ITypeSymbol? symbol)
        {
            return symbol?.MetadataName == "FormattableString"
                && symbol.ContainingType == null
                && symbol.ContainingNamespace?.Name == "System"
                && symbol.ContainingNamespace.ContainingNamespace?.IsGlobalNamespace == true;
        }

        public static bool IsUnexpressibleTypeParameterConstraint(this ITypeSymbol typeSymbol)
        {
            if (typeSymbol.IsSealed || typeSymbol.IsValueType) {
                return true;
            }

            switch (typeSymbol.TypeKind) {
                case TypeKind.Array:
                case TypeKind.Delegate:
                    return true;
            }

            switch (typeSymbol.SpecialType) {
                case SpecialType.System_Array:
                case SpecialType.System_Delegate:
                case SpecialType.System_MulticastDelegate:
                case SpecialType.System_Enum:
                case SpecialType.System_ValueType:
                    return true;
            }

            return false;
        }

        public static bool IsFractionalNumericType(this ITypeSymbol? type)
        {
            if (type != null) {
                switch (type.SpecialType) {
                    case SpecialType.System_Single:
                    case SpecialType.System_Double:
                    case SpecialType.System_Decimal:
                        return true;
                }
            }

            return false;
        }

        public static bool IsNumericType(this ITypeSymbol? type)
        {
            if (type != null) {
                switch (type.SpecialType) {
                    case SpecialType.System_Byte:
                    case SpecialType.System_SByte:
                    case SpecialType.System_Int16:
                    case SpecialType.System_UInt16:
                    case SpecialType.System_Int32:
                    case SpecialType.System_UInt32:
                    case SpecialType.System_Int64:
                    case SpecialType.System_UInt64:
                    case SpecialType.System_Single:
                    case SpecialType.System_Double:
                    case SpecialType.System_Decimal:
                        return true;
                }
            }

            return false;
        }
        public static bool ContainsAnonymousType(this ITypeSymbol? symbol)
        {
            switch (symbol) {
                case IArrayTypeSymbol a: return ContainsAnonymousType(a.ElementType);
                case IPointerTypeSymbol p: return ContainsAnonymousType(p.PointedAtType);
                case INamedTypeSymbol n: return ContainsAnonymousType(n);
                default: return false;
            }
        }

        private static bool ContainsAnonymousType(INamedTypeSymbol type)
        {
            if (type.IsAnonymousType) {
                return true;
            }

            foreach (var typeArg in type.GetAllTypeArguments()) {
                if (ContainsAnonymousType(typeArg)) {
                    return true;
                }
            }

            return false;
        }

        public static string CreateParameterName(this ITypeSymbol type, bool capitalize = false)
        {
            while (true) {
                switch (type) {
                    case IArrayTypeSymbol arrayType:
                        type = arrayType.ElementType;
                        continue;
                    case IPointerTypeSymbol pointerType:
                        type = pointerType.PointedAtType;
                        continue;
                }

                break;
            }

            var shortName = GetParameterName(type);
            return capitalize ? shortName.ToPascalCase() : shortName.ToCamelCase();
        }

        private static string GetParameterName(ITypeSymbol? type)
        {
            if (type == null || type.IsAnonymousType() || type.IsTupleType) {
                return DefaultParameterName;
            }

            if (type.IsSpecialType() || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) {
                return DefaultBuiltInParameterName;
            }

            var shortName = type.GetShortName();
            return shortName.Length == 0
                ? DefaultParameterName
                : shortName;
        }

        public static bool IsSpecialType(this ITypeSymbol? symbol)
        {
            if (symbol != null) {
                switch (symbol.SpecialType) {
                    case SpecialType.System_Object:
                    case SpecialType.System_Void:
                    case SpecialType.System_Boolean:
                    case SpecialType.System_SByte:
                    case SpecialType.System_Byte:
                    case SpecialType.System_Decimal:
                    case SpecialType.System_Single:
                    case SpecialType.System_Double:
                    case SpecialType.System_Int16:
                    case SpecialType.System_Int32:
                    case SpecialType.System_Int64:
                    case SpecialType.System_Char:
                    case SpecialType.System_String:
                    case SpecialType.System_UInt16:
                    case SpecialType.System_UInt32:
                    case SpecialType.System_UInt64:
                        return true;
                }
            }

            return false;
        }

        public static bool CanSupportCollectionInitializer(this ITypeSymbol typeSymbol, ISymbol within)
        {
            return
                typeSymbol.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable) &&
                typeSymbol.GetBaseTypesAndThis()
                    .Union(typeSymbol.GetOriginalInterfacesAndTheirBaseInterfaces())
                    .SelectAccessibleMembers<IMethodSymbol>(WellKnownMemberNames.CollectionInitializerAddMethodName, within)
                    .OfType<IMethodSymbol>()
                    .Any(m => m.Parameters.Any());
        }

        public static INamedTypeSymbol? GetDelegateType(this ITypeSymbol? typeSymbol, Compilation compilation)
        {
            if (typeSymbol != null) {
                var expressionOfT = compilation.ExpressionOfTType();
                if (typeSymbol.OriginalDefinition.Equals(expressionOfT)) {
                    var typeArgument = ((INamedTypeSymbol)typeSymbol).TypeArguments[0];
                    return typeArgument as INamedTypeSymbol;
                }

                if (typeSymbol.IsDelegateType()) {
                    return typeSymbol as INamedTypeSymbol;
                }
            }

            return null;
        }

        public static IEnumerable<T> GetAccessibleMembersInBaseTypes<T>(this ITypeSymbol containingType, ISymbol within) where T : class, ISymbol
        {
            if (containingType == null) {
                return SpecializedCollections.EmptyEnumerable<T>();
            }

            var types = containingType.GetBaseTypes();
            return types.SelectMany(x => x.GetMembers().OfType<T>().Where(m => m.IsAccessibleWithin(within)));
        }

        public static ImmutableArray<T> GetAccessibleMembersInThisAndBaseTypes<T>(this ITypeSymbol? containingType, ISymbol within) where T : class, ISymbol
        {
            if (containingType == null) {
                return ImmutableArray<T>.Empty;
            }

            return containingType.GetBaseTypesAndThis().SelectAccessibleMembers<T>(within).ToImmutableArray();
        }

        public static bool? AreMoreSpecificThan(this IList<ITypeSymbol> t1, IList<ITypeSymbol> t2)
        {
            if (t1.Count != t2.Count) {
                return null;
            }

            // For t1 to be more specific than t2, it has to be not less specific in every member,
            // and more specific in at least one.

            bool? result = null;
            for (var i = 0; i < t1.Count; ++i) {
                var r = t1[i].IsMoreSpecificThan(t2[i]);
                if (r == null) {
                    // We learned nothing. Do nothing.
                } else if (result == null) {
                    // We have found the first more specific type. See if
                    // all the rest on this side are not less specific.
                    result = r;
                } else if (result != r) {
                    // We have more specific types on both left and right, so we
                    // cannot succeed in picking a better type list. Bail out now.
                    return null;
                }
            }

            return result;
        }

        private static IEnumerable<T> SelectAccessibleMembers<T>(this IEnumerable<ITypeSymbol>? types, ISymbol within) where T : class, ISymbol
        {
            if (types == null) {
                return ImmutableArray<T>.Empty;
            }

            return types.SelectMany(x => x.GetMembers().OfType<T>().Where(m => m.IsAccessibleWithin(within)));
        }

        private static IEnumerable<T> SelectAccessibleMembers<T>(this IEnumerable<ITypeSymbol>? types, string memberName, ISymbol within) where T : class, ISymbol
        {
            if (types == null) {
                return ImmutableArray<T>.Empty;
            }

            return types.SelectMany(x => x.GetMembers(memberName).OfType<T>().Where(m => m.IsAccessibleWithin(within)));
        }

        private static bool? IsMoreSpecificThan(this ITypeSymbol t1, ITypeSymbol t2)
        {
            // SPEC: A type parameter is less specific than a non-type parameter.

            var isTypeParameter1 = t1 is ITypeParameterSymbol;
            var isTypeParameter2 = t2 is ITypeParameterSymbol;

            if (isTypeParameter1 && !isTypeParameter2) {
                return false;
            }

            if (!isTypeParameter1 && isTypeParameter2) {
                return true;
            }

            if (isTypeParameter1) {
                Debug.Assert(isTypeParameter2);
                return null;
            }

            if (t1.TypeKind != t2.TypeKind) {
                return null;
            }

            // There is an identity conversion between the types and they are both substitutions on type parameters.
            // They had better be the same kind.

            // UNDONE: Strip off the dynamics.

            // SPEC: An array type is more specific than another
            // SPEC: array type (with the same number of dimensions)
            // SPEC: if the element type of the first is
            // SPEC: more specific than the element type of the second.

            if (t1 is IArrayTypeSymbol) {
                var arr1 = (IArrayTypeSymbol)t1;
                var arr2 = (IArrayTypeSymbol)t2;

                // We should not have gotten here unless there were identity conversions
                // between the two types.

                return arr1.ElementType.IsMoreSpecificThan(arr2.ElementType);
            }

            // SPEC EXTENSION: We apply the same rule to pointer types.

            if (t1 is IPointerTypeSymbol) {
                var p1 = (IPointerTypeSymbol)t1;
                var p2 = (IPointerTypeSymbol)t2;
                return p1.PointedAtType.IsMoreSpecificThan(p2.PointedAtType);
            }

            // SPEC: A constructed type is more specific than another
            // SPEC: constructed type (with the same number of type arguments) if at least one type
            // SPEC: argument is more specific and no type argument is less specific than the
            // SPEC: corresponding type argument in the other.

            var n1 = t1 as INamedTypeSymbol;
            var n2 = t2 as INamedTypeSymbol;

            if (n1 == null) {
                return null;
            }

            // We should not have gotten here unless there were identity conversions between the
            // two types.

            var allTypeArgs1 = n1.GetAllTypeArguments().ToList();
            var allTypeArgs2 = n2.GetAllTypeArguments().ToList();

            return allTypeArgs1.AreMoreSpecificThan(allTypeArgs2);
        }

        public static bool IsEnumType(this ITypeSymbol type)
        {
            return type.IsValueType && type.TypeKind == TypeKind.Enum;
        }

        /// <remarks>
        /// WARNING: BUG: TODO: Modified from original Roslyn source because NullableAnnotation is not supported in CodeAnalysis 2.8.2
        /// </remarks>
        public static ITypeSymbol WithNullableAnnotationFrom(this ITypeSymbol type, ITypeSymbol symbolForNullableAnnotation)
        {
            return type;
        }

        public static ITypeSymbol WithNullableAnnotation(this ITypeSymbol type, NullableAnnotation nullableAnnotation)
        {
#if CODE_STYLE // TODO: Remove this #if once 'WithNullableAnnotation' is available in CodeStyle layer.
            return type;
#else
            return type.WithNullableAnnotation(nullableAnnotation);
#endif
        }

        public static ITypeSymbol? RemoveNullableIfPresent(this ITypeSymbol? symbol)
        {
            if (symbol.IsNullable()) {
                return symbol.GetTypeArguments().Single();
            }

            return symbol;
        }
    }

    /// <summary>
    /// Represents the nullability of values that can be assigned
    /// to an expression used as an lvalue.
    /// </summary>
    // Review docs: https://github.com/dotnet/roslyn/issues/35046
    public enum NullableAnnotation : byte
    {
        /// <summary>
        /// The expression has not been analyzed, or the syntax is
        /// not an expression (such as a statement).
        /// </summary>
        /// <remarks>
        /// There are a few different reasons the expression could
        /// have not been analyzed:
        ///     1) The symbol producing the expression comes from
        ///        a method that has not been annotated, such as
        ///        invoking a C# 7.3 or earlier method, or a
        ///        method in this compilation that is in a disabled
        ///        context.
        ///     2) Nullable is completely disabled in this
        ///        compilation.
        /// </remarks>
        None = 0,
        /// <summary>
        /// The expression is not annotated (does not have a ?).
        /// </summary>
        NotAnnotated,
        /// <summary>
        /// The expression is annotated (does have a ?).
        /// </summary>
        Annotated,
    }
}
