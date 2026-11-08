using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace MarketplaceSync.Web.Helpers;

/// <summary>
/// Lee la primera hoja de un .xlsx (Excel, Google Sheets, LibreOffice) o un .csv como filas de texto, sin librerías.
/// Los números se regresan con punto decimal (formato invariante).
/// </summary>
public static class SimpleXlsxReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public const int MaxRows = 5000;

    public static List<List<string>> Read(Stream stream, string fileName)
    {
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return ReadCsv(stream);
        return ReadXlsx(stream);
    }

    public static List<List<string>> ReadXlsx(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var sheetPath = FirstSheetPath(zip) ?? "xl/worksheets/sheet1.xml";
        var shared = SharedStrings(zip);
        var entry = zip.GetEntry(sheetPath) ?? throw new InvalidDataException("El archivo no tiene hojas.");

        XDocument sheet;
        using (var s = entry.Open())
            sheet = XDocument.Load(s);

        var rows = new List<List<string>>();
        foreach (var row in sheet.Descendants(Main + "row"))
        {
            if (rows.Count >= MaxRows) break;
            var values = new List<string>();
            var next = 0;
            foreach (var cell in row.Elements(Main + "c"))
            {
                var index = ColumnIndex((string?)cell.Attribute("r")) ?? next;
                while (values.Count < index) values.Add(string.Empty);
                values.Add(CellText(cell, shared));
                next = index + 1;
            }
            // Las filas vacías se conservan para que el número de renglón coincida con Excel.
            var rowNumber = int.TryParse((string?)row.Attribute("r"), out var r) ? r : rows.Count + 1;
            while (rows.Count < rowNumber - 1) rows.Add(new List<string>());
            rows.Add(values);
        }
        return rows;
    }

    private static string CellText(XElement cell, IReadOnlyList<string> shared)
    {
        var type = (string?)cell.Attribute("t");
        var value = cell.Element(Main + "v")?.Value;
        switch (type)
        {
            case "s":
                return int.TryParse(value, out var i) && i >= 0 && i < shared.Count ? shared[i].Trim() : string.Empty;
            case "inlineStr":
                return string.Concat(cell.Descendants(Main + "t").Select(t => t.Value)).Trim();
            case "b":
                return value == "1" ? "TRUE" : "FALSE";
            case "str":
            case "e":
                return (value ?? string.Empty).Trim();
            default:
                // Número: se normaliza (Excel guarda 1299.5 o 1.2995E3).
                if (value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return Math.Round((decimal)d, 6).ToString("0.######", CultureInfo.InvariantCulture);
                return (value ?? string.Empty).Trim();
        }
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        var list = new List<string>();
        if (entry is null) return list;
        using var s = entry.Open();
        var doc = XDocument.Load(s);
        foreach (var si in doc.Root!.Elements(Main + "si"))
            list.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));
        return list;
    }

    private static string? FirstSheetPath(ZipArchive zip)
    {
        var workbookEntry = zip.GetEntry("xl/workbook.xml");
        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbookEntry is null || relsEntry is null) return null;
        XDocument workbook, rels;
        using (var s = workbookEntry.Open()) workbook = XDocument.Load(s);
        using (var s = relsEntry.Open()) rels = XDocument.Load(s);
        var firstSheet = workbook.Descendants(Main + "sheet").FirstOrDefault();
        var relId = (string?)firstSheet?.Attribute(Rel + "id");
        var target = rels.Root?.Elements(PackageRel + "Relationship").FirstOrDefault(r => (string?)r.Attribute("Id") == relId)?.Attribute("Target")?.Value;
        if (target is null) return null;
        target = target.TrimStart('/');
        return target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target;
    }

    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var index = 0;
        var letters = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            letters++;
        }
        return letters == 0 ? null : index - 1;
    }

    public static List<List<string>> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = reader.ReadToEnd();
        var firstLine = text.Split('\n', 2)[0];
        var separator = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : firstLine.Contains('\t') ? '\t' : ',';

        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            if (c == '"') quoted = true;
            else if (c == separator) { row.Add(field.ToString().Trim()); field.Clear(); }
            else if (c == '\n')
            {
                row.Add(field.ToString().Trim()); field.Clear();
                rows.Add(row); row = new List<string>();
                if (rows.Count >= MaxRows) break;
            }
            else if (c != '\r') field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString().Trim()); rows.Add(row); }
        return rows;
    }
}
