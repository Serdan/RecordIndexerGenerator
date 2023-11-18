using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RecordIndexerGenerator;

[Generator]
public class IndexerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.SyntaxProvider.CreateSyntaxProvider(
            (syntax, _) => syntax is RecordDeclarationSyntax record && record.Modifiers.Any(SyntaxKind.PartialKeyword),
            Transform
        );
        context.RegisterSourceOutput(provider, Execute);
    }

    private static PartialRecordSyntax Transform(GeneratorSyntaxContext context, CancellationToken token)
    {
        var node = (RecordDeclarationSyntax) context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(node)!;

        var query = from member in symbol.GetMembers()
                    where member is { DeclaredAccessibility: Accessibility.Public, Kind: SymbolKind.Property }
                    let property = (IPropertySymbol) member
                    where property.IsIndexer is false
                    select new Property(property.Name, property.Type.ToString(), property.GetMethod is not null, property.SetMethod?.IsInitOnly is false);

        var properties = query.ToImmutableArray();

        return new(
            node.Identifier.ToString(),
            node.Keyword + " " + node.ClassOrStructKeyword + " " + node.Identifier,
            symbol.ContainingNamespace?.ToString(),
            properties
        );
    }

    private static void Execute(SourceProductionContext context, PartialRecordSyntax record)
    {
        var source = GenerateSource(record);
        context.AddSource($"{record.Identifier}.Indexer.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    private static string GenerateSource(PartialRecordSyntax record)
    {
        var query =
            from property in record.Properties
            where property.IsGettable
            let name = property.Name
            select $"""
                            "{name}" => {name},
                """;

        var switchExpr = string.Join("\r\n", query);

        var q = from property in record.Properties
                where property.IsSettable
                let name = property.Name
                select $"""
                                    case "{name}":
                                        {name} = ({property.Type}) value;
                                        break;
                    """;

        var switchStmt = string.Join("\r\n", q);

        var indexer = $$"""
                public object this[string name]
                {
                    get => name switch {
            {{switchExpr}}
                        _ => throw new IndexOutOfRangeException(name)
                    };
                    set
                    {
                        switch (name)
                        {
            {{switchStmt}}
                            default:
                                throw new IndexOutOfRangeException(name);
                        }
                    }
                }
            """;

        // Create the class
        var @class = $$"""
            partial {{record.Declaration}}
            {
            {{indexer}}
            }
            """;

        const string usings = "using System;\n";

        var @namespace = usings + (record.Namespace is not null
                ? $"""
                namespace {record.Namespace};

                {@class}
                """
                : @class
            );

        return @namespace;
    }

    private record PartialRecordSyntax(string Identifier, string Declaration, string? Namespace, ImmutableArray<Property> Properties);

    private record Property(string Name, string Type, bool IsGettable, bool IsSettable);
}
