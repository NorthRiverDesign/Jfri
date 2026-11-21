using Jfri.Core.Analysis;
using Jfri.Core.Planning;

namespace Jfri.Core.Generation;

/// <summary>How to run, probe and back up each backing service, shared by every runtime generator.</summary>
internal static class ServiceRecipes
{
	/// <summary>Environment for the service container. Values may reference {{secret:...}} tokens.</summary>
	public static List<(string Key, string Value)> Environment(PlannedService service) => service.Kind switch
	{
		ServiceKind.PostgreSql => VersionRange.ParseVersion(service.Version).Major >= 18
			? [("POSTGRES_DB", Planner.DbName), ("POSTGRES_USER", Planner.DbUser), ("POSTGRES_PASSWORD", "{{secret:DB_PASSWORD}}")]
			: [("POSTGRES_DB", Planner.DbName), ("POSTGRES_USER", Planner.DbUser), ("POSTGRES_PASSWORD", "{{secret:DB_PASSWORD}}"), ("PGDATA", "/var/lib/postgresql/data/pgdata")],
		ServiceKind.MySql => [("MYSQL_DATABASE", Planner.DbName), ("MYSQL_USER", Planner.DbUser), ("MYSQL_PASSWORD", "{{secret:DB_PASSWORD}}"), ("MYSQL_ROOT_PASSWORD", "{{secret:DB_ROOT_PASSWORD}}")],
		ServiceKind.MariaDb => [("MARIADB_DATABASE", Planner.DbName), ("MARIADB_USER", Planner.DbUser), ("MARIADB_PASSWORD", "{{secret:DB_PASSWORD}}"), ("MARIADB_ROOT_PASSWORD", "{{secret:DB_ROOT_PASSWORD}}")],
		ServiceKind.MongoDb => [("MONGO_INITDB_ROOT_USERNAME", Planner.DbUser), ("MONGO_INITDB_ROOT_PASSWORD", "{{secret:DB_PASSWORD}}"), ("MONGO_INITDB_DATABASE", Planner.DbName)],
		ServiceKind.SqlServer => [("ACCEPT_EULA", "Y"), ("MSSQL_PID", "Express"), ("MSSQL_SA_PASSWORD", "{{secret:DB_PASSWORD}}")],
		ServiceKind.Redis => [("REDIS_PASSWORD", "{{secret:REDIS_PASSWORD}}"), ("REDISCLI_AUTH", "{{secret:REDIS_PASSWORD}}")],
		_ => [],
	};

	/// <summary>Extra secrets a service needs beyond the app's (e.g. the MySQL root password).</summary>
	public static IEnumerable<PlannedSecret> Secrets(PlannedService service) => service.Kind is ServiceKind.MySql or ServiceKind.MariaDb
		? [new PlannedSecret("DB_ROOT_PASSWORD", SecretKind.Password, "database root password")]
		: [];

	/// <summary>Container args (after the image entrypoint), or null for the image default.</summary>
	public static string[]? Args(PlannedService service, bool kubernetes) => service.Kind switch
	{
		ServiceKind.Redis => ["--requirepass", kubernetes ? "$(REDIS_PASSWORD)" : "${REDIS_PASSWORD}", "--appendonly", "yes", "--maxmemory", "200mb", "--maxmemory-policy", "noeviction"],
		ServiceKind.MySql or ServiceKind.MariaDb => ["--character-set-server=utf8mb4", "--collation-server=utf8mb4_unicode_ci"],
		_ => null,
	};

	/// <summary>A shell command that exits 0 when the service accepts connections.</summary>
	public static string Ready(PlannedService service) => service.Kind switch
	{
		ServiceKind.PostgreSql => $"pg_isready -U {Planner.DbUser} -d {Planner.DbName} -h 127.0.0.1",
		ServiceKind.MySql => "mysqladmin ping -h 127.0.0.1 --silent",
		ServiceKind.MariaDb => "mariadb-admin ping -h 127.0.0.1 --silent",
		ServiceKind.MongoDb => "mongosh --quiet --eval \"db.adminCommand('ping').ok\"",
		ServiceKind.SqlServer => "/opt/mssql-tools18/bin/sqlcmd -C -S 127.0.0.1 -U sa -P \"$MSSQL_SA_PASSWORD\" -Q 'SELECT 1' -b -o /dev/null",
		ServiceKind.Redis => "valkey-cli ping | grep -q PONG",
		_ => "true",
	};

	/// <summary>UID the service image runs its process as (so it can run with runAsNonRoot).</summary>
	public static int Uid(PlannedService service) => service.Kind switch
	{
		ServiceKind.PostgreSql => 70,
		ServiceKind.SqlServer => 10001,
		_ => 999,
	};

	/// <summary>
	/// Shell run by the backup job: dumps the database into /backups with a timestamp and deletes dumps
	/// older than the retention. Credentials come from environment variables, never the command line.
	/// </summary>
	public static string? BackupCommand(PlannedService service, int retentionDays)
	{
		var stamp = "$(date -u +%Y%m%dT%H%M%SZ)";
		var dump = service.Kind switch
		{
			ServiceKind.PostgreSql => $"pg_dump -h {service.Name} -U {Planner.DbUser} -d {Planner.DbName} -Fc -f /backups/{Planner.DbName}-{stamp}.dump",
			ServiceKind.MySql => $"mysqldump -h {service.Name} -u {Planner.DbUser} --single-transaction --routines --no-tablespaces {Planner.DbName} | gzip > /backups/{Planner.DbName}-{stamp}.sql.gz",
			ServiceKind.MariaDb => $"mariadb-dump -h {service.Name} -u {Planner.DbUser} --single-transaction --routines {Planner.DbName} | gzip > /backups/{Planner.DbName}-{stamp}.sql.gz",
			ServiceKind.MongoDb => $"mongodump --uri \"mongodb://{Planner.DbUser}:$DB_PASSWORD@{service.Name}:{service.Port}/?authSource=admin\" --archive=/backups/{Planner.DbName}-{stamp}.archive.gz --gzip",
			_ => null,
		};
		return dump is null ? null : $"set -eu; {dump}; find /backups -type f -mtime +{retentionDays} -delete; ls -lh /backups | tail -n 3";
	}

	/// <summary>Environment the backup job needs (password variables the dump tools read).</summary>
	public static List<(string Key, string Value)> BackupEnvironment(PlannedService service) => service.Kind switch
	{
		ServiceKind.PostgreSql => [("PGPASSWORD", "{{secret:DB_PASSWORD}}")],
		ServiceKind.MySql or ServiceKind.MariaDb => [("MYSQL_PWD", "{{secret:DB_PASSWORD}}")],
		ServiceKind.MongoDb => [("DB_PASSWORD", "{{secret:DB_PASSWORD}}")],
		_ => [],
	};

	/// <summary>Shell for backing up SQLite files that live on the app's volumes, with the SQLite online-backup API.</summary>
	public static string SqliteBackupCommand(int retentionDays) =>
		"set -eu; apk add --no-cache sqlite >/dev/null; stamp=$(date -u +%Y%m%dT%H%M%SZ); "
		+ "find /data -maxdepth 3 -type f \\( -name '*.db' -o -name '*.sqlite' -o -name '*.sqlite3' \\) | while read -r f; do sqlite3 \"$f\" \".backup /backups/$(basename \"$f\")-$stamp\"; done; "
		+ $"find /backups -type f -mtime +{retentionDays} -delete; ls -lh /backups | tail -n 3";
}
