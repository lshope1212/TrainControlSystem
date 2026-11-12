using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace TrackController.Core.Control;

/// <summary>Compiles and executes Boolean PLC programs and converts their fixed-point numeric inputs and outputs.</summary>
public sealed class VitalPlcProgram
{
    public static readonly string[] BooleanInputs =
    [
        "running", "maintenance", "request_fresh", "request_retired", "origin_occupied", "route_conflict",
        "scan_live", "fault_in", "yellow_in", "signal_in", "is_origin", "feedback_fresh", "block_clear", "block_unknown",
        "block_faulted", "block_closed", "block_pending", "has_signal", "signal_proceed", "signal_yellow_feedback",
        "manual_red", "has_crossing", "crossing_closed", "has_switch", "switch_known", "switch_confirmed",
        "has_next", "has_stem", "entry_stem", "exit_stem", "entry_normal", "entry_reverse", "exit_normal", "exit_reverse",
        "reported_normal", "reported_reverse", "requested_normal", "switch_request_known", "zone_ok_in", "reserved_known", "reserved",
        "movement_locked", "crossing_needed_in", "active_route", "manual_crossing_close",
        // True only on the signal-clearance scan (never on the authorization scan); see TrackControllerService.PrepareSignals.
        "signal_clearance"
    ];
    public static readonly string[] WordInputs = ["suggested_speed", "suggested_authority", "distance_in", "remaining_in", "speed_in", "block_length", "track_limit"];
    private static readonly string[] BooleanOutputs =
    [
        "route_continue", "route_fault", "route_yellow", "route_signal", "allow_route", "signal_clear", "signal_yellow",
        "zone_ok", "switch_move", "switch_normal", "crossing_needed", "crossing_close", "closure_closed"
    ];
    private static readonly string[] WordOutputs = ["route_distance", "route_remaining", "route_speed", "authorized_speed", "authorized_authority"];
    public const int MaximumSourceBytes = 262_144;
    // Numeric PLC words use fixed point; bit zero is least significant.
    public const int Width = 48;
    public const double Scale = 1_000_000;
    private sealed record Port(string Name, int[] Slots);
    private readonly Port[] _inputs;
    private readonly Port[] _outputs;

    /// <summary>
    /// Every assignment of the program, in source order, as one compiled method: <c>values[slot] = gate expression</c>.
    /// The program is translated once, at load, from the same scalar AND/OR/NOT gates (no short-circuiting, no
    /// other operation), so each scan runs the program's gates directly instead of walking thousands of nested
    /// delegates.
    /// </summary>
    private readonly Action<bool[]> _gates;
    private readonly int _slotCount;

    private VitalPlcProgram(Port[] inputs, Port[] outputs, Action<bool[]> gates, int slotCount)
    {
        _inputs = inputs;
        _outputs = outputs;
        _gates = gates;
        _slotCount = slotCount;
    }

    /// <summary>Recently compiled programs by exact source text. A program is immutable, so waysides loading the same source share it.</summary>
    private static readonly Dictionary<string, VitalPlcProgram> Compiled = new(StringComparer.Ordinal);
    private const int CompiledCacheSize = 8;

    /// <summary>Accepts only scalar Boolean assignments. Every numeric channel bit must be explicitly assigned.</summary>
    public static VitalPlcProgram Compile(string source)
    {
        if (source is not null)
        {
            lock (Compiled)
            {
                if (Compiled.TryGetValue(source, out var cached))
                {
                    return cached;
                }
            }
        }

        // Invalid source throws here and is never cached.
        var program = CompileUncached(source!);
        lock (Compiled)
        {
            if (Compiled.Count >= CompiledCacheSize)
            {
                Compiled.Clear();
            }

            Compiled[source!] = program;
        }

        return program;
    }

    private static VitalPlcProgram CompileUncached(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || System.Text.Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes)
        {
            throw new ArgumentException("Boolean PLC source must contain 1–262,144 bytes.");
        }
        var known = new Dictionary<string, int>(StringComparer.Ordinal);
        var inputs = new List<Port>();
        foreach (string name in BooleanInputs.Concat(WordInputs))
        {
            bool single = BooleanInputs.Contains(name);
            var slots = new List<int>();
            for (int bit = 0; bit < (single ? 1 : Width); bit++)
            {
                string input = single ? name : $"{name}_{bit:00}";
                slots.Add(known.Count);
                known.Add(input, known.Count);
            }
            inputs.Add(new(name, slots.ToArray()));
        }
        var values = Expression.Parameter(typeof(bool[]), "values");
        var assignments = new List<Expression>();
        bool header = false;
        int lineNumber = 0;
        foreach (string raw in source.Replace("\r", "").Split('\n'))
        {
            lineNumber++;
            string line = raw.Split('#')[0].Trim().TrimEnd(';');
            if (line.Length == 0)
            {
                continue;
            }
            if (!header)
            {
                if (line != "plc boolean_v1")
                {
                    throw new ArgumentException("Use an explicit Boolean PLC beginning with 'plc boolean_v1'. Helper-function and permission-only programs are not accepted.");
                }
                header = true;
                continue;
            }
            if (assignments.Count >= 4096)
            {
                throw new ArgumentException("Boolean PLC programs are limited to 4,096 assignments.");
            }
            var match = Regex.Match(line, @"^([a-z][a-z0-9_]*)\s*=\s*(.+)$");
            if (!match.Success)
            {
                throw new ArgumentException($"PLC line {lineNumber}: expected name = Boolean expression.");
            }
            string name = match.Groups[1].Value;
            if (known.ContainsKey(name) || name is "true" or "false" or "and" or "or" or "not")
            {
                throw new ArgumentException($"PLC line {lineNumber}: duplicate or reserved name '{name}'.");
            }
            var expression = new Parser(match.Groups[2].Value, known, lineNumber, values).Parse();
            assignments.Add(Expression.Assign(Expression.ArrayAccess(values, Expression.Constant(known.Count)), expression));
            known.Add(name, known.Count);
        }
        var outputs = new List<Port>();
        foreach (string name in BooleanOutputs.Concat(WordOutputs))
        {
            bool single = BooleanOutputs.Contains(name);
            var slots = new List<int>();
            for (int bit = 0; bit < (single ? 1 : Width); bit++)
            {
                string output = single ? name : $"{name}_{bit:00}";
                if (!known.TryGetValue(output, out int slot))
                {
                    throw new ArgumentException($"Boolean PLC must explicitly assign {output}.");
                }
                slots.Add(slot);
            }
            outputs.Add(new(name, slots.ToArray()));
        }
        // An empty program has no gates; Block needs at least one expression.
        assignments.Add(Expression.Empty());
        var gates = Expression.Lambda<Action<bool[]>>(Expression.Block(assignments), values).Compile();
        return new(inputs.ToArray(), outputs.ToArray(), gates, known.Count);
    }

    /// <summary>Wires I/O bits into scalar Boolean slots, evaluates gates, and collects output bits without arithmetic.</summary>
    public IReadOnlyDictionary<string, bool[]> Execute(IReadOnlyDictionary<string, bool[]> inputs)
    {
        var values = new bool[_slotCount];
        foreach (var port in _inputs)
        {
            if (!inputs.TryGetValue(port.Name, out var bits) || bits is null || bits.Length != port.Slots.Length)
            {
                throw new ArgumentException($"Missing or incorrectly sized PLC input: {port.Name}.");
            }
            for (int bit = 0; bit < port.Slots.Length; bit++)
            {
                values[port.Slots[bit]] = bits[bit];
            }
        }
        _gates(values);
        var outputs = new Dictionary<string, bool[]>(_outputs.Length, StringComparer.Ordinal);
        foreach (var port in _outputs)
        {
            var bits = new bool[port.Slots.Length];
            for (int bit = 0; bit < bits.Length; bit++)
            {
                bits[bit] = values[port.Slots[bit]];
            }
            outputs.Add(port.Name, bits);
        }
        return outputs;
    }

    /// <summary>Encodes a nonnegative external SI value, rounding downward at one-millionth resolution.</summary>
    public static bool[] Encode(double value)
    {
        double scaled = value * Scale;
        if (!double.IsFinite(value) || value < 0 || scaled > (1UL << Width) - 1)
        {
            throw new ArgumentException("PLC fixed-point input is outside its 48-bit range.");
        }

        ulong units = (ulong)Math.Floor(scaled);
        if (units / Scale > value && units > 0)
        {
            units--;
        }
        var bits = new bool[Width];
        for (int bit = 0; bit < Width; bit++)
        {
            bits[bit] = (units & (1UL << bit)) != 0;
        }
        return bits;
    }

    /// <summary>Decodes PLC output bits into the existing external SI contract.</summary>
    public static double Decode(bool[] bits)
    {
        if (bits.Length != Width)
        {
            throw new ArgumentException("Expected a 48-bit PLC word.");
        }
        ulong units = 0;
        for (int bit = 0; bit < Width; bit++)
        {
            if (bits[bit])
            {
                units |= 1UL << bit;
            }
        }
        return units / Scale;
    }

    /// <summary>Translates one Boolean expression into the same gates over <paramref name="values"/>.</summary>
    private sealed class Parser(string source, Dictionary<string, int> known, int line, ParameterExpression values)
    {
        private int _position, _depth, _tokens;
        private string _token = "";

        public Expression Parse()
        {
            Next();
            var result = Or();
            Require(_token == "", "unexpected token; only Boolean expressions are supported");
            return result;
        }

        private void Next()
        {
            Require(++_tokens <= 512, "expression too long");
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
            if (character is '(' or ')')
            {
                _token = character.ToString();
                return;
            }
            Require(char.IsAsciiLetter(character), "only Boolean names, true/false, AND, OR, NOT and parentheses are allowed");
            int start = _position - 1;
            while (_position < source.Length && (char.IsAsciiLetterOrDigit(source[_position]) || source[_position] == '_'))
            {
                _position++;
            }
            _token = source[start.._position];
        }

        // Expression.Or/And on bool operands are the non-short-circuit gates | and &, as before.
        private Expression Or()
        {
            var left = And();
            while (_token == "or")
            {
                Next();
                left = Expression.Or(left, And());
            }
            return left;
        }

        private Expression And()
        {
            var left = Atom();
            while (_token == "and")
            {
                Next();
                left = Expression.And(left, Atom());
            }
            return left;
        }

        private Expression Atom()
        {
            Require(++_depth <= 32, "expression nesting exceeds 32");
            string name = _token;
            Next();
            Expression result;
            if (name == "not")
            {
                result = Expression.Not(Atom());
            }
            else if (name == "(")
            {
                result = Or();
                Require(_token == ")", "missing closing parenthesis");
                Next();
            }
            else if (name is "true" or "false")
            {
                result = Expression.Constant(name == "true");
            }
            else
            {
                Require(known.TryGetValue(name, out int slot), $"unknown Boolean '{name}'");
                result = Expression.ArrayIndex(values, Expression.Constant(slot));
            }
            _depth--;
            return result;
        }

        private void Require(bool condition, string reason)
        {
            if (!condition)
            {
                throw new ArgumentException($"PLC line {line}: {reason}.");
            }
        }
    }
}
