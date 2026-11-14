using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PNMTD.Files;

namespace PNMTD.Tests.Files;

/// <summary>
/// Images by their bytes (docs/locations-knowledge-plan.md §5.5, §12): after storing, no metadata is left and the image data
/// is byte for byte the same; what isn't an image, or is too large, is refused.
/// </summary>
[TestClass]
public sealed class ImageCheckTests
{
    [TestMethod]
    public void A_png_loses_its_text_exif_and_time_chunks_and_keeps_the_rest()
    {
        var (withMetadata, clean) = TestImages.PngPair();
        var (image, info, refusal) = ImageCheck.Check(withMetadata);
        Assert.IsNull(refusal);
        CollectionAssert.AreEqual(clean, image);
        Assert.AreEqual(new ImageInfo("image/png", "png", 3, 2), info);
        Assert.IsFalse(Encoding.Latin1.GetString(image!).Contains("Philipp", StringComparison.Ordinal));
    }

    [TestMethod]
    public void A_jpeg_loses_exif_with_gps_xmp_iptc_comments_and_trailing_data_and_keeps_jfif_and_the_icc_profile()
    {
        var (withMetadata, clean) = TestImages.JpegPair();
        var (image, info, refusal) = ImageCheck.Check(withMetadata);
        Assert.IsNull(refusal);
        CollectionAssert.AreEqual(clean, image, "the scans with their restart markers, tables and APP0/APP2 untouched");
        Assert.AreEqual(new ImageInfo("image/jpeg", "jpg", 8, 8), info);
        var text = Encoding.Latin1.GetString(image!);
        Assert.IsFalse(text.Contains("Exif", StringComparison.Ordinal) || text.Contains("xmpmeta", StringComparison.Ordinal)
            || text.Contains("Photoshop", StringComparison.Ordinal) || text.Contains("motion", StringComparison.Ordinal));
        StringAssert.Contains(text, "ICC_PROFILE");
    }

    [TestMethod]
    public void A_webp_loses_exif_and_xmp_with_its_flags_and_riff_size_corrected()
    {
        var (withMetadata, clean) = TestImages.WebPPair();
        var (image, info, refusal) = ImageCheck.Check(withMetadata);
        Assert.IsNull(refusal);
        CollectionAssert.AreEqual(clean, image);
        Assert.AreEqual(new ImageInfo("image/webp", "webp", 1, 1), info);
        Assert.AreEqual(clean.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(4)));
        Assert.AreEqual(0x20, image![20], "ICC stays, Exif and XMP are gone from VP8X's flags");
    }

    [TestMethod]
    public void An_animated_gif_loses_its_comment_and_keeps_both_frames_and_the_loop()
    {
        var (withMetadata, clean) = TestImages.GifPair();
        var (image, info, refusal) = ImageCheck.Check(withMetadata);
        Assert.IsNull(refusal);
        CollectionAssert.AreEqual(clean, image);
        Assert.AreEqual(new ImageInfo("image/gif", "gif", 1, 1), info);
        StringAssert.Contains(Encoding.Latin1.GetString(image!), "NETSCAPE2.0");
    }

    [TestMethod]
    public void Svg_html_polyglots_heic_and_damaged_files_are_not_images()
    {
        var (png, _) = TestImages.PngPair();
        var refused = new Dictionary<string, byte[]>
        {
            ["SVG"] = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"),
            ["HTML named .png"] = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body><script>alert(document.cookie)</script></body></html>"),
            ["PNG/HTML polyglot"] = TestImages.Png(3, 2, ("tEXt", Encoding.Latin1.GetBytes("x\0<HTML><SCRIPT>alert(1)</SCRIPT>"))),
            ["HTML after IEND"] = [.. png, .. "<html><script>alert(1)</script></html>"u8.ToArray()],
            ["HEIC"] = [0, 0, 0, 0x18, .. "ftypheic"u8.ToArray(), 0, 0, 0, 0],
            ["cut off PNG"] = png[..40],
            ["empty"] = [],
        };
        foreach (var (what, bytes) in refused)
        {
            var (image, _, refusal) = ImageCheck.Check(bytes);
            Assert.IsNull(image, what);
            Assert.IsNotNull(refusal, what);
            Assert.IsFalse(refusal.TooLarge, what);
        }
    }

    [TestMethod]
    public void Images_over_ten_thousand_pixels_a_side_or_forty_megapixels_are_too_large()
    {
        foreach (var (width, height) in new[] { (10_001, 1), (1, 10_001), (7_000, 6_000) })
        {
            var (image, _, refusal) = ImageCheck.Check(TestImages.Png(width, height));
            Assert.IsNull(image);
            Assert.IsTrue(refusal!.TooLarge, $"{width} × {height}");
        }
        Assert.IsNull(ImageCheck.Check(TestImages.Png(10_000, 4_000)).Refusal, "exactly at the limits");
    }
}

/// <summary>Small images with and without metadata, built here so the tests show what each one holds.</summary>
internal static class TestImages
{
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Files", name);

    /// <summary>A PNG with the given chunks after IHDR; its IDAT holds real pixels for small sizes.</summary>
    public static byte[] Png(int width, int height, params (string Type, byte[] Data)[] extra)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        output.Write(Chunk("IHDR", header));
        foreach (var (type, data) in extra)
        {
            output.Write(Chunk(type, data));
        }
        output.Write(Chunk("IDAT", Pixels(Math.Min(width, 16), Math.Min(height, 16))));
        output.Write(Chunk("IEND", []));
        return output.ToArray();
    }

    public static (byte[] WithMetadata, byte[] Clean) PngPair()
    {
        var exif = "MM\0*\0\0\0\b\0\0\0\0\0\0"u8.ToArray();
        var withMetadata = Png(3, 2,
            ("tEXt", Encoding.Latin1.GetBytes("Author\0Philipp")),
            ("gAMA", [0, 0, 0xB1, 0x8F]),
            ("zTXt", Encoding.Latin1.GetBytes("Comment\0\0x")),
            ("iTXt", Encoding.UTF8.GetBytes("XML:com.adobe.xmp\0\0\0\0\0<x:xmpmeta/>")),
            ("eXIf", exif),
            ("tIME", [0x07, 0xEA, 11, 14, 10, 0, 0]),
            ("pHYs", [0, 0, 0x0B, 0x13, 0, 0, 0x0B, 0x13, 1]));
        var clean = Png(3, 2, ("gAMA", [0, 0, 0xB1, 0x8F]), ("pHYs", [0, 0, 0x0B, 0x13, 0, 0, 0x0B, 0x13, 1]));
        return (withMetadata, clean);
    }

    /// <summary>
    /// The fixture (8 × 8 from sips, with a restart interval, without metadata) with EXIF holding GPS, XMP, IPTC, a comment
    /// and a motion photo's trailer added; and the fixture with only the ICC profile (APP2) added, which stays.
    /// </summary>
    public static (byte[] WithMetadata, byte[] Clean) JpegPair()
    {
        var plain = File.ReadAllBytes(Fixture("plain.jpg"));
        var app0End = 4 + BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(4));
        var icc = Segment(0xE2, [.. "ICC_PROFILE\0"u8.ToArray(), 1, 1, .. new byte[20]]);
        var withMetadata = Concat(plain[..app0End], Segment(0xE1, ExifWithGps()), icc,
            Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8.ToArray(), .. "<x:xmpmeta><dc:creator>Philipp</dc:creator></x:xmpmeta>"u8.ToArray()]),
            Segment(0xED, [.. "Photoshop 3.0\08BIM"u8.ToArray(), 4, 4, 0, 0]), Segment(0xFE, "taken at home"u8.ToArray()),
            plain[app0End..], "motion photo data"u8.ToArray());
        var clean = Concat(plain[..app0End], icc, plain[app0End..]);
        return (withMetadata, clean);
    }

    /// <summary>The well-known 1 × 1 lossless WebP, extended (VP8X) with an ICC profile, EXIF and XMP; and without the last two.</summary>
    public static (byte[] WithMetadata, byte[] Clean) WebPPair()
    {
        var vp8l = Convert.FromBase64String("UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==")[12..];
        byte[] Build(byte flags, params byte[][] chunks)
        {
            var body = Concat([.. "WEBP"u8.ToArray(), .. RiffChunk("VP8X", [flags, 0, 0, 0, 0, 0, 0, 0, 0, 0])], Concat(chunks));
            var riff = new byte[8 + body.Length];
            "RIFF"u8.CopyTo(riff);
            BinaryPrimitives.WriteInt32LittleEndian(riff.AsSpan(4), body.Length);
            body.CopyTo(riff, 8);
            return riff;
        }
        var iccp = RiffChunk("ICCP", new byte[7]);
        var withMetadata = Build(0x2C, iccp, vp8l, RiffChunk("EXIF", ExifWithGps()[6..]), RiffChunk("XMP ", "<x:xmpmeta/>"u8.ToArray()));
        var clean = Build(0x20, iccp, vp8l);
        return (withMetadata, clean);
    }

    /// <summary>A 1 × 1 GIF with two frames, a loop (NETSCAPE2.0) and a comment; and without the comment.</summary>
    public static (byte[] WithMetadata, byte[] Clean) GifPair()
    {
        byte[] head = [.. "GIF89a"u8.ToArray(), 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF];
        byte[] loop = [0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8.ToArray(), 3, 1, 0, 0, 0];
        byte[] comment = [0x21, 0xFE, 13, .. "taken at home"u8.ToArray(), 0];
        byte[] frame = [0x21, 0xF9, 4, 4, 10, 0, 0, 0, 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0];
        return (Concat(head, loop, comment, frame, frame, [0x3B]), Concat(head, loop, frame, frame, [0x3B]));
    }

    /// <summary>EXIF (big-endian TIFF) with a GPS IFD: latitude N 47° 22′ 0″.</summary>
    private static byte[] ExifWithGps()
    {
        var tiff = new byte[8 + 2 + 12 + 4 + 2 + 24 + 4 + 24];
        "MM"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
        // IFD0: GPSInfo (0x8825), LONG, 1, the GPS IFD's offset.
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x8825);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(18), 26);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(26), 2);
        // GPSLatitudeRef "N", GPSLatitude 3 RATIONALs at 56.
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(30), 2);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(32), 2);
        tiff[36] = (byte)'N';
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(40), 2);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(42), 5);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(44), 3);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(48), 56);
        foreach (var (i, value) in new[] { (0, 47u), (1, 22u), (2, 0u) })
        {
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(56 + i * 8), value);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(60 + i * 8), 1);
        }
        return [.. "Exif\0\0"u8.ToArray(), .. tiff];
    }

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return segment;
    }

    private static byte[] RiffChunk(string type, byte[] data)
    {
        var chunk = new byte[8 + data.Length + (data.Length & 1)];
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 0);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), data.Length);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static byte[] Pixels(int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = y * (1 + width * 3) + 1 + x * 3;
                raw[at] = (byte)(x * 40);
                raw[at + 1] = (byte)(y * 80);
                raw[at + 2] = 128;
            }
        }
        var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        return output.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
