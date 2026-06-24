using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FishXIVItemReader.Update
{
    public sealed class PluginUpdateService
    {
        private const string UpdateSourceResourceName = "FishXIVItemReader.Update.UpdateSource.json";
        private const string DefaultManifestUrl = "https://raw.githubusercontent.com/Cyrus-Vance/FishXIVItemReader/main/FishXIVItemReader/Update/FishXIVItemReader.update.json";
        private const int BufferSize = 81920;

        public PluginUpdateService()
        {
            CurrentVersion = NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));
            ManifestUrl = LoadManifestUrl();
        }

        public Version CurrentVersion { get; }

        public string CurrentVersionText
        {
            get { return FormatVersion(CurrentVersion); }
        }

        public string ManifestUrl { get; }

        public async Task<PluginUpdateCheckResult> CheckAsync(CancellationToken token)
        {
            var manifestJson = await DownloadStringAsync(ManifestUrl, token).ConfigureAwait(false);
            var manifest = Deserialize<PluginUpdateManifest>(manifestJson);
            ValidateManifest(manifest);

            var latestVersion = NormalizeVersion(ParseVersion(manifest.Version));
            return new PluginUpdateCheckResult(
                manifest,
                latestVersion,
                latestVersion.CompareTo(CurrentVersion) > 0);
        }

        public async Task<string> DownloadUpdateAsync(
            PluginUpdateManifest manifest,
            string downloadDirectory,
            CancellationToken token)
        {
            ValidateManifest(manifest);

            Directory.CreateDirectory(downloadDirectory);
            var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(manifest.FileName)
                ? "FishXIVItemReader-v" + manifest.Version + ".zip"
                : manifest.FileName);
            var finalPath = Path.Combine(downloadDirectory, fileName);
            var tempPath = finalPath + ".download";

            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            await DownloadFileAsync(manifest.DownloadUrl, tempPath, token).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                var actualSha256 = ComputeSha256(tempPath);
                if (!string.Equals(actualSha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPath);
                    throw new InvalidOperationException("更新包校验失败。");
                }
            }

            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(tempPath, finalPath);
            return finalPath;
        }

        public static string FormatVersion(Version version)
        {
            var normalized = NormalizeVersion(version);
            return string.Format(
                "{0}.{1}.{2}.{3}",
                normalized.Major,
                normalized.Minor,
                normalized.Build,
                normalized.Revision);
        }

        private static void ValidateManifest(PluginUpdateManifest manifest)
        {
            if (manifest == null ||
                manifest.SchemaVersion != 1 ||
                string.IsNullOrWhiteSpace(manifest.Version) ||
                string.IsNullOrWhiteSpace(manifest.DownloadUrl))
            {
                throw new InvalidOperationException("更新清单格式异常。");
            }

            ParseVersion(manifest.Version);
            if (!Uri.IsWellFormedUriString(manifest.DownloadUrl, UriKind.Absolute))
            {
                throw new InvalidOperationException("更新包地址异常。");
            }
        }

        private static async Task<string> DownloadStringAsync(string url, CancellationToken token)
        {
            var request = CreateRequest(url);
            using (token.Register(request.Abort))
            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        private static async Task DownloadFileAsync(string url, string targetPath, CancellationToken token)
        {
            var request = CreateRequest(url);
            using (token.Register(request.Abort))
            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var input = response.GetResponseStream())
            using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                }
            }
        }

        private static HttpWebRequest CreateRequest(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.UserAgent = "FishXIVItemReader/" + FormatVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));
            request.Timeout = 15000;
            request.ReadWriteTimeout = 30000;
            return request;
        }

        private static Version ParseVersion(string value)
        {
            Version version;
            if (!Version.TryParse(value.Trim().TrimStart('v', 'V'), out version))
            {
                throw new InvalidOperationException("更新清单版本异常。");
            }

            return NormalizeVersion(version);
        }

        private static Version NormalizeVersion(Version version)
        {
            return new Version(
                version.Major,
                version.Minor,
                Math.Max(0, version.Build),
                Math.Max(0, version.Revision));
        }

        private static T Deserialize<T>(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        private static string LoadManifestUrl()
        {
            try
            {
                var source = Deserialize<PluginUpdateSource>(ReadEmbeddedResource(UpdateSourceResourceName));
                if (source != null &&
                    source.SchemaVersion == 1 &&
                    Uri.IsWellFormedUriString(source.ManifestUrl, UriKind.Absolute))
                {
                    return source.ManifestUrl;
                }
            }
            catch
            {
            }

            return DefaultManifestUrl;
        }

        private static string ReadEmbeddedResource(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException();
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var value in hash)
                {
                    sb.Append(value.ToString("x2"));
                }

                return sb.ToString();
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(fileName.Length);
            foreach (var ch in fileName)
            {
                sb.Append(Array.IndexOf(invalidChars, ch) >= 0 ? '_' : ch);
            }

            return sb.ToString();
        }
    }

    public sealed class PluginUpdateCheckResult
    {
        public PluginUpdateCheckResult(
            PluginUpdateManifest manifest,
            Version latestVersion,
            bool updateAvailable)
        {
            Manifest = manifest;
            LatestVersion = latestVersion;
            UpdateAvailable = updateAvailable;
        }

        public PluginUpdateManifest Manifest { get; }

        public Version LatestVersion { get; }

        public bool UpdateAvailable { get; }
    }

    [DataContract]
    public sealed class PluginUpdateManifest
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "plugin")]
        public string Plugin { get; set; }

        [DataMember(Name = "version")]
        public string Version { get; set; }

        [DataMember(Name = "downloadUrl")]
        public string DownloadUrl { get; set; }

        [DataMember(Name = "fileName")]
        public string FileName { get; set; }

        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; }

        [DataMember(Name = "releasePageUrl")]
        public string ReleasePageUrl { get; set; }

        [DataMember(Name = "publishedAt")]
        public string PublishedAt { get; set; }

        [DataMember(Name = "notes")]
        public string Notes { get; set; }
    }

    [DataContract]
    internal sealed class PluginUpdateSource
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "manifestUrl")]
        public string ManifestUrl { get; set; }
    }
}
