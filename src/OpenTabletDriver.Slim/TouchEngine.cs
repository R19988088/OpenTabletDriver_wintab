using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Tablet;
using OpenTabletDriver.Plugin.Tablet.Touch;
using OpenTabletDriver.Plugin.Tablet.Wheel;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 触摸 / 环管线：从 OTD 的报文流里挑出两类输入，翻译成滚轮和快捷键。
    ///
    ///   0x11（IntuosV2AuxReport）  —— 环的绝对位置（0..71，report[4] 的 bit7 是"有效"标志）+ 环按钮
    ///   0x21 / 0xD2（TouchReport） —— 最多 5 个触点的坐标（0..8959 / 0..5919）
    ///
    /// 不自己开 HID：报文是 DeviceReader 读出来的，经 InputDeviceTree → OutputMode.Read →
    /// WintabOutputMode.ReportReceived 送到这里。触摸口和笔口共用同一条读取线程，
    /// 不会出现两个句柄抢同一个接口；也因此笔的压感和这个管线完全解耦。
    ///
    /// 三个开关（撤销 / 旋转 / 缩放）存在 feel.json 的 TouchUndo / TouchRotate / TouchZoom，
    /// 每次手势都先读一遍（1 秒缓存），关掉就只计数、不注入。
    /// </summary>
    public sealed class TouchEngine
    {
        // PTH-660 的环量程：PTH-660.json 里 Wheels.AbsoluteWheelMax = 71，即 0..71 共 72 格。
        private const int RingMax = 71;

        // 环每转过 3 格算一个滚轮刻度（WHEEL_DELTA = 120），太小会抖、太大会顿。
        private const int RingUnitsPerNotch = 3;

        // 双指缩放的触发阈值：两指间距相对上一次变化超过 15% 才动一次，回到 ±7% 内才重新武装，
        // 这样一次捏合只缩一档，不会连续刷屏。
        private const float ZoomFireRatio = 1.15f;
        private const float ZoomRearmLow = 0.93f;
        private const float ZoomRearmHigh = 1.07f;

        // 旋转：两指连线角度变化 ≥ 12° 才发一次，两次之间至少隔 350ms。
        private const float RotateFireDegrees = 12f;
        private const long RotateCooldownMs = 350;

        // 三指撤销：手势结束后 400ms 内不重复触发，避免抬手抖动连发 Ctrl+Z。
        private const long UndoCooldownMs = 400;

        private FeelConfig _config = FeelConfig.Load();
        private long _configTick = Environment.TickCount64;

        // 环：上一次读到的绝对位置，以及没攒够一格的余量
        private int? _lastRing;
        private double _wheelAccum;

        // 触点：一次手势里最多同时按了几根手指、这次手势是否已经触发过撤销
        private int _gesturePoints;
        private bool _gestureFired;
        private float _lastDistance;
        private float _lastAngle;
        private bool _zoomArmed = true;
        private long _lastRotateAt;
        private long _lastUndoAt;

        /// <summary>收到 0x11 报文的数量。</summary>
        public long RingReports { get; private set; }

        /// <summary>收到 0x21 / 0xD2 报文的数量（真机上目前一直是 0，见 docs/otd-slim.md）。</summary>
        public long TouchReports { get; private set; }

        /// <summary>既不是环也不是触点的报文数量（例如 0x13 按键报文）。</summary>
        public long OtherReports { get; private set; }

        public int UndoCount { get; private set; }
        public int ZoomCount { get; private set; }
        public int RotateCount { get; private set; }

        /// <summary>最后一包的解析类型，用来在界面上判断"到底有没有触摸报文进来"。</summary>
        public string LastKind { get; private set; } = "-";

        /// <summary>最后一次动作，例如 "缩放 +1" / "撤销"。</summary>
        public string LastAction { get; private set; } = "无";

        /// <summary>
        /// 注入动作都排到这条后台线程上执行。Feed 跑在 OTD 的设备读取线程上，
        /// 和笔的报文是同一个线程；SendInput 万一被目标程序卡住，绝不能拖住笔。
        /// </summary>
        private readonly BlockingCollection<Action> _actions = new(128);
        private readonly Thread _worker;

        public TouchEngine()
        {
            _worker = new Thread(Pump)
            {
                IsBackground = true,
                Name = "OTD Touch Actions",
                Priority = ThreadPriority.BelowNormal
            };
            _worker.Start();
        }

        private void Pump()
        {
            foreach (var action in _actions.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch
                {
                    // 注入失败不能把辅助线程带崩
                }
            }
        }

        private void Post(Action action)
        {
            try
            {
                _actions.TryAdd(action);
            }
            catch
            {
                // 队列已关/满：宁可丢这一次手势，也不要卡住笔
            }
        }

        /// <summary>喂一包报文。必须在输出线程上调用（InputDeviceTree 已经串行化了）。</summary>
        public void Feed(IDeviceReport? report)
        {
            if (report == null)
                return;

            // 笔自己的报文（0x10 / 0x1E）每支笔一秒几百包，这里一概不碰，
            // 免得计数和字符串拼接拖慢压感路径。
            if (report is ITabletReport)
                return;

            LastKind = report.GetType().Name;
            RefreshConfig();

            // 环的报文同时实现 IAuxReport 和 IAbsoluteWheelReport，必须先认环，
            // 否则会被当成普通辅助键吞掉。
            if (report is IAbsoluteWheelReport wheel)
            {
                HandleRing(wheel);
                return;
            }

            if (report is ITouchReport touch)
            {
                HandleTouch(touch);
                return;
            }

            if (report is IAuxReport)
            {
                // 认得出形状但没给环位置（bit7 没置位）：只计数
                RingReports++;
                return;
            }

            OtherReports++;
        }

        /// <summary>界面刷新用的一行摘要。</summary>
        public string Describe() =>
            $"环 {RingReports} 包 · 触点 {TouchReports} 包 · 撤销 {UndoCount} / 缩放 {ZoomCount} / 旋转 {RotateCount} · 最近 {LastKind} {LastAction}";

        private void RefreshConfig()
        {
            var now = Environment.TickCount64;
            if (now - _configTick < 1000)
                return;

            _configTick = now;
            _config = FeelConfig.Load();
        }

        private void HandleRing(IAbsoluteWheelReport report)
        {
            RingReports++;

            var positions = report.AnalogPositions;
            if (positions == null || positions.Length == 0)
                return;

            var reading = positions[0];
            if (reading == null)
                return; // 这一包没有环位置（"有效"位没置起来）

            var current = (int)reading.Value;
            if (current < 0 || current > RingMax)
                return;

            if (_lastRing is { } last)
            {
                var delta = current - last;

                // 0 和 71 首尾相接，转过端点时要当成一小步而不是一整圈
                if (delta > RingMax / 2)
                    delta -= RingMax + 1;
                else if (delta < -(RingMax / 2))
                    delta += RingMax + 1;

                if (delta != 0)
                {
                    _wheelAccum += delta;

                    while (_wheelAccum >= RingUnitsPerNotch)
                    {
                        _wheelAccum -= RingUnitsPerNotch;
                        Post(() => HotkeyEngine.Wheel(-120));
                        LastAction = "环→下滚";
                    }

                    while (_wheelAccum <= -RingUnitsPerNotch)
                    {
                        _wheelAccum += RingUnitsPerNotch;
                        Post(() => HotkeyEngine.Wheel(120));
                        LastAction = "环→上滚";
                    }
                }
            }

            _lastRing = current;
        }

        private void HandleTouch(ITouchReport report)
        {
            TouchReports++;

            var points = new List<TouchPoint>(5);
            foreach (var point in report.Touches)
            {
                if (point != null)
                    points.Add(point);
            }

            if (points.Count == 0)
            {
                FinishGesture();
                return;
            }

            if (points.Count > _gesturePoints)
                _gesturePoints = points.Count;

            if (points.Count != 2)
            {
                _lastDistance = 0f;
                _lastAngle = 0f;
                return;
            }

            var a = points[0].Position;
            var b = points[1].Position;
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            var angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;

            if (_lastDistance > 1f)
            {
                TryZoom(distance / _lastDistance);
                TryRotate(angle);
            }

            _lastDistance = distance;
            _lastAngle = angle;
        }

        private void TryZoom(float ratio)
        {
            if (!_config.TouchZoom)
                return;

            if (_zoomArmed && (ratio > ZoomFireRatio || ratio < 2f - ZoomFireRatio))
            {
                // ratio > 1 = 两指张开 = 放大；< 1 = 捏合 = 缩小
                Zoom(ratio > 1f);
                _zoomArmed = false;
                return;
            }

            if (!_zoomArmed && ratio > ZoomRearmLow && ratio < ZoomRearmHigh)
                _zoomArmed = true;
        }

        private void TryRotate(float angle)
        {
            if (!_config.TouchRotate)
                return;
            if (_lastDistance <= 1f)
                return;

            var delta = NormalizeAngle(angle - _lastAngle);
            if (MathF.Abs(delta) < RotateFireDegrees)
                return;

            var now = Environment.TickCount64;
            if (now - _lastRotateAt < RotateCooldownMs)
                return;

            Rotate(delta > 0f);
            _lastRotateAt = now;
        }

        private void FinishGesture()
        {
            if (_gesturePoints >= 3 && !_gestureFired && _config.TouchUndo)
            {
                var now = Environment.TickCount64;
                if (now - _lastUndoAt > UndoCooldownMs)
                {
                    _lastUndoAt = now;
                    _gestureFired = true;
                    Undo();
                }
            }

            _gesturePoints = 0;
            _gestureFired = false;
            _lastDistance = 0f;
            _lastAngle = 0f;
            _zoomArmed = true;
        }

        private void Undo()
        {
            Post(() => Chord(["Ctrl"], WindowsKeyboard.VirtualKey("Z")));
            UndoCount++;
            LastAction = "撤销";
        }

        private void Zoom(bool zoomIn)
        {
            Post(() => Chord(["Ctrl"], WindowsKeyboard.VirtualKey(zoomIn ? "PLUS" : "MINUS")));
            ZoomCount++;
            LastAction = zoomIn ? "缩放 放大" : "缩放 缩小";
        }

        private void Rotate(bool clockwise)
        {
            Post(() => Chord(["Ctrl", "Alt"], WindowsKeyboard.VirtualKey(clockwise ? "RIGHT" : "LEFT")));
            RotateCount++;
            LastAction = clockwise ? "旋转 顺时针" : "旋转 逆时针";
        }

        /// <summary>按下修饰键 + 主键，然后整体松开。</summary>
        private static void Chord(string[] modifiers, ushort key)
        {
            try
            {
                WindowsKeyboard.Press(modifiers, key);
                WindowsKeyboard.Release(modifiers, key);
            }
            catch
            {
                // 注入失败不该把触摸线程带崩
            }
        }

        private static float NormalizeAngle(float degrees)
        {
            while (degrees > 180f)
                degrees -= 360f;
            while (degrees < -180f)
                degrees += 360f;
            return degrees;
        }
    }
}
