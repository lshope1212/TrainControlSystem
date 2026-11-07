# TrainControl.Common

Generic, reusable helpers that are **not specific to any subsystem**.

| Folder        | Purpose                                              |
| ------------- | ---------------------------------------------------- |
| `Utilities/`  | Small general-purpose helpers (unit conversion, ...). |
| `Timing/`     | The shared simulation clock (`SimulationClockService`) and its read-only / control interfaces. Hosted once, by the Launcher. |
| `Validation/` | Argument / input guard helpers.                       |
| `Communication/` | Inter-process message envelope, JSON serializer, local named-pipe transport and endpoint names. |

## Rules

- No WPF, no UI types.
- Nothing here may know about trains, track blocks, dispatching, or any other
  subsystem concept. If it does, it belongs in that subsystem's `.Core` project or
  in `TrainControl.Contracts`.
- `Timing/` knows nothing about messages or pipes: the Launcher's `SimulationClockHost`
  maps its state onto `SystemTimeMessage` and distributes it (see the root README,
  *Simulation timing*).
