using System;
using System.IO;
using AssetRipper.IO.Files.BundleFiles;
using Xunit;
using ZstdSharp;

namespace Unpackrat.UnityProbe.Tests
{
    public sealed class UnityZstdCompressionTests
    {
        private static readonly byte[] Payload = CreatePayload();

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(19)]
        public void DecompressStream_RoundTripsFrameAtNonZeroOffsets(
            int compressionLevel)
        {
            byte[] compressedFrame;
            using (var compressor = new Compressor(compressionLevel))
            {
                compressedFrame = compressor.Wrap(Payload).ToArray();
            }

            byte[] inputPrefix = { 0x10, 0x21, 0x32, 0x43, 0x54 };
            byte[] inputTail = { 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7 };
            byte[] inputSeed = Concat(inputPrefix, compressedFrame, inputTail);

            using var compressedStream = new MemoryStream(inputSeed, writable: true);
            compressedStream.Position = inputPrefix.Length;
            long frameStart = compressedStream.Position;

            byte[] outputPrefix = { 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6 };
            byte[] outputTail = { 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7 };
            byte[] outputSeed = new byte[
                outputPrefix.Length + Payload.Length + outputTail.Length];
            outputSeed.AsSpan().Fill(0xCC);
            outputPrefix.CopyTo(outputSeed, 0);
            outputTail.CopyTo(outputSeed, outputPrefix.Length + Payload.Length);

            using var decompressedStream = new MemoryStream(outputSeed, writable: true);
            decompressedStream.Position = outputPrefix.Length;

            ZstdCompression.DecompressStream(
                compressedStream,
                compressedFrame.Length,
                decompressedStream,
                Payload.Length);

            Assert.True(
                compressedStream.CanRead,
                "The compressed stream was closed or made unreadable.");
            Assert.True(
                compressedStream.CanWrite,
                "The compressed stream was closed or made unwritable.");
            Assert.True(
                decompressedStream.CanRead,
                "The decompressed stream was closed or made unreadable.");
            Assert.True(
                decompressedStream.CanWrite,
                "The decompressed stream was closed or made unwritable.");

            Assert.InRange(
                compressedStream.Position,
                frameStart,
                frameStart + compressedFrame.Length);
            Assert.True(
                decompressedStream.Position >= outputPrefix.Length,
                "Decoding moved the output position before the wrapped region.");
            Assert.True(
                decompressedStream.Position <= outputPrefix.Length + Payload.Length,
                "Decoding moved the output position past the announced limit.");
            Assert.Equal((long)outputSeed.Length, decompressedStream.Length);

            Assert.True(
                inputSeed.AsSpan(0, inputPrefix.Length).SequenceEqual(inputPrefix),
                "The input prefix was modified.");
            Assert.True(
                inputSeed.AsSpan(inputPrefix.Length + compressedFrame.Length)
                    .SequenceEqual(inputTail),
                "The input trailing bytes were modified.");
            Assert.True(
                outputSeed.AsSpan(0, outputPrefix.Length).SequenceEqual(outputPrefix),
                "The output prefix was modified.");
            Assert.True(
                outputSeed.AsSpan(outputPrefix.Length, Payload.Length)
                    .SequenceEqual(Payload),
                "The decoded payload bytes did not match the original payload.");
            Assert.True(
                outputSeed.AsSpan(outputPrefix.Length + Payload.Length)
                    .SequenceEqual(outputTail),
                "The output trailing bytes were modified.");
        }

        [Fact]
        public void DecompressStream_HandlesEmptyFrameAtNonZeroOffsets()
        {
            byte[] compressedFrame;
            using (var compressor = new Compressor(3))
            {
                compressedFrame = compressor.Wrap(Array.Empty<byte>()).ToArray();
            }

            byte[] inputPrefix = { 0x01, 0x02, 0x03 };
            byte[] inputTail = { 0x81, 0x82, 0x83, 0x84 };
            byte[] inputSeed = Concat(inputPrefix, compressedFrame, inputTail);

            using var compressedStream = new MemoryStream(inputSeed, writable: true);
            compressedStream.Position = inputPrefix.Length;
            long frameStart = compressedStream.Position;

            byte[] outputPrefix = { 0x11, 0x12, 0x13, 0x14 };
            byte[] outputTail = { 0x91, 0x92, 0x93, 0x94, 0x95 };
            byte[] outputSeed = new byte[outputPrefix.Length + outputTail.Length];
            outputSeed.AsSpan().Fill(0x5A);
            outputPrefix.CopyTo(outputSeed, 0);
            outputTail.CopyTo(outputSeed, outputPrefix.Length);

            using var decompressedStream = new MemoryStream(outputSeed, writable: true);
            decompressedStream.Position = outputPrefix.Length;

            ZstdCompression.DecompressStream(
                compressedStream,
                compressedFrame.Length,
                decompressedStream,
                0);

            Assert.True(compressedStream.CanRead);
            Assert.True(compressedStream.CanWrite);
            Assert.True(decompressedStream.CanRead);
            Assert.True(decompressedStream.CanWrite);

            Assert.InRange(
                compressedStream.Position,
                frameStart,
                frameStart + compressedFrame.Length);
            Assert.Equal((long)outputPrefix.Length, decompressedStream.Position);
            Assert.Equal((long)outputSeed.Length, decompressedStream.Length);

            Assert.True(
                inputSeed.AsSpan(0, inputPrefix.Length).SequenceEqual(inputPrefix),
                "The input prefix was modified.");
            Assert.True(
                inputSeed.AsSpan(inputPrefix.Length + compressedFrame.Length)
                    .SequenceEqual(inputTail),
                "The input trailing bytes were modified.");
            Assert.True(
                outputSeed.AsSpan(0, outputPrefix.Length).SequenceEqual(outputPrefix),
                "The output prefix was modified.");
            Assert.True(
                outputSeed.AsSpan(outputPrefix.Length).SequenceEqual(outputTail),
                "The output trailing bytes were modified.");
        }

        [Fact]
        public void IsZstd_ReturnsTrueAtNonZeroPositionAndPreservesPosition()
        {
            byte[] compressedFrame;
            using (var compressor = new Compressor(3))
            {
                compressedFrame = compressor.Wrap(Payload).ToArray();
            }

            byte[] prefix = { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 };
            byte[] inputSeed = Concat(prefix, compressedFrame, Array.Empty<byte>());

            using var input = new MemoryStream(inputSeed, writable: true);
            input.Position = prefix.Length;
            long initialPosition = input.Position;

            Assert.True(ZstdCompression.IsZstd(input));
            Assert.Equal(initialPosition, input.Position);
        }

        [Fact]
        public void IsZstd_ReturnsFalseForFourWrongBytesAndPreservesPosition()
        {
            byte[] notMagic = { 0x00, 0x01, 0x02, 0x03 };

            using var input = new MemoryStream(notMagic, writable: true);
            input.Position = 0;
            long initialPosition = input.Position;

            Assert.False(ZstdCompression.IsZstd(input));
            Assert.Equal(initialPosition, input.Position);
        }

        private static byte[] CreatePayload()
        {
            var payload = new byte[4096];
            Array.Fill(payload, (byte)'A', 0, 1024);
            Array.Fill(payload, (byte)0, 1024, 512);

            for (int i = 1536; i < 2048; i++)
            {
                payload[i] = (byte)((i * 37) & 0xFF);
            }

            for (int i = 2048; i < payload.Length; i++)
            {
                payload[i] = (byte)(i & 0xFF);
            }

            return payload;
        }

        private static byte[] Concat(byte[] first, byte[] second, byte[] third)
        {
            var result = new byte[first.Length + second.Length + third.Length];
            first.CopyTo(result, 0);
            second.CopyTo(result, first.Length);
            third.CopyTo(result, first.Length + second.Length);
            return result;
        }
    }
}
