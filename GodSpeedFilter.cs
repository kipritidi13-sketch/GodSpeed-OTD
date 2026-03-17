// GodSpeed OTD Plugin v5.5 — dynamic timeout + CPU optimization [PROPER OTD TIMING FIX]
using System;
using System.Diagnostics;
using System.Numerics;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;
using OpenTabletDriver.Plugin.Timing; // Добавлено для HPETDeltaStopwatch

namespace GodSpeedOTD
{
    [PluginName("GodSpeed Filter")]
    public class GodSpeedFilter : AsyncPositionedPipelineElement<IDeviceReport>
    {
        [Property("Hz"), DefaultPropertyValue(1000f)]
        [ToolTip("Interpolation frequency. 1000 = 1000Hz. Higher = smoother but more CPU.")]
        public new float Frequency
        {
            get => base.Frequency;
            set => base.Frequency = value;
        }

        [Property("Filter Mode"), DefaultPropertyValue(0)]
        [ToolTip("0=EMA  1=Kalman  2=Ring  3=Hybrid")]
        public int Mode { get; set; } = 0;

        [Property("Smooth Ms"), DefaultPropertyValue(8f)]
        [ToolTip("Smoothing in ms. 8-30.")]
        public float SmoothMs { get; set; } = 8f;

        [Property("Prediction Ms"), DefaultPropertyValue(0f)]
        [ToolTip("Prediction in ms. 0-5.")]
        public float PredMs { get; set; } = 0f;

        [Property("Deadzone"), DefaultPropertyValue(0f)]
        [ToolTip("Dead zone in tablet units.")]
        public float DeadzoneUnits { get; set; } = 0f;

        [Property("Aggression"), DefaultPropertyValue(5f)]
        [ToolTip("Speed transition curve slow→fast (1-10).")]
        public float Aggression { get; set; } = 5f;

        [Property("Pro Mode"), DefaultPropertyValue(false)]
        [ToolTip("Quadratic speed curve for fast jumps (osu!).")]
        public bool ProMode { get; set; } = false;

        [Property("Kalman Q"), DefaultPropertyValue(2f)]
        [ToolTip("Kalman process noise.")]
        public float KalmanQ { get; set; } = 2f;

        [Property("Kalman R"), DefaultPropertyValue(4f)]
        [ToolTip("Kalman measurement noise.")]
        public float KalmanR { get; set; } = 4f;

        [Property("Ring Size"), DefaultPropertyValue(8)]
        [ToolTip("Ring buffer size (Ring mode). 1-64.")]
        public int RingSize { get; set; } = 8;

        public override PipelinePosition Position => PipelinePosition.PreTransform;

        // ── State ─────────────────────────────────────────────────
        readonly object _lock = new object();
        float _rawX, _rawY;
        bool _hasInput = false;
        bool _newInput = false;

        float _fX, _fY;
        float _fastSpd, _baseSpd;
        float _vDirX, _vDirY;
        float _predVX, _predVY;
        float _prevRawX, _prevRawY;
        float _ringAnchorX, _ringAnchorY;
        bool _first = true;
        int _lastMode = -1;
        readonly Kalman2D _kalman = new Kalman2D();
        readonly RingBuffer2D _ring = new RingBuffer2D(8);

        // ИСПОЛЬЗУЕМ ВЫСОКОТОЧНЫЙ ТАЙМЕР ВМЕСТО ОБЫЧНОГО STOPWATCH
        private HPETDeltaStopwatch _updateStopwatch = new HPETDeltaStopwatch(true);
        float _predDtAccum = 0f;

        // ── ConsumeState ──────────────────────────────────────────
        protected override void ConsumeState()
        {
            if (State is ITabletReport tablet)
            {
                lock (_lock)
                {
                    _rawX = tablet.Position.X;
                    _rawY = tablet.Position.Y;
                    _hasInput = true;
                    _newInput = true;

                    int m = Mode;
                    if (m != _lastMode)
                    {
                        _first = true;
                        _fX = _fY = _fastSpd = _baseSpd = 0f;
                        _vDirX = _vDirY = _predVX = _predVY = 0f;
                        _predDtAccum = 0f;
                        _kalman.Reset();
                        _ring.Reset();
                        _lastMode = m;
                    }
                }
            }
        }

        // ── UpdateState ───────────────────────────────────────────
        protected override void UpdateState()
        {
            if (!(State is ITabletReport tablet))
                return;

            // --- ЖЕЛЕЗНАЯ ПРОВЕРКА OTD: ФИКС ДАБЛ-КЛИКОВ ---
            // Если перо поднято, сбрасываем фильтры и не эмитим координаты.
            if (!PenIsInRange())
            {
                lock (_lock)
                {
                    _first = true;
                    _hasInput = false;
                    _fastSpd = _baseSpd = 0f;
                    _vDirX = _vDirY = _predVX = _predVY = 0f;
                    _predDtAccum = 0f;
                    _kalman.Reset();
                    _ring.Reset();
                    _updateStopwatch.Restart(); // Сброс таймера, чтобы не накопить огромный dt при возврате пера
                }
                return;
            }

            // --- ИДЕАЛЬНЫЙ РАСЧЕТ ВРЕМЕНИ (dt в миллисекундах) ---
            float dt = (float)_updateStopwatch.Restart().TotalMilliseconds;
            // Защита от спайков ЦП: если поток залагал, ограничиваем dt
            dt = Math.Max(0.1f, Math.Min(50.0f, dt));

            float rawX, rawY;
            bool isNew;

            lock (_lock)
            {
                if (!_hasInput) return; // Ждем первые валидные координаты после подноса пера

                rawX = _rawX;
                rawY = _rawY;
                isNew = _newInput;
                _newInput = false;
            }

            // CPU optimization: skip interpolation ticks when pen is idle
            // Only skip if: no new input AND filter has already converged to raw position
            if (!isNew)
            {
                float dx = rawX - _fX, dy = rawY - _fY;
                float distToRaw = MathF.Sqrt(dx * dx + dy * dy);
                // If filter is within 0.1 units of target — nothing to interpolate, skip
                if (distToRaw < 0.1f)
                    return; // dt уже обновлен через Restart(), можно смело скипать кадр
            }

            _predDtAccum += dt;

            if (_first)
            {
                _fX = rawX; _fY = rawY;
                _prevRawX = rawX; _prevRawY = rawY;
                _ringAnchorX = rawX; _ringAnchorY = rawY;
                _fastSpd = _baseSpd = _predVX = _predVY = _vDirX = _vDirY = 0f;
                _predDtAccum = 0f;
                _kalman.Q = KalmanQ; _kalman.R = KalmanR;
                _kalman.Reset();
                _ring.Capacity = Math.Max(1, Math.Min(64, RingSize));
                _ring.Reset();
                _first = false;
                tablet.Position = new Vector2(rawX, rawY);
                OnEmit();
                return;
            }

            _kalman.Q = KalmanQ;
            _kalman.R = KalmanR;
            _ring.Capacity = Math.Max(1, Math.Min(64, RingSize));

            float outX, outY;
            switch (Mode)
            {
                case 1: RunKalman(rawX, rawY, dt, isNew, out outX, out outY); break;
                case 2: RunRing(rawX, rawY, dt, isNew, out outX, out outY); break;
                case 3: RunHybrid(rawX, rawY, dt, isNew, out outX, out outY); break;
                default: RunEMA(rawX, rawY, dt, isNew, out outX, out outY); break;
            }

            tablet.Position = new Vector2(outX, outY);
            OnEmit();
        }

        // ── EMA ───────────────────────────────────────────────────
        void RunEMA(float rawX, float rawY, float dt, bool isNew, out float outX, out float outY)
        {
            float dx = rawX - _fX, dy = rawY - _fY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float spd = dist / Math.Max(dt, 0.1f);

            _fastSpd += (spd - _fastSpd) * (dt / (10f + dt));
            if (spd > 0.5f)
                _baseSpd += (spd - _baseSpd) * (spd < _baseSpd ? dt / (80f + dt) : dt / (180f + dt));

            float sf = Math.Max(0f, Math.Min(1f,
                (_fastSpd / (Math.Max(_baseSpd, 2f) * 1.5f) - 1f)
                / Math.Max(0.1f, 10f - Aggression)));

            float vf;
            if (ProMode)
            {
                vf = sf * sf; // quadratic curve
                _vDirX = _vDirY = 0f;
            }
            else
            {
                float cdx = dist > 0.5f ? dx / dist : 0f;
                float cdy = dist > 0.5f ? dy / dist : 0f;
                float da = dt / (8f + dt);
                _vDirX += (cdx - _vDirX) * da;
                _vDirY += (cdy - _vDirY) * da;
                float dl = MathF.Sqrt(_vDirX * _vDirX + _vDirY * _vDirY);
                float dot = dist > 0.5f ? cdx * _vDirX + cdy * _vDirY : 0f;
                vf = sf * Math.Max(0f, dot) * Math.Max(0f, Math.Min(1f, dl));
            }

            float adz = DeadzoneUnits * (1f - vf * 0.95f);
            if (dist < adz)
            {
                outX = _fX; outY = _fY;
                return;
            }

            float smoothMs = SmoothMs * (1f - vf);
            float alpha = smoothMs > 0.01f ? dt / (smoothMs + dt) : 1f;
            _fX += dx * alpha;
            _fY += dy * alpha;
            outX = _fX; outY = _fY;

            if (PredMs > 0.01f && isNew && _predDtAccum > 0.01f)
            {
                float vx = (rawX - _prevRawX) / _predDtAccum;
                float vy = (rawY - _prevRawY) / _predDtAccum;
                float predScale = 1f - vf * 0.5f;
                outX = _fX + vx * PredMs * predScale;
                outY = _fY + vy * PredMs * predScale;
            }

            if (isNew)
            {
                _prevRawX = rawX; _prevRawY = rawY;
                _predDtAccum = 0f;
            }
        }

        // ── Kalman ────────────────────────────────────────────────
        void RunKalman(float rawX, float rawY, float dt, bool isNew, out float outX, out float outY)
        {
            float kDt = Math.Max(0.5f, Math.Min(20f, dt));
            float adz = DeadzoneUnits;

            float tx = rawX, ty = rawY;

            if (adz > 0f && isNew)
            {
                float rawDist = MathF.Sqrt(
                    (rawX - _prevRawX) * (rawX - _prevRawX) +
                    (rawY - _prevRawY) * (rawY - _prevRawY));
                float idle = Math.Max(1.5f, adz * 0.5f);

                if (rawDist < idle)
                {
                    _kalman.DampVelocity(kDt / (15f + kDt));
                    tx = _fX + (rawX - _fX) * 0.3f;
                    ty = _fY + (rawY - _fY) * 0.3f;
                }
                _prevRawX = rawX;
                _prevRawY = rawY;
            }

            _kalman.Update(tx, ty, kDt, out float kX, out float kY);
            _fX = kX; _fY = kY;
            outX = kX; outY = kY;

            float curSpd = MathF.Sqrt(
                _kalman.VelocityX * _kalman.VelocityX +
                _kalman.VelocityY * _kalman.VelocityY);
            float idleSpd = Math.Max(1.5f, adz * 0.5f) / Math.Max(kDt, 0.5f);

            if (PredMs > 0.01f && curSpd > idleSpd)
            {
                _kalman.PredictAhead(PredMs, out float px, out float py);
                float pdx = px - kX, pdy = py - kY;
                float md = curSpd * PredMs * 1.5f + 0.5f;
                float pd = MathF.Sqrt(pdx * pdx + pdy * pdy);
                if (pd > md && pd > 0f)
                {
                    float sc = md / pd;
                    px = kX + pdx * sc;
                    py = kY + pdy * sc;
                }
                outX = px; outY = py;
            }
        }

        // ── Ring ──────────────────────────────────────────────────
        void RunRing(float rawX, float rawY, float dt, bool isNew, out float outX, out float outY)
        {
            if (isNew)
            {
                float dx = rawX - _ringAnchorX, dy = rawY - _ringAnchorY;
                float dist = MathF.Sqrt(dx * dx + dy * dy);

                if (dist < DeadzoneUnits && DeadzoneUnits > 0f)
                    _ring.Push(_ringAnchorX, _ringAnchorY);
                else
                {
                    _ring.Push(rawX, rawY);
                    _ringAnchorX = rawX;
                    _ringAnchorY = rawY;
                }
            }

            _ring.WeightedAverage(out float rx, out float ry);

            float ba = SmoothMs > 0.01f ? dt / (SmoothMs + dt) : 1f;
            _fX += (rx - _fX) * ba;
            _fY += (ry - _fY) * ba;

            outX = _fX;
            outY = _fY;
        }

        // ── Hybrid ────────────────────────────────────────────────
        void RunHybrid(float rawX, float rawY, float dt, bool isNew, out float outX, out float outY)
        {
            float kDt = Math.Max(0.5f, Math.Min(20f, dt));

            float dx = rawX - _fX, dy = rawY - _fY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float spd = dist / Math.Max(dt, 0.1f);

            _fastSpd += (spd - _fastSpd) * (dt / (10f + dt));
            if (spd > 0.5f)
                _baseSpd += (spd - _baseSpd) * (spd < _baseSpd ? dt / (80f + dt) : dt / (180f + dt));

            float sf = Math.Max(0f, Math.Min(1f,
                (_fastSpd / (Math.Max(_baseSpd, 2f) * 1.5f) - 1f)
                / Math.Max(0.1f, 10f - Aggression)));

            float adz = DeadzoneUnits * (1f - sf * 0.95f);
            float tx = dist < adz ? _fX : rawX;
            float ty = dist < adz ? _fY : rawY;

            _kalman.Update(tx, ty, kDt, out float kX, out float kY);
            _fX = kX; _fY = kY;

            float ep = PredMs * (1f - sf);
            outX = kX; outY = kY;

            if (ep > 0.01f)
            {
                _kalman.PredictAhead(ep, out float px, out float py);
                float pdx = px - kX, pdy = py - kY;
                float s = MathF.Sqrt(
                    _kalman.VelocityX * _kalman.VelocityX +
                    _kalman.VelocityY * _kalman.VelocityY);
                float md = s * ep * 1.5f + 0.5f;
                float pd = MathF.Sqrt(pdx * pdx + pdy * pdy);
                if (pd > md && pd > 0f)
                {
                    float sc = md / pd;
                    px = kX + pdx * sc;
                    py = kY + pdy * sc;
                }
                outX = px; outY = py;
            }
        }
    }

    public class Kalman1D
    {
        float _x, _v, _p00, _p01, _p10, _p11;
        bool _init;
        public float Q = 2f, R = 4f;
        public float Velocity => _v;

        public void Reset()
        {
            _init = false;
            _x = _v = _p00 = _p01 = _p10 = _p11 = 0f;
        }

        public void DampVelocity(float a) { _v *= (1f - a); }

        public float Update(float z, float dt)
        {
            if (!_init) { _x = z; _p00 = _p11 = 1f; _init = true; return z; }
            float xp = _x + _v * dt;
            float dt2 = dt * dt, dt3 = dt2 * dt;
            float pp00 = _p00 + dt * (_p01 + _p10) + dt2 * _p11 + Q * dt3 / 3f;
            float pp01 = _p01 + dt * _p11 + Q * dt2 / 2f;
            float pp10 = _p10 + dt * _p11 + Q * dt2 / 2f;
            float pp11 = _p11 + Q * dt;
            float S = Math.Max(pp00 + R, 1e-6f);
            float K0 = pp00 / S, K1 = pp10 / S;
            float inn = z - xp;
            _x = xp + K0 * inn;
            _v += K1 * inn;
            _p00 = (1f - K0) * pp00;
            _p01 = (1f - K0) * pp01;
            _p10 = pp10 - K1 * pp00;
            _p11 = pp11 - K1 * pp01;
            _p00 = Math.Min(_p00, 1e4f);
            _p11 = Math.Min(_p11, 1e4f);
            return _x;
        }

        public float PredictAhead(float dt) => _x + _v * dt;
    }

    public class Kalman2D
    {
        readonly Kalman1D _kx = new Kalman1D(), _ky = new Kalman1D();
        public float Q { set { _kx.Q = value; _ky.Q = value; } get => _kx.Q; }
        public float R { set { _kx.R = value; _ky.R = value; } get => _kx.R; }
        public float VelocityX => _kx.Velocity;
        public float VelocityY => _ky.Velocity;

        public void Reset() { _kx.Reset(); _ky.Reset(); }
        public void DampVelocity(float a) { _kx.DampVelocity(a); _ky.DampVelocity(a); }

        public void Update(float mx, float my, float dt, out float ox, out float oy)
        {
            ox = _kx.Update(mx, dt);
            oy = _ky.Update(my, dt);
        }

        public void PredictAhead(float dt, out float px, out float py)
        {
            px = _kx.PredictAhead(dt);
            py = _ky.PredictAhead(dt);
        }
    }

    public class RingBuffer2D
    {
        float[] _bx, _by;
        int _head, _count, _cap;

        public int Capacity
        {
            get => _cap;
            set
            {
                int nv = Math.Max(1, Math.Min(64, value));
                if (nv == _cap) return;
                _bx = new float[nv];
                _by = new float[nv];
                _head = _count = 0;
                _cap = nv;
            }
        }

        public RingBuffer2D(int cap = 8)
        {
            _cap = Math.Max(1, cap);
            _bx = new float[_cap];
            _by = new float[_cap];
        }

        public void Push(float x, float y)
        {
            _bx[_head] = x;
            _by[_head] = y;
            _head = (_head + 1) % _cap;
            if (_count < _cap) _count++;
        }

        public void WeightedAverage(out float ax, out float ay)
        {
            if (_count == 0) { ax = ay = 0; return; }
            double sx = 0, sy = 0, sw = 0;
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - 1 - i + _cap * 2) % _cap;
                float w = _count - i;
                sx += _bx[idx] * w;
                sy += _by[idx] * w;
                sw += w;
            }
            ax = (float)(sx / sw);
            ay = (float)(sy / sw);
        }

        public void Reset() { _head = _count = 0; }
    }
}
