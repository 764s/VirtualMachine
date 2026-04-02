using System;
using System.IO;
using System.Text;

namespace FFVM.Debug
{
    /// <summary>
    /// Content-Length framed I/O for DAP and LSP protocols.
    /// Reads/writes messages with "Content-Length: N\r\n\r\n" header framing.
    /// Shared between DAP adapter and future LSP server.
    /// </summary>
    public static class ContentLengthStream
    {
        private static readonly byte[] ContentLengthHeader = Encoding.ASCII.GetBytes("Content-Length: ");
        private static readonly byte[] HeaderTerminator = Encoding.ASCII.GetBytes("\r\n\r\n");

        /// <summary>
        /// Read one framed message from the input stream.
        /// Returns the message body as a string, or null if the stream is closed.
        /// </summary>
        public static string ReadMessage(Stream input)
        {
            // Read header line: "Content-Length: N\r\n"
            // There may be additional headers, terminated by "\r\n\r\n"
            int contentLength = -1;
            string line;
            while ((line = ReadLine(input)) != null)
            {
                if (line.Length == 0)
                {
                    // Empty line = end of headers
                    break;
                }

                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    string value = line.Substring("Content-Length:".Length).Trim();
                    if (int.TryParse(value, out int len))
                        contentLength = len;
                }
                // Ignore other headers (Content-Type, etc.)
            }

            if (contentLength < 0)
                return null; // Stream closed or malformed

            // Read exactly contentLength bytes
            byte[] buffer = new byte[contentLength];
            int totalRead = 0;
            while (totalRead < contentLength)
            {
                int bytesRead = input.Read(buffer, totalRead, contentLength - totalRead);
                if (bytesRead <= 0)
                    return null; // Stream closed
                totalRead += bytesRead;
            }

            return Encoding.UTF8.GetString(buffer);
        }

        /// <summary>
        /// Write one framed message to the output stream.
        /// </summary>
        public static void WriteMessage(Stream output, string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
            output.Write(header, 0, header.Length);
            output.Write(bodyBytes, 0, bodyBytes.Length);
            output.Flush();
        }

        /// <summary>
        /// Read a single line terminated by \r\n from the input stream.
        /// Returns null if stream is closed. Returns empty string for blank line.
        /// </summary>
        private static string ReadLine(Stream input)
        {
            var sb = new StringBuilder();
            int prev = -1;

            while (true)
            {
                int b = input.ReadByte();
                if (b < 0)
                    return sb.Length > 0 ? sb.ToString() : null;

                if (b == '\n' && prev == '\r')
                {
                    // Remove the trailing \r we already appended
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
                        sb.Remove(sb.Length - 1, 1);
                    return sb.ToString();
                }

                sb.Append((char)b);
                prev = b;
            }
        }
    }
}
