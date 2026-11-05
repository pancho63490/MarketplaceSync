using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarketplaceSync.Services.Ai;

/// <summary>
/// Pasa al español de México los títulos que llegan en inglés (eBay, Amazon, Walmart, RockAuto).
/// Con IA usa Claude; sin IA usa un diccionario de autopartes con términos mexicanos
/// (balatas, maza, calavera, defensa, cofre, marcha, balero…). Marca, número de parte,
/// años y motor se conservan tal cual.
/// </summary>
public sealed class ProductTitleTranslator
{
    public const int MaxTitleLength = 60;

    private readonly ClaudeClient _claude;
    private readonly ILogger<ProductTitleTranslator> _logger;

    public ProductTitleTranslator(ClaudeClient claude, ILogger<ProductTitleTranslator> logger)
    {
        _claude = claude;
        _logger = logger;
    }

    /// <summary>Devuelve el título en español. Si ya está en español lo regresa igual.</summary>
    public async Task<string?> ToSpanishAsync(string? title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || !LooksEnglish(title))
            return title;

        if (_claude.IsConfigured)
        {
            try
            {
                var translated = await _claude.CompleteAsync(
                    """
                    Traduces títulos de productos (sobre todo autopartes) del inglés al español de México para Mercado Libre.
                    Formato: [pieza o producto] + [marca] + [número de parte] + [vehículo / aplicación] + [años o motor].
                    Usa los términos que se usan en México: balatas, discos de freno, maza, balero, rótula, terminal de dirección,
                    calavera, faro, defensa, cofre, salpicadera, mofle, marcha, banda, polea loca, soporte de motor, flecha homocinética,
                    amortiguador, plumas limpiaparabrisas, tapetes, birlos, estribos, lado piloto / lado copiloto.
                    Conserva marca, modelo, números de parte, años y medidas exactamente como vienen. No agregues datos.
                    Máximo 60 caracteres. Sin palabras como oferta, gratis, original, nuevo, envío. Responde solo con el título.
                    """,
                    title.Trim(),
                    120,
                    0.1,
                    cancellationToken);

                var clean = CleanAiTitle(translated);
                // Las marcas pueden ser palabras en inglés ("Monster Energy"): basta con que el resto venga en español.
                if (clean is not null && (!LooksEnglish(clean) || CountSpanish(clean) >= 2))
                    return clean;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "AI title translation failed; using dictionary.");
            }
        }

        return Translate(title);
    }

    private static string? CleanAiTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var line = value.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim().Trim('"', '“', '”', '*', '.');
        line = Regex.Replace(line, @"^(t[ií]tulo|title)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
        if (line.Length < 4)
            return null;
        return FitTitle(line);
    }

    // ─────────────────────────────────────────────────────────────
    // Detección de idioma
    // ─────────────────────────────────────────────────────────────

    private static readonly HashSet<string> EnglishMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "for", "with", "fits", "fit", "and", "the", "of", "front", "rear", "left", "right", "driver", "passenger",
        "side", "pump", "filter", "pads", "pad", "brake", "brakes", "light", "lights", "lamp", "mirror", "water",
        "oil", "air", "fuel", "assembly", "replacement", "pair", "set", "upper", "lower", "wheel", "bearing",
        "hub", "sensor", "spark", "plug", "plugs", "coil", "belt", "kit", "engine", "mount", "door", "handle",
        "cover", "new", "black", "clear", "chrome", "piece", "pcs", "pack", "tail", "head", "headlight", "headlights",
        "taillight", "taillights", "bumper", "hood", "fender", "grille", "starter", "radiator", "fan", "hose",
        "gasket", "valve", "control", "arm", "joint", "rod", "end", "axle", "strut", "struts", "shock", "shocks",
        "spring", "rotor", "rotors", "caliper", "steering", "power", "ignition", "seat", "floor", "mats", "wiper",
        "blade", "blades", "truck", "car", "heavy", "duty", "performance", "premium", "genuine", "oem", "compatible",
        "oxygen", "speed", "position", "temperature", "housing", "lens", "energy", "sugar", "zero", "drink", "drinks",
        "candy", "flavor", "variety", "count", "oz", "pack", "original", "equipment"
    };

    private static readonly HashSet<string> SpanishMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "para", "con", "de", "del", "la", "el", "las", "los", "y", "bomba", "filtro", "balatas", "juego", "par",
        "delantero", "delantera", "trasero", "trasera", "izquierdo", "derecho", "faro", "calavera", "espejo",
        "maza", "balero", "amortiguador", "sensor", "bujía", "bujias", "bujías", "banda", "soporte", "defensa",
        "cofre", "manija", "piezas", "nuevo", "negro", "sin", "paquete", "azúcar", "sabor", "dulces", "bebida",
        "lado", "piloto", "copiloto", "rueda", "llanta", "luz", "focos", "foco", "agua", "aceite", "aire", "gasolina"
    };

    private static int CountSpanish(string text) =>
        Regex.Matches(text, @"[A-Za-zÁÉÍÓÚÑáéíóúñü]+").Select(m => m.Value)
            .Where(SpanishMarkers.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>True si el título parece estar en inglés.</summary>
    public static bool LooksEnglish(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var words = Regex.Matches(text, @"[A-Za-zÁÉÍÓÚÑáéíóúñü]+").Select(m => m.Value).ToList();
        var english = words.Where(EnglishMarkers.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var spanish = words.Where(SpanishMarkers.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        // "sensor" y "kit" existen en ambos idiomas; se pide al menos dos palabras inglesas y más que españolas.
        if (english >= 2 && english > spanish)
            return true;

        // Títulos cortos ("Denso 234-4209 Oxygen Sensor"): basta con una pieza escrita en inglés.
        var lower = " " + text.ToLowerInvariant() + " ";
        return spanish == 0 && Nouns.Any(n => n.English.Contains(' ') || !AmbiguousNouns.Contains(n.English)
            ? Regex.IsMatch(lower, $@"(?<![\w-]){Regex.Escape(n.English)}(?![\w-])") && !AmbiguousNouns.Contains(n.English)
            : false);
    }

    // Palabras que se escriben igual en español.
    private static readonly HashSet<string> AmbiguousNouns = new(StringComparer.OrdinalIgnoreCase) { "sensor", "rotor", "motor" };

    // Palabras en inglés que no aportan en un título de Mercado Libre.
    private static readonly string[] Filler =
    {
        "original equipment", "oe replacement", "oem replacement", "direct fit", "plug and play", "plug & play",
        "housing", "lens", "lamp", "oe", "brand", "quality", "durable", "easy install", "easy installation"
    };

    // ─────────────────────────────────────────────────────────────
    // Traducción por diccionario
    // ─────────────────────────────────────────────────────────────

    private sealed record Noun(string Spanish, bool Feminine, bool Plural);

    // Piezas (de la frase más larga a la más corta). Las claves van en minúsculas.
    private static readonly (string English, Noun Noun)[] Nouns = BuildNouns();

    private static (string, Noun)[] BuildNouns()
    {
        var list = new List<(string, Noun)>();
        void M(string en, string es, bool plural = false) => list.Add((en, new Noun(es, false, plural)));
        void F(string en, string es, bool plural = false) => list.Add((en, new Noun(es, true, plural)));

        // Frenos
        F("brake pad set", "balatas", true); F("brake pads", "balatas", true); F("brake pad", "balata");
        F("disc brake pads", "balatas de disco", true);
        M("brake rotors", "discos de freno", true); M("brake rotor", "disco de freno"); M("rotors", "discos de freno", true); M("rotor", "disco de freno");
        M("brake caliper", "caliper de freno"); M("calipers", "calipers de freno", true); M("caliper", "caliper de freno");
        F("brake shoes", "zapatas de freno", true); M("brake drum", "tambor de freno");
        M("brake master cylinder", "cilindro maestro de frenos"); M("master cylinder", "cilindro maestro");
        F("brake hose", "manguera de freno"); M("brake booster", "booster de frenos");
        // Motor y enfriamiento
        F("water pump", "bomba de agua"); F("fuel pump", "bomba de gasolina"); F("oil pump", "bomba de aceite");
        F("power steering pump", "bomba de dirección hidráulica");
        M("cabin air filter", "filtro de aire de cabina"); M("engine air filter", "filtro de aire de motor");
        M("air filter", "filtro de aire"); M("oil filter", "filtro de aceite"); M("fuel filter", "filtro de gasolina");
        M("transmission filter", "filtro de transmisión");
        F("spark plugs", "bujías", true); F("spark plug", "bujía");
        F("ignition coils", "bobinas de encendido", true); F("ignition coil", "bobina de encendido");
        F("coil packs", "bobinas de encendido", true); F("coil pack", "bobina de encendido");
        M("oxygen sensor", "sensor de oxígeno"); M("o2 sensor", "sensor de oxígeno");
        M("mass air flow sensor", "sensor MAF"); M("maf sensor", "sensor MAF");
        M("camshaft position sensor", "sensor de posición del árbol de levas");
        M("crankshaft position sensor", "sensor de posición del cigüeñal");
        M("abs wheel speed sensor", "sensor de velocidad ABS"); M("wheel speed sensor", "sensor de velocidad ABS"); M("abs sensor", "sensor ABS");
        M("coolant temperature sensor", "sensor de temperatura"); M("knock sensor", "sensor de detonación");
        M("throttle body", "cuerpo de aceleración"); M("alternator", "alternador");
        F("starter motor", "marcha"); F("starter", "marcha");
        M("radiator fan", "ventilador de radiador"); M("cooling fan", "ventilador de radiador"); M("radiator", "radiador");
        F("thermostat housing", "toma de agua"); M("thermostat", "termostato");
        M("coolant reservoir", "depósito de anticongelante"); M("overflow tank", "depósito de anticongelante");
        F("radiator hose", "manguera de radiador");
        F("serpentine belt", "banda serpentina"); F("drive belt", "banda");
        M("timing belt kit", "kit de banda de distribución"); M("timing chain kit", "kit de cadena de distribución");
        F("timing belt", "banda de distribución"); F("timing chain", "cadena de distribución");
        M("belt tensioner", "tensor de banda"); M("tensioner", "tensor"); F("idler pulley", "polea loca"); F("pulley", "polea");
        M("engine mount", "soporte de motor"); M("motor mount", "soporte de motor"); M("transmission mount", "soporte de transmisión");
        M("valve cover gasket", "empaque de tapa de punterías"); M("head gasket", "empaque de cabeza");
        M("gasket set", "juego de empaques"); M("gasket", "empaque");
        M("intake manifold", "múltiple de admisión"); M("exhaust manifold", "múltiple de escape");
        M("catalytic converter", "convertidor catalítico"); M("muffler", "mofle");
        M("fuel injectors", "inyectores", true); M("fuel injector", "inyector");
        F("egr valve", "válvula EGR"); F("pcv valve", "válvula PCV");
        M("ac compressor", "compresor de aire acondicionado"); M("a/c compressor", "compresor de aire acondicionado");
        M("condenser", "condensador"); M("blower motor resistor", "resistencia de ventilador"); M("blower motor", "motor de ventilador de calefacción");
        M("clutch kit", "kit de clutch"); M("flywheel", "volante motor"); F("battery", "batería");
        // Suspensión y dirección
        M("control arms", "brazos de control", true); M("control arm", "brazo de control");
        F("ball joints", "rótulas", true); F("ball joint", "rótula");
        F("inner tie rod", "bieleta de dirección"); F("outer tie rod end", "terminal de dirección exterior");
        F("tie rod ends", "terminales de dirección", true); F("tie rod end", "terminal de dirección");
        M("sway bar links", "tornillos estabilizadores", true); M("sway bar link", "tornillo estabilizador");
        M("stabilizer link", "tornillo estabilizador");
        F("wheel hub bearing assembly", "maza con balero"); F("hub bearing assembly", "maza con balero");
        F("wheel hub assembly", "maza"); F("hub assembly", "maza"); F("wheel hub", "maza");
        M("wheel bearing", "balero de rueda"); M("bearing", "balero");
        F("cv axle", "flecha homocinética"); F("axle shaft", "flecha");
        M("strut assembly", "amortiguador completo"); M("struts", "amortiguadores", true); M("strut", "amortiguador");
        M("shock absorbers", "amortiguadores", true); M("shock absorber", "amortiguador"); M("shocks", "amortiguadores", true);
        M("coil springs", "resortes", true); M("coil spring", "resorte");
        F("steering rack", "cremallera de dirección");
        // Carrocería y luces
        M("headlight assembly", "faro"); M("headlights", "faros", true); M("headlight", "faro"); M("headlamp", "faro");
        F("tail light assembly", "calavera"); F("tail lights", "calaveras", true); F("tail light", "calavera");
        F("taillights", "calaveras", true); F("taillight", "calavera");
        M("fog lights", "faros de niebla", true); M("fog light", "faro de niebla");
        F("third brake light", "tercera luz de freno"); F("turn signal", "direccional");
        M("led bulbs", "focos LED", true); M("bulbs", "focos", true); M("bulb", "foco");
        M("side mirror", "espejo lateral"); M("door mirror", "espejo lateral"); M("mirror", "espejo");
        F("grille", "parrilla"); F("bumper cover", "fascia"); F("bumper", "defensa"); F("fender", "salpicadera");
        M("hood", "cofre"); F("door handle", "manija de puerta"); M("window regulator", "elevador de ventana");
        F("wiper blades", "plumas limpiaparabrisas", true); F("wiper blade", "pluma limpiaparabrisas");
        M("floor mats", "tapetes", true); F("seat covers", "fundas de asiento", true);
        F("steering wheel cover", "funda de volante"); M("gas cap", "tapón de gasolina"); M("fuel cap", "tapón de gasolina");
        M("tow hitch", "enganche de remolque"); M("trailer hitch", "enganche de remolque");
        M("running boards", "estribos", true); F("tonneau cover", "tapa de caja"); M("bed liner", "protector de caja");
        F("mud flaps", "loderas", true); M("lug nuts", "birlos", true); M("wheel spacers", "separadores de rueda", true);
        M("hubcaps", "tapones de rueda", true); F("key fob", "llave con control"); F("remote key", "llave con control");
        // Herramienta y accesorios
        M("socket set", "juego de dados"); M("jump starter", "arrancador de batería portátil");
        M("tire inflator", "inflador de llantas"); M("floor jack", "gato hidráulico"); M("torque wrench", "torquímetro");
        M("dash cam", "cámara para auto");
        // Genéricas (al final)
        F("pump", "bomba"); M("filter", "filtro"); M("sensor", "sensor"); M("fan", "ventilador");
        F("hose", "manguera"); F("valve", "válvula"); F("light", "luz"); F("lamp", "lámpara");
        F("belt", "banda"); F("chain", "cadena"); F("cover", "cubierta"); M("switch", "interruptor");
        F("tire", "llanta"); F("wheel", "rueda");

        return list.OrderByDescending(x => x.Item1.Length).ToArray();
    }

    // Posición y material: van después de la pieza y concuerdan en género y número.
    private static readonly (string English, string Spanish, bool Inflect)[] Modifiers =
    {
        ("driver side", "lado piloto", false), ("driver's side", "lado piloto", false),
        ("passenger side", "lado copiloto", false),
        ("upper and lower", "superior e inferior", false), ("upper & lower", "superior e inferior", false),
        ("front and rear", "delantero y trasero", true), ("front & rear", "delantero y trasero", true),
        ("left and right", "izquierdo y derecho", true), ("left & right", "izquierdo y derecho", true),
        ("front", "delantero", true), ("rear", "trasero", true),
        ("left", "izquierdo", true), ("lh", "izquierdo", true), ("right", "derecho", true), ("rh", "derecho", true),
        ("upper", "superior", true), ("lower", "inferior", true), ("inner", "interior", true), ("outer", "exterior", true),
        ("ceramic", "cerámico", true), ("semi-metallic", "semimetálico", true), ("heavy duty", "uso rudo", false)
    };

    // Cantidades: van antes de la pieza ("Juego de 4 Bujías").
    private static readonly (string Pattern, Func<Match, string> Spanish)[] Quantities =
    {
        (@"\bset of (\d+)\b", m => $"Juego de {m.Groups[1].Value}"),
        (@"\b(\d+)\s*(pcs|pc|pieces|piece)\b", m => $"Juego de {m.Groups[1].Value}"),
        (@"\b(\d+)[\s-]*(pack|count|ct)\b", m => $"Paquete de {m.Groups[1].Value}"),
        (@"\bpack of (\d+)\b", m => $"Paquete de {m.Groups[1].Value}"),
        (@"\bpair\b", _ => "Par de"),
        (@"\bkit\b", _ => "Kit de"),
        (@"\bset\b", _ => "Juego de")
    };

    // Resto de palabras sueltas.
    private static readonly (string English, string Spanish)[] Words =
    {
        ("energy drink", "bebida energética"), ("energy drinks", "bebidas energéticas"), ("zero sugar", "sin azúcar"),
        ("sugar free", "sin azúcar"), ("variety pack", "surtido"), ("fl oz", "oz"), ("candy", "dulces"), ("flavor", "sabor"),
        ("flavors", "sabores"), ("chocolate", "chocolate"), ("assorted", "surtido"),
        ("compatible with", "para"), ("replacement for", "para"), ("fits for", "para"), ("fits", "para"), ("fit", "para"),
        ("for", "para"), ("w/", "con "), ("with", "con"), ("and", "y"), ("&", "y"), ("without", "sin"),
        ("automatic transmission", "transmisión automática"), ("manual transmission", "transmisión estándar"),
        ("transmission", "transmisión"), ("engine", "motor"), ("motor", "motor"), ("truck", "camioneta"),
        ("pickup", "pick up"), ("car", "auto"), ("door", "puerta"), ("window", "ventana"), ("seat", "asiento"),
        ("black", "negro"), ("chrome", "cromado"), ("clear", "transparente"), ("smoked", "ahumado"), ("red", "rojo"),
        ("silver", "plata"), ("white", "blanco"), ("waterproof", "a prueba de agua"), ("universal", "universal"),
        ("performance", "alto desempeño"), ("assembly", ""), ("replacement", ""), ("premium", ""), ("brand new", ""),
        ("new", ""), ("genuine", ""), ("high quality", ""), ("oem", "OEM"), ("aftermarket", ""), ("the", ""), ("of", "de"),
        ("year", "año"), ("years", "años"), ("models", "modelos"), ("model", "modelo")
    };

    private static readonly HashSet<string> Connectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "del", "para", "con", "y", "sin", "la", "el", "a"
    };

    /// <summary>Traducción por diccionario (sin IA). Pública para pruebas y para el respaldo del enriquecedor.</summary>
    public static string Translate(string title)
    {
        var text = " " + Regex.Replace(title.Trim(), @"\s+", " ") + " ";

        // 1) Cantidad
        string? quantity = null;
        foreach (var (pattern, spanish) in Quantities)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            quantity = spanish(match);
            text = text.Remove(match.Index, match.Length).Insert(match.Index, " ");
            break;
        }

        // 2) Pieza principal (la primera que aparezca en el título)
        Noun? noun = null;
        var nounIndex = int.MaxValue;
        string? nounEnglish = null;
        foreach (var (english, candidate) in Nouns)
        {
            var match = Regex.Match(text, $@"(?<![\w-]){Regex.Escape(english)}(?![\w-])", RegexOptions.IgnoreCase);
            if (match.Success && match.Index < nounIndex)
            {
                // Ante dos piezas en la misma posición gana la frase más larga (ya vienen ordenadas).
                noun = candidate;
                nounIndex = match.Index;
                nounEnglish = match.Value;
            }
        }
        if (noun is not null)
            text = text.Remove(nounIndex, nounEnglish!.Length).Insert(nounIndex, " ");

        // 3) Posición y material
        var modifiers = new List<string>();
        foreach (var (english, spanish, inflect) in Modifiers)
        {
            var pattern = $@"(?<![\w-]){Regex.Escape(english)}(?![\w-])";
            if (!Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)) continue;
            text = Regex.Replace(text, pattern, " ", RegexOptions.IgnoreCase);
            var value = inflect && noun is not null ? Inflect(spanish, noun) : spanish;
            if (!modifiers.Contains(value)) modifiers.Add(value);
        }
        // "lado piloto" e "izquierdo" dicen lo mismo en México.
        if (modifiers.Contains("lado piloto")) modifiers.RemoveAll(m => m.StartsWith("izquierd", StringComparison.Ordinal));
        if (modifiers.Contains("lado copiloto")) modifiers.RemoveAll(m => m.StartsWith("derech", StringComparison.Ordinal));

        // 3b) Relleno en inglés y otras piezas mencionadas ("w/ Ball Joint" → "con Rótula")
        foreach (var filler in Filler)
            text = Regex.Replace(text, $@"(?<![\w-]){Regex.Escape(filler)}(?![\w-])", " ", RegexOptions.IgnoreCase);
        foreach (var (english, other) in Nouns)
        {
            var pattern = $@"(?<![\w-]){Regex.Escape(english)}(?![\w-])";
            text = Regex.Replace(text, pattern, $" {other.Spanish} ", RegexOptions.IgnoreCase);
        }

        // 4) Resto palabra por palabra (marca, número de parte, vehículo y años se quedan)
        foreach (var (english, spanish) in Words)
        {
            var pattern = english.EndsWith('/') || english == "&"
                ? Regex.Escape(english)
                : $@"(?<![\w-]){Regex.Escape(english)}(?![\w-])";
            text = Regex.Replace(text, pattern, spanish.Length == 0 ? " " : $" {spanish} ", RegexOptions.IgnoreCase);
        }
        text = Regex.Replace(text, @"[,|]+", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"^(para|con|y|de)\s+(?=(para|con)\b)", "", RegexOptions.IgnoreCase);

        var parts = new List<string>();
        var quantityAtEnd = noun is null && quantity is not null;
        if (quantity is not null && !quantityAtEnd) parts.Add(quantity);
        if (noun is not null)
        {
            var nounText = noun.Spanish;
            if (quantity is not null && !noun.Plural)
                nounText = Pluralize(nounText);
            if (quantity is not null && quantity.StartsWith("Juego", StringComparison.Ordinal) && nounText.StartsWith("juego de ", StringComparison.OrdinalIgnoreCase))
                nounText = nounText[9..];
            parts.Add(nounText);
            parts.AddRange(quantity is not null && !noun.Plural
                ? modifiers.Select(m => Inflect(m, noun with { Plural = true }))
                : modifiers);
        }
        else
        {
            parts.AddRange(modifiers);
        }
        if (text.Length > 0) parts.Add(text);
        if (quantityAtEnd) parts.Add(quantity!);

        var result = Regex.Replace(string.Join(" ", parts), @"\s+", " ").Trim();
        result = Regex.Replace(result, @"\b(para|con|y|de)\s*$", "", RegexOptions.IgnoreCase).Trim();
        result = Regex.Replace(result, @"\b(para)\s+(para)\b", "$1", RegexOptions.IgnoreCase);
        return FitTitle(Capitalize(result));
    }

    /// <summary>
    /// Ajusta a 60 caracteres cuidando lo que más busca el comprador: la pieza y el vehículo con sus años.
    /// Primero quita accesorios ("con Empaque"), luego marcas de fábrica sueltas, y al final recorta.
    /// </summary>
    private static string FitTitle(string title)
    {
        if (title.Length <= MaxTitleLength)
            return title;

        var shorter = Regex.Replace(title, @"\s+con\s+[^,]+?(?=\s+para\b)", "", RegexOptions.IgnoreCase);
        if (shorter.Length <= MaxTitleLength)
            return shorter;

        // Quita palabras sueltas entre el número de parte y "para" (ej. "GM", "Platinum") de una en una.
        var paraIndex = shorter.IndexOf(" para ", StringComparison.OrdinalIgnoreCase);
        if (paraIndex > 0)
        {
            var head = shorter[..paraIndex].Split(' ').ToList();
            var tail = shorter[paraIndex..];
            for (var i = head.Count - 1; i > 0 && string.Join(" ", head).Length + tail.Length > MaxTitleLength; i--)
            {
                if (!head[i].Any(char.IsDigit) && head[i].Length <= 4 && head[i].All(char.IsUpper))
                    head.RemoveAt(i);
            }
            shorter = string.Join(" ", head) + tail;
            if (shorter.Length <= MaxTitleLength)
                return shorter;

            // "para" ocupa 5 caracteres: se puede omitir antes del vehículo.
            var withoutPara = Regex.Replace(shorter, @"\s+para\s+", " ", RegexOptions.IgnoreCase);
            if (withoutPara.Length <= MaxTitleLength)
                return withoutPara;

            // El número de parte se queda en el atributo PART_NUMBER; en el título pesa más el vehículo y los años.
            paraIndex = shorter.IndexOf(" para ", StringComparison.OrdinalIgnoreCase);
            var vehicle = shorter[(paraIndex + 6)..].Trim();
            var headWords = shorter[..paraIndex].Split(' ').ToList();
            var withoutPart = string.Join(" ", headWords.Where((w, i) => i == 0 || !IsPartNumber(w)));
            var candidate = $"{withoutPart} {vehicle}";
            if (candidate.Length <= MaxTitleLength)
                return candidate;

            // Último recurso: la pieza al inicio y el vehículo con años al final, recortando lo de en medio.
            var room = MaxTitleLength - vehicle.Length - 1;
            if (room >= 20)
                return $"{Shorten(withoutPart, room)} {vehicle}";
        }

        // Sin "para" (como lo escribe la IA): quita números de parte, nunca años ni motor.
        var words = shorter.Split(' ').ToList();
        for (var i = words.Count - 1; i > 0 && string.Join(" ", words).Length > MaxTitleLength; i--)
        {
            if (IsPartNumber(words[i]))
                words.RemoveAt(i);
        }
        shorter = string.Join(" ", words);

        return Shorten(shorter, MaxTitleLength);
    }

    private static bool IsPartNumber(string word) =>
        word.Length >= 5 &&
        word.Any(char.IsDigit) &&
        !Regex.IsMatch(word, @"^(19|20)\d{2}([-–/](19|20)?\d{2})?$") &&   // años 2014-2018
        !Regex.IsMatch(word, @"^\d(\.\d)?L$", RegexOptions.IgnoreCase) &&  // motor 5.3L
        !Regex.IsMatch(word, @"^[A-Z]-?\d{2,4}$");                        // F-150, K1500

    private static string Inflect(string adjective, Noun noun)
    {
        if (adjective.Contains(' ') && !adjective.Contains(" y ", StringComparison.Ordinal))
            return adjective; // "lado piloto", "uso rudo"

        return string.Join(" y ", adjective.Split(" y ").Select(word =>
        {
            var result = word;
            if (noun.Feminine && result.EndsWith('o')) result = result[..^1] + "a";
            if (noun.Plural) result = Pluralize(result);
            return result;
        }));
    }

    private static string Pluralize(string phrase)
    {
        // Solo la primera palabra: "filtro de aire" → "filtros de aire".
        var space = phrase.IndexOf(' ');
        var head = space < 0 ? phrase : phrase[..space];
        var tail = space < 0 ? "" : phrase[space..];
        if (head.EndsWith('s')) return phrase;
        head = Regex.IsMatch(head, "[aeiouáéíóú]$", RegexOptions.IgnoreCase) ? head + "s"
            : head.EndsWith('z') ? head[..^1] + "ces"
            : head + "es";
        return head + tail;
    }

    private static string Capitalize(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (i > 0) builder.Append(' ');
            if (i > 0 && Connectors.Contains(word))
            {
                builder.Append(word.ToLowerInvariant());
                continue;
            }
            // Marcas y números de parte en mayúsculas o mixtos se quedan igual.
            if (word.Any(char.IsDigit) || word.Skip(1).Any(char.IsUpper) || word.Length <= 1)
            {
                builder.Append(word);
                continue;
            }
            builder.Append(char.ToUpper(word[0], CultureInfo.GetCultureInfo("es-MX"))).Append(word[1..]);
        }
        return builder.ToString();
    }

    public static string Shorten(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = text[..max];
        var lastSpace = cut.LastIndexOf(' ');
        cut = lastSpace > 20 ? cut[..lastSpace] : cut;
        return Regex.Replace(cut, @"\s+(para|con|y|de|del)$", "", RegexOptions.IgnoreCase).Trim();
    }
}
