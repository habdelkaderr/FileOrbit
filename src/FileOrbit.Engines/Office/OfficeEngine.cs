using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FileOrbit.Core.Capabilities;
using FileOrbit.Engines.Pdf;
using FileOrbit.Engines.Text;
using Microsoft.Win32;

namespace FileOrbit.Engines.Office;

/// <summary>
/// Uses an installed Microsoft Office (Word, Excel, PowerPoint) through COM automation for the
/// highest-fidelity Office conversions: DOCX/XLSX/PPTX to PDF, PDF to DOCX (Word's PDF reflow),
/// slides to images or video, and legacy/OpenDocument formats.
/// Macros are force-disabled, files are opened read-only, and dialogs are suppressed.
/// </summary>
public sealed partial class OfficeEngine : ConversionEngineBase
{
    public const string EngineId = "ms-office";

    // Never a real password: makes Office fail fast on protected files instead of showing a dialog.
    private const string NoPassword = "\u0001FileOrbit-no-password";

    private static readonly string[] WordSources = ["docx", "doc", "rtf", "odt", "txt", "html", "md", "pdf"];
    private static readonly string[] WordTargets = ["pdf", "docx", "doc", "rtf", "odt", "html", "txt", "png", "jpeg"];
    private static readonly string[] ExcelSources = ["xlsx", "xls", "ods", "csv"];
    private static readonly string[] ExcelTargets = ["pdf", "xlsx", "xls", "ods", "csv"];
    private static readonly string[] PowerPointSources = ["pptx", "ppt", "odp"];
    private static readonly string[] PowerPointTargets = ["pdf", "pptx", "ppt", "odp", "png", "jpeg", "mp4"];

    private static readonly SemaphoreSlim WordGate = new(1, 1);
    private static readonly SemaphoreSlim ExcelGate = new(1, 1);
    private static readonly SemaphoreSlim PowerPointGate = new(1, 1);

    private readonly Dictionary<OfficeApp, string?> _installed = [];

    private enum OfficeApp
    {
        Word,
        Excel,
        PowerPoint,
    }

    public override string Id => EngineId;

    public override string DisplayName => "Microsoft Office";

    public override string Description => "Uses your installed Word, Excel and PowerPoint for the most faithful Office conversions. Runs locally; macros are disabled.";

    public override bool IsExternal => true;

    public override DependencyInfo Dependency { get; } = new(
        "Microsoft Office",
        "Best-quality DOCX/XLSX/PPTX to PDF, PDF to editable Word, slides to images and video",
        "Proprietary (your Microsoft 365 / Office licence)",
        "https://www.microsoft.com/microsoft-365",
        InstallNotes: "Word, Excel and PowerPoint are each detected separately. LibreOffice is a free alternative for most document conversions.");

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    protected override EngineStatus CheckStatus()
    {
        _installed[OfficeApp.Word] = FindApp("Word.Application", "WINWORD.EXE");
        _installed[OfficeApp.Excel] = FindApp("Excel.Application", "EXCEL.EXE");
        _installed[OfficeApp.PowerPoint] = FindApp("PowerPoint.Application", "POWERPNT.EXE");
        var found = _installed.Where(p => p.Value is not null).Select(p => p.Key.ToString()).ToList();
        if (found.Count == 0)
        {
            return EngineStatus.Unavailable("Microsoft Office is not installed.");
        }

        var anyPath = _installed.Values.First(v => v is not null)!;
        var version = FileVersionInfo.GetVersionInfo(anyPath).ProductVersion;
        return EngineStatus.Available($"Ready: {string.Join(", ", found)}", version, Path.GetDirectoryName(anyPath));
    }

    public override CapabilityStatus GetCapabilityStatus(ConversionCapability capability)
    {
        var status = GetStatus();
        if (!status.IsAvailable)
        {
            return status.ToCapabilityStatus();
        }

        var app = AppFor(capability.SourceFormatId);
        return _installed.GetValueOrDefault(app) is null
            ? CapabilityStatus.Missing($"Microsoft {(app == OfficeApp.PowerPoint ? "PowerPoint" : app.ToString())} is not installed.")
            : CapabilityStatus.Ok;
    }

    protected override IEnumerable<ConversionCapability> DeclareCapabilities()
    {
        foreach (var source in WordSources)
        {
            var targets = source == "pdf" ? ["docx", "rtf", "odt"] : WordTargets;
            foreach (var target in targets.Where(t => t != source))
            {
                yield return Cap(source, target, WordFidelity(source, target), quality: WordQuality(source, target)) with
                {
                    Shape = target is "png" or "jpeg" ? OutputShape.FileOrFolder : OutputShape.File,
                    Note = WordNote(source, target),
                    Options = target switch
                    {
                        "pdf" => [PdfAOption],
                        "png" or "jpeg" => [WindowsPdfEngine.ResolutionOption, WindowsPdfEngine.PagesOption],
                        _ => [],
                    },
                    Cost = source == "pdf" ? ResourceCost.High : ResourceCost.Medium,
                    SupportsCancellation = true,
                };
            }
        }

        foreach (var source in ExcelSources)
        {
            foreach (var target in ExcelTargets.Where(t => t != source))
            {
                yield return Cap(source, target, target == "csv" ? Fidelity.Extraction : SameFamily(source, target) ? Fidelity.Lossless : Fidelity.LayoutSensitive,
                    quality: target == "csv" || (source == "csv" && target == "xlsx") ? 40 : source == "ods" ? 75 : 90) with
                {
                    Note = target switch
                    {
                        "pdf" => "Uses each sheet's print settings",
                        "csv" => "Exports the first sheet only",
                        _ => null,
                    },
                    Cost = ResourceCost.Medium,
                };
            }
        }

        foreach (var source in PowerPointSources)
        {
            foreach (var target in PowerPointTargets.Where(t => t != source))
            {
                yield return Cap(source, target, target switch
                {
                    "png" or "jpeg" or "mp4" => Fidelity.Rasterized,
                    _ when SameFamily(source, target) => Fidelity.Lossless,
                    _ => Fidelity.LayoutSensitive,
                }, quality: source == "odp" ? 75 : 90) with
                {
                    Shape = target is "png" or "jpeg" ? OutputShape.FileOrFolder : OutputShape.File,
                    Note = target switch
                    {
                        "png" or "jpeg" => "One image per slide",
                        "mp4" => "Uses slide timings and narration if present; rendering can take several minutes",
                        _ => null,
                    },
                    Options = target switch
                    {
                        "pdf" => [PdfAOption],
                        "png" or "jpeg" => [WindowsPdfEngine.ResolutionOption],
                        "mp4" => [VideoResolutionOption, SlideDurationOption],
                        _ => [],
                    },
                    Cost = target == "mp4" ? ResourceCost.High : ResourceCost.Medium,
                };
            }
        }
    }

    public override async Task ConvertAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        var app = AppFor(context.Input.Format.Id);
        var gate = app switch
        {
            OfficeApp.Word => WordGate,
            OfficeApp.Excel => ExcelGate,
            _ => PowerPointGate,
        };

        context.Report(null, $"Waiting for Microsoft {app}");
        await gate.WaitAsync(cancellationToken);
        try
        {
            switch (app)
            {
                case OfficeApp.Word:
                    await ConvertWithWordAsync(context, cancellationToken);
                    break;
                case OfficeApp.Excel:
                    await RunGuardedAsync("EXCEL", guard => ExcelConvert(context, guard), context, cancellationToken, Timeout);
                    break;
                default:
                    await RunGuardedAsync("POWERPNT", guard => PowerPointConvert(context, guard, cancellationToken), context, cancellationToken,
                        context.Target.Id == "mp4" ? TimeSpan.FromHours(1) : Timeout);
                    break;
            }
        }
        finally
        {
            gate.Release();
        }

        context.Report(1, "Done");
    }

    private async Task ConvertWithWordAsync(ConversionContext context, CancellationToken cancellationToken)
    {
        var work = Path.Combine(context.StagingDirectory, ".work");
        Directory.CreateDirectory(work);
        try
        {
            var input = PrepareWordInput(context, work);
            var target = context.Target.Id;
            var rendersPages = target is "png" or "jpeg";
            var output = rendersPages ? Path.Combine(work, "pages.pdf") : context.PrimaryOutputPath;
            var exportTarget = rendersPages ? "pdf" : target;

            context.Report(null, context.Input.Format.Id == "pdf" ? "Word is converting the PDF (this can take a while)" : "Converting with Word");
            await RunGuardedAsync("WINWORD", guard => WordConvert(input, output, exportTarget, context, guard), context, cancellationToken, Timeout);

            if (target == "html")
            {
                InlineHtmlAssets(output, context.StagingDirectory);
            }

            if (rendersPages)
            {
                await WindowsPdfEngine.RenderPdfAsync(output, null, context, cancellationToken);
            }
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    /// <summary>Markdown is rendered to HTML first; HTML is sanitised so Word never fetches remote content.</summary>
    private static string PrepareWordInput(ConversionContext context, string work)
    {
        var source = context.Input.Format.Id;
        if (source is not ("md" or "html"))
        {
            return context.Input.Path;
        }

        var text = File.ReadAllText(context.Input.Path);
        var title = Path.GetFileNameWithoutExtension(context.Input.Path);
        var html = source == "md" ? HtmlTools.MarkdownToHtmlDocument(text, title) : text;
        html = HtmlTools.StripRemoteResources(html, out var removed);
        if (removed > 0)
        {
            context.AddWarning("Remote images, scripts and links to online resources were not loaded (privacy).");
        }

        var path = Path.Combine(work, "source.html");
        File.WriteAllText(path, html, new UTF8Encoding(true));
        return path;
    }

    private static void WordConvert(string input, string output, string target, ConversionContext context, OfficeProcessGuard guard)
    {
        dynamic? word = null;
        dynamic? document = null;
        try
        {
            word = CreateApplication("Word.Application");
            guard.Capture();
            word.Visible = false;
            word.DisplayAlerts = 0; // wdAlertsNone
            word.AutomationSecurity = 3; // msoAutomationSecurityForceDisable
            var isPdf = Path.GetExtension(input).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
            using var pdfNotice = isPdf ? WordPdfPromptSuppressor.Suppress((string)word.Version) : null;

            try
            {
                document = word.Documents.Open(
                    FileName: input,
                    ConfirmConversions: false,
                    ReadOnly: true,
                    AddToRecentFiles: false,
                    PasswordDocument: string.IsNullOrEmpty(context.Password) ? NoPassword : context.Password,
                    PasswordTemplate: NoPassword,
                    Revert: false,
                    WritePasswordDocument: NoPassword,
                    WritePasswordTemplate: NoPassword,
                    Visible: false,
                    OpenAndRepair: false,
                    NoEncodingDialog: true);
            }
            catch (COMException ex)
            {
                throw TranslateOpenError(ex, context);
            }

            switch (target)
            {
                case "pdf":
                    document.ExportAsFixedFormat(
                        OutputFileName: output,
                        ExportFormat: 17, // wdExportFormatPDF
                        OpenAfterExport: false,
                        OptimizeFor: 0, // wdExportOptimizeForPrint
                        IncludeDocProps: true,
                        KeepIRM: true,
                        CreateBookmarks: 1, // wdExportCreateHeadingBookmarks
                        DocStructureTags: true,
                        BitmapMissingFonts: true,
                        UseISO19005_1: context.Options.GetBool("pdfa", false));
                    break;
                case "txt":
                    document.SaveAs2(FileName: output, FileFormat: 2, AddToRecentFiles: false, Encoding: 65001); // wdFormatText, UTF-8
                    break;
                case "html":
                    document.SaveAs2(FileName: output, FileFormat: 10, AddToRecentFiles: false, Encoding: 65001); // wdFormatFilteredHTML
                    break;
                default:
                    var format = target switch
                    {
                        "docx" => 16, // wdFormatDocumentDefault
                        "doc" => 0, // wdFormatDocument97
                        "rtf" => 6, // wdFormatRTF
                        "odt" => 23, // wdFormatOpenDocumentText
                        _ => throw new ConversionException($"Word cannot produce {target.ToUpperInvariant()}."),
                    };
                    document.SaveAs2(FileName: output, FileFormat: format, AddToRecentFiles: false);
                    break;
            }
        }
        finally
        {
            CloseQuietly(() => document?.Close(SaveChanges: 0));
            CloseQuietly(() => word?.Quit(SaveChanges: 0));
            Release(document);
            Release(word);
        }
    }

    private static void ExcelConvert(ConversionContext context, OfficeProcessGuard guard)
    {
        dynamic? excel = null;
        dynamic? workbook = null;
        try
        {
            excel = CreateApplication("Excel.Application");
            guard.Capture(new IntPtr((int)excel.Hwnd));
            excel.Visible = false;
            excel.DisplayAlerts = false;
            excel.AutomationSecurity = 3;
            excel.ScreenUpdating = false;
            excel.EnableEvents = false;
            excel.AskToUpdateLinks = false;

            try
            {
                workbook = excel.Workbooks.Open(
                    Filename: context.Input.Path,
                    UpdateLinks: 0,
                    ReadOnly: true,
                    Password: string.IsNullOrEmpty(context.Password) ? NoPassword : context.Password,
                    // No WriteResPassword: Excel rejects one for files without write reservation, and the
                    // workbook is opened read-only anyway.
                    IgnoreReadOnlyRecommended: true,
                    Notify: false,
                    AddToMru: false,
                    Local: true);
            }
            catch (COMException ex)
            {
                throw TranslateOpenError(ex, context);
            }

            var output = context.PrimaryOutputPath;
            switch (context.Target.Id)
            {
                case "pdf":
                    workbook.ExportAsFixedFormat(Type: 0, Filename: output, Quality: 0, IncludeDocProperties: true, IgnorePrintAreas: false, OpenAfterPublish: false);
                    break;
                default:
                    var format = context.Target.Id switch
                    {
                        "xlsx" => 51, // xlOpenXMLWorkbook
                        "xls" => 56, // xlExcel8
                        "ods" => 60, // xlOpenDocumentSpreadsheet
                        "csv" => 62, // xlCSVUTF8
                        _ => throw new ConversionException($"Excel cannot produce {context.Target.Name}."),
                    };
                    if (format == 62 && (int)workbook.Worksheets.Count > 1)
                    {
                        context.AddWarning("Excel exports only the first sheet to CSV.");
                        workbook.Worksheets[1].Activate();
                    }

                    workbook.SaveAs(Filename: output, FileFormat: format, AddToMru: false);
                    break;
            }
        }
        finally
        {
            CloseQuietly(() => workbook?.Close(SaveChanges: false));
            CloseQuietly(() => excel?.Quit());
            Release(workbook);
            Release(excel);
        }
    }

    private static void PowerPointConvert(ConversionContext context, OfficeProcessGuard guard, CancellationToken cancellationToken)
    {
        if (OfficeFiles.IsEncryptedPackage(context.Input.Path) && string.IsNullOrEmpty(context.Password))
        {
            // PowerPoint shows a blocking password dialog for protected files, so ask first.
            throw new PasswordRequiredException(wasIncorrect: false);
        }

        dynamic? app = null;
        dynamic? presentation = null;
        try
        {
            app = CreateApplication("PowerPoint.Application");
            guard.Capture();
            app.DisplayAlerts = 1; // ppAlertsNone
            app.AutomationSecurity = 3;

            var fileName = string.IsNullOrEmpty(context.Password) ? context.Input.Path : $"{context.Input.Path}::{context.Password}::";
            try
            {
                presentation = app.Presentations.Open(FileName: fileName, ReadOnly: -1, Untitled: 0, WithWindow: 0);
            }
            catch (COMException ex)
            {
                throw TranslateOpenError(ex, context);
            }

            var output = context.PrimaryOutputPath;
            switch (context.Target.Id)
            {
                case "pdf" when context.Options.GetBool("pdfa", false):
                    // All arguments positionally: late binding cannot convert omitted optional enum arguments.
                    presentation.ExportAsFixedFormat(
                        output,
                        2, // ppFixedFormatTypePDF
                        2, // ppFixedFormatIntentPrint
                        0, // FrameSlides: msoFalse
                        1, // ppPrintHandoutVerticalFirst
                        1, // ppPrintOutputSlides
                        0, // PrintHiddenSlides: msoFalse
                        null, // PrintRange
                        1, // ppPrintAll
                        string.Empty, // SlideShowName
                        true, // IncludeDocProperties
                        true, // KeepIRMSettings
                        true, // DocStructureTags
                        true, // BitmapMissingFonts
                        true); // UseISO19005_1 (PDF/A)
                    break;
                case "pdf":
                    presentation.SaveAs(FileName: output, FileFormat: 32); // ppSaveAsPDF
                    break;
                case "png" or "jpeg":
                    ExportSlides(presentation, context, cancellationToken);
                    break;
                case "mp4":
                    CreateVideo(presentation, output, context, cancellationToken);
                    break;
                default:
                    var format = context.Target.Id switch
                    {
                        "pptx" => 24, // ppSaveAsOpenXMLPresentation
                        "ppt" => 1, // ppSaveAsPresentation
                        "odp" => 35, // ppSaveAsOpenDocumentPresentation
                        _ => throw new ConversionException($"PowerPoint cannot produce {context.Target.Name}."),
                    };
                    presentation.SaveAs(FileName: output, FileFormat: format);
                    break;
            }
        }
        finally
        {
            CloseQuietly(() => presentation?.Close());
            if (!guard.WasAlreadyRunning)
            {
                // Only quit PowerPoint if FileOrbit started it; never close the user's presentations.
                CloseQuietly(() =>
                {
                    if (app is not null && (int)app.Presentations.Count == 0)
                    {
                        app.Quit();
                    }
                });
            }

            Release(presentation);
            Release(app);
        }
    }

    private static void ExportSlides(dynamic presentation, ConversionContext context, CancellationToken cancellationToken)
    {
        var dpi = context.Options.GetInt("dpi", 150);
        var width = (int)Math.Round((double)presentation.PageSetup.SlideWidth * dpi / 72.0);
        var height = (int)Math.Round((double)presentation.PageSetup.SlideHeight * dpi / 72.0);
        var count = (int)presentation.Slides.Count;
        if (count == 0)
        {
            throw new ConversionException("The presentation has no slides.");
        }

        var filter = context.Target.Id == "jpeg" ? "JPG" : "PNG";
        var digits = Math.Max(2, count.ToString().Length);
        for (var i = 1; i <= count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Report((double)(i - 1) / count, $"Exporting slide {i} of {count}");
            var name = count == 1
                ? context.OutputBaseName + context.Target.PrimaryExtension
                : $"{context.OutputBaseName} - slide {i.ToString().PadLeft(digits, '0')}{context.Target.PrimaryExtension}";
            presentation.Slides[i].Export(context.OutputPath(name), filter, width, height);
        }
    }

    private static void CreateVideo(dynamic presentation, string output, ConversionContext context, CancellationToken cancellationToken)
    {
        var resolution = context.Options.GetInt("height", 1080);
        var duration = context.Options.GetInt("slideSeconds", 5);
        presentation.CreateVideo(FileName: output, UseTimingsAndNarrations: true, DefaultSlideDuration: duration, VertResolution: resolution, FramesPerSecond: 30, Quality: 85);
        var started = DateTime.UtcNow;
        while (true)
        {
            var status = (int)presentation.CreateVideoStatus; // 1 in progress, 2 queued, 3 done, 4 failed
            if (status == 3)
            {
                return;
            }

            if (status == 4)
            {
                throw new ConversionException("PowerPoint could not create the video.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            context.Report(null, $"PowerPoint is rendering the video ({(DateTime.UtcNow - started).TotalSeconds:0} s)");
            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// Runs Office work on an STA thread. Cancellation and timeouts end the Office process that
    /// FileOrbit started, which aborts the pending COM call.
    /// </summary>
    private static async Task RunGuardedAsync(string processName, Action<OfficeProcessGuard> work, ConversionContext context, CancellationToken cancellationToken, TimeSpan timeout)
    {
        var guard = new OfficeProcessGuard(processName);
        var task = StaWorker.Run(() => work(guard), $"FileOrbit {processName}");
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var registration = cancellationToken.Register(guard.KillOwned);
        using var timeoutRegistration = timeoutSource.Token.Register(guard.KillOwned);
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (timeoutSource.IsCancellationRequested)
        {
            throw new ConversionException($"Microsoft Office did not finish within {timeout.TotalMinutes:0} minutes and was stopped.", ex.Message, ex);
        }
        catch (COMException ex)
        {
            throw new ConversionException("Microsoft Office could not complete the conversion.", $"0x{ex.HResult:X8}: {ex.Message}", ex);
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex)
        {
            throw new ConversionException("This version of Microsoft Office does not support the requested operation.", ex.Message, ex);
        }
    }

    private static dynamic CreateApplication(string progId)
    {
        var type = Type.GetTypeFromProgID(progId) ?? throw new ConversionException($"{progId} is not registered.");
        return Activator.CreateInstance(type) ?? throw new ConversionException($"Could not start {progId}.");
    }

    private static Exception TranslateOpenError(COMException ex, ConversionContext context)
    {
        var message = ex.Message;
        if (message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return new PasswordRequiredException(wasIncorrect: !string.IsNullOrEmpty(context.Password));
        }

        return new ConversionException("Microsoft Office could not open the file. It may be damaged or in an unsupported format.", $"0x{ex.HResult:X8}: {message}", ex);
    }

    /// <summary>Replaces images saved beside a Word HTML export with inline data URIs, giving one self-contained file.</summary>
    private static void InlineHtmlAssets(string htmlPath, string staging)
    {
        if (!File.Exists(htmlPath))
        {
            return;
        }

        var assetFolders = Directory.GetDirectories(staging).Where(d => !Path.GetFileName(d).StartsWith('.')).ToList();
        if (assetFolders.Count == 0)
        {
            return;
        }

        var html = File.ReadAllText(htmlPath);
        html = ImageSource().Replace(html, match =>
        {
            var relative = Uri.UnescapeDataString(match.Groups["src"].Value).Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(staging, relative));
            if (!full.StartsWith(staging, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                return match.Value;
            }

            var mime = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream",
            };
            return $"{match.Groups["prefix"].Value}data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(full))}{match.Groups["quote"].Value}";
        });
        File.WriteAllText(htmlPath, html, new UTF8Encoding(false));
        foreach (var folder in assetFolders)
        {
            TryDeleteDirectory(folder);
        }
    }

    private static string? FindApp(string progId, string exeName)
    {
        if (Type.GetTypeFromProgID(progId) is null)
        {
            return null;
        }

        using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
        var path = key?.GetValue(null) as string;
        return path is not null && File.Exists(path) ? path : null;
    }

    private static OfficeApp AppFor(string sourceFormat) =>
        ExcelSources.Contains(sourceFormat) ? OfficeApp.Excel
        : PowerPointSources.Contains(sourceFormat) ? OfficeApp.PowerPoint
        : OfficeApp.Word;

    private static Fidelity WordFidelity(string source, string target) => (source, target) switch
    {
        (_, "txt") => Fidelity.Extraction,
        (_, "png" or "jpeg") => Fidelity.Rasterized,
        _ when SameFamily(source, target) => Fidelity.Lossless,
        _ => Fidelity.LayoutSensitive,
    };

    /// <summary>Microsoft's own current and legacy formats round-trip without layout changes.</summary>
    private static bool SameFamily(string source, string target) =>
        (source, target) is ("docx", "doc") or ("doc", "docx") or ("xlsx", "xls") or ("xls", "xlsx") or ("pptx", "ppt") or ("ppt", "pptx");

    private static int WordQuality(string source, string target) => (source, target) switch
    {
        ("pdf", _) => 85,
        ("odt", _) => 75,
        ("md" or "html" or "txt", _) => 70,
        (_, "txt") => 60,
        (_, "html") => 75,
        _ => 90,
    };

    private static string? WordNote(string source, string target) => (source, target) switch
    {
        ("pdf", _) => "Word rebuilds the PDF as an editable document; complex layouts, fonts and tables may shift",
        ("md", _) => "Markdown is rendered to HTML first",
        (_, "png" or "jpeg") => "One image per page",
        (_, "html") => "Single HTML file with embedded images",
        _ => null,
    };

    private static void CloseQuietly(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // The Office process may already be gone (cancelled or crashed).
        }
    }

    private static void Release(object? comObject)
    {
        try
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("""(?<prefix>\bsrc\s*=\s*(?<quote>["']))(?<src>(?!data:|https?:|//)[^"']+)\k<quote>""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSource();

    private static readonly OptionDescriptor PdfAOption = OptionDescriptor.Toggle("pdfa", "PDF/A (archival)", false,
        "ISO 19005-1 compliant PDF for long-term archiving") with { IsAdvanced = true };

    private static readonly OptionDescriptor VideoResolutionOption = OptionDescriptor.Choice("height", "Resolution", "1080",
        new("2160", "4K (2160p)"), new("1080", "1080p"), new("720", "720p"), new("480", "480p"));

    private static readonly OptionDescriptor SlideDurationOption = OptionDescriptor.Choice("slideSeconds", "Seconds per slide", "5",
        new("3", "3 seconds"), new("5", "5 seconds"), new("8", "8 seconds"), new("10", "10 seconds")) with
    {
        Description = "Used for slides without their own timings",
    };
}

internal static class OfficeFiles
{
    /// <summary>Encrypted OOXML files are stored inside an OLE compound file instead of a ZIP package.</summary>
    public static bool IsEncryptedPackage(string path)
    {
        Span<byte> header = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == 8 && header.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })
               && Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx";
    }
}
