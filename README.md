# TrainControl System

`TrainControlSystem.sln` is the overall Visual Studio solution for the train
control / simulation project. Modules are being developed independently. The
Track Controller has a working WPF/MVVM implementation with route protection,
Boolean PLC execution, independent test inputs, and local named-pipe communication.
See [Track Controller setup, demo, and integration notes](docs/TrackController.md).
For assigned PC/Pi territories, the shared PLC program, Ethernet/SSH deployment,
and indication LEDs, see [Raspberry Pi 5 setup](docs/TrackController-Pi5.md).
For the shared track map, named ownership territories, and the PC/Pi execution editor,
see [Territory and execution setup](docs/TrackController-Territories.md).
Track Controllerâ€™s private PC/Pi/test message types live in `src/TrackController/TrackController.Contracts`;
its core/runtime tests live in `tests/TrackController.Tests`, with WPF integration
tests in `tests/TrackController.Wpf.Tests`. Team shared sources and the shared
test projectâ€™s target framework remain unchanged. Other modules continue to use
`TrainControl.Contracts`; movement and actuator integration remains incomplete.
Other modules still contain a mixture of implementations and placeholders.

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
  `TrainControl.Common/Communication`.
  Track Controller uses this same transport for live and test inputs and outputs;
  its internal hardware link routes assigned territories over TCP through SSH to
  the Pi. The other modules keep their local named-pipe interfaces.
- **Process launching and subsystem communication are two separate concerns.**
  Starting an executable says nothing about how the running programs will talk to
  each other. Do not let the launcher grow into a message broker.
- **Shared simulation timing will be implemented later.** The launcher's *System
  Time*, *Simulation Speed*, *Pause*, and *Resume* controls are placeholders, marked
  in code as future integration points. They currently affect nothing.

### How the launcher finds the module executables

Visual Studio builds every project into its own
`bin\{Configuration}\{TargetFramework}` folder, so the subsystem executables are not
siblings of the launcher executable. `ModuleLauncherService` resolves them at run time
by walking up from its own output folder to the directory containing
`TrainControlSystem.sln`, then reusing its own build-output suffix under each module's
project directory:

```
<repo root>\src\TrainModel\TrainModel.Wpf\bin\Debug\net10.0-windows\TrainModel.Wpf.exe
            â””â”€â”€â”€â”€â”€â”€â”€â”€ from ModuleInfo â”€â”€â”€â”€â”€â”€â”€â”€â”˜â””â”€ same suffix the launcher â”€â”˜
                                                  was built into
```

This needs no configuration file and keeps working across Debug/Release and target
framework changes.

**Limitations of this development-time approach:**

- It only works from a build tree. Build the **whole solution** at least once â€”
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

## Project types

| Suffix     | What it is                                                                   |
| ---------- | ---------------------------------------------------------------------------- |
| `.Core`    | Class library holding domain models, business logic, and simulation logic.    |
| `.Wpf`     | Runnable WPF desktop application â€” the UI for one subsystem, or the launcher. |

Rules the team should keep to:

- Each `.Wpf` project references its matching `.Core` project.
- **`.Core` projects never depend on WPF.** No `Window`, `UserControl`, XAML,
  `ViewModel`, or `ICommand` code belongs in a `.Core` project.
- `.Core` projects never reference `.Wpf` projects.
- Views and ViewModels live only in `.Wpf` projects.

## Shared projects

- **`TrainControl.Contracts`** â€” defines the data exchanged *between* subsystems:
  messages, shared enums, and cross-subsystem interfaces.
- **`TrainControl.Common`** â€” generic shared functionality (unit conversion, timing
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
â”‚
â”œâ”€â”€ Shared
â”‚   â”œâ”€â”€ TrainControl.Contracts
â”‚   â””â”€â”€ TrainControl.Common
â”‚
â”œâ”€â”€ Launcher
â”‚   â””â”€â”€ TrainControlSystem.Launcher.Wpf
â”‚
â”œâ”€â”€ TrainModel
â”‚   â”œâ”€â”€ TrainModel.Core
â”‚   â””â”€â”€ TrainModel.Wpf
â”‚
â”œâ”€â”€ TrackModel
â”‚   â”œâ”€â”€ TrackModel.Core
â”‚   â””â”€â”€ TrackModel.Wpf
â”‚
â”œâ”€â”€ TrainController
â”‚   â”œâ”€â”€ TrainController.Core
â”‚   â””â”€â”€ TrainController.Wpf
â”‚
â”œâ”€â”€ TrackController
â”‚   â”œâ”€â”€ TrackController.Contracts
â”‚   â”œâ”€â”€ TrackController.Core
â”‚   â”œâ”€â”€ TrackController.Runtime
â”‚   â”œâ”€â”€ TrackController.Pi
â”‚   â””â”€â”€ TrackController.Wpf
â”‚
â”œâ”€â”€ CTC
â”‚   â”œâ”€â”€ CTC.Core
â”‚   â”œâ”€â”€ CTC.Wpf
â”‚   â””â”€â”€ CTC.TestUI.Wpf   (development-only external-module simulator)
â”‚
â””â”€â”€ Tests
    â”œâ”€â”€ TrainControl.Tests
    â””â”€â”€ TrackController.Tests
```

Those are Visual Studio **solution folders**. On disk the projects live under
`/src/<Subsystem>/<Project>`, `/src/Launcher/TrainControlSystem.Launcher.Wpf`, and
`/tests/<TestProject>`.

## Running the applications

### Set the launcher as the Startup Project

In Solution Explorer, right-click **TrainControlSystem.Launcher.Wpf** â†’
*Set as Startup Project*.

This is a per-developer Visual Studio setting stored in the `.vs` folder, which is
not committed, so **each team member needs to do this once** after cloning.

- **Pressing F5 with the launcher set as Startup Project** starts the central system
  launcher. From there you can start individual modules or all of them.
- **Each subsystem WPF project can still be set as the Startup Project and run by
  itself** â€” right-click it â†’ *Set as Startup Project* â†’ F5. Nothing about the
  launcher makes the subsystems dependent on it.
- **Visual Studio's Multiple Startup Projects feature** (right-click the solution â†’
  *Configure Startup Projectsâ€¦* â†’ *Multiple startup projects*) can also be used
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
  WPF applications and `TrackController.Wpf.Tests`, which exercises WPF commands.
- Test framework: MSTest.

## MVVM conventions

Each WPF application follows the same minimal pattern:

- `MainWindow.xaml` is the one and only application window.
- `MainWindow` sets its `DataContext` to `MainWindowViewModel`; code-behind contains
  nothing else.
- `ViewModels/ViewModelBase.cs` provides `INotifyPropertyChanged`.
- `Commands/RelayCommand.cs` is a plain `ICommand` implementation.
- `Views/` is for future `UserControl`s, not for a second main window.
- No simulation or business logic in ViewModels â€” that belongs in `.Core`.

The launcher follows the same pattern: its `MainWindowViewModel` exposes an
`ObservableCollection<ModuleInfo>` plus `LaunchModuleCommand`, `LaunchAllCommand`,
`PauseCommand`, and `ResumeCommand`. All process work lives in
`Services/ModuleLauncherService.cs` â€” **never** in `MainWindow.xaml.cs`.

`ViewModelBase` and `RelayCommand` are deliberately duplicated across the six WPF
projects rather than extracted into a shared presentation library. A few duplicate
lines are cheaper than another project for a team of this size.
