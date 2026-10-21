using System.Text.RegularExpressions;

namespace TrackController.Core.Control;

/// <summary>Strict scalar Boolean PLC interpreter. Only explicit AND, OR and NOT equations are accepted.</summary>
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
        "movement_locked", "crossing_needed_in", "active_route", "manual_crossing_close"
    ];
    public static readonly string[] WordInputs = ["suggested_speed", "suggested_authority", "distance_in", "remaining_in", "speed_in", "block_length", "track_limit"];
    private static readonly string[] BooleanOutputs =
    [
        "route_continue", "route_fault", "route_yellow", "route_signal", "allow_route", "signal_clear", "signal_yellow",
        "zone_ok", "switch_move", "switch_normal", "crossing_needed", "crossing_close", "closure_closed"
    ];
    private static readonly string[] WordOutputs = ["route_distance", "route_remaining", "route_speed", "authorized_speed", "authorized_authority"];
    public const int MaximumSourceBytes = 262_144;
    private sealed record Port(string Name, int[] Slots);
    private readonly Port[] _inputs;
    private readonly Port[] _outputs;
    private readonly (int Slot, Func<bool[], bool> Evaluate)[] _assignments;
    private readonly int _slotCount;

    private VitalPlcProgram(Port[] inputs, Port[] outputs, (int, Func<bool[], bool>)[] assignments, int slotCount)
    {
        _inputs = inputs;
        _outputs = outputs;
        _assignments = assignments;
        _slotCount = slotCount;
    }

    /// <summary>Accepts only scalar Boolean assignments. Every numeric channel bit must be explicitly assigned.</summary>
    public static VitalPlcProgram Compile(string source)
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
            for (int bit = 0; bit < (single ? 1 : PlcBits.Width); bit++)
            {
                string input = single ? name : $"{name}_{bit:00}";
                slots.Add(known.Count);
                known.Add(input, known.Count);
            }
            inputs.Add(new(name, slots.ToArray()));
        }
        var assignments = new List<(int, Func<bool[], bool>)>();
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
            var expression = new Parser(match.Groups[2].Value, known, lineNumber).Parse();
            assignments.Add((known.Count, expression));
            known.Add(name, known.Count);
        }
        var outputs = new List<Port>();
        foreach (string name in BooleanOutputs.Concat(WordOutputs))
        {
            bool single = BooleanOutputs.Contains(name);
            var slots = new List<int>();
            for (int bit = 0; bit < (single ? 1 : PlcBits.Width); bit++)
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
        return new(inputs.ToArray(), outputs.ToArray(), assignments.ToArray(), known.Count);
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
        foreach (var (slot, evaluate) in _assignments)
        {
            values[slot] = evaluate(values);
        }
        return _outputs.ToDictionary(port => port.Name, port => port.Slots.Select(slot => values[slot]).ToArray(), StringComparer.Ordinal);
    }

    private sealed class Parser(string source, Dictionary<string, int> known, int line)
    {
        private int _position, _depth, _tokens;
        private string _token = "";

        public Func<bool[], bool> Parse()
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

        private Func<bool[], bool> Or()
        {
            var left = And();
            while (_token == "or")
            {
                Next();
                var previous = left;
                var right = And();
                left = values => previous(values) | right(values);
            }
            return left;
        }

        private Func<bool[], bool> And()
        {
            var left = Atom();
            while (_token == "and")
            {
                Next();
                var previous = left;
                var right = Atom();
                left = values => previous(values) & right(values);
            }
            return left;
        }

        private Func<bool[], bool> Atom()
        {
            Require(++_depth <= 32, "expression nesting exceeds 32");
            string name = _token;
            Next();
            Func<bool[], bool> result;
            if (name == "not")
            {
                var inner = Atom();
                result = values => !inner(values);
            }
            else if (name == "(")
            {
                result = Or();
                Require(_token == ")", "missing closing parenthesis");
                Next();
            }
            else if (name is "true" or "false")
            {
                result = _ => name == "true";
            }
            else
            {
                Require(known.TryGetValue(name, out int slot), $"unknown Boolean '{name}'");
                result = values => values[slot];
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
