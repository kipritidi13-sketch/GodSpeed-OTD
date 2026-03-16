// GodSpeed OTD Plugin v5.4 — фикс даблклика + предикция
using System;
using System.Diagnostics;
using System.Numerics;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace GodSpeedOTD
{
    [PluginName("GodSpeed Filter")]
    public class GodSpeedFilter : AsyncPositionedPipelineElement<IDeviceReport>
    {
        [Property("Hz"), DefaultPropertyValue(1000f)]
        [ToolTip("Частота интерполяции. 1000 = 1000Hz.")]
        public new float Frequency
        {
            get => base.Frequency;
            set => base.Frequency = value;
        }

        [Property("Filter Mode"), DefaultPropertyValue(0)]
        [ToolTip("0=EMA  1=Kalman  2=Ring  3=Hybrid")]
        public int Mode { get; set; } = 0;

        [Property("Smooth Ms"), DefaultPropertyValue(8f)]
        [ToolTip("Сглаживание в мс. 8-30.")]
        public float SmoothMs { get; set; } = 8f;

        [Property("Prediction Ms"), DefaultPropertyValue(0f)]
        [ToolTip("Предикция в мс. 0-5.")]
        public float PredMs { get; set; } = 0f;

        [Property("Deadzone"), DefaultPropertyValue(0f)]
        [ToolTip("Мёртвая зона в единицах планшета.")]
        public float DeadzoneUnits { get; set; } = 0f;

        [Property("Aggression"), DefaultPropertyValue(5f)]
        [ToolTip("Скорость перехода slow→fast (1-10).")]
        public float Aggression { get; set; } = 5f;

        [Property("Pro Mode"), DefaultPropertyValue(false)]
        [ToolTip("vFactor^2 кривая для быстрых прыжков.")]
        public bool ProMode { get; set; } = false;

        [Property("Kalman Q"), DefaultPropertyValue(2f)]
        [ToolTip("Шум процесса Калмана.")]
        public float KalmanQ { get; set; } = 2f;

        [Property("Kalman R"), DefaultPropertyValue(4f)]
        [ToolTip("Шум измерения Калмана.")]
        public float KalmanR { get; set; } = 4f;

        [Property("Ring Size"), DefaultPropertyValue(8)]
        [ToolTip("Размер кольцевого буфера (режим Ring). 1-64.")]
        public int RingSize { get; set; } = 8;

        public override PipelinePosition Position => PipelinePosition.PreTransform;

        // ── Состояние ─────────────────────────────────────────────
        readonly object _lock = new object();
        float _rawX, _rawY;
        bool _hasInput = false;
        bool _newInput = false;
        bool _penActive = false;

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

        static readonly Stopwatch _sw = Stopwatch.StartNew();
        long _lastTick = -1;
        long _lastInputTime = 0;

        // Скорость предикции — обновляется только при реальных пакетах
        float _predDtAccum = 0f; // накопленный dt между реальными пакетами

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
                    _penActive = true;
                    _lastInputTime = _sw.ElapsedMilliseconds;

                    int m = Mode;
                    if (m != _lastMode)
                    {
                        _first = true;
                        _lastTick = -1;
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

            float rawX, rawY;
            bool isNew;
            bool active;

            lock (_lock)
            {
                if (!_hasInput) return;

                rawX = _rawX;
                rawY = _rawY;
                isNew = _newInput;
                _newInput = false;

                // Таймаут 300ms — не 100ms, чтобы не даблкликало
                long elapsed = _sw.ElapsedMilliseconds - _lastInputTime;
                if (elapsed > 300)
                {
                    _penActive = false;
                }
                active = _penActive;
            }

            if (!active)
            {
                // НЕ ресетим _first — фильтр продолжит с того же места
                // Только сбрасываем dt чтобы не было гигантского прыжка
                _lastTick = -1;
                return;
            }

            long now = _sw.ElapsedTicks;
            float dt;
            if (_lastTick < 0)
                dt = 1f;
            else
            {
                double ms = (now - _lastTick) / (double)Stopwatch.Frequency * 1000.0;
                dt = (float)Math.Max(0.1, Math.Min(50.0, ms));
            }
            _lastTick = now;

            // Накапливаем dt между реальными пакетами для расчёта скорости
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
                vf = sf;
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

            float adz = DeadzoneUnits * (1f - vf * (ProMode ? 1f : 0.95f));
            float tx = dist < adz ? _fX : rawX;
            float ty = dist < adz ? _fY : rawY;

            float ba = SmoothMs > 0.01f ? dt / (SmoothMs + dt) : 1f;
            float al = ba + (1f - ba) * (ProMode ? vf * vf : vf);

            _fX += (tx - _fX) * al;
            _fY += (ty - _fY) * al;

            // Скорость предикции — считаем только при РЕАЛЬНОМ новом пакете
            // Используем накопленный dt между пакетами, а не тиковый dt
            if (isNew)
            {
                float realDt = Math.Max(_predDtAccum, 0.5f);
                float rvx = (rawX - _prevRawX) / realDt;
                float rvy = (rawY - _prevRawY) / realDt;
                float pa = Math.Min(realDt / (5f + realDt), 0.5f);
                _predVX += (rvx - _predVX) * pa;
                _predVY += (rvy - _predVY) * pa;
                _prevRawX = rawX;
                _prevRawY = rawY;
                _predDtAccum = 0f;
            }

            outX = _fX;
            outY = _fY;

            // Предикция — PredMs напрямую как смещение в мс
            // НЕ умножаем на dt, только на скорость
            float ep = ProMode ? 0f : PredMs * (1f - vf);
            if (ep > 0.01f)
            {
                float px = _fX + _predVX * ep;
                float py = _fY + _predVY * ep;
                float s = MathF.Sqrt(_predVX * _predVX + _predVY * _predVY);
                float md = s * ep * 1.5f + 0.5f;
                float pdx = px - _fX, pdy = py - _fY;
                float pd = MathF.Sqrt(pdx * pdx + pdy * pdy);
                if (pd > md && pd > 0f)
                {
                    float sc = md / pd;
                    px = _fX + pdx * sc;
                    py = _fY + pdy * sc;
                }
                outX = px;
                outY = py;
            }
        }

        // ── Kalman ────────────────────────────────────────────────
        void RunKalman(float rawX, float rawY, float dt, bool isNew, out float outX, out float outY)
        {
            float kDt = Math.Max(0.5f, Math.Min(20f, dt));

            float ks = MathF.Sqrt(
                _kalman.VelocityX * _kalman.VelocityX +
                _kalman.VelocityY * _kalman.VelocityY);

            float adz = DeadzoneUnits > 0f
                ? DeadzoneUnits * (1f - Math.Max(0f, Math.Min(1f, ks * kDt / (DeadzoneUnits + 1f))))
                : 0f;

            float dx = rawX - _fX, dy = rawY - _fY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float mx = dist < adz && adz > 0f ? _fX : rawX;
            float my = dist < adz && adz > 0f ? _fY : rawY;

            _kalman.Update(mx, my, kDt, out float kX, out float kY);

            if (isNew)
            {
                float rawDist = MathF.Sqrt(
                    (rawX - _prevRawX) * (rawX - _prevRawX) +
                    (rawY - _prevRawY) * (rawY - _prevRawY));
                float idle = Math.Max(1.5f, adz * 0.5f);

                if (rawDist < idle)
                {
                    _kalman.DampVelocity(kDt / (15f + kDt));
                    kX = _fX + (rawX - _fX) * 0.3f;
                    kY = _fY + (rawY - _fY) * 0.3f;
                }
                _prevRawX = rawX;
                _prevRawY = rawY;
            }

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