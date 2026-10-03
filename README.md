# SMI Widget

A small Windows desktop gadget for NVIDIA GPUs. It reads `nvidia-smi` and can apply the official power and clock controls.

## Run

```bash
dotnet run --project SMI-Widget
```

Or double-click `run.bat`.

The window is borderless and stays above other windows until you unpin it. The X hides it to the tray (green S icon). Right-click that icon to exit.

## What it shows

The glance view covers clocks, temperature, power draw, memory, utilization, fan, performance state, and PCIe. Details adds throttle reasons, encoder/decoder/JPEG/OFA, voltage, BAR1, ECC and remapped rows when the driver reports them, processes, and the usual identity fields (driver, CUDA, VBIOS, bus, UUID).

Data comes from `nvidia-smi -q -x`, which is the full query behind flags like `-d CLOCK,TEMPERATURE,POWER,MEMORY`, plus utilization, performance, PCIe, processes, and the other useful sections. Supported-clocks tables are left out because they are huge and not useful in a gadget.

## What it can set

You cannot set instantaneous power draw. The control that matters is the power limit (`nvidia-smi -pl`), which caps draw. Presets are 70%, 85%, default, and max, always clamped to the range the driver reports.

Also available when the GPU supports them:

- Graphics clock lock / unlock (`-lgc`, `-rgc`)
- Memory clock lock / unlock (`-lmc`, `-rmc`)
- Applications clocks (`-ac`, `-rac`)
- GPU target temperature (`-gtt`)
- Persistence mode (`-pm`)
- Compute mode (`-c`)

Fan curves and voltage offsets are not included. `nvidia-smi` cannot set those on GeForce cards.

Changes run as you first. If the driver asks for administrator rights, Windows shows a UAC prompt. Cancelling that prompt leaves the card unchanged.

## Settings

Position, pin, opacity, refresh rate, and the open panels are saved in `%AppData%\SMI-Widget\settings.json`.
