using System;
using System.IO;
using System.Numerics;
using OpenTabletDriver.Plugin.Platform.Pointer;

namespace OpenTabletDriver.Wintab
{
    /// <summary>
    /// Forwards every pen event to OTD's own pointer (cursor movement, clicks,
    /// scroll) and mirrors the same sample into the Wintab32 shared buffer, so a
    /// Wintab application sees the identical stream the desktop does.
    /// </summary>
    public sealed class WintabPointer : IAbsolutePointer, IPressureHandler, ITiltHandler,
        IEraserHandler, IRotationHandler, IHoverDistanceHandler, IPenActionHandler,
        ISynchronousPointer, IDisposable
    {
        private readonly IAbsolutePointer native;
        private WintabSharedBuffer? buffer;

        private int screenX;
        private int screenY;
        private uint pressure;
        private int tiltX;
        private int tiltY;
        private int rotation;
        /// <summary>
        /// Wintab pkButtons: low word is the button number, high word is the
        /// transition code (TBN_DOWN = 2, TBN_UP = 1).
        /// </summary>
        private uint buttonNumber;
        private bool inRange;
        private bool eraser;

        public WintabPointer(IAbsolutePointer native)
        {
            this.native = native;
        }

        /// <summary>Digitizer range, used to map screen pixels back to tablet units.</summary>
        public void Configure(int maxX, int maxY, uint maxPressure)
        {
            if (buffer != null && buffer.Width == maxX && buffer.Height == maxY && buffer.MaxPressure == maxPressure)
                return;

            buffer?.Dispose();
            buffer = new WintabSharedBuffer(maxX, maxY, maxPressure);
        }

        public void ConfigureDisplay(Vector2 displaySize)
        {
            if (displaySize.X > 0 && displaySize.Y > 0)
                display = displaySize;
        }

        private Vector2 display = new(3840, 2160);

        /// <summary>
        /// File switch `%LOCALAPPDATA%\OTDWintab\pen-only.txt` containing `1`
        /// stops this pointer from forwarding anything to OTD's own mouse
        /// pointer. The desktop is then driven exclusively by the synthetic
        /// PEN pointer the Wintab32 shim injects, which is what an application
        /// that reads pressure from the system pen needs: with a mouse also
        /// present it takes the mouse path and sees pressure as zero.
        /// </summary>
        private static bool penOnly()
        {
            var now = Environment.TickCount64;
            if (now - penOnlyChecked < 1000)
                return penOnlyCached;

            penOnlyChecked = now;
            try
            {
                var dir = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                penOnlyCached = !string.IsNullOrEmpty(dir) &&
                    File.ReadAllText(Path.Combine(dir!, "OTDWintab", "pen-only.txt")).Trim().StartsWith("1");
            }
            catch
            {
                penOnlyCached = false;
            }
            return penOnlyCached;
        }

        private static long penOnlyChecked;
        private static bool penOnlyCached;

        /// <summary>
        /// File switch `%LOCALAPPDATA%\OTDWintab\pen-contact.txt` containing `1`:
        /// while the tip is down this pointer stops feeding OTD's mouse, so the
        /// synthetic PEN the shim injects owns the stroke and delivers pressure
        /// to the application. Hover, menus and normal clicking stay on the
        /// mouse, which keeps a single cursor on screen.
        /// </summary>
        private static bool contactMode()
        {
            var now = Environment.TickCount64;
            if (now - contactChecked < 1000)
                return contactCached;

            contactChecked = now;
            try
            {
                var dir = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                contactCached = !string.IsNullOrEmpty(dir) &&
                    File.ReadAllText(Path.Combine(dir!, "OTDWintab", "pen-contact.txt")).Trim().StartsWith("1");
            }
            catch
            {
                contactCached = false;
            }
            return contactCached;
        }

        private static long contactChecked;
        private static bool contactCached;
        private bool tipDown;

        private bool suppressNative() => penOnly() || (contactMode() && tipDown);

        public void SetPosition(Vector2 pos)
        {
            SetPositionCalls++;
            screenX = (int)Math.Round(pos.X);
            screenY = (int)Math.Round(pos.Y);
            inRange = true;

            // OTD's pointer owns cursor movement and click injection.
            if (!suppressNative())
                native.SetPosition(pos);
        }

        /// <summary>
        /// The digitizer position in the tablet's own units, captured before OTD transforms it
        /// into screen pixels. Wintab drivers report tablet units; applications map them onto
        /// their canvas through the context's in/out extents. Publishing screen pixels makes
        /// that mapping measure two already-transformed spaces, so strokes land shifted.
        /// </summary>
        private int tabletX, tabletY;
        private bool hasTabletPosition;

        public void SetTabletPosition(Vector2 raw)
        {
            tabletX = (int)Math.Round(raw.X);
            tabletY = (int)Math.Round(raw.Y);
            hasTabletPosition = true;
            // Publishing only from Flush() relied on the pipeline calling
            // ISynchronousPointer.Flush(), which it does not: the shared buffer
            // stayed frozen and Wintab clients saw no packets at all. This
            // override runs exactly once per report, so publish here instead.
            Publish();
        }

        internal static int SetPressureCalls, SetPositionCalls, FlushCalls, PublishCalls;

        public void SetPressure(float percentage)
        {
            SetPressureCalls++;
            var max = buffer?.MaxPressure ?? WintabPressure.Max;
            pressure = percentage <= 0 ? 0u : (uint)Math.Clamp(Math.Round(percentage * max), 1, max);
            if (native is IPressureHandler handler)
                handler.SetPressure(percentage);
        }

        public void SetTilt(Vector2 tilt)
        {
            tiltX = (int)Math.Round(tilt.X);
            tiltY = (int)Math.Round(tilt.Y);
            if (native is ITiltHandler handler)
                handler.SetTilt(tilt);
        }

        public void SetRotation(float percentage)
        {
            rotation = (int)Math.Round(percentage * 1800.0 - 900.0);
            if (native is IRotationHandler handler)
                handler.SetRotation(percentage);
        }

        public void SetEraser(bool isEraser)
        {
            eraser = isEraser;
            if (native is IEraserHandler handler)
                handler.SetEraser(isEraser);
        }

        public void SetHoverDistance(uint distance)
        {
            if (native is IHoverDistanceHandler handler)
                handler.SetHoverDistance(distance);
        }

        private const uint TBN_UP = 1;
        private const uint TBN_DOWN = 2;

        /// <summary>
        /// pkButtons in absolute mode is a state bitmask, not a transition code: bit N set
        /// means button N is currently held. (The TBN_DOWN/TBN_UP codes belong in
        /// btnUpMask/btnDnMask, and in relative mode's pkButtons.) Applications test bit 0
        /// for the tip and report no pressure while it is clear, so latching the state is
        /// required — a one-shot event leaves every packet looking like a lifted pen.
        /// </summary>
        private uint buttonState;
        private bool tipForwarded;

        public void Activate(PenAction action)
        {
            buttonState |= 1u << (int)ButtonCode(action);
            if (action == PenAction.Tip)
            {
                // Remember whether this press reached OTD's mouse, so the
                // matching release never arrives as a stray mouse-up.
                tipDown = true;
                tipForwarded = !suppressNative();
                if (tipForwarded) Forward(action, down: true);
                return;
            }
            if (!suppressNative()) Forward(action, down: true);
        }

        public void Deactivate(PenAction action)
        {
            buttonState &= ~(1u << (int)ButtonCode(action));
            if (action == PenAction.Tip)
            {
                if (tipForwarded) Forward(action, down: false);
                tipForwarded = false;
                tipDown = false;
                return;
            }
            if (!suppressNative()) Forward(action, down: false);
        }

        /// <summary>
        /// Sends the click to OTD's pointer. The platform pointer only implements
        /// IMouseButtonHandler, so pen actions must be translated to mouse events.
        /// </summary>
        private void Forward(PenAction action, bool down)
        {
            if (penOnly())
                return;

            if (native is IPenActionHandler penHandler)
            {
                if (down) penHandler.Activate(action);
                else penHandler.Deactivate(action);
                return;
            }

            if (native is not IMouseButtonHandler mouseHandler)
                return;

            var button = ToMouseButton(action);
            if (button == MouseButton.None)
                return;

            if (down) mouseHandler.MouseDown(button);
            else mouseHandler.MouseUp(button);
        }

        private static MouseButton ToMouseButton(PenAction action) => action switch
        {
            PenAction.Tip => MouseButton.Left,
            PenAction.Eraser => MouseButton.Left,
            PenAction.BarrelButton1 => MouseButton.Right,
            PenAction.BarrelButton2 => MouseButton.Middle,
            _ => MouseButton.None
        };

        /// <summary>Wintab cursor button numbering: 0 tip, 1-3 barrel, 4 eraser.</summary>
        private static uint ButtonCode(PenAction action) => action switch
        {
            PenAction.Tip => 0u,
            PenAction.BarrelButton1 => 1u,
            PenAction.BarrelButton2 => 2u,
            PenAction.BarrelButton3 => 3u,
            PenAction.Eraser => 4u,
            _ => 0u
        };

        public void Flush()
        {
            FlushCalls++;
            Publish();
            if (!penOnly() && native is ISynchronousPointer pointer)
                pointer.Flush();
        }

        public void Reset()
        {
            inRange = false;
            pressure = 0;
            buttonState = 0;
            Publish();
            if (!penOnly() && native is ISynchronousPointer pointer)
                pointer.Reset();
        }

        private void Publish()
        {
            PublishCalls++;
            if (buffer == null)
                return;

            // Tablet units, scaled only if the device reports beyond the digitizer extent
            // (defensive: the context advertises exactly maxX/maxY).
            //
            // Screen pixels must never reach this buffer: consumers divide by the
            // digitizer extent, so a publish that still carried screen coordinates
            // (0..3840 instead of 0..44800) mapped to the top-left corner and made
            // the cursor jump between the pen position and the origin.
            if (!hasTabletPosition)
                return;

            var maxX = buffer.Width;
            var maxY = buffer.Height;
            var x = Math.Clamp(tabletX, 0, maxX);
            var y = Math.Clamp(tabletY, 0, maxY);

            buffer.Publish(x, y, pressure, tiltX, tiltY, rotation, buttonState, inRange);

            // One line the first time a non-zero pressure reaches the ring: separates "the
            // tablet never pressed" from "the ring got pressure and the shim did not".
            if (pressure > 0 && !firstPressureLogged)
            {
                firstPressureLogged = true;
                WintabDiag.Write($"FIRST pressure to shm x={x} y={y} p={pressure} buttons=0x{buttonState:X} inRange={inRange}");
            }
        }

        private static bool firstPressureLogged;

        public void Dispose()
        {
            buffer?.Dispose();
            buffer = null;
        }
    }
}
