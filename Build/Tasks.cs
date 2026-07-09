using System;
using System.Collections.Generic;
using System.Linq;
using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Build;
using Cake.Common.Tools.DotNet.MSBuild;
using Cake.Common.Tools.DotNet.NuGet.Push;
using Cake.Common.Tools.DotNet.Pack;
using Cake.Common.Tools.DotNet.Restore;
using Cake.Common.Tools.DotNet.Test;
using Cake.Common.Tools.GitVersion;
using Cake.Common.Tools.ReportGenerator;
using Cake.Common.Tools.XUnit;
using Cake.Core;
using Cake.Core.IO;
using Cake.Frosting;

namespace Build;

/// <summary>
/// Base class for tasks that should not abort the build when they fail. The failure is recorded
/// and re-thrown by the <see cref="DefaultTask"/> at the end of the run, mirroring NUKE's
/// <c>ProceedAfterFailure</c>.
/// </summary>
public abstract class ProceedAfterFailureTask : FrostingTask<BuildContext>
{
    public override void OnError(Exception exception, BuildContext context)
    {
        context.Error($"Task failed but the build continues: {exception.Message}");
        context.DeferredExceptions.Add(exception);
    }
}

[TaskName("Clean")]
public sealed class CleanTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasSourceChanges;

    public override void Run(BuildContext context)
    {
        context.CleanDirectory(context.ArtifactsDirectory);
        context.CleanDirectory(context.TestResultsDirectory);
    }
}

[TaskName("CalculateNugetVersion")]
public sealed class CalculateNugetVersionTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasSourceChanges;

    public override void Run(BuildContext context)
    {
        context.GitVersion = context.GitVersion(new GitVersionSettings
        {
            NoFetch = true,
            NoCache = true,
            WorkingDirectory = context.RootDirectory,
        });

        context.SemVer = context.GitVersion.SemVer;

        if (context.IsPullRequest)
        {
            context.Information(
                "Branch spec {0} is a pull request. Adding build number {1}",
                context.BranchSpec, context.BuildNumber);

            context.SemVer = string.Join(
                '.',
                context.GitVersion.SemVer.Split('.').Take(3).Union([context.BuildNumber]));
        }

        context.Information("SemVer = {0}", context.SemVer);
    }
}

[TaskName("Restore")]
[IsDependentOn(typeof(CleanTask))]
public sealed class RestoreTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasSourceChanges;

    public override void Run(BuildContext context)
    {
        context.DotNetRestore(context.Solution.FullPath, new DotNetRestoreSettings
        {
            NoCache = true,
            ConfigFile = context.RootDirectory.CombineWithFilePath("nuget.config").FullPath,
        });
    }
}

[TaskName("Compile")]
[IsDependentOn(typeof(RestoreTask))]
[IsDependentOn(typeof(CalculateNugetVersionTask))]
public sealed class CompileTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasSourceChanges;

    public override void Run(BuildContext context)
    {
        if (context.SemVer != null)
        {
            context.Information("Version = {0}", context.SemVer);
        }

        var msBuild = new DotNetMSBuildSettings
        {
            NoLogo = true,
        };

        if (context.SemVer != null)
        {
            msBuild
                .SetVersion(context.SemVer)
                .SetAssemblyVersion(context.GitVersion.AssemblySemVer)
                .SetFileVersion(context.GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(context.GitVersion.InformationalVersion);
        }

        if (context.GenerateBinLog)
        {
            msBuild.EnableBinaryLogger(
                context.ArtifactsDirectory.CombineWithFilePath("AwesomeAssertions.binlog").FullPath);
        }

        context.DotNetBuild(context.Solution.FullPath, new DotNetBuildSettings
        {
            Configuration = context.MsBuildConfiguration,
            NoLogo = true,
            NoRestore = true,
            MSBuildSettings = msBuild,
        });
    }
}

[TaskName("ApiChecks")]
[IsDependentOn(typeof(CompileTask))]
public sealed class ApiChecksTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasSourceChanges;

    public override void Run(BuildContext context)
    {
        FilePath project = context.RootDirectory.CombineWithFilePath(Projects.ApprovalTests);
        string name = project.GetFilenameWithoutExtension().FullPath;

        context.DotNetTest(project.FullPath, new DotNetTestSettings
        {
            Configuration = context.MsBuildConfiguration == "Debug" ? "Debug" : "Release",
            NoBuild = true,
            ResultsDirectory = context.TestResultsDirectory,
            EnvironmentVariables = new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" },
            Loggers = [$"trx;LogFileName={name}.trx"],
        });
    }
}

[TaskName("UnitTestsNet47")]
[IsDependentOn(typeof(CompileTask))]
public sealed class UnitTestsNet47Task : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) =>
        context.Environment.Platform.Family == PlatformFamily.Windows
        && (context.RunAllTargets || context.HasSourceChanges);

    public override void Run(BuildContext context)
    {
        var testAssemblies = new List<FilePath>();
        foreach (string project in Projects.UnitTestProjects)
        {
            DirectoryPath directory = context.RootDirectory.CombineWithFilePath(project).GetDirectory();
            testAssemblies.AddRange(context.GetFiles($"{directory.FullPath}/bin/Debug/net47/*.Specs.dll"));
        }

        if (testAssemblies.Count == 0)
        {
            throw new CakeException("No net47 test assemblies were found.");
        }

        // The net47 assemblies must be executed by the .NET Framework console runner. Cake would
        // otherwise pick the first xunit.console.exe it finds (the .NET (Core) one), which cannot
        // load a .NET Framework assembly.
        FilePath runner =
            context.GetFiles($"{context.RootDirectory.FullPath}/tools/**/xunit.runner.console*/**/net47*/xunit.console.exe")
                .Concat(context.GetFiles($"{context.RootDirectory.FullPath}/**/xunit.runner.console*/**/net47*/xunit.console.exe"))
                .FirstOrDefault();

        if (runner == null)
        {
            throw new CakeException("Could not locate the .NET Framework xUnit console runner (net47x).");
        }

        context.XUnit2(testAssemblies, new XUnit2Settings { ToolPath = runner });
    }
}

[TaskName("UnitTestsNet6OrGreater")]
[IsDependentOn(typeof(CompileTask))]
public sealed class UnitTestsNet6OrGreaterTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasSourceChanges;

    public override void Run(BuildContext context)
    {
        var failures = new List<Exception>();

        foreach (string project in Projects.UnitTestProjects)
        {
            FilePath projectFile = context.RootDirectory.CombineWithFilePath(project);
            string name = projectFile.GetFilenameWithoutExtension().FullPath;

            foreach (string framework in Projects.GetTargetFrameworks(projectFile).Where(f => f != Constants.Net47))
            {
                TestHelpers.RunWithCoverage(context, failures, projectFile, name, framework);
            }
        }

        TestHelpers.ThrowIfAny(failures);
    }
}

[TaskName("UnitTests")]
[IsDependentOn(typeof(UnitTestsNet47Task))]
[IsDependentOn(typeof(UnitTestsNet6OrGreaterTask))]
public sealed class UnitTestsTask : FrostingTask<BuildContext>
{
}

[TaskName("VSTestFrameworks")]
[IsDependentOn(typeof(CompileTask))]
public sealed class VSTestFrameworksTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasSourceChanges;

    public override void Run(BuildContext context)
    {
        bool isWindows = context.Environment.Platform.Family == PlatformFamily.Windows;
        var failures = new List<Exception>();

        foreach (string project in Projects.VSTestFrameworkProjects)
        {
            FilePath projectFile = context.RootDirectory.CombineWithFilePath(project);
            string name = projectFile.GetFilenameWithoutExtension().FullPath;

            IEnumerable<string> frameworks = Projects.GetTargetFrameworks(projectFile);
            if (!isWindows)
            {
                frameworks = frameworks.Where(f => f != Constants.Net47);
            }

            foreach (string framework in frameworks)
            {
                TestHelpers.RunWithCoverage(context, failures, projectFile, name, framework);
            }
        }

        TestHelpers.ThrowIfAny(failures);
    }
}

[TaskName("TestingPlatformFrameworks")]
[IsDependentOn(typeof(CompileTask))]
public sealed class TestingPlatformFrameworksTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasSourceChanges;

    public override void Run(BuildContext context)
    {
        DirectoryPath workingDirectory = context.RootDirectory.Combine(Projects.TestingPlatformDirectory);

        foreach (string project in Projects.TestingPlatformProjects)
        {
            FilePath projectFile = context.RootDirectory.CombineWithFilePath(project);
            string name = projectFile.GetFilenameWithoutExtension().FullPath;

            foreach (string framework in Projects.GetTargetFrameworks(projectFile))
            {
                context.DotNetTest((string)null, new DotNetTestSettings
                {
                    Configuration = "Debug",
                    NoBuild = true,
                    Framework = framework,
                    WorkingDirectory = workingDirectory,
                    EnvironmentVariables = new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" },
                    ArgumentCustomization = args => args
                        .Append("--project").AppendQuoted(projectFile.FullPath)
                        .Append("--coverage")
                        .Append("--report-trx")
                        .Append("--report-trx-filename").Append($"{name}_{framework}.trx")
                        .Append("--results-directory").AppendQuoted(context.TestResultsDirectory.FullPath),
                });
            }
        }
    }
}

[TaskName("TestFrameworks")]
[IsDependentOn(typeof(VSTestFrameworksTask))]
[IsDependentOn(typeof(TestingPlatformFrameworksTask))]
public sealed class TestFrameworksTask : FrostingTask<BuildContext>
{
}

[TaskName("CodeCoverage")]
[IsDependentOn(typeof(TestFrameworksTask))]
[IsDependentOn(typeof(UnitTestsTask))]
public sealed class CodeCoverageTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasSourceChanges;

    public override void Run(BuildContext context)
    {
        DirectoryPath reportDirectory = context.TestResultsDirectory.Combine("reports");

        var settings = new ReportGeneratorSettings();
        settings.ReportTypes.Clear();
        settings.ReportTypes.Add(ReportGeneratorReportType.lcov);
        settings.ReportTypes.Add(ReportGeneratorReportType.HtmlInline_AzurePipelines_Dark);
        settings.AssemblyFilters.Add("+AwesomeAssertions");

        // ReportGeneratorSettings has no dedicated file filter property, so pass it as a raw argument.
        settings.ArgumentCustomization = args => args.Append("-filefilters:-*.g.cs;-*.nuget*");

        context.ReportGenerator(
            new GlobPattern($"{context.TestResultsDirectory.FullPath}/**/coverage.cobertura.xml"),
            reportDirectory,
            settings);

        FilePath link = reportDirectory.CombineWithFilePath("index.html");
        context.Information($"Code coverage report: {link.FullPath}");
    }
}

[TaskName("Pack")]
[IsDependentOn(typeof(ApiChecksTask))]
[IsDependentOn(typeof(TestFrameworksTask))]
[IsDependentOn(typeof(UnitTestsTask))]
[IsDependentOn(typeof(CodeCoverageTask))]
public sealed class PackTask : FrostingTask<BuildContext>
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasSourceChanges;

    public override void Run(BuildContext context)
    {
        if (context.SemVer != null)
        {
            context.Information("Packed version = {0}", context.SemVer);
        }

        var msBuild = new DotNetMSBuildSettings
        {
            ContinuousIntegrationBuild = true, // Necessary for deterministic builds
        };

        if (context.SemVer != null)
        {
            msBuild.SetVersion(context.SemVer);
        }

        context.DotNetPack(
            context.RootDirectory.CombineWithFilePath(Projects.Core).FullPath,
            new DotNetPackSettings
            {
                Configuration = context.MsBuildConfiguration == "Debug" ? "Debug" : "Release",
                OutputDirectory = context.ArtifactsDirectory,
                NoLogo = true,
                NoRestore = true,
                MSBuildSettings = msBuild,
            });
    }
}

[TaskName("Push")]
[IsDependentOn(typeof(PackTask))]
public sealed class PushTask : ProceedAfterFailureTask
{
    public override bool ShouldRun(BuildContext context) => context.IsTag;

    public override void Run(BuildContext context)
    {
        FilePathCollection packages = context.GetFiles($"{context.ArtifactsDirectory.FullPath}/*.nupkg");

        if (!packages.Any())
        {
            throw new CakeException("No NuGet packages were found to push.");
        }

        foreach (FilePath package in packages)
        {
            context.DotNetNuGetPush(package.FullPath, new DotNetNuGetPushSettings
            {
                ApiKey = context.NuGetApiKey,
                Source = "https://api.nuget.org/v3/index.json",
                SkipDuplicate = true,
                IgnoreSymbols = true,
            });
        }
    }
}

[TaskName("InstallNode")]
public sealed class InstallNodeTask : ProceedAfterFailureTask
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasDocumentationChanges;

    public override void Run(BuildContext context)
    {
        Npm.Initialize(context, context.RootDirectory);
        Npm.FetchRuntime(context);

        if (Npm.HasCachedNodeModules)
        {
            context.Information("Skipped downloading and extracting node.js");
        }
    }
}

[TaskName("SpellCheck")]
[IsDependentOn(typeof(InstallNodeTask))]
public sealed class SpellCheckTask : ProceedAfterFailureTask
{
    public override bool ShouldRun(BuildContext context) => context.RunAllTargets || context.HasDocumentationChanges;

    public override void Run(BuildContext context)
    {
        Npm.Install(context, silent: true, workingDirectory: context.RootDirectory);
        Npm.Run(context, "cspell", silent: true);
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(SpellCheckTask))]
[IsDependentOn(typeof(PushTask))]
public sealed class DefaultTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        if (context.DeferredExceptions.Count > 0)
        {
            throw new AggregateException(
                "One or more tasks that were allowed to proceed after failure did fail.",
                context.DeferredExceptions);
        }
    }
}

internal static class Constants
{
    public const string Net47 = "net47";
}

internal static class TestHelpers
{
    public static void RunWithCoverage(
        BuildContext context, List<Exception> failures, FilePath projectFile, string name, string framework)
    {
        try
        {
            context.DotNetTest(projectFile.FullPath, new DotNetTestSettings
            {
                Configuration = "Debug",
                NoBuild = true,
                Framework = framework,
                Collectors = ["XPlat Code Coverage"],
                ResultsDirectory = context.TestResultsDirectory,
                EnvironmentVariables = new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" },
                Loggers = [$"trx;LogFileName={name}_{framework}.trx"],
                ArgumentCustomization = args => args
                    .Append("--")
                    .Append("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.DoesNotReturnAttribute=DoesNotReturnAttribute"),
            });
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    public static void ThrowIfAny(List<Exception> failures)
    {
        if (failures.Count > 0)
        {
            throw new AggregateException("One or more test runs failed.", failures);
        }
    }
}
