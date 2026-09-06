using System.Collections.Generic;

namespace Frognet.Data
{
    /// <summary>
    /// Shared line handling for both text formats: strips <c>//</c> comments and blank lines, and
    /// keeps the indent so a data file can tell a section's entries from the next field above it.
    /// </summary>
    public static class DataTextReader
    {
        public struct Line
        {
            public int number;
            public int indent;
            public string content;
        }

        public static IEnumerable<Line> Read(string text)
        {
            if (string.IsNullOrEmpty(text))
                yield break;

            string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < raw.Length; i++)
            {
                string line = raw[i];
                int comment = line.IndexOf("//", System.StringComparison.Ordinal);

                if (comment >= 0)
                    line = line.Substring(0, comment);

                int indent = 0;

                while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
                    indent++;

                string content = line.Trim();

                if (content.Length == 0)
                    continue;

                yield return new Line { number = i + 1, indent = indent, content = content };
            }
        }
    }
}
