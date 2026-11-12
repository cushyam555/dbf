using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Text;

namespace Mixer.Core;

/// <summary>Fixed-record DBF reader. Character fields deliberately preserve padding for reference-compatible grouping.</summary>
public static class DbfReader
{
    record Field(string Name, char Type, int Length);
    public static DataTable Read(string file, CancellationToken token = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var thai = Encoding.GetEncoding(874);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(input);
        var header = reader.ReadBytes(32);
        if (header.Length != 32) throw new InvalidDataException("ส่วนหัว DBF ไม่ครบ");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
        int recordSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10, 2));
        if (headerSize < 33 || recordSize < 1 || headerSize > input.Length || (long)headerSize + (long)count * recordSize > input.Length)
            throw new InvalidDataException("ขนาดหรือจำนวนระเบียน DBF ไม่ตรงกับไฟล์ กรุณาตรวจสอบฐานข้อมูล");
        if (header[15] != 0) throw new InvalidDataException("ไม่รองรับ DBF ที่เข้ารหัส");
        var fields = new List<Field>(); bool terminated = false;
        while (input.Position < headerSize)
        {
            if (reader.ReadByte() == 0x0D) { terminated = true; break; }
            input.Position--;
            var descriptor = reader.ReadBytes(32);
            if (descriptor.Length != 32) throw new InvalidDataException("รายละเอียดคอลัมน์ DBF ไม่ครบ");
            var name = Encoding.ASCII.GetString(descriptor, 0, 11).Split('\0')[0];
            fields.Add(new(name, (char)descriptor[11], descriptor[16]));
        }
        if (!terminated || fields.Count == 0 || fields.Sum(f => f.Length) + 1 > recordSize || fields.Any(f => f.Length == 0 || f.Name.Length == 0))
            throw new InvalidDataException("โครงสร้างคอลัมน์ DBF ไม่ถูกต้อง");
        // The reference decides the N column type by a decimal point in the first physical record.
        input.Position = headerSize;
        var first = count > 0 ? reader.ReadBytes(recordSize) : new byte[recordSize];
        var table = new DataTable("Songs");
        int fieldOffset = 1;
        foreach (var f in fields)
        {
            var raw = Encoding.ASCII.GetString(first, fieldOffset, f.Length);
            Type type = f.Type switch
            {
                'C' => typeof(string), 'N' => raw.Contains('.') ? typeof(decimal) : typeof(int),
                'D' or 'T' => typeof(DateTime), 'L' => typeof(bool), 'F' => typeof(double),
                _ => throw new NotSupportedException($"ไม่รองรับชนิดคอลัมน์ DBF '{f.Type}' ({f.Name})")
            };
            table.Columns.Add(f.Name, type); fieldOffset += f.Length;
        }
        input.Position = headerSize;
        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var data = reader.ReadBytes(recordSize);
            if (data.Length != recordSize) throw new InvalidDataException($"ระเบียน DBF ที่ {i + 1} ไม่ครบ");
            if (data[0] == (byte)'*') continue;
            if (data[0] != (byte)' ') throw new InvalidDataException($"สถานะระเบียน DBF ที่ {i + 1} ไม่ถูกต้อง");
            var row = table.NewRow(); int offset = 1;
            for (int n = 0; n < fields.Count; n++)
            {
                var f = fields[n]; var raw = Encoding.ASCII.GetString(data, offset, f.Length);
                row[n] = f.Type switch
                {
                    'C' => thai.GetString(data, offset, f.Length),
                    'N' => Number(raw, table.Columns[n].DataType),
                    'F' => Number(raw, typeof(double)),
                    'L' => data[offset] == (byte)'Y',
                    'D' => DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year > 1900 ? date : DBNull.Value,
                    'T' when f.Length == 8 => Timestamp(data.AsSpan(offset, 8)),
                    _ => throw new InvalidDataException($"ขนาดคอลัมน์ {f.Name} ไม่ถูกต้อง")
                };
                offset += f.Length;
            }
            table.Rows.Add(row);
        }
        return table;
    }
    static object Number(string text, Type type)
    {
        // Match the reference: unsigned decimal digits, spaces and at most one decimal point.
        bool numeric = text.Any(char.IsAsciiDigit) && text.All(c => char.IsAsciiDigit(c) || c == ' ' || c == '.') && text.Count(c => c == '.') < 2;
        if (!numeric) return Convert.ChangeType(0, type, CultureInfo.InvariantCulture);
        if (type == typeof(int)) return checked((int)decimal.Parse(text, CultureInfo.InvariantCulture));
        if (type == typeof(decimal)) return decimal.Parse(text, CultureInfo.InvariantCulture);
        return double.Parse(text, CultureInfo.InvariantCulture);
    }
    static DateTime Timestamp(ReadOnlySpan<byte> data)
    {
        int julian = BinaryPrimitives.ReadInt32LittleEndian(data[..4]);
        int millis = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        return DateTime.FromOADate(julian - 2415019).Date.AddMilliseconds(millis);
    }
}
