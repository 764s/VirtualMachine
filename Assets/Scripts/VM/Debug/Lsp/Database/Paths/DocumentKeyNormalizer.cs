using System;
using FFVM.Debug.Tooling;

namespace FFVM.Debug.Lsp.Database.Paths
{
	/// <summary>
	/// Canonical normalization for database document keys.
	///
	/// Policy:
	/// - Trim whitespace.
	/// - Canonicalize file URIs to stable file:/// form.
	/// - Convert absolute file-system paths to file URIs.
	/// - Leave non-file identifiers untouched.
	/// </summary>
	public static class DocumentKeyNormalizer
	{
		public static string Normalize(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
				return string.Empty;

			string trimmed = value.Trim();
			if (trimmed.Length == 0)
				return string.Empty;

			if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
			{
				string asPath = WorkspacePathTool.UriToPath(trimmed);
				string canonicalUri = WorkspacePathTool.PathToFileUri(asPath);
				return string.IsNullOrWhiteSpace(canonicalUri) ? trimmed : canonicalUri;
			}

			if (WorkspacePathTool.IsAbsolutePath(trimmed))
			{
				string canonicalUri = WorkspacePathTool.PathToFileUri(trimmed);
				if (!string.IsNullOrWhiteSpace(canonicalUri))
					return canonicalUri;
			}

			return trimmed;
		}
	}
}
