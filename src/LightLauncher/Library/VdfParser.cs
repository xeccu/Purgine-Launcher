using System.Text;

namespace LightLauncher.Library;

/// <summary>Nodo de un documento KeyValues (VDF/ACF) de Valve. Claves sin distinguir mayúsculas.</summary>
public sealed class VdfNode
{
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode? this[string key] => Children.TryGetValue(key, out var node) ? node : null;

    public string? GetValue(string key) => Values.TryGetValue(key, out var value) ? value : null;
}

/// <summary>
/// Parser del formato de texto KeyValues de Valve, usado por <c>libraryfolders.vdf</c> y
/// <c>appmanifest_*.acf</c>. Soporta cadenas entre comillas con escapes, tokens sin comillas,
/// comentarios <c>//</c> y condicionales de plataforma (<c>[$WIN32]</c>), que se ignoran.
/// </summary>
public static class VdfParser
{
    private enum TokenKind { End, String, OpenBrace, CloseBrace, Conditional }

    private readonly record struct Token(TokenKind Kind, string Text);

    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path));

    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        int pos = 0;
        ParseObject(text, ref pos, root, isRoot: true);
        return root;
    }

    private static void ParseObject(string text, ref int pos, VdfNode node, bool isRoot)
    {
        while (true)
        {
            var key = NextToken(text, ref pos);
            switch (key.Kind)
            {
                case TokenKind.End:
                    return; // tolerante con archivos truncados
                case TokenKind.CloseBrace:
                    if (isRoot) continue; // llave sobrante: ignorar
                    return;
                case TokenKind.Conditional:
                case TokenKind.OpenBrace:
                    continue; // basura inesperada: saltar
            }

            var value = NextToken(text, ref pos);
            if (value.Kind == TokenKind.Conditional)
                value = NextToken(text, ref pos);

            if (value.Kind == TokenKind.OpenBrace)
            {
                var child = new VdfNode();
                ParseObject(text, ref pos, child, isRoot: false);
                node.Children[key.Text] = child;
            }
            else if (value.Kind == TokenKind.String)
            {
                node.Values[key.Text] = value.Text;
                SkipConditional(text, ref pos);
            }
            else
            {
                return; // fin de archivo o '}' inesperado tras una clave
            }
        }
    }

    private static void SkipConditional(string text, ref int pos)
    {
        int save = pos;
        if (NextToken(text, ref pos).Kind != TokenKind.Conditional)
            pos = save;
    }

    private static Token NextToken(string text, ref int pos)
    {
        SkipWhitespaceAndComments(text, ref pos);
        if (pos >= text.Length) return new Token(TokenKind.End, string.Empty);

        char c = text[pos];
        switch (c)
        {
            case '{': pos++; return new Token(TokenKind.OpenBrace, "{");
            case '}': pos++; return new Token(TokenKind.CloseBrace, "}");
            case '"': return new Token(TokenKind.String, ReadQuoted(text, ref pos));
            case '[':
            {
                int end = text.IndexOf(']', pos);
                pos = end < 0 ? text.Length : end + 1;
                return new Token(TokenKind.Conditional, string.Empty);
            }
            default:
            {
                int start = pos;
                while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"'))
                    pos++;
                return new Token(TokenKind.String, text[start..pos]);
            }
        }
    }

    private static string ReadQuoted(string text, ref int pos)
    {
        pos++; // comilla de apertura
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            char c = text[pos++];
            if (c == '"') break;
            if (c == '\\' && pos < text.Length)
            {
                char e = text[pos++];
                sb.Append(e switch
                {
                    'n' => '\n',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => e,
                });
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void SkipWhitespaceAndComments(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos])) { pos++; continue; }
            if (text[pos] == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                int nl = text.IndexOf('\n', pos);
                pos = nl < 0 ? text.Length : nl + 1;
                continue;
            }
            break;
        }
    }
}
