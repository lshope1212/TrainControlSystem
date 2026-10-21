# TrackController.Pi

The Windows application owns the UI, test inputs, and routing. Each block has one controller owner. A software territory runs locally on the PC; a hardware territory runs on the Pi. Both execute the same `TrackController.Core` code, Boolean interpreter. Each territory has its own PLC assignment, defaulting to `wayside.plc`. They control different territories; they are not redundant copies voting on one territory.

The original **PLC program** option was only a local preview. To execute on hardware, use **Controller setup → Track and controllers**. One shared track map, named territory ownership lists, and editable PC/Pi placement are explained in the [territory guide](../TrackController.Runtime/README.md). The selected block inspector shows its execution target.

## What the PLC file means

The Pi executes the assigned `plc boolean_v1` source with the same scalar Boolean interpreter as Windows. The program evaluates the interlocks and calculates commanded speed and authority through explicit AND/OR/NOT equations in the PLC file. C# handles I/O, unit encoding/decoding, clocks, and an independent unsafe-output veto. It does not precompute the normal movement decision for the PLC.

The [PLC language contract](../TrackController.Core/Control/README.md) explains the inputs, output bits, fixed-point representation and scan sequence. Select **PLC territory → Advanced → Replace selected PLC…** while paused to assign a program, then save applied choices to retain it. The source is uploaded when establishing each Pi session. Old permission-only PLC files are rejected. Rebuild/redeploy the Pi package for protocol version 6.

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

## Build and copy the Pi runtime

From the repository root in PowerShell:

```powershell
.\src\TrackController\TrackController.Pi\scripts\Publish-TrackControllerPi.ps1
scp .\src\TrackController\TrackController.Pi\artifacts\track-controller-pi5.tar.gz yourname@192.168.50.2:~/
```

The script publishes a **self-contained Linux ARM64** application, including the .NET runtime. The Pi does not need Visual Studio or a .NET SDK. This is Microsoft's supported [ARM deployment approach](https://learn.microsoft.com/en-us/dotnet/iot/deployment). The first publish needs internet access on the PC to download the ARM64 runtime pack.

In an SSH terminal on the Pi:

```bash
mkdir -p ~/track-controller
tar -xzf ~/track-controller-pi5.tar.gz -C ~/track-controller
cd ~/track-controller
chmod +x TrackController.Pi
./TrackController.Pi --port 11405 --status-file status.json
```

Keep that terminal open. The runtime reports that it is waiting for the PC router. Its TCP listener is bound to **127.0.0.1 on the Pi**, not exposed to the school network.

In a second Windows PowerShell terminal, open the encrypted tunnel:

```powershell
.\src\TrackController\TrackController.Pi\scripts\Connect-TrackControllerPi.ps1 -User yourname -PiAddress 192.168.50.2
```

Keep this terminal open too. It forwards `127.0.0.1:11405` on Windows to the Pi's local runtime. SSH handles authentication/encryption. The deployment JSON intentionally contains `127.0.0.1`, not the Pi's Ethernet address.

## Use the separate Test UI with the actual Pi

1. Start `TrackController.Wpf` and `TrackController.TestUI.Wpf` as separate startup projects.
2. In the controller window, stay paused.
3. In **Controller setup**, choose **Saved setup file…**, select **Load setup**, and open `Samples/pc-and-pi.deployment.json` from the application's output folder. The file dialog starts in that folder. Alternatively, **Load setup** starts both territories on PC; change the junction's **Run on** to **Pi** and select **Apply execution**.
4. The example assigns **West approach (1–3) to PC** and **Junction and crossing (4–12) to Pi**. Both reference the same track map and load the shared `wayside.plc`; no separate manual PLC transfer is needed. Hardware placement is editable without changing territory ownership.
5. In the Test UI, load **Clear route**, then **Send feedback**. Wait for 12/12 current blocks. If only the PC blocks are current, inspect the Pi terminal and SSH tunnel.
6. Select **Resume** in the controller window, then **Send movement request** in the Test UI with target 5, route `6, 7, 8`, 25 mph, and 1,200 ft. The request is routed to the Pi; the returned approval appears in **Controller response**.
7. Load **Switch mismatch**, then **Send feedback**. The Pi should return zero movement authority.
8. To demonstrate local execution, use a clear preset, report block 1 occupied in the Blocks editor, send feedback, and submit a request from 1 over `2, 3` with a distinct train label. The PC evaluates it. One request must stay within its assigned territory.
9. Disconnect the SSH tunnel. The Pi territory becomes unavailable and its last authorizations become zero. The LED monitor treats expired outputs as stopped. Reconnect: the Pi session starts paused, with no old requests or feedback. Send fresh feedback and explicitly resume. If the PC territory is still running, select **Pause**, then **Resume** to resume both together.

Each Pi runtime permits one owner at a time. The external Test UI talks to the existing PC controller; it does not open a second Pi session. Use a separate runtime and tunnel port for each independent Pi territory. This deployment uses protocol version 6; update the Pi package alongside Windows when replacing an older version.

Local testing without a Pi is also possible: run `TrackController.Pi` on Windows as a second application, then load the same deployment with no SSH tunnel. This verifies the network/runtime path, but it is not hardware validation.

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

- The shared map retains its 3→4 boundary connection. Territories exchange adjacent feedback as read-only observations, with exactly one command owner per block. No request may span owners yet: coordinated inter-controller handoff remains to be implemented. Every switch/crossing protection zone must stay with its controller. Final territory allocation needs your team's actual topology.
- Routing uses block IDs on one line. Those IDs must be unique across the deployment.
- CTC/Track Model continue using the same Windows named-pipe messages. The PC's internal Pi link uses those same JSON envelopes over an ordered request/reply TCP stream protected by SSH. The other modules do not need to know the Pi address.
- Reconnection creates a new paused controller session rather than replaying old movement commands. A missing Pi never causes its territory to run on a local substitute.
- The transport estimates the PC/Pi clock offset during connection and translates feedback timestamps and output leases. Received leases are also bounded from the local request start so network delay cannot extend permission.
- CTC must still supply block/route-aware movement requests, and Track Model needs its snapshot/actuator adapter, as described in the [module integration notes](../TrackController.Wpf/README.md#module-integration).
- This is a course prototype. It does not implement redundant vital hardware or certify physical equipment operation.
