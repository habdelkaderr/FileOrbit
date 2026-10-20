using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FileOrbit.Engines.Pdf;

/// <summary>
/// Writes a minimal, standards-conformant PowerPoint (.pptx) package in which each slide holds one
/// picture. Used for PDF to PPTX, where pages become non-editable picture slides.
/// </summary>
public static class SlideDeckWriter
{
    private const long EmuPerPoint = 12700;
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string NsRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public sealed record Slide(byte[] Png, double WidthPoints, double HeightPoints);

    public static void Write(string path, IReadOnlyList<Slide> slides)
    {
        if (slides.Count == 0)
        {
            throw new ArgumentException("A presentation needs at least one slide.", nameof(slides));
        }

        // Slide size follows the first page (clamped to PowerPoint's limits: 1–56 inches).
        var slideWidth = Math.Clamp((long)(slides[0].WidthPoints * EmuPerPoint), 914400, 51206400);
        var slideHeight = Math.Clamp((long)(slides[0].HeightPoints * EmuPerPoint), 914400, 51206400);

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        var types = new StringBuilder();
        types.Append($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="png" ContentType="image/png"/><Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/><Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/><Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/><Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>""");
        for (var i = 1; i <= slides.Count; i++)
        {
            types.Append($"""<Override PartName="/ppt/slides/slide{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>""");
        }

        types.Append("</Types>");
        Add(zip, "[Content_Types].xml", types.ToString());

        Add(zip, "_rels/.rels", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{NsRel}"><Relationship Id="rId1" Type="{RelBase}/officeDocument" Target="ppt/presentation.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>""");
        Add(zip, "docProps/core.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:creator>FileOrbit</dc:creator><dcterms:created xsi:type="dcterms:W3CDTF">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created></cp:coreProperties>""");

        var presentation = new StringBuilder($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:presentation xmlns:a="{NsA}" xmlns:r="{NsR}" xmlns:p="{NsP}"><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst><p:sldIdLst>""");
        var presentationRels = new StringBuilder($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{NsRel}"><Relationship Id="rId1" Type="{RelBase}/slideMaster" Target="slideMasters/slideMaster1.xml"/><Relationship Id="rId2" Type="{RelBase}/theme" Target="theme/theme1.xml"/>""");
        for (var i = 1; i <= slides.Count; i++)
        {
            presentation.Append(CultureInfo.InvariantCulture, $"""<p:sldId id="{255 + i}" r:id="rId{i + 2}"/>""");
            presentationRels.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{i + 2}" Type="{RelBase}/slide" Target="slides/slide{i}.xml"/>""");
        }

        presentation.Append(CultureInfo.InvariantCulture, $"""</p:sldIdLst><p:sldSz cx="{slideWidth}" cy="{slideHeight}"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>""");
        presentationRels.Append("</Relationships>");
        Add(zip, "ppt/presentation.xml", presentation.ToString());
        Add(zip, "ppt/_rels/presentation.xml.rels", presentationRels.ToString());

        const string emptyTree = """<p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>""";
        Add(zip, "ppt/slideMasters/slideMaster1.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldMaster xmlns:a="{NsA}" xmlns:r="{NsR}" xmlns:p="{NsP}"><p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill><a:effectLst/></p:bgPr></p:bg><p:spTree>{emptyTree}</p:spTree></p:cSld><p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/><p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst></p:sldMaster>""");
        Add(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{NsRel}"><Relationship Id="rId1" Type="{RelBase}/slideLayout" Target="../slideLayouts/slideLayout1.xml"/><Relationship Id="rId2" Type="{RelBase}/theme" Target="../theme/theme1.xml"/></Relationships>""");
        Add(zip, "ppt/slideLayouts/slideLayout1.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldLayout xmlns:a="{NsA}" xmlns:r="{NsR}" xmlns:p="{NsP}" type="blank" preserve="1"><p:cSld name="Blank"><p:spTree>{emptyTree}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>""");
        Add(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{NsRel}"><Relationship Id="rId1" Type="{RelBase}/slideMaster" Target="../slideMasters/slideMaster1.xml"/></Relationships>""");
        Add(zip, "ppt/theme/theme1.xml", Theme);

        for (var i = 1; i <= slides.Count; i++)
        {
            var slide = slides[i - 1];
            // Fit the page inside the slide, preserving aspect ratio, centred.
            var pageWidth = slide.WidthPoints * EmuPerPoint;
            var pageHeight = slide.HeightPoints * EmuPerPoint;
            var scale = Math.Min(slideWidth / pageWidth, slideHeight / pageHeight);
            var width = (long)(pageWidth * scale);
            var height = (long)(pageHeight * scale);
            var x = (slideWidth - width) / 2;
            var y = (slideHeight - height) / 2;

            Add(zip, $"ppt/slides/slide{i}.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sld xmlns:a="{NsA}" xmlns:r="{NsR}" xmlns:p="{NsP}"><p:cSld><p:spTree>{emptyTree}<p:pic><p:nvPicPr><p:cNvPr id="2" name="Page {i}" descr="Page {i}"/><p:cNvPicPr><a:picLocks noChangeAspect="1"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed="rId2"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="{width}" cy="{height}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr></p:pic></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>""");
            Add(zip, $"ppt/slides/_rels/slide{i}.xml.rels", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{NsRel}"><Relationship Id="rId1" Type="{RelBase}/slideLayout" Target="../slideLayouts/slideLayout1.xml"/><Relationship Id="rId2" Type="{RelBase}/image" Target="../media/image{i}.png"/></Relationships>""");
            var entry = zip.CreateEntry($"ppt/media/image{i}.png", CompressionLevel.NoCompression);
            using var imageStream = entry.Open();
            imageStream.Write(slide.Png);
        }
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private const string Theme = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="FileOrbit"><a:themeElements><a:clrScheme name="Office"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="44546A"/></a:dk2><a:lt2><a:srgbClr val="E7E6E6"/></a:lt2><a:accent1><a:srgbClr val="4472C4"/></a:accent1><a:accent2><a:srgbClr val="ED7D31"/></a:accent2><a:accent3><a:srgbClr val="A5A5A5"/></a:accent3><a:accent4><a:srgbClr val="FFC000"/></a:accent4><a:accent5><a:srgbClr val="5B9BD5"/></a:accent5><a:accent6><a:srgbClr val="70AD47"/></a:accent6><a:hlink><a:srgbClr val="0563C1"/></a:hlink><a:folHlink><a:srgbClr val="954F72"/></a:folHlink></a:clrScheme><a:fontScheme name="Office"><a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme><a:fmtScheme name="Office"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w="6350"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>""";
}
