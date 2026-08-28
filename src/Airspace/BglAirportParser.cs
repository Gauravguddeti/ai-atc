using Microsoft.Extensions.Logging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MsfsAiAtc.Airspace;

/// <summary>
/// Parses MSFS 2020/2024 BGL scenery files to extract airport, runway, and taxiway data.
///
/// BGL files are binary containers (FSX-compatible format used by MSFS) that hold
/// airport records including:
///  - ICAO identifier and name
///  - Runway designators (25L, 07R, etc.)
///  - Taxiway path names (Alpha, Bravo, C, etc.)
///  - Parking spots (gates, ramps)
///
/// Design choices:
///  - We scan for the airport record signature (0x3C / 60) rather than navigating
///    the full BGL section table — more resilient to BGL format variations.
///  - Community folder BGLs are loaded AFTER Official BGLs so they override
///    default airports (same ICAO = addon wins, just like MSFS itself).
///  - Entirely defensive: any parse error is logged and skipped, never crashes.
///  - Cache-friendly: results exposed as plain C# records (serialisable to JSON).
/// </summary>
public class BglAirportParser
{
    private readonly ILogger<BglAirportParser> _logger;

    // Airport record identifier in FSX/MSFS BGLs
    private const byte AirportRecordId = 0x3C;  // 60 decimal

    // Maximum size of a single airport record (sanity check to prevent runaway reads)
    private const int MaxAirportRecordBytes = 512 * 1024; // 512 KB

    // Sub-record identifiers within an airport record
    private const ushort SubRec_Runway     = 0x04;
    private const ushort SubRec_TaxiName   = 0x24; // TaxiwayName array
    private const ushort SubRec_TaxiPath   = 0x22; // TaxiwayPath (references name index)
    private const ushort SubRec_Parking    = 0x23; // Parking/gate record
    private const ushort SubRec_ApronEdge  = 0x30;

    public BglAirportParser(ILogger<BglAirportParser> logger)
    {
        _logger = logger;
    }

    // ─── Public scan API ─────────────────────────────────────────────────────

    /// <summary>
    /// Scans a folder tree for .bgl files and extracts all airport records.
    /// Official folder scanned first, then Community (Community overrides Official).
    /// Returns a dictionary keyed by ICAO code.
    /// </summary>
    public Dictionary<string, BglAirportData> ScanFolders(
        string officialFolder, string communityFolder,
        IProgress<string>? progress = null)
    {
        var results = new Dictionary<string, BglAirportData>(StringComparer.OrdinalIgnoreCase);
        int parsed = 0, skipped = 0;

        // Official first — Community overrides
        foreach (var folder in new[] { officialFolder, communityFolder })
        {
            if (!Directory.Exists(folder)) continue;

            var bglFiles = Directory
                .EnumerateFiles(folder, "*.bgl", SearchOption.AllDirectories)
                .ToList();

            _logger.LogInformation("Scanning {Count} BGL files in {Folder}", bglFiles.Count, folder);

            foreach (var bglFile in bglFiles)
            {
                progress?.Report(Path.GetFileName(bglFile));

                var airports = ParseFile(bglFile);
                if (airports.Count == 0) { skipped++; continue; }

                parsed++;
                foreach (var ap in airports)
                    results[ap.IcaoIdent] = ap; // Community overwrites Official
            }
        }

        _logger.LogInformation(
            "BGL scan complete — {Airports} airports from {Files} files ({Skip} skipped/no airports)",
            results.Count, parsed, skipped);

        return results;
    }

    // ─── File-level parsing ───────────────────────────────────────────────────

    public List<BglAirportData> ParseFile(string filePath)
    {
        var airports = new List<BglAirportData>();

        try
        {
            using var fs   = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br   = new BinaryReader(fs, Encoding.ASCII);

            // BGL files must be at least 56 bytes (minimum valid header)
            if (fs.Length < 56) return airports;

            // We use a "signature scan" approach:
            // Search the file for the airport record byte (0x3C) preceded by
            // what looks like a valid airport record size field.
            // This is more robust than parsing the full section table.
            airports.AddRange(ScanForAirportRecords(br, filePath));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Skip {File}: {Err}", Path.GetFileName(filePath), ex.Message);
        }

        return airports;
    }

    // ─── Core record scanner ──────────────────────────────────────────────────

    private List<BglAirportData> ScanForAirportRecords(BinaryReader br, string filePath)
    {
        var airports = new List<BglAirportData>();
        var stream   = br.BaseStream;
        long length  = stream.Length;

        // Read BGL header to find section table
        // BGL file header: first 56 bytes contain section count at offset 0x14 (20)
        // and section table starts at the header size (usually 56)
        stream.Position = 0;
        var header = br.ReadBytes(56);

        if (header.Length < 56) return airports;

        // Try to use section table first (more accurate than scanning)
        var sectionCountOffset = 20; // 0x14
        int sectionCount = BitConverter.ToInt32(header, sectionCountOffset);

        if (sectionCount > 0 && sectionCount < 2000)
        {
            airports.AddRange(ParseViaSectionTable(br, header, sectionCount, filePath));
            if (airports.Count > 0) return airports;
        }

        // Fallback: brute-force scan for airport record signature
        airports.AddRange(BruteForceAirportScan(br, filePath));
        return airports;
    }

    private List<BglAirportData> ParseViaSectionTable(
        BinaryReader br, byte[] header, int sectionCount, string filePath)
    {
        var airports = new List<BglAirportData>();
        var stream   = br.BaseStream;

        // Section table starts at header size (usually 56 = 0x38)
        // Each section entry is 24 bytes
        const int SectionEntrySize = 24;
        int headerSize = 56;

        try
        {
            for (int i = 0; i < sectionCount; i++)
            {
                long sectionOffset = headerSize + (long)i * SectionEntrySize;
                if (sectionOffset + SectionEntrySize > stream.Length) break;

                stream.Position = sectionOffset;
                uint sectionType     = br.ReadUInt32();
                uint unknown1        = br.ReadUInt32();
                uint numSubsections  = br.ReadUInt32();
                uint dataOffset      = br.ReadUInt32();
                uint totalPackedSize = br.ReadUInt32();

                // Airport sections: type 0x0003 or type 0x0013 (MSFS extended)
                if (sectionType != 0x0003 && sectionType != 0x0013 &&
                    sectionType != 0x0001 && sectionType != 0x0011)
                    continue;

                if (dataOffset == 0 || dataOffset >= stream.Length) continue;

                // Read subsections
                for (uint s = 0; s < numSubsections && s < 5000; s++)
                {
                    long subOffset = dataOffset + s * 8;
                    if (subOffset + 8 > stream.Length) break;

                    stream.Position = subOffset;
                    uint recordOffset = br.ReadUInt32();
                    uint recordSize   = br.ReadUInt32();

                    if (recordOffset == 0 || recordOffset >= stream.Length) continue;
                    if (recordSize == 0 || recordSize > MaxAirportRecordBytes) continue;

                    stream.Position = recordOffset;
                    var ap = TryReadAirportRecord(br, recordOffset, recordSize, filePath);
                    if (ap != null) airports.Add(ap);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Section table parse failed for {F}: {E}",
                Path.GetFileName(filePath), ex.Message);
        }

        return airports;
    }

    private List<BglAirportData> BruteForceAirportScan(BinaryReader br, string filePath)
    {
        var airports = new List<BglAirportData>();
        var stream   = br.BaseStream;

        try
        {
            // Read entire file into memory for fast scanning (most BGLs are <10 MB)
            stream.Position = 0;
            var data = br.ReadBytes((int)Math.Min(stream.Length, 20 * 1024 * 1024));

            for (int i = 0; i < data.Length - 32; i++)
            {
                // Airport record starts with record-size (4 bytes) then 0x3C (1 byte)
                // Validate: size must be reasonable, followed by 0x3C, then valid ICAO chars
                if (data[i + 4] != AirportRecordId) continue;

                uint recSize = BitConverter.ToUInt32(data, i);
                if (recSize < 60 || recSize > MaxAirportRecordBytes) continue;
                if (i + recSize > data.Length) continue;

                // Validate ICAO: 4 bytes starting at offset 8 from record start
                // Must be uppercase ASCII letters/numbers
                int icaoStart = i + 8;
                if (icaoStart + 4 > data.Length) continue;

                if (!IsValidIcao(data, icaoStart)) continue;

                // Looks like a valid airport record — parse it
                using var ms = new MemoryStream(data, i, (int)recSize);
                using var mbr = new BinaryReader(ms, Encoding.ASCII);

                var ap = TryReadAirportRecord(mbr, i, recSize, filePath);
                if (ap != null)
                {
                    airports.Add(ap);
                    i += (int)recSize - 1; // skip past this record
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Brute force scan failed for {F}: {E}",
                Path.GetFileName(filePath), ex.Message);
        }

        return airports;
    }

    // ─── Airport record parser ────────────────────────────────────────────────

    private BglAirportData? TryReadAirportRecord(
        BinaryReader br, long offset, uint size, string sourceFile)
    {
        try
        {
            var stream = br.BaseStream;
            stream.Position = offset;

            // Airport record layout (FSX BGL format, MSFS compatible):
            // +0  DWORD  RecordSize
            // +4  BYTE   RecordId (0x3C)
            // +5  BYTE   NumRunways
            // +6  BYTE   NumComs (communication frequencies)
            // +7  BYTE   NumStarts (start positions)
            // +8  DWORD  IcaoIdent (4 ASCII chars, little-endian packed)
            // +12 DWORD  RegionIdent
            // +16 FLOAT  Latitude  (degrees * (90.0 / 10001750.0) + ...)
            // +20 FLOAT  Longitude (degrees encoded)
            // +24 FLOAT  AltitudeFt (float)
            // +28 ... more fields
            // +? sub-records start

            uint recSize  = br.ReadUInt32();
            byte recId    = br.ReadByte();
            if (recId != AirportRecordId) return null;

            byte numRunways = br.ReadByte();
            byte numComs    = br.ReadByte();
            byte numStarts  = br.ReadByte();

            // ICAO is stored as a 4-byte packed value
            byte[] icaoBytes = br.ReadBytes(4);
            string icao      = DecodeIcao(icaoBytes);

            if (string.IsNullOrWhiteSpace(icao) || !IsValidIcaoString(icao))
                return null;

            // Skip region ident (4 bytes)
            uint regionIdent = br.ReadUInt32();

            // Latitude (encoded as DWORD: lat_deg = (value * 90.0) / 10001750.0
            // Actually in FSX BGL it is stored as float degrees
            uint latRaw  = br.ReadUInt32();
            uint lonRaw  = br.ReadUInt32();
            double lat   = DecodeBglLatitude(latRaw);
            double lon   = DecodeBglLongitude(lonRaw);

            // Altitude stored as floating-point meters in some versions, feet in others
            float altRaw = br.ReadSingle();
            double altFt = altRaw < 50000 ? altRaw * 3.28084 : altRaw; // heuristic

            // Skip remaining fixed header (varies, typically extends to ~56 bytes into record)
            // We'll use NumRunways and scan for sub-records
            // Advance to fixed-header end: we skip to offset 28 within record
            long recordEnd = offset + recSize;

            // Sub-record data starts at record + 56 bytes (standard FSX airport header)
            // but may vary — we scan forward
            long subStart = offset + 56;
            if (subStart >= recordEnd) subStart = offset + 28;

            stream.Position = subStart;

            var airport = new BglAirportData
            {
                IcaoIdent  = icao,
                LatitudeDeg = lat,
                LongitudeDeg = lon,
                AltitudeFt = altFt,
                SourceFile = Path.GetFileName(sourceFile),
            };

            // Parse sub-records (runways, taxiways, parking)
            ParseSubRecords(br, airport, recordEnd);

            _logger.LogDebug("Airport {Icao} ({Rwy} runways, {Twy} taxiways) from {File}",
                icao, airport.Runways.Count, airport.TaxiwayNames.Count, airport.SourceFile);

            return airport;
        }
        catch
        {
            return null;
        }
    }

    private void ParseSubRecords(BinaryReader br, BglAirportData airport, long recordEnd)
    {
        var stream = br.BaseStream;

        while (stream.Position + 6 < recordEnd)
        {
            long subStart  = stream.Position;
            ushort subId   = br.ReadUInt16();
            uint   subSize = br.ReadUInt32();

            if (subSize < 6 || subSize > MaxAirportRecordBytes) break;
            long subEnd = subStart + subSize;
            if (subEnd > recordEnd) break;

            try
            {
                switch (subId)
                {
                    case SubRec_Runway:
                        ReadRunway(br, airport);
                        break;

                    case SubRec_TaxiName:
                        ReadTaxiwayNames(br, airport, subSize);
                        break;

                    case SubRec_Parking:
                        ReadParking(br, airport, subSize);
                        break;
                }
            }
            catch { /* skip bad sub-record */ }

            // Advance to next sub-record
            stream.Position = subEnd;
        }
    }

    private static void ReadRunway(BinaryReader br, BglAirportData airport)
    {
        // Runway sub-record structure (FSX BGL):
        // +6  BYTE   Surface type
        // +7  BYTE   NumMarkings
        // +8  BYTE   Primary number (1-36)
        // +9  BYTE   Primary designator (0=None, 1=L, 2=R, 3=C, 4=W, 5=A, 6=B)
        // +10 BYTE   Secondary number
        // +11 BYTE   Secondary designator
        // +12 DWORD  Latitude (encoded)
        // +16 DWORD  Longitude (encoded)
        // +20 FLOAT  Altitude ft
        // +24 FLOAT  Heading (true degrees)
        // +28 FLOAT  Length (feet)
        // +32 FLOAT  Width (feet)

        var surface      = br.ReadByte();
        var markings     = br.ReadByte();
        var priNum       = br.ReadByte();
        var priDesig     = br.ReadByte();
        var secNum       = br.ReadByte();
        var secDesig     = br.ReadByte();

        br.ReadUInt32(); // lat
        br.ReadUInt32(); // lon
        float altFt  = br.ReadSingle();
        float heading = br.ReadSingle();
        float length  = br.ReadSingle();
        float width   = br.ReadSingle();

        string priLabel = FormatRunwayDesignator(priNum, priDesig);
        string secLabel = FormatRunwayDesignator(secNum, secDesig);

        if (!string.IsNullOrEmpty(priLabel))
            airport.Runways.Add(new BglRunway
            {
                Designation = priLabel,
                HeadingDeg  = heading,
                LengthFt    = length,
                WidthFt     = width,
            });

        if (!string.IsNullOrEmpty(secLabel))
            airport.Runways.Add(new BglRunway
            {
                Designation = secLabel,
                HeadingDeg  = (heading + 180) % 360,
                LengthFt    = length,
                WidthFt     = width,
            });
    }

    private static void ReadTaxiwayNames(BinaryReader br, BglAirportData airport, uint subSize)
    {
        // TaxiwayName sub-record: array of null-terminated strings
        // +6 WORD  Count (number of name entries)
        // Then Count * (null-terminated ASCII string)
        if (subSize < 8) return;

        ushort nameCount = br.ReadUInt16();
        if (nameCount == 0 || nameCount > 200) return;

        var names = new List<string>();
        for (int i = 0; i < nameCount; i++)
        {
            var sb = new StringBuilder();
            byte c;
            int guard = 0;
            while ((c = br.ReadByte()) != 0 && ++guard < 32)
                sb.Append((char)c);

            var name = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        airport.TaxiwayNames.AddRange(names.Distinct());
    }

    private static void ReadParking(BinaryReader br, BglAirportData airport, uint subSize)
    {
        // Parking sub-record: contains gate/ramp/tie-down info
        // +6 BYTE  PushbackType
        // +7 BYTE  ParkingType (1=Ramp GA, 2=Ramp GA small, 3=Ramp GA medium,
        //                       4=Ramp Cargo, 5=Ramp Military Cargo, 6=Ramp Military Combat,
        //                       7=Gate Small, 8=Gate Medium, 9=Gate Heavy,
        //                       10=Fuel Parking, 11=Vehicles, 12=Crew, 13=Hangar)
        // +8 BYTE  NumberSuffix (gate letter: A=1, B=2, ...)
        // +9 BYTE  Name (gate name string or prefix number)
        if (subSize < 12) return;

        byte pushback = br.ReadByte();
        byte parkType = br.ReadByte();
        byte suffix   = br.ReadByte();
        byte nameNum  = br.ReadByte();

        string typeLabel = parkType switch
        {
            7  => "Gate",
            8  => "Gate",
            9  => "Gate (Heavy)",
            1  => "Ramp GA",
            4  => "Ramp Cargo",
            _  => string.Empty,
        };

        if (string.IsNullOrEmpty(typeLabel)) return;

        string gateName = nameNum > 0
            ? $"{typeLabel} {nameNum}{(suffix > 0 && suffix <= 26 ? ((char)('A' + suffix - 1)).ToString() : string.Empty)}"
            : typeLabel;

        airport.ParkingSpots.Add(gateName);
    }

    // ─── ICAO decoding ────────────────────────────────────────────────────────

    private static string DecodeIcao(byte[] bytes)
    {
        // FSX BGL encodes ICAO as 4 bytes, packed differently depending on the version.
        // Most common: straight ASCII, null-padded.
        // Some files use a base-36 encoded integer.

        // Try straight ASCII first
        var sb = new StringBuilder();
        foreach (var b in bytes)
        {
            if (b == 0) break;
            if (b >= 32 && b <= 127) sb.Append((char)b);
            else { sb.Clear(); break; }
        }

        if (sb.Length >= 3 && sb.Length <= 4)
            return sb.ToString().ToUpperInvariant();

        // Try packed integer decode (MSFS format)
        uint packed = BitConverter.ToUInt32(bytes, 0);
        if (packed == 0) return string.Empty;

        char[] decoded = new char[5];
        const string chars = " ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        for (int i = 3; i >= 0; i--)
        {
            int idx = (int)(packed % 38);
            decoded[i] = idx < chars.Length ? chars[idx] : ' ';
            packed /= 38;
        }

        var result = new string(decoded).Trim();
        return result.Length >= 3 ? result : string.Empty;
    }

    private static bool IsValidIcao(byte[] data, int offset)
    {
        for (int i = 0; i < 4; i++)
        {
            byte b = data[offset + i];
            if (b == 0) return i >= 3; // null-terminated is OK if at least 3 chars
            if (!((b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == ' '))
                return false;
        }
        return true;
    }

    private static bool IsValidIcaoString(string s)
    {
        if (s.Length < 3 || s.Length > 5) return false;
        foreach (char c in s)
        {
            if (!char.IsLetterOrDigit(c)) return false;
        }
        return true;
    }

    // ─── Coordinate decoding ──────────────────────────────────────────────────

    private static double DecodeBglLatitude(uint raw)
    {
        // FSX BGL lat: signed 32-bit, range maps to [-90, +90]
        // Formula: lat = (raw * 90.0) / 10001750.0
        return ((int)raw * 90.0) / 10001750.0;
    }

    private static double DecodeBglLongitude(uint raw)
    {
        // FSX BGL lon: unsigned 32-bit, range maps to [-180, +180]
        return (raw * 360.0 / 4294967296.0) - 180.0;
    }

    // ─── Runway formatting ────────────────────────────────────────────────────

    private static string FormatRunwayDesignator(byte num, byte designator)
    {
        if (num == 0 || num > 36) return string.Empty;
        string suffix = designator switch
        {
            1 => "L",
            2 => "R",
            3 => "C",
            4 => "W",
            _ => string.Empty,
        };
        return $"{num:D2}{suffix}";
    }
}

// ─── Data models ─────────────────────────────────────────────────────────────

public class BglAirportData
{
    public string IcaoIdent   { get; set; } = string.Empty;
    public double LatitudeDeg  { get; set; }
    public double LongitudeDeg { get; set; }
    public double AltitudeFt   { get; set; }
    public string SourceFile   { get; set; } = string.Empty; // which BGL file this came from
    public List<BglRunway> Runways       { get; set; } = new();
    public List<string>    TaxiwayNames  { get; set; } = new();
    public List<string>    ParkingSpots  { get; set; } = new();

    /// <summary>Compact LLM-ready description of the airport layout.</summary>
    public string ToLayoutString()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Airport {IcaoIdent}");

        if (Runways.Count > 0)
        {
            var rwys = Runways.Select(r => $"{r.Designation}({r.LengthFt:F0}ft)");
            sb.Append($" | Runways: {string.Join(", ", rwys)}");
        }

        if (TaxiwayNames.Count > 0)
        {
            sb.Append($" | Taxiways: {string.Join(", ", TaxiwayNames.Take(12))}");
        }

        if (ParkingSpots.Count > 0)
        {
            sb.Append($" | Parking: {string.Join(", ", ParkingSpots.Take(6))}");
        }

        return sb.ToString();
    }
}

public class BglRunway
{
    public string Designation { get; set; } = string.Empty;
    public double HeadingDeg  { get; set; }
    public double LengthFt    { get; set; }
    public double WidthFt     { get; set; }
}
