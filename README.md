# TrainControl System

`TrainControlSystem.sln` is the overall Visual Studio solution for the train
control / simulation project. CTC and Track Model now have working domain state,
WPF interfaces, and local named-pipe communication. Train Model, Train Controller,
and Track Controller remain architecture placeholders; train physics and hardware
communication are not implemented yet.

For the independent Track Model dashboard and external-module Test UI, see
[Track Model running and integration instructions](src/TrackModel/README.md).

For the live Track Model → Track Controller → CTC integration (shared block and train
identifiers, layout synchronization, live block status, movement requests and
acknowledgements, a step-by-step demonstration and the outstanding blockers), see the
[live integration guide](docs/integration/LIVE_INTEGRATION_GUIDE.md).

## The launcher

`TrainControlSystem.Launcher.Wpf` is the **entry point for the overall system**.

```
              TrainControlSystem.Launcher.Wpf
                          |
                          |  Process.Start
                          |
     +------------+-------+-------+------------+------------+
     |            |               |            |            |
     v            v               v            v            v
 TrainModel   TrackModel   TrainController TrackController  CTC
    .Wpf         .Wpf            .Wpf           .Wpf        .Wpf
     |            |               |              |           |
     v            v               v              v           v
   .Core        .Core           .Core          .Core       .Core
     |            |               |              |           |
     +------------+-------+-------+--------------+-----------+
                          |
                          v
            TrainControl.Contracts / Common
```

What this means:

- **The launcher is the entry point for the overall system**, and nothing more.
- **The launcher is not a train-control subsystem.** It contains no domain logic, no
  physics, no control algorithms. That is why it has no `Launcher.Core` project.
- **Subsystem WPF applications remain independent executables.** They are not
  `UserControl`s hosted inside the launcher window, and the launcher holds **no C#
  project reference** to any of them.
- **The launcher starts subsystem executables as separate OS processes**, using
  `System.Diagnostics.Process.Start`.
- **Subsystem communication happens through shared contracts** sent over local
  Windows named pipes: one newline-delimited JSON envelope (`messageType` + `payload`)
  per connection. The envelope format, transport and endpoint names live in
  `TrainControl.Common/Communication`. No networking, sockets, or message bus between
  Windows modules. The single exception is the Train Controller's link to its Raspberry
  Pi Hardware controller, which uses TCP/IP over the local network (see
  `src/TrainController/README.md`).
- **Process launching and subsystem communication are two separate concerns.**
  Starting an executable says nothing about how the running programs will talk to
  each other. Do not let the launcher grow into a message broker.
- **The launcher hosts the shared simulation clock** (and only that): the one
  authoritative clock for the whole system runs inside the launcher process. The
  launcher only *displays* it; the clock is controlled from the CTC Office. See
  [Simulation timing](#simulation-timing).

### How the launcher finds the module executables

Visual Studio builds every project into its own
`bin\{Configuration}\{TargetFramework}` folder, so the subsystem executables are not
siblings of the launcher executable. `ModuleLauncherService` resolves them at run time
by walking up from its own output folder to the directory containing
`TrainControlSystem.sln`, then reusing its own build-output suffix under each module's
project directory:

```
<repo root>\src\TrainModel\TrainModel.Wpf\bin\Debug\net10.0-windows\TrainModel.Wpf.exe
            └──────── from ModuleInfo ────────┘└─ same suffix the launcher ─┘
                                                  was built into
```

This needs no configuration file and keeps working across Debug/Release and target
framework changes.

**Limitations of this development-time approach:**

- It only works from a build tree. Build the **whole solution** at least once —
  building only the launcher leaves the other `bin` folders empty and *Launch* will
  report that the executable was not found.
- Launcher and modules must share a configuration. Running the launcher from `Debug`
  looks for modules in `Debug`.
- Only processes this launcher started are tracked. A module you started yourself
  from Visual Studio still shows as *Stopped*.
- Module status is tracked via the `Process.Exited` event, so a module that crashes
  correctly flips back to *Stopped*, but the launcher does not restart it.
- There is no deployment or install story yet; a published layout would need a
  different resolution strategy (the service falls back to looking next to itself).

## Simulation timing

The system has **one authoritative simulation clock**.

```text
                   clock control (SimulationTimeControlMessage)
CTC --------------------------------------------------------->
                                              Shared Clock
                                    (SimulationClockService, hosted
                                     by the Launcher process; pipe
                                     TrainControl.SimulationClock)
                                                   |
                                                   | SystemTimeMessage, every 100 ms
                                    +--------------+--------------+
                                    |              |              |
                                    v              v              v
                                   CTC        Train Model    Other modules
                                                              (subscribe later)
```

1. **CTC is the sole simulation-time authority from the user's perspective.** The CTC
   Office header has *Run*, *Pause*, speed *1x/2x/5x/10x*, *Set Time* and *Reset*.
2. **The shared clock is the single source of truth.** `SimulationClockService`
   (`TrainControl.Common/Timing`) owns `SystemTime`, `SpeedMultiplier` (1..10) and
   `IsPaused`. Exactly one instance runs, inside the Launcher
   (`Services/SimulationClockHost`). Modules are separate processes, so it is shared
   over named pipes, not as a DI singleton. A named mutex stops a second Launcher from
   running a second clock.
3. **CTC sends clock CONTROL requests**, `SimulationTimeControlMessage { Command = Run |
   Pause | SetSpeed | SetTime | Reset, SpeedMultiplier?, SystemTime? }`, to the
   `TrainControl.SimulationClock` pipe. A request is not an acknowledgement. CTC shows
   only the state the clock reports back, never the state it asked for. Invalid
   requests (e.g. 50x) are rejected and leave the clock unchanged.
4. **The shared clock sends `SystemTimeMessage { SystemTime, SpeedMultiplier, IsPaused,
   Sequence }`** to each subscribed module pipe, once per module, every 100 ms (and
   right after each control request). Each tick advances by the *measured* real time
   (Stopwatch) times the speed, so a late timer causes no drift. At 1x a tick is about
   +0.1 s of simulated time, at 5x about +0.5 s, at 10x about +1.0 s. A module that is not
   running only misses updates. The clock keeps advancing and the other modules keep
   receiving. Each module has at most one send in flight, so a stalled module never
   blocks the clock.
5. **Real modules consume time but never change it.** Each module applies the messages
   one at a time, in arrival order (CTC on its UI thread, Train Model on its UI thread).
6. **TestUIs may simulate time for isolated module testing** (CTC.TestUI's 1x clock,
   TrainModel.TestUI's test clock). They are test clock sources only. Do not run them
   alongside the Launcher, or a module would receive two clocks.
7. **The Train Model always keeps a fixed 0.1 s physics timestep** whatever the speed.
   It runs `newSystemTime - previousSystemTime` as N fixed steps (0.5 s elapsed is
   5 x `Step(0.1 s)`, at any speed) and carries any remainder to the next update
   (`TrainModel.Core/Simulation/SystemTimeStepper`).

Behaviour at the edges:

| Situation | Clock | CTC | Train Model |
| --------- | ----- | --- | ----------- |
| Startup | 12:00:00, 1x, **paused**, so nothing moves before track and schedule are loaded | Shows "Clock not connected" until the first state arrives | Trains hold still until the first time arrives |
| First message | - | Releases trains already due | Sets its baseline only (does not simulate from 00:00) |
| Paused | Time does not advance; state keeps being sent | Nothing becomes due | No physics steps |
| Time set backwards / Reset | Allowed | Never un-dispatches a train; pending trains wait | Rebases. No negative or reversed physics |
| Time set far forward (> 10 s jump) | Allowed | Releases every train it passed, in order | Rebases without simulating the gap |
| End of day | Stops at 23:59:59 and pauses (no rollover) | - | - |

**Running it:** start the Launcher, then launch CTC Office and Train Model from it (or
run them from Visual Studio while the Launcher is open). The Launcher window shows the
clock's state and whether CTC and Train Model are receiving it.

**Subscribing another module:** handle `SystemTimeMessage` in that module's pipe
receiver (apply it on one thread, in order, and never change it), then add the module's
pipe to the subscriber list in `SimulationClockHost`. Only CTC may send
`SimulationTimeControlMessage`. Current status:

- **Track Model** already applies `SystemTimeMessage` (hourly ticket sales). It is not
  subscribed yet because each accepted time currently triggers a full layout and
  block snapshot to every neighbour, which would flood the pipes at 10 Hz. The Track
  Model team should decouple time updates from snapshots, then subscribe it
  (optionally at a lower rate).
- **Train Controller** and **Track Controller** run their own internal test
  simulation loops with their own speed multipliers. Their receivers do not handle
  `SystemTimeMessage` yet. For integration they should follow system-time deltas the way
  the Train Model does, rather than keeping independent speed controls.

## Project types

| Suffix     | What it is                                                                   |
| ---------- | ---------------------------------------------------------------------------- |
| `.Core`    | Class library holding domain models, business logic, and simulation logic.    |
| `.Wpf`     | Runnable WPF desktop application — the UI for one subsystem, or the launcher. |

Rules the team should keep to:

- Each `.Wpf` project references its matching `.Core` project.
- **`.Core` projects never depend on WPF.** No `Window`, `UserControl`, XAML,
  `ViewModel`, or `ICommand` code belongs in a `.Core` project.
- `.Core` projects never reference `.Wpf` projects.
- Views and ViewModels live only in `.Wpf` projects.

## Shared projects

- **`TrainControl.Contracts`** — defines the data exchanged *between* subsystems:
  messages, shared enums, and cross-subsystem interfaces.
- **`TrainControl.Common`** — generic shared functionality (unit conversion, timing
  abstractions, validation helpers) that is not specific to any subsystem.

Subsystems communicate **through shared contracts**, not by referencing each other's
internal classes. For example, `TrainController.Core` does not reference
`TrainModel.Core`; it consumes a `TrainStateMessage` from `TrainControl.Contracts`
instead. This keeps the five subsystems independently developable by different team
members.

## Dependency direction

```
TrainModel.Wpf
      |
      v
TrainModel.Core
      |
      v
TrainControl.Contracts
```

The same shape holds for every subsystem. The architectural dependency is strictly
one-directional:

```
UI  ->  Core  ->  Contracts / Common
```

Nothing ever points back up the chain, and no arrow runs sideways between two
subsystems' `.Core` projects.

## Solution layout

```
TrainControlSystem
│
├── Shared
│   ├── TrainControl.Contracts
│   └── TrainControl.Common
│
├── Launcher
│   └── TrainControlSystem.Launcher.Wpf
│
├── TrainModel
│   ├── TrainModel.Core
│   └── TrainModel.Wpf
│
├── TrackModel
│   ├── TrackModel.Core
│   └── TrackModel.Wpf
│
├── TrainController
│   ├── TrainController.Abstractions
│   ├── TrainController.Core
│   ├── TrainController.Integration
│   ├── TrainController.Hardware.Pi   (runs on the Raspberry Pi)
│   └── TrainController.Wpf
│
├── TrackController
│   ├── TrackController.Core
│   └── TrackController.Wpf
│
├── CTC
│   ├── CTC.Core
│   ├── CTC.Wpf
│   └── CTC.TestUI.Wpf   (development-only external-module simulator)
│
└── Tests
    ├── TrainControl.Tests
    └── TrainController.Wpf.Tests   (Windows-only view-model tests)
```

Those are Visual Studio **solution folders**. On disk the projects live under
`/src/<Subsystem>/<Project>`, `/src/Launcher/TrainControlSystem.Launcher.Wpf`, and
`/tests/TrainControl.Tests`.

## Running the applications

### Set the launcher as the Startup Project

In Solution Explorer, right-click **TrainControlSystem.Launcher.Wpf** →
*Set as Startup Project*.

This is a per-developer Visual Studio setting stored in the `.vs` folder, which is
not committed, so **each team member needs to do this once** after cloning.

- **Pressing F5 with the launcher set as Startup Project** starts the central system
  launcher. From there you can start individual modules or all of them.
- **Each subsystem WPF project can still be set as the Startup Project and run by
  itself** — right-click it → *Set as Startup Project* → F5. Nothing about the
  launcher makes the subsystems dependent on it.
- **Visual Studio's Multiple Startup Projects feature** (right-click the solution →
  *Configure Startup Projects…* → *Multiple startup projects*) can also be used
  during integration and debugging, when you want the debugger attached to several
  subsystems at once. The launcher starts modules as plain processes, so modules
  started *through* the launcher are not debugged by your Visual Studio session.

Build the whole solution once before using the launcher, so that every module's
executable exists.

### From the command line

Each `.Wpf` project is a standalone executable and can be run independently:

```
dotnet run --project src/Launcher/TrainControlSystem.Launcher.Wpf
dotnet run --project src/TrainModel/TrainModel.Wpf
dotnet run --project src/TrackModel/TrackModel.Wpf
dotnet run --project src/TrainController/TrainController.Wpf
dotnet run --project src/TrackController/TrackController.Wpf
dotnet run --project src/CTC/CTC.Wpf
```

## Building and testing

```
dotnet build TrainControlSystem.sln
dotnet test  TrainControlSystem.sln
```

- Target framework: `net10.0` for libraries and tests, `net10.0-windows` for the
  WPF applications.
- Test framework: MSTest.

## MVVM conventions

Each WPF application follows the same minimal pattern:

- `MainWindow.xaml` is the one and only application window. (Exception:
  `TrainController.Wpf` has two peer windows, a Main UI and a Test UI — see
  `src/TrainController/README.md`.)
- `MainWindow` sets its `DataContext` to `MainWindowViewModel`; code-behind contains
  nothing else.
- `ViewModels/ViewModelBase.cs` provides `INotifyPropertyChanged`.
- `Commands/RelayCommand.cs` is a plain `ICommand` implementation.
- `Views/` is for future `UserControl`s, not for a second main window.
- No simulation or business logic in ViewModels — that belongs in `.Core`.

The launcher follows the same pattern: its `MainWindowViewModel` exposes an
`ObservableCollection<ModuleInfo>` plus `LaunchModuleCommand` and `LaunchAllCommand`,
and a read-only view of the shared clock. All process work lives in
`Services/ModuleLauncherService.cs` and the clock in `Services/SimulationClockHost.cs`,
**never** in `MainWindow.xaml.cs`.

`ViewModelBase` and `RelayCommand` are deliberately duplicated across the six WPF
projects rather than extracted into a shared presentation library. A few duplicate
lines are cheaper than another project for a team of this size.
