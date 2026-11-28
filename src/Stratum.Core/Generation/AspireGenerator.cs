using System.Security.Cryptography;
using System.Text;
using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>An Stratum service found under the system root.</summary>
public sealed record AspireService(string RelativeDirectory, ServiceDefinition Definition)
{
    public string Name => Definition.Service.Name;

    public string ResourceName => ServiceName.ToKebab(Name);

    public string ApiProject => $"{RelativeDirectory}/src/{Name}.Api/{Name}.Api.csproj";
}

public sealed record AspireResult(string AppHostName, IReadOnlyList<AspireService> Services, IReadOnlyList<string> Created, IReadOnlyList<string> Updated);

/// <summary>
/// Generates an Aspire AppHost that runs every Stratum service under a folder, with their databases.
/// <list type="bullet">
/// <item><c>StratumServices.g.cs</c> and the system <c>.slnx</c> are regenerated on every run (Stratum owns them).</item>
/// <item><c>AppHost.cs</c>, settings and launch profile are created once: they are yours.</item>
/// <item>The AppHost .csproj only gets the missing references added.</item>
/// </list>
/// </summary>
public static class AspireGenerator
{
    /// <summary>Latest 13.x: the line validated with .NET 10. Bump with "aspire update" when you want to.</summary>
    public const string AspireVersion = "13.6.5";

    /// <summary>Aspire.Hosting.Azure.Sql has no 13.6.5.</summary>
    public const string AspireAzureSqlVersion = "13.6.4";

    /// <summary>Where "aspire deploy" can take the system ("stratum deploy").</summary>
    public const string DeployDocker = "docker";

    public const string DeployAzure = "azure";

    public static readonly IReadOnlyList<string> DeployTargets = [DeployDocker, DeployAzure];

    /// <summary>The deploy targets of the system: the &lt;StratumDeploy&gt; property of its AppHost (none without it).</summary>
    public static IReadOnlyList<string> Deploy(string root, string appHostName)
    {
        var csproj = Path.Combine(root, $"{appHostName}.AppHost", $"{appHostName}.AppHost.csproj");
        if (!File.Exists(csproj) || System.Text.RegularExpressions.Regex.Match(File.ReadAllText(csproj), "<StratumDeploy>(?<t>[^<]*)</StratumDeploy>") is not { Success: true } match)
        {
            return [];
        }

        return [.. DeployTargets.Where(t => match.Groups["t"].Value.Split(';', ',', ' ').Contains(t, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>Why a target doesn't fit the system (null when it does).</summary>
    public static string? DeployProblem(string target, IReadOnlyList<AspireService> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!DeployTargets.Contains(target))
        {
            return $"No conozco el destino '{target}'. Destinos: {string.Join(", ", DeployTargets)}.";
        }

        return target == DeployDocker && UsesServiceBus(services)
            ? "Azure Service Bus no funciona en Docker Compose (su emulador es solo para desarrollo): despliega en Azure, o usa Kafka en los servicios que quieras llevar a Docker."
            : null;
    }

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "artifacts", "TestResults",
    };

    /// <summary>
    /// Subfolders to look into for services: the ones you can't open (Windows protects some in your user folder, such as
    /// "Temporary Internet Files"), links (junctions, which also make loops) and hidden or system ones are left out.
    /// </summary>
    internal static IEnumerable<string> Subdirectories(string directory)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(directory, "*", SafeEnumeration)];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static readonly EnumerationOptions SafeEnumeration = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
    };

    /// <param name="skipped">Receives the stratum.json files that could not be read (another tool's file, or broken):
    /// they are left out instead of failing the whole discovery.</param>
    public static IReadOnlyList<AspireService> Discover(string root, int maxDepth = 5, ICollection<string>? skipped = null)
    {
        var rootFull = Path.GetFullPath(root);
        var found = new List<AspireService>();
        Walk(rootFull, 0);

        // Services in other folders, listed in stratum.workspace.json.
        foreach (var directory in WorkspaceFile.ExternalServices(rootFull))
        {
            if (ServiceGenerator.FindDefinition(directory) is not { } definitionFile || TryRead(definitionFile) is not { } definition)
            {
                continue;
            }

            var relative = Path.GetRelativePath(rootFull, directory).Replace('\\', '/');
            var service = new AspireService(relative, definition);
            if (File.Exists(Path.Combine(rootFull, service.ApiProject)) && !found.Any(f => f.RelativeDirectory == relative))
            {
                found.Add(service);
            }
        }

        return [.. found.OrderBy(s => s.RelativeDirectory, StringComparer.Ordinal)];

        void Walk(string directory, int depth)
        {
            if (directory != rootFull && ServiceGenerator.FindDefinition(directory) is { } definitionPath)
            {
                if (TryRead(definitionPath) is not { } definition)
                {
                    return;
                }

                var relative = Path.GetRelativePath(rootFull, directory).Replace('\\', '/');
                var service = new AspireService(relative, definition);

                if (File.Exists(Path.Combine(rootFull, service.ApiProject)))
                {
                    found.Add(service);
                }

                return; // services don't contain other services
            }

            if (depth >= maxDepth)
            {
                return;
            }

            foreach (var child in Subdirectories(directory))
            {
                if (!SkippedFolders.Contains(Path.GetFileName(child)) && !Path.GetFileName(child).StartsWith('.') && !child.EndsWith(".AppHost", StringComparison.Ordinal))
                {
                    Walk(child, depth + 1);
                }
            }
        }

        ServiceDefinition? TryRead(string path)
        {
            try
            {
                return ServiceGenerator.Deserialize(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is StratumException or IOException or UnauthorizedAccessException)
            {
                skipped?.Add($"{path}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Name for the system's AppHost: the existing one ("Shop" for Shop.AppHost) so it is updated instead of
    /// creating a second one; otherwise the folder name.
    /// </summary>
    public static string DefaultSystemName(string root)
    {
        var full = Path.GetFullPath(root);
        var existing = Directory.Exists(full)
            ? Directory.EnumerateDirectories(full, "*.AppHost").Select(Path.GetFileName).Order(StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (existing is not null)
        {
            return existing[..^".AppHost".Length];
        }

        try
        {
            return ServiceName.Normalize(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)));
        }
        catch (StratumException)
        {
            return "System";
        }
    }

    /// <param name="deploy">Deploy targets to set (null keeps the ones the AppHost has).</param>
    public static AspireResult Generate(string root, string appHostName, IReadOnlyList<AspireService> services, IReadOnlyList<string>? deploy = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ServiceName.Validate(appHostName);

        if (services.Count == 0)
        {
            throw new StratumException($"No se encontró ningún servicio Stratum (stratum.json) bajo '{Path.GetFullPath(root)}'.");
        }

        var duplicated = services.GroupBy(s => s.ResourceName).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            throw new StratumException($"Hay varios servicios llamados '{duplicated.Key}': {string.Join(", ", duplicated.Select(s => s.RelativeDirectory))}.");
        }

        deploy = [.. DeployTargets.Where((deploy ?? Deploy(root, appHostName)).Contains)];
        foreach (var target in deploy)
        {
            if (DeployProblem(target, services) is { } problem)
            {
                throw new StratumException(problem);
            }
        }

        var appHost = $"{appHostName}.AppHost";
        var created = new List<string>();
        var updated = new List<string>();

        void CreateOnce(string relative, string content)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
                created.Add(relative);
            }
        }

        void Overwrite(string relative, string content)
        {
            var path = Path.Combine(root, relative);
            var existed = File.Exists(path);
            if (!existed || File.ReadAllText(path) != content)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
                (existed ? updated : created).Add(relative);
            }
        }

        var csprojPath = $"{appHost}/{appHost}.csproj";
        var csproj = Csproj(root, appHostName, services, deploy);
        if (File.Exists(Path.Combine(root, csprojPath)))
        {
            // Services that left the system (removed or moved): their *.Api project references go too.
            // Other references the user added to the AppHost are kept.
            var full = Path.Combine(root, csprojPath);
            var existing = File.ReadAllText(full);
            var current = System.Text.RegularExpressions.Regex.Matches(csproj, "<ProjectReference Include=\"(?<p>[^\"]+)\"")
                .Select(m => m.Groups["p"].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                existing,
                "[ \t]*<ProjectReference Include=\"(?<p>[^\"]+\\.Api\\.csproj)\"\\s*/>[ \t]*\r?\n?",
                m => current.Contains(m.Groups["p"].Value) ? m.Value : string.Empty);

            // Deploy targets ("stratum deploy"): their packages and the <StratumDeploy> property follow the targets.
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
                "[ \t]*<PackageReference Include=\"Aspire\\.Hosting\\.(Docker|Azure\\.AppContainers|Azure\\.PostgreSQL|Azure\\.Sql)\"[^>]*/>[ \t]*\r?\n?", string.Empty);
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "[ \t]*<StratumDeploy>[^<]*</StratumDeploy>[ \t]*\r?\n?", string.Empty);
            if (deploy.Count > 0
                && System.Text.RegularExpressions.Regex.Match(cleaned, "([ \t]*)<OutputType>Exe</OutputType>(\r?\n)") is { Success: true } exe)
            {
                cleaned = cleaned.Insert(exe.Index + exe.Length, $"{exe.Groups[1].Value}<StratumDeploy>{string.Join(';', deploy)}</StratumDeploy>{exe.Groups[2].Value}");
            }

            // AppHosts from before 0.35.1: opt out of a Directory.Packages.props found in a parent folder (NU1008).
            if (!cleaned.Contains("<ManagePackageVersionsCentrally>", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.Match(cleaned, "([ \t]*)<OutputType>Exe</OutputType>(\r?\n)") is { Success: true } output)
            {
                var indent = output.Groups[1].Value;
                var nl = output.Groups[2].Value;
                cleaned = cleaned.Insert(output.Index + output.Length,
                    $"{indent}<!-- Its packages carry their versions: a Directory.Packages.props in a parent folder doesn't apply. -->{nl}" +
                    $"{indent}<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>{nl}");
            }

            if (cleaned != existing)
            {
                File.WriteAllText(full, cleaned);
            }

            if (ProjectFileMerger.Merge(full, csproj) || cleaned != existing)
            {
                updated.Add(csprojPath);
            }
        }
        else
        {
            CreateOnce(csprojPath, csproj);
        }

        CreateOnce($"{appHost}/AppHost.cs", AppHostProgram(services));
        CreateOnce($"{appHost}/appsettings.json", AppSettings);
        CreateOnce($"{appHost}/Properties/launchSettings.json", LaunchSettings(appHostName));
        CreateOnce(".aspire/settings.json", $$"""
            {
              "appHostPath": "../{{appHost}}/{{appHost}}.csproj"
            }

            """);
        // The AppHost restores every service from the system root: NuGet only sees nuget.config files from there
        // upwards, so a service's local Stratum.* source ("stratum new --package-source") must also exist at the root.
        var serviceConfig = services
            .Select(s => Path.Combine(root, s.RelativeDirectory, "nuget.config"))
            .FirstOrDefault(File.Exists);
        if (serviceConfig is not null && !(StratumInfo.IsPublished && File.ReadAllText(serviceConfig).Contains("Generated by \"stratum", StringComparison.Ordinal)))
        {
            CreateOnce("nuget.config", File.ReadAllText(serviceConfig));
        }

        // And the packages that came with the stratum tool (~/.stratum/packages), for services without a nuget.config.
        // Once Stratum.* is on nuget.org, a root nuget.config that only served the local packages goes away.
        if (StratumInfo.IsPublished && StratumPackages.LocalSource() is null && StratumPackages.RemoveLocalOnlyConfig(Path.Combine(root, "nuget.config")))
        {
            updated.Add("nuget.config (eliminado: Stratum.* ya está en nuget.org)");
        }
        else if (StratumPackages.LocalSource() is { } source)
        {
            var rootConfig = Path.Combine(root, "nuget.config");
            if (File.Exists(rootConfig) && ProjectFileMerger.RenameStratumPackages(File.ReadAllText(rootConfig)) is { } renamedConfig)
            {
                File.WriteAllText(rootConfig, renamedConfig);   // Stratum.* → StratumKit.* (0.36.1)
            }

            var existed = File.Exists(rootConfig);
            if (StratumPackages.EnsureSource(rootConfig, source))
            {
                (existed ? updated : created).Add("nuget.config");
            }
        }

        Overwrite($"{appHost}/StratumServices.g.cs", StratumServices(services, appHostName, deploy));
        Overwrite($"{appHostName}.slnx", Solution(root, appHost, services));

        return new AspireResult(appHost, services, created, updated);
    }

    private static string Csproj(string root, string name, IReadOnlyList<AspireService> services, IReadOnlyList<string> deploy)
    {
        var providers = services.Select(s => s.Definition.Persistence?.Provider).Where(p => p is not null).Distinct().ToList();
        var packages = new StringBuilder();
        if (providers.Contains(PersistenceSettings.Postgres))
        {
            packages.Append($"    <PackageReference Include=\"Aspire.Hosting.PostgreSQL\" Version=\"{AspireVersion}\" />\n");
        }

        if (providers.Contains(PersistenceSettings.SqlServer))
        {
            packages.Append($"    <PackageReference Include=\"Aspire.Hosting.SqlServer\" Version=\"{AspireVersion}\" />\n");
        }

        if (UsesKafka(services))
        {
            packages.Append($"    <PackageReference Include=\"Aspire.Hosting.Kafka\" Version=\"{AspireVersion}\" />\n");
        }

        if (UsesServiceBus(services))
        {
            packages.Append($"    <PackageReference Include=\"Aspire.Hosting.Azure.ServiceBus\" Version=\"{AspireVersion}\" />\n");
        }

        if (deploy.Contains(DeployDocker))
        {
            packages.Append($"    <PackageReference Include=\"Aspire.Hosting.Docker\" Version=\"{AspireVersion}\" />\n");
        }

        if (deploy.Contains(DeployAzure))
        {
            packages.Append($"    <PackageReference Include=\"Aspire.Hosting.Azure.AppContainers\" Version=\"{AspireVersion}\" />\n");
            if (providers.Contains(PersistenceSettings.Postgres))
            {
                packages.Append($"    <PackageReference Include=\"Aspire.Hosting.Azure.PostgreSQL\" Version=\"{AspireVersion}\" />\n");
            }

            if (providers.Contains(PersistenceSettings.SqlServer))
            {
                packages.Append($"    <PackageReference Include=\"Aspire.Hosting.Azure.Sql\" Version=\"{AspireAzureSqlVersion}\" />\n");
            }
        }

        var deployProperty = deploy.Count == 0 ? string.Empty
            : $"    <!-- Where \"aspire deploy\" takes the system: stratum deploy add|remove docker|azure. -->\n    <StratumDeploy>{string.Join(';', deploy)}</StratumDeploy>\n";

        // Relative to the AppHost folder; absolute for a service on another drive.
        var projects = string.Concat(services.Select(s =>
            $"    <ProjectReference Include=\"{Path.GetRelativePath(Path.GetFullPath(Path.Combine(root, $"{name}.AppHost")), Path.GetFullPath(Path.Combine(root, s.ApiProject))).Replace('/', '\\')}\" />\n"));

        return $"""
            <Project Sdk="Aspire.AppHost.Sdk/{AspireVersion}">

              <!-- Generated by "stratum aspire". Runs every Stratum service of this folder with its dependencies. -->

              <PropertyGroup>
                <OutputType>Exe</OutputType>
            {deployProperty}    <!-- Its packages carry their versions: a Directory.Packages.props in a parent folder doesn't apply. -->
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <UserSecretsId>{StableGuid(name)}</UserSecretsId>
                <!-- ASPIRE010: "dotnet run" works without the Aspire CLI bundle; opt in with AspireUseCliBundle=true if you use the CLI. -->
                <NoWarn>$(NoWarn);ASPIRE010</NoWarn>
              </PropertyGroup>

              <ItemGroup>
            {packages}  </ItemGroup>

              <ItemGroup>
            {projects}  </ItemGroup>

            </Project>

            """;
    }

    private static string AppHostProgram(IReadOnlyList<AspireService> services)
    {
        var example = services.Count > 1
            ? $"// services.{Property(services[1])}.WithReference(services.{Property(services[0])}).WaitFor(services.{Property(services[0])});"
            : "// services.Orders.WithReference(services.Billing).WaitFor(services.Billing);";

        return $"""
            // Aspire AppHost. "aspire run" (or dotnet run) starts every service, its database and the dashboard.
            // This file is yours: Stratum creates it once and never touches it again.
            var builder = DistributedApplication.CreateBuilder(args);

            // Generated from the stratum.json of each service (StratumServices.g.cs, regenerated by "stratum aspire").
            var services = builder.AddStratumServices();

            // Let a service call another by name (http://<name>) with service discovery, e.g.:
            {example}

            // Add your own resources here: Redis, RabbitMQ, a frontend...

            builder.Build().Run();

            """;
    }

    private static string StratumServices(IReadOnlyList<AspireService> services, string appHostName, IReadOnlyList<string> deploy)
    {
        var body = new StringBuilder();
        var providers = services.Select(s => s.Definition.Persistence?.Provider).Where(p => p is not null).Distinct().ToList();
        var azure = deploy.Contains(DeployAzure);

        if (deploy.Count > 0)
        {
            // "aspire publish" / "aspire deploy" take the system where STRATUM_DEPLOY says (aspire run ignores it).
            body.Append("        var deploy = builder.ExecutionContext.IsPublishMode ? StratumDeploy.Target(builder.Configuration) : null;\n");
            if (deploy.Contains(DeployDocker))
            {
                body.Append("        if (deploy == StratumDeploy.Docker)\n        {\n")
                    .Append("            builder.AddDockerComposeEnvironment(\"compose\");   // aspire deploy: docker compose up (aspire publish: only the files)\n        }\n\n");
            }

            if (azure)
            {
                body.Append("        if (deploy == StratumDeploy.Azure)\n        {\n")
                    .Append("            builder.AddAzureContainerAppEnvironment(\"aca\");   // aspire deploy: Azure Container Apps (az login first)\n        }\n\n");
            }
        }

        if (providers.Contains(PersistenceSettings.Postgres))
        {
            if (azure)
            {
                // Azure: Azure Database for PostgreSQL (flexible server) instead of a container; locally and in Docker, a container.
                body.Append("        var azurePostgres = deploy == StratumDeploy.Azure ? builder.AddAzurePostgresFlexibleServer(\"postgres\").WithPasswordAuthentication() : null;\n")
                    .Append("        var postgres = azurePostgres is null\n")
                    .Append("            ? builder.AddPostgres(\"postgres\").WithDataVolume().WithLifetime(ContainerLifetime.Persistent)\n")
                    .Append("            : null;\n\n");
            }
            else
            {
                body.Append("        var postgres = builder.AddPostgres(\"postgres\")\n")
                    .Append("            .WithDataVolume()\n")
                    .Append("            .WithLifetime(ContainerLifetime.Persistent);\n\n");
            }
        }

        if (providers.Contains(PersistenceSettings.SqlServer))
        {
            if (azure)
            {
                // Azure: Azure SQL Database (the services connect with their managed identity); locally and in Docker, a container.
                body.Append("        var azureSql = deploy == StratumDeploy.Azure ? builder.AddAzureSqlServer(\"sqlserver\") : null;\n")
                    .Append("        var sqlserver = azureSql is null\n")
                    .Append("            ? builder.AddSqlServer(\"sqlserver\").WithDataVolume().WithLifetime(ContainerLifetime.Persistent)\n")
                    .Append("            : null;\n\n");
            }
            else
            {
                body.Append("        var sqlserver = builder.AddSqlServer(\"sqlserver\")\n")
                    .Append("            .WithDataVolume()\n")
                    .Append("            .WithLifetime(ContainerLifetime.Persistent);\n\n");
            }
        }

        if (UsesKafka(services))
        {
            // One broker for the whole system; Kafka UI (link in the dashboard, fixed port for Stratum Studio's event
            // traces) shows topics and messages.
            if (deploy.Count == 0)
            {
                body.Append("        var kafka = builder.AddKafka(\"kafka\")\n")
                    .Append($"            .WithKafkaUI(ui => ui.WithHostPort({KafkaUiPort(appHostName)}))\n")
                    .Append("            .WithDataVolume()\n")
                    .Append("            .WithLifetime(ContainerLifetime.Persistent);\n\n");
            }
            else
            {
                // Deployed, Kafka is one more container (Kafka UI only locally).
                body.Append("        var kafka = builder.AddKafka(\"kafka\");\n")
                    .Append("        if (deploy is null)\n        {\n")
                    .Append($"            kafka.WithKafkaUI(ui => ui.WithHostPort({KafkaUiPort(appHostName)})).WithLifetime(ContainerLifetime.Persistent);\n        }}\n\n")
                    .Append(azure ? "        if (deploy != StratumDeploy.Azure)\n        {\n            kafka.WithDataVolume();   // Azure Files doesn't suit Kafka: deployed to Azure, its data lives in the container\n        }\n\n"
                                  : "        kafka.WithDataVolume();\n\n");
            }
        }

        if (UsesServiceBus(services))
        {
            // Local emulator (no Azure subscription). It only knows the topics and subscriptions declared here, so
            // they come from stratum.json: every event published, and one subscription per service that listens to it.
            body.Append("        var serviceBus = builder.AddAzureServiceBus(\"servicebus\")\n")
                .Append("            .RunAsEmulator();\n");

            var onServiceBus = services.Where(s => s.Definition.Messaging?.Broker == MessagingSettings.AzureServiceBus).Select(s => s.Definition).ToList();
            var topics = onServiceBus
                .SelectMany(d => IntegrationEvents.Of(d).Select(e => e.Topic))
                .Concat(onServiceBus.SelectMany(d => (d.Subscriptions ?? []).Select(s => s.Topic)))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            foreach (var topic in topics)
            {
                var resource = topic.Replace('.', '-');
                var consumers = onServiceBus.Where(d => (d.Subscriptions ?? []).Any(s => s.Topic == topic)).ToList();
                body.Append($"        var {TopicVariable(topic)} = serviceBus.AddServiceBusTopic(\"{resource}\", \"{topic}\");\n");
                if (consumers.Count == 0)
                {
                    // The emulator needs a subscription on every topic. Nobody listens to this event yet: its filter
                    // never matches (Subject is always the topic), so what is published is discarded, not piled up.
                    body.Append($"        {TopicVariable(topic)}.AddServiceBusSubscription(\"{resource}-none\", \"stratum-no-consumers\")\n")
                        .Append("            .WithProperties(subscription => subscription.Rules.Add(\n")
                        .Append("                new global::Aspire.Hosting.Azure.AzureServiceBusRule(\"none\") { CorrelationFilter = new() { Subject = \"stratum:no-consumers\" } }));\n");
                }

                foreach (var consumer in consumers)
                {
                    var subscription = ServiceName.ToKebab(consumer.Service.Name);
                    body.Append($"        {TopicVariable(topic)}.AddServiceBusSubscription(\"{subscription}-{resource}\", \"{subscription}\");\n");
                }
            }

            body.Append('\n');
        }

        foreach (var service in services)
        {
            var variable = Variable(service);
            var provider = service.Definition.Persistence?.Provider;

            if (provider is not null)
            {
                var server = provider == PersistenceSettings.Postgres ? "postgres" : "sqlserver";
                body.Append(azure
                    ? $"        IResourceBuilder<global::Aspire.Hosting.ApplicationModel.IResourceWithConnectionString> {variable}Db = {(provider == PersistenceSettings.Postgres ? "azurePostgres" : "azureSql")} is {{ }} azure{variable}\n" +
                      $"            ? azure{variable}.AddDatabase(\"{service.ResourceName}-db\")\n" +
                      $"            : {server}!.AddDatabase(\"{service.ResourceName}-db\");\n"
                    : $"        var {variable}Db = {server}.AddDatabase(\"{service.ResourceName}-db\");\n");
            }

            body.Append($"        var {variable} = builder.AddProject<Projects.{service.Name}_Api>(\"{service.ResourceName}\")\n");
            if (provider is not null)
            {
                body.Append($"            .WithReference({variable}Db, connectionName: \"Database\")\n")
                    .Append($"            .WaitFor({variable}Db)\n")
                    .Append("            .WithEnvironment(\"Database__MigrateOnStartup\", \"true\")\n");
            }

            if (service.Definition.Messaging?.Broker == MessagingSettings.Kafka)
            {
                body.Append("            .WithReference(kafka)\n")       // ConnectionStrings__kafka
                    .Append("            .WaitFor(kafka)\n");
            }

            if (service.Definition.Messaging?.Broker == MessagingSettings.AzureServiceBus)
            {
                body.Append("            .WithReference(serviceBus)\n")  // ConnectionStrings__servicebus
                    .Append("            .WaitFor(serviceBus)\n");
            }

            if (service.Definition.Features.OpenApi)
            {
                body.Append("            .WithUrlForEndpoint(\"http\", endpoint => new() { Url = \"/scalar\", DisplayText = \"Scalar\" })\n");
            }

            body.Append("            .WithHttpHealthCheck(\"/health/ready\");\n\n");
        }

        // A service with replicas reads the publisher's list once, when its copy is empty (initial load): it needs its address.
        var replicaReferences = services
            .SelectMany(consumer => (consumer.Definition.Persistence is null ? [] : consumer.Definition.Replicas ?? [])
                .Select(r => services.FirstOrDefault(s => s.Name == r.Service && s != consumer))
                .OfType<AspireService>()
                .Distinct()
                .Select(publisher => $"        {Variable(consumer)}.WithReference({Variable(publisher)});   // initial load of its replicas\n"))
            .ToList();
        if (replicaReferences.Count > 0)
        {
            body.Append(string.Concat(replicaReferences)).Append('\n');
        }

        var properties = string.Join("\n\n", services.Select(s =>
            $"    public required IResourceBuilder<ProjectResource> {Property(s)} {{ get; init; }}"));
        var assignments = string.Join("\n", services.Select(s => $"            {Property(s)} = {Variable(s)},"));

        return $$"""
            // <auto-generated>
            //   Generated by "stratum aspire" from the stratum.json of each service. Do not edit: it is regenerated.
            //   Customize in AppHost.cs using the returned resources.
            // </auto-generated>
            #nullable enable

            internal static class StratumServices
            {
                public static StratumServiceResources AddStratumServices(this IDistributedApplicationBuilder builder)
                {
                    ArgumentNullException.ThrowIfNull(builder);

            {{body}}        return new StratumServiceResources
                    {
            {{assignments}}
                    };
                }
            }

            internal sealed class StratumServiceResources
            {
            {{properties}}
            }
            {{(deploy.Count == 0 ? string.Empty : DeployHelper(deploy))}}
            """;
    }

    private static string DeployHelper(IReadOnlyList<string> deploy) => $$"""

        /// <summary>
        /// Where "aspire publish" and "aspire deploy" take the system: the STRATUM_DEPLOY variable
        /// ({{string.Join(" or ", deploy)}}){{(deploy.Count == 1 ? $"; without it, {deploy[0]}" : string.Empty)}}. Change the targets with "stratum deploy".
        /// </summary>
        internal static class StratumDeploy
        {
            public const string Docker = "docker";

            public const string Azure = "azure";

            public static readonly string[] Targets = [{{string.Join(", ", deploy.Select(d => d == DeployDocker ? "Docker" : "Azure"))}}];

            public static string Target(global::Microsoft.Extensions.Configuration.IConfiguration configuration)
            {
                var target = configuration["STRATUM_DEPLOY"]?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(target))
                {
                    return Targets.Length == 1
                        ? Targets[0]
                        : throw new InvalidOperationException($"Elige dónde desplegar: STRATUM_DEPLOY = {string.Join(" o ", Targets)} (PowerShell: $env:STRATUM_DEPLOY = \"{Targets[0]}\").");
                }

                return Targets.Contains(target)
                    ? target
                    : throw new InvalidOperationException($"STRATUM_DEPLOY = '{target}': este sistema despliega en {string.Join(" o ", Targets)} (stratum deploy add para otro).");
            }
        }

        """;

    private static string Solution(string root, string appHost, IReadOnlyList<AspireService> services)
    {
        var sb = new StringBuilder("<Solution>\n");
        sb.Append($"  <Project Path=\"{appHost}/{appHost}.csproj\" />\n");

        foreach (var service in services)
        {
            var projects = Directory.EnumerateFiles(Path.Combine(root, service.RelativeDirectory), "*.csproj", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .Where(p => !p.Contains("/bin/", StringComparison.Ordinal) && !p.Contains("/obj/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal);

            sb.Append($"  <Folder Name=\"/{service.ResourceName}/\">\n");
            foreach (var project in projects)
            {
                sb.Append($"    <Project Path=\"{project}\" />\n");
            }

            sb.Append("  </Folder>\n");
        }

        return sb.Append("</Solution>\n").ToString();
    }

    private const string AppSettings = """
        {
          "Logging": {
            "LogLevel": {
              "Default": "Information",
              "Microsoft.AspNetCore": "Warning",
              "Aspire.Hosting.Dcp": "Warning"
            }
          }
        }

        """;

    /// <summary>Port of the Aspire dashboard of an AppHost generated by Stratum (its launchSettings.json).</summary>
    public static int DashboardPort(string appHostName) => ServiceName.DevPort(appHostName + "AppHost") + 10000; // 15100-15999: away from the services' ports

    /// <summary>Port of Kafka UI in an AppHost generated by Stratum (18100-18999).</summary>
    public static int KafkaUiPort(string appHostName) => DashboardPort(appHostName) + 3000;

    private static string LaunchSettings(string name)
    {
        var port = DashboardPort(name);
        return $$"""
            {
              "$schema": "https://json.schemastore.org/launchsettings.json",
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "dotnetRunMessages": true,
                  "launchBrowser": true,
                  "applicationUrl": "http://localhost:{{port}}",
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development",
                    "DOTNET_ENVIRONMENT": "Development",
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "http://localhost:{{port + 1000}}",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "http://localhost:{{port + 2000}}",
                    "ASPIRE_ALLOW_UNSECURED_TRANSPORT": "true"
                  }
                }
              }
            }

            """;
    }

    private static bool UsesServiceBus(IReadOnlyList<AspireService> services) =>
        services.Any(s => s.Definition.Messaging?.Broker == MessagingSettings.AzureServiceBus);

    private static string TopicVariable(string topic)
    {
        var parts = topic.Split('.', '-');
        return "topic" + string.Concat(parts.Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static bool UsesKafka(IReadOnlyList<AspireService> services) =>
        services.Any(s => s.Definition.Messaging?.Broker == MessagingSettings.Kafka);

    private static string Variable(AspireService service) => EntityCodeGen.Camel(service.Name);

    private static string Property(AspireService service) => service.Name;

    private static Guid StableGuid(string name) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("msg-apphost:" + name)).AsSpan(0, 16));
}
