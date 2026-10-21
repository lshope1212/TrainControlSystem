using System.Text.RegularExpressions;

namespace TrackController.Core.Control;

/// <summary>A bounded Boolean expression interpreter, never executable host code.</summary>
public sealed class BooleanPlcProgram
{
    public static readonly string[] InputNames = ["route_safe", "switch_safe", "requested_normal", "crossing_required", "running"];
    public static readonly string[] OutputNames = ["allow_route", "switch_normal", "signal_clear", "crossing_close"];
    public static readonly string[] AdditionalInputNames = ["route_restricted", "maintenance"];
    private static readonly (string Name, string Expression)[] LegacyOutputs =
    [
        ("speed_enable", "allow_route"),
        ("authority_enable", "allow_route"),
        ("switch_move", "switch_safe and (running or maintenance)"),
        ("signal_yellow", "signal_clear and route_restricted")
    ];
    private readonly List<(string Name, Func<IReadOnlyDictionary<string, bool>, bool> Evaluate)> _assignments;

    private BooleanPlcProgram(List<(string, Func<IReadOnlyDictionary<string, bool>, bool>)> assignments) => _assignments = assignments;

    /// <summary>Validates and compiles bounded Boolean expressions without executing host-language code.</summary>
    public static BooleanPlcProgram Compile(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 32768)
        {
            throw new ArgumentException("PLC source must contain 1–32,768 characters.");
        }

        var known = new HashSet<string>(InputNames.Concat(AdditionalInputNames), StringComparer.Ordinal);
        var assignments = new List<(string, Func<IReadOnlyDictionary<string, bool>, bool>)>();
        int lineNumber = 0;
        foreach (string raw in source.Replace("\r", "").Split('\n'))
        {
            lineNumber++;
            string line = raw.Split('#')[0].Trim().TrimEnd(';');
            if (line.Length == 0)
            {
                continue;
            }

            if (assignments.Count >= 128)
            {
                throw new ArgumentException("PLC programs are limited to 128 assignments.");
            }

            var match = Regex.Match(line, @"^([a-z][a-z0-9_]*)\s*=\s*(.+)$");
            if (!match.Success)
            {
                throw new ArgumentException($"PLC line {lineNumber}: expected name = Boolean expression.");
            }

            string name = match.Groups[1].Value;
            if (known.Contains(name) || name is "true" or "false" or "and" or "or" or "not")
            {
                throw new ArgumentException($"PLC line {lineNumber}: duplicate or reserved name '{name}'.");
            }

            var parser = new Parser(match.Groups[2].Value, known, lineNumber);
            assignments.Add((name, parser.Parse()));
            known.Add(name);
        }

        if (!OutputNames.All(known.Contains))
        {
            throw new ArgumentException("PLC must assign allow_route, switch_normal, signal_clear, and crossing_close.");
        }

        // Older four-output files are expanded into explicit Boolean output assignments.
        // New programs should declare these outputs themselves for review and demonstration.
        foreach (var (name, expression) in LegacyOutputs)
        {
            if (known.Add(name))
            {
                assignments.Add((name, new Parser(expression, known, lineNumber).Parse()));
            }
        }

        return new BooleanPlcProgram(assignments);
    }

    /// <summary>Evaluates the compiled Boolean assignments against a complete set of controller inputs.</summary>
    public IReadOnlyDictionary<string, bool> Execute(IReadOnlyDictionary<string, bool> inputs)
    {
        if (InputNames.Any(inputName => !inputs.ContainsKey(inputName)))
        {
            throw new ArgumentException("Missing PLC input.");
        }

        var values = new Dictionary<string, bool>(inputs, StringComparer.Ordinal);
        foreach (string name in AdditionalInputNames)
        {
            values.TryAdd(name, false);
        }
        foreach (var (name, evaluate) in _assignments)
        {
            values[name] = evaluate(values);
        }
        return values;
    }

    private sealed class Parser(string source, HashSet<string> known, int line)
    {
        private int _position;
        private int _depth;
        private int _tokens;
        private string _token = "";

        /// <summary>Parses one Boolean expression and rejects trailing or unsupported tokens.</summary>
        public Func<IReadOnlyDictionary<string, bool>, bool> Parse()
        {
            ReadNextToken();
            var value = ParseOr();
            if (_token != "")
            {
                ThrowParseError("unexpected token");
            }

            return value;
        }

        private void ReadNextToken()
        {
            if (++_tokens > 256)
            {
                ThrowParseError("expression too long");
            }

            while (_position < source.Length && char.IsWhiteSpace(source[_position]))
            {
                _position++;
            }

            if (_position == source.Length)
            {
                _token = "";
                return;
            }

            char character = source[_position++];
            if (character is '(' or ')' or '!')
            {
                _token = character.ToString();
                return;
            }

            if (character is '&' or '|')
            {
                if (_position >= source.Length || source[_position++] != character)
                {
                    ThrowParseError("use && or ||");
                }

                _token = character == '&' ? "and" : "or";
                return;
            }

            if (!char.IsAsciiLetter(character))
            {
                ThrowParseError("only Boolean names, constants, and operators are allowed");
            }

            int start = _position - 1;
            while (_position < source.Length && (char.IsAsciiLetterOrDigit(source[_position]) || source[_position] == '_'))
            {
                _position++;
            }

            _token = source[start.._position];
        }

        private Func<IReadOnlyDictionary<string, bool>, bool> ParseOr()
        {
            var left = ParseAnd();
            while (_token == "or")
            {
                ReadNextToken();
                var leftOperand = left;
                var rightOperand = ParseAnd();
                left = values => leftOperand(values) | rightOperand(values);
            }

            return left;
        }

        private Func<IReadOnlyDictionary<string, bool>, bool> ParseAnd()
        {
            var left = ParseAtom();
            while (_token == "and")
            {
                ReadNextToken();
                var leftOperand = left;
                var rightOperand = ParseAtom();
                left = values => leftOperand(values) & rightOperand(values);
            }

            return left;
        }

        private Func<IReadOnlyDictionary<string, bool>, bool> ParseAtom()
        {
            if (++_depth > 32)
            {
                ThrowParseError("expression nesting exceeds 32");
            }

            Func<IReadOnlyDictionary<string, bool>, bool> result;
            if (_token is "not" or "!")
            {
                ReadNextToken();
                var inner = ParseAtom();
                result = values => !inner(values);
            }
            else if (_token == "(")
            {
                ReadNextToken();
                result = ParseOr();
                if (_token != ")")
                {
                    ThrowParseError("missing closing parenthesis");
                }

                ReadNextToken();
            }
            else
            {
                string name = _token;
                if (name is "true" or "false")
                {
                    result = _ => name == "true";
                }
                else if (known.Contains(name))
                {
                    result = values => values[name];
                }
                else
                {
                    ThrowParseError($"unknown Boolean '{name}'");
                    return null!;
                }

                ReadNextToken();
            }

            _depth--;
            return result;
        }

        private void ThrowParseError(string reason) => throw new ArgumentException($"PLC line {line}: {reason}.");
    }
}
