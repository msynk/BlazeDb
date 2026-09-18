using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BlazeDb.SourceGen;

/// <summary>
/// Emits a <c>BlazeDbTableDescriptor</c> (binary row/key serializers, key extractor, index
/// definitions) for every partial type annotated with <c>[BlazeDb.BlazeDbTable]</c>.
/// </summary>
[Generator]
public sealed class BlazeDbTableGenerator : IIncrementalGenerator
{
    private const string TableAttributeName = "BlazeDb.BlazeDbTableAttribute";
    private const string KeyAttributeName = "BlazeDb.BlazeDbKeyAttribute";
    private const string FieldAttributeName = "BlazeDb.BlazeDbFieldAttribute";
    private const string IgnoreAttributeName = "BlazeDb.BlazeDbIgnoreAttribute";
    private const string IndexAttributeName = "BlazeDb.BlazeDbIndexAttribute";
    private const string OrderedIndexAttributeName = "BlazeDb.BlazeDbOrderedIndexAttribute";
    private const string CompoundIndexAttributeName = "BlazeDb.BlazeDbCompoundIndexAttribute";
    private const string CompoundOrderedIndexAttributeName = "BlazeDb.BlazeDbCompoundOrderedIndexAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider.ForAttributeWithMetadataName(
            TableAttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, ct) => BuildModel(ctx, ct));

        context.RegisterSourceOutput(results, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                spc.ReportDiagnostic(Diagnostic.Create(diagnostic.Descriptor, diagnostic.Location, diagnostic.Args));
            }
            if (result.Model != null)
            {
                spc.AddSource(result.Model.HintName, BlazeDbSourceEmitter.Emit(result.Model));
            }
        });
    }

    private static BlazeDbGenerationResult BuildModel(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var result = new BlazeDbGenerationResult();
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var location = symbol.Locations.FirstOrDefault();
        var typeName = symbol.Name;

        if (ctx.TargetNode is TypeDeclarationSyntax tds && !tds.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            result.Diagnostics.Add(new BlazeDbDiagnosticInfo(BlazeDbDiagnostics.NotPartial, location, typeName));
            return result;
        }

        if (symbol.TypeParameters.Length > 0 || symbol.ContainingType != null)
        {
            result.Diagnostics.Add(new BlazeDbDiagnosticInfo(BlazeDbDiagnostics.UnsupportedTypeShape, location, typeName));
            return result;
        }

        if (symbol.TypeKind == TypeKind.Class && !symbol.InstanceConstructors.Any(c => c.Parameters.Length == 0))
        {
            result.Diagnostics.Add(new BlazeDbDiagnosticInfo(BlazeDbDiagnostics.NoParameterlessConstructor, location, typeName));
            return result;
        }

        if (FindInheritedProperty(symbol) is { } inherited)
        {
            result.Diagnostics.Add(new BlazeDbDiagnosticInfo(
                BlazeDbDiagnostics.InheritedProperties, location, typeName,
                inherited.ContainingType.Name, inherited.Name));
            return result;
        }

        var tableName = ctx.Attributes[0].ConstructorArguments.Length > 0
            ? ctx.Attributes[0].ConstructorArguments[0].Value as string
            : null;

        var model = new BlazeDbTableModel
        {
            Namespace = symbol.ContainingNamespace.IsGlobalNamespace
                ? null
                : symbol.ContainingNamespace.ToDisplayString(),
            TypeName = typeName,
            TypeKeyword = symbol.IsRecord
                ? (symbol.TypeKind == TypeKind.Struct ? "record struct" : "record")
                : (symbol.TypeKind == TypeKind.Struct ? "struct" : "class"),
            TableName = tableName ?? typeName,
        };
        model.HintName = (model.Namespace is null ? "" : model.Namespace + ".") + typeName + ".BlazeDb.g.cs";

        var usedNumbers = new HashSet<int>();
        var pendingAuto = new List<BlazeDbPropModel>();

        foreach (var member in symbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (member is not IPropertySymbol prop ||
                prop.IsStatic ||
                prop.IsIndexer ||
                prop.GetMethod is null ||
                prop.SetMethod is null ||
                prop.IsImplicitlyDeclared)
            {
                continue;
            }

            var attrs = prop.GetAttributes();
            if (HasAttribute(attrs, IgnoreAttributeName))
            {
                continue;
            }

            var propLocation = prop.Locations.FirstOrDefault() ?? location;
            var propModel = Categorize(prop.Type);
            if (propModel is null)
            {
                result.Diagnostics.Add(new BlazeDbDiagnosticInfo(
                    BlazeDbDiagnostics.UnsupportedPropertyType, propLocation, typeName, prop.Name, prop.Type.ToDisplayString()));
                continue;
            }

            propModel.Name = prop.Name;
            propModel.IsKey = HasAttribute(attrs, KeyAttributeName);

            if (propModel.IsKey &&
                (propModel.Category != BlazeDbPropCategory.Scalar || propModel.Kind == BlazeDbScalarKind.ByteArray))
            {
                result.Diagnostics.Add(new BlazeDbDiagnosticInfo(
                    BlazeDbDiagnostics.UnsupportedKeyType, propLocation, typeName, prop.Name));
                continue;
            }

            var indexAttr = FindAttribute(attrs, IndexAttributeName);
            var orderedAttr = FindAttribute(attrs, OrderedIndexAttributeName);
            if ((indexAttr != null || orderedAttr != null) &&
                propModel.Category is BlazeDbPropCategory.ListCollection or BlazeDbPropCategory.ArrayCollection)
            {
                result.Diagnostics.Add(new BlazeDbDiagnosticInfo(
                    BlazeDbDiagnostics.UnsupportedPropertyType, propLocation, typeName, prop.Name,
                    prop.Type.ToDisplayString() + " (collections cannot be indexed)"));
                continue;
            }
            propModel.HashIndexName = indexAttr is null ? null : GetIndexName(indexAttr) ?? prop.Name;
            propModel.HashIndexUnique = indexAttr != null && GetBoolNamedArg(indexAttr, "Unique");
            propModel.OrderedIndexName = orderedAttr is null ? null : GetIndexName(orderedAttr) ?? prop.Name;
            propModel.OrderedIndexUnique = orderedAttr != null && GetBoolNamedArg(orderedAttr, "Unique");

            var fieldAttr = FindAttribute(attrs, FieldAttributeName);
            if (fieldAttr != null && fieldAttr.ConstructorArguments.Length > 0 &&
                fieldAttr.ConstructorArguments[0].Value is int explicitNumber)
            {
                if (!usedNumbers.Add(explicitNumber))
                {
                    result.Diagnostics.Add(new BlazeDbDiagnosticInfo(
                        BlazeDbDiagnostics.DuplicateFieldNumber, propLocation, typeName, explicitNumber));
                    continue;
                }
                propModel.FieldNumber = explicitNumber;
            }
            else
            {
                pendingAuto.Add(propModel);
            }

            model.Props.Add(propModel);
        }

        var next = 1;
        foreach (var auto in pendingAuto)
        {
            while (!usedNumbers.Add(next))
            {
                next++;
            }
            auto.FieldNumber = next;
        }

        var keys = model.Props.Where(p => p.IsKey).ToList();
        if (keys.Count != 1)
        {
            result.Diagnostics.Add(new BlazeDbDiagnosticInfo(BlazeDbDiagnostics.MissingOrMultipleKey, location, typeName));
            return result;
        }
        model.Key = keys[0];

        BuildCompoundIndexes(symbol, model, result, location, typeName);
        if (!ValidateIndexNames(model, result, location, typeName))
        {
            // Emitting anyway would bury the diagnostic under syntax errors in generated code.
            return result;
        }

        result.Model = model;
        return result;
    }

    private static void BuildCompoundIndexes(
        INamedTypeSymbol symbol, BlazeDbTableModel model, BlazeDbGenerationResult result, Location? location, string typeName)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            var attrName = attr.AttributeClass?.ToDisplayString();
            var ordered = attrName == CompoundOrderedIndexAttributeName;
            if (!ordered && attrName != CompoundIndexAttributeName)
            {
                continue;
            }

            var args = attr.ConstructorArguments;
            var name = args.Length > 0 ? args[0].Value as string : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var propNames = args.Length > 1 && !args[1].IsNull
                ? args[1].Values.Select(v => v.Value as string).ToList()
                : new List<string?>();

            var compound = new BlazeDbCompoundIndexModel { Name = name!, Ordered = ordered, Unique = GetBoolNamedArg(attr, "Unique") };
            var resolved = true;
            foreach (var propName in propNames)
            {
                var target = model.Props.FirstOrDefault(p =>
                    p.Name == propName && p.Category is not (BlazeDbPropCategory.ListCollection or BlazeDbPropCategory.ArrayCollection));
                if (target is null)
                {
                    result.Diagnostics.Add(new BlazeDbDiagnosticInfo(
                        BlazeDbDiagnostics.CompoundIndexPropertyNotFound, location, name!, typeName, propName ?? "<null>"));
                    resolved = false;
                    break;
                }
                compound.Props.Add(target);
            }

            if (!resolved)
            {
                continue;
            }
            if (compound.Props.Count < 2)
            {
                result.Diagnostics.Add(new BlazeDbDiagnosticInfo(
                    BlazeDbDiagnostics.CompoundIndexNeedsTwoProperties, location, name!, typeName));
                continue;
            }

            model.CompoundIndexes.Add(compound);
        }
    }

    /// <summary>Returns false when a name makes the generated <c>Indexes</c> class impossible.</summary>
    private static bool ValidateIndexNames(
        BlazeDbTableModel model, BlazeDbGenerationResult result, Location? location, string typeName)
    {
        var valid = true;
        var seen = new HashSet<string>();
        foreach (var name in model.Props.Select(p => p.HashIndexName)
                     .Concat(model.Props.Select(p => p.OrderedIndexName))
                     .Concat(model.CompoundIndexes.Select(c => c.Name)))
        {
            if (name is null)
            {
                continue;
            }
            // An index name is emitted as a member of the generated Indexes class, so anything that
            // is not an identifier would surface as a syntax error in generated code instead of as
            // a diagnostic pointing at the attribute that caused it.
            if (!SyntaxFacts.IsValidIdentifier(name))
            {
                result.Diagnostics.Add(new BlazeDbDiagnosticInfo(BlazeDbDiagnostics.InvalidIndexName, location, typeName, name));
                valid = false;
                continue;
            }
            if (!seen.Add(name))
            {
                result.Diagnostics.Add(new BlazeDbDiagnosticInfo(BlazeDbDiagnostics.DuplicateIndexName, location, typeName, name));
                valid = false;
            }
        }
        return valid;
    }

    /// <summary>
    /// The first property a table type inherits that a reader would expect to be persisted. The
    /// generator only walks a type's own members, so an inherited one would be left out of every
    /// row without a word - refusing the type is the lesser evil. <c>[BlazeDbIgnore]</c> on the
    /// base property says the omission is intended.
    /// </summary>
    private static IPropertySymbol? FindInheritedProperty(INamedTypeSymbol symbol)
    {
        for (var baseType = symbol.BaseType;
             baseType is { TypeKind: TypeKind.Class, SpecialType: not SpecialType.System_Object };
             baseType = baseType.BaseType)
        {
            foreach (var member in baseType.GetMembers())
            {
                if (member is IPropertySymbol
                    {
                        IsStatic: false,
                        IsIndexer: false,
                        IsImplicitlyDeclared: false,
                        DeclaredAccessibility: Accessibility.Public,
                        GetMethod: not null,
                        SetMethod: { DeclaredAccessibility: Accessibility.Public },
                    } prop &&
                    !HasAttribute(prop.GetAttributes(), IgnoreAttributeName) &&
                    Categorize(prop.Type) is not null)
                {
                    return prop;
                }
            }
        }
        return null;
    }

    private static BlazeDbPropModel? Categorize(ITypeSymbol type)
    {
        // Nullable<T> where T is a supported scalar.
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var inner = nullable.TypeArguments[0];
            var innerKind = BlazeDbTypeMap.GetScalarKind(inner);
            if (innerKind is null)
            {
                return null;
            }
            return new BlazeDbPropModel
            {
                Category = BlazeDbPropCategory.NullableValueScalar,
                Kind = innerKind.Value,
                TypeDisplay = BlazeDbTypeMap.Display(type),
                ScalarTypeDisplay = BlazeDbTypeMap.Display(inner),
            };
        }

        // Plain scalar (includes string, byte[], enums, Guid, DateTime, ...).
        var kind = BlazeDbTypeMap.GetScalarKind(type);
        if (kind is not null)
        {
            var isAnnotatedRef = type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated;
            return new BlazeDbPropModel
            {
                Category = isAnnotatedRef ? BlazeDbPropCategory.NullableRefScalar : BlazeDbPropCategory.Scalar,
                Kind = kind.Value,
                TypeDisplay = BlazeDbTypeMap.Display(type),
                ScalarTypeDisplay = BlazeDbTypeMap.Display(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)),
            };
        }

        // Arrays and List<T> of scalars (byte[] was already handled as a scalar blob above).
        if (type is IArrayTypeSymbol array)
        {
            var elementKind = BlazeDbTypeMap.GetScalarKind(array.ElementType);
            if (elementKind is null || elementKind == BlazeDbScalarKind.ByteArray)
            {
                return null;
            }
            return new BlazeDbPropModel
            {
                Category = BlazeDbPropCategory.ArrayCollection,
                Kind = elementKind.Value,
                TypeDisplay = BlazeDbTypeMap.Display(type),
                ScalarTypeDisplay = BlazeDbTypeMap.Display(array.ElementType),
            };
        }

        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
        {
            var elementKind = BlazeDbTypeMap.GetScalarKind(named.TypeArguments[0]);
            if (elementKind is null || elementKind == BlazeDbScalarKind.ByteArray)
            {
                return null;
            }
            return new BlazeDbPropModel
            {
                Category = BlazeDbPropCategory.ListCollection,
                Kind = elementKind.Value,
                TypeDisplay = BlazeDbTypeMap.Display(type),
                ScalarTypeDisplay = BlazeDbTypeMap.Display(named.TypeArguments[0]),
            };
        }

        return null;
    }

    private static bool HasAttribute(System.Collections.Immutable.ImmutableArray<AttributeData> attrs, string fullName) =>
        FindAttribute(attrs, fullName) != null;

    private static AttributeData? FindAttribute(System.Collections.Immutable.ImmutableArray<AttributeData> attrs, string fullName)
    {
        foreach (var attr in attrs)
        {
            if (attr.AttributeClass?.ToDisplayString() == fullName)
            {
                return attr;
            }
        }
        return null;
    }

    private static bool GetBoolNamedArg(AttributeData attr, string name)
    {
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == name && named.Value.Value is bool b)
            {
                return b;
            }
        }
        return false;
    }

    private static string? GetIndexName(AttributeData attr)
    {
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "Name" && named.Value.Value is string s)
            {
                return s;
            }
        }
        return null;
    }
}
