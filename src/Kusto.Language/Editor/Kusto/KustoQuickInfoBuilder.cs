﻿using System;
using System.Collections.Generic;

namespace Kusto.Language.Editor
{
    using Kusto.Language.Symbols;
    using Syntax;
    using System.Text;
    using Utils;

    internal class KustoQuickInfoBuilder
    {
        private readonly KustoCodeService _service;
        private readonly KustoCode _code;
        private readonly QuickInfoOptions _options;

        public KustoQuickInfoBuilder(KustoCodeService service, KustoCode code, QuickInfoOptions options)
        {
            _service = service;
            _code = code;
            _options = options;
        }

        /// <summary>
        /// Gets the quick info for the item near the specified text position.
        /// </summary>
        public QuickInfo GetQuickInfo(int position, CancellationToken cancellationToken)
        {
            if (_code.HasSemantics)
            {
                var builder = new StringBuilder();

                var symbolInfo = GetSymbolInfo(position) ?? GetSyntaxInfo(position);
                var diagnosticInfo = GetDiagnosticInfo(position, cancellationToken);

                if (symbolInfo != null)
                {
                    if (diagnosticInfo != null)
                    {
                        return new QuickInfo(symbolInfo, diagnosticInfo);
                    }
                    else
                    {
                        return new QuickInfo(symbolInfo);
                    }
                }
                else if (diagnosticInfo != null)
                {
                    return new QuickInfo(diagnosticInfo);
                }
            }

            return QuickInfo.Empty;
        }

        private QuickInfoItem GetSymbolInfo(int position)
        {
            var token = _code.Syntax.GetTokenAt(position);
            if (token != null && position >= token.TextStart)
            {
                if (token.Parent is SyntaxNode node)
                {
                    var expr = node.GetFirstAncestorOrSelf<Expression>();

                    if (node.ReferencedSymbol != null && expr != node)
                    {
                        return GetSymbolInfo(node.ReferencedSymbol, null, null);
                    }
                    else if (expr != null)
                    {
                        if (expr.IsLiteral)
                        {
                            if (expr.Parent is BracketedName bn
                                && bn.Parent is Expression bne)
                            {
                                // special case for ['column name']
                                expr = bne;
                            }
                            else if (expr.ResultType != null && expr.ResultType.IsScalar)
                            {
                                return GetSymbolInfo(expr.ResultType, expr.ResultType, expr.ConstantValueInfo, QuickInfoKind.Literal);
                            }
                        }

                        return GetSymbolInfo(expr.ReferencedSymbol, expr.ResultType, expr.ConstantValueInfo);
                    }
                }
            }

            return null;
        }

        private QuickInfoItem GetSymbolInfo(Symbol symbol, TypeSymbol type, ValueInfo value, QuickInfoKind? itemKind = null)
        {
            if (symbol != null)
            {
                var texts = new List<ClassifiedText>();
                SymbolDisplay.GetSymbolDisplay(symbol, type, value, texts);

                if (itemKind == null)
                    itemKind = GetItemKind(symbol);

                return new QuickInfoItem(itemKind.Value, texts);
            }
            else
            {
                return null;
            }
        }

        private QuickInfoKind GetItemKind(Symbol symbol)
        {
            switch (symbol)
            {
                case ClusterSymbol _:
                    return QuickInfoKind.Cluster;
                case ColumnSymbol _:
                    return QuickInfoKind.Column;
                case DatabaseSymbol _:
                    return QuickInfoKind.Database;
                case FunctionSymbol f:
                    if (_code.Globals.IsBuiltInFunction(f))
                    {
                        return QuickInfoKind.BuiltInFunction;
                    }
                    else if (_code.Globals.IsDatabaseFunction(f))
                    {
                        return QuickInfoKind.DatabaseFunction;
                    }
                    else
                    {
                        return QuickInfoKind.LocalFunction;
                    }
                case OperatorSymbol _:
                    return QuickInfoKind.Operator;
                case ParameterSymbol _:
                    return QuickInfoKind.Parameter;
                case PatternSymbol _:
                    return QuickInfoKind.Pattern;
                case TableSymbol _:
                    return QuickInfoKind.Table;
                case VariableSymbol v:
                    if (v.Type is FunctionSymbol
                        || v.Type is PatternSymbol
                        || v.Type is TableSymbol
                        || v.Type is GraphSymbol
                        || v.Type is ScalarSymbol)
                    {
                        return GetItemKind(v.Type);
                    }
                    else
                    {
                        return QuickInfoKind.Variable;
                    }
                case ScalarSymbol _:
                    return QuickInfoKind.Type;
                case CommandSymbol _:
                    return QuickInfoKind.Command;
                case OptionSymbol _:
                    return QuickInfoKind.Option;
                case GraphSymbol _:
                    return QuickInfoKind.Graph;
                case GroupSymbol gs:
                    if (gs.Members.Count > 0)
                        return GetItemKind(gs.Members[0]);
                    return QuickInfoKind.Text;
                default:
                    return QuickInfoKind.Text;
            }
        }

        private QuickInfoItem GetSyntaxInfo(int position)
        {
            // place holder for adding information for the current grammar
            return null;
        }

        private QuickInfoItem GetDiagnosticInfo(int position, CancellationToken cancellationToken)
        {
            var diagnostics = _service.GetCombinedDiagnostics(waitForAnalysis: false, filter: _options.DiagnosticFilter, cancellationToken: cancellationToken);

            Diagnostic bestDx = null;

            foreach (var d in diagnostics)
            {
                var end = d.End > d.Start ? d.End : d.End + 1;
                if (position >= d.Start && position < end)
                {
                    // a later matching diagnostic is better if it starts closer to the position
                    if (bestDx == null || d.Start > bestDx.Start)
                    {
                        bestDx = d;
                    }
                }
            }

            if (bestDx != null)
            {
                return new QuickInfoItem(GetQuickInfoKind(bestDx.Severity), bestDx.Message);
            }

            return null;
        }

        private static QuickInfoKind GetQuickInfoKind(string severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error:
                    return QuickInfoKind.Error;
                case DiagnosticSeverity.Warning:
                    return QuickInfoKind.Warning;
                case DiagnosticSeverity.Suggestion:
                    return QuickInfoKind.Suggestion;
                default:
                    return QuickInfoKind.Text;
            }
        }
    }

    public static class SymbolDisplay
    {
        public static void GetSymbolDisplay(Symbol symbol, TypeSymbol type, List<ClassifiedText> texts, bool showDescription = true)
        {
            GetSymbolDisplay(symbol, type, null, texts, showDescription);
        }

        public static void GetSymbolDisplay(Symbol symbol, TypeSymbol type, ValueInfo value, List<ClassifiedText> texts, bool showDescription = true)
        {
            if (symbol is GroupSymbol gs)
            {
                var lines = Math.Min(gs.Members.Count, 5);

                for (int i = 0; i < lines; i++)
                {
                    var subsym = gs.Members[i];
                    if (i > 0)
                        texts.Add(new ClassifiedText("\n"));

                    // TODO: get the correct types for the sub symbols
                    GetSymbolDisplay(subsym, null, texts, showDescription: false);
                }

                if (lines < gs.Members.Count)
                {
                    texts.Add(new ClassifiedText($"\n+ ({gs.Members.Count - lines}) additional"));
                }
            }
            else if (symbol is ScalarSymbol)
            {
                texts.Add(new ClassifiedText(ClassificationKind.Identifier, symbol.Name));
            }
            else if (symbol is VariableSymbol v
                && (v.Type is FunctionSymbol || v.Type is PatternSymbol))
            {
                GetSymbolDisplay(v.Type, type, value, texts);
            }
            else if (symbol != null)
            {
                var nameKind = GetNameClassificationKind(symbol);
                texts.Add(new ClassifiedText(nameKind, symbol.Name));

                if (symbol is FunctionSymbol fs)
                {
                    // TODO: have this use the correct signature?
                    GetSignatureDisplay(fs.Signatures[0], texts);
                }

                if (type == null && symbol is VariableSymbol vs)
                {
                    type = vs.Type;
                }

                if (type != null && type != ScalarTypes.Unknown && !type.IsError)
                {
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, ": "));
                    GetTypeDisplay(type, texts);
                }

                if (value != null && value.RawText != null)
                {
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, " = "));
                    var kind = value.Value is string
                        ? ClassificationKind.StringLiteral
                        : ClassificationKind.Literal;

                    var text = value.RawText;
                    if (text.Length > 40)
                        text = text.Substring(0, 40) + " ...";

                    texts.Add(new ClassifiedText(kind, text));
                }

                if (showDescription)
                {
                    var description = GetDescription(symbol);
                    if (!string.IsNullOrEmpty(description))
                    {
                        texts.Add(new ClassifiedText(ClassificationKind.Comment, "\n\n" + description));
                    }
                }
            }
        }

        private static string GetDescription(Symbol symbol)
        {
            switch (symbol)
            {
                case TableSymbol t:
                    return t.Description;
                case ColumnSymbol c:
                    return c.Description;
                case ParameterSymbol p:
                    return p.Description;
                case FunctionSymbol f:
                    return f.Description;
                case OptionSymbol o:
                    return o.Description;
                case EntityGroupSymbol eg:
                    return "Entity Group";
                case EntityGroupElementSymbol eges:
                    return GetDescription(eges.UnderlyingSymbol);
                default:
                    return "";
            }
        }

        private static ClassificationKind GetNameClassificationKind(Symbol symbol)
        {
            switch (symbol)
            {
                case ColumnSymbol _:
                    return ClassificationKind.Column;
                case DatabaseSymbol _:
                    return ClassificationKind.Database;
                case FunctionSymbol _:
                    return ClassificationKind.Function;
                case OperatorSymbol _:
                    return ClassificationKind.MathOperator;
                case ParameterSymbol _:
                    return ClassificationKind.Parameter;
                case PatternSymbol _:
                    return ClassificationKind.Function; // TODO: need separate classification
                case TableSymbol _:
                    return ClassificationKind.Table;
                case VariableSymbol _:
                    return ClassificationKind.Variable;
                case CommandSymbol _:
                    return ClassificationKind.Command;
                case OptionSymbol _:
                    return ClassificationKind.Option;
                case EntityGroupElementSymbol eges:
                    return GetNameClassificationKind(eges.UnderlyingSymbol);
                //case GraphSymbol _:
                //return ClassificationKind.Graph;
                case ScalarSymbol s:
                    if (s == ScalarTypes.String)
                    {
                        return ClassificationKind.StringLiteral;
                    }
                    else
                    {
                        return ClassificationKind.Literal;
                    }
                default:
                    return ClassificationKind.PlainText;
            }
        }

        public static void GetSignatureDisplay(Signature sig, List<ClassifiedText> texts)
        {
            texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "("));

            for (int i = 0; i < sig.Parameters.Count; i++)
            {
                if (i > 0)
                {
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, ", "));
                }

                var p = sig.Parameters[i];

                if (p.IsOptional)
                {
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "["));
                    texts.Add(new ClassifiedText(ClassificationKind.SignatureParameter, p.Name));
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "]"));
                }
                else
                {
                    texts.Add(new ClassifiedText(ClassificationKind.SignatureParameter, p.Name));
                }
            }

            if (sig.HasRepeatableParameters)
            {
                texts.Add(new ClassifiedText(ClassificationKind.Punctuation, ", "));
                texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "..."));
            }

            texts.Add(new ClassifiedText(ClassificationKind.Punctuation, ")"));
        }

        private static readonly int MaxColumns = 7;

        public static void GetTypeDisplay(TypeSymbol type, List<ClassifiedText> texts, bool useName = false, bool nested = false)
        {
            switch (type)
            {
                case TableSymbol ts:
                    if (useName && !string.IsNullOrEmpty(ts.Name))
                    {
                        texts.Add(new ClassifiedText(ClassificationKind.Table, ts.Name));
                    }
                    else
                    {
                        texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "("));
                        AddSchemaMembers(ts.Columns, texts);
                        texts.Add(new ClassifiedText(ClassificationKind.Punctuation, ")"));
                    }
                    break;

                case TupleSymbol tus:
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "{"));
                    AddSchemaMembers(tus.Columns, texts);
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "}"));
                    break;

                case ClusterSymbol cs:
                    texts.Add(new ClassifiedText(ClassificationKind.Type, $"cluster('{cs.Name}')"));
                    break;

                case DatabaseSymbol db:
                    texts.Add(new ClassifiedText(ClassificationKind.Type, $"database('{db.Name}')"));
                    break;

                case EntityGroupElementSymbol eges:
                    GetTypeDisplay(eges.UnderlyingSymbol, texts, useName);
                    break;

                case DynamicBagSymbol dbag:
                    texts.Add(new ClassifiedText(ClassificationKind.Type, "dynamic("));
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "{"));
                    AddSchemaMembers(dbag.Properties, texts);
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "}"));
                    texts.Add(new ClassifiedText(ClassificationKind.Type, ")"));
                    break;

                case DynamicArraySymbol darray:
                    if (!nested)
                        texts.Add(new ClassifiedText(ClassificationKind.Type, "dynamic("));
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "["));
                    if (darray.ElementType != ScalarTypes.Dynamic)
                        GetTypeDisplay(darray.ElementType, texts, nested: true);
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "]"));
                    if (!nested)
                        texts.Add(new ClassifiedText(ClassificationKind.Type, ")"));
                    break;

                case DynamicPrimitiveSymbol dprim:
                    if (!nested)
                        texts.Add(new ClassifiedText(ClassificationKind.Type, "dynamic("));
                    GetTypeDisplay(dprim.UnderlyingType, texts, nested: true);
                    if (!nested)
                        texts.Add(new ClassifiedText(ClassificationKind.Type, ")"));
                    break;

                default:
                    if (type != null)
                    {
                        texts.Add(new ClassifiedText(ClassificationKind.Type, type.Name));
                    }
                    break;
            }
        }

        private static void AddSchemaMembers(IReadOnlyList<ColumnSymbol> columns, List<ClassifiedText> texts)
        {
            var maxCol = Math.Min(MaxColumns, columns.Count);
            for (int i = 0; i < maxCol; i++)
            {
                if (i > 0)
                {
                    texts.Add(new ClassifiedText(ClassificationKind.Punctuation, ", "));
                }

                var col = columns[i];
                texts.Add(new ClassifiedText(ClassificationKind.SchemaMember, col.Name));
            }

            if (maxCol < columns.Count)
            {
                texts.Add(new ClassifiedText(ClassificationKind.Punctuation, ", "));
                texts.Add(new ClassifiedText(ClassificationKind.Punctuation, "..."));
            }
        }
    }
}