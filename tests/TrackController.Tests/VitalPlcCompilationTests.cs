using TrackController.Core.Control;

namespace TrackController.Tests;

/// <summary>
/// The PLC is translated once into a single compiled method of its gates. These tests check it bit for bit against
/// an independent, deliberately naive interpreter of the same source text.
/// </summary>
[TestClass]
public sealed class VitalPlcCompilationTests
{
    private static string Source => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PiSamples", "wayside.plc"));

    /// <summary>Evaluates the program line by line from its text: names in a dictionary, recursive descent per expression.</summary>
    private static Dictionary<string, bool> Interpret(string source, IReadOnlyDictionary<string, bool[]> inputs)
    {
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (name, bits) in inputs)
        {
            if (bits.Length == 1) values[name] = bits[0];
            else for (int bit = 0; bit < bits.Length; bit++) values[$"{name}_{bit:00}"] = bits[bit];
        }

        foreach (string raw in source.Replace("\r", "").Split('\n'))
        {
            string line = raw.Split('#')[0].Trim().TrimEnd(';');
            if (line.Length == 0 || line == "plc boolean_v1") continue;
            int equals = line.IndexOf('=');
            var tokens = line[(equals + 1)..].Replace("(", " ( ").Replace(")", " ) ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int position = 0;
            bool Or()
            {
                bool result = And();
                while (position < tokens.Length && tokens[position] == "or") { position++; bool right = And(); result = result || right; }
                return result;
            }
            bool And()
            {
                bool result = Atom();
                while (position < tokens.Length && tokens[position] == "and") { position++; bool right = Atom(); result = result && right; }
                return result;
            }
            bool Atom()
            {
                string token = tokens[position++];
                switch (token)
                {
                    case "not": return !Atom();
                    case "(": { bool inner = Or(); Assert.AreEqual(")", tokens[position++]); return inner; }
                    case "true": return true;
                    case "false": return false;
                    default: return values[token];
                }
            }

            values[line[..equals].Trim()] = Or();
            Assert.AreEqual(tokens.Length, position, line);
        }

        return values;
    }

    private static Dictionary<string, bool[]> RandomInputs(Random random)
    {
        var inputs = VitalPlcProgram.BooleanInputs.ToDictionary(name => name, _ => new[] { random.Next(2) == 0 }, StringComparer.Ordinal);
        foreach (string name in VitalPlcProgram.WordInputs)
        {
            // Mostly realistic words (encoded SI values), sometimes raw random bits.
            inputs[name] = random.Next(4) == 0
                ? Enumerable.Range(0, VitalPlcProgram.Width).Select(_ => random.Next(2) == 0).ToArray()
                : VitalPlcProgram.Encode(random.NextDouble() * Math.Pow(10, random.Next(5)));
        }

        return inputs;
    }

    [TestMethod]
    public void CompiledProgram_MatchesIndependentInterpreter_OnEveryOutputBit()
    {
        string source = Source;
        var program = VitalPlcProgram.Compile(source);
        var random = new Random(20261112);
        for (int run = 0; run < 400; run++)
        {
            var inputs = RandomInputs(random);
            var reference = Interpret(source, inputs);
            foreach (var (port, bits) in program.Execute(inputs))
            {
                for (int bit = 0; bit < bits.Length; bit++)
                {
                    string name = bits.Length == 1 ? port : $"{port}_{bit:00}";
                    Assert.AreEqual(reference[name], bits[bit], $"run {run}: {name}");
                }
            }
        }
    }

    [TestMethod]
    public void Compile_SameSource_SharesTheImmutableProgram_AndOutputsAreFreshArrays()
    {
        var first = VitalPlcProgram.Compile(Source);
        Assert.AreSame(first, VitalPlcProgram.Compile(Source));
        var inputs = RandomInputs(new Random(7));
        var a = first.Execute(inputs);
        var b = first.Execute(inputs);
        foreach (var (port, bits) in a)
        {
            Assert.AreNotSame(bits, b[port], "Callers may keep output words; each scan returns its own arrays.");
            CollectionAssert.AreEqual(bits, b[port]);
        }
    }

    [TestMethod]
    public void Compile_InvalidSource_IsRejectedEveryTime()
    {
        string invalid = Source.Replace("crossing_ok = not has_crossing or crossing_closed", "crossing_ok = not has_crossing or crossing_closed or 2");
        Assert.ThrowsExactly<ArgumentException>(() => VitalPlcProgram.Compile(invalid));
        Assert.ThrowsExactly<ArgumentException>(() => VitalPlcProgram.Compile(invalid), "A rejected program is never cached.");
    }
}
