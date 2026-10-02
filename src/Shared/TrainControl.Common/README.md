# TrainControl.Common

Generic, reusable helpers that are **not specific to any subsystem**.

| Folder        | Purpose                                              |
| ------------- | ---------------------------------------------------- |
| `Utilities/`  | Small general-purpose helpers (unit conversion, ...). |
| `Timing/`     | Shared timing abstractions for the simulation.        |
| `Validation/` | Argument / input guard helpers.                       |
| `Communication/` | Inter-process message envelope, JSON serializer, local named-pipe transport and endpoint names. |

## Rules

- No WPF, no UI types.
- Nothing here may know about trains, track blocks, dispatching, or any other
  subsystem concept. If it does, it belongs in that subsystem's `.Core` project or
  in `TrainControl.Contracts`.
- The current contents are placeholders showing where things go.
