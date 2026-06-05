using System.IO.Compression;
using System.Security;

namespace LaadinfrastructuurPlanner.Tests;

internal static class TestXlsxData
{
    public static void WriteCharterStandplaatsen(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddText(archive, "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        AddText(archive, "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        AddText(archive, "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
            </Relationships>
            """);
        AddText(archive, "xl/workbook.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="Voertuigen" sheetId="1" r:id="rId1"/>
                <sheet name="Aantallen" sheetId="2" r:id="rId2"/>
              </sheets>
            </workbook>
            """);
        AddText(archive, "xl/worksheets/sheet1.xml", WorksheetXml([
            ["Voertuig: Wagencode", "Type voertuig", "Brandstoftype", "Inzet type", "Standplaats: Adres", "Standplaats: Plaats", "Standplaats: Land"],
            ["CHAR01", "Trekker + Oplegger", "Diesel", "Vast", "Teststraat 1", "Utrecht", "Nederland"],
            ["CHAR02", "Bakwagen", "Diesel", "Flex", "Teststraat 1", "Utrecht", "Nederland"],
        ]));
        AddText(archive, "xl/worksheets/sheet2.xml", WorksheetXml([
            ["Standplaats: Adres", "Standplaats: Plaats", "Standplaats: Land", "Aantal wagens"],
            ["Teststraat 1", "Utrecht", "Nederland", "2"],
        ]));
    }

    private static string WorksheetXml(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var xml = new System.Text.StringBuilder();
        xml.Append("""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            xml.Append(CultureInvariant($"<row r=\"{rowIndex + 1}\">"));
            for (var colIndex = 0; colIndex < rows[rowIndex].Count; colIndex++)
            {
                var cell = ColumnName(colIndex) + (rowIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                xml.Append(CultureInvariant($"<c r=\"{cell}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(rows[rowIndex][colIndex])}</t></is></c>"));
            }
            xml.Append("</row>");
        }
        xml.Append("</sheetData></worksheet>");
        return xml.ToString();
    }

    private static string ColumnName(int index)
    {
        var value = "";
        index++;
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            value = (char)('A' + remainder) + value;
            index = (index - 1) / 26;
        }
        return value;
    }

    private static string CultureInvariant(FormattableString value) => FormattableString.Invariant(value);

    private static void AddText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
