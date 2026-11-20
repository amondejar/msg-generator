using System.Security.Cryptography;
using System.Text;
using Msg.Core.Model;

namespace Msg.Core.Generation;

/// <summary>An MSG service found under the system root.</summary>
public sealed record AspireService(string RelativeDirectory, ServiceDefinition Definition)
{
    public string Name => Definition.Service.Name;

    public string ResourceName => ServiceName.ToKebab(Name);

    public string ApiProject => $"{RelativeDirectory}/src/{Name}.Api/{Name}.Api.csproj";
}

public sealed record AspireResult(string AppHostName, IReadOnlyList<AspireService> Services, IReadOnlyList<string> Created, IReadOnlyList<string> Updated);

/// <summary>
/// Generates an Aspire AppHost that runs every MSG service under a folder, with their databases.
/// <list type="bullet">
/// <item><c>MsgServices.g.cs</c> and the system <c>.slnx</c> are regenerated on every run (MSG owns them).</item>
/// <item><c>AppHost.cs</c>, settings and launch profile are created once: they are yours.</item>
/// <item>The AppHost .csproj only gets the missing references added.</item>
/// </list>
/// </summary>
public static class AspireGenerator
{
    /// <summary>Latest 13.x: the line validated with .NET 10. Bump with "aspire update" when you want to.</summary>
    public const string AspireVersion = "13.6.5";

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "artifacts", "TestResults",
    };

    public static IReadOnlyList<AspireService> Discover(string root, int maxDepth = 3)
    {
        var rootFull = Path.GetFullPath(root);
        var found = new List<AspireService>();
        Walk(rootFull, 0);

        // Services in other folders, listed in msg.workspace.json.
        foreach (var directory in WorkspaceFile.ExternalServices(rootFull))
        {
            var definition = ServiceGenerator.Deserialize(File.ReadAllText(Path.Combine(directory, ServiceGenerator.DefinitionFileName)));
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
            var definitionPath = Path.Combine(directory, ServiceGenerator.DefinitionFileName);
            if (directory != rootFull && File.Exists(definitionPath))
            {
                var definition = ServiceGenerator.Deserialize(File.ReadAllText(definitionPath));
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

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!SkippedFolders.Contains(Path.GetFileName(child)) && !child.EndsWith(".AppHost", StringComparison.Ordinal))
                {
                    Walk(child, depth + 1);
                }
            }
        }
    }

    public static AspireResult Generate(string root, string appHostName, IReadOnlyList<AspireService> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ServiceName.Validate(appHostName);

        if (services.Count == 0)
        {
            throw new MsgException($"No se encontró ningún servicio MSG (msg.json) bajo '{Path.GetFullPath(root)}'.");
        }

        var duplicated = services.GroupBy(s => s.ResourceName).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            throw new MsgException($"Hay varios servicios llamados '{duplicated.Key}': {string.Join(", ", duplicated.Select(s => s.RelativeDirectory))}.");
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
        var csproj = Csproj(root, appHostName, services);
        if (File.Exists(Path.Combine(root, csprojPath)))
        {
            if (ProjectFileMerger.Merge(Path.Combine(root, csprojPath), csproj))
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
        // upwards, so a service's local Msg.* source ("msg new --package-source") must also exist at the root.
        var serviceConfig = services
            .Select(s => Path.Combine(root, s.RelativeDirectory, "nuget.config"))
            .FirstOrDefault(File.Exists);
        if (serviceConfig is not null)
        {
            CreateOnce("nuget.config", File.ReadAllText(serviceConfig));
        }

        Overwrite($"{appHost}/MsgServices.g.cs", MsgServices(services));
        Overwrite($"{appHostName}.slnx", Solution(root, appHost, services));

        return new AspireResult(appHost, services, created, updated);
    }

    private static string Csproj(string root, string name, IReadOnlyList<AspireService> services)
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

        // Relative to the AppHost folder; absolute for a service on another drive.
        var projects = string.Concat(services.Select(s =>
            $"    <ProjectReference Include=\"{Path.GetRelativePath(Path.GetFullPath(Path.Combine(root, $"{name}.AppHost")), Path.GetFullPath(Path.Combine(root, s.ApiProject))).Replace('/', '\\')}\" />\n"));

        return $"""
            <Project Sdk="Aspire.AppHost.Sdk/{AspireVersion}">

              <!-- Generated by "msg aspire". Runs every MSG service of this folder with its dependencies. -->

              <PropertyGroup>
                <OutputType>Exe</OutputType>
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
            // This file is yours: MSG creates it once and never touches it again.
            var builder = DistributedApplication.CreateBuilder(args);

            // Generated from the msg.json of each service (MsgServices.g.cs, regenerated by "msg aspire").
            var services = builder.AddMsgServices();

            // Let a service call another by name (http://<name>) with service discovery, e.g.:
            {example}

            // Add your own resources here: Redis, RabbitMQ, a frontend...

            builder.Build().Run();

            """;
    }

    private static string MsgServices(IReadOnlyList<AspireService> services)
    {
        var body = new StringBuilder();
        var providers = services.Select(s => s.Definition.Persistence?.Provider).Where(p => p is not null).Distinct().ToList();

        if (providers.Contains(PersistenceSettings.Postgres))
        {
            body.Append("        var postgres = builder.AddPostgres(\"postgres\")\n")
                .Append("            .WithDataVolume()\n")
                .Append("            .WithLifetime(ContainerLifetime.Persistent);\n\n");
        }

        if (providers.Contains(PersistenceSettings.SqlServer))
        {
            body.Append("        var sqlserver = builder.AddSqlServer(\"sqlserver\")\n")
                .Append("            .WithDataVolume()\n")
                .Append("            .WithLifetime(ContainerLifetime.Persistent);\n\n");
        }

        if (UsesKafka(services))
        {
            // One broker for the whole system; Kafka UI (link in the dashboard) shows topics and messages.
            body.Append("        var kafka = builder.AddKafka(\"kafka\")\n")
                .Append("            .WithKafkaUI()\n")
                .Append("            .WithDataVolume()\n")
                .Append("            .WithLifetime(ContainerLifetime.Persistent);\n\n");
        }

        foreach (var service in services)
        {
            var variable = Variable(service);
            var provider = service.Definition.Persistence?.Provider;

            if (provider is not null)
            {
                var server = provider == PersistenceSettings.Postgres ? "postgres" : "sqlserver";
                body.Append($"        var {variable}Db = {server}.AddDatabase(\"{service.ResourceName}-db\");\n");
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

            if (service.Definition.Features.OpenApi)
            {
                body.Append("            .WithUrlForEndpoint(\"http\", endpoint => new() { Url = \"/scalar\", DisplayText = \"Scalar\" })\n");
            }

            body.Append("            .WithHttpHealthCheck(\"/health/ready\");\n\n");
        }

        var properties = string.Join("\n\n", services.Select(s =>
            $"    public required IResourceBuilder<ProjectResource> {Property(s)} {{ get; init; }}"));
        var assignments = string.Join("\n", services.Select(s => $"            {Property(s)} = {Variable(s)},"));

        return $$"""
            // <auto-generated>
            //   Generated by "msg aspire" from the msg.json of each service. Do not edit: it is regenerated.
            //   Customize in AppHost.cs using the returned resources.
            // </auto-generated>
            #nullable enable

            internal static class MsgServices
            {
                public static MsgServiceResources AddMsgServices(this IDistributedApplicationBuilder builder)
                {
                    ArgumentNullException.ThrowIfNull(builder);

            {{body}}        return new MsgServiceResources
                    {
            {{assignments}}
                    };
                }
            }

            internal sealed class MsgServiceResources
            {
            {{properties}}
            }

            """;
    }

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

    private static string LaunchSettings(string name)
    {
        var port = ServiceName.DevPort(name + "AppHost") + 10000; // 15100-15999: away from the services' ports
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

    private static bool UsesKafka(IReadOnlyList<AspireService> services) =>
        services.Any(s => s.Definition.Messaging?.Broker == MessagingSettings.Kafka);

    private static string Variable(AspireService service) => EntityCodeGen.Camel(service.Name);

    private static string Property(AspireService service) => service.Name;

    private static Guid StableGuid(string name) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("msg-apphost:" + name)).AsSpan(0, 16));
}
