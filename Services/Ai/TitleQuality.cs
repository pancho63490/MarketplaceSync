using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarketplaceSync.Services.Ai;

/// <summary>Un problema del título: <c>block</c> (Mercado Libre lo rechaza o castiga) o <c>warn</c> (le quita ventas).</summary>
public sealed record TitleIssue(string Severity, string Message)
{
    public bool Blocks => Severity == "block";
}

/// <summary>
/// Revisa títulos de Mercado Libre México: largo, palabras prohibidas (réplica, imitación, datos de contacto),
/// palabras que no aportan o están mal vistas (oferta, gratis, nuevo, original), restos en inglés, términos que
/// no se usan en México (pastillas → balatas, parachoques → defensa), mayúsculas y símbolos.
/// <see cref="Clean"/> corrige sin IA; <see cref="SuggestAsync"/> pide a la IA un título mejor y lo vuelve a limpiar.
/// </summary>
public sealed class TitleQuality
{
    public const int MaxLength = 60;
    public const int MinGoodLength = 25;

    private readonly ClaudeClient _claude;
    private readonly ILogger<TitleQuality> _logger;

    public TitleQuality(ClaudeClient claude, ILogger<TitleQuality> logger)
    {
        _claude = claude;
        _logger = logger;
    }

    // Mercado Libre las prohíbe o las toma como falsificación.
    private static readonly (Regex Pattern, string Message)[] Prohibited =
    {
        (W(@"r[eé]plicas?|imitaci[oó]n(es)?|clon(es)?|pirata|copia\s+(de|fiel)|tipo\s+original|1\s*:\s*1|calidad\s+original|similar\s+a"), "Dice réplica, imitación o «tipo original»: Mercado Libre lo toma como falsificación."),
        (new Regex(@"(\+?52)?[\s(-]*\d{2,3}[\s)-]*\d{3,4}[\s-]*\d{4}", RegexOptions.Compiled), "Trae un teléfono: Mercado Libre prohíbe datos de contacto."),
        (new Regex(@"[\w.+-]+@[\w-]+\.[\w.]+|https?://|www\.|\.com\b|whats\s*app|wa\.me|facebook|instagram", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Trae correo, link o red social: Mercado Libre prohíbe datos de contacto.")
    };

    // No aportan en la búsqueda o Mercado Libre pide no usarlas (van en otros campos).
    private static readonly (Regex Pattern, string Word)[] NotRecommended =
    {
        (W(@"ofertas?|promoci[oó]n|descuentos?|rebajas?|liquidaci[oó]n|remate|ganga"), "oferta/promoción"),
        (W(@"env[ií]o\s+gratis|gratis|env[ií]o\s+inmediato|env[ií]o\s+r[aá]pido|entrega\s+inmediata"), "envío gratis"),
        (W(@"barat[oa]s?|el\s+mejor|mejor\s+precio|precio\s+de\s+mayoreo|mayoreo|s[uú]per\s+precio"), "barato/mejor precio"),
        (W(@"nuev[oa]s?|nuevecit[oa]"), "nuevo (la condición ya va aparte)"),
        (W(@"original(es)?|genuin[oa]s?|100\s*%"), "original/100%"),
        (W(@"garant[ií]a|garantizad[oa]|calidad\s+premium|alta\s+calidad"), "garantía/calidad"),
        (W(@"msi|meses\s+sin\s+intereses"), "meses sin intereses")
    };

    // Español de España u otros países → como se busca en México.
    private static readonly (string From, string To)[] Mexican =
    {
        ("pastillas de freno", "balatas"), ("pastillas de frenos", "balatas"), ("pastillas", "balatas"),
        ("parachoques", "defensa"), ("paragolpes", "defensa"), ("capó", "cofre"), ("capo", "cofre"), ("capot", "cofre"),
        ("motor de arranque", "marcha"), ("arrancador", "marcha"), ("guardabarros", "salpicadera"), ("guardafangos", "salpicadera"),
        ("silenciador", "mofle"), ("rodamientos", "baleros"), ("rodamiento", "balero"), ("cubo de rueda", "maza"),
        ("luz trasera", "calavera"), ("luces traseras", "calaveras"), ("piloto trasero", "calavera"), ("pilotos traseros", "calaveras"),
        ("faro trasero", "calavera"), ("faros traseros", "calaveras"), ("faro antiniebla", "faro de niebla"), ("faros antiniebla", "faros de niebla"),
        ("correa de distribución", "banda de distribución"), ("correa serpentina", "banda serpentina"), ("correa del alternador", "banda del alternador"),
        ("correa de accesorios", "banda de accesorios"), ("neumáticos", "llantas"), ("neumático", "llanta"),
        ("filtro de combustible", "filtro de gasolina"), ("bomba de combustible", "bomba de gasolina"),
        ("kit de embrague", "kit de clutch"), ("embrague", "clutch"), ("lado conductor", "lado piloto"), ("lado del conductor", "lado piloto"),
        ("lado pasajero", "lado copiloto"), ("lado del pasajero", "lado copiloto"), ("matrícula", "placa"), ("maletero", "cajuela"),
        ("coche", "auto"), ("furgoneta", "camioneta"), ("teléfono móvil", "celular"), ("ordenador", "computadora"),
        ("bombilla", "foco"), ("bombillas", "focos"), ("bujías de encendido", "bujías"),
        ("limpiaparabrisas", "plumas limpiaparabrisas"), ("escobillas", "plumas limpiaparabrisas")
    };

    // Palabras inglesas de piezas que no deberían quedar (marcas como "Monster Energy" no están aquí).
    private static readonly HashSet<string> EnglishLeftovers = new(StringComparer.OrdinalIgnoreCase)
    {
        "for", "with", "fits", "and", "front", "rear", "left", "right", "driver", "passenger", "pump", "brake", "pads",
        "headlight", "headlights", "taillight", "taillights", "tail", "lamp", "mirror", "assembly", "replacement", "pair",
        "upper", "lower", "bearing", "hub", "door", "handle", "bumper", "hood", "fender", "grille", "starter", "wheel",
        "strut", "struts", "shock", "shocks", "spark", "plug", "plugs", "belt", "gasket", "hose", "valve", "radiator",
        "water", "fuel", "oil", "filter", "cover", "light", "lights", "bulb", "bulbs", "piece", "pcs", "side", "new"
    };

    private static readonly string[] LowerWords = { "de", "del", "la", "el", "las", "los", "para", "con", "y", "e", "en", "a", "o", "al", "sin", "por" };
    private static readonly string[] UpperTokens = { "led", "abs", "oem", "ac", "a/c", "gm", "vw", "bmw", "gmc", "ram", "suv", "usb", "hid", "lh", "rh", "maf", "egr", "pcv", "cv", "tpms", "4x4", "4x2", "awd", "4wd", "2wd", "v6", "v8" };

    private static Regex W(string pattern) => new(@"(?<![\p{L}\p{N}])(" + pattern + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ─────────────────────────────────────────────────────────────
    // Revisar
    // ─────────────────────────────────────────────────────────────

    public static List<TitleIssue> Review(string? title)
    {
        var issues = new List<TitleIssue>();
        var t = (title ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            issues.Add(new("block", "No tiene título."));
            return issues;
        }

        if (t.Length > MaxLength)
            issues.Add(new("block", $"Tiene {t.Length} caracteres; el máximo es {MaxLength}."));
        foreach (var (pattern, message) in Prohibited)
            if (pattern.IsMatch(t) && !LooksLikeYearsOrPart(pattern, t))
                issues.Add(new("block", message));

        var words = NotRecommended.Where(n => n.Pattern.IsMatch(t)).Select(n => n.Word).ToList();
        if (words.Count > 0)
            issues.Add(new("warn", "Sobra: " + string.Join(", ", words) + ". Mercado Libre pide no usarlas y quitan espacio."));

        if (ProductTitleTranslator.LooksEnglish(t))
            issues.Add(new("warn", "Está en inglés: en México se busca en español."));
        else
        {
            var leftovers = Regex.Matches(t, @"[A-Za-z]+").Select(m => m.Value).Where(EnglishLeftovers.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (leftovers.Count > 0)
                issues.Add(new("warn", "Quedan palabras en inglés: " + string.Join(", ", leftovers.Take(4)) + "."));
        }

        var lower = " " + t.ToLowerInvariant() + " ";
        var spain = Mexican.Where(m => Regex.IsMatch(lower, @"(?<![\p{L}])" + Regex.Escape(m.From) + @"(?![\p{L}])")).Take(3).ToList();
        if (spain.Count > 0)
            issues.Add(new("warn", "En México se busca: " + string.Join(", ", spain.Select(m => $"«{m.To}» en vez de «{m.From}»")) + "."));

        var letters = t.Where(char.IsLetter).ToList();
        if (letters.Count > 10 && letters.Count(char.IsUpper) > letters.Count * 0.7)
            issues.Add(new("warn", "Está todo en mayúsculas: se lee como grito y se ve menos profesional."));
        if (Regex.IsMatch(t, @"[!¡*★☆✓✔•|~^<>{}\[\]#]|\p{So}|[-_/.]{3,}"))
            issues.Add(new("warn", "Tiene símbolos o emojis: Mercado Libre los quita o los castiga."));
        if (Regex.Matches(t.ToLowerInvariant(), @"\p{L}{4,}").GroupBy(m => m.Value).Any(g => g.Count() > 1))
            issues.Add(new("warn", "Repite palabras."));
        if (t.Length < MinGoodLength)
            issues.Add(new("warn", "Es muy corto: agrega marca, número de parte o vehículo y años."));
        return issues;
    }

    // "1:1" no debe confundirse con años como "2015-2020" (el patrón de teléfono tampoco).
    private static bool LooksLikeYearsOrPart(Regex pattern, string text)
    {
        if (!pattern.ToString().Contains(@"\d{2,3}"))
            return false;
        var match = pattern.Match(text).Value;
        var digits = new string(match.Where(char.IsDigit).ToArray());
        return digits.Length != 10 && digits.Length != 12 || Regex.IsMatch(match, @"(19|20)\d{2}\s*-\s*(19|20)\d{2}");
    }

    // ─────────────────────────────────────────────────────────────
    // Corregir sin IA
    // ─────────────────────────────────────────────────────────────

    /// <summary>Título corregido con reglas: español, términos de México, sin palabras de más, sin símbolos, 60 caracteres.</summary>
    public static string Clean(string? title)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length == 0)
            return t;
        if (ProductTitleTranslator.LooksEnglish(t))
            t = ProductTitleTranslator.Translate(t);

        foreach (var (pattern, _) in Prohibited)
            t = pattern.Replace(t, " ");
        foreach (var (pattern, _) in NotRecommended)
            t = pattern.Replace(t, " ");

        foreach (var (from, to) in Mexican)
            t = Regex.Replace(t, @"(?<![\p{L}])" + Regex.Escape(from) + @"(?![\p{L}])", m => MatchCase(m.Value, to), RegexOptions.IgnoreCase);

        t = Regex.Replace(t, @"[!¡*★☆✓✔•|~^<>{}\[\]#]|\p{So}", " ");
        t = Regex.Replace(t, @"[-_/.]{3,}", " ");
        t = Regex.Replace(t, @"\s+([,.;:])", "$1");
        t = Regex.Replace(t, @"\s{2,}", " ").Trim(' ', ',', '-', '.', ';', ':');

        var letters = t.Where(char.IsLetter).ToList();
        if (letters.Count > 10 && letters.Count(char.IsUpper) > letters.Count * 0.7)
            t = TitleCase(t);
        t = RemoveRepeatedWords(t);
        return ProductTitleTranslator.Fit(t);
    }

    private static string MatchCase(string original, string replacement) =>
        original.Length > 0 && char.IsUpper(original[0]) ? char.ToUpper(replacement[0], CultureInfo.GetCultureInfo("es-MX")) + replacement[1..] : replacement;

    private static string TitleCase(string text)
    {
        var mx = CultureInfo.GetCultureInfo("es-MX");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            var lower = w.ToLower(mx);
            string fixedWord;
            if (w.Any(char.IsDigit) || UpperTokens.Contains(lower)) fixedWord = w.ToUpper(mx);
            else if (i > 0 && LowerWords.Contains(lower)) fixedWord = lower;
            else fixedWord = char.ToUpper(lower[0], mx) + lower[1..];
            if (result.Length > 0) result.Append(' ');
            result.Append(fixedWord);
        }
        return result.ToString();
    }

    private static string RemoveRepeatedWords(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Where(w => w.Length < 4 || w.Any(char.IsDigit) || seen.Add(w.Trim(',', '.'))).ToList();
        return string.Join(' ', kept);
    }

    // ─────────────────────────────────────────────────────────────
    // Con IA
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Pide a la IA un título claro en español de México con los datos que hay (sin inventar) y lo limpia con las reglas.
    /// Sin IA regresa <see cref="Clean"/>.
    /// </summary>
    public async Task<string> SuggestAsync(string? title, string? brand, string? model, string? description, CancellationToken cancellationToken = default)
    {
        var cleaned = Clean(title);
        if (!_claude.IsConfigured || string.IsNullOrWhiteSpace(title))
            return cleaned;
        try
        {
            var prompt = $"""
                Título actual: {title}
                Marca: {brand ?? "N/D"}
                Modelo o número de parte: {model ?? "N/D"}
                Descripción (fragmento): {Short(description, 600) ?? "N/D"}
                """;
            var reply = await _claude.CompleteAsync(
                """
                Escribes títulos para Mercado Libre México. Reglas:
                - Español de México, como busca el comprador: balatas, maza, balero, calavera, faro, defensa, cofre, salpicadera,
                  mofle, marcha, banda, rótula, terminal, plumas limpiaparabrisas, lado piloto / lado copiloto.
                - Orden: producto + marca + modelo o número de parte + vehículo/aplicación + años o medida.
                - Máximo 60 caracteres. Primera letra de cada palabra en mayúscula (salvo de, para, con, y).
                - Solo datos que vienen en el título, marca, modelo o descripción. No inventes años, vehículos ni medidas.
                - Prohibido: original, réplica, tipo, nuevo, oferta, gratis, envío, garantía, calidad, mejor, barato, %,
                  símbolos, emojis, teléfonos o links.
                Responde solo con el título.
                """,
                prompt, 120, 0.2, cancellationToken);
            var line = (reply ?? string.Empty).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().Trim('"', '“', '”', '*', '.') ?? string.Empty;
            line = Regex.Replace(line, @"^(t[ií]tulo|title)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
            var suggestion = Clean(line);
            // Si la IA dejó algo bloqueante, muy corto o que no habla del mismo producto, se queda la versión con reglas.
            return suggestion.Length >= 15 && !Review(suggestion).Any(i => i.Blocks) && SharesKeyTerms(suggestion, title, brand, model)
                ? suggestion
                : cleaned;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "AI title suggestion failed.");
            return cleaned;
        }
    }

    /// <summary>
    /// La sugerencia debe conservar algo que identifique al producto: marca, modelo o número de parte, un año
    /// o alguna palabra distintiva del título original (no "de", "para"…). Si no, la IA habló de otra cosa.
    /// </summary>
    public static bool SharesKeyTerms(string suggestion, string original, string? brand, string? model)
    {
        static IEnumerable<string> Tokens(string? text) =>
            Regex.Matches(RemoveAccents(text ?? string.Empty).ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}\-]{2,}").Select(m => m.Value);
        var stop = new HashSet<string> { "para", "con", "del", "las", "los", "par", "kit", "juego", "lado", "nuevo", "original", "the", "and", "for", "with" };
        var key = Tokens(original).Concat(Tokens(brand)).Concat(Tokens(model)).Where(t => !stop.Contains(t)).ToHashSet();
        var translated = Tokens(Clean(original)).Where(t => !stop.Contains(t));
        key.UnionWith(translated);
        return Tokens(suggestion).Any(key.Contains);
    }

    private static string RemoveAccents(string text) =>
        new string(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());

    private static string? Short(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length > max ? text[..max] : text;
}
