using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

class Build : NukeBuild
{
    const string PluginName = "FishXIVItemReader";
    const string UpdateManifestFileName = "FishXIVItemReader.update.json";
    const string UpdateManifestBranch = "main";

    public static int Main()
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return Execute<Build>(x => x.Compile);
    }

    [Parameter("Build configuration used for plugin compilation.")]
    readonly Configuration Configuration = Configuration.Release;

    [Parameter("ACT installation directory used only as the compile-time reference source.")]
    readonly AbsolutePath ActInstallDir = SelectDefaultActInstallDir();

    [Parameter("Local ACT plugin directory updated by the Local target.")]
    readonly AbsolutePath LocalPluginDirectory = (AbsolutePath)@"D:\FFXIVACTPlugin\FishXIVItemReader";

    [Parameter("Release version used by Package/Github. Defaults to Properties/AssemblyVersion.cs.")]
    readonly string? Version;

    [Parameter("GitHub repository in owner/name form. Defaults to the origin remote.")]
    readonly string? GitHubRepository;

    [Parameter("Release notes used by Github.")]
    readonly string? ReleaseNotes;

    [Parameter("Version part incremented by Github. 1=0.0.0.x, 2=0.0.x.0, 3=0.x.0.0, 4=x.0.0.0.")]
    readonly int? VersionPart;

    AbsolutePath ProjectFile => RootDirectory / "FishXIVItemReader" / "FishXIVItemReader.csproj";
    AbsolutePath AssemblyVersionFile => RootDirectory / "FishXIVItemReader" / "Properties" / "AssemblyVersion.cs";
    AbsolutePath SourceUpdateManifestFile => RootDirectory / "Update" / UpdateManifestFileName;
    AbsolutePath PluginOutputDirectory => RootDirectory / "FishXIVItemReader" / "bin" / Configuration.ToString() / "net48";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath AllowedLocalPluginRoot => (AbsolutePath)@"D:\FFXIVACTPlugin";

    Target Restore => _ => _
        .Executes(() =>
        {
            RestorePlugin();
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            CompilePlugin();
        });

    Target Local => _ => _
        .Description(@"Builds the plugin and publishes it to D:\FFXIVACTPlugin\FishXIVItemReader.")
        .DependsOn(Compile)
        .Executes(() =>
        {
            EnsureActReferenceExists();
            EnsurePluginOutputExists();
            EnsureCleanLocalPluginDirectory(LocalPluginDirectory);
            CopyPluginOutput(PluginOutputDirectory, LocalPluginDirectory);

            Console.WriteLine($"ACT reference directory: {ActInstallDir}");
            Console.WriteLine($"Local ACT plugin directory: {LocalPluginDirectory}");
            Console.WriteLine($"Load this DLL in ACT: {LocalPluginDirectory / "FishXIVItemReader.dll"}");
        });

    Target Package => _ => _
        .Description("Builds a release zip for local verification.")
        .DependsOn(Compile)
        .Executes(() =>
        {
            EnsurePluginOutputExists();
            var version = GetReleaseVersion();
            CreateReleasePackage(version);
        });

    Target Github => _ => _
        .Description("Increments version, updates source manifest, and publishes the plugin zip to GitHub Releases.")
        .Executes(() =>
        {
            EnsureGitWorkingTreeCleanForGitHubPublish();

            var version = GetGitHubReleaseVersion();
            var repository = GetGitHubRepository();
            var notes = GetReleaseNotes(version);

            UpdateAssemblyVersions(version);
            RestorePlugin();
            CompilePlugin();
            EnsurePluginOutputExists();
            var package = CreateReleasePackage(version);
            WriteSourceUpdateManifest(version, repository, package.FileName, package.Sha256, notes);
            CommitAndPushUpdateMetadata(version);

            var tag = GetReleaseTag(version);
            var title = $"{PluginName} {version}";

            RunProcess("gh", new[] { "--version" }, RootDirectory);
            RunProcess("gh", new[] { "auth", "status", "--hostname", "github.com" }, RootDirectory);

            var existingRelease = RunProcess(
                "gh",
                new[] { "release", "view", tag, "--repo", repository },
                RootDirectory,
                allowNonZeroExit: true);

            if (existingRelease.ExitCode == 0)
            {
                RunProcess(
                    "gh",
                    new[] { "release", "upload", tag, package.Path.ToString(), "--repo", repository, "--clobber" },
                    RootDirectory);
                RunProcess(
                    "gh",
                    new[] { "release", "edit", tag, "--repo", repository, "--title", title, "--notes", notes },
                    RootDirectory);
            }
            else
            {
                RunProcess(
                    "gh",
                    new[] { "release", "create", tag, package.Path.ToString(), "--repo", repository, "--title", title, "--notes", notes },
                    RootDirectory);
            }

            Console.WriteLine($"Published release: https://github.com/{repository}/releases/tag/{tag}");
            Console.WriteLine($"Update manifest: {GetRawUpdateManifestUrl(repository)}");
        });

    void RestorePlugin()
    {
        DotNetRestore(s => s
            .SetProjectFile(ProjectFile)
            .SetProperty("ActInstallDir", ActInstallDir));
    }

    void CompilePlugin()
    {
        DotNetBuild(s => s
            .SetProjectFile(ProjectFile)
            .SetConfiguration(Configuration)
            .SetProperty("ActInstallDir", ActInstallDir)
            .EnableNoRestore());
    }

    ReleasePackage CreateReleasePackage(string version)
    {
        Directory.CreateDirectory(ArtifactsDirectory);

        var packagePath = GetPackagePath(version);
        var staleManifestPath = ArtifactsDirectory / UpdateManifestFileName;

        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }
        if (File.Exists(staleManifestPath))
        {
            File.Delete(staleManifestPath);
        }

        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            foreach (var sourceFile in Directory.GetFiles(PluginOutputDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(PluginOutputDirectory, sourceFile);
                if (ShouldSkipPluginOutputFile(relativePath))
                {
                    continue;
                }

                archive.CreateEntryFromFile(sourceFile, relativePath, CompressionLevel.Optimal);
            }
        }

        var sha256 = ComputeSha256(packagePath);
        Console.WriteLine($"Release package: {packagePath}");
        Console.WriteLine($"SHA256: {sha256}");
        return new ReleasePackage(packagePath, packagePath.Name, sha256);
    }

    string GetReleaseVersion()
    {
        if (!string.IsNullOrWhiteSpace(Version))
        {
            return NormalizeVersion(Version);
        }

        return GetAssemblyVersion();
    }

    string GetGitHubReleaseVersion()
    {
        if (!string.IsNullOrWhiteSpace(Version))
        {
            return NormalizeVersion(Version);
        }

        return IncrementVersion(GetAssemblyVersion(), GetVersionPart());
    }

    string GetAssemblyVersion()
    {
        var assemblyVersion = File.ReadAllText(AssemblyVersionFile, Encoding.UTF8);
        var match = Regex.Match(
            assemblyVersion,
            @"AssemblyVersion\(""(?<version>[^""]+)""\)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException($"AssemblyVersion was not found in {AssemblyVersionFile}.");
        }

        return NormalizeVersion(match.Groups["version"].Value);
    }

    int GetVersionPart()
    {
        var versionPart = VersionPart ?? 1;
        if (versionPart < 1 || versionPart > 4)
        {
            throw new InvalidOperationException("VersionPart must be 1, 2, 3, or 4.");
        }

        return versionPart;
    }

    static string IncrementVersion(string version, int versionPart)
    {
        var normalized = System.Version.Parse(NormalizeVersion(version));
        var major = normalized.Major;
        var minor = normalized.Minor;
        var build = normalized.Build;
        var revision = normalized.Revision;

        switch (versionPart)
        {
            case 1:
                revision++;
                break;
            case 2:
                build++;
                revision = 0;
                break;
            case 3:
                minor++;
                build = 0;
                revision = 0;
                break;
            case 4:
                major++;
                minor = 0;
                build = 0;
                revision = 0;
                break;
            default:
                throw new InvalidOperationException("VersionPart must be 1, 2, 3, or 4.");
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}.{1}.{2}.{3}",
            major,
            minor,
            build,
            revision);
    }

    void UpdateAssemblyVersions(string version)
    {
        var assemblyVersion = File.ReadAllText(AssemblyVersionFile, Encoding.UTF8);
        assemblyVersion = Regex.Replace(
            assemblyVersion,
            @"AssemblyVersion\(""[^""]+""\)",
            $"AssemblyVersion(\"{version}\")",
            RegexOptions.CultureInvariant);
        assemblyVersion = Regex.Replace(
            assemblyVersion,
            @"AssemblyFileVersion\(""[^""]+""\)",
            $"AssemblyFileVersion(\"{version}\")",
            RegexOptions.CultureInvariant);
        File.WriteAllText(AssemblyVersionFile, assemblyVersion, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    string GetGitHubRepository()
    {
        if (!string.IsNullOrWhiteSpace(GitHubRepository))
        {
            return NormalizeGitHubRepository(GitHubRepository);
        }

        var remote = RunProcess(
            "git",
            new[] { "remote", "get-url", "origin" },
            RootDirectory).StdOut;
        return NormalizeGitHubRepository(remote);
    }

    string GetReleaseNotes(string version)
    {
        return string.IsNullOrWhiteSpace(ReleaseNotes)
            ? $"{PluginName} {version}"
            : ReleaseNotes.Trim();
    }

    void WriteSourceUpdateManifest(string version, string repository, string fileName, string sha256, string notes)
    {
        Directory.CreateDirectory(SourceUpdateManifestFile.Parent);
        File.WriteAllText(
            SourceUpdateManifestFile,
            BuildUpdateManifestJson(version, repository, fileName, sha256, notes),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"Source update manifest: {SourceUpdateManifestFile}");
    }

    AbsolutePath GetPackagePath(string version)
    {
        return ArtifactsDirectory / $"{PluginName}-v{version}.zip";
    }

    static string GetRawUpdateManifestUrl(string repository)
    {
        return $"https://raw.githubusercontent.com/{repository}/{UpdateManifestBranch}/Update/{UpdateManifestFileName}";
    }

    static string GetReleaseTag(string version)
    {
        return "v" + version;
    }

    static string NormalizeVersion(string version)
    {
        var cleanVersion = version.Trim().TrimStart('v', 'V');
        if (!System.Version.TryParse(cleanVersion, out var parsed))
        {
            throw new InvalidOperationException($"Invalid release version: {version}");
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}.{1}.{2}.{3}",
            parsed.Major,
            parsed.Minor,
            Math.Max(0, parsed.Build),
            Math.Max(0, parsed.Revision));
    }

    static string NormalizeGitHubRepository(string value)
    {
        var text = value.Trim();
        const string httpsPrefix = "https://github.com/";
        const string sshPrefix = "git@github.com:";

        if (text.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(httpsPrefix.Length);
        }
        else if (text.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(sshPrefix.Length);
        }

        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(0, text.Length - 4);
        }

        text = text.Trim('/');
        if (text.Count(ch => ch == '/') != 1)
        {
            throw new InvalidOperationException($"Unable to determine GitHub repository from: {value}");
        }

        return text;
    }

    static string BuildUpdateManifestJson(string version, string repository, string fileName, string sha256, string notes)
    {
        var tag = GetReleaseTag(version);
        var downloadUrl = $"https://github.com/{repository}/releases/download/{tag}/{fileName}";
        var releasePageUrl = $"https://github.com/{repository}/releases/tag/{tag}";
        var publishedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return "{\n" +
            "  \"schemaVersion\": 1,\n" +
            $"  \"plugin\": \"{JsonEscape(PluginName)}\",\n" +
            $"  \"version\": \"{JsonEscape(version)}\",\n" +
            $"  \"downloadUrl\": \"{JsonEscape(downloadUrl)}\",\n" +
            $"  \"fileName\": \"{JsonEscape(fileName)}\",\n" +
            $"  \"sha256\": \"{JsonEscape(sha256)}\",\n" +
            $"  \"releasePageUrl\": \"{JsonEscape(releasePageUrl)}\",\n" +
            $"  \"publishedAt\": \"{JsonEscape(publishedAt)}\",\n" +
            $"  \"notes\": \"{JsonEscape(notes)}\"\n" +
            "}\n";
    }

    void CommitAndPushUpdateMetadata(string version)
    {
        var assemblyVersionPath = ToGitPath(AssemblyVersionFile);
        var manifestPath = ToGitPath(SourceUpdateManifestFile);

        RunProcess("git", new[] { "add", assemblyVersionPath, manifestPath }, RootDirectory);
        var status = RunProcess(
            "git",
            new[] { "status", "--short", "--", assemblyVersionPath, manifestPath },
            RootDirectory);
        if (!string.IsNullOrWhiteSpace(status.StdOut))
        {
            RunProcess(
                "git",
                new[] { "commit", "-m", $"版本更新至 v{version}" },
                RootDirectory);
        }
        else
        {
            Console.WriteLine("No update metadata changes to commit.");
        }

        var branch = RunProcess("git", new[] { "branch", "--show-current" }, RootDirectory).StdOut;
        if (string.IsNullOrWhiteSpace(branch))
        {
            throw new InvalidOperationException("Unable to determine current git branch.");
        }

        RunProcess("git", new[] { "push", "origin", branch }, RootDirectory);
    }

    void EnsureGitWorkingTreeCleanForGitHubPublish()
    {
        var status = RunProcess(
            "git",
            new[] { "status", "--porcelain" },
            RootDirectory);
        if (string.IsNullOrWhiteSpace(status.StdOut))
        {
            return;
        }

        throw new InvalidOperationException(
            "发布已中止：当前存在未提交的文件改动。请先提交或清理工作区后再运行 nuke github。\n" +
            status.StdOut);
    }

    string ToGitPath(AbsolutePath path)
    {
        return Path.GetRelativePath(RootDirectory, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    static string JsonEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    void EnsureActReferenceExists()
    {
        if (File.Exists(ActInstallDir / "Advanced Combat Tracker.dll") ||
            File.Exists(ActInstallDir / "Advanced Combat Tracker.exe"))
        {
            return;
        }

        throw new FileNotFoundException(
            $"ACT assembly was not found under {ActInstallDir}. Pass --act-install-dir or set ACT_HOME.");
    }

    void EnsurePluginOutputExists()
    {
        var pluginDll = PluginOutputDirectory / "FishXIVItemReader.dll";

        if (!File.Exists(pluginDll))
        {
            throw new InvalidOperationException($"Plugin build output is incomplete: {pluginDll}");
        }
    }

    void EnsureCleanLocalPluginDirectory(AbsolutePath directory)
    {
        var fullPath = NormalizeDirectoryPath(directory);
        var allowedRoot = NormalizeDirectoryPath(AllowedLocalPluginRoot);

        if (!IsPathInside(fullPath, allowedRoot))
        {
            throw new InvalidOperationException($"Refusing to clean publish directory outside {allowedRoot}: {fullPath}");
        }

        try
        {
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }

            Directory.CreateDirectory(fullPath);
        }
        catch (IOException ex)
        {
            throw new IOException($"Unable to refresh {fullPath}. Unload the plugin from ACT and retry.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"Unable to refresh {fullPath}. Check permissions or unload the plugin from ACT and retry.", ex);
        }
    }

    static void CopyPluginOutput(AbsolutePath sourceDirectory, AbsolutePath targetDirectory)
    {
        foreach (var sourceFile in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);

            if (ShouldSkipPluginOutputFile(relativePath))
            {
                continue;
            }

            var targetFile = Path.Combine(targetDirectory, relativePath);
            var targetParent = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetParent))
            {
                Directory.CreateDirectory(targetParent);
            }

            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    static bool ShouldSkipPluginOutputFile(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);

        return string.Equals(fileName, "Advanced Combat Tracker.dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "Advanced Combat Tracker.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(fileName), ".pdb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);
    }

    static AbsolutePath SelectDefaultActInstallDir()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("ACT_HOME");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return (AbsolutePath)fromEnvironment;
        }

        var candidates = new[]
        {
            @"D:\FFXIVACT_CN",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Advanced Combat Tracker"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Advanced Combat Tracker"),
        };

        return (AbsolutePath)(candidates.FirstOrDefault(ContainsActAssembly) ?? candidates[0]);
    }

    static bool ContainsActAssembly(string directory)
    {
        return File.Exists(Path.Combine(directory, "Advanced Combat Tracker.dll")) ||
               File.Exists(Path.Combine(directory, "Advanced Combat Tracker.exe"));
    }

    static string NormalizeDirectoryPath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    static bool IsPathInside(string path, string root)
    {
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
        {
            sb.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    static ProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        AbsolutePath workingDirectory,
        bool allowNonZeroExit = false)
    {
        var executablePath = ResolveExecutablePath(fileName);
        Console.WriteLine($"> {executablePath} {string.Join(" ", arguments.Select(QuoteForDisplay))}");

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            var message = string.Equals(fileName, "gh", StringComparison.OrdinalIgnoreCase)
                ? "未找到 GitHub CLI gh。请先安装 GitHub CLI，并执行 gh auth login 后再运行 nuke github。"
                : $"未找到命令：{fileName}";
            throw new InvalidOperationException(message, ex);
        }

        if (process == null)
        {
            throw new InvalidOperationException($"Unable to start process: {fileName}");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.WriteLine(stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.Error.WriteLine(stderr.TrimEnd());
        }

        if (process.ExitCode != 0 && !allowNonZeroExit)
        {
            throw new InvalidOperationException(
                $"{fileName} exited with code {process.ExitCode}.\n{stdout}\n{stderr}".Trim());
        }

        return new ProcessResult(process.ExitCode, stdout.Trim(), stderr.Trim());
    }

    static string ResolveExecutablePath(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName) && File.Exists(fileName))
        {
            return fileName;
        }

        foreach (var candidate in EnumerateExecutableCandidates(fileName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return fileName;
    }

    static IEnumerable<string> EnumerateExecutableCandidates(string fileName)
    {
        var hasExtension = !string.IsNullOrEmpty(Path.GetExtension(fileName));
        var fileNames = hasExtension
            ? new[] { fileName }
            : new[] { fileName, fileName + ".exe", fileName + ".cmd", fileName + ".bat" };

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var name in fileNames)
            {
                yield return Path.Combine(directory.Trim(), name);
            }
        }

        if (string.Equals(Path.GetFileNameWithoutExtension(fileName), "gh", StringComparison.OrdinalIgnoreCase))
        {
            var commonDirectories = new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "GitHub CLI"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "GitHub CLI"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "GitHub CLI"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "scoop",
                    "apps",
                    "gh",
                    "current",
                    "bin"),
            };

            foreach (var directory in commonDirectories)
            {
                yield return Path.Combine(directory, "gh.exe");
            }
        }
    }

    static string QuoteForDisplay(string argument)
    {
        return argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0
            ? argument
            : "\"" + argument.Replace("\"", "\\\"") + "\"";
    }

    readonly struct ReleasePackage
    {
        public ReleasePackage(AbsolutePath path, string fileName, string sha256)
        {
            Path = path;
            FileName = fileName;
            Sha256 = sha256;
        }

        public AbsolutePath Path { get; }

        public string FileName { get; }

        public string Sha256 { get; }
    }

    readonly struct ProcessResult
    {
        public ProcessResult(int exitCode, string stdOut, string stdErr)
        {
            ExitCode = exitCode;
            StdOut = stdOut;
            StdErr = stdErr;
        }

        public int ExitCode { get; }

        public string StdOut { get; }

        public string StdErr { get; }
    }
}
