using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SwyfInstaller
{
    // ---------- GitHub releases API (only the fields we use) ----------

    [DataContract]
    internal class GhRelease
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "body")] public string Body { get; set; }
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "assets")] public List<GhAsset> Assets { get; set; }
    }

    [DataContract]
    internal class GhAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "label")] public string Label { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "browser_download_url")] public string Url { get; set; }
    }

    /// <summary>A newer file found in the releases: the hub itself or a mod's .swyfmod.</summary>
    public sealed class UpdateOffer
    {
        public string Id { get; set; }        // mod id, or null for the hub
        public Version Version { get; set; }
        public string FileName { get; set; }
        public string Url { get; set; }
        public long Size { get; set; }
        public string VersionText => Version.ToString(3);
    }

    /// <summary>
    /// Looks for newer versions of the hub and of installed mods in the GitHub releases of dexhauls/swyf-mods.
    /// Only checks; nothing is downloaded until the player clicks. Mods are assets named &lt;id&gt;-&lt;version&gt;.swyfmod,
    /// the hub is SWYF-Hub.exe whose version is in the release name ("… SWYF Mod Hub 1.1.3"), the asset label or the notes.
    /// </summary>
    public static class Updater
    {
        private const string ReleasesUrl = "https://api.github.com/repos/dexhauls/swyf-mods/releases";
        private const string HubAsset = "SWYF-Hub.exe";
        private static readonly Regex ModAsset = new Regex(@"^(?<id>[A-Za-z0-9_-]+?)-(?<v>\d+(\.\d+){1,3})\.swyfmod$", RegexOptions.IgnoreCase);
        private static readonly Regex HubVersionInText = new Regex(@"(?:SWYF[\s-]*(?:Mod[\s-]*)?Hub)\s*v?(?<v>\d+(\.\d+){1,3})", RegexOptions.IgnoreCase);
        private static readonly Regex AnyVersion = new Regex(@"\d+(\.\d+){1,3}");

        public sealed class Result
        {
            public UpdateOffer Hub { get; set; }
            public Dictionary<string, UpdateOffer> Mods { get; set; } = new Dictionary<string, UpdateOffer>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The releases, or null without internet / over the API limit / on anything unexpected.</summary>
        internal static async Task<List<GhRelease>> FetchReleasesAsync()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("SWYF-Mod-Hub/" + SwyfInstaller.Hub.Version);
                    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    using (var response = await http.GetAsync(ReleasesUrl).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return null; // 403/429 = rate limit, 404, …
                        using (var s = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            return (List<GhRelease>)new DataContractJsonSerializer(typeof(List<GhRelease>)).ReadObject(s);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return null;
            }
        }

        /// <summary>Compares the releases with the running hub and the installed mods (id → version from mod.json).</summary>
        internal static Result FindUpdates(IEnumerable<GhRelease> releases, Version hubVersion, IDictionary<string, string> installedMods)
        {
            var result = new Result();
            var installed = installedMods
                .Select(kv => (Id: kv.Key, Version: Parse(kv.Value)))
                .Where(x => x.Version != null)
                .ToDictionary(x => x.Id, x => x.Version, StringComparer.OrdinalIgnoreCase);
            hubVersion = Normalize(hubVersion);

            foreach (var release in releases.Where(r => r != null && !r.Draft && !r.Prerelease))
            {
                foreach (var asset in release.Assets ?? new List<GhAsset>())
                {
                    if (asset?.Name == null || asset.Url == null) continue;
                    if (asset.Name.Equals(HubAsset, StringComparison.OrdinalIgnoreCase))
                    {
                        var v = HubVersionOf(release, asset);
                        if (v != null && v > hubVersion && (result.Hub == null || v > result.Hub.Version))
                            result.Hub = new UpdateOffer { Version = v, FileName = asset.Name, Url = asset.Url, Size = asset.Size };
                        continue;
                    }
                    var m = ModAsset.Match(asset.Name);
                    if (!m.Success) continue;
                    var id = m.Groups["id"].Value;
                    var version = Parse(m.Groups["v"].Value);
                    if (version == null || !installed.TryGetValue(id, out var have) || version <= have) continue;
                    if (result.Mods.TryGetValue(id, out var best) && best.Version >= version) continue;
                    result.Mods[id] = new UpdateOffer { Id = id, Version = version, FileName = asset.Name, Url = asset.Url, Size = asset.Size };
                }
            }
            return result;
        }

        private static Version HubVersionOf(GhRelease release, GhAsset asset)
        {
            if (!string.IsNullOrEmpty(asset.Label))
            {
                var m = AnyVersion.Match(asset.Label);
                if (m.Success) return Parse(m.Value);
            }
            foreach (var text in new[] { release.Name, release.Body })
            {
                var m = HubVersionInText.Match(text ?? "");
                if (m.Success) return Parse(m.Groups["v"].Value);
            }
            return null;
        }

        internal static Version Parse(string s) =>
            System.Version.TryParse((s ?? "").Trim().TrimStart('v', 'V'), out var v) ? Normalize(v) : null;

        /// <summary>1.0 and 1.0.0 are the same version (System.Version treats a missing part as lower).</summary>
        private static Version Normalize(Version v) =>
            new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

        // ---------- Downloads (only after a click) ----------

        /// <summary>Downloads a release file to <paramref name="target"/> (via a .part file), reporting 0..1.</summary>
        public static async Task DownloadAsync(UpdateOffer offer, string target, IProgress<double> progress)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var part = target + ".part";
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SWYF-Mod-Hub/" + SwyfInstaller.Hub.Version);
                using (var response = await http.GetAsync(offer.Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? offer.Size;
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                    {
                        var buffer = new byte[1 << 16];
                        long done = 0;
                        int read;
                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        {
                            await file.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                            done += read;
                            if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
                        }
                    }
                }
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
        }

        /// <summary>Downloads a mod update into a temp folder; returns the .swyfmod path to install like a double-clicked file.</summary>
        public static async Task<string> DownloadModAsync(UpdateOffer offer, IProgress<double> progress)
        {
            var target = Path.Combine(Path.GetTempPath(), "swyf-hub", Guid.NewGuid().ToString("N"), offer.FileName);
            await DownloadAsync(offer, target, progress).ConfigureAwait(false);
            return target;
        }

        // ---------- Replacing the running hub ----------

        private static string CurrentExe => Assembly.GetExecutingAssembly().Location;

        /// <summary>
        /// Downloads the new hub next to the running one as .new, moves the running exe aside as .old (Windows allows
        /// renaming a running program's file), puts the new one in its place and starts it. The caller then closes
        /// this hub. The new hub deletes the .old on start (see <see cref="CleanUpAfterUpdate"/>) and, if it isn't the
        /// installed copy, refreshes %LOCALAPPDATA%\SWYF Mod Hub (ShellIntegration.UpdateInstalledCopy).
        /// </summary>
        public static async Task DownloadAndStartHubAsync(UpdateOffer offer, IProgress<double> progress)
        {
            var exe = CurrentExe;
            var fresh = exe + ".new";
            var old = exe + ".old";
            await DownloadAsync(offer, fresh, progress).ConfigureAwait(false);

            var got = FileVersionInfo.GetVersionInfo(fresh);
            if (got.FileMajorPart == 0 && got.FileMinorPart == 0)
            {
                File.Delete(fresh);
                throw new InvalidDataException(Lang.T("Stažený hub je poškozený. Zkus to znovu.", "The downloaded hub is corrupted. Please try again."));
            }

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Move(fresh, exe);
            }
            catch
            {
                File.Move(old, exe); // put the running one back
                throw;
            }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        }

        /// <summary>Deletes what a previous self-update left next to this exe (the old hub may still be closing, then next time).</summary>
        public static void CleanUpAfterUpdate()
        {
            foreach (var leftover in new[] { CurrentExe + ".old", CurrentExe + ".new", CurrentExe + ".new.part" })
            {
                try
                {
                    if (File.Exists(leftover)) File.Delete(leftover);
                }
                catch (Exception) { /* still running; next time */ }
            }
        }
    }
}
