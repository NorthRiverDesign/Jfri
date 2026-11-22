using System.Text;
using Jfri.Core.Planning;

namespace Jfri.Core.Generation;

/// <summary>Shell scripts Jfri runs on a server over SSH. Every script is idempotent: re-running it converges, never duplicates.</summary>
public static class ServerScripts
{
	public const string Root = "/opt/jfri";
	public const int BootstrapVersion = 1;

	public static string AppDir(string appName) => $"{Root}/apps/{appName}";

	/// <summary>
	/// Hardens a fresh Ubuntu/Debian server and installs the runtime: firewall (SSH, HTTP, HTTPS
	/// only), unattended security upgrades, fail2ban, swap on small machines, Docker with BuildKit,
	/// and for Kubernetes, k3s with secrets encrypted at rest plus Traefik configured for Let's Encrypt.
	/// </summary>
	public static string Bootstrap(RuntimeKind runtime, string? acmeEmail, int sshPort = 22)
	{
		if (sshPort is < 1 or > 65535)
		{
			throw new ArgumentOutOfRangeException(nameof(sshPort));
		}
		var marker = $"/etc/jfri/bootstrap-{runtime.ToString().ToLowerInvariant()}-v{BootstrapVersion}";
		var sb = new StringBuilder();
		sb.AppendLine("#!/usr/bin/env bash");
		sb.AppendLine("set -euo pipefail");
		sb.AppendLine("export DEBIAN_FRONTEND=noninteractive");
		sb.AppendLine($"if [ -f {marker} ]; then echo 'Server already prepared.'; exit 0; fi");
		sb.AppendLine("if [ \"$(id -u)\" -ne 0 ]; then SUDO=sudo; else SUDO=; fi");
		sb.AppendLine("echo '==> Waiting for cloud-init and apt locks'");
		sb.AppendLine("command -v cloud-init >/dev/null && $SUDO cloud-init status --wait >/dev/null 2>&1 || true");
		sb.AppendLine("for i in $(seq 1 60); do $SUDO fuser /var/lib/dpkg/lock-frontend >/dev/null 2>&1 || break; sleep 5; done");
		sb.AppendLine("echo '==> Installing base packages'");
		sb.AppendLine("$SUDO apt-get update -qq");
		sb.AppendLine("$SUDO apt-get install -y -qq ca-certificates curl ufw fail2ban unattended-upgrades >/dev/null");
		sb.AppendLine("echo '==> Enabling automatic security updates'");
		sb.AppendLine("printf 'APT::Periodic::Update-Package-Lists \"1\";\\nAPT::Periodic::Unattended-Upgrade \"1\";\\n' | $SUDO tee /etc/apt/apt.conf.d/20auto-upgrades >/dev/null");
		sb.AppendLine("$SUDO systemctl enable --now fail2ban >/dev/null 2>&1 || true");
		sb.AppendLine($"echo '==> Configuring the firewall (SSH on {sshPort}, 80, 443 only)'");
		sb.AppendLine("$SUDO ufw default deny incoming >/dev/null");
		sb.AppendLine("$SUDO ufw default allow outgoing >/dev/null");
		sb.AppendLine($"$SUDO ufw allow {sshPort}/tcp >/dev/null");
		sb.AppendLine("$SUDO ufw allow 80/tcp >/dev/null");
		sb.AppendLine("$SUDO ufw allow 443/tcp >/dev/null");
		sb.AppendLine("$SUDO ufw allow 443/udp >/dev/null");
		if (runtime == RuntimeKind.Kubernetes)
		{
			sb.AppendLine("# Pod and service networks must reach each other (k3s defaults).");
			sb.AppendLine("$SUDO ufw allow from 10.42.0.0/16 >/dev/null");
			sb.AppendLine("$SUDO ufw allow from 10.43.0.0/16 >/dev/null");
		}
		sb.AppendLine("$SUDO ufw --force enable >/dev/null");
		sb.AppendLine("echo '==> Adding swap on small servers'");
		sb.AppendLine("if [ \"$(swapon --show | wc -l)\" -eq 0 ] && [ \"$(awk '/MemTotal/ {print $2}' /proc/meminfo)\" -lt 8000000 ]; then");
		sb.AppendLine("  $SUDO fallocate -l 2G /swapfile && $SUDO chmod 600 /swapfile && $SUDO mkswap /swapfile >/dev/null && $SUDO swapon /swapfile");
		sb.AppendLine("  echo '/swapfile none swap sw 0 0' | $SUDO tee -a /etc/fstab >/dev/null");
		sb.AppendLine("fi");
		sb.AppendLine("echo '==> Installing Docker with BuildKit'");
		sb.AppendLine("if ! command -v docker >/dev/null; then");
		sb.AppendLine("  $SUDO apt-get install -y -qq docker.io >/dev/null");
		sb.AppendLine("fi");
		sb.AppendLine("$SUDO apt-get install -y -qq docker-buildx >/dev/null 2>&1 || $SUDO apt-get install -y -qq docker-buildx-plugin >/dev/null 2>&1 || true");
		if (runtime == RuntimeKind.Compose)
		{
			sb.AppendLine("$SUDO apt-get install -y -qq docker-compose-v2 >/dev/null 2>&1 || $SUDO apt-get install -y -qq docker-compose-plugin >/dev/null 2>&1 || $SUDO apt-get install -y -qq docker-compose >/dev/null");
		}
		sb.AppendLine("$SUDO mkdir -p /etc/docker");
		sb.AppendLine("[ -f /etc/docker/daemon.json ] || echo '{\"log-driver\":\"json-file\",\"log-opts\":{\"max-size\":\"10m\",\"max-file\":\"5\"},\"live-restore\":true}' | $SUDO tee /etc/docker/daemon.json >/dev/null");
		sb.AppendLine("$SUDO systemctl enable docker >/dev/null 2>&1 && $SUDO systemctl restart docker");
		if (runtime == RuntimeKind.Kubernetes)
		{
			sb.AppendLine("echo '==> Installing k3s'");
			sb.AppendLine("if ! command -v k3s >/dev/null; then");
			sb.AppendLine("  curl -sfL https://get.k3s.io | $SUDO env INSTALL_K3S_CHANNEL=stable INSTALL_K3S_EXEC='server --write-kubeconfig-mode 600 --secrets-encryption' sh -");
			sb.AppendLine("fi");
			sb.AppendLine("$SUDO mkdir -p /var/lib/rancher/k3s/server/manifests");
			sb.AppendLine("$SUDO tee /var/lib/rancher/k3s/server/manifests/jfri-traefik.yaml >/dev/null <<'JFRI_EOF'");
			sb.Append(KubernetesGenerator.TraefikConfig(acmeEmail));
			sb.AppendLine("JFRI_EOF");
			sb.AppendLine("echo '==> Waiting for the cluster'");
			sb.AppendLine("for i in $(seq 1 60); do $SUDO k3s kubectl get nodes 2>/dev/null | grep -q ' Ready' && break; sleep 5; done");
			sb.AppendLine("$SUDO k3s kubectl get nodes");
		}
		sb.AppendLine($"$SUDO mkdir -p /etc/jfri {Root}/apps /var/lib/jfri/backups");
		sb.AppendLine($"$SUDO chmod 700 {Root} /var/lib/jfri");
		sb.AppendLine($"$SUDO touch {marker}");
		sb.AppendLine("echo 'Server ready.'");
		return sb.ToString();
	}

	/// <summary>
	/// Unpacks the uploaded source, builds the image with BuildKit, makes it available to the runtime
	/// and runs the generated deploy script. Old builds and images are pruned (last 3 kept).
	/// </summary>
	public static string BuildAndDeploy(DeploymentPlan plan, string revision, string dockerfileRelative)
	{
		var app = AppDir(plan.AppName);
		var image = $"{plan.ImageRepository}:{revision}";
		var context = string.IsNullOrEmpty(plan.BuildContext) ? "." : plan.BuildContext;
		var runtimeDir = plan.Runtime == RuntimeKind.Kubernetes ? "k8s" : "compose";
		var sb = new StringBuilder();
		sb.AppendLine("#!/usr/bin/env bash");
		sb.AppendLine("set -euo pipefail");
		sb.AppendLine("if [ \"$(id -u)\" -ne 0 ]; then SUDO=sudo; else SUDO=; fi");
		sb.AppendLine($"app={app}");
		sb.AppendLine($"rev={revision}");
		sb.AppendLine("src=\"$app/builds/$rev\"");
		sb.AppendLine("echo '==> Unpacking source'");
		sb.AppendLine("rm -rf \"$src\" && mkdir -p \"$src\"");
		sb.AppendLine("tar -xzf \"$app/source.tar.gz\" -C \"$src\" --no-same-owner");
		sb.AppendLine("if [ -d \"$app/overlay\" ]; then cp -a \"$app/overlay/.\" \"$src/\"; fi");
		sb.AppendLine($"echo '==> Building {image}'");
		sb.AppendLine("cd \"$src\"");
		sb.AppendLine($"DOCKER_BUILDKIT=1 $SUDO docker build --pull -f {dockerfileRelative} -t {image} {context}");
		if (plan.Runtime == RuntimeKind.Kubernetes)
		{
			sb.AppendLine("echo '==> Loading the image into k3s'");
			sb.AppendLine($"$SUDO docker save {image} | $SUDO k3s ctr -n k8s.io images import - >/dev/null");
		}
		sb.AppendLine("echo '==> Deploying'");
		sb.AppendLine($"$SUDO env JFRI_REVISION=\"$rev\" bash \"$app/{runtimeDir}/deploy.sh\"");
		sb.AppendLine("echo '==> Cleaning up old builds'");
		sb.AppendLine("ls -1dt \"$app\"/builds/* 2>/dev/null | tail -n +4 | xargs -r rm -rf");
		sb.AppendLine($"$SUDO docker images {plan.ImageRepository} --format '{{{{.Tag}}}}' | tail -n +4 | xargs -r -I{{}} $SUDO docker rmi {plan.ImageRepository}:{{}} >/dev/null 2>&1 || true");
		sb.AppendLine("rm -f \"$app/source.tar.gz\"");
		return sb.ToString();
	}

	/// <summary>
	/// Makes sure an earlier build is still on the server before a rollback changes anything. Docker
	/// keeps the last three builds; k3s keeps its own copy until its image garbage collection runs.
	/// </summary>
	public static string CheckImage(DeploymentPlan plan, string revision)
	{
		var image = $"{plan.ImageRepository}:{revision}";
		var sb = new StringBuilder();
		sb.AppendLine("#!/usr/bin/env bash");
		sb.AppendLine("set -euo pipefail");
		sb.AppendLine("if [ \"$(id -u)\" -ne 0 ]; then SUDO=sudo; else SUDO=; fi");
		sb.AppendLine($"image={image}");
		sb.AppendLine("echo \"==> Looking for $image\"");
		if (plan.Runtime == RuntimeKind.Kubernetes)
		{
			// No grep -q: it exits at the first match, and pipefail would then fail on ctr's broken pipe.
			sb.AppendLine("if $SUDO k3s ctr -n k8s.io images ls -q | grep -Fx \"docker.io/$image\" >/dev/null; then echo 'Found it in k3s.'; exit 0; fi");
			sb.AppendLine("if $SUDO docker image inspect \"$image\" >/dev/null 2>&1; then");
			sb.AppendLine("  echo 'Loading it into k3s'");
			sb.AppendLine("  $SUDO docker save \"$image\" | $SUDO k3s ctr -n k8s.io images import - >/dev/null");
			sb.AppendLine("  exit 0");
			sb.AppendLine("fi");
		}
		else
		{
			sb.AppendLine("if $SUDO docker image inspect \"$image\" >/dev/null 2>&1; then echo 'Found it.'; exit 0; fi");
		}
		sb.AppendLine($"echo 'The build of {revision} is no longer on the server (Jfri keeps the last three). Deploy that commit again instead.' >&2");
		sb.AppendLine("exit 1");
		return sb.ToString();
	}

	/// <summary>Runs the uploaded deploy script for an image that's already on the server.</summary>
	public static string Release(DeploymentPlan plan, string revision)
	{
		var runtimeDir = plan.Runtime == RuntimeKind.Kubernetes ? "k8s" : "compose";
		return $"""
			#!/usr/bin/env bash
			set -euo pipefail
			if [ "$(id -u)" -ne 0 ]; then SUDO=sudo; else SUDO=; fi
			echo '==> Releasing {revision}'
			$SUDO env JFRI_REVISION={revision} bash {AppDir(plan.AppName)}/{runtimeDir}/deploy.sh

			""";
	}

	/// <summary>
	/// Removes an app from a server: its Kubernetes namespace or Compose project with the database and
	/// volumes, its images and its files. Backups are kept in /var/lib/jfri/backups/&lt;app&gt;; on
	/// Kubernetes they live in a volume inside the namespace, so they're copied out first, and nothing is
	/// deleted if that copy fails. Works whichever runtime the app used, and on half-deployed apps.
	/// </summary>
	public static string Teardown(string appName)
	{
		if (!Planner.IsValidSlug(appName))
		{
			throw new ArgumentException($"Invalid app name {appName}.", nameof(appName));
		}
		return $$"""
			#!/usr/bin/env bash
			set -euo pipefail
			if [ "$(id -u)" -ne 0 ]; then SUDO=sudo; else SUDO=; fi
			name={{appName}}
			app={{AppDir(appName)}}
			keep=/var/lib/jfri/backups/$name
			if command -v k3s >/dev/null 2>&1 && $SUDO k3s kubectl get namespace "$name" >/dev/null 2>&1; then
			  managed=$($SUDO k3s kubectl get namespace "$name" -o jsonpath='{.metadata.labels.app\.kubernetes\.io/managed-by}')
			  if [ "$managed" != "jfri" ]; then
			    echo "Namespace $name wasn't created by Jfri; leaving it alone." >&2
			    exit 1
			  fi
			  # Any error other than "no such volume claim" stops the script: the backups must not go with the namespace.
			  pv=$($SUDO k3s kubectl -n "$name" get pvc backups --ignore-not-found -o jsonpath='{.spec.volumeName}')
			  if [ -n "$pv" ]; then
			    path=$($SUDO k3s kubectl get pv "$pv" -o jsonpath='{.spec.local.path}{.spec.hostPath.path}')
			    if [ -z "$path" ] || ! $SUDO test -d "$path"; then
			      echo "Couldn't find the backups volume ($pv) on disk, so nothing was deleted." >&2
			      exit 1
			    fi
			    echo "==> Keeping the backups in $keep"
			    $SUDO mkdir -p "$keep"
			    $SUDO cp -a "$path/." "$keep/"
			  fi
			  echo "==> Deleting namespace $name (app, database, volumes)"
			  $SUDO k3s kubectl delete namespace "$name" --timeout=300s
			  $SUDO k3s ctr -n k8s.io images ls -q | grep -F "docker.io/jfri/$name:" | xargs -r $SUDO k3s ctr -n k8s.io images rm >/dev/null 2>&1 || true
			fi
			if command -v docker >/dev/null 2>&1; then
			  if [ -f "$app/compose/compose.yaml" ]; then
			    echo "==> Stopping the containers and deleting their volumes"
			    $SUDO docker compose -f "$app/compose/compose.yaml" down --volumes --remove-orphans --timeout 30
			  elif [ -n "$($SUDO docker ps -aq --filter "label=com.docker.compose.project=$name")" ]; then
			    echo "==> Stopping the containers and deleting their volumes"
			    $SUDO docker compose -p "$name" down --volumes --remove-orphans --timeout 30
			  fi
			  $SUDO docker volume ls -q --filter "label=com.docker.compose.project=$name" | xargs -r $SUDO docker volume rm >/dev/null
			  $SUDO docker images "jfri/$name" -q | sort -u | xargs -r $SUDO docker rmi -f >/dev/null 2>&1 || true
			fi
			echo "==> Deleting $app"
			$SUDO rm -rf "$app"
			if $SUDO test -d "$keep"; then echo "Backups are kept in $keep."; fi
			echo "Removed $name from this server."

			""";
	}

	/// <summary>Checks the app answers through the public proxy (from the server itself, with the right Host header).</summary>
	public static string Verify(DeploymentPlan plan, string host)
	{
		var path = plan.App.HealthCheckPath ?? "/";
		return $"""
			#!/usr/bin/env bash
			set -uo pipefail
			for i in $(seq 1 30); do
			  code=$(curl -sk -o /dev/null -w '%{"{http_code}"}' --resolve '{host}:443:127.0.0.1' 'https://{host}{path}')
			  if [ "$code" -ge 200 ] && [ "$code" -lt 500 ]; then echo "HTTP $code from https://{host}{path}"; exit 0; fi
			  sleep 5
			done
			echo "The app didn't answer through the proxy (last status: $code)." >&2
			exit 1

			""";
	}
}
