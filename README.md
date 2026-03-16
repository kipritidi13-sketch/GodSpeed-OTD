# GodSpeed Filter for OpenTabletDriver

Advanced cursor smoothing filter with multiple modes, designed for competitive osu! and digital art.

## Features

- **4 Filter Modes:** EMA, Kalman, Ring Buffer, Hybrid
- **1000Hz Interpolation:** Smooth cursor movement between tablet reports
- **Adaptive Speed Detection:** Less smoothing during fast movements
- **Prediction Compensation:** Reduces perceived input lag
- **Pro Mode:** Instant filter bypass during fast jumps (osu!)
- **Deadzone:** Eliminates micro-jitter when holding still

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Hz | 1000 | Interpolation frequency |
| Filter Mode | 0 | 0=EMA, 1=Kalman, 2=Ring, 3=Hybrid |
| Smooth Ms | 8 | Smoothing strength in milliseconds |
| Prediction Ms | 0 | Prediction compensation in milliseconds |
| Deadzone | 0 | Dead zone in tablet units |
| Aggression | 5 | Speed transition curve (

## Filter Modes Explained

### EMA (Mode 0)
Exponential Moving Average with adaptive speed detection. Best for most users. Smoothing decreases automatically during fast movements.

### Kalman (Mode 1)
Kalman filter with velocity estimation. Predicts cursor position based on movement model. Better at handling noise while maintaining responsiveness.

### Ring Buffer (Mode 2)
Weighted average of recent positions. Simple and predictable. Good for digital art where consistent smoothing is preferred.

### Hybrid (Mode 3)
Combines Kalman filtering with adaptive speed detection from EMA mode. Best of both worlds but slightly more CPU usage.
