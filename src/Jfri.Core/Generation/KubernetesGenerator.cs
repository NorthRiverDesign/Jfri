using System.Globalization;
using System.Text;
using Jfri.Core.Analysis;
using Jfri.Core.Planning;

namespace Jfri.Core.Generation;

/// <summary>
/// Renders the plan as Kubernetes manifests for a single-node k3s server: a namespace per app with
/// Pod Security admission, Secrets for anything sensitive, StatefulSet databases on persistent
/// volumes, a release Job for migrations, hardened Deployments with probes and resource limits,
/// HTTPS ingress through Traefik + Let's Encrypt, nightly backup CronJobs, and default-deny network
/// policies so only the ingress controller reaches the app and only the app reaches its data.
/// </summary>
public static class KubernetesGenerator
{
	const string Dir = "k8s";

	public static List<Artifact> Generate(DeploymentPlan plan, RenderContext context)
	{
		var files = new List<Artifact>
		{
			new($"{Dir}/00-namespace.yaml", Namespace(plan)),
			new($"{Dir}/10-secrets.yaml", Secrets(plan, context), "0600", Sensitive: true),
			new($"{Dir}/11-config.yaml", Config(plan, context)),
		};
		if (plan.Services.Count > 0)
		{
			files.Add(new($"{Dir}/20-services.yaml", Services(plan)));
		}
		if (plan.Volumes.Count > 0)
		{
			// Volumes exist before the release Job, which mounts them.
			files.Add(new($"{Dir}/25-volumes.yaml", Volumes(plan)));
		}
		if (plan.ReleaseCommand is not null)
		{
			files.Add(new($"{Dir}/30-release.yaml", Release(plan, context)));
		}
		files.Add(new($"{Dir}/40-app.yaml", App(plan, context)));
		files.Add(new($"{Dir}/50-ingress.yaml", Ingress(plan, context)));
		if (plan.Backups.Enabled && Backups(plan) is { Length: > 0 } backups)
		{
			files.Add(new($"{Dir}/60-backups.yaml", backups));
		}
		files.Add(new($"{Dir}/70-network-policy.yaml", NetworkPolicies(plan)));
		files.Add(new($"{Dir}/deploy.sh", DeployScript(plan, context), "0755"));
		return files;
	}

	/// <summary>Cluster-wide Traefik settings (HTTP→HTTPS redirect, Let's Encrypt). Written to k3s' auto-deploy manifests directory.</summary>
	public static string TraefikConfig(string? acmeEmail)
	{
		var sb = new StringBuilder();
		sb.AppendLine("# Managed by Jfri: HTTPS for every app on this server.");
		sb.AppendLine("apiVersion: helm.cattle.io/v1");
		sb.AppendLine("kind: HelmChartConfig");
		sb.AppendLine("metadata:");
		sb.AppendLine("  name: traefik");
		sb.AppendLine("  namespace: kube-system");
		sb.AppendLine("spec:");
		sb.AppendLine("  valuesContent: |-");
		sb.AppendLine("    persistence:");
		sb.AppendLine("      enabled: true");
		sb.AppendLine("      size: 128Mi");
		sb.AppendLine("    additionalArguments:");
		sb.AppendLine("      - \"--entrypoints.web.http.redirections.entrypoint.to=:443\"");
		sb.AppendLine("      - \"--entrypoints.web.http.redirections.entrypoint.scheme=https\"");
		sb.AppendLine("      - \"--certificatesresolvers.letsencrypt.acme.storage=/data/acme.json\"");
		sb.AppendLine("      - \"--certificatesresolvers.letsencrypt.acme.tlschallenge=true\"");
		if (!string.IsNullOrWhiteSpace(acmeEmail))
		{
			sb.AppendLine($"      - {Yaml.Q($"--certificatesresolvers.letsencrypt.acme.email={acmeEmail}")}");
		}
		return sb.ToString();
	}

	static string Labels(DeploymentPlan plan, string component, string indent, string? tier = null)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"{indent}app.kubernetes.io/name: {plan.AppName}");
		sb.AppendLine($"{indent}app.kubernetes.io/component: {component}");
		sb.Append($"{indent}app.kubernetes.io/managed-by: jfri");
		if (tier is not null)
		{
			sb.AppendLine();
			sb.Append($"{indent}jfri.dev/tier: {tier}");
		}
		return sb.ToString();
	}

	static string Namespace(DeploymentPlan plan) => $"""
		apiVersion: v1
		kind: Namespace
		metadata:
		  name: {plan.Namespace}
		  labels:
		    app.kubernetes.io/managed-by: jfri
		    pod-security.kubernetes.io/enforce: baseline
		    pod-security.kubernetes.io/warn: restricted

		""";

	static string Secrets(DeploymentPlan plan, RenderContext context)
	{
		var sb = new StringBuilder();
		sb.AppendLine("# Sensitive values. Jfri uploads this file with mode 0600 and never logs it.");
		sb.AppendLine("apiVersion: v1");
		sb.AppendLine("kind: Secret");
		sb.AppendLine("metadata:");
		sb.AppendLine($"  name: {plan.AppName}-env");
		sb.AppendLine($"  namespace: {plan.Namespace}");
		sb.AppendLine("type: Opaque");
		sb.AppendLine("stringData:");
		var sensitive = plan.Environment.Where(e => e.IsSensitive).ToList();
		if (sensitive.Count == 0)
		{
			sb.Length -= Environment.NewLine.Length;
			sb.AppendLine(" {}");
		}
		foreach (var env in sensitive)
		{
			sb.AppendLine($"  {env.Key}: {Yaml.Q(Template.Resolve(env.Value, context))}");
		}
		foreach (var service in plan.Services)
		{
			sb.AppendLine("---");
			sb.AppendLine("apiVersion: v1");
			sb.AppendLine("kind: Secret");
			sb.AppendLine("metadata:");
			sb.AppendLine($"  name: {plan.AppName}-{service.Name}");
			sb.AppendLine($"  namespace: {plan.Namespace}");
			sb.AppendLine("type: Opaque");
			sb.AppendLine("stringData:");
			foreach (var (key, value) in ServiceRecipes.Environment(service).Concat(ServiceRecipes.BackupEnvironment(service)).DistinctBy(e => e.Key))
			{
				sb.AppendLine($"  {key}: {Yaml.Q(Template.Resolve(value, context))}");
			}
		}
		return sb.ToString();
	}

	static string Config(DeploymentPlan plan, RenderContext context)
	{
		var sb = new StringBuilder();
		sb.AppendLine("apiVersion: v1");
		sb.AppendLine("kind: ConfigMap");
		sb.AppendLine("metadata:");
		sb.AppendLine($"  name: {plan.AppName}-env");
		sb.AppendLine($"  namespace: {plan.Namespace}");
		sb.Append("data:");
		var plain = plan.Environment.Where(e => !e.IsSensitive).ToList();
		sb.AppendLine(plain.Count == 0 ? " {}" : "");
		foreach (var env in plain)
		{
			sb.AppendLine($"  {env.Key}: {Yaml.Q(Template.Resolve(env.Value, context))}");
		}
		return sb.ToString();
	}

	static string Services(DeploymentPlan plan)
	{
		var sb = new StringBuilder();
		var first = true;
		foreach (var service in plan.Services)
		{
			if (!first)
			{
				sb.AppendLine("---");
			}
			first = false;
			var name = $"{service.Name}";
			var uid = ServiceRecipes.Uid(service);
			var args = ServiceRecipes.Args(service, kubernetes: true);
			sb.AppendLine($"""
				apiVersion: v1
				kind: Service
				metadata:
				  name: {name}
				  namespace: {plan.Namespace}
				  labels:
				{Labels(plan, name, "    ", "data")}
				spec:
				  clusterIP: None
				  selector:
				    app.kubernetes.io/name: {plan.AppName}
				    app.kubernetes.io/component: {name}
				  ports:
				    - name: {name}
				      port: {service.Port}
				      targetPort: {service.Port}
				---
				apiVersion: apps/v1
				kind: StatefulSet
				metadata:
				  name: {name}
				  namespace: {plan.Namespace}
				  labels:
				{Labels(plan, name, "    ", "data")}
				spec:
				  serviceName: {name}
				  replicas: 1
				  selector:
				    matchLabels:
				      app.kubernetes.io/name: {plan.AppName}
				      app.kubernetes.io/component: {name}
				  template:
				    metadata:
				      labels:
				{Labels(plan, name, "        ", "data")}
				    spec:
				      automountServiceAccountToken: false
				      terminationGracePeriodSeconds: 60
				      securityContext:
				        runAsNonRoot: true
				        runAsUser: {uid}
				        runAsGroup: {uid}
				        fsGroup: {uid}
				        seccompProfile:
				          type: RuntimeDefault
				      containers:
				        - name: {name}
				          image: {service.Image}
				""".TrimEnd());
			if (args is not null)
			{
				sb.AppendLine($"          args: {Yaml.List(args)}");
			}
			sb.AppendLine($"""
				          envFrom:
				            - secretRef:
				                name: {plan.AppName}-{service.Name}
				          ports:
				            - containerPort: {service.Port}
				              name: {name}
				          readinessProbe:
				            exec:
				              command: {Yaml.Shell(ServiceRecipes.Ready(service))}
				            initialDelaySeconds: 5
				            periodSeconds: 10
				            timeoutSeconds: 5
				          livenessProbe:
				            exec:
				              command: {Yaml.Shell(ServiceRecipes.Ready(service))}
				            initialDelaySeconds: 60
				            periodSeconds: 20
				            timeoutSeconds: 5
				            failureThreshold: 6
				          resources:
				{ResourceBlock(service.Resources, "            ")}
				          securityContext:
				            allowPrivilegeEscalation: false
				            capabilities:
				              drop: ["ALL"]
				          volumeMounts:
				            - name: data
				              mountPath: {service.DataPath}
				  volumeClaimTemplates:
				    - metadata:
				        name: data
				      spec:
				        accessModes: ["ReadWriteOnce"]
				        resources:
				          requests:
				            storage: {service.StorageGb}Gi
				""");
		}
		return sb.ToString();
	}

	static string Release(DeploymentPlan plan, RenderContext context) => $"""
		# Runs once per deploy, before the new version takes traffic (database migrations).
		apiVersion: batch/v1
		kind: Job
		metadata:
		  name: release-{JobSuffix(context.Revision)}
		  namespace: {plan.Namespace}
		  labels:
		{Labels(plan, "release", "    ", "app")}
		spec:
		  backoffLimit: 1
		  activeDeadlineSeconds: 900
		  ttlSecondsAfterFinished: 86400
		  template:
		    metadata:
		      labels:
		{Labels(plan, "release", "        ", "app")}
		    spec:
		      restartPolicy: Never
		{PodSpecBody(plan, context, plan.ReleaseCommand, "release", "      ", new Resources(100, 256, 1000, 1024), probes: false, execCommand: false)}

		""";

	static string Volumes(DeploymentPlan plan)
	{
		var docs = new List<string>();
		foreach (var volume in plan.Volumes)
		{
			docs.Add($"""
				apiVersion: v1
				kind: PersistentVolumeClaim
				metadata:
				  name: {volume.Name}
				  namespace: {plan.Namespace}
				  labels:
				{Labels(plan, "volume", "    ")}
				spec:
				  accessModes: ["ReadWriteOnce"]
				  resources:
				    requests:
				      storage: {volume.SizeGb}Gi
				""");
		}
		return string.Join("\n---\n", docs) + "\n";
	}

	static string App(DeploymentPlan plan, RenderContext context)
	{
		var docs = new List<string>();
		foreach (var process in plan.Processes)
		{
			var component = process.Name;
			var singleton = process.Kind == ProcessKind.Scheduler || (plan.Volumes.Count > 0 && process.Replicas == 1);
			docs.Add($"""
				apiVersion: apps/v1
				kind: Deployment
				metadata:
				  name: {component}
				  namespace: {plan.Namespace}
				  labels:
				{Labels(plan, component, "    ", "app")}
				spec:
				  replicas: {process.Replicas}
				  revisionHistoryLimit: 3
				  strategy:
				{(singleton ? "    type: Recreate" : "    type: RollingUpdate\n    rollingUpdate:\n      maxUnavailable: 0\n      maxSurge: 1")}
				  selector:
				    matchLabels:
				      app.kubernetes.io/name: {plan.AppName}
				      app.kubernetes.io/component: {component}
				  template:
				    metadata:
				      labels:
				{Labels(plan, component, "        ", "app")}
				      annotations:
				        jfri.dev/revision: {Yaml.Q(context.Revision)}
				    spec:
				      terminationGracePeriodSeconds: {(process.Kind == ProcessKind.Web ? 30 : 90)}
				{PodSpecBody(plan, context, process.Command, component, "      ", process.Resources, probes: process.Kind == ProcessKind.Web)}
				""");
		}
		var web = plan.Web;
		if (web is not null)
		{
			docs.Add($"""
				apiVersion: v1
				kind: Service
				metadata:
				  name: web
				  namespace: {plan.Namespace}
				  labels:
				{Labels(plan, "web", "    ", "app")}
				spec:
				  selector:
				    app.kubernetes.io/name: {plan.AppName}
				    app.kubernetes.io/component: web
				  ports:
				    - name: http
				      port: 80
				      targetPort: {plan.App.Port}
				""");
			if (web.Replicas > 1)
			{
				docs.Add($"""
					apiVersion: policy/v1
					kind: PodDisruptionBudget
					metadata:
					  name: web
					  namespace: {plan.Namespace}
					spec:
					  minAvailable: 1
					  selector:
					    matchLabels:
					      app.kubernetes.io/name: {plan.AppName}
					      app.kubernetes.io/component: web
					""");
			}
		}
		return string.Join("\n---\n", docs) + "\n";
	}

	/// <summary>The container part of a pod spec shared by Deployments, the release Job and backups.</summary>
	static string PodSpecBody(DeploymentPlan plan, RenderContext context, string? command, string name, string indent, Resources resources, bool probes, bool execCommand = true)
	{
		var uid = DockerfileGenerator.RuntimeUid(plan.App);
		var sb = new StringBuilder();
		sb.AppendLine("automountServiceAccountToken: false");
		sb.AppendLine("securityContext:");
		if (uid > 0)
		{
			sb.AppendLine("  runAsNonRoot: true");
			sb.AppendLine($"  runAsUser: {uid}");
			sb.AppendLine($"  runAsGroup: {uid}");
			sb.AppendLine($"  fsGroup: {uid}");
		}
		sb.AppendLine("  seccompProfile:");
		sb.AppendLine("    type: RuntimeDefault");
		sb.AppendLine("containers:");
		sb.AppendLine($"  - name: {name}");
		sb.AppendLine($"    image: {plan.ImageRepository}:{context.Revision}");
		sb.AppendLine("    imagePullPolicy: IfNotPresent");
		if (command is not null)
		{
			sb.AppendLine($"    command: {Yaml.Shell(execCommand ? "exec " + command : command)}");
		}
		sb.AppendLine("    envFrom:");
		sb.AppendLine("      - configMapRef:");
		sb.AppendLine($"          name: {plan.AppName}-env");
		sb.AppendLine("      - secretRef:");
		sb.AppendLine($"          name: {plan.AppName}-env");
		if (probes)
		{
			sb.AppendLine("    ports:");
			sb.AppendLine($"      - containerPort: {plan.App.Port}");
			sb.AppendLine("        name: http");
			var probe = plan.App.HealthCheckPath is { } path
				? $"httpGet:\n  path: {Yaml.Q(path)}\n  port: http"
				: "tcpSocket:\n  port: http";
			sb.AppendLine("    startupProbe:");
			sb.AppendLine(Indent(probe, "      "));
			sb.AppendLine("      periodSeconds: 5");
			sb.AppendLine("      failureThreshold: 60");
			sb.AppendLine("    readinessProbe:");
			sb.AppendLine(Indent(probe, "      "));
			sb.AppendLine("      periodSeconds: 10");
			sb.AppendLine("      timeoutSeconds: 5");
			sb.AppendLine("    livenessProbe:");
			sb.AppendLine(Indent(probe, "      "));
			sb.AppendLine("      periodSeconds: 20");
			sb.AppendLine("      timeoutSeconds: 5");
			sb.AppendLine("      failureThreshold: 3");
		}
		sb.AppendLine("    resources:");
		sb.AppendLine(ResourceBlock(resources, "      "));
		sb.AppendLine("    securityContext:");
		sb.AppendLine("      allowPrivilegeEscalation: false");
		sb.AppendLine("      capabilities:");
		sb.AppendLine("        drop: [\"ALL\"]");
		if (plan.Volumes.Count > 0)
		{
			sb.AppendLine("    volumeMounts:");
			foreach (var volume in plan.Volumes)
			{
				sb.AppendLine($"      - name: {volume.Name}");
				sb.AppendLine($"        mountPath: {volume.MountPath}");
			}
			sb.AppendLine("volumes:");
			foreach (var volume in plan.Volumes)
			{
				sb.AppendLine($"  - name: {volume.Name}");
				sb.AppendLine("    persistentVolumeClaim:");
				sb.AppendLine($"      claimName: {volume.Name}");
			}
		}
		return Indent(sb.ToString().TrimEnd(), indent);
	}

	static string Ingress(DeploymentPlan plan, RenderContext context) => $"""
		apiVersion: traefik.io/v1alpha1
		kind: Middleware
		metadata:
		  name: security-headers
		  namespace: {plan.Namespace}
		spec:
		  headers:
		    stsSeconds: 31536000
		    stsIncludeSubdomains: false
		    contentTypeNosniff: true
		    referrerPolicy: strict-origin-when-cross-origin
		---
		apiVersion: networking.k8s.io/v1
		kind: Ingress
		metadata:
		  name: web
		  namespace: {plan.Namespace}
		  annotations:
		    traefik.ingress.kubernetes.io/router.entrypoints: websecure
		    traefik.ingress.kubernetes.io/router.tls: "true"
		    traefik.ingress.kubernetes.io/router.tls.certresolver: letsencrypt
		    traefik.ingress.kubernetes.io/router.middlewares: {plan.Namespace}-security-headers@kubernetescrd
		spec:
		  rules:
		    - host: {Yaml.Q(context.Host)}
		      http:
		        paths:
		          - path: /
		            pathType: Prefix
		            backend:
		              service:
		                name: web
		                port:
		                  number: 80

		""";

	static string Backups(DeploymentPlan plan)
	{
		var docs = new List<string>
		{
			$"""
			apiVersion: v1
			kind: PersistentVolumeClaim
			metadata:
			  name: backups
			  namespace: {plan.Namespace}
			  labels:
			{Labels(plan, "backups", "    ")}
			spec:
			  accessModes: ["ReadWriteOnce"]
			  resources:
			    requests:
			      storage: {Math.Max(2, plan.Services.Where(s => s.IsDatabase).Sum(s => s.StorageGb))}Gi
			""",
		};
		foreach (var service in plan.Services.Where(s => s.IsDatabase))
		{
			var command = ServiceRecipes.BackupCommand(service, plan.Backups.RetentionDays);
			if (command is null)
			{
				continue;
			}
			docs.Add(BackupCronJob(plan, $"backup-{service.Name}", service.Image, command, ServiceRecipes.Uid(service), $"{plan.AppName}-{service.Name}", null));
		}
		var sqliteVolume = plan.Volumes.FirstOrDefault(v => v.Reason.Contains("SQLite", StringComparison.Ordinal));
		if (sqliteVolume is not null)
		{
			docs.Add(BackupCronJob(plan, "backup-sqlite", "alpine:3", ServiceRecipes.SqliteBackupCommand(plan.Backups.RetentionDays), 0, null, sqliteVolume.Name));
		}
		return docs.Count > 1 ? string.Join("\n---\n", docs) + "\n" : "";
	}

	static string BackupCronJob(DeploymentPlan plan, string name, string image, string command, int uid, string? secretName, string? dataVolume)
	{
		var sb = new StringBuilder();
		sb.AppendLine("apiVersion: batch/v1");
		sb.AppendLine("kind: CronJob");
		sb.AppendLine("metadata:");
		sb.AppendLine($"  name: {name}");
		sb.AppendLine($"  namespace: {plan.Namespace}");
		sb.AppendLine("spec:");
		sb.AppendLine($"  schedule: {Yaml.Q(plan.Backups.Schedule)}");
		sb.AppendLine("  timeZone: Etc/UTC");
		sb.AppendLine("  concurrencyPolicy: Forbid");
		sb.AppendLine("  successfulJobsHistoryLimit: 3");
		sb.AppendLine("  failedJobsHistoryLimit: 3");
		sb.AppendLine("  jobTemplate:");
		sb.AppendLine("    spec:");
		sb.AppendLine("      backoffLimit: 2");
		sb.AppendLine("      template:");
		sb.AppendLine("        metadata:");
		sb.AppendLine("          labels:");
		sb.AppendLine(Labels(plan, "backup", "            ", "app"));
		sb.AppendLine("        spec:");
		sb.AppendLine("          restartPolicy: OnFailure");
		sb.AppendLine("          automountServiceAccountToken: false");
		if (uid > 0)
		{
			sb.AppendLine("          securityContext:");
			sb.AppendLine("            runAsNonRoot: true");
			sb.AppendLine($"            runAsUser: {uid}");
			sb.AppendLine($"            fsGroup: {uid}");
		}
		sb.AppendLine("          containers:");
		sb.AppendLine("            - name: backup");
		sb.AppendLine($"              image: {image}");
		sb.AppendLine($"              command: {Yaml.Shell(command)}");
		if (secretName is not null)
		{
			sb.AppendLine("              envFrom:");
			sb.AppendLine("                - secretRef:");
			sb.AppendLine($"                    name: {secretName}");
		}
		sb.AppendLine("              volumeMounts:");
		sb.AppendLine("                - name: backups");
		sb.AppendLine("                  mountPath: /backups");
		if (dataVolume is not null)
		{
			sb.AppendLine("                - name: data");
			sb.AppendLine("                  mountPath: /data");
			sb.AppendLine("                  readOnly: true");
		}
		sb.AppendLine("          volumes:");
		sb.AppendLine("            - name: backups");
		sb.AppendLine("              persistentVolumeClaim:");
		sb.AppendLine("                claimName: backups");
		if (dataVolume is not null)
		{
			sb.AppendLine("            - name: data");
			sb.AppendLine("              persistentVolumeClaim:");
			sb.AppendLine($"                claimName: {dataVolume}");
		}
		return sb.ToString().TrimEnd();
	}

	static string NetworkPolicies(DeploymentPlan plan) => $"""
		# Nothing reaches this namespace unless a policy below allows it.
		apiVersion: networking.k8s.io/v1
		kind: NetworkPolicy
		metadata:
		  name: default-deny-ingress
		  namespace: {plan.Namespace}
		spec:
		  podSelector: {"{}"}
		  policyTypes: ["Ingress"]
		---
		# The web pods accept traffic from the ingress controller (Traefik in kube-system).
		apiVersion: networking.k8s.io/v1
		kind: NetworkPolicy
		metadata:
		  name: web-from-ingress
		  namespace: {plan.Namespace}
		spec:
		  podSelector:
		    matchLabels:
		      app.kubernetes.io/component: web
		  policyTypes: ["Ingress"]
		  ingress:
		    - from:
		        - namespaceSelector:
		            matchLabels:
		              kubernetes.io/metadata.name: kube-system
		      ports:
		        - port: {plan.App.Port}
		---
		# Databases and caches accept connections only from pods of this app.
		apiVersion: networking.k8s.io/v1
		kind: NetworkPolicy
		metadata:
		  name: data-from-app
		  namespace: {plan.Namespace}
		spec:
		  podSelector:
		    matchLabels:
		      jfri.dev/tier: data
		  policyTypes: ["Ingress"]
		  ingress:
		    - from:
		        - podSelector:
		            matchLabels:
		              jfri.dev/tier: app

		""";

	static string DeployScript(DeploymentPlan plan, RenderContext context)
	{
		var sb = new StringBuilder();
		sb.AppendLine("#!/usr/bin/env bash");
		sb.AppendLine($"# Applies {plan.AppName} revision {context.Revision} to the local k3s cluster. Safe to re-run.");
		sb.AppendLine("set -euo pipefail");
		sb.AppendLine("cd \"$(dirname \"$0\")\"");
		sb.AppendLine("export KUBECONFIG=${KUBECONFIG:-/etc/rancher/k3s/k3s.yaml}");
		sb.AppendLine($"ns={plan.Namespace}");
		sb.AppendLine("kubectl apply -f 00-namespace.yaml -f 10-secrets.yaml -f 11-config.yaml");
		if (plan.Volumes.Count > 0)
		{
			sb.AppendLine("kubectl apply -f 25-volumes.yaml");
		}
		if (plan.Services.Count > 0)
		{
			sb.AppendLine("kubectl apply -f 20-services.yaml");
			foreach (var service in plan.Services)
			{
				sb.AppendLine($"echo \"Waiting for {service.Name}...\"");
				sb.AppendLine($"kubectl -n \"$ns\" rollout status statefulset/{service.Name} --timeout=600s");
			}
		}
		if (plan.ReleaseCommand is not null)
		{
			var job = $"release-{JobSuffix(context.Revision)}";
			sb.AppendLine($"kubectl -n \"$ns\" delete job {job} --ignore-not-found");
			sb.AppendLine("kubectl apply -f 30-release.yaml");
			sb.AppendLine("echo \"Running release command...\"");
			sb.AppendLine("for i in $(seq 1 180); do");
			sb.AppendLine($"  succeeded=$(kubectl -n \"$ns\" get job {job} -o jsonpath='{{.status.succeeded}}')");
			sb.AppendLine($"  failed=$(kubectl -n \"$ns\" get job {job} -o jsonpath='{{.status.failed}}')");
			sb.AppendLine("  if [ \"${succeeded:-0}\" -ge 1 ]; then break; fi");
			sb.AppendLine($"  if [ \"${{failed:-0}}\" -ge 2 ]; then kubectl -n \"$ns\" logs job/{job} --tail=100 || true; echo 'Release command failed.' >&2; exit 1; fi");
			sb.AppendLine("  sleep 5");
			sb.AppendLine("done");
			sb.AppendLine($"kubectl -n \"$ns\" logs job/{job} --tail=50 || true");
			sb.AppendLine("if [ \"${succeeded:-0}\" -lt 1 ]; then echo 'Release command timed out.' >&2; exit 1; fi");
		}
		sb.AppendLine("kubectl apply -f 40-app.yaml -f 50-ingress.yaml -f 70-network-policy.yaml");
		if (plan.Backups.Enabled)
		{
			sb.AppendLine("if [ -f 60-backups.yaml ]; then kubectl apply -f 60-backups.yaml; fi");
		}
		foreach (var process in plan.Processes)
		{
			sb.AppendLine($"kubectl -n \"$ns\" rollout status deployment/{process.Name} --timeout=600s");
		}
		sb.AppendLine("echo \"Deployed.\"");
		return sb.ToString();
	}

	static string ResourceBlock(Resources r, string indent) =>
		$"{indent}requests:\n{indent}  cpu: {r.CpuRequest}m\n{indent}  memory: {r.MemoryRequest}Mi\n{indent}limits:\n{indent}  cpu: {r.CpuLimit}m\n{indent}  memory: {r.MemoryLimit}Mi";

	static string Indent(string text, string indent) => string.Join('\n', text.Split('\n').Select(l => l.Length == 0 ? l : indent + l));

	/// <summary>Kubernetes object names: lowercase, ≤ 63 chars.</summary>
	static string JobSuffix(string revision)
	{
		var clean = new string(revision.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()).Trim('-');
		return clean.Length == 0 ? "latest" : clean.Length > 40 ? clean[..40] : clean;
	}

	internal static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
