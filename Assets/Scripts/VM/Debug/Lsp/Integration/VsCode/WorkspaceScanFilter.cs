// Responsibility:
//   Directory-level exclusion rules and filtered .ffs enumeration for workspace bootstrap scans.
// Owns:
//   Hard-coded excluded directory names and recursive walker that skips them before descending.
// Inputs/Outputs:
//   In: absolute directory path.
//   Out: list of absolute .ffs file paths outside excluded directories, plus scan counts.
// Allowed Dependencies:
//   - System.IO
//   - WorkspacePathTool
// Forbidden Dependencies:
//   - LSP protocol / database contracts / parser.
// Invariants:
//   - Excluded directory names are compared case-insensitively on the segment itself, not substring.
//   - Enumeration never throws; IO failures are counted and skipped.

using System;
using System.Collections.Generic;
using System.IO;
using FFVM.Debug.Tooling;

namespace FFVM.Debug.Lsp.Integration.VsCode
{
	internal sealed class WorkspaceScanMetrics
	{
		public int DirectoriesVisited;
		public int DirectoriesSkipped;
		public int FilesFound;
		public int FileErrors;
		public int BootstrapBatchCount;
		public int BootstrapAppliedDocuments;
		public int BootstrapDeferredDocuments;
		public int BootstrapApplyFailures;
		public bool BootstrapBudgetExceeded;
		public long ElapsedMilliseconds;
	}

	internal static class WorkspaceScanFilter
	{
		private const string FfsExtension = ".ffs";

		private static readonly HashSet<string> ExcludedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"Library",
			"Temp",
			"Logs",
			"obj",
			"bin",
			".git",
			".svn",
			".hg",
			"node_modules",
			".vs",
			".vscode",
			".idea",
			"UserSettings",
		};

		public static bool ShouldExcludeDirectory(string directoryName)
		{
			if (string.IsNullOrEmpty(directoryName))
				return false;

			return ExcludedDirectoryNames.Contains(directoryName);
		}

		public static List<string> EnumerateFfsFiles(string absoluteRoot, WorkspaceScanMetrics metrics)
		{
			var output = new List<string>();
			if (string.IsNullOrWhiteSpace(absoluteRoot) || !Directory.Exists(absoluteRoot))
				return output;

			var stack = new Stack<string>();
			stack.Push(absoluteRoot);

			while (stack.Count > 0)
			{
				string current = stack.Pop();
				if (metrics != null)
					metrics.DirectoriesVisited++;

				try
				{
					string[] subdirectories = Directory.GetDirectories(current);
					for (int i = 0; i < subdirectories.Length; i++)
					{
						string subdirectory = subdirectories[i];
						string name = Path.GetFileName(subdirectory);
						if (ShouldExcludeDirectory(name))
						{
							if (metrics != null)
								metrics.DirectoriesSkipped++;
							continue;
						}

						stack.Push(subdirectory);
					}

					string[] files = Directory.GetFiles(current);
					for (int i = 0; i < files.Length; i++)
					{
						string file = files[i];
						if (!file.EndsWith(FfsExtension, StringComparison.OrdinalIgnoreCase))
							continue;

						string normalized = WorkspacePathTool.NormalizePath(file);
						if (string.IsNullOrWhiteSpace(normalized))
							continue;

						output.Add(normalized);
						if (metrics != null)
							metrics.FilesFound++;
					}
				}
				catch
				{
					if (metrics != null)
						metrics.FileErrors++;
				}
			}

			return output;
		}
	}
}
