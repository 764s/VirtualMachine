// Responsibility:
//   Resolve an include-path literal to a list of candidate normalized target document URIs.
// Owns:
//   Path normalization rules for include targets: extension completion, base-dir resolution,
//   and canonical file:// URI form.
// Inputs/Outputs:
//   In: source document URI, raw include path literal (maybe unquoted).
//   Out: ordered list of candidate document URIs (most likely first).
// Allowed Dependencies:
//   - WorkspacePathTool
//   - DocumentKeyNormalizer
// Forbidden Dependencies:
//   - Parser / AST / database contracts.
// Invariants:
//   - Never throws; returns empty list when resolution is impossible.
//   - Candidates are URI-form, lowercase drive letter, forward slashes, without trailing slash.

using System;
using System.Collections.Generic;
using System.IO;
using FFVM.Debug.Tooling;

namespace FFVM.Debug.Lsp.Database.Paths
{
	internal static class IncludeTargetResolver
	{
		private const string FfsExtension = ".ffs";
		private static readonly object WorkspaceRootsSync = new object();
		private static IReadOnlyList<string> WorkspaceIncludeRoots = new List<string>(0);

		public static void SetWorkspaceIncludeRoots(IReadOnlyList<string> includeRoots)
		{
			var normalizedRoots = new List<string>();
			if (includeRoots != null)
			{
				for (int i = 0; i < includeRoots.Count; i++)
				{
					string candidate = WorkspacePathTool.NormalizePath(includeRoots[i]);
					if (string.IsNullOrWhiteSpace(candidate) || ContainsIgnoreCase(normalizedRoots, candidate))
						continue;

					normalizedRoots.Add(candidate);
				}
			}

			lock (WorkspaceRootsSync)
				WorkspaceIncludeRoots = normalizedRoots;
		}

		public static List<string> ResolveCandidates(string sourceDocumentUri, string includePath)
		{
			var results = new List<string>();
			if (string.IsNullOrWhiteSpace(includePath))
				return results;

			string trimmed = includePath.Trim();
			if (trimmed.Length == 0)
				return results;

			string sourceAbsPath = !string.IsNullOrWhiteSpace(sourceDocumentUri)
				? WorkspacePathTool.UriToPath(sourceDocumentUri)
				: null;

			string baseDir = !string.IsNullOrWhiteSpace(sourceAbsPath)
				? WorkspacePathTool.NormalizePath(Path.GetDirectoryName(sourceAbsPath))
				: null;

			string withExt = AppendFfsExtensionIfMissing(trimmed);
			AppendCandidate(results, ResolveSingleCandidate(baseDir, trimmed));
			AppendCandidate(results, ResolveSingleCandidate(baseDir, withExt));

			IReadOnlyList<string> workspaceRoots = GetWorkspaceIncludeRootsSnapshot();
			for (int i = 0; i < workspaceRoots.Count; i++)
			{
				string workspaceRoot = workspaceRoots[i];
				if (string.IsNullOrWhiteSpace(workspaceRoot))
					continue;

				AppendCandidate(results, ResolveSingleCandidate(workspaceRoot, trimmed));
				AppendCandidate(results, ResolveSingleCandidate(workspaceRoot, withExt));
			}

			return results;
		}

		private static IReadOnlyList<string> GetWorkspaceIncludeRootsSnapshot()
		{
			lock (WorkspaceRootsSync)
			{
				if (WorkspaceIncludeRoots == null || WorkspaceIncludeRoots.Count == 0)
					return new List<string>(0);

				var copy = new List<string>(WorkspaceIncludeRoots.Count);
				for (int i = 0; i < WorkspaceIncludeRoots.Count; i++)
					copy.Add(WorkspaceIncludeRoots[i]);

				return copy;
			}
		}

		private static void AppendCandidate(List<string> results, string candidate)
		{
			if (string.IsNullOrWhiteSpace(candidate) || ContainsIgnoreCase(results, candidate))
				return;

			results.Add(candidate);
		}

		private static string ResolveSingleCandidate(string baseDir, string relativePath)
		{
			if (string.IsNullOrWhiteSpace(relativePath))
				return null;

			string resolvedAbsPath = WorkspacePathTool.ResolvePath(baseDir, relativePath);
			if (string.IsNullOrWhiteSpace(resolvedAbsPath))
				return null;

			string withExt = AppendFfsExtensionIfMissing(resolvedAbsPath);
			string uri = WorkspacePathTool.PathToFileUri(withExt);
			if (string.IsNullOrWhiteSpace(uri))
				return null;

			return DocumentKeyNormalizer.Normalize(uri);
		}

		private static string AppendFfsExtensionIfMissing(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return path;

			if (path.EndsWith(FfsExtension, StringComparison.OrdinalIgnoreCase))
				return path;

			return path + FfsExtension;
		}

		private static bool ContainsIgnoreCase(List<string> list, string value)
		{
			if (list == null || list.Count == 0 || string.IsNullOrWhiteSpace(value))
				return false;

			for (int i = 0; i < list.Count; i++)
			{
				if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
	}
}
