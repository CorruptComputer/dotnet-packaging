using Microsoft.Build.Locator;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConsoleLogger = Microsoft.Build.Logging.ConsoleLogger;
using IMSBuildLogger = Microsoft.Build.Framework.ILogger;
using LoggerVerbosity = Microsoft.Build.Framework.LoggerVerbosity;
using MSBuild = Microsoft.Build.Evaluation;

namespace Dotnet.Packaging
{
    public class PackagingRunner
    {
        private readonly string outputName;
        private readonly string msbuildTarget;
        private readonly string commandName;

        public PackagingRunner(string outputName, string msbuildTarget, string commandName)
        {
            var instance = MSBuildLocator.RegisterDefaults();

            // Workaround for https://github.com/microsoft/MSBuildLocator/issues/86
            AssemblyLoadContext.Default.Resolving += (assemblyLoadContext, assemblyName) =>
            {
                var path = Path.Combine(instance.MSBuildPath, assemblyName.Name + ".dll");
                if (File.Exists(path))
                {
                    return assemblyLoadContext.LoadFromAssemblyPath(path);
                }

                return null;
            };

            this.outputName = outputName;
            this.msbuildTarget = msbuildTarget;
            this.commandName = commandName;
        }

        public int Run(string[] args)
        {
            RootCommand rootCommand =
            [
                new Option<string>("--runtime", "-r")
                {
                    HelpName = "runtime",
                    Description = $"Target runtime of the {outputName}. The target runtime has to be specified in the project file.",
                    Arity = ArgumentArity.ExactlyOne
                },
                new Option<string>("--framework", "-f")
                {
                    HelpName = "framework",
                    Description = $"Target framework of the {outputName}. The target framework has to be specified in the project file.",
                    Arity = ArgumentArity.ExactlyOne
                },
                new Option<string>("--configuration", "-c")
                {
                    HelpName = "configuration",
                    Description = $"Target configuration of the {outputName}. The default for most projects is 'Debug'.",
                    Arity = ArgumentArity.ExactlyOne
                },
                new Option<string>("--output", "-o")
                {
                    HelpName = "output-dir",
                    Description = $"The output directory to place built packages in. The default is the output directory of your project.",
                    Arity = ArgumentArity.ExactlyOne
                },
                new Option<string>("--version-suffix")
                {
                    HelpName = "version-suffix",
                    Description = "Defines the value for the $(VersionSuffix) property in the project.",
                    Arity = ArgumentArity.ExactlyOne
                },
                new Option<bool>("--no-restore")
                {
                    Description = "Do not restore the project before building.",
                    Arity = ArgumentArity.Zero
                },
                new Option<bool>("--verbose", "-v")
                {
                    Description = "Enable verbose output.",
                    Arity = ArgumentArity.Zero
                },
                new Argument<string>("project")
                {
                    Description = "The project file to operate on. If a file is not specified, the command will search the current directory for one.",
                    Arity = ArgumentArity.ZeroOrOne,
                },
                new Command("install")
                {
                    Action = new InstallAction(commandName, outputName)
                }
            ];

            rootCommand.Action = new RootAction(commandName, msbuildTarget);

            return rootCommand.Parse(args).Invoke();
        }

        class LockFile
        {
            [JsonPropertyName("libraries")]
            public Dictionary<string, object> Libraries { get; set; }
        }

        private class RootAction(string commandName, string msbuildTarget) : SynchronousCommandLineAction
        {
            public override int Invoke(ParseResult parseResult)
            {
                string runtime = parseResult.GetValue<string>("runtime");
                string framework = parseResult.GetValue<string>("framework");
                string configuration = parseResult.GetValue<string>("configuration");
                string output = parseResult.GetValue<string>("output");
                string versionSuffix = parseResult.GetValue<string>("version-suffix");
                bool noRestore = parseResult.GetValue<bool>("no-restore");
                bool verbose = parseResult.GetValue<bool>("verbose");
                string project = parseResult.GetValue<string>("project");
                TextWriter consoleWriter = parseResult.InvocationConfiguration.Output;
                TextWriter errorWriter = parseResult.InvocationConfiguration.Error;

                consoleWriter.WriteLine($"dotnet {commandName} ({ThisAssembly.AssemblyInformationalVersion})");

                if (verbose)
                {
                    consoleWriter.WriteLine($"{nameof(runtime)}: {runtime}");
                    consoleWriter.WriteLine($"{nameof(framework)}: {framework}");
                    consoleWriter.WriteLine($"{nameof(configuration)}: {configuration}");
                    consoleWriter.WriteLine($"{nameof(output)}: {output}");
                    consoleWriter.WriteLine($"{nameof(versionSuffix)}: {versionSuffix}");
                    consoleWriter.WriteLine($"{nameof(noRestore)}: {noRestore}");
                    consoleWriter.WriteLine($"{nameof(verbose)}: {verbose}");
                }

                if (!TryGetProjectFilePath(errorWriter, project, out string projectFilePath))
                {
                    return -1;
                }

                if (verbose)
                {
                    consoleWriter.WriteLine($"User specified project '{project}', using '{projectFilePath}'.");
                }

                if (!noRestore)
                {
                    if (!IsPackagingTargetsInstalled(errorWriter, verbose, projectFilePath, framework))
                    {
                        return -1;
                    }
                }

                StringBuilder msbuildArguments = new StringBuilder();
                msbuildArguments.Append($"msbuild /t:{msbuildTarget} ");

                if (!string.IsNullOrWhiteSpace(runtime))
                {
                    msbuildArguments.Append($"/p:RuntimeIdentifier={runtime} ");
                }

                if (!string.IsNullOrWhiteSpace(framework))
                {
                    msbuildArguments.Append($"/p:TargetFramework={framework} ");
                }

                if (!string.IsNullOrWhiteSpace(configuration))
                {
                    msbuildArguments.Append($"/p:Configuration={configuration} ");
                }

                if (!string.IsNullOrWhiteSpace(versionSuffix))
                {
                    msbuildArguments.Append($"/p:VersionSuffix={versionSuffix} ");
                }

                if (!string.IsNullOrWhiteSpace(output))
                {
                    msbuildArguments.Append($"/p:PackageDir={output} ");
                }

                msbuildArguments.Append($"{projectFilePath} ");

                return RunDotnet(msbuildArguments);
            }

            public int RunDotnet(StringBuilder msbuildArguments)
            {
                ProcessStartInfo psi = new()
                {
                    FileName = "dotnet",
                    Arguments = msbuildArguments.ToString()
                };

                Process process = new()
                {
                    StartInfo = psi,
                };

                process.Start();
                process.WaitForExit();

                return process.ExitCode;
            }

            public bool IsPackagingTargetsInstalled(TextWriter errorWriter, bool verbose, string projectFilePath, string framework)
            {
                IMSBuildLogger[] loggers = [new ConsoleLogger(verbose ? LoggerVerbosity.Detailed : LoggerVerbosity.Quiet)];
                MSBuild.Project project = new(projectFilePath);

                if (!string.IsNullOrWhiteSpace(framework))
                {
                    project.SetProperty("TargetFramework", framework);
                }

                if (!project.Build("Restore", loggers))
                {
                    errorWriter.WriteLine($"Failed to restore '{Path.GetFileName(projectFilePath)}'. Please run dotnet restore, and try again.");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(project.GetPropertyValue("TargetFramework")))
                {
                    errorWriter.WriteLine($"The project '{Path.GetFileName(projectFilePath)}' does not specify a default target framework. Please specify the -f {{framework}} option, and try again.");
                    return false;
                }

                string projectAssetsPath = project.GetPropertyValue("ProjectAssetsFile");

                if (string.IsNullOrWhiteSpace(projectAssetsPath))
                {
                    errorWriter.WriteLine($"Failed to read the ProjectAssetsFile property for '{Path.GetFileName(projectFilePath)}'. Please run dotnet restore, and try again.");
                    return false;
                }

                // NuGet has a LockFileUtilities.GetLockFile API which provides direct access to this file format,
                // but loading NuGet in the same process as MSBuild creates dependency conflicts.
                byte[] lockFileContent = File.ReadAllBytes(projectAssetsPath);

                LockFile lockFile = JsonSerializer.Deserialize<LockFile>(lockFileContent);

                if (!lockFile.Libraries.Any(l => l.Key.StartsWith("Packaging.Targets/")))
                {
                    errorWriter.WriteLine($"The project '{Path.GetFileName(projectFilePath)}' doesn't have a PackageReference to Packaging.Targets.");
                    errorWriter.WriteLine($"Please run 'dotnet {commandName} install', and try again.");
                    return false;
                }

                return true;
            }

            private bool TryGetProjectFilePath(TextWriter errorWriter, string project, out string projectFilePath)
            {
                if (!string.IsNullOrWhiteSpace(project))
                {
                    projectFilePath = project;

                    if (!File.Exists(project))
                    {
                        errorWriter.WriteLine($"Could not find the project file '{project}'.");
                        return false;
                    }

                    return true;
                }

                projectFilePath = Directory.GetFiles(Environment.CurrentDirectory, "*.*proj").SingleOrDefault();

                if (projectFilePath == null)
                {
                    errorWriter.WriteLine($"Failed to find a .*proj file in '{Environment.CurrentDirectory}'. dotnet {commandName} only works if");
                    errorWriter.WriteLine($"you have exactly one .*proj file in your directory. For advanced scenarios, please use 'dotnet msbuild /t:{msbuildTarget}'");
                    return false;
                }

                return true;
            }
        }

        private class InstallAction(string commandName, string outputName) : SynchronousCommandLineAction
        {
            public override int Invoke(ParseResult parseResult)
            {
                TextWriter consoleWriter = parseResult.InvocationConfiguration.Output;
                TextWriter errorWriter = parseResult.InvocationConfiguration.Error;

                consoleWriter.WriteLine($"dotnet {commandName} ({ThisAssembly.AssemblyInformationalVersion})");

                // Create/update the Directory.Build.props file in the directory of the version.json file to add the Packaging.Targets package.
                string directoryBuildPropsPath = Path.Combine(Environment.CurrentDirectory, "Directory.Build.props");
                MSBuild.Project propsFile;
                if (File.Exists(directoryBuildPropsPath))
                {
                    propsFile = new MSBuild.Project(directoryBuildPropsPath);
                }
                else
                {
                    propsFile = new MSBuild.Project();
                }

                const string PackageReferenceItemType = "PackageReference";
                const string PackageId = "Packaging.Targets";
                if (!propsFile.GetItemsByEvaluatedInclude(PackageId).Any(i => i.ItemType == PackageReferenceItemType && i.EvaluatedInclude == PackageId))
                {
                    // Using the -* suffix will allow us to match both released and prereleased versions, in the absence
                    // of https://github.com/AArnott/Nerdbank.GitVersioning/issues/409
                    string packageVersion = $"{new Version(ThisAssembly.AssemblyFileVersion).ToString(3)}-*";
                    propsFile.AddItem(
                        PackageReferenceItemType,
                        PackageId,
                        new Dictionary<string, string>
                        {
                        { "Version", packageVersion },
                        { "PrivateAssets", "all" },
                        });

                    propsFile.Save(directoryBuildPropsPath);
                }

                consoleWriter.WriteLine($"Successfully installed dotnet {commandName}. Now run 'dotnet {commandName}' to package your");
                consoleWriter.WriteLine($"application as a {outputName}");

                return 0;
            }
        }
    }
}
