using System;
using System.IO;
using System.IO.Compression;
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
        private const string PluginAssemblyFileName = "FishXIVItemReader.dll";
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

        public Task<PluginUpdatePreparedInstallResult> PrepareUpdateInstallAsync(
            string updateArchivePath,
            string updateWorkingDirectory,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(updateArchivePath))
            {
                throw new ArgumentException("更新包路径异常。", "updateArchivePath");
            }

            if (string.IsNullOrWhiteSpace(updateWorkingDirectory))
            {
                throw new ArgumentException("更新工作目录异常。", "updateWorkingDirectory");
            }

            return Task.Run(
                delegate
                {
                    return PrepareUpdateInstall(updateArchivePath, updateWorkingDirectory, token);
                },
                token);
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

        private static PluginUpdatePreparedInstallResult PrepareUpdateInstall(
            string updateArchivePath,
            string updateWorkingDirectory,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (!File.Exists(updateArchivePath))
            {
                throw new FileNotFoundException("更新包不存在。", updateArchivePath);
            }

            var normalizedWorkingDirectory = NormalizeDirectoryPath(updateWorkingDirectory);
            Directory.CreateDirectory(normalizedWorkingDirectory);

            var stagingDirectory = Path.Combine(
                normalizedWorkingDirectory,
                "prepared-" + Guid.NewGuid().ToString("N"));

            try
            {
                ExtractPackage(updateArchivePath, stagingDirectory, token);

                var extractedPluginAssembly = Path.Combine(stagingDirectory, PluginAssemblyFileName);
                if (!File.Exists(extractedPluginAssembly))
                {
                    throw new InvalidOperationException("更新包缺少插件文件。");
                }

                var preparedFileCount = 0;
                foreach (var sourceFile in Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();

                    var relativePath = GetRelativePath(stagingDirectory, sourceFile);
                    if (!ShouldSkipInstallFile(relativePath))
                    {
                        preparedFileCount++;
                    }
                }

                return new PluginUpdatePreparedInstallResult(stagingDirectory, preparedFileCount);
            }
            catch
            {
                TryDeleteDirectory(stagingDirectory);
                throw;
            }
        }

        private static void ExtractPackage(
            string updateArchivePath,
            string extractDirectory,
            CancellationToken token)
        {
            Directory.CreateDirectory(extractDirectory);
            var normalizedExtractDirectory = NormalizeDirectoryPath(extractDirectory);

            using (var archive = ZipFile.OpenRead(updateArchivePath))
            {
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(entry.FullName))
                    {
                        continue;
                    }

                    var entryName = entry.FullName.Replace('\\', '/');
                    if (Path.IsPathRooted(entryName))
                    {
                        throw new InvalidOperationException("更新包路径异常。");
                    }

                    var targetPath = Path.GetFullPath(Path.Combine(normalizedExtractDirectory, entryName));
                    if (!IsPathInside(targetPath, normalizedExtractDirectory))
                    {
                        throw new InvalidOperationException("更新包路径异常。");
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(targetPath);
                        continue;
                    }

                    var parent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    entry.ExtractToFile(targetPath, true);
                }
            }
        }

        private static bool ShouldSkipInstallFile(string relativePath)
        {
            return string.Equals(Path.GetExtension(relativePath), ".pdb", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRelativePath(string baseDirectory, string path)
        {
            var normalizedBaseDirectory = NormalizeDirectoryPath(baseDirectory);
            var normalizedPath = Path.GetFullPath(path);
            if (string.Equals(normalizedBaseDirectory, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var prefix = normalizedBaseDirectory + Path.DirectorySeparatorChar;
            if (!normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("路径不在预期目录中。");
            }

            return normalizedPath.Substring(prefix.Length);
        }

        private static bool IsPathInside(string path, string root)
        {
            var normalizedPath = Path.GetFullPath(path);
            var normalizedRoot = NormalizeDirectoryPath(root);

            return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(
                       normalizedRoot + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch
            {
            }
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

    public sealed class PluginUpdatePreparedInstallResult
    {
        public PluginUpdatePreparedInstallResult(string stagingDirectory, int preparedFileCount)
        {
            StagingDirectory = stagingDirectory;
            PreparedFileCount = preparedFileCount;
        }

        public string StagingDirectory { get; }

        public int PreparedFileCount { get; }
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
