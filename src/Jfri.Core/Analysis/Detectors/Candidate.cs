namespace Jfri.Core.Analysis.Detectors;

/// <summary>
/// An app a detector recognised, with the backing services, volumes and environment it needs.
/// The analyzer picks the best candidate as the deployable app and merges its needs into the profile.
/// </summary>
internal sealed class Candidate(AppProfile app)
{
	public AppProfile App { get; } = app;
	/// <summary>Services, persistent paths, environment and warnings specific to this app.</summary>
	public RepositoryProfile Needs { get; } = new();
	/// <summary>Higher wins when a repo contains several apps.</summary>
	public int Score { get; set; }
}

internal interface IStackDetector
{
	IEnumerable<Candidate> Detect(RepoFiles files);
}
