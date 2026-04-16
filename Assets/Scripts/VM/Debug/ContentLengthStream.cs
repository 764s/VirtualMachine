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
        private const string ContentLengthHeaderPrefix = "Content-Length:";
        private const string ContentLengthHeaderWithSpace = "Content-Length: ";
        private const string HeaderTerminator = "\r\n\r\n";
        private const int UnknownContentLength = -1;
        private const int EndOfStream = -1;
        private const char CarriageReturn = '\r';
        private const char LineFeed = '\n';

        /// <summary>
        /// Read one framed message from the input stream.
        /// Returns the message body as a string, or null if the stream is closed.
        /// </summary>
        public static string ReadMessage(Stream input)
        {
            // Read header line: "Content-Length: N\r\n"
            // There may be additional headers, terminated by "\r\n\r\n"
            int contentLength = UnknownContentLength;
            string line;
            while ((line = ReadLine(input)) != null)
            {
                if (line.Length == 0)
                {
                    // Empty line = end of headers
                    break;
                }

                if (line.StartsWith(ContentLengthHeaderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string value = line.Substring(ContentLengthHeaderPrefix.Length).Trim();
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
            byte[] header = Encoding.ASCII.GetBytes(ContentLengthHeaderWithSpace + bodyBytes.Length + HeaderTerminator);
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
            int prev = EndOfStream;

            while (true)
            {
                int b = input.ReadByte();
                if (b == EndOfStream)
                    return sb.Length > 0 ? sb.ToString() : null;

                if (b == LineFeed && prev == CarriageReturn)
                {
                    // Remove the trailing \r we already appended
                    if (sb.Length > 0 && sb[sb.Length - 1] == CarriageReturn)
                        sb.Remove(sb.Length - 1, 1);
                    return sb.ToString();
                }

                sb.Append((char)b);
                prev = b;
            }
        }
    }
}
