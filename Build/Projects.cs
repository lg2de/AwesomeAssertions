using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Cake.Core.IO;

namespace Build;

/// <summary>
/// Central list of the projects that the build operates on, mirroring the solution folders
/// that the former NUKE build referenced through the typed <c>Solution</c> model.
/// Paths are relative to the repository root.
/// </summary>
public static class Projects
{
    public const string Core = "Src/AwesomeAssertions/AwesomeAssertions.csproj";

    public const string ApprovalTests = "Tests/Approval.Tests/Approval.Tests.csproj";

    public static readonly string[] UnitTestProjects =
    [
        "Tests/AwesomeAssertions.Specs/AwesomeAssertions.Specs.csproj",
        "Tests/AwesomeAssertions.Equivalency.Specs/AwesomeAssertions.Equivalency.Specs.csproj",
        "Tests/AwesomeAssertions.Extensibility.Specs/AwesomeAssertions.Extensibility.Specs.csproj",
        "Tests/FSharp.Specs/FSharp.Specs.fsproj",
        "Tests/VB.Specs/VB.Specs.vbproj",
    ];

    public static readonly string[] VSTestFrameworkProjects =
    [
        "Tests/TestFrameworks/MSpec.Specs/MSpec.Specs.csproj",
        "Tests/TestFrameworks/MSTestV2.Specs/MSTestV2.Specs.csproj",
        "Tests/TestFrameworks/MSTestV4.Specs/MSTestV4.Specs.csproj",
        "Tests/TestFrameworks/NUnit3.Specs/NUnit3.Specs.csproj",
        "Tests/TestFrameworks/NUnit4.Specs/NUnit4.Specs.csproj",
        "Tests/TestFrameworks/XUnit2.Specs/XUnit2.Specs.csproj",
        "Tests/TestFrameworks/XUnit3.Specs/XUnit3.Specs.csproj",
        "Tests/TestFrameworks/XUnit3Core.Specs/XUnit3Core.Specs.csproj",
    ];

    public const string TestingPlatformDirectory = "Tests/TestFrameworks/MicrosoftTestingPlatform";

    public static readonly string[] TestingPlatformProjects =
    [
        "Tests/TestFrameworks/MicrosoftTestingPlatform/NUnit4.Mtp.Specs/NUnit4.Mtp.Specs.csproj",
        "Tests/TestFrameworks/MicrosoftTestingPlatform/TUnit.Specs/TUnit.Specs.csproj",
    ];

    /// <summary>
    /// Reads the target frameworks declared in a project file (supports both
    /// <c>TargetFramework</c> and <c>TargetFrameworks</c>).
    /// </summary>
    public static string[] GetTargetFrameworks(FilePath projectFile)
    {
        XDocument document = XDocument.Load(projectFile.FullPath);

        IEnumerable<string> values = document
            .Descendants()
            .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .SelectMany(e => e.Value.Split(';'));

        return values
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct()
            .ToArray();
    }
}
