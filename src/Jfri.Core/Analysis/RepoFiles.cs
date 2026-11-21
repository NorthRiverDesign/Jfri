using System.Text.RegularExpressions;

namespace Jfri.Core.Analysis;

/// <summary>
/// A read-only, bounded view of a checked-out repository. Analysis only ever reads files through
/// this class: nothing from the repository is executed, symlinks are never followed (so a
/// malicious repo cannot point the analyzer outside its checkout), and file count and file size
/// are capped.
/// </summary>
public sealed class RepoFiles
{
	public const int DefaultMaxFiles = 40_000;
	public const long MaxReadBytes = 1024 * 1024;

	static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
	{
		".git", ".hg", ".svn", "node_modules", "vendor", "bin", "obj", "dist", "build", "out",
		".next", ".nuxt", ".output", ".svelte-kit", ".venv", "venv", "env", "__pycache__",
		".pytest_cache", ".mypy_cache", "target", ".gradle", ".idea", ".vs", ".vscode",
		"coverage", "tmp", ".cache", "packages", "bower_components", ".terraform",
	};

	readonly string _root;
	readonly List<string> _files;
	readonly HashSet<string> _fileSet;

	RepoFiles(string root, List<string> files, bool truncated)
	{
		_root = root;
		_files = files;
		_fileSet = new HashSet<string>(files, StringComparer.Ordinal);
		Truncated = truncated;
	}

	/// <summary>Relative paths (forward slashes) of every file scanned.</summary>
	public IReadOnlyList<string> Files => _files;

	public bool Truncated { get; }

	public static RepoFiles Open(string root, int maxFiles = DefaultMaxFiles)
	{
		var full = Path.GetFullPath(root);
		if (!Directory.Exists(full))
		{
			throw new DirectoryNotFoundException($"Repository directory not found: {root}");
		}
		var files = new List<string>();
		var truncated = false;
		var pending = new Stack<string>();
		pending.Push(full);
		while (pending.Count > 0 && !truncated)
		{
			var dir = pending.Pop();
			IEnumerable<FileSystemInfo> entries;
			try
			{
				entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
			{
				continue;
			}
			foreach (var entry in entries)
			{
				if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
				{
					continue;
				}
				if (entry is DirectoryInfo)
				{
					if (!SkippedDirectories.Contains(entry.Name))
					{
						pending.Push(entry.FullName);
					}
					continue;
				}
				if (files.Count >= maxFiles)
				{
					truncated = true;
					break;
				}
				files.Add(Path.GetRelativePath(full, entry.FullName).Replace('\\', '/'));
			}
		}
		files.Sort(StringComparer.Ordinal);
		return new RepoFiles(full, files, truncated);
	}

	public bool Exists(string relativePath) => _fileSet.Contains(Normalize(relativePath));

	/// <summary>True when any scanned file lives under the given directory.</summary>
	public bool DirectoryExists(string relativeDir)
	{
		var prefix = Normalize(relativeDir).TrimEnd('/') + "/";
		return _files.Any(f => f.StartsWith(prefix, StringComparison.Ordinal));
	}

	/// <summary>Reads a text file, or returns null if it does not exist or is too large.</summary>
	public string? Read(string relativePath)
	{
		var rel = Normalize(relativePath);
		if (!_fileSet.Contains(rel))
		{
			return null;
		}
		var path = Path.GetFullPath(Path.Combine(_root, rel));
		if (!path.StartsWith(_root, StringComparison.Ordinal))
		{
			return null;
		}
		var info = new FileInfo(path);
		if (!info.Exists || info.LinkTarget is not null || info.Length > MaxReadBytes)
		{
			return null;
		}
		try
		{
			return File.ReadAllText(path);
		}
		catch (IOException)
		{
			return null;
		}
	}

	/// <summary>Files whose name (not path) matches the glob (supports * and ?).</summary>
	public IEnumerable<string> FindByName(string glob, string underDir = "")
	{
		var regex = GlobToRegex(glob);
		var prefix = underDir.Length == 0 ? "" : Normalize(underDir).TrimEnd('/') + "/";
		return _files.Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && regex.IsMatch(FileName(f)));
	}

	/// <summary>Files directly inside a directory ("" = repo root).</summary>
	public IEnumerable<string> FilesIn(string dir)
	{
		var prefix = dir.Length == 0 ? "" : Normalize(dir).TrimEnd('/') + "/";
		return _files.Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.IndexOf('/', prefix.Length) < 0);
	}

	/// <summary>Returns the first of the candidate paths that exists.</summary>
	public string? FirstExisting(params string[] candidates) => candidates.FirstOrDefault(Exists);

	public static string Join(string dir, string name) => dir.Length == 0 ? name : $"{dir.TrimEnd('/')}/{name}";

	public static string FileName(string path)
	{
		var i = path.LastIndexOf('/');
		return i < 0 ? path : path[(i + 1)..];
	}

	public static string DirectoryOf(string path)
	{
		var i = path.LastIndexOf('/');
		return i < 0 ? "" : path[..i];
	}

	static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

	static Regex GlobToRegex(string glob) =>
		new("^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
