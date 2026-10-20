# Third-party notices

FileOrbit includes the following open-source components. Each is used under its own licence,
reproduced or referenced below. Licence texts are available at the linked project pages and in each
NuGet package.

## Bundled libraries

| Component | Version | Licence | Used for |
|---|---|---|---|
| [PdfPig](https://github.com/UglyToad/PdfPig) | 0.1.16 | Apache License 2.0 | PDF text and table extraction |
| [ClosedXML](https://github.com/ClosedXML/ClosedXML) | 0.105.1 | MIT | Reading and writing Excel workbooks |
| [Open XML SDK](https://github.com/dotnet/Open-XML-SDK) (DocumentFormat.OpenXml) | 3.5.1 | MIT | Reading and writing Word documents |
| [Markdig](https://github.com/xoofx/markdig) | 1.4.1 | BSD 2-Clause | Markdown rendering |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 4.153.1 | MIT (Skia: BSD 3-Clause) | WebP encoding, PDF creation, SVG rendering |
| [Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia) | 5.2.3 | MIT | SVG parsing and rendering |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | 1.31.0 | MIT | Runs the background-removal model locally |
| [U²-Net](https://github.com/xuebinqin/U-2-Net) model `u2netp.onnx` (ONNX export from [rembg](https://github.com/danielgatis/rembg)) | — | Apache License 2.0 (model); MIT (rembg) | Background removal. MD5 `8e83ca70e441ab06c318d82300c84806` |
| .NET runtime and Windows Desktop runtime | 10.0 | MIT | Application runtime (self-contained builds) |

Transitive dependencies of these packages (for example `ExcelNumberFormat`, `RBush`, `SixLabors.Fonts`
used by ClosedXML, and `ShimSkiaSharp`/`Svg.Model` used by Svg.Skia) are distributed under their own
permissive licences as declared in their NuGet packages.

## Windows components (not redistributed)

FileOrbit calls APIs that are part of Windows: Windows Imaging Component, Windows.Data.Pdf,
Windows Media Foundation (Windows.Media.Transcoding) and Windows.Media.Ocr. Optional codec
extensions from the Microsoft Store (HEIF, WebP, AV1) are used if the user installed them.

## Optional external programs (never bundled, never downloaded automatically)

FileOrbit detects and uses these programs only when the user has installed them. FileOrbit does not
distribute them, and their licences apply to the user's own installation.

| Program | Licence | Used for |
|---|---|---|
| Microsoft Office (Word, Excel, PowerPoint) | Proprietary (user's Office licence) | High-fidelity Office conversions, PDF to editable Word |
| [LibreOffice](https://www.libreoffice.org/) | MPL 2.0 | Office and OpenDocument conversions |
| [FFmpeg](https://ffmpeg.org/) | LGPL 2.1+ / GPL 2+ (depends on build) | Audio and video formats beyond Windows' built-in codecs |
| [7-Zip](https://www.7-zip.org/) | LGPL 2.1 with unRAR restriction | 7Z/RAR and encrypted archives |
| [Ghostscript](https://ghostscript.com/) | AGPL 3.0 (or commercial) | PDF optimisation and PDF/A (via the optional Ghostscript engine plugin) |

FileOrbit invokes these programs as separate processes and does not link to them.
