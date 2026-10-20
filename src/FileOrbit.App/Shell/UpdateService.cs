using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Threading;
using FileOrbit.Core.Logging;
using FileOrbit.Core.Updates;

namespace FileOrbit.App.Shell;

public enum UpdateCheckOutcome
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Release, string Message);

/// <summary>
/// Checks GitHub for a newer FileOrbit release: shortly after start-up and then once a day while the
/// app runs, plus on demand from Settings. It reads only the latest version number and release page
/// (one HTTPS request, no file data or identifiers). Updating stays a deliberate user action: the
/// user is shown the release page and downloads the installer themselves.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private readonly AppServices _services;
    private readonly Action<ReleaseInfo> _notify;
    private readonly DispatcherTimer _timer;
    private readonly HttpClient _http;

    public UpdateService(AppServices services, Action<ReleaseInfo> notify)
    {
        _services = services;
        _notify = notify;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FileOrbit", CurrentVersion.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        // First check shortly after start-up (not during it), then re-evaluate hourly whether a day has passed.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(1);
            if (_services.Settings.CheckForUpdates && IsDue())
            {
                await CheckAsync(userInitiated: false);
            }
        };
        _timer.Start();
    }

    /// <summary>Raised on the UI thread whenever the known update state changes.</summary>
    public event EventHandler? StateChanged;

    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>The newer release found by the last check, if any (survives restarts).</summary>
    public ReleaseInfo? AvailableUpdate
    {
        get
        {
            var settings = _services.Settings;
            if (settings.AvailableUpdateTag is not { } tag || ReleaseFeed.TryParseVersion(tag) is not { } version || !ReleaseFeed.IsNewer(version, CurrentVersion))
            {
                return null;
            }

            var page = Uri.TryCreate(settings.AvailableUpdateUrl, UriKind.Absolute, out var url) && ReleaseFeed.IsTrustedPage(url) ? url : ReleaseFeed.ReleasesPage;
            return new ReleaseInfo(version, tag, $"FileOrbit {tag}", page, null);
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(bool userInitiated)
    {
        var settings = _services.Settings;
        try
        {
            using var response = await _http.GetAsync(ReleaseFeed.LatestReleaseApi);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Record(new UpdateCheckResult(UpdateCheckOutcome.UpToDate, null, "No releases have been published yet."));
            }

            response.EnsureSuccessStatusCode();
            var release = ReleaseFeed.Parse(await response.Content.ReadAsStringAsync());
            if (release is null)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.Failed, null, "GitHub returned release information FileOrbit could not read.");
            }

            if (!ReleaseFeed.IsNewer(release.Version, CurrentVersion))
            {
                settings.AvailableUpdateTag = null;
                settings.AvailableUpdateUrl = null;
                return Record(new UpdateCheckResult(UpdateCheckOutcome.UpToDate, release, $"You have the latest version ({CurrentVersion.ToString(3)})."));
            }

            settings.AvailableUpdateTag = release.Tag;
            settings.AvailableUpdateUrl = release.PageUrl.AbsoluteUri;
            if (!userInitiated && settings.NotifiedUpdateTag != release.Tag)
            {
                settings.NotifiedUpdateTag = release.Tag;
                _notify(release);
            }

            return Record(new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, release, $"FileOrbit {release.Version.ToString(3)} is available (you have {CurrentVersion.ToString(3)})."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _services.Log.Warn("Update check failed", ("error", ex.Message));
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, null,
                userInitiated ? "Could not reach GitHub to check for updates. Check your internet connection and try again." : ex.Message);
        }
    }

    public static void OpenReleasePage(ReleaseInfo? release) =>
        ShellActions.OpenUrl((release?.PageUrl is { } page && ReleaseFeed.IsTrustedPage(page) ? page : ReleaseFeed.ReleasesPage).AbsoluteUri);

    public void Dispose()
    {
        _timer.Stop();
        _http.Dispose();
    }

    private bool IsDue() =>
        _services.Settings.LastUpdateCheck is not { } last || DateTimeOffset.Now - last >= CheckInterval;

    private UpdateCheckResult Record(UpdateCheckResult result)
    {
        _services.Settings.LastUpdateCheck = DateTimeOffset.Now;
        _services.SaveSettings();
        _services.Log.Info("Update check", ("outcome", result.Outcome), ("latest", result.Release?.Tag));
        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }
}
