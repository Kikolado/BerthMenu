using System;
using System.Globalization;
using System.Text;

namespace BerthMenu.Services
{
    /// <summary>
    /// The search box's built-in calculator (AppConfig.SearchCalculator): if what was
    /// typed is a math expression, works out the answer — entirely locally, no
    /// internet involved.
    ///
    /// Understands numbers, + - * / ^, parentheses, × and ÷, "x" between numbers
    /// (3x4), a trailing % meaning "divided by 100" (15% = 0.15), and "of"
    /// (15% of 80 = 12). Thousands separators (1,000) are ignored.
    ///
    /// Only kicks in when the text contains at least one operator, so a search like
    /// "2026" or "Office 365" stays an ordinary search.
    /// </summary>
    public static class Calculator
    {
        public static bool TryEvaluate(string input, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            string expr = Normalize(input);
            if (expr.Length == 0 || !HasOperator(expr))
                return false;

            foreach (char c in expr)
            {
                if (!(char.IsDigit(c) || c is '.' or '+' or '-' or '*' or '/' or '^' or '%' or '(' or ')'))
                    return false; // letters or other symbols — not math, just a search
            }

            try
            {
                var parser = new Parser(expr);
                double value = parser.ParseExpression();
                if (!parser.AtEnd || double.IsNaN(value) || double.IsInfinity(value))
                    return false;
                result = value;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The answer as shown and copied: up to 12 significant digits, no
        /// thousands separators, no trailing zeros.</summary>
        public static string Format(double value)
        {
            if (Math.Abs(value) < 1e-12)
                value = 0;
            return value.ToString("G12", CultureInfo.InvariantCulture);
        }

        private static string Normalize(string input)
        {
            string s = input.Trim().ToLowerInvariant()
                .Replace("×", "*").Replace("÷", "/")
                .Replace(" of ", "*");

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c))
                    continue;

                // "1,000" → "1000": a comma between digits is a thousands separator.
                if (c == ',' && i > 0 && i + 1 < s.Length && char.IsDigit(s[i - 1]) && char.IsDigit(s[i + 1]))
                    continue;

                // "3x4" → "3*4": an x between a number and a number/parenthesis.
                if (c == 'x' && sb.Length > 0 && (char.IsDigit(sb[^1]) || sb[^1] is ')' or '%')
                    && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] is '(' or '.' or ' '))
                {
                    sb.Append('*');
                    continue;
                }

                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>True if there's a real operator: a % sign, or + - * / ^ that comes
        /// after something (so "-5" on its own doesn't count).</summary>
        private static bool HasOperator(string expr)
        {
            for (int i = 0; i < expr.Length; i++)
            {
                char c = expr[i];
                if (c == '%')
                    return true;
                if (i > 0 && c is '+' or '-' or '*' or '/' or '^')
                    return true;
            }
            return false;
        }

        /// <summary>Standard recursive-descent parser:
        ///   expression = term (('+' | '-') term)*
        ///   term       = power (('*' | '/') power)*
        ///   power      = unary ('^' power)?          (right-associative)
        ///   unary      = ('+' | '-') unary | postfix
        ///   postfix    = primary '%'*
        ///   primary    = number | '(' expression ')'</summary>
        private sealed class Parser
        {
            private readonly string _s;
            private int _pos;

            public Parser(string s) => _s = s;

            public bool AtEnd => _pos >= _s.Length;

            private char Peek => _pos < _s.Length ? _s[_pos] : '\0';

            public double ParseExpression()
            {
                double value = ParseTerm();
                while (Peek is '+' or '-')
                {
                    char op = _s[_pos++];
                    double rhs = ParseTerm();
                    value = op == '+' ? value + rhs : value - rhs;
                }
                return value;
            }

            private double ParseTerm()
            {
                double value = ParsePower();
                while (Peek is '*' or '/')
                {
                    char op = _s[_pos++];
                    double rhs = ParsePower();
                    value = op == '*' ? value * rhs : value / rhs;
                }
                return value;
            }

            private double ParsePower()
            {
                double value = ParseUnary();
                if (Peek == '^')
                {
                    _pos++;
                    value = Math.Pow(value, ParsePower());
                }
                return value;
            }

            private double ParseUnary()
            {
                if (Peek == '-') { _pos++; return -ParseUnary(); }
                if (Peek == '+') { _pos++; return ParseUnary(); }
                return ParsePostfix();
            }

            private double ParsePostfix()
            {
                double value = ParsePrimary();
                while (Peek == '%')
                {
                    _pos++;
                    value /= 100;
                }
                return value;
            }

            private double ParsePrimary()
            {
                if (Peek == '(')
                {
                    _pos++;
                    double inner = ParseExpression();
                    if (Peek != ')')
                        throw new FormatException("Missing )");
                    _pos++;
                    return inner;
                }

                int start = _pos;
                while (char.IsDigit(Peek) || Peek == '.')
                    _pos++;
                if (_pos == start)
                    throw new FormatException("Expected a number");

                return double.Parse(_s.AsSpan(start, _pos - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            }
        }
    }
}
