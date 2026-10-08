using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KvindoCode.Core.Tools;

/// <summary>Text extraction for the non-plain-text formats the Read tool understands: PDF, docx/xlsx/pptx, notebooks.</summary>
public static class DocumentReaders
{
    public static readonly HashSet<string> ImageExts = new() { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
    public static readonly HashSet<string> OfficeExts = new() { ".docx", ".xlsx", ".pptx", ".odt" };

    static string Run(string exe, IEnumerable<string> args, int timeoutMs, out int exitCode)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } exitCode = -1; return "timed out"; }
        exitCode = p.ExitCode;
        return exitCode == 0 ? outp.Result : (err.Result.Length > 0 ? err.Result : outp.Result);
    }

    static bool Has(string exe)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')) if (File.Exists(Path.Combine(d, exe))) return true;
        return false;
    }

    // ------------------------------------------------------------------ PDF

    public static ToolResult ReadPdf(string path, string? pages)
    {
        if (!Has("pdftotext")) return ToolResult.Err("Reading PDFs needs `pdftotext` (package poppler-utils), which is not installed. Install it (sudo apt install poppler-utils) or convert the PDF to text first.");
        int first = 1, last = 20;
        if (!string.IsNullOrWhiteSpace(pages))
        {
            var m = Regex.Match(pages.Trim(), @"^(\d+)(?:\s*-\s*(\d+))?$");
            if (!m.Success) return ToolResult.Err("pages must look like \"3\" or \"1-5\".");
            first = int.Parse(m.Groups[1].Value); last = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : first;
            if (last < first || last - first >= 50) return ToolResult.Err("pages: at most 50 pages per call, and the range must be ascending.");
        }
        string total = "";
        if (Has("pdfinfo"))
        {
            var info = Run("pdfinfo", new[] { path }, 15000, out var ic);
            var m = Regex.Match(info, @"^Pages:\s+(\d+)", RegexOptions.Multiline);
            if (m.Success) total = m.Groups[1].Value;
        }
        var text = Run("pdftotext", new[] { "-layout", "-f", first.ToString(), "-l", last.ToString(), path, "-" }, 60000, out var code);
        if (code != 0) return ToolResult.Err("pdftotext failed: " + text.Trim());
        var header = $"PDF {Path.GetFileName(path)} — pages {first}-{last}{(total.Length > 0 ? " of " + total : "")}\n";
        if (string.IsNullOrWhiteSpace(text))
            return ToolResult.Ok(header + "(no extractable text on these pages — the PDF is probably scanned images; OCR would be needed)");
        var more = total.Length > 0 && int.TryParse(total, out var t) && last < t ? $"\n\n(more pages available: call Read again with pages=\"{last + 1}-{Math.Min(t, last + 20)}\")" : "";
        return ToolResult.Ok(Tool.Truncate(header + text.TrimEnd() + more, 80_000));
    }

    // ------------------------------------------------------------------ images

    public static ToolResult ReadImage(string path, bool vision)
    {
        var fi = new FileInfo(path);
        if (!vision) return ToolResult.Err($"{Path.GetFileName(path)} is an image ({fi.Length / 1024} KB) and the current model cannot see images. Switch to a vision-capable model, or use Bash (e.g. tesseract) to OCR it.");
        var bytes = File.ReadAllBytes(path);
        string note = "";
        if (bytes.Length > 3_500_000 && Has("convert"))
        {
            var tmp = Path.Combine(Path.GetTempPath(), "kvindocode-img-" + Guid.NewGuid().ToString("N") + ".jpg");
            try
            {
                Run("convert", new[] { path + "[0]", "-resize", "2048x2048>", "-quality", "85", tmp }, 60000, out var code);
                if (code == 0 && File.Exists(tmp)) { bytes = File.ReadAllBytes(tmp); note = " (downscaled to fit)"; }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        if (bytes.Length > 12_000_000) return ToolResult.Err("Image is too large to attach even after downscaling.");
        string dims = "";
        if (Has("identify")) { var d = Run("identify", new[] { "-format", "%wx%h", path + "[0]" }, 15000, out var c); if (c == 0) dims = ", " + d.Trim(); }
        return new ToolResult($"Image {Path.GetFileName(path)} ({fi.Length / 1024} KB{dims}){note} is attached to the next message — look at it there.", false, new[] { bytes });
    }

    // ------------------------------------------------------------------ Office

    public static ToolResult ReadOffice(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            string text = ext switch
            {
                ".docx" => Docx(zip),
                ".xlsx" => Xlsx(zip),
                ".pptx" => Pptx(zip),
                ".odt" => Odt(zip),
                _ => "",
            };
            if (string.IsNullOrWhiteSpace(text)) return ToolResult.Ok($"{Path.GetFileName(path)}: no text content found.");
            return ToolResult.Ok(Tool.Truncate($"{Path.GetFileName(path)} (text extracted)\n\n" + text.TrimEnd(), 80_000));
        }
        catch (InvalidDataException) { return ToolResult.Err($"{Path.GetFileName(path)} is not a valid Office (zip) file."); }
    }

    static XDocument Load(ZipArchive z, string name)
    {
        var e = z.GetEntry(name);
        if (e is null) return new XDocument();
        using var s = e.Open();
        return XDocument.Load(s);
    }

    static string Docx(ZipArchive z)
    {
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var doc = Load(z, "word/document.xml");
        var sb = new StringBuilder();
        foreach (var p in doc.Descendants(w + "p"))
        {
            var line = new StringBuilder();
            foreach (var n in p.Descendants())
            {
                if (n.Name == w + "t") line.Append(n.Value);
                else if (n.Name == w + "tab") line.Append('\t');
                else if (n.Name == w + "br") line.Append('\n');
            }
            var style = p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? "";
            var t = line.ToString();
            if (t.Length == 0) { sb.AppendLine(); continue; }
            if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out var lvl)) t = new string('#', Math.Clamp(lvl, 1, 6)) + " " + t;
            else if (p.Element(w + "pPr")?.Element(w + "numPr") != null) t = "- " + t;
            sb.AppendLine(t);
        }
        return sb.ToString();
    }

    static string Pptx(ZipArchive z)
    {
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var slides = z.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$")).OrderBy(e => int.Parse(Regex.Match(e.FullName, @"\d+").Value)).ToList();
        var sb = new StringBuilder();
        int n = 0;
        foreach (var s in slides)
        {
            sb.AppendLine($"--- Slide {++n} ---");
            using var st = s.Open();
            var x = XDocument.Load(st);
            foreach (var p in x.Descendants(a + "p"))
            {
                var t = string.Concat(p.Descendants(a + "t").Select(e => e.Value));
                if (t.Length > 0) sb.AppendLine(t);
            }
            var notes = z.GetEntry($"ppt/notesSlides/notesSlide{n}.xml");
            if (notes != null)
            {
                using var ns = notes.Open();
                var nt = string.Join(" ", XDocument.Load(ns).Descendants(a + "t").Select(e => e.Value)).Trim();
                if (nt.Length > 0) sb.AppendLine("[notes] " + nt);
            }
        }
        return sb.ToString();
    }

    static string Odt(ZipArchive z)
    {
        var doc = Load(z, "content.xml");
        XNamespace t = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        var sb = new StringBuilder();
        foreach (var p in doc.Descendants().Where(e => e.Name == t + "p" || e.Name == t + "h")) sb.AppendLine(p.Value);
        return sb.ToString();
    }

    static string Xlsx(ZipArchive z)
    {
        XNamespace m = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var shared = Load(z, "xl/sharedStrings.xml").Descendants(m + "si").Select(si => string.Concat(si.Descendants(m + "t").Select(t => t.Value))).ToList();

        // sheet names + files
        var wb = Load(z, "xl/workbook.xml");
        var rels = Load(z, "xl/_rels/workbook.xml.rels").Descendants().Where(e => e.Attribute("Id") != null).ToDictionary(e => e.Attribute("Id")!.Value, e => e.Attribute("Target")!.Value);
        var sb = new StringBuilder();
        foreach (var sh in wb.Descendants(m + "sheet"))
        {
            var rid = sh.Attribute(r + "id")?.Value ?? "";
            if (!rels.TryGetValue(rid, out var target)) continue;
            var file = target.StartsWith("/") ? target[1..] : "xl/" + target;
            if (z.GetEntry(file) is null) continue;
            sb.AppendLine($"--- Sheet: {sh.Attribute("name")?.Value} ---");
            var sheet = Load(z, file);
            int rows = 0;
            foreach (var row in sheet.Descendants(m + "row"))
            {
                if (++rows > 500) { sb.AppendLine("… (more rows not shown)"); break; }
                var cells = new List<string>();
                foreach (var c in row.Elements(m + "c"))
                {
                    var reference = c.Attribute("r")?.Value ?? "";
                    var type = c.Attribute("t")?.Value;
                    var v = c.Element(m + "v")?.Value ?? string.Concat(c.Descendants(m + "t").Select(t => t.Value));
                    if (type == "s" && int.TryParse(v, out var idx) && idx < shared.Count) v = shared[idx];
                    var f = c.Element(m + "f")?.Value;
                    if (string.IsNullOrEmpty(v) && string.IsNullOrEmpty(f)) continue;
                    cells.Add(f is { Length: > 0 } ? $"{reference}: {v} (={f})" : $"{reference}: {v}");
                }
                if (cells.Count > 0) sb.AppendLine(string.Join(" | ", cells.Take(100)));
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ notebooks

    public static ToolResult ReadNotebook(string path)
    {
        try
        {
            var nb = JsonText.TryParse(File.ReadAllText(path))!;
            var sb = new StringBuilder($"Notebook {Path.GetFileName(path)}\n\n");
            int i = 0;
            foreach (var cell in nb["cells"]!.AsArray())
            {
                string Src(JsonNode? n) => n is JsonArray a ? string.Concat(a.Select(x => x?.ToString())) : n?.ToString() ?? "";
                var type = (string?)cell!["cell_type"] ?? "code";
                sb.AppendLine($"# [{++i}] {type}");
                sb.AppendLine(Src(cell["source"]).TrimEnd());
                if (type == "code" && cell["outputs"] is JsonArray outs)
                    foreach (var o in outs)
                    {
                        var t = Src(o?["text"]);
                        if (t.Length == 0) t = Src(o?["data"]?["text/plain"]);
                        if (t.Length == 0 && o?["ename"] != null) t = $"{o["ename"]}: {o["evalue"]}";
                        if (t.Length > 0) sb.AppendLine("  → " + (t.Length > 1500 ? t[..1500] + "…" : t).TrimEnd().Replace("\n", "\n    "));
                    }
                sb.AppendLine();
            }
            return ToolResult.Ok(Tool.Truncate(sb.ToString(), 80_000));
        }
        catch (Exception e) { return ToolResult.Err("Could not parse notebook: " + e.Message); }
    }

    public static string DescribeBinary(string path)
    {
        try { var d = Run("file", new[] { "-b", path }, 10000, out var c); return c == 0 ? d.Trim() : ""; } catch { return ""; }
    }
}
