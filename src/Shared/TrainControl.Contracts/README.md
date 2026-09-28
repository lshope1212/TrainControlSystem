# TrainControl.Contracts

Defines the **data and interfaces exchanged between subsystems**. This is the only
project that every other project is allowed to depend on for inter-module
communication.

| Folder        | Purpose                                                                 |
| ------------- | ----------------------------------------------------------------------- |
| `Messages/`   | Plain data objects passed across subsystem boundaries.                   |
| `Enums/`      | Shared vocabulary (signal aspects, switch positions, ...).               |
| `Interfaces/` | Contracts a subsystem implements so others can use it without coupling.  |

## Rules

- No WPF, no UI types.
- No subsystem-specific logic — if only one subsystem needs it, it belongs in that
  subsystem's `.Core` project.
- Keep message types as simple data carriers.

## Why this exists

`TrainController.Core` must never reference `TrainModel.Core` directly. When the two
need to exchange information, the shape of that information is defined here and both
sides depend on this project instead of on each other.
