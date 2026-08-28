using System.Runtime.InteropServices;

namespace MsfsAiAtc.Traffic;

/// <summary>
/// SimConnect struct for AI/multiplayer aircraft data.
/// Layout must exactly match the SimConnect data definition order.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct TrafficObjectData
{
    public double Latitude;
    public double Longitude;
    public double AltitudeMsl;   // feet
    public double HeadingTrue;   // degrees
    public double GroundSpeed;   // knots
    public double SimOnGround;   // bool as double
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string Title;         // aircraft type/model name
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string AtcId;         // callsign/registration
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
    public string AtcAirline;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
    public string AtcFlightNum;
}
