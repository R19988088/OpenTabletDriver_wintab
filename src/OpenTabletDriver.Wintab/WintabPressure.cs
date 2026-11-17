namespace OpenTabletDriver.Wintab
{
    /// <summary>Pressure scale shared by the plugin and the Wintab shim.</summary>
    internal static class WintabPressure
    {
        /// <summary>
        /// Upper bound reported through DVC_NPRESSURE. 32767 is the value
        /// desktop Wintab applications treat as full scale.
        /// </summary>
        public const uint Max = 32767;
    }
}
