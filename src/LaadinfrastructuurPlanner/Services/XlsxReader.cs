using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace LaadinfrastructuurPlanner.Services;

internal static class XlsxReader
{
    public static string[] ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        var values = new List<string>();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = false });
        var text = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                text.Clear();
            }
            else if (reader.NodeType == XmlNodeType.Text)
            {
                text.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si")
            {
                values.Add(text.ToString());
            }
        }

        return values.ToArray();
    }

    public static string? ResolveWorksheetPath(ZipArchive archive, string sheetName)
    {
        var workbook = archive.GetEntry("xl/workbook.xml");
        var rels = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbook is null || rels is null)
        {
            return null;
        }

        string? relationId = null;
        using (var stream = workbook.Open())
        using (var reader = XmlReader.Create(stream))
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && reader.LocalName == "sheet"
                    && string.Equals(reader.GetAttribute("name"), sheetName, StringComparison.OrdinalIgnoreCase))
                {
                    relationId = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    break;
                }
            }
        }

        if (relationId is null)
        {
            return null;
        }

        using var relStream = rels.Open();
        using var relReader = XmlReader.Create(relStream);
        while (relReader.Read())
        {
            if (relReader.NodeType == XmlNodeType.Element
                && relReader.LocalName == "Relationship"
                && string.Equals(relReader.GetAttribute("Id"), relationId, StringComparison.Ordinal))
            {
                var target = relReader.GetAttribute("Target");
                if (string.IsNullOrWhiteSpace(target))
                {
                    return null;
                }

                return target.StartsWith("worksheets/", StringComparison.OrdinalIgnoreCase)
                    ? "xl/" + target
                    : target.TrimStart('/');
            }
        }

        return null;
    }

    public static IEnumerable<string[]> ReadWorksheetRows(ZipArchive archive, string sheetPath, IReadOnlyList<string> sharedStrings)
    {
        var entry = archive.GetEntry(sheetPath);
        if (entry is null)
        {
            yield break;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true });
        var row = new Dictionary<int, string>();
        string? cellRef = null;
        string? cellType = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                row.Clear();
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
            {
                cellRef = reader.GetAttribute("r");
                cellType = reader.GetAttribute("t");
            }
            else if (reader.NodeType == XmlNodeType.Element && (reader.LocalName == "v" || reader.LocalName == "t"))
            {
                var rawValue = reader.ReadElementContentAsString();
                var column = ColumnIndex(cellRef);
                if (column >= 0)
                {
                    row[column] = cellType == "s"
                        && int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedIndex)
                        && sharedIndex >= 0
                        && sharedIndex < sharedStrings.Count
                            ? sharedStrings[sharedIndex]
                            : rawValue;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
            {
                var width = row.Count == 0 ? 0 : row.Keys.Max() + 1;
                var cells = new string[width];
                foreach (var item in row)
                {
                    cells[item.Key] = item.Value;
                }

                yield return cells;
            }
        }
    }

    public static int ColumnIndex(string? cellReference)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
        {
            return -1;
        }

        var index = 0;
        var seenLetter = false;
        foreach (var ch in cellReference)
        {
            if (!char.IsLetter(ch))
            {
                break;
            }

            seenLetter = true;
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return seenLetter ? index - 1 : -1;
    }

    public static string Cell(IReadOnlyList<string> cells, int index)
    {
        return index >= 0 && index < cells.Count ? cells[index] : "";
    }

    public static string NormalizeHeader(string value)
    {
        return value.Trim().ToLowerInvariant().Replace(" ", "_", StringComparison.Ordinal);
    }
}
