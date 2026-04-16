using System;
using System.IO;

namespace FFVM.Debug.Pathing
{
    /// <summary>
    /// Centralized path and file-URI normalization utility.
    ///
    /// Usage constraints:
    /// 1) Never compare raw path strings directly in dependency/index keys.
    /// 2) Always normalize through NormalizePath before map lookup/storage.
    /// 3) Use UriToPath/PathToFileUri for protocol boundary conversions.
    /// 4) Use ResolvePath for base-path + relative-path composition.
    /// </summary>
    public static class WorkspacePathTool
    {
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string normalized = path.Trim().Replace('\\', '/');
            normalized = CollapseSlashes(normalized);

            // Uri.LocalPath may return "/C:/..." in some environments.
            if (normalized.Length >= 3
                && normalized[0] == '/'
                && char.IsLetter(normalized[1])
                && normalized[2] == ':')
            {
                normalized = normalized.Substring(1);
            }

            if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
            {
                bool keepDriveRoot = normalized.Length == 3 && char.IsLetter(normalized[0]) && normalized[1] == ':';
                if (!keepDriveRoot)
                    normalized = normalized.TrimEnd('/');
            }

            return normalized;
        }

        public static bool IsAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (LooksLikeFileUri(path))
                return true;

            if (path.StartsWith("\\\\", StringComparison.Ordinal)
                || path.StartsWith("//", StringComparison.Ordinal))
                return true;

            if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
                return true;

            return path.StartsWith("/", StringComparison.Ordinal);
        }

        public static string ResolvePath(string basePath, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            if (LooksLikeFileUri(path))
                return UriToPath(path);

            if (IsAbsolutePath(path))
                return NormalizePath(path);

            if (string.IsNullOrWhiteSpace(basePath))
                return NormalizePath(path);

            string normalizedBase = NormalizePath(basePath);
            if (LooksLikePosixRoot(normalizedBase))
            {
                string combinedPosix = CombinePosix(normalizedBase, path);
                return NormalizePath(combinedPosix);
            }

            string combined = Path.GetFullPath(Path.Combine(ToSystemPath(normalizedBase), ToSystemPath(path)));
            return NormalizePath(combined);
        }

        public static string UriToPath(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return null;

            if (!LooksLikeFileUri(uri))
                return NormalizePath(uri);

            if (Uri.TryCreate(uri, UriKind.Absolute, out Uri parsed) && parsed.IsFile)
            {
                string localPath = Uri.UnescapeDataString(parsed.LocalPath);
                return NormalizePath(localPath);
            }

            // Fallback for malformed file:// payloads.
            string raw = uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? uri.Substring("file://".Length)
                : uri;
            return NormalizePath(Uri.UnescapeDataString(raw));
        }

        public static string PathToFileUri(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string normalized = LooksLikeFileUri(path)
                ? UriToPath(path)
                : NormalizePath(path);

            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            string escaped = EscapePath(normalized);

            if (normalized.StartsWith("//", StringComparison.Ordinal))
                return "file:" + escaped;

            if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
                return "file:///" + escaped;

            if (normalized.StartsWith("/", StringComparison.Ordinal))
                return "file://" + escaped;

            return "file:///" + escaped;
        }

        public static string FilePathToUri(string originFile, string rootPath)
        {
            if (string.IsNullOrWhiteSpace(originFile))
                return null;

            if (!IsAbsolutePath(originFile) && string.IsNullOrWhiteSpace(rootPath))
                return null;

            string absolute = IsAbsolutePath(originFile)
                ? ResolvePath(null, originFile)
                : ResolvePath(rootPath, originFile);

            if (string.IsNullOrWhiteSpace(absolute))
                return null;

            return PathToFileUri(absolute);
        }

        public static string ToRelativePathOrAbsolute(string absolutePath, string rootPath)
        {
            string normalizedAbsolute = NormalizePath(absolutePath);
            string normalizedRoot = NormalizePath(rootPath);

            if (string.IsNullOrWhiteSpace(normalizedAbsolute)
                || string.IsNullOrWhiteSpace(normalizedRoot))
                return null;

            string rooted = EnsureTrailingSlash(normalizedRoot);
            StringComparison comparison = GetComparison(normalizedAbsolute, normalizedRoot);

            if (normalizedAbsolute.StartsWith(rooted, comparison))
                return normalizedAbsolute.Substring(rooted.Length);

            return normalizedAbsolute;
        }

        public static bool IsUnderDirectory(string path, string directory)
        {
            string normalizedPath = NormalizePath(path);
            string normalizedDirectory = NormalizePath(directory);
            if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(normalizedDirectory))
                return false;

            string rooted = EnsureTrailingSlash(normalizedDirectory);
            StringComparison comparison = GetComparison(normalizedPath, normalizedDirectory);

            return normalizedPath.StartsWith(rooted, comparison)
                || string.Equals(normalizedPath, normalizedDirectory, comparison);
        }

        private static bool LooksLikeFileUri(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikePosixRoot(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (value.StartsWith("//", StringComparison.Ordinal))
                return false;

            if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
                return false;

            return value.StartsWith("/", StringComparison.Ordinal);
        }

        private static string CombinePosix(string basePath, string relativePath)
        {
            string left = NormalizePath(basePath) ?? string.Empty;
            string right = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');

            if (string.IsNullOrEmpty(left))
                return "/" + right;

            if (left.EndsWith("/", StringComparison.Ordinal))
                return left + right;

            return left + "/" + right;
        }

        private static string ToSystemPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            char separator = Path.DirectorySeparatorChar;
            return path.Replace('/', separator).Replace('\\', separator);
        }

        private static string EnsureTrailingSlash(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            return path.EndsWith("/", StringComparison.Ordinal)
                ? path
                : path + "/";
        }

        private static StringComparison GetComparison(string left, string right)
        {
            bool windowsLike = IsWindowsLikePath(left) || IsWindowsLikePath(right);
            return windowsLike ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        }

        private static bool IsWindowsLikePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return path.StartsWith("//", StringComparison.Ordinal)
                || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');
        }

        private static string EscapePath(string path)
        {
            return Uri.EscapeDataString(path)
                .Replace("%2F", "/")
                .Replace("%3A", ":");
        }

        private static string CollapseSlashes(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            bool uncPrefix = value.StartsWith("//", StringComparison.Ordinal);
            int start = uncPrefix ? 2 : 0;

            var chars = new char[value.Length];
            int index = 0;
            bool previousSlash = false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool slash = c == '/';

                if (i < start)
                {
                    chars[index++] = c;
                    previousSlash = slash;
                    continue;
                }

                if (slash && previousSlash)
                    continue;

                chars[index++] = c;
                previousSlash = slash;
            }

            return new string(chars, 0, index);
        }
    }
}
