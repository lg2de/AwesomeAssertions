using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Cake.Common;
using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Core;
using Cake.Core.IO;

namespace Build;

/// <summary>
/// Downloads a self-contained node.js runtime and runs npm through it, so that the spell check
/// does not depend on a globally installed node.js. This replaces the NUKE based tooling helpers.
/// </summary>
public static class Npm
{
    static DirectoryPath rootDirectory;
    static DirectoryPath nodeCacheDirectory;
    static DirectoryPath nodeHomeDirectory;
    static FilePath nodeExecutable;
    static FilePath npmCli;
    static DirectoryPath pathDirectory;
    static string version;

    public static bool HasCachedNodeModules { get; private set; }

    public static void Initialize(ICakeContext context, DirectoryPath root)
    {
        rootDirectory = root;
        nodeCacheDirectory = root.Combine(".cake").Combine("node");
        version = File.ReadAllText(root.CombineWithFilePath("NodeVersion").FullPath).Trim();
    }

    public static void FetchRuntime(ICakeContext context)
    {
        FilePath archive = DownloadNodeArchive(context);
        ExtractNodeArchive(context, archive);
        ResolveTools(context);
    }

    static FilePath DownloadNodeArchive(ICakeContext context)
    {
        bool isWindows = context.Environment.Platform.Family == PlatformFamily.Windows;
        bool isOsx = context.Environment.Platform.Family == PlatformFamily.OSX;

        string os;
        string archiveType;
        if (isWindows)
        {
            os = "win";
            archiveType = ".zip";
        }
        else if (isOsx)
        {
            os = "darwin";
            archiveType = ".tar.gz";
        }
        else
        {
            os = "linux";
            archiveType = ".tar.xz";
        }

        string architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x86",
        };

        os = $"{os}-{architecture}";

        HasCachedNodeModules =
            context.GetFiles($"{nodeCacheDirectory.FullPath}/node*{version}-{os}*/**/node*").Count
            + context.GetFiles($"{nodeCacheDirectory.FullPath}/node*{version}-{os}*/**/npm*").Count > 0;

        FilePath archive = nodeCacheDirectory.CombineWithFilePath($"node{archiveType}");

        if (!HasCachedNodeModules)
        {
            context.Information($"Fetching node.js ({version}) for {os}");
            context.EnsureDirectoryExists(nodeCacheDirectory);

            string downloadUrl = $"https://nodejs.org/dist/v{version}/node-v{version}-{os}{archiveType}";
            DownloadFile(downloadUrl, archive);
        }
        else
        {
            context.Information("Skipping archive download due to cache");
        }

        nodeHomeDirectory = nodeCacheDirectory.Combine($"node-v{version}-{os}");

        return archive;
    }

    static void ExtractNodeArchive(ICakeContext context, FilePath archive)
    {
        if (!HasCachedNodeModules)
        {
            context.Information($"Extracting node.js binary archive ({archive.FullPath}) to {nodeCacheDirectory.FullPath}");

            if (context.Environment.Platform.Family == PlatformFamily.Windows)
            {
                context.Unzip(archive, nodeCacheDirectory);
            }
            else
            {
                CompressionExtensions.ExtractTar(archive.FullPath, nodeCacheDirectory.FullPath);
            }
        }
        else
        {
            context.Information("Skipping archive extraction due to cache");
        }
    }

    static void ResolveTools(ICakeContext context)
    {
        if (context.Environment.Platform.Family == PlatformFamily.Windows)
        {
            nodeExecutable = nodeHomeDirectory.CombineWithFilePath("node.exe");
            npmCli = nodeHomeDirectory.CombineWithFilePath("node_modules/npm/bin/npm-cli.js");
            pathDirectory = nodeHomeDirectory;
        }
        else
        {
            nodeExecutable = nodeHomeDirectory.CombineWithFilePath("bin/node");
            npmCli = nodeHomeDirectory.CombineWithFilePath("lib/node_modules/npm/bin/npm-cli.js");
            pathDirectory = nodeHomeDirectory.Combine("bin");

            MakeExecutable(context, nodeExecutable);
        }

        // Disable the update notifier to keep the output clean and deterministic.
        RunNpm(context, silent: false, rootDirectory, "config", "set", "update-notifier", "false");
    }

    public static void Install(ICakeContext context, bool silent, DirectoryPath workingDirectory)
    {
        RunNpm(context, silent, workingDirectory, "install");
    }

    public static void Run(ICakeContext context, string script, bool silent)
    {
        RunNpm(context, silent, rootDirectory, "run", script);
    }

    static void RunNpm(ICakeContext context, bool silent, DirectoryPath workingDirectory, params string[] arguments)
    {
        var builder = new ProcessArgumentBuilder();
        builder.AppendQuoted(npmCli.FullPath);
        foreach (string argument in arguments)
        {
            builder.Append(argument);
        }

        if (silent)
        {
            builder.Append("--silent");
        }

        int exitCode = context.StartProcess(nodeExecutable.FullPath, new ProcessSettings
        {
            Arguments = builder,
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = BuildEnvironment(),
        });

        if (exitCode != 0)
        {
            throw new CakeException($"npm {string.Join(' ', arguments)} failed with exit code {exitCode}.");
        }
    }

    static IDictionary<string, string> BuildEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = entry.Value as string;
        }

        environment.TryGetValue("PATH", out string existingPath);
        environment["PATH"] = pathDirectory.FullPath + System.IO.Path.PathSeparator + existingPath;

        return environment;
    }

    static void MakeExecutable(ICakeContext context, FilePath file)
    {
        context.StartProcess("chmod", new ProcessSettings
        {
            Arguments = new ProcessArgumentBuilder().Append("+x").AppendQuoted(file.FullPath),
        });
    }

    static void DownloadFile(string url, FilePath target)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using Stream source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using FileStream destination = File.Create(target.FullPath);
        source.CopyTo(destination);
    }
}
