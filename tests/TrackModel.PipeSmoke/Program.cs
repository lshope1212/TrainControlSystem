using System.Collections.Concurrent;
using System.Threading.Channels;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;
using TrainControl.Contracts.Enums;

var values=new ConcurrentDictionary<string,object>();
var replies=Channel.CreateUnbounded<TrackModelInputResultMessage>();
using var stop=new CancellationTokenSource();
var endpoints=new[]{NamedPipeNames.TrackController,NamedPipeNames.TrainModel,NamedPipeNames.TrainController,NamedPipeNames.Ctc,NamedPipeNames.TrackModelTestUi};
var tasks=endpoints.Select(pipe=>NamedPipeTransport.ListenAsync(pipe,envelope=>
{
    switch(envelope.MessageType)
    {
        case nameof(TrackLayoutMessage): values["layout"]=MessageSerializer.DeserializePayload<TrackLayoutMessage>(envelope);break;
        case nameof(TrackModelBlockStateMessage): var s=MessageSerializer.DeserializePayload<TrackModelBlockStateMessage>(envelope);values["state:"+s.BlockId]=s;break;
        case nameof(TrackModelTrainEnvironmentMessage): var e=MessageSerializer.DeserializePayload<TrackModelTrainEnvironmentMessage>(envelope);values["env:"+e.BlockId]=e;break;
        case nameof(TrackModelSignalMessage): var l=MessageSerializer.DeserializePayload<TrackModelSignalMessage>(envelope);values["signal:"+l.BlockId]=l;break;
        case nameof(TicketSalesMessage): values["tickets"]=MessageSerializer.DeserializePayload<TicketSalesMessage>(envelope);break;
        case nameof(SystemTimeMessage): values["time"]=MessageSerializer.DeserializePayload<SystemTimeMessage>(envelope);break;
        case nameof(TrackModelInputResultMessage): replies.Writer.TryWrite(MessageSerializer.DeserializePayload<TrackModelInputResultMessage>(envelope));break;
    }
    return Task.CompletedTask;
},error=>Console.WriteLine("Transport error: "+error.Message),stop.Token)).ToArray();
T? Get<T>(string key) where T:class => values.TryGetValue(key,out var v)?v as T:null;
TrackModelBlockStateMessage? State(string id)=>Get<TrackModelBlockStateMessage>("state:"+id);
TrackModelTrainEnvironmentMessage? Env(string id)=>Get<TrackModelTrainEnvironmentMessage>("env:"+id);
var checks=0;
async Task Check(string label,Func<bool> predicate)
{
    var deadline=DateTime.UtcNow.AddSeconds(8);
    while(!predicate())
    {
        if(DateTime.UtcNow>deadline)throw new Exception("FAILED: "+label);
        await Task.Delay(50);
    }
    checks++;Console.WriteLine("PASS: "+label);
}
async Task Send(object message,bool accepted=true)
{
    await NamedPipeTransport.SendAsync(NamedPipeNames.TrackModel,message);
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var reply=await replies.Reader.ReadAsync(timeout.Token);
    if(reply.MessageType!=message.GetType().Name || reply.Accepted!=accepted)
        throw new Exception("Unexpected acknowledgement: "+reply.MessageType+" "+reply.Detail);
}
try
{
    await Send(new TrackModelSnapshotRequestMessage());
    await Check("full 15-block layout received by CTC",()=>Get<TrackLayoutMessage>("layout")?.Lines.Single().Blocks.Count==15);
    await Check("clean Blue Line profile (click Restore Blue Line before running)",()=>Env("1")?.TrainId=="01"&&Env("10")?.TrainId==""&&Env("10")?.TicketsSold==0);
    await Check("beacons on 9 and 14, no station beacon on 10",()=>Env("9")?.Beacon=="Station B"&&Env("14")?.Beacon=="Station C"&&Env("10")?.Beacon=="");
    await Send(new TrackModelCommandMessage{BlockId="5",CommandedSpeedMetersPerSecond=20*0.44704,AuthorityMeters=600*0.3048,Switch=SwitchPosition.Reverse});
    await Check("command speed/authority and reverse switch reach separate outputs",()=>Env("5")?.NextBlockId=="11"&&Env("5")?.AuthorityMeters==600*0.3048&&State("5")?.Switch==SwitchPosition.Reverse);
    foreach(var signal in new[]{SignalState.Green,SignalState.Yellow,SignalState.Red})
    {
        await Send(new TrackModelCommandMessage{BlockId="6",Signal=signal});
        await Check(signal+" reaches Track Controller, Train Model, Train Controller",()=>State("6")?.Signal==signal&&Env("6")?.Signal==signal&&Get<TrackModelSignalMessage>("signal:6")?.Signal==signal);
    }
    await Send(new TrackModelCommandMessage{BlockId="3",Crossing=CrossingState.Closed});
    await Check("crossing closed output",()=>State("3")?.Crossing==CrossingState.Closed);
    await Send(new TrackModelPassengerDemandMessage{BlockId="10",WaitingPassengers=7});
    await Check("demand changes waiting without selling tickets",()=>Env("10")?.WaitingPassengers==7&&Env("10")?.TicketsSold==0);
    var exchange=new TrackModelTrainUpdateMessage{TrainId="01",CurrentBlockId="10",BoardingPassengers=3,DisembarkingPassengers=2,ExchangeId=Guid.NewGuid().ToString()};
    await Send(exchange);
    await Check("train move and passenger exchange update occupancy, demand, totals and CTC",()=>State("1")?.Occupancy==OccupancyState.Clear&&State("10")?.Occupancy==OccupancyState.Occupied&&Env("10")?.WaitingPassengers==4&&Env("10")?.BoardingPassengers==3&&Env("10")?.DisembarkingPassengers==2&&Env("10")?.TicketsSold==3&&Get<TicketSalesMessage>("tickets")?.TicketsPerHour==3);
    await Send(exchange);
    var before=Get<TrackLayoutMessage>("layout")!.SnapshotId;
    await Send(new TrackModelSnapshotRequestMessage());
    await Check("retry and fresh snapshot preserve one-shot counts",()=>Get<TrackLayoutMessage>("layout")!.SnapshotId!=before&&Env("10")?.SnapshotId==Get<TrackLayoutMessage>("layout")!.SnapshotId&&Env("10")?.TicketsSold==3&&Env("10")?.WaitingPassengers==4);
    await Send(new TrackModelFailureCommandMessage{BlockId="10",BrokenRail=true,TrackCircuitFailure=true,PowerFailure=true});
    await Check("all failures reach wayside, physical train remains, heater off",()=>State("10") is {BrokenRail:true,TrackCircuitFailure:true,PowerFailure:true,Occupancy:OccupancyState.Unknown}&&Env("10") is {TrainId:"01",HeaterOn:false,Signal:SignalState.Unknown});
    await Send(new TrackModelFailureCommandMessage{BlockId="10"});
    await Send(new TrackModelTemperatureCommandMessage{BlockId="3",TemperatureCelsius=0});
    await Check("freezing temperature enables installed heater",()=>Env("3") is {TemperatureCelsius:0,HeaterOn:true});
    await Send(new TrackModelTemperatureCommandMessage{BlockId="3",TemperatureCelsius=20});
    await Check("warm temperature disables heater",()=>Env("3") is {TemperatureCelsius:20,HeaterOn:false});
    await Send(new MaintenanceRequestMessage{BlockId="3",RequestedState=MaintenanceState.Closed});
    await Check("maintenance request reaches captured block state",()=>State("3")?.IsClosed==true);
    await Send(new TrackModelTrainUpdateMessage{TrainId="01",CurrentBlockId="3"},accepted:false);
    await Send(new TrackModelSnapshotRequestMessage());
    await Check("rejected closed-block entry retains train at station",()=>Env("10")?.TrainId=="01"&&Env("3")?.TrainId=="");
    await Send(new MaintenanceRequestMessage{BlockId="3",RequestedState=MaintenanceState.Open});
    await Send(new TrackModelCommandMessage{BlockId="5",CommandedSpeedMetersPerSecond=-1},accepted:false);
    await Check("rejected negative command preserves accepted speed",()=>Env("5")?.CommandedSpeedMetersPerSecond==20*0.44704);
    await Send(new SystemTimeMessage{SystemTime=new TimeSpan(10,0,0)});
    await Check("clock reaches CTC, hourly tickets expire, cumulative tickets remain",()=>Get<SystemTimeMessage>("time")?.SystemTime==new TimeSpan(10,0,0)&&Get<TicketSalesMessage>("tickets")?.TicketsPerHour==0&&Env("10")?.TicketsSold==3);
    Console.WriteLine($"COMPLETE: {checks} real-process output checks passed, with accepted/rejected acknowledgements for every input.");
}
finally {stop.Cancel();await Task.WhenAll(tasks);}
