using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace FileExplorer.Services;

/// Lightweight, text-only preview extraction for modern (XML-based) Office documents. Not a
/// rendered preview - just enough content to identify the file at a glance - since real
/// rendering would need either Office itself installed or a much heavier dependency.
public static class OfficeTextExtractor
{
    private const int MaxPreviewChars = 4000;
    private const int MaxSpreadsheetRows = 50;
    private const int MaxSlides = 20;

    public static string Extract(string path, string extension)
    {
        try
        {
            var text = extension.ToLowerInvariant() switch
            {
                ".docx" => ExtractDocx(path),
                ".xlsx" => ExtractXlsx(path),
                ".pptx" => ExtractPptx(path),
                _ => string.Empty,
            };

            return text.Length > MaxPreviewChars ? text[..MaxPreviewChars] + "\n..." : text;
        }
        catch (Exception)
        {
            // Malformed or password-protected documents, unexpected OpenXml SDK errors, etc. -
            // this is a best-effort text preview, never worth crashing the app over.
            return "(couldn't read this document)";
        }
    }

    private static string ExtractDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }

        var paragraphs = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Select(p => p.InnerText);
        return string.Join(Environment.NewLine, paragraphs);
    }

    private static string ExtractPptx(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        var slideParts = doc.PresentationPart?.SlideParts ?? Enumerable.Empty<SlidePart>();

        var sb = new StringBuilder();
        var slideNumber = 1;
        foreach (var slidePart in slideParts.Take(MaxSlides))
        {
            var texts = slidePart.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Select(t => t.Text);
            sb.AppendLine($"--- Slide {slideNumber++} ---");
            sb.AppendLine(string.Join(" ", texts));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string ExtractXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var workbookPart = doc.WorkbookPart;
        var sheet = workbookPart?.Workbook.Descendants<Sheet>().FirstOrDefault();
        if (workbookPart is null || sheet?.Id?.Value is not string sheetId)
        {
            return string.Empty;
        }

        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheetId);
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

        var sb = new StringBuilder();
        sb.AppendLine($"Sheet: {sheet.Name}");

        var rowCount = 0;
        foreach (var row in worksheetPart.Worksheet.Descendants<Row>())
        {
            if (rowCount++ >= MaxSpreadsheetRows)
            {
                sb.AppendLine("...");
                break;
            }

            var cells = row.Descendants<Cell>().Select(c => GetCellText(c, sharedStrings));
            sb.AppendLine(string.Join("\t", cells));
        }

        return sb.ToString();
    }

    private static string GetCellText(Cell cell, SharedStringTable? sharedStrings)
    {
        var raw = cell.CellValue?.InnerText ?? string.Empty;

        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings is not null && int.TryParse(raw, out var index))
        {
            var items = sharedStrings.Elements<SharedStringItem>().ToList();
            return index >= 0 && index < items.Count ? items[index].InnerText : raw;
        }

        return raw;
    }
}
