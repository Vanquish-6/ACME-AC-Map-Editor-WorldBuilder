using System.Collections.Generic;
using System.Text;

namespace WorldBuilder.Editors.Layout {
    /// <summary>
    /// Subset of client StringTable meta-language for layout preview (not a full parser).
    /// </summary>
    internal static class StringTableMetaLanguageRenderer {
        static readonly char[] EscapeChars = "[]!{}#\\|^$".ToCharArray();

        public static bool ContainsMetaLanguage(string text) {
            return text.IndexOfAny(EscapeChars) >= 0;
        }

        /// <summary>
        /// Best-effort preview: literal segments, first branch of [a|b], simple {var} passthrough.
        /// </summary>
        public static string RenderForPreview(string raw, IReadOnlyDictionary<string, string>? variables = null) {
            if (string.IsNullOrEmpty(raw))
                return raw;

            if (!ContainsMetaLanguage(raw))
                return raw;

            var sb = new StringBuilder(raw.Length);
            int i = 0;
            while (i < raw.Length) {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length) {
                    sb.Append(Unescape(raw[++i]));
                    i++;
                    continue;
                }

                if (c == '[') {
                    if (TryReadBracketChoice(raw, i, out var consumed, out var segment)) {
                        sb.Append(RenderForPreview(segment, variables));
                        i += consumed;
                        continue;
                    }
                }

                if (c == '{' && variables != null) {
                    if (TryReadBraceVariable(raw, i, out var consumed, out var name) &&
                        variables.TryGetValue(name, out var value)) {
                        sb.Append(value);
                        i += consumed;
                        continue;
                    }
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        static char Unescape(char c) => c switch {
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            '"' => '"',
            _ => c
        };

        static bool TryReadBracketChoice(string raw, int start, out int consumed, out string firstBranch) {
            consumed = 0;
            firstBranch = "";
            if (start >= raw.Length || raw[start] != '[')
                return false;

            int depth = 0;
            for (int j = start; j < raw.Length; j++) {
                if (raw[j] == '[') depth++;
                else if (raw[j] == ']') {
                    depth--;
                    if (depth == 0) {
                        var inner = raw.Substring(start + 1, j - start - 1);
                        int pipe = FindTopLevelPipe(inner);
                        firstBranch = pipe >= 0 ? inner[..pipe] : inner;
                        consumed = j - start + 1;
                        return true;
                    }
                }
            }

            return false;
        }

        static int FindTopLevelPipe(string inner) {
            int depth = 0;
            for (int i = 0; i < inner.Length; i++) {
                switch (inner[i]) {
                    case '[': depth++; break;
                    case ']': depth--; break;
                    case '|' when depth == 0: return i;
                }
            }
            return -1;
        }

        static bool TryReadBraceVariable(string raw, int start, out int consumed, out string name) {
            consumed = 0;
            name = "";
            if (start >= raw.Length || raw[start] != '{')
                return false;

            int end = raw.IndexOf('}', start + 1);
            if (end < 0)
                return false;

            name = raw.Substring(start + 1, end - start - 1).Trim();
            consumed = end - start + 1;
            return name.Length > 0;
        }
    }
}
