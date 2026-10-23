using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using AssetsTools.NET;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

// Regressions for behavior the schema route must preserve before it can replace the mature
// iterator. The comparisons use only the public/internal projection entry points.
public sealed class UnitySchemaRouteRegressionTests
{
	[Fact]
	public void TruncatedCapturedScalarsKeepTheMatureLogicalAndSourceCursor()
	{
		AssetTypeTemplateField template = Root(
			Field("m_Width", AssetValueType.Int32),
			Field("m_Height", AssetValueType.Int32));
		const int offset = 13;
		byte[] objectBytes = [0x2A, 0x00, 0x00, 0x00];
		byte[] source = Embedded(objectBytes, offset, suffixLength: 0);

		using CountingStream matureInput = new(source);
		matureInput.Position = offset;
		Exception? matureError = Capture(
			() => UnityProjection.Read(matureInput, template, false, new UnityProjection.State(template)),
			out UnityProjection? mature);

		using CountingStream routeInput = new(source);
		routeInput.Position = offset;
		Exception? routeError = Capture(
			() => UnityProjection.ReadBorrowed(routeInput, offset, objectBytes.Length, template, false,
				new UnityProjection.State(template)),
			out UnityProjection? route);

		Assert.Null(mature);
		Assert.IsType<EndOfStreamException>(matureError);
		Assert.Null(route);
		Assert.IsType<EndOfStreamException>(routeError);
		Assert.Equal(matureInput.Position, routeInput.Position);
		Assert.Equal<ReadWindow>(matureInput.Reads, routeInput.Reads);
		Assert.Equal(offset + objectBytes.Length, matureInput.Position);
		Assert.All(routeInput.Reads, read =>
			Assert.InRange(read.Offset, offset, offset + objectBytes.Length - read.Count));
	}

	[Fact]
	public void IgnoredFixedSkipsBeforeATruncatedCaptureKeepTheMatureSourceCursor()
	{
		AssetTypeTemplateField template = Root(
			Field("m_Width", AssetValueType.Int32),
			Field("ignored", AssetValueType.Int32),
			Field("m_Height", AssetValueType.Int32));
		const int offset = 23;
		byte[] objectBytes = [0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
		byte[] source = Embedded(objectBytes, offset, suffixLength: 0);

		using CountingStream matureInput = new(source);
		matureInput.Position = offset;
		Exception? matureError = Capture(
			() => UnityProjection.Read(matureInput, template, false, new UnityProjection.State(template)),
			out UnityProjection? mature);

		using CountingStream routeInput = new(source);
		routeInput.Position = offset;
		Exception? routeError = Capture(
			() => UnityProjection.ReadBorrowed(routeInput, offset, objectBytes.Length, template, false,
				new UnityProjection.State(template)),
			out UnityProjection? route);

		Assert.Null(mature);
		Assert.IsType<EndOfStreamException>(matureError);
		Assert.Null(route);
		Assert.IsType<EndOfStreamException>(routeError);
		Assert.Equal(matureInput.Position, routeInput.Position);
		Assert.Equal<ReadWindow>(matureInput.Reads, routeInput.Reads);
		Assert.Equal(offset + objectBytes.Length, matureInput.Position);
	}

	[Fact]
	public void SharedSchemaAliasesCannotBypassTheMatureFieldBudget()
	{
		byte[] source = new byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(source, 123456);

		AssetTypeTemplateField controlTemplate = AliasedNoneTemplate(levels: 3);
		UnityProjection.State controlState = new(controlTemplate);
		using (CountingStream matureControlInput = new(source))
		{
			UnityProjection matureControl = UnityProjection.Read(matureControlInput, controlTemplate, false, controlState);
			Assert.Equal(123456, matureControl.Number("m_Width"));
		}
		using (CountingStream routeControlInput = new(source))
		{
			UnityProjection routeControl = UnityProjection.ReadBorrowed(
				routeControlInput, 0, source.Length, controlTemplate, false, controlState);
			Assert.Equal(123456, routeControl.Number("m_Width"));
		}

		AssetTypeTemplateField template = AliasedNoneTemplate(levels: 23);
		UnityProjection.State state = new(template);
		using CountingStream matureInput = new(source);
		Exception? matureError = Capture(
			() => UnityProjection.Read(matureInput, template, false, state),
			out UnityProjection? mature);

		using CountingStream routeInput = new(source);
		Exception? routeError = Capture(
			() => UnityProjection.ReadBorrowed(routeInput, 0, source.Length, template, false, state),
			out UnityProjection? route);

		Assert.Null(mature);
		Assert.IsType<InvalidDataException>(matureError);
		Assert.Null(route);
		Assert.IsType<InvalidDataException>(routeError);
	}

	[Fact]
	public void RealTexture2DTpkSchemaUsesTheBorrowedRouteWithoutReadingTheImagePayload()
	{
		const string name = "hero";
		byte[] pixels = [10, 20, 30, 255, 40, 50, 60, 255];
		byte[] fileBytes = UnityFixtureBuilder.SerializedInlineTexture(name, pixels);
		using SmartStream smart = SmartStream.CreateMemory(fileBytes);
		using SerializedFile file = (SerializedFile)SchemeReader.ReadFile(
			smart, "texture.assets", "texture.assets", FileReadMode.MetadataOnly);
		ObjectInfo info = file.Objects[0];
		int objectOffset = checked((int)info.DataOffset);
		int objectLength = checked((int)info.DataSize);
		byte[] objectBytes = new byte[objectLength];
		Array.Copy(fileBytes, objectOffset, objectBytes, 0, objectLength);
		Assert.True(objectOffset > 0);
		Assert.True(TypeTreeNodeStruct.TryMakeFromTpk(
			AssetRipper.SourceGenerated.ClassIDType.Texture2D, file.Version,
			out TypeTreeNodeStruct release, out _));
		AssetTypeTemplateField template = UnityProjection.Template(release);
		bool bigEndian = file.EndianType == EndianType.BigEndian;

		using CountingStream matureInput = new(objectBytes);
		UnityProjection mature = UnityProjection.Read(
			matureInput, template, bigEndian, new UnityProjection.State(template));
		using CountingStream routeInput = new(fileBytes);
		UnityProjection route = UnityProjection.ReadBorrowed(
			routeInput, objectOffset, objectLength, template, bigEndian,
			new UnityProjection.State(template), textureTail: true, captureRawName: true);

		Assert.Equal(name, Encoding.UTF8.GetString(route.RawName));
		Assert.Equal<byte>(Encoding.UTF8.GetBytes(mature.Text("m_Name")), route.RawName.ToArray());
		Assert.Equal(mature.Number("m_Width"), route.Number("m_Width"));
		Assert.Equal(mature.Number("m_Height"), route.Number("m_Height"));
		UnityProjection.ByteRange image = route.Bytes("image data");
		Assert.Equal(mature.Bytes("image data"), image);
		Assert.Equal(pixels.Length, image.Length);
		Assert.Equal(mature.Text("m_StreamData/path"), route.Text("m_StreamData/path"));
		Assert.Equal(mature.Unsigned("m_StreamData/size"), route.Unsigned("m_StreamData/size"));
		Assert.True(routeInput.Reads.Count < matureInput.Reads.Count,
			$"The TPK texture route used {routeInput.Reads.Count} reads; the mature iterator used {matureInput.Reads.Count}.");
		Assert.All(routeInput.Reads, read =>
			Assert.InRange(read.Offset, objectOffset, objectOffset + objectLength - read.Count));
		Assert.DoesNotContain(routeInput.Reads, read =>
			Intersects(read, objectOffset + (long)image.Offset, image.Length));
	}

	private static AssetTypeTemplateField AliasedNoneTemplate(int levels)
	{
		AssetTypeTemplateField shared = Field("ignoredLeaf", AssetValueType.None);
		for (int i = 0; i < levels; i++)
			shared = Field($"ignored{i}", AssetValueType.None, shared, shared);
		return Root(shared, Field("m_Width", AssetValueType.Int32));
	}

	private static AssetTypeTemplateField Root(params AssetTypeTemplateField[] fields)
		=> Field("Base", AssetValueType.None, fields);

	private static AssetTypeTemplateField Field(string name, AssetValueType kind, params AssetTypeTemplateField[] children)
		=> new()
		{
			Name = name,
			Type = name,
			ValueType = kind,
			HasValue = kind != AssetValueType.None,
			IsArray = kind is AssetValueType.Array or AssetValueType.ByteArray,
			Children = children.ToList(),
		};

	private static byte[] Embedded(byte[] body, int offset, int suffixLength)
	{
		byte[] source = new byte[checked(offset + body.Length + suffixLength)];
		Array.Fill(source, (byte)0xA5);
		body.CopyTo(source, offset);
		if (suffixLength != 0)
			Array.Fill(source, (byte)0x5A, offset + body.Length, suffixLength);
		return source;
	}

	private static Exception? Capture(Func<UnityProjection> read, out UnityProjection? projection)
	{
		try
		{
			projection = read();
			return null;
		}
		catch (Exception error)
		{
			projection = null;
			return error;
		}
	}

	private static bool Intersects(ReadWindow read, long start, int length)
		=> read.Count > 0 && length > 0 && read.Offset < start + length && read.Offset + read.Count > start;

	private readonly record struct ReadWindow(long Offset, int Count);

	private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, false)
	{
		private readonly List<ReadWindow> reads = [];
		public IReadOnlyList<ReadWindow> Reads => reads;

		public override int Read(byte[] buffer, int offset, int count)
		{
			int read = base.Read(buffer, offset, count);
			if (read > 0) reads.Add(new(Position - read, read));
			return read;
		}

		public override int Read(Span<byte> buffer)
		{
			int read = base.Read(buffer);
			if (read > 0) reads.Add(new(Position - read, read));
			return read;
		}

		public override int ReadByte()
		{
			long position = Position;
			int value = base.ReadByte();
			if (value >= 0) reads.Add(new(position, 1));
			return value;
		}
	}
}
