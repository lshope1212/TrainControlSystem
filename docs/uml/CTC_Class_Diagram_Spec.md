# CTC Module Class Diagram: Specification

Diagram name in StarUML: **CTC Module Class Diagram**

Companion files (all three describe the **same** design and were generated from one model definition):

| File | Purpose |
|---|---|
| `CTC_Class_Diagram_Spec.md` | This document: what to draw and why |
| `CTC_Class_Diagram.puml` | PlantUML reference rendering |
| `CTC_Class_Diagram.mdj` | StarUML 7 project containing the finished diagram (opened and rendered with StarUML 7.1.1's CLI to confirm it loads) |

Source inspected: branch `Luke_CTC`, the `src/CTC/CTC.Core` and `src/CTC/CTC.Wpf` projects, and `src/Shared/TrainControl.Contracts`.

---

## 1. Conventions used in this diagram

- **Visibility:** `+` public, `-` private, `#` protected. **Static** members are underlined in StarUML and marked `{static}` in PlantUML.
- **`/name`** is a derived (computed, get-only) property, for example `ScheduledTrain./DepartureTime`.
- **`[0..1]`** on an attribute means a C# nullable (`TimeSpan?`, `string?`, `T?`).
- **Async signatures** leave out the trailing `cancellationToken : CancellationToken = default` parameter that every `…Async` method in `ICTCService`/`CTCService` has. `CtcNamedPipeReceiver.RunAsync` keeps it because there it is the only parameter.
- **`«static»` classes** are C# `static class`es: stateless planners called directly. The service **depends on** them («use»). It does not own or hold them.
- **`«event»`** marks a C# event.
- **`«create»`** on an operation marks a constructor.
- **Object references drawn as lines.** When one class in the diagram refers to another class in the diagram, the reference is drawn as an association, aggregation or composition. It is not also listed as an attribute. The role name on the line is the C# property or field name.
- **Exception: references from ViewModels to domain objects.** References such as `MainWindowViewModel.SelectedLine : CtcLineState[0..1]` are left as typed attributes. Drawing them as lines would only add long cross-layer lines.
- **Logical keys are not associations.** `DispatchedTrainState.CurrentBlockId`, `ActiveRouteBlockIds`, `MovementPlanStop.BlockId`, `ScheduledRouteBlock.BlockId` and `DispatchQueueEntry.TrainId` are **string IDs**, not object references. These IDs are resolved through `CtcSystemState.FindBlock`/`FindDispatchedTrain`. They are shown as attributes, with a note, and **no** line is drawn. Drawing one would invent a structural link that the code does not have.

---

## 2. Included classes (25 production + 8 external)

### 2.1 Core service (`CTC.Core`)

#### ICTCService
- **Kind:** «interface»
- **Namespace / project:** `CTC.Core.Interfaces` / `CTC.Core`
- **Responsibility:** Boundary of the CTC office. Inbound methods map shared-contract messages into CTC state; dispatcher actions are sent to the Track Controller through IMessageSender. Raises StateChanged after every change so presentation layers can refresh without CTC.Core knowing about UI.
- **Attributes:**
  ```text
  +State: CtcSystemState {readOnly}
  +«event» StateChanged: EventHandler<CtcStateChangedEventArgs>
  ```
- **Operations:**
  ```text
  +ApplyTrackLayout(message: TrackLayoutMessage)
  +ApplyBlockStatus(message: BlockStatusMessage)
  +ApplyTicketSales(message: TicketSalesMessage)
  +SetSystemTimeAsync(systemTime: TimeSpan): Task
  +QueueSchedule(scheduledTrains: IEnumerable<ScheduledTrain>)
  +CreateMaintenanceRequest(blockId: string, requestedState: MaintenanceState): MaintenanceRequestMessage
  +CreateSwitchPositionRequest(blockId: string, requestedPosition: SwitchPosition): SwitchPositionRequestMessage
  +CreateMovementRequest(trainId: string): MovementRequestMessage
  +CloseBlockAsync(blockId: string): Task
  +ReopenBlockAsync(blockId: string): Task
  +SetSwitchPositionAsync(blockId: string, requestedPosition: SwitchPosition): Task
  +UpdateDispatchedTrainPlanAsync(trainId: string, destinationBlockId: string, targetArrivalTime: TimeSpan): Task<string?>
  +UpdateMovementPlanStopAsync(trainId: string, stopIndex: int, destinationBlockId: string, arrivalTime: TimeSpan, dwellTime: TimeSpan): Task<string?>
  ```

#### CTCService
- **Kind:** class
- **Namespace / project:** `CTC.Core.Services` / `CTC.Core`
- **Responsibility:** The CTC office service and the one implementation of ICTCService. Owns the CtcSystemState, validates and queues schedules, dispatches trains when SystemTime reaches their departure time, re-plans released trains, and delegates routing, speed, authority, movement-plan timing and location inference to the stateless planners. Knows nothing about the transport: every outbound message goes through the injected IMessageSender.
- **Attributes:**
  ```text
  -_movementsInFlight: HashSet<string>
  ```
- **Operations:**
  ```text
  «create» +CTCService(messageSender: IMessageSender)
  +ApplyTrackLayout(message: TrackLayoutMessage)
  +ApplyBlockStatus(message: BlockStatusMessage)
  +ApplyTicketSales(message: TicketSalesMessage)
  +SetSystemTimeAsync(systemTime: TimeSpan): Task
  +QueueSchedule(scheduledTrains: IEnumerable<ScheduledTrain>)
  +CloseBlockAsync(blockId: string): Task
  +ReopenBlockAsync(blockId: string): Task
  +SetSwitchPositionAsync(blockId: string, requestedPosition: SwitchPosition): Task
  +UpdateMovementPlanStopAsync(trainId: string, stopIndex: int, destinationBlockId: string, arrivalTime: TimeSpan, dwellTime: TimeSpan): Task<string?>
  -DispatchTrainAsync(entry: DispatchQueueEntry): Task<string?>
  -AdvanceMovementPlansAsync(): Task
  -StartNextMovementAsync(train: DispatchedTrainState): Task<string>
  ```

#### IMessageSender
- **Kind:** «interface»
- **Namespace / project:** `CTC.Core.Interfaces` / `CTC.Core`
- **Responsibility:** Outbound port of CTC.Core. CTC.Core decides WHAT to send; the implementation (in CTC.Wpf) decides HOW it is transported. Completion means the message was handed to the transport, not that the receiver acted on it.
- **Attributes:** none shown
- **Operations:**
  ```text
  +SendAsync<TMessage>(message: TMessage): Task
  ```

### 2.2 Infrastructure / module communication (`CTC.Wpf.Services`)

#### CtcNamedPipeReceiver
- **Kind:** class (sealed)
- **Namespace / project:** `CTC.Wpf.Services` / `CTC.Wpf`
- **Responsibility:** CTC's inbound endpoint. Hosts the CTC named-pipe server, deserializes each envelope into its shared-contract message and calls the matching ICTCService method on the WPF UI thread. Contains no CTC behavior of its own.
- **Attributes:**
  ```text
  -_dispatcher: Dispatcher
  +«event» StatusReported: EventHandler<string>
  ```
- **Operations:**
  ```text
  «create» +CtcNamedPipeReceiver(ctc: ICTCService, dispatcher: Dispatcher)
  +RunAsync(cancellationToken: CancellationToken): Task
  -HandleAsync(envelope: MessageEnvelope): Task
  ```

#### NamedPipeMessageSender
- **Kind:** class (sealed)
- **Namespace / project:** `CTC.Wpf.Services` / `CTC.Wpf`
- **Responsibility:** IMessageSender implementation that sends each outbound message to the Track Controller pipe as a JSON envelope, translating transport failures into MessageSendException.
- **Attributes:** none shown
- **Operations:**
  ```text
  +SendAsync<TMessage>(message: TMessage): Task
  ```

### 2.3 Presentation (`CTC.Wpf.ViewModels`)

#### ViewModelBase
- **Kind:** abstract class
- **Namespace / project:** `CTC.Wpf.ViewModels` / `CTC.Wpf`
- **Responsibility:** Minimal INotifyPropertyChanged base class for the CTC view models.
- **Attributes:**
  ```text
  +«event» PropertyChanged: PropertyChangedEventHandler
  ```
- **Operations:**
  ```text
  #OnPropertyChanged(propertyName: string?)
  #SetProperty<T>(field: ref T, value: T, propertyName: string?): bool
  ```
- *Note:* `ViewModelBase` implements .NET's `INotifyPropertyChanged`. The .NET interface is not drawn. You can add it as a stereotype note if wanted.

#### MainWindowViewModel
- **Kind:** class
- **Namespace / project:** `CTC.Wpf.ViewModels` / `CTC.Wpf`
- **Responsibility:** Dispatcher's main screen. Observes ICTCService.StateChanged and rebuilds its block, dispatch-queue and dispatched-train lists; exposes commands for maintenance closures, switch changes and Active Movement Plan edits, which it forwards to ICTCService.
- **Attributes:**
  ```text
  +SelectedLine: CtcLineState[0..1]
  +Blocks: ObservableCollection<BlockViewModel>
  +DispatchQueue: ObservableCollection<DispatchQueueEntryViewModel>
  +DispatchedTrains: ObservableCollection<DispatchedTrainViewModel>
  +IsMaintenanceMode: bool
  +SystemTimeDisplay: string
  +CloseSelectedBlockCommand: ICommand
  +ReopenSelectedBlockCommand: ICommand
  +ToggleSelectedBlockSwitchCommand: ICommand
  +ApplyTrainPlanCommand: ICommand
  ```
- **Operations:**
  ```text
  «create» +MainWindowViewModel(ctc: ICTCService)
  -OnCtcStateChanged(sender: object?, e: CtcStateChangedEventArgs)
  -ApplyTrainPlanAsync(): Task
  -CloseSelectedBlockAsync(): Task
  ```

#### ScheduleBuilderViewModel
- **Kind:** class
- **Namespace / project:** `CTC.Wpf.ViewModels` / `CTC.Wpf`
- **Responsibility:** Schedule Builder tab. Generates a ScheduleTemplate for the selected line (or imports one from CSV), lets the dispatcher fill in times and dwell, converts it to ScheduledTrain objects and hands them to ICTCService.QueueSchedule.
- **Attributes:**
  ```text
  +Line: CtcLineState[0..1]
  +NumberOfTrainsText: string
  +StatusMessage: string
  +Rows: ObservableCollection<ScheduleTemplateRowViewModel>
  +GenerateTemplateCommand: ICommand
  +UploadScheduleCommand: ICommand
  +QueueScheduleCommand: ICommand
  +ClearTemplateCommand: ICommand
  ```
- **Operations:**
  ```text
  «create» +ScheduleBuilderViewModel(ctc: ICTCService)
  -GenerateTemplate()
  -UploadSchedule()
  -QueueSchedule()
  ```

### 2.4 Domain models (`CTC.Core.Models`)

#### CtcSystemState
- **Kind:** class
- **Namespace / project:** `CTC.Core.Models` / `CTC.Core`
- **Responsibility:** Aggregate root of everything the CTC office currently knows: track lines, the stored schedule, the pending dispatch queue, the dispatched trains and the current simulation time.
- **Attributes:**
  ```text
  +SystemTime: TimeSpan
  ```
- **Operations:**
  ```text
  +FindLine(lineId: string): CtcLineState?
  +FindBlock(blockId: string): CtcBlockState?
  +FindDispatchedTrain(trainId: string): DispatchedTrainState?
  ```

#### CtcLineState
- **Kind:** class
- **Namespace / project:** `CTC.Core.Models` / `CTC.Core`
- **Responsibility:** CTC's view of one transit line and its blocks.
- **Attributes:**
  ```text
  +LineId: string
  +Name: string
  +TicketSalesPerHour: int
  ```
- **Operations:** none shown

#### CtcBlockState
- **Kind:** class
- **Namespace / project:** `CTC.Core.Models` / `CTC.Core`
- **Responsibility:** CTC's view of one block: static layout from the Track Model (length, speed limit, station, connections) plus the latest wayside status reported by the Track Controller and CTC's own maintenance request.
- **Attributes:**
  ```text
  +BlockId: string
  +LengthMeters: double
  +SpeedLimitKilometersPerHour: double
  +StationName: string
  +HasSwitch: bool
  +ConnectedBlockIds: IList<string>
  +Occupancy: OccupancyState
  +Signal: SignalState
  +Switch: SwitchPosition
  +ConfirmedMaintenanceState: MaintenanceState
  +RequestedMaintenanceState: MaintenanceState
  ```
- **Operations:** none shown

#### ScheduledTrain
- **Kind:** class
- **Namespace / project:** `CTC.Core.Models` / `CTC.Core`
- **Responsibility:** One train's normalized schedule on one line: its ordered route and the times it enters the timed blocks. Never changed after queuing; the live plan lives in DispatchedTrainState.
- **Attributes:**
  ```text
  +TrainId: string
  +LineId: string
  +/StartBlockId: string
  +/DepartureTime: TimeSpan
  ```
- **Operations:** none shown

#### ScheduledRouteBlock
- **Kind:** class
- **Namespace / project:** `CTC.Core.Models` / `CTC.Core`
- **Responsibility:** One block on a scheduled route. A timed block (ArrivalTime set) is a stop/waypoint and carries a dwell time; an untimed block is only routed through.
- **Attributes:**
  ```text
  +BlockId: string
  +ArrivalTime: TimeSpan[0..1]
  +DwellTime: TimeSpan
  +/IsTimed: bool
  +/DepartureTime: TimeSpan[0..1]
  ```
- **Operations:** none shown

#### DispatchQueueEntry
- **Kind:** class
- **Namespace / project:** `CTC.Core.Models` / `CTC.Core`
- **Responsibility:** A scheduled train waiting for the system time to reach its departure time. Leaves the queue when its release succeeds.
- **Attributes:**
  ```text
  +TrainId: string
  +LineId: string
  +DepartureTime: TimeSpan
  +QueueStatus: DispatchQueueStatus
  ```
- **Operations:** none shown

#### DispatchedTrainState
- **Kind:** class
- **Namespace / project:** `CTC.Core.Models` / `CTC.Core`
- **Responsibility:** CTC's view of a released train: the suggestion last sent for it, the block CTC infers it occupies, its active route and its Active Movement Plan (remaining stops).
- **Attributes:**
  ```text
  +TrainId: string
  +LineId: string
  +SuggestedSpeedMetersPerSecond: double
  +SuggestedAuthorityMeters: double
  +CurrentBlockId: string
  +ActiveRouteBlockIds: IList<string>
  +ActiveRouteIndex: int
  +CurrentBlockOccupancyObserved: bool
  +ActualArrivalTime: TimeSpan[0..1]
  +IsLegSuggestionPending: bool
  +/CurrentStop: MovementPlanStop[0..1]
  +/ReadyToDepartTime: TimeSpan[0..1]
  ```
- **Operations:** none shown

#### MovementPlanStop
- **Kind:** class
- **Namespace / project:** `CTC.Core.Models` / `CTC.Core`
- **Responsibility:** One remaining stop of an Active Movement Plan: ARRIVE, dwell, DEPART, travel to the next stop. RouteIndex points into the owning train's ActiveRouteBlockIds.
- **Attributes:**
  ```text
  +BlockId: string
  +ArrivalTime: TimeSpan
  +DwellTime: TimeSpan
  +/DepartureTime: TimeSpan
  +RouteIndex: int
  +PlannedSpeedMetersPerSecond: double
  +TimingWarning: string[0..1]
  ```
- **Operations:** none shown

#### TrainMovementPhase
- **Kind:** «enumeration»
- **Namespace / project:** `CTC.Core.Models` / `CTC.Core`
- **Responsibility:** Where a dispatched train is relative to its current stop. Driven only by occupancy (arrival) and SystemTime updates (dwell elapsing).
- **Literals:** `EnRoute`, `Arrived`, `Dwelling`, `Completed`

### 2.5 Domain logic: Dispatching (`CTC.Core.Dispatching`)

#### MovementPlanner
- **Kind:** class «static»
- **Namespace / project:** `CTC.Core.Dispatching` / `CTC.Core`
- **Responsibility:** Active Movement Plan timing: builds the stops from a schedule, validates stop timing (arrival + dwell), re-plans the speeds of later stops, and moves a train through EnRoute/Dwelling/Arrived/Completed.
- **Attributes:** none shown
- **Operations:**
  ```text
  +CreateStops(train: ScheduledTrain): List<MovementPlanStop>   (static, underlined)
  +ValidateStopTiming(trainId: string, stops: IReadOnlyList<MovementPlanStop>): string?   (static, underlined)
  +ResolveRoute(train: DispatchedTrainState, line: CtcLineState, start: int, end: int): List<CtcBlockState>?   (static, underlined)
  +RecalculatePlannedSpeeds(train: DispatchedTrainState, line: CtcLineState, systemTime: TimeSpan): IReadOnlyList<string>   (static, underlined)
  +ApplyTrainAdvanced(train: DispatchedTrainState, systemTime: TimeSpan): bool   (static, underlined)
  +CompleteDwellIfDue(train: DispatchedTrainState, systemTime: TimeSpan): bool   (static, underlined)
  ```

#### SpeedPlanner
- **Kind:** class «static»
- **Namespace / project:** `CTC.Core.Dispatching` / `CTC.Core`
- **Responsibility:** Suggested-speed calculation: the speed that reaches a target block on time, capped by the block speed limits and the train performance limit; also rejects schedules that cannot be run at permitted speed.
- **Attributes:** none shown
- **Operations:**
  ```text
  +GetMaxPermittedSpeedMetersPerSecond(blocks: IEnumerable<CtcBlockState>): double   (static, underlined)
  +ValidateFeasibility(train: ScheduledTrain, route: IReadOnlyList<CtcBlockState>): string?   (static, underlined)
  +CalculateInitialSpeed(train: ScheduledTrain, route: IReadOnlyList<CtcBlockState>, systemTime: TimeSpan): SpeedSuggestion   (static, underlined)
  +CalculateSpeedToTarget(route: IReadOnlyList<CtcBlockState>, targetArrivalTime: TimeSpan, systemTime: TimeSpan): SpeedSuggestion   (static, underlined)
  +CalculateLegSpeed(route: IReadOnlyList<CtcBlockState>, departureTime: TimeSpan, targetArrivalTime: TimeSpan, systemTime: TimeSpan): SpeedSuggestion   (static, underlined)
  ```

#### AuthorityManager
- **Kind:** class «static»
- **Namespace / project:** `CTC.Core.Dispatching` / `CTC.Core`
- **Responsibility:** Authority calculation: the distance along a route the train may travel before the first unsafe (occupied or closed) block.
- **Attributes:** none shown
- **Operations:**
  ```text
  +IsUnsafe(block: CtcBlockState): bool   (static, underlined)
  +CalculateInitialAuthorityMeters(route: IEnumerable<CtcBlockState>): double   (static, underlined)
  +CalculateUpdatedAuthorityMeters(route: IEnumerable<CtcBlockState>): double   (static, underlined)
  ```

#### RouteManager
- **Kind:** class «static»
- **Namespace / project:** `CTC.Core.Dispatching` / `CTC.Core`
- **Responsibility:** Route calculation: shortest path between two blocks of a line over the blocks' ConnectedBlockIds graph.
- **Attributes:** none shown
- **Operations:**
  ```text
  +FindRoute(line: CtcLineState, startBlockId: string, destinationBlockId: string): IReadOnlyList<CtcBlockState>?   (static, underlined)
  ```

### 2.6 Domain logic: Tracking (`CTC.Core.Tracking`)

#### TrainLocationTracker
- **Kind:** class «static»
- **Namespace / project:** `CTC.Core.Tracking` / `CTC.Core`
- **Responsibility:** Infers dispatched-train locations from anonymous, sequential block occupancy: when a block becomes occupied that is the next active-route block of exactly one dispatched train, that train advances into it; if several trains could explain it, none is moved.
- **Attributes:** none shown
- **Operations:**
  ```text
  +ApplyOccupancy(state: CtcSystemState, block: CtcBlockState, previousOccupancy: OccupancyState): TrainLocationResult   (static, underlined)
  +NextBlockId(train: DispatchedTrainState): string?   (static, underlined)
  ```

### 2.7 Domain logic: Scheduling (`CTC.Core.Scheduling`)

#### DwellTimePolicy
- **Kind:** class «static»
- **Namespace / project:** `CTC.Core.Scheduling` / `CTC.Core`
- **Responsibility:** Dwell-time rules: 45 s default for a block with a station, 0 s otherwise (decided from CtcBlockState.StationName), plus validation, parsing and formatting of dispatcher-entered dwell seconds.
- **Attributes:**
  ```text
  +StationDwellTime: TimeSpan = 45 s {readOnly}   (static, underlined)
  +NonStationDwellTime: TimeSpan = 0 s {readOnly}   (static, underlined)
  ```
- **Operations:**
  ```text
  +GetDefaultDwellTime(block: CtcBlockState): TimeSpan   (static, underlined)
  +GetDefaultDwellTime(line: CtcLineState, blockId: string?): TimeSpan   (static, underlined)
  +Validate(dwellTime: TimeSpan): string?   (static, underlined)
  +TryParseSeconds(text: string?, dwellTime: out TimeSpan): string?   (static, underlined)
  +FormatSeconds(dwellTime: TimeSpan): string   (static, underlined)
  ```

#### ScheduleTemplate
- **Kind:** class (sealed)
- **Namespace / project:** `CTC.Core.Scheduling` / `CTC.Core`
- **Responsibility:** The editable schedule grid for one line: one row per block (with its station, infrastructure and default dwell) and one time/dwell column per train. Built by ScheduleTemplateFactory or ScheduleCsvImporter.
- **Attributes:**
  ```text
  +LineId: string {readOnly}
  +TrainIds: IReadOnlyList<string> {readOnly}
  +Rows: IReadOnlyList<ScheduleTemplateRow> {readOnly}
  ```
- **Operations:**
  ```text
  «create» +ScheduleTemplate(lineId: string, trainIds: IEnumerable<string>, rows: IEnumerable<ScheduleTemplateRow>)
  ```

#### ScheduleTemplateConverter
- **Kind:** class «static»
- **Namespace / project:** `CTC.Core.Scheduling` / `CTC.Core`
- **Responsibility:** Converts a filled-in ScheduleTemplate into normalized ScheduledTrain objects, routing between the timed blocks and carrying each stop's arrival and dwell time.
- **Attributes:**
  ```text
  +TimeFormat: string = "HH:mm:ss" {readOnly}   (static, underlined)
  ```
- **Operations:**
  ```text
  +Convert(template: ScheduleTemplate): ScheduleConversionResult   (static, underlined)
  +TryParseTime(text: string?, time: out TimeSpan): bool   (static, underlined)
  ```

### 2.8 External boundary: `«external» TrainControl.Contracts.Messages`

These boxes are grey and sit inside one `«external»` package frame. Six of them show only their name. `MovementSuggestionMessage` and `MovementRequestMessage` also show their fields, because they are the two messages that carry CTC's dispatch decisions. The CTC classes connect to the **package frame** with dashed dependencies, not to each message, which keeps the boundary readable.

#### TrackLayoutMessage
- **Kind:** class
- **Namespace / project:** `TrainControl.Contracts.Messages` / `TrainControl.Contracts`
- **Responsibility:** Inbound (Track Model): lines and block definitions.
- **Attributes:** none shown
- **Operations:** none shown

#### BlockStatusMessage
- **Kind:** class
- **Namespace / project:** `TrainControl.Contracts.Messages` / `TrainControl.Contracts`
- **Responsibility:** Inbound (Track Controller): occupancy, signal, switch, crossing and maintenance of one block. Never names a train.
- **Attributes:** none shown
- **Operations:** none shown

#### TicketSalesMessage
- **Kind:** class
- **Namespace / project:** `TrainControl.Contracts.Messages` / `TrainControl.Contracts`
- **Responsibility:** Inbound: ticket sales per hour for a line.
- **Attributes:** none shown
- **Operations:** none shown

#### SystemTimeMessage
- **Kind:** class
- **Namespace / project:** `TrainControl.Contracts.Messages` / `TrainControl.Contracts`
- **Responsibility:** Inbound: current simulation time (drives dispatching and dwell).
- **Attributes:** none shown
- **Operations:** none shown

#### MovementSuggestionMessage
- **Kind:** class
- **Namespace / project:** `TrainControl.Contracts.Messages` / `TrainControl.Contracts`
- **Responsibility:** Outbound (to Track Controller): suggested speed and authority for a train.
- **Attributes:**
  ```text
  +TrainId: string
  +SuggestedSpeedMetersPerSecond: double
  +SuggestedAuthorityMeters: double
  ```
- **Operations:** none shown

#### MovementRequestMessage
- **Kind:** class
- **Namespace / project:** `TrainControl.Contracts.Messages` / `TrainControl.Contracts`
- **Responsibility:** Outbound (to Track Controller): request to release a train.
- **Attributes:**
  ```text
  +TrainId: string
  +RequestType: MovementRequestType
  ```
- **Operations:** none shown

#### MaintenanceRequestMessage
- **Kind:** class
- **Namespace / project:** `TrainControl.Contracts.Messages` / `TrainControl.Contracts`
- **Responsibility:** Outbound: close/reopen a block for maintenance.
- **Attributes:** none shown
- **Operations:** none shown

#### SwitchPositionRequestMessage
- **Kind:** class
- **Namespace / project:** `TrainControl.Contracts.Messages` / `TrainControl.Contracts`
- **Responsibility:** Outbound: move a switch.
- **Attributes:** none shown
- **Operations:** none shown

---

## 3. Relationships to draw

Directed associations are drawn **from** the class that holds the reference **to** the referenced class. Navigability points at the target. For compositions and aggregations, the **diamond goes on the Source (whole) end**. The *role* is the name of the Target end.

In StarUML, draw each relationship from Source to Target with these tools:

| Relationship | StarUML tool |
|---|---|
| Interface realization | **Interface Realization** |
| Generalization | **Generalization** |
| Directed association | **Directed Association** |
| Composition | **Composition** |
| Aggregation | **Aggregation** |
| Dependency | **Dependency**, with the stereotype entered in its *stereotype* field |

| # | Source | Relationship | Target | Source Mult. | Target Mult. | Label / role | Reason (from code) |
|---|---|---|---|---|---|---|---|
| R1 | CTCService | Interface realization | ICTCService |  |  |  | `public class CTCService : ICTCService` |
| R2 | NamedPipeMessageSender | Interface realization | IMessageSender |  |  |  | `public sealed class NamedPipeMessageSender : IMessageSender` |
| R3 | MainWindowViewModel | Generalization | ViewModelBase |  |  |  | `public class MainWindowViewModel : ViewModelBase` |
| R4 | ScheduleBuilderViewModel | Generalization | ViewModelBase |  |  |  | `public class ScheduleBuilderViewModel : ViewModelBase` |
| R5 | CTCService | Directed association | IMessageSender |  | 1 | -_messageSender | readonly field injected through the constructor (non-null); the only path for outbound messages |
| R6 | CTCService | Composition | CtcSystemState | 1 | 1 | +State | `State { get; } = new CtcSystemState()`: created with the service, never replaced, lives exactly as long as it |
| R7 | CtcSystemState | Composition | CtcLineState | 1 | * | +Lines {ordered} | get-only list; lines are created inside ApplyTrackLayout and discarded by Lines.Clear() on the next layout |
| R8 | CtcLineState | Composition | CtcBlockState | 1 | * | +Blocks {ordered} | blocks are created together with their line in ApplyTrackLayout and destroyed with it |
| R9 | CtcSystemState | Aggregation (shared) | ScheduledTrain | 1 | * | +ScheduledTrains | trains are created OUTSIDE the state (ScheduleTemplateConverter) and handed in by QueueSchedule; the state stores but did not create them |
| R10 | CtcSystemState | Composition | DispatchQueueEntry | 1 | * | +DispatchQueue {ordered} | entries are created by QueueSchedule, ordered by departure time, and removed when the train is released |
| R11 | CtcSystemState | Composition | DispatchedTrainState | 1 | * | +DispatchedTrains | created by CTCService when a release succeeds; exists only inside the state |
| R12 | ScheduledTrain | Composition | ScheduledRouteBlock | 1 | * | +Route {ordered} | get-only list; route blocks are created together with their train (ScheduleTemplateConverter) and have no meaning without it |
| R13 | DispatchedTrainState | Composition | MovementPlanStop | 1 | * | +Stops {ordered} | the Active Movement Plan; stops are created for this train and removed (RemoveAt(0)) when it departs them |
| R14 | DispatchedTrainState | Directed association | TrainMovementPhase |  | 1 | +Phase | enum-typed property |
| R15 | MainWindowViewModel | Directed association | ICTCService |  | 1 | -_ctc | readonly field from the constructor; subscribes to StateChanged and calls dispatcher actions |
| R16 | ScheduleBuilderViewModel | Directed association | ICTCService |  | 1 | -_ctc | readonly field from the constructor; calls QueueSchedule and reads State |
| R17 | MainWindowViewModel | Composition | ScheduleBuilderViewModel | 1 | 1 | +ScheduleBuilder | created in the MainWindowViewModel constructor and exposed get-only |
| R18 | ScheduleBuilderViewModel | Directed association | ScheduleTemplate |  | 0..1 | -_template | nullable field holding the template currently being edited |
| R19 | ScheduleBuilderViewModel | Dependency | ScheduleTemplateConverter |  |  | «use» | QueueSchedule() calls ScheduleTemplateConverter.Convert |
| R20 | CtcNamedPipeReceiver | Directed association | ICTCService |  | 1 | -_ctc | readonly field from the constructor; every inbound message is forwarded to it |
| R21 | CTCService | Dependency | MovementPlanner |  |  | «use» | CreateStops, RecalculatePlannedSpeeds, ApplyTrainAdvanced, CompleteDwellIfDue, ValidateStopTiming |
| R22 | CTCService | Dependency | SpeedPlanner |  |  | «use» | CalculateInitialSpeed, CalculateSpeedToTarget, CalculateLegSpeed, ValidateFeasibility |
| R23 | CTCService | Dependency | AuthorityManager |  |  | «use» | CalculateInitialAuthorityMeters, CalculateUpdatedAuthorityMeters |
| R24 | CTCService | Dependency | RouteManager |  |  | «use» | FindRoute when the dispatcher changes a destination |
| R25 | CTCService | Dependency | TrainLocationTracker |  |  | «use» | ApplyBlockStatus calls ApplyOccupancy |
| R26 | CTCService | Dependency | DwellTimePolicy |  |  | «use» | default dwell and dwell validation when a plan is edited |
| R27 | MovementPlanner | Dependency | SpeedPlanner |  |  | «use» | RecalculatePlannedSpeeds calls CalculateSpeedToTarget/CalculateLegSpeed |
| R28 | MovementPlanner | Dependency | MovementPlanStop |  |  | «create» | CreateStops builds the stops from the schedule's timed blocks |
| R29 | MovementPlanner | Dependency | DispatchedTrainState |  |  | «update» | changes Phase, ActualArrivalTime and Stops |
| R30 | SpeedPlanner | Dependency | CtcBlockState |  |  |  | reads LengthMeters and SpeedLimitKilometersPerHour of route blocks |
| R31 | AuthorityManager | Dependency | CtcBlockState |  |  |  | reads length, occupancy and maintenance state of route blocks |
| R32 | RouteManager | Dependency | CtcLineState |  |  |  | searches the line's Blocks/ConnectedBlockIds graph |
| R33 | TrainLocationTracker | Dependency | DispatchedTrainState |  |  | «update» | advances CurrentBlockId / ActiveRouteIndex |
| R34 | TrainLocationTracker | Dependency | CtcBlockState |  |  |  | the newly occupied block and its previous occupancy |
| R35 | DwellTimePolicy | Dependency | CtcBlockState |  |  |  | default dwell is decided from StationName |
| R36 | ScheduleTemplateConverter | Dependency | ScheduleTemplate |  |  |  | Convert reads the template rows and train columns |
| R37 | ScheduleTemplateConverter | Dependency | ScheduledTrain |  |  | «create» | creates ScheduledTrain and its ScheduledRouteBlocks |
| R38 | CtcNamedPipeReceiver | Dependency | package `TrainControl.Contracts.Messages` |  |  | «deserialize» | turns each MessageEnvelope into the matching inbound contract message |
| R39 | ICTCService | Dependency | package `TrainControl.Contracts.Messages` |  |  | «use» | inbound parameters and Create* return types are contract messages |
| R40 | CTCService | Dependency | package `TrainControl.Contracts.Messages` |  |  | «send» | builds MovementSuggestion/MovementRequest/MaintenanceRequest/SwitchPositionRequest messages and passes them to IMessageSender |

### 3.1 Relationships deliberately **not** drawn

| Not drawn | Why |
|---|---|
| `DispatchedTrainState` → `CtcBlockState` (CurrentBlockId / ActiveRouteBlockIds) | String keys resolved through `CtcSystemState.FindBlock`. There is no object reference (see the note on the diagram). |
| `MovementPlanStop` / `ScheduledRouteBlock` → `CtcBlockState` | Same reason (`BlockId : string`). |
| `DispatchQueueEntry` → `ScheduledTrain` | Matched by `TrainId` + `LineId`. There is no reference. |
| `CTCService` → `DispatchedTrainState` / `DispatchQueueEntry` / `MovementPlanStop` «create» | CTCService does create these. They are reached through its `State` composition, and extra «create» lines would duplicate that path. |
| `MovementPlanner` → `DwellTimePolicy` «use» | Real (`Validate`) but minor. It would cross the whole diagram. It is listed here so nothing is hidden. |
| `ScheduleTemplate` → `DwellTimePolicy` «use» | Real (dwell parsing/formatting in `ScheduleTemplateRow`), minor. |
| `SpeedPlanner` → `TrainPerformance` | `TrainPerformance` (70 km/h train limit constant) is omitted. See §5. |
| `MainWindowViewModel`/`ScheduleBuilderViewModel` → `CtcLineState` | Shown as typed attributes (`SelectedLine`, `Line`) instead (see §1). |
| `App` → everything | `App.OnStartup` is the composition root that wires `NamedPipeMessageSender` → `CTCService` → `MainWindowViewModel` / `CtcNamedPipeReceiver`. Drawing its creation dependencies would clutter the diagram. It is described in §6. |

---

## 4. Layout in StarUML

The canvas is roughly 3,650 × 1,950 px. The coordinates below are what `CTC_Class_Diagram.mdj` uses (top-left corner of each box). Widths and heights are approximate, because StarUML sizes boxes to fit their text.

```text
 x: 30 ─────────── ~1000 │ 1080 ─────── ~1990 │ 2090 ─────── ~3050 │ 3030 ──── ~3620
 ┌───────────────────────┐ ┌──────────────────┐                        ┌──────────────────┐
 │ CTC.Wpf.ViewModels    │ │ «interface»      │ ◄─────────────────────┐│«external»        │
 │   ViewModelBase       │◄│   ICTCService    │ ──────«use»──────────►││TrainControl.     │
 │   △         △         │ └────────△─────────┘                       ││Contracts.Messages│
 │ MainWindow  Schedule  │          ┆ realizes                        │└────────▲─────────┘
 │ ViewModel ◆ Builder VM│ ┌────────┴─────────┐   «interface»          │┌────────┴─────────┐
 └───────────────────────┘ │    CTCService    │──►IMessageSender◄┄┄┄┄┄┄┤│ CTC.Wpf.Services │
 ┌───────────────────────┐ └──┬────────┬──────┘                        └┤ CtcNamedPipeRecv │
 │ CTC.Core.Scheduling   │    ◆ State  ┆ «use»   ┌──────────────────┐   │ NamedPipeMsgSend │
 │  ScheduleTemplate     │    │        └┄┄┄┄┄┄┄┄►│CTC.Core.Dispatch.│   └──────────────────┘
 │  Converter  DwellPol. │    │                  │ MovementPlanner  │
 └───────────────────────┘    │                  │ SpeedPlanner     │
 ┌────────────────────────────┴──────────────┐   │ AuthorityManager │
 │ CTC.Core.Models          CtcSystemState    │   │ RouteManager     │
 │ ScheduledTrain DispatchQueue DispatchedTr. │   └──────────────────┘
 │ ScheduledRouteBlk  Phase  MovementPlanStop │   ┌──────────────────┐
 │                    CtcLineState CtcBlock   │   │CTC.Core.Tracking │
 └────────────────────────────────────────────┘   └──────────────────┘
```

Why the groups are placed like this:

- **Center column.** `ICTCService` sits above `CTCService`, so the realization arrow is a short vertical line. `CTCService` is the visual hub: its «use» lines fan out right to the planners, and its `State` composition goes straight down into the models.
- **Presentation, top-left.** Both ViewModels sit next to `ICTCService`, so their `_ctc` associations are short and point right. `ViewModelBase` is above both, so both generalization triangles point up.
- **Scheduling, left, under Presentation.** This is a deliberate change from "logic on the right". Its two clients are `ScheduleBuilderViewModel` (directly above) and `ScheduledTrain` (directly below, in the models). Putting it on the right would make both lines cross the whole diagram.
- **Dispatching and Tracking, right.** These sit next to `CtcBlockState`, `CtcLineState` and `DispatchedTrainState`, which are placed at the right side of the models frame for that reason.
- **Infrastructure and Contracts, far right.** `CtcNamedPipeReceiver` sits just right of the core, slightly lower than `ICTCService`, so its `_ctc` line runs up-left through empty space. `NamedPipeMessageSender` is exactly level with `IMessageSender` (y = 760), so their realization is a horizontal line. Together they reproduce the flow *External → Receiver → ICTCService ◁┈ CTCService → IMessageSender ◁┈ Sender → External* as static relationships. The contracts frame sits directly above the receiver.
- **Models.** The `CtcSystemState` aggregate is at the top of the models frame. Its parts are in the row below: schedule (left), queue, dispatched trains (middle), track (right). The sub-parts are one row lower: `ScheduledRouteBlock`, `MovementPlanStop`, `CtcBlockState`.

Exact coordinates:

| Element | Group | Left | Top | Approx. width | Approx. height |
|---|---|---|---|---|---|
| package frame `CTC.Wpf.ViewModels` | Presentation | 30 | 40 | 926 | 535 |
| package frame `CTC.Wpf.Services` | Infrastructure / module communication | 3030 | 510 | 462 | 340 |
| package frame `CTC.Core.Models` | Domain models | 30 | 1160 | 1950 | 780 |
| package frame `CTC.Core.Dispatching` | Domain logic: dispatching | 2090 | 910 | 963 | 705 |
| package frame `CTC.Core.Tracking` | Domain logic: tracking | 2090 | 1670 | 774 | 170 |
| package frame `CTC.Core.Scheduling` | Domain logic: scheduling | 30 | 640 | 971 | 415 |
| package frame `TrainControl.Contracts.Messages` | External boundary | 3030 | 40 | 584 | 425 |
| ICTCService | Core service | 1080 | 60 | 903 | 300 |
| CTCService | Core service | 1080 | 470 | 903 | 270 |
| IMessageSender | Core service | 2200 | 760 | 296 | 90 |
| CtcNamedPipeReceiver | Infrastructure / module communication | 3060 | 560 | 402 | 120 |
| NamedPipeMessageSender | Infrastructure / module communication | 3060 | 760 | 296 | 60 |
| ViewModelBase | Presentation | 330 | 90 | 431 | 90 |
| MainWindowViewModel | Presentation | 60 | 290 | 414 | 255 |
| ScheduleBuilderViewModel | Presentation | 560 | 290 | 366 | 225 |
| CtcSystemState | Domain models | 1240 | 1210 | 384 | 105 |
| CtcLineState | Domain models | 1660 | 1440 | 172 | 90 |
| CtcBlockState | Domain models | 1660 | 1600 | 290 | 210 |
| ScheduledTrain | Domain models | 60 | 1440 | 178 | 105 |
| ScheduledRouteBlock | Domain models | 60 | 1640 | 213 | 120 |
| DispatchQueueEntry | Domain models | 420 | 1440 | 225 | 105 |
| DispatchedTrainState | Domain models | 1100 | 1440 | 254 | 225 |
| MovementPlanStop | Domain models | 1100 | 1760 | 242 | 150 |
| TrainMovementPhase | Domain models | 820 | 1500 | 174 | 120 |
| MovementPlanner | Domain logic: dispatching | 2120 | 960 | 732 | 150 |
| SpeedPlanner | Domain logic: dispatching | 2120 | 1160 | 903 | 135 |
| AuthorityManager | Domain logic: dispatching | 2120 | 1350 | 473 | 105 |
| RouteManager | Domain logic: dispatching | 2120 | 1510 | 685 | 75 |
| TrainLocationTracker | Domain logic: tracking | 2120 | 1720 | 714 | 90 |
| DwellTimePolicy | Domain logic: scheduling | 540 | 860 | 431 | 165 |
| ScheduleTemplate | Domain logic: scheduling | 60 | 690 | 644 | 105 |
| ScheduleTemplateConverter | Domain logic: scheduling | 60 | 860 | 396 | 105 |
| TrackLayoutMessage | External boundary | 3060 | 90 | 174 | 25 |
| BlockStatusMessage | External boundary | 3060 | 160 | 174 | 25 |
| TicketSalesMessage | External boundary | 3060 | 230 | 174 | 25 |
| SystemTimeMessage | External boundary | 3060 | 300 | 166 | 25 |
| MovementSuggestionMessage | External boundary | 3330 | 90 | 254 | 90 |
| MovementRequestMessage | External boundary | 3330 | 230 | 225 | 75 |
| MaintenanceRequestMessage | External boundary | 3330 | 340 | 230 | 25 |
| SwitchPositionRequestMessage | External boundary | 3330 | 410 | 254 | 25 |

---

## 5. Intentionally omitted

| Omitted | Reason |
|---|---|
| `CTC.TestUI.Wpf` (entire project: `FakeTrackControllerReceiver`, `NamedPipeCtcMessageSender`, `ICtcMessageSender`, its ViewModels, `SampleTrackLayout`) | Development-only stand-in for the Track Model, Track Controller and clock. It talks to CTC only through the same named pipes and contract messages that the real modules will use. That is why the receiver/sender pair plus the `«external» TrainControl.Contracts` frame is the whole boundary. TestUI is not part of the production architecture. |
| `RelayCommand`, `AsyncRelayCommand` | Generic `ICommand` helpers. The commands appear only as `ICommand` properties. |
| `DataGridTrainColumns` (WPF behavior), XAML code-behind (`MainWindow`) | View plumbing |
| `MessageSendException`, `DispatchException` | Exceptions (named in the operation docs only) |
| `BlockViewModel`, `DispatchedTrainViewModel`, `MovementPlanStopViewModel`, `DispatchQueueEntryViewModel`, `ScheduleTemplateRowViewModel`, `ScheduleTimeCellViewModel` | Thin row wrappers over the domain objects. Their *types* still appear in `MainWindowViewModel`/`ScheduleBuilderViewModel` collection attributes. `BlockViewModel` and `DispatchedTrainViewModel` derive from `ViewModelBase`. |
| `CtcStateChangedEventArgs`, `CtcStateChangeKind` | Event payload. Appears as the type of `ICTCService.StateChanged`. |
| `DispatchQueueStatus` | Small enum (`Queued`, `Dispatching`). Appears as an attribute type. |
| `TrainIds` | Static helper for formatting and allocating train IDs |
| `TrainPerformance` | One constant (70 km/h). It is described in `SpeedPlanner`'s responsibility. |
| `SpeedSuggestion`, `TrainLocationResult`, `ScheduleConversionResult`, `ScheduleCsvImportResult` | Result records. They appear as return types. |
| `ScheduleTemplateFactory`, `ScheduleCsvImporter`, `ScheduleTemplateRow` | Two more producers of `ScheduleTemplate` and its row type. `ScheduleTemplateFactory.Create` is where each row's default dwell comes from (`DwellTimePolicy.GetDefaultDwellTime(block)`). If you have room, add `ScheduleTemplateFactory «static»` next to `ScheduleTemplate` with `«create»` → `ScheduleTemplate` and «use» → `DwellTimePolicy`. |
| `App` | Composition root (see §6) |
| Contract enums (`OccupancyState`, `SignalState`, `SwitchPosition`, `CrossingState`, `MaintenanceState`) and the other ~20 contract messages | Appear only as attribute or parameter types |
| `TrainControl.Common` (`NamedPipeTransport`, `MessageSerializer`, `MessageEnvelope`) | Transport details used inside the receiver and sender |

---

## 6. How the pieces are wired (context for the diagram)

`CTC.Wpf.App.OnStartup` builds exactly one object graph:

```text
IMessageSender sender   = new NamedPipeMessageSender();
ICTCService    ctc      = new CTCService(sender);
var viewModel           = new MainWindowViewModel(ctc);         // creates ScheduleBuilderViewModel(ctc)
var receiver            = new CtcNamedPipeReceiver(ctc, Dispatcher);
```

So the single `CTCService` is shared by the dispatcher UI and by the inbound pipe. Both depend only on `ICTCService`. `CTCService` depends only on `IMessageSender`. This dependency inversion is what the realization and association lines in the center of the diagram show.

What the diagram communicates:

- **Route calculation.** `RouteManager.FindRoute` searches the `ConnectedBlockIds` graph of a `CtcLineState`.
- **Authority calculation.** `AuthorityManager` measures the route up to the first occupied or closed `CtcBlockState`.
- **Suggested-speed calculation.** `SpeedPlanner` works out the speed that reaches a target block on time, capped by block speed limits.
- **Movement-plan generation and re-planning.** `MovementPlanner` creates `MovementPlanStop`s from a `ScheduledTrain`, validates arrival + dwell, re-plans later speeds, and moves `TrainMovementPhase` along.
- **Dwell.** `ScheduledRouteBlock.DwellTime` → `MovementPlanStop.DwellTime`. Defaults come from `DwellTimePolicy` using `CtcBlockState.StationName` (45 s at a station, 0 s elsewhere).
- **Location inference.** `CTCService.ApplyBlockStatus` → `TrainLocationTracker.ApplyOccupancy` advances a `DispatchedTrainState` when the next block of its active route becomes occupied. `BlockStatusMessage` never identifies a train, and there is no train-position message.
