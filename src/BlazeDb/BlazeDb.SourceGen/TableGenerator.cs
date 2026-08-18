using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BlazeDb.SourceGen;

/// <summary>
/// Emits a <c>TableDescriptor</c> (binary row/key serializers, key extractor, index
/// definitions) for every partial type annotated with <c>[BlazeDb.Table]</c>.
/// </summary>
[Generator]
public sealed class TableGenerator : IIncrementalGenerator
{
    private const string TableAttributeName = "BlazeDb.TableAttribute";
    private const string KeyAttributeName = "BlazeDb.KeyAttribute";
    private const string FieldAttributeName = "BlazeDb.FieldAttribute";
    private const string IgnoreAttributeName = "BlazeDb.IgnoreAttribute";
    private const string IndexAttributeName = "BlazeDb.IndexAttribute";
    private const string OrderedIndexAttributeName = "BlazeDb.OrderedIndexAttribute";
    private const string CompoundIndexAttributeName = "BlazeDb.CompoundIndexAttribute";
    private const string CompoundOrderedIndexAttributeName = "BlazeDb.CompoundOrderedIndexAttribute";

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
                spc.AddSource(result.Model.HintName, SourceEmitter.Emit(result.Model));
            }
        });
    }

    private static GenerationResult BuildModel(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var result = new GenerationResult();
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var location = symbol.Locations.FirstOrDefault();
        var typeName = symbol.Name;

        if (ctx.TargetNode is TypeDeclarationSyntax tds && !tds.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            result.Diagnostics.Add(new DiagnosticInfo(Diagnostics.NotPartial, location, typeName));
            return result;
        }

        if (symbol.TypeParameters.Length > 0 || symbol.ContainingType != null)
        {
            result.Diagnostics.Add(new DiagnosticInfo(Diagnostics.UnsupportedTypeShape, location, typeName));
            return result;
        }

        if (symbol.TypeKind == TypeKind.Class && !symbol.InstanceConstructors.Any(c => c.Parameters.Length == 0))
        {
            result.Diagnostics.Add(new DiagnosticInfo(Diagnostics.NoParameterlessConstructor, location, typeName));
            return result;
        }

        var tableName = ctx.Attributes[0].ConstructorArguments.Length > 0
            ? ctx.Attributes[0].ConstructorArguments[0].Value as string
            : null;

        var model = new TableModel
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
        var pendingAuto = new List<PropModel>();

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
                result.Diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.UnsupportedPropertyType, propLocation, typeName, prop.Name, prop.Type.ToDisplayString()));
                continue;
            }

            propModel.Name = prop.Name;
            propModel.IsKey = HasAttribute(attrs, KeyAttributeName);

            if (propModel.IsKey &&
                (propModel.Category != PropCategory.Scalar || propModel.Kind == ScalarKind.ByteArray))
            {
                result.Diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.UnsupportedKeyType, propLocation, typeName, prop.Name));
                continue;
            }

            var indexAttr = FindAttribute(attrs, IndexAttributeName);
            var orderedAttr = FindAttribute(attrs, OrderedIndexAttributeName);
            if ((indexAttr != null || orderedAttr != null) &&
                propModel.Category is PropCategory.ListCollection or PropCategory.ArrayCollection)
            {
                result.Diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.UnsupportedPropertyType, propLocation, typeName, prop.Name,
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
                    result.Diagnostics.Add(new DiagnosticInfo(
                        Diagnostics.DuplicateFieldNumber, propLocation, typeName, explicitNumber));
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
            result.Diagnostics.Add(new DiagnosticInfo(Diagnostics.MissingOrMultipleKey, location, typeName));
            return result;
        }
        model.Key = keys[0];

        BuildCompoundIndexes(symbol, model, result, location, typeName);
        ValidateIndexNames(model, result, location, typeName);

        result.Model = model;
        return result;
    }

    private static void BuildCompoundIndexes(
        INamedTypeSymbol symbol, TableModel model, GenerationResult result, Location? location, string typeName)
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

            var compound = new CompoundIndexModel { Name = name!, Ordered = ordered, Unique = GetBoolNamedArg(attr, "Unique") };
            var resolved = true;
            foreach (var propName in propNames)
            {
                var target = model.Props.FirstOrDefault(p =>
                    p.Name == propName && p.Category is not (PropCategory.ListCollection or PropCategory.ArrayCollection));
                if (target is null)
                {
                    result.Diagnostics.Add(new DiagnosticInfo(
                        Diagnostics.CompoundIndexPropertyNotFound, location, name!, typeName, propName ?? "<null>"));
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
                result.Diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.CompoundIndexNeedsTwoProperties, location, name!, typeName));
                continue;
            }

            model.CompoundIndexes.Add(compound);
        }
    }

    private static void ValidateIndexNames(
        TableModel model, GenerationResult result, Location? location, string typeName)
    {
        var seen = new HashSet<string>();
        foreach (var name in model.Props.Select(p => p.HashIndexName)
                     .Concat(model.Props.Select(p => p.OrderedIndexName))
                     .Concat(model.CompoundIndexes.Select(c => c.Name)))
        {
            if (name != null && !seen.Add(name))
            {
                result.Diagnostics.Add(new DiagnosticInfo(Diagnostics.DuplicateIndexName, location, typeName, name));
            }
        }
    }

    private static PropModel? Categorize(ITypeSymbol type)
    {
        // Nullable<T> where T is a supported scalar.
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var inner = nullable.TypeArguments[0];
            var innerKind = TypeMap.GetScalarKind(inner);
            if (innerKind is null)
            {
                return null;
            }
            return new PropModel
            {
                Category = PropCategory.NullableValueScalar,
                Kind = innerKind.Value,
                TypeDisplay = TypeMap.Display(type),
                ScalarTypeDisplay = TypeMap.Display(inner),
            };
        }

        // Plain scalar (includes string, byte[], enums, Guid, DateTime, ...).
        var kind = TypeMap.GetScalarKind(type);
        if (kind is not null)
        {
            var isAnnotatedRef = type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated;
            return new PropModel
            {
                Category = isAnnotatedRef ? PropCategory.NullableRefScalar : PropCategory.Scalar,
                Kind = kind.Value,
                TypeDisplay = TypeMap.Display(type),
                ScalarTypeDisplay = TypeMap.Display(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)),
            };
        }

        // Arrays and List<T> of scalars (byte[] was already handled as a scalar blob above).
        if (type is IArrayTypeSymbol array)
        {
            var elementKind = TypeMap.GetScalarKind(array.ElementType);
            if (elementKind is null || elementKind == ScalarKind.ByteArray)
            {
                return null;
            }
            return new PropModel
            {
                Category = PropCategory.ArrayCollection,
                Kind = elementKind.Value,
                TypeDisplay = TypeMap.Display(type),
                ScalarTypeDisplay = TypeMap.Display(array.ElementType),
            };
        }

        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
        {
            var elementKind = TypeMap.GetScalarKind(named.TypeArguments[0]);
            if (elementKind is null || elementKind == ScalarKind.ByteArray)
            {
                return null;
            }
            return new PropModel
            {
                Category = PropCategory.ListCollection,
                Kind = elementKind.Value,
                TypeDisplay = TypeMap.Display(type),
                ScalarTypeDisplay = TypeMap.Display(named.TypeArguments[0]),
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
