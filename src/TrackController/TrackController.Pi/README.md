# TrackController.Pi

The Windows application owns the UI, test inputs, and routing. Each block has one controller owner. A software territory runs locally on the PC; a hardware territory runs on the Pi. Both execute the same `TrackController.Core` code, Boolean interpreter. Each territory has its own PLC assignment, defaulting to `wayside.plc`. They control different territories; they are not redundant copies voting on one territory.

The original **PLC program** option was only a local preview. To execute on hardware, use **Controller setup → Wayside territories**. One shared track map, named territory ownership lists, and editable PC/Pi placement are explained in the [territory guide](../TrackController.Runtime/README.md). The selected block inspector shows its execution target.

## What the PLC file means

The Pi executes the assigned `plc boolean_v1` source with the same scalar Boolean interpreter as Windows. The program evaluates the interlocks and calculates commanded speed and authority through explicit AND/OR/NOT equations in the PLC file. C# handles I/O, unit encoding/decoding, clocks, and an independent unsafe-output veto. It does not precompute the normal movement decision for the PLC.

The [PLC language contract](../TrackController.Core/Control/README.md) explains the inputs, output bits, fixed-point representation and scan sequence. Select **Wayside territories → Advanced → Replace selected PLC…** in maintenance mode to assign a program, then save applied choices to retain it. The source is uploaded when establishing each Pi session. Old permission-only PLC files are rejected. Rebuild/redeploy the Pi package for protocol version 11.

## Recommended connection at school

Use a direct Ethernet cable between the PC and Pi. If the laptop has no Ethernet port, use a USB-to-Ethernet adapter. Keep the PC on school Wi-Fi for internet access. The direct link can work without putting the Pi on the school network; it does not bridge or share the school connection.

Set up the Pi using a monitor and keyboard initially. Install **64-bit Raspberry Pi OS** with Raspberry Pi Imager, create your own username/password, and enable SSH. Do not assume the username is `pi`. Raspberry Pi documents SSH and address discovery in its [remote-access guide](https://www.raspberrypi.com/documentation/computers/remote-access.html).

Use these example addresses only if that subnet is not already used by another adapter or VPN:

| Device | Direct Ethernet IPv4 | Mask | Gateway / DNS |
| --- | --- | --- | --- |
| Windows PC | `192.168.50.1` | `255.255.255.0` | Leave blank |
| Pi 5 | `192.168.50.2` | `255.255.255.0` | Leave blank |

On Windows, open the Ethernet adapter's IPv4 properties and enter the PC values. Change the Ethernet adapter, not the school Wi-Fi adapter. This may require administrator access on a managed computer.

On the Pi, inspect the actual interface name:

```bash
nmcli device status
```

For an interface named `eth0`, create a dedicated direct-cable profile once:

```bash
sudo nmcli connection add type ethernet ifname eth0 con-name track-controller-direct ipv4.method manual ipv4.addresses 192.168.50.2/24 ipv4.never-default yes ipv6.method disabled
sudo nmcli connection up track-controller-direct
```

If your interface has a different name, replace `eth0`. Do not keep adding duplicate profiles; use `nmcli connection modify track-controller-direct ...` for later changes. Current Raspberry Pi OS uses NetworkManager; these commands follow its [profile configuration](https://networkmanager.pages.freedesktop.org/NetworkManager/NetworkManager/nm-settings-nmcli.html).

From Windows, test the connection (replace `yourname`):

```powershell
ssh yourname@192.168.50.2
```

Check the host-key fingerprint at first login against the Pi, then accept it. If this fails, fix SSH/Ethernet before trying the controller. If a direct cable is unavailable, both devices can use an approved private router/network; school Wi-Fi may isolate clients, so it should not be assumed to work.

## Publish, copy, run: same workflow as the hardware Train Controller

The two controllers run as independent programs on the same Pi. Use the existing private lab connection, with the PC at `192.168.50.1` and Pi at `192.168.50.2`. The direct TCP link has no SSH authentication/encryption. The optional tunnel workflow remains available below.

| | Hardware Train Controller | Track Controller |
| --- | --- | --- |
| Project | `src/TrainController/TrainController.Hardware.Pi` | `src/TrackController/TrackController.Pi` |
| PC publish folder | `publish/pi` | `publish/track-controller-pi` |
| Pi installation folder | `~/train-controller` | `~/track-controller` |
| Executable | `TrainController.Hardware.Pi` | `TrackController.Pi` |
| TCP port | 5050 | 11405 |
| Windows connection setting | Train Controller host/port | Track Controller territory's Pi IP address/port |

### 1. Publish on Windows

From the repository root, use the same publish command as the Train Controller, substituting the Track Controller project and output folder:

```powershell
dotnet publish src/TrackController/TrackController.Pi -c Release -r linux-arm64 --self-contained true -p:PublishReadyToRun=true -o publish/track-controller-pi
```

Alternatively, `./src/TrackController/TrackController.Pi/scripts/Publish-TrackControllerPi.ps1` runs that command and also copies supporting documentation. The output contains the Pi executable, Track Controller runtime/core and contract libraries, .NET runtime, sample PLC and optional LED script. No Windows UI or Train Controller executable is included. The Pi needs no .NET SDK. `-OutputDirectory PATH` selects another output folder; `-Archive` additionally creates an optional tarball.

### 2. Copy to the Pi

Replace `<user>` and `<pi-ip>` with the same account/address your teammate uses. This creates the destination if needed and updates its contents without nesting another folder on repeat copies:

```powershell
ssh <user>@<pi-ip> "mkdir -p ~/track-controller"
scp -r publish/track-controller-pi/* <user>@<pi-ip>:~/track-controller/
```

Keep it separate from `~/train-controller`. Stop the old Track Controller process before replacing its files, then restart it after the copy. Publishing on Windows alone never updates the Pi.

### 3. Run on the Pi

In an SSH terminal, just as for her Train Controller:

```bash
chmod +x ~/track-controller/TrackController.Pi
~/track-controller/TrackController.Pi --port 11405
```

It binds to the lab Pi IP `192.168.50.2` by default. If the Pi address differs, add `--bind <pi-ip>`. The terminal stays open. A separate terminal can run her unchanged `~/train-controller/TrainController.Hardware.Pi --port 5050` at the same time. Status is written beside the Track Controller executable by default; `--status-file PATH` overrides it.

### 4. Connect from the Windows Track Controller

1. Enable maintenance mode and open **Controller setup → Wayside territories**.
2. Load the desired course preset. Set the chosen territory's **Run this territory on** to **Pi**, **Pi IP address** to `192.168.50.2`, and **Port** to `11405`.
3. Select **Apply changes**, then **Save setup** if desired. Windows uploads that territory configuration and its assigned PLC automatically. It does not install the executable.
4. Send fresh Track Model feedback, then leave maintenance mode when ready. Valid PLCs evaluate automatically; no separate Resume is needed. The territory inspector identifies Pi execution.

Every additional territory on this same Pi needs a separate Track Controller process, port and status file, for example `--port 11406 --status-file ~/track-controller/status-11406.json`. Multiple territories can use the same installed executable; a single runtime process accepts one territory owner. Other territories may continue running on the PC. Coordinated routes may cross PC/Pi boundaries.

The PC keeps its UI and CTC/Track Model named-pipe adapters. The Pi runs the assigned wayside's PLC and independent unsafe-output veto. A broken connection withdraws its authority; it never silently falls back to PC. Reconnecting creates a new session with no old feedback or requests. It preserves the requested maintenance state and otherwise evaluates fresh inputs automatically.

### Local testing without the Pi

As with the hardware Train Controller, run a second process on Windows and use loopback:

```powershell
dotnet run --project src/TrackController/TrackController.Pi -- --port 11405 --bind 127.0.0.1
```

Set the territory's IP to `127.0.0.1`, port `11405`, and apply. This tests the real TCP path; actual shared-Pi timing and physical hardware still need testing on the device.

### Optional: start at boot

Use the same systemd service workflow as the Train Controller, with a separate service name. Replace `<user>` in the file:

```bash
sudo tee /etc/systemd/system/track-controller.service >/dev/null <<'UNIT'
[Unit]
Description=ECE1140 Hardware Track Controller
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/home/<user>/track-controller/TrackController.Pi --port 11405
WorkingDirectory=/home/<user>/track-controller
Restart=on-failure
User=<user>

[Install]
WantedBy=multi-user.target
UNIT
sudo systemctl daemon-reload
sudo systemctl enable --now track-controller
journalctl -u track-controller -f
```

### Optional SSH tunnel

For existing tunneled setups, run the Pi executable with `--bind 127.0.0.1`, start `scripts/Connect-TrackControllerPi.ps1 -User <user> -PiAddress <pi-ip>` on Windows, and keep the territory host at `127.0.0.1`. This is optional; the direct workflow above needs no forwarding terminal.

### Versions and shared hardware

Windows and Pi runtimes must use compatible protocol versions (currently 11). Rebuild, copy and restart after runtime/protocol changes; a current Git branch does not make an old published folder or installed binary current. The optional LED process uses BCM GPIO 17, 27 and 22; reserve these from the Train Controller's hardware if using it. The Track Controller publish script does not modify or start her program.

## Optional indication LEDs

You can initially read the Pi terminal instead. For physical indication, use three ordinary LEDs, three **330 Ω series resistors**, and jumper wires. With the Pi powered off, wire each GPIO through its resistor to the LED anode; connect each cathode to a ground pin. Do not connect these LEDs to 5 V.

| Meaning | BCM GPIO | Physical header pin |
| --- | --- | --- |
| Red: stopped / no permitted movement | 17 | 11 |
| Green: current movement permission | 27 | 13 |
| Amber: missing/stale feedback or fault | 22 | 15 |
| Ground for all LED cathodes | GND | 6 |

Red and amber may both be on. These are aggregate **indication LEDs**, not commands to real track signals, relays, motors, or railway equipment. All LEDs off is not an approval indication.

Install GPIO Zero and its Pi 5 backend using an approved internet connection or your lab's package setup:

```bash
sudo apt update
sudo apt install python3-gpiozero python3-lgpio
```

In a third Pi terminal, with the runtime running:

```bash
cd ~/track-controller
python3 Hardware/led_status.py --status-file status.json
```

The script uses `LGPIOFactory`, which GPIO Zero documents for [Pi 5 support](https://gpiozero.readthedocs.io/en/stable/api_pins.html). BCM numbering and LED/resistor wiring follow its [LED recipe](https://gpiozero.readthedocs.io/en/stable/recipes.html#led). Use the distribution's current packages; `--chip NUMBER` is available if your installed image requires an explicit gpiochip selection.

Without GPIO or before wiring, inspect the same indication logic with:

```bash
python3 Hardware/led_status.py --status-file status.json --console
```

The LED monitor reads the runtime's atomic status file and checks its output expiry every 100 ms. An expired/malformed/missing status cannot light green. The software has been tested with network loopback and LED-state unit tests; actual Pi timing, OS dependencies, pin selection, wiring, and power behavior must be checked on your device.

## Boundaries and remaining integration work

- The shared map retains boundary connections. Course routes may span owners: the PC router coordinates each participating wayside's independent approval, with exactly one command owner per block. Local and Pi execution use the same path. Every switch/crossing protection zone stays with its controller. See the Runtime README for the 16-way course split.
- Routing uses block IDs, which must be unique across the deployment; a deployment may span several lines (see the Runtime README).
- CTC/Track Model continue using the same Windows named-pipe messages. The PC's internal Pi link uses those same JSON envelopes over an ordered request/reply TCP stream on the private lab connection (or the optional SSH tunnel). The other modules do not need to know the Pi address.
- Reconnection creates a new controller session with the requested maintenance state and requires new feedback/requests rather than replaying old movement commands. A missing Pi never causes its territory to run on a local substitute.
- The transport estimates the PC/Pi clock offset during connection and translates feedback timestamps and output leases. Received leases are also bounded from the local request start so network delay cannot extend permission.
- CTC supplies block/route-aware movement requests and Track Model supplies measured feedback through the production adapters described in the [module integration notes](../TrackController.Wpf/README.md#module-integration).
- This is a course prototype. It does not implement redundant vital hardware or certify physical equipment operation.
