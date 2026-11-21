using System.Text.RegularExpressions;

namespace Jfri.Core.Analysis.Detectors;

/// <summary>Detects Django, Flask and FastAPI apps from requirements.txt / pyproject.toml / Pipfile.</summary>
internal sealed partial class PythonDetector : IStackDetector
{
	public IEnumerable<Candidate> Detect(RepoFiles files)
	{
		var manifests = files.FindByName("requirements.txt").Concat(files.FindByName("pyproject.toml")).Concat(files.FindByName("Pipfile"))
			.Select(RepoFiles.DirectoryOf)
			.Where(d => d.Split('/').Length <= 2)
			.Distinct()
			.ToList();
		foreach (var dir in manifests)
		{
			var candidate = Build(files, dir);
			if (candidate is not null)
			{
				yield return candidate;
			}
		}
	}

	static Candidate? Build(RepoFiles files, string dir)
	{
		var requirementsPath = RepoFiles.Join(dir, "requirements.txt");
		var pyprojectPath = RepoFiles.Join(dir, "pyproject.toml");
		var pipfilePath = RepoFiles.Join(dir, "Pipfile");
		var deps = (files.Read(requirementsPath) ?? "") + "\n" + (files.Read(pyprojectPath) ?? "") + "\n" + (files.Read(pipfilePath) ?? "");
		bool Has(string package) => Regex.IsMatch(deps, $@"(^|[\s""'\[,]){Regex.Escape(package)}(\s*[=<>~!\[;,""']|\s*$)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
		var manifest = files.FirstExisting(requirementsPath, pyprojectPath, pipfilePath)!;

		var app = new AppProfile { Stack = Stack.Python, RootPath = dir, Port = 8000, Confidence = Confidence.High };
		var candidate = new Candidate(app) { Score = 80 - dir.Count(c => c == '/') * 5 };
		app.PackageManager = files.Exists(RepoFiles.Join(dir, "uv.lock")) ? "uv"
			: files.Exists(RepoFiles.Join(dir, "poetry.lock")) ? "poetry"
			: files.Exists(pipfilePath) ? "pipenv"
			: files.Exists(requirementsPath) ? "pip"
			: "pip";
		app.RuntimeVersion = PythonVersion(files, dir);
		app.AddEvidence(manifest, $"Python dependencies (installed with {app.PackageManager})");

		var managePy = RepoFiles.Join(dir, "manage.py");
		string? celeryApp = null;
		if (Has("django") && files.Exists(managePy))
		{
			app.Framework = "Django";
			var settingsModule = SettingsModuleRegex().Match(files.Read(managePy) ?? "");
			var project = settingsModule.Success ? settingsModule.Groups[1].Value : files.FindByName("wsgi.py", dir).Select(p => RepoFiles.DirectoryOf(p)[dir.Length..].Trim('/').Replace('/', '.')).FirstOrDefault();
			app.StartCommand = $"gunicorn {project ?? "config"}.wsgi:application --bind 0.0.0.0:8000 --workers 3 --access-logfile -";
			app.ReleaseCommand = "python manage.py migrate --noinput";
			app.BuildCommand = "python manage.py collectstatic --noinput";
			app.TestCommand = "python manage.py test";
			app.AddEvidence(managePy, $"Django project ({project ?? "unknown settings module"})");
			celeryApp = project;
			candidate.Needs.AddPersistentPath("/app/media", "Django MEDIA_ROOT (user uploads)", new Evidence(managePy, "Django project"));
			candidate.Needs.AddEnvironment(new EnvironmentKey("DJANGO_SECRET_KEY", null, true, managePy));
			candidate.Score += 15;
			if (!Has("gunicorn"))
			{
				candidate.Needs.Warnings.Add("gunicorn isn't a dependency; the generated image installs it so Django runs on a production server, not runserver.");
			}
		}
		else if (Has("fastapi"))
		{
			app.Framework = "FastAPI";
			var module = FindAppModule(files, dir, FastApiRegex()) ?? "main";
			app.StartCommand = $"uvicorn {module}:app --host 0.0.0.0 --port 8000 --proxy-headers";
			app.AddEvidence(manifest, $"FastAPI app in {module}");
			candidate.Score += 10;
		}
		else if (Has("flask"))
		{
			app.Framework = "Flask";
			var module = FindAppModule(files, dir, FlaskRegex()) ?? "app";
			app.StartCommand = $"gunicorn {module}:app --bind 0.0.0.0:8000 --workers 3 --access-logfile -";
			app.AddEvidence(manifest, $"Flask app in {module}");
			candidate.Score += 10;
		}
		else
		{
			return null;
		}
		app.Processes.Add(new ProcessSpec("web", ProcessKind.Web, null));
		if (Has("pytest") && app.TestCommand is null)
		{
			app.TestCommand = "pytest";
		}
		if (Has("celery"))
		{
			app.Processes.Add(new ProcessSpec("worker", ProcessKind.Worker, $"celery -A {celeryApp ?? "app"} worker --loglevel=info"));
			candidate.Needs.Warnings.Add("Celery detected: check the worker's -A module in the plan.");
		}

		if (Has("psycopg") || Has("psycopg2") || Has("psycopg2-binary") || Has("asyncpg") || Has("psycopg-binary"))
		{
			candidate.Needs.Require(ServiceKind.PostgreSql, new Evidence(manifest, "PostgreSQL driver"), Confidence.High);
		}
		if (Has("mysqlclient") || Has("pymysql") || Has("aiomysql") || Has("mysql-connector-python"))
		{
			candidate.Needs.Require(ServiceKind.MySql, new Evidence(manifest, "MySQL driver"), Confidence.High);
		}
		if (Has("pymongo") || Has("motor") || Has("mongoengine"))
		{
			candidate.Needs.Require(ServiceKind.MongoDb, new Evidence(manifest, "MongoDB driver"), Confidence.High);
		}
		if (Has("redis") || Has("django-redis") || Has("celery"))
		{
			candidate.Needs.Require(ServiceKind.Redis, new Evidence(manifest, Has("celery") ? "Celery broker" : "Redis client"), Has("redis") ? Confidence.High : Confidence.Medium);
		}
		if (app.Framework == "Django" && candidate.Needs.Database is null)
		{
			candidate.Needs.Require(ServiceKind.Sqlite, new Evidence(managePy, "Django's default database"), Confidence.Low);
		}
		var envSource = files.FirstExisting(RepoFiles.Join(dir, ".env.example"), RepoFiles.Join(dir, ".env.sample"));
		if (envSource is not null)
		{
			foreach (var (key, value) in Parsing.ParseEnv(files.Read(envSource)))
			{
				var secret = Parsing.LooksSecret(key);
				candidate.Needs.AddEnvironment(new EnvironmentKey(key, secret ? null : value, secret, envSource));
			}
		}
		return candidate;
	}

	static string? FindAppModule(RepoFiles files, string dir, Regex pattern)
	{
		foreach (var file in files.FindByName("*.py", dir).Where(f => f.Count(c => c == '/') - dir.Count(c => c == '/') <= 2).Take(400))
		{
			if (pattern.IsMatch(files.Read(file) ?? ""))
			{
				var rel = (dir.Length == 0 ? file : file[(dir.Length + 1)..])[..^3];
				return rel.Replace('/', '.');
			}
		}
		return null;
	}

	static string PythonVersion(RepoFiles files, string dir)
	{
		var pinned = files.Read(RepoFiles.Join(dir, ".python-version"));
		if (VersionRange.MajorMinor(pinned) is { } v)
		{
			return v;
		}
		var runtime = files.Read(RepoFiles.Join(dir, "runtime.txt"));
		if (VersionRange.MajorMinor(runtime) is { } r)
		{
			return r;
		}
		var requires = Parsing.TomlValue(files.Read(RepoFiles.Join(dir, "pyproject.toml")), "project", "requires-python")
			?? Parsing.TomlValue(files.Read(RepoFiles.Join(dir, "pyproject.toml")), "tool.poetry.dependencies", "python");
		return VersionRange.Pick(requires, RuntimeDefaults.Python, RuntimeDefaults.PythonSupported) ?? RuntimeDefaults.Python;
	}

	[GeneratedRegex(@"DJANGO_SETTINGS_MODULE['""]?\s*,\s*['""]([\w]+)\.settings")]
	private static partial Regex SettingsModuleRegex();

	[GeneratedRegex(@"^\s*app\s*=\s*FastAPI\(", RegexOptions.Multiline)]
	private static partial Regex FastApiRegex();

	[GeneratedRegex(@"^\s*app\s*=\s*Flask\(", RegexOptions.Multiline)]
	private static partial Regex FlaskRegex();
}
