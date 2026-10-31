using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlScalar = YamlDotNet.Core.Events.Scalar;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// JSON / YAML / XML Formatter - format, minify, validate, and convert. Every format is read into one tree
    /// (<see cref="JsonNode"/>) and written back out, so any pair converts. Portable: no WPF, no I/O.
    /// What a conversion keeps: values, order, and nesting. What it can't: YAML comments and anchors, XML comments,
    /// processing instructions, and namespace prefixes (names are kept without their prefix).
    /// </summary>
    public sealed class DataFormatterManager
    {
        public const int MaxDepth = 100;

        private static readonly Regex IntegerPattern = new(@"^[-+]?(0|[1-9][0-9]*)$", RegexOptions.Compiled);
        private static readonly Regex LeadingZeroPattern = new(@"^[-+]?0[0-9]", RegexOptions.Compiled);
        private static readonly Regex FloatPattern = new(@"^[-+]?([0-9]+\.[0-9]*|\.[0-9]+|[0-9]+)([eE][-+]?[0-9]+)?$", RegexOptions.Compiled);

        // =================================================================
        //  Entry points
        // =================================================================

        /// <summary>Guesses the format from the first character: { or [ is JSON, &lt; is XML, anything else YAML.</summary>
        public static DataFormatKind Detect(string text)
        {
            string t = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
            if (t.Length == 0) return DataFormatKind.Json;
            return t[0] switch
            {
                '{' or '[' => DataFormatKind.Json,
                '<' => DataFormatKind.Xml,
                _ => DataFormatKind.Yaml,
            };
        }

        /// <summary>Reads <paramref name="text"/> as <paramref name="from"/> and writes it as <paramref name="to"/> (the same format = reformat).</summary>
        public static DataFormatterResult Run(string text, DataFormatKind from, DataFormatKind to, DataFormatterAction action, DataFormatterIndent indent)
        {
            if (string.IsNullOrWhiteSpace(text)) return DataFormatterResult.Fail(from, "");

            JsonNode? tree;
            try
            {
                tree = Read(text, from);
            }
            catch (Exception ex) when (ex is JsonException or XmlException or YamlException or FormatException)
            {
                var (line, column) = Position(ex);
                return DataFormatterResult.Fail(from, MessageOf(ex), line, column);
            }
            catch (InvalidOperationException)
            {
                // YamlDotNet's scanner throws this (not a YamlException) for some malformed input, such as an unclosed [ list.
                return DataFormatterResult.Fail(from, "The text isn't valid " + from.ToString().ToUpperInvariant() + ".");
            }

            try
            {
                string output = Write(tree, to, action == DataFormatterAction.Minify, indent);
                return new DataFormatterResult { Ok = true, Text = output, Source = from };
            }
            catch (Exception ex) when (ex is JsonException or XmlException or YamlException or FormatException or ArgumentException or InvalidOperationException)
            {
                return DataFormatterResult.Fail(from, ex.Message);
            }
        }

        // =================================================================
        //  Reading
        // =================================================================

        private static JsonNode? Read(string text, DataFormatKind kind) => kind switch
        {
            DataFormatKind.Json => JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = MaxDepth }),
            DataFormatKind.Xml => ReadXml(text),
            _ => ReadYaml(text),
        };

        private static JsonNode? ReadXml(string text)
        {
            var doc = XDocument.Parse(text, LoadOptions.None);
            if (doc.Root == null) throw new FormatException("The XML has no root element.");
            return new JsonObject { [doc.Root.Name.LocalName] = ElementToNode(doc.Root, 0) };
        }

        /// <summary>Attributes become "@name" keys, mixed text "#text", repeated child names an array, a text-only element its text, an empty element null.</summary>
        private static JsonNode? ElementToNode(XElement element, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("The XML is nested too deeply.");

            var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
            var children = element.Elements().ToList();
            string text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
            if (children.Count > 0 || attributes.Count > 0) text = text.Trim();

            if (attributes.Count == 0 && children.Count == 0) return text.Length == 0 ? null : JsonValue.Create(text);

            var obj = new JsonObject();
            foreach (var a in attributes) obj["@" + a.Name.LocalName] = a.Value;
            foreach (var group in children.GroupBy(c => c.Name.LocalName))
            {
                var items = group.ToList();
                if (items.Count == 1) obj[group.Key] = ElementToNode(items[0], depth + 1);
                else
                {
                    var array = new JsonArray();
                    foreach (var item in items) array.Add(ElementToNode(item, depth + 1));
                    obj[group.Key] = array;
                }
            }
            if (text.Length > 0) obj["#text"] = text;
            return obj;
        }

        private static JsonNode? ReadYaml(string text)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0) return null;
            if (stream.Documents.Count == 1) return YamlToNode(stream.Documents[0].RootNode, 0);

            var all = new JsonArray();
            foreach (var document in stream.Documents) all.Add(YamlToNode(document.RootNode, 0));
            return all;
        }

        private static JsonNode? YamlToNode(YamlNode node, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("The YAML is nested too deeply (or refers to itself).");
            switch (node)
            {
                case YamlMappingNode map:
                    var obj = new JsonObject();
                    foreach (var pair in map.Children)
                    {
                        string key = pair.Key is YamlScalarNode k ? k.Value ?? "" : pair.Key.ToString();
                        obj[key] = YamlToNode(pair.Value, depth + 1);   // a repeated key keeps the last value, like most parsers
                    }
                    return obj;
                case YamlSequenceNode seq:
                    var array = new JsonArray();
                    foreach (var item in seq.Children) array.Add(YamlToNode(item, depth + 1));
                    return array;
                case YamlScalarNode scalar:
                    return Scalar(scalar);
                default:
                    return null;
            }
        }

        /// <summary>Plain (unquoted) scalars are typed like YAML's core schema; quoted ones are always text. A number with leading zeros stays text so nothing is silently changed.</summary>
        private static JsonNode? Scalar(YamlScalarNode scalar)
        {
            string value = scalar.Value ?? "";
            return scalar.Style == ScalarStyle.Plain ? Infer(value) : JsonValue.Create(value);
        }

        /// <summary>The value an unquoted YAML scalar stands for: null, true/false, a number, or else the text itself.</summary>
        private static JsonNode? Infer(string value)
        {
            if (value.Length == 0 || value is "~" or "null" or "Null" or "NULL") return null;
            if (value is "true" or "True" or "TRUE") return JsonValue.Create(true);
            if (value is "false" or "False" or "FALSE") return JsonValue.Create(false);
            if (LeadingZeroPattern.IsMatch(value)) return JsonValue.Create(value);   // 007, 0123: keep as text rather than change it to 7

            if (IntegerPattern.IsMatch(value))
            {
                if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole)) return JsonValue.Create(whole);
                // Too big for a long: a decimal keeps every digit up to 28; beyond that it stays text instead of losing digits.
                return decimal.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal big) ? JsonValue.Create(big) : JsonValue.Create(value);
            }
            if (FloatPattern.IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) && double.IsFinite(real)) return JsonValue.Create(real);
            return JsonValue.Create(value);
        }

        // =================================================================
        //  Writing
        // =================================================================

        private static string Write(JsonNode? tree, DataFormatKind kind, bool minify, DataFormatterIndent indent) => kind switch
        {
            DataFormatKind.Json => WriteJson(tree, minify, indent),
            DataFormatKind.Xml => WriteXml(tree, minify, indent),
            _ => WriteYaml(tree),
        };

        private static string WriteJson(JsonNode? tree, bool minify, DataFormatterIndent indent)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = !minify,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                IndentCharacter = indent == DataFormatterIndent.Tab ? '\t' : ' ',
                IndentSize = indent switch { DataFormatterIndent.Tab => 1, DataFormatterIndent.FourSpaces => 4, _ => 2 },
            };
            return tree == null ? "null" : tree.ToJsonString(options);
        }

        /// <summary>
        /// Written with the YAML emitter directly so null is "null" (not an empty value) and a string is only quoted when
        /// plain text would read back as something else: "42", "true", "007", "~" keep their quotes, ordinary words don't.
        /// </summary>
        private static string WriteYaml(JsonNode? tree)
        {
            var writer = new StringWriter();
            var emitter = new Emitter(writer, EmitterSettings.Default.WithBestIndent(2).WithBestWidth(int.MaxValue).WithIndentedSequences());
            emitter.Emit(new StreamStart());
            emitter.Emit(new DocumentStart());
            EmitNode(emitter, tree, 0);
            emitter.Emit(new DocumentEnd(true));
            emitter.Emit(new StreamEnd());
            return writer.ToString().ReplaceLineEndings().TrimEnd('\r', '\n') + Environment.NewLine;
        }

        private static void EmitNode(Emitter emitter, JsonNode? node, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("The data is nested too deeply.");
            switch (node)
            {
                case null:
                    emitter.Emit(new YamlScalar(AnchorName.Empty, TagName.Empty, "null", ScalarStyle.Plain, true, false));
                    break;
                case JsonObject obj:
                    emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, obj.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
                    foreach (var pair in obj)
                    {
                        EmitText(emitter, pair.Key);
                        EmitNode(emitter, pair.Value, depth + 1);
                    }
                    emitter.Emit(new MappingEnd());
                    break;
                case JsonArray array:
                    emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, array.Count == 0 ? SequenceStyle.Flow : SequenceStyle.Block));
                    foreach (var item in array) EmitNode(emitter, item, depth + 1);
                    emitter.Emit(new SequenceEnd());
                    break;
                case JsonValue value:
                    switch (value.GetValueKind())
                    {
                        case JsonValueKind.True: emitter.Emit(new YamlScalar(AnchorName.Empty, TagName.Empty, "true", ScalarStyle.Plain, true, false)); break;
                        case JsonValueKind.False: emitter.Emit(new YamlScalar(AnchorName.Empty, TagName.Empty, "false", ScalarStyle.Plain, true, false)); break;
                        case JsonValueKind.Number: emitter.Emit(new YamlScalar(AnchorName.Empty, TagName.Empty, value.ToJsonString(), ScalarStyle.Plain, true, false)); break;
                        default: EmitText(emitter, StringOf(value)); break;
                    }
                    break;
            }
        }

        private static void EmitText(Emitter emitter, string text)
        {
            var style = text.Contains('\n') ? ScalarStyle.Literal : NeedsQuotes(text) ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain;
            emitter.Emit(new YamlScalar(AnchorName.Empty, TagName.Empty, text, style, style == ScalarStyle.Plain, style != ScalarStyle.Plain));
        }

        /// <summary>
        /// True when the text, written plain, would be read back as null, a boolean, or a number. Numbers with leading zeros
        /// count too: YAML 1.2 reads 007 as 7 and older parsers as octal, so it is always quoted.
        /// </summary>
        private static bool NeedsQuotes(string text) =>
            LeadingZeroPattern.IsMatch(text) || Infer(text) is not JsonValue inferred || !inferred.TryGetValue(out string? same) || same != text;

        /// <summary>The text of a string value, whether it came from JSON text (a JsonElement) or from XML / YAML (a plain string).</summary>
        private static string StringOf(JsonValue value)
        {
            if (value.TryGetValue(out string? text)) return text ?? "";
            if (value.TryGetValue(out JsonElement element) && element.ValueKind == JsonValueKind.String) return element.GetString() ?? "";
            return value.ToString();
        }

        private static string WriteXml(JsonNode? tree, bool minify, DataFormatterIndent indent)
        {
            XElement root = RootElement(tree);
            var settings = new XmlWriterSettings
            {
                Indent = !minify,
                IndentChars = indent switch { DataFormatterIndent.Tab => "\t", DataFormatterIndent.FourSpaces => "    ", _ => "  " },
                OmitXmlDeclaration = true,
                NewLineHandling = NewLineHandling.Replace,
            };
            var builder = new StringBuilder();
            using (var writer = XmlWriter.Create(builder, settings)) root.WriteTo(writer);
            return builder.ToString();
        }

        /// <summary>One top-level object key (not an attribute) is the root element; anything else is wrapped in &lt;root&gt;.</summary>
        private static XElement RootElement(JsonNode? tree)
        {
            if (tree is JsonObject obj)
            {
                var elementKeys = obj.Where(p => !p.Key.StartsWith('@') && p.Key != "#text").ToList();
                if (elementKeys.Count == 1 && obj.Count == 1 && elementKeys[0].Value is not JsonArray)
                    return NodeToElement(elementKeys[0].Key, elementKeys[0].Value, 0);
            }
            return NodeToElement("root", tree, 0);
        }

        private static XElement NodeToElement(string name, JsonNode? node, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("The data is nested too deeply.");
            var element = new XElement(XmlConvert.EncodeLocalName(name));
            switch (node)
            {
                case JsonObject obj:
                    foreach (var pair in obj)
                    {
                        if (pair.Key.StartsWith('@') && pair.Key.Length > 1) element.SetAttributeValue(XmlConvert.EncodeLocalName(pair.Key[1..]), TextOf(pair.Value));
                        else if (pair.Key == "#text") element.Add(new XText(TextOf(pair.Value)));
                        else if (pair.Value is JsonArray list) foreach (var item in list) element.Add(NodeToElement(pair.Key, item, depth + 1));
                        else element.Add(NodeToElement(pair.Key, pair.Value, depth + 1));
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array) element.Add(NodeToElement("item", item, depth + 1));
                    break;
                default:
                    string text = TextOf(node);
                    if (text.Length > 0) element.Add(new XText(text));
                    break;
            }
            return element;
        }

        private static string TextOf(JsonNode? node) => node switch
        {
            null => "",
            JsonValue v when v.GetValueKind() == JsonValueKind.String => StringOf(v),
            JsonValue v => v.ToJsonString(),
            _ => node.ToJsonString(),
        };

        // =================================================================
        //  Errors
        // =================================================================

        private static (int Line, int Column) Position(Exception ex) => ex switch
        {
            JsonException { LineNumber: { } line, BytePositionInLine: { } column } => ((int)line + 1, (int)column + 1),
            XmlException x => (x.LineNumber, x.LinePosition),
            YamlException y => ((int)y.Start.Line, (int)y.Start.Column),
            _ => (0, 0),
        };

        /// <summary>The parser's message without its own "Line x, position y" suffix, since the view shows the position itself.</summary>
        private static string MessageOf(Exception ex)
        {
            string message = ex switch
            {
                XmlException x => x.Message,
                YamlException y => y.InnerException?.Message ?? y.Message,
                JsonException j => j.Message,
                _ => ex.Message,
            };
            int cut = message.IndexOf(" Line ", StringComparison.Ordinal);
            if (cut > 0 && ex is XmlException) message = message[..cut];
            int path = message.IndexOf(" Path:", StringComparison.Ordinal);
            if (path > 0) message = message[..path];
            return message.Trim();
        }
    }
}
