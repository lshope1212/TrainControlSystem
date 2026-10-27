# Hardware Train Controller (Raspberry Pi)

Independent process that controls **TRAIN-002, 004, 006, 008, 010**. It is a TCP server; the
Windows Train Controller (Hardware backend) connects to it as a client. Each received request
is exactly one controller evaluation for one train — the Pi never advances time by itself.
Each train has its own controller state (`Controller/HardwareTrainController.cs`). This
project must not reference `TrainController.Core` (enforced by an architecture test).

## 1. Run it on the Windows PC (testing without the Pi)

From the repository root, in a separate terminal:

```powershell
dotnet run --project src/TrainController/TrainController.Hardware.Pi -- --port 5050 --bind 127.0.0.1
```

`--bind 127.0.0.1` avoids a Windows Firewall prompt. Then start the Train Controller UI with a
per-run host override (appsettings.json keeps the real Pi address):

```powershell
dotnet run --project src/TrainController/TrainController.Wpf -- --pi-host 127.0.0.1
```

The Main UI shows **Pi link: Ready/Active** once a Hardware train runs.

## 2. Deploy to the Raspberry Pi 5 (64-bit Raspberry Pi OS)

Publish a self-contained, ReadyToRun build (no .NET installation needed on the Pi; code is
precompiled so the first requests are not slowed by JIT), from the repository root:

```powershell
dotnet publish src/TrainController/TrainController.Hardware.Pi -c Release -r linux-arm64 --self-contained true -p:PublishReadyToRun=true -o publish/pi
```

On start-up the Pi program also runs a short warm-up before listening and logs
`Warm-up complete in … ms`.

Copy it to the Pi (replace user / IP with yours, e.g. the static IP you configured):

```powershell
scp -r publish/pi <user>@<pi-ip>:~/train-controller
```

On the Pi (`ssh <user>@<pi-ip>`):

```bash
chmod +x ~/train-controller/TrainController.Hardware.Pi
~/train-controller/TrainController.Hardware.Pi --port 5050
```

`src/TrainController/TrainController.Wpf/appsettings.json` already points at the Pi:
`"Host": "192.168.50.2"`, `"Port": 5050` (Windows host is 192.168.50.1, /24 direct link). Start
the Train Controller UI without overrides:

```powershell
dotnet run --project src/TrainController/TrainController.Wpf
```

The Pi console logs each connection. If you change the Pi's `--port`, change `Port` to match.

Options: `--port <n>` (default 5050), `--bind <address>` (default 0.0.0.0 = all interfaces).

### Optional: start automatically at boot (systemd)

```bash
sudo tee /etc/systemd/system/train-controller.service >/dev/null <<'UNIT'
[Unit]
Description=ECE1140 Hardware Train Controller
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/home/<user>/train-controller/TrainController.Hardware.Pi --port 5050
Restart=on-failure
User=<user>

[Install]
WantedBy=multi-user.target
UNIT
sudo systemctl daemon-reload
sudo systemctl enable --now train-controller
journalctl -u train-controller -f      # view logs
```

## 3. Troubleshooting

| Symptom (Main UI) | Check |
| ----------------- | ----- |
| Pi link **Disconnected** | Pi program running? `ping <pi-ip>` works? Correct Host/Port in appsettings.json? |
| Pi link **Faulted**, trains in FAIL-SAFE | Pi log for errors; network stable (cable)? Request timeout too short for the network? |
| "link fault latched … press E-brake reset" | Expected after any link failure: once the link is up, press **E-brake reset** for that train. |
| Version error | Windows and Pi builds are from different commits; republish the Pi build. |
| Pi log: "Unable to write data … aborted by the software in your host machine" | Windows closed the connection first — usually a request timeout. Check the Windows event log / Pi link detail; if it persists, raise `RequestTimeoutMs`. |
