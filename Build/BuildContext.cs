using System;
using System.Collections.Generic;
using System.Linq;
using Cake.Common;
using Cake.Common.Tools.GitVersion;
using Cake.Core;
using Cake.Core.IO;
using Cake.Frosting;
using Cake.Git;

namespace Build;

public class BuildContext : FrostingContext
{
    public BuildContext(ICakeContext context)
        : base(context)
    {
        IsLocalBuild = string.IsNullOrEmpty(context.Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

        // The solution configuration to build. Default is 'Debug' (local) or 'CI' (server).
        MsBuildConfiguration = context.Argument("configuration", IsLocalBuild ? "Debug" : "CI");

        // Use this parameter if you encounter build problems in any way,
        // to generate a .binlog file which holds some useful information.
        GenerateBinLog = context.Argument("GenerateBinLog", false);

        // The key to push to NuGet.
        NuGetApiKey = context.Argument(
            "NuGetApiKey",
            context.Environment.GetEnvironmentVariable("NuGetApiKey"));

        BranchSpec = context.Environment.GetEnvironmentVariable("GITHUB_REF");
        BuildNumber = context.Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER");
        PullRequestBase = context.Environment.GetEnvironmentVariable("GITHUB_BASE_REF");
        IsPullRequest = string.Equals(
            context.Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME"),
            "pull_request",
            StringComparison.OrdinalIgnoreCase);

        RootDirectory = context.GitFindRootFromPath(context.Environment.WorkingDirectory);
    }

    public bool IsLocalBuild { get; }

    public string MsBuildConfiguration { get; }

    public bool GenerateBinLog { get; }

    public string NuGetApiKey { get; }

    public string BranchSpec { get; }

    public string BuildNumber { get; }

    public string PullRequestBase { get; }

    public bool IsPullRequest { get; }

    public DirectoryPath RootDirectory { get; }

    /// <summary>
    /// Failures of tasks that are allowed to proceed after failure; re-thrown by the Default task.
    /// </summary>
    public List<Exception> DeferredExceptions { get; } = [];

    public DirectoryPath ArtifactsDirectory => RootDirectory.Combine("Artifacts");

    public DirectoryPath TestResultsDirectory => RootDirectory.Combine("TestResults");

    public FilePath Solution => RootDirectory.CombineWithFilePath("AwesomeAssertions.slnx");

    /// <summary>
    /// The version information calculated by GitVersion. Set by the CalculateNugetVersion task.
    /// </summary>
    public GitVersion GitVersion { get; set; }

    /// <summary>
    /// The effective semantic version used for packing. Set by the CalculateNugetVersion task.
    /// </summary>
    public string SemVer { get; set; }

    public bool IsTag =>
        BranchSpec != null && BranchSpec.Contains("refs/tags", StringComparison.OrdinalIgnoreCase);

    // A pull request only runs the targets that are actually affected by the changes, everything else
    // (local builds, pushes to branches, tag builds) runs all targets.
    public bool RunAllTargets =>
        string.IsNullOrWhiteSpace(PullRequestBase) || Changes.Any(x => x.StartsWith("Build", StringComparison.Ordinal));

    public bool HasSourceChanges => Changes.Any(x => !IsDocumentation(x));

    public bool HasDocumentationChanges => Changes.Any(x => IsDocumentation(x));

    string[] _changes;

    string[] Changes => _changes ??= CalculateChanges();

    string[] CalculateChanges()
    {
        ICollection<GitBranch> branches = this.GitBranches(RootDirectory);

        GitBranch baseBranch =
            branches.FirstOrDefault(b => b.FriendlyName == PullRequestBase)
            ?? branches.FirstOrDefault(b => b.FriendlyName.EndsWith("/" + PullRequestBase, StringComparison.Ordinal));

        if (baseBranch == null)
        {
            return [];
        }

        string headSha = this.GitBranchCurrent(RootDirectory).Tip.Sha;

        return this.GitDiff(RootDirectory, baseBranch.Tip.Sha, headSha)
            .Where(x => x.Exists)
            .Select(x => x.Path)
            .ToArray();
    }

    static bool IsDocumentation(string path) =>
        path.StartsWith("docs", StringComparison.Ordinal) ||
        path.StartsWith("CONTRIBUTING.md", StringComparison.Ordinal) ||
        path.StartsWith("cSpell.json", StringComparison.Ordinal) ||
        path.StartsWith("LICENSE", StringComparison.Ordinal) ||
        path.StartsWith("package.json", StringComparison.Ordinal) ||
        path.StartsWith("package-lock.json", StringComparison.Ordinal) ||
        path.StartsWith("NodeVersion", StringComparison.Ordinal) ||
        path.StartsWith("README.md", StringComparison.Ordinal);
}
