# GodSpeed Filter for OpenTabletDriver

Advanced cursor smoothing filter with multiple modes.

## Features
- 4 Filter Modes: EMA, Kalman, Ring Buffer, Hybrid
- 1000Hz Interpolation
- Adaptive Speed Detection
- Prediction Compensation
- Pro Mode for osu! jumps
- Deadzone support

## Installation
1. Download `GodSpeedOTD.dll` from [Releases](../../releases)
2. Copy to `%appdata%/OpenTabletDriver/Plugins/`
3. Restart OpenTabletDriver
4. Filters → Enable **GodSpeed Filter**

## Building
```bash
dotnet build -c Release
```

## License
MIT