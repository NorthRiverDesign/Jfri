using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jfri.Core.Analysis.Detectors;

/// <summary>Detects Node.js servers (Next.js, Nuxt, Express, NestJS, ...) and Vite-style static front ends.</summary>
internal sealed partial class NodeDetector : IStackDetector
{
	static readonly (string Package, ServiceKind Kind)[] ServicePackages =
	[
		("pg", ServiceKind.PostgreSql), ("postgres", ServiceKind.PostgreSql), ("@neondatabase/serverless", ServiceKind.PostgreSql),
		("mysql2", ServiceKind.MySql), ("mysql", ServiceKind.MySql), ("mariadb", ServiceKind.MariaDb),
		("mongoose", ServiceKind.MongoDb), ("mongodb", ServiceKind.MongoDb),
		("redis", ServiceKind.Redis), ("ioredis", ServiceKind.Redis), ("bullmq", ServiceKind.Redis), ("bull", ServiceKind.Redis),
		("better-sqlite3", ServiceKind.Sqlite), ("sqlite3", ServiceKind.Sqlite), ("@libsql/client", ServiceKind.Sqlite),
	];

	public IEnumerable<Candidate> Detect(RepoFiles files)
	{
		foreach (var packagePath in files.FindByName("package.json"))
		{
			var dir = RepoFiles.DirectoryOf(packagePath);
			if (dir.Split('/').Length > 3 || files.Exists(RepoFiles.Join(dir, "composer.json")) || IsDotNetClientApp(files, dir))
			{
				// PHP apps build their assets with npm; that's handled by the PHP detector.
				continue;
			}
			using var doc = Parsing.Json(files.Read(packagePath));
			if (doc is null)
			{
				continue;
			}
			var candidate = Build(files, dir, packagePath, doc.RootElement);
			if (candidate is not null)
			{
				yield return candidate;
			}
		}
	}

	static bool IsDotNetClientApp(RepoFiles files, string dir) =>
		dir.Length > 0 && (files.FilesIn(RepoFiles.DirectoryOf(dir)).Any(f => f.EndsWith(".csproj", StringComparison.Ordinal)));

	static Candidate? Build(RepoFiles files, string dir, string packagePath, JsonElement pkg)
	{
		var deps = Parsing.KeysOf(pkg, "dependencies");
		var allDeps = Parsing.KeysOf(pkg, "dependencies", "devDependencies");
		var scripts = pkg.GetObject("scripts");
		string? Script(string name) => scripts?.GetString(name);
		if (pkg.TryGetProperty("workspaces", out _) && Script("start") is null)
		{
			// A monorepo root; its packages are detected individually.
			return null;
		}

		var app = new AppProfile
		{
			Stack = Stack.Node,
			RootPath = dir,
			Port = 3000,
			Confidence = Confidence.High,
		};
		var candidate = new Candidate(app) { Score = 80 - dir.Count(c => c == '/') * 5 };
		app.PackageManager = PackageManager(files, dir, pkg);
		var run = app.PackageManager switch { "pnpm" => "pnpm", "yarn" => "yarn", "bun" => "bun run", _ => "npm run" };
		app.RuntimeVersion = NodeVersion(files, dir, pkg);
		app.AddEvidence(packagePath, $"package.json (package manager: {app.PackageManager})");
		if (Script("build") is not null)
		{
			app.BuildCommand = $"{run} build";
		}
		if (Script("test") is { } test && !test.Contains("no test specified", StringComparison.Ordinal))
		{
			app.TestCommand = $"{run} test";
		}

		if (deps.Contains("next"))
		{
			app.Framework = "Next.js";
			app.StartCommand = "node server.js";
			var nextConfig = files.FindByName("next.config.*", dir).Select(files.Read).FirstOrDefault(t => t is not null) ?? "";
			if (!StandaloneRegex().IsMatch(nextConfig))
			{
				app.StartCommand = $"{run} start";
				candidate.Needs.Warnings.Add("Next.js: set output: 'standalone' in next.config for a much smaller production image.");
			}
			else
			{
				app.AddEvidence(dir.Length == 0 ? "next.config" : $"{dir}/next.config", "standalone output");
			}
			candidate.Score += 10;
		}
		else if (deps.Contains("nuxt"))
		{
			app.Framework = "Nuxt";
			app.StartCommand = "node .output/server/index.mjs";
			candidate.Score += 10;
		}
		else if (deps.Contains("@sveltejs/kit") && allDeps.Contains("@sveltejs/adapter-node"))
		{
			app.Framework = "SvelteKit";
			app.StartCommand = "node build";
			candidate.Score += 10;
		}
		else if (deps.Contains("@remix-run/serve") || deps.Contains("@react-router/serve"))
		{
			app.Framework = deps.Contains("@react-router/serve") ? "React Router" : "Remix";
			app.StartCommand = $"{run} start";
			candidate.Score += 10;
		}
		else if (deps.Contains("@nestjs/core"))
		{
			app.Framework = "NestJS";
			app.StartCommand = Script("start:prod") is not null ? $"{run} start:prod" : "node dist/main.js";
			candidate.Score += 10;
		}
		else if (new[] { "express", "fastify", "koa", "hono", "@hapi/hapi", "@adonisjs/core", "h3" }.FirstOrDefault(deps.Contains) is { } server)
		{
			app.Framework = server switch { "express" => "Express", "fastify" => "Fastify", "koa" => "Koa", "hono" => "Hono", "@hapi/hapi" => "hapi", "@adonisjs/core" => "AdonisJS", _ => "h3" };
			app.StartCommand = Script("start") is not null ? $"{run} start" : MainFile(pkg) is { } main ? $"node {main}" : null;
		}
		else if (allDeps.Contains("vite") || allDeps.Contains("@angular/core") || allDeps.Contains("react-scripts") || allDeps.Contains("astro") || deps.Contains("@sveltejs/kit"))
		{
			// A client-side build: compile once, serve the files with a hardened static web server.
			app.Stack = Stack.Static;
			app.Framework = allDeps.Contains("astro") ? "Astro (static)" : allDeps.Contains("@angular/core") ? "Angular" : allDeps.Contains("react-scripts") ? "Create React App" : allDeps.Contains("@sveltejs/kit") ? "SvelteKit (static)" : "Vite";
			app.BuildCommand ??= $"{run} build";
			app.DocumentRoot = allDeps.Contains("react-scripts") ? "build" : allDeps.Contains("@angular/core") ? $"dist/{pkg.GetString("name") ?? "app"}/browser" : allDeps.Contains("@sveltejs/kit") ? "build" : "dist";
			app.Port = 8080;
			app.Processes.Add(new ProcessSpec("web", ProcessKind.Web, null));
			candidate.Score -= 10;
			return candidate;
		}
		else if (Script("start") is not null)
		{
			app.StartCommand = $"{run} start";
			app.Confidence = Confidence.Medium;
		}
		else
		{
			// A library or tooling package; not deployable on its own.
			return null;
		}
		app.Framework ??= "Node.js";
		app.Processes.Add(new ProcessSpec("web", ProcessKind.Web, null));

		foreach (var (package, kind) in ServicePackages)
		{
			if (deps.Contains(package))
			{
				candidate.Needs.Require(kind, new Evidence(packagePath, $"depends on {package}"), Confidence.High);
			}
		}
		Prisma(files, dir, candidate);
		Drizzle(files, dir, candidate, allDeps);
		var envSource = files.FirstExisting(RepoFiles.Join(dir, ".env.example"), RepoFiles.Join(dir, ".env.sample"));
		if (envSource is not null)
		{
			foreach (var (key, value) in Parsing.ParseEnv(files.Read(envSource)))
			{
				var secret = Parsing.LooksSecret(key);
				candidate.Needs.AddEnvironment(new EnvironmentKey(key, secret ? null : value, secret, envSource));
				if (key == "DATABASE_URL" && value.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
				{
					candidate.Needs.Require(ServiceKind.PostgreSql, new Evidence(envSource, "DATABASE_URL is a PostgreSQL URL"), Confidence.Medium);
				}
				else if (key == "DATABASE_URL" && value.StartsWith("mysql", StringComparison.OrdinalIgnoreCase))
				{
					candidate.Needs.Require(ServiceKind.MySql, new Evidence(envSource, "DATABASE_URL is a MySQL URL"), Confidence.Medium);
				}
				else if (key is "REDIS_URL" or "REDIS_HOST")
				{
					candidate.Needs.Require(ServiceKind.Redis, new Evidence(envSource, key), Confidence.Medium);
				}
			}
		}
		if (allDeps.Contains("bullmq") || allDeps.Contains("bull"))
		{
			candidate.Needs.Warnings.Add("A job queue library is used; if workers run as a separate script, add a worker process to the plan.");
		}
		return candidate;
	}

	static void Prisma(RepoFiles files, string dir, Candidate candidate)
	{
		var schemaPath = files.FirstExisting(RepoFiles.Join(dir, "prisma/schema.prisma"), RepoFiles.Join(dir, "schema.prisma"));
		if (schemaPath is null)
		{
			return;
		}
		var schema = files.Read(schemaPath) ?? "";
		var provider = PrismaProviderRegex().Match(schema);
		if (provider.Success)
		{
			ServiceKind? kind = provider.Groups[1].Value switch
			{
				"postgresql" or "postgres" or "cockroachdb" => ServiceKind.PostgreSql,
				"mysql" => ServiceKind.MySql,
				"sqlite" => ServiceKind.Sqlite,
				"mongodb" => ServiceKind.MongoDb,
				"sqlserver" => ServiceKind.SqlServer,
				_ => null,
			};
			if (kind is not null)
			{
				candidate.Needs.Require(kind.Value, new Evidence(schemaPath, $"Prisma datasource provider \"{provider.Groups[1].Value}\""), Confidence.High);
			}
		}
		if (files.DirectoryExists(RepoFiles.Join(dir, "prisma/migrations")))
		{
			candidate.App.ReleaseCommand = "npx prisma migrate deploy";
			candidate.App.AddEvidence(RepoFiles.Join(dir, "prisma/migrations"), "Prisma migrations run on each release");
		}
	}

	static void Drizzle(RepoFiles files, string dir, Candidate candidate, HashSet<string> allDeps)
	{
		if (!allDeps.Contains("drizzle-orm"))
		{
			return;
		}
		var config = files.FindByName("drizzle.config.*", dir).FirstOrDefault();
		var text = config is null ? "" : files.Read(config) ?? "";
		var dialect = DrizzleDialectRegex().Match(text);
		if (dialect.Success && config is not null)
		{
			ServiceKind? kind = dialect.Groups[1].Value switch { "postgresql" => ServiceKind.PostgreSql, "mysql" => ServiceKind.MySql, "sqlite" or "turso" => ServiceKind.Sqlite, _ => null };
			if (kind is not null)
			{
				candidate.Needs.Require(kind.Value, new Evidence(config, $"Drizzle dialect \"{dialect.Groups[1].Value}\""), Confidence.High);
			}
		}
	}

	static string PackageManager(RepoFiles files, string dir, JsonElement pkg)
	{
		var declared = pkg.GetString("packageManager");
		if (declared is not null)
		{
			return declared.Split('@')[0];
		}
		if (files.Exists(RepoFiles.Join(dir, "pnpm-lock.yaml")) || files.Exists("pnpm-lock.yaml"))
		{
			return "pnpm";
		}
		if (files.Exists(RepoFiles.Join(dir, "yarn.lock")) || files.Exists("yarn.lock"))
		{
			return "yarn";
		}
		if (files.Exists(RepoFiles.Join(dir, "bun.lockb")) || files.Exists(RepoFiles.Join(dir, "bun.lock")))
		{
			return "bun";
		}
		return "npm";
	}

	static string NodeVersion(RepoFiles files, string dir, JsonElement pkg)
	{
		var pinned = files.Read(RepoFiles.Join(dir, ".nvmrc")) ?? files.Read(RepoFiles.Join(dir, ".node-version")) ?? files.Read(".nvmrc") ?? files.Read(".node-version");
		if (pinned is not null && VersionRange.Major(pinned.Trim()) is { } major && !pinned.Contains("lts", StringComparison.OrdinalIgnoreCase))
		{
			return major;
		}
		var engines = pkg.GetObject("engines")?.GetString("node");
		return VersionRange.Pick(engines, RuntimeDefaults.Node, RuntimeDefaults.NodeSupported) ?? RuntimeDefaults.Node;
	}

	static string? MainFile(JsonElement pkg) => pkg.GetString("main");

	[GeneratedRegex(@"output\s*:\s*['""]standalone['""]")]
	private static partial Regex StandaloneRegex();

	[GeneratedRegex(@"provider\s*=\s*""(postgresql|postgres|mysql|sqlite|mongodb|sqlserver|cockroachdb)""")]
	private static partial Regex PrismaProviderRegex();

	[GeneratedRegex(@"dialect\s*:\s*['""](\w+)['""]")]
	private static partial Regex DrizzleDialectRegex();
}
