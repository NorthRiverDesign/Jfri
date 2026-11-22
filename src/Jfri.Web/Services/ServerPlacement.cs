using Jfri.Core.Execution;
using Jfri.Core.Planning;
using Jfri.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jfri.Web.Services;

/// <summary>Whether an app can run on a server next to the apps already there.</summary>
public static class ServerPlacement
{
	/// <summary>Returns why <paramref name="app"/> can't go on <paramref name="server"/>, or null if it can.</summary>
	public static async Task<string?> ProblemAsync(JfriDb db, App app, DeploymentPlan plan, Server server, CancellationToken ct)
	{
		if (server.Runtime is not null && server.Runtime != plan.Runtime.ToString())
		{
			return $"{server.Name} already runs {server.Runtime}; this app is planned for {plan.Runtime}. Pick another server or change the app's runtime.";
		}
		var others = await db.Apps.AsNoTracking().Where(a => a.Id != app.Id && a.ServerId == server.Id).OrderBy(a => a.Name).ToListAsync(ct);
		if (others.Count == 0)
		{
			return null;
		}
		if (plan.Runtime == RuntimeKind.Compose || server.Runtime == nameof(RuntimeKind.Compose))
		{
			return $"{server.Name} already runs {others[0].Name}. A Compose server runs one app, because its Caddy proxy owns ports 80 and 443. Pick another server, take {others[0].Name} down, or use Kubernetes.";
		}
		if (server.Host.Length > 0)
		{
			var host = DeploymentEngine.PublicHost(plan.Domain, server.Host, app.Name);
			foreach (var other in others)
			{
				if (string.Equals(DeploymentEngine.PublicHost(AppService.Options(other).Domain, server.Host, other.Name), host, StringComparison.OrdinalIgnoreCase))
				{
					return $"{other.Name} on {server.Name} is already served at {host}. Give this app its own domain.";
				}
			}
		}
		return null;
	}
}
