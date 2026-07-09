using System;
using Cake.Frosting;

namespace Build;

public static class Program
{
    public static int Main(string[] args) => new CakeHost()
        .UseContext<BuildContext>()
        .InstallTool(new Uri("dotnet:?package=GitVersion.Tool&version=6.5.1"))
        .InstallTool(new Uri("dotnet:?package=dotnet-reportgenerator-globaltool&version=5.5.0"))
        .InstallTool(new Uri("nuget:?package=xunit.runner.console&version=2.9.2"))
        .Run(args);
}
