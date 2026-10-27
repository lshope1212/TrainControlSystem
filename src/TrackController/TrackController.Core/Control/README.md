# Explicit Boolean PLC

The program begins with `plc boolean_v1`. Every input, intermediate and output in the program is a single Boolean. The only expression operators are `and`, `or`, and `not`, with parentheses and `true`/`false` constants. There are no functions, vectors, numeric literals, arithmetic operators, comparisons or implicit generated gates in the loader.

All comparison, selection, addition and subtraction equations are written explicitly in `TrackController.Wpf/Samples/wayside.plc`. For example, each speed-output bit uses AND/OR selection between two input bits. The comparison signal itself is the result of the preceding per-bit Boolean equations. The source contains 1,184 explicit assignments.

Outside the PLC, the C# I/O adapter encodes numeric contracts into individually named bits such as `track_limit_00` through `track_limit_47`, then decodes output bits such as `authorized_speed_00` through `authorized_speed_47`. Bit 00 is least significant. The boundary representation uses millionths of SI units and rounds downward. Grouping wires into arrays in the I/O adapter is transport bookkeeping; the interpreter evaluates only scalar Boolean gates. No C# comparator, selector or adder helper executes PLC control logic.

The PLC owns route interlocking, speed selection, authority accumulation, signal selection, switch protection, crossing control and closure completion. The host supplies measured facts, timestamp freshness and topology identities. Route-scan state and protection-zone state are explicitly wired between scans. Independent C# protection may reject unsafe PLC outputs; it does not supply normal positive control decisions. The advanced standalone Software logic comparison mode remains separate.

The sample is longer because all gates are visible. Policy statements such as `allow_route`, `signal_clear`, `switch_move` and `crossing_close` remain readable named Boolean equations. For demonstration, the replacement hold program changes these policy equations while retaining the full explicit gate network.

Limits: 256 KiB UTF-8 source, 4,096 assignments, 512 tokens per expression, 32 nested expressions. Inputs cannot be overwritten; duplicate names, missing output bits, forward references, helper calls and non-Boolean expressions are rejected. Old permission-only and `vital_bits_v1` files are rejected. Use the updated files and matching protocol-v6 Windows/Pi binaries.

This is a tested course simulation, not a certified vital controller.
