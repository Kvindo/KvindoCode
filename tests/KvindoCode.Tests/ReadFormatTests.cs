using System.IO.Compression;
using System.Text;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

public class ReadFormatTests
{
    static void Zip(string path, params (string name, string content)[] files)
    {
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (n, c) in files) { var e = z.CreateEntry(n); using var w = new StreamWriter(e.Open(), new UTF8Encoding(false)); w.Write(c); }
    }

    [Fact]
    public async Task Images_are_attached_not_rejected_and_text_models_get_a_clear_message()
    {
        using var sb = new Sandbox();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        File.WriteAllBytes(Path.Combine(sb.Project, "pic.png"), png);
        var (r, _) = await Helpers.Run(sb, new ReadTool(), new { file_path = "pic.png" });
        Assert.False(r.IsError, r.Output);
        Assert.NotNull(r.Images); Assert.Equal(png, r.Images![0]);

        var noVision = Helpers.Ctx(sb, new KvindoCode.Core.Agent.AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction()) { ModelLookup = _ => new KvindoCode.Core.Llm.ModelInfo("m", 1000, 100, false) });
        var r2 = await new ReadTool().RunAsync((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("{\"file_path\":\"pic.png\"}")!, noVision, default);
        Assert.True(r2.IsError); Assert.Contains("cannot see images", r2.Output);
    }

    [Fact]
    public async Task Docx_xlsx_pptx_and_notebooks_are_extracted_as_text()
    {
        using var sb = new Sandbox();
        Zip(Path.Combine(sb.Project, "a.docx"), ("word/document.xml", """<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Contract</w:t></w:r></w:p><w:p><w:r><w:t>Party A pays </w:t></w:r><w:r><w:t>100 RUB</w:t></w:r></w:p></w:body></w:document>"""));
        Zip(Path.Combine(sb.Project, "b.xlsx"),
            ("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Costs" sheetId="1" r:id="rId1"/></sheets></workbook>"""),
            ("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>"""),
            ("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Server</t></si><si><t>msk-1</t></si></sst>"""),
            ("xl/worksheets/sheet1.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1"><v>42</v></c></row><row r="2"><c r="A2" t="s"><v>1</v></c><c r="B2"><f>B1*2</f><v>84</v></c></row></sheetData></worksheet>"""));
        Zip(Path.Combine(sb.Project, "c.pptx"), ("ppt/slides/slide1.xml", """<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:p><a:r><a:t>Kvindo Cloud</a:t></a:r></a:p></p:sld>"""));
        File.WriteAllText(Path.Combine(sb.Project, "d.ipynb"), """{"cells":[{"cell_type":"markdown","source":["# Title"]},{"cell_type":"code","source":["print(1+1)"],"outputs":[{"output_type":"stream","text":["2\n"]}]}]}""");

        var docx = (await Helpers.Run(sb, new ReadTool(), new { file_path = "a.docx" })).res;
        Assert.False(docx.IsError, docx.Output); Assert.Contains("# Contract", docx.Output); Assert.Contains("Party A pays 100 RUB", docx.Output);
        var xlsx = (await Helpers.Run(sb, new ReadTool(), new { file_path = "b.xlsx" })).res;
        Assert.Contains("Sheet: Costs", xlsx.Output); Assert.Contains("A1: Server | B1: 42", xlsx.Output); Assert.Contains("B2: 84 (=B1*2)", xlsx.Output);
        var pptx = (await Helpers.Run(sb, new ReadTool(), new { file_path = "c.pptx" })).res;
        Assert.Contains("Slide 1", pptx.Output); Assert.Contains("Kvindo Cloud", pptx.Output);
        var nb = (await Helpers.Run(sb, new ReadTool(), new { file_path = "d.ipynb" })).res;
        Assert.Contains("# Title", nb.Output); Assert.Contains("print(1+1)", nb.Output); Assert.Contains("→ 2", nb.Output);
    }

    [Fact]
    public async Task Pdf_text_is_extracted_with_page_ranges_when_poppler_is_installed()
    {
        if (!File.Exists("/usr/bin/pdftotext")) return;
        using var sb = new Sandbox();
        // tiny hand-built one-page PDF with correct xref offsets
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length 44 >>\nstream\nBT /F1 18 Tf 20 50 Td (Hello PDF 4711) Tj ET\nendstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var pdf = new StringBuilder("%PDF-1.4\n"); var offs = new List<int>();
        for (int i = 0; i < objs.Length; i++) { offs.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offs) pdf.Append($"{o:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(Path.Combine(sb.Project, "t.pdf"), pdf.ToString(), Encoding.ASCII);
        var (r, _) = await Helpers.Run(sb, new ReadTool(), new { file_path = "t.pdf" });
        Assert.False(r.IsError, r.Output); Assert.Contains("Hello PDF 4711", r.Output);
        Assert.True((await Helpers.Run(sb, new ReadTool(), new { file_path = "t.pdf", pages = "x" })).res.IsError);
    }

    [Fact]
    public async Task Other_binaries_get_a_helpful_error_naming_the_type()
    {
        using var sb = new Sandbox();
        File.WriteAllBytes(Path.Combine(sb.Project, "blob.bin"), new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0, 1, 2, 3 });
        var (r, _) = await Helpers.Run(sb, new ReadTool(), new { file_path = "blob.bin" });
        Assert.True(r.IsError); Assert.Contains("binary file", r.Output); Assert.Contains("Bash", r.Output);
    }
}
