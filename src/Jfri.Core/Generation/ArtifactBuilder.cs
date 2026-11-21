using System.Text.Json;
using System.Text.Json.Serialization;
using Jfri.Core.Analysis;
using Jfri.Core.Planning;

namespace Jfri.Core.Generation;

/// <summary>Renders every file a plan produces: Dockerfile, runtime manifests, CI workflow and the plan itself.</summary>
public static class ArtifactBuilder
{
	public static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	public static List<Artifact> Build(DeploymentPlan plan, RenderContext context, string branch = "main")
	{
		var files = new List<Artifact>();
		if (plan.App.BuildStrategy == BuildStrategy.Generated)
		{
			var prefix = string.IsNullOrEmpty(plan.BuildContext) ? "" : plan.BuildContext + "/";
			files.Add(new($"{prefix}Dockerfile", DockerfileGenerator.Generate(plan)));
			files.Add(new($"{prefix}.dockerignore", DockerfileGenerator.DockerIgnore(plan)));
		}
		files.AddRange(plan.Runtime == RuntimeKind.Kubernetes
			? KubernetesGenerator.Generate(plan, context)
			: ComposeGenerator.Generate(plan, context));
		files.Add(new(CiGenerator.Path, CiGenerator.Generate(plan, branch)));
		files.Add(new("jfri.plan.json", JsonSerializer.Serialize(plan, JsonOptions)));
		return files;
	}
}
