using System.Text.Json;

namespace Soundsys.Engine.Updates;

/// <summary>Semantic-version comparison (2.0 rules: numeric core, pre-release identifiers, build metadata ignored).</summary>
public static class SemVer
{
    /// <summary>Negative if a &lt; b, 0 if equal, positive if a &gt; b. Accepts a leading "v".</summary>
    public static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        for (var i = 0; i < 3; i++)
        {
            var c = coreA[i].CompareTo(coreB[i]);
            if (c != 0)
            {
                return c;
            }
        }

        if (preA.Length == 0 || preB.Length == 0)
        {
            return preB.Length.CompareTo(preA.Length); // a release sorts after its pre-releases
        }

        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var na = long.TryParse(preA[i], out var ia);
            var nb = long.TryParse(preB[i], out var ib);
            var c = (na, nb) switch
            {
                (true, true) => ia.CompareTo(ib),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(preA[i], preB[i]),
            };
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return preA.Length.CompareTo(preB.Length);
    }

    public static bool IsPrerelease(string version) => Split(version).Pre.Length > 0;

    /// <summary>Normalises "v0.1.0-alpha+abc" to "0.1.0-alpha".</summary>
    public static string Normalize(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? v[..plus] : v;
    }

    private static (long[] Core, string[] Pre) Split(string version)
    {
        var v = Normalize(version);
        var dash = v.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? v[..dash] : v;
        var pre = dash >= 0 ? v[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries) : [];
        var parts = core.Split('.');
        var nums = new long[3];
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            _ = long.TryParse(parts[i], out nums[i]);
        }

        return (nums, pre);
    }
}

public sealed record UpdateCheckResult(bool UpdateAvailable, string? LatestVersion, string? Url, string Message);

/// <summary>Checks GitHub Releases for a newer soundsys. Read-only, anonymous, no personal data sent.</summary>
public sealed class UpdateChecker(HttpClient http, string owner = "ishaanmakam", string repo = "soundsys")
{
    public const string ReleasesPage = "https://github.com/ishaanmakam/soundsys/releases";

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var current = SemVer.Normalize(currentVersion);

        // Pre-release builds see pre-releases; stable builds only see stable releases.
        var includePre = SemVer.IsPrerelease(current);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=20");
            request.Headers.UserAgent.ParseAdd("soundsys-update-check");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, null, null, "Couldn't check for updates right now.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            string? bestTag = null;
            string? bestUrl = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                {
                    continue;
                }

                var pre = release.TryGetProperty("prerelease", out var p) && p.GetBoolean();
                if (pre && !includePre)
                {
                    continue;
                }

                var tag = release.GetProperty("tag_name").GetString();
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                if (bestTag is null || SemVer.Compare(tag, bestTag) > 0)
                {
                    bestTag = tag;
                    bestUrl = release.TryGetProperty("html_url", out var u) ? u.GetString() : ReleasesPage;
                }
            }

            if (bestTag is null || SemVer.Compare(bestTag, current) <= 0)
            {
                return new UpdateCheckResult(false, bestTag is null ? null : SemVer.Normalize(bestTag), bestUrl, $"You're up to date ({current}).");
            }

            var latest = SemVer.Normalize(bestTag);
            return new UpdateCheckResult(true, latest, bestUrl ?? ReleasesPage, $"soundsys {latest} is available.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new UpdateCheckResult(false, null, null, "Couldn't check for updates. Are you online?");
        }
    }
}
