using System.Buffers.Binary;
using System.Text;

namespace PNMTD.Files;

/// <summary>An image as its bytes say: the type it is served as, the extension it is stored with, its size in pixels.</summary>
public sealed record ImageInfo(string ContentType, string Extension, int Width, int Height);

/// <summary>Why bytes are not taken as an image; <see cref="Message"/> is for the person.</summary>
public sealed record ImageRefusal(bool TooLarge, string Message);

/// <summary>
/// Images by their bytes (docs/locations-knowledge-plan.md §5.5): PNG, JPEG, GIF and WebP, never by the name or the type
/// sent. Their structure is walked, nothing is decoded, and no image library is needed.
/// <list type="bullet">
/// <item><b>The size</b> comes from the headers: at most <see cref="MaxSide"/> pixels a side and <see cref="MaxPixels"/>
///   (each frame of a GIF too), against decompression bombs.</item>
/// <item><b>Metadata is removed without re-encoding</b>, the image data staying byte for byte: JPEG's APP1 (EXIF, XMP),
///   APP13 (IPTC) and comments (APP0, APP2 with the ICC profile, APP14 and the rest stay); PNG's tEXt, zTXt, iTXt, eXIf
///   and tIME; WebP's EXIF and XMP chunks, with the VP8X flags and the RIFF size corrected; GIF's comments. Bytes after
///   the image's end go too.</item>
/// <item><b>Refused:</b> anything else (SVG, HEIC, AVIF, HTML named .png), a broken structure, and a polyglot: markup a
///   browser would run, anywhere in the bytes.</item>
/// </list>
/// </summary>
public static class ImageCheck
{
    public const int MaxSide = 10_000;
    public const long MaxPixels = 40_000_000;

    /// <summary>The stripped image, or why not.</summary>
    public static (byte[]? Image, ImageInfo? Info, ImageRefusal? Refusal) Check(ReadOnlySpan<byte> data)
    {
        if (LooksLikeMarkup(data))
        {
            return (null, null, NotAnImage("The file holds markup (HTML, SVG or script), so it isn't stored as an image."));
        }
        try
        {
            var result = data switch
            {
                _ when data.StartsWith(PngSignature) => Png(data),
                _ when data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF => Jpeg(data),
                _ when data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8) => Gif(data),
                _ when data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8) => WebP(data),
                _ => (null, null, NotAnImage("Not a PNG, JPEG, GIF or WebP image. Other files can be attached.")),
            };
            return result;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            return (null, null, NotAnImage("The image is damaged or incomplete."));
        }
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ImageRefusal NotAnImage(string message) => new(false, message);

    private static ImageRefusal? Dimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return NotAnImage("The image has no size.");
        }
        if (width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
        {
            return new ImageRefusal(true, $"The image has {width} × {height} pixels: at most {MaxSide:N0} a side and {MaxPixels / 1_000_000} megapixels.");
        }
        return null;
    }

    /// <summary>Signatures of markup a browser would act on, ignoring case: a PNG/HTML polyglot and its kin.</summary>
    private static bool LooksLikeMarkup(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<string> needles = ["<html", "<script", "<svg", "<!doctype", "<body", "<iframe", "<object", "<embed", "javascript:"];
        foreach (var needle in needles)
        {
            if (IndexOfIgnoreCase(data, Encoding.ASCII.GetBytes(needle)) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static int IndexOfIgnoreCase(ReadOnlySpan<byte> data, byte[] lowerNeedle)
    {
        var first = lowerNeedle[0];
        for (var i = data.IndexOf(first); i >= 0 && i <= data.Length - lowerNeedle.Length;)
        {
            var match = true;
            for (var j = 1; j < lowerNeedle.Length; j++)
            {
                var c = data[i + j];
                if ((c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c) != lowerNeedle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return i;
            }
            var next = data[(i + 1)..].IndexOf(first);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return -1;
    }

    // --- PNG: chunks after the signature, IHDR first, up to IEND ------------------------------------------------------

    private static readonly HashSet<string> PngDropped = ["tEXt", "zTXt", "iTXt", "eXIf", "tIME"];

    private static (byte[]?, ImageInfo?, ImageRefusal?) Png(ReadOnlySpan<byte> data)
    {
        var output = new MemoryStream(data.Length);
        output.Write(PngSignature);
        var position = PngSignature.Length;
        int width = 0, height = 0;
        var first = true;
        while (true)
        {
            if (position + 12 > data.Length)
            {
                throw new InvalidDataException("PNG ends without IEND");
            }
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            if (length > int.MaxValue || position + 12 + (long)length > data.Length)
            {
                throw new InvalidDataException("PNG chunk too long");
            }
            var type = Encoding.ASCII.GetString(data.Slice(position + 4, 4));
            var chunk = data.Slice(position, 12 + (int)length);
            if (first)
            {
                if (type != "IHDR" || length != 13)
                {
                    throw new InvalidDataException("PNG without IHDR");
                }
                width = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data[(position + 8)..]));
                height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data[(position + 12)..]));
                if (Dimensions(width, height) is { } refusal)
                {
                    return (null, null, refusal);
                }
                first = false;
            }
            if (!PngDropped.Contains(type))
            {
                output.Write(chunk);
            }
            position += chunk.Length;
            if (type == "IEND")
            {
                break;
            }
        }
        return (output.ToArray(), new ImageInfo("image/png", "png", width, height), null);
    }

    // --- JPEG: marker segments up to the first scan, entropy-coded data, more segments and scans, EOI ------------------

    private static (byte[]?, ImageInfo?, ImageRefusal?) Jpeg(ReadOnlySpan<byte> data)
    {
        var output = new MemoryStream(data.Length);
        output.Write(data[..2]);
        var position = 2;
        int width = 0, height = 0;
        while (true)
        {
            // Fill bytes (0xFF) may precede a marker.
            if (position >= data.Length || data[position] != 0xFF)
            {
                throw new InvalidDataException("JPEG: a marker expected");
            }
            while (position < data.Length && data[position] == 0xFF)
            {
                position++;
            }
            var marker = data[position++];
            if (marker == 0xD9)
            {
                output.Write([0xFF, 0xD9]);
                break;
            }
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                // Markers without a length.
                output.Write([0xFF, marker]);
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (length < 2 || position + length > data.Length)
            {
                throw new InvalidDataException("JPEG segment too long");
            }
            var segment = data.Slice(position, length);
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF && width == 0)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(segment[5..]);
                if (Dimensions(width, height) is { } refusal)
                {
                    return (null, null, refusal);
                }
            }
            // APP1 (EXIF, XMP), APP13 (IPTC) and comments go.
            var dropped = marker is 0xE1 or 0xED or 0xFE;
            if (!dropped)
            {
                output.Write([0xFF, marker]);
                output.Write(segment);
            }
            position += length;
            if (marker == 0xDA)
            {
                // The scan's data, up to the next marker that isn't a byte stuffing (FF 00) or a restart (FF D0–D7).
                var start = position;
                while (true)
                {
                    var next = data[position..].IndexOf((byte)0xFF);
                    if (next < 0)
                    {
                        throw new InvalidDataException("JPEG scan without an end");
                    }
                    position += next;
                    var after = data[position + 1];
                    if (after == 0x00 || after is >= 0xD0 and <= 0xD7)
                    {
                        position += 2;
                        continue;
                    }
                    break;
                }
                output.Write(data[start..position]);
            }
        }
        if (width == 0)
        {
            throw new InvalidDataException("JPEG without a frame");
        }
        return (output.ToArray(), new ImageInfo("image/jpeg", "jpg", width, height), null);
    }

    // --- GIF: header, logical screen, colour table, then blocks up to the trailer ---------------------------------------

    private static (byte[]?, ImageInfo?, ImageRefusal?) Gif(ReadOnlySpan<byte> data)
    {
        var output = new MemoryStream(data.Length);
        var width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        if (Dimensions(width, height) is { } refusal)
        {
            return (null, null, refusal);
        }
        var flags = data[10];
        var position = 13 + ((flags & 0x80) != 0 ? 3 * (1 << ((flags & 0x07) + 1)) : 0);
        if (position > data.Length)
        {
            throw new InvalidDataException("GIF colour table cut off");
        }
        output.Write(data[..position]);
        while (true)
        {
            var introducer = data[position];
            if (introducer == 0x3B)
            {
                output.WriteByte(0x3B);
                break;
            }
            if (introducer == 0x21)
            {
                var label = data[position + 1];
                var end = SubBlocks(data, position + 2);
                if (label != 0xFE)
                {
                    output.Write(data[position..end]);
                }
                position = end;
                continue;
            }
            if (introducer == 0x2C)
            {
                var frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(data[(position + 5)..]);
                var frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(data[(position + 7)..]);
                if (Dimensions(frameWidth, frameHeight) is { } frame)
                {
                    return (null, null, frame);
                }
                var frameFlags = data[position + 9];
                var tableEnd = position + 10 + ((frameFlags & 0x80) != 0 ? 3 * (1 << ((frameFlags & 0x07) + 1)) : 0);
                // The LZW minimum code size, then the image data's sub-blocks.
                var end = SubBlocks(data, tableEnd + 1);
                output.Write(data[position..end]);
                position = end;
                continue;
            }
            throw new InvalidDataException("GIF: unknown block");
        }
        return (output.ToArray(), new ImageInfo("image/gif", "gif", width, height), null);
    }

    /// <summary>The position after a run of sub-blocks (a size byte, its bytes) and its terminator (0).</summary>
    private static int SubBlocks(ReadOnlySpan<byte> data, int position)
    {
        while (true)
        {
            var size = data[position];
            position += 1 + size;
            if (size == 0)
            {
                return position;
            }
            if (position > data.Length)
            {
                throw new InvalidDataException("GIF block cut off");
            }
        }
    }

    // --- WebP: a RIFF container of chunks (VP8, VP8L, or VP8X with ICC, ANIM, ALPH, EXIF, XMP) --------------------------

    private static (byte[]?, ImageInfo?, ImageRefusal?) WebP(ReadOnlySpan<byte> data)
    {
        var riffEnd = 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (riffEnd > data.Length || riffEnd < 12)
        {
            throw new InvalidDataException("WebP: RIFF size beyond the data");
        }
        var chunks = new List<(string Type, int Start, int End)>();
        var position = 12;
        while (position + 8 <= riffEnd)
        {
            var type = Encoding.ASCII.GetString(data.Slice(position, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
            var end = position + 8 + (long)size;
            if (end > riffEnd)
            {
                throw new InvalidDataException("WebP chunk too long");
            }
            // An odd chunk is padded to an even length; a last one may lack its padding.
            var padded = Math.Min(end + (size & 1), riffEnd);
            chunks.Add((type, position, (int)padded));
            position = (int)padded;
        }
        if (chunks.Count == 0)
        {
            throw new InvalidDataException("WebP without chunks");
        }
        int width, height;
        var head = chunks[0];
        var payload = data[(head.Start + 8)..];
        switch (head.Type)
        {
            case "VP8X":
                width = 1 + (payload[4] | payload[5] << 8 | payload[6] << 16);
                height = 1 + (payload[7] | payload[8] << 8 | payload[9] << 16);
                break;
            case "VP8 ":
                if (payload[3] != 0x9D || payload[4] != 0x01 || payload[5] != 0x2A)
                {
                    throw new InvalidDataException("VP8 without its start code");
                }
                width = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) & 0x3FFF;
                height = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) & 0x3FFF;
                break;
            case "VP8L":
                if (payload[0] != 0x2F)
                {
                    throw new InvalidDataException("VP8L without its signature");
                }
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
                width = 1 + (int)(bits & 0x3FFF);
                height = 1 + (int)((bits >> 14) & 0x3FFF);
                break;
            default:
                throw new InvalidDataException("WebP: unknown first chunk");
        }
        if (Dimensions(width, height) is { } refusal)
        {
            return (null, null, refusal);
        }
        var output = new MemoryStream(data.Length);
        output.Write(data[..12]);
        foreach (var (type, start, end) in chunks)
        {
            if (type is "EXIF" or "XMP ")
            {
                continue;
            }
            var chunk = data[start..end].ToArray();
            if (type == "VP8X")
            {
                // Exif (0x08) and XMP (0x04) are gone with their chunks.
                chunk[8] &= 0xF3;
            }
            output.Write(chunk);
        }
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return (bytes, new ImageInfo("image/webp", "webp", width, height), null);
    }
}
