using System.Security.Cryptography;
using PNMTD.Lib.Udp;

namespace PNMTD.Tests.Udp;

/// <summary>
/// PNMT-UDP's codec (docs/udp-protocol.md §3): the RFC vectors of the algorithms, the protocol's own
/// vectors (§3.14), every type × suite × id length, malformed input, the counter window and the KDF.
/// </summary>
[TestClass]
public sealed class CodecTests
{
    private static readonly byte[] ReferenceKey = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] ReferenceToken = Convert.FromHexString("5A17C39E");

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    // --- the algorithms ---------------------------------------------------------------------

    [TestMethod]
    [DataRow("", "bb1d6929e95937287fa37d129b756746")]
    [DataRow("6bc1bee22e409f96e93d7e117393172a", "070a16b46b4d4144f79bdd9dd04a287c")]
    [DataRow("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411", "dfa66747de9ae63030ca32611497c827")]
    [DataRow("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710", "51f0bebf7e3b9d92fc49741779363cfe")]
    public void AesCmacMatchesRfc4493(string message, string mac)
    {
        var key = Hex("2b7e151628aed2a6abf7158809cf4f3c");
        Assert.AreEqual(mac, Convert.ToHexString(AesModes.Cmac(key, Hex(message))).ToLowerInvariant());
    }

    /// <summary>RFC 3610 packet vector #1: 8 bytes of header (associated data), 23 of payload, an 8-byte tag.</summary>
    [TestMethod]
    public void AesCcmMatchesRfc3610()
    {
        var key = Hex("C0C1C2C3C4C5C6C7C8C9CACBCCCDCECF");
        var nonce = Hex("00000003020100A0A1A2A3A4A5");
        var packet = Hex("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E");
        var (ciphertext, tag) = AesModes.CcmEncrypt(key, nonce, packet.AsSpan(0, 8), packet.AsSpan(8), 8);
        Assert.AreEqual("588C979A61C663D2F066D0C2C0F989806D5F6B61DAC384", Convert.ToHexString(ciphertext));
        Assert.AreEqual("17E8D12CFDF926E0", Convert.ToHexString(tag));
        CollectionAssert.AreEqual(packet[8..], AesModes.CcmDecrypt(key, nonce, packet.AsSpan(0, 8), ciphertext, tag));
        tag[0] ^= 1;
        Assert.IsNull(AesModes.CcmDecrypt(key, nonce, packet.AsSpan(0, 8), ciphertext, tag));
    }

    /// <summary>Where the platform has AES-CCM (Linux), the hand-built one must agree with it.</summary>
    [TestMethod]
    public void AesCcmAgreesWithThePlatform()
    {
        if (!AesCcm.IsSupported)
        {
            Assert.Inconclusive("AesCcm is not supported here (macOS); the Linux run compares.");
        }
        var random = new Random(7);
        foreach (var tagLength in new[] { 4, 8 })
        {
            for (var length = 0; length < 70; length += 7)
            {
                var key = new byte[16];
                var nonce = new byte[13];
                var aad = new byte[random.Next(5, 9)];
                var plaintext = new byte[length];
                random.NextBytes(key);
                random.NextBytes(nonce);
                random.NextBytes(aad);
                random.NextBytes(plaintext);
                var (ciphertext, tag) = AesModes.CcmEncrypt(key, nonce, aad, plaintext, tagLength);
                using var platform = new AesCcm(key);
                var expectedCipher = new byte[length];
                var expectedTag = new byte[tagLength];
                platform.Encrypt(nonce, plaintext, expectedCipher, expectedTag, aad);
                CollectionAssert.AreEqual(expectedCipher, ciphertext);
                CollectionAssert.AreEqual(expectedTag, tag);
            }
        }
    }

    [TestMethod]
    public void HmacsMatchRfc4231AndRfc2202()
    {
        var key = "Jefe"u8.ToArray();
        var data = "what do ya want for nothing?"u8.ToArray();
        Assert.AreEqual("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant());
        Assert.AreEqual("effcdf6ae5eb2fa2d27416d5f184df9c259a7c79", Convert.ToHexString(HMACSHA1.HashData(key, data)).ToLowerInvariant());
        Assert.AreEqual("750c783e6ab0b503eaa86e310a5db738", Convert.ToHexString(HMACMD5.HashData(key, data)).ToLowerInvariant());
    }

    // --- the protocol's vectors (§3.14) -------------------------------------------------------

    [TestMethod]
    [DataRow("HEARTBEAT, A, HMAC-SHA256/32, FCNT 42", PacketType.Heartbeat, SecuritySuite.HmacSha256_32, 7u, 42u, true, "", "40 82 07 00 2A DF 27 88 7E")]
    [DataRow("ACK for #1", PacketType.Ack, SecuritySuite.HmacSha256_32, 7u, 42u, false, "", "60 02 07 00 2A 55 D9 7A 31")]
    [DataRow("DATA 21.5C, HMAC-SHA256/64", PacketType.Data, SecuritySuite.HmacSha256_64, 7u, 43u, false, "3231 2E35 43", "44 03 07 00 2B 32 31 2E 35 43 35 0A B3 32 32 76 BB 45")]
    [DataRow("HEARTBEAT, HMAC/32, FCNT 44", PacketType.Heartbeat, SecuritySuite.HmacSha256_32, 7u, 44u, false, "", "40 02 07 00 2C 73 CE 03 12")]
    [DataRow("COMMAND for #4", PacketType.Command, SecuritySuite.HmacSha256_32, 7u, 44u, false, "01 02 02 58", "64 02 07 00 2C 01 02 02 58 E6 B0 5E F4")]
    [DataRow("CMD_RESULT OK", PacketType.CmdResult, SecuritySuite.HmacSha256_32, 7u, 45u, true, "01 00 4F 4B", "4C 82 07 00 2D 01 00 4F 4B FA A0 DD 20")]
    [DataRow("TOKEN32, id 300", PacketType.Heartbeat, SecuritySuite.Token32, 300u, 1u, false, "", "41 01 01 2C 00 01 5A 17 C3 9E")]
    [DataRow("NONE", PacketType.Heartbeat, SecuritySuite.None, 7u, 1u, false, "", "40 00 07 00 01")]
    [DataRow("HMAC-MD5/32", PacketType.Heartbeat, SecuritySuite.HmacMd5_32, 7u, 42u, true, "", "40 88 07 00 2A CD 5C FD B1")]
    [DataRow("HMAC-MD5/64", PacketType.Heartbeat, SecuritySuite.HmacMd5_64, 7u, 42u, true, "", "40 89 07 00 2A 6E 1E 4C 4C A3 C2 F2 A7")]
    [DataRow("HMAC-SHA1/32", PacketType.Heartbeat, SecuritySuite.HmacSha1_32, 7u, 42u, true, "", "40 8A 07 00 2A 68 AB C6 CF")]
    [DataRow("HMAC-SHA1/64", PacketType.Heartbeat, SecuritySuite.HmacSha1_64, 7u, 42u, true, "", "40 8B 07 00 2A B8 25 70 92 47 E1 82 E1")]
    public void ProtocolVectors(string name, PacketType type, SecuritySuite suite, uint id, uint fcnt, bool ack, string payload, string expected)
    {
        var header = new PacketHeader(type, suite, id, (ushort)fcnt, ack);
        var bytes = PacketCodec.Build(header, Hex(payload), ReferenceKey, ReferenceToken, fcnt);
        Assert.AreEqual(expected.Replace(" ", ""), Convert.ToHexString(bytes), name);

        Assert.IsTrue(PacketCodec.TryParse(bytes, out var parsed, out var error), $"{name}: {error}");
        Assert.AreEqual(header with { IdLength = header.EffectiveIdLength }, parsed.Header, name);
        CollectionAssert.AreEqual(Hex(payload), PacketCodec.Open(parsed, ReferenceKey, ReferenceToken, fcnt), name);
        if (suite != SecuritySuite.None)
        {
            Assert.IsNull(PacketCodec.Open(parsed, Flip(ReferenceKey), Flip(ReferenceToken), fcnt), $"{name}: another key or token");
        }
    }

    private static byte[] Flip(byte[] bytes)
    {
        var copy = bytes.ToArray();
        copy[^1] ^= 0x01;
        return copy;
    }

    // --- every combination --------------------------------------------------------------------

    [TestMethod]
    public void EveryTypeSuiteAndIdLengthRoundTrips()
    {
        var random = new Random(11);
        foreach (var type in Enum.GetValues<PacketType>())
        {
            foreach (var suite in Enum.GetValues<SecuritySuite>())
            {
                foreach (var (id, idLength) in new[] { (7u, 1), (300u, 2), (70000u, 3), (0x12345678u, 4), (7u, 4) })
                {
                    var payload = new byte[random.Next(0, 40)];
                    random.NextBytes(payload);
                    var fcnt = (uint)random.Next(1, int.MaxValue);
                    var header = new PacketHeader(type, suite, id, (ushort)fcnt, !type.IsDownlink() && random.Next(2) == 0,
                        type.IsDownlink() && random.Next(2) == 0, idLength);
                    var bytes = PacketCodec.Build(header, payload, ReferenceKey, ReferenceToken, fcnt);
                    var label = $"{type} {suite} id {id}/{idLength}";
                    Assert.AreEqual(header.Length + payload.Length + suite.TagLength(), bytes.Length, label);
                    Assert.IsTrue(PacketCodec.TryParse(bytes, out var parsed, out var error), $"{label}: {error}");
                    Assert.AreEqual(header, parsed.Header, label);
                    CollectionAssert.AreEqual(payload, PacketCodec.Open(parsed, ReferenceKey, ReferenceToken, fcnt), label);
                    if (suite.IsKeyed())
                    {
                        Assert.IsNull(PacketCodec.Open(parsed, ReferenceKey, ReferenceToken, fcnt ^ 1), $"{label}: the counter is covered");
                        var tampered = bytes.ToArray();
                        tampered[1] ^= 0x80; // the A flag
                        Assert.IsTrue(PacketCodec.TryParse(tampered, out var t, out _));
                        Assert.IsNull(PacketCodec.Open(t, ReferenceKey, ReferenceToken, fcnt), $"{label}: the header is covered");
                    }
                    if (suite.IsEncrypted() && payload.Length > 0)
                    {
                        CollectionAssert.AreNotEqual(payload, parsed.Body.ToArray(), $"{label}: encrypted on the wire");
                    }
                }
            }
        }
    }

    [TestMethod]
    public void UplinkAndDownlinkNoncesDiffer()
    {
        CollectionAssert.AreNotEqual(PacketCodec.Nonce(false, 7, 42), PacketCodec.Nonce(true, 7, 42));
        Assert.AreEqual("00000000070000002A00000000", Convert.ToHexString(PacketCodec.Nonce(false, 7, 42)));
    }

    // --- malformed input ----------------------------------------------------------------------

    [TestMethod]
    [DataRow("40 00 07 00", ParseError.TooShort)]
    [DataRow("00 00 07 00 01", ParseError.BadVersion)]
    [DataRow("80 00 07 00 01", ParseError.BadVersion)]
    [DataRow("C0 00 07 00 01", ParseError.BadVersion)]
    [DataRow("5C 00 07 00 01", ParseError.UnknownType)]
    [DataRow("7C 00 07 00 01", ParseError.UnknownType)]
    [DataRow("40 10 07 00 01", ParseError.ReservedBits)]
    [DataRow("40 20 07 00 01", ParseError.ReservedBits)]
    [DataRow("40 0C 07 00 01", ParseError.UnknownSuite)]
    [DataRow("40 00 00 00 01", ParseError.ZeroDeviceId)]
    [DataRow("40 02 07 00 01 AA BB", ParseError.TagDoesNotFit)]
    [DataRow("43 00 00 00 07 00", ParseError.TooShort)]
    public void MalformedDatagramsAreRefused(string hex, ParseError expected)
    {
        Assert.IsFalse(PacketCodec.TryParse(Hex(hex), out _, out var error));
        Assert.AreEqual(expected, error);
    }

    [TestMethod]
    public void OversizedDatagramsAreRefused()
    {
        var bytes = new byte[PacketCodec.MaxDatagram + 1];
        bytes[0] = 0x40;
        bytes[2] = 7;
        Assert.IsFalse(PacketCodec.TryParse(bytes, out _, out var error));
        Assert.AreEqual(ParseError.TooLong, error);
        Assert.ThrowsExactly<ArgumentException>(() =>
            PacketCodec.Build(new PacketHeader(PacketType.Data, SecuritySuite.None, 7, 1), new byte[504], [], [], 1));
    }

    /// <summary>Random bytes never make the parser throw (a cheap fuzz; the codec faces the internet).</summary>
    [TestMethod]
    public void RandomBytesNeverThrow()
    {
        var random = new Random(1234);
        var accepted = 0;
        for (var i = 0; i < 200_000; i++)
        {
            var bytes = new byte[random.Next(0, 80)];
            random.NextBytes(bytes);
            if (i % 2 == 0 && bytes.Length > 0)
            {
                bytes[0] = (byte)(0x40 | (bytes[0] & 0x3F));
            }
            if (PacketCodec.TryParse(bytes, out var packet, out _))
            {
                accepted++;
                _ = PacketCodec.Open(packet, ReferenceKey, ReferenceToken, 1);
                _ = PacketFields.Describe(packet, packet.Body.ToArray(), null);
                _ = Payloads.ReadOta(packet.Body.Span);
                _ = Payloads.ReadOtaOffer(packet.Body.Span);
                _ = Payloads.ReadEvent(packet.Body.Span);
            }
        }
        Assert.IsGreaterThan(1000, accepted);
    }

    // --- the counter --------------------------------------------------------------------------

    [TestMethod]
    [DataRow(null, (ushort)5, 5L)]
    [DataRow(100L, (ushort)101, 101L)]
    [DataRow(100L, (ushort)90, 90L)]
    [DataRow(65530L, (ushort)3, 65539L)]
    [DataRow(65540L, (ushort)65530, 65530L)]
    [DataRow(5L, (ushort)65535, -1L)]
    [DataRow(200000L, (ushort)0, 196608L)]
    public void TheFullCounterIsReconstructed(long? high, ushort wire, long expected) =>
        Assert.AreEqual(expected, FrameCounter.Reconstruct(high, wire));

    [TestMethod]
    public void TheWindowAcceptsReorderingAndRefusesReplays()
    {
        var state = CounterState.Empty;
        Assert.AreEqual(CounterVerdict.New, FrameCounter.Check(state, 10));
        state = FrameCounter.Accept(state, 10);
        Assert.AreEqual(CounterVerdict.Replay, FrameCounter.Check(state, 10));
        Assert.AreEqual(CounterVerdict.New, FrameCounter.Check(state, 12));
        state = FrameCounter.Accept(state, 12);
        Assert.AreEqual(CounterVerdict.Reordered, FrameCounter.Check(state, 11));
        state = FrameCounter.Accept(state, 11);
        Assert.AreEqual(CounterVerdict.Replay, FrameCounter.Check(state, 11));
        Assert.AreEqual(CounterVerdict.Replay, FrameCounter.Check(state, 10));
        state = FrameCounter.Accept(state, 100);
        Assert.AreEqual(CounterVerdict.Replay, FrameCounter.Check(state, 12), "64 behind is out of the window");
        Assert.AreEqual(CounterVerdict.Reordered, FrameCounter.Check(state, 60));
        Assert.AreEqual(CounterVerdict.Jump, FrameCounter.Check(state, 100 + FrameCounter.MaxGap + 1));
        Assert.AreEqual(CounterVerdict.New, FrameCounter.Check(state, 100 + FrameCounter.MaxGap));
        Assert.AreEqual(CounterVerdict.Replay, FrameCounter.Check(state, -1));
    }

    [TestMethod]
    public void TheCounterSurvivesThe16BitWrap()
    {
        var state = FrameCounter.Accept(CounterState.Empty, 65534);
        for (var fcnt = 65535L; fcnt < 65540; fcnt++)
        {
            var full = FrameCounter.Reconstruct(state.High, (ushort)fcnt);
            Assert.AreEqual(fcnt, full);
            Assert.AreEqual(CounterVerdict.New, FrameCounter.Check(state, full));
            state = FrameCounter.Accept(state, full);
        }
    }

    // --- sessions and the device side ---------------------------------------------------------

    [TestMethod]
    public void SessionKeysDifferPerFamilyAndNonce()
    {
        var keys = Enum.GetValues<KeyFamily>().Select(f => Convert.ToHexString(SessionKeys.Derive(f, ReferenceKey, 1, 2))).ToList();
        Assert.AreEqual(keys.Count, keys.Distinct().Count());
        Assert.IsTrue(keys.All(k => k.Length == 32));
        Assert.AreNotEqual(keys[0], Convert.ToHexString(SessionKeys.Derive(KeyFamily.HmacSha256, ReferenceKey, 1, 3)));
        CollectionAssert.AreEqual(SessionKeys.Derive(KeyFamily.Aes, ReferenceKey, 1, 2), SessionKeys.Derive(KeyFamily.Aes, ReferenceKey, 1, 2));
    }

    [TestMethod]
    public void ADeviceJoinsAndUsesItsSessionKey()
    {
        var device = new UdpDevice(7, KeyFamily.Aes, ReferenceKey, ReferenceToken);
        var join = device.BuildJoin(0xA1B2C3D4);
        Assert.AreEqual(SecuritySuite.AesCmac64, join.Suite);
        // The server's side: a JOIN_ACCEPT bound to DEV_NONCE, under the root key.
        var accept = PacketCodec.Build(new PacketHeader(PacketType.JoinAccept, SecuritySuite.AesCmac64, 7, 0),
            Payloads.JoinAccept(0x01020304), ReferenceKey, [], 0xA1B2C3D4);
        Assert.IsNotNull(device.Open(accept, join, out var why), why);
        CollectionAssert.AreEqual(SessionKeys.Derive(KeyFamily.Aes, ReferenceKey, 0xA1B2C3D4, 0x01020304), device.SessionKey);
        Assert.AreEqual(1u, device.NextCounter);

        var heartbeat = device.Build(PacketType.Heartbeat, SecuritySuite.AesCcm64, "x"u8);
        Assert.IsTrue(PacketCodec.TryParse(heartbeat.Datagram, out var parsed, out _));
        Assert.IsNull(PacketCodec.Open(parsed, ReferenceKey, [], 1), "not under the root key");
        CollectionAssert.AreEqual("x"u8.ToArray(), PacketCodec.Open(parsed, device.SessionKey, [], 1));

        // A JOIN_ACCEPT for another DEV_NONCE does not verify.
        var stale = PacketCodec.Build(new PacketHeader(PacketType.JoinAccept, SecuritySuite.AesCmac64, 7, 0),
            Payloads.JoinAccept(5), ReferenceKey, [], 0x0BADF00D);
        Assert.IsNull(device.Open(stale, join, out _));
    }

    [TestMethod]
    public void ADeviceOnlyTakesDownlinksThatAnswerItsUplink()
    {
        var device = new UdpDevice(7, KeyFamily.HmacSha256, ReferenceKey, ReferenceToken);
        var uplink = device.Build(PacketType.Heartbeat, SecuritySuite.HmacSha256_32, [], ack: true);
        byte[] Ack(SecuritySuite suite, ushort counter, uint prefix) =>
            PacketCodec.Build(new PacketHeader(PacketType.Ack, suite, 7, counter), [], ReferenceKey, ReferenceToken, prefix);

        Assert.IsNotNull(device.Open(Ack(SecuritySuite.HmacSha256_32, 1, 1), uplink, out var why), why);
        Assert.IsNull(device.Open(Ack(SecuritySuite.HmacSha256_32, 2, 2), uplink, out _), "another counter");
        Assert.IsNull(device.Open(Ack(SecuritySuite.Token32, 1, 1), uplink, out _), "a suite it does not accept");
        Assert.IsNull(device.Open(uplink.Datagram, uplink, out _), "an uplink reflected");
        device.ResponseSuite = SecuritySuite.HmacSha256_64;
        Assert.IsNotNull(device.Open(Ack(SecuritySuite.HmacSha256_64, 1, 1), uplink, out why), why);
    }

    [TestMethod]
    public void PayloadsRoundTrip()
    {
        Assert.AreEqual(new EventPayload(3, 500, "pump ä"), Payloads.ReadEvent(Payloads.Event(new EventPayload(3, 500, "pump ä"))));
        var result = Payloads.ReadCmdResult(Payloads.CmdResult(new CmdResultPayload(9, 0, "OK"u8.ToArray())))!;
        Assert.AreEqual(9, result.CmdId);
        CollectionAssert.AreEqual("OK"u8.ToArray(), result.Result);
        Assert.AreEqual(new BootPayload(2, "1.5.0"), Payloads.ReadBoot(Payloads.Boot(new BootPayload(2, "1.5.0"))));
        Assert.AreEqual(new OtaPayload(3, OtaOp.Get, 448), Payloads.ReadOta(Payloads.Ota(new OtaPayload(3, OtaOp.Get, 448))));
        Assert.AreEqual(new OtaPayload(3, OtaOp.Get, 896, 64), Payloads.ReadOta(Payloads.Ota(new OtaPayload(3, OtaOp.Get, 896, 64))));
        Assert.AreEqual(new OtaPayload(3, OtaOp.Applied, Version: "1.5.0"), Payloads.ReadOta(Payloads.Ota(new OtaPayload(3, OtaOp.Applied, Version: "1.5.0"))));
        Assert.IsNull(Payloads.ReadOta([3, 1, 0, 0, 1]), "a GET must be 6 or 8 bytes");
        Assert.IsNull(Payloads.ReadOta([3, 9]), "an unknown OP");
        var sha = SHA256.HashData("image"u8);
        var offer = Payloads.ReadOtaOffer(Payloads.OtaOffer(new OtaOfferPayload(3, 1_048_576, 448, sha, "1.5.0")))!;
        Assert.AreEqual(1_048_576u, offer.Size);
        CollectionAssert.AreEqual(sha, offer.Sha256);
        Assert.AreEqual("1.5.0", offer.Version);
        Assert.AreEqual(OtaChunkPayload.Abort, Payloads.ReadOtaChunk(Payloads.OtaChunk(new OtaChunkPayload(3, OtaChunkPayload.Abort, [])))!.Offset);
    }

    [TestMethod]
    [DataRow("3231 2E35 43", "21.5C")]
    [DataRow("6C 69 6E 65 0A 74 77 6F", "line\ntwo")]
    [DataRow("00 01 FF", "hex:0001FF")]
    [DataRow("C3 28", "hex:C328")]
    [DataRow("", "")]
    public void PayloadsRenderAsTextOrHex(string hex, string expected) => Assert.AreEqual(expected, PacketCodec.Render(Hex(hex)));

    [TestMethod]
    public void FieldsNameEveryByte()
    {
        var bytes = Hex("44 03 07 00 2B 32 31 2E 35 43 35 0A B3 32 32 76 BB 45");
        Assert.IsTrue(PacketCodec.TryParse(bytes, out var packet, out _));
        var fields = PacketFields.Describe(packet, PacketCodec.Open(packet, ReferenceKey, [], 43), true);
        Assert.AreEqual(bytes.Length, fields.Sum(f => f.Length));
        Assert.AreEqual("PAYLOAD", fields[4].Name);
        Assert.AreEqual("21.5C", fields[4].Value);
        StringAssert.Contains(fields[^1].Value, "verified");
    }
}
