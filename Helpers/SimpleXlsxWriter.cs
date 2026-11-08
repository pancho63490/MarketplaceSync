using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace MarketplaceSync.Web.Helpers;

/// <summary>
/// Genera un .xlsx sencillo (varias hojas, encabezado en negritas, formato de pesos y de fecha) sin librerías:
/// un .xlsx es un zip con XML de SpreadsheetML. Suficiente para reportes que se abren en Excel o Google Sheets.
/// </summary>
public sealed class SimpleXlsxWriter
{
    public enum CellStyle { Text = 0, Header = 1, Money = 2, Date = 3, Bold = 4, BoldMoney = 5, Integer = 6 }

    public sealed record Cell(object? Value, CellStyle Style = CellStyle.Text);

    private sealed class Sheet
    {
        public required string Name { get; init; }
        public List<List<Cell>> Rows { get; } = new();
        public double[] Widths { get; set; } = Array.Empty<double>();
    }

    private readonly List<Sheet> _sheets = new();

    /// <summary>Agrega una hoja; regresa la lista de renglones para llenarla.</summary>
    public List<List<Cell>> AddSheet(string name, params double[] columnWidths)
    {
        var clean = new string(name.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        var sheet = new Sheet { Name = clean.Length > 31 ? clean[..31] : clean, Widths = columnWidths };
        _sheets.Add(sheet);
        return sheet.Rows;
    }

    public byte[] Build()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes());
            Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/workbook.xml", Workbook());
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
            Add(zip, "xl/styles.xml", Styles());
            for (var i = 0; i < _sheets.Count; i++)
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(_sheets[i]));
        }
        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private string ContentTypes()
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (var i = 1; i <= _sheets.Count; i++)
            sb.Append($"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        return sb.Append("</Types>").ToString();
    }

    private string Workbook()
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
        for (var i = 0; i < _sheets.Count; i++)
            sb.Append($"""<sheet name="{Esc(_sheets[i].Name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
        return sb.Append("</sheets></workbook>").ToString();
    }

    private string WorkbookRels()
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (var i = 0; i < _sheets.Count; i++)
            sb.Append($"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>""");
        sb.Append($"""<Relationship Id="rId{_sheets.Count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        return sb.Append("</Relationships>").ToString();
    }

    // 0 texto · 1 encabezado (negritas + fondo) · 2 pesos · 3 fecha · 4 negritas · 5 pesos en negritas · 6 entero
    private static string Styles() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
        <numFmts count="2"><numFmt numFmtId="164" formatCode="&quot;$&quot;#,##0.00;[Red]-&quot;$&quot;#,##0.00"/><numFmt numFmtId="165" formatCode="dd/mm/yyyy"/></numFmts>
        <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
        <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEFEDFF"/><bgColor indexed="64"/></patternFill></fill></fills>
        <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
        <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
        <cellXfs count="7">
        <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
        <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/>
        <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
        <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
        <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
        <xf numFmtId="164" fontId="1" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/>
        <xf numFmtId="1" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
        </cellXfs>
        <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """.Trim();

    private static string SheetXml(Sheet sheet)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        if (sheet.Widths.Length > 0)
        {
            sb.Append("<cols>");
            for (var i = 0; i < sheet.Widths.Length; i++)
                sb.Append($"""<col min="{i + 1}" max="{i + 1}" width="{sheet.Widths[i].ToString(CultureInfo.InvariantCulture)}" customWidth="1"/>""");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            sb.Append($"""<row r="{r + 1}">""");
            var row = sheet.Rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                var cell = row[c];
                var reference = Column(c) + (r + 1);
                var style = (int)cell.Style;
                switch (cell.Value)
                {
                    case null:
                        sb.Append($"""<c r="{reference}" s="{style}"/>""");
                        break;
                    case decimal or double or int or long or float:
                        sb.Append($"""<c r="{reference}" s="{style}"><v>{Convert.ToDecimal(cell.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}</v></c>""");
                        break;
                    case DateOnly date:
                        sb.Append($"""<c r="{reference}" s="{(int)CellStyle.Date}"><v>{date.ToDateTime(TimeOnly.MinValue).ToOADate().ToString(CultureInfo.InvariantCulture)}</v></c>""");
                        break;
                    case DateTime dateTime:
                        sb.Append($"""<c r="{reference}" s="{(int)CellStyle.Date}"><v>{dateTime.Date.ToOADate().ToString(CultureInfo.InvariantCulture)}</v></c>""");
                        break;
                    default:
                        sb.Append($"""<c r="{reference}" s="{style}" t="inlineStr"><is><t xml:space="preserve">{Esc(cell.Value.ToString())}</t></is></c>""");
                        break;
                }
            }
            sb.Append("</row>");
        }
        return sb.Append("</sheetData></worksheet>").ToString();
    }

    private static string Column(int index)
    {
        var name = string.Empty;
        index++;
        while (index > 0)
        {
            var m = (index - 1) % 26;
            name = (char)('A' + m) + name;
            index = (index - m) / 26;
        }
        return name;
    }

    private static string Esc(string? value)
    {
        // Quita caracteres de control que rompen el XML.
        var clean = new string((value ?? string.Empty).Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= ' ').ToArray());
        return SecurityElement.Escape(clean) ?? string.Empty;
    }
}
