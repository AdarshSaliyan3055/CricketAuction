using System.Security;
using System.Text;
using CricketAuction.Domain;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PdfSharpCore.Drawing;

namespace CricketAuction.Infrastructure;
public sealed class PowerPointService(AppLog log) : IPowerPointService
{
    private const string Ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    private const string Group = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";
    private const string Colors = "<p:clrMap accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" bg1=\"lt1\" bg2=\"lt2\" folHlink=\"folHlink\" hlink=\"hlink\" tx1=\"dk1\" tx2=\"dk2\"/>";
    private static string Escape(string? value) => SecurityElement.Escape(value ?? "") ?? "";
    private static void Xml(OpenXmlPart part, string xml) { using var stream = part.GetStream(FileMode.Create, FileAccess.Write); var bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + xml); stream.Write(bytes); }
    public void Generate(string path, IReadOnlyList<Player> players, string tournament, int round)
    {
        if (players.Count == 0) throw new InvalidOperationException("No players selected for the presentation.");
        AtomicFile.Write(path, temporary =>
        {
            using (var document = PresentationDocument.Create(temporary, PresentationDocumentType.Presentation))
            {
                var presentation = document.AddPresentationPart();
                var master = presentation.AddNewPart<SlideMasterPart>("rIdMaster"); var layout = master.AddNewPart<SlideLayoutPart>("rIdLayout");
                layout.AddPart(master, "rIdMaster");
                Xml(layout, $"<p:sldLayout {Ns} type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\"><p:spTree>{Group}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
                Xml(master, $"<p:sldMaster {Ns}><p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"081F1B\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg><p:spTree>{Group}</p:spTree></p:cSld>{Colors}<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rIdLayout\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>");
                var theme = master.AddNewPart<ThemePart>("rIdTheme"); Xml(theme, Theme());
                var slides = new StringBuilder();
                for (var i = 0; i < players.Count; i++)
                {
                    var player = players[i]; var id = "rIdSlide" + (i + 1); var slide = presentation.AddNewPart<SlidePart>(id); slide.AddPart(layout, "rIdLayout");
                    var shapes = new StringBuilder(Group);
                    shapes.Append(Rect(2, "Top accent", 0, 0, 12192000, 90000, "E6B64B"));
                    shapes.Append(Text(3, "Tournament", tournament.ToUpperInvariant(), 500000, 250000, 11000000, 420000, 1900, "7CE2BD", true));
                    shapes.Append(Text(4, "Player status", $"{player.Status.ToString().ToUpperInvariant()} PLAYER   |   ROUND {round}   |   PLAYER {i + 1:00} / {players.Count:00}", 500000, 690000, 11000000, 380000, 1800, "E6B64B", true));
                    shapes.Append(Rect(5, "Photo mat", 450000, 1250000, 4100000, 4950000, "183B33"));
                    if (player.Photo is { Length: > 8 } photo && ImageService.IsImage(photo))
                    {
                        var part = slide.AddImagePart(photo[0] == 137 ? ImagePartType.Png : ImagePartType.Jpeg, "rIdPhoto"); using var input = new MemoryStream(photo); part.FeedData(input);
                        // The image is letterboxed using its encoded dimensions; faces are never stretched.
                        var (width, height) = ImageDimensions.Read(photo); const long boxX = 540000, boxY = 1340000, boxW = 3920000, boxH = 4770000;
                        var scale = Math.Min(boxW / (double)width, boxH / (double)height); var w = (long)(width * scale); var h = (long)(height * scale);
                        shapes.Append($"<p:pic><p:nvPicPr><p:cNvPr id=\"6\" name=\"Player photo\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rIdPhoto\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x=\"{boxX + (boxW - w) / 2}\" y=\"{boxY + (boxH - h) / 2}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");
                    }
                    else shapes.Append(Text(6, "Photo placeholder", "PHOTO\nNOT AVAILABLE", 650000, 3200000, 3700000, 1000000, 2500, "C0D7CF", true));
                    shapes.Append(Text(7, "Player name", player.FullName, 4950000, 1420000, 6650000, 1000000, 5200, "FFFFFF", true));
                    shapes.Append(Text(8, "Role", string.IsNullOrWhiteSpace(player.PlayerType) ? "PLAYER" : player.PlayerType, 4950000, 2520000, 6500000, 620000, 3000, "7CE2BD", true));
                    shapes.Append(Rect(9, "Detail divider", 4950000, 3420000, 6650000, 45000, "E6B64B"));
                    shapes.Append(Text(10, "Age label", "AGE", 4950000, 3780000, 2200000, 320000, 1600, "A8C1B7", true));
                    shapes.Append(Text(11, "Age", player.Age?.ToString() ?? "NA", 4950000, 4100000, 2500000, 520000, 2700, "FFFFFF", true));
                    shapes.Append(Text(12, "City label", "CITY", 8100000, 3780000, 3200000, 320000, 1600, "A8C1B7", true));
                    shapes.Append(Text(13, "City", string.IsNullOrWhiteSpace(player.AreaCity) ? "NA" : player.AreaCity, 8100000, 4100000, 3500000, 620000, 2600, "FFFFFF", true));
                    shapes.Append(Rect(14, "Row divider", 4950000, 4800000, 6650000, 20000, "29483F"));
                    shapes.Append(Text(15, "Batting label", "BATTING", 4950000, 5040000, 1700000, 350000, 1600, "A8C1B7", true));
                    shapes.Append(Text(16, "Batting", string.IsNullOrWhiteSpace(player.BattingStyle) ? "NA" : player.BattingStyle, 6900000, 4920000, 4700000, 520000, 2300, "FFFFFF", true));
                    shapes.Append(Rect(17, "Row divider", 4950000, 5580000, 6650000, 20000, "29483F"));
                    shapes.Append(Text(18, "Bowling label", "BOWLING", 4950000, 5810000, 1700000, 350000, 1600, "A8C1B7", true));
                    shapes.Append(Text(19, "Bowling", string.IsNullOrWhiteSpace(player.BowlingStyle) ? "NA" : player.BowlingStyle, 6900000, 5690000, 4700000, 520000, 2300, "FFFFFF", true));
                    if (player.Status == AuctionStatus.Sold)
                        shapes.Append(Text(20, "Sale details", $"SOLD TO  {player.TeamName}  |  {player.SoldPrice}", 4950000, 6300000, 6650000, 380000, 1800, "E6B64B", true));
                    Xml(slide, $"<p:sld {Ns}><p:cSld><p:spTree>{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
                    slides.Append($"<p:sldId id=\"{256 + i}\" r:id=\"{id}\"/>");
                }
                Xml(presentation, $"<p:presentation {Ns}><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rIdMaster\"/></p:sldMasterIdLst><p:sldIdLst>{slides}</p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\" type=\"screen16x9\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
            }
            using var check = PresentationDocument.Open(temporary, false);
            var errors = new OpenXmlValidator().Validate(check).Take(3).ToList();
            if (errors.Count > 0) throw new InvalidOperationException("PowerPoint validation failed: " + string.Join("; ", errors.Select(e => e.Description)));
        });
        log.Write($"PowerPoint export slides={players.Count}");
    }
    private static string Rect(int id, string name, long x, long y, long w, long h, string fill) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val=\"{fill}\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr></p:sp>";
    private static string Text(int id, string name, string text, long x, long y, long w, long h, int size, string color, bool bold = false)
    {
        using var measure = XGraphics.CreateMeasureContext(new XSize(1000, 1000), XGraphicsUnit.Point, XPageDirection.Downwards);
        var layout = ExportTextLayout.Fit(text, w / 12700d - 8, h / 12700d - 2, size / 100d, Math.Min(12, size / 100d),
            (value, fontSize) => measure.MeasureString(value, new XFont("Arial", fontSize, bold ? XFontStyle.Bold : XFontStyle.Regular)).Width);
        var fontHundredths = (int)Math.Round(layout.FontSize * 100);
        var lineHundredths = (int)Math.Ceiling(layout.LineHeight * 100);
        var paragraphs = string.Join("", layout.Lines.Select(line => $"<a:p><a:pPr algn=\"l\"><a:lnSpc><a:spcPts val=\"{lineHundredths}\"/></a:lnSpc><a:spcBef><a:spcPts val=\"0\"/></a:spcBef><a:spcAft><a:spcPts val=\"0\"/></a:spcAft></a:pPr><a:r><a:rPr lang=\"en-IN\" sz=\"{fontHundredths}\"{(bold ? " b=\"1\"" : "")}><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:latin typeface=\"Arial\"/></a:rPr><a:t>{Escape(line)}</a:t></a:r><a:endParaRPr lang=\"en-IN\" sz=\"{fontHundredths}\"/></a:p>"));
        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr><p:txBody><a:bodyPr wrap=\"none\" lIns=\"25400\" rIns=\"25400\" tIns=\"0\" bIns=\"0\" anchor=\"t\" vertOverflow=\"clip\" horzOverflow=\"clip\"><a:noAutofit/></a:bodyPr><a:lstStyle/>{paragraphs}</p:txBody></p:sp>";
    }
    private static string Theme()
    {
        var colors = string.Join("", new[] { ("dk1", "102A26"), ("lt1", "FFFFFF"), ("dk2", "12382F"), ("lt2", "EEF5F1"), ("accent1", "7CE2BD"), ("accent2", "F6C46A"), ("accent3", "5FA8D3"), ("accent4", "C98474"), ("accent5", "A3A0D8"), ("accent6", "80B192"), ("hlink", "5FA8D3"), ("folHlink", "A3A0D8") }.Select(c => $"<a:{c.Item1}><a:srgbClr val=\"{c.Item2}\"/></a:{c.Item1}>"));
        const string fill = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        return $"<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Cricket Auction\"><a:themeElements><a:clrScheme name=\"Auction\">{colors}</a:clrScheme><a:fontScheme name=\"Segoe\"><a:majorFont><a:latin typeface=\"Segoe UI\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Segoe UI\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme><a:fmtScheme name=\"Auction\"><a:fillStyleLst>{fill}{fill}{fill}</a:fillStyleLst><a:lnStyleLst><a:ln w=\"9525\">{fill}</a:ln><a:ln w=\"25400\">{fill}</a:ln><a:ln w=\"38100\">{fill}</a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst>{fill}{fill}{fill}</a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>";
    }
}
internal static class ImageDimensions
{
    public static (int Width, int Height) Read(byte[] data)
    {
        if (data[0] == 137 && data.Length >= 24) return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4)));
        for (var i = 2; i + 8 < data.Length;)
        {
            if (data[i] != 0xff) break;
            var marker = data[i + 1]; var length = (data[i + 2] << 8) + data[i + 3];
            if (marker is 0xc0 or 0xc1 or 0xc2) return ((data[i + 7] << 8) + data[i + 8], (data[i + 5] << 8) + data[i + 6]);
            if (length < 2) break; i += 2 + length;
        }
        return (1, 1);
    }
}
