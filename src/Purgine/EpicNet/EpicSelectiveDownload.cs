using System.Globalization;
using System.Text.Json;

namespace Purgine.EpicNet;

/// <summary>
/// Una opción del selector de descarga de Epic, tal como la muestra Epic Games Launcher: nombre y descripción en el
/// idioma del sistema, si viene marcada y las etiquetas de instalación que añade. <see cref="Group"/>: nombre del
/// grupo al que pertenece (p. ej. «Idiomas adicionales»), o null.
/// </summary>
public sealed record EpicSdOption(string Id, string Name, string Description, string? Group, bool DefaultSelected, IReadOnlyList<string> Tags);

/// <summary>
/// Descarga selectiva de Epic («SDMeta»): el archivo que Epic publica para cada build de los juegos que la admiten
/// (Fortnite, ARK: Survival Ascended, Fallout…), con las opciones exactas que enseña Epic Games Launcher:
/// <list type="bullet">
///   <item>Obligatorias (<c>IsRequired</c>): no se enseñan y se instalan siempre.</item>
///   <item>Opcionales: con su nombre traducido y si vienen marcadas (<c>IsDefaultSelected</c> o una expresión).</item>
///   <item>Grupos (<c>Children</c>), p. ej. idiomas de voces: por defecto, el del sistema.</item>
///   <item>Invisibles: no se enseñan; Epic las añade según las demás (<c>InvisibleSelectedExpression</c>).</item>
/// </list>
/// Los archivos del manifiesto sin etiqueta van siempre; los de etiquetas que ninguna opción elegida menciona no se
/// instalan (en Fortnite, el contenido que el juego descarga bajo demanda). Los juegos sin SDMeta se instalan
/// completos, como hace Epic.
/// </summary>
public sealed class EpicSelectiveDownload
{
    private sealed class Node
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Description = string.Empty;
        public string? Group;
        public bool Required;
        public bool Invisible;
        public bool DefaultSelected;
        public string? DefaultExpression;
        public string? InvisibleExpression;
        public string? ConfigHandler;
        public string? ConfigValue;
        public List<string> Tags = [];
    }

    private readonly List<Node> _nodes = [];
    private readonly Func<string, bool> _hasAccess;

    private EpicSelectiveDownload(Func<string, bool> hasAccess) => _hasAccess = hasAccess;

    /// <summary>
    /// Lee el SDMeta. <paramref name="hasAccess"/>: ¿posee la cuenta este artículo del catálogo? (para opciones como
    /// «Salvar el mundo, si lo tienes»). Null si el archivo no tiene opciones.
    /// </summary>
    public static EpicSelectiveDownload? Parse(string json, Func<string, bool>? hasAccess = null)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        var sd = new EpicSelectiveDownload(hasAccess ?? (_ => false));
        foreach (var o in data.EnumerateArray()) sd.Add(o, group: null, groupHandler: null);
        return sd._nodes.Count == 0 ? null : sd;
    }

    private void Add(JsonElement o, string? group, string? groupHandler)
    {
        var node = new Node
        {
            Id = Str(o, "UniqueId") ?? Guid.NewGuid().ToString("N"),
            Name = Localized(o, "Title") ?? string.Empty,
            Description = Localized(o, "Description") ?? string.Empty,
            Group = group,
            Required = Bool(o, "IsRequired"),
            Invisible = Bool(o, "Invisible"),
            DefaultSelected = Bool(o, "IsDefaultSelected"),
            DefaultExpression = Str(o, "DefaultSelectedExpression"),
            InvisibleExpression = Str(o, "InvisibleSelectedExpression"),
            ConfigHandler = groupHandler,
            ConfigValue = Str(o, "ConfigValue"),
            Tags = o.TryGetProperty("Tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Select(t => t.GetString()).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList()
                : [],
        };
        if (o.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            // El grupo en sí solo es un título; sus hijos son las opciones (heredan si el grupo es obligatorio).
            if (node.Tags.Count > 0) _nodes.Add(node);
            foreach (var c in children.EnumerateArray())
            {
                var before = _nodes.Count;
                Add(c, node.Name, Str(o, "ConfigHandler"));
                if (node.Required) for (var i = before; i < _nodes.Count; i++) _nodes[i].Required = true;
            }
            return;
        }
        _nodes.Add(node);
    }

    /// <summary>Opciones que se enseñan (ni obligatorias ni invisibles), con su estado por defecto.</summary>
    public IReadOnlyList<EpicSdOption> VisibleOptions =>
        _nodes.Where(n => !n.Required && !n.Invisible)
            .Select(n => new EpicSdOption(n.Id, n.Name, n.Description, n.Group, IsDefault(n), n.Tags))
            .ToList();

    /// <summary>Lo que Epic marca por defecto (ids de las opciones visibles).</summary>
    public IReadOnlyCollection<string> DefaultSelection => VisibleOptions.Where(o => o.DefaultSelected).Select(o => o.Id).ToList();

    /// <summary>
    /// Etiquetas que se instalan con estas opciones visibles marcadas: las obligatorias, las marcadas y las invisibles
    /// que Epic añade según lo marcado. (Los archivos sin etiqueta del manifiesto se instalan siempre.)
    /// </summary>
    public IReadOnlyCollection<string> TagsFor(IEnumerable<string> selectedIds)
    {
        var selected = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
        foreach (var n in _nodes.Where(n => n.Required)) selected.Add(n.Id);
        foreach (var n in _nodes.Where(n => n.Invisible && !n.Required))
            if (n.InvisibleExpression is { Length: > 0 } expr && Evaluate(expr, selected)) selected.Add(n.Id);
        return _nodes.Where(n => selected.Contains(n.Id)).SelectMany(n => n.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool IsDefault(Node n)
    {
        if (n.DefaultExpression is { Length: > 0 } expr) return Evaluate(expr, []);
        // Grupo de idiomas: el del sistema (como Epic Games Launcher).
        if (string.Equals(n.ConfigHandler, "DefaultLanguage", StringComparison.OrdinalIgnoreCase) && n.ConfigValue is { } lang)
        {
            var ui = CultureInfo.CurrentUICulture;
            return string.Equals(lang, ui.Name, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(lang, ui.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase);
        }
        return n.DefaultSelected;
    }

    // =====================================================================================
    // Expresiones de Epic: IsComponentSelected(id), HasAccess(id), true/false, !, &&, ||, paréntesis.
    // Lo que no se entiende cuenta como falso (no se instala nada que Epic no haya pedido claramente).
    // =====================================================================================

    private bool Evaluate(string expr, HashSet<string> selected)
    {
        try
        {
            var pos = 0;
            var result = ParseOr(expr, ref pos, selected);
            return result;
        }
        catch (Exception) { return false; }
    }

    private bool ParseOr(string s, ref int p, HashSet<string> sel)
    {
        var v = ParseAnd(s, ref p, sel);
        while (Skip(s, ref p, "||")) v = ParseAnd(s, ref p, sel) | v;
        return v;
    }

    private bool ParseAnd(string s, ref int p, HashSet<string> sel)
    {
        var v = ParseUnary(s, ref p, sel);
        while (Skip(s, ref p, "&&")) v = ParseUnary(s, ref p, sel) & v;
        return v;
    }

    private bool ParseUnary(string s, ref int p, HashSet<string> sel)
    {
        if (Skip(s, ref p, "!")) return !ParseUnary(s, ref p, sel);
        if (Skip(s, ref p, "("))
        {
            var v = ParseOr(s, ref p, sel);
            Skip(s, ref p, ")");
            return v;
        }
        SkipSpaces(s, ref p);
        var start = p;
        while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] == '_')) p++;
        var name = s[start..p];
        string? arg = null;
        if (Skip(s, ref p, "("))
        {
            var close = s.IndexOf(')', p);
            arg = s[p..close].Trim();
            p = close + 1;
        }
        return name.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            "iscomponentselected" => arg is not null && sel.Contains(arg),
            "hasaccess" => arg is not null && _hasAccess(arg),
            _ => false,
        };
    }

    private static bool Skip(string s, ref int p, string token)
    {
        SkipSpaces(s, ref p);
        if (string.CompareOrdinal(s, p, token, 0, token.Length) != 0) return false;
        p += token.Length;
        return true;
    }

    private static void SkipSpaces(string s, ref int p) { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }

    // =====================================================================================

    private static string? Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static bool Bool(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True ||
            (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Texto en el idioma del sistema (<c>Title_es-ES</c>, <c>Title_es</c>…) o en inglés.</summary>
    private static string? Localized(JsonElement o, string key)
    {
        var ui = CultureInfo.CurrentUICulture;
        return Str(o, $"{key}_{ui.Name}") ?? Str(o, $"{key}_{ui.TwoLetterISOLanguageName}") ?? Str(o, key);
    }
}
