﻿using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using static Pocket.Logger<MLS.Agent.Program>;
using Pocket;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Pocket.For.ApplicationInsights;
using Recipes;
using Serilog.Sinks.RollingFileAlternate;
using WorkspaceServer.Servers.Roslyn;
using SerilogLoggerConfiguration = Serilog.LoggerConfiguration;
using MLS.Agent.Tools;
using WorkspaceServer;
using System.Linq;
using MLS.Repositories;

namespace MLS.Agent
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var parser = CreateParser(startServer: (options, console) => ConstructWebHost(options).Run(), (r, c) => GithubHandler(r, c, new RepoLocator()));

            return await parser.InvokeAsync(args);
        }

        public static X509Certificate2 ParseKey(string base64EncodedKey)
        {
            var bytes = Convert.FromBase64String(base64EncodedKey);
            return new X509Certificate2(bytes);
        }

        private static readonly Assembly[] assembliesEmittingPocketLoggerLogs = {
            typeof(Startup).Assembly,
            typeof(AsyncLazy<>).Assembly,
            typeof(RoslynWorkspaceServer).Assembly
        };

        private static void StartLogging(CompositeDisposable disposables, StartupOptions options)
        {
            if (options.Production)
            {
                var applicationVersion = VersionSensor.Version().AssemblyInformationalVersion;
                var websiteSiteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ?? "UNKNOWN-AGENT";
                var regionId = options.RegionId ?? "undefined";
                disposables.Add(
                    LogEvents.Enrich(a =>
                    {
                        a(("regionId", regionId));
                        a(("applicationVersion", applicationVersion));
                        a(("websiteSiteName", websiteSiteName));
                        a(("id", options.Id));
                    }));
            }

            var shouldLogToFile = options.LogToFile;

#if DEBUG
            shouldLogToFile = true;
#endif 

            if (shouldLogToFile)
            {
                var log = new SerilogLoggerConfiguration()
                          .WriteTo
                          .RollingFileAlternate("./logs", outputTemplate: "{Message}{NewLine}")
                          .CreateLogger();

                var subscription = LogEvents.Subscribe(
                    e => log.Information(e.ToLogString()),
                    assembliesEmittingPocketLoggerLogs);

                disposables.Add(subscription);
                disposables.Add(log);
            }

            disposables.Add(
                LogEvents.Subscribe(e => Console.WriteLine(e.ToLogString()),
                                    assembliesEmittingPocketLoggerLogs));

            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Log.Warning($"{nameof(TaskScheduler.UnobservedTaskException)}", args.Exception);
                args.SetObserved();
            };

            if (options.ApplicationInsightsKey != null)
            {
                var telemetryClient = new TelemetryClient(new TelemetryConfiguration(options.ApplicationInsightsKey))
                {
                    InstrumentationKey = options.ApplicationInsightsKey
                };
                disposables.Add(telemetryClient.SubscribeToPocketLogger(assembliesEmittingPocketLoggerLogs));
            }

            Log.Event("AgentStarting");
        }

        public static IWebHost ConstructWebHost(StartupOptions options)
        {
            var disposables = new CompositeDisposable();
            StartLogging(disposables, options);

            if (options.Key is null)
            {
                Log.Trace("No Key Provided");
            }
            else
            {
                Log.Trace("Received Key: {key}", options.Key);
            }

            var webHost = new WebHostBuilder()
                          .UseKestrel()
                          .UseContentRoot(Directory.GetCurrentDirectory())
                          .ConfigureServices(c =>
                          {
                              if (!String.IsNullOrEmpty(options.ApplicationInsightsKey))
                              {
                                  c.AddApplicationInsightsTelemetry(options.ApplicationInsightsKey);
                              }

                              c.AddSingleton(options);
                          })
                          .UseEnvironment(options.Production
                                              ? EnvironmentName.Production
                                              : EnvironmentName.Development)
                          .UseStartup<Startup>()
                          .Build();

            return webHost;
        }

        private static readonly TypeBinder _typeBinder = new TypeBinder(typeof(StartupOptions));

        public static async Task GithubHandler(string repo, IConsole console, IRepoLocator locator)
        {
            var repos = (await locator.LocateRepo(repo)).ToArray();

            if (repos.Length == 0)
            {
                console.Out.WriteLine($"Didn't find any repos called `{repo}`");
            }
            else if (repos[0].Name == repo)
            {
                console.Out.WriteLine(GenerateCommandExample(repos[0].Name, repos[0].CloneUrl));

            }
            else
            {
                console.Out.WriteLine("Which of the following did you mean?");
                foreach (var instance in repos)
                {
                    console.Out.WriteLine($"\t{instance.Name}");
                }
            }

            string GenerateCommandExample(string name, string cloneUrl)
            {
                string text = $"Found repo `{name}`\n";
                text += $"To try `{name}`, cd to your desired directory and run the following command:\n\n";
                text += $"\tgit clone {cloneUrl} && dotnet try .";

                return text;
            }
        }

        public static Parser CreateParser(Action<StartupOptions, InvocationContext> startServer, Func<string, IConsole, Task> githubHandler)
        {
            var rootCommand = StartServer();

            rootCommand.AddCommand(ListWorkspaces());
            rootCommand.AddCommand(GitHub());

            return new CommandLineBuilder(rootCommand)
                   .UseDefaults()
                   .Build();

            RootCommand StartServer()
            {
                var startServerCommand = new RootCommand
                                         {
                                             Description = "Starts the Try .NET agent."
                                         };

                startServerCommand.AddOption(new Option(
                                                 "--id",
                                                 "A unique id for the agent instance (e.g. its development environment id).",
                                                 new Argument<string>(defaultValue: () => Environment.MachineName)));
                startServerCommand.AddOption(new Option(
                                                 "--production",
                                                 "Specifies whether the agent is being run using production resources",
                                                 new Argument<bool>()));
                startServerCommand.AddOption(new Option(
                                                 "--language-service",
                                                 "Specifies whether the agent is being run in language service-only mode",
                                                 new Argument<bool>()));
                startServerCommand.AddOption(new Option(
                                                 new[] { "-k", "--key" },
                                                 "The encryption key",
                                                 new Argument<string>()));
                startServerCommand.AddOption(new Option(
                                                 new[] { "--ai-key", "--application-insights-key" },
                                                 "Application Insights key.",
                                                 new Argument<string>()));
                startServerCommand.AddOption(new Option(
                                                 "--region-id",
                                                 "A unique id for the agent region",
                                                 new Argument<string>()));
                startServerCommand.AddOption(new Option(
                                                 "--log-to-file",
                                                 "Writes a log file",
                                                 new Argument<bool>()));
                
                startServerCommand.AddOption(new Option(
                                                 "--root-directory",
                                                 "Specify the path to the root directory",
                                                 new Argument<DirectoryInfo>(new DirectoryInfo(Directory.GetCurrentDirectory())).ExistingOnly()));

                startServerCommand.Handler = CommandHandler.Create<InvocationContext>(context =>
                {
                    var options = (StartupOptions) _typeBinder.CreateInstance(context);

                    startServer(options, context);
                });

                return startServerCommand;
            }

            Command ListWorkspaces()
            {
                var run = new Command("list-packages", "Lists the available Try .NET packages");

                run.Handler = CommandHandler.Create((IConsole console) =>
                {
                    var registry = WorkspaceRegistry.CreateDefault();

                    foreach (var workspace in registry)
                    {
                        console.Out.WriteLine(workspace.WorkspaceName);
                    }
                });

                return run;
            }

            Command GitHub()
            {
                var argument = new Argument<string>();
                argument.Name = "repo";
                var run = new Command("github", "Try a GitHub repo", argument: argument);

                run.Handler = CommandHandler.Create(githubHandler);

                return run;
            }
        }
    }
}
